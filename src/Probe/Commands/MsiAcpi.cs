using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Calls methods on the <c>MSI_ACPI</c> ACPI-WMI class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed present on this device as GUID <c>{ABBC0F6E-8EA1-11d1-00A0-C90629100000}</c> in
    /// <c>Diagnostics/device-report.txt</c>. This is the driver-free route to the embedded
    /// controller: ACPI-mediated, so it needs no kernel driver and no port I/O.
    /// </para>
    /// <para>
    /// <b>The parameter shape of these methods is not known ahead of time.</b> They are generated
    /// from the DSDT method each one fronts, so this class inspects <c>InParameters</c> at runtime
    /// and adapts, rather than hard-coding a signature that a guess would get wrong. Everything it
    /// decides is printed, because the whole point of running this is to find out what the shape
    /// actually is.
    /// </para>
    /// </remarks>
    internal static class MsiAcpi
    {
        public const string ClassName = "MSI_ACPI";

        /// <summary>
        /// MSI's ACPI-WMI buffer methods conventionally take a fixed 32-byte package. Used only to
        /// pad a short payload; the padding is always reported so it is never a silent assumption.
        /// </summary>
        private const int ConventionalBufferSize = 32;

        /// <summary>Gets the first <c>MSI_ACPI</c> instance, or null with a reason.</summary>
        public static ManagementObject TryGetInstance(out string error)
        {
            error = null;

            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM {ClassName}"));

                foreach (ManagementObject instance in searcher.Get())
                    return instance;

                error = $"{ClassName} exists but reported no instances.";
                return null;
            }
            catch (ManagementException ex)
            {
                error = $"Could not reach {ClassName}: {ex.Message}. "
                      + "This class is MSI-specific - confirm with --wmi-classes MSI.";
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Access denied reaching root\\wmi. Run this elevated.";
                return null;
            }
        }

        /// <summary>
        /// Invokes one method with a byte payload and returns its output parameters.
        /// </summary>
        /// <remarks>
        /// Callers are responsible for deciding whether a method is safe to call. This does not
        /// police read versus write - see the <c>Get_</c> guard in
        /// <see cref="WmiExplorer.AcpiGet"/> and the single-method restriction in
        /// <see cref="BatteryInfo"/>.
        /// </remarks>
        public static ManagementBaseObject Invoke(
            ManagementObject instance, string methodName, byte[] payload, out string error)
        {
            error = null;

            ManagementBaseObject inParams;
            try
            {
                inParams = instance.GetMethodParameters(methodName);
            }
            catch (ManagementException ex)
            {
                error = $"{ClassName} has no callable method {methodName}: {ex.Message}";
                return null;
            }

            if (inParams == null)
            {
                // A method that declares no inputs. Nothing to fill; call it bare.
                return TryInvoke(instance, methodName, null, out error);
            }

            if (!TryFillParameters(inParams, payload, out error)) return null;

            return TryInvoke(instance, methodName, inParams, out error);
        }

        private static ManagementBaseObject TryInvoke(
            ManagementObject instance, string methodName, ManagementBaseObject inParams,
            out string error)
        {
            error = null;

            try
            {
                return instance.InvokeMethod(methodName, inParams, null);
            }
            catch (ManagementException ex)
            {
                error = $"{methodName} failed: {ex.Message}. "
                      + $"Check the declared shape with --wmi-method {ClassName} {methodName}.";
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Access denied calling {methodName}. Run this elevated.";
                return null;
            }
        }

        /// <summary>
        /// Writes the payload into whichever input parameter can carry it, reporting the choice.
        /// </summary>
        private static bool TryFillParameters(
            ManagementBaseObject inParams, byte[] payload, out string error)
        {
            error = null;

            var properties = inParams.Properties.Cast<PropertyData>().ToList();
            if (properties.Count == 0) return true;

            // Prefer a byte array - MSI's buffer methods take a package, not a scalar.
            var target = properties.FirstOrDefault(p => p.IsArray)
                      ?? properties[0];

            if (target.IsArray)
            {
                var buffer = payload ?? Array.Empty<byte>();

                if (buffer.Length < ConventionalBufferSize)
                {
                    var padded = new byte[ConventionalBufferSize];
                    Array.Copy(buffer, padded, buffer.Length);
                    Console.WriteLine(
                        $"  note   : padded {buffer.Length} byte(s) to {ConventionalBufferSize} "
                        + "(MSI's conventional package size)");
                    buffer = padded;
                }

                target.Value = buffer;
                Console.WriteLine($"  in     : {target.Name} = {Hex(buffer)}");
                return true;
            }

            // Scalar input. Only meaningful with a single byte to give it.
            if (payload == null || payload.Length == 0)
            {
                target.Value = 0;
                Console.WriteLine($"  in     : {target.Name} = 0");
                return true;
            }

            try
            {
                target.Value = Convert.ChangeType(
                    payload[0], TypeFromCimType(target.Type), CultureInfo.InvariantCulture);
                Console.WriteLine($"  in     : {target.Name} = 0x{payload[0]:X2} ({payload[0]})");

                if (payload.Length > 1)
                {
                    Console.WriteLine(
                        $"  warn   : {target.Name} is a scalar; only the first byte was used. "
                        + $"Ignored {payload.Length - 1} extra byte(s).");
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not assign the payload to input '{target.Name}' "
                      + $"of type {target.Type}: {ex.Message}";
                return false;
            }
        }

        private static Type TypeFromCimType(CimType type)
        {
            switch (type)
            {
                case CimType.UInt8: return typeof(byte);
                case CimType.SInt8: return typeof(sbyte);
                case CimType.UInt16: return typeof(ushort);
                case CimType.SInt16: return typeof(short);
                case CimType.UInt32: return typeof(uint);
                case CimType.SInt32: return typeof(int);
                case CimType.UInt64: return typeof(ulong);
                case CimType.SInt64: return typeof(long);
                default: return typeof(uint);
            }
        }

        /// <summary>
        /// Prints every output property. Byte arrays get an indexed hex dump, because finding
        /// WHICH index carries a value is the entire purpose of running these commands.
        /// </summary>
        public static void DumpResult(ManagementBaseObject outParams)
        {
            if (outParams == null)
            {
                Console.WriteLine("  out    : (nothing returned)");
                return;
            }

            bool any = false;
            foreach (PropertyData p in outParams.Properties)
            {
                any = true;

                if (p.Value is byte[] bytes)
                {
                    Console.WriteLine($"  out    : {p.Name} = byte[{bytes.Length}]");
                    DumpIndexedBytes(bytes);
                }
                else
                {
                    Console.WriteLine($"  out    : {p.Name} = {Describe(p.Value)}");
                }
            }

            if (!any) Console.WriteLine("  out    : (no properties)");
        }

        private static void DumpIndexedBytes(byte[] bytes)
        {
            const int PerRow = 16;

            for (int offset = 0; offset < bytes.Length; offset += PerRow)
            {
                var hex = new StringBuilder();
                var dec = new StringBuilder();

                for (int i = offset; i < Math.Min(offset + PerRow, bytes.Length); i++)
                {
                    hex.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    dec.Append(bytes[i].ToString(CultureInfo.InvariantCulture)).Append(' ');
                }

                Console.WriteLine($"           [{offset,2}] {hex}");
                Console.WriteLine($"                {dec}");
            }
        }

        private static string Describe(object value)
        {
            if (value == null) return "(null)";

            if (value is IConvertible && value is not string)
            {
                try
                {
                    long asLong = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return $"0x{asLong:X} ({asLong})";
                }
                catch (Exception)
                {
                    // Not an integral type; fall through to the plain rendering.
                }
            }

            return value.ToString();
        }

        public static string Hex(IEnumerable<byte> bytes) =>
            string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        /// <summary>
        /// Parses payload bytes from the command line. Accepts <c>0xD7</c>, <c>D7</c> or <c>215</c>.
        /// </summary>
        public static bool TryParseBytes(string[] args, out byte[] bytes, out string error)
        {
            error = null;
            var parsed = new List<byte>();

            foreach (var arg in args)
            {
                var text = arg.Trim();
                bool isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (isHex) text = text.Substring(2);

                // A bare token with a hex letter is unambiguous; a bare number is decimal.
                bool looksHex = isHex
                    || text.Any(c => char.IsLetter(c));

                bool ok = looksHex
                    ? byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
                    : byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

                if (!ok)
                {
                    bytes = null;
                    error = $"'{arg}' is not a byte. Use 0xD7, D7 or 215.";
                    return false;
                }

                parsed.Add(value);
            }

            bytes = parsed.ToArray();
            return true;
        }
    }
}
