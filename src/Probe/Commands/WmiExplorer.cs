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
