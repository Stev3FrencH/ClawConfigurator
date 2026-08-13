using System;
using System.Globalization;
using System.Linq;
using System.Management;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads and sets the fan duty tables through <c>MSI_ACPI.Get_Fan</c> / <c>Set_Fan</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gate G2. The read was measured on 2026-08-12 with <c>Diagnostics/Watch-Fan.ps1</c>, by
    /// diffing snapshots taken across MSI Center M's own Auto / minimum / maximum fan settings.
    /// <b>Two fans, one table each</b>, at sub-functions 1 and 2:
    /// </para>
    /// <code>
    /// byte:  0   1    2  3  4  5  6  7    8      9..31
    ///       01  58   70 74 76 78 80 84   94      00 …
    ///        |   |    \___________ ___/    \_ ceiling, EC state - never written by MSI
    ///        |   |                v
    ///        |   |         duty % at 47 50 57 64 71 78 C
    ///        |   idle duty, below the first breakpoint
    ///        status
    /// </code>
    /// <para>
    /// <b>Duty is a plain percentage, 0-100.</b> MSI Center M's own slider wrote exactly 0 at
    /// minimum and exactly 100 at maximum. That refutes two things this project carried from desk
    /// research and never measured: a cap at duty 75, and a possible 0-150 raw-byte scale.
    /// </para>
    /// <para>
    /// <b>The temperature axis is fixed and is not written here.</b> It did not move across any
    /// snapshot, including MSI Center's own Advanced mode. <see cref="Read"/> prints it from
    /// <c>Get_Temperature</c> for reference only.
    /// </para>
    /// <para>
    /// <b>The firmware does not enforce a duty floor.</b> A table of zeros was accepted and both
    /// tachometers read 0 - the fans stopped, at every temperature. The 58 above is the factory
    /// curve's first value, not a limit the EC imposes. Nothing underneath this command will
    /// refuse a dangerous table, so <see cref="Set"/> warns loudly and this is the only place in
    /// the probe where that warning is the point rather than a formality.
    /// </para>
    /// </remarks>
    internal static class Fan
    {
        private const string ReadMethod = "Get_Fan";
        private const string WriteMethod = "Set_Fan";
        private const string TemperatureMethod = "Get_Temperature";

        /// <summary>Sub-function 0 of <c>Get_Fan</c> returns live tachometers, not a table.</summary>
        private const byte TachSubFunction = 0x00;

        private const int TachFan1Byte = 2;
        private const int TachFan2Byte = 4;

        /// <summary>The two fans, as sub-function values.</summary>
        private static readonly byte[] Fans = { 1, 2 };

        /// <summary>Byte 1 is the idle duty; bytes 2-7 are the six curve points.</summary>
        private const int FirstDutyByte = 1;
        private const int DutyCount = 7;
        private const int CeilingByte = 8;

        private const int MaxDuty = 100;

        /// <summary>
        /// The factory table, captured from this device in Auto on 2026-08-12. Both fans read
        /// identically. This is what <c>--set-fan &lt;fan&gt; auto</c> restores.
        /// </summary>
        /// <remarks>
        /// Recorded rather than re-read at restore time on purpose: the whole reason to keep it is
        /// for the case where the live table is already something we wrote. Cross-checked against
        /// <c>Default_Fan</c> in MSI Center M's registry, which carries the six curve points.
        /// </remarks>
        private static readonly byte[] FactoryDuties = { 58, 70, 74, 76, 78, 80, 84 };

        public static int Read(string[] args)
        {
            _ = args;

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (instance)
            {
                PrintTemperatureAxis(instance);
                Console.WriteLine();

                foreach (var fan in Fans)
                {
                    if (!TryReadTable(instance, fan, out var package, out error))
                    {
                        Console.Error.WriteLine($"Fan {fan}: {error}");
                        continue;
                    }

                    ReportTable($"Fan {fan}", package);
                }

                Console.WriteLine();
                PrintTachometers(instance);
            }

            return 0;
        }

        public static int Set(string[] args)
        {
            if (args.Length < 2)
            {
                PrintSetUsage();
                return 64;
            }

            if (!TryParseTarget(args[0], out var targets))
            {
                Console.Error.WriteLine($"'{args[0]}' is not a fan. Use 1, 2 or both.");
                return 64;
            }

            bool restoring = args[1].Equals("auto", StringComparison.OrdinalIgnoreCase);

            byte[] duties;
            if (restoring)
            {
                duties = (byte[])FactoryDuties.Clone();
            }
            else if (!TryParseDuties(args[1], out duties, out var parseError))
            {
                Console.Error.WriteLine(parseError);
                return 64;
            }

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            if (!restoring) WarnAboutTable(duties);

            using (instance)
            {
                foreach (var fan in targets)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== Fan {fan} ===");

                    int code = WriteOneFan(instance, fan, duties);
                    if (code != 0) return code;
                }
            }

            Console.WriteLine();
            Console.WriteLine(restoring
                ? "Restored the factory table."
                : "Applied. Listen to the device and re-run --fan to confirm it held.");

            return 0;
        }

        private static int WriteOneFan(ManagementObject instance, byte fan, byte[] duties)
        {
            if (!TryReadTable(instance, fan, out var before, out var error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            ReportTable("Before", before);

            // Echo the package the firmware just returned, changing only the sub-function and the
            // duties. Byte 8 is a ceiling MSI's own UI never touched, and bytes past it are
            // unexplained - sending back what the firmware just reported is not a guess, whereas
            // sending zeros would be.
            var payload = (byte[])before.Clone();
            payload[0] = fan;
            Array.Copy(duties, 0, payload, FirstDutyByte, DutyCount);

            Console.WriteLine($"Sending {WriteMethod}: {MsiAcpi.Hex(payload.Take(16))} …");

            var result = MsiAcpi.Invoke(instance, WriteMethod, payload, out error);
            if (result == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (result) MsiAcpi.DumpResult(result);

            // Confirm with a SEPARATE read. Set_* on this class is not known to echo what was
            // written - Set_SlaveBattery returns a constant - so the reply above proves nothing.
            if (!TryReadTable(instance, fan, out var after, out error))
            {
                Console.Error.WriteLine($"Wrote, but could not read back: {error}");
                return 1;
            }

            ReportTable("After", after);

            var actual = Duties(after);
            if (!actual.SequenceEqual(duties))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    $"FAILED: asked for {Join(duties)}, device reports {Join(actual)}.");
                Console.Error.WriteLine();

                if (actual.SequenceEqual(Duties(before)))
                {
                    Console.Error.WriteLine(
                        "Nothing moved at all, so the write shape is wrong rather than the values.");
                    Console.Error.WriteLine(
                        "Check whether Set_Fan wants the fan in a different byte, and confirm its");
                    Console.Error.WriteLine(
                        "declared shape with --wmi-method MSI_ACPI Set_Fan.");
                }
                else
                {
                    Console.Error.WriteLine(
                        "Some bytes moved, so the write reached the table but was transformed.");
                    Console.Error.WriteLine(
                        "Compare the two lines above byte by byte before trying again.");
                }

                return 5;
            }

            Console.WriteLine($"OK - fan {fan} now reports {Join(actual)}.");
            return 0;
        }

        /// <summary>
        /// Says plainly what a table will do before it is sent.
        /// </summary>
        /// <remarks>
        /// This warns rather than refuses, which is a deliberate decision: MSI Center M itself
        /// permits a table of zeros, and this is a discovery tool whose job is to reach what the
        /// firmware reaches. The shipping widget makes the same choice and carries the same
        /// warning - see the fan card. What must never happen is a stopped fan going unmentioned.
        /// </remarks>
        private static void WarnAboutTable(byte[] duties)
        {
            if (duties.All(d => d == 0))
            {
                Console.WriteLine();
                Console.WriteLine("WARNING: every duty is 0. This STOPS the fan at every");
                Console.WriteLine("         temperature, including under load. Verified on this");
                Console.WriteLine("         device - the firmware accepts it and the tachometer");
                Console.WriteLine("         reads zero. Restore with:  --set-fan both auto");
            }
            else if (duties.Any(d => d == 0))
            {
                Console.WriteLine();
                Console.WriteLine("WARNING: some duties are 0. The fan will be stopped in those");
                Console.WriteLine("         bands. The firmware does not floor this for you.");
            }

            var curve = duties.Skip(1).ToArray();
            for (int i = 1; i < curve.Length; i++)
            {
                if (curve[i] < curve[i - 1])
                {
                    Console.WriteLine();
                    Console.WriteLine("NOTE   : the curve falls as temperature rises. MSI's own");
                    Console.WriteLine("         table only ever rises. Sending it anyway.");
                    break;
                }
            }
        }

        private static bool TryReadTable(
            ManagementObject instance, byte fan, out byte[] package, out string error)
        {
            package = null;

            var result = MsiAcpi.Invoke(instance, ReadMethod, new byte[] { fan }, out error);
            if (result == null) return false;

            using (result)
            {
                package = MsiAcpi.ExtractBytes(result);
            }

            if (package == null || package.Length <= CeilingByte)
            {
                error = $"{ReadMethod} sub-function {fan} returned no usable package.";
                return false;
            }

            return true;
        }

        private static byte[] Duties(byte[] package) =>
            package.Skip(FirstDutyByte).Take(DutyCount).ToArray();

        private static void ReportTable(string label, byte[] package)
        {
            var duties = Duties(package);

            Console.WriteLine(
                $"{label,-8}: idle {duties[0],3}%  |  curve {Join(duties.Skip(1))}  "
                + $"|  ceiling {package[CeilingByte]}");
            Console.WriteLine($"          {MsiAcpi.Hex(package.Take(16))} …");
        }

        /// <summary>
        /// Prints the fixed temperature breakpoints the duties are indexed against.
        /// </summary>
        /// <remarks>
        /// Layout is not a plain run: the first breakpoint is byte 1 and the remaining five are
        /// bytes 4-8, with bytes 2 and 3 holding 85 and 105 - meaning not established, plausibly
        /// throttle and critical. Read exactly the bytes that were measured rather than assuming
        /// the gap is part of the axis.
        /// </remarks>
        private static void PrintTemperatureAxis(ManagementObject instance)
        {
            var result = MsiAcpi.Invoke(instance, TemperatureMethod, new byte[] { 1 }, out var error);
            if (result == null)
            {
                Console.WriteLine($"Temperature axis: unavailable ({error})");
                return;
            }

            byte[] package;
            using (result) package = MsiAcpi.ExtractBytes(result);

            if (package == null || package.Length < 9)
            {
                Console.WriteLine("Temperature axis: no usable package.");
                return;
            }

            var axis = new[] { package[1], package[4], package[5], package[6], package[7], package[8] };

            Console.WriteLine($"Breakpoints    : {string.Join(", ", axis.Select(t => t + " C"))}");
            Console.WriteLine($"Unexplained    : bytes 2,3 = {package[2]}, {package[3]}");
            Console.WriteLine("                 (fixed - MSI Center M's Advanced UI edits duty only)");
        }

        private static void PrintTachometers(ManagementObject instance)
        {
            var result = MsiAcpi.Invoke(
                instance, ReadMethod, new byte[] { TachSubFunction }, out var error);

            if (result == null)
            {
                Console.WriteLine($"Tachometers   : unavailable ({error})");
                return;
            }

            byte[] package;
            using (result) package = MsiAcpi.ExtractBytes(result);

            if (package == null || package.Length <= TachFan2Byte)
            {
                Console.WriteLine("Tachometers   : no usable package.");
                return;
            }

            // Units unknown. Auto idle read 134 on this device, which is above 100, so this is NOT
            // the duty percentage read back - do not present it as one.
            Console.WriteLine(
                $"Tachometers   : fan 1 = {package[TachFan1Byte]}, fan 2 = {package[TachFan2Byte]} "
                + "(raw; units not established)");

            if (package[TachFan1Byte] == 0 || package[TachFan2Byte] == 0)
                Console.WriteLine("                a zero here means that fan is STOPPED.");
        }

        private static bool TryParseTarget(string text, out byte[] targets)
        {
            if (text.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                targets = (byte[])Fans.Clone();
                return true;
            }

            if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte fan)
                && Fans.Contains(fan))
            {
                targets = new[] { fan };
                return true;
            }

            targets = null;
            return false;
        }

        private static bool TryParseDuties(string text, out byte[] duties, out string error)
        {
            duties = null;
            error = null;

            var parts = text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != DutyCount)
            {
                error = $"Expected {DutyCount} duties (idle plus six curve points), got {parts.Length}. "
                      + "e.g. 30;35;45;55;65;75;85";
                return false;
            }

            var parsed = new byte[DutyCount];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int value))
                {
                    error = $"'{parts[i].Trim()}' is not a number.";
                    return false;
                }

                if (value < 0 || value > MaxDuty)
                {
                    error = $"Refusing {value}: duty is a percentage, 0-{MaxDuty}.";
                    return false;
                }

                parsed[i] = (byte)value;
            }

            duties = parsed;
            return true;
        }

        private static string Join(System.Collections.Generic.IEnumerable<byte> values) =>
            string.Join(";", values);

        private static void PrintSetUsage()
        {
            Console.Error.WriteLine("Usage: --set-fan <1|2|both> <idle;d1;d2;d3;d4;d5;d6>");
            Console.Error.WriteLine("       --set-fan <1|2|both> auto");
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Seven duties, 0-{MaxDuty}: the idle duty then the six curve");
            Console.Error.WriteLine("  points at 47, 50, 57, 64, 71 and 78 C.");
            Console.Error.WriteLine($"  'auto' restores the factory table {Join(FactoryDuties)}.");
        }
    }
}
