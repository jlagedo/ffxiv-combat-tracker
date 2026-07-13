using System;
using System.IO;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Fct.LegacyHost.Logging
{
    // The satellite's logging pipeline. Serilog is the backend behind the Microsoft.Extensions.Logging
    // ILogger surface (the same API the net10 host uses). It writes three places:
    //   * the unified rolling file in %LOCALAPPDATA%\FFXIVCombatTracker\logs (shared with the host),
    //   * the bridge sink, which forwards records to the host's pipeline, and
    //   * s2-ffxiv.log next to the exe — a flat, message-only verification artifact the integration
    //     tests read as a black box.
    internal static class SatelliteLogging
    {
        private const string FullTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {EventId} {Message:lj}{NewLine}{Exception}";

        // Caps a single day's shared file so a runaway error loop can't fill the disk.
        private const long FileSizeLimitBytes = 50_000_000;

        // The live minimum level, adjustable at runtime without a restart. Seeded from FCT_LOG_LEVEL /
        // build config at Initialize.
        public static LoggingLevelSwitch LevelSwitch { get; } = new(ResolveLevel());

        private static ILoggerFactory _factory;

        // ILogger for satellite-owned code (Program, FacadeHost). Null-safe before Initialize.
        public static ILogger Log { get; private set; } = NullLogger.Instance;

        public static ILogger CreateLogger(string category) =>
            _factory != null ? _factory.CreateLogger(category) : NullLogger.Instance;

        public static void Initialize(string satelliteId = "ffxiv")
        {
            var logsDir = LogPaths.EnsureLogsDirectory();

            // Per-satellite verification artifact next to the exe (P3): N satellites share the staged dir,
            // so the flat log is keyed by identity. Defaults to s2-ffxiv.log (the historical name the
            // integration tests read for the parser satellite). Fresh each run.
            var id = string.IsNullOrWhiteSpace(satelliteId) ? "ffxiv" : satelliteId;
            var verificationLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "s2-" + id + ".log");
            try { File.Delete(verificationLog); } catch { }

            var serilog = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .Enrich.WithProperty("Process", "satellite")
                // The unified rolling file is shared by every satellite process, so its disk I/O runs
                // on a background worker rather than blocking the dispatch/UI thread that logged. The
                // verification artifact and the bridge sink stay synchronous: the integration tests
                // read the artifact as a black box, and the bridge sink only hands off to the pipe.
                .WriteTo.Async(a => a.File(
                    path: Path.Combine(logsDir, "satellite-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: FullTemplate))
                .WriteTo.File(
                    path: verificationLog,
                    outputTemplate: "{Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(new BridgeLogSink())
                .CreateLogger();

            _factory = new SerilogLoggerFactory(serilog, dispose: true);
            Log = _factory.CreateLogger(LogCategories.Satellite);
        }

        // Flush + close on shutdown.
        public static void Shutdown()
        {
            try { _factory?.Dispose(); } catch { }
        }

        // Legacy seam: the ACT facade and the plugin wrapper emit diagnostics as plain strings through
        // an Action<string>, tagging the severity in a "[Tag]" prefix. Map that tag to a level + the
        // matching EventId and strip it, so those records classify like the rest.
        public static void WriteLegacy(string message)
        {
            if (message == null) return;
            var level = ClassifyLevel(message, out var evt, out var text);
            Log.Log(level, evt, "{Message}", text);
        }

        private static LogLevel ClassifyLevel(string s, out EventId evt, out string text)
        {
            if (TryTag(s, "[Exception]", out text)) { evt = LogEvents.ActException; return LogLevel.Error; }
            if (TryTag(s, "[Info]", out text)) { evt = LogEvents.ActInfo; return LogLevel.Information; }
            if (TryTag(s, "[Debug]", out text)) { evt = LogEvents.ActDebug; return LogLevel.Debug; }
            if (TryTag(s, "[Notify]", out text)) { evt = LogEvents.ActNotification; return LogLevel.Information; }
            if (TryTag(s, "[RestartACT]", out text)) { evt = LogEvents.ActRestartRequested; return LogLevel.Warning; }
            if (TryTag(s, "[ActCommands]", out text)) { evt = LogEvents.ActCommand; return LogLevel.Information; }
            if (TryTag(s, "[OpenLog]", out text)) { evt = LogEvents.ActDebug; return LogLevel.Debug; }
            if (TryTag(s, "[ChangeZone]", out text)) { evt = LogEvents.ActDebug; return LogLevel.Debug; }
            if (TryTag(s, "[Wrap]", out text)) { evt = LogEvents.RealPluginBound; return LogLevel.Information; }
            if (TryTag(s, "[RingDispatch]", out text)) { evt = LogEvents.SubscriberThrew; return LogLevel.Warning; }
            if (TryTag(s, "[StandIn]", out text)) { evt = LogEvents.DispatcherDiagnostics; return LogLevel.Information; }
            if (TryTag(s, "[NamedCallback]", out text)) { evt = LogEvents.ActCommand; return LogLevel.Information; }
            if (TryTag(s, "[LogTail]", out text)) { evt = LogEvents.ActDebug; return LogLevel.Debug; }
            text = s;
            evt = LogEvents.ActInfo;
            return LogLevel.Information;
        }

        private static bool TryTag(string s, string tag, out string rest)
        {
            if (s.StartsWith(tag, StringComparison.Ordinal))
            {
                rest = s.Substring(tag.Length).TrimStart();
                return true;
            }
            rest = s;
            return false;
        }

        private static LogEventLevel ResolveLevel()
        {
            var env = Environment.GetEnvironmentVariable("FCT_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse(env, ignoreCase: true, out LogEventLevel lvl))
                return lvl;
#if DEBUG
            return LogEventLevel.Debug;
#else
            return LogEventLevel.Information;
#endif
        }
    }
}
