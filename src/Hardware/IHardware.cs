using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;

namespace McenterLite.Hardware
{
    /// <summary>
    /// The outcome of a hardware write.
    /// </summary>
    /// <remarks>
    /// Writes return a result rather than throwing or returning bare bool because "it failed and
    /// here is why" has to reach the widget. Silent failure is the specific behaviour this app
    /// must not have: the EC can refuse or partially apply a write - for instance while MSI
    /// Center holds the ACPI-WMI interface - and a UI that shows success while the hardware
    /// ignored us is worse than one that shows nothing.
    /// </remarks>
    public readonly struct OpResult
    {
        public bool Ok { get; }

        /// <summary>Human-readable failure reason, surfaced to the widget. Null on success.</summary>
        public string Error { get; }

        private OpResult(bool ok, string error)
        {
            Ok = ok;
            Error = error;
        }

        public static OpResult Success() => new OpResult(true, null);

        public static OpResult Fail(string error) => new OpResult(false, error ?? "unknown error");

        /// <summary>The feature exists in the UI but not on this device or in this configuration.</summary>
        public static OpResult Unavailable(string reason) => new OpResult(false, reason ?? "not available");
    }

    /// <summary>Common shape for every hardware feature: it may simply not exist here.</summary>
    public interface IFeatureProvider
    {
        /// <summary>
        /// False when this device, firmware or software configuration cannot support the feature.
        /// The helper reports this in <see cref="DeviceCaps"/> and the widget hides the control -
        /// a control the user cannot see is a value they cannot send.
        /// </summary>
        bool Available { get; }

        /// <summary>Why the feature is unavailable, for the UI and the log. Null when available.</summary>
        string UnavailableReason { get; }
    }

    /// <summary>Sustained and boost power limits.</summary>
    public interface ITdpProvider : IFeatureProvider
    {
        /// <summary>Which path reached the hardware. Reported to the user, since the two differ in kind.</summary>
        TdpBackendKind Backend { get; }

        bool TryRead(out int pl1, out int pl2);

        /// <summary>
        /// Applies a pair that has ALREADY been clamped by <see cref="DeviceCaps.ClampPowerLimits"/>.
        /// Implementations must still read back rather than assume the write took.
        /// </summary>
        OpResult Apply(int pl1, int pl2);

        /// <summary>MSI's current performance mode, which gates whether the limits are honoured.</summary>
        bool TryReadMode(out PerfMode mode);

        /// <summary>
        /// Switches MSI's performance mode.
        /// </summary>
        /// <remarks>
        /// Exposed as a control of its own rather than something applied silently behind a power
        /// limit. Only <see cref="PerfMode.UserScenario"/> honours manual limits, so the user needs
        /// to be able to see which mode they are in and change it deliberately - a hidden switch
        /// would also move settings that have nothing to do with power, since mode changes affect
        /// lighting too.
        /// </remarks>
        OpResult ApplyMode(PerfMode mode);
    }

    /// <summary>The EC fan curve, exposed only as a small fixed set of presets.</summary>
    public interface IFanProvider : IFeatureProvider
    {
        /// <summary>
        /// The device's own factory temperature axis, captured before the first write.
        /// Null when it could not be read. Also what uninstall restores.
        /// </summary>
        int[] FactoryTemps { get; }

        /// <summary>The device's own factory duty curve, captured before the first write.</summary>
        int[] FactoryDuties { get; }

        /// <summary>Lowest duty the firmware honours at idle. Below this the curve is overridden.</summary>
        int DutyFloor { get; }

        /// <summary>The preset last applied, used to judge whether the EC still holds it.</summary>
        FanPreset CurrentPreset { get; }

        /// <summary>
        /// Reads the EC, including <see cref="FanState.Matches"/>.
        /// </summary>
        /// <remarks>
        /// The implementation must set <c>Matches</c> itself - it is the only party that knows both
        /// the factory curve and the duty floor the preset resolves against, so it is the only one
        /// that can compare the read-back against what was actually written.
        /// </remarks>
        bool TryReadState(out FanState state);

        /// <summary>Writes a preset, then re-reads and verifies the bytes it wrote.</summary>
        OpResult ApplyPreset(FanPreset preset);

        /// <summary>Hands the curve back to firmware (false) or takes software control (true).</summary>
        OpResult SetEnabled(bool enabled);

        OpResult SetFullSpeed(bool on);

        /// <summary>Puts the factory table back. Called on uninstall, and available as a panic action.</summary>
        OpResult RestoreFactory();
    }

    /// <summary>
    /// The controller's firmware desktop-mouse mode.
    /// </summary>
    /// <remarks>
    /// This drives the FIRMWARE, producing a real HID mouse, which is why it keeps working on the
    /// UAC secure desktop where injected input cannot. The physical MSI button changes the same
    /// state, so this is polled and pushed rather than owned.
    /// </remarks>
    public interface IHwMouseProvider : IFeatureProvider
    {
        bool TryRead(out bool desktopMode);
        OpResult Apply(bool desktopMode);
    }

    /// <summary>
    /// CPU boost and the Windows power-mode overlay.
    /// </summary>
    /// <remarks>
    /// Pure documented Win32, so unlike every other provider here this one works on any Windows
    /// machine and is fully testable in a VM without the handheld.
    /// </remarks>
    public interface IPowerProvider : IFeatureProvider
    {
        bool TryReadCpuBoost(out bool enabled);
        OpResult ApplyCpuBoost(bool enabled);

        bool TryReadPowerMode(out OsPowerMode mode);
        OpResult ApplyPowerMode(OsPowerMode mode);
    }

    /// <summary>Intel graphics settings via the Intel Graphics Control Library.</summary>
    public interface IIgclProvider : IFeatureProvider
    {
        /// <summary>Not every driver build exposes every feature; the UI greys what is missing.</summary>
        bool Supports(Function fn);

        bool TryRead(Function fn, out int value);
        OpResult Apply(Function fn, int value);
    }

    /// <summary>
    /// Everything the helper can talk to. One composition root so a single <c>--fake-hardware</c>
    /// switch swaps the entire hardware layer, which is what makes the widget, IPC, packaging and
    /// deployment all developable without the device.
    /// </summary>
    public interface IHardware
    {
        DeviceCaps Caps { get; }

        ITdpProvider Tdp { get; }
        IFanProvider Fan { get; }
        IHwMouseProvider HwMouse { get; }
        IPowerProvider Power { get; }
        IIgclProvider Igcl { get; }

        /// <summary>True when MSI Center M is running and may be contending for the EC or HID.</summary>
        bool IsMsiCenterRunning();
    }
}
