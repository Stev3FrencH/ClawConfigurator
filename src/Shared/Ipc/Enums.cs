namespace McenterLite.Shared.Ipc
{
    // The PerfMode enum - Endurance / UserScenario / AiEngine / Unknown - was REMOVED on
    // 2026-08-13 along with the registry-mirror backend it described. It modelled MSI Center M's
    // own performance-mode selector, which mattered only because that backend's manual power
    // limits were honoured in User Scenario alone. The WMI path writes the EC directly and is not
    // gated by MSI's triple at all, so with the mirror gone there is no mode left to model.
    //
    // Its wire ordinal, Function.PerfMode = 13, is RETIRED and must never be reused.

    /// <summary>How TDP reaches the hardware. Resolved at runtime; see <see cref="Function.TdpBackend"/>.</summary>
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

        // RegistryMirror = 2 is RETIRED and must never be reused. It wrote MSI Center's own
        // registry model and let its service push the values to the EC, so it hard-required MSI
        // Center M installed AND running. Removed 2026-08-13: MSI Center M is uninstalled, and on
        // the first boot without it the helper resolved Wmi anyway - the mirror was never reached
        // and could not have worked if it had been.

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
