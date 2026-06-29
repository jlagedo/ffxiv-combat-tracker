using System;

namespace Fct.Parser.Legacy
{
    // The opt-in raw-packet escape hatch (docs/DATA-FLOW.md §4.4 / §5). Lets a caller push
    // NetworkReceived events into the dispatcher without going through the real plugin's
    // capture — the seam the integration tests use to inject synthetic packets, and the
    // future bridge feed. Injected packets share the dispatcher's ring and in-order fan-out.
    public interface IRawPacketSource
    {
        // Enqueue a raw packet as if the real subscription had raised NetworkReceived.
        void InjectNetworkReceived(string connection, long epoch, byte[] message);

        // Total events dropped because the ring was full (drop-oldest backpressure). A slow
        // overlay subscriber can never block packet capture; it only loses the oldest events.
        long DroppedCount { get; }
    }
}
