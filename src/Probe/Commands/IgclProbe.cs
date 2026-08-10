using System;
using System.Runtime.InteropServices;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Brings up the Intel Graphics Control Library and reports what it sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Gate G6's first three checkboxes, answered on real hardware rather than assumed:
    /// <c>ControlLib.dll</c> present, <c>ctlInit</c> succeeds, and an Intel adapter is enumerated
    /// with <c>pci_vendor_id == 0x8086</c>.
    /// </para>
    /// <para>
    /// <b>Read-only and deliberately narrow.</b> It initialises, enumerates and closes. It does not
    /// call <c>ctlGetSet3DFeature</c>, because the capability structs carry a
    /// <c>ctl_3d_feature_details_t*</c> array whose element size depends on nested property-info
    /// unions this has not verified against the header. Getting that size wrong walks a pointer off
    /// the end of a driver-allocated array - the wrong kind of guess to make against a live GPU
    /// driver, and this project's rule is that "probably" never belongs in a hardware call.
    /// </para>
    /// <para>
    /// Every struct below is laid out to match igcl_api.h as published by Intel. The one that
    /// matters is <see cref="CtlInitArgs"/>: its <c>Size</c> field is how the driver version-checks
    /// the caller, so a wrong size is rejected rather than misread - which is the failure mode we
    /// want, and why this is safe to try.
    /// </para>
    /// </remarks>
    internal static class IgclProbe
    {
        private const string ControlLib = "ControlLib.dll";

        // CTL_MAKE_VERSION(major, minor) = (major << 16) | (minor & 0xFFFF). The header ships
        // CTL_IMPL_MAJOR_VERSION 1 / CTL_IMPL_MINOR_VERSION 1.
        private const uint ImplVersion = (1u << 16) | 1u;

        [StructLayout(LayoutKind.Sequential)]
        private struct CtlApplicationId
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data4;
        }

        /// <remarks>
        /// Field order is exactly the header's. <c>Version</c> is a uint8 followed by a uint32, so
        /// the compiler inserts three bytes of padding - Pack is left at the default precisely so
        /// that natural alignment matches what the C compiler did when the driver was built.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlInitArgs
        {
            public uint Size;
            public byte Version;
            public uint AppVersion;
            public uint Flags;
            public uint SupportedVersion;
            public CtlApplicationId ApplicationUID;
        }

        [DllImport(ControlLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ctlInit(ref CtlInitArgs pInitDesc, out IntPtr phAPIHandle);

        [DllImport(ControlLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ctlClose(IntPtr hAPIHandle);

        [DllImport(ControlLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ctlEnumerateDevices(IntPtr hAPIHandle, ref uint pCount, IntPtr phDevices);

        /// <summary>
        /// The union behind <c>ctl_property_t</c>, sized to its widest member.
        /// </summary>
        /// <remarks>
        /// Members are bool(1), {bool,float}(8), {bool,int32}(8), uint32(4), {bool,uint32}(8), so
        /// eight bytes at four-byte alignment covers all of them. Declared explicitly rather than
        /// as a real union because every member this probe reads is either the enum's uint32 or
        /// the int32 value, both of which sit at the same offsets.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct CtlPropertyValue
        {
            public uint EnableOrType;   // bool Enable, or uint32 EnableType for the enum form
            public int Value;           // int32/uint32/float payload where the type has one
        }

        /// <remarks>
        /// Mirrors <c>ctl_3d_feature_getset_t</c>. Like <see cref="CtlInitArgs"/> this leads with a
        /// <c>Size</c> the driver validates, which is what makes it safe to try: a layout mistake
        /// is rejected as UNSUPPORTED_SIZE rather than silently misread.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct Ctl3DFeatureGetSet
        {
            public uint Size;
            public byte Version;
            public int FeatureType;
            public IntPtr ApplicationName;   // NULL = global rather than per-application
            public int ValueType;
            public CtlPropertyValue Value;
            public int CustomValueSize;
            public IntPtr pCustomValue;
        }

        [DllImport(ControlLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ctlGetSet3DFeature(IntPtr hDAhandle, ref Ctl3DFeatureGetSet pFeature);

        /// <summary>Every ctl_3d_feature_t, from igcl_api.h.</summary>
        private static readonly (int Id, string Name)[] Features =
        {
            (0, "FRAME_PACING"), (1, "ENDURANCE_GAMING"), (2, "FRAME_LIMIT"),
            (3, "ANISOTROPIC"), (4, "CMAA"), (5, "TEXTURE_FILTERING_QUALITY"),
            (6, "ADAPTIVE_TESSELLATION"), (7, "SHARPENING_FILTER"), (8, "MSAA"),
            (9, "GAMING_FLIP_MODES"), (10, "ADAPTIVE_SYNC_PLUS"), (11, "APP_PROFILES"),
            (12, "APP_PROFILE_DETAILS"), (13, "EMULATED_TYPED_64BIT_ATOMICS"),
            (14, "VRR_WINDOWED_BLT"), (15, "GLOBAL_OR_PER_APP"), (16, "LOW_LATENCY"),
            (17, "FRAME_GENERATION"), (18, "PREBUILT_SHADER_DOWNLOAD"), (19, "LIVE_STATE"),
        };

        private static readonly string[] ValueTypeNames =
        {
            "bool", "float", "int32", "uint32", "enum", "custom",
        };

        public static int Run(string[] commandArgs = null)
        {
            // Optional executable name. NULL means query the GLOBAL scope, which is what a
            // per-application-only feature reports DATA_NOT_FOUND for.
            _applicationName = commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] : null;

            Console.WriteLine("Intel Graphics Control Library probe");
            Console.WriteLine("====================================");
            Console.WriteLine();

            if (!ReportLibrary()) return 1;

            var args = new CtlInitArgs
            {
                Size = (uint)Marshal.SizeOf<CtlInitArgs>(),
                Version = 0,
                AppVersion = ImplVersion,
                Flags = 0,
                SupportedVersion = 0,
                ApplicationUID = new CtlApplicationId { Data4 = new byte[8] },
            };

            Console.WriteLine($"ctl_init_args_t size: {args.Size} bytes");

            IntPtr api;
            int result;
            try
            {
                result = ctlInit(ref args, out api);
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine("ControlLib.dll could not be loaded. No Intel graphics driver?");
                return 1;
            }
            catch (EntryPointNotFoundException)
            {
                Console.WriteLine("ctlInit is missing from ControlLib.dll - unexpectedly old driver.");
                return 1;
            }

            if (result != 0)
            {
                Console.WriteLine($"ctlInit FAILED: {Describe(result)}");
                Console.WriteLine();
                Console.WriteLine("G6 is blocked here. Everything downstream needs this handle.");
                return 1;
            }

            Console.WriteLine($"ctlInit OK. Driver reports supported version {Version(args.SupportedVersion)}.");
            Console.WriteLine();

            try
            {
                EnumerateAdapters(api);
            }
            finally
            {
                ctlClose(api);
                Console.WriteLine();
                Console.WriteLine("ctlClose done.");
            }

            return 0;
        }

        private static bool ReportLibrary()
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "ControlLib.dll");

            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"ControlLib.dll NOT FOUND at {path}.");
                Console.WriteLine("It ships with the Intel graphics driver, so this machine has none.");
                return false;
            }

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            Console.WriteLine($"ControlLib.dll  {info.FileVersion}");
            Console.WriteLine($"  {path}");
            Console.WriteLine();
            return true;
        }

        /// <remarks>
        /// Two-call idiom, which is how the whole IGCL enumeration surface works: pass a null
        /// buffer to learn the count, then allocate and call again. Passing a guessed count the
        /// first time is how you get a truncated list and never notice.
        /// </remarks>
        private static void EnumerateAdapters(IntPtr api)
        {
            uint count = 0;

            int result = ctlEnumerateDevices(api, ref count, IntPtr.Zero);
            if (result != 0)
            {
                Console.WriteLine($"ctlEnumerateDevices (count) FAILED: {Describe(result)}");
                return;
            }

            Console.WriteLine($"Adapters reported: {count}");

            if (count == 0)
            {
                Console.WriteLine("No Intel adapter. On a hybrid machine check that the iGPU is enabled.");
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            try
            {
                result = ctlEnumerateDevices(api, ref count, buffer);
                if (result != 0)
                {
                    Console.WriteLine($"ctlEnumerateDevices (fetch) FAILED: {Describe(result)}");
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    IntPtr handle = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                    Console.WriteLine($"  [{i}] adapter handle 0x{handle.ToInt64():X}");
                    Query3DFeatures(handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Asks the driver about each 3D feature individually.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One GET per feature rather than <c>ctlGetSupported3DCapabilities</c>, which is the
        /// documented way to enumerate them. That call hands back a driver-allocated
        /// <c>ctl_3d_feature_details_t</c> ARRAY, and stepping through it needs the element size
        /// exactly right - it depends on nested property-info unions and their range structs, none
        /// of which this has verified. Wrong stride, and the pointer walks off the end.
        /// </para>
        /// <para>
        /// This route has no array to index, and <c>ctl_3d_feature_getset_t</c> leads with a
        /// <c>Size</c> the driver checks - so a layout error surfaces as UNSUPPORTED_SIZE on the
        /// first call rather than as garbage or a fault. Strictly less information (no ranges, no
        /// per-app support flags) in exchange for being safe to run against a live driver.
        /// </para>
        /// <para>
        /// A feature the driver does not implement answers NOT_SUPPORTED, which is exactly the
        /// question being asked.
        /// </para>
        /// </remarks>
        private static string _applicationName;

        private static void Query3DFeatures(IntPtr adapter)
        {
            string scope = _applicationName == null
                ? "global scope (ApplicationName = NULL)"
                : $"per-application scope (\"{_applicationName}\")";

            Console.WriteLine();
            Console.WriteLine($"      3D features (one GET each, {scope}):");
            Console.WriteLine($"      ctl_3d_feature_getset_t size: {Marshal.SizeOf<Ctl3DFeatureGetSet>()} bytes");
            Console.WriteLine();

            // Marshalled once and freed once, rather than per call: the driver only reads it.
            IntPtr appName = _applicationName == null
                ? IntPtr.Zero
                : Marshal.StringToHGlobalAnsi(_applicationName);

            try
            {
                int supported = 0;

                foreach (var feature in Features)
                {
                    var request = new Ctl3DFeatureGetSet
                    {
                        Size = (uint)Marshal.SizeOf<Ctl3DFeatureGetSet>(),
                        Version = 0,
                        FeatureType = feature.Id,
                        ApplicationName = appName,
                        ValueType = 0,
                        Value = default,
                        CustomValueSize = 0,
                        pCustomValue = IntPtr.Zero,
                    };

                    int result;
                    try
                    {
                        result = ctlGetSet3DFeature(adapter, ref request);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"        {feature.Name,-30} THREW: {ex.GetType().Name}");
                        continue;
                    }

                    if (result == 0)
                    {
                        supported++;
                        string type = request.ValueType >= 0 && request.ValueType < ValueTypeNames.Length
                            ? ValueTypeNames[request.ValueType]
                            : $"type {request.ValueType}";

                        Console.WriteLine(
                            $"        {feature.Name,-30} SUPPORTED  ({type}, " +
                            $"enable/type={request.Value.EnableOrType}, value={request.Value.Value})");
                    }
                    else
                    {
                        Console.WriteLine($"        {feature.Name,-30} {Describe(result)}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"      {supported} of {Features.Length} features readable at this scope.");

                if (_applicationName == null)
                {
                    Console.WriteLine();
                    Console.WriteLine("      DATA_NOT_FOUND is not the same as UNSUPPORTED_FEATURE. It means the");
                    Console.WriteLine("      driver knows the feature but has nothing at GLOBAL scope - which is");
                    Console.WriteLine("      what a per-application feature looks like from here. Re-run with an");
                    Console.WriteLine("      executable name to test that:   probe igcl game.exe");
                }
            }
            finally
            {
                if (appName != IntPtr.Zero) Marshal.FreeHGlobal(appName);
            }
        }

        private static string Version(uint packed) => $"{packed >> 16}.{packed & 0xFFFF}";

        /// <summary>
        /// ctl_result_t, taken from igcl_api.h.
        /// </summary>
        /// <remarks>
        /// The two that matter when reading the feature sweep say DIFFERENT things, and conflating
        /// them would lead the UI design astray:
        ///
        ///   UNSUPPORTED_FEATURE  the driver does not implement it here at all.
        ///   DATA_NOT_FOUND       the driver knows the feature, but has nothing for the scope that
        ///                        was asked - which for a NULL ApplicationName means global.
        /// </remarks>
        private static string Describe(int result)
        {
            switch (result)
            {
                case 0x00000000: return "SUCCESS";
                case 0x40000001: return "NOT_INITIALIZED";
                case 0x40000003: return "DEVICE_LOST";
                case 0x40000006: return "INSUFFICIENT_PERMISSIONS";
                case 0x40000007: return "NOT_AVAILABLE";
                case 0x40000008: return "UNINITIALIZED";
                case 0x40000009: return "UNSUPPORTED_VERSION";
                case 0x4000000A: return "UNSUPPORTED_FEATURE  (driver does not implement it)";
                case 0x4000000B: return "INVALID_ARGUMENT";
                case 0x4000000C: return "INVALID_API_HANDLE";
                case 0x4000000D: return "INVALID_NULL_HANDLE";
                case 0x4000000E: return "INVALID_NULL_POINTER";
                case 0x4000000F: return "INVALID_SIZE";
                case 0x40000010: return "UNSUPPORTED_SIZE  (struct layout mismatch)";
                case 0x40000012: return "DATA_READ";
                case 0x40000013: return "DATA_WRITE";
                case 0x40000014: return "DATA_NOT_FOUND  (known, but nothing at this scope)";
                case 0x40000015: return "NOT_IMPLEMENTED";
                case 0x40000016: return "OS_CALL";
                case 0x40000017: return "KMD_CALL";
                case 0x4000001A: return "INVALID_OPERATION_TYPE";
                case 0x4000001F: return "PERSISTANCE_NOT_SUPPORTED";
                case 0x40000020: return "PLATFORM_NOT_SUPPORTED";
                default: return $"0x{result:X8}";
            }
        }
    }
}
