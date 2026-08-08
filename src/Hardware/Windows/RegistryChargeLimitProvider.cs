using System;
using System.Globalization;
using System.Runtime.Versioning;
using McenterLite.Shared.Model;
using Microsoft.Win32;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// Battery charge limit, via MSI Center M's own model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on device 2026-08-07 from before/after registry captures of MSI Center's own UI,
    /// across all three transitions (100 -> 80, 80 -> 60, 60 -> 100).
    /// </para>
    /// <para>
    /// <b>It is not a percentage.</b> MSI stores a three-state selector, and the numbering runs
    /// opposite to the percentage: <c>"0"</c> is 100%, <c>"1"</c> is 80%, <c>"2"</c> is 60%. So a
    /// higher stored number means a lower limit, and anything that treats the value as a percent -
    /// or even as ascending - gets it backwards.
    /// </para>
    /// <para>
    /// <b>It is REG_SZ, not REG_DWORD</b>, unlike the power limits in the neighbouring key. Writing
    /// a DWORD here would change the value's type under MSI Center rather than its content.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class RegistryChargeLimitProvider : IChargeLimitProvider
    {
        private const string KeyPath = @"SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery";
        private const string LevelValue = "BatteryLevel";

        private readonly string _unavailableReason;

        public RegistryChargeLimitProvider()
        {
            using var key = OpenRead();
            if (key == null)
            {
                _unavailableReason =
                    "MSI Center M is not installed, so its battery model does not exist.";
                return;
            }

            if (key.GetValue(LevelValue) as string == null)
            {
                _unavailableReason =
                    "MSI Center M has not written a battery model yet. Open it once and set a "
                    + "charging limit.";
            }
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        /// <summary>
        /// Reads the limit.
        /// </summary>
        /// <remarks>
        /// "Charge to 100%" IS the off state - there is no separate enable flag on the device - so
        /// <paramref name="enabled"/> is false exactly when the level is 100%. In that case the
        /// reported percent is a SUGGESTION for what to re-enable at, not something the device
        /// holds; the helper overlays the user's remembered choice on top.
        /// </remarks>
        public bool TryRead(out bool enabled, out int percent)
        {
            enabled = false;
            percent = ChargeLevels.Default;

            if (!Available) return false;

            try
            {
                using var key = OpenRead();
                if (key?.GetValue(LevelValue) is not string level) return false;

                // An unrecognised level fails the read rather than being guessed at.
                if (!ChargeLevels.TryFromMsiLevel(level, out int stored)) return false;

                // 100% is the off state - the device has no separate enable flag. The percent
                // reported alongside it is only a suggestion for what to re-enable at.
                enabled = stored != ChargeLevels.Full;
                percent = enabled ? stored : ChargeLevels.Default;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public OpResult Apply(bool enabled, int percent)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            // Disabled means "charge to full", which is the same three-state selector at 100%.
            int target = enabled ? ChargeLevels.Snap(percent) : ChargeLevels.Full;
            string level = ChargeLevels.ToMsiLevel(target);

            try
            {
                using var key = OpenWrite();
                if (key == null)
                {
                    return OpResult.Fail(
                        "Could not open MSI Center's battery key for writing. "
                        + "The helper needs to run elevated.");
                }

                key.SetValue(LevelValue, level, RegistryValueKind.String);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail(
                    "Access denied writing MSI Center's battery key. The helper needs to run elevated.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Could not write the charge limit: {ex.Message}");
            }

            // Read back. As with the power limits, a second process is what carries this to the
            // controller, so the registry accepting a string proves only that the string landed.
            if (!TryRead(out bool actualEnabled, out int actualPercent))
                return OpResult.Fail("Wrote the charge limit but could not read it back.");

            if (actualEnabled != enabled || (enabled && actualPercent != target))
            {
                return OpResult.Fail(
                    $"Charge limit did not stick: asked for {Describe(enabled, target)}, "
                    + $"found {Describe(actualEnabled, actualPercent)}.");
            }

            return OpResult.Success();
        }

        private static string Describe(bool enabled, int percent) =>
            enabled
                ? percent.ToString(CultureInfo.InvariantCulture) + "%"
                : "no limit";

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
