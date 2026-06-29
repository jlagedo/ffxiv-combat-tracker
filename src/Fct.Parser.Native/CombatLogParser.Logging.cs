using Microsoft.Extensions.Logging;

namespace Fct.Parser.Native;

// Source-generated, allocation-free logging for the parser hot path. EventIds sit in the 30xx
// (parser) band of the stack-wide taxonomy. Static form so the methods short-circuit on a disabled
// (e.g. Null) logger with no work done.
public sealed partial class CombatLogParser
{
    [LoggerMessage(EventId = 3000, EventName = "LineDropped", Level = LogLevel.Trace,
        Message = "Dropped unparseable log line: {Raw}")]
    private static partial void LogLineDropped(ILogger logger, string raw);

    [LoggerMessage(EventId = 3002, EventName = "ParseSummary", Level = LogLevel.Debug,
        Message = "Parsed {Fed} line(s); {Dropped} were unparseable and skipped")]
    private static partial void LogParseSummary(ILogger logger, long fed, long dropped);
}
