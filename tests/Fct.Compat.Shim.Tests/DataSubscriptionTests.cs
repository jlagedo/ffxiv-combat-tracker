using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using FFXIV_ACT_Plugin.Common;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D6: <see cref="DataSubscriptionAdapter"/> projects the modern host surfaces onto the SDK's
/// <see cref="IDataSubscription"/> delegates. Six events map from the typed bus
/// (<c>LogLine</c>/<c>ZoneChanged</c>/<c>PartyListChanged</c>/<c>PrimaryPlayerChanged</c>/
/// <c>CombatantAdded</c>/<c>CombatantRemoved</c>) and <c>NetworkReceived</c>/<c>NetworkSent</c> from the
/// raw-packet firehose; the remaining three are inert. Mirrors the emit→assert shape of
/// <see cref="RawLogLineTests"/>.
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

    private static (DataSubscriptionAdapter sub, FakeRawPacketSource packets) NewAdapterWithPackets()
    {
        var host = new FakePluginHost();
        var adapter = new DataSubscriptionAdapter(host.Game.Events, host.RawPackets);
        return (adapter, host.RawPacketsFake!);
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
    public void PartyListChanged_forwards_the_real_partysize()
    {
        var (sub, bus) = NewAdapter();
        ReadOnlyCollection<uint> list = null!; int size = -1;
        sub.PartyListChanged += (l, s) => { list = l; size = s; };

        // PartySize given explicitly (P3.5) and distinct from Members.Count, proving the adapter
        // forwards it verbatim rather than deriving it from the roster length (G7).
        bus.Emit(new PartyChanged(1, T0, new List<uint> { 100, 200, 300 }, PartySize: 3));

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
    public void NetworkReceived_fires_with_the_connection_epoch_bytes_triple()
    {
        var (sub, packets) = NewAdapterWithPackets();
        string conn = null!; long epoch = 0; byte[] msg = null!;
        sub.NetworkReceived += (c, e, m) => { conn = c; epoch = e; msg = m; };

        var bytes = new byte[] { 1, 2, 3 };
        packets.Push(new RawPacket("tcp-1", 55L, bytes, PacketDirection.Received));

        Assert.Equal("tcp-1", conn);
        Assert.Equal(55L, epoch);
        Assert.Same(bytes, msg);
    }

    [Fact]
    public void NetworkSent_fires_only_for_outbound_packets()
    {
        var (sub, packets) = NewAdapterWithPackets();
        int received = 0, sent = 0;
        sub.NetworkReceived += (_, _, _) => received++;
        sub.NetworkSent += (_, _, _) => sent++;

        packets.Push(new RawPacket("c", 1L, new byte[] { 9 }, PacketDirection.Sent));

        Assert.Equal(0, received);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void Packet_events_are_inert_and_dispose_is_clean_without_a_source()
    {
        var (sub, _) = NewAdapter();   // no raw-packet source supplied
        int fired = 0;
        sub.NetworkReceived += (_, _, _) => fired++;

        sub.Dispose();   // must not throw despite the null packet subscription

        Assert.Equal(0, fired);
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
