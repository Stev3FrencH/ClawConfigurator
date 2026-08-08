namespace McenterLite.Shared.Ipc
{
    /// <summary>How TDP reaches the hardware. Resolved at runtime; see <see cref="Function.TdpBackend"/>.</summary>
    /// <summary>
    /// MSI's performance mode selector, as offered by MSI Center M.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These ordinals are OURS, not MSI's. On device MSI stores 3 = Endurance, 4 = User Scenario,
    /// 5 = AI Engine across three separate registry values that move together
    /// (<c>Mode</c>, <c>ShiftMode</c>, <c>GamingEvent</c>) - the hardware layer owns that mapping.
    /// Putting MSI's raw numbers on the wire would bake a firmware detail into a contract the
    /// widget also speaks, and it would silently change meaning if MSI ever renumbered.
    /// </para>
    /// <para>
    /// This matters more than usual because the mode <b>gates the power limits</b>: manual PL1/PL2
    /// are only honoured in <see cref="UserScenario"/>.
    /// </para>
    /// </remarks>
    public enum PerfMode
    {
        /// <summary>Battery-first. MSI manages power; manual limits are ignored.</summary>
        Endurance = 0,

        /// <summary>The only mode in which manual power limits apply.</summary>
        UserScenario = 1,

        /// <summary>MSI's automatic mode. Manual limits are ignored.</summary>
        AiEngine = 2,

        /// <summary>MSI reported a mode we do not recognise. Treated as "not User Scenario".</summary>
        Unknown = 99,
    }

    public enum TdpBackendKind
    {
        /// <summary>
        /// Prefer <see cref="RegistryMirror"/>; fall back to <see cref="Wmi"/> only if MSI Center
        /// is absent. The mirror is the primary path by project decision — this app runs alongside
        /// MSI Center M rather than replacing it, so the MSI-conform route is the supported one.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Call MSI's ACPI-WMI method directly, independent of MSI Center.
        /// Unimplemented, deliberately: it only matters if we ever want to run without MSI Center M,
        /// which is no longer a goal. Kept in the enum so wire values stay stable if that changes.
        /// </summary>
        Wmi = 1,

        /// <summary>
        /// Write MSI Center's own registry model and let its service push the values to the EC.
        /// Stays MSI-conform, but hard-requires MSI Center M to be installed AND running — with the
        /// service stopped nothing applies the values and TDP silently does nothing, which is why
        /// the widget warns when MSI Center is NOT running rather than when it is.
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
