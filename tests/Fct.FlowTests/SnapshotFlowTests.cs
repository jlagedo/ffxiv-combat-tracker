using System.Collections.Generic;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Legacy state polling → the native immutable snapshot (B5).</summary>
    public sealed class SnapshotFlowTests
    {
        // B5 — legacy GetCombatantList() poll → native IGameSnapshot (XIVPluginHelper.cs:904).
        // The superset Actor carries Statuses/Enmity/InCombat and the snapshot carries Focus/Hover,
        // closing Hojoring's Sharlayan/OverlayPlugin side-channels.
        [Fact]
        public void B5_SnapshotPoll_ExposesSupersetFields()
        {
            // G9: StatusEffect round-trips the SDK NetworkBuff refresh flag + applied timestamp.
            var statuses = new[]
            {
                new StatusEffect(124, "Slashing Resistance Down", 1, 30f, 100)
                {
                    RefreshPending = true,
                    AppliedAt = Flow.T0,
                },
            };
            var enmity = new[] { new EnmityEntry(200, "Boss", 5000, 100f) };
            var player = FakeActors.Player(100, "Warrior of Light", inCombat: true, statuses: statuses, enmity: enmity);
            var focus = FakeActors.Player(300, "Focus Target");
            var hover = FakeActors.Player(400, "Hover Target");
            // G10: a DoH/DoL actor with CP/GP + current-world + order for a lossless Combatant→Actor projection.
            var crafter = FakeActors.Player(500, "Crafter", inCombat: false,
                currentCp: 400, maxCp: 500, currentGp: 700, maxGp: 800, currentWorldId: 99, order: 3);

            var state = new FakeSnapshot
            {
                Player = player,
                Actors = new List<Actor> { player, crafter },
                Focus = focus,
                Hover = hover,
            };
            var host = new FakePluginHost(game: new FakeGameSession(state: state));

            // A native plugin polls the free-threaded snapshot.
            var snap = host.Game.Snapshot();
            var me = snap.Find(100);

            Assert.NotNull(me);
            Assert.True(me!.InCombat);
            Assert.Single(me.Statuses);
            Assert.Equal(124, me.Statuses[0].StatusId);
            Assert.True(me.Statuses[0].RefreshPending);          // G9 round-trips
            Assert.Equal(Flow.T0, me.Statuses[0].AppliedAt);     // G9 round-trips
            Assert.Single(me.Enmity);
            Assert.Equal(5000u, me.Enmity[0].Enmity);

            // Focus/Hover live on the snapshot (already present — not a gap).
            Assert.Equal(300u, snap.Focus!.Id);
            Assert.Equal(400u, snap.Hover!.Id);

            // G10: CP/GP + CurrentWorldId + Order round-trip (lossless DoL/DoH projection).
            var dol = snap.Find(500);
            Assert.Equal(400u, dol!.CurrentCp);
            Assert.Equal(500u, dol.MaxCp);
            Assert.Equal(700u, dol.CurrentGp);
            Assert.Equal(800u, dol.MaxGp);
            Assert.Equal(99u, dol.CurrentWorldId);
            Assert.Equal(3, dol.Order);
        }
    }
}
