using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>A subscription handle that also surfaces its own drop-oldest counter (a single consumer's
/// backpressure loss, distinct from the bus-wide aggregate).</summary>
internal interface ISubscriptionHandle : IDisposable
{
    long Dropped { get; }
}

/// <summary>
/// Production <see cref="IGameEventStream"/> — the real form of the reference
/// <c>InMemoryEventBus</c>, generalizing the shipped RingBufferDataSubscription dispatch model.
/// Each subscription owns a bounded channel <b>drained in order by its own background pump</b>, so a
/// slow handler never stalls the producer or peers; a full channel drops the oldest event and bumps a
/// per-subscription counter; each handler call is fault-guarded (a throw never kills the stream).
/// </summary>
internal sealed class GameEventBus : IGameEventStream, IGameEventSink, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subs = new();
    private readonly int _capacity;
    private long _sequence;
    private bool _disposed;

    public GameEventBus(int capacity = 1024)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
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

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Emit(GameEvent evt)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));
        Subscription[] snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = _subs.ToArray();
        }
        foreach (var s in snapshot)
        {
            if (Matches(s.Filter, evt)) s.Post(evt);
        }
    }

    public IDisposable Subscribe(GameEventFilter filter, Action<GameEvent> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var sub = new CallbackSubscription(this, filter ?? GameEventFilter.All, handler, _capacity);
        Add(sub);
        return sub;
    }

    public IAsyncEnumerable<GameEvent> Subscribe(GameEventFilter filter, CancellationToken ct)
        => Iterate(filter ?? GameEventFilter.All, ct);

    private async IAsyncEnumerable<GameEvent> Iterate(GameEventFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var sub = new ChannelSubscription(this, filter, _capacity);
        Add(sub);
        try
        {
            await foreach (var e in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return e;
        }
        finally
        {
            sub.Dispose();
        }
    }

    /// <summary>Filter semantics: raw log lines gate on <see cref="GameEventFilter.IncludeRawLogLines"/>;
    /// raw packets gate on <see cref="GameEventFilter.IncludeRawPackets"/> (opt-in);
    /// typed events gate on <see cref="GameEventFilter.Types"/> (null/empty = all typed).</summary>
    internal static bool Matches(GameEventFilter filter, GameEvent evt)
    {
        if (evt is RawLogLine) return filter.IncludeRawLogLines;
        if (evt is RawPacketReceived) return filter.IncludeRawPackets;
        if (filter.Types is null || filter.Types.Count == 0) return true;
        var actual = evt.GetType();
        foreach (var t in filter.Types)
        {
            if (t.IsAssignableFrom(actual)) return true;
        }
        return false;
    }

    private void Add(Subscription sub)
    {
        lock (_gate)
        {
            if (_disposed) { sub.Dispose(); return; }
            _subs.Add(sub);
        }
    }

    private void Remove(Subscription sub)
    {
        lock (_gate) _subs.Remove(sub);
    }

    public void Dispose()
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = _subs.ToArray();
            _subs.Clear();
        }
        foreach (var s in snapshot) s.Dispose();
    }

    /// <summary>A live subscription: a bounded channel + drop-oldest counter, drained off the producer.</summary>
    private abstract class Subscription : ISubscriptionHandle
    {
        protected readonly GameEventBus Bus;
        protected readonly Channel<GameEvent> Channel;
        private long _dropped;

        protected Subscription(GameEventBus bus, GameEventFilter filter, int capacity)
        {
            Bus = bus;
            Filter = filter;
            Channel = System.Threading.Channels.Channel.CreateBounded<GameEvent>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                _ => Interlocked.Increment(ref _dropped));
        }

        public GameEventFilter Filter { get; }
        public long Dropped => Interlocked.Read(ref _dropped);

        public void Post(GameEvent evt) => Channel.Writer.TryWrite(evt);

        public virtual void Dispose()
        {
            Channel.Writer.TryComplete();
            Bus.Remove(this);
        }
    }

    /// <summary>Low-latency callback subscription: a background pump invokes the handler in order.</summary>
    private sealed class CallbackSubscription : Subscription
    {
        private readonly Action<GameEvent> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;

        public CallbackSubscription(GameEventBus bus, GameEventFilter filter, Action<GameEvent> handler, int capacity)
            : base(bus, filter, capacity)
        {
            _handler = handler;
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var e in Channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try { _handler(e); }
                    catch { /* isolated: a throwing handler never kills the stream or starves peers */ }
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
        }

        public override void Dispose()
        {
            _cts.Cancel();
            base.Dispose();
        }
    }

    /// <summary>The <c>await foreach</c> projection over the same bounded/drop-oldest channel.</summary>
    private sealed class ChannelSubscription : Subscription
    {
        public ChannelSubscription(GameEventBus bus, GameEventFilter filter, int capacity)
            : base(bus, filter, capacity) { }

        public ChannelReader<GameEvent> Reader => Channel.Reader;
    }
}
