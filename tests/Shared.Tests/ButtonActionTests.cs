using System;
using System.IO;
using McenterLite.Shared.Model;
using Xunit;

namespace McenterLite.Shared.Tests
{
    /// <summary>
    /// Pins the hand-edited button configuration: what it accepts, and what it does with nonsense.
    /// </summary>
    /// <remarks>
    /// The failure mode that matters is not "an action did the wrong thing" but "a typo did
    /// something". This file is edited by hand, by someone who will not re-read the README, and
    /// every action it could pick changes the machine.
    /// </remarks>
    public class ButtonActionTests
    {
        [Theory]
        [InlineData("none", ButtonAction.None)]
        [InlineData("rtss-overlay", ButtonAction.RtssOverlay)]
        [InlineData("fan-profile", ButtonAction.FanProfile)]
        [InlineData("performance-mode", ButtonAction.PerfMode)]
        [InlineData("lighting", ButtonAction.LightingProfile)]
        [InlineData("controller-mode", ButtonAction.ControllerMode)]
        public void ParsesEveryNameTheReadmeDocuments(string text, ButtonAction expected)
        {
            Assert.True(ButtonActions.TryParse(text, out var action));
            Assert.Equal(expected, action);
        }

        [Theory]
        [InlineData("RTSS-Overlay")]
        [InlineData("  rtss-overlay  ")]
        [InlineData("rtss")]
        [InlineData("osd")]
        public void IsForgivingAboutCaseSpacingAndShorthand(string text)
        {
            Assert.True(ButtonActions.TryParse(text, out var action));
            Assert.Equal(ButtonAction.RtssOverlay, action);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("rtss overlay")]
        [InlineData("toggle the overlay")]
        [InlineData("fan-profil")]
        public void RefusesAnythingItDoesNotRecognise(string text)
        {
            // False, not a guess. A near-miss like "fan-profil" resolving to the fan action would
            // mean a typo silently changes fan curves.
            Assert.False(ButtonActions.TryParse(text, out var action));
            Assert.Equal(ButtonAction.None, action);
        }

        [Fact]
        public void EveryActionRoundTripsThroughItsWrittenName()
        {
            foreach (ButtonAction action in Enum.GetValues(typeof(ButtonAction)))
            {
                Assert.True(
                    ButtonActions.TryParse(ButtonActions.Format(action), out var parsed),
                    $"{action} formats to '{ButtonActions.Format(action)}', which does not parse back.");

                Assert.Equal(action, parsed);
            }
        }

        // ── The file on disk ─────────────────────────────────────────────────────

        [Fact]
        public void SeedsToNoneSoAFreshInstallChangesNothing()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);

            store.EnsureSeeded();

            Assert.True(File.Exists(store.ActionPath));
            Assert.True(File.Exists(store.ReadmePath));
            Assert.Equal(ButtonAction.None, store.Load());
        }

        [Fact]
        public void ReadsABareActionName()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ActionPath, "rtss-overlay\r\n");

            Assert.Equal(ButtonAction.RtssOverlay, store.Load());
        }

        [Fact]
        public void ReadsTheKeyEqualsValueFormPeopleWriteAnyway()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ActionPath, "# a comment\r\nAction = lighting\r\n");

            Assert.Equal(ButtonAction.LightingProfile, store.Load());
        }

        [Fact]
        public void SkipsCommentsAndBlankLines()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ActionPath, "\r\n# none\r\n\r\n   \r\nfan-profile\r\n");

            Assert.Equal(ButtonAction.FanProfile, store.Load());
        }

        [Fact]
        public void AMissingFileDoesNothingRatherThanThrowing()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);

            Assert.Equal(ButtonAction.None, store.Load());
        }

        [Fact]
        public void AnUnreadableActionReportsItAndDoesNothing()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ActionPath, "make it loud\r\n");

            string reported = null;
            Assert.Equal(ButtonAction.None, store.Load(m => reported = m));
            Assert.NotNull(reported);
            Assert.Contains("make it loud", reported);
        }

        [Fact]
        public void TheReadmeIsRewrittenButTheChoiceIsNot()
        {
            using var dir = new TempDirectory();
            var store = new ButtonActionStore(dir.Path);
            store.EnsureSeeded();

            File.WriteAllText(store.ActionPath, "lighting\r\n");
            File.WriteAllText(store.ReadmePath, "clobbered");

            store.EnsureSeeded();

            // The README is ours and tracks the build; the choice is the user's and must survive.
            Assert.NotEqual("clobbered", File.ReadAllText(store.ReadmePath));
            Assert.Equal(ButtonAction.LightingProfile, store.Load());
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "McenterLiteButtonTests", Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (Exception) { /* a temp directory that outlives the test is not a failure */ }
            }
        }
    }
}
