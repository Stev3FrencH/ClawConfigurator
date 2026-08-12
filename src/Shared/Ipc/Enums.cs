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
        /// <summary>Unused today. Kept so wire values stay stable if an auto-selection value is ever needed.</summary>
        Auto = 0,

        /// <summary>
        /// Call MSI's ACPI-WMI method (<c>MSI_ACPI.Get_SlaveBattery</c>/<c>Set_SlaveBattery</c>)
        /// directly, independent of MSI Center M. The preferred backend: confirmed 2026-08-11 to
        /// write the EC with MSI Center M's entire user-mode stack stopped, on both AC and
        /// battery. See <c>WmiTdpProvider</c> and <c>docs/hardware-notes.md</c>, Gate G1.
        /// </summary>
        Wmi = 1,

        /// <summary>
        /// Write MSI Center's own registry model and let its service push the values to the EC.
        /// The fallback when <see cref="Wmi"/> is unavailable. Hard-requires MSI Center M to be
        /// installed AND running — with the service stopped nothing applies the values and TDP
        /// silently does nothing, which is why the widget warns when MSI Center is NOT running
        /// rather than when it is.
        /// </summary>
        RegistryMirror = 2,

        /// <summary>No usable backend was found on this device. All TDP controls are disabled.</summary>
        Unavailable = 99,
    }

    /// <summary>Windows 11 power-mode overlay (the slider under the battery flyout).</summary>
    public enum OsPowerMode
    {
        BestPowerEfficiency = 0,
        Balanced = 1,
        BestPerformance = 2,
    }
}
