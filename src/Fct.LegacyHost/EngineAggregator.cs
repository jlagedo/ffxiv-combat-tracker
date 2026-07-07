using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Advanced_Combat_Tracker;

namespace Fct.LegacyHost
{
    // Corpus-scale ACT-engine parity. The real plugin (the sole parser) has already turned each
    // Network_*.log into a MasterSwing stream — captured by --mass-oracle as <name>.oracle.tsv.
    // Here we feed those SAME swings through OUR from-scratch Fct.Compat.Act aggregation and dump the
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

        // Corpus-scale plugin-in-the-loop parity: same replay as
        // Run/AggregateAndDump above (OUR Fct.Compat.Act engine, EngineTables.Install() already
        // registers the ported ACT_UIMods/G1 keys), but dumps the FULL enumerated
        // CombatantData/EncounterData.ExportVariables key set — never the hardcoded ACT-core-only
        // ExportKeys/EncounterExportKeys arrays above — to <name>.engine.full.exports.tsv. Diffed by
        // MassCompare against tools/act-oracle's <name>.plugin.exports.tsv (the real plugin-in-the-loop
        // baseline, ActOracle --plugin-baseline-folder), the corpus-scale sibling of
        // Fct.Engine.Tests/OracleParityTests.ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5.
        public static void RunFull(string folder)
        {
            string logPath = Path.Combine(folder, "_mass-engine-exports-full.log");
            void Log(string s) { try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { } }
            try
            {
                InstallTables();
                var files = Directory.GetFiles(folder, "*.oracle.tsv")
                                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
                Log($"mass-engine-exports-full: {files.Length} oracle stream(s) in {folder}");
                int done = 0;
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    name = name.Substring(0, name.Length - ".oracle.tsv".Length);
                    int swings = AggregateAndDumpFull(f, Path.Combine(folder, name + ".engine.full.exports.tsv"));
                    Log($"  [{++done}/{files.Length}] {name}: swings={swings}");
                }
                Log("mass-engine-exports-full done");
            }
            catch (Exception ex) { Log("FATAL: " + ex); }
        }

        // Same swing replay as AggregateAndDump, but dumps every registered ExportVariables key
        // (enumerated, never a hardcoded list) — the shape tools/act-oracle's DumpAllExportVariables
        // uses for the plugin-in-the-loop baseline, so the two TSVs join 1:1 on every key either side
        // registers.
        private static int AggregateAndDumpFull(string inTsv, string exportsOut)
        {
            var zone = new ZoneData { ZoneName = "" };
            var enc = new EncounterData("YOU", "", zone) { Active = true };
            zone.ActiveEncounter = enc;
            zone.Items.Add(enc);

            int n = 0;
            var lastSwingTime = DateTime.MinValue;
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
                lastSwingTime = time;
            }

            // LastNDPS (P5.6) reads AggregationGlobals.lastKnownTime at GetExportString-call time. The
            // uninitialized oFormActMain used by InstallTables() below has no live _lifecycle instance
            // (FormActMain's real ctor never ran), so the default accessor would NRE mid-enumeration and
            // silently drop these 6 keys (caught by the <EX:...> guard below) — a test-harness artifact,
            // not a real divergence, exactly the class of issue root-caused in the OverlaySatelliteTests
            // P5.6 verdict. Pin it to this replay's own last swing time (mirroring ActOracle's identical
            // per-file `lastKnownTime` backing-field set for --plugin-baseline) so the two sides compare
            // the SAME clock.
            AggregationGlobals.LastKnownTimeAccessor = () => lastSwingTime;

            using (var w = new StreamWriter(exportsOut))
            {
                w.WriteLine("name\tkey\tvalue");
                foreach (var cd in enc.Items.Values)
                {
                    foreach (var kv in CombatantData.ExportVariables)
                    {
                        string val;
                        try { val = kv.Value.GetExportString(cd, ""); }
                        catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                        if (val == null || val == "ERROR" || val.StartsWith("<EX:")) continue;
                        if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                        w.WriteLine(cd.Name + "\t" + kv.Key + "\t" + val);
                    }
                }

                var allies = enc.GetAllies();
                foreach (var kv in EncounterData.ExportVariables)
                {
                    string val;
                    try { val = kv.Value.GetExportString(enc, allies, ""); }
                    catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                    if (val == null || val == "ERROR" || val.StartsWith("<EX:")) continue;
                    if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                    w.WriteLine("*ENCOUNTER*\t" + kv.Key + "\t" + val);
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

        // Stand the engine up for replay: register ExportVariables + install the FFXIV damage-type
        // routing tables (the plugin's ACT_UIMods do this live; replay has no plugin). Shared with the
        // modern net10 engine via EngineTables.Install so the two runtimes aggregate identically.
        private static bool _installed;
        private static void InstallTables()
        {
            if (_installed) return;
            _installed = true;

            FormActMain.Log = _ => { };
            // Build the ACT form WITHOUT running the WinForms Form ctor (as tools/act-oracle does): a
            // fully-constructed Form in this long-running, message-loop-less batch process destabilises
            // over many large files. The export formatters render through the stateless
            // DamageString.Create, so an uninitialized form suffices as the oFormActMain global.
            ActGlobals.oFormActMain = (FormActMain)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(FormActMain));
            ActGlobals.charName = "YOU";

            EngineTables.Install();
        }
    }
}
