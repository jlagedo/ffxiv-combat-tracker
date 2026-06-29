using System;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // The export-variable formatters are the contract OverlayPlugin/cactbot consume for the
    // MiniParse/DPS overlay. These pin the exact strings that flow across that boundary.
    [Collection("act")]
    public class ExportVariablesTests
    {
        public ExportVariablesTests(ActTablesFixture _) { }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        private static (EncounterData enc, CombatantData pc) BuildKnownEncounter()
        {
            var enc = ActTables.NewEncounter("Player One", "Self-Test Zone");
            for (int i = 0; i < 10; i++)
                enc.AddCombatAction(ActTables.Hit("Player One", "Dummy", 1000, T0.AddSeconds(i), seq: i, crit: i % 3 == 0));
            return (enc, enc.GetCombatant("Player One"));
        }

        private static string Combatant(string key, CombatantData cd)
        {
            Assert.True(CombatantData.ExportVariables.TryGetValue(key, out var f), $"missing combatant export var '{key}'");
            return f.GetExportString(cd, "");
        }

        private static string Encounter(string key, EncounterData ed)
        {
            Assert.True(EncounterData.ExportVariables.TryGetValue(key, out var f), $"missing encounter export var '{key}'");
            // OverlayPlugin always passes the encounter's allies as SelectiveAllies; the numeric
            // encounter formatters sum over that list (exactly as ACT's EncounterFormatSwitch does).
            return f.GetExportString(ed, ed.GetAllies(), "");
        }

        // Formats mirror ACT's CombatantFormatSwitch: lower-case per-second keys carry two decimals
        // ("F"), upper-case round to integer ("0"), percentages round via "0'%", and maxhit is the
        // "AttackType-Damage" string. (10 hits of 1000 over 9 s ⇒ EncDPS = 1111.11; every 3rd a crit.)
        [Theory]
        [InlineData("encdps", "1111.11")]
        [InlineData("ENCDPS", "1111")]
        [InlineData("damage", "10000")]
        [InlineData("name", "Player One")]
        [InlineData("NAME", "Player One")]
        [InlineData("crithit%", "40%")]
        [InlineData("hits", "10")]
        [InlineData("crithits", "4")]
        [InlineData("maxhit", "Attack-1000")]
        [InlineData("healed", "0")]
        public void Combatant_export_vars_match_known_vector(string key, string expected)
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var (_, pc) = BuildKnownEncounter();
            Assert.Equal(expected, Combatant(key, pc));
        }

        [Fact]
        public void Combatant_duration_is_formatted_mm_ss()
        {
            var (_, pc) = BuildKnownEncounter();
            Assert.Equal("00:09", Combatant("duration", pc));
        }

        [Theory]
        [InlineData("encdps", "1111.11")]
        [InlineData("ENCDPS", "1111")]
        [InlineData("damage", "10000")]
        [InlineData("CurrentZoneName", "Self-Test Zone")]
        public void Encounter_export_vars_match_known_vector(string key, string expected)
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var (enc, _) = BuildKnownEncounter();
            Assert.Equal(expected, Encounter(key, enc));
        }

        [Fact]
        public void Base_export_var_keys_overlayplugin_depends_on_are_registered()
        {
            // OverlayPlugin's MiniParse reads these by name; a missing key silently blanks a column.
            foreach (var key in new[] { "name", "duration", "dps", "encdps", "damage", "damage%",
                                        "healed", "hps", "maxhit", "hits", "crithit%", "deaths" })
                Assert.True(CombatantData.ExportVariables.ContainsKey(key), $"combatant export var '{key}' not registered");
            foreach (var key in new[] { "title", "duration", "encdps", "damage", "CurrentZoneName" })
                Assert.True(EncounterData.ExportVariables.ContainsKey(key), $"encounter export var '{key}' not registered");
        }
    }
}
