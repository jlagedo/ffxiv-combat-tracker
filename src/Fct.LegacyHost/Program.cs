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

        private static LoadedPlugin _ffxiv;
        private static LoadedPlugin _overlay;

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

            // End-to-end live route on a recorded log: plugin parse -> our ACT facade (with
            // idle-end encounter splitting) -> per-encounter ExportVariables.
            //   --replay <logPath> <maxLines> <outPath>
            int rpi = Array.IndexOf(args, "--replay");
            if (rpi >= 0 && args.Length >= rpi + 4)
            {
                ParseOracle.Replay(args[rpi + 1], int.Parse(args[rpi + 2]), args[rpi + 3]);
                return;
            }

            // Batch oracle over a whole log folder (months of logs), one plugin load:
            //   --mass-oracle <logFolder> <outFolder> [maxLinesPerFile]
            int mo = Array.IndexOf(args, "--mass-oracle");
            if (mo >= 0 && args.Length >= mo + 3)
            {
                int max = (args.Length >= mo + 4 && int.TryParse(args[mo + 3], out var m)) ? m : int.MaxValue;
                ParseOracle.MassOracle(args[mo + 1], args[mo + 2], max);
                return;
            }

            // Survey the plugin's resource tables (action category, status names, pet list):
            //   --introspect <outPath>
            int ii = Array.IndexOf(args, "--introspect");
            if (ii >= 0 && args.Length >= ii + 2)
            {
                ParseOracle.Introspect(args[ii + 1]);
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

            // Dump the internal game-data tables (action categories, status names) the native
            // parser needs:  --dump-tables <outFolder>
            int dt = Array.IndexOf(args, "--dump-tables");
            if (dt >= 0 && args.Length >= dt + 2)
            {
                ParseOracle.DumpTables(args[dt + 1]);
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

            _standalone = pipeName == null;

            // Load plugins once the message loop is running (some plugins poll via timers).
            ScheduleOnce(250, LoadPlugins);
            ScheduleOnce(3000, StartDispatcherDiagnostics);
            ScheduleOnce(12000, WriteSummary);

            Application.Run(new ApplicationContext());
        }

        private static bool _standalone;

        private static void LoadPlugins()
        {
            // Each plugin loads into its own borderless window; the host embeds whichever the
            // user selects, so a plugin's window shows only its own configuration tabs.
            Log("loading FFXIV_ACT_Plugin (wrapped) from: " + FacadeHost.FfxivPluginPath);
            _ffxiv = FacadeHost.LoadWrappedFfxivPlugin(FacadeHost.FfxivPluginPath);
            SendLine($"HWND {_ffxiv.Hwnd.ToInt64():X}");   // primary window (bridge-handshake compat)
            SendPlugin(_ffxiv);

            Log("loading OverlayPlugin from: " + FacadeHost.OverlayPluginPath);
            _overlay = FacadeHost.LoadPlugin("overlay", "OverlayPlugin", FacadeHost.OverlayPluginPath, null);
            SendPlugin(_overlay);

            SendLine("PLUGINS-END");
            Log($"loaded {2} plugin window(s): {_ffxiv.Title}=0x{_ffxiv.Hwnd.ToInt64():X}, " +
                $"{_overlay.Title}=0x{_overlay.Hwnd.ToInt64():X}");

            if (_standalone)
                foreach (var p in new[] { _ffxiv, _overlay })
                {
                    p.Window.FormBorderStyle = FormBorderStyle.Sizable;
                    p.Window.StartPosition = FormStartPosition.WindowsDefaultLocation;
                    p.Window.Text = p.Title;
                    p.Window.Show();
                }

            // Drive ACT's idle-end off the live log stream: the FFXIV plugin raises OnLogLineRead for
            // every parsed line, so advancing the clock per line splits combat into per-pull
            // encounters (matching ACT). OverlayPlugin reads ActiveZone.ActiveEncounter, so this is
            // what gives a live, per-encounter DPS instead of one ever-growing all-time encounter.
            ActGlobals.oFormActMain.OnLogLineRead += (isImport, args) =>
            {
                if (args.detectedTime > DateTime.MinValue)
                    ActGlobals.oFormActMain.AdvanceClock(args.detectedTime);
            };

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

        // Ring-buffer dispatcher health, asserted by the Tier 2 integration test. Proves the
        // type-identity unification held (one Common loaded) and the real OverlayPlugin discovered
        // our wrapper and bound onto the ring (ProcessChanged), then smoke-injects synthetic packets.
        // Polls because OverlayPlugin binds on a background Task after InitPlugin returns — emit the
        // definitive line once it has bound (or after a deadline). NetworkReceived stays 0 with no
        // game (OverlayPlugin gates packet capture on a live FFXIV process — see the Tier 3 path).
        private static void StartDispatcherDiagnostics()
        {
            int ticks = 0;
            var t = new Timer { Interval = 1000 };
            t.Tick += (s, e) =>
            {
                ticks++;
                var wrapper = _ffxiv?.Data?.pluginObj as Fct.Parser.Legacy.WrappedFfxivPlugin;
                int bound = wrapper?.ProcessChangedSubscriberCount ?? 0;
                if (bound > 0 || ticks >= 40)
                {
                    t.Stop(); t.Dispose();
                    EmitDispatcherDiagnostics(wrapper);
                }
            };
            t.Start();
        }

        private static void EmitDispatcherDiagnostics(Fct.Parser.Legacy.WrappedFfxivPlugin wrapper)
        {
            try
            {
                int commonCopies = AppDomain.CurrentDomain.GetAssemblies()
                    .Count(a => a.GetName().Name == "FFXIV_ACT_Plugin.Common");
                Log($"[Diag] FFXIV_ACT_Plugin.Common loaded copies={commonCopies}");

                if (wrapper == null) { Log("[Diag] wrapper missing (pluginObj is not WrappedFfxivPlugin)"); return; }

                Log($"[Diag] OverlayPlugin bound to ring: ProcessChanged subscribers={wrapper.ProcessChangedSubscriberCount} " +
                    $"NetworkReceived subscribers={wrapper.NetworkReceivedSubscriberCount} (NetworkReceived is game-gated)");

                long before = wrapper.RawPackets.DroppedCount;
                for (int i = 0; i < 8; i++)
                    wrapper.RawPackets.InjectNetworkReceived("diag", i, new byte[64]);
                long dropped = wrapper.RawPackets.DroppedCount - before;
                Log($"[Diag] injected=8 dropped={dropped}");
            }
            catch (Exception ex) { Log("[Diag] FAILED: " + ex); }
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
            Log($"FFXIV status: '{_ffxiv?.Data?.lblPluginStatus?.Text}'");
            Log($"OverlayPlugin status: '{_overlay?.Data?.lblPluginStatus?.Text}'");
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

        // Announce a loaded plugin's embeddable window to the host:
        //   PLUGIN <key>|<hwndHex>|<status>|<title>
        private static void SendPlugin(LoadedPlugin p)
        {
            if (p == null) return;
            var status = (p.Status ?? "").Replace('|', '/');
            var title = (p.Title ?? "").Replace('|', '/');
            SendLine($"PLUGIN {p.Key}|{p.Hwnd.ToInt64():X}|{status}|{title}");
        }

        private static void Log(string s)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
        }
    }
}
