using System;
using Fct.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Fct.App.Logging;

// The host owns the single logging pipeline for the whole stack: it writes the console + a rolling
// file, and the satellite's forwarded records are re-emitted into this same pipeline (see
// SatelliteHost). Serilog is the backend; application code logs through Microsoft.Extensions.Logging
// ILogger, which Serilog.Extensions.Hosting routes here.
internal static class LoggingBootstrap
{
    // Console stays terse; the file keeps full context (category + EventId) for diagnosis.
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {EventId} {Message:lj}{NewLine}{Exception}";

    // Caps a single day's file so a runaway error loop can't fill the disk; the sink rolls to
    // host-<date>_001.log etc. when the cap is hit (retainedFileCountLimit still bounds the set).
    private const long FileSizeLimitBytes = 50_000_000;

    // The live minimum level. Held so it can be raised/lowered at runtime (e.g. a Settings "verbose
    // logging" toggle) without a restart — Serilog re-reads it per event. Seeded from FCT_LOG_LEVEL /
    // build config at startup.
    public static LoggingLevelSwitch LevelSwitch { get; } = new(ResolveMinimumLevel());

    // Built before the host so the earliest startup (and any startup failure) is captured. The
    // in-app Console reads the shared stream the UiLogSink feeds — so it too sees the whole stack
    // (host records + the satellite's forwarded records re-emitted through this same pipeline).
    public static Logger CreateLogger(LogStream stream)
    {
        var logsDir = LogPaths.EnsureLogsDirectory();
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            // Avalonia is chatty at Debug; keep its noise out of our file unless explicitly raised.
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Process", "host")
            // Console + file share one background worker so disk/console I/O never blocks the thread
            // that logged (the UI thread included). The in-app Console sink stays synchronous — it only
            // enqueues onto a bounded ring, so it must not lag behind the live feed.
            .WriteTo.Async(a =>
            {
                a.Console(outputTemplate: ConsoleTemplate);
                a.File(
                    path: System.IO.Path.Combine(logsDir, "host-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: FileTemplate);
            })
            .WriteTo.Sink(new UiLogSink(stream))
            .CreateLogger();
    }

    // Default Information (Debug in DEBUG builds); FCT_LOG_LEVEL overrides at runtime
    // (Trace/Debug/Information/Warning/Error/Fatal).
    private static LogEventLevel ResolveMinimumLevel()
    {
        var env = Environment.GetEnvironmentVariable("FCT_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse<LogEventLevel>(env, ignoreCase: true, out var lvl))
            return lvl;
#if DEBUG
        return LogEventLevel.Debug;
#else
        return LogEventLevel.Information;
#endif
    }
}
