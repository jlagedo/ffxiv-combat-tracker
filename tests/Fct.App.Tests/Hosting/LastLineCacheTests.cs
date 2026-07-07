using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Hosting;

/// <summary>
/// P4.1: <see cref="LastLineCache"/> caches the last-seen verbatim <see cref="RawLogLine"/> per
/// one-shot <see cref="LogMessageType"/> (plan §3 — <see cref="OneShotLineTypes"/>), fed purely from the
/// bus's rawlog fan-in, zero decoding. Events are delivered on the bus pump thread, so assertions wait
/// for the fold to settle (the same <see cref="TestWait"/> pattern as <see cref="GameSnapshotAggregatorTests"/>).
/// </summary>
public class LastLineCacheTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static RawLogLine Line(long seq, LogMessageType type, string body)
    {
        var raw = $"{(int)type}|{body}";
        return new RawLogLine(seq, T0, type, raw, raw);
    }

    private static async Task<(GameEventBus bus, LastLineCache cache)> NewAsync()
    {
        var bus = new GameEventBus();
        var cache = new LastLineCache(bus, NullLogger<LastLineCache>.Instance);
        await cache.StartAsync(CancellationToken.None);
        return (bus, cache);
    }

    [Fact]
    public async Task Holds_only_the_last_instance_per_one_shot_type_and_ignores_non_one_shot_types()
    {
        var (bus, cache) = await NewAsync();

        // An earlier "first boot" instance of every one-shot type...
        bus.Emit(Line(1, LogMessageType.Version, "first-boot"));
        bus.Emit(Line(2, LogMessageType.Settings, "first-boot"));
        bus.Emit(Line(3, LogMessageType.Process, "first-boot"));
        bus.Emit(Line(4, LogMessageType.Territory, "Limsa Lominsa"));
        bus.Emit(Line(5, LogMessageType.ChangePrimaryPlayer, "Alice"));
        bus.Emit(Line(6, LogMessageType.ChangeMap, "map-1"));
        bus.Emit(Line(7, LogMessageType.PlayerStats, "stats-1"));

        // ...combat/chat lines interleaved in between — never one-shot, must never be cached...
        bus.Emit(Line(8, LogMessageType.ChatLog, "hello"));
        bus.Emit(Line(9, LogMessageType.ActionEffect, "swing-1"));
        bus.Emit(Line(10, LogMessageType.AOEActionEffect, "aoe-1"));
        bus.Emit(Line(11, LogMessageType.StatusAdd, "status-1"));

        // ...then a LATER "relog / zone move" instance of every one-shot type, which must win.
        bus.Emit(Line(12, LogMessageType.Version, "relog"));
        bus.Emit(Line(13, LogMessageType.Settings, "relog"));
        bus.Emit(Line(14, LogMessageType.Process, "relog"));
        bus.Emit(Line(15, LogMessageType.Territory, "Kugane"));
        bus.Emit(Line(16, LogMessageType.ChangePrimaryPlayer, "Bob"));
        bus.Emit(Line(17, LogMessageType.ChangeMap, "map-2"));
        bus.Emit(Line(18, LogMessageType.PlayerStats, "stats-2"));

        Assert.True(TestWait.Until(() => cache.Snapshot().Count == 7));
        var snapshot = cache.Snapshot();

        // ACT emission order (253/249/250 -> 01 -> 02 -> 40 -> 12), each slot holding the LAST instance.
        Assert.Equal(
            new[]
            {
                LogMessageType.Version, LogMessageType.Settings, LogMessageType.Process,
                LogMessageType.Territory, LogMessageType.ChangePrimaryPlayer, LogMessageType.ChangeMap,
                LogMessageType.PlayerStats,
            },
            snapshot.Select(l => l.Type));

        Assert.Equal(12L, snapshot.First(l => l.Type == LogMessageType.Version).Sequence);
        Assert.Equal(13L, snapshot.First(l => l.Type == LogMessageType.Settings).Sequence);
        Assert.Equal(14L, snapshot.First(l => l.Type == LogMessageType.Process).Sequence);
        Assert.Equal(15L, snapshot.First(l => l.Type == LogMessageType.Territory).Sequence);
        Assert.Equal(16L, snapshot.First(l => l.Type == LogMessageType.ChangePrimaryPlayer).Sequence);
        Assert.Equal(17L, snapshot.First(l => l.Type == LogMessageType.ChangeMap).Sequence);
        Assert.Equal(18L, snapshot.First(l => l.Type == LogMessageType.PlayerStats).Sequence);
        Assert.Equal("1|Kugane", snapshot.First(l => l.Type == LogMessageType.Territory).Line);

        // Never cached: combat/chat types are outside the one-shot set entirely, first instance or last.
        Assert.DoesNotContain(snapshot, l => l.Type == LogMessageType.ChatLog);
        Assert.DoesNotContain(snapshot, l => l.Type == LogMessageType.ActionEffect);
        Assert.DoesNotContain(snapshot, l => l.Type == LogMessageType.AOEActionEffect);
        Assert.DoesNotContain(snapshot, l => l.Type == LogMessageType.StatusAdd);

        await cache.StopAsync(CancellationToken.None);
        bus.Dispose();
    }

    [Fact]
    public async Task Snapshot_stays_empty_when_only_non_one_shot_lines_have_been_observed()
    {
        var (bus, cache) = await NewAsync();

        bus.Emit(Line(1, LogMessageType.ActionEffect, "swing"));
        bus.Emit(Line(2, LogMessageType.ChatLog, "hi"));
        bus.Emit(Line(3, LogMessageType.AOEActionEffect, "aoe"));

        // No positive-wait exists for "stays empty forever" — let the pump settle, then assert.
        TestWait.Until(() => false, timeoutMs: 200);
        Assert.Empty(cache.Snapshot());

        await cache.StopAsync(CancellationToken.None);
        bus.Dispose();
    }
}
