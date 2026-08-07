using System;
using McenterLite.Shared.Ipc;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// CPU boost and the Windows power-mode overlay, via documented Win32.
    ///
    /// <para>
    /// The only provider in this project that needs no MSI hardware, no vendor HID and no
    /// reverse-engineering. It therefore ships and is verifiable in a plain Windows VM, well
    /// before the handheld is involved.
    /// </para>
    /// </summary>
    public sealed class WindowsPowerProvider : IPowerProvider
    {
        /// <summary>
        /// What "boost on" writes. Aggressive rather than Enabled because on this class of
        /// hardware Enabled is close enough to Disabled to make the toggle look broken.
        /// </summary>
        private const PerfBoostMode BoostOn = PerfBoostMode.Aggressive;

        private const PerfBoostMode BoostOff = PerfBoostMode.Disabled;

        public bool Available => OperatingSystem.IsWindows();

        public string UnavailableReason => Available ? null : "Windows power APIs are not available on this platform.";

        // ── CPU boost ───────────────────────────────────────────────────────────

        public bool TryReadCpuBoost(out bool enabled)
        {
            enabled = false;
            if (!Available) return false;

            if (!TryGetActiveScheme(out var scheme)) return false;

            var subgroup = PowerGuids.ProcessorSettingsSubgroup;
            var setting = PowerGuids.ProcessorPerfBoostMode;

            // Read the AC side: it is the one the user is choosing when they flip the toggle
            // while docked, and we always write both.
            uint rc = PowerInterop.PowerReadACValueIndex(
                IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint value);

            if (rc != PowerInterop.ErrorSuccess) return false;

            enabled = value != (uint)PerfBoostMode.Disabled;
            return true;
        }

        public OpResult ApplyCpuBoost(bool enabled)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);

            if (!TryGetActiveScheme(out var scheme))
                return OpResult.Fail("Could not read the active power scheme.");

            var subgroup = PowerGuids.ProcessorSettingsSubgroup;
            var setting = PowerGuids.ProcessorPerfBoostMode;
            uint value = (uint)(enabled ? BoostOn : BoostOff);

            // Write BOTH power sources. Writing only AC produces a toggle that appears to revert
            // itself the moment the device is unplugged.
            uint acRc = PowerInterop.PowerWriteACValueIndex(
                IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
            if (acRc != PowerInterop.ErrorSuccess)
                return OpResult.Fail($"Failed to write the AC boost mode (error {acRc}).");

            uint dcRc = PowerInterop.PowerWriteDCValueIndex(
                IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
            if (dcRc != PowerInterop.ErrorSuccess)
                return OpResult.Fail($"Failed to write the DC boost mode (error {dcRc}).");

            // A written value does not take effect until the scheme is re-activated. Skipping
            // this is the classic reason a powercfg change "does nothing" until reboot.
            uint activateRc = PowerInterop.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (activateRc != PowerInterop.ErrorSuccess)
                return OpResult.Fail($"Failed to re-activate the power scheme (error {activateRc}).");

            if (TryReadCpuBoost(out bool actual) && actual != enabled)
                return OpResult.Fail("The system did not accept the boost mode change.");

            return OpResult.Success();
        }

        // ── OS power mode ───────────────────────────────────────────────────────

        public bool TryReadPowerMode(out OsPowerMode mode)
        {
            mode = OsPowerMode.Balanced;
            if (!Available) return false;

            uint rc = PowerInterop.PowerGetEffectiveOverlayScheme(out Guid overlay);
            if (rc != PowerInterop.ErrorSuccess) return false;

            if (overlay == PowerGuids.OverlayBestPowerEfficiency) mode = OsPowerMode.BestPowerEfficiency;
            else if (overlay == PowerGuids.OverlayBestPerformance) mode = OsPowerMode.BestPerformance;
            else mode = OsPowerMode.Balanced; // includes Guid.Empty, which means "no overlay"

            return true;
        }

        public OpResult ApplyPowerMode(OsPowerMode mode)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);

            Guid overlay;
            switch (mode)
            {
                case OsPowerMode.BestPowerEfficiency: overlay = PowerGuids.OverlayBestPowerEfficiency; break;
                case OsPowerMode.BestPerformance: overlay = PowerGuids.OverlayBestPerformance; break;
                case OsPowerMode.Balanced: overlay = PowerGuids.OverlayBalanced; break;
                default: return OpResult.Fail($"Unknown power mode {(int)mode}.");
            }

            uint rc = PowerInterop.PowerSetActiveOverlayScheme(overlay);
            if (rc != PowerInterop.ErrorSuccess)
                return OpResult.Fail($"Failed to set the power-mode overlay (error {rc}).");

            if (TryReadPowerMode(out var actual) && actual != mode)
                return OpResult.Fail("The system did not accept the power-mode change.");

            return OpResult.Success();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the active scheme GUID, releasing the buffer the API allocates for it.
        /// </summary>
        private static bool TryGetActiveScheme(out Guid scheme)
        {
            scheme = Guid.Empty;

            uint rc = PowerInterop.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr);
            if (rc != PowerInterop.ErrorSuccess || ptr == IntPtr.Zero) return false;

            try
            {
                scheme = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(ptr);
                return true;
            }
            finally
            {
                // The API allocates this with LocalAlloc; leaking it leaks on every poll.
                PowerInterop.LocalFree(ptr);
            }
        }
    }
}
