using System;
using System.Runtime.Versioning;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The controller's lighting protocol, on the same vendor HID channel as controller mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lighting is not a separate feature on this device: it is a slice of the controller's single
    /// configuration blob, the same one that holds key mappings, stick calibration and rumble. So
    /// there is no "set colour" command. Changing the lighting means writing bytes 586..1468 of
    /// that blob and letting the firmware animate what it finds there.
    /// </para>
    /// <code>
    /// profile blob, 1478 bytes
    ///   0    custom data, keys, sticks, triggers, macros   (not ours - do not write)
    ///   586  light block, 883 bytes
    ///        586      active animation index, 0-3
    ///        587      animation 0   220 bytes
    ///        807      animation 1   220 bytes
    ///        1027     animation 2   220 bytes
    ///        1247     animation 3   220 bytes
    ///        1467     audio rhythm enable
    ///        1468     reserved
    ///
    /// animation, 220 bytes
    ///   0    active keyframe count, 1-8
    ///   1    effect number, always 9
    ///   2    speed, STORED INVERTED - see <see cref="EncodeSpeed"/>
    ///   3    brightness, 0-100
    ///   4    8 keyframes of 27 bytes: 9 RGB triples each
    ///
    /// the 9 LEDs
    ///   0-3  left stick ring
    ///   4-7  right stick ring
    ///   8    ABXY cluster
    /// </code>
    /// <para>
    /// <b>Provenance: this layout was read, not guessed.</b> MSI's own
    /// <c>API_ControlMode.dll</c> is unobfuscated .NET and carries the whole protocol - the
    /// <c>CommandType</c> enum, the packet framing and the profile parser. It independently
    /// confirms the controller-mode opcodes that gate G5 decoded by observation, which is a strong
    /// check on both. See gate G4 in <c>docs/hardware-notes.md</c>.
    /// </para>
    /// <para>
    /// Nothing here needs MSI Center M, and nothing here needs elevation.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static class MsiLightingProtocol
    {
        // Opcodes, from MSI's CommandType enum. Note that the write is 0x21 and NOT the enum's
        // WriteProfile = 3: MSI names 3 but sends 33, and 0x21..0x24 form the coherent family
        // (write RAM, save ROM, load ROM, switch mode) that the device actually implements.
        public const byte OpReadProfile = 0x04;
        public const byte OpReadProfileAck = 0x05;
        public const byte OpWriteProfileToRam = 0x21;
        public const byte OpSaveToRom = 0x22;

        public const int ProfileLength = 1478;
        public const int LightOffset = 586;
        public const int LightLength = 883;

        public const int AnimationLength = 220;
        public const int AnimationCount = 4;
        public const int KeyframeLength = 27;
        public const int MaxKeyframes = 8;
        public const int RgbCount = 9;

        /// <summary>Payload bytes per frame: 64 less the 9-byte header.</summary>
        public const int ChunkSize = 55;

        public const int MaxBrightness = 100;
        public const int MaxSpeed = 20;

        /// <summary>Which byte of the light block holds each field.</summary>
        public const int ActiveAnimationIndexByte = 0;

        private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Speed is stored inverted: the firmware's number counts delay, not rate.
        /// </summary>
        /// <remarks>
        /// Its own inverse, so the same method decodes. Kept as a named method rather than an
        /// inline subtraction because a raw <c>20 - x</c> at a call site reads like a bug.
        /// </remarks>
        public static int EncodeSpeed(int speed) => Math.Clamp(MaxSpeed - Math.Clamp(speed, 0, MaxSpeed), 0, MaxSpeed);

        public static int AnimationStart(int index) => 1 + (index * AnimationLength);

        public static int KeyframeStart(int animation, int keyframe) =>
            AnimationStart(animation) + 4 + (keyframe * KeyframeLength);

        /// <summary>
        /// Reads the light block out of the controller's live configuration.
        /// </summary>
        /// <remarks>
        /// Reads only bytes 586..1468 rather than the whole blob - the rest is key mapping and
        /// calibration, which is none of our business and 17 frames cheaper not to fetch.
        /// </remarks>
        public static bool TryReadLightBlock(MsiVendorHidChannel channel, out byte[] light, out string error)
        {
            light = new byte[LightLength];

            for (int done = 0; done < LightLength; done += ChunkSize)
            {
                int offset = LightOffset + done;
                int length = Math.Min(ChunkSize, LightLength - done);

                channel.Send(OpReadProfile, 0x00, (byte)(offset / 256), (byte)(offset % 256), (byte)length);

                if (!TryAwaitChunk(channel, offset, length, out var chunk))
                {
                    light = null;
                    error = $"The controller did not answer a profile read at offset {offset}.";
                    return false;
                }

                Buffer.BlockCopy(chunk, 9, light, done, length);
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Writes the light block back, into RAM only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// RAM, deliberately. <see cref="OpSaveToRom"/> exists and would survive a power cycle, but
        /// flash has a write budget and this is driven by a widget the user can poke repeatedly.
        /// The helper re-applies on start instead, which is the same mechanism the charge limit
        /// uses and costs the hardware nothing.
        /// </para>
        /// <para>
        /// MSI writes only the range that differs from what it last sent. We write the whole
        /// block: 17 frames instead of a handful, still well under a tenth of a second, and it
        /// removes an entire class of bug where our idea of the device's state has drifted.
        /// </para>
        /// </remarks>
        public static bool TryWriteLightBlock(MsiVendorHidChannel channel, byte[] light, out string error)
        {
            if (light == null || light.Length != LightLength)
            {
                error = $"A light block must be exactly {LightLength} bytes.";
                return false;
            }

            for (int done = 0; done < LightLength; done += ChunkSize)
            {
                int offset = LightOffset + done;
                int length = Math.Min(ChunkSize, LightLength - done);

                var payload = new byte[4 + length];
                payload[0] = 0x00;
                payload[1] = (byte)(offset / 256);
                payload[2] = (byte)(offset % 256);
                payload[3] = (byte)length;
                Buffer.BlockCopy(light, done, payload, 4, length);

                channel.Send(OpWriteProfileToRam, payload);
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Lays a rendered animation into a light block, leaving everything else untouched.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Takes the block that was just read rather than building one from nothing, and edits
        /// animation 0 in place. The other three animation slots hold whatever the firmware or MSI
        /// Center M last left there, and the tail carries an audio-rhythm flag and a reserved byte
        /// we have no decode for. None of that is ours to clear.
        /// </para>
        /// <para>
        /// Animation 0 specifically, because that is the only slot MSI's own UI ever writes, so it
        /// is the one slot the firmware is known to animate correctly.
        /// </para>
        /// </remarks>
        public static byte[] BuildLightBlock(byte[] current, LightingAnimation animation)
        {
            var block = current != null && current.Length == LightLength
                ? (byte[])current.Clone()
                : new byte[LightLength];

            block[ActiveAnimationIndexByte] = 0;

            int start = AnimationStart(0);
            block[start] = (byte)Math.Clamp(animation.KeyframeCount, 1, MaxKeyframes);
            block[start + 1] = 9;
            block[start + 2] = (byte)EncodeSpeed(animation.Speed);
            block[start + 3] = (byte)Math.Clamp(animation.Brightness, 0, MaxBrightness);

            for (int frame = 0; frame < MaxKeyframes; frame++)
            {
                int at = KeyframeStart(0, frame);
                var leds = animation.Keyframes[frame].Leds;

                for (int led = 0; led < RgbCount; led++)
                {
                    block[at + (led * 3)] = leds[led].R;
                    block[at + (led * 3) + 1] = leds[led].G;
                    block[at + (led * 3) + 2] = leds[led].B;
                }
            }

            return block;
        }

        /// <summary>
        /// Waits for the read acknowledgement that matches this request.
        /// </summary>
        /// <remarks>
        /// Matching on offset and length, not just opcode. The device interleaves unsolicited
        /// traffic and answers reads out of order under load, so taking the first <c>0x05</c> to
        /// arrive would assemble the block from the wrong pieces - and it would assemble
        /// <i>something</i>, which is the dangerous part.
        /// </remarks>
        private static bool TryAwaitChunk(MsiVendorHidChannel channel, int offset, int length, out byte[] chunk)
        {
            var deadline = DateTime.UtcNow + ChunkTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (!channel.ReadAny(deadline - DateTime.UtcNow, out var frame, out int count)) break;

                if (count < 9 + length) continue;
                if (frame[4] != OpReadProfileAck) continue;
                if (frame[6] != (byte)(offset / 256)) continue;
                if (frame[7] != (byte)(offset % 256)) continue;
                if (frame[8] != (byte)length) continue;

                chunk = frame;
                return true;
            }

            chunk = null;
            return false;
        }
    }
}
