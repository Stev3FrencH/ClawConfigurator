using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using HidSharp;
using HidSharp.Reports;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Watches MSI's HID interfaces for controller-mode activity: both the set of enumerated
    /// interfaces and every input report arriving on the vendor collection.
    ///
    /// <para>
    /// Two things are being answered at once, and they need to be watched together because a mode
    /// switch may well do both:
    /// </para>
    /// <list type="number">
    /// <item>
    /// Does the interface set CHANGE on a mode switch? The controller enumerates as PID 0x1901 in
    /// XInput mode; the reference notes list 0x1902 for DirectInput. If desktop-mouse mode also
    /// re-enumerates, then the current mode is readable from the device list alone - no vendor
    /// report framing to decode, and nothing that can go stale.
    /// </item>
    /// <item>
    /// Does the firmware ANNOUNCE the change on input report 0x10? That is the read-back path
    /// gate G5 is missing, and it is the only route that could follow the physical MSI button
    /// without polling.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// READ-ONLY. Opens the vendor collection for reading and never writes to it.
    /// </para>
    /// </summary>
    internal static class HidWatcher
    {
        private const int DefaultVendorId = 0x0DB0;

        /// <summary>Vendor-defined usage pages start here. BOTH 0xFFA0 and 0xFFF0 matter.</summary>
        private const int VendorUsagePageFloor = 0xFF00;

        private const int PollMs = 400;

        public static int Run(string[] args)
        {
            int seconds = 120;
            int vendorId = DefaultVendorId;

            if (args.Length > 0 && !int.TryParse(args[0], NumberStyles.Integer,
                                                 CultureInfo.InvariantCulture, out seconds))
            {
                Console.Error.WriteLine("Usage: --hid-watch [seconds] [vendorIdInHex]");
                return 64;
            }

            if (args.Length > 1)
            {
                var raw = args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[1][2..] : args[1];
                if (!int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId))
                {
                    Console.Error.WriteLine("Usage: --hid-watch [seconds] [vendorIdInHex]");
                    return 64;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Watching HID interfaces for VID 0x{vendorId:X4} for {seconds}s.");
            Console.WriteLine();
            Console.WriteLine("  PRESS THE PHYSICAL MSI BUTTON to switch controller mode.");
            Console.WriteLine("  Switch it from MSI Center too, as a known-good control.");
            Console.WriteLine();
            Console.WriteLine("Interface arrivals and departures print as + and -. Input reports on the");
            Console.WriteLine("vendor collection print as 'rpt'. Ctrl+C to stop early.");
            Console.WriteLine();

            var readers = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool first = true;

            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var devices = DeviceList.Local.GetHidDevices(vendorID: vendorId).ToList();

                    var current = new Dictionary<string, HidDevice>(StringComparer.OrdinalIgnoreCase);
                    foreach (var device in devices) current[device.DevicePath] = device;

                    foreach (var gone in known.Keys.Except(current.Keys).ToList())
                    {
                        Stamp('-', known[gone]);
                        if (readers.Remove(gone, out var reader)) reader.Stop();
                        known.Remove(gone);
                    }

                    foreach (var path in current.Keys.Except(known.Keys).ToList())
                    {
                        var device = current[path];
                        bool vendor = IsVendorCollection(device);
                        var summary = Summarise(device, vendor);

                        // The first sweep is the baseline, not an event.
                        Stamp(first ? '=' : '+', summary);
                        known[path] = summary;

                        if (vendor) StartReader(readers, device, summary);
                    }

                    first = false;
                    Thread.Sleep(PollMs);
                }
            }
            finally
            {
                foreach (var reader in readers.Values) reader.Stop();
            }

            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine();
            Console.WriteLine("If the interface set changed on a mode switch, the current mode is readable by");
            Console.WriteLine("enumeration alone. If input reports arrived, decode byte 0 onwards - that frame");
            Console.WriteLine("is the read-back path, and it is what lets the helper follow the physical button.");
            Console.WriteLine();

            return 0;
        }

        private static void StartReader(Dictionary<string, Reader> readers, HidDevice device, string summary)
        {
            if (!device.TryOpen(out var stream))
            {
                Stamp('!', $"{summary} - could NOT open for reading (in use, or needs elevation)");
                return;
            }

            readers[device.DevicePath] = new Reader(stream, device);
            Stamp('>', $"{summary} - reading input reports");
        }

        private static bool IsVendorCollection(HidDevice device)
        {
            try
            {
                var descriptor = device.GetReportDescriptor();
                foreach (var item in descriptor.DeviceItems)
                {
                    foreach (var usage in item.Usages.GetAllValues())
                    {
                        if ((usage >> 16) >= VendorUsagePageFloor) return true;
                    }
                }
            }
            catch (Exception)
            {
                // An interface whose descriptor cannot be read is not one we can classify. Treat
                // it as non-vendor rather than guessing - opening the wrong handle can take an
                // exclusive lock on a collection something else needs.
            }

            return false;
        }

        private static string Summarise(HidDevice device, bool vendor)
        {
            string usages = "?";
            try
            {
                var descriptor = device.GetReportDescriptor();
                usages = string.Join(" ", descriptor.DeviceItems
                    .SelectMany(i => i.Usages.GetAllValues())
                    .Select(u => $"{u >> 16:X4}/{u & 0xFFFF:X4}"));

                var reports = string.Join(",", descriptor.Reports
                    .Select(r => $"{r.ReportType.ToString()[0]}{r.ReportID:X2}"));

                if (reports.Length > 0) usages += $"  [{reports}]";
            }
            catch (Exception)
            {
                // Same reasoning as IsVendorCollection: report what we have.
            }

            var tag = vendor ? "VENDOR " : "";
            return $"PID 0x{device.ProductID:X4}  {tag}{usages}  {Shorten(device.DevicePath)}";
        }

        // Full paths are long enough to wrap on the Claw's screen, which makes an arrival/departure
        // pair hard to read side by side. The instance segment is what actually distinguishes them.
        private static string Shorten(string path)
        {
            var start = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return path;

            var end = path.IndexOf('{', start);
            return end < 0 ? path[start..] : path[start..end].TrimEnd('#');
        }

        private static void Stamp(char marker, string text) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {marker} {text}");

        /// <summary>
        /// One blocking read loop per vendor collection, on its own thread.
        /// </summary>
        /// <remarks>
        /// A mode switch is expected to tear the device down, so the read will fail rather than
        /// end cleanly. That is a normal outcome here, not an error worth shouting about - the
        /// enumeration poll reports the departure a moment later.
        /// </remarks>
        private sealed class Reader
        {
            private readonly HidStream _stream;
            private readonly Thread _thread;
            private volatile bool _stop;

            public Reader(HidStream stream, HidDevice device)
            {
                _stream = stream;
                _stream.ReadTimeout = PollMs;

                int length;
                try { length = device.GetMaxInputReportLength(); }
                catch (Exception) { length = 64; }

                _thread = new Thread(() => Loop(Math.Max(length, 1))) { IsBackground = true };
                _thread.Start();
            }

            public void Stop()
            {
                _stop = true;
                try { _stream.Close(); } catch (Exception) { }
                _thread.Join(TimeSpan.FromSeconds(1));
            }

            private void Loop(int length)
            {
                var buffer = new byte[length];

                while (!_stop)
                {
                    int count;
                    try
                    {
                        count = _stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (!_stop) Stamp('!', $"read ended: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    if (count <= 0) continue;
                    Stamp('r', $"rpt {Format(buffer, count)}");
                }
            }

            // Trailing zeros in a 64-byte report are padding and drown the payload. The full length
            // is still printed, so a report that is genuinely all-zero past the payload is not
            // confused with a short one.
            private static string Format(byte[] buffer, int count)
            {
                int end = count;
                while (end > 1 && buffer[end - 1] == 0) end--;

                var hex = BitConverter.ToString(buffer, 0, end).Replace("-", " ");
                return end == count ? $"({count}) {hex}" : $"({count}) {hex} +{count - end} zero";
            }
        }
    }
}
