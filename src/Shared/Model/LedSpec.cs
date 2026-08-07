using System;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// The complete LED configuration, carried as one value.
    ///
    /// <para>
    /// One <see cref="Ipc.Function.LedSpec"/> rather than a Function per knob, because the LED is
    /// applied to the hardware as a single indivisible HID report. Splitting it across several
    /// messages would let the device sit in a half-applied state between them, and would multiply
    /// the write rate on a device we already have to rate-limit.
    /// </para>
    ///
    /// <para>Encoding: <c>mode;brightness;speed;r,g,b;r,g,b;r,g,b</c> - one colour per zone.</para>
    /// </summary>
    public sealed class LedSpec
    {
        /// <summary>The controller has three independently addressable zones.</summary>
        public const int ZoneCount = 3;

        public Ipc.LedMode Mode { get; set; } = Ipc.LedMode.Static;

        /// <summary>0..100. Zero is off; the mode is retained so the UI can restore it.</summary>
        public int Brightness { get; set; } = 80;

        /// <summary>0..100. Only meaningful for animated modes.</summary>
        public int Speed { get; set; } = 50;

        /// <summary>Per-zone colour, packed 0x00RRGGBB.</summary>
        public int[] Zones { get; set; }

        public LedSpec()
        {
            Zones = new int[ZoneCount];
            for (int i = 0; i < ZoneCount; i++) Zones[i] = 0x00A0FF; // a neutral blue
        }

        public string Serialize()
        {
            var sb = new StringBuilder(64);
            sb.Append(((int)Mode).ToString(CultureInfo.InvariantCulture));
            sb.Append(';').Append(Clamp(Brightness, 0, 100).ToString(CultureInfo.InvariantCulture));
            sb.Append(';').Append(Clamp(Speed, 0, 100).ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < ZoneCount; i++)
            {
                int rgb = (Zones != null && i < Zones.Length) ? Zones[i] : 0;
                sb.Append(';')
                  .Append(((rgb >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(((rgb >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append((rgb & 0xFF).ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        public static LedSpec Parse(string s)
        {
            var spec = new LedSpec();
            if (string.IsNullOrEmpty(s)) return spec;

            var parts = s.Split(';');

            if (parts.Length > 0)
            {
                int mode = ToInt(parts[0], (int)Ipc.LedMode.Static);
                spec.Mode = Enum.IsDefined(typeof(Ipc.LedMode), mode)
                    ? (Ipc.LedMode)mode
                    : Ipc.LedMode.Static;
            }

            if (parts.Length > 1) spec.Brightness = Clamp(ToInt(parts[1], 80), 0, 100);
            if (parts.Length > 2) spec.Speed = Clamp(ToInt(parts[2], 50), 0, 100);

            for (int z = 0; z < ZoneCount; z++)
            {
                int idx = 3 + z;
                if (idx >= parts.Length) break;

                var rgb = parts[idx].Split(',');
                if (rgb.Length != 3) continue;

                spec.Zones[z] =
                    (Clamp(ToInt(rgb[0], 0), 0, 255) << 16) |
                    (Clamp(ToInt(rgb[1], 0), 0, 255) << 8) |
                    Clamp(ToInt(rgb[2], 0), 0, 255);
            }

            return spec;
        }

        /// <summary>True when the two specs would produce an identical HID report, so the write can be skipped.</summary>
        public bool IsEquivalentTo(LedSpec other)
        {
            if (other == null) return false;
            if (Mode != other.Mode || Brightness != other.Brightness || Speed != other.Speed) return false;

            for (int i = 0; i < ZoneCount; i++)
            {
                int a = (Zones != null && i < Zones.Length) ? Zones[i] : 0;
                int b = (other.Zones != null && i < other.Zones.Length) ? other.Zones[i] : 0;
                if (a != b) return false;
            }
            return true;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int ToInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
