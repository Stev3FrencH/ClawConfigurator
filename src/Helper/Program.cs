using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McenterLite.Hardware;
using McenterLite.Hardware.Fake;
using McenterLite.Helper.Ipc;
using McenterLite.Helper.Settings;
using McenterLite.Shared.Ipc;

namespace McenterLite.Helper
{
    /// <summary>
    /// The elevated helper. Owns all hardware access; the widget is a sandboxed view that talks
    /// to it over a named pipe.
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
            Log.Info($"Starting. fakeHardware={options.FakeHardware} dataDir={dataDirectory}");

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
            catch (Exception ex)
            {
                Log.Error("Fatal error", ex);
                return 1;
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
                // Not a fatal error: the widget still connects and explains why every control is
                // disabled. Silently exiting would look like the helper had crashed.
                Log.Warn("Unsupported device. Hardware features are disabled.");
            }

            var dispatcher = new FeatureDispatcher(hardware, settings);

            // Apply persisted settings BEFORE accepting connections, so the widget's first
            // snapshot describes a device already in its intended state.
            StartupApplier.ApplyAll(hardware, settings);

            using var server = new PipeServer(dispatcher.Handle);

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Cancel();

            var telemetry = RunTelemetryLoopAsync(server, dispatcher, hardware, Shutdown.Token);
            var serving = server.RunAsync();

            await Task.WhenAny(serving, telemetry, Task.Delay(Timeout.Infinite, Shutdown.Token))
                      .ConfigureAwait(false);

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

            // TODO(M1): return RealHardware once Phase 0 has established the WMI and HID
            // protocol for this device. Until then the fake keeps every non-hardware layer
            // developable, and reports Supported=false so nothing pretends to work.
            Log.Warn("Real hardware providers are not implemented yet (pending Phase 0 discovery).");
            return new FakeHardware(simulateClaw8Ex: false);
        }

        /// <summary>
        /// Pushes fan telemetry while the widget is on screen.
        /// </summary>
        /// <remarks>
        /// Gated on visibility because the widget is a UWP app that Windows suspends whenever the
        /// Game Bar is dismissed. Polling the EC for a suspended reader would be pure cost on a
        /// battery-powered device.
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
                if (!hardware.Fan.Available) continue;

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
        }
    }

    internal sealed class CommandLineOptions
    {
        /// <summary>Use simulated hardware, so the app can be developed without the device.</summary>
        public bool FakeHardware { get; private set; }

        /// <summary>Deploy the helper and register its scheduled task, then exit. Implemented in M0.</summary>
        public bool Setup { get; private set; }

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
                }
            }

            return options;
        }
    }

    /// <summary>Where the helper keeps its settings and log.</summary>
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

            if (hardware.ChargeLimit.Available && settings.GetBool(SettingsKeys.ChargeLimitEnabled, false))
            {
                int percent = settings.GetInt(SettingsKeys.ChargeLimitPercent, 80);
                var result = hardware.ChargeLimit.Apply(true, percent);
                Log.Info(result.Ok
                    ? $"Re-applied the {percent}% charge limit."
                    : $"Could not re-apply the charge limit: {result.Error}");
            }

            if (hardware.Led.Available)
            {
                var stored = settings.Get(SettingsKeys.LedSpec);
                if (stored != null)
                {
                    var result = hardware.Led.Apply(Shared.Model.LedSpec.Parse(stored));
                    if (!result.Ok) Log.Info($"Could not re-apply the LED settings: {result.Error}");
                }
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
