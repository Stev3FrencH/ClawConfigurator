using System;
using System.Management;
using System.Runtime.Versioning;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The battery charge limit, through <c>MSI_ACPI.Get_AP</c> / <c>Set_AP</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gate G3. Needs nothing from MSI Center M - not running, not installed. Verified on device
    /// 2026-08-12 against the only oracle that counts here: with the battery at 74% and charging
    /// stopped against a 60% limit, raising the limit to 80% made it resume charging.
    /// </para>
    /// <code>
    /// byte:  0  1  2  3  4  5   6..31
    ///       01 00 00 C6 80 XX   00 …      XX = percent | 0x80
    /// </code>
    /// <para>
    /// <b>Read-modify-write, never a hand-built buffer.</b> Bytes 3 and 4 are constant across every
    /// setting measured and are presumed to identify the register, but that is presumption - so the
    /// package the firmware just returned is sent back with only the sub-function and byte 5
    /// changed. Zeroing bytes whose meaning is unknown is a guess; echoing them is not.
    /// </para>
    /// <para>
    /// <b>Read back with a separate call.</b> <c>Set_AP</c> replies with a bare <c>01 00 00 …</c>
    /// status that does not echo the value, exactly like <c>Set_SlaveBattery</c> in G1 - so its own
    /// reply is not evidence that anything was applied.
    /// </para>
    /// <para>
    /// <b>MSI Center M does not notice changes made here</b> and goes on showing its own cached
    /// value. That is expected, and is what proves this is the live register rather than another
    /// mirror. Whether MSI Center M later re-asserts its value is untested.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WmiChargeLimitProvider : IChargeLimitProvider
    {
        private const string Namespace = @"\\.\root\wmi";
        private const string ClassName = "MSI_ACPI";
        private const string ReadMethod = "Get_AP";
        private const string WriteMethod = "Set_AP";

        // Same embedded-instance shape as every other MSI_ACPI buffer method. See WmiTdpProvider.
        private const string ParameterName = "Data";
        private const string PackageClassName = "Package_32";
        private const string ArrayProperty = "Bytes";
        private const int PackageSize = 32;

        private const byte SubFunction = 0x00;
        private const int ValueByte = 5;
        private const byte EncodingFlag = 0x80;

        private readonly string _unavailableReason;

        public WmiChargeLimitProvider()
        {
            // Probe with a real read. The class existing says nothing about this method answering.
            if (!TryReadPackage(out _, out var error)) _unavailableReason = error;
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;

        public bool TryRead(out int percent)
        {
            percent = 0;

            if (!TryReadPackage(out var package, out _)) return false;

            int decoded = Decode(package);
            if (decoded < 0) return false;

            percent = decoded;
            return true;
        }

        public OpResult Apply(int percent)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            if (!TryReadPackage(out var package, out var readError))
                return OpResult.Fail($"Could not read the current charge limit: {readError}");

            var payload = (byte[])package.Clone();
            payload[0] = SubFunction;
            payload[ValueByte] = (byte)(percent | EncodingFlag);

            if (!TryInvoke(WriteMethod, payload, out _, out var writeError))
                return OpResult.Fail($"Could not set the charge limit: {writeError}");

            if (!TryReadPackage(out var after, out var verifyError))
                return OpResult.Fail($"Set the charge limit but could not read it back: {verifyError}");

            int actual = Decode(after);
            if (actual != percent)
            {
                return OpResult.Fail(
                    $"Charge limit did not stick: asked for {percent}%, "
                    + (actual < 0 ? "the device reported an undecodable value." : $"found {actual}%."));
            }

            return OpResult.Success();
        }

        private bool TryReadPackage(out byte[] package, out string error)
        {
            package = null;

            var request = new byte[PackageSize];
            request[0] = SubFunction;

            if (!TryInvoke(ReadMethod, request, out var result, out error)) return false;

            if (result == null || result.Length <= ValueByte)
            {
                error = $"{ReadMethod} returned no usable package.";
                return false;
            }

            package = result;
            return true;
        }

        /// <summary>The percentage in byte 5, or -1 when bit 7 is clear.</summary>
        /// <remarks>
        /// A byte without the flag is refused rather than masked. It would still yield a plausible
        /// 0-127 number, and reporting that as the charge limit would be a confident wrong answer
        /// instead of a visible failure.
        /// </remarks>
        private static int Decode(byte[] package)
        {
            byte raw = package[ValueByte];
            return (raw & EncodingFlag) == 0 ? -1 : raw & 0x7F;
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
