using System;
using System.IO;
using System.Text;

namespace McenterLite.Helper
{
    /// <summary>
    /// File logging. The helper runs as a windowless scheduled task, so the log is the only way
    /// to see what it did - particularly for hardware writes, where "what did we send and what
    /// came back" is the whole diagnostic.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        /// <summary>Keeps the log from growing without bound on a device that is always on.</summary>
        private const long MaxBytes = 2 * 1024 * 1024;

        public static void Initialize(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "helper.log");
                RollIfTooLarge();
            }
            catch (Exception)
            {
                _path = null; // logging must never be the reason the helper fails to start
            }
        }

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string message)
        {
            if (_path == null) return;

            try
            {
                var line = new StringBuilder(160)
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(' ').Append(level).Append(' ').Append(message)
                    .AppendLine()
                    .ToString();

                lock (Gate)
                {
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Swallow: a failed log write must not take down hardware control.
            }
        }

        private static void RollIfTooLarge()
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length < MaxBytes) return;

                var previous = _path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(_path, previous);
            }
            catch (Exception)
            {
                // Non-fatal.
            }
        }
    }
}
