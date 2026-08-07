using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Dumps the firmware's ACPI tables.
    ///
    /// <para>
    /// The highest-value probe in Phase 0, and completely read-only. The DSDT contains a
    /// <c>_WDG</c> buffer that maps every ACPI-WMI GUID to a method object id, and the
    /// corresponding <c>WMxx</c> method bodies show the sub-function switch the firmware
    /// implements. That turns "guess the magic number" into "read the firmware's own table".
    /// </para>
    ///
    /// <para>
    /// Uses only <c>EnumSystemFirmwareTables</c> / <c>GetSystemFirmwareTable</c> - documented
    /// Win32, no driver, no ring 0.
    /// </para>
    /// </summary>
    internal static class AcpiDump
    {
        /// <summary>'ACPI' as a little-endian FOURCC, which is how the API wants a provider signature.</summary>
        private const uint AcpiProvider = 0x41435049;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint EnumSystemFirmwareTables(uint firmwareTableProviderSignature,
            byte[] firmwareTableBuffer, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature,
            uint firmwareTableId, byte[] firmwareTableBuffer, uint bufferSize);

        public static int Run(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            var directory = args.Length > 0 ? args[0] : "acpi";
            Directory.CreateDirectory(directory);

            uint size = EnumSystemFirmwareTables(AcpiProvider, null, 0);
            if (size == 0)
            {
                Console.Error.WriteLine("Could not enumerate ACPI tables.");
                return 1;
            }

            var ids = new byte[size];
            if (EnumSystemFirmwareTables(AcpiProvider, ids, size) == 0)
            {
                Console.Error.WriteLine("Could not read the ACPI table list.");
                return 1;
            }

            int written = 0;
            var seen = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

            // Each entry is a 4-byte table signature.
            for (int offset = 0; offset + 4 <= ids.Length; offset += 4)
            {
                uint tableId = BitConverter.ToUInt32(ids, offset);
                var signature = Encoding.ASCII.GetString(ids, offset, 4);

                uint tableSize = GetSystemFirmwareTable(AcpiProvider, tableId, null, 0);
                if (tableSize == 0)
                {
                    Console.WriteLine($"  {signature}: could not read (skipped)");
                    continue;
                }

                var table = new byte[tableSize];
                if (GetSystemFirmwareTable(AcpiProvider, tableId, table, tableSize) == 0)
                {
                    Console.WriteLine($"  {signature}: read failed (skipped)");
                    continue;
                }

                // Signatures repeat - a machine typically has many SSDTs - so suffix duplicates
                // rather than silently overwriting the ones that matter.
                seen.TryGetValue(signature, out int count);
                seen[signature] = count + 1;

                var safeName = SanitizeFileName(signature);
                var fileName = count == 0 ? $"{safeName}.aml" : $"{safeName}-{count}.aml";
                var path = Path.Combine(directory, fileName);

                File.WriteAllBytes(path, table);
                Console.WriteLine($"  {signature}: {tableSize,7} bytes -> {fileName}");
                written++;
            }

            Console.WriteLine();
            Console.WriteLine($"Wrote {written} tables to {Path.GetFullPath(directory)}");
            Console.WriteLine();
            Console.WriteLine("Next (works on macOS too - brew install acpica):");
            Console.WriteLine("  iasl -d DSDT.aml");
            Console.WriteLine("  grep -n '_WDG\\|Method (WM\\|Method (WQ\\|Method (WS' DSDT.dsl");
            Console.WriteLine();
            Console.WriteLine("A _WDG entry is a 20-byte record: GUID(16), objectId[2], instanceCount, flags.");
            Console.WriteLine("Object id 'AA' means method WMAA with data block WQAA/WSAA. The GUID matches");
            Console.WriteLine("the 'guid' class qualifier reported by --wmi-classes.");

            return written > 0 ? 0 : 1;
        }

        /// <summary>
        /// ACPI signatures are ASCII but not guaranteed to be filename-safe, and one of the
        /// standard tables is literally "RSD PTR " with a space.
        /// </summary>
        private static string SanitizeFileName(string signature)
        {
            var sb = new StringBuilder(signature.Length);
            foreach (char c in signature)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
