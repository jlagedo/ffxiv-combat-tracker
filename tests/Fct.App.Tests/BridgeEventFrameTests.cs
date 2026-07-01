using System;
using System.Collections.Generic;
using Fct.Abstractions;
using Fct.App.Hosting;
using Fct.Bridge;
using Xunit;

namespace Fct.App.Tests
{
    // The piece-C wire codec: every forwardable GameEvent survives ToWire→TryParse, non-EVT lines are
    // rejected, and the decoded event reaches the real GameEventBus exactly as SatelliteHost drives it.
    public class BridgeEventFrameTests
    {
        private static readonly DateTimeOffset Ts =
            new DateTimeOffset(2026, 7, 1, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567);

        private static GameEvent RoundTrip(GameEvent src)
        {
            var wire = GameEventFrame.ToWire(src);
            Assert.NotNull(wire);
            Assert.StartsWith("EVT ", wire);
            Assert.DoesNotContain('\n', wire!);
            Assert.DoesNotContain('\r', wire);
            Assert.True(GameEventFrame.TryParse(wire, out var back));
            Assert.NotNull(back);
            // Sequence is not on the wire — the host re-stamps it; decode yields 0.
            Assert.Equal(0, back!.Sequence);
            Assert.Equal(src.Timestamp, back.Timestamp);
            return back;
        }

        [Fact]
        public void RawLogLine_roundtrips_with_delimiters_escaped()
        {
            var src = new RawLogLine(9, Ts, LogMessageType.ChatLog,
                "00|line with\ttab and\nnewline and \\ backslash", "orig\ttext");
            var e = Assert.IsType<RawLogLine>(RoundTrip(src));
            Assert.Equal(LogMessageType.ChatLog, e.Type);
            Assert.Equal("00|line with\ttab and\nnewline and \\ backslash", e.Line);
            Assert.Equal("orig\ttext", e.OriginalLine);
        }

        [Fact]
        public void ZoneChanged_roundtrips()
        {
            var e = Assert.IsType<ZoneChanged>(RoundTrip(new ZoneChanged(1, Ts, 1044, "Limsa Lominsa")));
            Assert.Equal(1044u, e.ZoneId);
            Assert.Equal("Limsa Lominsa", e.ZoneName);
        }

        [Fact]
        public void PartyChanged_roundtrips_members_and_empty()
        {
            var e = Assert.IsType<PartyChanged>(RoundTrip(new PartyChanged(1, Ts, new uint[] { 10, 20, 30 })));
            Assert.Equal(new uint[] { 10, 20, 30 }, e.Members);

            var empty = Assert.IsType<PartyChanged>(RoundTrip(new PartyChanged(1, Ts, Array.Empty<uint>())));
            Assert.Empty(empty.Members);
        }

        [Fact]
        public void PrimaryPlayerChanged_roundtrips()
        {
            var e = Assert.IsType<PrimaryPlayerChanged>(RoundTrip(new PrimaryPlayerChanged(1, Ts, 0x1234, "Y'shtola Rhul")));
            Assert.Equal(0x1234u, e.ActorId);
            Assert.Equal("Y'shtola Rhul", e.Name);
        }

        [Fact]
        public void CombatantRemoved_roundtrips()
        {
            var e = Assert.IsType<CombatantRemoved>(RoundTrip(new CombatantRemoved(1, Ts, 0xABCDEF)));
            Assert.Equal(0xABCDEFu, e.ActorId);
        }

        [Fact]
        public void CombatantAdded_roundtrips_scalar_and_nullable_fields()
        {
            var actor = new Actor(
                Id: 100, OwnerId: 5, Kind: ActorKind.Player, Job: 24, Level: 90, Name: "Tank\tMcTankface",
                Hp: 90000, MaxHp: 100000, Mp: 8000, MaxMp: 10000,
                Cast: null, Position: default,
                WorldId: 40, WorldName: "Odin",
                BNpcNameId: 0, BNpcId: 0, TargetId: 200, TargetOfTargetId: 0, EffectiveDistance: 3,
                Party: PartyMembership.Alliance, InCombat: true,
                Statuses: Array.Empty<StatusEffect>(), Enmity: Array.Empty<EnmityEntry>())
            {
                CurrentWorldId = 41, CurrentCp = 500, MaxCp = 600, CurrentGp = 700, MaxGp = 800, Order = 3,
            };
            var e = Assert.IsType<CombatantAdded>(RoundTrip(new CombatantAdded(1, Ts, actor)));
            var a = e.Combatant;
            Assert.Equal(100u, a.Id);
            Assert.Equal(5u, a.OwnerId);
            Assert.Equal(ActorKind.Player, a.Kind);
            Assert.Equal(24, a.Job);
            Assert.Equal(90, a.Level);
            Assert.Equal("Tank\tMcTankface", a.Name);
            Assert.Equal(90000u, a.Hp);
            Assert.Equal(100000u, a.MaxHp);
            Assert.Equal(8000u, a.Mp);
            Assert.Equal(10000u, a.MaxMp);
            Assert.Equal(40u, a.WorldId);
            Assert.Equal("Odin", a.WorldName);
            Assert.Equal(200u, a.TargetId);
            Assert.Equal(PartyMembership.Alliance, a.Party);
            Assert.True(a.InCombat);
            Assert.Equal(41u, a.CurrentWorldId);
            Assert.Equal(500u, a.CurrentCp);
            Assert.Equal(600u, a.MaxCp);
            Assert.Equal(700u, a.CurrentGp);
            Assert.Equal(800u, a.MaxGp);
            Assert.Equal(3, a.Order);
        }

        [Fact]
        public void CombatantAdded_roundtrips_null_optionals()
        {
            var actor = new Actor(
                Id: 1, OwnerId: 0, Kind: ActorKind.Npc, Job: 0, Level: 0, Name: "Striking Dummy",
                Hp: 1, MaxHp: 1, Mp: 0, MaxMp: 0,
                Cast: null, Position: default,
                WorldId: 0, WorldName: "",
                BNpcNameId: 0, BNpcId: 0, TargetId: 0, TargetOfTargetId: 0, EffectiveDistance: 0,
                Party: PartyMembership.None, InCombat: false,
                Statuses: Array.Empty<StatusEffect>(), Enmity: Array.Empty<EnmityEntry>());
            var e = Assert.IsType<CombatantAdded>(RoundTrip(new CombatantAdded(1, Ts, actor)));
            var a = e.Combatant;
            Assert.Null(a.CurrentWorldId);
            Assert.Null(a.CurrentCp);
            Assert.Null(a.MaxGp);
            Assert.Null(a.Order);
        }

        [Fact]
        public void ActionEffect_roundtrips_multiple_targets()
        {
            var src = new ActionEffect(1, Ts, new ActorRef(0, "Player One"), 0, "Attack",
                new List<EffectTarget>
                {
                    new(new ActorRef(0, "Striking Dummy"), 1234, EffectFlags.Critical),
                    new(new ActorRef(0, "Add\tTwo"), 56, EffectFlags.None),
                });
            var e = Assert.IsType<ActionEffect>(RoundTrip(src));
            Assert.Equal("Player One", e.Source.Name);
            Assert.Equal("Attack", e.ActionName);
            Assert.Equal(2, e.Targets.Count);
            Assert.Equal("Striking Dummy", e.Targets[0].Target.Name);
            Assert.Equal(1234, e.Targets[0].Amount);
            Assert.Equal(EffectFlags.Critical, e.Targets[0].Flags);
            Assert.Equal("Add\tTwo", e.Targets[1].Target.Name);
            Assert.Equal(56, e.Targets[1].Amount);
        }

        [Fact]
        public void RawPacketReceived_roundtrips_bytes_via_base64()
        {
            var bytes = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF, 0x0A, 0x09, 0x5C };  // incl. \n, \t, backslash
            var src = new RawPacketReceived(7, Ts, "tcp-conn\t1", 1_700_000_000_000L, PacketDirection.Received, bytes);
            var e = Assert.IsType<RawPacketReceived>(RoundTrip(src));
            Assert.Equal("tcp-conn\t1", e.Connection);
            Assert.Equal(1_700_000_000_000L, e.Epoch);
            Assert.Equal(PacketDirection.Received, e.Direction);
            Assert.Equal(bytes, e.Bytes);
        }

        [Fact]
        public void RawPacketReceived_roundtrips_empty_payload_and_sent_direction()
        {
            var e = Assert.IsType<RawPacketReceived>(
                RoundTrip(new RawPacketReceived(1, Ts, "c", 0L, PacketDirection.Sent, Array.Empty<byte>())));
            Assert.Equal(PacketDirection.Sent, e.Direction);
            Assert.Empty(e.Bytes);
        }

        [Fact]
        public void DecodedPacket_reaches_the_bus_only_when_opted_in()
        {
            using var bus = new GameEventBus();
            GameEvent? optedIn = null;
            GameEvent? defaultSub = null;
            using var _1 = bus.Subscribe(new GameEventFilter(IncludeRawPackets: true), e => optedIn = e);
            using var _2 = bus.Subscribe(GameEventFilter.All, e => defaultSub = e);

            var wire = GameEventFrame.ToWire(
                new RawPacketReceived(0, Ts, "tcp-1", 99L, PacketDirection.Received, new byte[] { 0xDE, 0xAD }))!;
            Assert.True(GameEventFrame.TryParse(wire, out var evt));
            bus.Emit(evt! with { Sequence = bus.NextSequence() });

            Assert.True(TestWait.Until(() => optedIn is RawPacketReceived));
            var p = Assert.IsType<RawPacketReceived>(optedIn);
            Assert.Equal(new byte[] { 0xDE, 0xAD }, p.Bytes);
            Assert.True(p.Sequence > 0);                 // re-stamped by the host sink
            Assert.Null(defaultSub);                     // GameEventFilter.All excludes the packet firehose
        }

        [Fact]
        public void ToWire_returns_null_for_firehose_only_events()
        {
            // Events with no structured SDK/ACT source are not forwarded as typed frames — they reach
            // consumers via the RawLogLine firehose. ToWire must decline them (never a bogus frame).
            Assert.Null(GameEventFrame.ToWire(new HpUpdated(1, Ts, 5, 100, 200)));
            Assert.Null(GameEventFrame.ToWire(new DeathOccurred(1, Ts, new ActorRef(1, "x"), null)));
            Assert.Null(GameEventFrame.ToWire(new StatusApplied(1, Ts, new ActorRef(1, "a"), new ActorRef(2, "b"), 7, 1, 30f)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("LOG 2026-07-01T00:00:00.0000000+00:00\t2\t0\t\tcat\tmsg\t")]  // a log frame, not an event
        [InlineData("READY pid=1 x64=True clr=4")]
        [InlineData("EVT")]                       // no body
        [InlineData("EVT ZONE\tnot-a-timestamp\t1\tZone")]
        [InlineData("EVT BOGUS\t2026-07-01T00:00:00.0000000+00:00\tx")]  // unknown tag
        [InlineData("EVT ZONE\t2026-07-01T00:00:00.0000000+00:00")]      // missing fields
        public void TryParse_rejects_non_event_lines(string? line)
        {
            Assert.False(GameEventFrame.TryParse(line, out var evt));
            Assert.Null(evt);
        }

        [Fact]
        public void DecodedEvent_reaches_the_real_bus_restamped()
        {
            // Mirrors SatelliteHost.TryEmitGameEvent: parse the wire line, re-stamp Sequence from the
            // bus's own counter, and publish. A subscriber must receive it with a fresh ordinal.
            using var bus = new GameEventBus();
            GameEvent? received = null;
            using var _ = bus.Subscribe(GameEventFilter.All, e => received = e);

            var wire = GameEventFrame.ToWire(new ZoneChanged(0, Ts, 813, "The Gold Saucer"))!;
            Assert.True(GameEventFrame.TryParse(wire, out var evt));
            bus.Emit(evt! with { Sequence = bus.NextSequence() });

            Assert.True(TestWait.Until(() => received is ZoneChanged));
            var z = Assert.IsType<ZoneChanged>(received);
            Assert.Equal(813u, z.ZoneId);
            Assert.Equal("The Gold Saucer", z.ZoneName);
            Assert.True(z.Sequence > 0);   // re-stamped by the host sink
        }
    }
}
