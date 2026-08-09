using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// What this device can actually do, decided by the helper and pushed to the widget once on
    /// connect. The widget owns no capability logic: a control it cannot see is a control it
    /// cannot mis-send, so every "is this supported" question is answered in exactly one place.
    ///
    /// <para>
    /// Encoded as <c>key=value;key=value</c> rather than nested JSON, so the wire format stays a
    /// flat object with string values. Unknown keys are ignored on parse, which lets a newer
    /// helper add capabilities without breaking an older widget.
    /// </para>
    /// </summary>
    public sealed class DeviceCaps
    {
        /// <summary>Model string as reported by the firmware, e.g. "Claw 8 EX AI+ CG3EM".</summary>
        public string Model { get; set; } = "";

        /// <summary>False when the device is not a supported Claw. The widget then shows only a notice.</summary>
        public bool Supported { get; set; }

        // ── TDP ──────────────────────────────────────────────────────────────────
        public int MaxPl1 { get; set; } = 30;
        public int MaxPl2 { get; set; } = 37;

        /// <summary>
        /// Power-limit ceilings applied while running on battery.
        /// </summary>
        /// <remarks>
        /// <para>
        /// MSI stores four values - <c>ManualPL1AC</c>, <c>ManualPL2AC</c>, <c>ManualPL1DC</c> and
        /// <c>ManualPL2DC</c> - so the firmware already supports a different limit on battery. This
        /// is a CEILING on the user's single choice, not a second setting: pick 35 W and the device
        /// runs 35 W plugged in and <see cref="MaxPl1Dc"/> unplugged, with no second control and
        /// nothing to remember to change.
        /// </para>
        /// <para>
        /// A ceiling above <see cref="MaxPl1"/> is meaningless and is treated as absent - the DC
        /// limit can only ever be the same or lower than the AC one.
        /// </para>
        /// </remarks>
        public int MaxPl1Dc { get; set; } = 25;

        /// <summary>Battery ceiling for PL2. See <see cref="MaxPl1Dc"/>.</summary>
        public int MaxPl2Dc { get; set; } = 30;

        /// <summary>Minimum PL2-over-PL1 headroom the platform enforces. 2 on the Claw 8 EX.</summary>
        public int Pl2MinOffset { get; set; } = 1;

        public int MinPl1 { get; set; } = 8;

        /// <summary>Which TDP path the helper resolved. <c>Unavailable</c> disables the controls.</summary>
        public Ipc.TdpBackendKind TdpBackend { get; set; } = Ipc.TdpBackendKind.Unavailable;

        // ── Feature availability ─────────────────────────────────────────────────
        public bool HasFan { get; set; }
        public bool HasHwMouse { get; set; }
        public bool HasIgcl { get; set; }

        /// <summary>
        /// Lowest duty the firmware will honour at idle (58 on the Claw 8 EX). Below this the
        /// firmware overrides the curve, so the UI should not imply the value is meaningful.
        /// </summary>
        public int FanDutyFloor { get; set; }

        public string Serialize()
        {
            var sb = new StringBuilder(160);
            Append(sb, "model", Model);
            Append(sb, "supported", Supported ? "1" : "0");
            Append(sb, "minPl1", MinPl1);
            Append(sb, "maxPl1", MaxPl1);
            Append(sb, "maxPl2", MaxPl2);
            Append(sb, "maxPl1Dc", MaxPl1Dc);
            Append(sb, "maxPl2Dc", MaxPl2Dc);
            Append(sb, "pl2Off", Pl2MinOffset);
            Append(sb, "tdpBackend", (int)TdpBackend);
            Append(sb, "fan", HasFan ? "1" : "0");
            Append(sb, "hwMouse", HasHwMouse ? "1" : "0");
            Append(sb, "igcl", HasIgcl ? "1" : "0");
            Append(sb, "dutyFloor", FanDutyFloor);
            return sb.ToString();
        }

        public static DeviceCaps Parse(string s)
        {
            var caps = new DeviceCaps();
            if (string.IsNullOrEmpty(s)) return caps;

            foreach (var pair in s.Split(';'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;

                var key = pair.Substring(0, eq);
                var value = Unescape(pair.Substring(eq + 1));

                switch (key)
                {
                    case "model": caps.Model = value; break;
                    case "supported": caps.Supported = value == "1"; break;
                    case "minPl1": caps.MinPl1 = ToInt(value, caps.MinPl1); break;
                    case "maxPl1": caps.MaxPl1 = ToInt(value, caps.MaxPl1); break;
                    case "maxPl2": caps.MaxPl2 = ToInt(value, caps.MaxPl2); break;
                    case "maxPl1Dc": caps.MaxPl1Dc = ToInt(value, caps.MaxPl1Dc); break;
                    case "maxPl2Dc": caps.MaxPl2Dc = ToInt(value, caps.MaxPl2Dc); break;
                    case "pl2Off": caps.Pl2MinOffset = ToInt(value, caps.Pl2MinOffset); break;
                    case "tdpBackend":
                        var backend = ToInt(value, (int)Ipc.TdpBackendKind.Unavailable);
                        caps.TdpBackend = Enum.IsDefined(typeof(Ipc.TdpBackendKind), backend)
                            ? (Ipc.TdpBackendKind)backend
                            : Ipc.TdpBackendKind.Unavailable;
                        break;
                    case "fan": caps.HasFan = value == "1"; break;
                    // "charge" retired with the charge-limit feature; an older helper may still
                    // send it, and the default branch below drops it harmlessly.
                    case "hwMouse": caps.HasHwMouse = value == "1"; break;
                    case "igcl": caps.HasIgcl = value == "1"; break;
                    case "dutyFloor": caps.FanDutyFloor = ToInt(value, caps.FanDutyFloor); break;
                    // Unknown keys are ignored on purpose - forward compatibility.
                }
            }

            return caps;
        }

        /// <summary>
        /// Clamps a requested (PL1, PL2) pair to what this device accepts on AC. Applied by the
        /// HELPER on every Set, never only by the widget - the pipe is reachable by any app on the
        /// box.
        /// </summary>
        public void ClampPowerLimits(ref int pl1, ref int pl2) =>
            Clamp(ref pl1, ref pl2, MaxPl1, MaxPl2);

        /// <summary>
        /// Clamps a pair to the battery ceilings, for the <c>ManualPL*DC</c> values.
        /// </summary>
        /// <remarks>
        /// Runs the same clamp with lower ceilings rather than a bare <c>Math.Min</c>, so the
        /// PL2-over-PL1 headroom rule is re-satisfied after capping. Capping the two independently
        /// could otherwise produce a DC pair the firmware rejects.
        /// </remarks>
        public void ClampPowerLimitsForBattery(ref int pl1, ref int pl2)
        {
            // A DC ceiling above the AC one is meaningless; treat it as absent rather than
            // letting a bad capability value raise the limit on battery.
            int maxPl1 = Math.Min(MaxPl1Dc, MaxPl1);
            int maxPl2 = Math.Min(MaxPl2Dc, MaxPl2);

            // Carry the GAP down rather than capping the two independently. A coupled 35/37 pair
            // becomes 25/27 on battery - the same relationship at a lower ceiling - where capping
            // separately would give 25/30 and silently hand the user boost headroom they never
            // asked for. A deliberately widened pair keeps its gap until the PL2 ceiling takes it.
            int gap = pl2 - pl1;
            if (gap < Pl2MinOffset) gap = Pl2MinOffset;

            if (pl1 > maxPl1) pl1 = maxPl1;
            if (pl1 < MinPl1) pl1 = MinPl1;

            pl2 = pl1 + gap;
            if (pl2 > maxPl2) pl2 = maxPl2;

            Clamp(ref pl1, ref pl2, maxPl1, maxPl2);
        }

        /// <summary>
        /// Recomputes the pair after the user moved the <b>PL1</b> slider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two limits move together. Raising PL1 by a watt raises PL2 by a watt, holding the
        /// firmware's minimum headroom, so the pair walks up as one from 8/10 to
        /// <see cref="MaxPl1"/>/<see cref="MaxPl1"/>+2 - 35/37 on AC, 25/27 on battery. Past that
        /// PL1 is pinned and only PL2 can go further, which is exactly the shape MSI's own UI
        /// produced in the captured transcripts.
        /// </para>
        /// <para>
        /// Consequence worth knowing: lowering PL1 from its ceiling pulls a widened PL2 back down
        /// to PL1 + 2. That is what "they move together" means, and it is also the only way to
        /// close a gap once opened.
        /// </para>
        /// </remarks>
        public void CoupleFromPl1(ref int pl1, ref int pl2)
        {
            if (pl1 < MinPl1) pl1 = MinPl1;
            if (pl1 > MaxPl1) pl1 = MaxPl1;

            pl2 = pl1 + Pl2MinOffset;
            if (pl2 > MaxPl2) pl2 = MaxPl2;

            Clamp(ref pl1, ref pl2, MaxPl1, MaxPl2);
        }

        /// <summary>
        /// Recomputes the pair after the user moved the <b>PL2</b> slider.
        /// </summary>
        /// <remarks>
        /// The mirror of <see cref="CoupleFromPl1"/>: PL1 follows PL2 down by the same headroom
        /// until PL1 reaches its own ceiling, after which PL2 continues alone to
        /// <see cref="MaxPl2"/>. So PL2 is the slider that reaches the top - 45 W on AC, 30 W on
        /// battery - and PL1 simply stops.
        /// </remarks>
        public void CoupleFromPl2(ref int pl1, ref int pl2)
        {
            int minPl2 = MinPl1 + Pl2MinOffset;
            if (pl2 < minPl2) pl2 = minPl2;
            if (pl2 > MaxPl2) pl2 = MaxPl2;

            pl1 = pl2 - Pl2MinOffset;
            if (pl1 > MaxPl1) pl1 = MaxPl1;
            if (pl1 < MinPl1) pl1 = MinPl1;

            Clamp(ref pl1, ref pl2, MaxPl1, MaxPl2);
        }

        private void Clamp(ref int pl1, ref int pl2, int maxPl1, int maxPl2)
        {
            if (pl1 < MinPl1) pl1 = MinPl1;
            if (pl1 > maxPl1) pl1 = maxPl1;

            int floor = pl1 + Pl2MinOffset;
            if (pl2 < floor) pl2 = floor;
            if (pl2 > maxPl2) pl2 = maxPl2;

            // If the PL2 ceiling cannot satisfy the required headroom, PL1 must give way -
            // the offset is a firmware rule, whereas PL1 is a preference.
            if (pl2 - pl1 < Pl2MinOffset)
            {
                pl1 = pl2 - Pl2MinOffset;
                if (pl1 < MinPl1) pl1 = MinPl1;
            }
        }

        private static int ToInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static void Append(StringBuilder sb, string key, int value) =>
            Append(sb, key, value.ToString(CultureInfo.InvariantCulture));

        private static void Append(StringBuilder sb, string key, string value)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(key).Append('=').Append(Escape(value ?? ""));
        }

        // The model string is firmware-supplied and could in principle contain our delimiters.
        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace(";", "\\s").Replace("=", "\\e");

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                i++;
                switch (s[i])
                {
                    case 's': sb.Append(';'); break;
                    case 'e': sb.Append('='); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(s[i]); break;
                }
            }
            return sb.ToString();
        }
    }
}
