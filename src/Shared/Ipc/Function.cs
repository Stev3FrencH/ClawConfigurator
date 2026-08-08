namespace McenterLite.Shared.Ipc
{
    /// <summary>
    /// The complete set of values that can cross the widget/helper boundary.
    ///
    /// Ordinals are ASSIGNED EXPLICITLY and must never be reused or renumbered. The reference
    /// project let ~250 members take implicit ordinals and then had to append every new member
    /// at the end with a "appended to preserve prior enum ordinals" comment - a versioning
    /// hazard that only shows up as silent misrouting when an old widget meets a new helper.
    /// Gaps between groups are intentional: new members go in their group's gap.
    /// </summary>
    public enum Function
    {
        None = 0,

        // ── Lifecycle / handshake ────────────────────────────────────────────────
        /// <summary>Widget -> helper, first message on every connect. Content is the widget version.</summary>
        Hello = 1,

        /// <summary>
        /// Helper -> widget, sent once in reply to <see cref="Hello"/>. Content is a JSON object
        /// holding every readable Function plus device capabilities, so a fresh widget reaches a
        /// correct UI in ONE round trip rather than one Get per control.
        /// </summary>
        Snapshot = 2,

        /// <summary>Helper -> widget. JSON <c>DeviceCaps</c>; also embedded in <see cref="Snapshot"/>.</summary>
        DeviceCaps = 3,

        /// <summary>
        /// Widget -> helper, "1" when the widget is on screen. Gates the ~1 Hz <see cref="FanState"/>
        /// push. The widget is a UWP app and is suspended when the Game Bar is dismissed, so the
        /// helper must be told rather than inferring it.
        /// </summary>
        WidgetVisible = 4,

        /// <summary>Widget -> helper. Restore every captured original value and unregister persistence.</summary>
        PrepareForUninstall = 5,

        // ── 1. TDP / power limits ────────────────────────────────────────────────
        /// <summary>Sustained power limit, watts. Clamped server-side to the device ceiling.</summary>
        Pl1 = 10,

        /// <summary>Boost power limit, watts. Clamped server-side, and to >= PL1 + Pl2MinOffset.</summary>
        Pl2 = 11,

        /// <summary>Which backend applies TDP. See <see cref="TdpBackendKind"/>.</summary>
        TdpBackend = 12,

        /// <summary>
        /// MSI's performance mode. See <see cref="PerfMode"/>.
        /// </summary>
        /// <remarks>
        /// Lives in the TDP group because it GATES the power limits: MSI only honours
        /// <see cref="Pl1"/> and <see cref="Pl2"/> in <see cref="PerfMode.UserScenario"/>. In the
        /// other modes MSI drives power itself and the sliders do nothing.
        /// </remarks>
        PerfMode = 13,

        // ── 2. Fan presets ───────────────────────────────────────────────────────
        /// <summary>Master switch. When false the firmware's own curve drives the fan.</summary>
        FanEnabled = 20,

        /// <summary>Selected profile. See <see cref="FanPreset"/>. No custom curve is exposed.</summary>
        FanPreset = 21,

        /// <summary>
        /// Helper -> widget telemetry, pushed at ~1 Hz while the widget is visible.
        /// Content is a serialized <c>FanState</c>.
        /// </summary>
        FanState = 22,

        /// <summary>Full-speed override. A separate EC control from the curve, not a preset.</summary>
        FanFullSpeed = 23,

        // ── 3. Battery charge limit ──────────────────────────────────────────────
        ChargeLimitEnabled = 30,

        /// <summary>Charge ceiling percent. Clamped server-side to [60, 100].</summary>
        ChargeLimitPercent = 31,

        // ── 4. RGB LED ───────────────────────────────────────────────────────────
        /// <summary>Whole LED configuration as one serialized <c>LedSpec</c>.</summary>
        LedSpec = 40,

        // ── 5. Controller mode ───────────────────────────────────────────────────
        /// <summary>
        /// True = drive the controller FIRMWARE into its native desktop-mouse mode. This is a real
        /// HID mouse, so unlike software cursor injection it keeps working on the UAC secure
        /// desktop. Not persisted - the physical MSI button also changes this, so the helper polls
        /// and pushes rather than assuming it owns the state.
        /// </summary>
        HwMouseMode = 50,

        // ── 6/7. Windows power ───────────────────────────────────────────────────
        CpuBoost = 60,

        /// <summary>Windows 11 power-mode overlay. See <see cref="OsPowerMode"/>.</summary>
        OsPowerMode = 61,

        // ── 8. Intel graphics (IGCL) ─────────────────────────────────────────────
        /// <summary>Endurance Gaming tier. NOTE: IGCL applies this PER APPLICATION, not globally.</summary>
        IntelFpsTier = 70,
        IntelLowLatency = 71,
        IntelFrameSync = 72,

        /// <summary>0 = off, 1..100 = adaptive sharpening intensity.</summary>
        IntelAdaptiveSharpness = 73,

        /// <summary>0..100, 50 = neutral.</summary>
        IntelSaturation = 74,

        /// <summary>0..100, 50 = neutral.</summary>
        IntelContrast = 75,

        /// <summary>Gamma x100, 30..280, 100 = 1.0 neutral.</summary>
        IntelGamma = 76,

        // ── Coexistence ──────────────────────────────────────────────────────────
        /// <summary>Helper -> widget. MSI Center M is running and may be fighting us for the EC/HID.</summary>
        MsiCenterRunning = 80,

        /// <summary>
        /// Intel thermal-stack control. Intel's IPF/DTT owns a fan participant ABOVE the EC and can
        /// latch the fan at maximum regardless of our table. See <see cref="IntelThermalCommand"/>.
        /// This is the escape hatch and must ship before any EC write.
        /// </summary>
        IntelThermalCmd = 81,
    }
}
