using System;
using System.Globalization;
using System.Management;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads and writes <c>Get_AP</c> / <c>Set_AP</c> sub-function 0, <b>byte 3</b> — the candidate
    /// gate for whether the firmware honours the manual power limits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on device 2026-08-13</b> by reinstalling MSI Center M and sweeping every
    /// <c>Get_*</c> register across its three performance modes. The low nibble of byte 3 tracks the
    /// mode selector exactly, and it is the <b>only</b> non-telemetry byte that does:
    /// </para>
    /// <code>
    /// byte:  0  1  2  3  4  5   6..31
    ///       01 00 00 C6 80 XX   00 …
    ///                 ^^ high nibble C throughout; the low nibble is the mode
    ///
    ///   6  User Scenario   manual PL1/PL2 in Get_SlaveBattery|1 are HONOURED
    ///   2  Endurance       MSI drives power
    ///   1  AI Engine       MSI drives power
    /// </code>
    /// <para>
    /// <b>This is the gate that broke TDP.</b> After the first full power cycle following MSI
    /// Center M's uninstall, byte 3 reads <c>0xC1</c> — identical to AI Engine. The widget writes
    /// 15/17 W, <c>Get_SlaveBattery|1</c> reads back 15/17, and the package still draws 37 W bursts
    /// settling to 25 W. Those are not "the limit being ignored": under AI Engine the same register
    /// carries <c>19 25</c> = 25/37, so the firmware is obeying its own automatic pair instead.
    /// </para>
    /// <para>
    /// MSI Center M sets this from its <b>service</b>, not its UI — a snapshot taken after
    /// reinstalling without ever opening the window already read <c>0xC6</c>. That is why this
    /// project never had to set it, and why nothing noticed the dependency until the EC was finally
    /// power-cycled without MSI Center M present to restore it.
    /// </para>
    /// <para>
    /// The earlier note in <c>docs/hardware-notes.md</c> calling bytes 3 and 4 "presumed to identify
    /// the register" and constant across captures was wrong. They were constant only because nothing
    /// had power-cycled the EC yet.
    /// </para>
    /// <para>
    /// <b>Only the low nibble is written</b>, preserving whatever the high nibble holds — it has
    /// read <c>C</c> in every observation, but echoing beats asserting. Read-modify-write, confirmed
    /// by a separate read, like every other write on this class.
    /// </para>
    /// </remarks>
    internal static class PerfGate
    {
        private const string ReadMethod = "Get_AP";
        private const string WriteMethod = "Set_AP";

        private const byte SubFunction = 0x00;
        private const int GateByte = 3;

        /// <summary>The mode lives in the low nibble; the high nibble is not ours.</summary>
        private const byte ModeMask = 0x0F;

        private const byte ModeUserScenario = 0x6;
        private const byte ModeEndurance = 0x2;
        private const byte ModeAiEngine = 0x1;

        private static string DescribeMode(int nibble) => nibble switch
        {
            ModeUserScenario => "User Scenario - manual power limits are honoured",
            ModeEndurance => "Endurance - MSI drives power, manual limits ignored",
            ModeAiEngine => "AI Engine - MSI drives power, manual limits ignored",
            _ => "unrecognised",
        };

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

                Report("Current", package);
            }

            return 0;
        }

        public static int Set(string[] args)
        {
            byte mode = ModeUserScenario;

            if (args.Length >= 1)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "manual":
                    case "user-scenario": mode = ModeUserScenario; break;
                    case "endurance": mode = ModeEndurance; break;
                    case "ai":
                    case "ai-engine": mode = ModeAiEngine; break;

                    default:
                        Console.Error.WriteLine(
                            "Usage: --set-perf-gate [manual|endurance|ai]   (default manual)");
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("  manual     low nibble 6 - the only mode that honours PL1/PL2");
                        Console.Error.WriteLine("  endurance  low nibble 2");
                        Console.Error.WriteLine("  ai         low nibble 1");
                        return 64;
                }
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

                // Only the low nibble is ours. The high nibble has read C in every observation,
                // but echoing what the firmware just reported beats asserting a value.
                byte target = (byte)((before[GateByte] & ~ModeMask) | mode);

                if (before[GateByte] == target)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"Byte {GateByte} is already 0x{target:X2} ({DescribeMode(mode)}). Nothing to do.");
                    return 0;
                }

                // Echo everything the firmware just returned, changing only the sub-function and
                // byte 3. Byte 5 carries the live charge limit and MUST come back untouched.
                var payload = (byte[])before.Clone();
                payload[0] = SubFunction;
                payload[GateByte] = target;

                Console.WriteLine($"Sending {WriteMethod}: {MsiAcpi.Hex(payload)}");

                var result = MsiAcpi.Invoke(instance, WriteMethod, payload, out error);
                if (result == null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                using (result) MsiAcpi.DumpResult(result);

                // Separate read. Set_AP replies with a bare status that echoes nothing.
                if (!TryReadPackage(instance, out var after, out error))
                {
                    Console.Error.WriteLine($"Wrote, but could not read back: {error}");
                    return 1;
                }

                Report("After", after);

                if (after[GateByte] != target)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        $"FAILED: asked for 0x{target:X2}, byte {GateByte} reads 0x{after[GateByte]:X2}.");
                    Console.Error.WriteLine(
                        "The byte may be read-only, or owned by something the EC re-asserts.");
                    return 1;
                }

                Console.WriteLine();
                Console.WriteLine("Byte 3 written and confirmed.");
                Console.WriteLine();
                Console.WriteLine("THAT PROVES NOTHING ON ITS OWN - it proves the byte is stored, which is");
                Console.WriteLine("exactly what the power limits already do while being ignored. Now set a low");
                Console.WriteLine("limit in the widget, load the CPU, and watch the package draw:");
                Console.WriteLine();
                Console.WriteLine("    --acpi-get Get_AP 0x02      byte 5 is live package watts");
                Console.WriteLine();
                Console.WriteLine("If it settles near the limit instead of ~25 W, the gate is real.");
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

            if (package == null || package.Length <= GateByte)
            {
                error = $"{ReadMethod} returned no usable package.";
                return false;
            }

            return true;
        }

        private static void Report(string label, byte[] package)
        {
            int nibble = package[GateByte] & ModeMask;

            Console.WriteLine($"{label,-8} {MsiAcpi.Hex(package)}");
            Console.WriteLine(
                $"         byte {GateByte} = 0x{package[GateByte]:X2}  ->  {DescribeMode(nibble)}");
        }
    }
}
