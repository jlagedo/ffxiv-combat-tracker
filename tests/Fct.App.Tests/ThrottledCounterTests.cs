using System;
using System.Collections.Generic;
using System.Threading;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fct.App.Tests;

// The shared hot-path failure logger: the first fault logs immediately (with the exception), further
// faults inside the interval are suppressed and folded into the next periodic emit's count. This is
// the linchpin the engine tick, the bus pump, and both bridge egress writers depend on — a broken
// throttle would either storm the log or hide the fault, both of which the go-live bar prohibits.
public class ThrottledCounterTests
{
    private sealed record Entry(LogLevel Level, EventId Event, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<Entry> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add(new Entry(level, eventId, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static readonly EventId TestEvent = new(4242, "TestFault");

    [Fact]
    public void First_record_logs_immediately_with_the_exception()
    {
        var log = new CapturingLogger();
        var counter = new ThrottledCounter(log, LogLevel.Error, TestEvent,
            "Faulted {Count} time(s) (total {Total})", TimeSpan.FromSeconds(30));
        var boom = new InvalidOperationException("boom");

        counter.Record(boom);

        Entry entry;
        lock (log.Entries) entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(TestEvent, entry.Event);
        Assert.Same(boom, entry.Exception);
        Assert.Equal(1, counter.Total);
    }

    [Fact]
    public void Bursts_inside_the_interval_are_suppressed_after_the_first()
    {
        var log = new CapturingLogger();
        var counter = new ThrottledCounter(log, LogLevel.Warning, TestEvent, "{Count}/{Total}",
            TimeSpan.FromSeconds(30));

        for (int i = 0; i < 100; i++) counter.Record();

        // Only the very first occurrence emitted; the other 99 were folded and await the next window.
        lock (log.Entries) Assert.Single(log.Entries);
        Assert.Equal(100, counter.Total);
    }

    [Fact]
    public void Next_emit_after_the_window_reports_the_batched_count()
    {
        var log = new CapturingLogger();
        var counter = new ThrottledCounter(log, LogLevel.Warning, TestEvent,
            "Faulted {Count} time(s) (total {Total})", TimeSpan.FromMilliseconds(40));

        counter.Record();                 // emits immediately, resets sinceLast -> 0
        for (int i = 0; i < 9; i++) counter.Record();   // 9 folded into the window

        Thread.Sleep(80);                 // let the throttle window elapse
        counter.Record();                 // 11th total -> second emit, count = the 10 held back

        List<Entry> entries;
        lock (log.Entries) entries = new(log.Entries);
        Assert.Equal(2, entries.Count);
        Assert.Contains("Faulted 10 time(s) (total 11)", entries[1].Message);
        Assert.Equal(11, counter.Total);
    }
}
