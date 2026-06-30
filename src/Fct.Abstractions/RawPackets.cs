using System;

namespace Fct.Abstractions
{
    /// <summary>
    /// The single opt-in escape hatch exposing raw network packets to consumers that need them
    /// (OverlayPlugin's <c>NetworkProcessors</c> via the legacy <c>RegisterNetworkParser</c> shim).
    /// The typed event path (<see cref="IGameSession.Events"/>) stays opcode-free for everyone else.
    /// Capability-gated: a plugin must declare it in its manifest to obtain this from the host.
    /// </summary>
    public interface IRawPacketSource
    {
        /// <summary>Subscribe to the raw packet firehose. Dispose to unsubscribe.</summary>
        IDisposable Subscribe(Action<RawPacket> handler);

        /// <summary>Events dropped under drop-oldest backpressure when a subscriber falls behind.</summary>
        long DroppedCount { get; }
    }

    /// <summary>A raw on-wire packet: opaque bytes plus connection/timestamp/direction.</summary>
    public readonly record struct RawPacket(string Connection, long Epoch, byte[] Bytes, PacketDirection Direction);

    /// <summary>Packet direction.</summary>
    public enum PacketDirection : byte { Received = 0, Sent = 1 }
}
