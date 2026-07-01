using System;
using System.Collections.ObjectModel;
using System.Linq;
using Fct.Abstractions;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Compat.Shim;

/// <summary>
/// Projects the modern <see cref="IGameEventStream"/> onto the SDK's <see cref="IDataSubscription"/>
/// surface a recompiled plugin binds to (OverlayPlugin reflects a plugin's <c>DataSubscription</c>
/// property and subscribes these delegates). One subscription to the bus fans typed
/// <see cref="GameEvent"/> records out to the legacy delegates, mirroring how real ACT delivers the
/// SDK events off one decoded packet.
/// </summary>
/// <remarks>
/// D6 maps the four events with a typed-bus source (<c>LogLine</c>, <c>ZoneChanged</c>,
/// <c>PartyListChanged</c>, <c>PrimaryPlayerChanged</c>). The other seven are interface-required but
/// inert: <c>CombatantAdded</c>/<c>CombatantRemoved</c> await the <c>Actor</c>→<c>Combatant</c>
/// projection (D7); <c>NetworkReceived</c>/<c>NetworkSent</c> (raw packets), <c>PlayerStatsChanged</c>,
/// <c>ParsedLogLine</c>, and <c>ProcessChanged</c> have no typed-bus source. Their add/remove are
/// no-ops — nothing raises them, so a handler would never be called.
/// </remarks>
public sealed class DataSubscriptionAdapter : IDataSubscription, IDisposable
{
    private readonly IDisposable _subscription;

    public DataSubscriptionAdapter(IGameEventStream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        _subscription = stream.Subscribe(GameEventFilter.All, OnGameEvent);
    }

    // --- Mapped events (raised from OnGameEvent) ---------------------------------------
    public event ZoneChangedDelegate? ZoneChanged;
    public event PartyListChangedDelegate? PartyListChanged;
    public event LogLineDelegate? LogLine;
    public event PrimaryPlayerDelegate? PrimaryPlayerChanged;

    // --- Inert events (interface contract; nothing raises them — see remarks) ----------
    public event NetworkReceivedDelegate NetworkReceived { add { } remove { } }
    public event NetworkSentDelegate NetworkSent { add { } remove { } }
    public event CombatantAddedDelegate CombatantAdded { add { } remove { } }          // → D7
    public event CombatantRemovedDelegate CombatantRemoved { add { } remove { } }      // → D7
    public event PlayerStatsChangedDelegate PlayerStatsChanged { add { } remove { } }
    public event ParsedLogLineDelegate ParsedLogLine { add { } remove { } }
    public event ProcessChangedDelegate ProcessChanged { add { } remove { } }

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
        }
    }

    public void Dispose() => _subscription.Dispose();
}
