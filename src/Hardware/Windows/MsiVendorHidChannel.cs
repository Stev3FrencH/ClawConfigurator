using System;
using System.Linq;
using System.Runtime.Versioning;
using HidSharp;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// MSI's vendor HID command channel on the Claw's controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interface <c>VID_0DB0&amp;PID_1901&amp;MI_01</c>, usage page <c>0xFFA0</c>. Two 64-byte
    /// reports and no feature report, so a read is a send followed by a listen:
    /// </para>
    /// <code>
    /// byte 0   report id   0x0F outbound, 0x10 inbound
    /// byte 1   0x00
    /// byte 2   0x00
    /// byte 3   0x3C        60 - payload length, the 63 report bytes less this header
    /// byte 4   opcode
    /// byte 5+  arguments, zero padded to 64
    /// </code>
    /// <para>
    /// Established by observation on 2026-08-12 rather than from the reference notes, which had
    /// only a single opcode and no framing. See gate G5 in <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// <b>Needs no elevation and no driver</b>, and the collection opens <i>shared</i> - verified
    /// working alongside a running MSI Center M holding the same handle. This is deliberately the
    /// whole surface: controller mode uses it now, and report <c>0x0F</c> is also the LED path, so
    /// G4 should extend this rather than open its own handle.
    /// </para>
    /// <para>
    /// Opened per operation rather than held. The controller is a USB device that can be asleep or
    /// re-enumerating, and a cached handle would outlive that; a fresh open also gives a clean
    /// input queue, so a stale unsolicited notification cannot be mistaken for a reply.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class MsiVendorHidChannel : IDisposable
    {
        public const byte OutputReportId = 0x0F;
        public const byte InputReportId = 0x10;
        public const byte PayloadLength = 0x3C;

        private const int VendorId = 0x0DB0;
        private const int VendorUsagePageFloor = 0xFF00;

        private const int ReadTimeoutMs = 250;

        /// <summary>
        /// The interface that answered last time, to skip re-identifying it on every open.
        /// </summary>
        /// <remarks>
        /// A plain field with no lock. Worst case two threads race and one overwrites the other
        /// with the same value, or a stale path misses and costs one full scan - so the cheap
        /// version is correct and the careful version would only be slower.
        /// </remarks>
        private static string _lastKnownPath;

        private readonly HidStream _stream;
        private readonly int _inputLength;
        private readonly int _outputLength;

        private MsiVendorHidChannel(HidStream stream, int inputLength, int outputLength)
        {
            _stream = stream;
            _inputLength = inputLength;
            _outputLength = outputLength;
            _stream.ReadTimeout = ReadTimeoutMs;
        }

        /// <summary>
        /// Opens the command channel, or returns null with a reason.
        /// </summary>
        public static MsiVendorHidChannel Open(out string error)
        {
            if (!OperatingSystem.IsWindows())
            {
                error = "The MSI vendor HID channel is only available on Windows.";
                return null;
            }

            HidDevice[] devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices(vendorID: VendorId).ToArray();
            }
            catch (Exception ex)
            {
                error = $"Could not enumerate HID devices: {ex.Message}";
                return null;
            }

            if (devices.Length == 0)
            {
                error = $"No HID interfaces for VID 0x{VendorId:X4}. The controller is not present.";
                return null;
            }

            // Fast path. Identifying the channel means parsing the report descriptor of every
            // interface, and the helper opens this once per telemetry tick - so remember which one
            // answered last time. Purely an optimisation: a stale path simply misses and falls
            // through to the scan below, which is what happens after a re-enumeration.
            var remembered = _lastKnownPath;
            if (remembered != null)
            {
                foreach (var device in devices)
                {
                    if (!string.Equals(device.DevicePath, remembered, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fast = TryOpenDevice(device);
                    if (fast != null)
                    {
                        error = null;
                        return fast;
                    }

                    break;
                }
            }

            // Do NOT take the first vendor collection. There are two, and the reference notes are
            // explicit that picking the wrong one fails silently; requiring output report 0x0F is
            // what actually identifies the command channel.
            foreach (var device in devices)
            {
                if (!IsCommandChannel(device)) continue;

                var channel = TryOpenDevice(device);
                if (channel == null) continue;

                _lastKnownPath = device.DevicePath;

                error = null;
                return channel;
            }

            error = "Found the MSI controller, but its vendor command channel could not be opened.";
            return null;
        }

        private static MsiVendorHidChannel TryOpenDevice(HidDevice device)
        {
            HidStream stream;
            try
            {
                if (!device.TryOpen(out stream)) return null;
            }
            catch (Exception)
            {
                return null;
            }

            return new MsiVendorHidChannel(
                stream,
                TryLength(() => device.GetMaxInputReportLength()),
                TryLength(() => device.GetMaxOutputReportLength()));
        }

        private static bool IsCommandChannel(HidDevice device)
        {
            try
            {
                var descriptor = device.GetReportDescriptor();

                bool vendor = descriptor.DeviceItems
                    .SelectMany(item => item.Usages.GetAllValues())
                    .Any(usage => (usage >> 16) >= VendorUsagePageFloor);

                return vendor && descriptor.Reports.Any(report => report.ReportID == OutputReportId);
            }
            catch (Exception)
            {
                // An interface whose descriptor will not read is not one to open blind.
                return false;
            }
        }

        public void Send(byte opcode, params byte[] arguments)
        {
            var frame = new byte[_outputLength];
            frame[0] = OutputReportId;
            frame[1] = 0x00;
            frame[2] = 0x00;
            frame[3] = PayloadLength;
            frame[4] = opcode;

            for (int i = 0; i < arguments.Length; i++) frame[5 + i] = arguments[i];

            _stream.Write(frame, 0, frame.Length);
        }

        /// <summary>
        /// Reads until a frame carrying <paramref name="opcode"/> arrives, or time runs out.
        /// </summary>
        /// <remarks>
        /// Frames with other opcodes are skipped rather than returned. The device interleaves
        /// unrelated traffic - <c>0x06</c> after every mode change, and long <c>0x05</c> config
        /// dumps - so taking the first report to arrive would read those as an answer.
        /// </remarks>
        public bool WaitFor(byte opcode, TimeSpan timeout, out byte[] frame)
        {
            var buffer = new byte[_inputLength];
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
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
                catch (Exception)
                {
                    break;
                }

                // Callers read byte 5, so a frame too short to carry an argument is not a match
                // however promising its opcode looks.
                if (count < 6) continue;
                if (buffer[0] != InputReportId || buffer[4] != opcode) continue;

                frame = buffer;
                return true;
            }

            frame = null;
            return false;
        }

        /// <summary>
        /// Reads the next input report of any kind, or returns false when time runs out.
        /// </summary>
        /// <remarks>
        /// The unfiltered counterpart to <see cref="WaitFor"/>, for discovery rather than for the
        /// providers - decoding an opcode means seeing the frames that are <i>not</i> the reply,
        /// which is exactly what <see cref="WaitFor"/> exists to hide. Returns the count separately
        /// because a short report is a fact worth recording here, not a frame to discard.
        /// </remarks>
        public bool ReadAny(TimeSpan timeout, out byte[] frame, out int count)
        {
            var buffer = new byte[_inputLength];
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    count = _stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception)
                {
                    break;
                }

                if (count <= 0) continue;

                frame = buffer;
                return true;
            }

            frame = null;
            count = 0;
            return false;
        }

        private static int TryLength(Func<int> read)
        {
            try
            {
                int length = read();
                return length > 0 ? length : 64;
            }
            catch (Exception)
            {
                return 64;
            }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch (Exception) { }
        }
    }
}
