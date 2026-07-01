using System;
using System.Collections.ObjectModel;
using System.Linq;
using Fct.Abstractions;
using FFXIV_ACT_Plugin.Common;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;

namespace Fct.Compat.Shim;

/// <summary>
/// Projects the modern <see cref="IGameEventStream"/> onto the SDK's <see cref="IDataSubscription"/>
/// surface a recompiled plugin binds to (OverlayPlugin reflects a plugin's <c>DataSubscription</c>
/// property and subscribes these delegates). One subscription to the bus fans typed
/// <see cref="GameEvent"/> records out to the legacy delegates, mirroring how real ACT delivers the
/// SDK events off one decoded packet.
/// </summary>
/// <remarks>
/// Eight events map from a host source: <c>LogLine</c>, <c>ZoneChanged</c>, <c>PartyListChanged</c>,
/// <c>PrimaryPlayerChanged</c>, and <c>CombatantAdded</c>/<c>CombatantRemoved</c> (projected via
/// <see cref="CombatantProjector"/>) from the typed bus, plus <c>NetworkReceived</c>/<c>NetworkSent</c>
/// from the raw-packet firehose (<see cref="IRawPacketSource"/>) — OverlayPlugin's
/// <c>RegisterNetworkParser</c> read path. The remaining three are interface-required but inert:
/// <c>PlayerStatsChanged</c>, <c>ParsedLogLine</c>, and <c>ProcessChanged</c> have no source yet. Their
/// add/remove are no-ops — nothing raises them, so a handler would never be called.
/// </remarks>
public sealed class DataSubscriptionAdapter : IDataSubscription, IDisposable
{
    private readonly IDisposable _subscription;
    private readonly IDisposable? _packetSubscription;

    public DataSubscriptionAdapter(IGameEventStream stream, IRawPacketSource? rawPackets = null)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        _subscription = stream.Subscribe(GameEventFilter.All, OnGameEvent);
        _packetSubscription = rawPackets?.Subscribe(OnRawPacket);
    }

    // --- Mapped events (raised from OnGameEvent / OnRawPacket) --------------------------
    public event ZoneChangedDelegate? ZoneChanged;
    public event PartyListChangedDelegate? PartyListChanged;
    public event LogLineDelegate? LogLine;
    public event PrimaryPlayerDelegate? PrimaryPlayerChanged;
    public event CombatantAddedDelegate? CombatantAdded;
    public event CombatantRemovedDelegate? CombatantRemoved;
    public event NetworkReceivedDelegate? NetworkReceived;
    public event NetworkSentDelegate? NetworkSent;

    // --- Inert events (interface contract; nothing raises them — see remarks) ----------
    public event PlayerStatsChangedDelegate PlayerStatsChanged { add { } remove { } }
    public event ParsedLogLineDelegate ParsedLogLine { add { } remove { } }
    public event ProcessChangedDelegate ProcessChanged { add { } remove { } }

    // Runs on the raw-packet source's fan-out thread (serialized per source). Routes by direction to
    // the SDK delegates OverlayPlugin's network processors bind — the exact (connection, epoch, bytes) triple.
    private void OnRawPacket(RawPacket p)
    {
        if (p.Direction == PacketDirection.Sent)
            NetworkSent?.Invoke(p.Connection, p.Epoch, p.Bytes);
        else
            NetworkReceived?.Invoke(p.Connection, p.Epoch, p.Bytes);
    }

    // Runs on the bus pump thread (serialized), so a plain field read + null-conditional invoke is safe.
    private void OnGameEvent(GameEvent e)
    {
        switch (e)
        {
            case RawLogLine r:
                // The SDK's original per-line seconds is not preserved on RawLogLine; synthesize it
                // best-effort from the event timestamp (the line's own timestamp is authoritative).
                LogLine?.Invoke((uint)r.Type, (uint)r.Timestamp.ToUnixTimeSeconds(), r.Line);
                break;
            case Fct.Abstractions.ZoneChanged z:
                ZoneChanged?.Invoke(z.ZoneId, z.ZoneName);
                break;
            case Fct.Abstractions.PartyChanged p:
                // partySize == member count: the real-SDK nuance (size != list count for cross-world /
                // alliance parties) is not reconstructable from the typed event.
                PartyListChanged?.Invoke(new ReadOnlyCollection<uint>(p.Members.ToList()), p.Members.Count);
                break;
            case Fct.Abstractions.PrimaryPlayerChanged:
                // The SDK delegate is parameterless; consumers re-poll the repository for the new player.
                PrimaryPlayerChanged?.Invoke();
                break;
            case Fct.Abstractions.CombatantAdded ca:
                CombatantAdded?.Invoke(CombatantProjector.ToCombatant(ca.Combatant));
                break;
            case Fct.Abstractions.CombatantRemoved cr:
                // The modern event carries only the id; the SDK delegate takes the removed Combatant.
                CombatantRemoved?.Invoke(new SdkModels.Combatant { ID = cr.ActorId });
                break;
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _packetSubscription?.Dispose();
    }
}
