using System.Collections.Generic;

namespace McenterLite.Shared.Model
{
    /// <summary>One animation keyframe: a colour for each of the nine LEDs.</summary>
    public sealed class LightingKeyframe
    {
        public LightingColor[] Leds { get; set; }

        public LightingKeyframe()
        {
            Leds = new LightingColor[LightingRenderer.LedCount];
        }
    }

    /// <summary>
    /// What the firmware actually animates: keyframes, a speed and a brightness.
    /// </summary>
    public sealed class LightingAnimation
    {
        public int KeyframeCount { get; set; }

        /// <summary>0-20, in the firmware's own scale where higher is faster.</summary>
        public int Speed { get; set; }

        /// <summary>0-100.</summary>
        public int Brightness { get; set; }

        public List<LightingKeyframe> Keyframes { get; set; }

        public LightingAnimation()
        {
            Keyframes = new List<LightingKeyframe>();
            for (int i = 0; i < LightingRenderer.MaxKeyframes; i++) Keyframes.Add(new LightingKeyframe());

            KeyframeCount = 1;
            Speed = 17;
            Brightness = 100;
        }
    }

    /// <summary>
    /// Flattens a <see cref="LightingProfile"/> into the keyframes the controller animates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a deliberate reimplementation of MSI's <c>SyncToLights</c>, and it is meant to
    /// match it byte for byte.</b> The user asked for their existing profiles to look exactly as
    /// MSI renders them, so every constant here - the speed numbers, the keyframe counts, the LED
    /// orderings, the rotation sequences - is copied from
    /// <c>API_ControlMode.dll</c> rather than chosen. They look arbitrary because they are:
    /// Breath's Medium is 14 while ColorCycle's is 15 and Wave's is 17, and none of that follows a
    /// rule. Do not "tidy" them.
    /// </para>
    /// <para>
    /// The one intentional divergence is brightness. MSI ignores the profile and reads a registry
    /// value that is only ever 0 or 100; here the profile's own 0-100 is honoured, because the
    /// firmware accepts the whole range and the registry mirror is the thing this project exists
    /// to remove.
    /// </para>
    /// </remarks>
    public static class LightingRenderer
    {
        public const int LedCount = 9;
        public const int MaxKeyframes = 8;

        /// <summary>Indices 0-3 are the left stick ring, 4-7 the right, 8 the ABXY cluster.</summary>
        public const int LeftStickFirstLed = 0;
        public const int RightStickFirstLed = 4;
        public const int AbxyLed = 8;

        private static readonly LightingColor Red = Hex(0xFF, 0x00, 0x00);
        private static readonly LightingColor Yellow = Hex(0xFF, 0xFF, 0x00);
        private static readonly LightingColor Green = Hex(0x00, 0xFF, 0x00);
        private static readonly LightingColor Cyan = Hex(0x00, 0xFF, 0xFF);
        private static readonly LightingColor Blue = Hex(0x00, 0x00, 0xFF);
        private static readonly LightingColor Magenta = Hex(0xFF, 0x00, 0xFF);

        /// <summary>MSI's built-in four-colour palette, used whenever a profile sets no colours.</summary>
        private static readonly LightingColor[] Palette = { Red, Yellow, Green, Blue };

        public static LightingAnimation Render(LightingProfile profile)
        {
            var animation = new LightingAnimation();
            if (profile == null) return animation;

            animation.Brightness = LightingProfile.Clamp(profile.Brightness, 0, 100);

            switch (profile.Style)
            {
                case LightingStyle.Off:
                    RenderOff(animation);
                    break;

                case LightingStyle.Steady:
                    RenderSteady(animation, profile);
                    break;

                case LightingStyle.Breath:
                    RenderBreath(animation, profile);
                    break;

                case LightingStyle.ColorCycle:
                    RenderColorCycle(animation, profile);
                    break;

                case LightingStyle.Wave:
                    RenderWave(animation, profile);
                    break;
            }

            return animation;
        }

        /// <summary>
        /// Off is every LED black, not a power-down.
        /// </summary>
        /// <remarks>
        /// There is no "lights off" command in the protocol; MSI's own <c>LightsOff</c> does the
        /// same thing. Brightness stays at 100 on purpose - dimming to zero would look identical
        /// but leaves a profile that reads as "on and invisible", which is harder to reason about
        /// when the next read-back comes in.
        /// </remarks>
        private static void RenderOff(LightingAnimation animation)
        {
            animation.KeyframeCount = 1;
            animation.Speed = 15;
            animation.Brightness = 100;
            Fill(animation.Keyframes[0], new LightingColor(0, 0, 0));
        }

        private static void RenderSteady(LightingAnimation animation, LightingProfile profile)
        {
            animation.Speed = 17;
            animation.KeyframeCount = 1;

            if (!profile.UsesOwnColors)
            {
                PaintRings(animation.Keyframes[0], Red, Yellow, Green, Blue);
                return;
            }

            Fill(animation.Keyframes[0], profile.Colors[0]);
        }

        private static void RenderBreath(LightingAnimation animation, LightingProfile profile)
        {
            if (!profile.UsesOwnColors)
            {
                animation.KeyframeCount = 2;
                PaintRings(animation.Keyframes[0], Red, Yellow, Green, Blue);
            }
            else
            {
                // MSI writes each colour into every OTHER keyframe and leaves the gaps black -
                // that dark frame between colours is what makes it read as a breath rather than a
                // cycle. Hence a count of 4 for two colours.
                animation.KeyframeCount = 4;
                for (int i = 0; i < profile.Colors.Count && (i * 2) < MaxKeyframes; i++)
                    Fill(animation.Keyframes[i * 2], profile.Colors[i]);
            }

            animation.Speed = Speed(profile.Speed, 11, 14, 17);
        }

        private static void RenderColorCycle(LightingAnimation animation, LightingProfile profile)
        {
            if (!profile.UsesOwnColors)
            {
                animation.KeyframeCount = 6;
                var wheel = new[] { Red, Yellow, Green, Cyan, Blue, Magenta };
                for (int i = 0; i < wheel.Length; i++) Fill(animation.Keyframes[i], wheel[i]);
            }
            else
            {
                animation.KeyframeCount = 3;
                for (int i = 0; i < profile.Colors.Count && i < MaxKeyframes; i++)
                    Fill(animation.Keyframes[i], profile.Colors[i]);
            }

            animation.Speed = Speed(profile.Speed, 13, 15, 17);
        }

        private static void RenderWave(LightingAnimation animation, LightingProfile profile)
        {
            animation.KeyframeCount = 4;

            var colours = profile.UsesOwnColors ? Take4(profile.Colors) : Palette;

            // The wave is one palette rotated by one position per keyframe. Clockwise steps
            // backwards through the palette and counterclockwise forwards - which looks inverted
            // written down, and is what MSI does.
            bool clockwise = profile.Direction == LightingDirection.Clockwise;

            for (int frame = 0; frame < 4; frame++)
            {
                int shift = clockwise ? (4 - frame) % 4 : frame % 4;

                PaintRings(
                    animation.Keyframes[frame],
                    colours[shift % 4],
                    colours[(shift + 1) % 4],
                    colours[(shift + 2) % 4],
                    colours[(shift + 3) % 4]);
            }

            animation.Speed = Speed(profile.Speed, 14, 17, 20);
        }

        /// <summary>
        /// Paints both stick rings and the ABXY LED from four corner colours.
        /// </summary>
        /// <remarks>
        /// The two rings do NOT take the same order. MSI maps the left ring
        /// <c>[downLeft, downRight, upRight, upLeft]</c> and the right ring
        /// <c>[upRight, upLeft, downLeft, downRight]</c> - the rings are physically mirrored, so
        /// identical indices would make a wave visibly run the wrong way on one stick. ABXY takes
        /// the first corner.
        /// </remarks>
        private static void PaintRings(
            LightingKeyframe frame,
            LightingColor upLeft,
            LightingColor upRight,
            LightingColor downRight,
            LightingColor downLeft)
        {
            frame.Leds[LeftStickFirstLed + 0] = downLeft;
            frame.Leds[LeftStickFirstLed + 1] = downRight;
            frame.Leds[LeftStickFirstLed + 2] = upRight;
            frame.Leds[LeftStickFirstLed + 3] = upLeft;

            frame.Leds[RightStickFirstLed + 0] = upRight;
            frame.Leds[RightStickFirstLed + 1] = upLeft;
            frame.Leds[RightStickFirstLed + 2] = downLeft;
            frame.Leds[RightStickFirstLed + 3] = downRight;

            frame.Leds[AbxyLed] = upLeft;
        }

        private static void Fill(LightingKeyframe frame, LightingColor colour)
        {
            for (int i = 0; i < LedCount; i++) frame.Leds[i] = colour;
        }

        /// <summary>Pads a short colour list up to the four corners a wave needs.</summary>
        private static LightingColor[] Take4(List<LightingColor> colours)
        {
            var result = new LightingColor[4];
            for (int i = 0; i < 4; i++) result[i] = colours[i % colours.Count];
            return result;
        }

        private static int Speed(LightingSpeed speed, int slow, int medium, int fast)
        {
            switch (speed)
            {
                case LightingSpeed.Slow: return slow;
                case LightingSpeed.Fast: return fast;
                default: return medium;
            }
        }

        private static LightingColor Hex(byte r, byte g, byte b) => new LightingColor(r, g, b);
    }
}
