using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Enumerates the <c>root\WMI</c> namespace, which is where ACPI-WMI methods surface.
    ///
    /// <para>
    /// The important column is the <c>guid</c> CLASS QUALIFIER: it is the same GUID that appears
    /// in the firmware's <c>_WDG</c> table, so it is the bridge from a disassembled DSDT method
    /// to a class name that can actually be called. Read-only.
    /// </para>
    /// </summary>
    internal static class WmiExplorer
    {
        public static int ListClasses(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            var filter = args.Length > 0 ? args[0] : null;

            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            var query = new ManagementObjectSearcher(scope,
                new WqlObjectQuery("SELECT * FROM meta_class"),
                new EnumerationOptions { EnumerateDeep = true });

            var rows = new List<(string Name, string Guid, string Methods)>();

            foreach (ManagementClass cls in query.Get())
            {
                string name;
                try { name = cls["__CLASS"] as string ?? ""; }
                catch (Exception) { continue; }

                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string guid = "";
                try
                {
                    foreach (QualifierData q in cls.Qualifiers)
                    {
                        if (string.Equals(q.Name, "guid", StringComparison.OrdinalIgnoreCase))
                        {
                            guid = q.Value?.ToString() ?? "";
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Some classes expose no qualifiers; not an error.
                }

                string methods = "";
                try
                {
                    methods = string.Join(",", cls.Methods.Cast<MethodData>().Select(m => m.Name));
                }
                catch (Exception)
                {
                    // Likewise for methods.
                }

                rows.Add((name, guid, methods));
            }

            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(row.Name);
                if (row.Guid.Length > 0) Console.WriteLine($"    guid    : {row.Guid}");
                if (row.Methods.Length > 0) Console.WriteLine($"    methods : {row.Methods}");
            }

            Console.WriteLine();
            Console.WriteLine($"{rows.Count} classes" + (filter != null ? $" matching '{filter}'" : ""));
            Console.WriteLine();
            Console.WriteLine("Classes carrying a 'guid' qualifier are ACPI-WMI. Match that GUID against the");
            Console.WriteLine("_WDG table from --dump-acpi to find which firmware method a class invokes.");
            Console.WriteLine("Try:  --wmi-classes MSI     --wmi-classes AckSys     --wmi-classes MS_");

            return 0;
        }

        /// <summary>
        /// Dumps one method's declared parameter schema, WITHOUT calling it.
        /// </summary>
        /// <remarks>
        /// <c>--wmi-classes</c> only lists method names; it says nothing about what a method takes
        /// or returns. ACPI-WMI wrapper classes (like <c>MSI_ACPI</c>) generate their in/out
        /// parameter shape from the DSDT method they front, so it has to be read off the class
        /// metadata rather than guessed - a guessed argument count or type is a guess about what
        /// gets written to the embedded controller.
        /// </remarks>
        public static int DescribeMethod(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: --wmi-method <ClassName> <MethodName>");
                return 64;
            }

            var className = args[0];
            var methodName = args[1];

            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            using var cls = new ManagementClass(scope, new ManagementPath(className), null);
            try
            {
                cls.Get();
            }
            catch (ManagementException ex)
            {
                Console.Error.WriteLine($"Could not read class {className}: {ex.Message}");
                return 1;
            }

            MethodData method = null;
            foreach (MethodData m in cls.Methods)
            {
                if (string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                {
                    method = m;
                    break;
                }
            }

            if (method == null)
            {
                Console.Error.WriteLine($"{className} has no method named {methodName}.");
                return 1;
            }

            Console.WriteLine($"{className}.{method.Name}");
            DumpParameters("IN ", method.InParameters);
            DumpParameters("OUT", method.OutParameters);

            return 0;
        }

        /// <summary>
        /// Calls a READ-ONLY <c>MSI_ACPI</c> method and dumps its output buffer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <c>Get_</c> prefix check below is a real safety boundary, not a naming nicety.
        /// <c>MSI_ACPI</c> exposes <c>Set_EC</c>, which writes a raw byte to an arbitrary embedded
        /// controller address - a wrong address there lands on fan or thermal registers of real
        /// firmware. Writes are deliberately confined to one purpose-built method, in
        /// <see cref="BatteryInfo"/>, where the firmware validates the value. Nothing reachable
        /// from this command can write.
        /// </para>
        /// <para>
        /// Reading is unrestricted because it is safe and because finding which address or index
        /// carries a value is the whole job.
        /// </para>
        /// </remarks>
        public static int AcpiGet(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            if (args.Length < 1)
            {
                Console.Error.WriteLine(
                    "Usage: --acpi-get <Get_Method> [bytes...]\n" +
                    "  e.g. --acpi-get Get_MasterBattery\n" +
                    "       --acpi-get Get_EC 0xD7");
                return 64;
            }

            var methodName = args[0];

            if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"Refusing to call '{methodName}': this command only calls Get_* methods.\n" +
                    "Every write has its own named command with its own argument checking, so a\n" +
                    "raw Set_EC is deliberately not reachable from here.");
                return 64;
            }

            if (!MsiAcpi.TryParseBytes(args.Skip(1).ToArray(), out var payload, out var parseError))
            {
                Console.Error.WriteLine(parseError);
                return 64;
            }

            var instance = MsiAcpi.TryGetInstance(out var error);
            if (instance == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            using (instance)
            {
                Console.WriteLine($"{MsiAcpi.ClassName}.{methodName}");

                var result = MsiAcpi.Invoke(instance, methodName, payload, out error);
                if (result == null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                MsiAcpi.DumpResult(result);
            }

            return 0;
        }

        private static void DumpParameters(string label, ManagementBaseObject parameters)
        {
            if (parameters == null)
            {
                Console.WriteLine($"  {label}: (none)");
                return;
            }

            bool any = false;
            foreach (PropertyData p in parameters.Properties)
            {
                any = true;
                string quals;
                try
                {
                    quals = string.Join(",", p.Qualifiers.Cast<QualifierData>()
                        .Select(q => $"{q.Name}={q.Value}"));
                }
                catch (Exception)
                {
                    quals = "";
                }

                Console.WriteLine($"  {label}: {p.Name} : {p.Type}"
                    + (quals.Length > 0 ? $"  [{quals}]" : ""));
            }

            if (!any) Console.WriteLine($"  {label}: (none)");
        }

        public static int DumpInstances(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This command needs Windows.");
                return 1;
            }

            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: --wmi-instances <ClassName>");
                return 64;
            }

            var className = args[0];
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT * FROM {className}"));

            int index = 0;
            foreach (ManagementObject instance in searcher.Get())
            {
                Console.WriteLine($"[instance {index++}]");

                foreach (PropertyData property in instance.Properties)
                {
                    var rendered = property.Value switch
                    {
                        null => "(null)",
                        byte[] bytes => $"byte[{bytes.Length}] {string.Join(",", bytes)}",
                        Array array => $"array[{array.Length}]",
                        _ => property.Value.ToString(),
                    };

                    Console.WriteLine($"  {property.Name} = {rendered}");
                }

                Console.WriteLine();
            }

            if (index == 0) Console.WriteLine("(no instances)");
            return 0;
        }
    }
}
