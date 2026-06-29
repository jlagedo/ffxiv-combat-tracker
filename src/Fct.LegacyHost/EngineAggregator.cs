using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Advanced_Combat_Tracker;
using DamageTypeDef = Advanced_Combat_Tracker.CombatantData.DamageTypeDef;

namespace Fct.LegacyHost
{
    // Corpus-scale ACT-engine parity. The real plugin (the sole parser) has already turned each
    // Network_*.log into a MasterSwing stream — captured by --mass-oracle as <name>.oracle.tsv.
    // Here we feed those SAME swings through OUR clean-room Fct.Compat.Act aggregation and dump the
    // ExportVariables payload OverlayPlugin/cactbot read, to <name>.engine.exports.tsv. The real-ACT
    // baseline over the identical swings is produced by tools/act-oracle (<name>.oracle.exports.tsv);
    // MassCompare --exports-diff diffs the two. This is the corpus version of
    // Fct.Compat.Act.Tests/ExportVarsCompatTests: plugin swings -> our engine, asserted == real ACT.
    //
    // The plugin is NOT loaded in this mode — parsing was already done to produce the oracle.tsv.
    // We exercise only the consumer (our engine), so it runs without a game, plugin, or message pump.
    internal static class EngineAggregator
    {
        public static void Run(string folder)
        {
            string logPath = Path.Combine(folder, "_mass-engine-exports.log");
            void Log(string s) { try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { } }
            try
            {
                InstallTables();
                var files = Directory.GetFiles(folder, "*.oracle.tsv")
                                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
                Log($"mass-engine-exports: {files.Length} oracle stream(s) in {folder}");
                int done = 0;
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    name = name.Substring(0, name.Length - ".oracle.tsv".Length);
                    int swings = AggregateAndDump(f, Path.Combine(folder, name + ".engine.exports.tsv"));
                    Log($"  [{++done}/{files.Length}] {name}: swings={swings}");
                }
                Log("mass-engine-exports done");
            }
            catch (Exception ex) { Log("FATAL: " + ex); }
        }

        // Feed one 9-col timed swing stream (<name>.oracle.tsv) through a fresh encounter in our
        // engine and write the ExportVariables payload. Returns the swing count.
        private static int AggregateAndDump(string inTsv, string exportsOut)
        {
            var zone = new ZoneData { ZoneName = "" };
            var enc = new EncounterData("YOU", "", zone) { Active = true };
            zone.ActiveEncounter = enc;
            zone.Items.Add(enc);

            int n = 0;
            foreach (var line in File.ReadLines(inTsv))
            {
                if (n++ == 0 && line.StartsWith("swingType")) continue;
                var c = line.Split('\t');
                if (c.Length < 9) continue;
                int swingType = int.Parse(c[0], CultureInfo.InvariantCulture);
                bool crit = c[1] == "1";
                long dmg = long.Parse(c[2], CultureInfo.InvariantCulture);
                string special = c[3], attackType = c[4], attacker = c[5], damageType = c[6], victim = c[7];
                var time = DateTime.Parse(c[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                enc.AddCombatAction(new MasterSwing(swingType, crit, special, new Dnum(dmg), time, n, attackType, attacker, damageType, victim));
            }

            using (var w = new StreamWriter(exportsOut))
            {
                w.WriteLine("name\tkey\tvalue");
                foreach (var cd in enc.Items.Values)
                    foreach (var key in ExportKeys)
                    {
                        if (!CombatantData.ExportVariables.TryGetValue(key, out var fmt)) continue;
                        string val;
                        try { val = fmt.GetExportString(cd, ""); }
                        catch { continue; }   // keys needing a live form (NAME/crittypes/threat*) — match ActOracle's skip
                        if (val == null || val == "ERROR" || val.StartsWith("<EX:")) continue;
                        if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                        w.WriteLine(cd.Name + "\t" + key + "\t" + val);
                    }

                // Encounter-level ExportVariables (the cactbot "Encounter" object) through OUR engine,
                // under the *ENCOUNTER* sentinel — diffed against ActOracle's real-ACT *ENCOUNTER* rows.
                var allies = enc.GetAllies();
                foreach (var key in EncounterExportKeys)
                {
                    if (!EncounterData.ExportVariables.TryGetValue(key, out var fmt)) continue;
                    string val;
                    try { val = fmt.GetExportString(enc, allies, ""); }
                    catch { continue; }
                    if (val == null || val == "ERROR" || val.StartsWith("<EX:")) continue;
                    if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                    w.WriteLine("*ENCOUNTER*\t" + key + "\t" + val);
                }
            }
            return n;
        }

        // The exact ExportVariables key set tools/act-oracle dumps, so the two payloads join 1:1 in
        // the diff. Kept identical to ActOracle.cs.
        private static readonly string[] ExportKeys =
        {
            "name", "duration", "DURATION",
            "damage", "damage-m", "damage-b", "damage-*", "DAMAGE-k", "DAMAGE-m", "DAMAGE-b", "DAMAGE-*", "damage%",
            "dps", "dps-*", "DPS", "DPS-k", "DPS-m", "DPS-*",
            "encdps", "encdps-*", "ENCDPS", "ENCDPS-k", "ENCDPS-m", "ENCDPS-*",
            "hits", "crithits", "crithit%", "misses", "hitfailed", "swings", "tohit", "TOHIT",
            "maxhit", "MAXHIT", "maxhit-*", "MAXHIT-*",
            "healed", "healed%", "enchps", "enchps-*", "ENCHPS", "ENCHPS-k", "ENCHPS-m", "ENCHPS-*",
            "critheals", "critheal%", "heals", "cures",
            "maxheal", "MAXHEAL", "maxhealward", "MAXHEALWARD", "maxheal-*", "MAXHEAL-*", "maxhealward-*", "MAXHEALWARD-*",
            "damagetaken", "damagetaken-*", "healstaken", "healstaken-*",
            "powerdrain", "powerdrain-*", "powerheal", "powerheal-*",
            "kills", "deaths",
        };

        // The ACT-core encounter ExportVariables keys that produce a real value. Kept identical to
        // tools/act-oracle ActOracle.EncounterKeys so the two *ENCOUNTER* payloads join 1:1 in the diff.
        private static readonly string[] EncounterExportKeys =
        {
            "title", "duration", "DURATION",
            "damage", "damage-m", "damage-*", "DAMAGE-k", "DAMAGE-m", "DAMAGE-b", "DAMAGE-*",
            "dps", "DPS", "DPS-k", "DPS-*", "encdps", "encdps-*", "ENCDPS", "ENCDPS-k", "ENCDPS-m", "ENCDPS-*",
            "hits", "crithits", "crithit%", "misses", "hitfailed", "swings", "tohit", "TOHIT",
            "maxhit", "MAXHIT", "maxhit-*", "MAXHIT-*",
            "healed", "enchps", "enchps-*", "ENCHPS", "ENCHPS-k", "ENCHPS-m", "ENCHPS-*",
            "heals", "critheals", "critheal%", "cures",
            "maxheal", "MAXHEAL", "maxhealward", "MAXHEALWARD", "maxheal-*", "MAXHEAL-*", "maxhealward-*", "MAXHEALWARD-*",
            "damagetaken", "damagetaken-*", "healstaken", "healstaken-*", "powerdrain", "powerdrain-*", "powerheal", "powerheal-*",
            "kills", "deaths",
        };

        // The FFXIV damage-type routing tables ACT runs with (installed live by the plugin's
        // ACT_UIMods). Replay-only aggregation has no plugin to install them, so we set the same
        // state here. Held to the real ACT binary by Fct.Compat.Act.Tests/ExportVarsCompatTests.
        private static bool _installed;
        private static void InstallTables()
        {
            if (_installed) return;
            _installed = true;

            FormActMain.Log = _ => { };
            // Build the ACT form WITHOUT running the WinForms Form ctor (as tools/act-oracle does): a
            // fully-constructed Form in this long-running, message-loop-less batch process destabilises
            // over many large files. The export formatters need only its stateless CreateDamageString.
            // The bypassed ctor is what normally registers the ExportVariables, so invoke that
            // registration (CombatTables.Setup) explicitly.
            ActGlobals.oFormActMain = (FormActMain)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(FormActMain));
            typeof(EncounterData).Assembly.GetType("Advanced_Combat_Tracker.CombatTables")
                .GetMethod("Setup", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Invoke(null, null);
            ActGlobals.charName = "YOU";

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
