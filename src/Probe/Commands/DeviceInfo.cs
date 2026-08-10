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
                // The power ceilings differ between Claw generations. Applying 8 EX values
                // elsewhere would write wrong power limits to real firmware.
                Console.WriteLine("A Claw, but NOT the 8 EX. Hardware features stay disabled: the power limits");
                Console.WriteLine("in this project are calibrated to the 8 EX only.");
                return 3;
            }

            Console.WriteLine("Not an MSI Claw. Hardware features are disabled.");
            return 3;
        }
    }
}
