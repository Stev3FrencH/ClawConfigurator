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

        /// <summary>Minimum PL2-over-PL1 headroom the platform enforces. 2 on the Claw 8 EX.</summary>
        public int Pl2MinOffset { get; set; } = 1;

        public int MinPl1 { get; set; } = 8;

        /// <summary>Which TDP path the helper resolved. <c>Unavailable</c> disables the controls.</summary>
        public Ipc.TdpBackendKind TdpBackend { get; set; } = Ipc.TdpBackendKind.Unavailable;

        // ── Battery charge limit ─────────────────────────────────────────────────
        /// <summary>
        /// Lowest charge limit this app OFFERS. 50 - a product choice, not a hardware limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The firmware accepts 20.</b> That was measured (gate G3), and it is recorded in
        /// <c>docs/hardware-notes.md</c> rather than here, because this number is not it. Nothing
        /// below about half charge is useful for battery longevity, so the slider stops at 50 and
        /// spends its travel where the values matter.
        /// </para>
        /// <para>
        /// Kept as a capability rather than a widget constant so the helper clamps to the same
        /// number the slider offers. The retired version of this feature had the same kind of floor
        /// at 60, and it was mistaken for a firmware limit more than once - hence the emphasis.
        /// </para>
        /// </remarks>
        public int MinChargeLimit { get; set; } = 50;

        /// <summary>100, i.e. charge to full. This is how "no limit" is expressed.</summary>
        public int MaxChargeLimit { get; set; } = 100;

        // ── Feature availability ─────────────────────────────────────────────────
        public bool HasChargeLimit { get; set; }
        public bool HasHwMouse { get; set; }

        /// <summary>The controller's nine RGB LEDs, over the vendor HID channel.</summary>
        public bool HasRgb { get; set; }

        public bool HasIgcl { get; set; }

        public string Serialize()
        {
            var sb = new StringBuilder(160);
            Append(sb, "model", Model);
            Append(sb, "supported", Supported ? "1" : "0");
            Append(sb, "minPl1", MinPl1);
            Append(sb, "maxPl1", MaxPl1);
            Append(sb, "maxPl2", MaxPl2);
            Append(sb, "pl2Off", Pl2MinOffset);
            Append(sb, "tdpBackend", (int)TdpBackend);
            Append(sb, "minCharge", MinChargeLimit);
            Append(sb, "maxCharge", MaxChargeLimit);
            Append(sb, "hasCharge", HasChargeLimit ? "1" : "0");
            Append(sb, "hwMouse", HasHwMouse ? "1" : "0");
            Append(sb, "hasRgb", HasRgb ? "1" : "0");
            Append(sb, "igcl", HasIgcl ? "1" : "0");
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
                    case "pl2Off": caps.Pl2MinOffset = ToInt(value, caps.Pl2MinOffset); break;
                    case "tdpBackend":
                        var backend = ToInt(value, (int)Ipc.TdpBackendKind.Unavailable);
                        caps.TdpBackend = Enum.IsDefined(typeof(Ipc.TdpBackendKind), backend)
                            ? (Ipc.TdpBackendKind)backend
                            : Ipc.TdpBackendKind.Unavailable;
                        break;
                    // "led", "fan", "dutyFloor", "maxPl1Dc" and "maxPl2Dc" retired with their
                    // features. An older helper may still send them, and unknown keys fall through
                    // harmlessly. So did "charge" - the charge limit came back on 2026-08-12 under
                    // NEW key names rather than reclaiming that one, because an old helper still
                    // sends it with the old enabled/percent meaning.
                    case "minCharge": caps.MinChargeLimit = ToInt(value, caps.MinChargeLimit); break;
                    case "maxCharge": caps.MaxChargeLimit = ToInt(value, caps.MaxChargeLimit); break;
                    case "hasCharge": caps.HasChargeLimit = value == "1"; break;
                    case "hwMouse": caps.HasHwMouse = value == "1"; break;
                    // "hasRgb", not the retired "led": that key meant a brightness on/off flag in
                    // a registry mirror, and an old helper still sends it with that meaning.
                    case "hasRgb": caps.HasRgb = value == "1"; break;
                    case "igcl": caps.HasIgcl = value == "1"; break;
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
        /// Clamps a requested charge limit into what the firmware accepts. Applied by the HELPER
        /// on every Set, for the same reason as <see cref="ClampPowerLimits"/>.
        /// </summary>
        public void ClampChargeLimit(ref int percent)
        {
            if (percent < MinChargeLimit) percent = MinChargeLimit;
            if (percent > MaxChargeLimit) percent = MaxChargeLimit;
        }

        /// <summary>
        /// Recomputes the pair after the user moved the <b>PL1</b> slider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two limits are INDEPENDENT, bound only by <see cref="Pl2MinOffset"/> - PL2 must sit
        /// at least that far above PL1, and any wider gap is legal and is kept. So PL1 moves on its
        /// own and PL2 stays exactly where the user left it, in both directions: lowering PL1 opens
        /// the gap rather than dragging PL2 down with it.
        /// </para>
        /// <para>
        /// The one case where PL2 moves is when PL1 rises into it. PL1 is then <b>pushed through</b>
        /// rather than blocked - the requested PL1 is honoured and PL2 is carried up to
        /// <c>PL1 + Pl2MinOffset</c>. Blocking PL1 at <c>PL2 - Pl2MinOffset</c> would satisfy the
        /// same rule, but it makes the common case (both limits at the bottom, raise the sustained
        /// limit) impossible without moving the other slider first, one watt at a time.
        /// </para>
        /// <para>
        /// This REPLACED a rigid coupling that recomputed <c>pl2 = pl1 + Pl2MinOffset</c> on every
        /// move. That was inferred from four captured MSI Center pairs - 8/10, 17/19, 35/37, 35/45 -
        /// which are all consistent with rigid coupling but only because their gap happened to be
        /// at the minimum. Watching MSI Center directly showed the weaker rule. See
        /// <c>docs/hardware-notes.md</c>.
        /// </para>
        /// </remarks>
        public void ConstrainFromPl1(ref int pl1, ref int pl2)
        {
            if (pl1 < MinPl1) pl1 = MinPl1;
            if (pl1 > MaxPl1) pl1 = MaxPl1;

            // Clamp is already exactly PL1-driven: it raises PL2 to the floor when the gap is too
            // small, never lowers it, and gives up PL1 only when MaxPl2 makes the rule
            // unsatisfiable. Nothing to do beforehand.
            Clamp(ref pl1, ref pl2, MaxPl1, MaxPl2);
        }

        /// <summary>
        /// Recomputes the pair after the user moved the <b>PL2</b> slider.
        /// </summary>
        /// <remarks>
        /// The mirror of <see cref="ConstrainFromPl1"/>. PL2 moves on its own, and PL1 is pulled
        /// down only when PL2 descends into the headroom above it.
        /// </remarks>
        public void ConstrainFromPl2(ref int pl1, ref int pl2)
        {
            int minPl2 = MinPl1 + Pl2MinOffset;
            if (pl2 < minPl2) pl2 = minPl2;
            if (pl2 > MaxPl2) pl2 = MaxPl2;

            // Load-bearing, and the whole reason this is not just a call to Clamp. PL2 is what the
            // user moved, so PL1 is what gives way. Clamp restores the same invariant from the
            // other side - it would push PL2 back UP to pl1 + offset and undo the drag.
            int ceiling = pl2 - Pl2MinOffset;
            if (pl1 > ceiling) pl1 = ceiling;

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
