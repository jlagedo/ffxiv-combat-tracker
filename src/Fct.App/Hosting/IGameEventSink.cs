using System;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// The producer side of the event bus — the single in-process seam every game-data source feeds.
/// The net48→net10 bridge forwarder (piece C) decodes EVT frames in <c>SatelliteHost</c> and
/// calls <see cref="Emit"/>; the capability-gated <see cref="RawLogLineEmitter"/> is the other caller.
/// </summary>
internal interface IGameEventSink
{
    /// <summary>Publish an event to every matching subscription. Never blocks on a consumer.</summary>
    void Emit(GameEvent evt);

    /// <summary>The next monotonic per-session sequence ordinal, for a source building a <see cref="GameEvent"/>.</summary>
    long NextSequence();
}

/// <summary>A no-op sink for design-time / previewer construction paths that never run the bridge.</summary>
internal sealed class NullGameEventSink : IGameEventSink
{
    public static readonly NullGameEventSink Instance = new();
    public void Emit(GameEvent evt) { }
    public long NextSequence() => 0;
}
