using System;
using Microsoft.Win32.TaskScheduler;

namespace McenterLite.Helper.Deployment
{
    /// <summary>
    /// Registers the logon task that starts the helper elevated.
    ///
    /// <para>
    /// Registration goes through the Task Scheduler COM API and spawns NO child processes. The
    /// reference project shells out to <c>schtasks.exe</c> and then twice more to
    /// <c>powershell.exe</c> to adjust settings, and documents that this pattern got its helper
    /// quarantined by Defender as <c>Behavior:Win32/Persistence.A!ml</c>. An elevated,
    /// self-signed binary launching PowerShell to create a HIGHEST-privilege ONLOGON task is
    /// close to the textbook description of persistence malware. This does the same work as a
    /// single in-process API call.
    /// </para>
    /// </summary>
    internal static class ScheduledTaskRegistrar
    {
        public const string TaskFolder = "McenterLite";
        public const string TaskName = "McenterLiteHelper";
        public const string FullTaskPath = @"\" + TaskFolder + @"\" + TaskName;

        /// <summary>
        /// Creates or updates the task so it launches <paramref name="executablePath"/> elevated
        /// at logon. Idempotent.
        /// </summary>
        public static bool Register(string executablePath)
        {
            if (!OperatingSystem.IsWindows())
            {
                Log.Warn("Task registration needs Windows.");
                return false;
            }

            try
            {
                using var service = new TaskService();

                var definition = service.NewTask();

                definition.RegistrationInfo.Description =
                    "Starts the msi-mcenter-lite helper, which owns hardware access for the Game Bar widget.";
                definition.RegistrationInfo.Author = "msi-mcenter-lite";

                // Elevated, because every hardware path needs it: ACPI-WMI writes, the EC, and
                // HKLM. This is the whole reason the task exists rather than a Run key.
                definition.Principal.RunLevel = TaskRunLevel.Highest;
                definition.Principal.LogonType = TaskLogonType.InteractiveToken;

                // Small delay so we are not competing with the rest of logon for the WMI and HID
                // stacks we immediately want to talk to.
                definition.Triggers.Add(new LogonTrigger
                {
                    Delay = TimeSpan.FromSeconds(5),
                    Enabled = true,
                });

                definition.Actions.Add(new ExecAction(
                    $"\"{executablePath}\"",
                    null,
                    System.IO.Path.GetDirectoryName(executablePath)));

                // This is a handheld. Every one of these defaults is wrong for a device that
                // spends its life on battery and asleep.
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;

                // A long-running service, not a job: never time it out.
                definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                // The single-instance mutex already guards the hardware, but refusing a second
                // start here keeps two builds from ever racing for the EC.
                definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

                definition.Settings.StartWhenAvailable = true;
                definition.Settings.RunOnlyIfIdle = false;
                definition.Settings.IdleSettings.StopOnIdleEnd = false;
                definition.Settings.Enabled = true;
                definition.Settings.Hidden = false;

                var folder = GetOrCreateFolder(service, TaskFolder);
                folder.RegisterTaskDefinition(
                    TaskName,
                    definition,
                    TaskCreation.CreateOrUpdate,
                    userId: null,
                    password: null,
                    TaskLogonType.InteractiveToken);

                Log.Info($"Registered the scheduled task {FullTaskPath} -> {executablePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not register the scheduled task", ex);
                return false;
            }
        }

        /// <summary>Returns the task's current executable path, or null when it is not registered.</summary>
        public static string GetRegisteredExecutable()
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                using var service = new TaskService();
                using var task = service.GetTask(FullTaskPath);
                if (task == null) return null;

                foreach (var action in task.Definition.Actions)
                {
                    if (action is ExecAction exec)
                        return exec.Path?.Trim('"');
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the scheduled task: {ex.Message}");
                return null;
            }
        }

        public static bool Start()
        {
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                using var service = new TaskService();
                using var task = service.GetTask(FullTaskPath);
                if (task == null)
                {
                    Log.Warn("Cannot start: the task is not registered.");
                    return false;
                }

                task.Run();
                Log.Info("Started the helper task.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the task", ex);
                return false;
            }
        }

        /// <summary>Removes the task, and the folder once it is empty. Used by uninstall.</summary>
        public static bool Unregister()
        {
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                using var service = new TaskService();

                var folder = service.GetFolder(@"\" + TaskFolder);
                if (folder == null) return true; // already gone

                folder.DeleteTask(TaskName, exceptionOnNotExists: false);

                // Leaving an empty folder behind in Task Scheduler is untidy for anyone
                // inspecting the machine afterwards.
                if (folder.Tasks.Count == 0 && folder.SubFolders.Count == 0)
                    service.RootFolder.DeleteFolder(TaskFolder, exceptionOnNotExists: false);

                Log.Info("Unregistered the scheduled task.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not unregister the task", ex);
                return false;
            }
        }

        private static TaskFolder GetOrCreateFolder(TaskService service, string name)
        {
            var existing = service.GetFolder(@"\" + name);
            if (existing != null) return existing;

            return service.RootFolder.CreateFolder(name, exceptionOnExists: false);
        }
    }
}
