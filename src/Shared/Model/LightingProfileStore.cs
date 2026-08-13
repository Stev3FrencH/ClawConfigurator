using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// The three lighting profiles on disk, as files a person is expected to open and edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The widget deliberately offers no colour picker - it switches between these three and off.
    /// Everything beyond that is done by editing these files, which is why they are plain
    /// commented text in a folder rather than an opaque blob: the file IS the advanced UI.
    /// </para>
    /// <para>
    /// Seeded on first run from <see cref="LightingProfile.Default"/>, which reproduces the three
    /// profiles MSI Center M had configured. <b>After that the user owns them</b> - a profile is
    /// never rewritten or "repaired" behind their back, because a file that edits itself back is
    /// worse than no file at all. A profile that fails to parse falls back field by field and
    /// reports what it ignored; see <see cref="LightingProfile.Parse"/>.
    /// </para>
    /// </remarks>
    public sealed class LightingProfileStore
    {
        public const int ProfileCount = 3;

        /// <summary>Slot used by the widget's off button. Not a file.</summary>
        public const int OffSlot = 0;

        private const string ReadmeName = "README.txt";

        private readonly string _directory;

        public LightingProfileStore(string directory)
        {
            _directory = directory;
        }

        public string Directory => _directory;

        public string PathFor(int slot) =>
            Path.Combine(_directory, "Profile_" + slot.ToString(CultureInfo.InvariantCulture) + ".txt");

        public string ReadmePath => Path.Combine(_directory, ReadmeName);

        /// <summary>
        /// Creates any profile file that does not exist yet, and refreshes the README.
        /// </summary>
        /// <remarks>
        /// The README is rewritten every time, the profiles never are. The README is ours and
        /// should track the build; the profiles are the user's and must not be touched once they
        /// exist. Missing files are recreated individually, so deleting one is a supported way to
        /// get its default back.
        /// </remarks>
        public void EnsureSeeded(Action<string> log = null)
        {
            System.IO.Directory.CreateDirectory(_directory);

            for (int slot = 1; slot <= ProfileCount; slot++)
            {
                var path = PathFor(slot);
                if (File.Exists(path)) continue;

                if (TryWrite(path, LightingProfile.Default(slot).Format(slot), log) && log != null)
                    log("Created lighting profile " + slot + " at " + path + ".");
            }

            TryWrite(ReadmePath, Readme(), log);
        }

        /// <summary>
        /// Writes a file, reporting failure rather than throwing.
        /// </summary>
        /// <remarks>
        /// Nothing here is worth taking the helper down for. A profile that cannot be written still
        /// applies from memory, so the lights work and the log says why the file did not change.
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
        /// Loads one profile, falling back to its default if the file is missing or unreadable.
        /// </summary>
        /// <remarks>
        /// <b>This is the recovery path.</b> Emptying a profile file or deleting it restores that
        /// slot's default AND rewrites the file, so "undo my bad edit" is one action in the folder
        /// the user is already in, with nothing to restart and no need to remember the syntax. An
        /// unreadable file - open in another program, or a permissions problem - falls back too but
        /// is deliberately NOT rewritten: that is a transient condition, and overwriting a file we
        /// merely failed to read would destroy work.
        /// </remarks>
        public LightingProfile Load(int slot, Action<string> log = null)
        {
            if (slot < 1 || slot > ProfileCount) return LightingProfile.Default(1);

            var path = PathFor(slot);

            string text;
            try
            {
                text = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                if (log != null) log("Could not read " + path + ": " + ex.Message + ". Using the default.");
                return LightingProfile.Default(slot);
            }

            // Missing, or emptied out. Whitespace counts as empty because select-all-and-delete is
            // what someone reaches for before they think to delete the file itself, and both should
            // mean the same thing.
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                var restored = LightingProfile.Default(slot);
                if (log != null) log("Profile " + slot + " was empty or missing; restoring its default.");
                TryWrite(path, restored.Format(slot), log);
                return restored;
            }

            List<string> problems;
            var profile = LightingProfile.Parse(text, slot, out problems);

            if (log != null)
            {
                foreach (var problem in problems) log("Profile " + slot + ": " + problem);
            }

            return profile;
        }

        public IList<LightingProfile> LoadAll(Action<string> log = null)
        {
            var profiles = new List<LightingProfile>();
            for (int slot = 1; slot <= ProfileCount; slot++) profiles.Add(Load(slot, log));
            return profiles;
        }

        private static string Readme()
        {
            return
@"McenterLite - lighting profiles
===============================

The widget's Lighting card switches between the three profiles in this folder, plus off.
There is no colour picker in the widget on purpose: this folder is where the detail lives.

Edit Profile_1.txt, Profile_2.txt or Profile_3.txt, save, then tap that profile in the
widget. The file is read at the moment you tap, so there is nothing to restart.


IF SOMETHING LOOKS WRONG
------------------------

Nothing you can type in these files can break the widget or the controller. A setting
that cannot be read is skipped and the previous value kept, so the worst case is a
profile that ignores part of your edit.

To undo an edit: DELETE THE FILE, or delete everything in it and save. Then tap that
profile. The default comes back, the file is rewritten, and there is nothing to restart.

To find out what was ignored, open  helper.log  in the folder ABOVE this one and look
for lines starting  Profile 1:  ,  Profile 2:  or  Profile 3:  . Every skipped setting
names itself there, with the value that was kept instead.

Two settings are valid but look exactly like a fault:  Style=Off  and  Brightness=0 .
Both turn the lights off. The log says so when it sees them.


SETTINGS
--------

Name          Text shown on the widget button.

Style         Off, Steady, Breath, ColorCycle or Wave.

Colors        A comma-separated list, e.g.  Colors=#FF0000, #00FF00
              Accepted spellings: #RRGGBB, RRGGBB, #RGB, or decimal R,G,B.
              LEAVE IT EMPTY to use the built-in palette for that style, which is what
              MSI Center did for its Wave and ColorCycle presets.

              How many colours a style uses:
                Steady       1
                Breath       up to 4, shown one at a time with a dark frame between
                ColorCycle   up to 3
                Wave         4, one per corner of each stick ring

Speed         Slow, Medium or Fast. Steady ignores it.

Direction     Clockwise or Counterclockwise. Wave only.

Brightness    0 to 100.


THE NINE LEDS
-------------

The controller has nine addressable LEDs: four around the left stick, four around the
right stick, and one behind the ABXY cluster. Styles paint all nine; there is no
per-LED setting in these files, because the firmware animates whole frames.


WHAT THE HARDWARE ACTUALLY STORES
---------------------------------

The controller does not know what ""Wave"" means. It stores up to eight keyframes of nine
colours each, plus a speed and a brightness, and it plays them in a loop. A style in this
file is a recipe for producing those keyframes, and the recipes are reproduced from MSI
Center M so that an existing profile looks exactly as it did before.

Settings are written to the controller's RAM, never its flash. That means lighting resets
if the controller loses power, and the helper re-applies your last choice when it starts.
This is deliberate - flash has a limited number of writes, and a button you can tap
repeatedly should not spend them.
";
        }
    }
}
