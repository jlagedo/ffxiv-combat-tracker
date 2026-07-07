using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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

            // ParryPct (ACT_UIMods.cs:1930-1959 CombatantData level; :2023-2041 DamageTypeData level;
            // :2080-2097 AttackType level). CombatantData.ColumnDefs["ParryPct"] resolves through
            // Items[DamageTypeDataIncomingDamage].GetColumnByName("ParryPct") ->
            // DamageTypeData.ColumnDefs["ParryPct"] (guarded by the "All" attack-type bucket's
            // presence, else "0%") -> Items["All"].GetColumnByName("ParryPct") ->
            // AttackType.ColumnDefs["ParryPct"], which reads Data.Parry()/Data.BlockParryCount()
            // (P5.1's ported CombatantDataExtension extension methods). "All" is the literal English
            // bucket key CombatTables.cs already uses (ActLocalization's "attackTypeTerm-all" in the
            // decompile — no localization layer on our side).
            CombatantData.ColumnDefs["ParryPct"] = new CombatantData.ColumnDef(
                "ParryPct", false, "VARCHAR(8)", "ParryPct",
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ParryPct")),
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ParryPct")),
                (Left, Right) => string.Compare(
                    Left.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ParryPct"),
                    Right.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ParryPct"),
                    StringComparison.OrdinalIgnoreCase));
            CombatantData.ExportVariables["ParryPct"] = new CombatantData.TextExportFormatter(
                "ParryPct", "Parry Percent", "Percent of hits that were parried.",
                (CombatantData.ExportStringDataCallback)((d, extraFormat) => d.GetColumnByName("ParryPct")));

            DamageTypeData.ColumnDefs["ParryPct"] = new DamageTypeData.ColumnDef(
                "ParryPct", false, "VARCHAR(8)", "ParryPct",
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All")
                    ? 0.ToString("0'%", CultureInfo.InvariantCulture)
                    : d.Items["All"].GetColumnByName("ParryPct")),
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All")
                    ? 0.ToString("0'%", CultureInfo.InvariantCulture)
                    : d.Items["All"].GetColumnByName("ParryPct")));

            AttackType.ColumnDefs["ParryPct"] = new AttackType.ColumnDef(
                "ParryPct", false, "VARCHAR(8)", "ParryPct",
                (AttackType.StringDataCallback)(d => ((double)d.Parry() * 100.0 / (double)CombatantDataExtension.OneOrInt(d.BlockParryCount())).ToString("0'%", CultureInfo.InvariantCulture)),
                (AttackType.StringDataCallback)(d => ((double)d.Parry() * 100.0 / (double)CombatantDataExtension.OneOrInt(d.BlockParryCount())).ToString("0'%", CultureInfo.InvariantCulture)),
                (Left, Right) => Left.Parry().CompareTo(Right.Parry()));

            // BlockPct (ACT_UIMods.cs:1961-1990 CombatantData level; :2042-2060 DamageTypeData level;
            // :2118-2135 AttackType level) — the identical chain shape as ParryPct, reading
            // Data.Block()/Data.BlockParryCount().
            CombatantData.ColumnDefs["BlockPct"] = new CombatantData.ColumnDef(
                "BlockPct", false, "VARCHAR(8)", "BlockPct",
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("BlockPct")),
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("BlockPct")),
                (Left, Right) => string.Compare(
                    Left.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("BlockPct"),
                    Right.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("BlockPct"),
                    StringComparison.OrdinalIgnoreCase));
            CombatantData.ExportVariables["BlockPct"] = new CombatantData.TextExportFormatter(
                "BlockPct", "Block Percent", "Percent of hits that were blocked.",
                (CombatantData.ExportStringDataCallback)((d, extraFormat) => d.GetColumnByName("BlockPct")));

            DamageTypeData.ColumnDefs["BlockPct"] = new DamageTypeData.ColumnDef(
                "BlockPct", false, "VARCHAR(8)", "BlockPct",
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All")
                    ? 0.ToString("0'%", CultureInfo.InvariantCulture)
                    : d.Items["All"].GetColumnByName("BlockPct")),
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All")
                    ? 0.ToString("0'%", CultureInfo.InvariantCulture)
                    : d.Items["All"].GetColumnByName("BlockPct")));

            AttackType.ColumnDefs["BlockPct"] = new AttackType.ColumnDef(
                "BlockPct", false, "VARCHAR(8)", "BlockPct",
                (AttackType.StringDataCallback)(d => ((double)d.Block() * 100.0 / (double)CombatantDataExtension.OneOrInt(d.BlockParryCount())).ToString("0'%", CultureInfo.InvariantCulture)),
                (AttackType.StringDataCallback)(d => ((double)d.Block() * 100.0 / (double)CombatantDataExtension.OneOrInt(d.BlockParryCount())).ToString("0'%", CultureInfo.InvariantCulture)),
                (Left, Right) => Left.Block().CompareTo(Right.Block()));

            // IncToHit (ACT_UIMods.cs:1992-2021) — a single-level indirection, unlike ParryPct/
            // BlockPct/OverHealPct: the cell body calls Items[DamageTypeDataIncomingDamage]
            // .GetColumnByName("ToHit"), but "ToHit" on DamageTypeData/AttackType is an ACT-core
            // column (real ACT's own FormActMain.cs registers it, never ACT_UIMods itself — grepped,
            // confirmed no such registration anywhere in the plugin's ACT_UIMods.cs) that
            // CombatTables.Setup() does not port (per the plan's locked "do not edit CombatTables.cs"
            // decision, G2's empty-ColumnDefs gap is FFXIV-scope only). The plugin-in-the-loop oracle
            // itself never has "ToHit" registered either (ActOracle.RegisterTables() only hand-mirrors
            // the FFXIV damage-type routing statics, never runs the real FormActMain's own ACT-core
            // ColumnDefs setup — the real oFormActMain is FormatterServices.GetUninitializedObject,
            // its constructor never runs), so GetColumnByName("ToHit") returns "" on both sides —
            // confirmed empirically: IncToHit is blank for every combatant in
            // combat-slice.plugin.exportvars.tsv. No DamageTypeData/AttackType "ToHit" registration is
            // added here: doing so would make our engine diverge FROM the oracle, not match it.
            CombatantData.ColumnDefs["IncToHit"] = new CombatantData.ColumnDef(
                "IncToHit", false, "VARCHAR(8)", "IncToHit",
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ToHit")),
                (CombatantData.StringDataCallback)(d => d.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ToHit")),
                (Left, Right) => string.Compare(
                    Left.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ToHit"),
                    Right.Items[CombatantData.DamageTypeDataIncomingDamage].GetColumnByName("ToHit"),
                    StringComparison.OrdinalIgnoreCase));
            CombatantData.ExportVariables["IncToHit"] = new CombatantData.TextExportFormatter(
                "IncToHit", "Incoming Hit Rate", "Incoming hits to the target.",
                (CombatantData.ExportStringDataCallback)((d, extraFormat) => d.GetColumnByName("IncToHit")));

            // OverHealPct (ACT_UIMods.cs:2137-2166 CombatantData level; :2168-2185 DamageTypeData
            // level; :2187-2204 AttackType["OverHeal"]). CombatantData.ColumnDefs["OverHealPct"]
            // resolves through Items[DamageTypeDataOutgoingHealing].GetColumnByName("OverHeal") ->
            // DamageTypeData.ColumnDefs["OverHeal"] (guarded by the "All" bucket, else "0") ->
            // Items["All"].GetColumnByName("OverHeal") -> AttackType.ColumnDefs["OverHeal"], which
            // reads Data.Overheal() (P5.1's ported extension — sums the "overheal" MasterSwing.Tags
            // value directly; it never calls GetColumnByName, so MasterSwing.ColumnDefs["OverHeal"]
            // (ACT_UIMods.cs:2206-2223) is NOT in this resolution chain and is intentionally not
            // registered here — that standalone MasterSwing raw-table column set is P5.5's job).
            // The denominator is Data.DirectHeal() (P5.1), guarded by the same "All" bucket presence
            // check and OneOrInt(long) (added to CombatantDataExtension above for this call).
            CombatantData.ColumnDefs["OverHealPct"] = new CombatantData.ColumnDef(
                "OverHealPct", true, "VARCHAR(8)", "OverHealPct",
                (CombatantData.StringDataCallback)(d => (long.Parse(d.Items[CombatantData.DamageTypeDataOutgoingHealing].GetColumnByName("OverHeal"), CultureInfo.InvariantCulture) * 100
                    / CombatantDataExtension.OneOrInt(!d.Items[CombatantData.DamageTypeDataOutgoingHealing].Items.ContainsKey("All") ? 0L : d.DirectHeal())).ToString("0'%", CultureInfo.InvariantCulture)),
                (CombatantData.StringDataCallback)(d => (long.Parse(d.Items[CombatantData.DamageTypeDataOutgoingHealing].GetColumnByName("OverHeal"), CultureInfo.InvariantCulture) * 100
                    / CombatantDataExtension.OneOrInt(!d.Items[CombatantData.DamageTypeDataOutgoingHealing].Items.ContainsKey("All") ? 0L : d.DirectHeal())).ToString("0'%", CultureInfo.InvariantCulture)),
                (Left, Right) => long.Parse(Left.GetColumnByName("OverHealPct").Replace('%', ' '), CultureInfo.InvariantCulture)
                    .CompareTo(long.Parse(Right.GetColumnByName("OverHealPct").Replace('%', ' '), CultureInfo.InvariantCulture)));
            CombatantData.ExportVariables["OverHealPct"] = new CombatantData.TextExportFormatter(
                "OverHealPct", "Over-Heal Percent", "Percent of heals above target's Max HP",
                (CombatantData.ExportStringDataCallback)((d, extraFormat) => d.GetColumnByName("OverHealPct")));

            DamageTypeData.ColumnDefs["OverHeal"] = new DamageTypeData.ColumnDef(
                "OverHeal", false, "INT", "OverHeal",
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All") ? "0" : d.Items["All"].GetColumnByName("OverHeal")),
                (DamageTypeData.StringDataCallback)(d => !d.Items.ContainsKey("All") ? "0" : d.Items["All"].GetColumnByName("OverHeal")));

            AttackType.ColumnDefs["OverHeal"] = new AttackType.ColumnDef(
                "OverHeal", false, "INT", "OverHeal",
                (AttackType.StringDataCallback)(d => d.Overheal().ToString(CultureInfo.InvariantCulture)),
                (AttackType.StringDataCallback)(d => d.Overheal().ToString(CultureInfo.InvariantCulture)),
                (Left, Right) => Left.Overheal().CompareTo(Right.Overheal()));

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
