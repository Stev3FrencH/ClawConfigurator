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
        /// Widget -> helper, "1" when the widget is on screen. Gates the ~1 Hz telemetry push (now
        /// just <see cref="OsPowerMode"/>). The widget is a UWP app and is suspended when the Game
        /// Bar is dismissed, so the helper must be told rather than inferring it.
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

        // ── 2. Fan presets — REMOVED ─────────────────────────────────────────────
        //
        // Ordinals 20 (FanEnabled), 21 (FanPreset), 22 (FanState) and 23 (FanFullSpeed) are
        // RETIRED and must never be reused.
        //
        // Descoped 2026-08-08. Fan control stays in MSI Center. Gate G2 never resolved the byte
        // layout - the six-point curve MSI ships could not be reconciled with the five-point model
        // the desk research described - so nothing was ever written to the EC. Findings kept in
        // docs/hardware-notes.md.

        // ── 3. Battery charge limit — REMOVED ────────────────────────────────────
        //
        // Ordinals 30 (ChargeLimitEnabled) and 31 (ChargeLimitPercent) are RETIRED. Per the rule
        // at the top of this enum they must never be reused: an old widget meeting a new helper
        // would otherwise route a stale charge-limit message onto whatever took the number.
        //
        // Descoped 2026-08-08. The limit is set in MSI Center, changes rarely, and the registry
        // path this app could reach did not enforce it. The hardware findings are kept in
        // docs/hardware-notes.md Gate G3 rather than thrown away.

        // ── 4. Lighting — REMOVED ────────────────────────────────────────────────
        //
        // Ordinal 40 (LedEnabled) is RETIRED and must never be reused, for the same reason as
        // 30/31 above.
        //
        // Descoped 2026-08-08. MSI Center's own lighting control is far more capable than the
        // on/off switch this app could offer - mode, colour and effect all ride a vendor HID
        // report that was never decoded (Gate G4). A toggle next to that is not worth the
        // surface. Findings kept in docs/hardware-notes.md.

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

        // Ordinal 81 (IntelThermalCmd) is RETIRED. It existed only to stop Intel's IPF/DTT thermal
        // stack from latching the fan above any table we wrote - an escape hatch for EC fan writes.
        // With fan control removed there are no EC writes, so it guards nothing.
    }
}
