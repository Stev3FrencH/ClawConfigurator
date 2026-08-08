using System.Runtime.Versioning;
using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware.Windows
{
    /// <summary>
    /// The real hardware layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features are enabled one at a time as Phase 0 establishes how they work. Anything not yet
    /// established reports <c>Available = false</c> with a reason, which hides its card rather
    /// than offering a control that does nothing.
    /// </para>
    /// <para>
    /// Live today: power limits, battery charge limit and lighting on/off (registry), CPU boost
    /// and OS power mode (plain Windows APIs). Pending: fan, firmware mouse mode, Intel GPU.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsHardware : IHardware
    {
        /// <summary>
        /// Lowest duty the EX firmware honours at idle. Below this the curve is overridden, so a
        /// lower point makes the device louder at idle rather than quieter.
        /// </summary>
        private const int ExDutyFloor = 58;

        public WindowsHardware(DeviceDetection.DeviceIdentity identity)
        {
            // Caps first: the provider needs the battery ceilings to derive the DC pair.
            Caps = new DeviceCaps
            {
                Model = identity.DisplayName,
                Supported = identity.IsClaw8Ex,

                // Confirmed on device 2026-08-07 at four captured points, and these are the limits
                // MSI's own UI offers.
                MinPl1 = 8,
                MaxPl1 = 35,
                MaxPl2 = 45,
                Pl2MinOffset = 2,

                // Battery ceilings. Below the AC limits on purpose: an 8-inch handheld running
                // 35 W unplugged empties itself fast, and the firmware already keeps a separate
                // DC pair, so this costs nothing to honour.
                MaxPl1Dc = 25,
                MaxPl2Dc = 30,

                // Not implemented yet. Reported as absent rather than broken.
                HasFan = false,
                HasChargeLimit = false,   // set below, once the provider has probed
                HasLed = false,           // set below, once the provider has probed
                HasHwMouse = false,
                HasIgcl = false,

                FanDutyFloor = ExDutyFloor,
            };

            var tdp = new RegistryTdpProvider(Caps);
            Caps.TdpBackend = (identity.IsClaw8Ex && tdp.Available)
                ? tdp.Backend
                : TdpBackendKind.Unavailable;

            var chargeLimit = new RegistryChargeLimitProvider();
            Caps.HasChargeLimit = identity.IsClaw8Ex && chargeLimit.Available;

            var led = new RegistryLedProvider();
            Caps.HasLed = identity.IsClaw8Ex && led.Available;

            // A device we do not recognise gets nothing, whatever the registry contains. Every
            // value above is calibrated to one model, and a wrong power limit on a different Claw
            // is a real write to real firmware.
            Tdp = identity.IsClaw8Ex
                ? tdp
                : new UnavailableTdp("This device is not an MSI Claw 8 EX AI+.");

            Fan = new UnavailableFan("Fan control is not implemented yet.", ExDutyFloor);
            ChargeLimit = identity.IsClaw8Ex
                ? (IChargeLimitProvider)chargeLimit
                : new UnavailableChargeLimit("This device is not an MSI Claw 8 EX AI+.");
            Led = identity.IsClaw8Ex
                ? (ILedProvider)led
                : new UnavailableLed("This device is not an MSI Claw 8 EX AI+.");
            HwMouse = new UnavailableHwMouse("Desktop mouse mode is not implemented yet.");
            Igcl = new UnavailableIgcl("Intel graphics controls are not implemented yet.");

            Power = new WindowsPowerProvider();
        }

        public DeviceCaps Caps { get; }
        public ITdpProvider Tdp { get; }
        public IFanProvider Fan { get; }
        public IChargeLimitProvider ChargeLimit { get; }
        public ILedProvider Led { get; }
        public IHwMouseProvider HwMouse { get; }
        public IPowerProvider Power { get; }
        public IIgclProvider Igcl { get; }

        public bool IsMsiCenterRunning() => DeviceDetection.IsMsiCenterRunning();
    }

    // ── Stubs ───────────────────────────────────────────────────────────────────
    //
    // One per feature rather than a single class implementing all of them. A combined stub is
    // shorter, but it resolves five interfaces' worth of TryRead/Apply overloads by signature, and
    // adding a real implementation later would mean untangling that. These say "no" and nothing
    // else, which is the only thing a stub should be able to do.

    internal sealed class UnavailableTdp : ITdpProvider
    {
        public UnavailableTdp(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }
        public TdpBackendKind Backend => TdpBackendKind.Unavailable;

        public bool TryRead(out int pl1, out int pl2)
        {
            pl1 = 0;
            pl2 = 0;
            return false;
        }

        public OpResult Apply(int pl1, int pl2) => OpResult.Unavailable(UnavailableReason);

        public bool TryReadMode(out PerfMode mode)
        {
            mode = PerfMode.Unknown;
            return false;
        }

        public OpResult ApplyMode(PerfMode mode) => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableFan : IFanProvider
    {
        public UnavailableFan(string reason, int dutyFloor)
        {
            UnavailableReason = reason;
            DutyFloor = dutyFloor;
        }

        public bool Available => false;
        public string UnavailableReason { get; }

        public int[] FactoryTemps => null;
        public int[] FactoryDuties => null;
        public int DutyFloor { get; }
        public FanPreset CurrentPreset => FanPreset.Default;

        public bool TryReadState(out FanState state)
        {
            state = null;
            return false;
        }

        public OpResult ApplyPreset(FanPreset preset) => OpResult.Unavailable(UnavailableReason);
        public OpResult SetEnabled(bool enabled) => OpResult.Unavailable(UnavailableReason);
        public OpResult SetFullSpeed(bool on) => OpResult.Unavailable(UnavailableReason);
        public OpResult RestoreFactory() => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableChargeLimit : IChargeLimitProvider
    {
        public UnavailableChargeLimit(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool TryRead(out bool enabled, out int percent)
        {
            enabled = false;
            percent = ChargeLevels.Default;
            return false;
        }

        public OpResult Apply(bool enabled, int percent) => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableLed : ILedProvider
    {
        public UnavailableLed(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool TryRead(out bool enabled)
        {
            enabled = false;
            return false;
        }

        public OpResult Apply(bool enabled) => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableHwMouse : IHwMouseProvider
    {
        public UnavailableHwMouse(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool TryRead(out bool desktopMode)
        {
            desktopMode = false;
            return false;
        }

        public OpResult Apply(bool desktopMode) => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableIgcl : IIgclProvider
    {
        public UnavailableIgcl(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool Supports(Function fn) => false;

        public bool TryRead(Function fn, out int value)
        {
            value = 0;
            return false;
        }

        public OpResult Apply(Function fn, int value) => OpResult.Unavailable(UnavailableReason);
    }
}
