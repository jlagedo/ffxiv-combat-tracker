using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Advanced_Combat_Tracker;

// ACT-layer differential oracle. Loads the REAL Advanced Combat Tracker binary, registers the
// exact damage-type routing tables the FFXIV plugin installs at runtime, replays a captured
// MasterSwing stream (the real plugin's authoritative parse, e.g. combat-slice.oracle.tsv)
// through the real EncounterData/CombatantData aggregation, and dumps the per-combatant and
// encounter aggregates. That output is the gold baseline our from-scratch Fct.Compat.Act facade
// is held to (see Fct.Compat.Act.Tests/AggregateCompatTests).
//
// The real FormActMain is never run; ActGlobals.oFormActMain is an uninitialized instance used
// only as the WriteDebugLog/SelectiveListGetSelected sink. ACT's default English localization
// is seeded so the "All" AttackType bucket is keyed correctly.
internal static class ActOracle
{
    private static string _result = "";
    private static string _actDir =
        Environment.GetEnvironmentVariable("ACT_DIR") ?? @"E:\dev\Advanced Combat Tracker";
    private static string _pluginDll = Environment.GetEnvironmentVariable("FFXIV_PLUGIN_DLL");

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        var simple = new AssemblyName(e.Name).Name;
        var exe = Path.Combine(_actDir, simple + ".exe");
        var dll = Path.Combine(_actDir, simple + ".dll");
        if (File.Exists(exe)) return Assembly.LoadFrom(exe);
        if (File.Exists(dll)) return Assembly.LoadFrom(dll);
        return null;
    }

    private static CombatantData.DamageTypeDef Def(string label, int ally, Color color)
    {
        return new CombatantData.DamageTypeDef(label, ally, color);
    }

    // Registers the damage-type routing tables the real ACT binary runs with for FFXIV, so the
    // captured aggregate baseline reflects ACT's own behavior.
    private static void RegisterTables()
    {
        // ACT sets these in FormActMain environment setup (which GetUninitializedObject bypasses).
        CombatantData.DamageTypeDataNonSkillDamage = "Auto-Attack (Out)";
        CombatantData.DamageTypeDataOutgoingDamage = "Outgoing Damage";
        CombatantData.DamageTypeDataOutgoingHealing = "Healed (Out)";
        CombatantData.DamageTypeDataIncomingDamage = "Incoming Damage";
        CombatantData.DamageTypeDataIncomingHealing = "Healed (Inc)";
        CombatantData.DamageTypeDataOutgoingPowerReplenish = "Power Replenish (Out)";
        CombatantData.DamageTypeDataOutgoingPowerDamage = "Power Drain (Out)";
        CombatantData.DamageTypeDataOutgoingCures = "Cure/Dispel (Out)";

        CombatantData.IncomingDamageTypeDataObjects = new Dictionary<string, CombatantData.DamageTypeDef>
        {
            {"Simulated DoTs (Inc)", Def("Simulated DoTs (Inc)", -1, Color.Red)},
            {"Incoming Damage", Def("Incoming Damage", -1, Color.Red)},
            {"Damage Shields (Inc)", Def("Damage Shields (Inc)", 1, Color.YellowGreen)},
            {"Simulated HoTs (Inc)", Def("Simulated HoTs (Inc)", 1, Color.GreenYellow)},
            {"Healed (Inc)", Def("Healed (Inc)", 1, Color.LimeGreen)},
            {"Other (Inc)", Def("Other (Inc)", 0, Color.Lime)},
            {"Status (Inc)", Def("Status (Inc)", 0, Color.Wheat)},
            {"Power Drain (Inc)", Def("Power Drain (Inc)", -1, Color.Magenta)},
            {"Power Replenish (Inc)", Def("Power Replenish (Inc)", 1, Color.MediumPurple)},
            {"Cure/Dispel (Inc)", Def("Cure/Dispel (Inc)", 0, Color.Wheat)},
            {"Threat (Inc)", Def("Threat (Inc)", -1, Color.Yellow)},
            {"All Incoming (Ref)", Def("All Incoming (Ref)", 0, Color.Black)},
        };
        CombatantData.OutgoingDamageTypeDataObjects = new Dictionary<string, CombatantData.DamageTypeDef>
        {
            {"Auto-Attack (Out)", Def("Auto-Attack (Out)", -1, Color.DarkGoldenrod)},
            {"Skill/Ability (Out)", Def("Skill/Ability (Out)", -1, Color.DarkOrange)},
            {"Simulated DoTs (Out)", Def("Simulated DoTs (Out)", -1, Color.OrangeRed)},
            {"Outgoing Damage", Def("Outgoing Damage", 0, Color.Orange)},
            {"Damage Shields (Out)", Def("Damage Shields (Out)", 1, Color.LightSkyBlue)},
            {"Simulated HoTs (Out)", Def("Simulated HoTs (Out)", 1, Color.LightBlue)},
            {"Healed (Out)", Def("Healed (Out)", 1, Color.Blue)},
            {"Other (Out)", Def("Other (Out)", 0, Color.Lime)},
            {"Status (Out)", Def("Status (Out)", 0, Color.Wheat)},
            {"Power Drain (Out)", Def("Power Drain (Out)", -1, Color.Purple)},
            {"Power Replenish (Out)", Def("Power Replenish (Out)", 1, Color.Violet)},
            {"Cure/Dispel (Out)", Def("Cure/Dispel (Out)", 0, Color.Wheat)},
            {"Threat (Out)", Def("Threat (Out)", -1, Color.Yellow)},
            {"All Outgoing (Ref)", Def("All Outgoing (Ref)", 0, Color.Black)},
        };
        CombatantData.SwingTypeToDamageTypeDataLinksOutgoing = new SortedDictionary<int, List<string>>
        {
            {0, new List<string>{"Auto-Attack (Out)", "Outgoing Damage"}},
            {1, new List<string>{"Other (Out)"}},
            {2, new List<string>{"Skill/Ability (Out)", "Outgoing Damage"}},
            {3, new List<string>{"Simulated DoTs (Out)", "Outgoing Damage"}},
            {4, new List<string>{"Healed (Out)"}},
            {5, new List<string>{"Simulated HoTs (Out)", "Healed (Out)"}},
            {6, new List<string>{"Power Drain (Out)"}},
            {7, new List<string>{"Power Replenish (Out)"}},
            {8, new List<string>{"Status (Out)"}},
            {9, new List<string>{"Cure/Dispel (Out)"}},
            {10, new List<string>{"Threat (Out)"}},
            {11, new List<string>{"Damage Shields (Out)", "Healed (Out)"}},
        };
        CombatantData.SwingTypeToDamageTypeDataLinksIncoming = new SortedDictionary<int, List<string>>
        {
            {0, new List<string>{"Incoming Damage"}},
            {1, new List<string>{"Other (Inc)"}},
            {2, new List<string>{"Incoming Damage"}},
            {3, new List<string>{"Simulated DoTs (Inc)", "Incoming Damage"}},
            {4, new List<string>{"Healed (Inc)"}},
            {5, new List<string>{"Simulated HoTs (Inc)", "Healed (Inc)"}},
            {6, new List<string>{"Power Drain (Inc)"}},
            {7, new List<string>{"Power Replenish (Inc)"}},
            {8, new List<string>{"Status (Inc)"}},
            {9, new List<string>{"Cure/Dispel (Inc)"}},
            {10, new List<string>{"Threat (Inc)"}},
            {11, new List<string>{"Damage Shields (Inc)", "Healed (Inc)"}},
        };
        CombatantData.DamageSwingTypes = new List<int> { 0, 2, 3 };
        CombatantData.HealingSwingTypes = new List<int> { 4, 5, 8, 9, 1 };
    }

    // Loads the real FFXIV_ACT_Plugin.dll into this AppDomain and invokes its own
    // ACT_UIMods.UpdateACTTables registration exactly as the plugin does at startup — the real
    // superset of what RegisterTables() hand-mirrors above, including the ExportVariables/ColumnDefs
    // keys ACT core doesn't ship (Job, ParryPct, Last10DPS, ...). showDebug:false so the 9 debug-only
    // MasterSwing columns are not added, matching production behavior.
    //
    // NAME CONSTRAINT: this method's name must contain "InitACT". UpdateACTTables ends by calling
    // ActGlobals.oFormActMain.ValidateLists()/ValidateTableSetup(), and both guard themselves with
    // `if (Environment.StackTrace.Contains("InitACT") && !Force) return;` (FormActMain.cs) to avoid
    // touching real WinForms controls our uninitialized oFormActMain doesn't have. Do not rename.
    private static void LoadPluginAndInitACT_UIMods()
    {
        if (string.IsNullOrEmpty(_pluginDll) || !File.Exists(_pluginDll))
            throw new FileNotFoundException(
                "FFXIV_ACT_Plugin.dll not found; set env FFXIV_PLUGIN_DLL to its path.", _pluginDll);

        var asm = Assembly.LoadFrom(_pluginDll);
        var uiMods = asm.GetType("FFXIV_ACT_Plugin.ACT_UIMods", throwOnError: true);
        var m = uiMods.GetMethod("UpdateACTTables", BindingFlags.Public | BindingFlags.Static);
        m.Invoke(null, new object[] { false });
    }

    private static string F(double d) { return d.ToString("0.##", CultureInfo.InvariantCulture); }
    private static string I(long d) { return d.ToString(CultureInfo.InvariantCulture); }

    private static MethodInfo _fmt;
    private static MethodInfo _encFmt;

    // The ACT-core encounter ExportVariables keys that produce a real value (the cactbot "Encounter"
    // object). The full registered set minus the control chars (n/t) and the two keys ACT registers
    // but whose switch echoes their own name (dps-*, DPS-m). Kept identical to
    // Fct.LegacyHost EngineAggregator.EncounterExportKeys so the two payloads join 1:1 in the diff.
    private static readonly string[] EncounterKeys =
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

    // One-time ACT setup: the uninitialized FormActMain sink, default English localization, the
    // FFXIV damage-type routing tables, and the CombatantFormatSwitch handle for ExportVariables.
    private static void SetupAct()
    {
        ActGlobals.oFormActMain = (FormActMain)FormatterServices.GetUninitializedObject(typeof(FormActMain));
        ActGlobals.charName = "YOU";
        // The encounter duration/DURATION arms take a wall-clock branch that dereferences the
        // uninitialized form's LastEstimatedTime (NRE headless). Our facade always renders the
        // swing-span duration (DurationS); pin ACT to the same non-wall-clock path for a clean,
        // deterministic baseline.
        ActGlobals.wallClockDuration = false;

        // Seed ACT's default English localization. Init() clears the table and loads one set
        // (helpPanel + attackTypeTerm-all, so the merged "All" AttackType bucket is keyed right);
        // AddPrebuild() loads the disjoint set the aggregation path looks up (data-dnum*,
        // encounterData-defaultEncounterName for the EncounterData ctor, specialAttackTerm*). BOTH
        // are required — any missing key makes ACT's localization get_Item warn via NotificationAdd,
        // which NREs on the uninitialized form. The two key sets are disjoint (no duplicate throw).
        var loc = typeof(ActGlobals).GetNestedType("ActLocalization");
        loc.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        loc.GetMethod("AddPrebuild", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        RegisterTables();
        _fmt = typeof(FormActMain).GetMethod("CombatantFormatSwitch",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _encFmt = typeof(FormActMain).GetMethod("EncounterFormatSwitch",
            BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static void Body(string[] argv)
    {
        try
        {
            SetupAct();
            if (argv[0] == "--plugin-baseline")
            {
                // Plugin-in-the-loop baseline: load the real FFXIV_ACT_Plugin.dll, install its own
                // ExportVariables/ColumnDefs registrations, then dump every key it registers (never a
                // hardcoded list) — the superset baseline ACT-core-only combat-slice.exportvars.tsv
                // can't produce. See docs/PIPELINE-COMPLETENESS-PLAN.md P1.1.
                LoadPluginAndInitACT_UIMods();
                int sw = PluginBaselineAndDump(argv[1], argv[2]);
                _result = "OK swings=" + sw;
            }
            else if (argv[0] == "--plugin-baseline-folder")
            {
                // Corpus-scale plugin-in-the-loop baseline (PIPELINE-COMPLETENESS-PLAN P5.9): load the
                // real FFXIV_ACT_Plugin.dll ONCE (its ExportVariables/ColumnDefs registrations are
                // static, process-wide), then replay every already-captured plugin swing stream
                // (<name>.oracle.tsv, produced by the satellite's --mass-oracle) through a fresh
                // real-ACT encounter and dump the FULL enumerated ExportVariables key set to
                // <name>.plugin.exports.tsv. This is the folder-batch sibling of --plugin-baseline —
                // same per-file replay (ReplaySwings + DumpAllExportVariables), just looped and with
                // the plugin loaded once up front instead of per file.
                LoadPluginAndInitACT_UIMods();
                string dir = argv[1];
                var files = new List<string>(Directory.GetFiles(dir, "*.oracle.tsv"));
                files.Sort(StringComparer.Ordinal);
                int done = 0;
                foreach (var f in files)
                {
                    string exportsOut = f.Substring(0, f.Length - ".oracle.tsv".Length) + ".plugin.exports.tsv";
                    int sw = PluginBaselineAndDump(f, exportsOut);
                    Console.WriteLine("  [" + (++done) + "/" + files.Count + "] " +
                        Path.GetFileName(f) + " swings=" + sw);
                }
                _result = "OK files=" + files.Count;
            }
            else if (argv[0] == "--folder")
            {
                // Batch: aggregate every captured plugin swing stream (*.oracle.tsv) through the real
                // ACT engine and dump each one's ExportVariables to <base>.oracle.exports.tsv — the
                // baseline our engine's <base>.engine.exports.tsv (satellite --mass-engine-exports) is
                // diffed against. The glob excludes the *.exports.tsv outputs themselves.
                string dir = argv[1];
                var files = new List<string>(Directory.GetFiles(dir, "*.oracle.tsv"));
                files.Sort(StringComparer.Ordinal);
                int done = 0;
                foreach (var f in files)
                {
                    string exportsOut = f.Substring(0, f.Length - 4) + ".exports.tsv";
                    int sw = AggregateAndDump(f, null, exportsOut);
                    Console.WriteLine("  [" + (++done) + "/" + files.Count + "] " +
                        Path.GetFileName(f) + " swings=" + sw);
                }
                _result = "OK files=" + files.Count;
            }
            else
            {
                int sw = AggregateAndDump(argv[0], argv[1], argv.Length >= 3 ? argv[2] : null);
                _result = "OK swings=" + sw;
            }
        }
        catch (Exception ex)
        {
            var x = ex;
            while (x.InnerException != null) x = x.InnerException;
            _result = "EX=" + x.GetType().FullName + ": " + x.Message + "\n" + x.StackTrace;
        }
    }

    // Replays one 9-col timed swing TSV into `enc` via the real EncounterData.AddCombatAction.
    // Returns the swing count; lastSwingTime is the last replayed swing's own timestamp (DateTime.MinValue
    // if the file had no data rows).
    private static int ReplaySwings(string inTsv, EncounterData enc, out DateTime lastSwingTime)
    {
        lastSwingTime = DateTime.MinValue;
        int n = 0;
        foreach (var line in File.ReadLines(inTsv))
        {
            if (n == 0 && line.StartsWith("swingType")) { n++; continue; }
            var c = line.Split('\t');
            int swingType = int.Parse(c[0]);
            bool crit = c[1] == "1";
            long dmg = long.Parse(c[2]);
            string special = c[3];
            string attackType = c[4];
            string attacker = c[5];
            string damageType = c[6];
            string victim = c[7];
            DateTime time = DateTime.Parse(c[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            enc.AddCombatAction(new MasterSwing(swingType, crit, special, (Dnum)dmg, time, n, attackType, attacker, damageType, victim));
            lastSwingTime = time;
            n++;
        }
        return n;
    }

    // Replay one 9-col timed swing TSV through a fresh real-ACT encounter; optionally dump the
    // per-combatant aggregate (aggOut) and the full ExportVariables payload (exportsOut). Returns
    // the swing count.
    private static int AggregateAndDump(string inTsv, string outTsv, string exportsOut)
    {
        {
            var zone = new ZoneData(DateTime.Now, "", true, false, false);
            var enc = new EncounterData("YOU", "", zone);
            enc.Active = true;
            zone.ActiveEncounter = enc;

            DateTime lastSwingTime;
            int n = ReplaySwings(inTsv, enc, out lastSwingTime);

            if (outTsv != null)
            using (var w = new StreamWriter(outTsv))
            {
                w.WriteLine("name\tDamage\tHealed\tDamageTaken\tHealsTaken\tHits\tCritHits\tSwings\tMisses\tCritDamPerc\tCritHealPerc\tHeals\tCritHeals\tEncDPS\tEncHPS\tMaxHit\tDeaths\tKills\tDamagePercent\tHealedPercent\tDurationSec");
                foreach (var cd in enc.Items.Values)
                {
                    AttackType atAll = cd.GetAttackType("All", CombatantData.DamageTypeDataOutgoingDamage);
                    long maxHit = atAll != null ? atAll.MaxHit : 0L;
                    w.WriteLine(string.Join("\t", new[]
                    {
                        cd.Name, I(cd.Damage), I(cd.Healed), I(cd.DamageTaken), I(cd.HealsTaken),
                        I(cd.Hits), I(cd.CritHits), I(cd.Swings), I(cd.Misses),
                        F(cd.CritDamPerc), F(cd.CritHealPerc), I(cd.Heals), I(cd.CritHeals),
                        F(cd.EncDPS), F(cd.EncHPS), I(maxHit), I(cd.Deaths), I(cd.Kills),
                        cd.DamagePercent, cd.HealedPercent, F(cd.Duration.TotalSeconds),
                    }));
                }
                w.WriteLine("*ENCOUNTER*\t" + string.Join("\t", new[]
                {
                    I(enc.Damage), I(enc.Healed), F(enc.DPS), F(enc.Duration.TotalSeconds),
                    I(enc.NumCombatants), I(enc.NumAllies),
                }));
            }
            // Dump the real ACT ExportVariables strings OverlayPlugin/cactbot read. The ExportVariables
            // formatters are thin wrappers over FormActMain.CombatantFormatSwitch, which we call
            // directly (the full env-setup method touches UI state and can't run on an uninitialized
            // form). These are the exact strings cactbot would receive.
            if (exportsOut != null)
            {
                // The ExportVariables key set OverlayPlugin's MiniParse iterates (ACT defaults). The
                // NAME{n}/crittypes/threat* keys are omitted: they call helpers that need a live
                // FormActMain and always throw on this headless harness (ACT logs the exception via
                // WriteExceptionLog), so they carry no comparable value and only add noise.
                string[] keys =
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
                using (var w = new StreamWriter(exportsOut))
                {
                    w.WriteLine("name\tkey\tvalue");
                    foreach (var cd in enc.Items.Values)
                        foreach (var key in keys)
                        {
                            string val;
                            try { val = (string)_fmt.Invoke(ActGlobals.oFormActMain, new object[] { cd, key, "" }); }
                            catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                            // Skip keys that error in this headless harness (NAME/crittypes/threat*
                            // call helpers that need a live form / threat data) — a harness artifact,
                            // not real ACT behaviour, so they are not part of the asserted baseline.
                            if (val == "ERROR" || val.StartsWith("<EX:")) continue;
                            // The literal "n"/"t" keys are newline/tab; skip control-char values
                            // (they would corrupt this TSV and carry no comparison value).
                            if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                            w.WriteLine(cd.Name + "\t" + key + "\t" + val);
                        }

                    // The encounter-level ExportVariables (the "Encounter" object OverlayPlugin builds),
                    // produced by ACT's EncounterFormatSwitch over the encounter's allies — emitted under
                    // the *ENCOUNTER* sentinel row so the diff treats them like any other row.
                    var allies = enc.GetAllies();
                    foreach (var key in EncounterKeys)
                    {
                        string val;
                        try { val = (string)_encFmt.Invoke(ActGlobals.oFormActMain, new object[] { enc, allies, key, "" }); }
                        catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                        if (val == "ERROR" || val.StartsWith("<EX:")) continue;
                        if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) continue;
                        w.WriteLine("*ENCOUNTER*\t" + key + "\t" + val);
                    }
                }
            }

            return n;
        }
    }

    // Enumerates every key CombatantData.ExportVariables/EncounterData.ExportVariables actually holds
    // — never a hardcoded key array — so a future plugin-registered key crosses into the baseline
    // automatically. Same "name\tkey\tvalue" / *ENCOUNTER* row shape as AggregateAndDump's exportsOut,
    // so a future consumer can parse both fixtures identically.
    private static void DumpAllExportVariables(EncounterData enc, string outPath)
    {
        int skipped = 0;
        using (var w = new StreamWriter(outPath))
        {
            w.WriteLine("name\tkey\tvalue");
            foreach (var cd in enc.Items.Values)
            {
                foreach (var kv in CombatantData.ExportVariables)
                {
                    string val;
                    try { val = kv.Value.GetExportString(cd, ""); }
                    catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                    if (val == null || val == "ERROR" || val.StartsWith("<EX:")) { skipped++; continue; }
                    if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) { skipped++; continue; }
                    w.WriteLine(cd.Name + "\t" + kv.Key + "\t" + val);
                }
            }

            var allies = enc.GetAllies();
            foreach (var kv in EncounterData.ExportVariables)
            {
                string val;
                try { val = kv.Value.GetExportString(enc, allies, ""); }
                catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                if (val == null || val == "ERROR" || val.StartsWith("<EX:")) { skipped++; continue; }
                if (val.IndexOf('\n') >= 0 || val.IndexOf('\t') >= 0 || val.IndexOf('\r') >= 0) { skipped++; continue; }
                w.WriteLine("*ENCOUNTER*\t" + kv.Key + "\t" + val);
            }
        }
        Console.WriteLine("  (plugin-baseline: " + CombatantData.ExportVariables.Count + " combatant keys, " +
            EncounterData.ExportVariables.Count + " encounter keys registered; " + skipped + " row(s) skipped)");
    }

    // Plugin-in-the-loop mode: replay swings through a fresh encounter with the real plugin's
    // ExportVariables/ColumnDefs tables installed (LoadPluginAndInitACT_UIMods, called by the caller
    // before this), then dump every registered key via enumeration. Returns the swing count.
    private static int PluginBaselineAndDump(string inTsv, string exportsOut)
    {
        var zone = new ZoneData(DateTime.Now, "", true, false, false);
        var enc = new EncounterData("YOU", "", zone);
        enc.Active = true;
        zone.ActiveEncounter = enc;

        DateTime lastSwingTime;
        int n = ReplaySwings(inTsv, enc, out lastSwingTime);
        // Last10/30/60DPS formatters read FormActMain.LastKnownTime at GetExportString-call time.
        // The public setter also restarts `estimatedTimer` (a Stopwatch field whose initializer never
        // ran on our constructor-bypassed instance) and would NRE, so set the backing field directly.
        typeof(FormActMain).GetField("lastKnownTime", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(ActGlobals.oFormActMain, lastSwingTime);
        DumpAllExportVariables(enc, exportsOut);
        return n;
    }

    [STAThread]
    private static int Main(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.Error.WriteLine("usage: ActOracle <swings.tsv> <out-aggregate.tsv>   (env ACT_DIR overrides ACT path)");
            return 2;
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var th = new Thread(() => Body(argv));
        th.SetApartmentState(ApartmentState.STA);
        th.IsBackground = true;
        th.Start();
        // Folder mode aggregates every *.oracle.tsv on the worker thread, so the watchdog must scale
        // to the corpus (a months-long single session is millions of swings); a single slice keeps
        // the original 2-minute budget.
        int timeoutMs = 120000;
        if (argv[0] == "--folder" || argv[0] == "--plugin-baseline-folder")
        {
            try { timeoutMs = Math.Max(120000, Directory.GetFiles(argv[1], "*.oracle.tsv").Length * 30000); }
            catch { timeoutMs = 3600000; }
        }
        if (!th.Join(timeoutMs)) { Console.WriteLine("TIMEOUT"); return 1; }
        Console.WriteLine(_result);
        return _result.StartsWith("OK") ? 0 : 1;
    }
}
