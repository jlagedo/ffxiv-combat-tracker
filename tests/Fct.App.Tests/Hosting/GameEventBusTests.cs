using System;
using System.Collections.Generic;
using System.Threading;
using Fct.Abstractions;
using Fct.App.Hosting;
using Xunit;

namespace Fct.App.Tests.Hosting;

public class GameEventBusTests
{
    private static ZoneChanged Zone(long seq = 1) => new(seq, DateTimeOffset.UnixEpoch, 100, "Zone");
    private static RawLogLine Raw(long seq = 1) => new(seq, DateTimeOffset.UnixEpoch, LogMessageType.ChatLog, "line", "line");

    [Fact]
    public void Delivers_to_callback_subscriber()
    {
        using var bus = new GameEventBus();
        var received = new List<GameEvent>();
        using var _ = bus.Subscribe(GameEventFilter.All, e => { lock (received) received.Add(e); });

        bus.Emit(Zone());

        Assert.True(TestWait.Until(() => { lock (received) return received.Count == 1; }));
        Assert.IsType<ZoneChanged>(received[0]);
    }

    [Fact]
    public void Filter_excludes_raw_log_lines_when_disabled()
    {
        using var bus = new GameEventBus();
        var received = new List<GameEvent>();
        var filter = new GameEventFilter(Types: null, IncludeRawLogLines: false);
        using var _ = bus.Subscribe(filter, e => { lock (received) received.Add(e); });

        bus.Emit(Raw());
        bus.Emit(Zone());

        Assert.True(TestWait.Until(() => { lock (received) return received.Count == 1; }));
        Assert.All(received, e => Assert.IsNotType<RawLogLine>(e));
    }

    [Fact]
    public void Filter_typed_selects_only_requested_type()
    {
        using var bus = new GameEventBus();
        var received = new List<GameEvent>();
        var filter = new GameEventFilter(Types: new[] { typeof(ZoneChanged) });
        using var _ = bus.Subscribe(filter, e => { lock (received) received.Add(e); });

        bus.Emit(Zone());
        bus.Emit(new PartyChanged(2, DateTimeOffset.UnixEpoch, Array.Empty<uint>()));

        Assert.True(TestWait.Until(() => { lock (received) return received.Count == 1; }));
        Assert.IsType<ZoneChanged>(received[0]);
    }

    [Fact]
    public void Throwing_handler_is_isolated_from_peers()
    {
        using var bus = new GameEventBus();
        var good = new List<GameEvent>();
        using var _a = bus.Subscribe(GameEventFilter.All, _ => throw new InvalidOperationException("boom"));
        using var _b = bus.Subscribe(GameEventFilter.All, e => { lock (good) good.Add(e); });

        bus.Emit(Zone());

        Assert.True(TestWait.Until(() => { lock (good) return good.Count == 1; }));
    }

    [Fact]
    public void Slow_consumer_gets_drop_oldest_backpressure()
    {
        using var bus = new GameEventBus(capacity: 2);
        var gate = new ManualResetEventSlim(false);
        var delivered = 0;
        using var _ = bus.Subscribe(GameEventFilter.All, _ =>
        {
            // Block the pump on the first event so the bounded channel fills and drops.
            gate.Wait(5000);
            Interlocked.Increment(ref delivered);
        });

        for (long i = 0; i < 50; i++) bus.Emit(Zone(i));

        // With the pump parked, capacity=2 means the excess must drop-oldest.
        Assert.True(TestWait.Until(() => bus.DroppedCount > 0));
        gate.Set();
    }

    [Fact]
    public void Matches_gates_raw_and_typed_events()
    {
        Assert.True(GameEventBus.Matches(GameEventFilter.All, Raw()));
        Assert.False(GameEventBus.Matches(new GameEventFilter(IncludeRawLogLines: false), Raw()));
        Assert.True(GameEventBus.Matches(new GameEventFilter(Types: new[] { typeof(ZoneChanged) }), Zone()));
        Assert.False(GameEventBus.Matches(new GameEventFilter(Types: new[] { typeof(PartyChanged) }), Zone()));
    }

    [Fact]
    public void NextSequence_is_monotonic()
    {
        using var bus = new GameEventBus();
        Assert.Equal(1, bus.NextSequence());
        Assert.Equal(2, bus.NextSequence());
        Assert.Equal(3, bus.NextSequence());
    }
}
