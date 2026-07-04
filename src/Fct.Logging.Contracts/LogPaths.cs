using System;
using System.IO;

namespace Fct.Logging
{
    // Single location for both processes' log files, so the host's rolling log and the satellite's
    // local fallback sit side by side. Referenced by both the host and the satellite.
    public static class LogPaths
    {
        // <AppData.Root>\logs
        public static string LogsDirectory => Path.Combine(AppData.Root, "logs");

        public static string EnsureLogsDirectory()
        {
            var dir = LogsDirectory;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
