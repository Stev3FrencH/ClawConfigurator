using System;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// The battery charge limits this device can actually represent.
    ///
    /// <para>
    /// MSI does not store a percentage. It stores a three-state selector - measured on device
    /// 2026-08-07 as <c>BatteryLevel</c> (REG_SZ) under
    /// <c>HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery</c>, where "0" = 100%, "1" = 80%,
    /// "2" = 60%. See <c>docs/hardware-notes.md</c>.
    /// </para>
    ///
    /// <para>
    /// The wire contract still carries a PERCENT rather than the raw 0/1/2 index, for two reasons:
    /// a percent stays meaningful if the direct <c>MSI_ACPI.Set_MasterBattery</c> path turns out to
    /// accept a wider range, and a stored "1" whose meaning shifts in a later firmware is a silent
    /// data-corruption bug where a stored "80" is merely wrong. <see cref="Snap"/> is the single
    /// place that reconciles the two.
    /// </para>
    /// </summary>
    public static class ChargeLevels
    {
        public const int Full = 100;
        public const int Balanced = 80;
        public const int Longevity = 60;

        /// <summary>The selectable limits, ascending. Order matches the widget's dropdown.</summary>
        public static int[] All() => new[] { Longevity, Balanced, Full };

        /// <summary>The default when nothing has been chosen: MSI's own middle option.</summary>
        public const int Default = Balanced;

        /// <summary>
        /// Rounds an arbitrary percent to the nearest limit the hardware can hold.
        /// </summary>
        /// <remarks>
        /// Never rejects. A value the device cannot represent is a UI or version-skew problem, not
        /// a reason to leave the battery unprotected - snapping to the nearest real level always
        /// beats refusing to set one.
        /// </remarks>
        public static int Snap(int percent)
        {
            var levels = All();
            int best = levels[0];
            int bestDistance = Math.Abs(percent - best);

            for (int i = 1; i < levels.Length; i++)
            {
                int distance = Math.Abs(percent - levels[i]);
                // Strict, so an exact tie keeps the LOWER level - the safer one for the battery.
                if (distance < bestDistance)
                {
                    best = levels[i];
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>Index into <see cref="All"/>, for binding a dropdown. -1 becomes the default.</summary>
        public static int ToIndex(int percent)
        {
            var levels = All();
            int snapped = Snap(percent);
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] == snapped) return i;
            return 1;
        }

        /// <summary>Inverse of <see cref="ToIndex"/>. Out-of-range indices become the default.</summary>
        public static int FromIndex(int index)
        {
            var levels = All();
            return (index >= 0 && index < levels.Length) ? levels[index] : Default;
        }

        // ── MSI's on-device encoding ─────────────────────────────────────────────
        //
        // Kept here, rather than in the Windows-only hardware layer, for one reason: it is
        // testable here. The mapping is INVERTED - a higher stored number means a LOWER limit -
        // and it decides how far a real battery charges. That is not a fact to leave resting on
        // careful reading of a file no test can reach.
        //
        // The widget never calls these; only the registry provider does.

        /// <summary>MSI's <c>BatteryLevel</c> string for a percent. Measured on device 2026-08-07.</summary>
        public static string ToMsiLevel(int percent)
        {
            switch (Snap(percent))
            {
                case Longevity: return "2";
                case Balanced: return "1";
                default: return "0";
            }
        }

        /// <summary>
        /// Decodes MSI's <c>BatteryLevel</c> string. Returns false for anything unrecognised -
        /// guessing here would pick a charge limit for someone's battery.
        /// </summary>
        public static bool TryFromMsiLevel(string level, out int percent)
        {
            percent = Default;
            if (level == null) return false;

            switch (level.Trim())
            {
                case "0": percent = Full; return true;
                case "1": percent = Balanced; return true;
                case "2": percent = Longevity; return true;
                default: return false;
            }
        }
    }
}
