using System;

namespace McenterLite.Shared.Model
{
    /// <summary>
    /// What the hardware button does when pressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The button raises <c>MSI_Event</c> with code <c>0x220029</c> and does nothing else — it
    /// reports, and leaves the decision to software. MSI Center M was the only subscriber, so
    /// uninstalling it left the event firing into an empty room. This enum is that decision.
    /// </para>
    /// <para>
    /// <b>Hotkey synthesis is deliberately absent.</b> Sending a keystroke with <c>SendInput</c>
    /// would drive anything that listens for one, which is tempting and general. But the keystroke
    /// enters the system input queue, so the foreground application receives it too, flagged as
    /// injected — and this button's whole purpose is to be pressed while a game is running with
    /// anti-cheat active. Every action here reaches its target directly instead. If a hotkey action
    /// is ever added, that trade is the thing to re-decide, not to rediscover.
    /// </para>
    /// </remarks>
    public enum ButtonAction
    {
        /// <summary>Press it and nothing happens. The honest default when nothing is configured.</summary>
        None = 0,

        /// <summary>
        /// Toggle RivaTuner Statistics Server's on-screen display.
        /// </summary>
        /// <remarks>
        /// Calls <c>SetFlags</c> in <c>RTSSHooks64.dll</c> — the same call RTSS's own hotkey handler
        /// makes when its OSD toggle hotkey fires. No keystroke is generated.
        /// </remarks>
        RtssOverlay = 1,

        /// <summary>Cycle the fan profile: Auto, then the custom curve, then back.</summary>
        FanProfile = 2,

        /// <summary>Cycle the performance mode: Endurance, AI Engine, Manual.</summary>
        PerfMode = 3,

        /// <summary>Cycle the lighting: off, then each profile in turn.</summary>
        LightingProfile = 4,

        /// <summary>Toggle the controller between Gamepad and Desktop.</summary>
        ControllerMode = 5,
    }

    /// <summary>Parses the action names written in the button's configuration file.</summary>
    public static class ButtonActions
    {
        /// <summary>
        /// The name written in the file, which is what the user sees and types.
        /// </summary>
        /// <remarks>
        /// Kebab-case rather than the enum's own spelling: the file is hand-edited, and
        /// <c>rtss-overlay</c> is easier to type correctly than <c>RtssOverlay</c>.
        /// </remarks>
        public static string Format(ButtonAction action)
        {
            switch (action)
            {
                case ButtonAction.RtssOverlay: return "rtss-overlay";
                case ButtonAction.FanProfile: return "fan-profile";
                case ButtonAction.PerfMode: return "performance-mode";
                case ButtonAction.LightingProfile: return "lighting";
                case ButtonAction.ControllerMode: return "controller-mode";
                default: return "none";
            }
        }

        /// <summary>
        /// Parses one action name. Unrecognised text yields <see cref="ButtonAction.None"/> and says
        /// so, rather than silently picking something.
        /// </summary>
        public static bool TryParse(string text, out ButtonAction action)
        {
            action = ButtonAction.None;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "none":
                case "off":
                    action = ButtonAction.None;
                    return true;

                case "rtss-overlay":
                case "rtss":
                case "osd":
                    action = ButtonAction.RtssOverlay;
                    return true;

                case "fan-profile":
                case "fan":
                    action = ButtonAction.FanProfile;
                    return true;

                case "performance-mode":
                case "perf-mode":
                case "tdp":
                    action = ButtonAction.PerfMode;
                    return true;

                case "lighting":
                case "rgb":
                    action = ButtonAction.LightingProfile;
                    return true;

                case "controller-mode":
                case "controller":
                    action = ButtonAction.ControllerMode;
                    return true;

                default:
                    return false;
            }
        }
    }
}
