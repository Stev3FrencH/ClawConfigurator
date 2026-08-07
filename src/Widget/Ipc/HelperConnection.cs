using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Widget.Ipc
{
    /// <summary>
    /// Owns the pipe client and holds the last known state of every value.
    ///
    /// <para>
    /// The widget is a pure view. It stores nothing across launches and never assumes a write
    /// succeeded: each <see cref="SetAsync"/> updates the cache from the helper's REPLY, which
    /// carries the value the hardware actually holds. The helper is the single source of truth,
    /// which is what keeps a clamped or refused write from being displayed as if it had worked.
    /// </para>
    /// </summary>
    internal sealed class HelperConnection : IDisposable
    {
        /// <summary>
        /// Separates records in a snapshot. Matches the helper's <c>RecordSeparator</c>; a
        /// character no payload contains, and escaped if one ever does.
        /// </summary>
        private const char RecordSeparator = '\u001F';

        private readonly NamedPipeClient _client = new NamedPipeClient();
        private readonly Dictionary<Function, string> _values = new Dictionary<Function, string>();
        private readonly object _gate = new object();
        private CancellationTokenSource _reconnect;

        public DeviceCaps Caps { get; private set; } = new DeviceCaps();

        public bool IsConnected => _client.IsConnected;

        /// <summary>Raised when any value changes, from a reply or an unsolicited push.</summary>
        public event Action<Function, string> ValueChanged;

        /// <summary>Raised after a snapshot lands, so the UI can rebuild wholesale.</summary>
        public event Action SnapshotApplied;

        public event Action<bool> ConnectionChanged;

        public HelperConnection()
        {
            _client.EventReceived += OnEventReceived;
            _client.ConnectionChanged += OnConnectionChanged;
        }

        /// <summary>
        /// Connects and pulls a full snapshot.
        /// </summary>
        /// <remarks>
        /// One <c>Hello</c> answered by one snapshot, rather than a Get per control. The widget
        /// is suspended and restarted every time the Game Bar is dismissed, so this path runs
        /// constantly - about twenty round trips through an elevated process each time would be
        /// felt.
        /// </remarks>
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            if (!await _client.ConnectAsync(token).ConfigureAwait(false)) return false;

            var reply = await _client.RequestAsync(Command.Get, Function.Hello).ConfigureAwait(false);
            if (reply == null || reply.Cmd == Command.Error)
            {
                System.Diagnostics.Debug.WriteLine($"[conn] handshake failed: {reply?.Error ?? "no reply"}");
                return false;
            }

            ApplySnapshot(reply.Value);
            return true;
        }

        /// <summary>Decodes a snapshot and replaces the cached state.</summary>
        internal void ApplySnapshot(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var caps = new DeviceCaps();
            var values = new Dictionary<Function, string>();

            foreach (var record in payload.Split(RecordSeparator))
            {
                if (record.Length == 0) continue;

                int eq = record.IndexOf('=');
                if (eq <= 0) continue;

                var key = record.Substring(0, eq);
                var value = Unescape(record.Substring(eq + 1));

                if (key == "caps")
                {
                    caps = DeviceCaps.Parse(value);
                    continue;
                }

                // Unknown or unparseable function ids are skipped rather than rejected: a newer
                // helper may report values this build has no UI for, and that must not discard
                // the rest of the snapshot.
                if (!int.TryParse(key, out int fnId)) continue;
                if (!Enum.IsDefined(typeof(Function), fnId)) continue;

                values[(Function)fnId] = value;
            }

            lock (_gate)
            {
                Caps = caps;
                _values.Clear();
                foreach (var pair in values) _values[pair.Key] = pair.Value;
            }

            try { SnapshotApplied?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[conn] {ex.Message}"); }
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\') { sb.Append(value[i]); continue; }

                if (i + 1 < value.Length && value[i + 1] == '\\') { sb.Append('\\'); i++; continue; }

                if (i + 3 < value.Length && value.Substring(i + 1, 3) == "x1f")
                {
                    sb.Append(RecordSeparator);
                    i += 3;
                    continue;
                }

                sb.Append(value[i]);
            }

            return sb.ToString();
        }

        /// <summary>Last known value, or null when the helper reported it unavailable.</summary>
        public string Get(Function function)
        {
            lock (_gate)
                return _values.TryGetValue(function, out var value) ? value : null;
        }

        public bool GetBool(Function function, bool fallback = false)
        {
            var raw = Get(function);
            if (raw == "1") return true;
            if (raw == "0") return false;
            return fallback;
        }

        public int GetInt(Function function, int fallback = 0)
        {
            var raw = Get(function);
            return int.TryParse(raw, out var value) ? value : fallback;
        }

        /// <summary>True when the helper reported a value for this function at all.</summary>
        public bool IsAvailable(Function function)
        {
            lock (_gate) return _values.ContainsKey(function);
        }

        /// <summary>
        /// Writes a value and adopts whatever the helper reports back.
        /// </summary>
        /// <returns>Null on success, or the failure reason to show the user.</returns>
        public async Task<string> SetAsync(Function function, string value)
        {
            var reply = await _client.SetAsync(function, value).ConfigureAwait(false);

            if (reply == null) return "The helper did not respond.";
            if (reply.Cmd == Command.Error) return reply.Error ?? "The request failed.";

            // Adopt the REPLY, not the request. It carries the post-clamp actual value, so a
            // slider that asked for more than the firmware allows snaps back to the truth.
            UpdateValue(function, reply.Value);
            return null;
        }

        public Task<string> SetAsync(Function function, bool value) =>
            SetAsync(function, value ? "1" : "0");

        public Task<string> SetAsync(Function function, int value) =>
            SetAsync(function, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Tells the helper whether to bother pushing telemetry.</summary>
        public Task SetVisibleAsync(bool visible) =>
            _client.SendAsync(new PipeEnvelope(0, Command.Set, Function.WidgetVisible, visible ? "1" : "0"));

        private void UpdateValue(Function function, string value)
        {
            lock (_gate)
            {
                if (_values.TryGetValue(function, out var existing) && existing == value) return;
                _values[function] = value;
            }

            try { ValueChanged?.Invoke(function, value); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[conn] {ex.Message}"); }
        }

        private void OnEventReceived(PipeEnvelope envelope)
        {
            // Pushes are how the widget learns about state it does not own: fan telemetry, and
            // things the user changed on the device itself, like the physical mode button.
            if (envelope.Fn == Function.Snapshot)
            {
                ApplySnapshot(envelope.Value);
                return;
            }

            UpdateValue(envelope.Fn, envelope.Value);
        }

        private void OnConnectionChanged(bool connected)
        {
            try { ConnectionChanged?.Invoke(connected); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[conn] {ex.Message}"); }

            if (!connected) StartReconnect();
        }

        /// <summary>
        /// Reconnects in the background after a drop.
        /// </summary>
        /// <remarks>
        /// Drops are routine, not exceptional: the helper restarts on update, and the widget is
        /// suspended whenever the Game Bar closes. Recovering silently is the difference between
        /// an app that feels broken and one that does not.
        /// </remarks>
        private void StartReconnect()
        {
            _reconnect?.Cancel();
            _reconnect = new CancellationTokenSource();
            var token = _reconnect.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    await ConnectAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[conn] reconnect failed: {ex.Message}");
                }
            }, token);
        }

        public void Dispose()
        {
            try { _reconnect?.Cancel(); } catch (Exception) { }
            _reconnect?.Dispose();
            _client.Dispose();
        }
    }
}
