using System;
using System.Collections.Generic;
using System.Threading;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Host;
using Xunit;

namespace Fct.App.Tests;

// ISOLATION-PLAN P4: the host→satellite fan-out router. Two satellites subscribed to the same stream
// set receive identical, identically-ordered frames; a satellite that did not subscribe to a stream
// never sees it; an artificially-stalled satellite drops its own frames (drop-oldest) while a fast peer
// stays lossless. Driven in-process over the bus (no satellite processes — that fabric is P3's gate).
public class SatelliteEgressTests
{
    private static readonly IReadOnlyDictionary<string, object> NoTags = new Dictionary<string, object>();

    private static CombatSwing Swing(int i) =>
        new(i, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
            2, false, "none", 100 + i, i, "Attack", "You", "", "Dummy", NoTags);

    private static GameEventFilter Only(params Type[] types) => new(types, IncludeRawLogLines: false);

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

    private static (List<string> lines, Action<string> send) Recorder()
    {
        var lines = new List<string>();
        Action<string> send = w => { lock (lines) lines.Add(w); };
        return (lines, send);
    }

    [Fact]
    public void Two_subscribers_get_identical_ordered_streams_and_a_nonsubscriber_gets_nothing()
    {
        var bus = new InMemoryEventBus();
        var (a, sendA) = Recorder();
        var (b, sendB) = Recorder();
        var (life, sendLife) = Recorder();

        using var egA = new SatelliteEgress("a", bus, Only(typeof(CombatSwing)), sendA);
        using var egB = new SatelliteEgress("b", bus, Only(typeof(CombatSwing)), sendB);
        using var egLife = new SatelliteEgress("life", bus, Only(typeof(SetEncounterRequested)), sendLife);

        bus.Emit(new SetEncounterRequested(0, Swing(0).Timestamp, "You", "Dummy"));
        for (int i = 0; i < 50; i++) bus.Emit(Swing(i));

        Assert.True(SpinUntil(() => egA.Sent == 50 && egB.Sent == 50 && egLife.Sent == 1),
            $"fan-out did not settle (a={egA.Sent} b={egB.Sent} life={egLife.Sent})");

        List<string> la, lb, ll;
        lock (a) la = new(a);
        lock (b) lb = new(b);
        lock (life) ll = new(life);

        Assert.Equal(50, la.Count);
        Assert.Equal(la, lb);                 // identical, identically-ordered
        Assert.Single(ll);                    // the lifecycle-only satellite saw only the SetEncounter
        Assert.DoesNotContain("EVT SWING", ll[0]);
        Assert.Equal(0, egA.Dropped);
        Assert.Equal(0, egB.Dropped);
    }

    [Fact]
    public void A_stalled_satellite_drops_alone_while_a_fast_peer_stays_lossless()
    {
        var bus = new InMemoryEventBus(4096);
        var (fast, sendFast) = Recorder();

        // The stalled satellite's writer blocks in send, so its small ring overflows and drops-oldest.
        var release = new ManualResetEventSlim(false);
        long stalledSeen = 0;
        Action<string> sendStalled = _ => { Interlocked.Increment(ref stalledSeen); release.Wait(); };

        using var egFast = new SatelliteEgress("fast", bus, Only(typeof(CombatSwing)), sendFast, capacity: 4096);
        using var egStalled = new SatelliteEgress("stalled", bus, Only(typeof(CombatSwing)), sendStalled, capacity: 8);

        const int n = 500;
        for (int i = 0; i < n; i++) bus.Emit(Swing(i));

        // The fast peer receives everything, in order, with no drops.
        Assert.True(SpinUntil(() => egFast.Sent == n), $"fast peer stalled too (sent={egFast.Sent})");
        Assert.Equal(0, egFast.Dropped);
        List<string> lf;
        lock (fast) lf = new(fast);
        Assert.Equal(n, lf.Count);

        // The stalled peer dropped-oldest: it is wedged on the first send with a full ring behind it.
        Assert.True(SpinUntil(() => egStalled.Dropped > 0), "stalled satellite did not drop");
        Assert.True(egStalled.Sent < n, "stalled satellite somehow kept up");

        release.Set();   // let the stalled writer unwind before dispose
    }
}
