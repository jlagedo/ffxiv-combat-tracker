using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// The capability-gated write-back hatch (G4): builds a synthetic <see cref="RawLogLine"/> (monotonic
/// sequence + clock timestamp) and fans it onto the bus — OverlayPlugin's packet→custom-line
/// round-trip. Handed to a plugin only when its manifest declares the <c>raw</c> capability; otherwise
/// the plugin gets <see cref="Noop"/>.
/// </summary>
internal sealed class RawLogLineEmitter : IRawLogLineEmitter
{
    private readonly IGameEventSink _sink;
    private readonly IClock _clock;

    public RawLogLineEmitter(IGameEventSink sink, IClock clock)
    {
        _sink = sink;
        _clock = clock;
    }

    public void Emit(LogMessageType type, string line)
    {
        line ??= string.Empty;
        _sink.Emit(new RawLogLine(_sink.NextSequence(), _clock.LocalNow, type, line, line));
    }

    /// <summary>No-op emitter for plugins without the <c>raw</c> capability.</summary>
    public static readonly IRawLogLineEmitter Noop = new NoopEmitter();

    private sealed class NoopEmitter : IRawLogLineEmitter
    {
        public void Emit(LogMessageType type, string line) { }
    }
}
