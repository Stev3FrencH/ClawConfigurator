using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McenterLite.Hardware;
using McenterLite.Hardware.Fake;
using McenterLite.Helper.Deployment;
using McenterLite.Helper.Ipc;
using McenterLite.Helper.Settings;
using McenterLite.Shared.Ipc;

namespace McenterLite.Helper
{
    /// <summary>
    /// The elevated helper. Owns all hardware access; the widget is a sandboxed view that talks
    /// to it over a named pipe.
    ///
    /// <para>
    /// The same executable plays three roles, distinguished by where it was started from and with
    /// which arguments:
    /// </para>
    /// <list type="number">
    ///   <item><b>Bootstrap</b> - launched from the MSIX package by the widget. Ensures a current
    ///   copy is deployed and the task is registered, then exits. Serves nothing.</item>
    ///   <item><b>Setup</b> (<c>--setup</c>) - the elevated instance that performs the copy and
    ///   registration. This is the one UAC prompt in the product.</item>
    ///   <item><b>Service</b> - launched from the deployed location by the scheduled task.
    ///   Elevated, no package identity, and the only role that touches hardware.</item>
    /// </list>
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Prevents two builds from driving the same embedded controller at once - the failure
        /// this guards is not a corrupted setting but a hard reset.
        /// </summary>
        private const string SingleInstanceMutex = @"Global\McenterLiteHelper_SingleInstance";

        private static readonly CancellationTokenSource Shutdown = new CancellationTokenSource();

        private static int Main(string[] args)
        {
            var options = CommandLineOptions.Parse(args);

            var dataDirectory = AppPaths.ResolveDataDirectory();
            Log.Initialize(dataDirectory);
            Log.Info($"Starting. role={DescribeRole(options)} version={HelperDeployment.CurrentVersion} " +
                     $"elevated={Elevation.IsElevated()} dataDir={dataDirectory}");

            try
            {
                if (options.Setup) return RunSetupRole();
                if (options.Uninstall) return RunUninstallRole();

                // Dev and test runs skip deployment entirely and serve in place, so the whole
                // IPC and UI stack can be exercised without touching persistence or elevation.
                if (options.FakeHardware || options.NoDeploy) return RunServiceRole(options, dataDirectory);

                if (!HelperDeployment.IsRunningFromDeployedLocation()) return RunBootstrapRole();

                return RunServiceRole(options, dataDirectory);
            }
            catch (Exception ex)
            {
                Log.Error("Fatal error", ex);
                return 1;
            }
        }

        private static string DescribeRole(CommandLineOptions options)
        {
            if (options.Setup) return "setup";
            if (options.Uninstall) return "uninstall";
            if (options.FakeHardware || options.NoDeploy) return "service(dev)";
            return HelperDeployment.IsRunningFromDeployedLocation() ? "service" : "bootstrap";
        }

        // ── Bootstrap ───────────────────────────────────────────────────────────

        /// <summary>
        /// Runs from inside the MSIX package, started by the widget. Makes sure a current copy is
        /// deployed and the task points at it, then exits without serving.
        /// </summary>
        /// <remarks>
        /// Serving from here would be wrong twice over: the package directory is read-only and is
        /// replaced wholesale on update, and this instance is not elevated, so every hardware
        /// write would fail with an opaque access-denied.
        /// </remarks>
        private static int RunBootstrapRole()
        {
            var state = HelperDeployment.Evaluate();
            Log.Info($"Deployment state: {state}");

            if (state == HelperDeployment.State.UpToDate)
            {
                // Already deployed and registered; just make sure it is actually running. The
                // widget reconnects on its own once the pipe appears.
                ScheduledTaskRegistrar.Start();
                return 0;
            }

            if (Elevation.IsElevated())
            {
                // Already elevated (a developer running it directly) - no prompt needed.
                return HelperDeployment.RunSetup() ? 0 : 1;
            }

            var exitCode = Elevation.RelaunchElevated("--setup");
            if (exitCode == null)
            {
                // The user declined. Not an error worth retrying: re-prompting in a loop is how
                // an app teaches people to click yes without reading.
                Log.Warn("Setup was declined. The helper will not start until it is allowed.");
                return 3;
            }

            return exitCode.Value;
        }

        // ── Setup / uninstall ───────────────────────────────────────────────────

        private static int RunSetupRole()
        {
            if (!Elevation.IsElevated())
            {
                // Reachable if --setup is invoked by hand. Ask rather than fail obscurely.
                var exitCode = Elevation.RelaunchElevated("--setup");
                return exitCode ?? 3;
            }

            return HelperDeployment.RunSetup() ? 0 : 1;
        }

        private static int RunUninstallRole()
        {
            if (!Elevation.IsElevated())
            {
                var exitCode = Elevation.RelaunchElevated("--uninstall");
                return exitCode ?? 3;
            }

            return HelperDeployment.RunTeardown() ? 0 : 1;
        }

        // ── Service ─────────────────────────────────────────────────────────────

        private static int RunServiceRole(CommandLineOptions options, string dataDirectory)
        {
            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isOnlyInstance);
            if (!isOnlyInstance)
            {
                Log.Warn("Another helper instance already holds the hardware. Exiting.");
                return 2;
            }

            try
            {
                return RunAsync(options, dataDirectory).GetAwaiter().GetResult();
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch (Exception) { }
            }
        }

        private static async Task<int> RunAsync(CommandLineOptions options, string dataDirectory)
        {
            var settings = new SettingsStore(dataDirectory);
            settings.Load();

            IHardware hardware = BuildHardware(options);
            Log.Info($"Device: {hardware.Caps.Model} (supported={hardware.Caps.Supported}, " +
                     $"tdpBackend={hardware.Caps.TdpBackend})");

            if (!hardware.Caps.Supported)
            {
                // Not fatal: the widget still connects and explains why the controls are
                // disabled. Exiting silently would look like a crash.
                Log.Warn("Unsupported device. Hardware features are disabled.");
            }

            var dispatcher = new FeatureDispatcher(hardware, settings);

            // Apply persisted settings BEFORE accepting connections, so the widget's first
            // snapshot describes a device already in its intended state.
            StartupApplier.ApplyAll(hardware, settings);

            using var server = new PipeServer(dispatcher.Handle);

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Cancel();

            var heartbeat = RunHeartbeatLoopAsync(dataDirectory, Shutdown.Token);
            var telemetry = RunTelemetryLoopAsync(server, dispatcher, hardware, Shutdown.Token);
            var serving = server.RunAsync();

            await Task.WhenAny(serving, telemetry, heartbeat,
                               Task.Delay(Timeout.Infinite, Shutdown.Token)).ConfigureAwait(false);

            Log.Info("Shutting down.");
            return 0;
        }

        private static IHardware BuildHardware(CommandLineOptions options)
        {
            if (options.FakeHardware)
            {
                Log.Info("Using simulated hardware. No device will be written to.");
                return new FakeHardware();
            }

            if (!OperatingSystem.IsWindows())
            {
                // Reachable when someone runs the helper on the authoring Mac. Refusing beats
                // throwing an obscure P/Invoke error.
                Log.Warn("Not running on Windows; falling back to simulated hardware.");
                return new FakeHardware(simulateClaw8Ex: false);
            }

            var identity = McenterLite.Hardware.Windows.DeviceDetection.Detect();
            Log.Info($"Detected: {identity.DisplayName}");

            if (!identity.IsClaw8Ex)
            {
                // Not a refusal to run - the Windows power features are device-independent and
                // still work. It is a refusal to write anything model-specific.
                Log.Warn("This is not an MSI Claw 8 EX AI+. Device-specific features are disabled.");
            }

            var hardware = new McenterLite.Hardware.Windows.WindowsHardware(identity);

            Log.Info(hardware.Tdp.Available
                ? $"Power limits: {hardware.Tdp.Backend}."
                : $"Power limits unavailable: {hardware.Tdp.UnavailableReason}");

            return hardware;
        }

        /// <summary>
        /// Touches a heartbeat file so the widget can tell "still starting" from "died".
        /// </summary>
        /// <remarks>
        /// The widget cannot see processes or scheduled tasks from inside its AppContainer, and a
        /// pipe that refuses to open looks identical whether the helper is mid-UAC or crashed.
        /// A file timestamp is the cheapest signal that crosses the sandbox boundary.
        /// </remarks>
        private static async Task RunHeartbeatLoopAsync(string dataDirectory, CancellationToken token)
        {
            var path = Path.Combine(dataDirectory, "heartbeat.txt");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(path,
                        $"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}\n{HelperDeployment.CurrentVersion}\n");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not write the heartbeat: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>
        /// Pushes fan telemetry and the OS power mode while the widget is on screen.
        /// </summary>
        /// <remarks>
        /// Gated on visibility because the widget is a UWP app that Windows suspends whenever the
        /// Game Bar is dismissed. Polling the EC for a suspended reader would be pure cost on a
        /// battery-powered device.
        ///
        /// <para>
        /// The power mode is a first-class Windows control - the taskbar battery flyout and
        /// Settings can both change it without this app being involved at all. Without polling it
        /// here the widget only ever learns the mode at connect time, and shows a stale choice
        /// indefinitely once something else changes it. Pushed the same way as fan telemetry: an
        /// unconditional read-and-send every tick, relying on the widget's own dedup (it only
        /// re-renders a value that actually changed) rather than tracking "did this change" twice.
        /// </para>
        /// </remarks>
        private static async Task RunTelemetryLoopAsync(
            PipeServer server,
            FeatureDispatcher dispatcher,
            IHardware hardware,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!server.IsConnected || !dispatcher.WidgetVisible) continue;

                if (hardware.Fan.Available)
                {
                    try
                    {
                        if (hardware.Fan.TryReadState(out var state))
                            server.Send(PipeEnvelope.Event(Function.FanState, state.Serialize()));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Telemetry read failed: {ex.Message}");
                    }
                }

                if (hardware.Power.Available)
                {
                    try
                    {
                        if (hardware.Power.TryReadPowerMode(out var mode))
                        {
                            server.Send(PipeEnvelope.Event(
                                Function.OsPowerMode,
                                ((int)mode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Power-mode telemetry read failed: {ex.Message}");
                    }
                }
            }
        }
    }

    internal sealed class CommandLineOptions
    {
        /// <summary>Use simulated hardware, so the app can be developed without the device.</summary>
        public bool FakeHardware { get; private set; }

        /// <summary>Perform the elevated deployment and task registration, then exit.</summary>
        public bool Setup { get; private set; }

        /// <summary>Remove the scheduled task and the deployed copy, then exit.</summary>
        public bool Uninstall { get; private set; }

        /// <summary>Serve in place without deploying. For debugging against real hardware.</summary>
        public bool NoDeploy { get; private set; }

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();
            if (args == null) return options;

            foreach (var arg in args)
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--fake-hardware": options.FakeHardware = true; break;
                    case "--setup": options.Setup = true; break;
                    case "--uninstall": options.Uninstall = true; break;
                    case "--no-deploy": options.NoDeploy = true; break;
                }
            }

            return options;
        }
    }

    /// <summary>Where the helper keeps its settings, log and heartbeat.</summary>
    internal static class AppPaths
    {
        /// <summary>
        /// Resolves a writable data directory.
        /// </summary>
        /// <remarks>
        /// When the helper runs from its deployed location it has NO package identity, so WinRT
        /// ApplicationData is unavailable. Deriving the path from the executable location keeps
        /// the widget and helper pointing at the same folder without a WinRT dependency - which
        /// is also what lets this project keep an unversioned TFM and build on macOS.
        /// </remarks>
        public static string ResolveDataDirectory()
        {
            try
            {
                var exeDir = AppContext.BaseDirectory ?? "";

                // Deployed layout: ...\Packages\<PFN>\LocalCache\McenterLite\Helper\
                // Settings belong beside it, one level up from the Helper folder.
                var marker = Path.DirectorySeparatorChar + "LocalCache" + Path.DirectorySeparatorChar;
                int index = exeDir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var localCache = exeDir.Substring(0, index + marker.Length);
                    return Path.Combine(localCache, "McenterLite");
                }

                // Running from inside the package: resolve the package's own LocalCache, so the
                // bootstrap and service roles agree on where settings live.
                var familyName = PackageInterop.GetPackageFamilyName();
                if (!string.IsNullOrEmpty(familyName))
                {
                    var packages = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", familyName, "LocalCache", "McenterLite");
                    return packages;
                }

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(appData))
                    return Path.Combine(appData, "McenterLite");

                return Path.Combine(exeDir, "data");
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), "McenterLite");
            }
        }
    }

    /// <summary>Re-applies persisted settings to the hardware at startup.</summary>
    internal static class StartupApplier
    {
        public static void ApplyAll(IHardware hardware, SettingsStore settings)
        {
            // TDP: the EC forgets across sleep and power-source changes, so this is re-applied
            // rather than assumed to have survived.
            if (hardware.Tdp.Available)
            {
                int pl1 = settings.GetInt(SettingsKeys.Pl1, -1);
                int pl2 = settings.GetInt(SettingsKeys.Pl2, -1);
                if (pl1 > 0 && pl2 > 0)
                {
                    hardware.Caps.ClampPowerLimits(ref pl1, ref pl2);
                    var result = hardware.Tdp.Apply(pl1, pl2);
                    Log.Info(result.Ok
                        ? $"Re-applied PL1={pl1}W PL2={pl2}W."
                        : $"Could not re-apply power limits: {result.Error}");
                }
            }

            if (hardware.Fan.Available && settings.GetBool(SettingsKeys.FanEnabled, false))
            {
                var preset = (FanPreset)settings.GetInt(SettingsKeys.FanPreset, 0);
                var result = hardware.Fan.ApplyPreset(preset);
                Log.Info(result.Ok
                    ? $"Re-applied the {preset} fan preset."
                    : $"Could not re-apply the fan preset: {result.Error}");
            }

            // CPU boost is only re-applied once the user has actually chosen a value. Writing a
            // default here would silently overwrite a system-wide setting we do not own and were
            // never asked to change.
            if (settings.GetBool(SettingsKeys.CpuBoostUserModified, false))
            {
                bool boost = settings.GetBool(SettingsKeys.CpuBoost, false);
                var result = hardware.Power.ApplyCpuBoost(boost);
                Log.Info(result.Ok
                    ? $"Re-applied CPU boost = {boost}."
                    : $"Could not re-apply CPU boost: {result.Error}");
            }

            // The power-mode overlay is deliberately NOT re-applied. It is a first-class Windows
            // control the user can change from the taskbar, and silently reasserting our value on
            // every logon would fight them.
        }
    }
}
