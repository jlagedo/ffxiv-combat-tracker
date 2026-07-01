using System;
using System.Collections.Generic;

namespace Fct.Abstractions.Testing
{
    /// <summary>Mutable builder-style <see cref="IGameSnapshot"/> over immutable <see cref="Actor"/> records.</summary>
    public sealed class FakeSnapshot : IGameSnapshot
    {
        public Actor? Player { get; set; }
        public IReadOnlyList<Actor> Actors { get; set; } = Array.Empty<Actor>();
        public Actor? Target { get; set; }
        public Actor? Focus { get; set; }
        public Actor? Hover { get; set; }
        public PartySnapshot Party { get; set; } = new PartySnapshot(Array.Empty<Actor>(), PartyMembership.None);
        public ZoneRef Zone { get; set; }
        public IResourceCatalog Resources { get; set; } = new FakeResourceCatalog();
        public GameClient Client { get; set; } = new GameClient("0.0", GameRegion.Global, GameLanguage.English, true, true);

        public Actor? Find(uint actorId)
        {
            foreach (var a in Actors)
            {
                if (a.Id == actorId) return a;
            }
            return null;
        }
    }

    /// <summary>In-memory <see cref="IGameSession"/> wiring an <see cref="InMemoryEventBus"/> + <see cref="FakeSnapshot"/>.</summary>
    public sealed class FakeGameSession : IGameSession
    {
        public InMemoryEventBus Bus { get; }
        public FakeSnapshot State { get; }

        public FakeGameSession(InMemoryEventBus? bus = null, FakeSnapshot? state = null)
        {
            Bus = bus ?? new InMemoryEventBus();
            State = state ?? new FakeSnapshot();
        }

        public IGameEventStream Events => Bus;
        public IGameSnapshot Snapshot() => State;
    }

    /// <summary>In-memory <see cref="IResourceCatalog"/> — seed id→name pairs with <see cref="Set"/>.</summary>
    public sealed class FakeResourceCatalog : IResourceCatalog
    {
        private readonly Dictionary<(ResourceKind Kind, uint Id), string> _names = new Dictionary<(ResourceKind, uint), string>();

        public void Set(ResourceKind kind, uint id, string name) => _names[(kind, id)] = name;

        public string? Name(ResourceKind kind, uint id) => _names.TryGetValue((kind, id), out var name) ? name : null;

        public IReadOnlyDictionary<uint, string> All(ResourceKind kind)
        {
            var result = new Dictionary<uint, string>();
            foreach (var kv in _names)
            {
                if (kv.Key.Kind == kind) result[kv.Key.Id] = kv.Value;
            }
            return result;
        }
    }

    /// <summary>Convenience factories for the 20-field <see cref="Actor"/> record.</summary>
    public static class FakeActors
    {
        public static Actor Player(
            uint id,
            string name,
            bool inCombat = true,
            int job = 0,
            IReadOnlyList<StatusEffect>? statuses = null,
            IReadOnlyList<EnmityEntry>? enmity = null)
            => new Actor(
                Id: id,
                OwnerId: 0,
                Kind: ActorKind.Player,
                Job: job,
                Level: 100,
                Name: name,
                Hp: 1, MaxHp: 1,
                Mp: 0, MaxMp: 0,
                Cast: null,
                Position: default,
                WorldId: 0, WorldName: string.Empty,
                BNpcNameId: 0, BNpcId: 0,
                TargetId: 0, TargetOfTargetId: 0,
                EffectiveDistance: 0,
                Party: PartyMembership.Party,
                InCombat: inCombat,
                Statuses: statuses ?? Array.Empty<StatusEffect>(),
                Enmity: enmity ?? Array.Empty<EnmityEntry>());
    }
}
