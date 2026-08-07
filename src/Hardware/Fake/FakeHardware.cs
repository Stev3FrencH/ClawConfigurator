using System;
using System.Collections.Generic;
using McenterLite.Shared.Fan;
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
    /// It models the awkward parts rather than just storing values: the EC's boundary bytes, the
    /// firmware duty floor, and reads that reflect what was actually "applied". A fake that always
    /// succeeds cleanly would let bugs through that the real device then finds.
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
                HasFan = simulateClaw8Ex,
                HasChargeLimit = simulateClaw8Ex,
                HasLed = simulateClaw8Ex,
                HasHwMouse = simulateClaw8Ex,
                HasIgcl = simulateClaw8Ex,
                FanDutyFloor = 58,
            };

            Tdp = new FakeTdp(Caps);
            Fan = new FakeFan(simulateClaw8Ex);
            ChargeLimit = new FakeChargeLimit(simulateClaw8Ex);
            Led = new FakeLed(simulateClaw8Ex);
            HwMouse = new FakeHwMouse(simulateClaw8Ex);
            Igcl = new FakeIgcl(simulateClaw8Ex);

            // Real on purpose. CPU boost and the power-mode overlay are plain Windows APIs, so
            // even a "fake hardware" run should exercise the genuine code path - on a VM that is
            // a real test, not a simulation.
            Power = new McenterLite.Hardware.Windows.WindowsPowerProvider();
        }

        public DeviceCaps Caps { get; }
        public ITdpProvider Tdp { get; }
        public IFanProvider Fan { get; }
        public IChargeLimitProvider ChargeLimit { get; }
        public ILedProvider Led { get; }
        public IHwMouseProvider HwMouse { get; }
        public IPowerProvider Power { get; }
        public IIgclProvider Igcl { get; }

        public bool IsMsiCenterRunning() => false;
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
            return OpResult.Success();
        }
    }

    internal sealed class FakeFan : IFanProvider
    {
        // Boundary bytes deliberately non-zero, matching a real Claw 8 EX which ships index 7 = 94.
        // A fake with clean zeros there would hide the exact bug the write window guards against.
        private byte[] _table = { 3, 0, 40, 49, 58, 67, 75, 94 };
        private bool _enabled;
        private bool _fullSpeed;
        private FanPreset _preset = FanPreset.Default;
        private readonly byte[] _factoryTable;
        private readonly Random _rng = new Random(20260807);

        public FakeFan(bool available)
        {
            Available = available;
            _factoryTable = (byte[])_table.Clone();
        }

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "Fan control is not available on this device.";
        public int[] FactoryTemps => new[] { 44, 54, 64, 74, 82 };
        public int[] FactoryDuties => new[] { 40, 49, 58, 67, 75 };
        public int DutyFloor => 58;
        public FanPreset CurrentPreset => _preset;

        public bool TryReadState(out FanState state)
        {
            FanProfiles.Resolve(_preset, FactoryTemps, FactoryDuties, out _, out var expected, DutyFloor);

            state = new FanState
            {
                Table = (byte[])_table.Clone(),
                ControlEnabled = _enabled,
                ReadOk = Available,
                FullSpeed = _fullSpeed,
                Rpm = Available ? EstimateRpm() : -1,
                Temps = FactoryTemps,
                Matches = FanProfiles.Matches(_table, expected),
            };
            return Available;
        }

        public OpResult ApplyPreset(FanPreset preset)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);

            FanProfiles.Resolve(preset, FactoryTemps, FactoryDuties, out _, out var duties, DutyFloor);
            _preset = preset;

            // Exercise the real write path: patch only our window, preserving EC state bytes.
            _table = FanProfiles.ApplyToTable(_table, duties);

            if (!FanProfiles.Matches(_table, duties))
                return OpResult.Fail("Read-back did not match the table we wrote.");

            return OpResult.Success();
        }

        public OpResult SetEnabled(bool enabled)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            _enabled = enabled;
            return OpResult.Success();
        }

        public OpResult SetFullSpeed(bool on)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            _fullSpeed = on;
            return OpResult.Success();
        }

        public OpResult RestoreFactory()
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            _table = (byte[])_factoryTable.Clone();
            _enabled = false;
            _fullSpeed = false;
            return OpResult.Success();
        }

        /// <summary>
        /// Rough duty-to-RPM mapping so the UI has plausible moving numbers. Anchors are real
        /// Claw 8 EX tachometer readings; the jitter is invented.
        /// </summary>
        private int EstimateRpm()
        {
            if (_fullSpeed) return 5400;

            int duty = Math.Max(_table[2], DutyFloor);
            int[] dx = { 0, 20, 39, 45, 51, 58, 62, 70, 75, 80, 84, 94 };
            int[] ry = { 0, 2633, 2673, 3112, 3175, 3571, 3839, 4466, 4684, 4938, 5220, 5413 };

            int rpm = ry[ry.Length - 1];
            for (int i = 1; i < dx.Length; i++)
            {
                if (duty > dx[i]) continue;
                double f = (duty - dx[i - 1]) / (double)(dx[i] - dx[i - 1]);
                rpm = (int)(ry[i - 1] + f * (ry[i] - ry[i - 1]));
                break;
            }

            return rpm + _rng.Next(-40, 41);
        }
    }

    internal sealed class FakeChargeLimit : IChargeLimitProvider
    {
        private bool _enabled;
        private int _percent = 80;

        public FakeChargeLimit(bool available) => Available = available;

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "Charge limiting is not available on this device.";

        public bool TryRead(out bool enabled, out int percent)
        {
            enabled = _enabled;
            percent = _percent;
            return Available;
        }

        public OpResult Apply(bool enabled, int percent)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            _enabled = enabled;
            _percent = Math.Max(60, Math.Min(100, percent));
            return OpResult.Success();
        }
    }

    internal sealed class FakeLed : ILedProvider
    {
        private LedSpec _spec = new LedSpec();

        public FakeLed(bool available) => Available = available;

        public bool Available { get; }
        public string UnavailableReason => Available ? null : "LED control is not available on this device.";

        public bool TryRead(out LedSpec spec)
        {
            spec = LedSpec.Parse(_spec.Serialize());
            return Available;
        }

        public OpResult Apply(LedSpec spec)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            if (spec == null) return OpResult.Fail("No LED specification supplied.");
            _spec = LedSpec.Parse(spec.Serialize());
            return OpResult.Success();
        }
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
