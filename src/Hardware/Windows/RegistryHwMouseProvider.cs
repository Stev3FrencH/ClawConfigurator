using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The controller's desktop-mouse / gamepad mode, via MSI Center M's own model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor</c>, value
    /// <c>ControlModeUserSet</c>, REG_SZ, <c>"Desktop"</c> or <c>"XInput"</c>. Confirmed on device
    /// 2026-08-07 in both directions - see Gate G5 in <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// <b>The same value name also exists, empty, under <c>Component\User Scenario</c>.</b> Writing
    /// that one would look completely convincing in a registry diff and do nothing at all. This
    /// provider deliberately hard-codes the <c>OsdEditor</c> path for that reason.
    /// </para>
    /// <para>
    /// <b>We do not own this state.</b> The physical MSI button switches the same mode, so a value
    /// read here can disagree with whatever this app last wrote at any moment and through no
    /// fault of ours. Two consequences, both deliberate: nothing re-applies a stored mode at
    /// startup, and the helper pushes this value on its telemetry tick so the widget follows the
    /// button rather than showing a stale choice.
    /// </para>
    /// <para>
    /// The firmware HID route (vendor opcode <c>0x24</c> SwitchMode, <c>0x04</c> desktop /
    /// <c>0x02</c> DInput) remains the documented fallback and is the only route that works with
    /// MSI Center stopped. It is not implemented: this path is verified and needs no HID handle.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class RegistryHwMouseProvider : IHwMouseProvider
    {
        // NOT Component\User Scenario, which holds an empty value of the same name. See the
        // remarks above - that mistake is invisible in a diff.
        private const string KeyPath = @"SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor";
        private const string ModeValue = "ControlModeUserSet";

        private const string DesktopMode = "Desktop";
        private const string GamepadMode = "XInput";

        private readonly string _unavailableReason;

        public RegistryHwMouseProvider()
        {
            using var key = OpenRead();
            if (key == null)
            {
                _unavailableReason =
                    "MSI Center M is not installed, so its controller-mode model does not exist.";
                return;
            }

            if (key.GetValue(ModeValue) as string == null)
            {
                _unavailableReason =
                    "MSI Center M has not written a controller mode yet. Open it once and switch "
                    + "between desktop and gamepad mode.";
            }
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        /// <summary>
        /// Reads the current mode. <paramref name="desktopMode"/> is true for the mouse mode.
        /// </summary>
        /// <remarks>
        /// An unrecognised string fails the read rather than being guessed at. Defaulting to
        /// "gamepad" would be a plausible-looking lie, and the widget would then show a mode the
        /// device is not in.
        /// </remarks>
        public bool TryRead(out bool desktopMode)
        {
            desktopMode = false;
            if (!Available) return false;

            try
            {
                using var key = OpenRead();
                if (key?.GetValue(ModeValue) is not string mode) return false;

                if (string.Equals(mode, DesktopMode, StringComparison.OrdinalIgnoreCase))
                {
                    desktopMode = true;
                    return true;
                }

                if (string.Equals(mode, GamepadMode, StringComparison.OrdinalIgnoreCase))
                {
                    desktopMode = false;
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public OpResult Apply(bool desktopMode)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            string target = desktopMode ? DesktopMode : GamepadMode;

            try
            {
                using var key = OpenWrite();
                if (key == null)
                {
                    return OpResult.Fail(
                        "Could not open MSI Center's controller-mode key for writing. "
                        + "The helper needs to run elevated.");
                }

                key.SetValue(ModeValue, target, RegistryValueKind.String);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail(
                    "Access denied writing MSI Center's controller-mode key. "
                    + "The helper needs to run elevated.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Could not switch the controller mode: {ex.Message}");
            }

            // Read back. A second process is what carries this to the controller firmware, so the
            // registry accepting the string proves only that the string landed.
            if (!TryRead(out bool actual))
                return OpResult.Fail("Switched the controller mode but could not read it back.");

            if (actual != desktopMode)
            {
                return OpResult.Fail(
                    $"Controller mode did not stick: asked for {Describe(desktopMode)}, "
                    + $"found {Describe(actual)}.");
            }

            return OpResult.Success();
        }

        // Matches the widget's button labels. An error that names a mode the user cannot find on
        // screen is a dead end.
        private static string Describe(bool desktopMode) => desktopMode ? "Desktop" : "Gamepad";

        // WOW6432Node is already in the path, so the 32-bit view must NOT also be requested -
        // that would redirect to WOW6432Node\WOW6432Node and silently find nothing.
        private static RegistryKey OpenRead() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                       .OpenSubKey(KeyPath, writable: false);

        private static RegistryKey OpenWrite() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                       .OpenSubKey(KeyPath, writable: true);
    }
}
