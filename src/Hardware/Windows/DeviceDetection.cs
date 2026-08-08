using System;
using System.Diagnostics;
using System.Management;

namespace McenterLite.Hardware.Windows
{
    /// <summary>Identifies the machine we are running on, and refuses to touch anything else.</summary>
    public static class DeviceDetection
    {
        /// <summary>
        /// Everything this project knows is calibrated to one model: EC table layout, duty floor,
        /// power-limit ceilings, HID report formats. Applying Claw 8 EX values to a different
        /// Claw generation would write a wrong fan table to a real embedded controller, so an
        /// unrecognised device disables every hardware feature rather than guessing.
        /// </summary>
        public sealed class DeviceIdentity
        {
            public string Vendor { get; set; } = "";
            public string Model { get; set; } = "";
            public string BaseBoard { get; set; } = "";

            /// <summary>True only for the MSI Claw 8 EX AI+.</summary>
            public bool IsClaw8Ex { get; set; }

            /// <summary>True for any MSI Claw. Broader than <see cref="IsClaw8Ex"/> and NOT sufficient to write.</summary>
            public bool IsAnyClaw { get; set; }

            public string DisplayName =>
                string.IsNullOrEmpty(Model) ? "Unknown device" : Model;
        }

        // Firmware identifiers for the Claw 8 EX AI+ (Panther Lake). The marketing name, the
        // model code and the board code are all checked because which one appears in
        // Win32_ComputerSystemProduct varies with the BIOS revision.
        private static readonly string[] Claw8ExMarkers = { "CG3EM", "1T91", "CLAW 8 EX", "CLAW8EX" };

        public static DeviceIdentity Detect()
        {
            var identity = new DeviceIdentity();
            if (!OperatingSystem.IsWindows()) return identity;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"\\.\root\cimv2",
                    "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct");

                foreach (ManagementObject item in searcher.Get())
                {
                    identity.Vendor = (item["Vendor"] as string)?.Trim() ?? "";
                    identity.Model = (item["Name"] as string)?.Trim() ?? "";
                    break;
                }
            }
            catch (Exception)
            {
                // WMI can be unavailable or corrupted. That is a "cannot identify the device"
                // answer, which correctly means "do not touch the hardware".
                return identity;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"\\.\root\cimv2",
                    "SELECT Product FROM Win32_BaseBoard");

                foreach (ManagementObject item in searcher.Get())
                {
                    identity.BaseBoard = (item["Product"] as string)?.Trim() ?? "";
                    break;
                }
            }
            catch (Exception)
            {
                // Non-fatal: the model string alone is usually enough.
            }

            var haystack = ((identity.Model ?? "") + " " + (identity.BaseBoard ?? "")).ToUpperInvariant();

            identity.IsAnyClaw = haystack.Contains("CLAW");
            foreach (var marker in Claw8ExMarkers)
            {
                if (haystack.Contains(marker))
                {
                    identity.IsClaw8Ex = true;
                    break;
                }
            }

            return identity;
        }

        /// <summary>
        /// True when the MSI Center M component that APPLIES our settings is running.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This deliberately does not look for the MSI Center window. Measured on device
        /// 2026-08-07: power limits written to the registry are applied by the background process
        /// <c>MSI_Center_M_Server_UserScenario</c>, and they apply just as well with the UWP front
        /// end (<c>MSI Center M</c>) closed - which is the normal state.
        /// </para>
        /// <para>
        /// Matching the window would therefore report "MSI Center is not running" for a machine
        /// where everything works, and the widget would show a warning contradicted by the
        /// hardware. The servers are what matter; the window is incidental.
        /// </para>
        /// </remarks>
        public static bool IsMsiCenterRunning()
        {
            if (!OperatingSystem.IsWindows()) return false;

            // The UserScenario server is the one that applies power limits. The others are listed
            // because MSI has renamed components across versions and any of them being up means
            // the stack is present; the front end is intentionally absent from this list.
            string[] names =
            {
                "MSI_Center_M_Server_UserScenario",
                "MSI_Center_M_Server",
                "MSI_Center_M_Server_Service",
                "MSI.CentralServer",
            };

            foreach (var name in names)
            {
                try
                {
                    if (Process.GetProcessesByName(name).Length > 0) return true;
                }
                catch (Exception)
                {
                    // Enumeration can fail for processes we cannot open; absence of proof only.
                }
            }

            return false;
        }
    }
}
