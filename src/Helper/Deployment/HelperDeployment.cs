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
        private const string HelperExeName = "ClawConfigurator.Helper.exe";

        /// <summary>
        /// Where the managed code actually lives, and so what has to be compared to detect a
        /// changed build.
        /// </summary>
        /// <remarks>
        /// The <c>.exe</c> beside it is the apphost - a native stub that launches this. It does
        /// not change when C# changes, so comparing it alone reports a code-only update as
        /// "UpToDate" and the stale helper keeps running. Observed on 2026-08-12: a new package
        /// installed, the bootstrap said UpToDate, and the previous build stayed live.
        /// </remarks>
        private const string HelperDllName = "ClawConfigurator.Helper.dll";

        /// <summary>Where the deployed copy lives.</summary>
        public static string DeployedDirectory =>
            Path.Combine(AppPaths.ResolveDataDirectory(), "Helper");

        public static string DeployedExecutable =>
            Path.Combine(DeployedDirectory, HelperExeName);

        /// <summary>
        /// This build's version, for diagnostics only - see <see cref="Evaluate"/> for what
        /// actually decides whether the deployed copy is stale.
        /// </summary>
        /// <remarks>
        /// Not usable for that decision: nothing in this project bumps <c>AssemblyVersion</c>, so
        /// this reports the same "1.0.0.0" for every build regardless of what actually changed.
        /// Relying on it meant the deployed copy was silently never updated past the very first
        /// install - an app update replaced the package, but the already-deployed helper kept
        /// running unchanged because the version string it compared against never moved.
        /// </remarks>
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

            // Both, and the DLL is the one that matters - see HelperDllName. The apphost is
            // checked too because a runtime or publish-settings change can move it without
            // touching the managed code.
            foreach (var name in new[] { HelperDllName, HelperExeName })
            {
                var source = Path.Combine(AppContext.BaseDirectory, name);
                var deployed = Path.Combine(DeployedDirectory, name);

                if (!IsSameBuild(source, deployed))
                {
                    Log.Info($"Deployed copy differs from the packaged build ({name}).");
                    return State.VersionMismatch;
                }
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

        /// <summary>
        /// Removes the task and the deployed copy. Called during uninstall.
        /// </summary>
        /// <param name="afterStop">
        /// Runs once the task is unregistered and any running helper is stopped, but BEFORE
        /// anything is deleted. This is the only moment a restore can safely happen: earlier and the
        /// service is still live and holding the hardware, later and this method has deleted the
        /// executable that would do the work. Failures inside it must not prevent the teardown.
        /// </param>
        public static bool RunTeardown(Action afterStop = null)
        {
            bool ok = ScheduledTaskRegistrar.Unregister();

            try
            {
                // A running instance holds its own binary open, so it must go first.
                StopRunningHelper();

                afterStop?.Invoke();

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

        /// <summary>
        /// Whether the deployed copy is byte-identical to what's in the package right now.
        /// </summary>
        /// <remarks>
        /// Used to be a version-string comparison (see <see cref="CurrentVersion"/>'s remarks for
        /// why that never actually caught an update). Size plus last-write time is enough to catch
        /// a real change without hashing a 60+ MB self-contained executable on every single start -
        /// <c>File.Copy</c> preserves the source timestamp, so a genuinely current deployed copy
        /// matches exactly and a stale one does not.
        ///
        /// <para>
        /// Comparing the right FILE turned out to matter as much as comparing it the right way:
        /// this was pointed at the apphost, which a code-only change never touches. See
        /// <see cref="HelperDllName"/>.
        /// </para>
        /// </remarks>
        private static bool IsSameBuild(string sourcePath, string deployedPath)
        {
            try
            {
                var source = new FileInfo(sourcePath);
                var deployed = new FileInfo(deployedPath);

                return source.Exists && deployed.Exists
                    && source.Length == deployed.Length
                    && source.LastWriteTimeUtc == deployed.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                // Cannot tell - treat as stale. A needless redeploy is cheap; running a stale
                // build silently is not.
                return false;
            }
        }
    }
}
