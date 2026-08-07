using System;
using McenterLite.Hardware.Windows;
using McenterLite.Shared.Ipc;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// CPU boost and power-mode overlay. These are the two features that need no MSI hardware at
    /// all, so this command is the independent oracle for them - the one part of the app that can
    /// be fully verified in a VM before the handheld is involved.
    /// </summary>
    internal static class PowerInfo
    {
        public static int Run()
        {
            var provider = new WindowsPowerProvider();

            if (!provider.Available)
            {
                Console.Error.WriteLine(provider.UnavailableReason);
                return 1;
            }

            Console.WriteLine("Windows power settings");

            Console.WriteLine(provider.TryReadCpuBoost(out bool boost)
                ? $"  CPU boost  : {(boost ? "on" : "off")}"
                : "  CPU boost  : could not read");

            Console.WriteLine(provider.TryReadPowerMode(out var mode)
                ? $"  Power mode : {mode}"
                : "  Power mode : could not read");

            Console.WriteLine();
            Console.WriteLine("Cross-check with:");
            Console.WriteLine("  powercfg /q SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE");
            Console.WriteLine("  the Windows battery flyout slider");

            return 0;
        }

        public static int SetMode(string[] args)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int raw) ||
                !Enum.IsDefined(typeof(OsPowerMode), raw))
            {
                Console.Error.WriteLine("Usage: --set-power-mode <0|1|2>   (0 efficiency, 1 balanced, 2 performance)");
                return 64;
            }

            var provider = new WindowsPowerProvider();
            var result = provider.ApplyPowerMode((OsPowerMode)raw);

            if (!result.Ok)
            {
                Console.Error.WriteLine($"Failed: {result.Error}");
                return 1;
            }

            provider.TryReadPowerMode(out var actual);
            Console.WriteLine($"Power mode is now {actual}.");
            return 0;
        }

        public static int SetBoost(string[] args)
        {
            if (args.Length < 1 || (args[0] != "on" && args[0] != "off"))
            {
                Console.Error.WriteLine("Usage: --set-cpu-boost <on|off>");
                return 64;
            }

            var provider = new WindowsPowerProvider();
            var result = provider.ApplyCpuBoost(args[0] == "on");

            if (!result.Ok)
            {
                Console.Error.WriteLine($"Failed: {result.Error}");
                return 1;
            }

            provider.TryReadCpuBoost(out bool actual);
            Console.WriteLine($"CPU boost is now {(actual ? "on" : "off")}.");
            return 0;
        }
    }
}
