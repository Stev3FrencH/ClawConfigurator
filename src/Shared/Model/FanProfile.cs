using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// A custom fan curve: seven duty percentages for each of the two fans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape comes straight from the firmware table measured in gate G2. Each fan holds an
    /// <b>idle duty</b>, used below the first breakpoint, followed by six duties at the fixed
    /// temperatures in <see cref="Breakpoints"/>. The axis is <b>not</b> editable — it did not move
    /// across any state MSI Center M itself could produce, so a curve editor would be offering
    /// control the hardware does not have.
    /// </para>
    /// <para>
    /// <b>Duty is a plain percentage, 0–100.</b> Measured, not assumed: MSI Center M's own slider
    /// wrote exactly 0 at minimum and exactly 100 at maximum.
    /// </para>
    /// <para>
    /// <b>Zero is allowed and stops the fan.</b> The firmware enforces no floor — an all-zero table
    /// was accepted on this device with both tachometers reading zero. That is deliberately not
    /// refused here, because MSI Center M permits it too and this app is replacing MSI Center M
    /// rather than second-guessing it. It is instead reported through <see cref="StopsAFan"/> so
    /// every layer above can say so plainly. What must never happen is a stopped fan going
    /// unmentioned.
    /// </para>
    /// </remarks>
    public sealed class FanProfile
    {
        /// <summary>Duties per fan: one idle value plus one per breakpoint.</summary>
        public const int DutyCount = 7;

        /// <summary>Two fans, addressed separately by the firmware.</summary>
        public const int FanCount = 2;

        public const int MinDuty = 0;
        public const int MaxDuty = 100;

        /// <summary>
        /// The fixed temperatures the six curve points are indexed against, in Celsius.
        /// </summary>
        /// <remarks>
        /// Read from <c>MSI_ACPI.Get_Temperature</c> and identical in every state captured. Present
        /// for display and for the profile file's comments; nothing here writes them.
        /// </remarks>
        public static readonly int[] Breakpoints = { 47, 50, 57, 64, 71, 78 };

        /// <summary>The factory table, captured from this device in Auto. Both fans read identically.</summary>
        public static readonly int[] FactoryDuties = { 58, 70, 74, 76, 78, 80, 84 };

        private readonly int[][] _duties;

        public FanProfile()
        {
            _duties = new int[FanCount][];
            for (int fan = 0; fan < FanCount; fan++) _duties[fan] = new int[DutyCount];
        }

        /// <summary>Shown on the widget's Custom button.</summary>
        public string Name { get; set; } = "Custom";

        /// <summary>
        /// One fan's seven duties. <paramref name="fan"/> is 1-based, matching the firmware
        /// sub-function and the profile file, so no call site has to remember an offset.
        /// </summary>
        public int[] Duties(int fan)
        {
            if (fan < 1 || fan > FanCount)
                throw new ArgumentOutOfRangeException(nameof(fan), fan, "There are " + FanCount + " fans.");

            return _duties[fan - 1];
        }

        /// <summary>True when any duty is 0, i.e. the profile stops a fan in at least one band.</summary>
        public bool StopsAFan
        {
            get
            {
                for (int fan = 1; fan <= FanCount; fan++)
                    foreach (var duty in Duties(fan))
                        if (duty <= 0) return true;

                return false;
            }
        }

        /// <summary>True when a curve falls as temperature rises, which MSI's own table never does.</summary>
        public bool FallsWithHeat
        {
            get
            {
                for (int fan = 1; fan <= FanCount; fan++)
                {
                    var duties = Duties(fan);

                    // From index 1: the idle duty sits below the first breakpoint and is legitimately
                    // allowed to be higher than the curve's start.
                    for (int i = 2; i < duties.Length; i++)
                        if (duties[i] < duties[i - 1]) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// The profile seeded on first run: quieter than factory when cool, more aggressive when hot.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT a copy of the factory table. A Custom profile identical to Auto would
        /// make the widget's two buttons do the same thing until the user found and edited the file,
        /// which reads as a broken feature. It is also deliberately never 0 — the seeded default
        /// should not be the one configuration that can stop a fan.
        /// </remarks>
        public static FanProfile Default()
        {
            var profile = new FanProfile { Name = "Custom" };

            for (int fan = 1; fan <= FanCount; fan++)
            {
                var duties = profile.Duties(fan);
                duties[0] = 30;                       // idle, against a factory 58
                duties[1] = 40;                       // 47 C
                duties[2] = 50;                       // 50 C
                duties[3] = 60;                       // 57 C
                duties[4] = 70;                       // 64 C
                duties[5] = 80;                       // 71 C
                duties[6] = 90;                       // 78 C, against a factory 84
            }

            return profile;
        }

        /// <summary>The factory curve, as a profile. What the widget's Auto button applies.</summary>
        public static FanProfile Factory()
        {
            var profile = new FanProfile { Name = "Auto" };

            for (int fan = 1; fan <= FanCount; fan++)
                Array.Copy(FactoryDuties, profile.Duties(fan), DutyCount);

            return profile;
        }

        /// <summary>
        /// Reads a profile file, keeping the default for anything that cannot be read.
        /// </summary>
        /// <remarks>
        /// Field by field, never all-or-nothing: one mistyped line must not discard the rest of a
        /// file someone spent time on. Everything skipped is appended to <paramref name="problems"/>
        /// with the value kept instead, and the helper writes those to its log.
        /// </remarks>
        public static FanProfile Parse(string text, out List<string> problems)
        {
            problems = new List<string>();
            var profile = Default();

            if (string.IsNullOrEmpty(text)) return profile;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    problems.Add("Ignored '" + line + "': every setting is Name = value.");
                    continue;
                }

                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();

                ApplySetting(profile, key, value, problems);
            }

            WarnAboutShape(profile, problems);
            return profile;
        }

        private static void ApplySetting(
            FanProfile profile, string key, string value, List<string> problems)
        {
            if (Same(key, "Name"))
            {
                if (value.Length == 0) problems.Add("Name is empty; keeping '" + profile.Name + "'.");
                else profile.Name = value;
                return;
            }

            // Both fans at once. Most people want one curve, and making them type it twice invites
            // the two drifting apart by a typo rather than by intent.
            if (Same(key, "FanIdle")) { SetIdle(profile, 0, value, problems); return; }
            if (Same(key, "Fan")) { SetCurve(profile, 0, value, problems); return; }

            for (int fan = 1; fan <= FanCount; fan++)
            {
                var n = fan.ToString(CultureInfo.InvariantCulture);
                if (Same(key, "Fan" + n + "Idle")) { SetIdle(profile, fan, value, problems); return; }
                if (Same(key, "Fan" + n)) { SetCurve(profile, fan, value, problems); return; }
            }

            problems.Add("Ignored unknown setting '" + key + "'.");
        }

        /// <summary>Sets the idle duty. <paramref name="fan"/> 0 means both.</summary>
        private static void SetIdle(FanProfile profile, int fan, string value, List<string> problems)
        {
            int duty;
            if (!TryParseDuty(value, out duty, problems, "idle duty")) return;

            for (int f = 1; f <= FanCount; f++)
                if (fan == 0 || fan == f) profile.Duties(f)[0] = duty;
        }

        /// <summary>Sets the six curve points. <paramref name="fan"/> 0 means both.</summary>
        private static void SetCurve(FanProfile profile, int fan, string value, List<string> problems)
        {
            var parts = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int expected = DutyCount - 1;

            if (parts.Length != expected)
            {
                problems.Add(
                    "Expected " + expected + " duties for " + (fan == 0 ? "the fans" : "fan " + fan)
                    + " (one per temperature), got " + parts.Length + "; keeping the previous curve.");
                return;
            }

            // Parsed into a scratch array first. A curve that fails halfway would otherwise leave
            // the profile holding half an edit, which is worse than keeping the whole previous one.
            var parsed = new int[expected];
            for (int i = 0; i < expected; i++)
            {
                if (!TryParseDuty(parts[i].Trim(), out parsed[i], problems,
                        Breakpoints[i] + " C point"))
                    return;
            }

            for (int f = 1; f <= FanCount; f++)
            {
                if (fan != 0 && fan != f) continue;
                Array.Copy(parsed, 0, profile.Duties(f), 1, expected);
            }
        }

        private static bool TryParseDuty(
            string text, out int duty, List<string> problems, string what)
        {
            duty = 0;

            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                problems.Add("'" + text + "' is not a number for the " + what + "; keeping the previous value.");
                return false;
            }

            if (value < MinDuty || value > MaxDuty)
            {
                int clamped = value < MinDuty ? MinDuty : MaxDuty;
                problems.Add(
                    "Duty " + value + " for the " + what + " is outside " + MinDuty + "-" + MaxDuty
                    + "; using " + clamped + ".");
                duty = clamped;
                return true;
            }

            duty = value;
            return true;
        }

        /// <summary>
        /// Reports the two shapes that are legal but almost never intended.
        /// </summary>
        /// <remarks>
        /// Warnings, not corrections. A profile that silently repaired itself would be lying about
        /// what it is going to send to the embedded controller, and the whole point of these files
        /// is that what you read is what gets written.
        /// </remarks>
        private static void WarnAboutShape(FanProfile profile, List<string> problems)
        {
            if (profile.StopsAFan)
            {
                problems.Add(
                    "This profile sets a duty of 0, which STOPS that fan in that band. The firmware "
                    + "does not prevent this. Delete the file to get the default back.");
            }

            if (profile.FallsWithHeat)
                problems.Add("This profile's fan speed drops as temperature rises, which is unusual.");
        }

        /// <summary>Renders the profile back to its file form, comments and all.</summary>
        public string Format()
        {
            var sb = new StringBuilder(700);

            sb.Append("# McenterLite - custom fan profile\n");
            sb.Append("#\n");
            sb.Append("# Duty is a percentage, 0-100. Each fan has an idle duty used below ");
            sb.Append(Breakpoints[0].ToString(CultureInfo.InvariantCulture));
            sb.Append(" C,\n# then one duty per temperature:\n#\n#   ");

            for (int i = 0; i < Breakpoints.Length; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append(Breakpoints[i].ToString(CultureInfo.InvariantCulture)).Append(" C");
            }

            sb.Append("\n#\n# MSI's own curve is idle ");
            sb.Append(FactoryDuties[0].ToString(CultureInfo.InvariantCulture)).Append(", then ");
            for (int i = 1; i < FactoryDuties.Length; i++)
            {
                if (i > 1) sb.Append(';');
                sb.Append(FactoryDuties[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(" - that is what the widget's Auto button applies.\n");
            sb.Append("#\n# WARNING: 0 stops the fan. The firmware allows it and will not stop you.\n");
            sb.Append("#\n# Use Fan = ... to set both fans at once, or Fan1/Fan2 to set them apart.\n\n");

            sb.Append("Name = ").Append(Name ?? "Custom").Append('\n');

            for (int fan = 1; fan <= FanCount; fan++)
            {
                var duties = Duties(fan);
                var n = fan.ToString(CultureInfo.InvariantCulture);

                sb.Append('\n');
                sb.Append("Fan").Append(n).Append("Idle = ")
                  .Append(duties[0].ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("Fan").Append(n).Append(" = ");

                for (int i = 1; i < duties.Length; i++)
                {
                    if (i > 1) sb.Append(';');
                    sb.Append(duties[i].ToString(CultureInfo.InvariantCulture));
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>One fan's duties as <c>58;70;74;…</c>, for logs and the probe.</summary>
        public string FormatDuties(int fan)
        {
            var duties = Duties(fan);
            var sb = new StringBuilder(32);

            for (int i = 0; i < duties.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(duties[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
