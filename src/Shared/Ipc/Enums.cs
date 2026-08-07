namespace McenterLite.Shared.Ipc
{
    /// <summary>How TDP reaches the hardware. Resolved at runtime; see <see cref="Function.TdpBackend"/>.</summary>
    public enum TdpBackendKind
    {
        /// <summary>Probe both and pick: registry mirror while MSI Center runs, else direct WMI.</summary>
        Auto = 0,

        /// <summary>Call MSI's ACPI-WMI method directly. Independent of MSI Center.</summary>
        Wmi = 1,

        /// <summary>
        /// Write MSI Center's own registry model and let its service push the values to the EC.
        /// Stays MSI-conform, but hard-requires MSI Center M to be installed AND running.
        /// </summary>
        RegistryMirror = 2,

        /// <summary>No usable backend was found on this device. All TDP controls are disabled.</summary>
        Unavailable = 99,
    }

    /// <summary>
    /// The fan profiles this app exposes. Deliberately a short fixed list - no custom curve editor,
    /// so no user-authored duty value ever reaches the EC.
    /// </summary>
    public enum FanPreset
    {
        /// <summary>The device's own factory curve, read back from the EC at startup.</summary>
        Default = 0,

        /// <summary>Lower duty at the bottom of the curve.</summary>
        QuietIdle = 1,

        /// <summary>Factory duties on an axis shifted 10 C cooler, so the fan ramps earlier.</summary>
        Cooling = 2,
    }

    /// <summary>Windows 11 power-mode overlay (the slider under the battery flyout).</summary>
    public enum OsPowerMode
    {
        BestPowerEfficiency = 0,
        Balanced = 1,
        BestPerformance = 2,
    }

    /// <summary>
    /// Control over Intel's IPF/DTT thermal stack, which owns a fan participant above the EC.
    /// When it is active it can hold the fan at maximum no matter what table we write.
    /// </summary>
    public enum IntelThermalCommand
    {
        /// <summary>Report whether the IPF services and fan participant are running.</summary>
        Status = 0,

        /// <summary>Stop the IPF services and disable the fan participant, yielding fan control to the EC.</summary>
        Stop = 1,

        /// <summary>Restore Intel's thermal stack to its normal state.</summary>
        Start = 2,
    }

    /// <summary>LED animation. A trimmed subset - battery-tinted and sync modes are out of scope.</summary>
    public enum LedMode
    {
        Off = 0,
        Static = 1,
        Breathing = 2,
        ColorCycle = 3,
        Wave = 4,
    }
}
