using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.LegacyHost.Logging;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.LegacyHost
{
    // The net48 satellite. Stands up the ACT facade, loads the real plugins into WinForms
    // tabs inside a borderless host window, and hands that window's HWND to the Avalonia
    // host for embedding. Logs through Serilog (rolling file + bridge forwarding + the
    // s2-ffxiv.log verification artifact); see SatelliteLogging.
    internal static class Program
    {
        private static NamedPipeClientStream _bridge;     // satellite -> host: handshake + logs
        private static StreamWriter _writer;
        private static readonly object _sendLock = new object();

        private static ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

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

            // Corpus-scale ACT-engine parity: aggregate every captured plugin swing stream
            // (<name>.oracle.tsv) through OUR Fct.Compat.Act engine and dump its ExportVariables to
            // <name>.engine.exports.tsv. Diffed against the real-ACT baseline (tools/act-oracle) by
            // the MassCompare comparer.  --mass-engine-exports <oracleFolder>
            int me = Array.IndexOf(args, "--mass-engine-exports");
            if (me >= 0 && args.Length >= me + 2)
            {
                EngineAggregator.Run(args[me + 1]);
                return;
            }

            SatelliteLogging.Initialize();
            _log = SatelliteLogging.Log;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Satellite starting (pid {Pid}, x64 {X64}, clr {Clr})",
                System.Diagnostics.Process.GetCurrentProcess().Id, Environment.Is64BitProcess, Environment.Version);

            // The ACT facade and plugin wrapper emit diagnostics through this Action<string>; route it
            // into the same pipeline (classifying by the legacy "[Tag]" prefix).
            FacadeHost.Log = SatelliteLogging.WriteLegacy;

            var pipeName = ParseBridgeArg(args);
            if (pipeName != null)
                ConnectBridge(pipeName);

            // Must be installed before any plugin assembly is loaded.
            FacadeHost.InstallAssemblyResolver();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The ACT facade (hidden form: handle + Invoke marshaling).
            FacadeHost.CreateAct();
            _log.LogDebug(LogEvents.FacadeCreated, "ACT facade created");

            _standalone = pipeName == null;

            // Load plugins once the message loop is running (some plugins poll via timers).
            ScheduleOnce(250, LoadPlugins);
            ScheduleOnce(3000, StartDispatcherDiagnostics);
            ScheduleOnce(12000, WriteSummary);
            ScheduleOnce(12500, StartCaptureHeartbeat);

            try
            {
                Application.Run(new ApplicationContext());
            }
            finally
            {
                SatelliteLogging.Shutdown();
            }
        }

        private static bool _standalone;

        private static void LoadPlugins()
        {
            // Each plugin loads into its own borderless window; the host embeds whichever the
            // user selects, so a plugin's window shows only its own configuration tabs.
            _log.LogInformation(LogEvents.PluginLoading,
                "Loading FFXIV_ACT_Plugin (wrapped) from {Path}", FacadeHost.FfxivPluginPath);
            _ffxiv = FacadeHost.LoadWrappedFfxivPlugin(FacadeHost.FfxivPluginPath);
            SendLine($"HWND {_ffxiv.Hwnd.ToInt64():X}");   // primary window (bridge-handshake compat)
            SendPlugin(_ffxiv);

            _log.LogInformation(LogEvents.PluginLoading,
                "Loading OverlayPlugin from {Path}", FacadeHost.OverlayPluginPath);
            _overlay = FacadeHost.LoadPlugin("overlay", "OverlayPlugin", FacadeHost.OverlayPluginPath, null);
            SendPlugin(_overlay);

            SendLine("PLUGINS-END");
            _log.LogInformation(LogEvents.PluginsReady,
                "Loaded 2 plugin window(s): {Ffxiv}=0x{FfxivHwnd:X}, {Overlay}=0x{OverlayHwnd:X}",
                _ffxiv.Title, _ffxiv.Hwnd.ToInt64(), _overlay.Title, _overlay.Hwnd.ToInt64());

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
                _log.LogInformation(LogEvents.SelfTest,
                    "[SelfTest] Damage={Damage} Hits={Hits} Crit%={Crit:0} Duration={Duration:0}s EncDPS={Dps:0.0}",
                    cd.Damage, cd.Hits, cd.CritDamPerc, enc.Duration.TotalSeconds, cd.EncDPS);
                string Ev(string k) => CombatantData.ExportVariables.TryGetValue(k, out var f) ? f.GetExportString(cd, "") : "(missing)";
                _log.LogInformation(LogEvents.SelfTest,
                    "[SelfTest] ExportVariables: encdps={Encdps} damage={Dmg} name={Name} crithit%={Crithit}",
                    Ev("encdps"), Ev("damage"), Ev("name"), Ev("crithit%"));
                // Anything the heartbeat sees above this baseline is real game-driven capture, not the
                // synthetic self-test swings.
                _captureBaseline = act.AddCombatActionCount;
            }
            catch (Exception ex) { _log.LogError(LogEvents.SelfTest, ex, "[SelfTest] FAILED"); }
        }

        // AddCombatActionCount immediately after the self-test; the live delta over it is real capture.
        private static int _captureBaseline;
        private static int _lastHeartbeatCount = int.MinValue;

        // Rolling live-capture readout. The one-shot WriteSummary fires before you are ever in combat,
        // so it can't show capture working; this ticks every few seconds while you play. The signal to
        // watch is `live` climbing above 0 (AddCombatAction past the self-test baseline) — that is the
        // Slice-1 acceptance bar (real MasterSwings reaching ACT). NetworkReceived is game-gated, so it
        // stays idle until FFXIV is running.
        private static void StartCaptureHeartbeat()
        {
            var t = new Timer { Interval = 5000 };
            t.Tick += (s, e) =>
            {
                try
                {
                    var act = ActGlobals.oFormActMain;
                    int total = act.AddCombatActionCount;
                    int live = total - _captureBaseline;
                    var wrapper = _ffxiv?.Data?.pluginObj as Fct.Parser.Legacy.WrappedFfxivPlugin;

                    // Quiet the log once capture is steady-idle: only emit when something changed or
                    // combat is live, so a long pre-pull wait doesn't spam identical lines.
                    bool changed = total != _lastHeartbeatCount;
                    _lastHeartbeatCount = total;
                    if (!changed && !act.InCombat) return;

                    _log.LogInformation(LogEvents.CaptureHeartbeat,
                        "[Capture] live AddCombatAction={Live} (total={Total}) SetEncounter={Enc} ChangeZone={Zones} " +
                        "InCombat={InCombat} Zone '{Zone}'; NetworkReceived subscribers={Nr} dropped={Dropped}{Dps}",
                        live, total, act.SetEncounterCount, act.ChangeZoneCount, act.InCombat, act.CurrentZone,
                        wrapper?.NetworkReceivedSubscriberCount ?? 0, wrapper?.RawPackets?.DroppedCount ?? 0,
                        DescribeActiveEncounter(act));
                }
                catch (Exception ex) { _log.LogError(LogEvents.CaptureHeartbeat, ex, "[Capture] heartbeat failed"); }
            };
            t.Start();
        }

        // The live DPS the overlay reads: the active encounter's aggregate damage/duration plus its
        // top combatant, formatted through the exact CombatantData.ExportVariables OverlayPlugin/cactbot
        // consume. Empty when idle so the heartbeat stays compact out of combat.
        private static string DescribeActiveEncounter(Advanced_Combat_Tracker.FormActMain act)
        {
            try
            {
                var enc = act.ActiveZone?.ActiveEncounter;
                if (enc == null || enc.Items.Count == 0) return "";
                string Ev(Advanced_Combat_Tracker.CombatantData cd, string k) =>
                    Advanced_Combat_Tracker.CombatantData.ExportVariables.TryGetValue(k, out var f)
                        ? f.GetExportString(cd, "") : "";
                var top = enc.Items.Values.OrderByDescending(c => c.Damage).First();
                return $"; EncDmg={enc.Damage} Dur={enc.Duration.TotalSeconds:0}s top='{top.Name}' " +
                       $"encdps={Ev(top, "encdps")} dmg={Ev(top, "damage")} crit%={Ev(top, "crithit%")}";
            }
            catch { return ""; }
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
                _log.LogInformation(LogEvents.DispatcherDiagnostics,
                    "[Diag] FFXIV_ACT_Plugin.Common loaded copies={Copies}", commonCopies);

                if (wrapper == null)
                {
                    _log.LogWarning(LogEvents.DispatcherDiagnostics,
                        "[Diag] wrapper missing (pluginObj is not WrappedFfxivPlugin)");
                    return;
                }

                _log.LogInformation(LogEvents.DispatcherDiagnostics,
                    "[Diag] OverlayPlugin bound to ring: ProcessChanged subscribers={Pc} NetworkReceived subscribers={Nr} (NetworkReceived is game-gated)",
                    wrapper.ProcessChangedSubscriberCount, wrapper.NetworkReceivedSubscriberCount);

                long before = wrapper.RawPackets.DroppedCount;
                for (int i = 0; i < 8; i++)
                    wrapper.RawPackets.InjectNetworkReceived("diag", i, new byte[64]);
                long dropped = wrapper.RawPackets.DroppedCount - before;
                _log.LogInformation(LogEvents.DispatcherDiagnostics,
                    "[Diag] injected=8 dropped={Dropped}", dropped);
            }
            catch (Exception ex) { _log.LogError(LogEvents.DispatcherDiagnostics, ex, "[Diag] FAILED"); }
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

            _log.LogInformation(LogEvents.Summary,
                "==== SUMMARY ==== FFXIV status '{FfxivStatus}', OverlayPlugin status '{OverlayStatus}', " +
                "WS(10501) open {WsOpen}; AddCombatAction={AddCombatAction} SetEncounter={SetEncounter} " +
                "ChangeZone={ChangeZone} InCombat={InCombat} Zone '{Zone}'; Network_*.log count={LogCount} latest {Latest}",
                _ffxiv?.Data?.lblPluginStatus?.Text, _overlay?.Data?.lblPluginStatus?.Text, IsPortOpen(10501),
                act.AddCombatActionCount, act.SetEncounterCount, act.ChangeZoneCount, act.InCombat,
                act.CurrentZone, networkLogs.Length, today);
        }

        private static void ScheduleOnce(int ms, Action action)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) =>
            {
                t.Stop(); t.Dispose();
                try { action(); }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Scheduled task failed"); }
            };
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
                // The pipe is up: start forwarding log records to the host's pipeline.
                BridgeLogSink.Sender = SendLine;
                // Wait on the host's cross-process graceful-shutdown signal (best effort).
                StartShutdownWaiter(pipeName + "-shutdown");
                _log.LogInformation(LogEvents.SatelliteBridgeConnected, "Bridge connected on {Pipe}", pipeName);
            }
            catch (Exception ex)
            {
                _bridge = null; _writer = null;
                _log.LogWarning(LogEvents.SatelliteBridgeConnectFailed, ex,
                    "Bridge connect failed on {Pipe}; running detached", pipeName);
            }
        }

        // Wait on the host's named graceful-shutdown event. A cross-process EventWaitHandle (not a
        // pipe) needs no connection rendezvous and never contends with the log pipe; the host opens
        // the same name and Sets it on close. Best effort: if the event can't be opened we simply
        // forgo graceful shutdown (the host's kill-on-close job still reaps us). The wait lives on a
        // background thread so it never stalls boot or the message loop.
        private static void StartShutdownWaiter(string eventName)
        {
            System.Threading.EventWaitHandle evt;
            try
            {
                evt = new System.Threading.EventWaitHandle(
                    false, System.Threading.EventResetMode.ManualReset, eventName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(LogEvents.SatelliteBridgeConnectFailed, ex,
                    "Could not open shutdown event {Event}; graceful shutdown disabled", eventName);
                return;
            }

            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    evt.WaitOne();
                    OnShutdownCommand();
                }
                catch { /* host gone; the message loop ends on its own */ }
            })
            { IsBackground = true, Name = "shutdown-waiter" };
            t.Start();
        }

        private static volatile bool _shuttingDown;

        // Drain the plugins (each persists its state in DeInitPlugin) on the UI thread, then end the
        // message loop so the process exits cleanly. Runs once; the host waits for our exit.
        private static void OnShutdownCommand()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _log.LogInformation(LogEvents.PluginDeInit, "SHUTDOWN received; de-initialising plugins");

            var act = ActGlobals.oFormActMain;
            Action drainAndExit = () =>
            {
                try { FacadeHost.DeInitPlugins(); }
                catch (Exception ex) { _log.LogError(LogEvents.PluginDeInit, ex, "Plugin de-init failed"); }
                finally { Application.Exit(); }
            };

            try
            {
                if (act != null && act.IsHandleCreated)
                    act.Invoke(drainAndExit);   // plugins were Init'd on the UI thread; de-init there too
                else
                    drainAndExit();
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.PluginDeInit, ex, "Shutdown dispatch failed");
                Application.Exit();
            }
        }

        // Single writer for the pipe, shared by the handshake/HWND lines and the forwarded LOG frames
        // (which arrive from arbitrary threads), so each line is written atomically.
        private static void SendLine(string s)
        {
            try
            {
                lock (_sendLock)
                    _writer?.WriteLine(s);
            }
            catch { }
        }

        // Announce a loaded plugin's embeddable window to the host:
        //   PLUGIN <key>|<hwndHex>|<status>|<title>
        private static void SendPlugin(LoadedPlugin p)
        {
            if (p == null) return;
            var status = (p.Status ?? "").Replace('|', '/');
            var title = (p.Title ?? "").Replace('|', '/');
            SendLine($"PLUGIN {p.Key}|{p.Hwnd.ToInt64():X}|{status}|{title}");
        }
    }
}
