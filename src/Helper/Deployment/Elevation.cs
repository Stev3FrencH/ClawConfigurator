using System;
using System.Diagnostics;
using System.Security.Principal;

namespace McenterLite.Helper.Deployment
{
    /// <summary>Elevation checks and the one relaunch that asks the user for it.</summary>
    internal static class Elevation
    {
        /// <summary>
        /// True when this process actually holds administrator rights.
        /// </summary>
        /// <remarks>
        /// Checks the token, not whether the process was "launched as admin" - the two differ,
        /// and a helper that believes it is elevated when it is not fails later at a WMI write
        /// with an opaque access-denied rather than here where it can ask.
        /// </remarks>
        public static bool IsElevated()
        {
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not determine elevation: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Relaunches this executable elevated with the given arguments and waits for it.
        /// </summary>
        /// <remarks>
        /// This is the ONLY UAC prompt in the product, and it happens once - on first run or after
        /// an update. Everything afterwards is started elevated by the scheduled task.
        /// </remarks>
        /// <returns>The child's exit code, or null when the user declined the prompt.</returns>
        public static int? RelaunchElevated(string arguments)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Error("Cannot relaunch: the executable path is unknown.");
                return null;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,

                // Both are required for the elevation prompt: ShellExecute is what understands
                // the runas verb, and CreateProcess cannot elevate at all.
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            try
            {
                Log.Info($"Requesting elevation: {exePath} {arguments}");

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    Log.Error("Elevated relaunch returned no process.");
                    return null;
                }

                process.WaitForExit();
                Log.Info($"Elevated instance exited with code {process.ExitCode}.");
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED - the user said no. A legitimate answer, not a failure: the
                // widget shows an explanation rather than retrying and re-prompting.
                Log.Warn("The user declined the elevation prompt.");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Elevated relaunch failed", ex);
                return null;
            }
        }
    }
}
