using System;
using System.Globalization;
using System.Management;
using McenterLite.Hardware;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Helper
{
    /// <summary>
    /// Subscribes to the hardware button and performs whatever the user configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The button raises <c>MSI_Event</c> with <c>MSIEvt = 0x220029</c>, once per press, and does
    /// nothing else. Measured 2026-08-14; see <c>docs/hardware-notes.md</c>. MSI Center M was the
    /// only subscriber, so uninstalling it left the event firing into an empty room — this fills the
    /// vacancy rather than taking anything over, which makes it the one feature here that cannot
    /// conflict with the firmware.
    /// </para>
    /// <para>
    /// <b>Feature actions go through the dispatcher</b>, building the same message the widget would
    /// send. That is not indirection for its own sake: it means the button gets the clamping,
    /// persistence, read-back and logging that every other path already has, and a change to how a
    /// feature is applied cannot leave the button behind.
    /// </para>
    /// </remarks>
    internal sealed class ButtonListener : IDisposable
    {
        private const string Namespace = @"\\.\root\wmi";
        private const string EventClass = "MSI_Event";
        private const string PayloadProperty = "MSIEvt";

        /// <summary>The one code this device's button produces. Identical on every press.</summary>
        private const uint ButtonCode = 0x220029;

        private readonly IHardware _hardware;
        private readonly FeatureDispatcher _dispatcher;
        private readonly ButtonActionStore _actions;
        private readonly LightingProfileStore _lighting;

        private ManagementEventWatcher _watcher;

        public ButtonListener(
            IHardware hardware,
            FeatureDispatcher dispatcher,
            ButtonActionStore actions,
            LightingProfileStore lighting)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        }

        /// <summary>
        /// Starts listening. Failure is reported and not fatal — every other feature still works.
        /// </summary>
        public void Start()
        {
            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                _watcher = new ManagementEventWatcher(
                    scope, new WqlEventQuery($"SELECT * FROM {EventClass}"));

                _watcher.EventArrived += OnEventArrived;
                _watcher.Start();

                Log.Info($"Listening for the hardware button on {EventClass}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not subscribe to {EventClass}; the hardware button will do nothing: {ex.Message}");
            }
        }

        private void OnEventArrived(object sender, EventArrivedEventArgs e)
        {
            uint code;
            try
            {
                var raw = e.NewEvent[PayloadProperty];
                if (raw == null) return;
                code = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {PayloadProperty} from a {EventClass}: {ex.Message}");
                return;
            }

            // Other codes may exist on this channel - only one button has ever been observed. An
            // unrecognised one is logged rather than ignored: it is how the next button gets found.
            if (code != ButtonCode)
            {
                Log.Info($"Button: ignoring an unrecognised {EventClass} code 0x{code:X}.");
                return;
            }

            // Read at the moment of the press, so editing the file and pressing is one action.
            var action = _actions.Load(Log.Warn);

            try
            {
                Perform(action);
            }
            catch (Exception ex)
            {
                // A button press must never take the helper down. This runs on a WMI callback
                // thread, where an escaping exception would.
                Log.Error($"Button: {ButtonActions.Format(action)} threw", ex);
            }
        }

        private void Perform(ButtonAction action)
        {
            switch (action)
            {
                case ButtonAction.None:
                    Log.Info("Button: pressed, and no action is configured. See Button/README.txt.");
                    return;

                case ButtonAction.RtssOverlay:
                {
                    bool? visible = Rtss.ToggleOverlay(out var error);
                    Log.Info(visible == null
                        ? $"Button: could not toggle the RTSS overlay - {error}"
                        : $"Button: RTSS overlay {(visible.Value ? "shown" : "hidden")}.");
                    return;
                }

                case ButtonAction.FanProfile:
                    Send(Function.FanProfile, Next(ReadInt(Function.FanProfile), 2));
                    return;

                case ButtonAction.PerfMode:
                    Send(Function.PerfMode, (int)NextPerfMode());
                    return;

                case ButtonAction.LightingProfile:
                    // Slot 0 is off, then one slot per profile - so the cycle is one longer than the
                    // profile count.
                    Send(Function.LightingProfile,
                        Next(ReadInt(Function.LightingProfile), LightingProfileStore.ProfileCount + 1));
                    return;

                case ButtonAction.ControllerMode:
                {
                    if (!_hardware.HwMouse.TryRead(out bool desktopMode))
                    {
                        Log.Warn("Button: could not read the controller mode.");
                        return;
                    }

                    Send(Function.HwMouseMode, !desktopMode);
                    return;
                }
            }
        }

        /// <summary>
        /// The next value in a cycle, wrapping. An unreadable current value starts at 0.
        /// </summary>
        private static int Next(int current, int count) =>
            current < 0 ? 0 : (current + 1) % count;

        /// <summary>
        /// Cycles the performance mode in the order the widget shows it, not by ordinal.
        /// </summary>
        /// <remarks>
        /// The card reads Endurance, AI Engine, Manual left to right, and a button that cycled by
        /// enum value would run in a different order from the control beside it.
        /// </remarks>
        private PerfMode NextPerfMode()
        {
            if (!_hardware.Tdp.TryReadMode(out var current)) return PerfMode.UserScenario;

            switch (current)
            {
                case PerfMode.Endurance: return PerfMode.AiEngine;
                case PerfMode.AiEngine: return PerfMode.UserScenario;
                case PerfMode.UserScenario: return PerfMode.Endurance;

                // Unknown means the firmware reported a mode we do not model. Landing on Manual is
                // the useful move: it is the one the user is most likely to want and the only one
                // whose effect is visible in the widget.
                default: return PerfMode.UserScenario;
            }
        }

        private int ReadInt(Function fn)
        {
            var reply = _dispatcher.Handle(PipeEnvelope.Get(0, fn));
            return reply != null && reply.Cmd == Command.Response ? reply.AsInt(-1) : -1;
        }

        private void Send(Function fn, int value) => Send(fn, PipeEnvelope.FromInt(value));

        private void Send(Function fn, bool value) => Send(fn, PipeEnvelope.FromBool(value));

        /// <summary>
        /// Applies a value exactly as an incoming widget message would.
        /// </summary>
        private void Send(Function fn, string value)
        {
            var reply = _dispatcher.Handle(PipeEnvelope.Set(0, fn, value));

            if (reply != null && reply.Cmd == Command.Error)
            {
                Log.Warn($"Button: {fn} failed - {reply.Error}");
                return;
            }

            Log.Info($"Button: {fn} -> {reply?.Value ?? value}.");
        }

        public void Dispose()
        {
            try
            {
                if (_watcher == null) return;

                _watcher.EventArrived -= OnEventArrived;
                _watcher.Stop();
                _watcher.Dispose();
                _watcher = null;
            }
            catch (Exception)
            {
                // Shutting down. A watcher that will not stop cleanly is not worth a crash on the
                // way out.
            }
        }
    }
}
