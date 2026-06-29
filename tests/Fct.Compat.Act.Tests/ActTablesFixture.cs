using System.Collections.Generic;
using System.Drawing;
using Advanced_Combat_Tracker;
using Xunit;
using DamageTypeDef = Advanced_Combat_Tracker.CombatantData.DamageTypeDef;

// The engine relies on global static routing tables that the REAL FFXIV_ACT_Plugin
// populates at init. These tests reproduce that exact setup (mirrored from the plugin's
// ACT_UIMods damage-type registration) so the aggregation is exercised deterministically
// without the live plugin. All test classes share this one fixture.
//
// The tables are global mutable statics, so test parallelization is disabled to keep the
// shared setup and per-test flag tweaks (e.g. ActGlobals.blockIsHit) from racing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fct.Compat.Act.Tests
{
    public sealed class ActTablesFixture
    {
        public ActTablesFixture() => ActTables.EnsureInstalled();
    }

    [CollectionDefinition("act")]
    public sealed class ActCollection : ICollectionFixture<ActTablesFixture> { }

    // Faithful reproduction of the plugin's damage-type routing (ACT_UIMods.cs).
    public static class ActTables
    {
        private static bool _installed;

        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;

            // A FormActMain registers the base ExportVariables (via CombatTables.Setup) and
            // gives ActGlobals.oFormActMain a real instance the engine reads (charName etc.).
            FormActMain.Log = _ => { };
            ActGlobals.oFormActMain = new FormActMain();

            DamageTypeDef Out(string l, int ally) => new DamageTypeDef(l, ally, Color.Orange);

            CombatantData.OutgoingDamageTypeDataObjects = new Dictionary<string, DamageTypeDef>
            {
                { "Auto-Attack (Out)", Out("Auto-Attack (Out)", -1) },
                { "Skill/Ability (Out)", Out("Skill/Ability (Out)", -1) },
                { "Simulated DoTs (Out)", Out("Simulated DoTs (Out)", -1) },
                { "Outgoing Damage", Out("Outgoing Damage", 0) },
                { "Damage Shields (Out)", Out("Damage Shields (Out)", 1) },
                { "Simulated HoTs (Out)", Out("Simulated HoTs (Out)", 1) },
                { "Healed (Out)", Out("Healed (Out)", 1) },
                { "Other (Out)", Out("Other (Out)", 0) },
                { "Status (Out)", Out("Status (Out)", 0) },
                { "Power Drain (Out)", Out("Power Drain (Out)", -1) },
                { "Power Replenish (Out)", Out("Power Replenish (Out)", 1) },
                { "Cure/Dispel (Out)", Out("Cure/Dispel (Out)", 0) },
                { "Threat (Out)", Out("Threat (Out)", -1) },
                // The reference bucket every outgoing swing also lands in; must be registered last so
                // CombatantData binds outAll to it (StartTime/EndTime/Duration derive from it).
                { "All Outgoing (Ref)", Out("All Outgoing (Ref)", 0) },
            };
            CombatantData.IncomingDamageTypeDataObjects = new Dictionary<string, DamageTypeDef>
            {
                { "Simulated DoTs (Inc)", Out("Simulated DoTs (Inc)", -1) },
                { "Incoming Damage", Out("Incoming Damage", -1) },
                { "Damage Shields (Inc)", Out("Damage Shields (Inc)", 1) },
                { "Simulated HoTs (Inc)", Out("Simulated HoTs (Inc)", 1) },
                { "Healed (Inc)", Out("Healed (Inc)", 1) },
                { "Other (Inc)", Out("Other (Inc)", 0) },
                { "Status (Inc)", Out("Status (Inc)", 0) },
                { "Power Drain (Inc)", Out("Power Drain (Inc)", -1) },
                { "Power Replenish (Inc)", Out("Power Replenish (Inc)", 1) },
                { "Cure/Dispel (Inc)", Out("Cure/Dispel (Inc)", 0) },
                { "Threat (Inc)", Out("Threat (Inc)", -1) },
                { "All Incoming (Ref)", Out("All Incoming (Ref)", 0) },
            };

            CombatantData.SwingTypeToDamageTypeDataLinksOutgoing = new SortedDictionary<int, List<string>>
            {
                { 0, new List<string> { "Auto-Attack (Out)", "Outgoing Damage" } },
                { 1, new List<string> { "Other (Out)" } },
                { 2, new List<string> { "Skill/Ability (Out)", "Outgoing Damage" } },
                { 3, new List<string> { "Simulated DoTs (Out)", "Outgoing Damage" } },
                { 4, new List<string> { "Healed (Out)" } },
                { 5, new List<string> { "Simulated HoTs (Out)", "Healed (Out)" } },
                { 6, new List<string> { "Power Drain (Out)" } },
                { 7, new List<string> { "Power Replenish (Out)" } },
                { 8, new List<string> { "Status (Out)" } },
                { 9, new List<string> { "Cure/Dispel (Out)" } },
                { 10, new List<string> { "Threat (Out)" } },
                { 11, new List<string> { "Damage Shields (Out)", "Healed (Out)" } },
            };
            CombatantData.SwingTypeToDamageTypeDataLinksIncoming = new SortedDictionary<int, List<string>>
            {
                { 0, new List<string> { "Incoming Damage" } },
                { 1, new List<string> { "Other (Inc)" } },
                { 2, new List<string> { "Incoming Damage" } },
                { 3, new List<string> { "Simulated DoTs (Inc)", "Incoming Damage" } },
                { 4, new List<string> { "Healed (Inc)" } },
                { 5, new List<string> { "Simulated HoTs (Inc)", "Healed (Inc)" } },
                { 6, new List<string> { "Power Drain (Inc)" } },
                { 7, new List<string> { "Power Replenish (Inc)" } },
                { 8, new List<string> { "Status (Inc)" } },
                { 9, new List<string> { "Cure/Dispel (Inc)" } },
                { 10, new List<string> { "Threat (Inc)" } },
                { 11, new List<string> { "Damage Shields (Inc)", "Healed (Inc)" } },
            };
            CombatantData.DamageSwingTypes = new List<int> { 0, 2, 3 };
            CombatantData.HealingSwingTypes = new List<int> { 4, 5, 8, 9, 1 };
        }

        // Build a fresh encounter wired to a zone, with the given charName as the ally anchor.
        public static EncounterData NewEncounter(string charName = "Player One", string zone = "Test Zone")
        {
            EnsureInstalled();
            var z = new ZoneData { ZoneName = zone };
            var enc = new EncounterData(charName, zone, z) { Active = true };
            z.ActiveEncounter = enc;
            z.Items.Add(enc);
            return enc;
        }

        // An outgoing ability-damage swing (SwingType 2 → routes into "Outgoing Damage").
        public static MasterSwing Hit(string attacker, string victim, long damage, System.DateTime time,
            int seq = 0, bool crit = false, string attackType = "Attack")
            => new MasterSwing(2, crit, "none", new Dnum(damage), time, seq, attackType, attacker, "physical", victim);
    }
}
