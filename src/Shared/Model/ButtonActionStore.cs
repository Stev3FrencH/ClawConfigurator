using System;
using System.IO;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// What the hardware button does, as a file a person is expected to open and edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same arrangement as <see cref="FanProfileStore"/> and <see cref="LightingProfileStore"/>, and
    /// for the same reason: there is no room on a Game Bar card in compact mode for a control that is
    /// set once and then forgotten, and the file IS the advanced UI.
    /// </para>
    /// <para>
    /// <b>Read at the moment of the press</b>, not cached at startup — so editing the file and
    /// pressing the button is one action with nothing to restart.
    /// </para>
    /// <para>
    /// Seeded on first run to <c>none</c>. The button did nothing before this existed, and a fresh
    /// install quietly acquiring a button that changes fan curves would be a surprise rather than a
    /// feature — the user has to ask for it.
    /// </para>
    /// </remarks>
    public sealed class ButtonActionStore
    {
        private const string ActionName = "Button.txt";
        private const string ReadmeName = "README.txt";

        private readonly string _directory;

        public ButtonActionStore(string directory) => _directory = directory;

        public string Directory => _directory;

        public string ActionPath => Path.Combine(_directory, ActionName);

        public string ReadmePath => Path.Combine(_directory, ReadmeName);

        /// <summary>
        /// Creates the action file if it does not exist, and refreshes the README.
        /// </summary>
        /// <remarks>
        /// The README is rewritten every time, the action file never is. The README is ours and
        /// should track the build; the choice is the user's. Deleting the file is therefore a
        /// supported way to get back to "does nothing".
        /// </remarks>
        public void EnsureSeeded(Action<string> log = null)
        {
            System.IO.Directory.CreateDirectory(_directory);

            if (!File.Exists(ActionPath)
                && TryWrite(ActionPath, DefaultContent(), log)
                && log != null)
            {
                log("Created the button action file at " + ActionPath + ".");
            }

            TryWrite(ReadmePath, Readme(), log);
        }

        /// <summary>
        /// Reads the configured action, falling back to <see cref="ButtonAction.None"/>.
        /// </summary>
        /// <remarks>
        /// A file that cannot be read or parsed reports what it ignored and does nothing. Doing
        /// nothing is the right failure here: a button whose meaning is in doubt should not guess,
        /// because every action it could pick changes the machine.
        /// </remarks>
        public ButtonAction Load(Action<string> log = null)
        {
            string text;
            try
            {
                if (!File.Exists(ActionPath)) return ButtonAction.None;
                text = File.ReadAllText(ActionPath);
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not read " + ActionPath + ": " + ex.Message + ". The button will do nothing.");
                return ButtonAction.None;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                // Tolerate "Action = rtss-overlay" as well as a bare value. The README shows the
                // bare form; people write the other one anyway.
                int equals = line.IndexOf('=');
                if (equals >= 0) line = line.Substring(equals + 1).Trim();

                if (ButtonActions.TryParse(line, out var action)) return action;

                log?.Invoke($"Ignored '{line}' in {ActionPath}: not an action name. The button will do nothing.");
                return ButtonAction.None;
            }

            return ButtonAction.None;
        }

        private static bool TryWrite(string path, string content, Action<string> log)
        {
            try
            {
                File.WriteAllText(path, content);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("Could not write " + path + ": " + ex.Message);
                return false;
            }
        }

        private static string DefaultContent() =>
            "# What the MSI hardware button does. See README.txt for the options.\r\n"
            + "none\r\n";

        private static string Readme() =>
            "The hardware button\r\n"
            + "===================\r\n"
            + "\r\n"
            + "Put ONE action name in Button.txt. Lines starting with # are ignored.\r\n"
            + "The file is read when you press the button, so edit it and press - nothing to restart.\r\n"
            + "\r\n"
            + "  none              Do nothing. The default.\r\n"
            + "  rtss-overlay      Toggle RivaTuner Statistics Server's on-screen display.\r\n"
            + "  fan-profile       Cycle the fan profile: Auto, custom, Auto...\r\n"
            + "  performance-mode  Cycle the performance mode: Endurance, AI Engine, Manual.\r\n"
            + "  lighting          Cycle the lighting: off, then each profile in turn.\r\n"
            + "  controller-mode   Toggle the controller between Gamepad and Desktop.\r\n"
            + "\r\n"
            + "\r\n"
            + "About this button\r\n"
            + "-----------------\r\n"
            + "\r\n"
            + "It was never broken. The button raises a firmware event and does nothing else - it\r\n"
            + "reports, and leaves the decision to software. MSI Center M was the only thing\r\n"
            + "subscribed, so uninstalling it left the event firing into an empty room.\r\n"
            + "\r\n"
            + "\r\n"
            + "rtss-overlay\r\n"
            + "------------\r\n"
            + "\r\n"
            + "Needs RivaTuner Statistics Server installed and running. It calls the same function\r\n"
            + "RTSS's own hotkey handler calls, so it behaves exactly as pressing RTSS's OSD toggle\r\n"
            + "hotkey would - but WITHOUT generating a keystroke.\r\n"
            + "\r\n"
            + "That difference is deliberate. Synthesising a hotkey would put the keypress into the\r\n"
            + "system input queue, where the game in the foreground receives it too, flagged as\r\n"
            + "injected input. This button is pressed while games are running, so that exposure would\r\n"
            + "be constant rather than occasional. Talking to RTSS directly means nothing reaches the\r\n"
            + "input layer at all.\r\n"
            + "\r\n"
            + "For the same reason there is no 'send a hotkey' action here, tempting as it is.\r\n"
            + "\r\n"
            + "\r\n"
            + "If a press does nothing\r\n"
            + "-----------------------\r\n"
            + "\r\n"
            + "Every press is logged, whatever the outcome. Look in helper.log two folders up for\r\n"
            + "lines starting 'Button'. A press that never reaches the log is a different problem\r\n"
            + "from one that reaches it and fails.\r\n";
    }
}
