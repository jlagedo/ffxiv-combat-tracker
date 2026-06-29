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
// encounter aggregates. That output is the gold baseline our clean-room Fct.Compat.Act facade
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

    // Mirrors FFXIV_ACT_Plugin ACT_UIMods damage-type registration.
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

    private static string F(double d) { return d.ToString("0.##", CultureInfo.InvariantCulture); }
    private static string I(long d) { return d.ToString(CultureInfo.InvariantCulture); }

    private static void Body(string[] argv)
    {
        try
        {
            string inTsv = argv[0], outTsv = argv[1];
            ActGlobals.oFormActMain = (FormActMain)FormatterServices.GetUninitializedObject(typeof(FormActMain));
            ActGlobals.charName = "YOU";

            // Seed ACT's default English localization (Trans["attackTypeTerm-all"] = "All", etc.).
            // Without it the "All" AttackType bucket is keyed by the fallback string and the
            // literal-"All" lookups (CritHits, Deaths, Kills, MaxHit) silently return 0.
            var loc = typeof(ActGlobals).GetNestedType("ActLocalization");
            loc.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

            RegisterTables();

            var zone = new ZoneData(DateTime.Now, "", true, false, false);
            var enc = new EncounterData("YOU", "", zone);
            enc.Active = true;
            zone.ActiveEncounter = enc;

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
                n++;
            }

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
            // Optional: dump the real ACT ExportVariables strings OverlayPlugin/cactbot read. The
            // ExportVariables formatters are thin wrappers over FormActMain.CombatantFormatSwitch,
            // which we call directly (the full env-setup method touches UI state and can't run on an
            // uninitialized form). These are the exact strings cactbot would receive.
            if (argv.Length >= 3)
            {
                var fmt = typeof(FormActMain).GetMethod("CombatantFormatSwitch",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                string[] keys =
                {
                    "name", "duration", "DURATION", "encdps", "ENCDPS", "dps", "DPS",
                    "damage", "damage%", "healed", "healed%", "enchps", "ENCHPS",
                    "hits", "crithits", "crithit%", "critheal%", "critheals", "heals",
                    "misses", "swings", "maxhit", "deaths", "kills",
                };
                using (var w = new StreamWriter(argv[2]))
                {
                    w.WriteLine("name\tkey\tvalue");
                    foreach (var cd in enc.Items.Values)
                        foreach (var key in keys)
                        {
                            string val;
                            try { val = (string)fmt.Invoke(ActGlobals.oFormActMain, new object[] { cd, key, "" }); }
                            catch (Exception ex) { var x = ex; while (x.InnerException != null) x = x.InnerException; val = "<EX:" + x.GetType().Name + ">"; }
                            w.WriteLine(cd.Name + "\t" + key + "\t" + val);
                        }
                }
            }

            _result = "OK combatants=" + enc.Items.Count + " swings=" + n;
        }
        catch (Exception ex)
        {
            var x = ex;
            while (x.InnerException != null) x = x.InnerException;
            _result = "EX=" + x.GetType().FullName + ": " + x.Message + "\n" + x.StackTrace;
        }
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
        if (!th.Join(120000)) { Console.WriteLine("TIMEOUT"); return 1; }
        Console.WriteLine(_result);
        return _result.StartsWith("OK") ? 0 : 1;
    }
}
