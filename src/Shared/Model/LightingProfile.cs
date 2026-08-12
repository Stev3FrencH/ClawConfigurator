using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Model
{
    /// <summary>The lighting effects the controller firmware can animate.</summary>
    /// <remarks>
    /// Values match MSI's <c>EnumStyle</c> so a profile file written against MSI's vocabulary reads
    /// the same here. <c>Customize</c> (5) and <c>InfoMode</c> (6) are deliberately absent: the
    /// first is a UI concept with no distinct rendering, and the second repaints the LEDs on every
    /// controller-mode change, which would fight the user's chosen profile.
    /// </remarks>
    public enum LightingStyle
    {
        Off = 0,
        Steady = 1,
        Breath = 2,
        ColorCycle = 3,
        Wave = 4,
    }

    public enum LightingSpeed
    {
        Slow = 0,
        Medium = 1,
        Fast = 2,
    }

    public enum LightingDirection
    {
        Clockwise = 0,
        Counterclockwise = 1,
    }

    /// <summary>One RGB colour. A struct-shaped value with no dependency on any UI type.</summary>
    public struct LightingColor
    {
        public byte R;
        public byte G;
        public byte B;

        public LightingColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public override string ToString() =>
            "#" + R.ToString("X2", CultureInfo.InvariantCulture)
                + G.ToString("X2", CultureInfo.InvariantCulture)
                + B.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>Parses <c>#RRGGBB</c>, <c>RRGGBB</c>, <c>#RGB</c> or <c>R,G,B</c>.</summary>
        /// <remarks>
        /// Deliberately liberal. This parses a file an ordinary person edits by hand, where the
        /// cost of rejecting a reasonable spelling is much higher than the cost of accepting
        /// several. Decimal <c>R,G,B</c> is supported because that is how MSI's own profile files
        /// write colours, so a value can be copied across without translation.
        /// </remarks>
        public static bool TryParse(string text, out LightingColor colour)
        {
            colour = default(LightingColor);
            if (string.IsNullOrWhiteSpace(text)) return false;

            var value = text.Trim();

            if (value.IndexOf(',') >= 0)
            {
                var parts = value.Split(',');
                if (parts.Length != 3) return false;

                byte[] channels = new byte[3];
                for (int i = 0; i < 3; i++)
                {
                    if (!byte.TryParse(parts[i].Trim(), NumberStyles.Integer,
                                       CultureInfo.InvariantCulture, out channels[i]))
                        return false;
                }

                colour = new LightingColor(channels[0], channels[1], channels[2]);
                return true;
            }

            if (value.Length > 0 && value[0] == '#') value = value.Substring(1);

            if (value.Length == 3)
            {
                value = new string(new[] { value[0], value[0], value[1], value[1], value[2], value[2] });
            }

            if (value.Length != 6) return false;

            byte r, g, b;
            if (!TryHex(value.Substring(0, 2), out r)) return false;
            if (!TryHex(value.Substring(2, 2), out g)) return false;
            if (!TryHex(value.Substring(4, 2), out b)) return false;

            colour = new LightingColor(r, g, b);
            return true;
        }

        private static bool TryHex(string pair, out byte value) =>
            byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// One user-editable lighting profile: a style plus the settings that style uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is MSI's vocabulary, not the firmware's. The controller has no notion of "wave" - it
    /// stores flattened keyframes, and MSI computes them from a style at write time. Keeping the
    /// style here rather than the keyframes is what makes a profile file editable by a person:
    /// "Style=Wave" is a thing to change, and 216 bytes of RGB is not.
    /// <see cref="LightingRenderer"/> does the flattening.
    /// </para>
    /// <para>
    /// One simplification over MSI's model. MSI carries an <c>RGBModeType</c> of Type1 (its
    /// built-in rainbow) or Type2 (your colours) as a separate field, which can disagree with
    /// whether any colours are actually set. Here it is derived: <b>give colours and you get
    /// yours; leave <see cref="Colors"/> empty and you get MSI's built-in palette.</b> Same two
    /// renderings, one less thing to get wrong.
    /// </para>
    /// </remarks>
    public sealed class LightingProfile
    {
        /// <summary>Shown on the widget's button. Free text.</summary>
        public string Name { get; set; }

        public LightingStyle Style { get; set; }

        /// <summary>Empty means "use MSI's built-in palette for this style".</summary>
        public List<LightingColor> Colors { get; set; }

        public LightingSpeed Speed { get; set; }

        public LightingDirection Direction { get; set; }

        /// <summary>0-100.</summary>
        public int Brightness { get; set; }

        public LightingProfile()
        {
            Name = "Profile";
            Style = LightingStyle.Steady;
            Colors = new List<LightingColor>();
            Speed = LightingSpeed.Medium;
            Direction = LightingDirection.Clockwise;
            Brightness = 100;
        }

        /// <summary>True when the user supplied colours, i.e. MSI's Type2.</summary>
        public bool UsesOwnColors => Colors != null && Colors.Count > 0;

        /// <summary>
        /// The three profiles as MSI Center M had them configured on 2026-08-12.
        /// </summary>
        /// <remarks>
        /// These are seeds for a first run, not constants to depend on - once the files exist on
        /// disk the user owns them and this is never consulted again. Profile 1 was verified
        /// against the device: reading the controller back gave <c>#7F00FF</c> on all nine LEDs.
        /// </remarks>
        public static LightingProfile Default(int slot)
        {
            switch (slot)
            {
                case 1:
                    return new LightingProfile
                    {
                        Name = "Purple",
                        Style = LightingStyle.Steady,
                        Colors = new List<LightingColor> { new LightingColor(0x7F, 0x00, 0xFF) },
                    };

                case 2:
                    return new LightingProfile
                    {
                        Name = "Wave",
                        Style = LightingStyle.Wave,
                        Speed = LightingSpeed.Medium,
                        Direction = LightingDirection.Clockwise,
                    };

                default:
                    return new LightingProfile
                    {
                        Name = "Cycle",
                        Style = LightingStyle.ColorCycle,
                        Speed = LightingSpeed.Medium,
                    };
            }
        }

        /// <summary>
        /// Writes the profile as the commented INI-style text the user edits.
        /// </summary>
        public string Format(int slot)
        {
            var text = new StringBuilder();

            text.AppendLine("# McenterLite lighting profile " + slot.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("#");
            text.AppendLine("# Edit and save, then pick this profile in the widget to see it.");
            text.AppendLine("# See README.txt in this folder for every accepted value.");
            text.AppendLine();
            text.AppendLine("# Shown on the widget button.");
            text.AppendLine("Name=" + Name);
            text.AppendLine();
            text.AppendLine("# Off, Steady, Breath, ColorCycle or Wave.");
            text.AppendLine("Style=" + Style);
            text.AppendLine();
            text.AppendLine("# Comma-separated, e.g. #FF0000, #00FF00. Leave empty for the built-in palette.");
            text.AppendLine("Colors=" + FormatColors());
            text.AppendLine();
            text.AppendLine("# Slow, Medium or Fast. Ignored by Steady.");
            text.AppendLine("Speed=" + Speed);
            text.AppendLine();
            text.AppendLine("# Clockwise or Counterclockwise. Used by Wave only.");
            text.AppendLine("Direction=" + Direction);
            text.AppendLine();
            text.AppendLine("# 0 to 100.");
            text.AppendLine("Brightness=" + Brightness.ToString(CultureInfo.InvariantCulture));

            return text.ToString();
        }

        private string FormatColors()
        {
            if (!UsesOwnColors) return string.Empty;

            var parts = new List<string>();
            foreach (var colour in Colors) parts.Add(colour.ToString());
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Reads a profile file. Unreadable lines fall back rather than throwing.
        /// </summary>
        /// <remarks>
        /// <b>A bad file must never stop the lighting from working.</b> This parses a file edited
        /// by hand outside any validating UI, on a device whose only feedback channel is the LEDs
        /// themselves - so an unparseable value keeps the default for that one field, and the rest
        /// of the profile still applies. <paramref name="problems"/> collects what was ignored, so
        /// a typo is reported in the helper log instead of vanishing.
        /// </remarks>
        public static LightingProfile Parse(string text, int slot, out List<string> problems)
        {
            var profile = Default(slot);
            problems = new List<string>();

            if (string.IsNullOrEmpty(text)) return profile;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;

                int split = trimmed.IndexOf('=');
                if (split <= 0) continue;

                var key = trimmed.Substring(0, split).Trim();
                var value = trimmed.Substring(split + 1).Trim();

                Apply(profile, key, value, problems);
            }

            return profile;
        }

        private static void Apply(LightingProfile profile, string key, string value, List<string> problems)
        {
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0) profile.Name = value;
                return;
            }

            if (key.Equals("Style", StringComparison.OrdinalIgnoreCase))
            {
                LightingStyle style;
                if (TryParseEnum(value, out style)) profile.Style = style;
                else problems.Add("Style=" + value + " is not a style; keeping " + profile.Style + ".");
                return;
            }

            if (key.Equals("Speed", StringComparison.OrdinalIgnoreCase))
            {
                LightingSpeed speed;
                if (TryParseEnum(value, out speed)) profile.Speed = speed;
                else problems.Add("Speed=" + value + " is not a speed; keeping " + profile.Speed + ".");
                return;
            }

            if (key.Equals("Direction", StringComparison.OrdinalIgnoreCase))
            {
                LightingDirection direction;
                if (TryParseEnum(value, out direction)) profile.Direction = direction;
                else problems.Add("Direction=" + value + " is not a direction; keeping " + profile.Direction + ".");
                return;
            }

            if (key.Equals("Brightness", StringComparison.OrdinalIgnoreCase))
            {
                int brightness;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out brightness))
                    profile.Brightness = Clamp(brightness, 0, 100);
                else
                    problems.Add("Brightness=" + value + " is not a number; keeping " + profile.Brightness + ".");
                return;
            }

            if (key.Equals("Colors", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Colours", StringComparison.OrdinalIgnoreCase))
            {
                // An empty value is meaningful - it selects the built-in palette - so this must
                // clear the seeded defaults rather than leave them in place.
                profile.Colors = new List<LightingColor>();
                if (value.Length == 0) return;

                foreach (var part in SplitColors(value))
                {
                    LightingColor colour;
                    if (LightingColor.TryParse(part, out colour)) profile.Colors.Add(colour);
                    else problems.Add(part + " is not a colour; ignoring it.");
                }

                return;
            }

            problems.Add("Unknown setting '" + key + "'; ignoring it.");
        }

        /// <summary>
        /// Splits a colour list on commas, keeping <c>R,G,B</c> triples together.
        /// </summary>
        /// <remarks>
        /// Commas separate colours AND channels, which is ambiguous on its own. Resolved by
        /// grouping in threes only when the value has no <c>#</c>, since a hex list never needs it.
        /// </remarks>
        private static IEnumerable<string> SplitColors(string value)
        {
            if (value.IndexOf('#') >= 0)
            {
                foreach (var part in value.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0) yield return trimmed;
                }

                yield break;
            }

            var fields = value.Split(',');
            if (fields.Length % 3 == 0 && fields.Length > 0)
            {
                for (int i = 0; i < fields.Length; i += 3)
                    yield return fields[i].Trim() + "," + fields[i + 1].Trim() + "," + fields[i + 2].Trim();

                yield break;
            }

            foreach (var part in fields)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        private static bool TryParseEnum<T>(string value, out T parsed) where T : struct
        {
            // netstandard2.0 has no Enum.TryParse<T> overload that rejects raw numbers, and a
            // stray "7" landing on a style would be worse than a rejection.
            parsed = default(T);
            if (string.IsNullOrWhiteSpace(value)) return false;

            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (!name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                parsed = (T)Enum.Parse(typeof(T), name);
                return true;
            }

            return false;
        }

        internal static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);
    }
}
