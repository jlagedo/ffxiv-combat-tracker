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
            var tabs = new TabControl();
            var ffxiv = FacadeHost.LoadPlugin(tabs, "FFXIV_ACT_Plugin",
                FacadeHost.FfxivPluginPath, "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin");
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
