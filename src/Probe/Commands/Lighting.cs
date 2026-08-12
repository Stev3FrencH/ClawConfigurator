using System;
using System.Linq;
using McenterLite.Hardware.Windows;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads the controller's live lighting state, and decodes it.
    ///
    /// <para>
    /// READ-ONLY, and the first check on gate G4. The byte offsets in
    /// <see cref="MsiLightingProtocol"/> were computed from MSI's own profile parser rather than
    /// found by poking the device, so the whole thing stands or falls on whether offset 586 really
    /// is the light block. That is what this command answers: if the decode below matches what the
    /// LEDs are visibly doing, the layout is right.
    /// </para>
    /// </summary>
    internal static class Lighting
    {
        public static int Read(string[] args)
        {
            bool raw = args.Any(a => a.Equals("--raw", StringComparison.OrdinalIgnoreCase));

            using var channel = MsiVendorHidChannel.Open(out var openError);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {openError}");
                return 1;
            }

            if (!MsiLightingProtocol.TryReadLightBlock(channel, out var light, out var readError))
            {
                Console.Error.WriteLine($"ERROR: {readError}");
                return 1;
            }

            int active = light[MsiLightingProtocol.ActiveAnimationIndexByte];

            Console.WriteLine();
            Console.WriteLine($"Light block: {light.Length} bytes from profile offset {MsiLightingProtocol.LightOffset}.");
            Console.WriteLine($"Active animation: {active}");
            Console.WriteLine($"Audio rhythm:     {light[MsiLightingProtocol.LightLength - 2]}");
            Console.WriteLine();

            for (int i = 0; i < MsiLightingProtocol.AnimationCount; i++)
            {
                DumpAnimation(light, i, i == active);
            }

            if (raw)
            {
                Console.WriteLine();
                Console.WriteLine("Raw light block:");
                DumpHex(light);
            }

            Console.WriteLine();
            Console.WriteLine("The 9 LEDs are: 0-3 left stick ring, 4-7 right stick ring, 8 ABXY.");
            Console.WriteLine();

            return 0;
        }

        private static void DumpAnimation(byte[] light, int index, bool active)
        {
            int start = MsiLightingProtocol.AnimationStart(index);

            int frames = light[start];
            int effect = light[start + 1];
            int speed = MsiLightingProtocol.EncodeSpeed(light[start + 2]);
            int brightness = light[start + 3];

            var marker = active ? "* " : "  ";
            Console.WriteLine($"{marker}animation {index}: {frames} keyframe(s), effect {effect}, " +
                              $"speed {speed}/{MsiLightingProtocol.MaxSpeed}, brightness {brightness}");

            // Only the active keyframes mean anything; the rest are whatever was left behind.
            for (int frame = 0; frame < Math.Min(frames, MsiLightingProtocol.MaxKeyframes); frame++)
            {
                int at = MsiLightingProtocol.KeyframeStart(index, frame);

                var colours = Enumerable.Range(0, MsiLightingProtocol.RgbCount)
                    .Select(led => $"#{light[at + (led * 3)]:X2}{light[at + (led * 3) + 1]:X2}{light[at + (led * 3) + 2]:X2}");

                Console.WriteLine($"{marker}    frame {frame}: {string.Join(" ", colours)}");
            }
        }

        private static void DumpHex(byte[] data)
        {
            for (int i = 0; i < data.Length; i += 16)
            {
                var slice = data.Skip(i).Take(16).ToArray();
                Console.WriteLine($"  {i,4}  {BitConverter.ToString(slice).Replace("-", " ")}");
            }
        }
    }
}
