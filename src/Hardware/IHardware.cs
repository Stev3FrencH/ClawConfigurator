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

        /// <summary>
        /// The performance mode, which decides whether <see cref="Apply"/> means anything.
        /// </summary>
        /// <remarks>
        /// <b>Removed and restored on 2026-08-13.</b> These were deleted on the belief that the WMI
        /// path was ungated, and the implementation that "proved" it answered
        /// <see cref="PerfMode.UserScenario"/> unconditionally and no-opped the write. It was not
        /// describing the hardware; it was describing the assumption. The mode is real, it is in the
        /// firmware rather than in MSI Center M, and a device sitting in Endurance or AI Engine
        /// accepts a power limit, reads it back unchanged, and runs its own numbers instead.
        /// </remarks>
        bool TryReadMode(out PerfMode mode);

        /// <summary>
        /// Switches the performance mode.
        /// </summary>
        /// <remarks>
        /// Kept separate from <see cref="Apply"/> rather than folded into it. Forcing the mode on
        /// every limit write would silently overrule a user who chose an automatic mode on purpose -
        /// and the mode is now a control of its own, so that choice is theirs to make and see.
        /// </remarks>
        OpResult ApplyMode(PerfMode mode);
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
    /// The battery charge limit, as a percentage. 100 means charge to full.
    /// </summary>
    /// <remarks>
    /// One value rather than an enabled/percent pair: the firmware has a single byte and expresses
    /// "off" as 100, so a separate flag would be a UI idea the hardware does not have.
    /// </remarks>
    public interface IChargeLimitProvider : IFeatureProvider
    {
        bool TryRead(out int percent);
        OpResult Apply(int percent);
    }

    /// <summary>
    /// The controller's nine RGB LEDs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Write-only, and that is not an oversight.</b> Every other provider here can read its
    /// value back, but the controller stores flattened keyframes rather than a profile number -
    /// so "which of the three profiles is selected" is a question the hardware cannot answer. Two
    /// profiles could even render to the same bytes. The selected slot is therefore the helper's
    /// state, kept in settings alongside the other persisted values.
    /// </para>
    /// <para>
    /// Takes a rendered <see cref="LightingAnimation"/> rather than a <c>LightingProfile</c>: the
    /// style-to-keyframe recipes are portable logic that belongs in Shared where it can be tested
    /// without a controller, and the hardware layer only needs the result.
    /// </para>
    /// </remarks>
    public interface IRgbProvider : IFeatureProvider
    {
        OpResult Apply(LightingAnimation animation);
    }

    /// <summary>
    /// The two fans: their duty tables, and whether the firmware is honouring them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gate G2. <b>Two separate things, and both are needed.</b> The duty table says what the fans
    /// should do; a flag elsewhere says whether the EC is reading that table at all. Writing the
    /// table alone changes nothing audible - it is stored, it reads back correctly, and the fans go
    /// on following the firmware's own curve. That was this interface's behaviour for its first
    /// build, and it is why fan control appeared to do nothing while every log line said success.
    /// </para>
    /// <para>
    /// This interface previously documented the opposite - that the firmware has no mode and always
    /// runs the last table it was given. That was drawn from MSI Center M's REGISTRY still reading
    /// Auto while a custom table was loaded, which only ever proved the registry was bookkeeping.
    /// The firmware does have a mode; it simply is not in the fan table. See
    /// <c>docs/hardware-notes.md</c>.
    /// </para>
    /// </remarks>
    public interface IFanProvider : IFeatureProvider
    {
        /// <summary>Reads both fans' live tables into a profile. Name is not meaningful.</summary>
        bool TryRead(out FanProfile current);

        /// <summary>
        /// Whether the firmware is honouring the tables rather than running its own curve.
        /// </summary>
        /// <remarks>
        /// The honest answer to "what are the fans doing", and better evidence than comparing the
        /// table against the factory one: a custom profile that happens to equal the factory curve
        /// is indistinguishable by table but perfectly distinguishable by this.
        /// </remarks>
        bool TryReadCustomCurve(out bool enabled);

        /// <summary>
        /// Writes both fans' tables, then hands control to them or back to the firmware.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The table is always written, in both directions: with <paramref name="customCurve"/>
        /// false the caller passes the factory curve, so Auto also puts the stock table back rather
        /// than leaving ours behind for whatever sets the flag next.
        /// </para>
        /// <para>
        /// Implementations must read back rather than trust the reply — <c>Set_Fan</c> answers with
        /// a bare status that does not echo what was written.
        /// </para>
        /// </remarks>
        OpResult Apply(FanProfile profile, bool customCurve);
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
        IChargeLimitProvider ChargeLimit { get; }
        IHwMouseProvider HwMouse { get; }
        IRgbProvider Rgb { get; }
        IFanProvider Fan { get; }
        IPowerProvider Power { get; }
        IIgclProvider Igcl { get; }

        // IsMsiCenterRunning is gone as of 2026-08-13. It reported whether MSI Center M's servers
        // were up so the widget could warn about contention for the EC and HID. MSI Center M is
        // uninstalled, no feature depends on it, and nothing ever consumed the answer - the widget
        // never referenced it in any build.
    }
}
