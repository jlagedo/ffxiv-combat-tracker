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

        public static void Introspect(string outPath) =>
            RunPumped(outPath, Log => IntrospectImpl(outPath, Log));

        // Survey the plugin's IDataRepository: its public methods, the ResourceType enum values,
        // and the type/size of every GetResourceDictionary(enum) — so we can find the FFXIV game-data
        // tables the native parser needs (action-category, status-name, pet). These are data, not logic.
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
        // file's captured MasterSwing stream is written to <outFolder>/<name>.oracle.tsv, plus a
        // manifest. This is the plugin's authoritative parse of months of logs — the shared input
        // both the real ACT binary and our engine aggregate for the corpus-scale parity diff
        // (tools/mass-compare).
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
            LoadStartedPlugin(Log); // plugin subscribes BeforeLogLineRead; state persists across files

            var rows = new List<string>(1 << 16);
            CombatActionDelegate handler = (isImport, info) =>
            {
                var s = info.combatAction;
                // 9th column (time) makes each oracle.tsv a valid ActOracle 9-col timed stream, so the
                // SAME real-ACT EncounterData/CombatantData aggregation that produced these swings can be
                // re-run for the overlay-payload (ExportVariables) diff. MassCompare's bag-diff reads only
                // the first 8 columns, so the extra column is inert there.
                rows.Add(string.Join("\t",
                    s.SwingType.ToString(CultureInfo.InvariantCulture),
                    s.Critical ? "1" : "0",
                    ((long)s.Damage).ToString(CultureInfo.InvariantCulture),
                    Clean(s.Special), Clean(s.AttackType), Clean(s.Attacker),
                    Clean(s.DamageType), Clean(s.Victim),
                    s.Time.ToString("o", CultureInfo.InvariantCulture)));
            };
            act.AfterCombatAction += handler;

            var files = Directory.GetFiles(logFolder, "Network_*.log")
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
            Log($"mass-oracle: {files.Length} file(s) from {logFolder}");
            const string header = "swingType\tcrit\tdamage\tspecial\tattackType\tattacker\tdamageType\tvictim\ttime";
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
