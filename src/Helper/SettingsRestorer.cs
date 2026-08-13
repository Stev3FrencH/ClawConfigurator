using System;
using System.Collections.Generic;
using McenterLite.Hardware;
using McenterLite.Shared.Model;

namespace McenterLite.Helper
{
    /// <summary>
    /// Puts the machine back to <see cref="FeatureDefaults"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared by the two callers that need it, and reachable from only one until 2026-08-13.</b>
    /// This logic lived inside the dispatcher, where the single way in was the
    /// <see cref="McenterLite.Shared.Ipc.Function.PrepareForUninstall"/> message — which nothing but
    /// <c>Diagnostics/Test-Helper.ps1 -Restore</c> ever sent. The <c>--uninstall</c> role, the case
    /// this exists for, tore down the task and the deployed copy and restored nothing at all, while
    /// the README promised that it did.
    /// </para>
    /// <para>
    /// <b>Writes are unconditional.</b> Every value here is applied whether or not the user ever
    /// changed it through this app. The old behaviour — restore only what was captured — sounds
    /// careful but leaves the machine wherever anything else put it, and MSI Center M is now
    /// uninstalled, so "anything else" no longer exists to put it back.
    /// </para>
    /// <para>
    /// <b>Two features are deliberately absent.</b> Lighting is not restored: it lives in the
    /// controller's RAM and a power cycle clears it, so there is no prior state to return to.
    /// Fan is not restored here because Auto — the factory table with the control flag cleared — is
    /// applied through <see cref="IFanProvider"/> below like any other profile rather than through a
    /// constant of its own.
    /// </para>
    /// </remarks>
    internal static class SettingsRestorer
    {
        /// <summary>
        /// Applies every default. Returns one message per failure; empty means clean.
        /// </summary>
        /// <remarks>
        /// One failure does not stop the rest. A restore that gave up on the first unavailable
        /// provider would leave the machine half-returned, which is worse than either extreme, and
        /// the caller needs the whole list to tell the user what is still theirs to fix.
        /// </remarks>
        public static List<string> RestoreAll(IHardware hardware, Action<string> log = null)
        {
            if (hardware == null) throw new ArgumentNullException(nameof(hardware));

            var problems = new List<string>();

            // Power limits. Without this the device keeps whatever limit was last set forever,
            // including after an uninstall, and the user has no way back.
            if (hardware.Tdp.Available)
            {
                int pl1 = FeatureDefaults.Pl1;
                int pl2 = FeatureDefaults.Pl2;
                hardware.Caps.ClampPowerLimits(ref pl1, ref pl2);

                Record(problems, log, "power limits",
                    hardware.Tdp.Apply(pl1, pl2), $"Restored PL1={pl1}W PL2={pl2}W.");
            }

            // Charge limit. Persists in firmware and outlives the app, which matters more now that
            // MSI Center M is gone and nothing else can put it back.
            if (hardware.ChargeLimit.Available)
            {
                int percent = FeatureDefaults.ChargeLimitPercent;
                hardware.Caps.ClampChargeLimit(ref percent);

                Record(problems, log, "charge limit",
                    hardware.ChargeLimit.Apply(percent), $"Restored charge limit = {percent}%.");
            }

            // Fan. Auto is both the default and the restore: it writes MSI's factory table back AND
            // hands the fans to the firmware, so nothing of ours is left in a register for whatever
            // sets the control flag next.
            if (hardware.Fan.Available)
            {
                Record(problems, log, "fan profile",
                    hardware.Fan.Apply(FanProfile.Factory(), customCurve: false),
                    "Restored the factory fan table; fans handed back to the firmware.");
            }

            // Controller mode. Restored on uninstall only - see FeatureDefaults.HwMouseDesktopMode
            // for why this reverses the earlier "the button owns it" decision.
            if (hardware.HwMouse.Available)
            {
                Record(problems, log, "controller mode",
                    hardware.HwMouse.Apply(FeatureDefaults.HwMouseDesktopMode),
                    "Restored controller mode = Gamepad.");
            }

            Record(problems, log, "CPU boost",
                hardware.Power.ApplyCpuBoost(FeatureDefaults.CpuBoost),
                $"Restored CPU boost = {FeatureDefaults.CpuBoost}.");

            Record(problems, log, "power mode",
                hardware.Power.ApplyPowerMode(FeatureDefaults.PowerMode),
                $"Restored power mode = {FeatureDefaults.PowerMode}.");

            return problems;
        }

        private static void Record(
            List<string> problems, Action<string> log, string what, OpResult result, string success)
        {
            if (result.Ok) log?.Invoke(success);
            else problems.Add($"{what}: {result.Error}");
        }
    }
}
