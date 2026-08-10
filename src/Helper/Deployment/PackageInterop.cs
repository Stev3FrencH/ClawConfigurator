using System;
using System.Runtime.InteropServices;
using System.Text;

namespace McenterLite.Helper.Deployment
{
    /// <summary>
    /// Reads MSIX package identity through Win32.
    /// </summary>
    /// <remarks>
    /// The obvious way to get this is the WinRT <c>Package.Current</c> API, which is deliberately
    /// avoided: consuming WinRT would force a versioned TFM
    /// (<c>net8.0-windows10.0.26100.0</c>), and that needs a Windows targeting pack, which would
    /// end the ability to build this project on macOS. Two documented kernel32 exports cost far
    /// less than that.
    /// </remarks>
    internal static class PackageInterop
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;

        /// <summary>Returned when the process has no package identity - i.e. it is not running from MSIX.</summary>
        private const int AppmodelErrorNoPackage = 15700;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetCurrentPackageFamilyName(
            ref uint packageFamilyNameLength,
            StringBuilder packageFamilyName);

        /// <summary>
        /// The package family name, or null when this process has no package identity.
        /// </summary>
        /// <remarks>
        /// Null is the normal case for the deployed copy: once the scheduled task launches the
        /// helper from LocalCache it runs OUTSIDE the package, so it has no identity at all. That
        /// is precisely why the data directory is derived from the executable path rather than
        /// from package APIs.
        /// </remarks>
        public static string GetPackageFamilyName()
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                uint length = 0;
                int rc = GetCurrentPackageFamilyName(ref length, null);

                if (rc == AppmodelErrorNoPackage) return null;
                if (rc != ErrorInsufficientBuffer && rc != ErrorSuccess) return null;
                if (length == 0) return null;

                var buffer = new StringBuilder((int)length);
                rc = GetCurrentPackageFamilyName(ref length, buffer);

                return rc == ErrorSuccess ? buffer.ToString() : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the package family name: {ex.Message}");
                return null;
            }
        }

        /// <summary>True when this process is running from inside the MSIX package.</summary>
        public static bool HasPackageIdentity() => GetPackageFamilyName() != null;
    }
}
