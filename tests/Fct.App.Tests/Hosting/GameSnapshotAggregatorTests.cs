using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.App.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Hosting;

/// <summary>
/// D7 (host link): <see cref="GameSnapshotAggregator"/> folds the live event bus into the pull
/// <see cref="IGameSnapshot"/> the compat shim's <c>IDataRepository</c> reads. Events are delivered on
/// the bus pump thread, so assertions wait for the published snapshot to settle.
/// </summary>
public class GameSnapshotAggregatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static Actor Combatant(uint id, string name, PartyMembership party = PartyMembership.None,
        uint hp = 100, uint maxHp = 100)
        => new(id, 0, ActorKind.Player, 0, 100, name, hp, maxHp, 0, 0, null, default,
            0, string.Empty, 0, 0, 0, 0, 0, party, false,
            Array.Empty<StatusEffect>(), Array.Empty<EnmityEntry>());

    private static async Task<(GameEventBus bus, GameSnapshotProvider provider, GameSnapshotAggregator agg)> NewAsync()
    {
        var bus = new GameEventBus();
        var provider = new GameSnapshotProvider();
        var agg = new GameSnapshotAggregator(bus, provider, NullLogger<GameSnapshotAggregator>.Instance);
        await agg.StartAsync(CancellationToken.None);
        return (bus, provider, agg);
    }

    [Fact]
    public async Task Folds_combatants_zone_party_and_player()
    {
        var (bus, provider, agg) = await NewAsync();

        bus.Emit(new ZoneChanged(1, T0, 132, "Limsa Lominsa"));
        bus.Emit(new CombatantAdded(2, T0, Combatant(10, "Alice", PartyMembership.Party)));
        bus.Emit(new CombatantAdded(3, T0, Combatant(20, "Boss")));
        bus.Emit(new PartyChanged(4, T0, new uint[] { 10 }));
        bus.Emit(new PrimaryPlayerChanged(5, T0, 10, "Alice"));

        Assert.True(TestWait.Until(() => provider.Current.Player?.Id == 10));
        var snap = provider.Current;

        Assert.Equal(132u, snap.Zone.Id);
        Assert.Equal("Limsa Lominsa", snap.Zone.Name);
        Assert.Equal(new uint[] { 10, 20 }, snap.Actors.Select(a => a.Id).OrderBy(x => x));
        Assert.Equal("Alice", snap.Player!.Name);
        Assert.Equal(new uint[] { 10 }, snap.Party.Members.Select(a => a.Id));
        Assert.Equal(PartyMembership.Party, snap.Party.Composition);
        Assert.Equal(20u, snap.Find(20)!.Id);

        await agg.StopAsync(CancellationToken.None);
        bus.Dispose();
    }

    [Fact]
    public async Task Removes_combatants_and_updates_hp()
    {
        var (bus, provider, agg) = await NewAsync();

        bus.Emit(new CombatantAdded(1, T0, Combatant(10, "Alice", hp: 100, maxHp: 100)));
        bus.Emit(new CombatantAdded(2, T0, Combatant(20, "Bob")));
        Assert.True(TestWait.Until(() => provider.Current.Actors.Count == 2));

        bus.Emit(new HpUpdated(3, T0, 10, 55, 100));
        Assert.True(TestWait.Until(() => provider.Current.Find(10)?.Hp == 55));

        bus.Emit(new CombatantRemoved(4, T0, 20));
        Assert.True(TestWait.Until(() => provider.Current.Find(20) is null));

        Assert.Single(provider.Current.Actors);

        await agg.StopAsync(CancellationToken.None);
        bus.Dispose();
    }

    [Fact]
    public async Task Raw_log_lines_do_not_publish_a_snapshot()
    {
        var (bus, provider, agg) = await NewAsync();
        var before = provider.Current;

        bus.Emit(new RawLogLine(1, T0, LogMessageType.ChatLog, "00|chat", "00|chat"));
        Thread.Sleep(50);

        // No state event fired → the aggregator never republished (still the initial empty snapshot).
        Assert.Same(before, provider.Current);

        await agg.StopAsync(CancellationToken.None);
        bus.Dispose();
    }
}
