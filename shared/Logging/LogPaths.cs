using System;
using System.IO;

namespace Fct.Logging
{
    // Single location for both processes' log files, so the host's rolling log and the satellite's
    // local fallback sit side by side. Shared source: compiled into the host and the satellite.
    internal static class LogPaths
    {
        // %LOCALAPPDATA%\FFXIVCombatTracker\logs
        public static string LogsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCombatTracker", "logs");

        public static string EnsureLogsDirectory()
        {
            var dir = LogsDirectory;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
