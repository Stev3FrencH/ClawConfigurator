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
using McenterLite.Shared.Model;

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
                if (options.Uninstall) return RunUninstallRole(options);
                if (options.Restore) return RunRestoreRole(options);

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
            if (options.Restore) return "restore";
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

        /// <summary>
        /// Puts the machine back to <see cref="FeatureDefaults"/>, then removes the task and the
        /// deployed copy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The restore is the point of this role, and until 2026-08-13 it did not happen.</b>
        /// This called <c>RunTeardown</c> and nothing else, while the README promised it "restores
        /// captured system values". The restore existed, but the only way to reach it was the
        /// <c>PrepareForUninstall</c> pipe message, which nothing but the test script ever sent.
        /// </para>
        /// <para>
        /// <b>Run this BEFORE removing the app, not after.</b> The deployed helper and
        /// <c>settings.json</c> both live in the package's LocalCache, so removing the app deletes
        /// the executable this role needs and every setting it would read. The README used to
        /// document the opposite order, which cannot work.
        /// </para>
        /// </remarks>
        private static int RunUninstallRole(CommandLineOptions options)
        {
            if (!Elevation.IsElevated())
            {
                var exitCode = Elevation.RelaunchElevated("--uninstall");
                return exitCode ?? 3;
            }

            return HelperDeployment.RunTeardown(() => RestoreDefaults(options)) ? 0 : 1;
        }

        /// <summary>
        /// Applies every default and stops, leaving the app installed. The uninstall's restore,
        /// runnable on its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Exists because the restore was otherwise untestable.</b> The only other way to reach
        /// it without uninstalling is the <c>PrepareForUninstall</c> pipe message, and the pipe is
        /// <c>maxNumberOfServerInstances: 1</c> — the widget connects on first show and never
        /// disconnects, so on any machine where the Game Bar has been opened since the helper
        /// started, no second client can connect at all. A flow this consequential should not be
        /// verifiable only by performing it.
        /// </para>
        /// <para>
        /// Unlike <c>--uninstall</c> this does not stop the service first, so the widget should be
        /// closed before running it — not for safety, since the service writes only when commanded,
        /// but so the card values on screen are not left disagreeing with the device.
        /// </para>
        /// </remarks>
        private static int RunRestoreRole(CommandLineOptions options)
        {
            if (!Elevation.IsElevated())
            {
                var exitCode = Elevation.RelaunchElevated("--restore");
                return exitCode ?? 3;
            }

            RestoreDefaults(options);
            return 0;
        }

        /// <summary>
        /// Applies every default during uninstall, reporting problems rather than throwing.
        /// </summary>
        /// <remarks>
        /// A failed restore must not abort the teardown. Leaving a scheduled task and a deployed
        /// helper behind because one hardware write failed is strictly worse than an un-restored
        /// setting the user can still change by hand - and a task pointing at a deleted executable
        /// is exactly the debris MSI Center M's own uninstaller left on this machine.
        /// </remarks>
        private static void RestoreDefaults(CommandLineOptions options)
        {
            try
            {
                var hardware = BuildHardware(options);
                var problems = SettingsRestorer.RestoreAll(hardware, Log.Info);

                Log.Info(problems.Count == 0
                    ? "Restored every feature to its default."
                    : "Some values could not be restored: " + string.Join("; ", problems));

                // Writing the hardware is only half of it. The saved choices have to go too, or
                // the next helper start reads them and re-applies everything straight back over
                // the restore - measured on device, six seconds after a successful one.
                var settings = new SettingsStore(AppPaths.ResolveDataDirectory());
                settings.Load();
                settings.ClearFeatureSettings();
            }
            catch (Exception ex)
            {
                Log.Error("Could not restore defaults", ex);
            }
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

            // Seeded before the dispatcher exists, so the very first snapshot can report real
            // profile names rather than placeholders. Creates missing files only; an existing
            // profile is the user's and is never rewritten.
            var lighting = new LightingProfileStore(Path.Combine(dataDirectory, "Lighting"));
            if (hardware.Rgb.Available)
            {
                lighting.EnsureSeeded(Log.Info);
                Log.Info($"Lighting profiles: {lighting.Directory}");
            }

            // Same reasoning as the lighting profiles above: seeded before the dispatcher so the
            // first snapshot carries the real profile name.
            var fans = new FanProfileStore(Path.Combine(dataDirectory, "Fan"));
            if (hardware.Fan.Available)
            {
                fans.EnsureSeeded(Log.Info);
                Log.Info($"Fan profile: {fans.Directory}");
            }

            var dispatcher = new FeatureDispatcher(hardware, settings, lighting, fans);

            // Apply persisted settings BEFORE accepting connections, so the widget's first
            // snapshot describes a device already in its intended state.
            StartupApplier.ApplyAll(hardware, settings, lighting, fans);

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

            // There is one backend now. This line used to name which of two had won, because a
            // silent fall back to the registry mirror was invisible until MSI Center M went - at
            // which point controller mode stopped working and nothing in the log said why. The
            // mirror is deleted; the name is kept because "firmware (vendor HID)" in the log is
            // still what distinguishes a working probe from an unavailable one.
            Log.Info(hardware.HwMouse.Available
                ? $"Controller mode: {DescribeHwMouseBackend(hardware.HwMouse)}."
                : $"Controller mode unavailable: {hardware.HwMouse.UnavailableReason}");

            // The provider probes with a real read, and an unavailable one hides the widget's
            // Battery card entirely - which looks identical to a UI bug from the outside. Say so.
            Log.Info(hardware.ChargeLimit.Available
                ? "Charge limit: MSI_ACPI Get_AP/Set_AP."
                : $"Charge limit unavailable: {hardware.ChargeLimit.UnavailableReason}");

            Log.Info(hardware.Rgb.Available
                ? "Lighting: vendor HID profile block (RAM)."
                : $"Lighting unavailable: {hardware.Rgb.UnavailableReason}");

            Log.Info(hardware.Fan.Available
                ? "Fan control: MSI_ACPI Get_Fan/Set_Fan, two fans."
                : $"Fan control unavailable: {hardware.Fan.UnavailableReason}");

            return hardware;
        }

        private static string DescribeHwMouseBackend(IHwMouseProvider provider) => provider switch
        {
            McenterLite.Hardware.Windows.HidHwMouseProvider => "firmware (vendor HID)",
            _ => provider.GetType().Name,
        };

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
        /// One tick of the telemetry loop, in seconds.
        /// </summary>
        private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How many ticks between fan-control reads. See <see cref="RunTelemetryLoopAsync"/>.
        /// </summary>
        private const int FanTelemetryEveryNTicks = 5;

        /// <summary>
        /// Pushes the OS power mode, the controller mode and the fan-control flag while the widget
        /// is on screen.
        /// </summary>
        /// <remarks>
        /// Gated on visibility because the widget is a UWP app that Windows suspends whenever the
        /// Game Bar is dismissed. Polling for a suspended reader would be pure cost on a
        /// battery-powered device.
        ///
        /// <para>
        /// The power mode is a first-class Windows control - the taskbar battery flyout and
        /// Settings can both change it without this app being involved at all. Without polling it
        /// here the widget only ever learns the mode at connect time, and shows a stale choice
        /// indefinitely once something else changes it. It is an unconditional read-and-send every
        /// tick, relying on the widget's own dedup (it only re-renders a value that actually
        /// changed) rather than tracking "did this change" twice.
        /// </para>
        /// <para>
        /// <b>Not everything here runs at the same rate.</b> The power and controller modes are read
        /// every tick; the fan-control flag is read every fifth. The two rates answer different
        /// questions - the first two are cheap OS-level reads, the fan flag is an ACPI-WMI round
        /// trip to the embedded controller on a battery-powered handheld, and what it is watching
        /// for is a person pressing a button in another app. Five seconds is well inside the time it
        /// takes to notice a change in the fans by ear.
        /// </para>
        /// <para>
        /// This loop once carried fan telemetry of a different kind - live RPM and temperatures at
        /// one second - and lost it in 295f68b when fan control was removed wholesale. It was the
        /// feature that went, not the tick: nothing was ever recorded against the polling itself.
        /// </para>
        /// </remarks>
        private static async Task RunTelemetryLoopAsync(
            PipeServer server,
            FeatureDispatcher dispatcher,
            IHardware hardware,
            CancellationToken token)
        {
            int ticksSinceFanRead = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TelemetryInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!server.IsConnected || !dispatcher.WidgetVisible) continue;

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

                // The controller mode has the same problem as the power mode, only more so: the
                // PHYSICAL MSI button switches it, so it can change with the widget open and this
                // app never told. Without this push the toggle shows whatever was true at connect
                // time and quietly disagrees with the device.
                if (hardware.HwMouse.Available)
                {
                    try
                    {
                        if (hardware.HwMouse.TryRead(out bool desktopMode))
                        {
                            server.Send(PipeEnvelope.Event(
                                Function.HwMouseMode, PipeEnvelope.FromBool(desktopMode)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Controller-mode telemetry read failed: {ex.Message}");
                    }
                }

                // The fan-control flag, on a slower beat - see the rate note above.
                //
                // Same problem as the controller mode, from a different direction: MSI Center M is
                // still installed, owns this same flag, and does not know about us. Without this
                // push the fan card shows whatever was true at connect and the user's only evidence
                // that something took the fans back is the noise.
                //
                // Read from the firmware, never echoed from our own settings - a tick that reports
                // what we last wrote would agree with itself forever and is worse than no tick.
                if (hardware.Fan.Available && ++ticksSinceFanRead >= FanTelemetryEveryNTicks)
                {
                    ticksSinceFanRead = 0;

                    try
                    {
                        if (dispatcher.TryReadFanSelection(out int selection))
                        {
                            server.Send(PipeEnvelope.Event(
                                Function.FanProfile, PipeEnvelope.FromInt(selection)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Fan-control telemetry read failed: {ex.Message}");
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

        /// <summary>
        /// Apply every default and exit, leaving the app installed. What <c>--uninstall</c> does
        /// first, on its own, so the restore can be verified without performing an uninstall.
        /// </summary>
        public bool Restore { get; private set; }

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
                    case "--restore": options.Restore = true; break;
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
        public static void ApplyAll(
            IHardware hardware, SettingsStore settings,
            LightingProfileStore lighting, FanProfileStore fans)
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

            // Charge limit, only if the user has actually set one through this app - GetInt
            // returns -1 when the key was never written, which is the gate.
            //
            // Whether the firmware keeps this across a reboot is UNVERIFIED. Re-applying is cheap
            // insurance either way, and it also wins back the setting if MSI Center M has asserted
            // its own cached value in the meantime - it holds one, and does not notice ours.
            if (hardware.ChargeLimit.Available)
            {
                int percent = settings.GetInt(SettingsKeys.ChargeLimit, -1);
                if (percent > 0)
                {
                    hardware.Caps.ClampChargeLimit(ref percent);
                    var result = hardware.ChargeLimit.Apply(percent);
                    Log.Info(result.Ok
                        ? $"Re-applied charge limit = {percent}%."
                        : $"Could not re-apply the charge limit: {result.Error}");
                }
            }

            // Lighting. Unlike the settings above this is not insurance - it is REQUIRED. The
            // controller keeps lighting in RAM and forgets it on a power cycle, so without this
            // the LEDs come back as whatever the firmware defaults to and the widget's own
            // selection would be a lie. Re-applied even for slot 0, because "off" is a state the
            // user chose and a power cycle would otherwise silently turn the lights back on.
            if (hardware.Rgb.Available)
            {
                int slot = settings.GetInt(SettingsKeys.LightingProfile, -1);

                // First run: no choice recorded yet, so pick one rather than leaving the LEDs on
                // whatever the firmware defaults to while the card claims otherwise. Recorded
                // immediately, so this is a starting point the user then owns rather than a value
                // reasserted on every start.
                bool firstRun = slot < LightingProfileStore.OffSlot || slot > LightingProfileStore.ProfileCount;
                if (firstRun)
                {
                    slot = FeatureDefaults.FirstRunLightingProfile;
                    settings.SetInt(SettingsKeys.LightingProfile, slot);
                }

                var profile = slot == LightingProfileStore.OffSlot
                    ? new LightingProfile { Name = "Off", Style = LightingStyle.Off }
                    : lighting.Load(slot, Log.Warn);

                var result = hardware.Rgb.Apply(LightingRenderer.Render(profile));
                Log.Info(result.Ok
                    ? (firstRun ? "First run: applied " : "Re-applied ")
                      + $"lighting profile {slot} '{profile.Name}'."
                    : $"Could not apply the lighting: {result.Error}");
            }

            // Fan curve. Re-applied for the same reason as the charge limit above, and one more
            // that is specific to fans: anything else that owns the same duty tables - MSI Center M
            // while it was installed, ClawTweaks, Intel's thermal stack - does not know about us. A
            // custom curve one of them overwrote would otherwise stay overwritten until the user
            // noticed the noise.
            //
            // Auto is applied too, not skipped as a no-op. "Auto" here means MSI's factory table
            // AND the control flag cleared, so if something else has written a different table or
            // taken the fans, putting it back is exactly what the user asked for.
            //
            // First run picks Auto rather than writing nothing. The fans are the one feature where
            // "leave whatever is there" can be actively wrong: an install inherits whatever curve
            // and control flag the last owner left behind, and on this device that was a custom
            // table the user could no longer see or change.
            if (hardware.Fan.Available)
            {
                int selection = settings.GetInt(SettingsKeys.FanProfile, -1);

                bool firstRun = selection < 0;
                if (firstRun)
                {
                    selection = FeatureDispatcher.FanAuto;
                    settings.SetInt(SettingsKeys.FanProfile, selection);
                }

                bool custom = selection == FeatureDispatcher.FanCustom;
                var profile = custom ? fans.Load(Log.Warn) : FanProfile.Factory();

                var result = hardware.Fan.Apply(profile, custom);
                Log.Info(result.Ok
                    ? (firstRun ? "First run: applied " : "Re-applied ")
                      + $"fan profile '{profile.Name}'; "
                      + (custom ? "fans follow this table." : "fans left to the firmware.")
                    : $"Could not apply the fan profile: {result.Error}");
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
