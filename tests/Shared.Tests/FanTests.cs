using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McenterLite.Shared.Model;
using Xunit;

namespace McenterLite.Shared.Tests
{
    /// <summary>
    /// Pins <see cref="FanProfile"/> to the firmware table measured in gate G2, and to the
    /// behaviour a hand-edited profile file has to have.
    /// </summary>
    /// <remarks>
    /// The factory numbers here are not design choices - they were read off this device on
    /// 2026-08-12 and cross-checked against MSI Center M's own <c>Default_Fan</c> registry value. A
    /// failure in the first group means we have drifted from the hardware, not that the expected
    /// values need updating.
    /// </remarks>
    public class FanTests
    {
        // ── The device-measured facts ────────────────────────────────────────────

        [Fact]
        public void FactoryTable_IsWhatTheDeviceReportedInAuto()
        {
            // Read with --fan on 2026-08-12: idle 58, then the six points, on BOTH fans.
            var factory = FanProfile.Factory();

            Assert.Equal(new[] { 58, 70, 74, 76, 78, 80, 84 }, factory.Duties(1));
            Assert.Equal(new[] { 58, 70, 74, 76, 78, 80, 84 }, factory.Duties(2));
        }

        [Fact]
        public void Breakpoints_AreTheSixFixedTemperatures()
        {
            // From Get_Temperature, identical in every state MSI Center M could produce. Six
            // temperatures against seven duties: the extra duty is the idle band below the first.
            Assert.Equal(new[] { 47, 50, 57, 64, 71, 78 }, FanProfile.Breakpoints);
            Assert.Equal(FanProfile.DutyCount, FanProfile.Breakpoints.Length + 1);
        }

        [Fact]
        public void FactoryProfile_NeverStopsAFan()
        {
            Assert.False(FanProfile.Factory().StopsAFan);
        }

        // ── The seeded default ───────────────────────────────────────────────────

        [Fact]
        public void Default_IsNotTheFactoryCurve()
        {
            // A Custom profile identical to Auto would make the card's two buttons do the same
            // thing until the user found the file, which reads as a broken feature.
            var custom = FanProfile.Default();
            var factory = FanProfile.Factory();

            Assert.NotEqual(factory.Duties(1), custom.Duties(1));
        }

        [Fact]
        public void Default_NeverStopsAFan()
        {
            // The one profile we author ourselves must not be the one that can stop a fan.
            Assert.False(FanProfile.Default().StopsAFan);
            Assert.DoesNotContain(0, FanProfile.Default().Duties(1));
            Assert.DoesNotContain(0, FanProfile.Default().Duties(2));
        }

        [Fact]
        public void Default_RoundTripsThroughItsOwnFileFormat()
        {
            var original = FanProfile.Default();

            var reparsed = FanProfile.Parse(original.Format(), out var problems);

            Assert.Empty(problems);
            Assert.Equal(original.Name, reparsed.Name);
            Assert.Equal(original.Duties(1), reparsed.Duties(1));
            Assert.Equal(original.Duties(2), reparsed.Duties(2));
        }

        // ── Reading a file someone typed ─────────────────────────────────────────

        [Fact]
        public void Fan_SetsBothFansAtOnce()
        {
            var profile = FanProfile.Parse(
                "FanIdle = 25\nFan = 30;40;50;60;70;80", out var problems);

            Assert.Empty(problems);
            Assert.Equal(new[] { 25, 30, 40, 50, 60, 70, 80 }, profile.Duties(1));
            Assert.Equal(new[] { 25, 30, 40, 50, 60, 70, 80 }, profile.Duties(2));
        }

        [Fact]
        public void Fan1AndFan2_CanDiffer()
        {
            var profile = FanProfile.Parse(
                "Fan1Idle = 20\nFan1 = 30;40;50;60;70;80\n"
                + "Fan2Idle = 25\nFan2 = 35;45;55;65;75;85", out var problems);

            Assert.Empty(problems);
            Assert.Equal(new[] { 20, 30, 40, 50, 60, 70, 80 }, profile.Duties(1));
            Assert.Equal(new[] { 25, 35, 45, 55, 65, 75, 85 }, profile.Duties(2));
        }

        [Fact]
        public void CommentsAndBlankLines_AreIgnored()
        {
            var profile = FanProfile.Parse(
                "# a comment\n\n; another\nName = Quiet\n", out var problems);

            Assert.Empty(problems);
            Assert.Equal("Quiet", profile.Name);
        }

        [Fact]
        public void KeysAreCaseInsensitive()
        {
            var profile = FanProfile.Parse("fan1idle = 42", out var problems);

            Assert.Empty(problems);
            Assert.Equal(42, profile.Duties(1)[0]);
        }

        // ── Surviving a bad edit ─────────────────────────────────────────────────

        [Fact]
        public void WrongNumberOfDuties_KeepsThePreviousCurve()
        {
            var expected = FanProfile.Default().Duties(1);

            var profile = FanProfile.Parse("Fan = 30;40;50", out var problems);

            Assert.Equal(expected, profile.Duties(1));
            Assert.Contains(problems, p => p.Contains("got 3"));
        }

        [Fact]
        public void ANonNumericDuty_LeavesTheWholeCurveAlone()
        {
            // Not half applied. A curve that failed partway would leave the profile holding an
            // edit the user never typed, which is worse than keeping the previous one whole.
            var expected = FanProfile.Default().Duties(1);

            var profile = FanProfile.Parse("Fan = 30;40;banana;60;70;80", out var problems);

            Assert.Equal(expected, profile.Duties(1));
            Assert.Contains(problems, p => p.Contains("banana"));
        }

        [Fact]
        public void DutyAbove100_IsClampedAndReported()
        {
            var profile = FanProfile.Parse("Fan = 30;40;50;60;70;150", out var problems);

            Assert.Equal(100, profile.Duties(1)[6]);
            Assert.Contains(problems, p => p.Contains("150") && p.Contains("100"));
        }

        [Fact]
        public void NegativeDuty_IsClampedToZeroAndReported()
        {
            var profile = FanProfile.Parse("FanIdle = -20", out var problems);

            Assert.Equal(0, profile.Duties(1)[0]);
            Assert.Contains(problems, p => p.Contains("-20"));
        }

        [Fact]
        public void UnknownSetting_IsReportedAndSkipped()
        {
            var profile = FanProfile.Parse("Turbo = yes", out var problems);

            Assert.Contains(problems, p => p.Contains("Turbo"));
            Assert.Equal(FanProfile.Default().Duties(1), profile.Duties(1));
        }

        [Fact]
        public void ALineWithNoEquals_IsReportedAndSkipped()
        {
            FanProfile.Parse("just some words", out var problems);

            Assert.Contains(problems, p => p.Contains("just some words"));
        }

        [Fact]
        public void EmptyName_KeepsThePreviousOne()
        {
            var profile = FanProfile.Parse("Name =", out var problems);

            Assert.Equal("Custom", profile.Name);
            Assert.Contains(problems, p => p.Contains("Name is empty"));
        }

        [Fact]
        public void EmptyText_IsTheDefault()
        {
            var profile = FanProfile.Parse("", out var problems);

            Assert.Empty(problems);
            Assert.Equal(FanProfile.Default().Duties(1), profile.Duties(1));
        }

        // ── The warnings that matter ─────────────────────────────────────────────

        [Fact]
        public void AZeroDuty_IsAcceptedAndReportedAsStoppingTheFan()
        {
            // Deliberately NOT refused. The firmware accepts it, MSI Center M offers it, and this
            // was the explicit product decision - warn, do not block.
            var profile = FanProfile.Parse("Fan = 0;0;0;0;0;0\nFanIdle = 0", out var problems);

            Assert.True(profile.StopsAFan);
            Assert.All(profile.Duties(1), duty => Assert.Equal(0, duty));
            Assert.Contains(problems, p => p.Contains("STOPS"));
        }

        [Fact]
        public void OneZeroAnywhere_CountsAsStoppingAFan()
        {
            var profile = FanProfile.Parse("Fan2 = 30;40;0;60;70;80", out _);

            Assert.True(profile.StopsAFan);
        }

        [Fact]
        public void ACurveThatFallsWithHeat_IsReportedButKept()
        {
            var profile = FanProfile.Parse("Fan = 80;70;60;50;40;30", out var problems);

            Assert.True(profile.FallsWithHeat);
            Assert.Equal(new[] { 80, 70, 60, 50, 40, 30 }, profile.Duties(1).Skip(1).ToArray());
            Assert.Contains(problems, p => p.Contains("drops as temperature rises"));
        }

        [Fact]
        public void AHighIdleAboveTheFirstPoint_IsNotAFallingCurve()
        {
            // The idle band sits BELOW the first breakpoint, so an idle duty higher than the
            // curve's start is unusual but not the falling-curve mistake this warns about.
            // The factory table is itself nearly this shape.
            var profile = FanProfile.Parse("FanIdle = 90\nFan = 30;40;50;60;70;80", out _);

            Assert.False(profile.FallsWithHeat);
        }

        // ── The store, and getting out of trouble ────────────────────────────────

        [Fact]
        public void Store_SeedsTheProfileAndReadmeOnFirstRun()
        {
            using var folder = new TempFolder();
            var store = new FanProfileStore(folder.Path);

            store.EnsureSeeded();

            Assert.True(File.Exists(store.ProfilePath));
            Assert.True(File.Exists(store.ReadmePath));
        }

        [Fact]
        public void Store_NeverRewritesAProfileThatAlreadyExists()
        {
            using var folder = new TempFolder();
            var store = new FanProfileStore(folder.Path);

            store.EnsureSeeded();
            File.WriteAllText(store.ProfilePath, "Name = Mine\nFan = 11;22;33;44;55;66");
            store.EnsureSeeded();

            var profile = store.Load();
            Assert.Equal("Mine", profile.Name);
        }

        [Fact]
        public void Store_RestoresTheDefaultWhenTheFileIsEmptied()
        {
            // The documented way out of a bad edit: select all, delete, save.
            using var folder = new TempFolder();
            var store = new FanProfileStore(folder.Path);

            store.EnsureSeeded();
            File.WriteAllText(store.ProfilePath, "   \n\n  ");

            var profile = store.Load();

            Assert.Equal(FanProfile.Default().Duties(1), profile.Duties(1));

            // And the file is put back, so the folder is usable again without retyping it.
            Assert.Contains("Fan1Idle", File.ReadAllText(store.ProfilePath));
        }

        [Fact]
        public void Store_RestoresTheDefaultWhenTheFileIsDeleted()
        {
            using var folder = new TempFolder();
            var store = new FanProfileStore(folder.Path);

            store.EnsureSeeded();
            File.Delete(store.ProfilePath);

            Assert.Equal(FanProfile.Default().Duties(2), store.Load().Duties(2));
        }

        [Fact]
        public void Store_ReportsWhatItIgnored()
        {
            using var folder = new TempFolder();
            var store = new FanProfileStore(folder.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ProfilePath, "Fan = 1;2;3");

            var logged = new List<string>();
            store.Load(logged.Add);

            Assert.Contains(logged, line => line.StartsWith("Fan profile: "));
        }

        private sealed class TempFolder : IDisposable
        {
            public TempFolder()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "McenterLiteFanTests", Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try { System.IO.Directory.Delete(Path, true); }
                catch (IOException) { /* a test folder that will not delete is not a test failure */ }
            }
        }
    }
}
