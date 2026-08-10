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

        public static int Run()
        {
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
                }

                Console.WriteLine();
                Console.WriteLine("G6 checkboxes 1-3 are answered: library present, ctlInit succeeds,");
                Console.WriteLine("adapters enumerate. Per-feature support (ctlGetSupported3DCapabilities)");
                Console.WriteLine("is NOT probed here - see the class remarks for why.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string Version(uint packed) => $"{packed >> 16}.{packed & 0xFFFF}";

        /// <summary>The handful of ctl_result_t values worth naming; anything else prints raw.</summary>
        private static string Describe(int result)
        {
            switch (result)
            {
                case 0x00000000: return "SUCCESS";
                case 0x40000000: return "NOT_SUPPORTED (0x40000000)";
                case 0x40000001: return "NOT_IMPLEMENTED";
                case 0x4000FFFF: return "UNKNOWN";
                case 0x7800000B: return "INVALID_NULL_POINTER";
                case 0x7800000C: return "INVALID_SIZE";
                case 0x7800000D: return "UNSUPPORTED_SIZE (struct layout mismatch)";
                case 0x7800000E: return "UNSUPPORTED_VERSION";
                case 0x78000012: return "INVALID_ARGUMENT";
                case 0x78000013: return "INVALID_API_HANDLE";
                case 0x7800001A: return "CORE_OVERCLOCK_NOT_SUPPORTED";
                default: return $"0x{result:X8}";
            }
        }
    }
}
