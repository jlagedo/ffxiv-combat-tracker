using System;
using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.Host.Hosting;

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
        // Version "" (never a "0.0" placeholder, plan §3); IsChatLogAvailable true — both mirror
        // ConsumerDataSurface's own pre-Apply() defaults (the real plugin's headless values, P0.3). An
        // unfolded snapshot means "unknown", not a fabricated stub; this surfaces directly now that
        // P4.2's repository priming reads this default for a subscriber that joins before any producer
        // env tap has ever folded a real SessionStateChanged.
        public GameClient Client { get; } =
            new GameClient("", GameRegion.Unknown, GameLanguage.Unknown, false, false) { IsChatLogAvailable = true };

        public Actor? Find(uint actorId) => null;
    }
}
