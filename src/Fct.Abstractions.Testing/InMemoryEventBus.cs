using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// In-memory <see cref="IGameEventStream"/> for flow tests. Generalizes the shipped
    /// RingBufferDataSubscription model: each subscription owns a bounded ring drained in order,
    /// each handler call is fault-guarded (a throw never kills the stream or starves peers), and a
    /// full ring drops the oldest event and bumps a per-subscription counter. Dispatch is synchronous
    /// on <see cref="Emit"/> so tests are deterministic (mirrors <c>FakeDataSubscription.RaiseXxx</c>).
    /// </summary>
    public sealed class InMemoryEventBus : IGameEventStream
    {
        private readonly object _gate = new object();
        private readonly List<Subscription> _subs = new List<Subscription>();
        private readonly int _defaultCapacity;

        public InMemoryEventBus(int defaultCapacity = 1024)
        {
            if (defaultCapacity < 1) throw new ArgumentOutOfRangeException(nameof(defaultCapacity));
            _defaultCapacity = defaultCapacity;
        }

        /// <summary>Total events dropped across all live subscriptions (drop-oldest backpressure).</summary>
        public long DroppedCount
        {
            get
            {
                lock (_gate)
                {
                    long total = 0;
                    foreach (var s in _subs) total += s.Dropped;
                    return total;
                }
            }
        }

        public IDisposable Subscribe(GameEventFilter filter, Action<GameEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var sub = new Subscription(this, filter ?? GameEventFilter.All, handler, _defaultCapacity);
            lock (_gate) _subs.Add(sub);
            return sub;
        }

        public IAsyncEnumerable<GameEvent> Subscribe(GameEventFilter filter, CancellationToken ct)
            => new AsyncSubscription(this, filter ?? GameEventFilter.All, _defaultCapacity, ct);

        /// <summary>Test-only producer seam: publish an event to all matching subscriptions.</summary>
        public void Emit(GameEvent evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            Subscription[] snapshot;
            lock (_gate) snapshot = _subs.ToArray();
            foreach (var s in snapshot)
            {
                if (Matches(s.Filter, evt)) s.Post(evt);
            }
        }

        /// <summary>Filter semantics: raw log lines gate on <see cref="GameEventFilter.IncludeRawLogLines"/>;
        /// typed events gate on <see cref="GameEventFilter.Types"/> (null/empty = all typed).</summary>
        internal static bool Matches(GameEventFilter filter, GameEvent evt)
        {
            if (evt is RawLogLine) return filter.IncludeRawLogLines;
            if (filter.Types == null || filter.Types.Count == 0) return true;
            var actual = evt.GetType();
            foreach (var t in filter.Types)
            {
                if (t.IsAssignableFrom(actual)) return true;
            }
            return false;
        }

        private void Remove(Subscription sub)
        {
            lock (_gate) _subs.Remove(sub);
        }

        /// <summary>A single low-latency callback subscription backed by a bounded ring.</summary>
        private sealed class Subscription : IDisposable
        {
            private readonly InMemoryEventBus _bus;
            private readonly Action<GameEvent> _handler;
            private readonly int _capacity;
            private readonly Queue<GameEvent> _ring = new Queue<GameEvent>();
            private readonly object _lock = new object();
            private bool _disposed;

            public GameEventFilter Filter { get; }

            /// <summary>When false, events buffer in the ring until <see cref="Drain"/> is called
            /// (lets a test model a slow consumer and exercise drop-oldest deterministically).</summary>
            public bool AutoDrain { get; set; } = true;

            public long Dropped { get; private set; }

            public Subscription(InMemoryEventBus bus, GameEventFilter filter, Action<GameEvent> handler, int capacity)
            {
                _bus = bus;
                Filter = filter;
                _handler = handler;
                _capacity = capacity;
            }

            public void Post(GameEvent evt)
            {
                bool drain;
                lock (_lock)
                {
                    if (_disposed) return;
                    _ring.Enqueue(evt);
                    while (_ring.Count > _capacity)
                    {
                        _ring.Dequeue();
                        Dropped++;
                    }
                    drain = AutoDrain;
                }
                if (drain) Drain();
            }

            /// <summary>Drain the ring in order, isolating each handler call.</summary>
            public int Drain()
            {
                int delivered = 0;
                while (true)
                {
                    GameEvent evt;
                    lock (_lock)
                    {
                        if (_disposed || _ring.Count == 0) break;
                        evt = _ring.Dequeue();
                    }
                    try { _handler(evt); }
                    catch { /* isolated: a throwing handler never kills the stream or starves peers */ }
                    delivered++;
                }
                return delivered;
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                    _ring.Clear();
                }
                _bus.Remove(this);
            }
        }

        /// <summary>The <c>await foreach</c> projection over the same delivery model.</summary>
        private sealed class AsyncSubscription : IAsyncEnumerable<GameEvent>
        {
            private readonly InMemoryEventBus _bus;
            private readonly GameEventFilter _filter;
            private readonly int _capacity;
            private readonly CancellationToken _outer;

            public AsyncSubscription(InMemoryEventBus bus, GameEventFilter filter, int capacity, CancellationToken outer)
            {
                _bus = bus;
                _filter = filter;
                _capacity = capacity;
                _outer = outer;
            }

            public async IAsyncEnumerator<GameEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_outer, cancellationToken);
                var token = linked.Token;
                var queue = new ConcurrentQueue<GameEvent>();
                var signal = new SemaphoreSlim(0);

                var sub = _bus.Subscribe(_filter, e =>
                {
                    queue.Enqueue(e);
                    while (queue.Count > _capacity && queue.TryDequeue(out _)) { /* drop-oldest */ }
                    signal.Release();
                });
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { await signal.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { yield break; }
                        if (queue.TryDequeue(out var evt)) yield return evt;
                    }
                }
                finally
                {
                    sub.Dispose();
                    signal.Dispose();
                }
            }
        }
    }
}
