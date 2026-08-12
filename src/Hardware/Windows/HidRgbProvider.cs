using System;
using System.Runtime.Versioning;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The controller's nine RGB LEDs, over MSI's vendor HID channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs MSI Center M neither running nor installed, and needs no elevation. Shares the
    /// channel with <see cref="HidHwMouseProvider"/> - it is the same command surface, and the
    /// notes said all along that G4 should extend it rather than open its own handle.
    /// </para>
    /// <para>
    /// <b>Read-modify-write, always.</b> The lighting is a slice of one configuration blob that
    /// also holds key mappings and calibration, so a write built from nothing would clobber
    /// settings that are not ours. Reading first also preserves the three animation slots we never
    /// use and the two tail bytes nobody has decoded.
    /// </para>
    /// <para>
    /// <b>RAM, never flash.</b> <c>SyncToROM</c> exists and would survive a power cycle, but this
    /// is driven by a widget button a user can tap as often as they like, and flash wears out. The
    /// helper re-applies the stored slot at startup instead - the same trade the charge limit
    /// makes, for the same reason.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class HidRgbProvider : IRgbProvider
    {
        private readonly string _unavailableReason;

        public HidRgbProvider()
        {
            // Probe with a real read, as the mode provider does. The controller can enumerate and
            // still not answer, and claiming availability then failing every write is worse than
            // saying so up front.
            using var channel = MsiVendorHidChannel.Open(out var openError);
            if (channel == null)
            {
                _unavailableReason = openError;
                return;
            }

            if (!MsiLightingProtocol.TryReadLightBlock(channel, out _, out var readError))
                _unavailableReason = readError;
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        public OpResult Apply(LightingAnimation animation)
        {
            if (animation == null) return OpResult.Fail("No lighting animation to apply.");

            using var channel = MsiVendorHidChannel.Open(out var openError);
            if (channel == null) return OpResult.Fail(openError);

            if (!MsiLightingProtocol.TryReadLightBlock(channel, out var current, out var readError))
                return OpResult.Fail($"Could not read the current lighting: {readError}");

            var updated = MsiLightingProtocol.BuildLightBlock(current, animation);

            if (!MsiLightingProtocol.TryWriteLightBlock(channel, updated, out var writeError))
                return OpResult.Fail($"Could not write the lighting: {writeError}");

            // Confirm by reading back. The write is acknowledged with a bare status that does not
            // echo the value, so a clean send proves only that something was sent - the same trap
            // the charge limit hit on MSI_ACPI.Set_AP.
            if (!MsiLightingProtocol.TryReadLightBlock(channel, out var after, out var confirmError))
                return OpResult.Fail($"Applied the lighting but could not confirm it: {confirmError}");

            for (int i = 0; i < updated.Length; i++)
            {
                if (updated[i] == after[i]) continue;

                return OpResult.Fail(
                    $"Lighting did not stick: byte {i} reads 0x{after[i]:X2}, expected 0x{updated[i]:X2}.");
            }

            return OpResult.Success();
        }
    }
}
