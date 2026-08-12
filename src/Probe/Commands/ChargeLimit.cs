using System;
using System.Globalization;
using System.Management;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads and sets the battery charge limit through <c>MSI_ACPI.Get_AP</c> / <c>Set_AP</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gate G3. The read was measured on 2026-08-08 by sweeping every <c>Get_*</c> method across
    /// sub-functions and diffing snapshots taken at MSI Center's 100 / 80 / 60 settings, and
    /// re-confirmed on 2026-08-12 against MSI Center reporting 80%:
    /// </para>
    /// <code>
    /// byte:  0  1  2  3  4  5   6..31
    ///       01 00 00 C6 80 XX   00 …      XX = percent | 0x80
    /// </code>
    /// <para>
    /// Byte 0 is presumed a success flag, bytes 3 and 4 presumed to identify the register. Neither
    /// is verified - only byte 5 is. That uncertainty is exactly why the write below echoes the
    /// package it just read rather than building one from scratch: whatever those bytes mean, the
    /// firmware has just told us what it expects to see in them.
    /// </para>
    /// <para>
    /// <b>The write has never been attempted before 2026-08-12.</b> The read being solid says
    /// nothing about it - <c>Set_SlaveBattery</c> on this same class returns a constant that does
    /// not echo what was written, so its reply proves nothing either. Every write here is confirmed
    /// by a separate read.
    /// </para>
    /// </remarks>
    internal static class ChargeLimit
    {
        private const string ReadMethod = "Get_AP";
        private const string WriteMethod = "Set_AP";

        /// <summary>Input byte 0. Sub-function 0 is the one that carries the charge limit.</summary>
        private const byte SubFunction = 0x00;

        private const int ValueByte = 5;
        private const byte EncodingFlag = 0x80;

        /// <summary>
        /// Firmware accepts 20-100. The widget used to offer 60-100, which was a scope choice
        /// rather than a hardware limit - do not confuse the two.
        /// </summary>
        private const int MinPercent = 20;
        private const int MaxPercent = 100;

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
                if (!TryReadPackage(instance, out var package, out error))
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                Report("Charge limit", package);
            }

            return 0;
        }

        public static int Set(string[] args)
        {
            if (args.Length < 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
            {
                Console.Error.WriteLine($"Usage: --set-charge-limit <{MinPercent}-{MaxPercent}>");
                return 64;
            }

            if (percent < MinPercent || percent > MaxPercent)
            {
                Console.Error.WriteLine(
                    $"Refusing {percent}%: the firmware accepts {MinPercent}-{MaxPercent}.");
                return 64;
            }

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (instance)
            {
                if (!TryReadPackage(instance, out var before, out error))
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                Report("Before", before);

                // Echo the package the firmware just returned, changing only the sub-function and
                // the value. Bytes 3 and 4 are unexplained, and sending zeros where the firmware
                // reported something is a guess; sending them back is not.
                var payload = (byte[])before.Clone();
                payload[0] = SubFunction;
                payload[ValueByte] = (byte)(percent | EncodingFlag);

                Console.WriteLine($"Sending {WriteMethod}: {MsiAcpi.Hex(payload)}");

                var result = MsiAcpi.Invoke(instance, WriteMethod, payload, out error);
                if (result == null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                using (result) MsiAcpi.DumpResult(result);

                // Confirm with a SEPARATE read. Set_* on this class is not known to echo what was
                // written, so its own reply is not evidence.
                if (!TryReadPackage(instance, out var after, out error))
                {
                    Console.Error.WriteLine($"Wrote, but could not read back: {error}");
                    return 1;
                }

                Report("After", after);

                int actual = Decode(after);
                if (actual != percent)
                {
                    Console.Error.WriteLine(
                        $"FAILED: asked for {percent}%, device reports "
                        + $"{(actual < 0 ? "an undecodable value" : actual + "%")}.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        "If byte 5 did not move at all, the write shape is wrong rather than the");
                    Console.Error.WriteLine(
                        "value - try a payload of zeros with only byte 0 and byte 5 set, and check");
                    Console.Error.WriteLine(
                        "whether Set_AP wants a different sub-function.");
                    return 5;
                }

                Console.WriteLine();
                Console.WriteLine($"OK - charge limit is now {percent}%.");
                Console.WriteLine("Cross-check it in MSI Center M before trusting this.");
            }

            return 0;
        }

        private static bool TryReadPackage(
            ManagementObject instance, out byte[] package, out string error)
        {
            package = null;

            var result = MsiAcpi.Invoke(instance, ReadMethod, new byte[] { SubFunction }, out error);
            if (result == null) return false;

            using (result)
            {
                package = MsiAcpi.ExtractBytes(result);
            }

            if (package == null || package.Length <= ValueByte)
            {
                error = $"{ReadMethod} returned no usable package.";
                return false;
            }

            return true;
        }

        /// <summary>The percentage in byte 5, or -1 if bit 7 is clear.</summary>
        /// <remarks>
        /// A byte without the flag is not decoded as a percentage. It would still produce a
        /// plausible 0-127 number, and reporting that as the charge limit would be a confident
        /// wrong answer rather than a visible failure.
        /// </remarks>
        private static int Decode(byte[] package)
        {
            byte raw = package[ValueByte];
            return (raw & EncodingFlag) == 0 ? -1 : raw & 0x7F;
        }

        private static void Report(string label, byte[] package)
        {
            int percent = Decode(package);
            byte raw = package[ValueByte];

            Console.WriteLine(percent < 0
                ? $"{label}: byte 5 = 0x{raw:X2} - bit 7 clear, not a percentage."
                : $"{label}: {percent}%  (byte 5 = 0x{raw:X2})");

            // The whole package, so a change ANYWHERE else is visible. Bytes 3 and 4 are
            // unexplained and the write echoes them back, which is only sound while they hold
            // still - so make that checkable rather than assumed.
            Console.WriteLine($"         {MsiAcpi.Hex(package)}");
        }
    }
}
