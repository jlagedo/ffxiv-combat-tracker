using System;
using System.Threading;
using Fct.Abstractions;
using Fct.Bridge;

namespace Fct.Host
{
    /// <summary>
    /// The host→satellite fan-out for ONE satellite (ISOLATION-PLAN P4): subscribes to the host bus for
    /// the stream set the satellite declared (SUBSCRIBE), serializes each matching event to a
    /// <see cref="GameEventFrame"/>, and writes it down that satellite's channel. A bounded ring with
    /// drop-oldest + sent/dropped counters (the host-side mirror of the satellite's upstream
    /// <c>BridgeForwarder</c>) sits between the bus and the pipe, so a slow/stalled satellite drops its
    /// own frames alone and never blocks the bus or its peers. One instance per subscribed satellite.
    /// </summary>
    internal sealed class SatelliteEgress : IDisposable
    {
        private readonly Action<string> _send;   // write one wire line down the satellite's channel
        private readonly GameEvent[] _ring;
        private readonly int _mask;
        private readonly object _gate = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _writer;
        private readonly IDisposable _subscription;
        private int _head, _tail, _count;
        private long _sent, _dropped;
        private volatile bool _running = true;

        public SatelliteEgress(string satelliteId, IGameEventStream stream, GameEventFilter filter,
            Action<string> send, int capacity = 4096, System.Collections.Generic.IReadOnlyList<GameEvent>? prime = null)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _ring = new GameEvent[capacity];
            _mask = capacity - 1;
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Fct.SatelliteEgress." + satelliteId };
            _writer.Start();
            // Snapshot-priming frames ride the same ring as the live fan-out, so the caller (the bridge
            // pump thread) never writes to the pipe itself and a stalled satellite can never block it.
            // Enqueued before the subscription, so a late joiner converges before the first live event.
            if (prime is not null)
                foreach (var evt in prime) Enqueue(evt);
            // Subscribe last, so the writer thread is live before the first event can arrive.
            _subscription = stream.Subscribe(filter ?? GameEventFilter.All, Enqueue);
        }

        public long Sent => Interlocked.Read(ref _sent);
        public long Dropped => Interlocked.Read(ref _dropped);

        private void Enqueue(GameEvent evt)
        {
            lock (_gate)
            {
                if (_count == _ring.Length)
                {
                    _head = (_head + 1) & _mask;   // drop oldest — never block the bus dispatch thread
                    _count--;
                    Interlocked.Increment(ref _dropped);
                }
                _ring[_tail] = evt;
                _tail = (_tail + 1) & _mask;
                _count++;
            }
            _signal.Set();
        }

        private bool TryDequeue(out GameEvent? evt)
        {
            lock (_gate)
            {
                if (_count == 0) { evt = null; return false; }
                evt = _ring[_head];
                _ring[_head] = null!;
                _head = (_head + 1) & _mask;
                _count--;
                return true;
            }
        }

        private void WriteLoop()
        {
            while (_running)
            {
                _signal.WaitOne(100);
                Drain();
            }
            Drain();   // final drain on shutdown
        }

        private void Drain()
        {
            while (_running && TryDequeue(out var evt) && evt is not null)
            {
                try
                {
                    var wire = GameEventFrame.ToWire(evt);
                    if (wire != null) { _send(wire); Interlocked.Increment(ref _sent); }
                }
                catch { /* a bad frame or a dead pipe must not kill the writer or stall peers */ }
            }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _running = false;
            _signal.Set();
            try { _writer.Join(2000); } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }
}
