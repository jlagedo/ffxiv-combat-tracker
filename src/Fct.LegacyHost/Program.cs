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
            // Fault containment for hosted plugins' UI-thread exceptions (e.g. a plugin's WinForms Timer
            // tick racing its own init): route them through Application.ThreadException and log. Without
            // this, WinForms raises its default MODAL crash dialog — a headless satellite has nobody to
            // click it, and the modal blocks the message pump and with it every plugin and the bridge.
            // Must be set before the first window handle is created.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    _log?.LogError(LogEvents.ActException, e.Exception,
                        "Unhandled plugin UI-thread exception (pump continues)");
                }
                catch { }
            };
            // Non-UI-thread throws terminate the process (CLR default); log + flush the sinks first so
            // the crash reaches the satellite log and the host (BridgeLogSink) before the process dies.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    _log?.LogCritical(LogEvents.ActException, e.ExceptionObject as Exception,
                        "Unhandled satellite exception (terminating={Terminating})", e.IsTerminating);
                    if (e.IsTerminating) SatelliteLogging.Shutdown();
                }
                catch { }
            };

            // The host-assigned identity this process hosts (P3): included in the READY handshake and used
            // for the per-satellite verification-log name. Defaults to "ffxiv" so a dev-standalone / parser
            // run keeps its historical s2-ffxiv.log artifact.
            _satelliteId = ParseArgValue(args, "--satellite-id") ?? "ffxiv";
            _package = ParseArgValue(args, "--package") ?? "";
            // The satellite's role (ISOLATION-PLAN P7): "producer" hosts the parser and forwards its stream
            // up; "consumer" hosts one real consumer plugin on the host-fanned projection. Default producer
            // so the parser satellite and dev-standalone keep their existing behavior. Only an EXPLICIT
            // --role opts into the one-package-per-process guard, so legacy/test harnesses that load several
            // plugins into one satellite without a role stay permissive (the production router always passes
            // --role, so the guard is always active there).
            var roleArg = ParseArgValue(args, "--role");
            _roleExplicit = roleArg != null;
            _role = roleArg ?? RoleProducer;

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

            // From-start facade-tail replay (PIPELINE-COMPLETENESS-PLAN P1.3, TEST-ONLY driver): tail a
            // given log path through the real ACT facade (OpenLog -> the same background tail P0.1's
            // LineSeamCoverageTests proves delivers every line type to OnLogLineRead) and attach the REAL
            // production BridgeForwarder so any P1.3 harness can observe over the real bridge whether
            // today's producer forwards facade-tailed lines onto the wire. Optionally loads the real
            // FFXIV_ACT_Plugin first (--load-parser, the [plugin-gated] variant) — this only changes
            // whether a parser is present, exactly the existing LOADPLUGIN->OnParserLoaded wiring; it adds
            // no new tap and fixes nothing.
            //   --replay-tail <logPath> [--load-parser] --bridge <pipe>
            int rti = Array.IndexOf(args, "--replay-tail");
            if (rti >= 0 && args.Length >= rti + 2)
            {
                var rtPipe = ParseBridgeArg(args);
                if (rtPipe != null)
                {
                    RunReplayTail(args[rti + 1], Array.IndexOf(args, "--load-parser") >= 0, rtPipe);
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
                    RunSink(args[si + 1], ParseArgValue(args, "--subscribe") ?? SatelliteProtocol.StreamSwings, sinkPipe,
                        ParseArgValue(args, "--verify-latency"));
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
                    RunConsume(args[ci + 1], ParseArgValue(args, "--subscribe") ?? SatelliteProtocol.StreamSwings,
                        consumePipe, ParseArgValue(args, "--verify-loglines"),
                        Array.IndexOf(args, "--stand-in") >= 0, ParseArgValue(args, "--verify-standin"),
                        Array.IndexOf(args, "--probe") >= 0, ParseArgValue(args, "--verify-latency"),
                        ParseArgValue(args, "--verify-loglines-full"));
                    return;
                }
            }

            // Production consumer-package satellite (ISOLATION-PLAN P7): a parser-free satellite that hosts
            // ONE real consumer plugin (Triggernometry/Discord) on the P5/P6 projection — subscribes to its
            // stream set, folds host-fanned frames into its facade replica, re-raises log lines, routes the
            // plugin's audio/callbacks up through the host, and loads the plugin on demand via LOADPLUGIN.
            // Selected by --role consumer (distinct from the plugin-free --consume test driver above).
            //   --role consumer --subscribe <streams> --bridge <pipe>
            if (string.Equals(_role, RoleConsumer, StringComparison.OrdinalIgnoreCase))
            {
                var consumerPipe = ParseBridgeArg(args);
                if (consumerPipe != null)
                {
                    RunConsumerPackage(ParseArgValue(args, "--subscribe") ?? SatelliteProtocol.StreamSwings, consumerPipe);
                    return;
                }
            }

            // Audio producer driver (ISOLATION-PLAN P6): connect, install the host route, and drive the ACT
            // facade's TTS/PlaySound so the produced call marshals up the bridge to the host's IAudioOutput
            // (which fans it to whichever peer satellite registered a sink). No plugin, no message loop.
            //   --audio-produce [--tts <text>] [--wav <path>] --bridge <pipe>
            int api = Array.IndexOf(args, "--audio-produce");
            if (api >= 0)
            {
                var apPipe = ParseBridgeArg(args);
                if (apPipe != null)
                {
                    RunAudioProduce(ParseArgValue(args, "--tts"), ParseArgValue(args, "--wav"), apPipe);
                    return;
                }
            }

            // Audio sink driver (ISOLATION-PLAN P6): connect, hijack the ACT PlayTts/PlaySound slots with a
            // recording delegate (exactly as Discord-Triggers does) so the poll registers a terminal host
            // sink, and record every audio call the host relays down the command pipe.
            //   --audio-sink <recordPath> --bridge <pipe>
            int asi = Array.IndexOf(args, "--audio-sink");
            if (asi >= 0 && args.Length >= asi + 2)
            {
                var asPipe = ParseBridgeArg(args);
                if (asPipe != null)
                {
                    RunAudioSink(args[asi + 1], asPipe);
                    return;
                }
            }

            // Custom-log-line write-back driver (ISOLATION-PLAN P6): a plugin-free satellite that SUBSCRIBEs
            // to rawlog, writes back N custom lines (LOGLINE up the event pipe), and records the fanned
            // RawLogLines the host sends back down — proving a written line is fanned to every rawlog
            // subscriber including the origin, in bus order.
            //   --emit-logline <recordPath> [--count N] --bridge <pipe>
            int eli = Array.IndexOf(args, "--emit-logline");
            if (eli >= 0 && args.Length >= eli + 2)
            {
                var elPipe = ParseBridgeArg(args);
                if (elPipe != null)
                {
                    RunEmitLogLine(args[eli + 1],
                        int.TryParse(ParseArgValue(args, "--count"), out var cnt) ? cnt : 10, elPipe);
                    return;
                }
            }

            // EndCombat route-up driver (ISOLATION-PLAN P9a): a plugin-free consumer that installs the
            // BridgeServiceRoute + RouteEndCombatUp and, after a settle, calls the facade EndCombat once —
            // exercising the full facade -> ENDCOMBAT wire -> host bus-injection chain. The host injects
            // EndCombatRequested onto the bus, which fans down the swings stream to every subscriber.
            //   --emit-endcombat --bridge <pipe>
            int eei = Array.IndexOf(args, "--emit-endcombat");
            if (eei >= 0)
            {
                var eePipe = ParseBridgeArg(args);
                if (eePipe != null) { RunEmitEndCombat(eePipe); return; }
            }

            // Stand-in write-back seam (ISOLATION-PLAN P6, plugin-gated): register the synthetic stand-in,
            // then reflect its _iocContainer → GetService(ILogOutput) → WriteLine EXACTLY as OverlayPlugin's
            // FFXIVRepository does, and record the RawLogLine the host fans back — proving a consumer's
            // ILogOutput write-back round-trips through the host. Needs FFXIV_ACT_Plugin.dll installed.
            //   --standin-writeback <recordPath> --bridge <pipe>
            int swi = Array.IndexOf(args, "--standin-writeback");
            if (swi >= 0 && args.Length >= swi + 2)
            {
                var swPipe = ParseBridgeArg(args);
                if (swPipe != null)
                {
                    RunStandInWriteBack(args[swi + 1], swPipe);
                    return;
                }
            }

            // Named-callback drivers (ISOLATION-PLAN P6): register a facade callback that records each
            // invoke, invoke a callback registered elsewhere, or self-test (register + invoke in one
            // satellite → receive its own, proving the single host fan). Plugin-free.
            //   --cb-register <name> <recordPath> --bridge <pipe>
            //   --cb-invoke   <name> <arg>        --bridge <pipe>
            //   --cb-selftest <name> <arg> <recordPath> --bridge <pipe>
            int cri = Array.IndexOf(args, "--cb-register");
            if (cri >= 0 && args.Length >= cri + 3)
            {
                var p = ParseBridgeArg(args);
                if (p != null) { RunCallbackRegister(args[cri + 1], args[cri + 2], p); return; }
            }
            int cii = Array.IndexOf(args, "--cb-invoke");
            if (cii >= 0 && args.Length >= cii + 3)
            {
                var p = ParseBridgeArg(args);
                if (p != null) { RunCallbackInvoke(args[cii + 1], args[cii + 2], p); return; }
            }
            int csi = Array.IndexOf(args, "--cb-selftest");
            if (csi >= 0 && args.Length >= csi + 4)
            {
                var p = ParseBridgeArg(args);
                if (p != null) { RunCallbackSelfTest(args[csi + 1], args[csi + 2], args[csi + 3], p); return; }
            }

            // Combined P6 soak drivers (ISOLATION-PLAN P6): exercise all three host-routed concerns at once
            // across two satellites — a sink that registers an audio sink + a named callback + a rawlog
            // subscription and records everything it receives, and a driver that produces audio, invokes the
            // callback, and writes log lines in a loop. Proves the three routes coexist without interference.
            //   --p6-sink  <recordPath>            --bridge <pipe>
            //   --p6-drive <recordPath> [--count N] --bridge <pipe>
            int psi = Array.IndexOf(args, "--p6-sink");
            if (psi >= 0 && args.Length >= psi + 2)
            {
                var p = ParseBridgeArg(args);
                if (p != null) { RunP6Sink(args[psi + 1], p); return; }
            }
            int pdi = Array.IndexOf(args, "--p6-drive");
            if (pdi >= 0 && args.Length >= pdi + 2)
            {
                var p = ParseBridgeArg(args);
                if (p != null)
                {
                    RunP6Drive(args[pdi + 1], int.TryParse(ParseArgValue(args, "--count"), out var c) ? c : 20, p);
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

            // Bridged: poll the ACT audio slots for a plugin takeover and announce it to the host (P6), so
            // audio produced in a peer satellite routes to this satellite's registered sink.
            if (!_standalone) StartAudioSinkPoll();

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

        // From-start facade-tail replay (PIPELINE-COMPLETENESS-PLAN P1.3 gate driver, TEST-ONLY — no
        // production behavior change): connect the bridge, stand up the ACT facade, optionally load the
        // real FFXIV_ACT_Plugin (mirroring LoadStandalonePlugins' exact OnParserLoaded wiring, so the
        // production BridgeForwarder attaches precisely as it would once a producer's LOADPLUGIN
        // completes), then start the REAL file tail (OpenLog) against the given log path — the same tail
        // LineSeamCoverageTests (P0.1) proves delivers every line type to OnLogLineRead byte-identical and
        // in order. This driver exercises only the EXISTING facade + BridgeForwarder; it adds no new tap
        // and fixes nothing (G14 stays exactly as it is today), so it lets the P1.3 gate observe over the
        // real wire whether a facade-tailed line ever reaches a rawlog-subscribed consumer.
        private static void RunReplayTail(string logPath, bool loadParser, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Replay-tail: log={Log} loadParser={LoadParser} pipe={Pipe}", logPath, loadParser, pipeName);

            ConnectBridge(pipeName);   // opens the event pipe (out) + sends READY + arms the shutdown waiter
            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            _standalone = false;

            // A hidden pump so BridgeForwarder's discovery Timer (and, if loaded, the plugin's own
            // WinForms timers) tick; the tail itself runs on its own background thread regardless.
            var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                  WindowState = FormWindowState.Minimized, Visible = false };
            _ = pump.Handle;

            var act = ActGlobals.oFormActMain;
            if (loadParser)
            {
                _ffxiv = FacadeHost.LoadWrappedFfxivPlugin(FacadeHost.FfxivPluginPath);
                _plugins[_ffxiv.Key] = _ffxiv;
                SendPlugin(_ffxiv);
                OnParserLoaded();   // real production wiring: creates + starts the REAL BridgeForwarder
            }
            else
            {
                _forwarder = new BridgeForwarder(SendLine, _log);
                _forwarder.Start();
            }
            SendLine(SatelliteProtocol.PluginsEnd);

            act.LogFilePath = logPath;
            act.OpenLog(false, false);   // real tail thread: FeedLine -> Before/OnLogLineRead

            Application.Run(pump);   // ends via the shutdown waiter (ConnectBridge) -> OnShutdownCommand
            SatelliteLogging.Shutdown();
        }

        // Downstream sink (ISOLATION-PLAN P4): connect the bridge, complete the handshake, declare a
        // stream set (SUBSCRIBE), and record every frame the host fans down the command pipe. No plugin,
        // no facade, no message loop — a headless subscriber that proves the host→satellite fan-out over
        // the real pipe. Exits when the host signals shutdown or closes the pipe.
        private static void RunSink(string recordPath, string streams, string pipeName, string latencyPath = null)
        {
            OpenLatency(latencyPath);   // P9b: record-time QPC capture for rawlog markers
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
                        RecordMarker(line);   // P9b: stamp record-time QPC for latency markers (no-op otherwise)
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
                CloseLatency();   // P9b: flush + close the latency artifact
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Consumer projection (ISOLATION-PLAN P5): stand up the ACT facade with NO parser, SUBSCRIBE to
        // the swing/lifecycle stream, and fold every host-fanned frame into the facade's own
        // Fct.Aggregation replica — exactly as a real consumer plugin (OverlayPlugin/Triggernometry) would
        // read ActiveZone.ActiveEncounter + ExportVariables. On shutdown, dump the YOU total summed across
        // the encounters the fanned lifecycle split the stream into (the parity value vs the host engine).
        private static void RunConsume(string dumpPath, string streams, string pipeName,
            string logLineVerifyPath = null, bool standIn = false, string standInVerifyPath = null, bool probe = false,
            string latencyPath = null, string logLineFullVerifyPath = null)
        {
            OpenLatency(latencyPath);   // P9b: fold-time QPC capture for rawlog markers
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

            // Log-line re-raise verification (plugin-free gate): count re-raised lines and prove the
            // Before→On args instance is shared by mutating logLine in a Before-handler and observing
            // it in an On-handler. Written to a sibling artifact so the YOU-total dump stays a bare int.
            _logLineVerifyPath = logLineVerifyPath;
            if (logLineVerifyPath != null)
            {
                act.BeforeLogLineRead += (imp, a) => { a.logLine = LogLineMutationMarker + a.logLine; };
                act.OnLogLineRead += (imp, a) =>
                {
                    System.Threading.Interlocked.Increment(ref _logLineCount);
                    if (a.logLine != null && a.logLine.StartsWith(LogLineMutationMarker, StringComparison.Ordinal))
                        System.Threading.Interlocked.Increment(ref _logLineMutationObserved);
                };
            }

            // Full-content log-line verification (PIPELINE-COMPLETENESS-PLAN P1.3): record every re-raised
            // OnLogLineRead line's type + exact bytes, in receipt order — unlike the count-only verify
            // above, this is a byte-diff artifact against the replayed slice. No mutation here (that
            // marker is the sibling gate's concern), so the recorded text is exactly what crossed the wire.
            _logLineFullVerifyPath = logLineFullVerifyPath;
            if (logLineFullVerifyPath != null)
            {
                act.OnLogLineRead += (imp, a) =>
                {
                    var row = a.detectedType.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + (a.logLine ?? "");
                    lock (_logLineFullLock) _logLineFullRecords.Add(row);
                };
            }

            // Synthetic parser stand-in (ISOLATION-PLAN P5): expose the SDK surface an unmodified consumer
            // discovers + binds, backed by the same host-routed frames — no parser. Make the Costura-embedded
            // FFXIV_ACT_Plugin.Common resolvable first (load the installed parser DLL + run its module
            // initializer), then materialize the stand-in only through the SDK-type-free seam so this path
            // is the only place that JITs SDK-touching code.
            if (standIn)
            {
                _standInVerifyPath = standInVerifyPath;
                if (TryEnsureSdkResolvable())
                {
                    // Route the stand-in's ILogOutput.WriteLine write-back up the bridge (P6): the host
                    // re-emits it as a bus RawLogLine fanned back to every rawlog subscriber incl. origin.
                    _standIn = Fct.Parser.Legacy.ConsumerStandInFactory.Create(FacadeHost.Log,
                        (id, text) => SendLine(SatelliteProtocol.FormatLogLine(id, text)));
                    _standIn.Register();
                }
                else
                {
                    _log.LogError(LogEvents.PluginNotFound,
                        "Stand-in requested but FFXIV_ACT_Plugin.dll is not installed; the SDK surface is unavailable");
                }
            }

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

            // Probe discovery variant (ISOLATION-PLAN P5): host the real, unmodified Fct.StreamProbe so it
            // discovers the stand-in by reflection under a message loop. Never returns (exits inside).
            if (probe)
            {
                RunConsumeProbeLoop(act, ref youTotal, dumpPath, pipeName);
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
                    // Fan-back: apply locally without re-routing up (RouteEndCombatUp is set on consumers,
                    // so act.EndCombat would loop the request back up the bridge).
                    act.EndCombatLocal(c.Export);
                    break;
                case Fct.Abstractions.RawLogLine r:
                    RecordMarker(r.Line);   // P9b: stamp fold-time QPC for latency markers (no-op otherwise)
                    // Re-raise the fanned raw line through ACT's Before/OnLogLineRead hooks so an
                    // unmodified consumer (Trig/cactbot regex) reads it exactly as under real ACT. The
                    // SAME LogLineEventArgs instance flows through both hooks, so a Before-handler edit
                    // of the mutable logLine/detectedType is visible to OnLogLineRead.
                    var logArgs = new LogLineEventArgs(r.Line ?? "", (int)r.Type,
                        r.Timestamp.LocalDateTime, act.CurrentZone ?? "", act.InCombat);
                    act.FireBeforeLogLineRead(false, logArgs);
                    act.FireLogLineRead(false, logArgs);
                    break;
            }
            // Also feed the synthetic parser stand-in (its SDK subscription + repository mirror), so a
            // discovered consumer plugin sees the same host-routed frames. No-op unless --stand-in.
            _standIn?.Fold(evt);
        }

        // Flush the parity value once. Any encounter still open (no trailing EndCombat) is closed so its
        // YOU damage is counted. Idempotent via the file existence + a one-shot guard.
        private static int _consumeFlushed;
        private const string LogLineMutationMarker = "MUT|";

        // P9b latency harness: markers ride the rawlog stream as sentinel RawLogLines ("FCT_MARK:<id>").
        // On fold/record we stamp the QPC clock (Stopwatch.GetTimestamp() — machine-wide, cross-process
        // comparable) and append "<id>\t<qpc>" to the --verify-latency artifact; the driving test stamps
        // QPC at bus.Emit and joins on <id> to compute host-egress→satellite-fold p99. The engine ignores
        // these chat-log lines, so they never perturb the parity YOU total.
        private const string LatencyMarkerPrefix = "FCT_MARK:";
        private static StreamWriter _latencyWriter;
        private static readonly object _latencyLock = new object();

        private static void OpenLatency(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { _latencyWriter = new StreamWriter(path, append: false) { AutoFlush = true }; } catch { }
        }

        // Stamp a fold-time QPC for a marker line ("FCT_MARK:<id>"), if this is one. Cheap substring gate
        // first so non-marker traffic pays nothing.
        private static void RecordMarker(string line)
        {
            if (_latencyWriter == null || line == null) return;
            int at = line.IndexOf(LatencyMarkerPrefix, StringComparison.Ordinal);
            if (at < 0) return;
            long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            // The consume path passes the decoded RawLogLine.Line ("FCT_MARK:<id>" exactly); the sink path
            // passes the whole wire line, so trailing tab-separated fields (and the '\' escape char) follow
            // the id — cut at the first of either so the id is clean in both cases.
            string rest = line.Substring(at + LatencyMarkerPrefix.Length);
            int end = rest.IndexOfAny(new[] { '\t', '\\' });
            string id = end >= 0 ? rest.Substring(0, end) : rest;
            lock (_latencyLock) { try { _latencyWriter.WriteLine(id + "\t" + qpc.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { } }
        }

        private static void CloseLatency()
        {
            lock (_latencyLock) { try { _latencyWriter?.Dispose(); } catch { } _latencyWriter = null; }
        }

        private static string _logLineVerifyPath;
        private static long _logLineCount;
        private static long _logLineMutationObserved;
        private static string _logLineFullVerifyPath;
        private static readonly List<string> _logLineFullRecords = new List<string>();
        private static readonly object _logLineFullLock = new object();
        private static Fct.Parser.Legacy.IConsumerStandIn _standIn;
        private static string _standInVerifyPath;
        private static void FlushConsume(FormActMain act, ref long youTotal, string dumpPath)
        {
            if (System.Threading.Interlocked.Exchange(ref _consumeFlushed, 1) != 0) return;
            // Local end — teardown closes the replica for the parity dump; it must never route up (the host
            // may already be gone), so use EndCombatLocal regardless of RouteEndCombatUp.
            try { if (act.InCombat) act.EndCombatLocal(true); } catch { }
            try { File.WriteAllText(dumpPath, youTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            // Sibling log-line verification artifact: "<re-raised count>\t<mutation-observed count>".
            if (_logLineVerifyPath != null)
            {
                try
                {
                    File.WriteAllText(_logLineVerifyPath, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t{1}", System.Threading.Interlocked.Read(ref _logLineCount),
                        System.Threading.Interlocked.Read(ref _logLineMutationObserved)));
                }
                catch { }
            }
            // Full-content log-line artifact: one "<type>\t<line>" row per re-raised OnLogLineRead event,
            // in receipt order — the byte-diff-against-the-slice input for the P1.3 gate.
            if (_logLineFullVerifyPath != null)
            {
                try
                {
                    string[] rows;
                    lock (_logLineFullLock) rows = _logLineFullRecords.ToArray();
                    File.WriteAllLines(_logLineFullVerifyPath, rows);
                }
                catch { }
            }
            // Stand-in discovery artifact:
            //   "<found>\t<sdkBound>\t<logLines>\t<combatants>\t<title>\t<status>\t<packets>\t<realIoc>\t<gameVersion>".
            // The packets column (P8) is the NetworkReceived/Sent count raised from fanned RawPacketReceived
            // frames — OverlayPlugin's NetworkProcessors bind point. The realIoc column (P9a) is 1
            // when _iocContainer is the real Microsoft.MinIoC.Container resolving ILogFormat+ILogOutput
            // (Hojoring's attach gate). The trailing gameVersion column (PIPELINE-COMPLETENESS-PLAN P1.4/G4)
            // is the stand-in repository's GetGameVersion() — appended purely for gate observability, never
            // read elsewhere, so it is safe to append without moving any existing column.
            if (_standInVerifyPath != null && _standIn != null)
            {
                try
                {
                    var v = _standIn.SelfVerify();
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    File.WriteAllText(_standInVerifyPath, string.Join("\t", new[]
                    {
                        v.Found ? "1" : "0", v.SdkTypesBound ? "1" : "0",
                        v.LogLines.ToString(inv), v.Combatants.ToString(inv),
                        v.Title ?? "", v.Status ?? "", v.Packets.ToString(inv),
                        v.RealIocContainer ? "1" : "0", v.GameVersion ?? "",
                    }));
                }
                catch { }
            }
            CloseLatency();   // P9b: flush + close the latency artifact
        }

        // Probe discovery variant (ISOLATION-PLAN P5 M4): host the real, unmodified Fct.StreamProbe as an
        // IActPluginV1 so it discovers the stand-in by reflection (title scan → cast DataSubscription/
        // DataRepository → poll the repository) exactly as OverlayPlugin's FFXIVRepository does — across an
        // assembly boundary, binding FFXIV_ACT_Plugin.Common through the facade's AssemblyResolve. StreamProbe
        // drives UI-thread timers, so this runs a WinForms message loop with the command-pipe fold on a
        // background thread; teardown (pipe close or host shutdown) exits the loop and flushes the probe log.
        private static void RunConsumeProbeLoop(FormActMain act, ref long youTotal, string dumpPath, string pipeName)
        {
            try { FacadeHost.LoadProbe(FacadeHost.StreamProbePath); }
            catch (Exception ex) { _log.LogError(LogEvents.PluginLoadFailed, ex, "[Probe] load failed"); }

            var reader = new System.Threading.Thread(() =>
            {
                try
                {
                    _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    _cmdPipe.Connect(30000);
                    using var r = new StreamReader(_cmdPipe);
                    string line;
                    while ((line = r.ReadLine()) != null)
                        if (GameEventFrame.TryParse(line, out var evt) && evt != null) FoldConsume(act, evt);
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "[Probe] command-pipe read failed"); }
                finally { try { Application.Exit(); } catch { } }
            }) { IsBackground = true, Name = "consume-probe-read" };
            reader.Start();

            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); try { Application.Exit(); } catch { } })
                    { IsBackground = true, Name = "consume-probe-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            using (var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                         WindowState = FormWindowState.Minimized, Visible = false })
            {
                _ = pump.Handle;
                Application.Run(pump);   // pumps StreamProbe's discover/snapshot timers until Application.Exit
            }

            try { FacadeHost.DeInitPlugins(); } catch { }   // flushes the StreamProbe log
            FlushConsume(act, ref youTotal, dumpPath);
            SatelliteLogging.Shutdown();
            Environment.Exit(0);
        }

        // Make the Costura-embedded FFXIV_ACT_Plugin.Common resolvable in a plugin-free consumer: load the
        // installed parser DLL and run its module initializer so Costura's AssemblyResolve hook registers
        // (Assembly.LoadFrom alone executes no code). No plugin type is constructed. The facade's Common
        // resolver (registered first) then unifies every request onto this one loaded copy.
        private static bool TryEnsureSdkResolvable()
        {
            var path = FacadeHost.FfxivPluginPath;
            if (!File.Exists(path)) return false;
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(path);
                System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);
                _log.LogInformation(LogEvents.PluginLoading, "[StandIn] SDK made resolvable via {Path}", path);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.PluginLoadFailed, ex, "[StandIn] failed to make the SDK resolvable from {Path}", path);
                return false;
            }
        }

        // Production consumer-package satellite (ISOLATION-PLAN P7): stand up a parser-free ACT facade that
        // serves one real consumer plugin (Triggernometry/Discord) from host-routed data. It installs the
        // engine tables the parser would (so the replica aggregates), SUBSCRIBEs to its stream set, folds
        // every host-fanned frame into the replica + re-raises log lines (the command reader, IsConsumer),
        // routes the plugin's TTS/PlaySound/callbacks up through the host (ConnectBridge's ServiceRoute +
        // the audio-sink poll), and loads the plugin on demand via LOADPLUGIN. This is the generalization
        // of RunConsumeProbeLoop into a real plugin host — the WinForms message loop pumps the plugin's
        // UI-thread timers while the reader folds frames on a background thread.
        private static void RunConsumerPackage(string streams, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Consumer package '{Pkg}': streams={Streams} pipe={Pipe}", _package, streams, pipeName);

            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            // No parser to install ACT's FFXIV damage-type routing tables — the replica installs them itself,
            // exactly the shared state the host engine stands up.
            EngineTables.Install();
            var act = ActGlobals.oFormActMain;
            _standalone = false;

            // Advance the idle-end clock off the fanned log stream so combat splits into per-pull encounters
            // (matching ACT), and consumers reading ActiveZone.ActiveEncounter see live per-encounter state.
            act.OnLogLineRead += (isImport, a) =>
            {
                if (a.detectedTime > DateTime.MinValue) act.AdvanceClock(a.detectedTime);
            };

            // Synthetic parser stand-in (P5), runtime plugin-gated: a consumer that discovers FFXIV_ACT_Plugin
            // by reflection (Triggernometry/OverlayPlugin) binds the stand-in's SDK surface backed by the same
            // fanned frames. Absent the installed parser DLL the consumer still works (log-line re-raise +
            // encounter replica), so this never blocks a plugin-free run. The stand-in's ILogOutput write-back
            // rides LOGLINE up the bridge (P6).
            if (TryEnsureSdkResolvable())
            {
                try
                {
                    _standIn = Fct.Parser.Legacy.ConsumerStandInFactory.Create(FacadeHost.Log,
                        (id, text) => SendLine(SatelliteProtocol.FormatLogLine(id, text)));
                    _standIn.Register();
                }
                catch (Exception ex) { _log.LogError(LogEvents.PluginLoadFailed, ex, "[Consumer] stand-in register failed"); }
            }
            else
            {
                _log.LogInformation(LogEvents.PluginLoading, "[Consumer] parser not installed; running without the SDK stand-in");
            }

            // ConnectBridge sends READY (with _package), installs FormActMain.ServiceRoute = BridgeServiceRoute
            // (so the plugin's TTS/PlaySound/callbacks route UP the bridge as SPEAK/PLAYSND/INVOKECB, never a
            // local slot), and starts the graceful-shutdown waiter. Required — the plugin-free --consume driver
            // deliberately omits the service route; a real consumer must have it.
            ConnectBridge(pipeName);
            if (_writer == null)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, "Consumer bridge connect failed on {Pipe}", pipeName);
                Environment.Exit(1);
                return;
            }
            // A consumer replica routes EndCombat UP (P9a): the host ends the authoritative encounter and
            // fans EndCombatRequested back down to every replica in one bus order. Set only here (producer +
            // dev-standalone keep the local/in-band path).
            FormActMain.RouteEndCombatUp = true;
            SendLine(SatelliteProtocol.PluginsEnd);                            // empty roster; plugin arrives via LOADPLUGIN
            SendLine(SatelliteProtocol.FormatSubscribe(streams.Split(',')));   // declare the downstream stream set

            // Announce audio-slot takeovers (Discord's PlayTts/PlaySound hijack) so the host registers a
            // terminal sink and relays produced audio down to this satellite (P6).
            StartAudioSinkPoll();

            // The unified command-pipe reader (folds fanned frames + dispatches LOADPLUGIN/service relays)
            // runs on a background thread; the message loop pumps the consumer plugin's UI-thread timers.
            StartCommandReader(pipeName);

            // Opt-in status diagnostics (FCT_CONSUMER_STATUS_POLL=1): log each loaded plugin's status label
            // a few times so a consumer plugin's async init progress (e.g. OverlayPlugin's init phases) is
            // observable headlessly. Self-stops; off by default so normal runs stay quiet.
            if (Environment.GetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL") == "1")
                StartConsumerStatusPoll();

            _log.LogInformation(LogEvents.SatelliteBooting, "[Consumer] entering message loop");
            using (var pump = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                         WindowState = FormWindowState.Minimized, Visible = false })
            {
                _ = pump.Handle;
                Application.Run(pump);
            }

            // On the graceful path OnShutdownCommand already de-init'd the plugins; only clean up here when
            // the loop ended via pipe-close (reader finally → Application.Exit) without a SHUTDOWN.
            if (!_shuttingDown) { try { FacadeHost.DeInitPlugins(); } catch { } }
            SatelliteLogging.Shutdown();
            Environment.Exit(0);
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

        // The satellite's role (P7): producer hosts the parser (forwards its stream up); consumer hosts one
        // real consumer plugin on the host-fanned projection. Gates the LOADPLUGIN one-package guard and the
        // command-reader downstream-frame fold.
        internal const string RoleProducer = "producer";
        internal const string RoleConsumer = "consumer";
        private static string _role = RoleProducer;
        private static bool _roleExplicit;   // true only when --role was passed → the one-package guard is active
        private static bool IsConsumer => string.Equals(_role, RoleConsumer, StringComparison.OrdinalIgnoreCase);

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
                // Route the facade's host-routed service calls (audio; callbacks in a later slice) up the
                // bridge (P6). Bridged only — a standalone run leaves ServiceRoute null so TTS/PlaySound
                // hit the local delegate slots.
                FormActMain.ServiceRoute = new BridgeServiceRoute();
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

        // Connect the host->satellite command pipe and read frames on a background thread. In a producer
        // satellite this carries only control commands (LOADPLUGIN/UNLOADPLUGIN, P6 service relays),
        // marshaled onto the WinForms UI thread (plugin Init/DeInit build and destroy WinForms controls).
        // In a CONSUMER satellite (P7) the same pipe ALSO carries the host's downstream game-event fan-out
        // (SatelliteEgress), so each line is disambiguated by its EVT prefix and folded into the facade
        // replica; everything else is a control command. The host creates the server before launching us,
        // so the connect succeeds immediately. Connect BEFORE the first log call: log records are forwarded
        // synchronously over the event pipe (SendLine), so a host that isn't draining that pipe must never
        // be able to keep this thread from reaching the command pipe.
        private static void StartCommandReader(string pipeName)
        {
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    _cmdPipe.Connect(30000);
                    _log.LogDebug(LogEvents.SatelliteBridgeConnected, "Command pipe connected");
                    var consumer = IsConsumer;
                    var act = ActGlobals.oFormActMain;
                    using (var reader = new StreamReader(_cmdPipe))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // Consumer: fold a fanned game-event frame into the replica; else it's a command.
                            if (consumer && GameEventFrame.TryParse(line, out var evt) && evt != null)
                                FoldConsume(act, evt);
                            else
                                HandleCommand(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(LogEvents.SatelliteBridgeConnectFailed, ex,
                        "Command pipe unavailable; live load/unload disabled");
                }
                finally
                {
                    // Consumer satellites pump a WinForms loop for their plugin's UI-thread timers; when the
                    // host closes the pipe (shutdown), end the loop so the process exits cleanly.
                    if (IsConsumer) try { Application.Exit(); } catch { }
                }
            })
            { IsBackground = true, Name = "cmd-reader" };
            t.Start();
        }

        private static void HandleCommand(string line)
        {
            if (SatelliteProtocol.TryParseLoadPlugin(line, out var loadKey, out var dll, out var title))
            {
                // One-package-per-process guard (ISOLATION-PLAN P7): a producer satellite hosts ONLY the
                // parser; a consumer satellite NEVER hosts the parser. The production host ALWAYS launches a
                // satellite with an explicit --role (the SatelliteRouter/SatelliteSupervisor pass
                // --role producer|consumer), so this guard is always active there and the multi-package path
                // is gone in production. The no-role bridged path stays permissive only for in-process
                // diagnostic harnesses (SatelliteRunFixture co-loads parser + OverlayPlugin), the same
                // dev/test carve-out as dev-standalone's LoadStandalonePlugins.
                bool isParser = string.Equals(Path.GetFileName(dll), "FFXIV_ACT_Plugin.dll", StringComparison.OrdinalIgnoreCase);
                if (_roleExplicit && IsConsumer && isParser)
                {
                    _log.LogWarning(LogEvents.PluginLoadFailed,
                        "LOADPLUGIN {Key} rejected: the parser cannot load in a consumer satellite ('{Pkg}')", loadKey, _package);
                    return;
                }
                if (_roleExplicit && !IsConsumer && !isParser)
                {
                    _log.LogWarning(LogEvents.PluginLoadFailed,
                        "LOADPLUGIN {Key} rejected: a producer satellite ('{Pkg}') hosts only the parser", loadKey, _package);
                    return;
                }
                InvokeOnUi(() =>
                {
                    try
                    {
                        // The parser needs the ring-buffer wrapper OverlayPlugin binds to; every other
                        // legacy plugin takes the generic path.
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
            // Host-relayed audio (P6): this satellite's plugin registered a terminal sink, so the host fans
            // a peer's produced call down here. Invoke the local delegate off the command-reader thread (a
            // synchronous TTS engine must not stall the pipe, which also carries the downstream firehose).
            else if (SatelliteProtocol.TryParseSpeak(line, out var speakText, out _, out _, out _))
            {
                _log.LogDebug(LogEvents.SatelliteBooting, "[Audio] relayed TTS received");
                var tts = ActGlobals.oFormActMain?.PlayTtsMethod;
                if (tts != null)
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    { try { tts(speakText); } catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Relayed TTS sink threw"); } });
            }
            else if (SatelliteProtocol.TryParsePlaySound(line, out var sndPath, out var sndVol))
            {
                var snd = ActGlobals.oFormActMain?.PlaySoundMethod;
                if (snd != null)
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    { try { snd(sndPath, sndVol); } catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Relayed sound sink threw"); } });
            }
            // Host-relayed named-callback invoke (P6): the host fanned an invoke to this satellite (it owns
            // the callback). Dispatch to the local delegate(s) off-thread — the callback owns its own
            // thread affinity, as the in-process registry model does.
            else if (SatelliteProtocol.TryParseInvokeCb(line, out var cbName, out var cbArg))
            {
                var act = ActGlobals.oFormActMain;
                if (act != null)
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    { try { act.InvokeNamedCallbackLocal(cbName, cbArg); } catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Relayed named callback threw"); } });
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

        // Audio producer driver (ISOLATION-PLAN P6): stand up the ACT facade with the host route installed,
        // connect the bridge, and drive TTS/PlaySound so the produced call marshals up the pipe. The host
        // fans it to whichever peer satellite registered a sink; this process then exits.
        private static void RunAudioProduce(string tts, string wav, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "Audio-produce: tts={Tts} wav={Wav} pipe={Pipe}", tts, wav, pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            var act = ActGlobals.oFormActMain;

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Audio-produce bridge connect failed");
                Environment.Exit(1);
                return;
            }

            FormActMain.ServiceRoute = new BridgeServiceRoute();
            // Produce through the real facade path: TTS/PlaySound -> ServiceRoute -> SendLine(SPEAK/PLAYSND).
            if (!string.IsNullOrEmpty(tts)) act.TTS(tts);
            if (!string.IsNullOrEmpty(wav)) act.PlaySound(wav);

            System.Threading.Thread.Sleep(500);   // let the writer flush before exit
            SatelliteLogging.Shutdown();
            Environment.Exit(0);
        }

        // Audio sink driver (ISOLATION-PLAN P6): stand up the ACT facade, hijack the PlayTts/PlaySound slots
        // with a recording delegate (as Discord-Triggers does) so the poll registers a terminal host sink,
        // then record every audio call the host relays down the command pipe until shutdown.
        private static void RunAudioSink(string recordPath, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "Audio-sink: record={Record} pipe={Pipe}", recordPath, pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            var act = ActGlobals.oFormActMain;

            var recWriter = new StreamWriter(recordPath, append: false) { AutoFlush = true };
            act.PlayTtsMethod = t => { lock (recWriter) recWriter.WriteLine("TTS|" + t); };
            act.PlaySoundMethod = (w, v) => { lock (recWriter) recWriter.WriteLine(
                "SND|" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + w); };

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Audio-sink bridge connect failed");
                Environment.Exit(1);
                return;
            }

            StartAudioSinkPoll();   // detects the hijack above and sends REGISTERSINK up

            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); Environment.Exit(0); })
                    { IsBackground = true, Name = "audio-sink-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                    HandleCommand(line);   // SPEAK/PLAYSND -> invoke the local (recording) delegate
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Audio-sink command-pipe read failed");
            }
            finally
            {
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Custom-log-line write-back driver (ISOLATION-PLAN P6): SUBSCRIBE to rawlog, write back N custom
        // lines up the event pipe, and record the RawLogLines the host fans back down. No plugin, no facade.
        private static void RunEmitLogLine(string recordPath, int count, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            _log.LogInformation(LogEvents.SatelliteBooting,
                "Emit-logline: record={Record} count={Count} pipe={Pipe}", recordPath, count, pipeName);

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(SatelliteProtocol.StreamRawLog));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Emit-logline bridge connect failed");
                Environment.Exit(1);
                return;
            }

            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); Environment.Exit(0); })
                    { IsBackground = true, Name = "emit-logline-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            // Write the custom lines back after a beat, so both this satellite's and any peer's egress are
            // subscribed before the first line is emitted. Custom lines are id 256+.
            new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(1500);
                    for (int i = 0; i < count; i++)
                        SendLine(SatelliteProtocol.FormatLogLine(256, "P6LINE|" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    _log.LogInformation(LogEvents.SatelliteBooting, "Emit-logline: wrote {Count} custom lines", count);
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Emit-logline write failed"); }
            }) { IsBackground = true, Name = "emit-logline-write" }.Start();

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                using var record = new StreamWriter(recordPath, append: false) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal))
                        record.WriteLine(line);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Emit-logline command-pipe read failed");
            }
            finally
            {
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // EndCombat route-up driver (ISOLATION-PLAN P9a): stand up the facade with a BridgeServiceRoute and
        // RouteEndCombatUp set (exactly a consumer satellite's config), then call the facade EndCombat once.
        // With the flag+route set, EndCombat sends ENDCOMBAT up the bridge; the host injects EndCombatRequested
        // onto the bus and fans it down the swings stream. Plugin-free.
        private static void RunEmitEndCombat(string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "Emit-endcombat: pipe={Pipe}", pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            var act = ActGlobals.oFormActMain;

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(SatelliteProtocol.StreamSwings));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Emit-endcombat bridge connect failed");
                Environment.Exit(1);
                return;
            }

            FormActMain.ServiceRoute = new BridgeServiceRoute();
            FormActMain.RouteEndCombatUp = true;   // a consumer replica routes EndCombat up the bridge
            StartShutdownExit(pipeName);

            // Route one EndCombat up after a settle, so both this satellite's and any peer's egress are
            // subscribed before the request is sent.
            new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(2000);
                    act.EndCombat(true);   // flag+route set → FormatEndCombat up the bridge
                    _log.LogInformation(LogEvents.SatelliteBooting, "Emit-endcombat: routed EndCombat up");
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Emit-endcombat route failed"); }
            }) { IsBackground = true, Name = "emit-endcombat-route" }.Start();

            // Keep the process alive (reading the command pipe) until the host tears us down.
            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                while (reader.ReadLine() != null) { }
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Emit-endcombat command-pipe read failed");
            }
            finally
            {
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // Stand-in write-back seam driver (ISOLATION-PLAN P6, plugin-gated): register the synthetic parser
        // stand-in, subscribe to rawlog, then reflect the _iocContainer → GetService(ILogOutput) → WriteLine
        // exactly as OverlayPlugin's FFXIVRepository.WriteLogLineImpl does, and record the RawLogLine the
        // host fans back down — the real seam a consumer plugin uses for custom lines 256+.
        private static void RunStandInWriteBack(string recordPath, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "StandIn-writeback: record={Record} pipe={Pipe}", recordPath, pipeName);

            FacadeHost.InstallAssemblyResolver();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            EngineTables.Install();

            if (!TryEnsureSdkResolvable())
            {
                _log.LogError(LogEvents.PluginNotFound, "StandIn-writeback needs FFXIV_ACT_Plugin.dll installed");
                Environment.Exit(2);
                return;
            }
            _standIn = Fct.Parser.Legacy.ConsumerStandInFactory.Create(FacadeHost.Log,
                (id, text) => SendLine(SatelliteProtocol.FormatLogLine(id, text)));
            _standIn.Register();

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(SatelliteProtocol.StreamRawLog));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "StandIn-writeback bridge connect failed");
                Environment.Exit(1);
                return;
            }

            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); Environment.Exit(0); })
                    { IsBackground = true, Name = "standin-writeback-shutdown" }.Start();
            }
            catch { /* best-effort */ }

            // After the egress is up, drive the exact OverlayPlugin reflection to write a custom line back.
            new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(1500);
                    ReflectAndWriteCustomLine("STANDIN|hi");
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "StandIn-writeback reflect failed"); }
            }) { IsBackground = true, Name = "standin-writeback-write" }.Start();

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                using var record = new StreamWriter(recordPath, append: false) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal))
                        record.WriteLine(line);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "StandIn-writeback command-pipe read failed");
            }
            finally
            {
                SatelliteLogging.Shutdown();
                Environment.Exit(0);
            }
        }

        // The reflection OverlayPlugin's FFXIVRepository performs to write a custom log line: find the
        // stand-in plugin, read its non-public _iocContainer, GetService(ILogOutput-by-name), then invoke
        // WriteLine((int)id, timestamp, line). Binds only by name/shape — no SDK type referenced here.
        private static void ReflectAndWriteCustomLine(string line)
        {
            const System.Reflection.BindingFlags nonPub = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var act = ActGlobals.oFormActMain;
            var pdata = act.ActPlugins.First(p => p.lblPluginTitle != null &&
                p.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
            var pluginObj = pdata.pluginObj;
            var ioc = pluginObj.GetType().GetField("_iocContainer", nonPub).GetValue(pluginObj);
            var getService = ioc.GetType().GetMethod("GetService");
            // ILogOutput lives in the Costura-embedded FFXIV_ACT_Plugin.Logfile sub-assembly (the exact
            // parentAssemblyName OverlayPlugin passes) — force-load it so its Type is resolvable, then look
            // it up there exactly as GetFFXIVACTPluginIOCService does.
            System.Reflection.Assembly logfileAsm = null;
            try { logfileAsm = System.Reflection.Assembly.Load("FFXIV_ACT_Plugin.Logfile"); } catch { }
            logfileAsm ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "FFXIV_ACT_Plugin.Logfile");
            var ilogType = logfileAsm?.GetType("FFXIV_ACT_Plugin.Logfile.ILogOutput");
            var logOutput = getService.Invoke(ioc, new object[] { ilogType });
            var writeLine = logOutput.GetType().GetMethod("WriteLine");
            writeLine.Invoke(logOutput, new object[] { 256, DateTime.Now, line });
            _log.LogInformation(LogEvents.SatelliteBooting, "StandIn-writeback: wrote custom line via ILogOutput reflection");
        }

        // Named-callback register driver (ISOLATION-PLAN P6): register a facade callback that appends each
        // invoke's argument to a record file, then serve host-fanned invokes off the command pipe.
        private static void RunCallbackRegister(string name, string recordPath, string pipeName)
        {
            if (!CallbackDriverBoot("cb-register", pipeName)) return;
            var recWriter = new StreamWriter(recordPath, append: false) { AutoFlush = true };
            ActGlobals.oFormActMain.RegisterNamedCallback(name,
                arg => { lock (recWriter) recWriter.WriteLine(arg?.ToString() ?? ""); });
            _log.LogInformation(LogEvents.SatelliteBooting, "Cb-register: registered '{Name}'", name);

            StartShutdownExit(pipeName);
            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                    HandleCommand(line);   // INVOKECB -> InvokeNamedCallbackLocal -> the recording callback
            }
            catch (Exception ex) { _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Cb-register command-pipe read failed"); }
            finally { SatelliteLogging.Shutdown(); Environment.Exit(0); }
        }

        // Named-callback invoke driver (ISOLATION-PLAN P6): invoke a callback registered elsewhere, once.
        private static void RunCallbackInvoke(string name, string arg, string pipeName)
        {
            if (!CallbackDriverBoot("cb-invoke", pipeName)) return;
            System.Threading.Thread.Sleep(1500);   // let the target's registration reach the host
            ActGlobals.oFormActMain.InvokeNamedCallback(name, arg);
            _log.LogInformation(LogEvents.SatelliteBooting, "Cb-invoke: invoked '{Name}' with '{Arg}'", name, arg);
            System.Threading.Thread.Sleep(500);
            SatelliteLogging.Shutdown();
            Environment.Exit(0);
        }

        // Named-callback self-test driver (ISOLATION-PLAN P6): register AND invoke in one satellite and
        // record what comes back — proving the host is the single fan point (origin receives its own).
        private static void RunCallbackSelfTest(string name, string arg, string recordPath, string pipeName)
        {
            if (!CallbackDriverBoot("cb-selftest", pipeName)) return;
            var recWriter = new StreamWriter(recordPath, append: false) { AutoFlush = true };
            ActGlobals.oFormActMain.RegisterNamedCallback(name,
                arg2 => { lock (recWriter) recWriter.WriteLine(arg2?.ToString() ?? ""); });

            StartShutdownExit(pipeName);
            new System.Threading.Thread(() =>
            {
                try { System.Threading.Thread.Sleep(1500); ActGlobals.oFormActMain.InvokeNamedCallback(name, arg); }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "Cb-selftest invoke failed"); }
            }) { IsBackground = true, Name = "cb-selftest-invoke" }.Start();

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                    HandleCommand(line);
            }
            catch (Exception ex) { _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "Cb-selftest command-pipe read failed"); }
            finally { SatelliteLogging.Shutdown(); Environment.Exit(0); }
        }

        // Shared boot for the callback drivers: stand up the ACT facade, connect the event pipe (READY +
        // empty roster), and install the host route. Returns false when the bridge connect failed (the
        // process has already exited via Environment.Exit).
        private static bool CallbackDriverBoot(string mode, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "{Mode}: pipe={Pipe}", mode, pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "{Mode} bridge connect failed", mode);
                Environment.Exit(1);
                return false;
            }
            FormActMain.ServiceRoute = new BridgeServiceRoute();
            return true;
        }

        // Exit the process the instant the host signals shutdown (drivers with no message loop).
        private static void StartShutdownExit(string pipeName)
        {
            try
            {
                var shutdown = System.Threading.EventWaitHandle.OpenExisting(pipeName + "-shutdown");
                new System.Threading.Thread(() => { shutdown.WaitOne(); Environment.Exit(0); })
                    { IsBackground = true, Name = "driver-shutdown" }.Start();
            }
            catch { /* best-effort */ }
        }

        // Combined P6 soak sink (ISOLATION-PLAN P6): hijack the audio slots, register a named callback,
        // subscribe to rawlog, and record every audio call, callback invoke, and fanned log line the host
        // routes here — the receiving end of the three-concern soak.
        private static void RunP6Sink(string recordPath, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "P6-sink: record={Record} pipe={Pipe}", recordPath, pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            var act = ActGlobals.oFormActMain;
            var rec = new StreamWriter(recordPath, append: false) { AutoFlush = true };
            void Write(string s) { lock (rec) rec.WriteLine(s); }

            act.PlayTtsMethod = t => Write("TTS|" + t);
            act.PlaySoundMethod = (w, v) => Write("SND|" + w);

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(SatelliteProtocol.StreamRawLog));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "P6-sink bridge connect failed");
                Environment.Exit(1);
                return;
            }

            FormActMain.ServiceRoute = new BridgeServiceRoute();
            act.RegisterNamedCallback("soak.cb", a => Write("CB|" + (a?.ToString() ?? "")));
            StartAudioSinkPoll();
            StartShutdownExit(pipeName);

            try
            {
                _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                _cmdPipe.Connect(30000);
                using var reader = new StreamReader(_cmdPipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (GameEventFrame.TryParse(line, out var evt) && evt is Fct.Abstractions.RawLogLine r)
                        Write("RAW|" + r.Line);
                    else
                        HandleCommand(line);   // SPEAK/PLAYSND/INVOKECB -> the recording delegate/callback
                }
            }
            catch (Exception ex) { _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "P6-sink command-pipe read failed"); }
            finally { SatelliteLogging.Shutdown(); Environment.Exit(0); }
        }

        // Combined P6 soak driver (ISOLATION-PLAN P6): produce audio, invoke the named callback, and write a
        // log line — count times — and record the log lines the host fans back to this origin.
        private static void RunP6Drive(string recordPath, int count, string pipeName)
        {
            SatelliteLogging.Initialize(_satelliteId);
            _log = SatelliteLogging.Log;
            FacadeHost.Log = SatelliteLogging.WriteLegacy;
            _log.LogInformation(LogEvents.SatelliteBooting, "P6-drive: record={Record} count={Count} pipe={Pipe}", recordPath, count, pipeName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FacadeHost.CreateAct();
            var act = ActGlobals.oFormActMain;

            try
            {
                _bridge = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                _bridge.Connect(5000);
                _writer = new StreamWriter(_bridge) { AutoFlush = true };
                SendLine(SatelliteProtocol.FormatReady(
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    Environment.Is64BitProcess, Environment.Version.ToString(), _satelliteId, _package));
                SendLine(SatelliteProtocol.PluginsEnd);
                SendLine(SatelliteProtocol.FormatSubscribe(SatelliteProtocol.StreamRawLog));
            }
            catch (Exception ex)
            {
                _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "P6-drive bridge connect failed");
                Environment.Exit(1);
                return;
            }

            FormActMain.ServiceRoute = new BridgeServiceRoute();
            StartShutdownExit(pipeName);

            // Record the log lines the host fans back to this origin on a background thread.
            new System.Threading.Thread(() =>
            {
                try
                {
                    _cmdPipe = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    _cmdPipe.Connect(30000);
                    using var reader = new StreamReader(_cmdPipe);
                    using var rec = new StreamWriter(recordPath, append: false) { AutoFlush = true };
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (GameEventFrame.TryParse(line, out var evt) && evt is Fct.Abstractions.RawLogLine r)
                            rec.WriteLine("RAW|" + r.Line);
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBridgeConnectFailed, ex, "P6-drive record failed"); }
            }) { IsBackground = true, Name = "p6-drive-record" }.Start();

            // Drive all three concerns in a loop after a settle so the sink's registrations are in place.
            System.Threading.Thread.Sleep(2000);
            for (int i = 0; i < count; i++)
            {
                act.TTS("tts" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                act.PlaySound("snd" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                act.InvokeNamedCallback("soak.cb", "cb" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SendLine(SatelliteProtocol.FormatLogLine(256, "P6LINE|" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            _log.LogInformation(LogEvents.SatelliteBooting, "P6-drive: drove {Count} of each concern", count);
            System.Threading.Thread.Sleep(2000);   // let the fan drain before the host tears us down
        }

        // The host-routed service seam (P6): the facade calls these on FormActMain.ServiceRoute; each is
        // marshaled up the event pipe. Installed only when bridged (a standalone run leaves ServiceRoute
        // null so the facade's local delegate slots serve directly).
        private sealed class BridgeServiceRoute : IHostServiceRoute
        {
            public void Speak(string text, int volume, int channel, bool synchronous)
                => SendLine(SatelliteProtocol.FormatSpeak(text ?? "", volume, channel, synchronous));
            public void PlaySound(string filePath, int volume)
                => SendLine(SatelliteProtocol.FormatPlaySound(filePath ?? "", volume));
            public void RegisterCallback(string name, bool allowDuplicate)
                => SendLine(SatelliteProtocol.FormatRegisterCb(name ?? "", allowDuplicate));
            public void UnregisterCallback(string name)
                => SendLine(SatelliteProtocol.FormatUnregisterCb(name ?? ""));
            public void InvokeCallback(string name, object argument)
                => SendLine(SatelliteProtocol.FormatInvokeCb(name ?? "", argument?.ToString() ?? ""));
            public void EndCombat(bool export)
                => SendLine(SatelliteProtocol.FormatEndCombat(export));
        }

        private static System.Threading.Timer _audioSinkPoll;
        private static bool _ttsSinkAnnounced;
        private static bool _soundSinkAnnounced;

        // Poll the ACT audio slots for a plugin takeover — they must stay plain fields (precompiled plugins
        // bind them by ldfld/stfld), so assignment can't be intercepted — and announce REGISTERSINK/
        // UNREGISTERSINK as the state flips. Runs off a threadpool timer (not the WinForms pump) so it works
        // in both the production message-loop boot and the headless audio-sink driver. Low rate: a plugin
        // takes over a slot at enable-time, long before combat, so a ~1 s cadence is ample.
        private static void StartAudioSinkPoll()
        {
            _audioSinkPoll = new System.Threading.Timer(_ =>
            {
                try
                {
                    var act = ActGlobals.oFormActMain;
                    if (act == null) return;
                    bool tts = act.TtsHijacked;
                    if (tts != _ttsSinkAnnounced)
                    {
                        _ttsSinkAnnounced = tts;
                        SendLine(tts ? SatelliteProtocol.FormatRegisterSink("tts") : SatelliteProtocol.FormatUnregisterSink("tts"));
                        _log.LogInformation(LogEvents.SatelliteBooting, "[Audio] tts sink {State}", tts ? "registered" : "released");
                    }
                    bool snd = act.SoundHijacked;
                    if (snd != _soundSinkAnnounced)
                    {
                        _soundSinkAnnounced = snd;
                        SendLine(snd ? SatelliteProtocol.FormatRegisterSink("sound") : SatelliteProtocol.FormatUnregisterSink("sound"));
                        _log.LogInformation(LogEvents.SatelliteBooting, "[Audio] sound sink {State}", snd ? "registered" : "released");
                    }
                }
                catch { /* best-effort; the next tick retries */ }
            }, null, 250, 1000);
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

        // Diagnostic: log every loaded plugin's status label on a self-stopping WinForms timer, so a
        // consumer plugin's async init progress is observable in the satellite log (opt-in).
        private static void StartConsumerStatusPoll()
        {
            int ticks = 0;
            var t = new Timer { Interval = 2000 };
            t.Tick += (s, e) =>
            {
                ticks++;
                try
                {
                    foreach (var p in ActGlobals.oFormActMain.ActPlugins)
                    {
                        _log.LogInformation(LogEvents.SatelliteBooting,
                            "[Status] '{Title}' = '{Status}'", p.lblPluginTitle?.Text, p.lblPluginStatus?.Text);
                        // Every 5th tick, dump the plugin's own in-UI log (e.g. OverlayPlugin's ControlPanel
                        // logBox) — the only place its internal logger surfaces in a release build.
                        if (ticks % 5 == 0 && p.tpPluginSpace != null)
                        {
                            var text = FindLargestText(p.tpPluginSpace);
                            if (!string.IsNullOrEmpty(text))
                            {
                                var head = text.Length > 4000 ? text.Substring(0, 4000) : text;
                                _log.LogInformation(LogEvents.SatelliteBooting,
                                    "[Status] '{Title}' UI log head ({Len} chars total):\n{Log}",
                                    p.lblPluginTitle?.Text, text.Length, head);
                                var interesting = string.Join("\n", text
                                    .Split('\n')
                                    .Where(l => l.IndexOf("WSServer", StringComparison.OrdinalIgnoreCase) >= 0
                                             || l.IndexOf("LoadConfig", StringComparison.OrdinalIgnoreCase) >= 0
                                             || l.IndexOf("InitPlugin:", StringComparison.Ordinal) >= 0));
                                if (interesting.Length > 6000) interesting = interesting.Substring(0, 6000);
                                _log.LogInformation(LogEvents.SatelliteBooting,
                                    "[Status] '{Title}' UI log filtered:\n{Log}", p.lblPluginTitle?.Text, interesting);
                                ProbeOverlayConfig(p.pluginObj);
                            }
                        }
                    }
                }
                catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "[Status] poll failed"); }
                if (ticks >= 15) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        // Diagnostic: reflect OverlayPlugin's live Config (PluginLoader.pluginMain -> PluginMain.Config)
        // and log the WSServer flags + the config file path it actually loaded.
        private static void ProbeOverlayConfig(object pluginObj)
        {
            try
            {
                const System.Reflection.BindingFlags any =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                var main = pluginObj?.GetType().GetField("pluginMain", any)?.GetValue(pluginObj);
                if (main == null) return;
                var cfg = main.GetType().GetProperty("Config", any)?.GetValue(main);
                if (cfg == null) { _log.LogInformation(LogEvents.SatelliteBooting, "[Status] overlay Config=null"); return; }
                var t = cfg.GetType();
                var path = t.GetField("filePath", any)?.GetValue(cfg);
                var running = t.GetProperty("WSServerRunning", any)?.GetValue(cfg);
                var ip = t.GetProperty("WSServerIP", any)?.GetValue(cfg);
                var port = t.GetProperty("WSServerPort", any)?.GetValue(cfg);
                _log.LogInformation(LogEvents.SatelliteBooting,
                    "[Status] overlay Config: WSServerRunning={Run} IP={Ip} Port={Port} file='{File}'",
                    running, ip, port, path);
            }
            catch (Exception ex) { _log.LogError(LogEvents.SatelliteBooting, ex, "[Status] overlay config probe failed"); }
        }

        // Depth-first: the longest Text of any TextBoxBase under the control tree (a plugin's log view).
        private static string FindLargestText(System.Windows.Forms.Control root)
        {
            string best = "";
            foreach (System.Windows.Forms.Control c in root.Controls)
            {
                if (c is System.Windows.Forms.TextBoxBase tb && (tb.Text?.Length ?? 0) > best.Length)
                    best = tb.Text;
                var inner = FindLargestText(c);
                if (inner.Length > best.Length) best = inner;
            }
            return best;
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
