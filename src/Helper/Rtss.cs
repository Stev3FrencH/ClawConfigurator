using System;
using System.IO;
using System.Runtime.InteropServices;

namespace McenterLite.Helper
{
    /// <summary>
    /// Toggles RivaTuner Statistics Server's on-screen display, without touching the input layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not synthesise RTSS's hotkey.</b> RTSS already has an OSD toggle hotkey and driving it
    /// with <c>SendInput</c> would have been three lines. But an injected keystroke goes into the
    /// system input queue, so the foreground application receives it too, flagged as injected — and
    /// this is triggered by a button pressed while games are running, so the exposure would be
    /// constant rather than occasional. <c>SetFlags</c> reaches RTSS directly and generates no input
    /// at all.
    /// </para>
    /// <para>
    /// <b>This is RTSS's own mechanism.</b> <c>SetFlags</c> is exported by <c>RTSSHooks64.dll</c> and
    /// declared in the SDK header RTSS ships at
    /// <c>SDK\Plugins\Client\HotkeyHandler\RTSSHooksInterface.h</c> — the interface its own hotkey
    /// handler plugin uses. Pressing RTSS's hotkey ends in this same call; the hotkey was only ever
    /// the trigger.
    /// </para>
    /// <code>
    /// DWORD SetFlags(DWORD dwAND, DWORD dwXOR);   // flags = (flags &amp; dwAND) ^ dwXOR
    /// #define RTSSHOOKSFLAG_OSD_VISIBLE 1
    /// </code>
    /// <para>
    /// <b>The DLL is loaded on first use and left loaded.</b> It is a hooking library, designed to be
    /// injected into every hooked game, so its entry point has to be cheap and side-effect-free —
    /// but that is inference from its job, not something measured here. If it ever turns out to bring
    /// machinery into this process that does not belong in a long-running elevated service, the fix
    /// is to make the call in a short-lived child process instead.
    /// </para>
    /// </remarks>
    internal static class Rtss
    {
        private const string DllName = "RTSSHooks64.dll";

        /// <summary>Bit 0 of RTSS's shared flags. From <c>RTSSHooksInterface.h</c>.</summary>
        private const uint FlagOsdVisible = 1;

        /// <summary>AND mask that keeps every flag, so only the XOR does anything.</summary>
        private const uint KeepEverything = 0xFFFFFFFF;

        private static IntPtr _module = IntPtr.Zero;
        private static SetFlagsDelegate _setFlags;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SetFlagsDelegate(uint dwAND, uint dwXOR);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Flips the OSD and reports what it became, or null when RTSS could not be reached.
        /// </summary>
        /// <remarks>
        /// Returns the resulting visibility rather than a bare success, because <c>SetFlags</c>
        /// answers with the modified bitmask — so we can log what the overlay actually is instead of
        /// what we asked for. Same discipline as reading a register back after writing it.
        /// </remarks>
        public static bool? ToggleOverlay(out string error)
        {
            if (!TryBind(out error)) return null;

            try
            {
                uint flags = _setFlags(KeepEverything, FlagOsdVisible);
                return (flags & FlagOsdVisible) != 0;
            }
            catch (Exception ex)
            {
                error = $"{DllName} SetFlags failed: {ex.Message}";
                return null;
            }
        }

        private static bool TryBind(out string error)
        {
            error = null;
            if (_setFlags != null) return true;

            var path = FindDll();
            if (path == null)
            {
                error = "RivaTuner Statistics Server is not installed, or " + DllName + " is missing.";
                return false;
            }

            if (_module == IntPtr.Zero)
            {
                _module = LoadLibraryW(path);
                if (_module == IntPtr.Zero)
                {
                    error = $"Could not load {path}: error {Marshal.GetLastWin32Error()}.";
                    return false;
                }
            }

            var address = GetProcAddress(_module, "SetFlags");
            if (address == IntPtr.Zero)
            {
                error = $"{DllName} does not export SetFlags. RTSS may have changed.";
                return false;
            }

            _setFlags = Marshal.GetDelegateForFunctionPointer<SetFlagsDelegate>(address);
            return true;
        }

        /// <summary>
        /// Locates RTSS from its uninstall entry, falling back to the usual install path.
        /// </summary>
        /// <remarks>
        /// Hardcoding <c>Program Files (x86)</c> would work on this device and quietly fail on one
        /// where RTSS lives elsewhere, in a way that reads as "the button is broken".
        /// </remarks>
        private static string FindDll()
        {
            foreach (var directory in CandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                var path = Path.Combine(directory, DllName);
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static string[] CandidateDirectories()
        {
            string fromRegistry = null;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server_is1");

                    fromRegistry = key?.GetValue("InstallLocation") as string;
                }
            }
            catch (Exception)
            {
                // A registry layout we cannot read is not worth failing over; the fallbacks below
                // cover every installation seen so far.
            }

            return new[]
            {
                fromRegistry,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "RivaTuner Statistics Server"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "RivaTuner Statistics Server"),
            };
        }
    }
}
