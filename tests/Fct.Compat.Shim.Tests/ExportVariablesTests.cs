using System;
using System.Globalization;
using System.Threading;
using Advanced_Combat_Tracker;
using Fct.Abstractions.Testing;
using Fct.Compat.ActEngine.TestSupport;
using Fct.Engine;
using Xunit;

// The engine uses global static routing tables + ExportVariables registration; serialize the suite so
// concurrent setup/flag tweaks across classes don't race (matching the net48 Fct.Compat.Act.Tests).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fct.Compat.Shim.Tests
{
    /// <summary>
    /// D5: the shared aggregation engine, compiled under net10, produces the same <c>ExportVariables</c>
    /// the net48 (oracle-verified) engine does — the cross-TFM parity guarantee for the shared source.
    /// Expected strings mirror <c>tests/Fct.Compat.Act.Tests/ExportVariablesTests</c> (the known
    /// 10×1000-over-9 s vector); <see cref="Fct.Compat.Shim.EncounterProjector"/> surfaces them on the
    /// modern snapshot's G1 bag.
    /// </summary>
    public class ExportVariablesTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        private static EncounterData BuildKnownEncounter()
        {
            // Constructing a FormActMain registers ACT's default ExportVariables (CombatTables.Setup).
            _ = new FormActMain(new FakePluginHost());
            var enc = ActEngineHarness.NewEncounter("Player One", "Self-Test Zone");
            for (int i = 0; i < 10; i++)
                enc.AddCombatAction(ActEngineHarness.Hit("Player One", "Dummy", 1000, T0.AddSeconds(i), seq: i, crit: i % 3 == 0));
            return enc;
        }

        [Theory]
        [InlineData("encdps", "1111.11")]
        [InlineData("ENCDPS", "1111")]
        [InlineData("damage", "10000")]
        [InlineData("name", "Player One")]
        [InlineData("crithit%", "40%")]
        [InlineData("hits", "10")]
        [InlineData("crithits", "4")]
        [InlineData("maxhit", "Attack-1000")]
        public void Projected_combatant_export_vars_match_the_net48_vector(string key, string expected)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var snapshot = EncounterProjector.Project(BuildKnownEncounter());
            var pc = Assert.Single(snapshot.Combatants);
            Assert.Equal(expected, pc.ExportVariables[key]);
        }

        [Theory]
        [InlineData("encdps", "1111.11")]
        [InlineData("ENCDPS", "1111")]
        [InlineData("damage", "10000")]
        [InlineData("CurrentZoneName", "Self-Test Zone")]
        public void Projected_encounter_export_vars_match_the_net48_vector(string key, string expected)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var snapshot = EncounterProjector.Project(BuildKnownEncounter());
            Assert.Equal(expected, snapshot.ExportVariables[key]);
        }

        [Fact]
        public void Projected_snapshot_carries_typed_metrics_and_the_g1_bag()
        {
            var snapshot = EncounterProjector.Project(BuildKnownEncounter());

            Assert.Equal(10000, snapshot.Damage);
            var pc = Assert.Single(snapshot.Combatants);
            Assert.Equal("Player One", pc.Name);
            Assert.Equal(10000, pc.Damage);
            Assert.NotEmpty(snapshot.ExportVariables);
            Assert.NotEmpty(pc.ExportVariables);
        }
    }
}
