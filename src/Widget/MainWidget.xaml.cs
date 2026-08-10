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
        /// <remarks>
        /// Starts true, not false. XAML applies markup defaults - and can raise synthetic
        /// ValueChanged events doing it - during InitializeComponent(), before the constructor
        /// body even runs and before any snapshot has arrived to flip this the "normal" way via
        /// OnSnapshotApplied's try/finally. A field initializer runs ahead of InitializeComponent(),
        /// so starting true closes that gap; the first real snapshot still flips it false once
        /// state has genuinely arrived. Without this, a synthetic construction-time event can reach
        /// a handler that touches a XAML element declared later in the same document and not yet
        /// wired up, e.g. Pl1Slider_ValueChanged writing to Pl1ValueText before it exists.
        /// </remarks>
        private bool _applyingFromHelper = true;

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
        /// it is cast straight to <c>PerfMode</c>, <c>FanPreset</c> and friends.
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

        /// <summary>
        /// A fixed row of individually-clickable options, the active one highlighted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="OptionCycler"/>'s single button read as a dropdown despite being clickable -
        /// right for the longer option lists elsewhere in this widget, wrong for a short, always-
        /// relevant set like the three power modes, where showing every choice up front removes any
        /// doubt about what pressing it does.
        /// </para>
        /// <para>
        /// Same index-is-the-wire-value contract as <see cref="OptionCycler"/>, and the same
        /// "Show never counts as user input" split between display and the <see cref="Selected"/>
        /// event, which only fires from a genuine click.
        /// </para>
        /// </remarks>
        private sealed class SegmentedControl
        {
            private readonly Button[] _segments;

            public SegmentedControl(Grid container, params string[] options)
            {
                _segments = new Button[options.Length];

                for (int i = 0; i < options.Length; i++)
                {
                    container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    int index = i; // capture per-iteration, not the loop variable
                    var button = new Button
                    {
                        Content = options[i],
                        Style = (Style)Application.Current.Resources["SegmentButtonStyle"],
                    };
                    button.Click += (_, __) => Selected?.Invoke(index);

                    Grid.SetColumn(button, i);
                    container.Children.Add(button);
                    _segments[i] = button;
                }

                Show(0);
            }

            public int Index { get; private set; }

            /// <summary>Fires only when a segment is actually clicked, never from <see cref="Show"/>.</summary>
            public event Action<int> Selected;

            /// <summary>Displays an option WITHOUT treating it as user input.</summary>
            public void Show(int index)
            {
                if (index < 0 || index >= _segments.Length) return;
                Index = index;

                for (int i = 0; i < _segments.Length; i++)
                {
                    _segments[i].Style = (Style)Application.Current.Resources[
                        i == index ? "SegmentButtonSelectedStyle" : "SegmentButtonStyle"];
                }
            }

            public bool IsEnabled
            {
                set { foreach (var segment in _segments) segment.IsEnabled = value; }
            }
        }

        /// <summary>
        /// The perf-mode segments, left to right: what each one says and which
        /// <see cref="PerfMode"/> it means.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists so the buttons can be arranged for the USER without touching the wire
        /// contract. They read Endurance → AI Engine → Manual, ordered by how much power the
        /// device draws, which is not the order of <see cref="PerfMode"/>'s ordinals
        /// (Endurance 0, UserScenario 1, AiEngine 2).
        /// </para>
        /// <para>
        /// Every other selector in this widget casts its index straight to an enum, so reordering
        /// one silently changes what the helper is told. This is the one control where display
        /// order and wire value are decoupled, and this table is the only thing keeping them
        /// honest - both directions go through it, never a cast.
        /// </para>
        /// <para>
        /// Label and mode are paired in ONE table rather than two arrays kept in step, so
        /// rearranging the buttons is a matter of moving whole rows and cannot desynchronise what
        /// a button says from what it does.
        /// </para>
        /// <para>
        /// "Manual" rather than MSI's "User Scenario": their name is meaningless out of context,
        /// and the thing that matters about the mode is that it is the only one where the sliders
        /// below do anything.
        /// </para>
        /// </remarks>
        private static readonly (PerfMode Mode, string Label)[] PerfModeSegmentOrder =
        {
            (PerfMode.Endurance, "Endurance"),
            (PerfMode.AiEngine, "AI Engine"),
            (PerfMode.UserScenario, "Manual"),
        };

        private SegmentedControl _perfMode;
        private OptionCycler _fanPreset;
        private SegmentedControl _powerMode;
        private OptionCycler _intelFpsTier;
        private OptionCycler _intelLowLatency;

        public MainWidget()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                App.LogCrash("ctor/InitializeComponent", ex);
                throw;
            }

            try
            {
                // Labels come from the table, so they cannot drift out of step with the modes they
                // stand for. Everywhere else in this file the index IS the wire value; this
                // control is the one exception - see PerfModeSegmentOrder.
                _perfMode = new SegmentedControl(PerfModeSegments,
                    Array.ConvertAll(PerfModeSegmentOrder, segment => segment.Label));
                _perfMode.Selected += OnPerfModeSelected;

                _fanPreset = new OptionCycler(FanPresetButton,
                    "MSI Default", "Quiet Idle", "Cooling \u00B7 Early Ramp");

                _powerMode = new SegmentedControl(PowerModeSegments,
                    "Efficiency", "Balanced", "Performance");
                _powerMode.Selected += OnPowerModeSelected;

                _intelFpsTier = new OptionCycler(IntelFpsTierButton,
                    "Off", "Performance (60 fps)", "Balanced (40 fps)", "Efficiency (30 fps)");

                _intelLowLatency = new OptionCycler(IntelLowLatencyButton,
                    "Off", "On", "On + boost");

                _connection.SnapshotApplied += OnSnapshotApplied;
                _connection.ValueChanged += OnValueChanged;
                _connection.ConnectionChanged += OnConnectionChanged;
            }
            catch (Exception ex)
            {
                App.LogCrash("ctor", ex);
                throw;
            }
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

            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                App.LogCrash("OnNavigatedTo/StartAsync", ex);
                throw;
            }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[widget] sizing: {ex.Message}");
                App.LogCrash("ConfigureWidget/sizing", ex);
            }

            try
            {
                // A device control panel is the archetypal thing to pin over a game.
                _widget.PinningSupported = true;

                // There is no settings widget. Claiming otherwise puts a button in the title bar
                // that does nothing when pressed.
                _widget.SettingsSupported = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[widget] flags: {ex.Message}");
                App.LogCrash("ConfigureWidget/flags", ex);
            }

            try
            {
                // Deliberately does NOT read _widget.RequestedOpacity here. That getter throws an
                // E_POINTER (a raw COM error, not a managed exception - see LogCrash's HResult
                // output) when called this early: the native widget object's opacity state is not
                // yet set up immediately after construction, and the failure surfaces
                // asynchronously, past any try/catch wrapped around the call itself, straight to
                // Application.UnhandledException with no stack trace. Every card ends up hidden
                // (ApplyCaps never gets a chance to run) because the process survives but the
                // exception fires close enough to activation to abort the rest of it in practice.
                //
                // Subscribing is safe - only reading the property up front is not. Defaulting to
                // full opacity until Game Bar actually tells us otherwise is a fine trade for
                // avoiding a call proven to crash.
                RootContent.Opacity = 1.0;
                _widget.RequestedOpacityChanged += OnRequestedOpacityChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[widget] opacity: {ex.Message}");
                App.LogCrash("ConfigureWidget/opacity", ex);
            }
        }

        private async void OnWidgetVisibleChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                bool visible = sender.Visible;
                await _connection.SetVisibleAsync(visible);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}");
                App.LogCrash("OnWidgetVisibleChanged", ex);
            }
        }

        private async void OnRequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            try { await RunOnUiAsync(ApplyRequestedOpacity); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}");
                App.LogCrash("OnRequestedOpacityChanged", ex);
            }
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
            App.Log("StartAsync: begin");
            ShowStatus("Starting the helper...", showRetry: false);

            // Launching the full-trust process is what triggers first-run setup, including its
            // single elevation prompt. It is safe to call when the helper is already running:
            // that instance sees the deployment is current and exits without serving.
            await LaunchHelperAsync();
            App.Log("StartAsync: LaunchHelperAsync returned");

            ShowStatus("Connecting...", showRetry: false);

            bool connected = await _connection.ConnectAsync();
            App.Log($"StartAsync: ConnectAsync returned {connected}");

            if (connected)
            {
                HideStatus();
                await _connection.SetVisibleAsync(true);
                App.Log("StartAsync: SetVisibleAsync done, HideStatus called");
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
            App.Log("OnSnapshotApplied: fired");
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

                App.Log("OnSnapshotApplied: _applyingFromHelper set false");
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
                Pl1Slider,
                FanEnabledToggle,
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

            // Cards are shown only when the helper reported a value for them. A control the user
            // cannot see is a value they cannot send, which matters because the pipe is reachable
            // by any app on the machine and the helper is the only real gate.
            TdpCard.Visibility = Visible(_connection.IsAvailable(Function.Pl1));
            FanCard.Visibility = Visible(caps.HasFan && _connection.IsAvailable(Function.FanPreset));
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

            // Unknown is not on the control: MSI reported a mode we do not model, so leave the
            // selection alone rather than misrepresenting it as one of the three we do.
            int segment = Array.FindIndex(PerfModeSegmentOrder, s => s.Mode == mode);
            if (segment >= 0)
                _perfMode.Show(segment);

            Pl1Slider.IsEnabled = manual;
            Pl2Slider.IsEnabled = manual;
            PerfModeHint.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void OnPerfModeSelected(int segment)
        {
            if (_applyingFromHelper) return;
            if (segment < 0 || segment >= PerfModeSegmentOrder.Length) return;

            _perfMode.Show(segment);
            var mode = PerfModeSegmentOrder[segment].Mode;
            ApplyPerfMode(mode);
            await SendAsync(Function.PerfMode, (int)mode);

            // A mode change moves more than the mode: the power limits MSI reports can change with
            // it. Re-sync everything rather than guessing the blast radius of someone else's state
            // machine.
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

        private async void OnPowerModeSelected(int index)
        {
            if (_applyingFromHelper) return;
            _powerMode.Show(index);
            await SendAsync(Function.OsPowerMode, index);
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

        private async Task RunOnUiAsync(Action action, [System.Runtime.CompilerServices.CallerMemberName] string caller = null)
        {
            if (Dispatcher.HasThreadAccess)
            {
                try { action(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}");
                    App.LogCrash($"RunOnUiAsync/sync/{caller}", ex);
                    throw;
                }
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    // This is the branch the pipe reader's background thread actually takes -
                    // without the LogCrash call, an exception here (e.g. from ApplyCaps or
                    // ApplyAllValues while applying the helper's first snapshot) was previously
                    // swallowed with no trace anywhere, leaving every capability-gated card stuck
                    // hidden and the widget looking permanently blank.
                    System.Diagnostics.Debug.WriteLine($"[widget] {ex.Message}");
                    App.LogCrash($"RunOnUiAsync/dispatched/{caller}", ex);
                }
            });
        }

        private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);
    }
}
