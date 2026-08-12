using System;
using System.Linq;
using McenterLite.Hardware.Windows;
using McenterLite.Shared.Model;

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

        /// <summary>
        /// Applies a profile, or turns the lighting off. WRITES to the controller.
        /// </summary>
        /// <remarks>
        /// Read-modify-write, and RAM only. Reading first preserves the three animation slots we
        /// do not use and the two tail bytes we have no decode for; see
        /// <see cref="MsiLightingProtocol.BuildLightBlock"/>.
        /// </remarks>
        public static int Set(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: --set-lighting <1|2|3|off> [profileDirectory]");
                return 64;
            }

            var directory = args.Length > 1
                ? args[1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "LightingProfiles");

            LightingProfile profile;

            if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                profile = new LightingProfile { Name = "Off", Style = LightingStyle.Off };
            }
            else
            {
                if (!int.TryParse(args[0], out int slot)
                    || slot < 1 || slot > LightingProfileStore.ProfileCount)
                {
                    Console.Error.WriteLine("Profile must be 1, 2, 3 or off.");
                    return 64;
                }

                var store = new LightingProfileStore(directory);
                store.EnsureSeeded(Console.WriteLine);
                profile = store.Load(slot, Console.WriteLine);

                Console.WriteLine($"Profiles in {store.Directory}");
            }

            var animation = LightingRenderer.Render(profile);

            Console.WriteLine();
            Console.WriteLine($"Applying '{profile.Name}': style {profile.Style}, " +
                              $"{animation.KeyframeCount} keyframe(s), speed {animation.Speed}, " +
                              $"brightness {animation.Brightness}.");

            using var channel = MsiVendorHidChannel.Open(out var openError);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {openError}");
                return 1;
            }

            if (!MsiLightingProtocol.TryReadLightBlock(channel, out var current, out var readError))
            {
                Console.Error.WriteLine($"ERROR: {readError}");
                return 1;
            }

            var updated = MsiLightingProtocol.BuildLightBlock(current, animation);

            if (!MsiLightingProtocol.TryWriteLightBlock(channel, updated, out var writeError))
            {
                Console.Error.WriteLine($"ERROR: {writeError}");
                return 1;
            }

            // The write is not acknowledged with the value, so confirm by reading it back. Same
            // rule the charge limit learned the hard way: a Set that returns cleanly proves only
            // that something was sent.
            if (!MsiLightingProtocol.TryReadLightBlock(channel, out var after, out var confirmError))
            {
                Console.Error.WriteLine($"Applied, but could not confirm: {confirmError}");
                return 1;
            }

            int mismatch = -1;
            for (int i = 0; i < updated.Length; i++)
            {
                if (updated[i] == after[i]) continue;
                mismatch = i;
                break;
            }

            Console.WriteLine();
            if (mismatch < 0)
            {
                Console.WriteLine("Confirmed: the controller reads back exactly what was written.");
            }
            else
            {
                Console.WriteLine($"WARNING: read-back differs from offset {mismatch} " +
                                  $"(wrote 0x{updated[mismatch]:X2}, read 0x{after[mismatch]:X2}).");
            }

            Console.WriteLine();
            return mismatch < 0 ? 0 : 1;
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
