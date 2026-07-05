using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Hosting;

/// <summary>
/// Folds the live event bus into the pull <see cref="IGameSnapshot"/>. This is the collector that turns
/// the forwarded event stream (piece C) into queryable state: it subscribes to the state-relevant
/// events, maintains a running actor/zone/party/player picture, and republishes an immutable snapshot
/// through <see cref="GameSnapshotProvider"/> after each change. The compat shim's
/// <c>IDataRepository.GetCombatantList()</c> reads whatever this publishes.
/// </summary>
/// <remarks>
/// The subscription is a single-reader bus pump, so <see cref="OnEvent"/> is serialized — the working
/// state is mutated only there and needs no lock. Each publish snapshots the working state into fresh
/// immutable collections. Raw log lines are filtered out so the firehose never drives a rebuild.
/// </remarks>
internal sealed class GameSnapshotAggregator : IHostedService
{
    private static readonly GameEventFilter StateFilter = new(
        new[]
        {
            typeof(CombatantAdded), typeof(CombatantRemoved), typeof(HpUpdated),
            typeof(ZoneChanged), typeof(PartyChanged), typeof(PrimaryPlayerChanged),
            typeof(RepositorySnapshot), typeof(ResourceDictionaryForwarded), typeof(GameProcessChanged),
        },
        IncludeRawLogLines: false);

    private readonly GameEventBus _bus;
    private readonly GameSnapshotProvider _provider;
    private readonly ILogger<GameSnapshotAggregator> _log;

    // Working state — mutated only on the (single-reader) bus pump thread, so no lock is needed.
    private readonly Dictionary<uint, Actor> _actors = new();
    private uint _playerId;
    private ZoneRef _zone;
    private HashSet<uint> _party = new();
    private readonly Dictionary<ResourceKind, IReadOnlyDictionary<uint, string>> _resources = new();
    private int _pid;

    private IDisposable? _subscription;

    public GameSnapshotAggregator(GameEventBus bus, GameSnapshotProvider provider, ILogger<GameSnapshotAggregator> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(StateFilter, OnEvent);
        _log.LogDebug("Game snapshot aggregator subscribed to the live event bus.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnEvent(GameEvent e)
    {
        switch (e)
        {
            case CombatantAdded a:
                _actors[a.Combatant.Id] = a.Combatant;
                break;
            case CombatantRemoved r:
                _actors.Remove(r.ActorId);
                break;
            case HpUpdated h:
                if (_actors.TryGetValue(h.ActorId, out var actor))
                    _actors[h.ActorId] = actor with { Hp = h.Hp, MaxHp = h.MaxHp };
                break;
            case ZoneChanged z:
                _zone = new ZoneRef(z.ZoneId, z.ZoneName);
                break;
            case PartyChanged p:
                _party = new HashSet<uint>(p.Members);
                break;
            case PrimaryPlayerChanged pp:
                _playerId = pp.ActorId;
                break;
            case RepositorySnapshot snap:
                // Authoritative full roster: replace wholesale so stale combatants drop and HP/position
                // refresh (the poll surface incremental CombatantAdded/HpUpdated can't keep fresh).
                _actors.Clear();
                foreach (var a in snap.Combatants) _actors[a.Id] = a;
                break;
            case ResourceDictionaryForwarded rf:
                _resources[rf.Kind] = rf.Entries;
                break;
            case GameProcessChanged gp:
                _pid = gp.Pid;
                break;
            default:
                return; // not state-relevant — nothing to republish
        }

        Publish();
    }

    private void Publish()
    {
        var actors = _actors.Values.ToArray();
        Actor? player = _actors.TryGetValue(_playerId, out var p) ? p : null;

        var members = actors.Where(a => _party.Contains(a.Id)).ToArray();
        var composition = members.Length == 0 ? PartyMembership.None
            : members.Any(a => a.Party == PartyMembership.Alliance) ? PartyMembership.Alliance
            : PartyMembership.Party;

        var resources = _resources.Count == 0
            ? (IResourceCatalog)EmptyResourceCatalog.Instance
            : new LiveResourceCatalog(new Dictionary<ResourceKind, IReadOnlyDictionary<uint, string>>(_resources));

        _provider.Publish(new ImmutableGameSnapshot(
            actors, player, _zone, new PartySnapshot(members, composition), resources, _pid));
    }
}

/// <summary>
/// A live id→name catalog folded from the parser satellite's forwarded resource dictionaries
/// (<see cref="ResourceDictionaryForwarded"/>). Missing kinds resolve empty.
/// </summary>
internal sealed class LiveResourceCatalog : IResourceCatalog
{
    private static readonly IReadOnlyDictionary<uint, string> Empty = new Dictionary<uint, string>();
    private readonly IReadOnlyDictionary<ResourceKind, IReadOnlyDictionary<uint, string>> _byKind;

    public LiveResourceCatalog(IReadOnlyDictionary<ResourceKind, IReadOnlyDictionary<uint, string>> byKind) => _byKind = byKind;

    public string? Name(ResourceKind kind, uint id) =>
        _byKind.TryGetValue(kind, out var d) && d.TryGetValue(id, out var name) ? name : null;

    public IReadOnlyDictionary<uint, string> All(ResourceKind kind) =>
        _byKind.TryGetValue(kind, out var d) ? d : Empty;
}

/// <summary>
/// An immutable snapshot published by <see cref="GameSnapshotAggregator"/>. Carries the folded actor
/// roster, resolved player, zone, party, forwarded resource catalog, and the game process id (when the
/// parser satellite forwards it). <c>Target</c>/<c>Focus</c>/<c>Hover</c> stay null — the bridge forwards
/// no targeting side-channels (a tracked open item).
/// </summary>
internal sealed class ImmutableGameSnapshot : IGameSnapshot
{
    private readonly IReadOnlyList<Actor> _actors;
    private readonly GameClient _client;

    public ImmutableGameSnapshot(IReadOnlyList<Actor> actors, Actor? player, ZoneRef zone, PartySnapshot party)
        : this(actors, player, zone, party, EmptyResourceCatalog.Instance, 0)
    {
    }

    public ImmutableGameSnapshot(IReadOnlyList<Actor> actors, Actor? player, ZoneRef zone, PartySnapshot party,
        IResourceCatalog resources, int pid)
    {
        _actors = actors;
        Player = player;
        Zone = zone;
        Party = party;
        Resources = resources;
        _client = new GameClient("0.0", GameRegion.Unknown, GameLanguage.Unknown, IsRunning: true, IsForeground: false)
        {
            ProcessId = pid == 0 ? null : pid,
        };
    }

    public Actor? Player { get; }
    public IReadOnlyList<Actor> Actors => _actors;
    public Actor? Target => null;
    public Actor? Focus => null;
    public Actor? Hover => null;
    public PartySnapshot Party { get; }
    public ZoneRef Zone { get; }
    public IResourceCatalog Resources { get; }
    public GameClient Client => _client;

    public Actor? Find(uint actorId)
    {
        foreach (var a in _actors)
        {
            if (a.Id == actorId) return a;
        }
        return null;
    }
}
