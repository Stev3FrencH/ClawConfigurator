using System;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// A snapshot of what the fan hardware is actually doing, pushed helper -&gt; widget at ~1 Hz
    /// while the widget is visible.
    ///
    /// <para>
    /// This carries the RAW EC read-back, not a summary. The widget needs to be able to say
    /// "we asked for X and the EC contains Y" - a control panel that reports success while the
    /// hardware quietly ignored the write is worse than one that reports nothing.
    /// </para>
    ///
    /// <para>Encoding: <c>b0,..,b7|control|readOk|fullSpeed|rpm|t0,..,tN|matches</c></para>
    /// </summary>
    public sealed class FanState
    {
        /// <summary>The eight duty bytes as read from the EC.</summary>
        public byte[] Table { get; set; }

        /// <summary>The EC's control bit: true when software owns the curve, false when firmware does.</summary>
        public bool ControlEnabled { get; set; }

        /// <summary>False when the read itself failed. Everything else is then meaningless.</summary>
        public bool ReadOk { get; set; }

        /// <summary>The full-speed override bit. Independent of the curve.</summary>
        public bool FullSpeed { get; set; }

        /// <summary>Measured fan speed from the tachometer, or -1 when unavailable.</summary>
        public int Rpm { get; set; } = -1;

        /// <summary>The EC's temperature axis, as read back.</summary>
        public int[] Temps { get; set; }

        /// <summary>
        /// Whether <see cref="Table"/> matches the curve the selected preset resolves to.
        /// </summary>
        /// <remarks>
        /// Decided by the HELPER, not the widget, and this is not a style preference. Resolving a
        /// preset to duties needs the device's factory curve and its model duty floor, both of
        /// which only the helper has. A widget that recomputes it from the preset index alone
        /// disagrees with what was actually written and cries mismatch on every push.
        /// Defaults true so a helper that cannot determine it stays quiet rather than alarming.
        /// </remarks>
        public bool Matches { get; set; } = true;

        public string Serialize()
        {
            var sb = new StringBuilder(80);

            AppendBytes(sb, Table);
            sb.Append('|').Append(ControlEnabled ? '1' : '0');
            sb.Append('|').Append(ReadOk ? '1' : '0');
            sb.Append('|').Append(FullSpeed ? '1' : '0');
            sb.Append('|').Append(Rpm.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');
            AppendInts(sb, Temps);
            sb.Append('|').Append(Matches ? '1' : '0');

            return sb.ToString();
        }

        public static FanState Parse(string s)
        {
            var state = new FanState();
            if (string.IsNullOrEmpty(s)) return state;

            var parts = s.Split('|');
            if (parts.Length > 0) state.Table = ParseBytes(parts[0]);
            if (parts.Length > 1) state.ControlEnabled = parts[1] == "1";
            if (parts.Length > 2) state.ReadOk = parts[2] == "1";
            if (parts.Length > 3) state.FullSpeed = parts[3] == "1";
            if (parts.Length > 4) state.Rpm = ToInt(parts[4], -1);
            if (parts.Length > 5) state.Temps = ParseInts(parts[5]);
            // Absent on an older helper: stay quiet rather than reporting a mismatch we cannot judge.
            if (parts.Length > 6) state.Matches = parts[6] == "1";

            return state;
        }

        private static void AppendBytes(StringBuilder sb, byte[] values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void AppendInts(StringBuilder sb, int[] values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
        }

        private static byte[] ParseBytes(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            var result = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int v = ToInt(parts[i], 0);
                result[i] = (byte)Math.Max(0, Math.Min(255, v));
            }
            return result;
        }

        private static int[] ParseInts(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = ToInt(parts[i], 0);
            return result;
        }

        private static int ToInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
