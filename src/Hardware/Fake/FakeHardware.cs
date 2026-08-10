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
                MaxPl1Dc = 25,
                MaxPl2Dc = 30,
                TdpBackend = simulateClaw8Ex ? TdpBackendKind.Wmi : TdpBackendKind.Unavailable,
                HasHwMouse = simulateClaw8Ex,
                HasIgcl = simulateClaw8Ex,
            };

            Tdp = new FakeTdp(Caps);
            HwMouse = new FakeHwMouse(simulateClaw8Ex);
            Igcl = new FakeIgcl(simulateClaw8Ex);

            // Real on purpose. CPU boost and the power-mode overlay are plain Windows APIs, so
            // even a "fake hardware" run should exercise the genuine code path - on a VM that is
            // a real test, not a simulation.
            Power = new McenterLite.Hardware.Windows.WindowsPowerProvider();
        }

        public DeviceCaps Caps { get; }
        public ITdpProvider Tdp { get; }
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

            // Models the real gate rather than always succeeding: MSI only honours manual limits
            // in User Scenario. Without this the widget's mode gating is untestable off-device.
            if (_mode != PerfMode.UserScenario)
            {
                // Wording tracks the real provider's, which uses the widget's button labels -
                // "Manual", not MSI's own "User Scenario".
                return OpResult.Fail(
                    $"Saved {pl1}/{pl2} W, but MSI Center is in {_mode} mode and is managing power "
                    + "itself. Switch to Manual for these limits to take effect.");
            }

            return OpResult.Success();
        }

        public bool TryReadMode(out PerfMode mode)
        {
            mode = _mode;
            return Available;
        }

        public OpResult ApplyMode(PerfMode mode)
        {
            if (!Available) return OpResult.Unavailable(UnavailableReason);
            if (mode == PerfMode.Unknown) return OpResult.Fail("Cannot switch to an unknown performance mode.");

            _mode = mode;
            return OpResult.Success();
        }

        private PerfMode _mode = PerfMode.UserScenario;
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
