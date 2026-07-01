using System.Collections.Generic;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Raw packet read path (B4-read) + custom-line write-back round-trip (B4).</summary>
    public sealed class RawPacketFlowTests
    {
        // B4 — OverlayPlugin turns a raw packet into a custom 256+ log line and re-injects it onto
        // the live bus (LineBaseCustom.cs:80-86 → FFXIVRepository.cs:493-515), which a native
        // RawLogLine consumer then sees. The gated IRawLogLineEmitter (G4) is the write-back hatch.
        [Fact]
        public void B4_RawPacket_BecomesSyntheticLine_OnTheBus()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var custom = new TrigDouble(@"^257\|");
            custom.Attach(shim);

            // OP decodes a packet and emits a custom 256+ line onto the live bus.
            host.RawLogLines.Emit((LogMessageType)257, "257|customdata");

            Assert.Single(custom.Fired);
        }

        // B4-read — the inbound half: a raw packet forwarded from the satellite reaches a shimmed
        // OverlayPlugin's RegisterNetworkParser handler with the exact (connection, epoch, bytes) triple
        // (FFXIVRepository.cs:525-531 → sub.NetworkReceived += handler → LineBaseCustom.MessageReceived).
        [Fact]
        public void B4Read_RawPacket_ReachesRegisterNetworkParser()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var received = new List<(string conn, long epoch, byte[] bytes)>();
            shim.RegisterNetworkParser((conn, epoch, bytes) => received.Add((conn, epoch, bytes)));

            var payload = new byte[] { 0x01, 0x02, 0xFF, 0x00 };
            host.RawPacketsFake!.Push(new RawPacket("tcp-1", 1234567890L, payload, PacketDirection.Received));

            var got = Assert.Single(received);
            Assert.Equal("tcp-1", got.conn);
            Assert.Equal(1234567890L, got.epoch);
            Assert.Equal(payload, got.bytes);
        }

        // A sent packet must not reach the inbound RegisterNetworkParser handler (direction routing).
        [Fact]
        public void B4Read_SentPacket_DoesNotReachInboundParser()
        {
            var host = new FakePluginHost();
            using var shim = new ShimStub(host);
            var received = new List<byte[]>();
            shim.RegisterNetworkParser((conn, epoch, bytes) => received.Add(bytes));

            host.RawPacketsFake!.Push(new RawPacket("tcp-1", 1L, new byte[] { 0x09 }, PacketDirection.Sent));

            Assert.Empty(received);
        }

        // The modern read hatch: a native plugin subscribes IRawPacketSource.Subscribe and sees the packet.
        [Fact]
        public void ModernConsumer_Subscribe_SeesRawPacket()
        {
            var host = new FakePluginHost();
            var seen = new List<RawPacket>();
            using var sub = host.RawPackets.Subscribe(seen.Add);

            var packet = new RawPacket("tcp-2", 42L, new byte[] { 0xDE, 0xAD }, PacketDirection.Received);
            host.RawPacketsFake!.Push(packet);

            var got = Assert.Single(seen);
            Assert.Equal(packet, got);
        }
    }
}
