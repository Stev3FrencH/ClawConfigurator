using System;
using System.Runtime.Versioning;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
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

        // Four values, not two. Which pair is live depends on whether the charger is connected, so
        // both are written identically on every apply - confirmed there is no lower ceiling MSI
        // itself enforces on battery, so there is nothing to derive the DC pair from beyond the
        // AC one.
        private const string Pl1Ac = "ManualPL1AC";
        private const string Pl2Ac = "ManualPL2AC";
        private const string Pl1Dc = "ManualPL1DC";
        private const string Pl2Dc = "ManualPL2DC";

        // MSI's mode is not one value but three that move together. Decoded from the mode
        // transcripts, verified by a full round trip through all three modes.
        private const string ModeValue = "Mode";
        private const string ShiftModeValue = "ShiftMode";
        private const string GamingEventValue = "GamingEvent";

        /// <summary>
        /// MSI's raw triples, indexed by our <see cref="PerfMode"/>.
        /// </summary>
        /// <remarks>
        /// All three are written together because all three moved together in every captured
        /// transition. Writing only <c>Mode</c> would leave MSI's model internally inconsistent,
        /// and there is no evidence it would be noticed at all.
        /// </remarks>
        private static readonly (int Mode, int ShiftMode, int GamingEvent) EnduranceTriple = (3, 3, 1);
        private static readonly (int Mode, int ShiftMode, int GamingEvent) UserScenarioTriple = (4, 6, 4);
        private static readonly (int Mode, int ShiftMode, int GamingEvent) AiEngineTriple = (5, 2, 2);

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

            // The widget no longer exposes MSI's Endurance/AI Engine/Manual picker - manual limits
            // are the only behaviour this app models, so make sure MSI is in the one mode that
            // honours them before writing. Silent on purpose: this is what the picker used to be
            // for, and there is no control left for the user to do it with themselves.
            if (TryReadMode(out var mode) && mode != PerfMode.UserScenario)
            {
                var modeResult = ApplyMode(PerfMode.UserScenario);
                if (!modeResult.Ok) return modeResult;
            }

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

            // The battery pair is verified too. It is the one the user is least likely to notice
            // going wrong - nothing on screen reflects it until they unplug.
            if (TryReadDc(out var actualDcPl1, out var actualDcPl2)
                && (actualDcPl1 != pl1 || actualDcPl2 != pl2))
            {
                return OpResult.Fail(
                    $"Battery power limits did not stick: asked for {pl1}/{pl2} W, "
                    + $"found {actualDcPl1}/{actualDcPl2} W.");
            }

            return OpResult.Success();
        }

        /// <summary>The battery pair, for verifying a write. Not exposed on the interface.</summary>
        private static bool TryReadDc(out int pl1, out int pl2)
        {
            pl1 = 0;
            pl2 = 0;

            try
            {
                using var key = OpenRead();
                if (key?.GetValue(Pl1Dc) is not int readPl1) return false;
                if (key.GetValue(Pl2Dc) is not int readPl2) return false;

                pl1 = readPl1;
                pl2 = readPl2;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryReadMode(out PerfMode mode)
        {
            mode = PerfMode.Unknown;
            if (!Available) return false;

            try
            {
                using var key = OpenRead();
                if (key?.GetValue(ModeValue) is not int raw) return false;

                mode = raw switch
                {
                    3 => PerfMode.Endurance,
                    4 => PerfMode.UserScenario,
                    5 => PerfMode.AiEngine,
                    _ => PerfMode.Unknown,
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public OpResult ApplyMode(PerfMode mode)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            if (mode == PerfMode.Unknown)
                return OpResult.Fail("Cannot switch to an unknown performance mode.");

            var triple = mode switch
            {
                PerfMode.Endurance => EnduranceTriple,
                PerfMode.AiEngine => AiEngineTriple,
                _ => UserScenarioTriple,
            };

            try
            {
                using var key = OpenWrite();
                if (key == null)
                {
                    return OpResult.Fail(
                        "Could not open MSI Center's key for writing. The helper needs to run elevated.");
                }

                key.SetValue(ModeValue, triple.Mode, RegistryValueKind.DWord);
                key.SetValue(ShiftModeValue, triple.ShiftMode, RegistryValueKind.DWord);
                key.SetValue(GamingEventValue, triple.GamingEvent, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail(
                    "Access denied writing MSI Center's key. The helper needs to run elevated.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Could not change the performance mode: {ex.Message}");
            }

            if (!TryReadMode(out var actual))
                return OpResult.Fail("Changed the performance mode but could not read it back.");

            if (actual != mode)
                return OpResult.Fail($"Performance mode did not stick: asked for {mode}, found {actual}.");

            return OpResult.Success();
        }

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
