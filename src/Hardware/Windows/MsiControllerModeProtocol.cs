using System;
using System.Runtime.Versioning;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The controller-mode conversation carried over <see cref="MsiVendorHidChannel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public, and deliberately so: <c>McenterLite.Probe</c> drives this too. The probe is the
    /// regression harness for everything in <c>docs/hardware-notes.md</c>, and a harness holding
    /// its own copy of the opcodes could pass while the shipping provider was broken - so both go
    /// through here.
    /// </para>
    /// <para>
    /// Three modes, expressed here as the raw bytes rather than a bool, because the firmware has
    /// three and <see cref="IHwMouseProvider"/> only has two. The narrowing belongs at the
    /// provider, not in the protocol.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static class MsiControllerModeProtocol
    {
        /// <summary>Ask the controller what mode it is in. Outbound.</summary>
        public const byte QueryOpcode = 0x26;

        /// <summary>
        /// "The mode is now X." Inbound - both as the reply to <see cref="QueryOpcode"/> and,
        /// unsolicited, when the physical MSI button changes the mode.
        /// </summary>
        public const byte ReportOpcode = 0x27;

        /// <summary>Switch mode. Outbound.</summary>
        public const byte SwitchOpcode = 0x24;

        public const byte ModeXInput = 0x01;
        public const byte ModeDInput = 0x02;
        public const byte ModeDesktop = 0x04;

        /// <summary>
        /// How long to wait for a <see cref="ReportOpcode"/> frame.
        /// </summary>
        /// <remarks>
        /// Generous on purpose. The reply lands in milliseconds when the controller is awake, and
        /// the cost of waiting only shows up when it is not - in which case the honest answer is a
        /// failed read, arrived at slowly, rather than a fast wrong one.
        /// </remarks>
        public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Asks the controller for its current mode.
        /// </summary>
        public static bool TryQuery(MsiVendorHidChannel channel, out byte mode)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            mode = 0;

            try
            {
                channel.Send(QueryOpcode);
                if (!channel.WaitFor(ReportOpcode, ReplyTimeout, out var frame)) return false;

                mode = frame[5];
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends a mode switch. Does NOT confirm it - see the remarks.
        /// </summary>
        /// <remarks>
        /// A software switch is never acknowledged. Only the physical button makes the firmware
        /// emit <see cref="ReportOpcode"/>, so waiting for an announcement here would block until
        /// it timed out on every single call. Callers confirm with <see cref="TryQuery"/> on the
        /// same channel instead.
        /// </remarks>
        public static bool TrySwitch(MsiVendorHidChannel channel, byte mode, out string error)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            try
            {
                channel.Send(SwitchOpcode, mode);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>The firmware's own name for a mode, for diagnostics and logs.</summary>
        public static string Describe(byte mode) => mode switch
        {
            ModeXInput => "XInput (gamepad)",
            ModeDInput => "DirectInput (gamepad)",
            ModeDesktop => "Desktop (mouse)",
            _ => $"unknown (0x{mode:X2})",
        };
    }
}
