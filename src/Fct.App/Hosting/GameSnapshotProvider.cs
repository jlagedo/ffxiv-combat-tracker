using System;
using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// Holds the current immutable <see cref="IGameSnapshot"/>. For slice A+B this is an empty
/// placeholder — real actor/party/zone state arrives when the bridge forwarder (piece C) and the
/// snapshot projection (piece D) land. The provider indirection is here now so those pieces only
/// swap the backing snapshot, never the plugin-facing seam.
/// </summary>
internal sealed class GameSnapshotProvider
{
    private volatile IGameSnapshot _current = EmptySnapshot.Instance;

    public IGameSnapshot Current => _current;

    /// <summary>Replace the published snapshot (called by the future data source).</summary>
    public void Publish(IGameSnapshot snapshot)
        => _current = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    /// <summary>An all-empty snapshot: no player, no actors, unknown zone. Free-threaded and shared.</summary>
    private sealed class EmptySnapshot : IGameSnapshot
    {
        public static readonly EmptySnapshot Instance = new();

        public Actor? Player => null;
        public IReadOnlyList<Actor> Actors { get; } = Array.Empty<Actor>();
        public Actor? Target => null;
        public Actor? Focus => null;
        public Actor? Hover => null;
        public PartySnapshot Party { get; } = new PartySnapshot(Array.Empty<Actor>(), PartyMembership.None);
        public ZoneRef Zone => default;
        public IResourceCatalog Resources { get; } = EmptyResourceCatalog.Instance;
        public GameClient Client { get; } = new GameClient("0.0", GameRegion.Unknown, GameLanguage.Unknown, false, false);

        public Actor? Find(uint actorId) => null;
    }

    /// <summary>An empty id→name catalog until <see cref="IResourceCatalog"/> is sourced (see PLUGIN-API open items).</summary>
    private sealed class EmptyResourceCatalog : IResourceCatalog
    {
        public static readonly EmptyResourceCatalog Instance = new();
        private static readonly IReadOnlyDictionary<uint, string> Empty = new Dictionary<uint, string>();

        public string? Name(ResourceKind kind, uint id) => null;
        public IReadOnlyDictionary<uint, string> All(ResourceKind kind) => Empty;
    }
}
