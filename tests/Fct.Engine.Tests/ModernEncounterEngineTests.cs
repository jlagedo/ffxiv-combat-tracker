using System;
using System.Collections.Generic;
using System.Threading;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// The engine uses global static routing tables + ExportVariables registration; serialize the suite
// so concurrent engine construction across classes doesn't race (matching the net48/shim suites).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fct.Engine.Tests
{
    // Drives full-fidelity CombatSwing + lifecycle events through ModernEncounterEngine over the
    // in-memory bus and asserts the projected IEncounterService reflects them — the event→engine→
    // snapshot path. Bus dispatch is async (per-subscriber queue), so state is polled.
    public sealed class ModernEncounterEngineTests
    {
        private static readonly IReadOnlyDictionary<string, object> NoTags = new Dictionary<string, object>();

        private static CombatSwing Swing(DateTimeOffset ts, int swingType, long damage, string attacker, string victim, bool crit = false)
            => new CombatSwing(0, ts, swingType, crit, "none", damage, 1, "Attack", attacker, "", victim, NoTags);

        private static bool SpinUntil(Func<bool> cond, int timeoutMs = 3000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (cond()) return true;
                Thread.Sleep(10);
            }
            return cond();
        }

        private static (ModernEncounterEngine engine, EngineEncounterService svc, FakeGameSession session, FakeClock clock) NewEngine()
        {
            var session = new FakeGameSession();
            var clock = new FakeClock();
            var engine = new ModernEncounterEngine(session, NullLogger<ModernEncounterEngine>.Instance);
            engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var svc = new EngineEncounterService(engine, clock);
            return (engine, svc, session, clock);
        }

        [Fact]
        public void Swings_off_the_bus_aggregate_into_a_live_projected_encounter()
        {
            var (engine, svc, session, _) = NewEngine();
            var t0 = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

            session.Bus.Emit(new SetEncounterRequested(0, t0, "You", "Dummy"));
            session.Bus.Emit(Swing(t0, swingType: 2, damage: 1000, attacker: "You", victim: "Dummy"));
            session.Bus.Emit(Swing(t0, swingType: 2, damage: 500, attacker: "You", victim: "Dummy", crit: true));

            // The full event path folded both swings into the "YOU" combatant (attacker key upper-cased).
            Assert.True(SpinUntil(() =>
                engine.Lifecycle.ActiveZone.ActiveEncounter is { } enc &&
                enc.Items.TryGetValue("YOU", out var you) && you.Damage == 1500),
                "engine did not aggregate the swings into the active encounter");

            Assert.True(engine.Lifecycle.InCombat);

            // The service projects the live encounter into the snapshot the UI polls.
            svc.Tick();
            var snap = svc.Active;
            Assert.NotNull(snap);
            Assert.True(snap!.Active);
            Assert.Equal(1500, snap.Damage);
            Assert.Null(svc.Last);
        }

        [Fact]
        public void Idle_gap_auto_ends_combat_and_publishes_the_last_encounter()
        {
            var (engine, svc, session, clock) = NewEngine();
            var t0 = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            clock.LocalNow = t0;

            session.Bus.Emit(new SetEncounterRequested(0, t0, "You", "Dummy"));
            session.Bus.Emit(Swing(t0, swingType: 2, damage: 2000, attacker: "You", victim: "Dummy"));

            Assert.True(SpinUntil(() => engine.Lifecycle.InCombat &&
                engine.Lifecycle.ActiveZone.ActiveEncounter?.Items.ContainsKey("YOU") == true));

            // Advance the wall clock past the 6 s idle limit; the service's tick advances the engine
            // clock and the idle watchdog ends combat, exactly as ACT's log-time watchdog does.
            clock.LocalNow = t0.AddSeconds(10);
            svc.Tick();

            Assert.False(engine.Lifecycle.InCombat);
            Assert.Null(svc.Active);
            Assert.NotNull(svc.Last);
            Assert.False(svc.Last!.Active);
            Assert.Equal(2000, svc.Last.Damage);
        }

        [Fact]
        public void EndCombatRequested_closes_the_encounter()
        {
            var (engine, svc, session, _) = NewEngine();
            var t0 = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

            session.Bus.Emit(new SetEncounterRequested(0, t0, "You", "Dummy"));
            session.Bus.Emit(Swing(t0, swingType: 2, damage: 750, attacker: "You", victim: "Dummy"));
            Assert.True(SpinUntil(() => engine.Lifecycle.InCombat &&
                engine.Lifecycle.ActiveZone.ActiveEncounter?.Damage == 750));

            session.Bus.Emit(new EndCombatRequested(0, t0, Export: false));
            Assert.True(SpinUntil(() => !engine.Lifecycle.InCombat));

            svc.Tick();
            Assert.Null(svc.Active);
            Assert.NotNull(svc.Last);
            Assert.Equal(750, svc.Last!.Damage);
        }
    }
}
