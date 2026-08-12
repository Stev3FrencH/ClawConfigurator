using System;
using System.Management;
using System.Runtime.Versioning;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// Power limits, applied directly through the ACPI-WMI interface with no MSI Center M
    /// involvement at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Confirmed standalone on 2026-08-11</b>, on both AC and battery: with MSI Center M's
    /// entire user-mode stack stopped (its scheduled task, every per-feature process, and MSI
    /// Foundation Service - everything except the boot-start <c>msisadrv.sys</c> driver), writing
    /// through this method still moved the sustained CPU clock under load. The mechanism is
    /// Windows' own native ACPI-to-WMI mapping, not anything MSI-authored, so it needs nothing
    /// installed at all - see <c>docs/hardware-notes.md</c>, Gate G1.
    /// </para>
    /// <para>
    /// <c>MSI_ACPI.Get_SlaveBattery</c> / <c>Set_SlaveBattery</c> despite the name - not the
    /// obviously-named <c>Get_Power</c>/<c>Set_Power</c>, which was swept first and found to
    /// return a constant value. Located by sweeping every <c>Get_*</c> method across
    /// sub-functions and diffing snapshots taken at two power-limit points, the same technique
    /// that found the battery charge limit in <c>Get_AP</c> rather than <c>Get_MasterBattery</c> -
    /// the obviously-named method is not reliably the right one on this device.
    /// </para>
    /// <para>
    /// Sub-function (input byte 0) = 1. PL1 at output byte 1, PL2 at output byte 2, watts 1:1, no
    /// scaling. <c>Set_SlaveBattery</c>'s own reply does not carry the values back - it is a
    /// constant <c>01 00 00...</c> regardless of what was written - so unlike every other gate on
    /// this device, a write is verified with a separate <c>Get_SlaveBattery</c> read rather than
    /// trusting the call's own return value.
    /// </para>
    /// <para>
    /// <b>One register, not two.</b> <see cref="RegistryTdpProvider"/> writes independent AC and
    /// DC pairs because MSI Center's own model has both; driving the EC directly here found only
    /// one live value shared by AC and DC (same reading under load, plugged and unplugged). So
    /// there is no separate battery ceiling to maintain - by design, confirmed against MSI
    /// Center's own UI, which offers the same PL1/PL2 range on battery as on AC.
    /// </para>
    /// <para>
    /// Also unlike the registry path, nothing here is gated by MSI's Endurance/AI Engine/Manual
    /// picker - see <see cref="TryReadMode"/>.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WmiTdpProvider : ITdpProvider
    {
        private const string Namespace = @"\\.\root\wmi";
        private const string ClassName = "MSI_ACPI";
        private const string ReadMethod = "Get_SlaveBattery";
        private const string WriteMethod = "Set_SlaveBattery";

        // Confirmed for every MSI_ACPI buffer method inspected so far (Gate G1, G3, G5's related
        // MasterBattery) - one [EmbeddedInstance, in, out] parameter named Data, embedded class
        // Package_32, one property Bytes : UInt8Array[32]. Hard-coded rather than introspected at
        // call time, the way the Phase-0 discovery tooling in src/Probe does it, because the
        // shape is now an established fact about this class rather than something being searched
        // for. See docs/hardware-notes.md.
        private const string ParameterName = "Data";
        private const string PackageClassName = "Package_32";
        private const string ArrayProperty = "Bytes";
        private const int PackageSize = 32;

        private const byte SubFunction = 0x01;
        private const int Pl1Byte = 1;
        private const int Pl2Byte = 2;

        private readonly string _unavailableReason;

        public WmiTdpProvider()
        {
            if (!TryInvoke(ReadMethod, BuildReadPayload(), out _, out _unavailableReason))
                _unavailableReason ??= $"{ReadMethod} returned no data.";
        }

        public bool Available => _unavailableReason == null;
        public string UnavailableReason => _unavailableReason;
        public TdpBackendKind Backend => TdpBackendKind.Wmi;

        public bool TryRead(out int pl1, out int pl2)
        {
            pl1 = 0;
            pl2 = 0;
            if (!Available) return false;

            if (!TryInvoke(ReadMethod, BuildReadPayload(), out var result, out _)) return false;

            pl1 = result[Pl1Byte];
            pl2 = result[Pl2Byte];
            return true;
        }

        public OpResult Apply(int pl1, int pl2)
        {
            if (!Available) return OpResult.Unavailable(_unavailableReason);

            var payload = new byte[PackageSize];
            payload[0] = SubFunction;
            payload[Pl1Byte] = (byte)pl1;
            payload[Pl2Byte] = (byte)pl2;

            if (!TryInvoke(WriteMethod, payload, out _, out var error))
                return OpResult.Fail(error ?? "Could not write the power limits.");

            // Read back independently - see the class remarks on Set_SlaveBattery's reply shape.
            if (!TryRead(out int actualPl1, out int actualPl2))
                return OpResult.Fail("Wrote the power limits but could not read them back.");

            if (actualPl1 != pl1 || actualPl2 != pl2)
            {
                return OpResult.Fail(
                    $"Power limits did not stick: asked for {pl1}/{pl2} W, found "
                    + $"{actualPl1}/{actualPl2} W.");
            }

            return OpResult.Success();
        }

        /// <summary>
        /// Always reports the one mode that honours a manual limit.
        /// </summary>
        /// <remarks>
        /// This path writes the EC directly and is not gated by MSI's Endurance/AI Engine/Manual
        /// triple at all - confirmed by writing through it with MSI Center M's whole user-mode
        /// stack, and with it any notion of "mode", stopped. <see cref="PerfMode.UserScenario"/>
        /// is reported unconditionally because that is the enum value that means "limits are
        /// honoured", not because MSI's mode registry value is being read.
        /// </remarks>
        public bool TryReadMode(out PerfMode mode)
        {
            mode = Available ? PerfMode.UserScenario : PerfMode.Unknown;
            return Available;
        }

        /// <summary>No-op. See <see cref="TryReadMode"/> - there is no mode for this path to switch.</summary>
        public OpResult ApplyMode(PerfMode mode) =>
            Available ? OpResult.Success() : OpResult.Unavailable(_unavailableReason);

        private static byte[] BuildReadPayload()
        {
            var payload = new byte[PackageSize];
            payload[0] = SubFunction;
            return payload;
        }

        // ── WMI plumbing ────────────────────────────────────────────────────────

        private static bool TryInvoke(
            string methodName, byte[] payload, out byte[] result, out string error)
        {
            result = null;
            error = null;

            ManagementScope scope;
            try
            {
                scope = new ManagementScope(Namespace);
                scope.Connect();
            }
            catch (ManagementException ex)
            {
                error = $"Could not reach root\\wmi: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Access denied reaching root\\wmi. The helper needs to run elevated.";
                return false;
            }

            ManagementObject instance = null;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery($"SELECT * FROM {ClassName}"));

                foreach (ManagementObject candidate in searcher.Get())
                {
                    instance = candidate;
                    break;
                }

                if (instance == null)
                {
                    error = $"{ClassName} exists but reported no instances.";
                    return false;
                }

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

                error = $"{methodName} returned no data.";
                return false;
            }
            catch (ManagementException ex)
            {
                error = $"{methodName} failed: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Access denied calling {methodName}. The helper needs to run elevated.";
                return false;
            }
            finally
            {
                instance?.Dispose();
            }
        }
    }
}
