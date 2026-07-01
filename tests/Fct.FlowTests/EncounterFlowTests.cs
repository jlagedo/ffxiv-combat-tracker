using System.Collections.Generic;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Xunit;

namespace Fct.FlowTests
{
    /// <summary>Combat-state driving/reading across the seam (A4, B2) — and G1's ExportVariables bag.</summary>
    public sealed class EncounterFlowTests
    {
        // A4 — native StartCombat → OverlayPlugin encounter reader (MiniParseEventSource.cs:300-360).
        // The cactbot payload (G1 ExportVariables) must round-trip through the snapshot.
        [Fact]
        public void A4_NativeStartCombat_ReaderSeesLabeledEncounter_AndExportVars()
        {
            var encounters = new FakeEncounterService
            {
                SeedExportVariables = new Dictionary<string, string>
                {
                    ["encdps"] = "12345",
                    ["Last60DPS"] = "11890",
                },
            };
            var host = new FakePluginHost(encounters: encounters);
            using var shim = new ShimStub(host);
            var op = new OpEncounterReaderDouble();

            // Native plugin opens an encounter labeled with the zone.
            host.Encounters.StartCombat("The Navel", zone: "The Navel (Hard)");
            op.Poll(shim);

            Assert.True(op.SawInCombat);
            Assert.Equal("The Navel", op.Title);
            Assert.Equal("The Navel (Hard)", host.Encounters.Active!.Zone); // G7 zone label carried
            Assert.NotNull(op.ExportVariables);
            Assert.Equal("12345", op.ExportVariables!["encdps"]);      // G1 bag round-trips
            Assert.Equal("11890", op.ExportVariables!["Last60DPS"]);
        }

        // G1 sanity: the bag defaults to empty on a hand-built snapshot (additive, no breakage).
        [Fact]
        public void G1_ExportVariables_DefaultsEmpty_OnBothRecords()
        {
            var metrics = new CombatantMetrics("Y'shtola", 1, 6, 4000, 400000, 33.3, 0, 20, 15, 0);
            var enc = new EncounterSnapshot("z", Flow.T0, System.TimeSpan.Zero, true, 4000, 400000,
                new[] { metrics });

            Assert.Empty(enc.ExportVariables);
            Assert.Empty(metrics.ExportVariables);

            // And it is init-settable (the shim's population path).
            var withBag = metrics with { ExportVariables = new Dictionary<string, string> { ["maxhit"] = "9999" } };
            Assert.Equal("9999", withBag.ExportVariables["maxhit"]);
            Assert.Empty(metrics.ExportVariables); // original unchanged
        }

        // G2 — OverlayPlugin's overHeal/damageShield/absorbHeal (Integration/FFXIVExportVariables.cs:31-114)
        // land as typed CombatantMetrics fields a native reader consumes directly.
        [Fact]
        public void G2_HealMetrics_TypedFields_RoundTrip()
        {
            var healer = new CombatantMetrics("Y'shtola", 1, 6, 4000, 400000, 33.3, 250000, 0, 0, 0)
            {
                Overheal = 90000,
                ShieldedDamage = 12000,
                Absorbed = 30000,
            };
            var encounters = new FakeEncounterService { SeedCombatants = new[] { healer } };
            var host = new FakePluginHost(encounters: encounters);

            host.Encounters.StartCombat("The Navel");

            var read = Assert.Single(host.Encounters.Active!.Combatants);
            Assert.Equal(90000, read.Overheal);
            Assert.Equal(12000, read.ShieldedDamage);
            Assert.Equal(30000, read.Absorbed);
        }

        // B2 — Triggernometry SetEncounter/EndCombat + AppendLogLine → native reader (ProxyPlugin.cs:323-334).
        [Fact]
        public void B2_LegacyEncounterDriver_ReachesNativeReader()
        {
            var encounters = new FakeEncounterService();
            var host = new FakePluginHost(encounters: encounters);
            using var shim = new ShimStub(host);

            // Trig passes the player name as the encounter title.
            shim.SetEncounter("Warrior of Light");
            Assert.True(host.Encounters.InCombat);
            Assert.Equal("Warrior of Light", host.Encounters.Active!.Title);
            Assert.Equal("Warrior of Light", host.Encounters.Active!.Zone); // G7: zone defaults to title

            shim.ActEncounterLogAppend("00|synthetic line");
            Assert.Contains("00|synthetic line", encounters.AppendedLines);

            shim.EndCombat();
            Assert.False(host.Encounters.InCombat);
            Assert.Null(host.Encounters.Active);
            Assert.Equal("Warrior of Light", host.Encounters.Last!.Title);
            Assert.False(host.Encounters.Last!.Active);
        }
    }
}
