using System;
using McenterLite.Hardware.Windows;

namespace McenterLite.Probe.Commands
{
    internal static class DeviceInfo
    {
        public static int Run()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            var identity = DeviceDetection.Detect();

            Console.WriteLine("Device identity");
            Console.WriteLine($"  Vendor     : {identity.Vendor}");
            Console.WriteLine($"  Model      : {identity.Model}");
            Console.WriteLine($"  Base board : {identity.BaseBoard}");
            Console.WriteLine();
            Console.WriteLine($"  Any Claw   : {identity.IsAnyClaw}");
            Console.WriteLine($"  Claw 8 EX  : {identity.IsClaw8Ex}");
            Console.WriteLine();
            Console.WriteLine($"  MSI Center M running : {DeviceDetection.IsMsiCenterRunning()}");
            Console.WriteLine();

            if (identity.IsClaw8Ex)
            {
                Console.WriteLine("Supported. Every calibrated value in this project applies to this machine.");
                return 0;
            }

            if (identity.IsAnyClaw)
            {
                // The EC table layout, duty floor and power ceilings differ between Claw
                // generations. Applying 8 EX values elsewhere would write a wrong fan table to a
                // real embedded controller.
                Console.WriteLine("A Claw, but NOT the 8 EX. Hardware features stay disabled: the EC layout,");
                Console.WriteLine("duty floor and power limits in this project are calibrated to the 8 EX only.");
                return 3;
            }

            Console.WriteLine("Not an MSI Claw. Hardware features are disabled.");
            return 3;
        }
    }
}
