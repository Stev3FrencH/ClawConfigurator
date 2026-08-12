using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads and switches the controller's mode over MSI's vendor HID channel, with no help from
    /// MSI Center M.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Frame format</b>, established by watching the channel on 2026-08-12 (see
    /// <c>--hid-watch</c>) and consistent with the one known-good frame in the reference notes:
    /// </para>
    /// <code>
    /// byte 0   report id   0x0F outbound, 0x10 inbound
    /// byte 1   0x00
    /// byte 2   0x00
    /// byte 3   0x3C        60 - the payload length, i.e. the 63 report bytes less this header
    /// byte 4   opcode
    /// byte 5+  arguments, zero padded to 64 bytes total
    /// </code>
    /// <para>
    /// <b>Opcodes.</b> <c>0x26</c> asks for the mode, <c>0x27</c> reports it, <c>0x24</c> sets it.
    /// The reference notes gave only <c>0x24</c> with <c>0x04</c> = desktop and <c>0x02</c> =
    /// DInput; <c>0x01</c> = XInput and the whole <c>0x26</c>/<c>0x27</c> pair were established
    /// here, by observation.
    /// </para>
    /// <para>
    /// <b>The mode is a three-state, not a toggle.</b> XInput and DInput are both "gamepad" as far
    /// as the widget is concerned, which is why the existing <c>IHwMouseProvider</c> boolean can
    /// read this but cannot fully express it.
    /// </para>
    /// </remarks>
    internal static class ControllerMode
    {
        private const int VendorId = 0x0DB0;
        private const int VendorUsagePageFloor = 0xFF00;

        private const byte OutputReportId = 0x0F;
        private const byte InputReportId = 0x10;
        private const byte PayloadLength = 0x3C;

        private const byte QueryOpcode = 0x26;
        private const byte ReportOpcode = 0x27;
        private const byte SwitchOpcode = 0x24;

        private const byte ModeXInput = 0x01;
        private const byte ModeDInput = 0x02;
        private const byte ModeDesktop = 0x04;

        private static readonly Dictionary<string, byte> ModesByName =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["xinput"] = ModeXInput,
                ["gamepad"] = ModeXInput,
                ["dinput"] = ModeDInput,
                ["desktop"] = ModeDesktop,
                ["mouse"] = ModeDesktop,
            };

        /// <summary>
        /// Asks the controller which mode it is in.
        /// </summary>
        /// <remarks>
        /// This does put bytes on the wire, which no other read command here does - the channel
        /// has no feature report, so the only way to ask is to send. It changes nothing: 0x26 is
        /// a query, and the mode that comes back is whatever was already true.
        /// </remarks>
        public static int Read(string[] args)
        {
            _ = args;

            using var channel = Channel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 3;
            }

            if (!TryQuery(channel, out byte mode, out var failure))
            {
                Console.Error.WriteLine($"ERROR: {failure}");
                return 4;
            }

            Console.WriteLine($"Controller mode: {Describe(mode)}");
            return 0;
        }

        public static int Set(string[] args)
        {
            if (args.Length < 1 || !ModesByName.TryGetValue(args[0], out byte target))
            {
                Console.Error.WriteLine(
                    "Usage: --set-controller-mode <desktop|xinput|dinput>   (mouse = desktop, gamepad = xinput)");
                return 64;
            }

            using var channel = Channel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 3;
            }

            if (TryQuery(channel, out byte before, out _))
                Console.WriteLine($"Before: {Describe(before)}");

            Console.WriteLine($"Sending SwitchMode 0x{SwitchOpcode:X2} mode 0x{target:X2} ({Describe(target)})...");
            channel.Send(SwitchOpcode, target);

            // The switch is announced rather than acknowledged: the device pushes an unsolicited
            // 0x27 when it actually changes. Catch that first, because it is the real proof - then
            // query anyway, so a mode that reverts a moment later is not reported as a success.
            if (channel.WaitFor(ReportOpcode, TimeSpan.FromSeconds(2), out var announced))
                Console.WriteLine($"Announced: {Describe(announced[5])}");
            else
                Console.WriteLine("No announcement within 2s.");

            if (!TryQuery(channel, out byte after, out var failure))
            {
                Console.Error.WriteLine($"ERROR: switched, but could not read back: {failure}");
                return 4;
            }

            Console.WriteLine($"After: {Describe(after)}");

            if (after != target)
            {
                Console.Error.WriteLine(
                    $"FAILED: asked for {Describe(target)}, device reports {Describe(after)}.");
                return 5;
            }

            Console.WriteLine("OK.");
            return 0;
        }

        private static bool TryQuery(Channel channel, out byte mode, out string error)
        {
            mode = 0;

            channel.Send(QueryOpcode);

            if (!channel.WaitFor(ReportOpcode, TimeSpan.FromSeconds(2), out var frame))
            {
                error = $"the controller did not answer the 0x{QueryOpcode:X2} query within 2s.";
                return false;
            }

            mode = frame[5];
            error = null;
            return true;
        }

        private static string Describe(byte mode) => mode switch
        {
            ModeXInput => "XInput (gamepad)",
            ModeDInput => "DirectInput (gamepad)",
            ModeDesktop => "Desktop (mouse)",
            _ => $"unknown (0x{mode:X2})",
        };

        /// <summary>
        /// The vendor collection, opened for reading and writing.
        /// </summary>
        /// <remarks>
        /// MSI Center M holds this handle too and that is fine - the collection is shared, which
        /// was confirmed by reading it alongside a running MSI Center M.
        /// </remarks>
        private sealed class Channel : IDisposable
        {
            private readonly HidStream _stream;
            private readonly int _inputLength;
            private readonly int _outputLength;

            private Channel(HidStream stream, int inputLength, int outputLength)
            {
                _stream = stream;
                _inputLength = inputLength;
                _outputLength = outputLength;
                _stream.ReadTimeout = 250;
            }

            public static Channel Open(out string error)
            {
                var devices = DeviceList.Local.GetHidDevices(vendorID: VendorId).ToList();
                if (devices.Count == 0)
                {
                    error = $"no HID interfaces found for VID 0x{VendorId:X4}. Is this a Claw?";
                    return null;
                }

                // Do NOT stop at the first vendor match. The reference notes are explicit that
                // there are two vendor collections and that picking the wrong one fails silently;
                // requiring report 0x0F is what actually identifies the right one.
                foreach (var device in devices)
                {
                    if (!IsCommandChannel(device)) continue;
                    if (!device.TryOpen(out var stream)) continue;

                    int input = Try(() => device.GetMaxInputReportLength(), 64);
                    int output = Try(() => device.GetMaxOutputReportLength(), 64);

                    error = null;
                    return new Channel(stream, input, output);
                }

                error = "found MSI HID interfaces, but none exposing the vendor command channel "
                      + $"(usage page >= 0x{VendorUsagePageFloor:X4} with output report 0x{OutputReportId:X2}) "
                      + "could be opened.";
                return null;
            }

            private static bool IsCommandChannel(HidDevice device)
            {
                try
                {
                    var descriptor = device.GetReportDescriptor();

                    bool vendor = descriptor.DeviceItems
                        .SelectMany(i => i.Usages.GetAllValues())
                        .Any(u => (u >> 16) >= VendorUsagePageFloor);

                    if (!vendor) return false;

                    return descriptor.Reports.Any(r => r.ReportID == OutputReportId);
                }
                catch (Exception)
                {
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
            /// Other traffic on this channel is skipped rather than treated as an answer - the
            /// device interleaves 0x06 frames with everything, and taking the first report to
            /// arrive would read those as a mode.
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

                    // Byte 5 is read by every caller, so a frame too short to hold an argument is
                    // not a match no matter what its opcode says.
                    if (count < 6) continue;
                    if (buffer[0] != InputReportId || buffer[4] != opcode) continue;

                    frame = buffer;
                    return true;
                }

                frame = null;
                return false;
            }

            private static int Try(Func<int> read, int fallback)
            {
                try { return read(); }
                catch (Exception) { return fallback; }
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch (Exception) { }
            }
        }
    }
}
