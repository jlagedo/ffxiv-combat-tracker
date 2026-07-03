using System;
using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>
/// The capability-gated raw-packet read hatch. A process-wide singleton that subscribes to the
/// <see cref="RawPacketReceived"/> firehose on the bus (opt-in via
/// <see cref="GameEventFilter.IncludeRawPackets"/>) and fans each packet out to every
/// <see cref="Subscribe"/> consumer as a <see cref="RawPacket"/> — OverlayPlugin's
/// <c>RegisterNetworkParser</c> read path. Handed to a plugin only when its manifest declares the
/// <c>raw</c> capability; otherwise the plugin gets <see cref="Noop"/>.
/// </summary>
internal sealed class RawPacketSource : IRawPacketSource, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Action<RawPacket>> _handlers = new();
    private readonly IDisposable _busSubscription;

    public RawPacketSource(IGameEventStream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        // Only this service opts into the higher-bandwidth packet firehose; every other subscriber
        // (snapshot aggregator, shim DataSubscription) uses GameEventFilter.All, which excludes it.
        _busSubscription = stream.Subscribe(new GameEventFilter(IncludeRawPackets: true), OnBusEvent);
    }

    // Runs on this subscription's bus pump thread (serialized). A slow/throwing consumer here backs up
    // the bus subscription's bounded channel → drop-oldest, counted by the handle below.
    private void OnBusEvent(GameEvent evt)
    {
        if (evt is not RawPacketReceived p) return;
        var packet = new RawPacket(p.Connection, p.Epoch, p.Bytes, p.Direction);

        Action<RawPacket>[] snapshot;
        lock (_gate) snapshot = _handlers.ToArray();
        foreach (var h in snapshot)
        {
            try { h(packet); }
            catch { /* isolated: a throwing consumer never starves peers */ }
        }
    }

    public IDisposable Subscribe(Action<RawPacket> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_gate) _handlers.Add(handler);
        return new Unsubscriber(this, handler);
    }

    public long DroppedCount => (_busSubscription as ISubscriptionHandle)?.Dropped ?? 0;

    public void Dispose() => _busSubscription.Dispose();

    private sealed class Unsubscriber : IDisposable
    {
        private readonly RawPacketSource _owner;
        private Action<RawPacket>? _handler;

        public Unsubscriber(RawPacketSource owner, Action<RawPacket> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var h = _handler;
            if (h is null) return;
            _handler = null;
            lock (_owner._gate) _owner._handlers.Remove(h);
        }
    }

    /// <summary>Inert source for plugins without the <c>raw</c> capability.</summary>
    public static readonly IRawPacketSource Noop = new NoopSource();

    private sealed class NoopSource : IRawPacketSource
    {
        public IDisposable Subscribe(Action<RawPacket> handler) => NoopDisposable.Instance;
        public long DroppedCount => 0;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
