#nullable enable
using System;
using System.IO;
using Fct.Abstractions;

namespace Fct.Bridge
{
    // Opt-in recorder: subscribes to a game-event stream and writes each frame to a
    // <see cref="FrameSession"/> file (offset-from-start + wire), so a live or replayed session can be
    // captured once and replayed deterministically forever after — the fixture-generation half of the
    // ISOLATION-PLAN P2 harness. The host wires one to its bus behind a flag; the P2 fixture generator
    // wires one to the bridged replay stream. Writes are serialized; disposing flushes + unsubscribes.
    public sealed class FrameSessionRecorder : IDisposable
    {
        private readonly TextWriter _writer;
        private readonly bool _ownsWriter;
        private readonly object _gate = new object();
        private readonly IDisposable _subscription;
        private long _t0Ticks = -1;
        private long _count;

        public FrameSessionRecorder(IGameEventStream stream, TextWriter writer, GameEventFilter? filter = null, bool ownsWriter = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _ownsWriter = ownsWriter;
            lock (_gate) _writer.WriteLine(FrameSession.Header);
            _subscription = stream.Subscribe(filter ?? GameEventFilter.All, OnEvent);
        }

        /// <summary>Frames written so far (excludes non-forwardable events the codec drops).</summary>
        public long Count { get { lock (_gate) return _count; } }

        private void OnEvent(GameEvent e)
        {
            // Offset each frame from the first event's timestamp so replay reproduces the in-game cadence.
            // Ticks are 100 ns; /10 → microseconds. FormatLine clamps any out-of-order offset to 0.
            lock (_gate)
            {
                long ticks = e.Timestamp.UtcTicks;
                if (_t0Ticks < 0) _t0Ticks = ticks;
                var line = FrameSession.FormatLine(e, (ticks - _t0Ticks) / 10);
                if (line != null) { _writer.WriteLine(line); _count++; }
            }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            lock (_gate)
            {
                _writer.Flush();
                if (_ownsWriter) _writer.Dispose();
            }
        }
    }
}
