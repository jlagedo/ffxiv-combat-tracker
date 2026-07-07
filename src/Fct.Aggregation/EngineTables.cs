using System;
using System.Collections.Generic;
using System.Drawing;
using DamageTypeDef = Advanced_Combat_Tracker.CombatantData.DamageTypeDef;

namespace Advanced_Combat_Tracker
{
    // The FFXIV damage-type routing tables ACT runs with (installed live by the plugin's ACT_UIMods).
    // A replay/headless aggregator has no plugin to install them, so this sets the identical static
    // state and registers the ExportVariables. Runtime-neutral (no WinForms, no FormActMain, no
    // ActGlobals): the CombatTables formatters render through the stateless DamageString.Create, so
    // both the net48 replay oracle and the modern net10 engine call this to stand the engine up.
    // Held to the real ACT binary by Fct.Compat.Act.Tests/ExportVarsCompatTests.
    public static class EngineTables
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            CombatTables.Setup();

            // ACT_UIMods keys — the FFXIV plugin's own ExportVariables/ColumnDefs additions on top of
            // ACT-core (CombatTables.Setup() above), ported from ACT_UIMods.cs. CombatTables.Setup()'s
            // ACT-core parity fixtures never call Install(), so they never see these keys and stay
            // bit-for-bit green (do not add here to CombatTables.cs — see the plan's locked decision).

            // Job (ACT_UIMods.cs:1899-1928): ColumnDefs["Job"] cell/sort read CombatantDataExtension.Job()
            // directly; ExportVariables["Job"] is the real formatter's indirection — it calls
            // GetColumnByName("Job"), which resolves back through the ColumnDef above, so a direct
            // GetColumnByName("Job") caller (e.g. a future ColumnDef body) also works.
            CombatantData.ColumnDefs["Job"] = new CombatantData.ColumnDef(
                "Job", true, "VARCHAR(8)", "Job",
                (CombatantData.StringDataCallback)(d => d.Job()),
                (CombatantData.StringDataCallback)(d => d.Job()),
                (Left, Right) => string.Compare(Left.Job(), Right.Job(), StringComparison.OrdinalIgnoreCase));
            CombatantData.ExportVariables["Job"] = new CombatantData.TextExportFormatter(
                "Job", "Job Name", "Player's Job",
                (CombatantData.ExportStringDataCallback)((d, extraFormat) => d.GetColumnByName("Job")));

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
    }
}
