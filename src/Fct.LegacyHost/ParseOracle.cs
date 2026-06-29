using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace Fct.LegacyHost
{
    // Oracle capture: replay a real Network_*.log through the REAL FFXIV_ACT_Plugin and record
    // every MasterSwing it produces. The plugin subscribes to FormActMain.BeforeLogLineRead and
    // parses each line via its own ParseStrategy pipeline, calling AddCombatAction on our facade
    // synchronously. We capture those calls — that is ACT's authoritative parse, the ground
    // truth our native parser is diffed against.
    //
    // Output is a TSV: one row per MasterSwing, in emission order.
    internal static class ParseOracle
    {
        public static void Run(string logPath, int maxLines, string outPath) =>
            RunPumped(outPath, Log => Capture(logPath, maxLines, outPath, Log));

        public static void DumpSkills(string sliceLog, string outPath) =>
            RunPumped(outPath, Log => DumpSkillsImpl(sliceLog, outPath, Log));

        public static void Introspect(string outPath) =>
            RunPumped(outPath, Log => IntrospectImpl(outPath, Log));

        public static void DumpTables(string outFolder) =>
            RunPumped(Path.Combine(outFolder, "_dump-tables"), Log => DumpTablesImpl(outFolder, Log));

        // Resolve a plugin-internal service from its MinIoC container by interface name. The
        // service interfaces live across several costura-packed assemblies, so the Type is taken
        // from the container's own registration keys (the exact Types it can construct).
        private static object Resolve(object container, string typeName)
        {
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var reg = container.GetType().GetField("_registeredTypes", BF)?.GetValue(container)
                      as System.Collections.IDictionary;
            Type t = null;
            if (reg != null)
                foreach (System.Collections.DictionaryEntry e in reg)
                    if (e.Key is Type kt && kt.FullName == typeName) { t = kt; break; }
            if (t == null) return null;
            if (container is IServiceProvider sp) { var s = sp.GetService(t); if (s != null) return s; }
            var resolve = container.GetType().GetMethod("Resolve", new[] { typeof(Type) });
            return resolve?.Invoke(container, new object[] { t });
        }

        // Export the FFXIV game-data tables the native parser needs but the public DataRepository
        // does not expose: action id -> category (auto-attack / limit-break / spell / ability), and
        // status id -> name. Resolved from the plugin's internal IActionList / BuffList resources.
        private static void DumpTablesImpl(string outFolder, Action<string> Log)
        {
            Directory.CreateDirectory(outFolder);
            var ffxiv = LoadStartedPlugin(Log);
            var plugin = ffxiv.pluginObj;
            var repo = plugin.GetType().GetProperty("DataRepository")?.GetValue(plugin);
            var grd = repo.GetType().GetMethod("GetResourceDictionary");
            var rtType = grd.GetParameters()[0].ParameterType;

            System.Collections.IDictionary Res(string n) =>
                (System.Collections.IDictionary)grd.Invoke(repo, new[] { Enum.Parse(rtType, n) });

            var skills = Res("SkillList_EN");
            var buffs = Res("BuffList_EN");

            // statuses.full.tsv — id -> name (the AddStatusEffect attackType source).
            var statusRows = new List<string> { "statusId\tname" };
            foreach (System.Collections.DictionaryEntry de in buffs)
                statusRows.Add($"{Convert.ToUInt32(de.Key):X}\t{Clean(de.Value?.ToString() ?? "")}");
            File.WriteAllLines(Path.Combine(outFolder, "statuses.full.tsv"), statusRows);
            Log($"dumped {statusRows.Count - 1} statuses");

            // actions.full.tsv — id -> name + ActionCategory (for auto-attack/limit-break swing typing).
            var container = plugin.GetType().GetField("_iocContainer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(plugin);
            var actionList = Resolve(container, "FFXIV_ACT_Plugin.Resource.IActionList");
            Log($"IActionList resolved: {actionList?.GetType().FullName}");
            var getCat = actionList?.GetType().GetMethod("GetActionCategory");

            string Cat(uint id)
            {
                try { return getCat?.Invoke(actionList, new object[] { id })?.ToString() ?? ""; }
                catch { return ""; }
            }

            var names = new Dictionary<uint, string>();
            var cats = new Dictionary<uint, string>();
            foreach (System.Collections.DictionaryEntry de in skills)
            {
                uint id = Convert.ToUInt32(de.Key);
                names[id] = Clean(de.Value?.ToString() ?? "");
                cats[id] = Cat(id);
            }
            // NPC auto-attacks and some abilities carry high action ids that are absent from the
            // display-name SkillList but still resolve a category (AutoAttack / LimitBreak) — those
            // drive swing typing, so scan the full id space and capture every typing-relevant one.
            for (uint id = 0; id < 0x80000u; id++)
            {
                if (cats.ContainsKey(id)) continue;
                var c = Cat(id);
                if (c == "AutoAttack" || c == "LimitBreak") cats[id] = c;
            }

            var allIds = new SortedSet<uint>(cats.Keys);
            var actionRows = new List<string> { "actionId\tname\tcategory" };
            foreach (var id in allIds)
                actionRows.Add($"{id:X}\t{(names.TryGetValue(id, out var nm) ? nm : "")}\t{cats[id]}");
            File.WriteAllLines(Path.Combine(outFolder, "actions.full.tsv"), actionRows);
            Log($"dumped {actionRows.Count - 1} actions ({cats.Values.Count(v => v == "AutoAttack")} auto) -> {outFolder}");

            DumpDefinitions(container, repo, grd, rtType, outFolder, Log);
        }

        // Export the DoT/HoT/shield/buff potency definitions and per-action potencies from the
        // plugin's IDefinitionRepository — the data the native simulator multiplies to reproduce
        // ACT's simulated swing amounts.
        private static void DumpDefinitions(object container, object repo, System.Reflection.MethodInfo grd,
            Type rtType, string outFolder, Action<string> Log)
        {
            var defRepo = Resolve(container, "FFXIV_ACT_Plugin.Resource.IDefinitionRepository");
            if (defRepo == null) { Log("IDefinitionRepository not resolved"); return; }
            var getStatus = defRepo.GetType().GetMethod("GetStatusEffectById");
            var getAction = defRepo.GetType().GetMethod("GetActionById");
            var buffs = (System.Collections.IDictionary)grd.Invoke(repo, new[] { Enum.Parse(rtType, "BuffList_EN") });
            var skills = (System.Collections.IDictionary)grd.Invoke(repo, new[] { Enum.Parse(rtType, "SkillList_EN") });

            string S(object o) => Clean(o?.ToString() ?? "");
            string Ids(object list) => list is System.Collections.IEnumerable en
                ? string.Join(",", en.Cast<object>().Select(x => Convert.ToUInt32(x).ToString("X"))) : "";

            // Serialize a Potency[] as type|amount|amountByte|isStacked|zone|cat|dmgType|actionIds;...
            string Potencies(object arr)
            {
                if (arr is not System.Collections.IEnumerable en) return "";
                var parts = new List<string>();
                foreach (var p in en)
                {
                    var T = p.GetType();
                    object P(string n) => T.GetProperty(n)?.GetValue(p);
                    parts.Add(string.Join("|", S(P("Type")), S(P("Amount")), S(P("AmountByte")),
                        S(P("IsStacked")), S(P("LimitToZoneId")), S(P("LimitToActionCategory")),
                        S(P("LimitToDamageType")), Ids(P("LimitToActionIds"))));
                }
                return string.Join(";", parts);
            }
            string Multipliers(object arr)
            {
                if (arr is not System.Collections.IEnumerable en) return "";
                var parts = new List<string>();
                foreach (var m in en)
                {
                    var T = m.GetType();
                    object P(string n) => T.GetProperty(n)?.GetValue(m);
                    parts.Add(string.Join("|", S(P("Type")), S(P("Amount")), Ids(P("LimitToActionIds"))));
                }
                return string.Join(";", parts);
            }

            var statusRows = new List<string> {
                "statusId\tname\ttpType\ttpPotency\ttpDamageType\ttpMaxTicks\tshieldType\tshieldAmount\tpotencyEffects\tmultipliers" };
            foreach (System.Collections.DictionaryEntry de in buffs)
            {
                uint sid = Convert.ToUInt32(de.Key);
                object def = null; try { def = getStatus.Invoke(defRepo, new object[] { sid }); } catch { }
                if (def == null) continue;
                var T = def.GetType();
                object tp = T.GetProperty("TimeProc")?.GetValue(def);
                object sh = T.GetProperty("DamageShield")?.GetValue(def);
                object pe = T.GetProperty("PotencyEffects")?.GetValue(def);
                object mu = T.GetProperty("Multipliers")?.GetValue(def);
                string tpType = tp != null ? S(tp.GetType().GetProperty("Type")?.GetValue(tp)) : "";
                string tpPot = tp != null ? S(tp.GetType().GetProperty("Potency")?.GetValue(tp)) : "";
                string tpDmg = tp != null ? S(tp.GetType().GetProperty("DamageType")?.GetValue(tp)) : "";
                string tpMax = tp != null ? S(tp.GetType().GetProperty("MaxTicks")?.GetValue(tp)) : "";
                string shType = sh != null ? S(sh.GetType().GetProperty("Type")?.GetValue(sh)) : "";
                string shAmt = sh != null ? S(sh.GetType().GetProperty("Amount")?.GetValue(sh)) : "";
                // Skip statuses with nothing the simulator uses, to keep the file lean.
                if (tp == null && sh == null && string.IsNullOrEmpty(Potencies(pe)) && string.IsNullOrEmpty(Multipliers(mu)))
                    continue;
                statusRows.Add(string.Join("\t", sid.ToString("X"), S(de.Value), tpType, tpPot, tpDmg, tpMax,
                    shType, shAmt, Potencies(pe), Multipliers(mu)));
            }
            File.WriteAllLines(Path.Combine(outFolder, "status-defs.tsv"), statusRows);
            Log($"dumped {statusRows.Count - 1} status defs");

            // Per-action base potency for the per-hit calibration: GetDamagePotency(targetIndex, combo).
            var actionRows = new List<string> { "actionId\tpot0\tpot0combo" };
            if (getAction != null)
            {
                foreach (System.Collections.DictionaryEntry de in skills)
                {
                    uint aid = Convert.ToUInt32(de.Key);
                    object adef = null; try { adef = getAction.Invoke(defRepo, new object[] { aid }); } catch { }
                    if (adef == null) continue;
                    var gdp = adef.GetType().GetMethod("GetDamagePotency");
                    if (gdp == null) continue;
                    object p0 = null, p0c = null;
                    try { p0 = gdp.Invoke(adef, new object[] { 0, false }); } catch { }
                    try { p0c = gdp.Invoke(adef, new object[] { 0, true }); } catch { }
                    if (p0 == null && p0c == null) continue;
                    actionRows.Add($"{aid:X}\t{S(p0)}\t{S(p0c)}");
                }
            }
            File.WriteAllLines(Path.Combine(outFolder, "action-potency.tsv"), actionRows);
            Log($"dumped {actionRows.Count - 1} action potencies -> {outFolder}");
        }

        // Survey the plugin's IDataRepository: its public methods, the ResourceType enum values,
        // and the type/size of every GetResourceDictionary(enum) — so we can find the action-category,
        // status-name, and pet tables the native parser needs to mirror ACT's swing construction.
        private static void IntrospectImpl(string outPath, Action<string> Log)
        {
            var ffxiv = LoadStartedPlugin(Log);
            var plugin = ffxiv.pluginObj;
            var repo = plugin.GetType().GetProperty("DataRepository")?.GetValue(plugin);
            var rows = new System.Collections.Generic.List<string>();
            rows.Add("== DataRepository type: " + repo?.GetType().FullName);
            foreach (var m in repo.GetType().GetMethods().OrderBy(m => m.Name))
                rows.Add("method\t" + m.ReturnType.Name + " " + m.Name + "(" +
                    string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");

            var grd = repo.GetType().GetMethod("GetResourceDictionary");
            var rtType = grd?.GetParameters()[0].ParameterType;
            if (rtType != null && rtType.IsEnum)
            {
                rows.Add("== ResourceType enum: " + rtType.FullName);
                foreach (var name in Enum.GetNames(rtType))
                {
                    try
                    {
                        var d = (System.Collections.IDictionary)grd.Invoke(repo, new[] { Enum.Parse(rtType, name) });
                        object firstK = null, firstV = null;
                        foreach (System.Collections.DictionaryEntry e in d) { firstK = e.Key; firstV = e.Value; break; }
                        rows.Add($"resource\t{name}\tcount={d?.Count}\tkey={firstK?.GetType().Name}\tval={firstV?.GetType().FullName}\tsample={firstK}={firstV}");
                    }
                    catch (Exception ex) { rows.Add($"resource\t{name}\tERROR={ex.GetType().Name}:{ex.Message}"); }
                }
            }

            // Map the plugin object's own fields/properties to locate the internal IoC container
            // (TinyIoC) and the resource services (IActionList / IDefinitionRepository / IStatusList)
            // that hold action categories and DoT/shield potency definitions — none of which the
            // public DataRepository exposes.
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            rows.Add("== pluginObj type: " + plugin.GetType().FullName);
            foreach (var f in plugin.GetType().GetFields(BF).OrderBy(f => f.Name))
            {
                object v = null; try { v = f.GetValue(plugin); } catch { }
                rows.Add($"plugin.field\t{f.FieldType.FullName}\t{f.Name}\t= {v?.GetType().FullName}");
            }
            foreach (var p in plugin.GetType().GetProperties(BF).OrderBy(p => p.Name))
                rows.Add($"plugin.prop\t{p.PropertyType.FullName}\t{p.Name}");

            // Find any field anywhere on the plugin that looks like a TinyIoC container, then dump
            // the registration types it holds so we can resolve the internal services by name.
            foreach (var f in plugin.GetType().GetFields(BF))
            {
                object v = null; try { v = f.GetValue(plugin); } catch { }
                if (v == null) continue;
                var tn = v.GetType().FullName ?? "";
                if (tn.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("TinyIoC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rows.Add("== CONTAINER candidate: " + f.Name + " : " + tn);
                    foreach (var cf in v.GetType().GetFields(BF))
                    {
                        object cv = null; try { cv = cf.GetValue(v); } catch { }
                        rows.Add($"  container.field\t{cf.FieldType.FullName}\t{cf.Name}\t= {cv?.GetType().FullName}");
                        if (cv is System.Collections.IDictionary cd)
                            foreach (System.Collections.DictionaryEntry e in cd)
                            { rows.Add($"    reg\t{e.Key}"); }
                    }
                }
            }
            File.WriteAllLines(outPath, rows);
            Log($"introspect -> {outPath} ({rows.Count} rows)");
        }

        // End-to-end live route on a recorded log: the real plugin parses each line and drives our
        // facade (SetEncounter/AddCombatAction); we advance the parse clock per line so ACT's
        // idle-end splits the stream into encounters, then dump each completed encounter's
        // ExportVariables — exactly the data OverlayPlugin/cactbot read live. Proves the whole
        // route works on recorded data, no live game required.
        public static void Replay(string logPath, int maxLines, string outPath) =>
            RunPumped(outPath, Log => ReplayImpl(logPath, maxLines, outPath, Log));

        // Batch oracle: load the real plugin ONCE, then run ACT's parse over every Network_*.log
        // in a folder, in chronological (filename) order, as one continuous stream — the plugin's
        // name/combat state carries across day-boundary log rotations exactly as ACT sees it. Each
        // file's captured MasterSwing stream is written to <outFolder>/<name>.oracle.tsv, plus the
        // full skill table (skills.full.tsv) and a manifest. This is ACT's authoritative parse of
        // months of logs, the ground truth the native parser is diffed against at scale.
        public static void MassOracle(string logFolder, string outFolder, int maxLines) =>
            RunPumped(Path.Combine(outFolder, "_mass-oracle"),
                Log => MassOracleImpl(logFolder, outFolder, maxLines, Log));

        // Stand up the facade + plugin under a live message loop (InitPlugin marshals to the UI
        // thread), then run the given work from a timer tick — the same pattern the satellite uses.
        private static void RunPumped(string outPath, Action<Action<string>> work)
        {
            void Log(string s) { try { File.AppendAllText(outPath + ".log", $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { } }

            FacadeHost.Log = Log;
            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();

            var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                  WindowState = FormWindowState.Minimized, Visible = false };
            _ = pump.Handle;
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try { work(Log); }
                catch (Exception ex) { Log("FATAL: " + ex); }
                finally { Environment.Exit(0); }
            };
            timer.Start();
            Application.Run(pump);
        }

        // Load the plugin and pump until it reports "Started".
        private static ActPluginData LoadStartedPlugin(Action<string> Log)
        {
            var ffxiv = FacadeHost.LoadPlugin("ffxiv", "FFXIV_ACT_Plugin",
                FacadeHost.FfxivPluginPath, "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin").Data;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                if ((ffxiv.lblPluginStatus?.Text ?? "").IndexOf("Started", StringComparison.OrdinalIgnoreCase) >= 0)
                    break;
                Thread.Sleep(50);
            }
            Log($"plugin status='{ffxiv.lblPluginStatus?.Text}'");
            return ffxiv;
        }

        // Dump the plugin's skill (action id -> name) table, filtered to the action ids that
        // appear in the slice, via reflection over IDataRepository.GetResourceDictionary. This is
        // FFXIV game data, independent of the oracle capture.
        private static void DumpSkillsImpl(string sliceLog, string outPath, Action<string> Log)
        {
            var ffxiv = LoadStartedPlugin(Log);

            var ids = new HashSet<uint>();
            foreach (var raw in File.ReadLines(sliceLog))
            {
                var f = raw.Split('|');
                if (f.Length > 5 && (f[0] == "21" || f[0] == "22") &&
                    uint.TryParse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                    ids.Add(id);
            }

            var plugin = ffxiv.pluginObj;
            var repo = plugin.GetType().GetProperty("DataRepository")?.GetValue(plugin);
            var method = repo?.GetType().GetMethod("GetResourceDictionary");
            var rtType = method?.GetParameters()[0].ParameterType;
            var skillList = rtType != null ? Enum.Parse(rtType, "SkillList_EN") : null;
            var dict = (System.Collections.IDictionary)method.Invoke(repo, new[] { skillList });

            var rows = new List<string>();
            foreach (var id in ids.OrderBy(x => x))
            {
                var name = dict.Contains(id) ? dict[id]?.ToString() : null;
                rows.Add($"{id:X}\t{(name ?? "")}");
            }
            File.WriteAllLines(outPath, new[] { "actionId\tname" }.Concat(rows));
            Log($"dumped {rows.Count} skill names -> {outPath}");
        }

        private static void Capture(string logPath, int maxLines, string outPath, Action<string> Log)
        {
            var act = ActGlobals.oFormActMain;
            LoadStartedPlugin(Log); // also wires the plugin's BeforeLogLineRead subscription

            var rows = new List<string>();
            CombatActionDelegate handler = (isImport, info) =>
            {
                var s = info.combatAction;
                rows.Add(string.Join("\t",
                    s.SwingType.ToString(CultureInfo.InvariantCulture),
                    s.Critical ? "1" : "0",
                    ((long)s.Damage).ToString(CultureInfo.InvariantCulture),
                    Clean(s.Special),
                    Clean(s.AttackType),
                    Clean(s.Attacker),
                    Clean(s.DamageType),
                    Clean(s.Victim),
                    s.Time.ToString("o", CultureInfo.InvariantCulture)));
            };
            act.AfterCombatAction += handler;

            int fed = 0;
            foreach (var raw in File.ReadLines(logPath).Take(maxLines))
            {
                if (raw.Length == 0) continue;
                fed++;
                var args = new LogLineEventArgs(raw, 0, ParseTimestamp(raw), act.CurrentZone ?? "", act.InCombat);
                try { act.FireBeforeLogLineRead(true, args); }
                catch (Exception ex) { Log($"line {fed} parse error: {ex.Message}"); }
            }
            act.AfterCombatAction -= handler;

            var header = "swingType\tcrit\tdamage\tspecial\tattackType\tattacker\tdamageType\tvictim\ttime";
            File.WriteAllLines(outPath, new[] { header }.Concat(rows));
            Log($"done fed={fed} captured={rows.Count} -> {outPath}");
        }

        private static void ReplayImpl(string logPath, int maxLines, string outPath, Action<string> Log)
        {
            var act = ActGlobals.oFormActMain;
            LoadStartedPlugin(Log); // plugin registers the damage-type tables + drives SetEncounter

            var rows = new List<string>();
            int encIdx = 0;
            string EV(CombatantData cd, string k) =>
                CombatantData.ExportVariables.TryGetValue(k, out var f) ? f.GetExportString(cd, "") : "";
            void DumpEncounter(EncounterData enc)
            {
                if (enc == null || enc.Items.Count == 0) return;
                encIdx++;
                foreach (var cd in enc.Items.Values)
                    rows.Add(string.Join("\t", encIdx.ToString(CultureInfo.InvariantCulture), Clean(cd.Name),
                        EV(cd, "encdps"), EV(cd, "damage"), EV(cd, "hits"), EV(cd, "crithit%"),
                        EV(cd, "healed"), EV(cd, "maxhit"), enc.DurationS));
            }

            CombatToggleEventDelegate onEnd = (imp, info) => DumpEncounter(info.encounter);
            act.OnCombatEnd += onEnd;

            int fed = 0;
            foreach (var raw in File.ReadLines(logPath).Take(maxLines))
            {
                if (raw.Length == 0) continue;
                fed++;
                var ts = ParseTimestamp(raw);
                if (ts > DateTime.MinValue) act.AdvanceClock(ts); // quiet gap > idle limit ends combat
                var args = new LogLineEventArgs(raw, 0, ts, act.CurrentZone ?? "", act.InCombat);
                try { act.FireBeforeLogLineRead(true, args); }
                catch (Exception ex) { Log($"line {fed} parse error: {ex.Message}"); }
            }
            if (act.InCombat) act.EndCombat(true); // flush the final encounter (dumped via OnCombatEnd)
            act.OnCombatEnd -= onEnd;

            var header = "encounter\tname\tencdps\tdamage\thits\tcrithit%\thealed\tmaxhit\tduration";
            File.WriteAllLines(outPath, new[] { header }.Concat(rows));
            Log($"done fed={fed} encounters={encIdx} rows={rows.Count} -> {outPath}");
        }

        private static void MassOracleImpl(string logFolder, string outFolder, int maxLines, Action<string> Log)
        {
            Directory.CreateDirectory(outFolder);
            var act = ActGlobals.oFormActMain;
            var ffxiv = LoadStartedPlugin(Log); // plugin subscribes BeforeLogLineRead; state persists across files

            // Full skill (action id -> name) table — FFXIV game data the native side uses to
            // resolve ability names for the damage-type/attack-type comparison.
            try { DumpFullSkills(ffxiv, Path.Combine(outFolder, "skills.full.tsv"), Log); }
            catch (Exception ex) { Log("skill dump failed: " + ex.Message); }

            var rows = new List<string>(1 << 16);
            CombatActionDelegate handler = (isImport, info) =>
            {
                var s = info.combatAction;
                rows.Add(string.Join("\t",
                    s.SwingType.ToString(CultureInfo.InvariantCulture),
                    s.Critical ? "1" : "0",
                    ((long)s.Damage).ToString(CultureInfo.InvariantCulture),
                    Clean(s.Special), Clean(s.AttackType), Clean(s.Attacker),
                    Clean(s.DamageType), Clean(s.Victim)));
            };
            act.AfterCombatAction += handler;

            var files = Directory.GetFiles(logFolder, "Network_*.log")
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
            Log($"mass-oracle: {files.Length} file(s) from {logFolder}");
            const string header = "swingType\tcrit\tdamage\tspecial\tattackType\tattacker\tdamageType\tvictim";
            var manifest = new List<string> { "file\tlines\tswings" };

            foreach (var file in files)
            {
                rows.Clear();
                int fed = 0;
                foreach (var raw in File.ReadLines(file).Take(maxLines))
                {
                    if (raw.Length == 0) continue;
                    fed++;
                    var argsE = new LogLineEventArgs(raw, 0, ParseTimestamp(raw), act.CurrentZone ?? "", act.InCombat);
                    try { act.FireBeforeLogLineRead(true, argsE); }
                    catch (Exception ex) { Log($"{Path.GetFileName(file)} line {fed}: {ex.Message}"); }
                }
                var outName = Path.GetFileNameWithoutExtension(file) + ".oracle.tsv";
                File.WriteAllLines(Path.Combine(outFolder, outName), new[] { header }.Concat(rows));
                manifest.Add($"{Path.GetFileName(file)}\t{fed}\t{rows.Count}");
                Log($"  {Path.GetFileName(file)}: fed={fed} swings={rows.Count}");
            }

            act.AfterCombatAction -= handler;
            File.WriteAllLines(Path.Combine(outFolder, "manifest.tsv"), manifest);
            Log("mass-oracle done");
        }

        // Dump the plugin's ENTIRE SkillList_EN (action id -> name) via reflection over
        // IDataRepository.GetResourceDictionary. Independent of the oracle capture (game data).
        private static void DumpFullSkills(ActPluginData ffxiv, string outPath, Action<string> Log)
        {
            var plugin = ffxiv.pluginObj;
            var repo = plugin.GetType().GetProperty("DataRepository")?.GetValue(plugin);
            var method = repo?.GetType().GetMethod("GetResourceDictionary");
            var rtType = method?.GetParameters()[0].ParameterType;
            var skillList = rtType != null ? Enum.Parse(rtType, "SkillList_EN") : null;
            var dict = (System.Collections.IDictionary)method.Invoke(repo, new[] { skillList });

            var rows = new List<string> { "actionId\tname" };
            foreach (System.Collections.DictionaryEntry de in dict)
                rows.Add($"{Convert.ToUInt32(de.Key):X}\t{Clean(de.Value?.ToString() ?? "")}");
            File.WriteAllLines(outPath, rows);
            Log($"dumped {rows.Count - 1} skills -> {outPath}");
        }

        private static DateTime ParseTimestamp(string raw)
        {
            int p = raw.IndexOf('|');
            int p2 = p >= 0 ? raw.IndexOf('|', p + 1) : -1;
            if (p >= 0 && p2 > p &&
                DateTime.TryParse(raw.Substring(p + 1, p2 - p - 1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts))
                return ts;
            return DateTime.MinValue;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ');
    }
}
