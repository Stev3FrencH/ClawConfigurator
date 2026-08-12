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

            if (!TryFillParameters(instance.Scope, inParams, payload, out error)) return null;

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
        /// <remarks>
        /// Three shapes are handled, and the middle one is the one that matters on this class.
        /// Every <c>MSI_ACPI</c> buffer method takes a single <b>embedded instance</b> - a
        /// <c>Package_32</c> object whose <c>Bytes</c> property is the actual array - not a bare
        /// byte array. Treating it as a scalar assigns an integer to an object parameter, and WMI
        /// rejects that with a bare "Type mismatch" that says nothing about which parameter or why.
        /// That was this method's behaviour for every method on the class until 2026-08-12; the
        /// gate-G3 charge-limit discovery happened to be done with <c>Sweep-MsiAcpi.ps1</c>
        /// instead, so nothing exercised it.
        /// </remarks>
        private static bool TryFillParameters(
            ManagementScope scope, ManagementBaseObject inParams, byte[] payload, out string error)
        {
            error = null;

            var properties = inParams.Properties.Cast<PropertyData>().ToList();
            if (properties.Count == 0) return true;

            // Prefer a byte array, then an embedded package. A bare array is not the shape this
            // class uses, but it costs nothing to keep supporting and this is discovery tooling.
            var target = properties.FirstOrDefault(p => p.IsArray)
                      ?? properties.FirstOrDefault(p => p.Type == CimType.Object)
                      ?? properties[0];

            if (target.Type == CimType.Object && !target.IsArray)
                return TryFillEmbeddedPackage(scope, inParams, target, payload, out error);

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

        /// <summary>
        /// Fills an <c>[EmbeddedInstance]</c> parameter: builds the embedded class, puts the
        /// payload in its array property, and assigns the whole object.
        /// </summary>
        /// <remarks>
        /// The embedded class name is read from the parameter's own <c>CIMTYPE</c> qualifier
        /// (<c>object:Package_32</c>) rather than hard-coded, because this is discovery tooling and
        /// a method taking a different package size should report that rather than fail. The
        /// shipping providers in <c>src/Hardware</c> deliberately do hard-code it - there the shape
        /// is an established fact, not something being looked for.
        /// </remarks>
        private static bool TryFillEmbeddedPackage(
            ManagementScope scope, ManagementBaseObject inParams, PropertyData target,
            byte[] payload, out string error)
        {
            error = null;

            var packageClass = EmbeddedClassName(target);
            if (packageClass == null)
            {
                error = $"Input '{target.Name}' is an embedded object but declares no CIMTYPE "
                      + "class name, so there is nothing to instantiate.";
                return false;
            }

            try
            {
                using var definition = new ManagementClass(scope, new ManagementPath(packageClass), null);
                var package = definition.CreateInstance();

                var arrayProperty = FirstArrayProperty(package);
                if (arrayProperty == null)
                {
                    error = $"Embedded class {packageClass} has no array property to carry a payload.";
                    return false;
                }

                var buffer = new byte[ConventionalBufferSize];
                var source = payload ?? Array.Empty<byte>();
                Array.Copy(source, buffer, Math.Min(source.Length, buffer.Length));

                if (source.Length > buffer.Length)
                {
                    Console.WriteLine(
                        $"  warn   : payload is {source.Length} bytes; only the first "
                        + $"{buffer.Length} were sent.");
                }

                package[arrayProperty] = buffer;
                inParams[target.Name] = package;

                Console.WriteLine(
                    $"  in     : {target.Name} = {packageClass}.{arrayProperty} = {Hex(buffer)}");
                return true;
            }
            catch (ManagementException ex)
            {
                error = $"Could not build embedded input '{target.Name}' of class {packageClass}: "
                      + ex.Message;
                return false;
            }
        }

        /// <summary>Reads <c>Package_32</c> out of a <c>CIMTYPE</c> qualifier of <c>object:Package_32</c>.</summary>
        private static string EmbeddedClassName(PropertyData property)
        {
            const string Prefix = "object:";

            try
            {
                if (property.Qualifiers["CIMTYPE"].Value is not string cimType) return null;

                return cimType.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    ? cimType.Substring(Prefix.Length)
                    : null;
            }
            catch (ManagementException)
            {
                // No CIMTYPE qualifier at all.
                return null;
            }
        }

        private static string FirstArrayProperty(ManagementBaseObject instance)
        {
            foreach (PropertyData property in instance.Properties)
                if (property.IsArray) return property.Name;

            return null;
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
                else if (p.Value is ManagementBaseObject embedded)
                {
                    // The reply comes back in the same embedded-instance shape as the input, so
                    // the interesting bytes are one level down. Printing the object itself would
                    // just show a class path.
                    DumpEmbedded(p.Name, embedded);
                }
                else
                {
                    Console.WriteLine($"  out    : {p.Name} = {Describe(p.Value)}");
                }
            }

            if (!any) Console.WriteLine("  out    : (no properties)");
        }

        /// <summary>
        /// Pulls the payload array out of a result, through the embedded instance if there is one.
        /// </summary>
        /// <remarks>
        /// Returns the first array found rather than looking for a name. These methods carry
        /// exactly one, and a command that has to know it is called <c>Bytes</c> would break on the
        /// next method that calls it something else - which is the sort of thing this tool exists
        /// to discover.
        /// </remarks>
        public static byte[] ExtractBytes(ManagementBaseObject outParams)
        {
            if (outParams == null) return null;

            foreach (PropertyData property in outParams.Properties)
            {
                if (property.Value is byte[] direct) return direct;

                if (property.Value is ManagementBaseObject embedded)
                {
                    foreach (PropertyData inner in embedded.Properties)
                        if (inner.Value is byte[] bytes) return bytes;
                }
            }

            return null;
        }

        private static void DumpEmbedded(string name, ManagementBaseObject embedded)
        {
            string className;
            try { className = embedded.ClassPath?.ClassName ?? "object"; }
            catch (ManagementException) { className = "object"; }

            Console.WriteLine($"  out    : {name} = {className}");

            foreach (PropertyData property in embedded.Properties)
            {
                if (property.Value is byte[] bytes)
                {
                    Console.WriteLine($"           {property.Name} = byte[{bytes.Length}]");
                    DumpIndexedBytes(bytes);
                }
                else
                {
                    Console.WriteLine($"           {property.Name} = {Describe(property.Value)}");
                }
            }
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
