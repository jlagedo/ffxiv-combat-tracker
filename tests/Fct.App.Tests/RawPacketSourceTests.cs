using System;
using System.Collections.Generic;
using Fct.Abstractions;
using Fct.App.Hosting;
using Xunit;

namespace Fct.App.Tests
{
    // The host read hatch: RawPacketSource opts into the bus RawPacketReceived firehose and fans each
    // packet to every IRawPacketSource.Subscribe consumer — OverlayPlugin's RegisterNetworkParser path.
    public class RawPacketSourceTests
    {
        private static readonly DateTimeOffset Ts = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        private static RawPacketReceived Packet(byte[] bytes, PacketDirection dir = PacketDirection.Received) =>
            new RawPacketReceived(1, Ts, "tcp-1", 10L, dir, bytes);

        [Fact]
        public void Fans_out_bus_packets_to_every_subscriber()
        {
            using var bus = new GameEventBus();
            using var source = new RawPacketSource(bus);
            var a = new List<RawPacket>();
            var b = new List<RawPacket>();
            using var _1 = source.Subscribe(a.Add);
            using var _2 = source.Subscribe(b.Add);

            bus.Emit(Packet(new byte[] { 1, 2, 3 }, PacketDirection.Sent));

            Assert.True(TestWait.Until(() => a.Count == 1 && b.Count == 1));
            Assert.Equal(new byte[] { 1, 2, 3 }, a[0].Bytes);
            Assert.Equal(PacketDirection.Sent, b[0].Direction);
            Assert.Equal("tcp-1", a[0].Connection);
            Assert.Equal(10L, a[0].Epoch);
        }

        [Fact]
        public void Ignores_non_packet_events()
        {
            using var bus = new GameEventBus();
            using var source = new RawPacketSource(bus);
            var seen = new List<RawPacket>();
            using var _ = source.Subscribe(seen.Add);

            bus.Emit(new RawLogLine(1, Ts, LogMessageType.ChatLog, "x", "x"));
            bus.Emit(Packet(new byte[] { 9 }));

            Assert.True(TestWait.Until(() => seen.Count == 1));
            Assert.Single(seen);   // only the packet, never the log line
        }

        [Fact]
        public void Unsubscribe_stops_delivery()
        {
            using var bus = new GameEventBus();
            using var source = new RawPacketSource(bus);
            var dropped = new List<RawPacket>();
            var kept = new List<RawPacket>();
            var sub = source.Subscribe(dropped.Add);
            using var _ = source.Subscribe(kept.Add);

            sub.Dispose();
            bus.Emit(Packet(new byte[] { 1 }));

            // The still-subscribed handler is the ordering barrier: once it has seen the packet, the
            // disposed handler would have too had it still been registered.
            Assert.True(TestWait.Until(() => kept.Count == 1));
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedCount_is_zero_on_the_happy_path()
        {
            using var bus = new GameEventBus();
            using var source = new RawPacketSource(bus);
            using var _ = source.Subscribe(_ => { });
            bus.Emit(Packet(new byte[] { 1 }));
            Assert.True(TestWait.Until(() => source.DroppedCount == 0 && bus.DroppedCount == 0));
        }

        [Fact]
        public void Noop_source_delivers_nothing()
        {
            var seen = 0;
            using var _ = RawPacketSource.Noop.Subscribe(_ => seen++);
            Assert.Equal(0, seen);
            Assert.Equal(0, RawPacketSource.Noop.DroppedCount);
        }
    }
}
