using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace McenterLite.Helper.Settings
{
    /// <summary>
    /// Persisted settings, plus the original system values captured before we ever changed them.
    ///
    /// <para>
    /// The helper is the ONLY writer. The widget persists nothing functional - it receives a
    /// snapshot on connect and is a pure view. That single rule removes the failure family where
    /// a XAML control's default value fires a change event during page construction and
    /// overwrites a stored setting before it has been loaded, which is the shape of most
    /// "my setting resets itself" bugs in this kind of app.
    /// </para>
    /// </summary>
    internal sealed class SettingsStore
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public SettingsStore(string directory)
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "settings.json");
        }

        public void Load()
        {
            lock (_gate)
            {
                _values.Clear();

                try
                {
                    if (!File.Exists(_path)) return;

                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return;

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            _values[property.Name] = property.Value.GetString();
                    }
                }
                catch (Exception ex)
                {
                    // A corrupt settings file must not stop the helper from starting. Starting
                    // with defaults is recoverable; refusing to start is not.
                    Log.Error("Could not read settings; continuing with defaults", ex);
                    _values.Clear();
                }
            }
        }

        public string Get(string key, string fallback = null)
        {
            lock (_gate)
                return _values.TryGetValue(key, out var value) ? value : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            var raw = Get(key);
            if (raw == "1") return true;
            if (raw == "0") return false;
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            var raw = Get(key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        public void Set(string key, string value)
        {
            lock (_gate)
            {
                if (value == null) _values.Remove(key);
                else _values[key] = value;
            }
            Save();
        }

        /// <summary>
        /// Forgets every remembered feature choice, so the next start re-applies nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Restoring the hardware is only half of putting the machine back.</b> Measured on
        /// device 2026-08-13: <c>--restore</c> wrote all six defaults correctly, and six seconds
        /// later the helper started, <c>StartupApplier</c> read these keys and re-applied 15/17 W,
        /// an 80% charge limit and the custom fan curve straight over the top. The restore was
        /// undone before the user could see it, and reported success on the way.
        /// </para>
        /// <para>
        /// This also protects the gap in a real uninstall, between <c>--uninstall</c> and removing
        /// the app: opening the Game Bar in that window redeploys the helper, and without this it
        /// would re-apply everything that was just restored.
        /// </para>
        /// </remarks>
        public void ClearFeatureSettings()
        {
            lock (_gate)
            {
                foreach (var key in FeatureKeys) _values.Remove(key);

                // Intel settings are written under a prefix rather than at fixed names, so they
                // have to be found rather than listed.
                var prefixed = new List<string>();
                foreach (var key in _values.Keys)
                {
                    if (key.StartsWith(SettingsKeys.IntelPrefix, StringComparison.Ordinal))
                        prefixed.Add(key);
                }

                foreach (var key in prefixed) _values.Remove(key);
            }

            Save();
            Log.Info("Cleared the saved feature settings; nothing will be re-applied at startup.");
        }

        /// <summary>
        /// Every key <see cref="StartupApplier"/> reads. Adding a re-applied setting without
        /// adding it here would leave that one feature reverting after a restore.
        /// </summary>
        private static readonly string[] FeatureKeys =
        {
            SettingsKeys.Pl1,
            SettingsKeys.Pl2,
            SettingsKeys.ChargeLimit,
            SettingsKeys.LightingProfile,
            SettingsKeys.FanProfile,
            SettingsKeys.CpuBoost,
            SettingsKeys.CpuBoostUserModified,
            SettingsKeys.OsPowerMode,
        };

        public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

        public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        // CaptureOriginal / GetOriginal lived here until 2026-08-13. They recorded the value each
        // setting held the first time this app was about to change it, and uninstall replayed them.
        //
        // Removed with the mechanism, not merely unused: while MSI Center M was installed, "the
        // value before we wrote" meant whatever MSI Center M happened to hold at that arbitrary
        // moment, so a restore returned the machine to a snapshot of a program that is now
        // uninstalled. On this device it would have replayed 15 W / 17 W, a 60% charge limit and
        // Best performance - none of them a neutral state. The Original_* keys were also stored in
        // settings.json inside the package's LocalCache, which Windows deletes when the app is
        // removed, so the one event they existed for was the event that destroyed them.
        //
        // Restore now applies FeatureDefaults, a table in code that cannot be lost. See
        // SettingsRestorer.

        private void Save()
        {
            lock (_gate)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(_values, options);

                    // Write-then-replace so a crash or power loss mid-write cannot leave a
                    // truncated settings file behind. On a handheld, sudden power loss is normal.
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, json, new UTF8Encoding(false));

                    if (File.Exists(_path)) File.Replace(temp, _path, null);
                    else File.Move(temp, _path);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not save settings", ex);
                }
            }
        }
    }

    /// <summary>Settings keys, in one place so a typo is a compile error rather than a lost setting.</summary>
    internal static class SettingsKeys
    {
        public const string Pl1 = "Pl1";
        public const string Pl2 = "Pl2";
        public const string TdpBackend = "TdpBackend";

        public const string ChargeLimit = "ChargeLimit";

        /// <summary>
        /// Which lighting profile is selected: 0 = off, 1-3 = that profile.
        /// </summary>
        /// <remarks>
        /// Persisted because the hardware cannot answer the question. The controller stores
        /// flattened keyframes with no profile number in them, so this setting is the only record
        /// of what the user chose - and it is what startup re-applies, since lighting is written
        /// to the controller's RAM and does not survive a power cycle.
        /// </remarks>
        public const string LightingProfile = "LightingProfile";

        /// <summary>
        /// Which fan profile the user last applied: 0 = Auto, 1 = the custom profile.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="LightingProfile"/> the hardware CAN answer this - the firmware runs
        /// whatever table it holds, and the helper compares it against the factory curve. This is
        /// persisted for a narrower job: knowing whether to re-apply at startup. Without it, a
        /// custom curve lost to a power cycle or overwritten by MSI Center M would silently become
        /// Auto, and the user would have no way to tell that from having chosen Auto.
        /// </remarks>
        public const string FanProfile = "FanProfile";

        public const string CpuBoost = "CpuBoost";
        public const string OsPowerMode = "OsPowerMode";

        public const string IntelPrefix = "Intel_";

        /// <summary>
        /// Set once the user has actually moved the CPU-boost control.
        /// </summary>
        /// <remarks>
        /// Until then the helper must NOT write boost mode at startup. Applying a default on
        /// every start would silently overwrite whatever the user had configured system-wide,
        /// which is a setting we do not own and had no instruction to change.
        /// </remarks>
        public const string CpuBoostUserModified = "CpuBoostUserModified";
    }
}
