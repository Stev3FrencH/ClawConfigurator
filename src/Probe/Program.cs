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
                case "charge-limit": return Commands.ChargeLimit.Read(rest);
                case "set-charge-limit": return Commands.ChargeLimit.Set(rest);
                case "dump-acpi": return Commands.AcpiDump.Run(rest);
                case "hid-list": return Commands.HidExplorer.List(rest);
                case "hid-watch": return Commands.HidWatcher.Run(rest);
                case "hid-listen": return Commands.HidRaw.Listen(rest);
                case "lighting": return Commands.Lighting.Read(rest);
                case "set-lighting": return Commands.Lighting.Set(rest);
                case "set-hid-raw": return Commands.HidRaw.Send(rest);
                case "controller-mode": return Commands.ControllerMode.Read(rest);
                case "set-controller-mode": return Commands.ControllerMode.Set(rest);
                case "fan": return Commands.Fan.Read(rest);
                case "set-fan": return Commands.Fan.Set(rest);
                case "set-fan-control": return Commands.Fan.SetControl(rest);
                case "watch-events": return Commands.EventWatcher.Run(rest);
                case "perf-gate": return Commands.PerfGate.Read(rest);
                case "set-perf-gate": return Commands.PerfGate.Set(rest);

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
  --hid-listen [secs]       Listen on the vendor command channel only, with no enumeration
                            polling. Change lighting in MSI Center M during a run to see
                            what, if anything, the device reports back.
  --watch-events [secs]     Subscribe to MSI_Event and print every firmware notification, with
                            its MSIEvt code. This is how a hardware button most likely reports
                            itself - MSI Center M was probably just the subscriber, which would
                            mean the buttons that stopped working are simply unlistened-to.
                            Read-only in the strongest sense: subscribing cannot write.

  --charge-limit            Read the battery charge limit (MSI_ACPI.Get_AP, byte 5) and dump
                            the whole package, so a change anywhere else is visible.
  --perf-gate               Read the performance mode: MSI_ACPI.Get_AP sub-function 0, byte 3,
                            low nibble. THIS GATES THE POWER LIMITS. Measured 2026-08-13 across
                            MSI Center M's own selector:
                              6 = User Scenario  manual PL1/PL2 are honoured
                              2 = Endurance      MSI drives power
                              1 = AI Engine      MSI drives power
                            After a full power cycle with MSI Center M uninstalled this reads 1,
                            and the limits written by --set-power-mode do nothing - the firmware
                            runs its own 25/37 W pair instead.
  --fan                     Read both fans' duty tables, the fixed temperature breakpoints they
                            are indexed against, and the live tachometers. Two fans, one table
                            each: an idle duty, six curve points, and a ceiling the EC owns.
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
  --set-hid-raw <opcode> [args...] [listenSeconds]
                            Send ONE arbitrary opcode on the vendor HID channel and print
                            whatever comes back. Hex, with or without 0x.
                            This is the bluntest command here - the opcode map is not known,
                            so it is a write to an undocumented controller. Used to find the
                            lighting opcodes for G4.  e.g.  --set-hid-raw 04
  --set-fan <1|2|both> <idle;d1;d2;d3;d4;d5;d6>
  --set-fan <1|2|both> auto
                            Write one fan duty table, or restore the factory one. Duties are
                            percentages, 0-100, read back and confirmed by a separate read.
                            THE FIRMWARE ENFORCES NO FLOOR - a table of zeros stops the fan at
                            every temperature, measured on this device. Warns, does not refuse.
                            QUOTE the duties in PowerShell - ';' separates statements there, so
                            an unquoted list runs as seven commands and never reaches this tool.
                            e.g.  --set-fan both ""30;35;45;55;65;75;85""
  --set-fan-control <auto|custom>
                            Choose WHO drives the fans: the firmware's own curve, or the duty
                            tables above. MSI_ACPI.Set_AP sub-function 1, byte 1 bit 0x80.
                            The tables do nothing at all while this reads auto - they store,
                            they read back, and the fans ignore them.
                            e.g.  --set-fan-control custom
  --set-perf-gate <manual|endurance|ai>
                            Set the performance mode that gates the power limits; see --perf-gate.
                            Defaults to 'manual' (User Scenario), the only mode in which PL1/PL2
                            are honoured. Writes ONLY the low nibble of byte 3, echoing the rest
                            of the package, and confirms with a separate read.
  --set-charge-limit <20-100>
                            Set the battery charge limit via MSI_ACPI.Set_AP. Echoes back the
                            package it just read with only byte 5 changed, and confirms with a
                            separate read - Set_* on this class does not echo what was written.

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
