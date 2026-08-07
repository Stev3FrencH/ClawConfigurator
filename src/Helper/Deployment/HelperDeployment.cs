using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace McenterLite.Helper.Deployment
{
    /// <summary>
    /// Copies the helper out of the MSIX package into a writable location and registers the
    /// scheduled task that keeps it running.
    ///
    /// <para><b>Why the helper deploys itself instead of the installer doing it.</b> The
    /// reference project's installer notes that doing this from PowerShell - copy an executable
    /// into LocalAppData, then create a HIGHEST-privilege ONLOGON scheduled task - tripped
    /// Defender's behavioural detection (<c>Behavior:Win32/Persistence.A!ml</c>) and got the
    /// helper quarantined. The same work performed by the signed binary itself, in-process, is
    /// not that pattern.</para>
    ///
    /// <para><b>Why it copies at all.</b> The package directory under <c>WindowsApps</c> is
    /// read-only and is replaced wholesale on update, so a task pointing into it breaks the
    /// moment the app is updated.</para>
    ///
    /// <para>The whole flow costs exactly ONE UAC prompt, on first run and after an update.</para>
    /// </summary>
    internal static class HelperDeployment
    {
        private const string HelperExeName = "McenterLite.Helper.exe";
        private const string VersionFileName = ".version";

        /// <summary>Where the deployed copy lives.</summary>
        public static string DeployedDirectory =>
            Path.Combine(AppPaths.ResolveDataDirectory(), "Helper");

        public static string DeployedExecutable =>
            Path.Combine(DeployedDirectory, HelperExeName);

        /// <summary>This build's version, used to decide whether the deployed copy is stale.</summary>
        public static string CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        /// <summary>Why deployment is or is not needed. Reported to the log and, on failure, the widget.</summary>
        public enum State
        {
            /// <summary>Deployed copy is present, current, and the task points at it.</summary>
            UpToDate,

            /// <summary>Nothing deployed yet.</summary>
            NotDeployed,

            /// <summary>A deployed copy exists but is a different version.</summary>
            VersionMismatch,

            /// <summary>The task is missing or points somewhere else.</summary>
            TaskMissing,
        }

        /// <summary>
        /// Decides whether setup has to run. Cheap and read-only, so it is safe on every start.
        /// </summary>
        public static State Evaluate()
        {
            if (!File.Exists(DeployedExecutable)) return State.NotDeployed;

            var deployedVersion = ReadDeployedVersion();
            if (!string.Equals(deployedVersion, CurrentVersion, StringComparison.Ordinal))
            {
                Log.Info($"Deployed version {deployedVersion ?? "(none)"} differs from {CurrentVersion}.");
                return State.VersionMismatch;
            }

            var registered = ScheduledTaskRegistrar.GetRegisteredExecutable();
            if (string.IsNullOrEmpty(registered) ||
                !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(DeployedExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Task points at {registered ?? "(nothing)"}, expected {DeployedExecutable}.");
                return State.TaskMissing;
            }

            return State.UpToDate;
        }

        /// <summary>True when this process is already running from the deployed location.</summary>
        public static bool IsRunningFromDeployedLocation()
        {
            try
            {
                var current = Environment.ProcessPath;
                if (string.IsNullOrEmpty(current)) return false;

                return string.Equals(
                    Path.GetFullPath(current),
                    Path.GetFullPath(DeployedExecutable),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Performs the deployment. Must already be elevated - this is what <c>--setup</c> runs.
        /// </summary>
        public static bool RunSetup()
        {
            if (!Elevation.IsElevated())
            {
                Log.Error("Setup requires elevation.");
                return false;
            }

            try
            {
                var sourceDirectory = AppContext.BaseDirectory;
                Log.Info($"Deploying {sourceDirectory} -> {DeployedDirectory}");

                // Stop whatever is already running from the target before overwriting it. Two
                // builds touching the same embedded controller at once is the failure the
                // reference project warns can hard-reset the machine.
                StopRunningHelper();

                Directory.CreateDirectory(DeployedDirectory);
                CopyPayload(sourceDirectory, DeployedDirectory);

                File.WriteAllText(Path.Combine(DeployedDirectory, VersionFileName), CurrentVersion);

                if (!ScheduledTaskRegistrar.Register(DeployedExecutable)) return false;
                if (!ScheduledTaskRegistrar.Start())
                {
                    // Registration succeeded, so the next logon will start it. Worth a warning,
                    // not a failure.
                    Log.Warn("The task was registered but could not be started now.");
                }

                Log.Info("Setup complete.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Setup failed", ex);
                return false;
            }
        }

        /// <summary>Removes the task and the deployed copy. Called during uninstall.</summary>
        public static bool RunTeardown()
        {
            bool ok = ScheduledTaskRegistrar.Unregister();

            try
            {
                // A running instance holds its own binary open, so it must go first.
                StopRunningHelper();

                if (Directory.Exists(DeployedDirectory))
                {
                    Directory.Delete(DeployedDirectory, recursive: true);
                    Log.Info("Removed the deployed helper.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not remove the deployed helper", ex);
                ok = false;
            }

            return ok;
        }

        private static void CopyPayload(string source, string destination)
        {
            // Copy everything: a self-contained single-file build is one executable, but a
            // framework-dependent or debug build brings dependencies alongside it, and a partial
            // copy fails at startup in a way that is tedious to diagnose.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            Log.Info($"Copied {Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count()} files.");
        }

        /// <summary>
        /// Stops any helper already running from the deployed location and waits for it to exit.
        /// </summary>
        private static void StopRunningHelper()
        {
            try
            {
                var self = Environment.ProcessId;
                var name = Path.GetFileNameWithoutExtension(HelperExeName);

                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        if (process.Id == self) continue;

                        Log.Info($"Stopping helper process {process.Id}.");

                        try
                        {
                            process.Kill();

                            // Bounded wait: the file must actually be closed before the copy, or
                            // the copy fails with a sharing violation.
                            if (!process.WaitForExit(10_000))
                                Log.Warn($"Process {process.Id} did not exit within 10s.");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Could not stop process {process.Id}: {ex.Message}");
                        }
                    }
                }

                // Windows releases the file handle slightly after the process disappears.
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not enumerate helper processes: {ex.Message}");
            }
        }

        private static string ReadDeployedVersion()
        {
            try
            {
                var path = Path.Combine(DeployedDirectory, VersionFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
