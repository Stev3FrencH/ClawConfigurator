using System;
using System.Globalization;
using System.Linq;
using McenterLite.Shared.Model;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// The battery charge limit, over MSI's ACPI-WMI interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Writing MSI Center's <c>BatteryLevel</c> registry value round-trips
    /// and MSI Center's own UI reflects it, but charging behaviour does not change - measured on
    /// device 2026-08-08 by setting the same value both ways and watching the battery. Only the
    /// change made through MSI Center's UI took effect. So the registry is a display mirror and
    /// something else is the apply path. See Gate G3 in <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// <b>The encoding is a hypothesis until this command confirms it.</b> The threshold byte is
    /// believed to be <c>percent | 0x80</c>, with bit 7 an enable/commit flag and bits 0-6 the
    /// percentage. That comes from the msi-ec Linux driver, which documents MSI firmware's EC
    /// layout - a property of the embedded controller, not of any operating system. It has NOT
    /// been measured on the Claw, which is a handheld rather than one of the laptops that driver
    /// covers. Read before writing, and check the read against MSI Center's own setting.
    /// </para>
    /// <para>
    /// <b>Only <c>Set_MasterBattery</c> is ever called.</b> <c>MSI_ACPI</c> also exposes
    /// <c>Set_EC</c>, which would write a raw byte to an arbitrary controller address; a wrong
    /// address there reaches fan or thermal registers on real firmware. The purpose-built method
    /// lets the firmware validate the value instead. If it turns out not to work, that is a reason
    /// to reassess, not to escalate to raw EC writes.
    /// </para>
    /// </remarks>
    internal static class BatteryInfo
    {
        private const string ReadMethod = "Get_MasterBattery";
        private const string WriteMethod = "Set_MasterBattery";

        /// <summary>Bit 7 marks the threshold as active. Bits 0-6 carry the percentage.</summary>
        private const byte EnableBit = 0x80;

        public static int Read()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (instance)
            {
                Console.WriteLine($"{MsiAcpi.ClassName}.{ReadMethod}");

                var result = MsiAcpi.Invoke(instance, ReadMethod, Array.Empty<byte>(), out error);
                if (result == null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                MsiAcpi.DumpResult(result);
            }

            Console.WriteLine();
            Console.WriteLine("Expected if the percent|0x80 encoding holds:");
            foreach (int level in ChargeLevels.All())
                Console.WriteLine($"  {level,3}% -> 0x{level | EnableBit:X2} ({level | EnableBit})");

            Console.WriteLine();
            Console.WriteLine("Cross-check: set the limit in MSI Center's own UI to each of 100/80/60");
            Console.WriteLine("and re-run. Whichever byte tracks those three is the threshold.");

            return 0;
        }

        public static int SetLimit(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            var levels = ChargeLevels.All();

            if (args.Length < 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
            {
                Console.Error.WriteLine(
                    $"Usage: --set-charge-limit <{string.Join("|", levels)}>");
                return 64;
            }

            // Snapped rather than clamped, and rejected rather than silently corrected: the device
            // holds one of three levels, and quietly charging to a different one than was asked for
            // is exactly the failure this whole exercise is chasing.
            if (!levels.Contains(requested))
            {
                Console.Error.WriteLine(
                    $"{requested}% is not one of the levels this device represents "
                    + $"({string.Join(", ", levels)}). Nearest is {ChargeLevels.Snap(requested)}%.");
                return 64;
            }

            byte payload = (byte)(requested | EnableBit);

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (instance)
            {
                Console.WriteLine($"Before ({ReadMethod}):");
                var before = MsiAcpi.Invoke(instance, ReadMethod, Array.Empty<byte>(), out error);
                if (before == null) Console.WriteLine($"  (could not read: {error})");
                else MsiAcpi.DumpResult(before);

                Console.WriteLine();
                Console.WriteLine($"Writing {requested}% as 0x{payload:X2} ({payload}) "
                    + $"via {MsiAcpi.ClassName}.{WriteMethod}");

                var result = MsiAcpi.Invoke(instance, WriteMethod, new[] { payload }, out error);
                if (result == null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                MsiAcpi.DumpResult(result);

                Console.WriteLine();
                Console.WriteLine($"After ({ReadMethod}):");
                var after = MsiAcpi.Invoke(instance, ReadMethod, Array.Empty<byte>(), out error);
                if (after == null) Console.WriteLine($"  (could not read: {error})");
                else MsiAcpi.DumpResult(after);
            }

            Console.WriteLine();
            Console.WriteLine("A changed read-back only proves the value landed. What matters is whether");
            Console.WriteLine("charging actually stops - plug in and run:");
            Console.WriteLine($"  .\\Watch-Battery.ps1 -Limit {requested}");

            return 0;
        }
    }
}
