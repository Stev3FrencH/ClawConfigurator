using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
using Xunit;

namespace McenterLite.Shared.Tests
{
    /// <summary>
    /// Pins <see cref="FeatureDefaults"/> — the values an uninstall puts back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are <b>product decisions taken on 2026-08-13</b>, not readings from the device, so a
    /// failure here means someone changed a decision rather than that the hardware moved. That is
    /// exactly why they are pinned: the restore runs once, at uninstall, on a machine the user is
    /// walking away from, and a silent drift in these numbers is a thing nobody would notice until
    /// it had already happened.
    /// </para>
    /// <para>
    /// The second group is the one that matters most. A default that violates the platform's own
    /// rules would be clamped on the way out, so the machine would end up somewhere nobody chose.
    /// </para>
    /// </remarks>
    public class FeatureDefaultsTests
    {
        // ── The decisions ────────────────────────────────────────────────────────

        [Fact]
        public void PowerLimits_AreMsisMidPair()
        {
            // 17/19 is one of the four captures that established ManualPL* as watts 1:1.
            Assert.Equal(17, FeatureDefaults.Pl1);
            Assert.Equal(19, FeatureDefaults.Pl2);
        }

        [Fact]
        public void ChargeLimit_IsFullCharge()
        {
            // The firmware expresses "no limit" as 100 rather than with a separate flag, so this is
            // genuinely off. A machine that quietly stopped charging at 80% after its battery app
            // was removed would be a bug report, not a kindness.
            Assert.Equal(100, FeatureDefaults.ChargeLimitPercent);
        }

        [Fact]
        public void WindowsSettings_AreWindowsOwnDefaults()
        {
            Assert.True(FeatureDefaults.CpuBoost);
            Assert.Equal(OsPowerMode.Balanced, FeatureDefaults.PowerMode);
        }

        [Fact]
        public void ControllerMode_IsGamepad()
        {
            // false is Gamepad. Restored on uninstall only - leaving the machine in desktop-mouse
            // mode with the app that switched it now gone strands the user in the one state where a
            // handheld does not behave like a handheld.
            Assert.False(FeatureDefaults.HwMouseDesktopMode);
        }

        // ── The defaults must survive the clamps ─────────────────────────────────

        [Fact]
        public void PowerLimits_PassTheClampUnchanged()
        {
            // A default outside the platform rules would be silently corrected on the way out, and
            // the device would land on a value nobody chose.
            var caps = new DeviceCaps { Supported = true };

            int pl1 = FeatureDefaults.Pl1;
            int pl2 = FeatureDefaults.Pl2;
            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(FeatureDefaults.Pl1, pl1);
            Assert.Equal(FeatureDefaults.Pl2, pl2);
        }

        [Fact]
        public void PowerLimits_HonourThePl2Offset()
        {
            var caps = new DeviceCaps();
            Assert.True(FeatureDefaults.Pl2 - FeatureDefaults.Pl1 >= caps.Pl2MinOffset);
        }

        [Fact]
        public void PowerLimits_AreInsideTheDeviceRange()
        {
            var caps = new DeviceCaps();

            Assert.InRange(FeatureDefaults.Pl1, caps.MinPl1, caps.MaxPl1);
            Assert.InRange(FeatureDefaults.Pl2, caps.MinPl1 + caps.Pl2MinOffset, caps.MaxPl2);
        }

        [Fact]
        public void ChargeLimit_PassesTheClampUnchanged()
        {
            var caps = new DeviceCaps { Supported = true };

            int percent = FeatureDefaults.ChargeLimitPercent;
            caps.ClampChargeLimit(ref percent);

            Assert.Equal(FeatureDefaults.ChargeLimitPercent, percent);
        }

        // ── What is deliberately absent ──────────────────────────────────────────

        [Fact]
        public void FirstRunLightingProfile_IsARealSlot()
        {
            // Must be a profile slot, never OffSlot: a fresh install choosing "off" would look
            // identical to lighting being broken.
            Assert.InRange(
                FeatureDefaults.FirstRunLightingProfile, 1, LightingProfileStore.ProfileCount);
            Assert.NotEqual(LightingProfileStore.OffSlot, FeatureDefaults.FirstRunLightingProfile);
        }

        [Fact]
        public void FirstRunLightingProfile_IsSeededAsPurple()
        {
            // The SLOT is the decision, not the colour - the profile files are the user's to
            // rename and edit. This pins what a fresh install gets out of the box, and would catch
            // the seeded defaults being reordered underneath it.
            Assert.Equal("Purple", LightingProfile.Default(FeatureDefaults.FirstRunLightingProfile).Name);
        }

        [Fact]
        public void FanDefault_IsTheFactoryTable()
        {
            // Fan has no constant of its own: Auto is FanProfile.Factory() with the control flag
            // cleared, a measured constant rather than a product choice. This test exists so that
            // stays true - if someone adds a FeatureDefaults fan curve, the two would drift.
            var factory = FanProfile.Factory();

            Assert.Equal(new[] { 58, 70, 74, 76, 78, 80, 84 }, factory.Duties(1));
            Assert.Equal(new[] { 58, 70, 74, 76, 78, 80, 84 }, factory.Duties(2));
            Assert.False(factory.StopsAFan);
        }
    }
}
