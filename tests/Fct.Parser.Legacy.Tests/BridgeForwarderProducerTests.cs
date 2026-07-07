using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.LegacyHost;
using FFXIV_ACT_Plugin.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;
using Xunit;

namespace Fct.Parser.Legacy.Tests
{
    // ISOLATION-PLAN P5 producer gate (plugin-free): the BridgeForwarder projects the SDK repository into
    // the new upstream frames — a RepositorySnapshot (combatant roster with fresh HP/position), the
    // one-shot resource dictionaries, and the game PID. Driven against fake SDK surfaces (no game, no
    // plugin, no message loop), so the projection is asserted deterministically over the real wire codec.
    public class BridgeForwarderProducerTests
    {
        private static bool WaitFor(Func<bool> cond, int ms = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; System.Threading.Thread.Sleep(2); }
            return cond();
        }

        private static SdkModels.Combatant Combatant(uint id, string name, uint hp, uint maxHp,
            float x, float y, float z, float heading) => new SdkModels.Combatant
        {
            ID = id, Name = name, type = 1, CurrentHP = hp, MaxHP = maxHp,
            PosX = x, PosY = y, PosZ = z, Heading = heading,
        };

        [Fact]
        public void Forwards_repository_snapshot_resource_dictionaries_and_pid()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);

            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository();
            repo.Combatants.Add(Combatant(0x1000, "You", 50, 100, 1.5f, 2.5f, 3.5f, 0.25f));
            repo.Combatants.Add(Combatant(0x2000, "Boss", 900000, 1000000, -10.5f, 0f, 10.5f, 3.140625f));
            repo.Resources[ResourceType.SkillList_EN] = new Dictionary<uint, string> { [1] = "Heavy Swing", [2] = "Maim" };
            repo.Process = Process.GetCurrentProcess();

            fwd.BindForTest(sub, repo);      // one-shot: resource dictionaries + initial PID
            fwd.EmitRepositorySnapshot();     // one combatant-list poll

            List<GameEvent> Decode() => captured
                .Select(l => GameEventFrame.TryParse(l, out var e) ? e : null)
                .Where(e => e != null).ToList()!;

            Assert.True(WaitFor(() =>
            {
                var evts = Decode();
                return evts.OfType<RepositorySnapshot>().Any()
                    && evts.OfType<ResourceDictionaryForwarded>().Any()
                    && evts.OfType<GameProcessChanged>().Any();
            }), "producer did not forward all of snapshot/resource/pid");

            var decoded = Decode();

            var snap = decoded.OfType<RepositorySnapshot>().Last();
            Assert.Equal(2, snap.Combatants.Count);
            var you = snap.Combatants.Single(c => c.Id == 0x1000);
            Assert.Equal("You", you.Name);
            Assert.Equal(50u, you.Hp);
            Assert.Equal(100u, you.MaxHp);
            Assert.Equal(new Position(1.5f, 2.5f, 3.5f, 0.25f), you.Position);
            Assert.Equal(new Position(-10.5f, 0f, 10.5f, 3.140625f), snap.Combatants.Single(c => c.Id == 0x2000).Position);

            var skills = decoded.OfType<ResourceDictionaryForwarded>().Single(r => r.Kind == ResourceKind.Action);
            Assert.Equal("Heavy Swing", skills.Entries[1]);
            Assert.Equal("Maim", skills.Entries[2]);

            Assert.Contains(decoded.OfType<GameProcessChanged>(), p => p.Pid == Process.GetCurrentProcess().Id);
        }

        [Fact]
        public void ProcessChanged_event_forwards_the_new_pid()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository();   // no initial process → no one-shot PID
            fwd.BindForTest(sub, repo);

            sub.RaiseProcessChanged(Process.GetCurrentProcess());

            Assert.True(WaitFor(() => captured
                .Select(l => GameEventFrame.TryParse(l, out var e) ? e : null)
                .OfType<GameProcessChanged>()
                .Any(p => p.Pid == Process.GetCurrentProcess().Id)), "ProcessChanged pid was not forwarded");
        }

        private static List<GameEvent> Decode(ConcurrentQueue<string> captured) => captured
            .Select(l => GameEventFrame.TryParse(l, out var e) ? e : null)
            .Where(e => e != null).ToList()!;

        // EmitInitialRepositoryState builds and forwards ONE
        // SessionStateChanged from the SDK repository, mapped through BridgeForwarder's
        // Language/Region enum maps — never the ConsumerDataRepository stubs (those are a P3.5 concern
        // on the OTHER end of the pipe; this test is the producer/origin side).
        [Fact]
        public void EmitInitialRepositoryState_forwards_one_mapped_SessionStateChanged()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository
            {
                GameVersion = "6.58",
                Language = Language.Japanese,
                Region = 3,                                   // Korean, per the decompiled Region enum
                ServerTimestamp = DateTime.UtcNow.AddSeconds(5),
                ChatLogAvailable = true,
            };

            fwd.BindForTest(sub, repo);

            Assert.True(WaitFor(() => Decode(captured).OfType<SessionStateChanged>().Any()),
                "no SessionStateChanged was forwarded on bind");

            var state = Decode(captured).OfType<SessionStateChanged>().Single();
            Assert.Equal("6.58", state.GameVersion);
            Assert.Equal(GameLanguage.Japanese, state.Language);
            Assert.Equal(GameRegion.Korean, state.Region);
            Assert.True(state.IsChatLogAvailable);
            // The offset is computed against DateTime.UtcNow at forward time, so assert closeness rather
            // than equality (the ~5s ServerTimestamp lead should survive, modulo test execution jitter).
            Assert.InRange(state.ServerClockOffset.TotalSeconds, 3.0, 8.0);
        }

        // P0.3's headless verdict: GetGameVersion() == "" and GetServerTimestamp() == DateTime.MinValue
        // with no live game process. The producer must forward "" (never a "0.0" placeholder, §3) and
        // must guard the clock offset to TimeSpan.Zero (never the ~2000-year garbage span).
        [Fact]
        public void EmitInitialRepositoryState_guards_unknown_version_and_default_server_timestamp()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository
            {
                GameVersion = "",              // P0.3: headless real plugin serves "" (no game path)
                ServerTimestamp = DateTime.MinValue,   // P0.3: no live memory scan
            };

            fwd.BindForTest(sub, repo);

            Assert.True(WaitFor(() => Decode(captured).OfType<SessionStateChanged>().Any()),
                "no SessionStateChanged was forwarded on bind");

            var state = Decode(captured).OfType<SessionStateChanged>().Single();
            Assert.Equal("", state.GameVersion);
            Assert.Equal(TimeSpan.Zero, state.ServerClockOffset);
        }

        // EmitInitialRepositoryState must forward the SDK's SELECTED
        // language's Buff/Skill resource dictionaries, not a hard-coded *_EN, wherever the SDK exposes a
        // locale variant at all (only BuffList/SkillList have one — WorldList/ZoneList do not, see
        // BridgeForwarder.EmitInitialRepositoryState's comment).
        [Fact]
        public void EmitInitialRepositoryState_forwards_the_selected_languages_buff_and_skill_lists()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository { Language = Language.Japanese };
            // JP-specific tables — proves the producer requested the locale variant, not *_EN.
            repo.Resources[ResourceType.BuffList_JP] = new Dictionary<uint, string> { [10] = "スタン" };
            repo.Resources[ResourceType.SkillList_JP] = new Dictionary<uint, string> { [20] = "ヘビースイング" };
            // EN tables present too, deliberately with DIFFERENT values, so a wrong (EN) request would be
            // caught by value, not just by which ResourceType key was populated.
            repo.Resources[ResourceType.BuffList_EN] = new Dictionary<uint, string> { [10] = "Stun" };
            repo.Resources[ResourceType.SkillList_EN] = new Dictionary<uint, string> { [20] = "Heavy Swing" };
            repo.Resources[ResourceType.ZoneList_EN] = new Dictionary<uint, string> { [1] = "Limsa Lominsa" };
            repo.Resources[ResourceType.WorldList_EN] = new Dictionary<uint, string> { [2] = "Gilgamesh" };

            fwd.BindForTest(sub, repo);

            Assert.True(WaitFor(() => Decode(captured).OfType<ResourceDictionaryForwarded>().Count() >= 4),
                "expected buff/skill/zone/world dictionaries to all forward");

            var decoded = Decode(captured).OfType<ResourceDictionaryForwarded>().ToList();
            var buffs = decoded.Single(r => r.Kind == ResourceKind.Status);
            Assert.Equal("スタン", buffs.Entries[10]);            // JP, never the EN "Stun"
            var skills = decoded.Single(r => r.Kind == ResourceKind.Action);
            Assert.Equal("ヘビースイング", skills.Entries[20]);   // JP, never the EN "Heavy Swing"
            // World/Zone have NO locale variant in the SDK's ResourceType enum at all — *_EN stays the
            // only request, the SDK's own ceiling rather than a shortcut.
            var zones = decoded.Single(r => r.Kind == ResourceKind.Zone);
            Assert.Equal("Limsa Lominsa", zones.Entries[1]);
            var worlds = decoded.Single(r => r.Kind == ResourceKind.World);
            Assert.Equal("Gilgamesh", worlds.Entries[2]);
        }

        // Version/region can change on relog/patch (P3.3) — OnProcessChanged must re-emit the current
        // SessionStateChanged, not just the one-shot bind-time snapshot.
        [Fact]
        public void ProcessChanged_event_re_emits_SessionStateChanged()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository { GameVersion = "1.0.0" };
            fwd.BindForTest(sub, repo);

            Assert.True(WaitFor(() => Decode(captured).OfType<SessionStateChanged>().Any()),
                "no SessionStateChanged was forwarded on bind");

            repo.GameVersion = "2.0.0";   // simulate a patch/relog changing the reported version
            sub.RaiseProcessChanged(Process.GetCurrentProcess());

            Assert.True(WaitFor(() => Decode(captured).OfType<SessionStateChanged>()
                .Any(s => s.GameVersion == "2.0.0")), "ProcessChanged did not re-emit the updated SessionStateChanged");
            Assert.True(Decode(captured).OfType<SessionStateChanged>().Count() >= 2,
                "expected both the bind-time and the ProcessChanged-time SessionStateChanged");
        }

        // G7/P3.3: PartyListChanged's real SDK partySize argument must ride the PartyChanged record,
        // distinct from Members.Count (alliance content: up to 24 visible members, 8-person party size).
        [Fact]
        public void PartyListChanged_event_forwards_the_real_partySize()
        {
            var captured = new ConcurrentQueue<string>();
            using var fwd = new BridgeForwarder(s => captured.Enqueue(s), NullLogger.Instance);
            var sub = new FakeDataSubscription();
            var repo = new FakeDataRepository();
            fwd.BindForTest(sub, repo);

            var roster = new System.Collections.ObjectModel.ReadOnlyCollection<uint>(
                Enumerable.Range(1, 24).Select(i => (uint)i).ToList());
            sub.RaisePartyListChanged(roster, 8);

            Assert.True(WaitFor(() => Decode(captured).OfType<PartyChanged>().Any(p => p.Members.Count == 24)),
                "PartyChanged with the full 24-member roster was not forwarded");

            var party = Decode(captured).OfType<PartyChanged>().Single(p => p.Members.Count == 24);
            Assert.Equal(8, party.PartySize);
        }
    }
}
