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

        // ── 2. Fan control ───────────────────────────────────────────────────────
        //
        // Ordinals 20 (FanEnabled), 21 (FanPreset), 22 (FanState) and 23 (FanFullSpeed) are
        // RETIRED and must never be reused. They belonged to the 2026-08-08 preset model, which
        // described a five-point table on a different device and was never written to hardware.
        // The feature came back on 2026-08-12 at NEW ordinals, per the rule at the top of this
        // enum - an old widget meeting a new helper would otherwise route a stale fan message onto
        // whatever took the number, and that number now reaches an embedded controller.

        /// <summary>
        /// Which fan profile is selected: 0 = Auto (MSI's factory curve), 1 = the custom profile.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <b>Set</b> is the Apply button. Choosing between Auto and Custom in the widget changes
        /// nothing on the device until this is sent, which is the whole shape of the card: a fan
        /// curve is not something that should follow a control as it is being dragged.
        /// </para>
        /// <para>
        /// The custom profile's file is read at the moment this arrives, so editing it and pressing
        /// Apply needs nothing restarted. See <c>FanProfileStore</c>.
        /// </para>
        /// <para>
        /// <b>Answered from the hardware, not from settings.</b> Unlike <see cref="LightingProfile"/>
        /// the device really does hold the value - the firmware runs whatever table it was last
        /// given. The helper reports Auto when both fans match the factory table and Custom
        /// otherwise, so a curve MSI Center M overwrote shows up as a changed selection rather than
        /// as our own stale optimism.
        /// </para>
        /// </remarks>
        FanProfile = 24,

        /// <summary>Helper -> widget. The custom profile's name, for its button.</summary>
        /// <remarks>
        /// Read-only and re-read from disk on every <see cref="Snapshot"/>, because the user renames
        /// it by editing the file - so this cannot live in <see cref="DeviceCaps"/> with the values
        /// that never change. Same arrangement as <see cref="LightingProfileNames"/>.
        /// </remarks>
        FanProfileName = 25,

        /// <summary>
        /// Helper -> widget. "1" when the profile about to be applied stops a fan.
        /// </summary>
        /// <remarks>
        /// The firmware enforces no duty floor - an all-zero table was accepted on this device with
        /// both tachometers reading zero - and neither this app nor MSI Center M refuses one. So the
        /// widget must be able to SAY so. Sent as its own value rather than folded into the name,
        /// because a warning that arrives as part of a label cannot be styled as a warning.
        /// </remarks>
        FanProfileStopsAFan = 26,

        // ── 3. Battery charge limit ──────────────────────────────────────────────
        //
        // Ordinals 30 (ChargeLimitEnabled) and 31 (ChargeLimitPercent) are RETIRED and must never
        // be reused, per the rule at the top of this enum - an old widget meeting a new helper
        // would otherwise route a stale charge-limit message onto whatever took the number. The
        // feature came back on 2026-08-12 at a NEW ordinal for exactly that reason.

        /// <summary>
        /// Stop charging at this percentage. 20-100; 100 means no limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ONE value, not the enabled/percent pair the retired ordinals used. The firmware has a
        /// single byte and no separate on/off - 100% is how "off" is expressed - so a second flag
        /// would be a UI concept the hardware does not have, and two functions that can disagree.
        /// </para>
        /// <para>
        /// Applied through <c>MSI_ACPI.Set_AP</c>, which needs nothing from MSI Center M. Note MSI
        /// Center M does NOT notice changes made this way and keeps showing its own cached value;
        /// see gate G3 in docs/hardware-notes.md.
        /// </para>
        /// </remarks>
        ChargeLimitPercent = 32,

        // ── 4. Lighting ──────────────────────────────────────────────────────────
        //
        // Ordinal 40 (LedEnabled) is RETIRED and must never be reused, for the same reason as
        // 30/31 above. The feature came back on 2026-08-12 at a NEW ordinal.
        //
        // The 2026-08-08 reasoning for dropping it - "mode, colour and effect all ride a vendor
        // HID report that was never decoded" - expired when G4 decoded that report.

        /// <summary>
        /// Which lighting profile is selected: 0 = off, 1-3 = the profile of that number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A slot number rather than a colour, because the widget deliberately has no colour
        /// picker: the three profiles are text files the user edits, and the widget only chooses
        /// between them. See <c>LightingProfileStore</c>.
        /// </para>
        /// <para>
        /// <b>This value is the helper's, not the device's.</b> The controller stores flattened
        /// keyframes with no profile number in them, so there is nothing to read back - two
        /// profiles could even render identically. The helper persists the slot and re-applies it
        /// at startup, and a <see cref="Snapshot"/> reports what the helper last set.
        /// </para>
        /// </remarks>
        LightingProfile = 41,

        /// <summary>
        /// Helper -> widget. The three profile names, joined by U+001F, for the buttons.
        /// </summary>
        /// <remarks>
        /// Read-only and re-read from disk on every <see cref="Snapshot"/>, because the user
        /// renames a profile by editing its file - so this cannot live in <see cref="DeviceCaps"/>
        /// with the values that never change. U+001F is safe inside the payload: the envelope
        /// escapes control characters when it serializes.
        /// </remarks>
        LightingProfileNames = 42,

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
