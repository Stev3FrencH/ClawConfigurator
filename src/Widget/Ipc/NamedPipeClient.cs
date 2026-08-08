using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McenterLite.Shared.Ipc;
using Microsoft.Win32.SafeHandles;

namespace McenterLite.Widget.Ipc
{
    /// <summary>
    /// The widget's end of the pipe to the elevated helper.
    ///
    /// <para>
    /// <b>Why this opens the pipe by hand instead of using NamedPipeClientStream.</b> This is a
    /// UWP AppContainer. The managed <c>NamedPipeClientStream</c> performs a name resolution and
    /// permission dance that the sandbox rejects, so it fails on a pipe the process is in fact
    /// allowed to open. Calling <c>CreateFileW</c> on the raw <c>\\.\pipe\</c> path works,
    /// because the AppContainer token genuinely carries the ALL APPLICATION PACKAGES grant the
    /// server put in its ACL.
    /// </para>
    /// </summary>
    internal sealed class NamedPipeClient : IDisposable
    {
        /// <summary>Must match the helper exactly. A mismatch is invisible: it looks like "not started yet".</summary>
        private const string PipePath = @"\\.\pipe\McenterLiteHelper";

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const int ErrorPipeBusy = 231;
        private const int ErrorFileNotFound = 2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WaitNamedPipeW(string name, uint timeoutMs);

        private readonly ConcurrentDictionary<int, TaskCompletionSource<PipeEnvelope>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<PipeEnvelope>>();

        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cts;
        private SafeFileHandle _handle;
        private FileStream _stream;
        private StreamWriter _writer;
        private int _nextId;

        /// <summary>Unsolicited pushes from the helper: telemetry, and state changed behind our back.</summary>
        public event Action<PipeEnvelope> EventReceived;

        public event Action<bool> ConnectionChanged;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// Connects, retrying on a schedule tuned for the first-run case.
        /// </summary>
        /// <remarks>
        /// The retry budget exists because of one specific sequence: on first run the helper puts
        /// up a UAC prompt and does not create the pipe until the user answers it. Giving up in a
        /// couple of seconds would make the widget report a permanent failure while the prompt is
        /// still on screen. So: fast attempts first for the normal reconnect case (the widget is
        /// suspended and resumed constantly), then slow ones for about a minute to cover a human
        /// reading a dialog.
        /// </remarks>
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            const int fastAttempts = 10;
            const int fastDelayMs = 250;
            const int slowAttempts = 50;
            const int slowDelayMs = 1000;

            for (int attempt = 0; attempt < fastAttempts + slowAttempts; attempt++)
            {
                if (token.IsCancellationRequested) return false;

                if (TryOpen())
                {
                    McenterLite.Widget.App.Log("NamedPipeClient: TryOpen succeeded, starting reader");
                    StartReading();
                    SetConnected(true);
                    return true;
                }

                int delay = attempt < fastAttempts ? fastDelayMs : slowDelayMs;
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            return false;
        }

        private bool TryOpen()
        {
            try
            {
                // FILE_FLAG_OVERLAPPED: opened synchronously, the FileStream wrapping this handle
                // hung forever on the very first write, discovered by tracing an install that got
                // stuck on "Connecting..." with every card unresponsive. The helper's own pipe
                // server (and everything else that talks .NET pipes, including
                // NamedPipeClientStream/NamedPipeServerStream internally) uses overlapped I/O; a
                // synchronous handle on this end was the asymmetry. Overlapped does not reintroduce
                // the AppContainer problem this raw CreateFileW call exists to route around - that
                // was NamedPipeClientStream's own name-resolution/permission path, unrelated to
                // FILE_FLAG_OVERLAPPED.
                var handle = CreateFileW(PipePath, GenericRead | GenericWrite, 0, IntPtr.Zero,
                    OpenExisting, FileFlagOverlapped, IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();

                    if (error == ErrorPipeBusy)
                    {
                        // Server exists but is serving someone else. Worth a short wait: this is
                        // usually the previous widget instance not yet torn down.
                        handle.Dispose();
                        WaitNamedPipeW(PipePath, 1000);
                        return false;
                    }

                    // ERROR_FILE_NOT_FOUND is the ordinary "helper not up yet" case and is not
                    // worth logging on every attempt.
                    if (error != ErrorFileNotFound)
                        System.Diagnostics.Debug.WriteLine($"[pipe] CreateFileW failed: {error}");

                    handle.Dispose();
                    return false;
                }

                _handle = handle;
                _stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipe] open threw: {ex.Message}");
                return false;
            }
        }

        private void StartReading()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var stream = _stream;

            // The handle is opened with FILE_FLAG_OVERLAPPED now, so the stream is genuinely
            // async - no dedicated thread needed to avoid tying up a thread-pool worker on a
            // blocking read.
            _ = ReadLoopAsync(stream, token);
        }

        private async Task ReadLoopAsync(FileStream stream, CancellationToken token)
        {
            try
            {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break; // helper closed or exited

                        if (line.Length == 0) continue;

                        if (!PipeEnvelope.TryParse(line, out var envelope))
                        {
                            System.Diagnostics.Debug.WriteLine($"[pipe] dropped an unparseable line ({line.Length} bytes)");
                            continue;
                        }

                        Dispatch(envelope);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipe] read loop ended: {ex.Message}");
            }
            finally
            {
                SetConnected(false);
                FailAllPending("The connection to the helper was lost.");
            }
        }

        private void Dispatch(PipeEnvelope envelope)
        {
            if (envelope.Cmd == Command.Response || envelope.Cmd == Command.Error)
            {
                if (_pending.TryRemove(envelope.Id, out var completion))
                {
                    completion.TrySetResult(envelope);
                    return;
                }

                // A reply whose request already timed out. Dropping it is correct - the caller
                // has moved on - but a burst of these means the helper is running slow.
                System.Diagnostics.Debug.WriteLine($"[pipe] unmatched reply id={envelope.Id} fn={envelope.Fn}");
                return;
            }

            try
            {
                EventReceived?.Invoke(envelope);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipe] event handler threw: {ex.Message}");
            }
        }

        /// <summary>Sends a request and waits for its reply.</summary>
        /// <returns>The reply, or null on timeout or disconnection.</returns>
        public async Task<PipeEnvelope> RequestAsync(
            Command command, Function function, string value = null, int timeoutMs = 5000)
        {
            if (!IsConnected) return null;

            int id = Interlocked.Increment(ref _nextId);
            var envelope = new PipeEnvelope(id, command, function, value);

            var completion = new TaskCompletionSource<PipeEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            if (!await SendAsync(envelope).ConfigureAwait(false))
            {
                _pending.TryRemove(id, out _);
                return null;
            }

            using (var timeout = new CancellationTokenSource(timeoutMs))
            using (timeout.Token.Register(() => completion.TrySetResult(null)))
            {
                var reply = await completion.Task.ConfigureAwait(false);

                // Always clean up: a timed-out entry left behind leaks, and its late reply would
                // later be matched against a recycled id.
                _pending.TryRemove(id, out _);
                return reply;
            }
        }

        /// <summary>Sends without waiting for a reply.</summary>
        public async Task<bool> SendAsync(PipeEnvelope envelope)
        {
            if (!IsConnected || _writer == null) return false;

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(envelope.Serialize()).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                // Usually the helper restarting. Expected, not exceptional.
                System.Diagnostics.Debug.WriteLine($"[pipe] write failed: {ex.Message}");
                SetConnected(false);
                return false;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public Task<PipeEnvelope> GetAsync(Function function) =>
            RequestAsync(Command.Get, function);

        public Task<PipeEnvelope> SetAsync(Function function, string value) =>
            RequestAsync(Command.Set, function, value);

        private void SetConnected(bool connected)
        {
            if (IsConnected == connected) return;
            IsConnected = connected;

            try { ConnectionChanged?.Invoke(connected); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[pipe] {ex.Message}"); }
        }

        /// <summary>
        /// Completes every outstanding request so no caller waits on a reply that can never come.
        /// </summary>
        private void FailAllPending(string reason)
        {
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var completion))
                    completion.TrySetResult(null);
            }

            System.Diagnostics.Debug.WriteLine($"[pipe] {reason}");
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch (Exception) { }

            FailAllPending("Client disposed.");
            SetConnected(false);

            try { _writer?.Dispose(); } catch (Exception) { }
            try { _stream?.Dispose(); } catch (Exception) { }
            try { _handle?.Dispose(); } catch (Exception) { }

            _cts?.Dispose();
            _writeGate.Dispose();
        }
    }
}
