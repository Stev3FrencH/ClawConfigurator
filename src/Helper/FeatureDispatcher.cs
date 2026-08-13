using System;
using System.Collections.Generic;
using System.Text;
using McenterLite.Hardware;
using McenterLite.Helper.Settings;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Helper
{
    /// <summary>
    /// Routes one incoming message to the right hardware provider and produces the reply.
    ///
    /// <para>
    /// The helper is authoritative. Every <see cref="Command.Set"/> answers with the value the
    /// hardware ACTUALLY holds afterwards, read back rather than assumed, so the widget renders
    /// truth instead of its own optimism. Every value is clamped here, never only in the UI - the
    /// pipe admits any app package on the machine, so the slider bounds are a convenience and
    /// this is the enforcement point.
    /// </para>
    /// </summary>
    internal sealed class FeatureDispatcher
    {
        private readonly IHardware _hw;
        private readonly SettingsStore _settings;
        private readonly LightingProfileStore _lighting;
        private readonly FanProfileStore _fans;

        public FeatureDispatcher(
            IHardware hardware, SettingsStore settings,
            LightingProfileStore lighting, FanProfileStore fans)
        {
            _hw = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
            _fans = fans ?? throw new ArgumentNullException(nameof(fans));
        }

        /// <summary>Fan selection: Auto is MSI's factory curve, Custom is the file on disk.</summary>
        public const int FanAuto = 0;
        public const int FanCustom = 1;

        /// <summary>Set by the widget so telemetry is only pushed while anyone is looking.</summary>
        public bool WidgetVisible { get; private set; }

        public PipeEnvelope Handle(PipeEnvelope request)
        {
            switch (request.Cmd)
            {
                case Command.Get: return HandleGet(request);
                case Command.Set: return HandleSet(request);

                default:
                    // Response/Event/Error are helper-to-widget directions. Receiving one means a
                    // confused client; drop it rather than reply and risk a loop.
                    Log.Warn($"Ignoring an inbound {request.Cmd} for {request.Fn}.");
                    return null;
            }
        }

        // ── Reads ───────────────────────────────────────────────────────────────

        private PipeEnvelope HandleGet(PipeEnvelope request)
        {
            switch (request.Fn)
            {
                case Function.Hello:
                case Function.Snapshot:
                    return PipeEnvelope.Response(request.Id, Function.Snapshot, BuildSnapshot());

                case Function.DeviceCaps:
                    return PipeEnvelope.Response(request.Id, request.Fn, _hw.Caps.Serialize());

                default:
                    var value = ReadValue(request.Fn);
                    return value == null
                        ? PipeEnvelope.Failure(request.Id, request.Fn, "This value cannot be read on this device.")
                        : Ok(request, value);
            }
        }

        /// <summary>Reads one value, or null when it is unavailable.</summary>
        private string ReadValue(Function fn)
        {
            switch (fn)
            {
                case Function.Pl1:
                case Function.Pl2:
                    if (!_hw.Tdp.TryRead(out int pl1, out int pl2)) return null;
                    return PipeEnvelope.FromInt(fn == Function.Pl1 ? pl1 : pl2);

                case Function.TdpBackend:
                    return PipeEnvelope.FromEnum(_hw.Tdp.Backend);

                case Function.ChargeLimitPercent:
                    return _hw.ChargeLimit.TryRead(out int chargePercent)
                        ? PipeEnvelope.FromInt(chargePercent)
                        : null;

                case Function.HwMouseMode:
                    return _hw.HwMouse.TryRead(out bool desktopMode)
                        ? PipeEnvelope.FromBool(desktopMode)
                        : null;

                // Answered from settings, not from hardware: the controller stores keyframes and
                // has no idea which profile produced them. See Function.LightingProfile.
                case Function.LightingProfile:
                    return _hw.Rgb.Available
                        ? PipeEnvelope.FromInt(_settings.GetInt(SettingsKeys.LightingProfile, LightingProfileStore.OffSlot))
                        : null;

                // Re-read from disk every time rather than cached, so renaming a profile in its
                // file shows up on the next time the widget opens.
                case Function.LightingProfileNames:
                    return _hw.Rgb.Available ? BuildProfileNames() : null;

                // Answered from the HARDWARE, unlike the lighting profile above. The firmware
                // tracks whether it is honouring our tables, so "which profile is running" is a
                // real question with a real answer - and MSI Center M can change it behind us.
                case Function.FanProfile:
                    return _hw.Fan.Available ? PipeEnvelope.FromInt(ReadFanSelection()) : null;

                // Re-read from disk every time, so renaming the profile in its file shows up the
                // next time the widget opens.
                case Function.FanProfileName:
                    return _hw.Fan.Available
                        ? (_fans.Load().Name ?? "Custom").Replace(RecordSeparator, " ").Trim()
                        : null;

                case Function.FanProfileStopsAFan:
                    return _hw.Fan.Available
                        ? PipeEnvelope.FromBool(_fans.Load().StopsAFan)
                        : null;

                case Function.CpuBoost:
                    return _hw.Power.TryReadCpuBoost(out bool boost) ? PipeEnvelope.FromBool(boost) : null;

                case Function.OsPowerMode:
                    return _hw.Power.TryReadPowerMode(out var powerMode)
                        ? PipeEnvelope.FromEnum(powerMode)
                        : null;

                case Function.IntelFpsTier:
                case Function.IntelLowLatency:
                case Function.IntelFrameSync:
                case Function.IntelAdaptiveSharpness:
                case Function.IntelSaturation:
                case Function.IntelContrast:
                case Function.IntelGamma:
                    return _hw.Igcl.TryRead(fn, out int igclValue) ? PipeEnvelope.FromInt(igclValue) : null;

                case Function.WidgetVisible:
                    return PipeEnvelope.FromBool(WidgetVisible);

                default:
                    return null;
            }
        }

        // ── Writes ──────────────────────────────────────────────────────────────

        private PipeEnvelope HandleSet(PipeEnvelope request)
        {
            switch (request.Fn)
            {
                case Function.Hello:
                    return PipeEnvelope.Response(request.Id, Function.Snapshot, BuildSnapshot());

                case Function.WidgetVisible:
                    WidgetVisible = request.AsBool();
                    return Ok(request, PipeEnvelope.FromBool(WidgetVisible));

                case Function.Pl1:
                case Function.Pl2:
                    return SetTdp(request);

                case Function.ChargeLimitPercent:
                    return SetChargeLimit(request);

                case Function.LightingProfile:
                    return SetLightingProfile(request);

                case Function.FanProfile:
                    return SetFanProfile(request);

                case Function.HwMouseMode:
                    return Apply(request, _hw.HwMouse.Apply(request.AsBool()),
                        () => ReadValue(Function.HwMouseMode));

                case Function.CpuBoost:
                    return SetCpuBoost(request);

                case Function.OsPowerMode:
                    return SetPowerMode(request);

                case Function.IntelFpsTier:
                case Function.IntelLowLatency:
                case Function.IntelFrameSync:
                case Function.IntelAdaptiveSharpness:
                case Function.IntelSaturation:
                case Function.IntelContrast:
                case Function.IntelGamma:
                    return SetIgcl(request);

                case Function.PrepareForUninstall:
                    return RestoreEverything(request);

                default:
                    return PipeEnvelope.Failure(request.Id, request.Fn, "This value is read-only or unknown.");
            }
        }

        private PipeEnvelope SetTdp(PipeEnvelope request)
        {
            if (!_hw.Tdp.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.Tdp.UnavailableReason);

            if (!_hw.Tdp.TryRead(out int pl1, out int pl2))
                return PipeEnvelope.Failure(request.Id, request.Fn, "Could not read the current power limits.");

            // PL1 and PL2 are one hardware operation with a coupling rule between them, so a
            // change to either is applied as a pair rather than independently.
            if (request.Fn == Function.Pl1) pl1 = request.AsInt(pl1);
            else pl2 = request.AsInt(pl2);

            _hw.Caps.ClampPowerLimits(ref pl1, ref pl2);

            var result = _hw.Tdp.Apply(pl1, pl2);
            if (!result.Ok)
            {
                Log.Warn($"TDP write FAILED: asked {pl1}/{pl2} W via {_hw.Tdp.Backend} - {result.Error}");
                return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);
            }

            _settings.SetInt(SettingsKeys.Pl1, pl1);
            _settings.SetInt(SettingsKeys.Pl2, pl2);

            // Report what the hardware ended up at, which may not be what was asked for.
            _hw.Tdp.TryRead(out int actualPl1, out int actualPl2);

            // Logged on SUCCESS too, not just failure. Log.cs states this file exists for exactly
            // this - "what did we send and what came back" on a hardware write - and TDP was the
            // one write that recorded nothing at all, so a slider that appeared not to work left
            // behind no evidence of whether the value even reached the helper.
            Log.Info($"TDP {request.Fn} -> {pl1}/{pl2} W via {_hw.Tdp.Backend}; hardware reports {actualPl1}/{actualPl2} W.");

            return Ok(request, PipeEnvelope.FromInt(request.Fn == Function.Pl1 ? actualPl1 : actualPl2));
        }

        private PipeEnvelope SetChargeLimit(PipeEnvelope request)
        {
            if (!_hw.ChargeLimit.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.ChargeLimit.UnavailableReason);

            if (!_hw.ChargeLimit.TryRead(out int current))
                return PipeEnvelope.Failure(request.Id, request.Fn, "Could not read the current charge limit.");

            int percent = request.AsInt(current);
            _hw.Caps.ClampChargeLimit(ref percent);

            var result = _hw.ChargeLimit.Apply(percent);
            if (!result.Ok)
            {
                Log.Warn($"Charge limit write FAILED: asked {percent}% - {result.Error}");
                return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);
            }

            _settings.SetInt(SettingsKeys.ChargeLimit, percent);

            // Logged on success as well as failure, for the same reason TDP is. This is a hardware
            // write whose effect is invisible until the battery next reaches the threshold, so
            // "what did we send" is not reconstructable from the device afterwards.
            _hw.ChargeLimit.TryRead(out int actual);
            Log.Info($"Charge limit -> {percent}%; hardware reports {actual}%.");

            return Ok(request, PipeEnvelope.FromInt(actual));
        }

        /// <summary>
        /// Applies a lighting profile, or turns the lighting off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The profile file is read HERE, at the moment of the tap, rather than cached at startup.
        /// That is what makes the files a usable editing surface: save the file, tap the button,
        /// see the change - with nothing to restart.
        /// </para>
        /// <para>
        /// Nothing is captured for uninstall restore. Unlike the power and charge limits there is
        /// no prior value to put back: lighting lives in the controller's RAM, so the state before
        /// we touched it was itself written by whatever ran last, and a power cycle clears it
        /// regardless. Restoring would mean inventing a value, not returning one.
        /// </para>
        /// </remarks>
        private PipeEnvelope SetLightingProfile(PipeEnvelope request)
        {
            if (!_hw.Rgb.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.Rgb.UnavailableReason);

            int slot = request.AsInt(LightingProfileStore.OffSlot);
            if (slot < LightingProfileStore.OffSlot || slot > LightingProfileStore.ProfileCount)
                return PipeEnvelope.Failure(request.Id, request.Fn, $"There is no lighting profile {slot}.");

            var profile = slot == LightingProfileStore.OffSlot
                ? new LightingProfile { Name = "Off", Style = LightingStyle.Off }
                : _lighting.Load(slot, Log.Warn);

            var result = _hw.Rgb.Apply(LightingRenderer.Render(profile));
            if (!result.Ok)
            {
                Log.Warn($"Lighting write FAILED: profile {slot} '{profile.Name}' - {result.Error}");
                return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);
            }

            _settings.SetInt(SettingsKeys.LightingProfile, slot);

            Log.Info($"Lighting -> profile {slot} '{profile.Name}' ({profile.Style}).");

            return Ok(request, PipeEnvelope.FromInt(slot));
        }

        /// <summary>
        /// Applies a fan profile: Auto writes MSI's factory curve, Custom writes the file on disk.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both halves, every time.</b> Auto is not merely "stop applying ours" - it writes the
        /// factory table back AND hands the fans to the firmware, so nothing of ours is left behind
        /// in a register for whatever sets the flag next.
        /// </para>
        /// <para>
        /// The profile file is read HERE, at the moment of the tap, not cached at startup. That is
        /// what makes the file a usable editing surface: save, press Custom, hear the change.
        /// </para>
        /// <para>
        /// Nothing is captured for uninstall restore, and nothing needs to be: <b>Auto is itself
        /// the restore.</b> The factory table is a constant measured from this device rather than a
        /// value we have to remember having seen, so "put it back" is a button the user already has
        /// rather than state we have to keep.
        /// </para>
        /// </remarks>
        private PipeEnvelope SetFanProfile(PipeEnvelope request)
        {
            if (!_hw.Fan.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.Fan.UnavailableReason);

            int selection = request.AsInt(FanAuto);
            if (selection != FanAuto && selection != FanCustom)
                return PipeEnvelope.Failure(request.Id, request.Fn, $"There is no fan profile {selection}.");

            bool custom = selection == FanCustom;

            var profile = custom ? _fans.Load(Log.Warn) : FanProfile.Factory();

            var result = _hw.Fan.Apply(profile, custom);
            if (!result.Ok)
            {
                Log.Warn($"Fan write FAILED: '{profile.Name}' - {result.Error}");
                return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);
            }

            _settings.SetInt(SettingsKeys.FanProfile, selection);

            // Logged on success as well as failure, like TDP and the charge limit. A fan curve's
            // effect is invisible until the device gets hot, so "what did we send" cannot be
            // reconstructed afterwards from how the machine sounds.
            // The control state is logged as well as the duties. Writing the tables without it was
            // the whole of the bug this feature shipped with, and that was invisible in the log
            // precisely because the log only ever recorded the duties.
            Log.Info(
                $"Fan -> '{profile.Name}': fan 1 {profile.FormatDuties(1)}, fan 2 {profile.FormatDuties(2)}; "
                + (custom ? "fans follow this table." : "fans handed back to the firmware.")
                + (custom && profile.StopsAFan ? " WARNING: this profile stops a fan." : ""));

            // Report what the hardware ended up holding, not what was asked for.
            return Ok(request, PipeEnvelope.FromInt(ReadFanSelection()));
        }

        /// <summary>
        /// Which profile the fans are actually running: <see cref="FanAuto"/> or <see cref="FanCustom"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read from the firmware's own fan-control flag rather than from what we last wrote. MSI
        /// Center M owns the same hardware and does not know about us, so control it took back must
        /// show up as a changed selection instead of as our own stale optimism.
        /// </para>
        /// <para>
        /// This compared the live table against the factory one until the flag was found. That was
        /// the best evidence available at the time and it was wrong in both directions: it reported
        /// Custom whenever a table we had written was still sitting there unread by the EC, and it
        /// could not tell a custom profile that happened to equal the factory curve from Auto. The
        /// flag answers both exactly.
        /// </para>
        /// </remarks>
        private int ReadFanSelection() =>
            TryReadFanSelection(out int selection)
                ? selection
                : _settings.GetInt(SettingsKeys.FanProfile, FanAuto);

        /// <summary>
        /// The fan selection as the FIRMWARE reports it, with no fallback to what we last wrote.
        /// </summary>
        /// <remarks>
        /// The telemetry loop's shape, not the snapshot's: a tick that cannot read the flag has
        /// nothing to say and should stay quiet, where a snapshot still has to answer with
        /// something. Both go through here so the flag-to-selection mapping lives in one place.
        /// </remarks>
        public bool TryReadFanSelection(out int selection)
        {
            selection = FanAuto;

            if (!_hw.Fan.Available) return false;
            if (!_hw.Fan.TryReadCustomCurve(out bool custom)) return false;

            selection = custom ? FanCustom : FanAuto;
            return true;
        }

        /// <summary>The three profile names for the widget's buttons, U+001F separated.</summary>
        private string BuildProfileNames()
        {
            var names = new List<string>();
            foreach (var profile in _lighting.LoadAll())
            {
                // A name carrying the separator would split into two buttons and silently shift
                // every profile after it. The file is hand-edited, so this is reachable.
                names.Add((profile.Name ?? "Profile").Replace(RecordSeparator, " ").Trim());
            }

            return string.Join(RecordSeparator, names.ToArray());
        }

        private PipeEnvelope SetCpuBoost(PipeEnvelope request)
        {
            bool enabled = request.AsBool();

            var result = _hw.Power.ApplyCpuBoost(enabled);
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);

            _settings.SetBool(SettingsKeys.CpuBoost, enabled);

            // Only now may startup re-apply this. Before the user has touched it, the system-wide
            // boost mode is not ours to set. See SettingsKeys.CpuBoostUserModified.
            _settings.SetBool(SettingsKeys.CpuBoostUserModified, true);

            return Ok(request, PipeEnvelope.FromBool(
                _hw.Power.TryReadCpuBoost(out bool actual) ? actual : enabled));
        }

        private PipeEnvelope SetPowerMode(PipeEnvelope request)
        {
            var mode = request.AsEnum(OsPowerMode.Balanced);

            var result = _hw.Power.ApplyPowerMode(mode);
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);

            _settings.SetInt(SettingsKeys.OsPowerMode, (int)mode);

            return Ok(request, PipeEnvelope.FromEnum(
                _hw.Power.TryReadPowerMode(out var actual) ? actual : mode));
        }

        private PipeEnvelope SetIgcl(PipeEnvelope request)
        {
            if (!_hw.Igcl.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.Igcl.UnavailableReason);

            if (!_hw.Igcl.Supports(request.Fn))
                return PipeEnvelope.Failure(request.Id, request.Fn,
                    "This graphics feature is not supported by the installed driver.");

            var result = _hw.Igcl.Apply(request.Fn, request.AsInt());
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);

            _settings.SetInt(SettingsKeys.IntelPrefix + request.Fn, request.AsInt());

            return Ok(request, PipeEnvelope.FromInt(
                _hw.Igcl.TryRead(request.Fn, out int actual) ? actual : request.AsInt()));
        }

        /// <summary>
        /// Puts every feature back to <see cref="FeatureDefaults"/>, for a clean uninstall.
        /// </summary>
        /// <remarks>
        /// The work lives in <see cref="SettingsRestorer"/> because <c>--uninstall</c> needs it too,
        /// and used not to have it: this message was the only way in, and nothing but
        /// <c>Test-Helper.ps1 -Restore</c> ever sent it.
        /// </remarks>
        private PipeEnvelope RestoreEverything(PipeEnvelope request)
        {
            var problems = SettingsRestorer.RestoreAll(_hw, Log.Info);

            if (problems.Count > 0)
            {
                var message = "Some values could not be restored: " + string.Join("; ", problems);
                Log.Warn(message);
                return PipeEnvelope.Failure(request.Id, request.Fn, message);
            }

            Log.Info("Restored every feature to its default.");
            return Ok(request, "1");
        }

        // ── Snapshot ────────────────────────────────────────────────────────────

        /// <summary>
        /// Every readable value plus device capabilities, in ONE message.
        /// </summary>
        /// <remarks>
        /// The widget is suspended and restarted constantly, so connect cost is paid often.
        /// A Get per control would be roughly twenty round trips through an elevated process
        /// every time the Game Bar opens; this is one.
        /// </remarks>
        private string BuildSnapshot()
        {
            var sb = new StringBuilder(512);
            sb.Append("caps=").Append(Escape(_hw.Caps.Serialize()));

            foreach (var fn in SnapshotFunctions)
            {
                var value = ReadValue(fn);
                if (value == null) continue; // unavailable: omit rather than send a fake default
                sb.Append(RecordSeparator).Append((int)fn).Append('=').Append(Escape(value));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Records are separated by US (0x1F), a character no payload here contains. Anything
        /// that does is escaped, so a firmware-supplied string cannot corrupt the record framing.
        /// </summary>
        /// <summary>US (0x1F). Declared as an escape so it survives every editor, diff and shell.</summary>
        private const string RecordSeparator = "\u001F";

        private static string Escape(string value) =>
            value?.Replace("\\", "\\\\").Replace(RecordSeparator, "\\x1f") ?? "";

        private static readonly Function[] SnapshotFunctions =
        {
            Function.Pl1,
            Function.Pl2,
            Function.TdpBackend,
            Function.ChargeLimitPercent,
            Function.HwMouseMode,
            Function.LightingProfile,
            Function.LightingProfileNames,
            Function.FanProfile,
            Function.FanProfileName,
            Function.FanProfileStopsAFan,
            Function.CpuBoost,
            Function.OsPowerMode,
            Function.IntelFpsTier,
            Function.IntelLowLatency,
            Function.IntelFrameSync,
            Function.IntelAdaptiveSharpness,
            Function.IntelSaturation,
            Function.IntelContrast,
            Function.IntelGamma,
        };

        // ── Plumbing ────────────────────────────────────────────────────────────

        private static PipeEnvelope Ok(PipeEnvelope request, string value) =>
            PipeEnvelope.Response(request.Id, request.Fn, value);

        private PipeEnvelope Apply(PipeEnvelope request, OpResult result, Func<string> readBack)
        {
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);
            return Ok(request, readBack() ?? request.Value);
        }
    }
}
