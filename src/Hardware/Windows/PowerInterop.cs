using System;
using System.Runtime.InteropServices;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// P/Invoke for the Windows power-scheme APIs in <c>powrprof.dll</c>.
    ///
    /// <para>
    /// Written from Microsoft's published documentation for these functions. Nothing here is
    /// derived from another project - see <c>LICENSE-NOTES.md</c> for why that distinction is
    /// enforced rather than assumed.
    /// </para>
    /// </summary>
    internal static class PowerInterop
    {
        private const string Dll = "powrprof.dll";

        /// <summary>All of these return a Win32 error code, where 0 is success - NOT a BOOL.</summary>
        internal const uint ErrorSuccess = 0;

        /// <summary>
        /// Retrieves the active power scheme. The returned pointer is allocated by the API and
        /// must be released with <see cref="LocalFree"/>.
        /// </summary>
        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerReadACValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subGroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint acValueIndex);

        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerReadDCValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subGroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint dcValueIndex);

        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerWriteACValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subGroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            uint acValueIndex);

        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subGroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            uint dcValueIndex);

        /// <summary>
        /// Sets the power-mode overlay - the slider in the Windows battery flyout. This sits on
        /// top of the active scheme rather than replacing it.
        /// </summary>
        /// <remarks>
        /// Not in the public SDK headers, but a stable, long-standing export that
        /// <c>powercfg /overlaysetscheme</c> itself uses. Failure is handled, not assumed away.
        /// </remarks>
        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

        /// <summary>Reads the overlay currently in effect. Same caveat as the setter.</summary>
        [DllImport(Dll, ExactSpelling = true)]
        internal static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>Documented, in the public SDK headers (winbase.h) - unlike the overlay pair above.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }

    /// <summary>Subset of the fields in Win32's SYSTEM_POWER_STATUS. Only ACLineStatus is used here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        /// <summary>0 = offline (running on battery), 1 = online (AC/plugged in), 255 = unknown.</summary>
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>
    /// Power-setting GUIDs.
    ///
    /// <para>
    /// These are published by Microsoft and are visible on any Windows machine via
    /// <c>powercfg /q</c>, which is where they were taken from - they are facts about Windows,
    /// not anyone's source code.
    /// </para>
    /// </summary>
    internal static class PowerGuids
    {
        /// <summary>Subgroup: Processor power management.</summary>
        internal static Guid ProcessorSettingsSubgroup =
            new Guid("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>Setting: Processor performance boost mode (<c>PERFBOOSTMODE</c>).</summary>
        internal static Guid ProcessorPerfBoostMode =
            new Guid("be337238-0d82-4146-a960-4f3749d470c7");

        // ── Power-mode overlays ─────────────────────────────────────────────────
        internal static readonly Guid OverlayBestPowerEfficiency =
            new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");

        /// <summary>Balanced is the all-zero GUID - it means "no overlay", not a scheme of its own.</summary>
        internal static readonly Guid OverlayBalanced = Guid.Empty;

        internal static readonly Guid OverlayBestPerformance =
            new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
    }

    /// <summary>Values accepted by <see cref="PowerGuids.ProcessorPerfBoostMode"/>.</summary>
    internal enum PerfBoostMode : uint
    {
        Disabled = 0,
        Enabled = 1,
        Aggressive = 2,
        EfficientEnabled = 3,
        EfficientAggressive = 4,
        AggressiveAtGuaranteed = 5,
        EfficientAggressiveAtGuaranteed = 6,
    }
}
