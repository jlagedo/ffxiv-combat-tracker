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

    // P1.6 (alliance party gate) — G7: PartyChanged.PartySize (the SDK's 8-person party size) must
    // cross the host fold distinct from the up-to-24 alliance Members list. Chosen as the
    // "satellite gate" home per the plan's own escape hatch: GameSnapshotAggregator is the faithful,
    // deterministic host-fold path that a real satellite ultimately routes through (constraint 2 —
    // the datum crosses the host pipe), without the added weight/nondeterminism of a real subprocess.
    [Fact]
    public async Task Alliance_party_size_survives_the_host_fold_distinct_from_member_count_pending_P3()
    {
        var (bus, provider, agg) = await NewAsync();

        var allianceRoster = Enumerable.Range(1, 24).Select(i => (uint)i).ToArray();
        foreach (var id in allianceRoster)
            bus.Emit(new CombatantAdded(id, T0, Combatant(id, $"Member{id}", PartyMembership.Alliance)));
        bus.Emit(new PartyChanged(100, T0, allianceRoster, PartySize: 8));

        Assert.True(TestWait.Until(() => provider.Current.Party.Members.Count == 24));
        var snap = provider.Current;

        // The full 24-member alliance roster crosses intact regardless of the gate's outcome.
        Assert.Equal(24, snap.Party.Members.Count);

        // RED today: GameSnapshotAggregator.Publish() (GameSnapshotAggregator.cs:127) constructs
        // PartySnapshot(members, composition) — it never reads PartyChanged.PartySize, so
        // PartySnapshot.Size stays its default 0 instead of the true 8-person party size.
        Assert.Equal(8, snap.Party.Size);

        await agg.StopAsync(CancellationToken.None);
        bus.Dispose();
    }

    // P3.4 (host fold + routing) — constraint 3 (modern-API parity): a SessionStateChanged env frame
    // folds into IGameSnapshot.Client, the exact seam a native net10 plugin reads. Process fields
    // (IsRunning/IsForeground/ProcessId) folded independently (GameProcessChanged) must survive
    // untouched alongside the newly-folded env fields.
    [Fact]
    public async Task SessionStateChanged_folds_env_fields_into_client_without_clobbering_process_fields()
    {
        var (bus, provider, agg) = await NewAsync();

        bus.Emit(new GameProcessChanged(1, T0, 4321));
        Assert.True(TestWait.Until(() => provider.Current.Client.ProcessId == 4321));

        bus.Emit(new SessionStateChanged(2, T0, "2025.07.01.0000.0000", GameLanguage.Korean,
            GameRegion.Korean, TimeSpan.FromSeconds(5), IsChatLogAvailable: true));

        Assert.True(TestWait.Until(() => provider.Current.Client.Version == "2025.07.01.0000.0000"));
        var snap = provider.Current;

        Assert.Equal("2025.07.01.0000.0000", snap.Client.Version);
        Assert.Equal(GameRegion.Korean, snap.Client.Region);
        Assert.Equal(GameLanguage.Korean, snap.Client.Language);
        Assert.Equal(TimeSpan.FromSeconds(5), snap.Client.ServerClockOffset);
        Assert.True(snap.Client.IsChatLogAvailable);

        // The process fold from GameProcessChanged must survive the later SessionStateChanged fold.
        Assert.Equal(4321, snap.Client.ProcessId);

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
    public async Task RepositorySnapshot_replaces_the_roster_wholesale_with_fresh_hp_and_position()
    {
        var (bus, provider, agg) = await NewAsync();

        // A stale incremental combatant that a later full snapshot must drop.
        bus.Emit(new CombatantAdded(1, T0, Combatant(99, "Ghost")));
        Assert.True(TestWait.Until(() => provider.Current.Find(99) is not null));

        var you = Combatant(10, "You", PartyMembership.Party, hp: 50, maxHp: 100) with
        {
            Position = new Position(1.5f, 2.5f, 3.5f, 0.25f),
        };
        var boss = Combatant(20, "Boss");
        bus.Emit(new RepositorySnapshot(2, T0, new[] { you, boss }));

        Assert.True(TestWait.Until(() => provider.Current.Find(10)?.Hp == 50 && provider.Current.Find(99) is null));
        var snap = provider.Current;
        Assert.Equal(new uint[] { 10, 20 }, snap.Actors.Select(a => a.Id).OrderBy(x => x));
        Assert.Equal(new Position(1.5f, 2.5f, 3.5f, 0.25f), snap.Find(10)!.Position);

        await agg.StopAsync(CancellationToken.None);
        bus.Dispose();
    }

    [Fact]
    public async Task Resource_dictionaries_and_pid_reach_the_snapshot_catalog_and_client()
    {
        var (bus, provider, agg) = await NewAsync();

        bus.Emit(new ResourceDictionaryForwarded(1, T0, ResourceKind.Action,
            new System.Collections.Generic.Dictionary<uint, string> { [1] = "Heavy Swing", [2] = "Maim" }));
        bus.Emit(new GameProcessChanged(2, T0, 4321));

        Assert.True(TestWait.Until(() => provider.Current.Client.ProcessId == 4321));
        var snap = provider.Current;
        Assert.Equal("Heavy Swing", snap.Resources.Name(ResourceKind.Action, 1));
        Assert.Equal(2, snap.Resources.All(ResourceKind.Action).Count);
        Assert.Null(snap.Resources.Name(ResourceKind.Status, 1));   // unforwarded kind → empty
        Assert.Equal(4321, snap.Client.ProcessId);

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
