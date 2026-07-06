using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Hosting;

/// <summary>
/// Production <see cref="IAudioOutput"/> — the real form of the reference <c>RecordingAudioOutput</c>.
/// Producers (<see cref="Speak"/>/<see cref="Play"/>) fan out to registered sinks in priority order;
/// a <c>terminal</c> sink stops the chain (G3 route-instead-of). Sinks are invoked fire-and-return so
/// a slow out-of-process sink (Discord) never blocks the producer; a faulting sink is logged, not
/// propagated.
/// </summary>
internal sealed class AudioService : IAudioOutput
{
    private readonly object _gate = new();
    private readonly List<Registration> _sinks = new();
    private int _seq;
    private readonly ILogger<AudioService> _log;

    public AudioService(ILogger<AudioService> log) => _log = log;

    public void Speak(string text, AudioOptions? options = null)
    {
        var opts = options ?? AudioOptions.Default;
        foreach (var reg in SinksByPriority())
        {
            Fire(reg.Sink.SpeakAsync(text, opts, CancellationToken.None), reg.Sink);
            if (reg.Terminal) break;
        }
    }

    public void Play(string filePath, int volume = 100)
    {
        foreach (var reg in SinksByPriority())
        {
            Fire(reg.Sink.PlayAsync(filePath, volume, CancellationToken.None), reg.Sink);
            if (reg.Terminal) break;
        }
    }

    public IDisposable RegisterSink(IAudioSink sink, int priority = 0, bool terminal = false)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        Registration reg;
        lock (_gate) { reg = new Registration(sink, priority, terminal, _seq++); _sinks.Add(reg); }
        return new ActionDisposable(() => { lock (_gate) _sinks.Remove(reg); });
    }

    // Higher priority first; among equal priority, MOST-RECENT registration first — so a later terminal
    // sink (e.g. TTSYukkuri hijacking the slot after Discord-Triggers) owns the chain, matching real ACT's
    // last-hijacker-wins delegate-swap semantics. OrderByDescending is stable, so the Seq tie-break is
    // what inverts the otherwise first-registered-wins order.
    private Registration[] SinksByPriority()
    {
        lock (_gate) return _sinks.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Seq).ToArray();
    }

    /// <summary>Observe the sink's ValueTask without blocking the producer; log a fault, never throw.</summary>
    private void Fire(ValueTask task, IAudioSink sink)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = Awaited(task, sink);

        async Task Awaited(ValueTask t, IAudioSink s)
        {
            try { await t.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Audio sink {Sink} threw", s.GetType().Name); }
        }
    }

    private sealed class Registration
    {
        public IAudioSink Sink { get; }
        public int Priority { get; }
        public bool Terminal { get; }
        public int Seq { get; }
        public Registration(IAudioSink sink, int priority, bool terminal, int seq) { Sink = sink; Priority = priority; Terminal = terminal; Seq = seq; }
    }
}
