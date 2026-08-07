using System;
using McenterLite.Shared.Fan;
using McenterLite.Shared.Ipc;
using Xunit;

namespace McenterLite.Shared.Tests
{
    /// <summary>
    /// The safety-critical tests. Everything here guards a property that, if violated, sends a
    /// wrong duty table to the embedded controller of a physical device.
    /// </summary>
    public class FanProfilesTests
    {
        private static readonly int[] SampleDuties = { 40, 49, 58, 67, 75 };

        [Fact]
        public void BuildTable_MatchesTheDocumentedEcLayout()
        {
            // Layout: { 0, 0, D0, D1, D2, D3, D4, D4 }
            var table = FanProfiles.BuildTable(SampleDuties);

            Assert.Equal(8, table.Length);
            Assert.Equal(new byte[] { 0, 0, 40, 49, 58, 67, 75, 75 }, table);
        }

        [Fact]
        public void ApplyToTable_PreservesTheEcsOwnBoundaryBytes()
        {
            // A Claw 8 EX ships index 7 = 94. Index 0 and 7 are EC state, not curve points -
            // overwriting them corrupts state AND makes verification report a false mismatch.
            var current = new byte[] { 3, 0, 40, 49, 58, 67, 75, 94 };

            var written = FanProfiles.ApplyToTable(current, new[] { 20, 30, 45, 67, 75 });

            Assert.Equal(3, written[0]);   // preserved
            Assert.Equal(94, written[7]);  // preserved
            Assert.Equal(new byte[] { 0, 20, 30, 45, 67 }, new[]
            {
                written[1], written[2], written[3], written[4], written[5],
            });
            Assert.Equal(75, written[6]);
        }

        [Fact]
        public void ApplyToTable_DoesNotMutateTheInput()
        {
            var current = new byte[] { 3, 0, 40, 49, 58, 67, 75, 94 };
            var copy = (byte[])current.Clone();

            FanProfiles.ApplyToTable(current, new[] { 20, 30, 45, 67, 75 });

            Assert.Equal(copy, current);
        }

        [Fact]
        public void ApplyToTable_RejectsWrongSizedTable()
        {
            Assert.Throws<ArgumentException>(() => FanProfiles.ApplyToTable(new byte[4], SampleDuties));
            Assert.Throws<ArgumentException>(() => FanProfiles.ApplyToTable(null, SampleDuties));
        }

        [Fact]
        public void Matches_ComparesOnlyTheBytesWeWrote()
        {
            // Same curve, different boundary bytes -> still a match, because we never wrote them.
            var readback = new byte[] { 7, 0, 40, 49, 58, 67, 75, 94 };
            Assert.True(FanProfiles.Matches(readback, SampleDuties));
        }

        [Fact]
        public void Matches_DetectsAnIgnoredWrite()
        {
            // The EC can silently refuse or partially apply a table, e.g. while MSI Center holds
            // the ACPI-WMI interface. Reporting success there is the failure mode this prevents.
            var stale = new byte[] { 0, 0, 40, 49, 58, 67, 75, 75 };
            Assert.False(FanProfiles.Matches(stale, new[] { 20, 30, 45, 67, 75 }));
        }

        [Fact]
        public void Matches_RejectsShortOrMissingReadback()
        {
            Assert.False(FanProfiles.Matches(null, SampleDuties));
            Assert.False(FanProfiles.Matches(new byte[3], SampleDuties));
        }

        [Fact]
        public void ClampDuties_EnforcesTheMsiCap()
        {
            // The EC accepts up to 150, but this app never exposes that range.
            var clamped = FanProfiles.ClampDuties(new[] { 0, 90, 120, 150, 200 });
            Assert.All(clamped, d => Assert.InRange(d, 0, FanProfiles.DutyCap));
        }

        [Fact]
        public void ClampDuties_ForcesNonDecreasingCurve()
        {
            // A curve that falls as temperature rises makes the device quieter the hotter it
            // gets - the one shape that can actually cook it.
            var clamped = FanProfiles.ClampDuties(new[] { 60, 40, 50, 30, 20 });

            for (int i = 1; i < clamped.Length; i++)
                Assert.True(clamped[i] >= clamped[i - 1], $"duty fell at index {i}: {string.Join(",", clamped)}");
        }

        [Fact]
        public void ClampTemps_ForcesStrictlyAscendingAxis()
        {
            var clamped = FanProfiles.ClampTemps(new[] { 60, 60, 50, 10, 5 });

            for (int i = 1; i < clamped.Length; i++)
                Assert.True(clamped[i] > clamped[i - 1], $"axis not ascending: {string.Join(",", clamped)}");
        }

        [Fact]
        public void ClampTemps_StaysWithinDeviceRange()
        {
            var clamped = FanProfiles.ClampTemps(new[] { -50, 0, 200, 300, 400 });
            Assert.All(clamped, t => Assert.InRange(t, FanProfiles.TempMin, FanProfiles.TempMax));
        }

        [Fact]
        public void ClampTemps_LeavesRoomForEveryRemainingPoint()
        {
            // All five pinned at the ceiling still has to yield five distinct ascending values.
            var clamped = FanProfiles.ClampTemps(new[] { 99, 99, 99, 99, 99 });

            Assert.Equal(5, clamped.Length);
            for (int i = 1; i < clamped.Length; i++) Assert.True(clamped[i] > clamped[i - 1]);
            Assert.All(clamped, t => Assert.InRange(t, FanProfiles.TempMin, FanProfiles.TempMax));
        }

        [Fact]
        public void ClampDuties_RejectsWrongPointCount()
        {
            Assert.Throws<ArgumentException>(() => FanProfiles.ClampDuties(new[] { 1, 2, 3 }));
            Assert.Throws<ArgumentException>(() => FanProfiles.ClampDuties(null));
        }

        // ── Preset resolution ───────────────────────────────────────────────────

        [Fact]
        public void Resolve_DefaultUsesTheDevicesOwnCurve()
        {
            var modelTemps = new[] { 45, 55, 65, 75, 83 };
            var modelDuties = new[] { 42, 50, 60, 68, 74 };

            FanProfiles.Resolve(FanPreset.Default, modelTemps, modelDuties, out var temps, out var duties);

            Assert.Equal(modelTemps, temps);
            Assert.Equal(modelDuties, duties);
        }

        [Fact]
        public void Resolve_FallsBackWhenTheDeviceCurveIsUnavailable()
        {
            FanProfiles.Resolve(FanPreset.Default, null, null, out var temps, out var duties);

            Assert.Equal(FanProfiles.FallbackTemps(), temps);
            Assert.Equal(FanProfiles.FallbackDuties(), duties);
        }

        [Fact]
        public void Resolve_CoolingShiftsTheAxisAndUsesTheFixedDutyTable()
        {
            var modelTemps = new[] { 44, 54, 64, 74, 82 };

            FanProfiles.Resolve(FanPreset.Cooling, modelTemps, new[] { 1, 2, 3, 4, 5 },
                out var temps, out var duties);

            Assert.Equal(new[] { 34, 44, 54, 64, 72 }, temps);

            // Cooling deliberately does NOT use the model's duties - it is the fixed table on an
            // earlier axis. Passing model duties must not change that.
            Assert.Equal(FanProfiles.CoolingDuties(), duties);
        }

        [Fact]
        public void Resolve_QuietIdleKeepsTheModelAxis()
        {
            var modelTemps = new[] { 44, 54, 64, 74, 82 };

            FanProfiles.Resolve(FanPreset.QuietIdle, modelTemps, null, out var temps, out var duties);

            Assert.Equal(modelTemps, temps);
            Assert.Equal(FanProfiles.QuietIdleDuties(), duties);
        }

        [Theory]
        [InlineData(FanPreset.Default)]
        [InlineData(FanPreset.QuietIdle)]
        [InlineData(FanPreset.Cooling)]
        public void EveryPreset_ProducesASafeTable(FanPreset preset)
        {
            FanProfiles.Resolve(preset, null, null, out var temps, out var duties);

            Assert.Equal(FanProfiles.Points, temps.Length);
            Assert.Equal(FanProfiles.Points, duties.Length);

            // No preset may reach past MSI's own ceiling.
            Assert.All(duties, d => Assert.InRange(d, 0, FanProfiles.DutyCap));

            for (int i = 1; i < temps.Length; i++) Assert.True(temps[i] > temps[i - 1]);
            for (int i = 1; i < duties.Length; i++) Assert.True(duties[i] >= duties[i - 1]);

            // A table built from any preset must verify against itself, or the read-back check
            // would report a mismatch on a correct write.
            var table = FanProfiles.BuildTable(duties);
            Assert.True(FanProfiles.Matches(table, duties));
        }

        [Fact]
        public void Resolve_TreatsAnUndefinedPresetAsDefault()
        {
            FanProfiles.Resolve((FanPreset)99, null, null, out _, out var duties);
            Assert.Equal(FanProfiles.FallbackDuties(), duties);
        }

        [Fact]
        public void DescribeWriteWindow_ShowsOnlyTheWrittenBytes()
        {
            var table = new byte[] { 7, 0, 40, 49, 58, 67, 75, 94 };
            Assert.Equal("0,40,49,58,67,75", FanProfiles.DescribeWriteWindow(table));
        }

        [Fact]
        public void EnforceDutyFloor_IsANoOpWithoutAFloor()
        {
            var duties = new[] { 20, 30, 45, 67, 75 };
            Assert.Equal(duties, FanProfiles.EnforceDutyFloor(duties, 0));
        }

        [Fact]
        public void EnforceDutyFloor_RaisesToTheFloorAndKeepsTheCurveRising()
        {
            // Quiet Idle on a Claw 8 EX: the bottom three points are all under the floor of 58.
            // Flooring alone would flatten them to 58,58,58 - a dead zone - so they are re-separated.
            var result = FanProfiles.EnforceDutyFloor(FanProfiles.QuietIdleDuties(), 58);

            Assert.Equal(new[] { 58, 59, 60, 67, 75 }, result);
        }

        [Fact]
        public void EnforceDutyFloor_LetsTheCapWinOverSeparation()
        {
            // Separation must never push a duty past MSI's own ceiling, even if that leaves a
            // flat run at the top.
            var result = FanProfiles.EnforceDutyFloor(new[] { 70, 75, 75, 75, 75 }, 75);

            Assert.All(result, d => Assert.Equal(FanProfiles.DutyCap, d));
        }

        [Fact]
        public void EnforceDutyFloor_IgnoresAFloorAboveTheCap()
        {
            var result = FanProfiles.EnforceDutyFloor(new[] { 10, 20, 30, 40, 50 }, 200);
            Assert.All(result, d => Assert.InRange(d, 0, FanProfiles.DutyCap));
        }

        [Fact]
        public void Resolve_AppliesTheModelDutyFloor()
        {
            FanProfiles.Resolve(FanPreset.QuietIdle, null, null, out _, out var floored, dutyFloor: 58);
            Assert.All(floored, d => Assert.True(d >= 58));

            // Without a floor the raw preset survives, so the floor is doing the work - not a clamp.
            FanProfiles.Resolve(FanPreset.QuietIdle, null, null, out _, out var raw);
            Assert.Equal(FanProfiles.QuietIdleDuties(), raw);
        }

        [Theory]
        [InlineData(FanPreset.Default)]
        [InlineData(FanPreset.QuietIdle)]
        [InlineData(FanPreset.Cooling)]
        public void EveryPreset_StaysSafeUnderTheExDutyFloor(FanPreset preset)
        {
            FanProfiles.Resolve(preset, null, null, out var temps, out var duties, dutyFloor: 58);

            Assert.All(duties, d => Assert.InRange(d, 58, FanProfiles.DutyCap));
            for (int i = 1; i < temps.Length; i++) Assert.True(temps[i] > temps[i - 1]);
            for (int i = 1; i < duties.Length; i++) Assert.True(duties[i] >= duties[i - 1]);

            var table = FanProfiles.BuildTable(duties);
            Assert.True(FanProfiles.Matches(table, duties));
        }
    }
}
