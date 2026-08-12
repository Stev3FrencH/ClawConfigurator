using System;
using System.Linq;

namespace McenterLite.Probe
{
    /// <summary>
    /// Phase-0 hardware discovery tool, and afterwards the regression harness for everything
    /// recorded in <c>docs/hardware-notes.md</c>.
    ///
    /// <para>
    /// Every command here is READ-ONLY unless its name begins with <c>set-</c>. That split is
    /// deliberate: discovery on an undocumented embedded controller should never be one typo away
    /// from a write.
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return 0;
            }

            try
            {
                return Dispatch(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static int Dispatch(string[] args)
        {
            var command = args[0].TrimStart('-').ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (command)
            {
                case "device": return Commands.DeviceInfo.Run();
                case "power": return Commands.PowerInfo.Run();
                case "set-power-mode": return Commands.PowerInfo.SetMode(rest);
                case "set-cpu-boost": return Commands.PowerInfo.SetBoost(rest);
                case "wmi-classes": return Commands.WmiExplorer.ListClasses(rest);
                case "wmi-instances": return Commands.WmiExplorer.DumpInstances(rest);
                case "wmi-method": return Commands.WmiExplorer.DescribeMethod(rest);
                case "acpi-get": return Commands.WmiExplorer.AcpiGet(rest);
                case "dump-acpi": return Commands.AcpiDump.Run(rest);
                case "hid-list": return Commands.HidExplorer.List(rest);
                case "hid-watch": return Commands.HidWatcher.Run(rest);
                case "controller-mode": return Commands.ControllerMode.Read(rest);
                case "set-controller-mode": return Commands.ControllerMode.Set(rest);

                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    PrintUsage();
                    return 64;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"
McenterLite.Probe - hardware discovery for the MSI Claw 8 EX AI+

  Run elevated. Read commands are safe; only 'set-*' changes anything.

READ
  --device                  Identify the machine and report whether it is a supported Claw.
  --power                   Read CPU boost mode and the Windows power-mode overlay.
  --wmi-classes [filter]    List root\WMI classes with their ACPI GUID qualifier and methods.
                            The GUID is what links a class to a _WDG entry in the ACPI tables.
  --wmi-instances <class>   Dump every property of every instance of one class.
  --wmi-method <class> <method>
                            Dump one method's declared in/out parameter names and types,
                            WITHOUT calling it. Use this before writing any code that calls a
                            Set_* method - the argument shape has to be read, not guessed.
  --acpi-get <method> [bytes...]
                            Call a READ-ONLY MSI_ACPI method and dump its output buffer as
                            indexed hex. Refuses anything not named Get_*, so raw Set_EC
                            writes are not reachable from here.
                            e.g.  --acpi-get Get_MasterBattery
                                  --acpi-get Get_EC 0xD7
  --dump-acpi <dir>         Write DSDT and all SSDT tables as .aml for offline disassembly.
                            Then, on any machine:  iasl -d DSDT.aml
  --hid-list [vid]          Enumerate HID interfaces (default VID 0x0DB0) with report
                            descriptors. The LED and firmware-mouse endpoint is the vendor
                            collection, usage page >= 0xFF00.
  --hid-watch [secs] [vid]  Watch those interfaces live: arrivals, departures, and every
                            input report on the vendor collection. Press the physical MSI
                            button during a run to see what a mode switch actually does.

  --controller-mode         Ask the controller whether it is in gamepad or desktop-mouse mode,
                            over the vendor HID channel. Needs no MSI Center. This one does
                            put bytes on the wire - the channel has no feature report, so a
                            query is the only way to ask - but it changes nothing.

WRITE (these change system state)
  --set-power-mode <0|1|2>  0 = best efficiency, 1 = balanced, 2 = best performance.
  --set-cpu-boost <on|off>
  --set-controller-mode <desktop|xinput|dinput>
                            Switch controller mode over the vendor HID channel. The physical
                            MSI button changes this too, so we do not own the state.

SUGGESTED PHASE-0 ORDER
  1. --device                     confirm the model gate matches
  2. --dump-acpi C:\acpi          then disassemble and search for _WDG and 'Method (WM'
  3. --wmi-classes MSI            bind those GUIDs to callable class names
  4. --hid-list                   find the vendor collection
  5. --power                      verify the two features that need no MSI hardware at all
");
        }
    }
}
