using System;
using System.Runtime.Versioning;
using McenterLite.Shared.Ipc;
using Microsoft.Win32;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// Power limits, applied by writing MSI Center M's own model and letting its background
    /// service push the values to the embedded controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a persistence mirror - it is the control surface.</b> Verified on device
    /// 2026-08-07: writing these values alone, with nothing else touched, moved the sustained
    /// clock under load. MSI Center's own UI updated to match, which is what watching looks like
    /// from outside.
    /// </para>
    /// <para>
    /// Verified again with the MSI Center <b>window closed</b>: the applier is the background
    /// process <c>MSI_Center_M_Server_UserScenario</c>, not the UWP front end. So this works
    /// while MSI Center is installed and its services run, which is the arrangement this project
    /// targets. See <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// The trade is that MSI Center is an <b>active participant</b>. It watches these values, so
    /// it can also overwrite them - from its own UI, and plausibly on mode changes, AC/DC
    /// transitions and resume. Never assume the last write still stands; re-read.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class RegistryTdpProvider : ITdpProvider
    {
        private const string KeyPath =
            @"SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario";

        // Four values, not two. Which pair is live depends on whether the charger is connected,
        // and MSI's own UI wrote both identically at every captured point - so we do too, rather
        // than making the result depend on the power source at the moment of the write.
        private const string Pl1Ac = "ManualPL1AC";
        private const string Pl2Ac = "ManualPL2AC";
        private const string Pl1Dc = "ManualPL1DC";
        private const string Pl2Dc = "ManualPL2DC";

        private const string ModeValue = "Mode";

        /// <summary>
        /// The performance mode in which MSI honours the manual limits. Decoded from the mode
        /// transcripts: 3 = Endurance, 4 = User Scenario, 5 = AI Engine.
        /// </summary>
        private const int ModeUserScenario = 4;

        private readonly string _unavailableReason;

        public RegistryTdpProvider()
        {
            using var key = OpenRead();
            if (key == null)
            {
                _unavailableReason =
                    "MSI Center M is not installed, so its power-limit model does not exist.";
                return;
            }

            if (key.GetValue(Pl1Ac) is not int)
            {
                _unavailableReason =
                    "MSI Center M is installed but has not written a power-limit model yet. "
                    + "Open it once and set a power limit.";
            }
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;
        public TdpBackendKind Backend => TdpBackendKind.RegistryMirror;

        public bool TryRead(out int pl1, out int pl2)
        {
            pl1 = 0;
            pl2 = 0;
            if (!Available) return false;

            try
            {
                using var key = OpenRead();
                if (key == null) return false;

                // AC is the reference pair. Both are written together, so reading one is enough,
                // and picking a fixed one keeps the reported value stable when the charger is
                // plugged or unplugged mid-session.
                if (key.GetValue(Pl1Ac) is not int readPl1) return false;
                if (key.GetValue(Pl2Ac) is not int readPl2) return false;

                pl1 = readPl1;
                pl2 = readPl2;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public OpResult Apply(int pl1, int pl2)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            try
            {
                using var key = OpenWrite();
                if (key == null)
                {
                    return OpResult.Fail(
                        "Could not open MSI Center's power-limit key for writing. "
                        + "The helper needs to run elevated.");
                }

                key.SetValue(Pl1Ac, pl1, RegistryValueKind.DWord);
                key.SetValue(Pl2Ac, pl2, RegistryValueKind.DWord);
                key.SetValue(Pl1Dc, pl1, RegistryValueKind.DWord);
                key.SetValue(Pl2Dc, pl2, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail(
                    "Access denied writing MSI Center's power-limit key. The helper needs to run elevated.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Could not write the power limits: {ex.Message}");
            }

            // Read back. The registry accepting a DWORD proves nothing about the EC, and this
            // whole design rests on a second process noticing the change.
            if (!TryRead(out var actualPl1, out var actualPl2))
                return OpResult.Fail("Wrote the power limits but could not read them back.");

            if (actualPl1 != pl1 || actualPl2 != pl2)
            {
                return OpResult.Fail(
                    $"Power limits did not stick: asked for {pl1}/{pl2} W, found {actualPl1}/{actualPl2} W. "
                    + "MSI Center may have overwritten them.");
            }

            // Deliberately reported rather than corrected.
            //
            // Manual limits are only expected to be honoured in User Scenario mode; the other two
            // modes are MSI driving power itself. We could force Mode=4 and make the slider always
            // "work", but that silently overrides a choice the user made in MSI Center, and it has
            // side effects beyond power - entering Endurance also switches the LEDs off, so the
            // reverse is likely true too. Telling the truth is better than a hidden mode change.
            int mode = ReadMode();
            if (mode >= 0 && mode != ModeUserScenario)
            {
                return OpResult.Fail(
                    $"Saved {pl1}/{pl2} W, but MSI Center is in {DescribeMode(mode)} mode and is "
                    + "managing power itself. Switch it to User Scenario for these limits to take effect.");
            }

            return OpResult.Success();
        }

        /// <summary>The current MSI performance mode, or -1 if it cannot be read.</summary>
        private static int ReadMode()
        {
            try
            {
                using var key = OpenRead();
                return key?.GetValue(ModeValue) is int mode ? mode : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string DescribeMode(int mode) => mode switch
        {
            3 => "Endurance",
            4 => "User Scenario",
            5 => "AI Engine",
            _ => $"an automatic (mode {mode})",
        };

        // WOW6432Node is already in the path, so the 32-bit view must NOT be requested as well -
        // that would redirect to WOW6432Node\WOW6432Node and silently find nothing.
        private static RegistryKey OpenRead() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                       .OpenSubKey(KeyPath, writable: false);

        private static RegistryKey OpenWrite() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                       .OpenSubKey(KeyPath, writable: true);
    }
}
