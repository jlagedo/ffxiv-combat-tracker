using System;
using System.Collections.Generic;
using System.Threading;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fct.App.Tests;

// A throwing subscriber must never kill the stream or starve peers — and must no longer be silent.
// This locks in the highest-impact fix: the bus pump swallow that used to hide EVERY engine fold
// error now logs (throttled) BusSubscriberFaulted while the stream keeps flowing to good peers.
public class GameEventBusFaultTests
{
    private static readonly IReadOnlyDictionary<string, object> NoTags = new Dictionary<string, object>();

    private static CombatSwing Swing(int i) =>
        new(i, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
            2, false, "none", 100 + i, i, "Attack", "You", "", "Dummy", NoTags);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<EventId> Events = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Events) Events.Add(eventId);
        }
        private sealed class Scope : IDisposable { public static readonly Scope Instance = new(); public void Dispose() { } }
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    [Fact]
    public void A_throwing_subscriber_is_logged_once_and_a_good_peer_keeps_receiving()
    {
        var log = new CapturingLogger<GameEventBus>();
        using var bus = new GameEventBus(capacity: 1024, log: log);

        long thrown = 0, received = 0;
        using var bad = bus.Subscribe(GameEventFilter.All, _ => { Interlocked.Increment(ref thrown); throw new InvalidOperationException("boom"); });
        using var good = bus.Subscribe(GameEventFilter.All, _ => Interlocked.Increment(ref received));

        const int n = 50;
        for (int i = 0; i < n; i++) bus.Emit(Swing(i));

        // The good peer receives everything despite the bad peer throwing on every event.
        Assert.True(SpinUntil(() => Interlocked.Read(ref received) == n),
            $"good peer starved (received={Interlocked.Read(ref received)})");
        Assert.True(SpinUntil(() => Interlocked.Read(ref thrown) == n),
            $"bad peer did not see every event (thrown={Interlocked.Read(ref thrown)})");

        // The throttled counter emits the first fault immediately, then folds the rest — so exactly one
        // BusSubscriberFaulted within the 5s window, not 50.
        int faults;
        lock (log.Events) faults = log.Events.FindAll(e => e == LogEvents.BusSubscriberFaulted).Count;
        Assert.Equal(1, faults);
    }
}
