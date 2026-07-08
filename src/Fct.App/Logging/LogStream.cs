using System;
using System.Collections.Generic;

namespace Fct.App.Logging;

// Severity of a console line, mirroring Serilog's LogEventLevel so the sink maps 1:1. Kept as an
// app-local enum so the view models take no Serilog dependency.
public enum ConsoleLevel { Verbose, Debug, Information, Warning, Error, Fatal }

// One rendered log line: what the UiLogSink pulls off a Serilog event and what a console row displays.
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    ConsoleLevel Level,
    string Source,
    string Message,
    string? Exception);

// The UI-facing seam over the host's single Serilog pipeline: the sink appends every rendered record
// here, and the Console subscribes. One bounded, newest-last history the whole stack feeds (host logs
// and re-emitted satellite records both funnel through the same pipeline). Modeled on
// Fct.Host.Hosting.NotificationService — but this is the full log firehose, not a curated subset.
public interface ILogStream
{
    // Raised on the logging (producer) thread for every appended record; marshal to the UI thread.
    event Action<LogEntry>? Emitted;

    // Oldest-first copy of the retained history (bounded), so a late subscriber can seed its view.
    IReadOnlyList<LogEntry> Snapshot();
}

// Bounded drop-oldest ring shared by the sink (writer) and the Console (reader). The cap keeps memory
// flat under a firehose; the Console keeps its own, larger display cap.
public sealed class LogStream : ILogStream
{
    private const int MaxHistory = 2000;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _history = new(MaxHistory);

    public event Action<LogEntry>? Emitted;

    // Called by UiLogSink on whatever thread logged. Cheap and lock-guarded; never blocks the logger.
    public void Append(LogEntry entry)
    {
        lock (_gate)
        {
            _history.Enqueue(entry);
            while (_history.Count > MaxHistory) _history.Dequeue();
        }
        Emitted?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _history.ToArray();
    }
}
