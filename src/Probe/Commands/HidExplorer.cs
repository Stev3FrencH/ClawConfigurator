using System;
using System.Globalization;
using System.Linq;
using HidSharp;
using HidSharp.Reports;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Enumerates HID interfaces and their report descriptors.
    ///
    /// <para>
    /// The Claw exposes several collections. The one that matters for LED control and firmware
    /// mouse mode is the VENDOR-DEFINED collection - usage page 0xFF00 or above - not the gamepad
    /// or keyboard collections. Purely user-mode: no driver, no elevation strictly required,
    /// though report descriptors read more reliably elevated.
    /// </para>
    /// </summary>
    internal static class HidExplorer
    {
        /// <summary>MSI's USB vendor id.</summary>
        private const int DefaultVendorId = 0x0DB0;

        public static int List(string[] args)
        {
            int vendorId = DefaultVendorId;

            if (args.Length > 0)
            {
                var raw = args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[0][2..] : args[0];
                if (!int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId))
                {
                    Console.Error.WriteLine("Usage: --hid-list [vendorIdInHex]   e.g. --hid-list 0DB0");
                    return 64;
                }
            }

            var devices = DeviceList.Local.GetHidDevices(vendorID: vendorId).ToList();

            Console.WriteLine($"HID interfaces for VID 0x{vendorId:X4}: {devices.Count} found");
            Console.WriteLine();

            if (devices.Count == 0)
            {
                Console.WriteLine("None. If this is the Claw, check that the controller is in gamepad mode");
                Console.WriteLine("and try running elevated.");
                return 3;
            }

            foreach (var device in devices)
            {
                Describe(device);
                Console.WriteLine();
            }

            Console.WriteLine("The LED / firmware-mouse endpoint is the VENDOR collection (usage page >= 0xFF00),");
            Console.WriteLine("not the gamepad or keyboard ones. Record its DevicePath and feature-report length");
            Console.WriteLine("in docs/hardware-notes.md.");
            Console.WriteLine();
            Console.WriteLine("Expect TWO vendor collections, not one:");
            Console.WriteLine("  PID 0x1901 (XInput)      usage page 0xFFA0 / usage 0x0001");
            Console.WriteLine("  PID 0x1902 (DirectInput) usage page 0xFFF0 / usage 0x0040");
            Console.WriteLine("Do not stop at the first match - report 0x0F (LED / mode switch, 64 bytes) is");
            Console.WriteLine("documented on one specific interface, and picking the other one silently fails.");

            return 0;
        }

        private static void Describe(HidDevice device)
        {
            Console.WriteLine($"PID 0x{device.ProductID:X4}");
            Console.WriteLine($"  path            : {device.DevicePath}");

            // Every one of these can throw for an interface the OS will not let us open.
            Console.WriteLine($"  product         : {TryGet(() => device.GetProductName())}");
            Console.WriteLine($"  manufacturer    : {TryGet(() => device.GetManufacturer())}");
            Console.WriteLine($"  serial          : {TryGet(() => device.GetSerialNumber())}");
            Console.WriteLine($"  release         : {TryGet(() => device.ReleaseNumberBcd.ToString())}");
            Console.WriteLine($"  max input rpt   : {TryGet(() => device.GetMaxInputReportLength().ToString())}");
            Console.WriteLine($"  max output rpt  : {TryGet(() => device.GetMaxOutputReportLength().ToString())}");
            Console.WriteLine($"  max feature rpt : {TryGet(() => device.GetMaxFeatureReportLength().ToString())}");

            try
            {
                var descriptor = device.GetReportDescriptor();

                foreach (var item in descriptor.DeviceItems)
                {
                    var usages = item.Usages.GetAllValues().ToList();
                    if (usages.Count == 0) continue;

                    uint usage = usages[0];
                    uint usagePage = usage >> 16;
                    uint usageId = usage & 0xFFFF;

                    var marker = usagePage >= 0xFF00 ? "   <-- VENDOR COLLECTION" : "";
                    Console.WriteLine($"  collection      : usagePage=0x{usagePage:X4} usage=0x{usageId:X4}{marker}");
                }

                Console.WriteLine($"  report ids      : {DescribeReportIds(descriptor)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  collections     : unavailable ({ex.GetType().Name}: {ex.Message})");
            }

            try
            {
                // The raw bytes matter: decoding them by hand is how the vendor collection's
                // feature-report layout gets established, and they belong verbatim in the notes.
                var raw = device.GetRawReportDescriptor();
                Console.WriteLine($"  descriptor      : {BitConverter.ToString(raw).Replace("-", " ")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  descriptor      : unavailable ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static string DescribeReportIds(ReportDescriptor descriptor)
        {
            try
            {
                var ids = descriptor.Reports
                    .Select(r => $"{r.ReportType}:{r.ReportID}(len {r.Length})")
                    .ToList();
                return ids.Count > 0 ? string.Join(", ", ids) : "(none)";
            }
            catch (Exception ex)
            {
                return $"(unavailable: {ex.GetType().Name})";
            }
        }

        private static string TryGet(Func<string> read)
        {
            try { return read() ?? "(null)"; }
            catch (Exception ex) { return $"(unavailable: {ex.GetType().Name})"; }
        }
    }
}
