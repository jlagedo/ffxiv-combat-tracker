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

    // Built before the host so the earliest startup (and any startup failure) is captured.
    public static Logger CreateLogger()
    {
        var logsDir = LogPaths.EnsureLogsDirectory();
        return new LoggerConfiguration()
            .MinimumLevel.Is(ResolveMinimumLevel())
            // Avalonia is chatty at Debug; keep its noise out of our file unless explicitly raised.
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Process", "host")
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .WriteTo.File(
                path: System.IO.Path.Combine(logsDir, "host-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: FileTemplate)
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
