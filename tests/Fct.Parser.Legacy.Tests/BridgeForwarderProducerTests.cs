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
    }
}
