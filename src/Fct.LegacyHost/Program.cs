using System;
using System.IO;
using System.IO.Pipes;
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

        [STAThread]
        private static void Main(string[] args)
        {
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
            ScheduleOnce(6000, WriteSummary);

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
