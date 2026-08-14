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
    /// Live today: power limits (WMI, direct to the EC - see <see cref="WmiTdpProvider"/>, with
    /// MSI Center's registry model as a fallback), controller mode (registry), CPU boost and OS
    /// power mode (plain Windows APIs). Pending: Intel GPU. Battery charge limit, lighting and fan
    /// control were all removed - each is better handled in MSI Center itself.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsHardware : IHardware
    {
        public WindowsHardware(DeviceDetection.DeviceIdentity identity)
        {
            Caps = new DeviceCaps
            {
                Model = identity.DisplayName,
                Supported = identity.IsClaw8Ex,

                // Confirmed on device 2026-08-07 at four captured points, and these are the limits
                // MSI's own UI offers - on AC and on battery alike; there is no lower ceiling
                // unplugged, confirmed against MSI Center's own UI on 2026-08-11.
                MinPl1 = 8,
                MaxPl1 = 35,
                MaxPl2 = 45,
                Pl2MinOffset = 2,

                HasHwMouse = false,   // set below, once the provider has probed

                // Not implemented yet. Reported as absent rather than broken.
                HasIgcl = false,
            };

            // The WMI path writes the EC directly and needs nothing from MSI Center M, not even
            // that it be installed. It used to fall back to a registry mirror that wrote MSI
            // Center's own model and relied on its service to push the values; that fallback was
            // deleted on 2026-08-13, once MSI Center M was uninstalled and this path resolved on
            // the first boot without it. A fallback that requires the thing we removed is not one.
            var wmiTdp = new WmiTdpProvider();
            ITdpProvider tdp = wmiTdp.Available
                ? (ITdpProvider)wmiTdp
                : new UnavailableTdp(wmiTdp.UnavailableReason);

            Caps.TdpBackend = (identity.IsClaw8Ex && tdp.Available)
                ? tdp.Backend
                : TdpBackendKind.Unavailable;

            // Same story, and the mirror here was weaker still: the registry value it read was one
            // MSI Center M maintained BY WATCHING this same HID channel, so it was a copy of the
            // answer rather than a second way to get it. Deleted with the TDP mirror.
            var hidHwMouse = new HidHwMouseProvider();
            IHwMouseProvider hwMouse = hidHwMouse.Available
                ? (IHwMouseProvider)hidHwMouse
                : new UnavailableHwMouse(hidHwMouse.UnavailableReason);

            Caps.HasHwMouse = identity.IsClaw8Ex && hwMouse.Available;

            // Gate G3. Same transport as TDP and the same read-back discipline - Set_AP's reply is
            // a bare status that does not echo the value, so it proves nothing on its own.
            var chargeLimit = new WmiChargeLimitProvider();
            Caps.HasChargeLimit = identity.IsClaw8Ex && chargeLimit.Available;

            // Gate G4, on the same vendor HID channel as controller mode above. No registry
            // fallback exists or is wanted: the only thing the registry ever held was a
            // brightness on/off flag, which is strictly less than this can do.
            var rgb = new HidRgbProvider();
            Caps.HasRgb = identity.IsClaw8Ex && rgb.Available;

            // Gate G2, on the same ACPI-WMI transport as TDP and the charge limit. No registry
            // fallback: MSI Center M's Component\User Scenario fan values are a mirror of its own
            // UI, and were observed holding a stale curve while the firmware ran the factory one.
            var fan = new WmiFanProvider();
            Caps.HasFan = identity.IsClaw8Ex && fan.Available;

            // A device we do not recognise gets nothing, whatever the registry contains. Every
            // value above is calibrated to one model, and a wrong power limit on a different Claw
            // is a real write to real firmware.
            Tdp = identity.IsClaw8Ex
                ? tdp
                : new UnavailableTdp("This device is not an MSI Claw 8 EX AI+.");

            ChargeLimit = identity.IsClaw8Ex
                ? chargeLimit
                : new UnavailableChargeLimit("This device is not an MSI Claw 8 EX AI+.");

            HwMouse = identity.IsClaw8Ex
                ? hwMouse
                : new UnavailableHwMouse("This device is not an MSI Claw 8 EX AI+.");

            Rgb = identity.IsClaw8Ex
                ? rgb
                : new UnavailableRgb("This device is not an MSI Claw 8 EX AI+.");

            Fan = identity.IsClaw8Ex
                ? fan
                : new UnavailableFan("This device is not an MSI Claw 8 EX AI+.");

            Igcl = new UnavailableIgcl("Intel graphics controls are not implemented yet.");

            Power = new WindowsPowerProvider();
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

    internal sealed class UnavailableChargeLimit : IChargeLimitProvider
    {
        public UnavailableChargeLimit(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool TryRead(out int percent)
        {
            percent = 0;
            return false;
        }

        public OpResult Apply(int percent) => OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableRgb : IRgbProvider
    {
        public UnavailableRgb(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public OpResult Apply(McenterLite.Shared.Model.LightingAnimation animation) =>
            OpResult.Unavailable(UnavailableReason);
    }

    internal sealed class UnavailableFan : IFanProvider
    {
        public UnavailableFan(string reason) => UnavailableReason = reason;

        public bool Available => false;
        public string UnavailableReason { get; }

        public bool TryRead(out McenterLite.Shared.Model.FanProfile current)
        {
            current = null;
            return false;
        }

        public bool TryReadCustomCurve(out bool enabled)
        {
            enabled = false;
            return false;
        }

        public OpResult Apply(McenterLite.Shared.Model.FanProfile profile, bool customCurve) =>
            OpResult.Unavailable(UnavailableReason);
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
