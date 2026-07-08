using System;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace Fct.App.Logging;

// Serilog sink that turns each log event into a LogEntry and appends it to the ILogStream the Console
// reads. Wired into the host's single LoggerConfiguration (LoggingBootstrap), so it sees the entire
// stack — host records and the satellite's forwarded records re-emitted through the same pipeline.
// Mirrors Fct.LegacyHost/Logging/BridgeLogSink's field extraction.
internal sealed class UiLogSink : ILogEventSink
{
    private readonly LogStream _stream;

    public UiLogSink(LogStream stream) => _stream = stream;

    public void Emit(LogEvent e)
    {
        var entry = new LogEntry(
            e.Timestamp,
            MapLevel(e.Level),
            ShortSource(e),
            e.RenderMessage(CultureInfo.InvariantCulture),
            e.Exception?.ToString());
        _stream.Append(entry);
    }

    private static ConsoleLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => ConsoleLevel.Verbose,
        LogEventLevel.Debug => ConsoleLevel.Debug,
        LogEventLevel.Information => ConsoleLevel.Information,
        LogEventLevel.Warning => ConsoleLevel.Warning,
        LogEventLevel.Error => ConsoleLevel.Error,
        LogEventLevel.Fatal => ConsoleLevel.Fatal,
        _ => ConsoleLevel.Information,
    };

    // The last dotted segment of SourceContext keeps rows scannable (e.g. "Fct.Satellite.ffxiv" → "ffxiv").
    private static string ShortSource(LogEvent e)
    {
        if (!e.Properties.TryGetValue("SourceContext", out var v) || v is not ScalarValue { Value: string full }
            || string.IsNullOrEmpty(full))
            return "";
        var dot = full.LastIndexOf('.');
        return dot >= 0 && dot < full.Length - 1 ? full[(dot + 1)..] : full;
    }
}
