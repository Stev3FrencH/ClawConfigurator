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

        public FeatureDispatcher(IHardware hardware, SettingsStore settings)
        {
            _hw = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

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

                case Function.MsiCenterRunning:
                    return Ok(request, PipeEnvelope.FromBool(_hw.IsMsiCenterRunning()));

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

                case Function.PerfMode:
                    return _hw.Tdp.TryReadMode(out var perfMode)
                        ? PipeEnvelope.FromEnum(perfMode)
                        : null;

                case Function.FanEnabled:
                    return _hw.Fan.TryReadState(out var fanEnabledState)
                        ? PipeEnvelope.FromBool(fanEnabledState.ControlEnabled)
                        : null;

                case Function.FanPreset:
                    // The EC stores a table, not a preset id, so the selection is ours to remember.
                    return PipeEnvelope.FromInt(_settings.GetInt(SettingsKeys.FanPreset, 0));

                case Function.FanState:
                    return _hw.Fan.TryReadState(out var fanState) ? fanState.Serialize() : null;

                case Function.FanFullSpeed:
                    return _hw.Fan.TryReadState(out var fullSpeedState)
                        ? PipeEnvelope.FromBool(fullSpeedState.FullSpeed)
                        : null;

                case Function.HwMouseMode:
                    return _hw.HwMouse.TryRead(out bool desktopMode)
                        ? PipeEnvelope.FromBool(desktopMode)
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

                case Function.MsiCenterRunning:
                    return PipeEnvelope.FromBool(_hw.IsMsiCenterRunning());

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

                case Function.PerfMode:
                    // Switching mode changes what MSI honours, and it moves settings beyond the
                    // one asked for - the power limits it reports can change with it. The widget
                    // re-reads the whole snapshot rather than assuming only this changed.
                    return Apply(request, _hw.Tdp.ApplyMode(request.AsEnum(PerfMode.UserScenario)),
                        () => ReadValue(Function.PerfMode));

                case Function.FanEnabled:
                    return Apply(request, _hw.Fan.SetEnabled(request.AsBool()), () =>
                    {
                        _settings.SetBool(SettingsKeys.FanEnabled, request.AsBool());
                        return ReadValue(Function.FanEnabled);
                    });

                case Function.FanPreset:
                    return SetFanPreset(request);

                case Function.FanFullSpeed:
                    return Apply(request, _hw.Fan.SetFullSpeed(request.AsBool()),
                        () => ReadValue(Function.FanFullSpeed));

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

                case Function.IntelThermalCmd:
                    // Stopping Intel's thermal stack is a system-level change and the escape
                    // hatch for a latched fan. Not yet implemented; it lands with fan control.
                    return PipeEnvelope.Failure(request.Id, request.Fn,
                        "Intel thermal control is not implemented yet.");

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

            _settings.CaptureOriginal(SettingsKeys.Pl1, PipeEnvelope.FromInt(pl1));
            _settings.CaptureOriginal(SettingsKeys.Pl2, PipeEnvelope.FromInt(pl2));

            // PL1 and PL2 are one hardware operation with a coupling rule between them, so a
            // change to either is applied as a pair rather than independently.
            if (request.Fn == Function.Pl1) pl1 = request.AsInt(pl1);
            else pl2 = request.AsInt(pl2);

            _hw.Caps.ClampPowerLimits(ref pl1, ref pl2);

            var result = _hw.Tdp.Apply(pl1, pl2);
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);

            _settings.SetInt(SettingsKeys.Pl1, pl1);
            _settings.SetInt(SettingsKeys.Pl2, pl2);

            // Report what the hardware ended up at, which may not be what was asked for.
            _hw.Tdp.TryRead(out int actualPl1, out int actualPl2);
            return Ok(request, PipeEnvelope.FromInt(request.Fn == Function.Pl1 ? actualPl1 : actualPl2));
        }

        private PipeEnvelope SetFanPreset(PipeEnvelope request)
        {
            if (!_hw.Fan.Available)
                return PipeEnvelope.Failure(request.Id, request.Fn, _hw.Fan.UnavailableReason);

            var preset = request.AsEnum(FanPreset.Default);

            // Capture the factory table before the first write, so uninstall can put it back.
            // Leaving a non-factory curve on the EC is a thermal problem, not a preference.
            if (_hw.Fan.TryReadState(out var before) && before.ReadOk && before.Table != null)
                _settings.CaptureOriginal(SettingsKeys.FanPreset,
                    new FanState { Table = before.Table, Temps = before.Temps, ReadOk = true }.Serialize());

            var result = _hw.Fan.ApplyPreset(preset);
            if (!result.Ok) return PipeEnvelope.Failure(request.Id, request.Fn, result.Error);

            _settings.SetInt(SettingsKeys.FanPreset, (int)preset);
            Log.Info($"Applied the {preset} fan preset.");

            return Ok(request, PipeEnvelope.FromEnum(preset));
        }

        private PipeEnvelope SetCpuBoost(PipeEnvelope request)
        {
            bool enabled = request.AsBool();

            if (_hw.Power.TryReadCpuBoost(out bool existing))
                _settings.CaptureOriginal(SettingsKeys.CpuBoost, PipeEnvelope.FromBool(existing));

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

            if (_hw.Power.TryReadPowerMode(out var existing))
                _settings.CaptureOriginal(SettingsKeys.OsPowerMode, PipeEnvelope.FromEnum(existing));

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
        /// Puts back every system value we captured before changing it, for a clean uninstall.
        /// </summary>
        private PipeEnvelope RestoreEverything(PipeEnvelope request)
        {
            var problems = new List<string>();

            // The fan first and unconditionally. Everything else here is a preference; a
            // non-factory fan table left on the EC is a thermal hazard.
            if (_hw.Fan.Available)
            {
                var fanResult = _hw.Fan.RestoreFactory();
                if (!fanResult.Ok) problems.Add($"fan: {fanResult.Error}");
            }

            // Power limits. Captured on the first write, restored here - without this the device
            // keeps whatever limit was last set forever, including after an uninstall, and the
            // user has no way back to the value they started with.
            var originalPl1 = _settings.GetOriginal(SettingsKeys.Pl1);
            var originalPl2 = _settings.GetOriginal(SettingsKeys.Pl2);
            if (originalPl1 != null && originalPl2 != null
                && int.TryParse(originalPl1, out int pl1) && int.TryParse(originalPl2, out int pl2)
                && _hw.Tdp.Available)
            {
                _hw.Caps.ClampPowerLimits(ref pl1, ref pl2);
                var r = _hw.Tdp.Apply(pl1, pl2);
                if (!r.Ok) problems.Add($"power limits: {r.Error}");
            }

            // Nothing to restore for the charge limit: the feature was removed, and this app never
            // wrote a value the user did not set in MSI Center themselves.

            var originalBoost = _settings.GetOriginal(SettingsKeys.CpuBoost);
            if (originalBoost != null)
            {
                var r = _hw.Power.ApplyCpuBoost(originalBoost == "1");
                if (!r.Ok) problems.Add($"CPU boost: {r.Error}");
            }

            var originalMode = _settings.GetOriginal(SettingsKeys.OsPowerMode);
            if (originalMode != null && int.TryParse(originalMode, out int modeValue) &&
                Enum.IsDefined(typeof(OsPowerMode), modeValue))
            {
                var r = _hw.Power.ApplyPowerMode((OsPowerMode)modeValue);
                if (!r.Ok) problems.Add($"power mode: {r.Error}");
            }

            if (problems.Count > 0)
            {
                var message = "Some values could not be restored: " + string.Join("; ", problems);
                Log.Warn(message);
                return PipeEnvelope.Failure(request.Id, request.Fn, message);
            }

            Log.Info("Restored all captured original values.");
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
            Function.PerfMode,
            Function.FanEnabled,
            Function.FanPreset,
            Function.FanState,
            Function.FanFullSpeed,
            Function.HwMouseMode,
            Function.CpuBoost,
            Function.OsPowerMode,
            Function.IntelFpsTier,
            Function.IntelLowLatency,
            Function.IntelFrameSync,
            Function.IntelAdaptiveSharpness,
            Function.IntelSaturation,
            Function.IntelContrast,
            Function.IntelGamma,
            Function.MsiCenterRunning,
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
