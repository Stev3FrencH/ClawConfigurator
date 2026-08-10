using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McenterLite.Shared.Ipc;

namespace McenterLite.Helper.Ipc
{
    /// <summary>
    /// The named-pipe server the widget talks to.
    ///
    /// <para>
    /// One client at a time - there is exactly one widget - and one message per line. Reconnects
    /// are expected and cheap: the widget is a UWP app that Windows suspends and terminates
    /// whenever the Game Bar is dismissed.
    /// </para>
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        /// <summary>
        /// Must match the widget. Changing it silently breaks the connection with no error
        /// anywhere, because a missing pipe is indistinguishable from a helper that has not
        /// started yet.
        /// </summary>
        public const string PipeName = "McenterLiteHelper";

        private readonly Func<PipeEnvelope, PipeEnvelope> _handler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private StreamWriter _writer;
        private readonly object _writeGate = new object();

        public PipeServer(Func<PipeEnvelope, PipeEnvelope> handler) =>
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <summary>Raised when a client connects, so the caller can push an unsolicited snapshot.</summary>
        public event Action ClientConnected;

        public bool IsConnected
        {
            get { lock (_writeGate) { return _writer != null; } }
        }

        public async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await ServeOneClientAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Pipe session failed", ex);

                    // Back off briefly so a persistently failing pipe cannot spin the CPU.
                    try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task ServeOneClientAsync()
        {
            using var server = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 8192,
                outBufferSize: 8192,
                BuildSecurity());

            await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            Log.Info("Widget connected.");

            // Leave the streams open so disposing them does not race the pipe's own disposal.
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            var writer = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            lock (_writeGate) { _writer = writer; }

            try
            {
                ClientConnected?.Invoke();

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break; // client closed

                    HandleLine(line);
                }
            }
            finally
            {
                lock (_writeGate) { _writer = null; }
                Log.Info("Widget disconnected.");

                try { if (server.IsConnected) server.Disconnect(); }
                catch (Exception) { /* already gone */ }
            }
        }

        private void HandleLine(string line)
        {
            if (line.Length == 0) return;

            // Anything malformed is DROPPED, never thrown. See BuildSecurity: this pipe is
            // reachable by every app package on the machine, so hostile or simply buggy input
            // is an expected condition, not an exceptional one.
            if (!PipeEnvelope.TryParse(line, out var request))
            {
                Log.Warn($"Dropped an unparseable message ({line.Length} bytes).");
                return;
            }

            PipeEnvelope reply;
            try
            {
                reply = _handler(request);
            }
            catch (Exception ex)
            {
                // A handler bug must not kill the connection - the widget would see a silent
                // disconnect with no way to report what went wrong.
                Log.Error($"Handler threw for {request.Fn}", ex);
                reply = PipeEnvelope.Failure(request.Id, request.Fn, "Internal error handling the request.");
            }

            if (reply != null) Send(reply);
        }

        /// <summary>Sends a message. Safe to call from any thread; a no-op when nothing is connected.</summary>
        public void Send(PipeEnvelope envelope)
        {
            if (envelope == null) return;

            lock (_writeGate)
            {
                if (_writer == null) return;

                try
                {
                    _writer.WriteLine(envelope.Serialize());
                }
                catch (Exception ex)
                {
                    // Almost always the widget being suspended mid-write. Expected, not an error.
                    Log.Warn($"Write failed, treating the client as gone: {ex.Message}");
                    _writer = null;
                }
            }
        }

        /// <summary>
        /// Grants the pipe to the interactive user and to app containers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two app-package SIDs are what make this work at all. The widget is a UWP app
        /// running in an AppContainer, and an AppContainer token carries none of the user's own
        /// group memberships - so a pipe ACL'd only to the current user is invisible to it. The
        /// failure mode is a connection that times out with no error on either side.
        /// </para>
        /// <para>
        /// The cost is real and is accepted deliberately: ANY app package on this machine can
        /// open this pipe and drive an elevated process. That is why the command surface is
        /// small, every value is clamped server-side, and nothing here accepts a path, URL or
        /// command line.
        /// </para>
        /// </remarks>
        private static PipeSecurity BuildSecurity()
        {
            var security = new PipeSecurity();

            // The signed-in user, so an unelevated debug run can still connect.
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                security.AddAccessRule(new PipeAccessRule(
                    identity.User,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not add the current-user ACE: {ex.Message}");
            }

            // S-1-15-2-1 = ALL APPLICATION PACKAGES. Without this the widget cannot connect.
            TryAddSid(security, "S-1-15-2-1", "ALL APPLICATION PACKAGES");

            // S-1-15-2-2 = ALL RESTRICTED APPLICATION PACKAGES. Absent on some builds, hence the try.
            TryAddSid(security, "S-1-15-2-2", "ALL RESTRICTED APPLICATION PACKAGES");

            return security;
        }

        private static void TryAddSid(PipeSecurity security, string sddl, string label)
        {
            try
            {
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(sddl),
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not add the {label} ACE ({sddl}): {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (Exception) { }
            _cts.Dispose();
        }
    }
}
