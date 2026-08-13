using System;
using System.Management;
using System.Runtime.Versioning;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The two fans' duty tables, through <c>MSI_ACPI.Get_Fan</c> / <c>Set_Fan</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gate G2. Needs nothing from MSI Center M — not running, not installed. Measured on device
    /// 2026-08-12 by diffing snapshots across MSI Center M's own Auto / minimum / maximum settings,
    /// and the write proven the same day with fan 2 held as a control.
    /// </para>
    /// <code>
    /// sub-function = fan number (1 or 2); sub-function 0 is live tachometers, not a table.
    ///
    /// byte:  0   1    2  3  4  5  6  7    8      9..31
    ///       01  58   70 74 76 78 80 84   94      00 …
    ///        |   |    \___________ ___/    \_ ceiling, EC state - never written
    ///        |   |                v
    ///        |   |         duty % at 47 50 57 64 71 78 C
    ///        |   idle duty, below the first breakpoint
    ///        status
    /// </code>
    /// <para>
    /// <b>Read-modify-write, never a hand-built buffer.</b> Byte 8 is a ceiling MSI's own UI never
    /// touched and bytes past it are unexplained, so the package the firmware just returned is sent
    /// back with only the sub-function and the seven duties changed. Zeroing bytes whose meaning is
    /// unknown is a guess; echoing them is not.
    /// </para>
    /// <para>
    /// <b>Read back with a separate call.</b> <c>Set_Fan</c> replies with a bare <c>01 00 00 …</c>
    /// status that does not echo the duties, exactly like <c>Set_AP</c> and <c>Set_SlaveBattery</c>.
    /// Its own reply is not evidence that anything was applied.
    /// </para>
    /// <para>
    /// <b>There is no mode to set.</b> The firmware runs whatever table it holds. A non-factory
    /// table applied while MSI Center M's registry still read <c>Fan = 1</c> (Auto), which is what
    /// proves the mode is MSI Center M's own bookkeeping rather than a firmware state.
    /// </para>
    /// <para>
    /// <b>No duty floor is enforced here.</b> The firmware accepts 0 and stops the fan; so does MSI
    /// Center M's own UI. Refusing it in this layer would be inventing a limit the hardware does
    /// not have, and would put it in the one place the user cannot see. The warning belongs in the
    /// UI and the log — see <see cref="FanProfile.StopsAFan"/>.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WmiFanProvider : IFanProvider
    {
        private const string Namespace = @"\\.\root\wmi";
        private const string ClassName = "MSI_ACPI";
        private const string ReadMethod = "Get_Fan";
        private const string WriteMethod = "Set_Fan";

        // Same embedded-instance shape as every other MSI_ACPI buffer method.
        private const string ParameterName = "Data";
        private const string PackageClassName = "Package_32";
        private const string ArrayProperty = "Bytes";
        private const int PackageSize = 32;

        /// <summary>Byte 1 is the idle duty; bytes 2-7 are the six curve points.</summary>
        private const int FirstDutyByte = 1;

        /// <summary>Byte 8, the ceiling. Read and echoed, never authored.</summary>
        private const int CeilingByte = 8;

        private readonly string _unavailableReason;

        public WmiFanProvider()
        {
            // Probe with a real read of fan 1. The class existing says nothing about this method
            // answering, and a device with one fan would fail on the second sub-function later.
            for (int fan = 1; fan <= FanProfile.FanCount; fan++)
            {
                string error;
                if (TryReadPackage(fan, out _, out error)) continue;

                _unavailableReason = "Fan " + fan + ": " + error;
                return;
            }
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        public bool TryRead(out FanProfile current)
        {
            current = null;
            var profile = new FanProfile { Name = "Current" };

            for (int fan = 1; fan <= FanProfile.FanCount; fan++)
            {
                byte[] package;
                if (!TryReadPackage(fan, out package, out _)) return false;

                var duties = profile.Duties(fan);
                for (int i = 0; i < FanProfile.DutyCount; i++)
                    duties[i] = package[FirstDutyByte + i];
            }

            current = profile;
            return true;
        }

        public OpResult Apply(FanProfile profile)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);
            if (profile == null) return OpResult.Fail("No fan profile was given.");

            for (int fan = 1; fan <= FanProfile.FanCount; fan++)
            {
                var result = ApplyOneFan(fan, profile.Duties(fan));
                if (!result.Ok) return result;
            }

            return OpResult.Success();
        }

        /// <summary>
        /// Writes one fan and confirms it.
        /// </summary>
        /// <remarks>
        /// Per fan rather than both at once because each is a separate sub-function and a partial
        /// failure has to be reported as one. Stopping at the first failure leaves the other fan on
        /// whatever it already had, which is the safe half of a bad outcome — the alternative,
        /// carrying on, risks two wrong tables instead of one.
        /// </remarks>
        private OpResult ApplyOneFan(int fan, int[] duties)
        {
            byte[] package;
            string readError;
            if (!TryReadPackage(fan, out package, out readError))
                return OpResult.Fail("Could not read fan " + fan + "'s table: " + readError);

            var payload = (byte[])package.Clone();
            payload[0] = (byte)fan;

            for (int i = 0; i < FanProfile.DutyCount; i++)
            {
                int duty = duties[i];
                if (duty < FanProfile.MinDuty) duty = FanProfile.MinDuty;
                if (duty > FanProfile.MaxDuty) duty = FanProfile.MaxDuty;
                payload[FirstDutyByte + i] = (byte)duty;
            }

            string writeError;
            if (!TryInvoke(WriteMethod, payload, out _, out writeError))
                return OpResult.Fail("Could not set fan " + fan + ": " + writeError);

            byte[] after;
            string verifyError;
            if (!TryReadPackage(fan, out after, out verifyError))
                return OpResult.Fail("Set fan " + fan + " but could not read it back: " + verifyError);

            for (int i = 0; i < FanProfile.DutyCount; i++)
            {
                if (after[FirstDutyByte + i] == payload[FirstDutyByte + i]) continue;

                return OpResult.Fail(
                    "Fan " + fan + " did not stick: asked for "
                    + payload[FirstDutyByte + i] + "% at position " + i
                    + ", found " + after[FirstDutyByte + i] + "%.");
            }

            return OpResult.Success();
        }

        private bool TryReadPackage(int fan, out byte[] package, out string error)
        {
            package = null;

            var request = new byte[PackageSize];
            request[0] = (byte)fan;

            byte[] result;
            if (!TryInvoke(ReadMethod, request, out result, out error)) return false;

            if (result == null || result.Length <= CeilingByte)
            {
                error = ReadMethod + " returned no usable package.";
                return false;
            }

            package = result;
            return true;
        }

        private static bool TryInvoke(
            string methodName, byte[] payload, out byte[] result, out string error)
        {
            result = null;
            error = null;

            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery($"SELECT * FROM {ClassName}"));

                ManagementObject instance = null;
                foreach (ManagementObject found in searcher.Get())
                {
                    instance = found;
                    break;
                }

                if (instance == null)
                {
                    error = $"{ClassName} exists but reported no instances.";
                    return false;
                }

                using (instance)
                {
                    using var inParams = instance.GetMethodParameters(methodName);
                    using var package = new ManagementClass(
                        scope, new ManagementPath(PackageClassName), null).CreateInstance();

                    var buffer = new byte[PackageSize];
                    Array.Copy(payload, buffer, Math.Min(payload.Length, PackageSize));
                    package[ArrayProperty] = buffer;
                    inParams[ParameterName] = package;

                    using var outParams = instance.InvokeMethod(methodName, inParams, null);

                    if (outParams?[ParameterName] is ManagementBaseObject data
                        && data[ArrayProperty] is byte[] bytes)
                    {
                        result = bytes;
                        return true;
                    }

                    error = $"{methodName} returned no {ParameterName} package.";
                    return false;
                }
            }
            catch (ManagementException ex)
            {
                error = $"{methodName} failed: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Access denied calling {methodName}. The helper must run elevated.";
                return false;
            }
        }
    }
}
