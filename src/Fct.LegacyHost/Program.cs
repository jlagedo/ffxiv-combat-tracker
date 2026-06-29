using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace Fct.LegacyHost
{
    // The net48 satellite. Stands up the ACT facade, loads the real plugins into WinForms
    // tabs inside a borderless host window, and hands that window's HWND to the Avalonia
    // host for embedding. Writes a verification log next to the executable.
    internal static class Program
    {
        private static NamedPipeClientStream _bridge;
        private static StreamWriter _writer;
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "s2-ffxiv.log");

        private static TabControl _tabs;
        private static ActPluginData _ffxiv;
        private static ActPluginData _overlay;

        [STAThread]
        private static void Main(string[] args)
        {
            // Oracle capture mode: replay a log through the real plugin and dump its parse.
            //   --parse-oracle <logPath> <maxLines> <outPath>
            int oi = Array.IndexOf(args, "--parse-oracle");
            if (oi >= 0 && args.Length >= oi + 4)
            {
                ParseOracle.Run(args[oi + 1], int.Parse(args[oi + 2]), args[oi + 3]);
                return;
            }

            // Dump the plugin's skill (action id -> name) table for a slice's action ids:
            //   --dump-skills <sliceLog> <outPath>
            int di = Array.IndexOf(args, "--dump-skills");
            if (di >= 0 && args.Length >= di + 3)
            {
                ParseOracle.DumpSkills(args[di + 1], args[di + 2]);
                return;
            }

            try { File.WriteAllText(LogPath, $"satellite start {DateTime.Now:HH:mm:ss}\n"); } catch { }
            FacadeHost.Log = Log;

            var pipeName = ParseBridgeArg(args);
            if (pipeName != null)
                ConnectBridge(pipeName);

            // Must be installed before any plugin assembly is loaded.
            FacadeHost.InstallAssemblyResolver();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The ACT facade (hidden form: handle + Invoke marshaling).
            FacadeHost.CreateAct();

            // The embeddable host window with the plugin tab strip.
            var hostForm = new Form
            {
                Text = "Fct.LegacyHost",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                Width = 700,
                Height = 460,
                BackColor = System.Drawing.Color.FromArgb(248, 250, 252),
            };
            _tabs = new TabControl { Dock = DockStyle.Fill };
            hostForm.Controls.Add(_tabs);

            var handle = hostForm.Handle;            // realize handle
            SendLine($"HWND {handle.ToInt64():X}");   // hand to host for reparenting

            // Load plugins once the message loop is running (some plugins poll via timers).
            ScheduleOnce(250, LoadPlugins);
            ScheduleOnce(12000, WriteSummary);

            if (pipeName != null)
                Application.Run(new ApplicationContext());
            else
            { hostForm.FormBorderStyle = FormBorderStyle.Sizable; Application.Run(hostForm); }

            GC.KeepAlive(hostForm);
        }

        private static void LoadPlugins()
        {
            Log("loading FFXIV_ACT_Plugin from: " + FacadeHost.FfxivPluginPath);
            _ffxiv = FacadeHost.LoadPlugin(_tabs, "FFXIV_ACT_Plugin",
                FacadeHost.FfxivPluginPath, "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin");
            Log("loading OverlayPlugin from: " + FacadeHost.OverlayPluginPath);
            _overlay = FacadeHost.LoadPlugin(_tabs, "OverlayPlugin",
                FacadeHost.OverlayPluginPath, null);
            Log($"embedded TabControl now has {_tabs.TabPages.Count} tab(s): " +
                string.Join(", ", _tabs.TabPages.Cast<TabPage>().Select(t => t.Text)));
            SelfTestAggregation();
        }

        // Deterministic S5 check: drive synthetic combat through the facade and read back
        // the clean-room aggregation + the export formatters OverlayPlugin/cactbot consume.
        private static void SelfTestAggregation()
        {
            try
            {
                var act = ActGlobals.oFormActMain;
                var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
                act.ChangeZone("Self-Test Zone");
                act.SetEncounter(t0, "Player One", "Striking Dummy");
                for (int i = 0; i < 10; i++) // 10 skill hits of 1000 over 9s; every 3rd crits
                    act.AddCombatAction(new MasterSwing(2, i % 3 == 0, "none", new Dnum(1000),
                        t0.AddSeconds(i), i, "Attack", "Player One", "physical", "Striking Dummy"));

                var enc = act.ActiveZone.ActiveEncounter;
                var cd = enc.GetCombatant("Player One");
                Log($"[SelfTest] Damage={cd.Damage} Hits={cd.Hits} Crit%={cd.CritDamPerc:0} " +
                    $"Duration={enc.Duration.TotalSeconds:0}s EncDPS={cd.EncDPS:0.0}");
                string Ev(string k) => CombatantData.ExportVariables.TryGetValue(k, out var f) ? f.GetExportString(cd, "") : "(missing)";
                Log($"[SelfTest] ExportVariables: encdps={Ev("encdps")} damage={Ev("damage")} " +
                    $"name={Ev("name")} crithit%={Ev("crithit%")}");
            }
            catch (Exception ex) { Log("[SelfTest] FAILED: " + ex); }
        }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                var ok = ar.AsyncWaitHandle.WaitOne(500);
                if (ok) c.EndConnect(ar);
                return ok && c.Connected;
            }
            catch { return false; }
        }

        private static void WriteSummary()
        {
            var act = ActGlobals.oFormActMain;
            var logFolder = Path.Combine(FacadeHost.AppData, "FFXIVLogs");
            string[] networkLogs = Array.Empty<string>();
            try { networkLogs = Directory.GetFiles(logFolder, "Network_*.log"); } catch { }
            var today = networkLogs.Length > 0
                ? Path.GetFileName(networkLogs[networkLogs.Length - 1])
                : "(none)";

            Log("==== SUMMARY ====");
            Log($"FFXIV status: '{_ffxiv?.lblPluginStatus?.Text}'");
            Log($"OverlayPlugin status: '{_overlay?.lblPluginStatus?.Text}'");
            Log($"WS server (10501) open: {IsPortOpen(10501)}");
            Log($"AddCombatAction={act.AddCombatActionCount} SetEncounter={act.SetEncounterCount} " +
                $"ChangeZone={act.ChangeZoneCount} InCombat={act.InCombat} Zone='{act.CurrentZone}'");
            Log($"Network_*.log count={networkLogs.Length} latest={today}");
        }

        private static void ScheduleOnce(int ms, Action action)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); try { action(); } catch (Exception ex) { Log("tick error: " + ex); } };
            t.Start();
        }

        private static string ParseBridgeArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--bridge")
                    return args[i + 1];
            return null;
        }

        private static void ConnectBridge(string pipeName)
        {
            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine($"READY pid={System.Diagnostics.Process.GetCurrentProcess().Id} " +
                         $"x64={Environment.Is64BitProcess} clr={Environment.Version}");
            }
            catch { _bridge = null; _writer = null; }
        }

        private static void SendLine(string s) { try { _writer?.WriteLine(s); } catch { } }

        private static void Log(string s)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
        }
    }
}
