using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using FFXIV_ACT_Plugin.Common;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D6: <see cref="DataSubscriptionAdapter"/> projects the modern <c>IGameEventStream</c> onto the
/// SDK's <see cref="IDataSubscription"/> delegates. Four events map from a typed-bus source
/// (<c>LogLine</c>/<c>ZoneChanged</c>/<c>PartyListChanged</c>/<c>PrimaryPlayerChanged</c>); the other
/// seven are inert. Mirrors the emit→assert shape of <see cref="RawLogLineTests"/>.
/// </summary>
public class DataSubscriptionTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (DataSubscriptionAdapter sub, InMemoryEventBus bus) NewAdapter()
    {
        var host = new FakePluginHost();
        var adapter = new DataSubscriptionAdapter(host.Game.Events);
        return (adapter, host.Bus!);
    }

    [Fact]
    public void ZoneChanged_maps_id_and_name()
    {
        var (sub, bus) = NewAdapter();
        uint id = 0; string name = null!;
        sub.ZoneChanged += (z, n) => { id = z; name = n; };

        bus.Emit(new ZoneChanged(1, T0, 132, "Limsa Lominsa"));

        Assert.Equal(132u, id);
        Assert.Equal("Limsa Lominsa", name);
    }

    [Fact]
    public void PartyListChanged_maps_members_with_size_equal_to_count()
    {
        var (sub, bus) = NewAdapter();
        ReadOnlyCollection<uint> list = null!; int size = -1;
        sub.PartyListChanged += (l, s) => { list = l; size = s; };

        bus.Emit(new PartyChanged(1, T0, new List<uint> { 100, 200, 300 }));

        Assert.Equal(new uint[] { 100, 200, 300 }, list);
        Assert.Equal(3, size);
    }

    [Fact]
    public void LogLine_maps_type_and_line()
    {
        var (sub, bus) = NewAdapter();
        uint type = 0; string line = null!;
        sub.LogLine += (t, _, l) => { type = t; line = l; };

        bus.Emit(new RawLogLine(1, T0, LogMessageType.ChatLog, "00|chat|hello", "00|chat|hello"));

        Assert.Equal((uint)LogMessageType.ChatLog, type);
        Assert.Equal("00|chat|hello", line);
    }

    [Fact]
    public void PrimaryPlayerChanged_fires_the_parameterless_delegate()
    {
        var (sub, bus) = NewAdapter();
        int fired = 0;
        sub.PrimaryPlayerChanged += () => fired++;

        bus.Emit(new PrimaryPlayerChanged(1, T0, 42, "Hero"));

        Assert.Equal(1, fired);
    }

    [Fact]
    public void CombatantAdded_maps_the_actor_to_a_combatant()
    {
        var (sub, bus) = NewAdapter();
        object? got = null;
        sub.CombatantAdded += c => got = c;

        bus.Emit(new CombatantAdded(1, T0, FakeActors.Player(7, "Dummy")));

        var combatant = Assert.IsType<FFXIV_ACT_Plugin.Common.Models.Combatant>(got);
        Assert.Equal(7u, combatant.ID);
        Assert.Equal("Dummy", combatant.Name);
    }

    [Fact]
    public void CombatantRemoved_carries_the_actor_id()
    {
        var (sub, bus) = NewAdapter();
        object? got = null;
        sub.CombatantRemoved += c => got = c;

        bus.Emit(new CombatantRemoved(2, T0, 7));

        var combatant = Assert.IsType<FFXIV_ACT_Plugin.Common.Models.Combatant>(got);
        Assert.Equal(7u, combatant.ID);
    }

    [Fact]
    public void Wired_hub_projection_delivers_events_to_the_reflected_property()
    {
        var host = new FakePluginHost();
        var hub = new Advanced_Combat_Tracker.FormActMain(host);
        hub.AttachDataSubscription(new DataSubscriptionAdapter(host.Game.Events));

        uint zoneId = 0;
        hub.DataSubscription.ZoneChanged += (z, _) => zoneId = z;
        host.Bus!.Emit(new ZoneChanged(1, T0, 154, "Old Gridania"));

        Assert.Equal(154u, zoneId);
    }
}
