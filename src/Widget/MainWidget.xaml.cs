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
        /// A fixed row of individually-clickable options, the active one highlighted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The only selector this widget uses. It replaced a ComboBox first, then a cycle-button
        /// that showed one option at a time: the device is a handheld driven with a game
        /// controller, and a dropdown costs a press to open, D-pad travel through a popup that
        /// steals focus, and a second press to commit. A cycle-button fixed that but still read as
        /// a dropdown and hid the choices. Showing every option up front removes both problems -
        /// one press, and no doubt about what pressing it does.
        /// </para>
        /// <para>
        /// <b>The index is NOT automatically the wire value.</b> It was, when every list happened
        /// to be ordered like its enum, and two selectors now deliberately are not - see
        /// <see cref="PerfModeSegmentOrder"/> and <see cref="IntelFrameGenSegmentOrder"/>. Those
        /// pair label to value in one table so display order can be chosen for the user without
        /// changing what gets sent. Where the two orders genuinely coincide the index is passed
        /// straight through, and that is stated at the call site.
        /// </para>
        /// <para>
        /// <see cref="Show"/> never counts as user input; only a genuine click raises
        /// <see cref="Selected"/>. That split is what keeps helper-driven updates from echoing
        /// back as writes.
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
                    Paint(_segments[i], i == index);
            }

            /// <summary>
            /// Recolours one segment IN PLACE.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Sets the three brushes that differ rather than swapping the button's
            /// <see cref="FrameworkElement.Style"/>, which is what this did originally.
            /// </para>
            /// <para>
            /// <b>Assigning Style re-applies the control template</b>, and a control that rebuilds
            /// its template loses focus. So selecting a segment with the gamepad threw focus out of
            /// the widget and the next D-pad press had nowhere to travel from - the control was
            /// unusable with a controller the moment you pressed anything on it. Nothing about that
            /// is visible with a mouse, which re-establishes focus on every click.
            /// </para>
            /// <para>
            /// The layout half of the appearance - height, corner radius, padding, content template
            /// - stays in <c>SegmentButtonStyle</c>, applied once at construction and never
            /// reassigned.
            /// </para>
            /// </remarks>
            private static void Paint(Button segment, bool selected)
            {
                var resources = Application.Current.Resources;

                segment.Background = (Brush)resources[selected ? "AccentBrush" : "SurfaceSubtleBrush"];
                segment.BorderBrush = (Brush)resources[selected ? "AccentBrush" : "SurfaceStrokeBrush"];
                segment.Foreground = (Brush)resources[
                    selected ? "SegmentSelectedForegroundBrush" : "TextPrimaryBrush"];
            }

            public bool IsEnabled
            {
                set { foreach (var segment in _segments) segment.IsEnabled = value; }
            }

            /// <summary>
            /// The leftmost button, for <see cref="SetInitialFocus"/>.
            /// </summary>
            /// <remarks>
            /// The segments are created in code, so they have no <c>x:Name</c> to reference from
            /// the focus-candidate list. Without this the only candidate left is a slider whose
            /// card hides on an unsupported device, and nothing would take focus at all.
            /// </remarks>
            public Control FirstSegment => _segments.Length > 0 ? _segments[0] : null;
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

        /// <summary>
        /// The controller-mode segments, left to right: what each says and the wire value it means.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Same paired-table shape as <see cref="PerfModeSegmentOrder"/>, and for the same reason:
        /// label and meaning move together, so rearranging the buttons cannot desynchronise what
        /// one says from what it does.
        /// </para>
        /// <para>
        /// Gamepad sits first because it is the device's native state - the mode it boots in and
        /// the one the physical button returns to. Reading left to right then runs from "as the
        /// device ships" to "overridden", matching how the perf-mode segments are ordered.
        /// </para>
        /// </remarks>
        private static readonly (bool DesktopMode, string Label)[] HwMouseSegmentOrder =
        {
            (false, "Gamepad"),
            (true, "Desktop"),
        };

        /// <summary>
        /// The CPU-boost segments, left to right. Off first, so the row reads in the same
        /// direction as every other selector here: least intervention on the left.
        /// </summary>
        private static readonly (bool Enabled, string Label)[] CpuBoostSegmentOrder =
        {
            (false, "Off"),
            (true, "On"),
        };

        /// <summary>
        /// The FPS-limit segments, left to right: the label and the IGCL tier it sends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The tiers run backwards.</b> IGCL numbers Endurance Gaming 0 = off, 1 = performance
        /// (60 fps), 2 = balanced (40), 3 = efficiency (30) - so ascending frame rate is
        /// DESCENDING wire value. Reading Off, 30, 40, 60 left to right is the order a frame-rate
        /// cap should read in; it is not the order the driver numbers them, and casting the index
        /// would send 30 fps when 60 was pressed.
        /// </para>
        /// <para>
        /// Shown as the frame rates rather than IGCL's "performance / balanced / efficiency", which
        /// name the intent instead of the effect - and the effect is a number the user can see in
        /// their game.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Frame generation, left to right: label and the IGCL override it sends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>"Off" is APP_CHOICE, and APP_CHOICE is not off.</b> IGCL's
        /// <c>ctl_3d_frame_generation_override_t</c> has no disable value - it OVERRIDES what the
        /// game asked for, and 0 means "do not override". A game that enables frame generation
        /// itself keeps it enabled while this shows Off. The label is the user's decision, taken
        /// knowingly; the behaviour is the driver's.
        /// </para>
        /// <para>
        /// Index and wire value coincide here (0,1,2,3), unlike the tier table this replaced. The
        /// pairing is still written out rather than cast, because "they happen to line up today"
        /// is exactly the assumption that made the old FPS tier wrong.
        /// </para>
        /// </remarks>
        private static readonly (int Override, string Label)[] IntelFrameGenSegmentOrder =
        {
            (0, "Off"),
            (1, "2x"),
            (2, "3x"),
            (3, "4x"),
        };

        private SegmentedControl _perfMode;
        private SegmentedControl _hwMouse;
        private SegmentedControl _cpuBoost;
        private SegmentedControl _powerMode;
        private SegmentedControl _intelEndurance;
        private SegmentedControl _intelEnduranceMode;
        private SegmentedControl _intelLowLatency;
        private SegmentedControl _intelFrameGen;
        private SegmentedControl _rtss;

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

                _hwMouse = new SegmentedControl(HwMouseSegments,
                    Array.ConvertAll(HwMouseSegmentOrder, segment => segment.Label));
                _hwMouse.Selected += OnHwMouseSelected;

                _cpuBoost = new SegmentedControl(CpuBoostSegments,
                    Array.ConvertAll(CpuBoostSegmentOrder, segment => segment.Label));
                _cpuBoost.Selected += OnCpuBoostSelected;

                _powerMode = new SegmentedControl(PowerModeSegments,
                    "Efficiency", "Balanced", "Performance");
                _powerMode.Selected += OnPowerModeSelected;

                // Endurance Gaming needs no mapping table: ctl_3d_endurance_gaming_control_t is
                // 0 = off, 1 = on, 2 = auto, already the order it reads in.
                _intelEndurance = new SegmentedControl(IntelEnduranceSegments,
                    "Off", "On", "Auto");
                _intelEndurance.Selected += OnIntelEnduranceSelected;

                // Likewise ctl_3d_endurance_gaming_mode_t: 0 = better performance, 1 = balanced,
                // 2 = maximum battery. Labelled for what they trade rather than IGCL's names.
                _intelEnduranceMode = new SegmentedControl(IntelEnduranceModeSegments,
                    "Performance", "Balanced", "Battery");
                _intelEnduranceMode.Selected += OnIntelEnduranceModeSelected;

                // Low latency needs no mapping table: IGCL numbers it 0 = Off, 1 = On,
                // 2 = On+Boost, which is already the order it reads in.
                _intelLowLatency = new SegmentedControl(IntelLowLatencySegments,
                    "Off", "On", "On + boost");
                _intelLowLatency.Selected += OnIntelLowLatencySelected;

                _intelFrameGen = new SegmentedControl(IntelFrameGenSegments,
                    Array.ConvertAll(IntelFrameGenSegmentOrder, segment => segment.Label));
                _intelFrameGen.Selected += OnIntelFrameGenSelected;

                _rtss = new SegmentedControl(RtssSegments,
                    Array.ConvertAll(CpuBoostSegmentOrder, segment => segment.Label));
                _rtss.Selected += OnRtssSelected;

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
            // window-level event does not know that. Getting it backwards means either polling for
            // a reader that is not there, or a pinned widget whose telemetry has silently stopped
            // updating.
            if (_widget != null)
                _widget.VisibleChanged += OnWidgetVisibleChanged;

            // Must be subscribed before the snapshot can arrive: it is what retries the initial
            // focus once the cards have real sizes. See OnLayoutUpdated.
            LayoutUpdated += OnLayoutUpdated;

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
            LayoutUpdated -= OnLayoutUpdated;

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

                // Being shown again is the moment to put focus back - see ArmInitialFocus.
                if (visible)
                    await RunOnUiAsync(ArmInitialFocus);
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

                // Gates the focus attempt: before the first snapshot the cards' visibility is not
                // known, and focusing a control that is about to be hidden is worse than waiting.
                //
                // Focus is NOT set here. ApplyCaps has just unhidden the cards and layout has not
                // run yet, so every control inside a newly-shown card still measures zero while the
                // never-hidden Windows power card already has a real height - focusing from here
                // reliably skipped the top card and landed on CPU boost. OnLayoutUpdated does it
                // once the sizes are real.
                _snapshotApplied = true;
            });
        }

        /// <summary>
        /// Retries <see cref="SetInitialFocus"/> once layout has actually run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes the widget usable with a gamepad at all.</b> The focus attempt in
        /// <see cref="OnSnapshotApplied"/> runs in the same dispatcher callback that made the cards
        /// visible, and XAML defers layout to the next frame - so every candidate still measured
        /// <c>ActualHeight == 0</c>, the size guard skipped all of them, and nothing was focused.
        /// Nothing called it again either, because the snapshot normally arrives once.
        /// </para>
        /// <para>
        /// With no focused element there is no origin for XY focus navigation, so the D-pad had
        /// nothing to move from and the widget could only be driven with a mouse - which sets focus
        /// on click, hiding the bug completely on a desktop.
        /// </para>
        /// <para>
        /// <see cref="FrameworkElement.LayoutUpdated"/> fires after each layout pass, so the first
        /// one following the snapshot has real sizes. It unsubscribes as soon as focus lands, since
        /// it fires often and there is nothing left to do.
        /// </para>
        /// </remarks>
        private void OnLayoutUpdated(object sender, object e)
        {
            SetInitialFocus();

            if (_initialFocusSet)
                LayoutUpdated -= OnLayoutUpdated;
        }

        /// <summary>
        /// Re-arms focus selection after Game Bar shows the widget again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Focus is a property of the live visual tree, not something the widget owns: when Game
        /// Bar hides us the focused element stops being focused, and nothing puts it back. Without
        /// this the very first show worked and every subsequent one was dead - the same symptom as
        /// having no focus code at all, because <see cref="_initialFocusSet"/> latched true on that
        /// first success and made <see cref="SetInitialFocus"/> a no-op forever after.
        /// </para>
        /// <para>
        /// Only ever triggered by BECOMING VISIBLE, never on a snapshot or a value change. Focus is
        /// the user's cursor on a device with no cursor, and moving it while they are working is
        /// worse than leaving it alone. Being re-shown is the one moment there is nothing to
        /// disturb.
        /// </para>
        /// </remarks>
        private void ArmInitialFocus()
        {
            _initialFocusSet = false;

            // Unsubscribe first: this can run many times over a session and handlers otherwise
            // stack up, one per show.
            LayoutUpdated -= OnLayoutUpdated;
            LayoutUpdated += OnLayoutUpdated;

            // Usually lands right here - on a re-show the tree is already laid out and the sizes
            // are real. The subscription above is for the first show, where they are not.
            SetInitialFocus();
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
        /// Set once per SHOW, not once per session, and never on a snapshot - re-focusing when a
        /// value arrives would yank focus out from under someone mid-adjustment.
        /// <see cref="ArmInitialFocus"/> owns the re-arming and explains the distinction.
        /// </para>
        /// </remarks>
        private void SetInitialFocus()
        {
            if (_initialFocusSet) return;

            // Nothing worth focusing until the helper has told us which cards exist - except the
            // banner's Retry button, which is the only control on screen when the helper is down
            // and would otherwise be unreachable without a mouse.
            if (!_snapshotApplied && StatusActionButton.Visibility != Visibility.Visible) return;

            // Top-down, so focus starts where the eye does. The mode segments come before the
            // sliders they gate because they are the top control in the top card AND they are on
            // screen whenever that card is, where the sliders are hidden outside Manual mode. The
            // Windows power card is the only one never hidden, so its segments are the guaranteed
            // fallback - every card above it collapses on an unsupported device.
            Control[] candidates =
            {
                StatusActionButton,
                _perfMode?.FirstSegment,
                Pl1Slider,
                _cpuBoost?.FirstSegment,
                _powerMode?.FirstSegment,
            };

            foreach (var control in candidates)
            {
                // A collapsed parent card leaves the control itself Visible, so the ancestor has
                // to be consulted - a non-zero size is the reliable signal here. It is also why
                // this cannot run before a layout pass; see OnLayoutUpdated.
                if (control == null) continue;
                if (control.Visibility != Visibility.Visible) continue;
                if (!control.IsEnabled) continue;
                if (control.ActualHeight <= 0) continue;

                // Keyboard, not Programmatic. Programmatic focus does not reveal the focus
                // rectangle, so on a device with no cursor the user gets a focused control they
                // cannot see and no way to tell navigation is working. This device is driven with
                // a stick; showing where focus is IS the feature.
                if (control.Focus(FocusState.Keyboard))
                {
                    _initialFocusSet = true;
                    return;
                }
            }
        }

        /// <summary>True once the helper's first snapshot has been applied and cards are settled.</summary>
        private bool _snapshotApplied;

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
            HwMouseCard.Visibility = Visible(caps.HasHwMouse);
            IntelCard.Visibility =
                Visible(caps.HasIgcl && _connection.IsAvailable(Function.IntelEnduranceGaming));

            // No capability gate: nothing acts on this yet, so there is no driver or device
            // support to check. It becomes conditional when the behaviour is decided.
            RtssCard.Visibility = Visible(_connection.IsAvailable(Function.RtssEnabled));
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
            ApplyValue(Function.HwMouseMode);
            ApplyValue(Function.CpuBoost);
            ApplyValue(Function.OsPowerMode);
            ApplyValue(Function.IntelEnduranceGaming);
            ApplyValue(Function.IntelEnduranceGamingMode);
            ApplyValue(Function.IntelLowLatency);
            ApplyValue(Function.IntelFrameGeneration);
            ApplyValue(Function.RtssEnabled);
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


                case Function.HwMouseMode:
                {
                    // Pushed on the helper's telemetry tick, not just at connect, because the
                    // physical MSI button changes this without going through the widget.
                    bool desktopMode = _connection.GetBool(Function.HwMouseMode);
                    int segment = Array.FindIndex(HwMouseSegmentOrder, s => s.DesktopMode == desktopMode);
                    if (segment >= 0) _hwMouse.Show(segment);
                    break;
                }

                case Function.CpuBoost:
                {
                    bool boost = _connection.GetBool(Function.CpuBoost);
                    int segment = Array.FindIndex(CpuBoostSegmentOrder, s => s.Enabled == boost);
                    if (segment >= 0) _cpuBoost.Show(segment);
                    break;
                }

                case Function.OsPowerMode:
                    _powerMode.Show(Clamp(_connection.GetInt(Function.OsPowerMode, 1), 0, 2));
                    break;

                case Function.IntelEnduranceGaming:
                    _intelEndurance.Show(Clamp(_connection.GetInt(Function.IntelEnduranceGaming, 0), 0, 2));
                    break;

                case Function.IntelEnduranceGamingMode:
                    _intelEnduranceMode.Show(
                        Clamp(_connection.GetInt(Function.IntelEnduranceGamingMode, 1), 0, 2));
                    break;

                case Function.IntelLowLatency:
                    _intelLowLatency.Show(Clamp(_connection.GetInt(Function.IntelLowLatency, 0), 0, 2));
                    break;

                case Function.IntelFrameGeneration:
                {
                    int value = _connection.GetInt(Function.IntelFrameGeneration, 0);
                    int segment = Array.FindIndex(IntelFrameGenSegmentOrder, s => s.Override == value);
                    if (segment >= 0) _intelFrameGen.Show(segment);
                    break;
                }

                case Function.RtssEnabled:
                {
                    bool enabled = _connection.GetBool(Function.RtssEnabled);
                    int segment = Array.FindIndex(CpuBoostSegmentOrder, s => s.Enabled == enabled);
                    if (segment >= 0) _rtss.Show(segment);
                    break;
                }

                case Function.MsiCenterRunning:
                    // Inverted on purpose. MSI Center M is a dependency, not a rival: its service is
                    // what applies the power limits we write. Warn when it is MISSING.
                    MsiCenterWarning.Visibility = Visible(!_connection.GetBool(Function.MsiCenterRunning));
                    break;
            }
        }

        // ── User input ──────────────────────────────────────────────────────────
        // Every handler starts with the same guard. See _applyingFromHelper.

        /// <summary>
        /// Paints the mode selector and shows or hides the sliders it gates.
        /// </summary>
        /// <remarks>
        /// The sliders are HIDDEN outside Manual mode, not greyed. They were greyed at first, on
        /// the argument that a disabled control next to the mode that disabled it explains itself
        /// - but on an 8-inch screen it mostly read as clutter, and the three mode buttons sitting
        /// directly above make "Manual is the one with sliders" obvious after a single press. The
        /// card collapsing to a single row is also a clearer signal that MSI is driving power than
        /// a paragraph of warning text was.
        ///
        /// Endurance Gaming, in the Graphics card, is gated by the same mode but is DISABLED
        /// rather than hidden. Different card, different reasoning: hiding two rows out of the
        /// middle of Graphics would make the card jump around as the mode changes, and unlike the
        /// sliders these rows are worth reading even when MSI owns them.
        /// </remarks>
        private void ApplyPerfMode(PerfMode mode)
        {
            bool manual = mode == PerfMode.UserScenario;

            // Unknown is not on the control: MSI reported a mode we do not model, so leave the
            // selection alone rather than misrepresenting it as one of the three we do.
            int segment = Array.FindIndex(PerfModeSegmentOrder, s => s.Mode == mode);
            if (segment >= 0)
                _perfMode.Show(segment);

            PowerLimitControls.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;

            // MSI's Endurance and AI Engine modes drive Intel's Endurance Gaming themselves, so
            // outside Manual there would be two owners for one setting and the widget would be
            // the one that loses. The labels grey with the buttons, or a live label sits above a
            // dead row and reads like a rendering fault.
            SetEnduranceEnabled(manual);
        }

        private void SetEnduranceEnabled(bool enabled)
        {
            if (_intelEndurance != null) _intelEndurance.IsEnabled = enabled;
            if (_intelEnduranceMode != null) _intelEnduranceMode.IsEnabled = enabled;

            double opacity = enabled ? 1.0 : 0.4;
            IntelEnduranceLabel.Opacity = opacity;
            IntelEnduranceModeLabel.Opacity = opacity;
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
        private bool _syncingLimits;

        private async void Pl1Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper || _syncingLimits) return;

            // The OTHER slider's current value is passed in, not recomputed, because the two limits
            // are independent - PL2 keeps whatever gap the user opened unless PL1 rises into it.
            int pl1 = (int)e.NewValue;
            int pl2 = (int)Pl2Slider.Value;
            _connection.Caps.ConstrainFromPl1(ref pl1, ref pl2);

            await ApplyLimitPairAsync(pl1, pl2);
        }

        private async void Pl2Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_applyingFromHelper || _syncingLimits) return;

            int pl1 = (int)Pl1Slider.Value;
            int pl2 = (int)e.NewValue;
            _connection.Caps.ConstrainFromPl2(ref pl1, ref pl2);

            await ApplyLimitPairAsync(pl1, pl2);
        }

        /// <summary>
        /// Paints the resulting pair and sends both halves.
        /// </summary>
        /// <remarks>
        /// Both are always sent, whichever slider the user touched. Most gestures now move only one
        /// limit, but one CAN still move both - raising PL1 into PL2 carries PL2 up with it - and
        /// sending only the moved one would leave the helper holding a pair the widget is no longer
        /// showing.
        ///
        /// PL1 goes first: the helper clamps PL2 to at least PL1 + the firmware headroom, so
        /// raising PL1 before PL2 never transits through a pair that gets clamped and echoed back.
        /// </remarks>
        private async Task ApplyLimitPairAsync(int pl1, int pl2)
        {
            _syncingLimits = true;
            try
            {
                Pl1Slider.Value = pl1;
                Pl2Slider.Value = pl2;
                Pl1ValueText.Text = $"{pl1} W";
                Pl2ValueText.Text = $"{pl2} W";
            }
            finally
            {
                _syncingLimits = false;
            }

            await SendAsync(Function.Pl1, pl1);
            await SendAsync(Function.Pl2, pl2);
        }

        private async void OnHwMouseSelected(int segment)
        {
            if (_applyingFromHelper) return;
            if (segment < 0 || segment >= HwMouseSegmentOrder.Length) return;

            _hwMouse.Show(segment);
            await SendAsync(Function.HwMouseMode, HwMouseSegmentOrder[segment].DesktopMode);
        }

        private async void OnCpuBoostSelected(int segment)
        {
            if (_applyingFromHelper) return;
            if (segment < 0 || segment >= CpuBoostSegmentOrder.Length) return;

            _cpuBoost.Show(segment);
            await SendAsync(Function.CpuBoost, CpuBoostSegmentOrder[segment].Enabled);
        }

        private async void OnPowerModeSelected(int index)
        {
            if (_applyingFromHelper) return;
            _powerMode.Show(index);
            await SendAsync(Function.OsPowerMode, index);
        }

        private async void OnIntelEnduranceSelected(int segment)
        {
            if (_applyingFromHelper) return;

            _intelEndurance.Show(segment);
            await SendAsync(Function.IntelEnduranceGaming, segment);
        }

        private async void OnIntelEnduranceModeSelected(int segment)
        {
            if (_applyingFromHelper) return;

            _intelEnduranceMode.Show(segment);
            await SendAsync(Function.IntelEnduranceGamingMode, segment);
        }

        private async void OnIntelFrameGenSelected(int segment)
        {
            if (_applyingFromHelper) return;
            if (segment < 0 || segment >= IntelFrameGenSegmentOrder.Length) return;

            _intelFrameGen.Show(segment);
            await SendAsync(Function.IntelFrameGeneration, IntelFrameGenSegmentOrder[segment].Override);
        }

        private async void OnRtssSelected(int segment)
        {
            if (_applyingFromHelper) return;
            if (segment < 0 || segment >= CpuBoostSegmentOrder.Length) return;

            _rtss.Show(segment);
            await SendAsync(Function.RtssEnabled, CpuBoostSegmentOrder[segment].Enabled);
        }

        private async void OnIntelLowLatencySelected(int segment)
        {
            if (_applyingFromHelper) return;

            _intelLowLatency.Show(segment);
            await SendAsync(Function.IntelLowLatency, segment);
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
