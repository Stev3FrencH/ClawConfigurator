using System;
using System.Threading.Tasks;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
using McenterLite.Widget.Ipc;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.FullTrustProcess;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace McenterLite.Widget
{
    /// <summary>
    /// The widget UI.
    ///
    /// <para>
    /// A pure view over helper state. It persists nothing and assumes nothing: controls are
    /// populated from the helper's snapshot, and every write adopts the value the helper reports
    /// back rather than the value that was requested.
    /// </para>
    /// </summary>
    public sealed partial class MainWidget : Page
    {
        private readonly HelperConnection _connection = new HelperConnection();
        private XboxGameBarWidget _widget;

        /// <summary>
        /// Suppresses control events while the UI is being populated from helper state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ONE flag, checked at the top of every handler, set around every programmatic update.
        /// This is load-bearing rather than defensive.
        /// </para>
        /// <para>
        /// A XAML <c>Slider</c> or <c>ToggleSwitch</c> raises its change event during page
        /// construction, when the framework applies the value declared in markup - before any
        /// real state has arrived. Without this guard those synthetic events are indistinguishable
        /// from user input, so the widget immediately writes its own markup defaults to the
        /// hardware and overwrites whatever the user had configured. That is the mechanism behind
        /// the whole family of "my setting resets itself after a restart" bugs, and the reference
        /// project carries three separate hand-written flags to patch instances of it.
        /// </para>
        /// </remarks>
        private bool _applyingFromHelper;

        public MainWidget()
        {
            InitializeComponent();

            _connection.SnapshotApplied += OnSnapshotApplied;
            _connection.ValueChanged += OnValueChanged;
            _connection.ConnectionChanged += OnConnectionChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _widget = e.Parameter as XboxGameBarWidget;

            // Telemetry is only pushed while the widget is on screen. The Game Bar suspends this
            // process whenever it is dismissed, so the helper has to be told rather than left to
            // poll the embedded controller for a reader that is not running.
            Window.Current.VisibilityChanged += OnWindowVisibilityChanged;

            await StartAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Window.Current.VisibilityChanged -= OnWindowVisibilityChanged;
            _connection.Dispose();
            base.OnNavigatedFrom(e);
        }

        private async void OnWindowVisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            try { await _connection.SetVisibleAsync(e.Visible); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}"); }
        }

        // ── Startup ─────────────────────────────────────────────────────────────

        private async Task StartAsync()
        {
            ShowStatus("Starting the helper...", showRetry: false);

            // Launching the full-trust process is what triggers first-run setup, including its
            // single elevation prompt. It is safe to call when the helper is already running:
            // that instance sees the deployment is current and exits without serving.
            await LaunchHelperAsync();

            ShowStatus("Connecting...", showRetry: false);

            if (await _connection.ConnectAsync())
            {
                HideStatus();
                await _connection.SetVisibleAsync(true);
                return;
            }

            // The most likely cause by far is a declined elevation prompt, so lead with that
            // rather than a generic failure.
            ShowStatus(
                "Could not reach the helper. If an administrator prompt appeared, it needs to be "
                + "accepted before hardware controls can work.",
                showRetry: true);
        }

        private async Task LaunchHelperAsync()
        {
            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
            }
            catch (Exception ex)
            {
                // Not fatal: the helper may already be running from its scheduled task, in which
                // case the connect below succeeds anyway.
                System.Diagnostics.Debug.WriteLine($"[widget] launch failed: {ex.Message}");
            }
        }

        // ── Applying helper state ───────────────────────────────────────────────

        private async void OnSnapshotApplied()
        {
            await RunOnUiAsync(() =>
            {
                _applyingFromHelper = true;
                try
                {
                    ApplyCaps(_connection.Caps);
                    ApplyAllValues();
                }
                finally
                {
                    _applyingFromHelper = false;
                }
            });
        }

        private async void OnValueChanged(Function function, string value)
        {
            await RunOnUiAsync(() =>
            {
                _applyingFromHelper = true;
                try { ApplyValue(function); }
                finally { _applyingFromHelper = false; }
            });
        }

        private async void OnConnectionChanged(bool connected)
        {
            await RunOnUiAsync(() =>
            {
                if (connected) HideStatus();
                else ShowStatus("Lost the connection to the helper. Reconnecting...", showRetry: true);
            });
        }

        private void ApplyCaps(DeviceCaps caps)
        {
            DeviceModelText.Text = string.IsNullOrEmpty(caps.Model) ? "Unknown device" : caps.Model;

            if (!caps.Supported)
            {
                DeviceBackendText.Text =
                    "This device is not supported. Hardware controls are disabled.";
            }
            else
            {
                DeviceBackendText.Text = caps.TdpBackend == TdpBackendKind.Unavailable
                    ? "No power-limit backend is available on this device."
                    : $"Power limits via {DescribeBackend(caps.TdpBackend)}.";
            }

            // Cards are shown only when the helper reported a value for them. A control the user
            // cannot see is a value they cannot send, which matters because the pipe is reachable
            // by any app on the machine and the helper is the only real gate.
            TdpCard.Visibility = Visible(_connection.IsAvailable(Function.Pl1));
            FanCard.Visibility = Visible(caps.HasFan && _connection.IsAvailable(Function.FanPreset));
            ChargeCard.Visibility = Visible(caps.HasChargeLimit);
            LedCard.Visibility = Visible(caps.HasLed);
            HwMouseCard.Visibility = Visible(caps.HasHwMouse);
            IntelCard.Visibility = Visible(caps.HasIgcl && _connection.IsAvailable(Function.IntelFpsTier));

            // Slider ranges come from the device, not from markup: the ceilings differ per model
            // and the helper is the one that knows them.
            Pl1Slider.Minimum = caps.MinPl1;
            Pl1Slider.Maximum = caps.MaxPl1;
            Pl2Slider.Minimum = caps.MinPl1 + caps.Pl2MinOffset;
            Pl2Slider.Maximum = caps.MaxPl2;
        }

        private static string DescribeBackend(TdpBackendKind backend)
        {
            switch (backend)
            {
                case TdpBackendKind.Wmi: return "the firmware interface";
                case TdpBackendKind.RegistryMirror: return "MSI Center (which must stay running)";
                default: return "an unknown backend";
            }
        }

        private void ApplyAllValues()
        {
            ApplyValue(Function.Pl1);
            ApplyValue(Function.Pl2);
            ApplyValue(Function.FanEnabled);
            ApplyValue(Function.FanPreset);
            ApplyValue(Function.FanState);
            ApplyValue(Function.ChargeLimitEnabled);
            ApplyValue(Function.ChargeLimitPercent);
            ApplyValue(Function.LedSpec);
            ApplyValue(Function.HwMouseMode);
            ApplyValue(Function.CpuBoost);
            ApplyValue(Function.OsPowerMode);
            ApplyValue(Function.IntelFpsTier);
            ApplyValue(Function.IntelLowLatency);
            ApplyValue(Function.MsiCenterRunning);
        }

        private void ApplyValue(Function function)
        {
            switch (function)
            {
                case Function.Pl1:
                    Pl1Slider.Value = _connection.GetInt(Function.Pl1, (int)Pl1Slider.Minimum);
                    Pl1ValueText.Text = $"{(int)Pl1Slider.Value} W";
                    break;

                case Function.Pl2:
                    Pl2Slider.Value = _connection.GetInt(Function.Pl2, (int)Pl2Slider.Minimum);
                    Pl2ValueText.Text = $"{(int)Pl2Slider.Value} W";
                    break;

                case Function.FanEnabled:
                    FanEnabledToggle.IsOn = _connection.GetBool(Function.FanEnabled);
                    FanPresetCombo.IsEnabled = FanEnabledToggle.IsOn;
                    break;

                case Function.FanPreset:
                    FanPresetCombo.SelectedIndex = Clamp(_connection.GetInt(Function.FanPreset, 0), 0, 2);
                    break;

                case Function.FanState:
                    ApplyFanState(_connection.Get(Function.FanState));
                    break;

                case Function.ChargeLimitEnabled:
                    ChargeLimitToggle.IsOn = _connection.GetBool(Function.ChargeLimitEnabled);
                    ChargeLimitSlider.IsEnabled = ChargeLimitToggle.IsOn;
                    break;

                case Function.ChargeLimitPercent:
                    ChargeLimitSlider.Value = _connection.GetInt(Function.ChargeLimitPercent, 80);
                    ChargeLimitValueText.Text = $"{(int)ChargeLimitSlider.Value}%";
                    break;

                case Function.LedSpec:
                    ApplyLedSpec(_connection.Get(Function.LedSpec));
                    break;

                case Function.HwMouseMode:
                    HwMouseToggle.IsOn = _connection.GetBool(Function.HwMouseMode);
                    break;

                case Function.CpuBoost:
                    CpuBoostToggle.IsOn = _connection.GetBool(Function.CpuBoost);
                    break;

                case Function.OsPowerMode:
                    PowerModeCombo.SelectedIndex = Clamp(_connection.GetInt(Function.OsPowerMode, 1), 0, 2);
                    break;

                case Function.IntelFpsTier:
                    IntelFpsTierCombo.SelectedIndex = Clamp(_connection.GetInt(Function.IntelFpsTier, 0), 0, 3);
                    break;

                case Function.IntelLowLatency:
                    IntelLowLatencyCombo.SelectedIndex = Clamp(_connection.GetInt(Function.IntelLowLatency, 0), 0, 2);
                    break;

                case Function.MsiCenterRunning:
                    // Inverted on purpose. MSI Center M is a dependency, not a rival: its service is
                    // what applies the power limits we write. Warn when it is MISSING.
                    MsiCenterWarning.Visibility = Visible(!_connection.GetBool(Function.MsiCenterRunning));
                    break;
            }
        }

        private void ApplyFanState(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                FanStateText.Text = "";
                FanMismatchText.Visibility = Visibility.Collapsed;
                return;
            }

            var state = FanState.Parse(payload);

            if (!state.ReadOk)
            {
                FanStateText.Text = "Could not read the fan controller.";
                FanMismatchText.Visibility = Visibility.Collapsed;
                return;
            }

            var rpm = state.Rpm >= 0 ? $"{state.Rpm} rpm" : "rpm unavailable";
            var owner = state.ControlEnabled ? "software curve" : "firmware curve";
            FanStateText.Text = state.FullSpeed ? $"{rpm} - full speed override" : $"{rpm} - {owner}";

            // Surfaced rather than swallowed: the controller can accept a write and keep running
            // the old curve, and reporting that as success is the failure this app must not have.
            var expectedPreset = (FanPreset)Clamp(_connection.GetInt(Function.FanPreset, 0), 0, 2);
            Shared.Fan.FanProfiles.Resolve(expectedPreset, null, null, out _, out var duties);

            bool matches = state.Table == null || Shared.Fan.FanProfiles.Matches(state.Table, duties);
            FanMismatchText.Visibility = Visible(state.ControlEnabled && !matches);
            if (state.ControlEnabled && !matches)
                FanMismatchText.Text = "The controller is not running the selected profile.";
        }

        private void ApplyLedSpec(string payload)
        {
            var spec = LedSpec.Parse(payload ?? "");
            LedModeCombo.SelectedIndex = Clamp((int)spec.Mode, 0, 4);
            LedBrightnessSlider.Value = spec.Brightness;
            LedBrightnessValueText.Text = $"{spec.Brightness}%";
        }

        // ── User input ──────────────────────────────────────────────────────────
        // Every handler starts with the same guard. See _applyingFromHelper.

        private async void Pl1Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            Pl1ValueText.Text = $"{(int)e.NewValue} W";
            await SendAsync(Function.Pl1, (int)e.NewValue);
        }

        private async void Pl2Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            Pl2ValueText.Text = $"{(int)e.NewValue} W";
            await SendAsync(Function.Pl2, (int)e.NewValue);
        }

        private async void FanEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            FanPresetCombo.IsEnabled = FanEnabledToggle.IsOn;
            await SendAsync(Function.FanEnabled, FanEnabledToggle.IsOn);
        }

        private async void FanPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.FanPreset, FanPresetCombo.SelectedIndex);
        }

        private async void ChargeLimitToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            ChargeLimitSlider.IsEnabled = ChargeLimitToggle.IsOn;
            await SendAsync(Function.ChargeLimitEnabled, ChargeLimitToggle.IsOn);
        }

        private async void ChargeLimitSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            ChargeLimitValueText.Text = $"{(int)e.NewValue}%";
            await SendAsync(Function.ChargeLimitPercent, (int)e.NewValue);
        }

        private async void LedModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendLedAsync();
        }

        private async void LedBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            LedBrightnessValueText.Text = $"{(int)e.NewValue}%";
            await SendLedAsync();
        }

        /// <summary>
        /// Sends the whole LED configuration as one value.
        /// </summary>
        /// <remarks>
        /// The device applies lighting as a single indivisible report, so sending mode and
        /// brightness separately would leave it half-configured between the two writes. The
        /// helper additionally suppresses writes that would change nothing, which matters because
        /// dragging a slider produces a stream of near-identical values.
        /// </remarks>
        private async Task SendLedAsync()
        {
            var spec = LedSpec.Parse(_connection.Get(Function.LedSpec) ?? "");
            spec.Mode = (LedMode)Clamp(LedModeCombo.SelectedIndex, 0, 4);
            spec.Brightness = (int)LedBrightnessSlider.Value;

            await SendAsync(Function.LedSpec, spec.Serialize());
        }

        private async void HwMouseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.HwMouseMode, HwMouseToggle.IsOn);
        }

        private async void CpuBoostToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.CpuBoost, CpuBoostToggle.IsOn);
        }

        private async void PowerModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.OsPowerMode, PowerModeCombo.SelectedIndex);
        }

        private async void IntelFpsTierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.IntelFpsTier, IntelFpsTierCombo.SelectedIndex);
        }

        private async void IntelLowLatencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.IntelLowLatency, IntelLowLatencyCombo.SelectedIndex);
        }

        private async void StatusActionButton_Click(object sender, RoutedEventArgs e) => await StartAsync();

        // ── Plumbing ────────────────────────────────────────────────────────────

        private Task SendAsync(Function function, bool value) => SendAsync(function, value ? "1" : "0");

        private Task SendAsync(Function function, int value) =>
            SendAsync(function, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private async Task SendAsync(Function function, string value)
        {
            var error = await _connection.SetAsync(function, value);

            if (error != null)
            {
                ShowStatus(error, showRetry: false);
                return;
            }

            // Re-read from the cache, which now holds the helper's reply. A clamped or refused
            // value snaps the control back to what the hardware actually holds instead of leaving
            // the UI showing a change that never happened.
            _applyingFromHelper = true;
            try { ApplyValue(function); }
            finally { _applyingFromHelper = false; }
        }

        private void ShowStatus(string message, bool showRetry)
        {
            StatusText.Text = message;
            StatusActionButton.Visibility = Visible(showRetry);
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        private async Task RunOnUiAsync(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { action(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}"); }
            });
        }

        private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);
    }
}
