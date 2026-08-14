using System;
using System.Globalization;
using System.Management;
using System.Threading;

namespace McenterLite.Probe.Commands
{
    /// <summary>
    /// Subscribes to <c>MSI_Event</c> and prints every notification the firmware raises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> A hardware button stopped doing anything when MSI Center M was
    /// uninstalled on 2026-08-13, while still feeling like it was trying to. The most likely reason
    /// is that the button does not "do" anything by itself: it raises a firmware event, and MSI
    /// Center M's service was the only thing subscribed. If so, nothing is broken and the button is
    /// available to us — subscribing is all that was ever required.
    /// </para>
    /// <para>
    /// <c>MSI_Event</c> derives from <c>WMIEvent</c> and thence <c>__ExtrinsicEvent</c>, so it is a
    /// genuine WMI event class rather than a pollable table, and it carries exactly one payload:
    /// <c>MSIEvt</c>, a <c>UInt32</c> event code. Its GUID
    /// <c>{5B3CC38A-40D9-7245-8AE6-1145B751BE3F}</c> links it to a <c>_WDG</c> entry in the ACPI
    /// tables, the same mechanism behind <c>MSI_ACPI</c>.
    /// </para>
    /// <para>
    /// <b>Strictly read-only.</b> Subscribing to an event receives; it cannot write. This is the
    /// safest command in the probe.
    /// </para>
    /// <para>
    /// <b>Press a button you know works as a control.</b> The physical MSI button switches
    /// controller mode and still works with MSI Center M gone, because the firmware handles that
    /// one itself. If it raises an event here too, the listener is proven and the codes can be told
    /// apart; if nothing at all arrives for any button, the channel is wrong rather than the theory
    /// — try <c>--hid-watch</c> next, since the button may be a HID report instead.
    /// </para>
    /// </remarks>
    internal static class EventWatcher
    {
        private const string Namespace = @"\\.\root\wmi";
        private const string EventClass = "MSI_Event";
        private const string PayloadProperty = "MSIEvt";

        private const int DefaultSeconds = 30;

        public static int Run(string[] args)
        {
            int seconds = DefaultSeconds;
            if (args.Length >= 1 &&
                int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 0)
            {
                seconds = parsed;
            }

            Console.WriteLine($"Listening on {EventClass} for {seconds}s. Press the buttons now.");
            Console.WriteLine();
            Console.WriteLine("  Try each one, and say out loud which is which - the codes mean nothing");
            Console.WriteLine("  without knowing which press produced them. The MSI button is a useful");
            Console.WriteLine("  control: it still works, so it proves the listener if it reports here.");
            Console.WriteLine();

            int received = 0;

            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                using var watcher = new ManagementEventWatcher(
                    scope, new WqlEventQuery($"SELECT * FROM {EventClass}"));

                watcher.EventArrived += (_, e) =>
                {
                    Interlocked.Increment(ref received);
                    Report(e.NewEvent);
                };

                watcher.Start();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                watcher.Stop();
            }
            catch (ManagementException ex)
            {
                Console.Error.WriteLine($"Could not subscribe to {EventClass}: {ex.Message}");
                return 1;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Access denied subscribing to {EventClass}. Run elevated.");
                return 1;
            }

            Console.WriteLine();
            if (received == 0)
            {
                Console.WriteLine("Nothing arrived.");
                Console.WriteLine();
                Console.WriteLine("That is a real result, not a failure. Either these buttons do not use");
                Console.WriteLine("this channel, or something has to enable event reporting first - MSI");
                Console.WriteLine("Center M may have been doing that as well as listening. Try --hid-watch");
                Console.WriteLine("next: the button may raise a vendor HID report instead.");
                return 0;
            }

            Console.WriteLine($"{received} event(s) received.");
            return 0;
        }

        private static void Report(ManagementBaseObject e)
        {
            string code = "?";
            try
            {
                var raw = e[PayloadProperty];
                if (raw != null)
                {
                    uint value = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
                    code = $"0x{value:X2} ({value})";
                }
            }
            catch (Exception)
            {
                // A property we cannot read is worth reporting as unknown rather than swallowing
                // the whole event - the arrival itself is the finding.
            }

            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {PayloadProperty} = {code}");

            // Dump everything else too. The class declares one payload property, but an event is
            // cheap to over-report and expensive to miss.
            foreach (var property in e.Properties)
            {
                if (property.Name == PayloadProperty) continue;
                if (property.Name == "SECURITY_DESCRIPTOR") continue;
                if (property.Value == null) continue;

                Console.WriteLine($"                  {property.Name} = {property.Value}");
            }
        }
    }
}
