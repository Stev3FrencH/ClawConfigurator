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
            InitializeComponent();

            UnhandledException += (_, e) =>
            {
                // A widget that dies takes the Game Bar panel with it and gives the user no clue
                // why. Log and keep running: a broken control is better than a blank frame.
                System.Diagnostics.Debug.WriteLine($"[app] unhandled: {e.Exception}");
                e.Handled = true;
            };
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
                Window.Current.Content = rootFrame;
            }

            _widget = new XboxGameBarWidget(
                args as XboxGameBarWidgetActivatedEventArgs,
                Window.Current.CoreWindow,
                rootFrame);

            rootFrame.Navigate(typeof(MainWidget), _widget);

            Window.Current.Closed += (_, __) =>
            {
                _widget?.Close();
                _widget = null;
            };

            Window.Current.Activate();
        }
    }
}
