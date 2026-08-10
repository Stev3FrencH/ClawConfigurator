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
    /// Live today: power limits and controller mode (registry), CPU boost and OS power mode
    /// (plain Windows APIs). Pending: Intel GPU. Battery charge limit, lighting and fan control
    /// were all removed - each is better handled in MSI Center itself.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsHardware : IHardware
    {
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

                HasHwMouse = false,   // set below, once the provider has probed

                // Not implemented yet. Reported as absent rather than broken.
                HasIgcl = false,
            };

            var tdp = new RegistryTdpProvider(Caps);
            Caps.TdpBackend = (identity.IsClaw8Ex && tdp.Available)
                ? tdp.Backend
                : TdpBackendKind.Unavailable;

            var hwMouse = new RegistryHwMouseProvider();
            Caps.HasHwMouse = identity.IsClaw8Ex && hwMouse.Available;

            // A device we do not recognise gets nothing, whatever the registry contains. Every
            // value above is calibrated to one model, and a wrong power limit on a different Claw
            // is a real write to real firmware.
            Tdp = identity.IsClaw8Ex
                ? tdp
                : new UnavailableTdp("This device is not an MSI Claw 8 EX AI+.");

            HwMouse = identity.IsClaw8Ex
                ? (IHwMouseProvider)hwMouse
                : new UnavailableHwMouse("This device is not an MSI Claw 8 EX AI+.");
            Igcl = new UnavailableIgcl("Intel graphics controls are not implemented yet.");

            Power = new WindowsPowerProvider();
        }

        public DeviceCaps Caps { get; }
        public ITdpProvider Tdp { get; }
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
