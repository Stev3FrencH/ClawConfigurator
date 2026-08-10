using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace McenterLite.Widget
{
    /// <summary>
    /// Application entry point.
    ///
    /// <para>
    /// This app is only ever activated by the Game Bar, never launched normally, so the usual
    /// <c>OnLaunched</c> path is not the one that matters - <see cref="OnActivated"/> is. The
    /// manifest also hides it from the app list for the same reason.
    /// </para>
    /// </summary>
    sealed partial class App : Application
    {
        private XboxGameBarWidget _widget;

        public App()
        {
            UnhandledException += (_, e) =>
            {
                // A widget that dies takes the Game Bar panel with it and gives the user no clue
                // why. Log and keep running: a broken control is better than a blank frame.
                //
                // Debug.WriteLine alone is a no-op in Release builds, so it goes to a file too -
                // otherwise this handler silently eats the exception with no trace anywhere.
                System.Diagnostics.Debug.WriteLine($"[app] unhandled: {e.Exception}");
                LogCrash("App.UnhandledException", e.Exception);
                e.Handled = true;
            };

            try
            {
                // Parses App.xaml, including its self-contained palette/card resource dictionary -
                // the highest-risk XAML in the app, per docs/building-the-widget.md, and the first
                // thing that runs, before OnActivated or any Frame.Navigate machinery exists to
                // catch it.
                InitializeComponent();
            }
            catch (Exception ex)
            {
                LogCrash("App.ctor/InitializeComponent", ex);
                throw;
            }
        }

        /// <summary>
        /// Writes a locally-caught exception to disk with its real stack trace.
        /// </summary>
        /// <remarks>
        /// Application.UnhandledException hands back an exception reconstructed across the
        /// WinRT/XAML boundary that carries the type and message but not a usable stack trace.
        /// Catching close to the throw site - in particular Frame.NavigationFailed, which is where
        /// a Page constructor's exception actually surfaces rather than throwing synchronously out
        /// of Navigate() - is the only way to get one.
        /// </remarks>
        internal static void LogCrash(string context, Exception ex)
        {
            try
            {
                // TargetSite/Source sometimes survive even when StackTrace does not, for
                // exceptions reconstructed across a WinRT ABI boundary - worth logging explicitly
                // rather than relying on ToString() to surface them.
                string targetSite;
                try { targetSite = ex.TargetSite?.ToString() ?? "(null)"; }
                catch (Exception targetSiteEx) { targetSite = $"(threw: {targetSiteEx.Message})"; }

                var logPath = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "widget-crash.log");
                System.IO.File.AppendAllText(
                    logPath,
                    $"{DateTime.Now:O} [{context}] {ex}\n"
                    + $"  HResult={ex.HResult:X8} Source={ex.Source} TargetSite={targetSite}\n\n");
            }
            catch { /* best-effort */ }
        }

        /// <summary>Plain trace line, same file as LogCrash, for tracing normal (non-exception) flow.</summary>
        internal static void Log(string message)
        {
            try
            {
                var logPath = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "widget-crash.log");
                System.IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [trace] {message}\n");
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Handles a normal (non-Game-Bar) launch by closing immediately.
        /// </summary>
        /// <remarks>
        /// This app has no standalone UI - it is only ever meant to run inside Game Bar via
        /// <see cref="OnActivated"/>. The manifest hides it from the Start menu and app search for
        /// the same reason, but the "ActivateAfterInstall" extension property still triggers one
        /// normal Launch activation right after install/update, which previously left a bare
        /// top-level window stuck on the OS splash screen forever, since nothing ever called
        /// Window.Current.Activate() for that activation kind. Closing immediately here is
        /// harmless for that expected case and correct for any other way this could get launched
        /// outside Game Bar.
        /// </remarks>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Exit();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind != ActivationKind.Protocol) return;

            var protocolArgs = args as IProtocolActivatedEventArgs;
            if (protocolArgs == null) return;

            // The Game Bar passes the widget identity through the activation URI scheme, which is
            // what distinguishes this widget from any other the package might expose.
            var scheme = protocolArgs.Uri?.Scheme ?? "";
            if (!scheme.StartsWith("ms-gamebarwidget", StringComparison.OrdinalIgnoreCase)) return;

            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += (_, navArgs) =>
                {
                    // A Page constructor's exception surfaces here, not as a throw out of
                    // Navigate() below - this is the one place that reliably has a real trace.
                    LogCrash("Frame.NavigationFailed", navArgs.Exception);
                    navArgs.Handled = true;
                };
                Window.Current.Content = rootFrame;
            }

            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;

            try
            {
                _widget = new XboxGameBarWidget(
                    widgetArgs,
                    Window.Current.CoreWindow,
                    rootFrame);
            }
            catch (Exception ex)
            {
                LogCrash("OnActivated/new XboxGameBarWidget", ex);
                throw;
            }

            try
            {
                rootFrame.Navigate(typeof(MainWidget), _widget);
            }
            catch (Exception ex)
            {
                // Belt and braces alongside NavigationFailed above: some exception categories
                // during page construction throw synchronously out of Navigate() instead of
                // routing through the event.
                LogCrash("OnActivated/rootFrame.Navigate", ex);
                throw;
            }

            Window.Current.Closed += (_, __) =>
            {
                _widget?.Close();
                _widget = null;
            };

            try
            {
                // Activate() can force the first layout/render pass synchronously, which is where
                // a XAML resource or binding failure deferred past InitializeComponent() would
                // actually surface.
                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                LogCrash("OnActivated/Window.Current.Activate", ex);
                throw;
            }
        }
    }
}
