using System;
using System.Collections.Generic;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Fake
{
    /// <summary>
    /// An in-memory stand-in for the whole hardware layer, selected with <c>--fake-hardware</c>.
    ///
    /// <para>
    /// This is what makes the project developable. The IPC, the widget, MSIX packaging, the
    /// UAC-once deployment flow and the settings store are all far more fiddly than the hardware
    /// calls, and none of them need a Claw. With this in place they can be built and debugged in
    /// a plain Windows VM, and the device is reserved for what genuinely requires it.
    /// </para>
    ///
    /// <para>
    /// It models the awkward parts rather than just storing values - clamping, the performance-mode
    /// gate on manual power limits, and reads that reflect what was actually "applied". A fake that
    /// always succeeds cleanly would let bugs through that the real device then finds.
    /// </para>
    /// </summary>
    public sealed class FakeHardware : IHardware
    {
        public FakeHardware(bool simulateClaw8Ex = true)
        {
            Caps = new DeviceCaps
            {
                Model = simulateClaw8Ex ? "MSI Claw 8 EX AI+ CG3EM (simulated)" : "Generic PC (simulated)",
                Supported = simulateClaw8Ex,
                MinPl1 = 8,
                MaxPl1 = 35,
                MaxPl2 = 45,
                Pl2MinOffset = 2,
                TdpBackend = simulateClaw8Ex ? TdpBackendKind.Wmi : TdpBackendKind.Unavailable,
                HasChargeLimit = simulateClaw8Ex,
                MinChargeLimit = 50,
                MaxChargeLimit = 100,
                HasHwMouse = simulateClaw8Ex,
                HasRgb = simulateClaw8Ex,
                HasFan = simulateClaw8Ex,
                HasIgcl = simulateClaw8Ex,
            };

            Tdp = new FakeTdp(Caps);
            ChargeLimit = new FakeChargeLimit(simulateClaw8Ex, Caps);
            HwMouse = new FakeHwMouse(simulateClaw8Ex);
            Rgb = new FakeRgb(simulateClaw8Ex);
            Fan = new FakeFan(simulateClaw8Ex);
            Igcl = new FakeIgcl(simulateClaw8Ex);

            // Real on purpose. CPU boost and the power-mode overlay are plain Windows APIs, so
            // even a "fake hardware" run should exercise the genuine code path - on a VM that is
            // a real test, not a simulation.
            Power = new McenterLite.Hardware.Windows.WindowsPowerProvider();
        }

        public DeviceCaps Caps { get; }
        public ITdpProvider Tdp { get; }
        public IChargeLimitProvider ChargeLimit { get; }
        public IHwMouseProvider HwMouse { get; }
        public IRgbProvider Rgb { get; }
        public IFanProvider Fan { get; }
        public IPowerProvider Power { get; }
        public IIgclProvider Igcl { get; }
    }

    /// <summary>
    /// Holds two duty tables and the control flag in memory, starting as an untouched device does.
    /// </summary>
    /// <remarks>
    /// Starts on <see cref="FanProfile.Factory"/> with the flag CLEAR, which is what a device that
    /// has never been touched reports. The flag is modelled separately from the tables rather than
    /// inferred from them, because that separation is the whole point on real hardware: the tables
    /// can hold anything at all while the firmware runs its own curve.
    /// </remarks>
    internal sealed class FakeFan : IFanProvider
    {
        private readonly bool _available;
        private readonly FanProfile _state = FanProfile.Factory();
        private bool _customCurve;

        public FakeFan(bool available) => _available = available;

        public bool Available => _available;
        public string UnavailableReason => _available ? null : "No fan control on this device.";

        public bool TryRead(out FanProfile current)
        {
            current = null;
            if (!_available) return false;

            var copy = new FanProfile { Name = "Current" };
            for (int fan = 1; fan <= FanProfile.FanCount; fan++)
                Array.Copy(_state.Duties(fan), copy.Duties(fan), FanProfile.DutyCount);

            current = copy;
            return true;
        }

        public bool TryReadCustomCurve(out bool enabled)
        {
            enabled = _customCurve;
            return _available;
        }

        public OpResult Apply(FanProfile profile, bool customCurve)
        {
            if (!_available) return OpResult.Unavailable(UnavailableReason);
            if (profile == null) return OpResult.Fail("No fan profile was given.");

            for (int fan = 1; fan <= FanProfile.FanCount; fan++)
            {
                var source = profile.Duties(fan);
                var target = _state.Duties(fan);

                for (int i = 0; i < FanProfile.DutyCount; i++)
                {
                    int duty = source[i];
                    if (duty < FanProfile.MinDuty) duty = FanProfile.MinDuty;
                    if (duty > FanProfile.MaxDuty) duty = FanProfile.MaxDuty;
                    target[i] = duty;
                }
            }

            _customCurve = customCurve;
            return OpResult.Success();
        }
    }

    internal sealed class FakeTdp : ITdpProvider
    {
        private readonly DeviceCaps _caps;
        private int _pl1 = 17;
        private int _pl2 = 25;

        public FakeTdp(DeviceCaps caps) => _caps = caps;

        public bool Available => _caps.TdpBackend != TdpBackendKind.Unavailable;
        public string UnavailableReason => Available ? null : "No TDP backend on this device.";
        public TdpBackendKind Backend => _caps.TdpBackend;

        public bool TryRead(out int pl1, out int pl2)
        {
            pl1 = _pl1;
            pl2 = _pl2;
            return Available;
        }

        public OpResult Apply(int pl1, int pl2)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);

            // Clamp again even though the caller already did. The real backend does too: the
            // pipe is reachable by any app on the machine, so no single layer is trusted.
            _caps.ClampPowerLimits(ref pl1, ref pl2);
            _pl1 = pl1;
            _pl2 = pl2;

            // The write is accepted whatever the mode, exactly as the firmware does - and exactly
            // as the firmware then ignores it outside UserScenario. Modelling "accepted but not
            // obeyed" faithfully matters here: a fake that refused the write outside Manual would
            // be kinder than the hardware and would hide the bug this feature exists to fix.
            return OpResult.Success();
        }

        public bool TryReadMode(out PerfMode mode)
        {
            mode = Available ? _mode : PerfMode.Unknown;
            return Available;
        }

        public OpResult ApplyMode(PerfMode mode)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            if (mode == PerfMode.Unknown) return OpResult.Fail("There is no performance mode 'Unknown' to switch to.");

            _mode = mode;
            return OpResult.Success();
        }

        // Starts in UserScenario so the simulated widget shows its sliders without a first press.
        // The real device starts wherever the EC left off, which after a cold boot is AiEngine.
        private PerfMode _mode = PerfMode.UserScenario;
    }

    internal sealed class FakeChargeLimit : IChargeLimitProvider
    {
        private readonly DeviceCaps _caps;

        // 100 = charge to full, which is the shipping default on a machine nobody has configured.
        private int _percent = 100;

        public FakeChargeLimit(bool available, DeviceCaps caps)
        {
            Available = available;
            _caps = caps;
        }

        public bool Available { get; }
        public string UnavailableReason =>
            Available ? null : "A battery charge limit is not available on this device.";

        public bool TryRead(out int percent)
        {
            percent = _percent;
            return Available;
        }

        public OpResult Apply(int percent)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);

            // Clamps like the real one, so the fake cannot hold a value the device would refuse.
            _caps.ClampChargeLimit(ref percent);
            _percent = percent;
            return OpResult.Success();
        }
    }

    /// <summary>
    /// Accepts any animation and remembers nothing.
    /// </summary>
    /// <remarks>
    /// There is nothing to store because <see cref="IRgbProvider"/> has no read: the selected
    /// profile is the helper's state, not the device's. So this fake is genuinely as thin as it
    /// looks - the interesting logic is in <c>LightingRenderer</c>, which is pure and unit-tested
    /// without any hardware at all.
    /// </remarks>
    internal sealed class FakeRgb : IRgbProvider
    {
        public FakeRgb(bool available) => Available = available;

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "Lighting is not available on this device.";

        public OpResult Apply(McenterLite.Shared.Model.LightingAnimation animation) =>
            Available ? OpResult.Success() : OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class FakeHwMouse : IHwMouseProvider
    {
        private bool _desktopMode;

        public FakeHwMouse(bool available) => Available = available;

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "Firmware mouse mode is not available on this device.";

        public bool TryRead(out bool desktopMode)
        {
            desktopMode = _desktopMode;
            return Available;
        }

        public OpResult Apply(bool desktopMode)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            _desktopMode = desktopMode;
            return OpResult.Success();
        }
    }

    internal sealed class FakeIgcl : IIgclProvider
    {
        private readonly Dictionary<Function, int> _values = new Dictionary<Function, int>
        {
            [Function.IntelFpsTier] = 0,
            [Function.IntelLowLatency] = 0,
            [Function.IntelFrameSync] = 0,
            [Function.IntelAdaptiveSharpness] = 0,
            [Function.IntelSaturation] = 50,
            [Function.IntelContrast] = 50,
            [Function.IntelGamma] = 100,
        };

        public FakeIgcl(bool available) => Available = available;

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "Intel Graphics Control Library is not present.";

        public bool Supports(Function fn) => Available && _values.ContainsKey(fn);

        public bool TryRead(Function fn, out int value) => _values.TryGetValue(fn, out value) && Available;

        public OpResult Apply(Function fn, int value)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            if (!_values.ContainsKey(fn)) return OpResult.Fail($"{fn} is not supported by this driver.");
            _values[fn] = value;
            return OpResult.Success();
        }
    }
}
