using System;

namespace McenterLite.Shared.Fan
{
    /// <summary>
    /// The fan curve model and its translation to the EC's 8-byte duty table.
    ///
    /// <para>
    /// Everything here is a FACT ABOUT THE HARDWARE, established by observation on an
    /// MSI Claw 8 EX AI+ and recorded in <c>docs/hardware-notes.md</c>. It lives in Shared,
    /// with no platform dependencies, precisely so it can be unit-tested on any machine -
    /// the table layout is the one part of this project that can physically damage the
    /// device if it is wrong, and it must not be the part that is only ever tested by hand.
    /// </para>
    /// </summary>
    public static class FanProfiles
    {
        /// <summary>The curve has five (temperature, duty) points.</summary>
        public const int Points = 5;

        /// <summary>The EC duty table is eight bytes.</summary>
        public const int TableLength = 8;

        /// <summary>
        /// First byte of the table we are allowed to write.
        /// </summary>
        /// <remarks>
        /// Only indices 1..6 belong to the curve. Index 0 and index 7 are EC state, not curve
        /// points, and must be preserved from the read-back. This is not defensive style - a
        /// Claw 8 EX ships with index 7 = 94, so writing the "whole" table both corrupts EC
        /// state and makes verification report a false mismatch.
        /// </remarks>
        public const int WriteFirst = 1;

        /// <summary>Last byte of the table we are allowed to write. See <see cref="WriteFirst"/>.</summary>
        public const int WriteLast = 6;

        /// <summary>Duty is the RAW EC byte - there is no scaling step. MSI's own UI stops here.</summary>
        public const int DutyCap = 75;

        /// <summary>
        /// The EC accepts duty up to 150, but this app never exposes it. Present only so the
        /// clamp has a documented absolute ceiling; no preset approaches it.
        /// </summary>
        public const int DutyHardMax = 150;

        public const int TempMin = 10;
        public const int TempMax = 99;

        /// <summary>How much cooler the <see cref="Ipc.FanPreset.Cooling"/> axis sits, in Celsius.</summary>
        public const int CoolingAxisOffset = 10;

        /// <summary>
        /// Fallback temperature axis, used only when the device's own axis cannot be read.
        /// Prefer the value read from the EC at startup - the EX's factory axis differs from this.
        /// </summary>
        public static int[] FallbackTemps() => new[] { 44, 54, 64, 74, 82 };

        /// <summary>
        /// Fallback duty curve, used only when the device's own curve cannot be read.
        /// </summary>
        public static int[] FallbackDuties() => new[] { 40, 49, 58, 67, 75 };

        /// <summary>Quiet Idle lowers the bottom of the curve and leaves the top alone.</summary>
        /// <remarks>
        /// These are the raw preset values. On a device with a duty floor they are RAISED to it by
        /// <see cref="EnforceDutyFloor"/> before any write - on the Claw 8 EX the first three points
        /// all sit below its floor of 58, so what actually reaches the EC is closer to
        /// <c>{58, 59, 60, 67, 75}</c>. Whether that is audibly quieter than Default depends
        /// entirely on the EX's own factory curve, which must be read from the device.
        /// </remarks>
        public static int[] QuietIdleDuties() => new[] { 20, 30, 45, 67, 75 };

        /// <summary>
        /// Cooling uses a FIXED duty table, not the device's own - it differs from Default purely
        /// by running that table on an axis shifted <see cref="CoolingAxisOffset"/> C cooler, so
        /// each duty step arrives earlier.
        /// </summary>
        public static int[] CoolingDuties() => new[] { 40, 49, 58, 67, 75 };

        /// <summary>
        /// Resolves a preset to the concrete (temps, duties) to apply.
        /// </summary>
        /// <param name="preset">The profile the user selected.</param>
        /// <param name="modelTemps">
        /// The device's own temperature axis, read from the EC. Null falls back to
        /// <see cref="FallbackTemps"/>.
        /// </param>
        /// <param name="modelDuties">
        /// The device's own factory duty curve, read from the EC. Null falls back to
        /// <see cref="FallbackDuties"/>.
        /// </param>
        /// <param name="dutyFloor">
        /// The model's idle duty floor, from <c>DeviceCaps.FanDutyFloor</c> (58 on the Claw 8 EX).
        /// Zero disables the floor. Passing the wrong value here does not damage anything, but it
        /// does produce a curve the firmware silently ignores at the bottom.
        /// </param>
        public static void Resolve(
            Ipc.FanPreset preset,
            int[] modelTemps,
            int[] modelDuties,
            out int[] temps,
            out int[] duties,
            int dutyFloor = 0)
        {
            var baseTemps = Sanitize(modelTemps, FallbackTemps());
            var baseDuties = Sanitize(modelDuties, FallbackDuties());

            switch (preset)
            {
                case Ipc.FanPreset.QuietIdle:
                    temps = baseTemps;
                    duties = QuietIdleDuties();
                    break;

                case Ipc.FanPreset.Cooling:
                    temps = ShiftAxis(baseTemps, -CoolingAxisOffset);
                    duties = CoolingDuties();
                    break;

                case Ipc.FanPreset.Default:
                default:
                    temps = baseTemps;
                    duties = baseDuties;
                    break;
            }

            temps = ClampTemps(temps);
            duties = EnforceDutyFloor(ClampDuties(duties), dutyFloor);
        }

        /// <summary>
        /// Raises every duty to <paramref name="floor"/>, then re-separates the points so the curve
        /// still rises.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Below the floor the firmware overrides the curve at idle regardless of what we write, so
        /// a lower point does not make the device quieter - it only inverts the loudness, leaving
        /// idle louder than light load. Raising to the floor is what makes the written curve
        /// describe what the fan will actually do.
        /// </para>
        /// <para>
        /// The second pass forces STRICTLY increasing, not merely non-decreasing as
        /// <see cref="ClampDuties"/> does: flooring collapses several points onto the same value,
        /// and a flat run reads as a dead zone to the EC. Points that collide with
        /// <see cref="DutyCap"/> stay flat - the cap wins over separation.
        /// </para>
        /// </remarks>
        public static int[] EnforceDutyFloor(int[] duties, int floor)
        {
            if (duties == null || duties.Length != Points)
                throw new ArgumentException($"Expected {Points} duty points.", nameof(duties));
            if (floor <= 0) return (int[])duties.Clone();

            int clampedFloor = Math.Min(floor, DutyCap);
            var result = new int[Points];
            for (int i = 0; i < Points; i++)
                result[i] = Math.Max(clampedFloor, duties[i]);

            for (int i = 1; i < Points; i++)
                if (result[i] <= result[i - 1])
                    result[i] = Math.Min(DutyCap, result[i - 1] + 1);

            return result;
        }

        private static int[] Sanitize(int[] candidate, int[] fallback) =>
            (candidate != null && candidate.Length == Points) ? (int[])candidate.Clone() : fallback;

        private static int[] ShiftAxis(int[] temps, int delta)
        {
            var result = new int[temps.Length];
            for (int i = 0; i < temps.Length; i++) result[i] = temps[i] + delta;
            return result;
        }

        /// <summary>
        /// Clamps to [<see cref="TempMin"/>, <see cref="TempMax"/>] and forces the axis strictly
        /// ascending. A non-monotonic axis is meaningless to the EC and its behaviour is undefined.
        /// </summary>
        public static int[] ClampTemps(int[] temps)
        {
            if (temps == null || temps.Length != Points)
                throw new ArgumentException($"Expected {Points} temperature points.", nameof(temps));

            var result = new int[Points];
            for (int i = 0; i < Points; i++)
            {
                int lo = (i == 0) ? TempMin : result[i - 1] + 1;
                // Leave room for every remaining point to still fit below TempMax.
                int hi = TempMax - (Points - 1 - i);
                if (lo > hi) lo = hi;
                result[i] = Math.Max(lo, Math.Min(hi, temps[i]));
            }
            return result;
        }

        /// <summary>
        /// Clamps every duty to [0, <see cref="DutyCap"/>] and forces the curve non-decreasing.
        /// A curve that falls as temperature rises would make the device quieter the hotter it
        /// gets, which is the one shape that can actually cook it.
        /// </summary>
        public static int[] ClampDuties(int[] duties)
        {
            if (duties == null || duties.Length != Points)
                throw new ArgumentException($"Expected {Points} duty points.", nameof(duties));

            var result = new int[Points];
            for (int i = 0; i < Points; i++)
            {
                int v = Math.Max(0, Math.Min(DutyCap, duties[i]));
                if (i > 0 && v < result[i - 1]) v = result[i - 1];
                result[i] = v;
            }
            return result;
        }

        /// <summary>
        /// Builds the full 8-byte table this curve represents.
        /// Layout: <c>{ 0, 0, D0, D1, D2, D3, D4, D4 }</c>.
        /// </summary>
        /// <remarks>
        /// Use this for VERIFICATION only. To write, use <see cref="ApplyToTable"/>, which
        /// preserves the EC's own boundary bytes instead of overwriting them with the zeros
        /// and duplicate that appear here.
        /// </remarks>
        public static byte[] BuildTable(int[] duties)
        {
            var d = ClampDuties(duties);
            return new byte[TableLength]
            {
                0,
                0,
                (byte)d[0],
                (byte)d[1],
                (byte)d[2],
                (byte)d[3],
                (byte)d[4],
                (byte)d[4],
            };
        }

        /// <summary>
        /// Produces the bytes to write, by patching only indices <see cref="WriteFirst"/>..<see cref="WriteLast"/>
        /// of the table as it was just read from the EC.
        /// </summary>
        /// <param name="current">The 8 bytes read back from the EC immediately before writing.</param>
        /// <param name="duties">The five duty points to apply.</param>
        public static byte[] ApplyToTable(byte[] current, int[] duties)
        {
            if (current == null || current.Length != TableLength)
                throw new ArgumentException($"Expected a {TableLength}-byte EC table.", nameof(current));

            var expected = BuildTable(duties);
            var result = (byte[])current.Clone();
            for (int i = WriteFirst; i <= WriteLast; i++)
                result[i] = expected[i];
            return result;
        }

        /// <summary>
        /// Checks a read-back against the curve we asked for, comparing ONLY the bytes we wrote.
        /// </summary>
        /// <remarks>
        /// Never trust a write. The EC can silently refuse or partially apply a table - for
        /// instance while MSI Center holds the ACPI-WMI interface - and a fan that reports success
        /// while running the old curve is exactly the failure this app must not have.
        /// </remarks>
        public static bool Matches(byte[] readback, int[] duties)
        {
            if (readback == null || readback.Length != TableLength) return false;

            var expected = BuildTable(duties);
            for (int i = WriteFirst; i <= WriteLast; i++)
                if (readback[i] != expected[i]) return false;
            return true;
        }

        /// <summary>Renders the written window of a table for logs and mismatch reports.</summary>
        public static string DescribeWriteWindow(byte[] table)
        {
            if (table == null || table.Length != TableLength) return "(invalid)";
            var parts = new string[WriteLast - WriteFirst + 1];
            for (int i = WriteFirst; i <= WriteLast; i++)
                parts[i - WriteFirst] = table[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
    }
}
