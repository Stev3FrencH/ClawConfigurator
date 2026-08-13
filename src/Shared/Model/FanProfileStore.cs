using System;
using System.Collections.Generic;
using System.IO;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// The custom fan profile on disk, as a file a person is expected to open and edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same arrangement as <see cref="LightingProfileStore"/>, and for the same reason: the
    /// widget has two buttons, Auto and Custom, and everything beyond that choice is done by
    /// editing this file. Fourteen duty values will not fit usefully on a Game Bar card in compact
    /// mode, and the file IS the advanced UI.
    /// </para>
    /// <para>
    /// Seeded on first run from <see cref="FanProfile.Default"/>. <b>After that the user owns it</b>
    /// — it is never rewritten or repaired behind their back. A profile that fails to parse falls
    /// back field by field and reports what it ignored.
    /// </para>
    /// <para>
    /// There is only one custom profile, not three. Lighting has three because switching look is
    /// something you do often and for fun; a fan curve is something you set once and leave, so a
    /// second slot would be a control with no occasion to use it.
    /// </para>
    /// </remarks>
    public sealed class FanProfileStore
    {
        private const string ProfileName = "Custom.txt";
        private const string ReadmeName = "README.txt";

        private readonly string _directory;

        public FanProfileStore(string directory)
        {
            _directory = directory;
        }

        public string Directory => _directory;

        public string ProfilePath => Path.Combine(_directory, ProfileName);

        public string ReadmePath => Path.Combine(_directory, ReadmeName);

        /// <summary>
        /// Creates the profile file if it does not exist, and refreshes the README.
        /// </summary>
        /// <remarks>
        /// The README is rewritten every time, the profile never is. The README is ours and should
        /// track the build; the profile is the user's. Deleting the profile is therefore a supported
        /// way to get the default back.
        /// </remarks>
        public void EnsureSeeded(Action<string> log = null)
        {
            System.IO.Directory.CreateDirectory(_directory);

            if (!File.Exists(ProfilePath)
                && TryWrite(ProfilePath, FanProfile.Default().Format(), log)
                && log != null)
            {
                log("Created the custom fan profile at " + ProfilePath + ".");
            }

            TryWrite(ReadmePath, Readme(), log);
        }

        /// <summary>
        /// Writes a file, reporting failure rather than throwing.
        /// </summary>
        /// <remarks>
        /// Nothing here is worth taking the helper down for. A profile that cannot be written still
        /// applies from memory, so the fans work and the log says why the file did not change.
        /// </remarks>
        private static bool TryWrite(string path, string content, Action<string> log)
        {
            try
            {
                File.WriteAllText(path, content);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("Could not write " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Loads the custom profile, falling back to the default if it is missing or unreadable.
        /// </summary>
        /// <remarks>
        /// <b>This is the recovery path.</b> Emptying the file or deleting it restores the default
        /// AND rewrites it, so "undo my bad edit" is one action in the folder the user is already
        /// in, with nothing to restart. An unreadable file — open elsewhere, or a permissions
        /// problem — falls back too but is deliberately NOT rewritten: that is transient, and
        /// overwriting a file we merely failed to read would destroy work.
        /// </remarks>
        public FanProfile Load(Action<string> log = null)
        {
            string text;
            try
            {
                text = File.Exists(ProfilePath) ? File.ReadAllText(ProfilePath) : null;
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("Could not read " + ProfilePath + ": " + ex.Message + ". Using the default.");

                return FanProfile.Default();
            }

            // Missing, or emptied out. Whitespace counts as empty because select-all-and-delete is
            // what someone reaches for before they think to delete the file itself.
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                var restored = FanProfile.Default();
                if (log != null) log("The custom fan profile was empty or missing; restoring the default.");
                TryWrite(ProfilePath, restored.Format(), log);
                return restored;
            }

            List<string> problems;
            var profile = FanProfile.Parse(text, out problems);

            if (log != null)
            {
                foreach (var problem in problems) log("Fan profile: " + problem);
            }

            return profile;
        }

        private static string Readme()
        {
            return
@"McenterLite - custom fan profile
================================

The widget's Fans card has two buttons, Auto and Custom, and an Apply button. Nothing
reaches the fans until you press Apply.

  Auto     MSI's own factory curve. This is what the device shipped with.
  Custom   The curve in Custom.txt, in this folder.

Edit Custom.txt, save, then press Custom and Apply in the widget. The file is read at the
moment you press Apply, so there is nothing to restart.


WHAT YOU ARE EDITING
--------------------

The device has TWO fans, and the firmware holds a small table for each one:

  an idle duty, used below 47 C
  then one duty at each of  47  50  57  64  71  78  C

Duty is a percentage, 0 to 100. The temperatures are FIXED - they are not editable, on this
device or in MSI Center M. Only the duties are yours.

MSI's own curve is idle 58, then 70;74;76;78;80;84. Auto puts exactly that back.

Use  Fan = ...  to set both fans at once, or  Fan1  and  Fan2  to set them separately.


WARNING: 0 STOPS THE FAN
------------------------

A duty of 0 stops that fan, in that temperature band, including under load. The firmware
accepts it and will NOT stop you - this was measured on this device, with both fans reading
zero on the tachometer. MSI Center M allows the same thing.

The widget shows a warning when the profile you are applying contains a 0, and the log says
so too. Neither will refuse it. If you did not mean it, press Auto and Apply.


IF SOMETHING LOOKS WRONG
------------------------

A setting that cannot be read is skipped and the previous value kept, so the worst case is a
profile that ignores part of your edit rather than one that does something wild.

To undo an edit: DELETE Custom.txt, or delete everything in it and save. Then press Custom
and Apply. The default comes back and the file is rewritten.

To find out what was ignored, open  helper.log  in the folder ABOVE this one and look for
lines starting  Fan profile:  . Every skipped setting names itself there, with the value that
was kept instead.


WHAT THE HARDWARE ACTUALLY DOES
-------------------------------

There is no ""auto mode"" to switch into. The firmware always runs whatever table it was last
given, and Auto simply writes MSI's factory table back. That is why Auto is instant and why
nothing needs MSI Center M to be running.

MSI Center M, if it is still installed, may overwrite a curve you applied - it owns the same
hardware and does not know about us. If your curve stops behaving, press Apply again.
";
        }
    }
}
