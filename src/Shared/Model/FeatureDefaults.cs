using McenterLite.Shared.Ipc;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// The value each feature returns to when this app is asked to put the machine back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chosen values, not captured ones.</b> Until 2026-08-13 the restore replayed
    /// <c>Original_*</c> keys written the first time each feature was touched. That sounded
    /// conservative and was not: while MSI Center M was installed, "the value before we wrote"
    /// meant *whatever MSI Center M happened to hold at that arbitrary moment*, so an uninstall
    /// restored the machine to a snapshot of a program that no longer exists. The captured values on
    /// this device were 15 W / 17 W, a 60% charge limit and Best performance — none of them a
    /// neutral state, all of them accidents of timing.
    /// </para>
    /// <para>
    /// A table in code also cannot be lost. The captured originals lived in <c>settings.json</c>
    /// inside the package's LocalCache, which Windows deletes when the app is removed — so the one
    /// event they existed for was the event that destroyed them.
    /// </para>
    /// <para>
    /// <b>These are product decisions, made 2026-08-13.</b> They are not readings from the device
    /// and must not be presented as though they were. Where a value has evidence behind it, the
    /// evidence is named below.
    /// </para>
    /// </remarks>
    public static class FeatureDefaults
    {
        /// <summary>
        /// Sustained power limit, watts.
        /// </summary>
        /// <remarks>
        /// 17 W / 19 W is MSI's own "mid" pair, one of the four captures that established
        /// <c>ManualPL*</c> as watts 1:1 (see <c>hardware-notes.md</c>). It satisfies the platform's
        /// one rule, <c>PL2 >= PL1 + 2</c>, and sits mid-range in 8–35 / 10–45 rather than at either
        /// extreme — which is what a default should do on a handheld where the top of the range
        /// costs battery and the bottom costs frames.
        /// </remarks>
        public const int Pl1 = 17;

        /// <summary>Burst power limit, watts. See <see cref="Pl1"/>.</summary>
        public const int Pl2 = 19;

        /// <summary>
        /// Battery charge limit, percent. 100 means charge to full.
        /// </summary>
        /// <remarks>
        /// The firmware expresses "no limit" as 100 rather than with a separate flag, so this is
        /// genuinely off rather than a high setting. Restoring here means "as if this app never
        /// ran", and a machine that quietly stops charging at 80% after its battery app was removed
        /// would be a bug report, not a kindness.
        /// </remarks>
        public const int ChargeLimitPercent = 100;

        /// <summary>CPU boost. Windows' own default is enabled.</summary>
        public const bool CpuBoost = true;

        /// <summary>
        /// The Windows power-mode overlay. Balanced is neutral ground rather than either extreme.
        /// </summary>
        public const OsPowerMode PowerMode = OsPowerMode.Balanced;

        /// <summary>
        /// Controller mode. <c>false</c> is Gamepad, which is what the device boots as.
        /// </summary>
        /// <remarks>
        /// <b>Restored, reversing an earlier decision.</b> This used to be deliberately left alone
        /// on the reasoning that the physical MSI button owns the state as much as we do. That still
        /// describes normal running — nothing re-asserts it on a tick or at startup — but it is the
        /// wrong call for an uninstall specifically: leaving the machine in desktop-mouse mode with
        /// the app that switched it now gone strands the user in the one state where a handheld does
        /// not behave like a handheld.
        /// </remarks>
        public const bool HwMouseDesktopMode = false;

        // Fan has no constant here on purpose: its default is Auto, which is
        // FanProfile.Factory() written to both tables with the control flag cleared. That is a
        // measured constant with its own home in FanProfile, not a product choice.

        // Lighting has no RESTORE constant either, and is deliberately not restored. It lives in
        // the controller's RAM, so the state before we touched it was itself written by whatever
        // ran last, and a power cycle clears it regardless. Decision 2026-08-13: leave the lights
        // on. Turning them off on the way out would be inventing a state rather than returning one.

        /// <summary>
        /// The lighting profile a <b>fresh install</b> applies. Slot 1, seeded as "Purple".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>First-run, not restore — the two differ on purpose.</b> Uninstalling leaves the lights
        /// exactly as they are, because there is no prior state to return to. Installing is the
        /// opposite situation: the controller keeps lighting in RAM and forgets it on a power cycle,
        /// so a new install that wrote nothing would leave the LEDs on whatever the firmware
        /// defaults to while the widget's own card claimed something else.
        /// </para>
        /// <para>
        /// The <i>slot</i> is the decision, not the colour. Slot 1 is seeded as Purple, but the
        /// profile files are the user's to rename and edit, so this must never be described as "the
        /// purple default" anywhere it could later be a lie.
        /// </para>
        /// <para>
        /// <b>A restore makes the next start look like a fresh install</b>, because it clears the
        /// saved selections — so the helper will apply this profile then too. That is coherent for a
        /// reset, but it does mean "restore leaves the lights alone" holds only until the next
        /// helper start.
        /// </para>
        /// </remarks>
        public const int FirstRunLightingProfile = 1;
    }
}
