using System;
using System.Collections.Generic;
using System.Drawing;
using Advanced_Combat_Tracker;
using DamageTypeDef = Advanced_Combat_Tracker.CombatantData.DamageTypeDef;

namespace Fct.Compat.ActEngine.TestSupport
{
    /// <summary>
    /// Shared setup for exercising the ACT aggregation engine (<c>shared/Aggregation</c>) deterministically
    /// without the live plugin. Installs the FFXIV damage-type routing tables the plugin's ACT_UIMods
    /// registers live (swing-type → bucket links, bucket names) into the engine's global statics, and
    /// provides swing/encounter builders. Linked into both the net48 (<c>Fct.Compat.Act.Tests</c>) and
    /// net10 (<c>Fct.Compat.Shim.Tests</c>) suites so the same input drives both compilations of the
    /// engine — the basis of the cross-TFM ExportVariables parity check. The table values are held to the
    /// real ACT binary by <c>Fct.Compat.Act.Tests/ExportVarsCompatTests</c>.
    /// </summary>
    public static class ActEngineHarness
    {
        private static bool _tablesInstalled;

        /// <summary>Install the FFXIV damage-type routing tables (idempotent). These are engine input
        /// config, not the engine itself; the engine is the shared source both suites compile.</summary>
        public static void InstallRoutingTables()
        {
            if (_tablesInstalled) return;
            _tablesInstalled = true;

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

        /// <summary>Build a fresh encounter wired to a zone, anchored on <paramref name="charName"/>.</summary>
        public static EncounterData NewEncounter(string charName = "Player One", string zone = "Test Zone")
        {
            InstallRoutingTables();
            var z = new ZoneData { ZoneName = zone };
            var enc = new EncounterData(charName, zone, z) { Active = true };
            z.ActiveEncounter = enc;
            z.Items.Add(enc);
            return enc;
        }

        /// <summary>An outgoing ability-damage swing (SwingType 2 → routes into "Outgoing Damage").</summary>
        public static MasterSwing Hit(string attacker, string victim, long damage, DateTime time,
            int seq = 0, bool crit = false, string attackType = "Attack")
            => new MasterSwing(2, crit, "none", new Dnum(damage), time, seq, attackType, attacker, "physical", victim);
    }
}
