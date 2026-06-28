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
        public static void Run(string logPath, int maxLines, string outPath)
        {
            void Log(string s) { try { File.AppendAllText(outPath + ".log", $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { } }
            Log($"oracle start log={logPath} max={maxLines}");

            FacadeHost.Log = Log;
            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();

            // The plugin's InitPlugin needs a live message loop (it marshals to the UI thread),
            // so run the capture from a timer tick after Application.Run starts — the same
            // pattern the normal satellite uses.
            var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                  WindowState = FormWindowState.Minimized, Visible = false };
            _ = pump.Handle;
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try { Capture(logPath, maxLines, outPath, Log); }
                catch (Exception ex) { Log("FATAL: " + ex); }
                finally { Environment.Exit(0); }
            };
            timer.Start();
            Application.Run(pump);
        }

        private static void Capture(string logPath, int maxLines, string outPath, Action<string> Log)
        {
            var act = ActGlobals.oFormActMain;
            var tabs = new TabControl();
            var ffxiv = FacadeHost.LoadPlugin(tabs, "FFXIV_ACT_Plugin",
                FacadeHost.FfxivPluginPath, "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin");

            // Let the plugin finish wiring its BeforeLogLineRead subscription.
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                if ((ffxiv.lblPluginStatus?.Text ?? "").IndexOf("Started", StringComparison.OrdinalIgnoreCase) >= 0)
                    break;
                Thread.Sleep(50);
            }
            Log($"plugin status='{ffxiv.lblPluginStatus?.Text}'");

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
