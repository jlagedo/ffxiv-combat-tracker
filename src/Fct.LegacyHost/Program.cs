using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.Bridge;
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
        private static BridgeForwarder _forwarder;
        private static bool _parserWired;

        private static NamedPipeClientStream _cmdPipe;   // host -> satellite: load/unload commands
        // Every plugin loaded into this satellite, keyed by the host-facing key, so a single one can
        // be torn down on an UNLOADPLUGIN command.
        private static readonly Dictionary<string, LoadedPlugin> _plugins =
            new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        private static void Main(string[] args)
        {
            // The host-assigned identity this process hosts (P3): included in the READY handshake and used
            // for the per-satellite verification-log name. Defaults to "ffxiv" so a dev-standalone / parser
            // run keeps its historical s2-ffxiv.log artifact.
            _satelliteId = ParseArgValue(args, "--satellite-id") ?? "ffxiv";
            _package = ParseArgValue(args, "--package") ?? "";

            // Oracle capture mode: replay a log through the real plugin and dump its parse.
            //   --parse-oracle <logPath> <maxLines> <outPath>
            int oi = Array.IndexOf(args, "--parse-oracle");
            if (oi >= 0 && args.Length >= oi + 4)
            {
                ParseOracle.Run(args[oi + 1], int.Parse(args[oi + 2]), args[oi + 3]);
                return;
            }

            // End-to-end live route on a recorded log: plugin parse -> our ACT facade (with
            // idle-end encounter splitting).
            //   --replay <logPath> <maxLines> <outPath>      : dump per-encounter ExportVariables to a TSV
            //   --replay <logPath> <maxLines> --bridge <pipe>: forward the swing/lifecycle stream to the
            //     net10 host engine over the real pipe (the P1 wire-path e2e). The host aggregates and is
            //     asserted bit-equal to this satellite's own captured totals.
            int rpi = Array.IndexOf(args, "--replay");
            if (rpi >= 0 && args.Length >= rpi + 3)
            {
                var replayPipe = ParseBridgeArg(args);
                if (replayPipe != null)
                {
                    RunReplayBridge(args[rpi + 1], int.Parse(args[rpi + 2]), replayPipe);
                    return;
                }
                if (args.Length >= rpi + 4)
                {
                    ParseOracle.Replay(args[rpi + 1], int.Parse(args[rpi + 2]), args[rpi + 3]);
                    return;
                }
            }

            // Replay-satellite: play a committed FrameSession fixture up the bridge with NO plugin (the
            // fixtures are already wire frames). Speaks the satellite handshake, then streams the frames —
            // the full-fabric replay driver for the P2 harness (host-side gates need no game, no plugin).
            //   --replay-frames <fixturePath> --bridge <pipe>
            int rfi = Array.IndexOf(args, "--replay-frames");
            if (rfi >= 0 && args.Length >= rfi + 2)
            {
                var framesPipe = ParseBridgeArg(args);
                if (framesPipe != null)
                {
                    RunReplayFrames(args[rfi + 1], framesPipe);
                    return;
                }
            }

            // Downstream sink: a plugin-free satellite that SUBSCRIBEs to a stream set and records every
            // frame the host fans down to it — the P4 downstream-data-plane e2e subject.
            //   --sink <recordPath> --subscribe <streams> --bridge <pipe>
            int si = Array.IndexOf(args, "--sink");
            if (si >= 0 && args.Length >= si + 2)
            {
                var sinkPipe = ParseBridgeArg(args);
                if (sinkPipe != null)
                {
                    RunSink(args[si + 1], ParseArgValue(args, "--subscribe") ?? SatelliteProtocol.StreamSwings, sinkPipe);
                    return;
                }
            }

            // Consumer projection (ISOLATION-PLAN P5): a plugin-free consumer satellite that folds the
            // host-fanned swing/lifecycle stream into its OWN ACT-facade replica and dumps the resulting
            // YOU total — proving the facade serves synchronous encounter reads from host-routed data.
            //   --consume <dumpPath> --subscribe <streams> --bridge <pipe>
            int ci = Array.IndexOf(args, "--consume");
            if (ci >= 0 && args.Length >= ci + 2)
            {
                var consumePipe = ParseBridgeArg(args);
                if (consumePipe != null)
                {
                    RunConsume(args[ci + 1], ParseArgValue(args, "--subscribe") ?? SatelliteProtocol.StreamSwings, consumePipe);
                    return;
                }
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

            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Satellite starting (pid {Pid}, x64 {X64}, clr {Clr})",
                System.Diagnostics.Process.GetCurrentProcess().Id, Environment.Is64BitProcess, Environment.Version);

            // The ACT facade and plugin wrapper emit diagnostics through this Action<string>; route it
            // into the same pipeline (classifying by the legacy "[Tag]" prefix).
            FacadeHost.Log = SatelliteLogging.WriteLegacy;

            var pipeName = ParseBridgeArg(args);
            if (pipeName != null)
            {
                ConnectBridge(pipeName);
                StartCommandReader(pipeName);
            }

            // Must be installed before any plugin assembly is loaded.
            FacadeHost.InstallAssemblyResolver();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The ACT facade (hidden form: handle + Invoke marshaling).
            FacadeHost.CreateAct();
            _log.LogDebug(LogEvents.FacadeCreated, "ACT facade created");

            _standalone = pipeName == null;

            // Load plugins once the message loop is running (some plugins poll via timers).
            ScheduleOnce(250, Boot);
            ScheduleOnce(12000, WriteSummary);

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

        // Replay-over-bridge (the ISOLATION-PLAN P1 wire-path e2e): connect the bridge, load the real
        // parser, attach the forwarder, and drive a recorded log through the facade so every swing +
        // encounter-lifecycle event forwards to the net10 host engine over the real pipe. No live game,
        // no live host — a headless proof that the host engine aggregates the bridged stream to the same
        // totals this satellite would compute itself. Runs under a message loop (plugin Init marshals to
        // the UI thread), drives from a one-shot timer tick, then drains the forwarder and exits.
        private static void RunReplayBridge(string logPath, int maxLines, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Replay-over-bridge: log={Log} max={Max} pipe={Pipe}", logPath, maxLines, pipeName);

            ConnectBridge(pipeName);   // opens the event pipe (out) + sends READY
            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            _standalone = false;

            var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                  WindowState = FormWindowState.Minimized, Visible = false };
            _ = pump.Handle;
            var timer = new Timer { Interval = 250 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    ParseOracle.ReplayOverBridge(logPath, maxLines, () =>
                    {
                        // Attach the forwarder once the parser is started, before any line is fed, then
                        // close the handshake so the host's StartAsync loop returns and begins draining.
                        _forwarder = new BridgeForwarder(SendLine, _log);
                        _forwarder.Start();
                        SendLine(SatelliteProtocol.PluginsEnd);
                    }, m => _log.LogDebug(LogEvents.PluginLoading, "[Replay] {Msg}", m));
                }
                catch (Exception ex)
                {
                    _log.LogError(LogEvents.SatelliteBooting, ex, "replay-over-bridge failed");
                }
                finally
                {
                    try { _forwarder?.Dispose(); } catch { }   // deterministic final drain of the ring to the pipe
                    SatelliteLogging.Shutdown();
                    Environment.Exit(0);
                }
            };
            timer.Start();
            Application.Run(pump);
        }

        // Replay-satellite (ISOLATION-PLAN P2 full-fabric driver): connect the bridge, complete the
        // handshake, and stream a committed FrameSession fixture up the event pipe as raw wire frames.
        // No plugin, no ACT facade, no message loop — a headless process that speaks the satellite
        // protocol and plays recorded frames, so any host-side gate can run over the real pipe in CI
        // without a game or the FFXIV_ACT_Plugin.
        private static void RunReplayFrames(string fixturePath, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Replay-frames: fixture={Fixture} pipe={Pipe}", fixturePath, pipeName);

            ConnectBridge(pipeName);   // opens the event pipe (out) + sends READY
            SendLine(SatelliteProtocol.PluginsEnd);   // empty roster — close the handshake

            int sent = 0;
            try
            {
                foreach (var line in File.ReadLines(fixturePath))
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    SendLine(line.Substring(tab + 1));   // strip the offset prefix → the EVT wire frame
                    sent++;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBooting, ex, "replay-frames failed after {Sent} frames", sent);
            }
            finally
            {
                _log.LogInformation(LogEvents.SatelliteBooting, "Replay-frames done: {Sent} frames", sent);
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Downstream sink (ISOLATION-PLAN P4): connect the bridge, complete the handshake, declare a
        // stream set (SUBSCRIBE), and record every frame the host fans down the command pipe. No plugin,
        // no facade, no message loop — a headless subscriber that proves the host→satellite fan-out over
        // the real pipe. Exits when the host signals shutdown or closes the pipe.
        private static void RunSink(string recordPath, string streams, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Sink: record={Record} streams={Streams} pipe={Pipe}", recordPath, streams, pipeName);

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(streams.Split(',')));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Sink bridge connect failed");
                Environment.Exit(1);
                return;
            }

            // Graceful shutdown: exit the instant the host signals (no message loop to pump).
            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); Environment.Exit(0); })
                    { IsBackground = true, Name = "sink-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                using var record = new StreamWriter(recordPath, append: false) { AutoFlush = true };
                string line;
                int recorded = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal))
                    {
                        record.WriteLine(line);
                        recorded++;
                    }
                }
                _log.LogInformation(LogEvents.SatelliteBooting, "Sink done: recorded {Count} downstream frames", recorded);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Sink command-pipe read failed");
            }
            finally
            {
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Consumer projection (ISOLATION-PLAN P5): stand up the ACT facade with NO parser, SUBSCRIBE to
        // the swing/lifecycle stream, and fold every host-fanned frame into the facade's own
        // Fct.Aggregation replica — exactly as a real consumer plugin (OverlayPlugin/Triggernometry) would
        // read ActiveZone.ActiveEncounter + ExportVariables. On shutdown, dump the YOU total summed across
        // the encounters the fanned lifecycle split the stream into (the parity value vs the host engine).
        private static void RunConsume(string dumpPath, string streams, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Consumer: dump={Dump} streams={Streams} pipe={Pipe}", dumpPath, streams, pipeName);

            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            // No parser to install ACT's FFXIV damage-type routing tables (ACT_UIMods) — the consumer
            // replica installs them itself, exactly the shared state the host engine stands up.
            EngineTables.Install();
            var act = ActGlobals.oFormActMain;

            long youTotal = 0;
            act.OnCombatEnd += (imp, info) =>
            {
                var you = info.encounter?.GetCombatant("YOU");
                if (you != null) System.Threading.Interlocked.Add(ref youTotal, you.Damage);
            };

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(streams.Split(',')));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Consumer bridge connect failed");
                Environment.Exit(1);
                return;
            }

            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); FlushConsume(act, ref youTotal, dumpPath); Environment.Exit(0); })
                    { IsBackground = true, Name = "consume-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!GameEventFrame.TryParse(line, out var evt) || evt == null) continue;
                    FoldConsume(act, evt);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Consumer command-pipe read failed");
            }
            finally
            {
                FlushConsume(act, ref youTotal, dumpPath);
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Fold one host-routed frame into the consumer's ACT-facade replica (the mirror of the host
        // engine's ModernEncounterEngine.OnEvent — same MasterSwing rebuild, same lifecycle calls).
        private static void FoldConsume(FormActMain act, Fct.Abstractions.GameEvent evt)
        {
            switch (evt)
            {
                case Fct.Abstractions.CombatSwing s:
                    var ms = new MasterSwing(s.SwingType, s.Critical, s.Special, new Dnum(s.Damage),
                        s.Timestamp.LocalDateTime, s.TimeSorter, s.AttackType, s.Attacker, s.DamageType, s.Victim);
                    if (s.Tags != null && s.Tags.Count > 0)
                    {
                        var tags = new Dictionary<string, object>(s.Tags.Count);
                        foreach (var kv in s.Tags) tags[kv.Key] = kv.Value;
                        ms.Tags = tags;
                    }
                    act.AddCombatAction(ms);
                    break;
                case Fct.Abstractions.SetEncounterRequested r:
                    act.SetEncounter(r.Timestamp.LocalDateTime, r.Attacker, r.Victim);
                    break;
                case Fct.Abstractions.ZoneChangeRequested z:
                    act.ChangeZone(z.ZoneName);
                    break;
                case Fct.Abstractions.EndCombatRequested c:
                    act.EndCombat(c.Export);
                    break;
            }
        }

        // Flush the parity value once. Any encounter still open (no trailing EndCombat) is closed so its
        // YOU damage is counted. Idempotent via the file existence + a one-shot guard.
        private static int _consumeFlushed;
        private static void FlushConsume(FormActMain act, ref long youTotal, string dumpPath)
        {
            if (System.Threading.Interlocked.Exchange(ref _consumeFlushed, 1) != 0) return;
            try { if (act.InCombat) act.EndCombat(true); } catch { }
            try { File.WriteAllText(dumpPath, youTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
        }

        // Boot the satellite with NO plugins loaded: a clean host ships nothing and boot-loads nothing.
        // Every plugin (parser included) arrives on demand via the host's LOADPLUGIN command
        // (HandleCommand). The one dev exception is a standalone run (no --bridge), which auto-loads the
        // usual pair so the satellite is useful on its own for manual testing / the oracle tooling.
        private static void Boot()
        {
            // Drive ACT's idle-end off the live log stream: whichever parser loads raises OnLogLineRead
            // for every parsed line, so advancing the clock per line splits combat into per-pull
            // encounters (matching ACT). Wired unconditionally — harmless before any parser exists,
            // and OverlayPlugin reads ActiveZone.ActiveEncounter to show live per-encounter DPS.
            ActGlobals.oFormActMain.OnLogLineRead += (isImport, args) =>
            {
                if (args.detectedTime > DateTime.MinValue)
                    ActGlobals.oFormActMain.AdvanceClock(args.detectedTime);
            };

            if (_standalone)
            {
                LoadStandalonePlugins();
            }
            else
            {
                // Bridged/production: no plugins at boot. Close the handshake immediately so the host's
                // StartAsync loop returns at once (empty roster) instead of waiting the 60s timeout.
                SendLine(SatelliteProtocol.PluginsEnd);
                _log.LogInformation(LogEvents.PluginsReady, "Boot complete: 0 plugins (catalog-driven; awaiting LOADPLUGIN)");
            }
        }

        // Parser-dependent wiring, run once the parser is up (from the standalone boot-load or the
        // on-demand LOADPLUGIN path) rather than at a fixed boot time — with catalog-driven loading the
        // parser can arrive at any point. Starts the bridge forwarder (bridged only) and the ring/capture
        // diagnostics. Idempotent: a later parser reload is a no-op here.
        private static void OnParserLoaded()
        {
            if (_parserWired) return;
            _parserWired = true;

            // Forward the live SDK/ACT stream to the net10 host as typed GameEvent frames (piece C).
            // Only when bridged: standalone runs have no host to receive them.
            if (!_standalone)
            {
                _forwarder = new BridgeForwarder(SendLine, _log);
                _forwarder.Start();
            }

            // Deterministic aggregation check — runs now that the parser has populated ACT's routing
            // tables / ExportVariables (it produces zeros without them); also sets the capture baseline.
            SelfTestAggregation();
            StartDispatcherDiagnostics();
            StartCaptureHeartbeat();
        }

        // Dev convenience for a standalone (no --bridge) run: auto-load the usual FFXIV_ACT_Plugin +
        // OverlayPlugin pair from the real ACT install so the satellite is exercisable on its own. This
        // path never runs in production (which is always bridged and catalog-driven).
        private static void LoadStandalonePlugins()
        {
            _log.LogInformation(LogEvents.PluginLoading,
                "Loading FFXIV_ACT_Plugin (wrapped) from {Path}", FacadeHost.FfxivPluginPath);
            _ffxiv = FacadeHost.LoadWrappedFfxivPlugin(FacadeHost.FfxivPluginPath);
            _plugins[_ffxiv.Key] = _ffxiv;
            SendLine(SatelliteProtocol.FormatHwnd(_ffxiv.Hwnd));   // primary window (bridge-handshake compat)
            SendPlugin(_ffxiv);
            OnParserLoaded();

            _log.LogInformation(LogEvents.PluginLoading,
                "Loading OverlayPlugin from {Path}", FacadeHost.OverlayPluginPath);
            _overlay = FacadeHost.LoadPlugin("overlay", "OverlayPlugin", FacadeHost.OverlayPluginPath, null);
            _plugins[_overlay.Key] = _overlay;
            SendPlugin(_overlay);

            SendLine(SatelliteProtocol.PluginsEnd);
            _log.LogInformation(LogEvents.PluginsReady,
                "Loaded 2 plugin window(s): {Ffxiv}=0x{FfxivHwnd:X}, {Overlay}=0x{OverlayHwnd:X}",
                _ffxiv.Title, _ffxiv.Hwnd.ToInt64(), _overlay.Title, _overlay.Hwnd.ToInt64());

            foreach (var p in new[] { _ffxiv, _overlay })
            {
                p.Window.FormBorderStyle = FormBorderStyle.Sizable;
                p.Window.StartPosition = FormStartPosition.WindowsDefaultLocation;
                p.Window.Text = p.Title;
                p.Window.Show();
            }
        }

        // Deterministic S5 check: drive synthetic combat through the facade and read back
        // the from-scratch aggregation + the export formatters OverlayPlugin/cactbot consume.
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
            // The FFXIV plugin writes Network_*.log under the (sandboxed) ACT AppDataFolder.
            var logFolder = Path.Combine(act.AppDataFolder.FullName, "FFXIVLogs");
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

        // The host-assigned satellite identity + hosted package (P3). Set once in Main from --satellite-id
        // / --package; read by ConnectBridge (handshake) and the logging init (verification-log name).
        private static string _satelliteId = "ffxiv";
        private static string _package = "";

        private static string ParseArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
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
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(),
                    _satelliteId, _package));
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

        // Connect the host->satellite command pipe and read LOADPLUGIN/UNLOADPLUGIN frames on a
        // background thread, marshaling each onto the WinForms UI thread (plugin Init/DeInit build and
        // destroy WinForms controls). The host creates the server before launching us, so the connect
        // succeeds immediately. Connect BEFORE the first log call: log records are forwarded
        // synchronously over the event pipe (SendLine), so a host that isn't draining that pipe must
        // never be able to keep this thread from reaching the command pipe.
        private static void StartCommandReader(string pipeName)
        {
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    _cmdPipe.Connect(30000);
                    _log.LogDebug(LogEvents.SatelliteBridgeConnected, "Command pipe connected");
                    using (var reader = new StreamReader(_cmdPipe))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            HandleCommand(line);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(LogEvents.SatelliteBridgeConnectFailed, ex,
                        "Command pipe unavailable; live load/unload disabled");
                }
            })
            { IsBackground = true, Name = "cmd-reader" };
            t.Start();
        }

        private static void HandleCommand(string line)
        {
            if (SatelliteProtocol.TryParseLoadPlugin(line, out var loadKey, out var dll, out var title))
            {
                InvokeOnUi(() =>
                {
                    try
                    {
                        // The parser needs the ring-buffer wrapper OverlayPlugin binds to; every other
                        // legacy plugin takes the generic path. Detect it by its (fixed) DLL name.
                        bool isParser = string.Equals(Path.GetFileName(dll), "FFXIV_ACT_Plugin.dll", StringComparison.OrdinalIgnoreCase);
                        var loaded = isParser
                            ? FacadeHost.LoadWrappedFfxivPlugin(dll, loadKey)
                            : FacadeHost.LoadPlugin(loadKey, title, dll, null);
                        _plugins[loadKey] = loaded;
                        if (isParser) { _ffxiv = loaded; OnParserLoaded(); }
                        SendPlugin(loaded);
                        _log.LogInformation(LogEvents.PluginInitialized, "Loaded plugin {Key} '{Title}' on command from {Dll}", loadKey, title, dll);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(LogEvents.PluginLoadFailed, ex, "LOADPLUGIN {Key} failed", loadKey);
                    }
                });
            }
            else if (SatelliteProtocol.TryParseUnloadPlugin(line, out var unloadKey))
            {
                InvokeOnUi(() =>
                {
                    bool ok = false;
                    try
                    {
                        if (_plugins.TryGetValue(unloadKey, out var p))
                        {
                            FacadeHost.UnloadPlugin(p);
                            _plugins.Remove(unloadKey);
                            ok = true;
                        }
                        else
                        {
                            _log.LogWarning(LogEvents.PluginDeInit, "UNLOADPLUGIN {Key}: no such loaded plugin", unloadKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(LogEvents.PluginDeInit, ex, "UNLOADPLUGIN {Key} failed", unloadKey);
                    }
                    SendLine(SatelliteProtocol.FormatUnloaded(unloadKey, ok));
                });
            }
        }

        // Run an action on the satellite's WinForms UI thread (where plugins were Init'd).
        private static void InvokeOnUi(Action action)
        {
            var act = ActGlobals.oFormActMain;
            try
            {
                if (act != null && act.IsHandleCreated) act.Invoke(action);
                else action();
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.PluginLoadFailed, ex, "UI-thread command dispatch failed");
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
                try { _forwarder?.Dispose(); }
                catch (Exception ex) { _log.LogError(LogEvents.PluginDeInit, ex, "Bridge forwarder dispose failed"); }
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
            SendLine(SatelliteProtocol.FormatPlugin(p.Key, p.Hwnd, p.Status, p.Title));
        }
    }
}
