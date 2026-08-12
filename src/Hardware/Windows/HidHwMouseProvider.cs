using System;
using System.Runtime.Versioning;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The controller's desktop-mouse / gamepad mode, over MSI's vendor HID channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the firmware route, and it needs MSI Center M neither running nor installed -
    /// unlike <see cref="RegistryHwMouseProvider"/>, whose registry value turned out to be a
    /// mirror MSI Center M maintains by watching this very channel. Writing the mode here makes
    /// that registry value update itself to match.
    /// </para>
    /// <para>
    /// <b>We do not own this state.</b> The physical MSI button switches the same mode, and the
    /// firmware handles that entirely on its own - confirmed with the whole MSI Center M stack
    /// stopped. So nothing re-applies a stored mode at startup, and the helper re-reads on its
    /// telemetry tick so the widget follows the button rather than showing a stale choice.
    /// </para>
    /// <para>
    /// <b>Three states, one boolean.</b> The firmware has XInput, DirectInput and Desktop; this
    /// interface has <c>bool desktopMode</c>. Both gamepad modes read back as "not desktop", and a
    /// write asking for gamepad picks XInput. Nothing on this device produces DirectInput - the
    /// button toggles XInput and Desktop only - so the narrowing costs nothing today, but it is a
    /// narrowing rather than a faithful model.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class HidHwMouseProvider : IHwMouseProvider
    {
        private const byte ModeXInput = MsiControllerModeProtocol.ModeXInput;
        private const byte ModeDInput = MsiControllerModeProtocol.ModeDInput;
        private const byte ModeDesktop = MsiControllerModeProtocol.ModeDesktop;

        private readonly string _unavailableReason;

        public HidHwMouseProvider()
        {
            // Probe with a real query. Opening the handle only proves the interface exists; the
            // controller can be present and still not answer, and a provider that reports itself
            // available and then fails every read is worse than one that admits it up front.
            if (!TryReadMode(out _, out var error)) _unavailableReason = error;
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        public bool TryRead(out bool desktopMode)
        {
            desktopMode = false;

            if (!TryReadMode(out byte mode, out _)) return false;

            switch (mode)
            {
                case ModeDesktop:
                    desktopMode = true;
                    return true;

                case ModeXInput:
                case ModeDInput:
                    desktopMode = false;
                    return true;

                default:
                    // An unrecognised mode fails the read rather than being guessed at. Defaulting
                    // to "gamepad" would be a plausible-looking lie and the widget would then show
                    // a mode the device is not in.
                    return false;
            }
        }

        public OpResult Apply(bool desktopMode)
        {
            byte target = desktopMode ? ModeDesktop : ModeXInput;

            using var channel = MsiVendorHidChannel.Open(out var openError);
            if (channel == null) return OpResult.Fail(openError);

            if (!MsiControllerModeProtocol.TrySwitch(channel, target, out var sendError))
                return OpResult.Fail($"Could not switch the controller mode: {sendError}");

            // Read back on the SAME handle. A software switch is not acknowledged - only the
            // physical button makes the firmware announce - so waiting for a notification here
            // would block until it timed out, every time.
            if (!MsiControllerModeProtocol.TryQuery(channel, out byte actual))
                return OpResult.Fail("Switched the controller mode but could not read it back.");

            if (actual != target)
            {
                return OpResult.Fail(
                    $"Controller mode did not stick: asked for {Describe(target)}, "
                    + $"found {Describe(actual)}.");
            }

            return OpResult.Success();
        }

        private static bool TryReadMode(out byte mode, out string error)
        {
            mode = 0;

            using var channel = MsiVendorHidChannel.Open(out error);
            if (channel == null) return false;

            if (!MsiControllerModeProtocol.TryQuery(channel, out mode))
            {
                error = "The controller did not report its mode. Is it connected?";
                return false;
            }

            error = null;
            return true;
        }

        // Matches the widget's button labels. An error naming a mode the user cannot find on
        // screen is a dead end.
        private static string Describe(byte mode) => mode switch
        {
            ModeDesktop => "Desktop",
            ModeXInput => "Gamepad",
            ModeDInput => "Gamepad (DirectInput)",
            _ => $"an unknown mode (0x{mode:X2})",
        };
    }
}
