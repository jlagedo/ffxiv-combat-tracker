using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Fct.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.Engine.Tests
{
    /// <summary>
    /// ISOLATION-PLAN P2 gate — the frame-replay foundation, deterministic and plugin-free: replay a
    /// committed <see cref="FrameSession"/> fixture (the recorded bridged frame stream) in-process through
    /// a real <see cref="ModernEncounterEngine"/> and assert the host engine reproduces the fixture's
    /// recorded encounter totals. The lifecycle frames (SetEncounter/EndCombat) are explicit in the
    /// stream, so the same idle-split encounters re-form with no wall clock and no FFXIV_ACT_Plugin —
    /// any consumer-side behavior can now be exercised headlessly from a committed fixture in CI.
    /// The YOU total is tied to the real-ACT oracle aggregate baseline (three-way parity: oracle →
    /// wire-path → frame-replay).
    /// </summary>
    public sealed class FrameReplayTests
    {
        private static string Fixture(string rel) => Path.Combine(AppContext.BaseDirectory, "fixtures", rel);

        // Construct + start the engine (subscribed to the bus). StartAsync completes synchronously; the
        // wait is confined to this helper so the xUnit1031 blocking-in-a-test-method rule is satisfied.
        private static ModernEncounterEngine StartedEngine(FakeGameSession session)
        {
            var engine = new ModernEncounterEngine(session, NullLogger<ModernEncounterEngine>.Instance);
            engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return engine;
        }

        // YOU's total outgoing damage from the real-ACT aggregate baseline (combat-slice*.aggregate.tsv,
        // Damage column) — conserved across the idle-split encounters the frame stream reproduces.
        private static long OracleYouDamage(string slice)
        {
            var lines = File.ReadAllLines(Fixture(slice + ".aggregate.tsv"));
            int damageIdx = lines[0].Split('\t').ToList().IndexOf("Damage");
            var you = lines.Skip(1).Select(l => l.Split('\t')).First(c => c[0] == "YOU");
            return long.Parse(you[damageIdx], CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public void Replaying_a_frame_fixture_reproduces_the_recorded_encounter_totals(string slice)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var session = new FakeGameSession();
            var engine = StartedEngine(session);

            long youDamage = 0;
            engine.Lifecycle.CombatEnded = enc =>
            {
                var you = enc.GetCombatant("YOU");
                if (you != null) youDamage += you.Damage;
            };

            int frames = 0, swings = 0;
            foreach (var line in File.ReadLines(Fixture(Path.Combine("frames", slice + ".frames.tsv"))))
            {
                if (!FrameSession.TryParseLine(line, out _, out var evt) || evt is null) continue;
                frames++;
                if (evt is Fct.Abstractions.CombatSwing) swings++;
                session.Bus.Emit(evt);   // InMemoryEventBus folds synchronously into the engine
            }

            Assert.True(swings > 0, $"fixture carried no swings ({frames} frames decoded)");
            Assert.Equal(OracleYouDamage(slice), youDamage);
        }
    }
}
