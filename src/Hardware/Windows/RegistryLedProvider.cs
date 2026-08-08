using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// Lighting on/off, via MSI Center M's own model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor</c>, value
    /// <c>LightingBrightness</c>, REG_DWORD, <c>0</c> = off / <c>1</c> = on. Confirmed on device
    /// 2026-08-07 as the only lighting fact this registry hive holds - switching RGB profile or
    /// colour produced no change here, so effect and colour live elsewhere (MysticLight's own
    /// store, or written straight to the device) and stay out of scope until the vendor HID report
    /// (<c>0x0F</c>) is decoded. See Gate G4 in <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// <b>MSI's performance-mode selector writes this value too.</b> Entering Endurance turned it
    /// off; leaving Endurance turned it back on. So a read here can legitimately disagree with the
    /// last value this provider applied, and nothing here re-asserts against that - the widget
    /// re-syncs the whole snapshot after a mode change instead (see
    /// <c>FeatureDispatcher.HandleSet</c>'s <see cref="Shared.Ipc.Function.PerfMode"/> case).
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class RegistryLedProvider : ILedProvider
    {
        private const string KeyPath = @"SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor";
        private const string BrightnessValue = "LightingBrightness";

        private readonly string _unavailableReason;

        public RegistryLedProvider()
        {
            using var key = OpenRead();
            if (key == null)
            {
                _unavailableReason =
                    "MSI Center M is not installed, so its lighting switch does not exist.";
                return;
            }

            if (key.GetValue(BrightnessValue) is not int)
            {
                _unavailableReason =
                    "MSI Center M has not written a lighting value yet. Open it once and toggle "
                    + "lighting.";
            }
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        public bool TryRead(out bool enabled)
        {
            enabled = false;
            if (!Available) return false;

            try
            {
                using var key = OpenRead();
                if (key?.GetValue(BrightnessValue) is not int raw) return false;

                enabled = raw != 0;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public OpResult Apply(bool enabled)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            try
            {
                using var key = OpenWrite();
                if (key == null)
                {
                    return OpResult.Fail(
                        "Could not open MSI Center's lighting key for writing. "
                        + "The helper needs to run elevated.");
                }

                key.SetValue(BrightnessValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail(
                    "Access denied writing MSI Center's lighting key. The helper needs to run elevated.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Could not write the lighting switch: {ex.Message}");
            }

            // Read back. As with the other registry mirrors, a second process is what carries this
            // to the device, so the registry accepting the write proves only that it landed.
            if (!TryRead(out bool actual))
                return OpResult.Fail("Wrote the lighting switch but could not read it back.");

            if (actual != enabled)
            {
                return OpResult.Fail(
                    $"Lighting did not stick: asked for {(enabled ? "on" : "off")}, "
                    + $"found {(actual ? "on" : "off")}.");
            }

            return OpResult.Success();
        }

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
