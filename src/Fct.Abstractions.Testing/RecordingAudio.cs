using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// Recording <see cref="IAudioOutput"/>. Captures producer calls and fans <see cref="Speak"/>/
    /// <see cref="Play"/> out to registered sinks in priority order (higher first). This is the
    /// current additive fan-out contract — terminal/route-instead-of routing (G3) is out of scope.
    /// </summary>
    public sealed class RecordingAudioOutput : IAudioOutput
    {
        private readonly object _gate = new object();
        private readonly List<Registration> _sinks = new List<Registration>();

        public List<(string Text, AudioOptions Options)> Speaks { get; } = new List<(string, AudioOptions)>();
        public List<(string FilePath, int Volume)> Plays { get; } = new List<(string, int)>();

        public void Speak(string text, AudioOptions? options = null)
        {
            var opts = options ?? AudioOptions.Default;
            Speaks.Add((text, opts));
            foreach (var sink in SinksByPriority()) _ = sink.SpeakAsync(text, opts, CancellationToken.None);
        }

        public void Play(string filePath, int volume = 100)
        {
            Plays.Add((filePath, volume));
            foreach (var sink in SinksByPriority()) _ = sink.PlayAsync(filePath, volume, CancellationToken.None);
        }

        public IDisposable RegisterSink(IAudioSink sink, int priority = 0)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            var reg = new Registration(sink, priority);
            lock (_gate) _sinks.Add(reg);
            return new ActionDisposable(() => { lock (_gate) _sinks.Remove(reg); });
        }

        private IAudioSink[] SinksByPriority()
        {
            lock (_gate)
            {
                return _sinks.OrderByDescending(r => r.Priority).Select(r => r.Sink).ToArray();
            }
        }

        private sealed class Registration
        {
            public IAudioSink Sink { get; }
            public int Priority { get; }
            public Registration(IAudioSink sink, int priority) { Sink = sink; Priority = priority; }
        }
    }

    /// <summary>A recording <see cref="IAudioSink"/> — captures calls synchronously so a test can
    /// assert immediately after the producer returns.</summary>
    public sealed class RecordingAudioSink : IAudioSink
    {
        public string Name { get; }

        public RecordingAudioSink(string name = "sink") => Name = name;

        public List<(string Text, AudioOptions Options)> Speaks { get; } = new List<(string, AudioOptions)>();
        public List<(string FilePath, int Volume)> Plays { get; } = new List<(string, int)>();

        public ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct)
        {
            Speaks.Add((text, options));
            return default;
        }

        public ValueTask PlayAsync(string filePath, int volume, CancellationToken ct)
        {
            Plays.Add((filePath, volume));
            return default;
        }
    }
}
