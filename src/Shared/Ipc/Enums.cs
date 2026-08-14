namespace McenterLite.Shared.Ipc
{
    /// <summary>
    /// The performance mode, which decides whether manual power limits mean anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Removed and restored on 2026-08-13, and the round trip is the useful part.</b> It was
    /// deleted with the registry-mirror backend on the recorded belief that "the WMI path writes the
    /// EC directly and is not gated by MSI's triple at all". That was wrong. The mode is not MSI
    /// Center M's idea — it lives in the firmware, at <c>Get_AP</c>/<c>Set_AP</c> sub-function 0,
    /// byte 3, low nibble, and it gates <c>Get_SlaveBattery</c>'s manual PL1/PL2 exactly as MSI's
    /// registry model implied. Uninstalling MSI Center M did not remove the mode; it removed the
    /// service that had been setting it to User Scenario on our behalf at every boot.
    /// </para>
    /// <para>
    /// These ordinals are OURS and unchanged from the original enum. The firmware's own encoding is
    /// a different set of numbers (6 = User Scenario, 2 = Endurance, 1 = AI Engine) and the hardware
    /// layer owns that mapping — putting the firmware's numbers on the wire would bake an EC detail
    /// into a contract the widget also speaks.
    /// </para>
    /// <para>
    /// The wire ordinal <c>Function.PerfMode = 13</c> stays RETIRED. This returns on <b>14</b>,
    /// under the same rule that brought fan control, the charge limit and lighting back on new
    /// numbers: an old widget meeting a new helper must not route a stale message onto a live one.
    /// </para>
    /// </remarks>
    public enum PerfMode
    {
        /// <summary>Battery-first. MSI manages power; manual limits are ignored.</summary>
        Endurance = 0,

        /// <summary>The only mode in which manual power limits apply. MSI calls it User Scenario.</summary>
        UserScenario = 1,

        /// <summary>MSI's automatic mode. Manual limits are ignored.</summary>
        AiEngine = 2,

        /// <summary>The firmware reported a nibble we do not recognise. Treated as "not manual".</summary>
        Unknown = 99,
    }

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
