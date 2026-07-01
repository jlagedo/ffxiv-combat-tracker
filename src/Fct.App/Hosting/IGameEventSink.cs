using System;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// The producer side of the event bus — the single in-process seam every game-data source feeds.
/// Today the synthetic <see cref="DevGameEventSource"/> and the capability-gated
/// <see cref="RawLogLineEmitter"/> push through it; when the net48→net10 bridge forwarder lands
/// (piece C) its decoder becomes just another caller of <see cref="Emit"/> — no bus change.
/// </summary>
internal interface IGameEventSink
{
    /// <summary>Publish an event to every matching subscription. Never blocks on a consumer.</summary>
    void Emit(GameEvent evt);

    /// <summary>The next monotonic per-session sequence ordinal, for a source building a <see cref="GameEvent"/>.</summary>
    long NextSequence();
}
