using System;
using System.Collections.Generic;
using System.Threading;

namespace Fct.Abstractions
{
    /// <summary>
    /// Live game data, split CQRS-style: a typed event <see cref="Events"/> stream (push) and an
    /// immutable <see cref="Snapshot"/> (pull). The split mirrors how legacy consumers actually
    /// work — OverlayPlugin/Triggernometry stream events; Hojoring polls state.
    /// </summary>
    public interface IGameSession
    {
        IGameEventStream Events { get; }

        /// <summary>A free-threaded, immutable point-in-time view. Safe to call from any thread.</summary>
        IGameSnapshot Snapshot();
    }

    /// <summary>
    /// Typed pub/sub. Each subscription is fed by a bounded per-subscriber queue drained in order;
    /// a handler that throws is isolated (never kills the stream or starves peers); a slow consumer
    /// gets drop-oldest backpressure (never stalls the producer). Generalizes the proven
    /// RingBufferDataSubscription dispatch model.
    /// </summary>
    public interface IGameEventStream
    {
        /// <summary>Ergonomic consumption via <c>await foreach</c>.</summary>
        IAsyncEnumerable<GameEvent> Subscribe(GameEventFilter filter, CancellationToken ct);

        /// <summary>Low-latency in-order callback. Dispose to unsubscribe.</summary>
        IDisposable Subscribe(GameEventFilter filter, Action<GameEvent> handler);
    }

    /// <summary>
    /// Selects which events a subscription receives. Empty <see cref="Types"/> = all typed events;
    /// <see cref="IncludeRawLogLines"/> toggles the <see cref="RawLogLine"/> firehose.
    /// </summary>
    public sealed record GameEventFilter(IReadOnlyCollection<Type>? Types = null, bool IncludeRawLogLines = true)
    {
        /// <summary>Everything: all typed events plus raw log lines.</summary>
        public static readonly GameEventFilter All = new GameEventFilter();
    }

    /// <summary>An immutable snapshot of game state. All members are free-threaded reads.</summary>
    public interface IGameSnapshot
    {
        Actor? Player { get; }
        IReadOnlyList<Actor> Actors { get; }
        Actor? Target { get; }
        Actor? Focus { get; }
        Actor? Hover { get; }
        PartySnapshot Party { get; }
        ZoneRef Zone { get; }
        IResourceCatalog Resources { get; }
        GameClient Client { get; }

        Actor? Find(uint actorId);
    }

    /// <summary>The party/alliance roster at snapshot time.</summary>
    public sealed record PartySnapshot(IReadOnlyList<Actor> Members, PartyMembership Composition);

    /// <summary>A zone identity.</summary>
    public readonly record struct ZoneRef(uint Id, string Name);

    /// <summary>FFXIV client language.</summary>
    public enum GameLanguage { Unknown = 0, English = 1, French = 2, German = 3, Japanese = 4, Chinese = 5, Korean = 6, TraditionalChinese = 7 }

    /// <summary>FFXIV data-center region.</summary>
    public enum GameRegion : byte { Unknown = 0, Global = 1, Chinese = 2, Korean = 3, TraditionalChinese = 4 }

    /// <summary>Client-level state (version/region/language + process liveness/foreground).</summary>
    public sealed record GameClient(string Version, GameRegion Region, GameLanguage Language, bool IsRunning, bool IsForeground);

    /// <summary>Resource categories for id→name lookups.</summary>
    public enum ResourceKind { Action, Status, Zone, World, Item }

    /// <summary>
    /// Typed, complete, all-locale name tables (skill/status/zone/world/item) — the data Hojoring
    /// patches today with bundled CSVs because the SDK's buff dictionary is never populated.
    /// </summary>
    public interface IResourceCatalog
    {
        string? Name(ResourceKind kind, uint id);
        IReadOnlyDictionary<uint, string> All(ResourceKind kind);
    }
}
