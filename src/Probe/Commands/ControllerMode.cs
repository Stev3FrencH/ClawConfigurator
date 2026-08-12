using System;
using System.Collections.Generic;
using McenterLite.Hardware.Windows;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Reads and switches the controller's mode over MSI's vendor HID channel, with no help from
    /// MSI Center M.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately thin. The framing and opcodes live in <see cref="MsiVendorHidChannel"/> and
    /// <see cref="MsiControllerModeProtocol"/> in the hardware layer, so this command exercises
    /// the code that actually ships. A probe with its own copy of the protocol would keep passing
    /// after the provider broke, which would make it worse than no harness at all.
    /// </para>
    /// <para>
    /// The frame format and the 0x26/0x27 pair are recorded under gate G5 in
    /// <c>docs/hardware-notes.md</c>.
    /// </para>
    /// </remarks>
    internal static class ControllerMode
    {
        private static readonly Dictionary<string, byte> ModesByName =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["xinput"] = MsiControllerModeProtocol.ModeXInput,
                ["gamepad"] = MsiControllerModeProtocol.ModeXInput,
                ["dinput"] = MsiControllerModeProtocol.ModeDInput,
                ["desktop"] = MsiControllerModeProtocol.ModeDesktop,
                ["mouse"] = MsiControllerModeProtocol.ModeDesktop,
            };

        /// <summary>
        /// Asks the controller which mode it is in.
        /// </summary>
        /// <remarks>
        /// This does put bytes on the wire, unlike every other read command here - the channel has
        /// no feature report, so sending a query is the only way to ask. It changes nothing.
        /// </remarks>
        public static int Read(string[] args)
        {
            _ = args;

            using var channel = MsiVendorHidChannel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 3;
            }

            if (!MsiControllerModeProtocol.TryQuery(channel, out byte mode))
            {
                Console.Error.WriteLine(
                    $"ERROR: the controller did not answer the 0x{MsiControllerModeProtocol.QueryOpcode:X2} query.");
                return 4;
            }

            Console.WriteLine($"Controller mode: {MsiControllerModeProtocol.Describe(mode)}");
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

            using var channel = MsiVendorHidChannel.Open(out var error);
            if (channel == null)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return 3;
            }

            if (MsiControllerModeProtocol.TryQuery(channel, out byte before))
                Console.WriteLine($"Before: {MsiControllerModeProtocol.Describe(before)}");

            Console.WriteLine(
                $"Sending SwitchMode 0x{MsiControllerModeProtocol.SwitchOpcode:X2} "
                + $"mode 0x{target:X2} ({MsiControllerModeProtocol.Describe(target)})...");

            if (!MsiControllerModeProtocol.TrySwitch(channel, target, out var sendError))
            {
                Console.Error.WriteLine($"ERROR: {sendError}");
                return 4;
            }

            if (!MsiControllerModeProtocol.TryQuery(channel, out byte after))
            {
                Console.Error.WriteLine("ERROR: switched, but could not read back.");
                return 4;
            }

            Console.WriteLine($"After: {MsiControllerModeProtocol.Describe(after)}");

            if (after != target)
            {
                Console.Error.WriteLine(
                    $"FAILED: asked for {MsiControllerModeProtocol.Describe(target)}, "
                    + $"device reports {MsiControllerModeProtocol.Describe(after)}.");
                return 5;
            }

            Console.WriteLine("OK.");
            return 0;
        }
    }
}
