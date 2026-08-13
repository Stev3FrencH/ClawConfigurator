using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McenterLite.Hardware.Windows;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Raw access to the vendor HID command channel, for decoding gate G4 (RGB).
    ///
    /// <para>
    /// Two commands, and the split between them is the usual read/write line:
    /// </para>
    /// <list type="bullet">
    /// <item><c>--hid-listen</c> opens the channel and prints every input report. READ-ONLY.</item>
    /// <item>
    /// <c>--set-hid-raw</c> sends one arbitrary opcode and then listens. It is named <c>set-</c>
    /// because it is the most dangerous command in this tool - arbitrary bytes at an undocumented
    /// embedded controller, with no idea what the opcode does.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// This exists because the lighting path cannot be decoded the way controller mode was. That
    /// one announced itself: the firmware pushes <c>0x27</c> on every change, so watching input
    /// reports while pressing the button revealed the whole protocol. Lighting has no physical
    /// control, so nothing pushes. What MSI Center M sends is an <i>output</i> report, and one
    /// process cannot see another's writes - so the only readable side is whatever the device
    /// reports back, and the only way to find the write opcode is to ask for it.
    /// </para>
    /// </summary>
    internal static class HidRaw
    {
        /// <summary>How long to keep listening after a send, absent an explicit argument.</summary>
        private static readonly TimeSpan DefaultListen = TimeSpan.FromSeconds(3);

        public static int Listen(string[] args)
        {
            if (!TryParseSeconds(args, 0, TimeSpan.FromSeconds(60), out var window)) return 64;

            using var channel = MsiVendorHidChannel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"Listening on the vendor command channel for {window.TotalSeconds:0}s.");
            Console.WriteLine("Change the lighting in MSI Center M now. Ctrl+C to stop early.");
            Console.WriteLine();

            int frames = Drain(channel, window);

            Console.WriteLine();
            Console.WriteLine(frames == 0
                ? "Nothing arrived. The device only reports when it has something to say."
                : $"{frames} frame(s).");
            Console.WriteLine();

            return 0;
        }

        public static int Send(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: --set-hid-raw <opcode> [args...] [listenSeconds]");
                Console.Error.WriteLine("       Values are hex, with or without 0x. e.g. --set-hid-raw 04");
                return 64;
            }

            // A trailing plain-decimal value is the listen window, not a payload byte. Hex args are
            // written 0x-prefixed or as two digits, so '5' is unambiguous where '05' would not be.
            var window = DefaultListen;
            var values = args.ToList();
            if (values.Count > 1
                && values[^1].Length == 1
                && !values[^1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(values[^1], out int seconds))
            {
                window = TimeSpan.FromSeconds(seconds);
                values.RemoveAt(values.Count - 1);
            }

            var bytes = new List<byte>();
            foreach (var value in values)
            {
                if (!TryParseByte(value, out byte parsed))
                {
                    Console.Error.WriteLine($"Not a byte: {value}");
                    return 64;
                }

                bytes.Add(parsed);
            }

            byte opcode = bytes[0];
            var arguments = bytes.Skip(1).ToArray();

            using var channel = MsiVendorHidChannel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 1;
            }

            // Anything queued from before the send would read as a response to it.
            Drain(channel, TimeSpan.FromMilliseconds(300), quiet: true);

            var described = arguments.Length == 0
                ? $"opcode 0x{opcode:X2}"
                : $"opcode 0x{opcode:X2} with {string.Join(" ", arguments.Select(b => b.ToString("X2")))}";

            Console.WriteLine();
            Console.WriteLine($"Sending {described}, then listening {window.TotalSeconds:0}s.");
            Console.WriteLine();

            channel.Send(opcode, arguments);

            int frames = Drain(channel, window);

            Console.WriteLine();
            Console.WriteLine(frames == 0
                ? "No reply. Either the opcode is not one, or it answers somewhere other than here."
                : $"{frames} frame(s) back.");
            Console.WriteLine();

            return 0;
        }

        /// <summary>
        /// Prints every frame that arrives inside <paramref name="window"/>, and returns the count.
        /// </summary>
        private static int Drain(MsiVendorHidChannel channel, TimeSpan window, bool quiet = false)
        {
            var deadline = DateTime.UtcNow + window;
            int frames = 0;

            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                if (!channel.ReadAny(remaining, out var frame, out int count)) break;

                frames++;
                if (!quiet) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Format(frame, count)}");
            }

            return frames;
        }

        /// <summary>
        /// Formats one frame, keeping the header separate from the payload.
        /// </summary>
        /// <remarks>
        /// Trailing zeros are trimmed because a 64-byte report is mostly padding and the payload
        /// drowns in it. The full count is still printed so an all-zero tail is not mistaken for a
        /// short report - the same treatment <c>--hid-watch</c> gives, kept identical on purpose so
        /// captures from the two tools can be diffed against each other.
        /// </remarks>
        private static string Format(byte[] buffer, int count)
        {
            int end = count;
            while (end > 1 && buffer[end - 1] == 0) end--;

            var hex = BitConverter.ToString(buffer, 0, end).Replace("-", " ");
            var tail = end == count ? "" : $" +{count - end} zero";

            var opcode = count > 4 ? $"op {buffer[4]:X2}  " : "";
            return $"({count}) {opcode}{hex}{tail}";
        }

        private static bool TryParseSeconds(string[] args, int index, TimeSpan fallback, out TimeSpan window)
        {
            window = fallback;
            if (args.Length <= index) return true;

            if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                || seconds <= 0)
            {
                Console.Error.WriteLine("Usage: --hid-listen [seconds]");
                return false;
            }

            window = TimeSpan.FromSeconds(seconds);
            return true;
        }

        private static bool TryParseByte(string value, out byte parsed)
        {
            var raw = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
            return byte.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }
    }
}
