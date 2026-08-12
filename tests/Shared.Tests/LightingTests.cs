using System.Collections.Generic;
using System.Linq;
using McenterLite.Shared.Model;
using Xunit;

namespace McenterLite.Shared.Tests
{
    /// <summary>
    /// Pins <see cref="LightingRenderer"/> to MSI's rendering.
    /// </summary>
    /// <remarks>
    /// These are characterisation tests, not design tests. The requirement is that the three
    /// profiles look exactly as MSI Center M drew them, so the expected values below are copied
    /// from MSI's <c>SyncToLights</c> in <c>API_ControlMode.dll</c>. A failure here means we have
    /// drifted from MSI, which is the whole thing worth catching - it does not mean the numbers
    /// should be updated to match the code.
    /// </remarks>
    public class LightingTests
    {
        private const string Red = "#FF0000";
        private const string Yellow = "#FFFF00";
        private const string Green = "#00FF00";
        private const string Cyan = "#00FFFF";
        private const string Blue = "#0000FF";
        private const string Magenta = "#FF00FF";
        private const string Black = "#000000";

        private static string[] Frame(LightingAnimation animation, int index) =>
            animation.Keyframes[index].Leds.Select(led => led.ToString()).ToArray();

        // ── The device-verified case ─────────────────────────────────────────────

        [Fact]
        public void Profile1_MatchesWhatTheControllerActuallyReadBack()
        {
            // Read off the device on 2026-08-12 with --lighting: one keyframe, all nine LEDs
            // #7F00FF, brightness 100, speed 17. The strongest test here, because the expected
            // value came from the hardware rather than from MSI's source.
            var animation = LightingRenderer.Render(LightingProfile.Default(1));

            Assert.Equal(1, animation.KeyframeCount);
            Assert.Equal(17, animation.Speed);
            Assert.Equal(100, animation.Brightness);
            Assert.All(Frame(animation, 0), led => Assert.Equal("#7F00FF", led));
        }

        // ── Styles without user colours: MSI's built-in palette ──────────────────

        [Fact]
        public void Wave_Clockwise_RotatesThePaletteBackwards()
        {
            var animation = LightingRenderer.Render(LightingProfile.Default(2));

            Assert.Equal(4, animation.KeyframeCount);
            Assert.Equal(17, animation.Speed);

            // MSI's keyframe 0 is LeftStick(R, Y, G, B), and LeftStick maps its arguments
            // (upLeft, upRight, downRight, downLeft) onto LEDs as (downLeft, downRight, upRight,
            // upLeft) - so the left ring reads back reversed.
            Assert.Equal(new[] { Blue, Green, Yellow, Red }, Frame(animation, 0).Take(4));

            // The right ring is mirrored: (upRight, upLeft, downLeft, downRight).
            Assert.Equal(new[] { Yellow, Red, Blue, Green }, Frame(animation, 0).Skip(4).Take(4));

            // ABXY takes the upLeft corner.
            Assert.Equal(Red, Frame(animation, 0)[8]);

            // Clockwise steps the palette backwards: frame 1 leads with B, not Y.
            Assert.Equal(Blue, Frame(animation, 1)[8]);
            Assert.Equal(Green, Frame(animation, 2)[8]);
            Assert.Equal(Yellow, Frame(animation, 3)[8]);
        }

        [Fact]
        public void Wave_Counterclockwise_RotatesTheOtherWay()
        {
            var profile = LightingProfile.Default(2);
            profile.Direction = LightingDirection.Counterclockwise;

            var animation = LightingRenderer.Render(profile);

            Assert.Equal(Red, Frame(animation, 0)[8]);
            Assert.Equal(Yellow, Frame(animation, 1)[8]);
            Assert.Equal(Green, Frame(animation, 2)[8]);
            Assert.Equal(Blue, Frame(animation, 3)[8]);
        }

        [Fact]
        public void ColorCycle_WalksSixWholeColours()
        {
            var animation = LightingRenderer.Render(LightingProfile.Default(3));

            Assert.Equal(6, animation.KeyframeCount);
            Assert.Equal(15, animation.Speed);

            var expected = new[] { Red, Yellow, Green, Cyan, Blue, Magenta };
            for (int i = 0; i < expected.Length; i++)
                Assert.All(Frame(animation, i), led => Assert.Equal(expected[i], led));
        }

        // ── Styles with user colours ─────────────────────────────────────────────

        [Fact]
        public void ColorCycle_WithOwnColours_TakesThreeKeyframes()
        {
            var profile = new LightingProfile
            {
                Style = LightingStyle.ColorCycle,
                Colors = new List<LightingColor>
                {
                    new LightingColor(0xFF, 0x00, 0x00),
                    new LightingColor(0x00, 0xFF, 0x00),
                },
            };

            var animation = LightingRenderer.Render(profile);

            // Three, even for two colours - MSI's number, not a derived one.
            Assert.Equal(3, animation.KeyframeCount);
            Assert.All(Frame(animation, 0), led => Assert.Equal(Red, led));
            Assert.All(Frame(animation, 1), led => Assert.Equal(Green, led));
        }

        [Fact]
        public void Breath_WithOwnColours_LeavesADarkFrameBetweenEachColour()
        {
            var profile = new LightingProfile
            {
                Style = LightingStyle.Breath,
                Colors = new List<LightingColor>
                {
                    new LightingColor(0xFF, 0x00, 0x00),
                    new LightingColor(0x00, 0xFF, 0x00),
                },
            };

            var animation = LightingRenderer.Render(profile);

            Assert.Equal(4, animation.KeyframeCount);
            Assert.All(Frame(animation, 0), led => Assert.Equal(Red, led));
            Assert.All(Frame(animation, 1), led => Assert.Equal(Black, led));
            Assert.All(Frame(animation, 2), led => Assert.Equal(Green, led));
            Assert.All(Frame(animation, 3), led => Assert.Equal(Black, led));
        }

        [Theory]
        [InlineData(LightingSpeed.Slow, 14)]
        [InlineData(LightingSpeed.Medium, 17)]
        [InlineData(LightingSpeed.Fast, 20)]
        public void Wave_SpeedsAreMsisNumbers(LightingSpeed speed, int expected)
        {
            var profile = LightingProfile.Default(2);
            profile.Speed = speed;

            Assert.Equal(expected, LightingRenderer.Render(profile).Speed);
        }

        [Fact]
        public void SpeedScalesDifferPerStyle()
        {
            // Medium is 14 for Breath, 15 for ColorCycle and 17 for Wave. There is no rule behind
            // that; it is copied. This test exists so a later "simplification" fails loudly.
            var breath = new LightingProfile { Style = LightingStyle.Breath, Speed = LightingSpeed.Medium };
            var cycle = new LightingProfile { Style = LightingStyle.ColorCycle, Speed = LightingSpeed.Medium };
            var wave = new LightingProfile { Style = LightingStyle.Wave, Speed = LightingSpeed.Medium };

            Assert.Equal(14, LightingRenderer.Render(breath).Speed);
            Assert.Equal(15, LightingRenderer.Render(cycle).Speed);
            Assert.Equal(17, LightingRenderer.Render(wave).Speed);
        }

        [Fact]
        public void Off_BlanksEveryLedRatherThanDimming()
        {
            var animation = LightingRenderer.Render(new LightingProfile { Style = LightingStyle.Off });

            Assert.Equal(1, animation.KeyframeCount);
            Assert.Equal(100, animation.Brightness);
            Assert.All(Frame(animation, 0), led => Assert.Equal(Black, led));
        }

        // ── The profile file ─────────────────────────────────────────────────────

        [Fact]
        public void AProfileSurvivesARoundTripThroughItsFile()
        {
            var original = LightingProfile.Default(1);

            var reparsed = LightingProfile.Parse(original.Format(1), 1, out var problems);

            Assert.Empty(problems);
            Assert.Equal(original.Name, reparsed.Name);
            Assert.Equal(original.Style, reparsed.Style);
            Assert.Equal(original.Speed, reparsed.Speed);
            Assert.Equal(original.Brightness, reparsed.Brightness);
            Assert.Equal(original.Colors.Select(c => c.ToString()), reparsed.Colors.Select(c => c.ToString()));
        }

        [Fact]
        public void AProfileWithNoColoursRoundTripsAsHavingNoColours()
        {
            // The empty case is the one that breaks: an empty Colors= must CLEAR the seeded
            // default rather than leave it, or profile 2 silently stops using MSI's palette.
            var reparsed = LightingProfile.Parse(LightingProfile.Default(2).Format(2), 2, out _);

            Assert.False(reparsed.UsesOwnColors);
            Assert.Equal(LightingStyle.Wave, reparsed.Style);
        }

        [Theory]
        [InlineData("#FF0000", "#FF0000")]
        [InlineData("FF0000", "#FF0000")]
        [InlineData("#F00", "#FF0000")]
        [InlineData("255,0,0", "#FF0000")]
        [InlineData("  #ff0000  ", "#FF0000")]
        public void ColoursParseInEverySpellingAPersonMightUse(string text, string expected)
        {
            Assert.True(LightingColor.TryParse(text, out var colour));
            Assert.Equal(expected, colour.ToString());
        }

        [Fact]
        public void DecimalTriplesParseSoMsisOwnFilesCanBePastedIn()
        {
            // MSI writes "127,0,255". Supporting it means a value can move across without
            // translation, which is the difference between a usable howto and a fiddly one.
            var profile = LightingProfile.Parse("Style=Steady\nColors=127,0,255", 1, out var problems);

            Assert.Empty(problems);
            Assert.Equal("#7F00FF", Assert.Single(profile.Colors).ToString());
        }

        [Fact]
        public void MultipleDecimalTriplesSplitInThrees()
        {
            var profile = LightingProfile.Parse("Colors=255,0,0, 0,255,0", 1, out _);

            Assert.Equal(new[] { "#FF0000", "#00FF00" }, profile.Colors.Select(c => c.ToString()));
        }

        [Fact]
        public void ABrokenValueIsReportedAndTheRestOfTheProfileStillApplies()
        {
            // The whole point of the lenient parser: a typo must not leave the user with no
            // lighting and no way to tell why.
            var profile = LightingProfile.Parse(
                "Name=Mine\nStyle=Sparkle\nColors=#00FF00\nBrightness=60", 1, out var problems);

            Assert.Contains(problems, p => p.Contains("Sparkle"));
            Assert.Equal("Mine", profile.Name);
            Assert.Equal(60, profile.Brightness);
            Assert.Equal("#00FF00", Assert.Single(profile.Colors).ToString());
            Assert.Equal(LightingStyle.Steady, profile.Style);
        }

        [Fact]
        public void ANumericStyleIsRejectedRatherThanCastBlind()
        {
            LightingProfile.Parse("Style=4", 1, out var problems);

            Assert.Contains(problems, p => p.Contains("Style=4"));
        }

        [Fact]
        public void BrightnessIsClampedRatherThanRefused()
        {
            Assert.Equal(100, LightingProfile.Parse("Brightness=400", 1, out _).Brightness);
            Assert.Equal(0, LightingProfile.Parse("Brightness=-5", 1, out _).Brightness);
        }

        [Fact]
        public void AWaveWithFewerThanFourColoursRepeatsThemRatherThanGoingBlack()
        {
            var profile = new LightingProfile
            {
                Style = LightingStyle.Wave,
                Colors = new List<LightingColor> { new LightingColor(0xFF, 0x00, 0x00) },
            };

            var animation = LightingRenderer.Render(profile);

            Assert.All(Frame(animation, 0), led => Assert.Equal(Red, led));
        }
    }
}
