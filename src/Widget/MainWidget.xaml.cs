using System;
using System.Threading.Tasks;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
using McenterLite.Widget.Ipc;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
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

        /// <summary>
        /// Drives a <see cref="Button"/> as a cycling selector over a fixed list of options.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Replaces the ComboBox everywhere in this widget. The device is a handheld driven with a
        /// game controller: a dropdown costs a press to open, D-pad travel through a popup that
        /// takes focus out of the card, and a second press to commit. This is one press per step.
        /// </para>
        /// <para>
        /// The option strings live here rather than in XAML because the index IS the wire value -
        /// it is cast straight to <c>PerfMode</c>, <c>FanPreset</c>, <c>LedMode</c> and friends.
        /// Keeping the list next to the code that casts it makes that coupling visible; in XAML it
        /// was three files apart.
        /// </para>
        /// </remarks>
        private sealed class OptionCycler
        {
            private readonly Button _button;
            private readonly string[] _options;

            public OptionCycler(Button button, params string[] options)
            {
                _button = button;
                _options = options;
                _button.Content = options[0];
            }

            public int Index { get; private set; }

            /// <summary>Displays an option WITHOUT treating it as user input.</summary>
            public void Show(int index)
            {
                if (index < 0 || index >= _options.Length) return;
                Index = index;
                _button.Content = _options[index];
            }

            /// <summary>Advances one step, wrapping past the end. Returns the new index.</summary>
            public int Advance()
            {
                Show((Index + 1) % _options.Length);
                return Index;
            }

            public bool IsEnabled
            {
                set => _button.IsEnabled = value;
            }
        }

        private OptionCycler _perfMode;
        private OptionCycler _fanPreset;
        private OptionCycler _ledMode;
        private OptionCycler _powerMode;
        private OptionCycler _intelFpsTier;
        private OptionCycler _intelLowLatency;

        public MainWidget()
        {
            InitializeComponent();

            // Order is the contract: every index below is cast directly to the enum it names.
            _perfMode = new OptionCycler(PerfModeButton,
                "Endurance", "User Scenario", "AI Engine");

            _fanPreset = new OptionCycler(FanPresetButton,
                "MSI Default", "Quiet Idle", "Cooling \u00B7 Early Ramp");

            _ledMode = new OptionCycler(LedModeButton,
                "Off", "Static", "Breathing", "Colour cycle", "Wave");

            _powerMode = new OptionCycler(PowerModeButton,
                "Best power efficiency", "Balanced", "Best performance");

            _intelFpsTier = new OptionCycler(IntelFpsTierButton,
                "Off", "Performance (60 fps)", "Balanced (40 fps)", "Efficiency (30 fps)");

            _intelLowLatency = new OptionCycler(IntelLowLatencyButton,
                "Off", "On", "On + boost");

            _connection.SnapshotApplied += OnSnapshotApplied;
            _connection.ValueChanged += OnValueChanged;
            _connection.ConnectionChanged += OnConnectionChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _widget = e.Parameter as XboxGameBarWidget;

            ConfigureWidget();

            // Telemetry is only pushed while the widget is on screen, so this has to be right.
            //
            // NOT Window.Current.VisibilityChanged, which was the original and was wrong. Game Bar
            // documents Visible as a COMPOSITE of GameBarDisplayMode, WindowState and Pinned - a
            // pinned widget stays visible when the Game Bar overlay itself is dismissed, and the
            // window-level event does not know that. Getting it backwards means either polling the
            // embedded controller for a reader that is not there, or a pinned widget showing fan
            // telemetry that has silently stopped updating.
            if (_widget != null)
                _widget.VisibleChanged += OnWidgetVisibleChanged;

            await StartAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_widget != null)
            {
                _widget.VisibleChanged -= OnWidgetVisibleChanged;
                _widget.RequestedOpacityChanged -= OnRequestedOpacityChanged;
            }

            _connection.Dispose();
            base.OnNavigatedFrom(e);
        }

        /// <summary>
        /// Tells Game Bar what this widget supports.
        /// </summary>
        /// <remarks>
        /// These are not cosmetic. Game Bar surfaces them in its own chrome - the pin button, the
        /// resize grips, the transparency slider - so leaving them unset makes the widget look
        /// broken next to every other one rather than merely unconfigured.
        ///
        /// Every call is guarded individually. A throw here happens during activation, and an
        /// exception on that path takes the whole Game Bar panel down with no indication why.
        /// </remarks>
        private void ConfigureWidget()
        {
            if (_widget == null) return;

            try
            {
                // Min is roughly one card plus the header; below that the value columns collide
                // with their labels. Max is generous because Game Bar clamps to the screen anyway,
                // which on this device is 960x600 effective pixels at 200% scaling.
                _widget.MinWindowSize = new Size(320, 320);
                _widget.MaxWindowSize = new Size(560, 1000);
                _widget.HorizontalResizeSupported = true;
                _widget.VerticalResizeSupported = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] sizing: {ex.Message}"); }

            try
            {
                // A device control panel is the archetypal thing to pin over a game.
                _widget.PinningSupported = true;

                // There is no settings widget. Claiming otherwise puts a button in the title bar
                // that does nothing when pressed.
                _widget.SettingsSupported = false;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] flags: {ex.Message}"); }

            try
            {
                ApplyRequestedOpacity();
                _widget.RequestedOpacityChanged += OnRequestedOpacityChanged;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] opacity: {ex.Message}"); }
        }

        private async void OnWidgetVisibleChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                bool visible = sender.Visible;
                await _connection.SetVisibleAsync(visible);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}"); }
        }

        private async void OnRequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            try { await RunOnUiAsync(ApplyRequestedOpacity); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}"); }
        }

        /// <summary>
        /// Applies the user's Game Bar transparency setting to the whole widget.
        /// </summary>
        /// <remarks>
        /// Game Bar reports 0-100. Applied to the root, so text fades with the panel rather than
        /// staying opaque over a see-through background - the setting means "let me see the game
        /// through this", and half-honouring it looks like a rendering bug.
        /// </remarks>
        private void ApplyRequestedOpacity()
        {
            if (_widget == null) return;

            double requested = _widget.RequestedOpacity;
            if (requested <= 0 || requested > 100) return;

            RootContent.Opacity = requested / 100.0;
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

                SetInitialFocus();
            });
        }

        /// <summary>
        /// Puts gamepad focus on the first control the user is likely to act on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Game Bar's guidance is to focus the first element the user would take action on, near
        /// the top-left. With a gamepad there is no cursor, so without this the first D-pad press
        /// goes somewhere arbitrary and the user has to hunt for the focus rectangle.
        /// </para>
        /// <para>
        /// It walks a list rather than naming one control because cards are HIDDEN when the helper
        /// reports the feature unavailable - on a machine with no MSI hardware the power card does
        /// not exist, and focusing it would focus nothing. Runs after the snapshot for the same
        /// reason: before that, visibility is not yet known.
        /// </para>
        /// <para>
        /// Only ever set once. Re-focusing on every snapshot would yank focus out from under
        /// someone mid-adjustment whenever a value arrived.
        /// </para>
        /// </remarks>
        private void SetInitialFocus()
        {
            if (_initialFocusSet) return;

            Control[] candidates =
            {
                PerfModeButton,
                Pl1Slider,
                FanEnabledToggle,
                ChargeLimitToggle,
                LedModeButton,
                HwMouseToggle,
                CpuBoostToggle,
            };

            foreach (var control in candidates)
            {
                // A collapsed parent card leaves the control itself Visible, so the ancestor has
                // to be consulted - IsLoaded plus a non-zero size is the reliable signal here.
                if (control == null) continue;
                if (control.Visibility != Visibility.Visible) continue;
                if (!control.IsEnabled) continue;
                if (control.ActualHeight <= 0) continue;

                // Programmatic, not Pointer: it preserves whether the focus rectangle was already
                // being shown, so a touch user does not suddenly get gamepad chrome.
                if (control.Focus(FocusState.Programmatic))
                {
                    _initialFocusSet = true;
                    return;
                }
            }
        }

        private bool _initialFocusSet;

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

            // Ranges come from the device, not the markup defaults. The helper is the only
            // authority on what this firmware accepts, and a slider that can reach a value the
            // helper will clamp is a slider that visibly snaps back.
            Pl1Slider.Minimum = caps.MinPl1;
            Pl1Slider.Maximum = caps.MaxPl1;
            Pl2Slider.Minimum = caps.MinPl1 + caps.Pl2MinOffset;
            Pl2Slider.Maximum = caps.MaxPl2;

            // Say the battery cap out loud. It is applied automatically and has no control of its
            // own, so without this the user sets 35 W, unplugs, and sees the device behave as
            // though the setting were ignored.
            bool capped = caps.MaxPl1Dc < caps.MaxPl1 || caps.MaxPl2Dc < caps.MaxPl2;
            BatteryLimitHint.Visibility = Visible(capped);
            if (capped)
            {
                BatteryLimitHint.Text =
                    $"On battery these are capped to {caps.MaxPl1Dc} W sustained and "
                    + $"{caps.MaxPl2Dc} W boost. Plugged in, the values above apply.";
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
            ApplyValue(Function.PerfMode);
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
                case Function.PerfMode:
                {
                    var mode = (PerfMode)_connection.GetInt(
                        Function.PerfMode, (int)PerfMode.UserScenario);
                    ApplyPerfMode(mode);
                    break;
                }

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
                    _fanPreset.IsEnabled = FanEnabledToggle.IsOn;
                    break;

                case Function.FanPreset:
                    _fanPreset.Show(Clamp(_connection.GetInt(Function.FanPreset, 0), 0, 2));
                    break;

                case Function.FanState:
                    ApplyFanState(_connection.Get(Function.FanState));
                    break;

                case Function.ChargeLimitEnabled:
                    ChargeLimitToggle.IsOn = _connection.GetBool(Function.ChargeLimitEnabled);
                    ChargeLimitSlider.IsEnabled = ChargeLimitToggle.IsOn;
                    break;

                case Function.ChargeLimitPercent:
                {
                    int percent = ChargeLevels.Snap(
                        _connection.GetInt(Function.ChargeLimitPercent, ChargeLevels.Default));
                    ChargeLimitSlider.Value = percent;
                    ChargeLimitValueText.Text = $"{percent}%";
                    break;
                }

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
                    _powerMode.Show(Clamp(_connection.GetInt(Function.OsPowerMode, 1), 0, 2));
                    break;

                case Function.IntelFpsTier:
                    _intelFpsTier.Show(Clamp(_connection.GetInt(Function.IntelFpsTier, 0), 0, 3));
                    break;

                case Function.IntelLowLatency:
                    _intelLowLatency.Show(Clamp(_connection.GetInt(Function.IntelLowLatency, 0), 0, 2));
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
            // The helper decides this - only it knows the factory curve and the model duty floor
            // that the preset resolves against.
            FanMismatchText.Visibility = Visible(state.ControlEnabled && !state.Matches);
            if (state.ControlEnabled && !state.Matches)
                FanMismatchText.Text = "The controller is not running the selected profile.";
        }

        private void ApplyLedSpec(string payload)
        {
            var spec = LedSpec.Parse(payload ?? "");
            _ledMode.Show(Clamp((int)spec.Mode, 0, 4));
            LedBrightnessSlider.Value = spec.Brightness;
            LedBrightnessValueText.Text = $"{spec.Brightness}%";
        }

        // ── User input ──────────────────────────────────────────────────────────
        // Every handler starts with the same guard. See _applyingFromHelper.

        /// <summary>
        /// Paints the mode dropdown and enables or disables the sliders it gates.
        /// </summary>
        /// <remarks>
        /// The sliders are disabled rather than hidden in the non-manual modes. A control that
        /// vanishes leaves the user with no way to understand why - a greyed one next to the mode
        /// that greyed it explains itself.
        /// </remarks>
        private void ApplyPerfMode(PerfMode mode)
        {
            bool manual = mode == PerfMode.UserScenario;

            // Unknown is not in the dropdown: MSI reported a mode we do not model, so leave the
            // selection alone rather than misrepresenting it as one of the three we do.
            if (mode != PerfMode.Unknown)
                _perfMode.Show((int)mode);

            Pl1Slider.IsEnabled = manual;
            Pl2Slider.IsEnabled = manual;
            PerfModeHint.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void PerfModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;

            var mode = (PerfMode)_perfMode.Advance();
            ApplyPerfMode(mode);
            await SendAsync(Function.PerfMode, (int)mode);

            // A mode change moves more than the mode: MSI couples lighting to it, and the power
            // limits it reports can change too. Re-sync everything rather than guessing the blast
            // radius of someone else's state machine.
            await _connection.RefreshAsync();
        }

        /// <summary>
        /// Guards the moment one power slider moves the other.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="_applyingFromHelper"/> on purpose. That one means "this change
        /// came from the device, do not echo it back"; this one means "this change came from the
        /// OTHER slider, do not recurse". Sharing a flag would work today and mislead whoever next
        /// has to reason about which suppression is in effect.
        /// </remarks>
        private bool _couplingLimits;

        private async void Pl1Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper || _couplingLimits) return;

            int pl1 = (int)e.NewValue;
            int pl2 = (int)Pl2Slider.Value;
            _connection.Caps.CoupleFromPl1(ref pl1, ref pl2);

            await ApplyCoupledLimitsAsync(pl1, pl2);
        }

        private async void Pl2Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper || _couplingLimits) return;

            int pl1 = (int)Pl1Slider.Value;
            int pl2 = (int)e.NewValue;
            _connection.Caps.CoupleFromPl2(ref pl1, ref pl2);

            await ApplyCoupledLimitsAsync(pl1, pl2);
        }

        /// <summary>
        /// Paints a coupled pair and sends both halves.
        /// </summary>
        /// <remarks>
        /// Both are always sent, whichever slider the user touched, because the coupling means one
        /// gesture changes two values. Sending only the moved one would leave the helper holding a
        /// pair the widget is no longer showing.
        ///
        /// PL1 goes first: the helper clamps PL2 to at least PL1 + the firmware headroom, so
        /// raising PL1 before PL2 never transits through a pair that gets clamped and echoed back.
        /// </remarks>
        private async Task ApplyCoupledLimitsAsync(int pl1, int pl2)
        {
            _couplingLimits = true;
            try
            {
                Pl1Slider.Value = pl1;
                Pl2Slider.Value = pl2;
                Pl1ValueText.Text = $"{pl1} W";
                Pl2ValueText.Text = $"{pl2} W";
            }
            finally
            {
                _couplingLimits = false;
            }

            await SendAsync(Function.Pl1, pl1);
            await SendAsync(Function.Pl2, pl2);
        }

        private async void FanEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            _fanPreset.IsEnabled = FanEnabledToggle.IsOn;
            await SendAsync(Function.FanEnabled, FanEnabledToggle.IsOn);
        }

        private async void FanPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.FanPreset, _fanPreset.Advance());
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

            // Snapped again even though StepFrequency should already guarantee it. The slider's
            // step is a UI affordance; this is the value that goes on the wire, and the helper is
            // entitled to assume it is one the hardware can hold.
            int percent = ChargeLevels.Snap((int)e.NewValue);
            ChargeLimitValueText.Text = $"{percent}%";
            await SendAsync(Function.ChargeLimitPercent, percent);
        }

        private async void LedModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            _ledMode.Advance();
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
            spec.Mode = (LedMode)Clamp(_ledMode.Index, 0, 4);
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

        private async void PowerModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.OsPowerMode, _powerMode.Advance());
        }

        private async void IntelFpsTierButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.IntelFpsTier, _intelFpsTier.Advance());
        }

        private async void IntelLowLatencyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingFromHelper) return;
            await SendAsync(Function.IntelLowLatency, _intelLowLatency.Advance());
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
            SetConnectionDot(false);
        }

        private void HideStatus()
        {
            StatusBanner.Visibility = Visibility.Collapsed;
            SetConnectionDot(true);
        }

        /// <summary>
        /// Colours the header dot. Driven from the same two calls that raise and clear the banner
        /// so the dot and the banner cannot disagree about whether the helper is up.
        /// </summary>
        private void SetConnectionDot(bool connected) =>
            ConnectionDot.Fill = (Brush)Application.Current.Resources[
                connected ? "SuccessBrush" : "TextSecondaryBrush"];

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
