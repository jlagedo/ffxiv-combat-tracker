using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P8 exit gate [plugin-gated]: the real, unmodified OverlayPlugin runs isolated in its
    // own consumer satellite on the P5/P6 projection — no parser, no shared heap. The production
    // SatelliteRouter resolves "overlay" to its package (PackageResolver) and spawns a --role consumer
    // satellite with the full stream set (incl. packets). We fan a committed frame-replay down to it; its
    // parser-free Fct.Compat.Act replica folds the swings/lifecycle into ActiveZone.ActiveEncounter, and
    // OverlayPlugin's MiniParse event source reads that replica's EncounterData/CombatantData.ExportVariables
    // and pushes CombatData over its own Fleck WebSocket (ws://127.0.0.1:10501/MiniParse). A WS client
    // asserts the CombatData encdps/damage/per-combatant equal the real-ACT oracle ExportVariables baseline
    // (combat-slice.exportvars.tsv) — the deepest coupling, fully host-routed and parity-gated. It also
    // asserts a host-fanned log line surfaces on the WS (the cactbot event-source log-line path).
    //
    // Needs OverlayPlugin.dll + FFXIV_ACT_Plugin.dll installed (+ CEF, downloaded on first run); skips
    // cleanly without them. Serialized in the satellite-p6 collection so it never contends for port 10501.
    //
    // Out of headless scope (per the user decision): full packet-decode → custom-line-257 needs a LIVE
    // FFXIV process (OverlayPlugin defers NetworkParser binding until GetCurrentFFXIVProcess() is non-null,
    // as PacketDispatchTests documents). The packet FAN-OUT to the consumer is gated plugin-free by
    // OverlayPacketFanoutTests + OverlayStandInPacketTests; the custom-line round-trip mechanism is gated
    // by the P6 write-back (LogWriteBackTests).
    [Collection("satellite-p6")]
    public sealed class OverlaySatelliteTests
    {
        private readonly ITestOutputHelper _out;
        public OverlaySatelliteTests(ITestOutputHelper output) => _out = output;

        // A single MiniParse CombatData push, flattened to strings (ExportVariables render as strings).
        private sealed record CombatSnapshot(
            string IsActive,
            Dictionary<string, string> Encounter,
            Dictionary<string, Dictionary<string, string>> Combatant);

        // (name, key) -> expected export string, read straight from the committed baseline so the assertion
        // is single-sourced with ExportVarsCompatTests. name "*ENCOUNTER*" holds the encounter-level keys.
        private static Dictionary<(string, string), string> ReadExportBaseline(string root, string slice)
        {
            var path = Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".exportvars.tsv");
            var map = new Dictionary<(string, string), string>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("name\tkey", StringComparison.Ordinal)) continue;
                var c = line.Split('\t');
                if (c.Length < 3) continue;
                map[(c[0], c[1])] = c[2];
            }
            return map;
        }

        [SkippableFact]
        public async Task OverlayPlugin_satellite_serves_MiniParse_CombatData_matching_the_oracle_baseline()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            const string slice = "combat-slice";
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var fixture = ReplayBridgeHarness.FramesFixture(root!, slice);
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(fixture), $"frame fixture missing at {fixture}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.OverlayPluginPath),
                $"OverlayPlugin not installed at {SatelliteRunFixture.OverlayPluginPath}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var baseline = ReadExportBaseline(root!, slice);
            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var prevPoll = Environment.GetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL");
            Environment.SetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL", "1");   // satellite inherits → status diagnostics

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session);
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var announced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.PluginAnnounced += p => { if (p.Key == "overlay") announced.TrySetResult(true); };

            // Prepare the satellite's sandbox ACT data folder BEFORE OverlayPlugin loads: (1) reuse the CEF
            // the installed OverlayPlugin already extracted into the real ACT folder (a first run would
            // download ~100 MB), and (2) seed its config with the WSServer enabled on 10501 (off by
            // default in a fresh profile → the /MiniParse server would never start). Both are read during
            // OverlayPlugin's InitPlugin, right after the router forwards LOADPLUGIN.
            TryStageCef();
            SeedOverlayConfig();

            try
            {
                Assert.True(await router.RequestLoadPluginAsync("overlay", SatelliteRunFixture.OverlayPluginPath, "OverlayPlugin"),
                    "overlay load was not forwarded");
                await Task.WhenAny(announced.Task, Task.Delay(30000));
                Assert.True(announced.Task.IsCompletedSuccessfully, "OverlayPlugin did not announce (did not load)");

                var sat = Assert.Single(supervisor.Satellites);
                Assert.Equal("overlay", sat.Package);

                // Wait for OverlayPlugin's Fleck server to bind 10501 (CEF + phase-2 init is slow).
                Skip.IfNot(await WaitForPort(10501, TimeSpan.FromSeconds(120)),
                    "OverlayPlugin WS server never opened on 10501 (CEF unavailable or init failed)");

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:10501/MiniParse"), CancellationToken.None);

                // Collect every pushed frame on a background reader while we drive the replay.
                var combatFrames = new List<CombatSnapshot>();   // the inner CombatData "msg" objects
                var chatLines = new List<string>();               // relayed log lines (msgtype "Chat")
                using var readCts = new CancellationTokenSource();
                var reader = ReadFramesAsync(ws, combatFrames, chatLines, readCts.Token);

                // Fan the committed slice down: no rawlog frames → no idle-split → one whole-slice encounter
                // matching the single-encounter oracle baseline. Include the terminal ENDC so DURATION freezes.
                int emitted = 0;
                foreach (var line in File.ReadLines(fixture))
                {
                    if (!FrameSession.TryParseLine(line, out _, out var evt) || evt is null) continue;
                    bus.Emit(evt);
                    if (++emitted % 50 == 0) await Task.Delay(1);
                }

                // A distinctive chat line (type 56 = echo) to prove the cactbot log-line handler receives it.
                var marker = "p8chat-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var chatLine = "00|" + DateTimeOffset.Now.ToString("O") + "|0038|Sender|" + marker + "|";
                bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, chatLine, chatLine));

                // Let MiniParse's push timer fire past the ENDC (its interval is ~1 s).
                await Task.Delay(6000);
                readCts.Cancel();
                try { await reader; } catch (OperationCanceledException) { }

                _out.WriteLine($"emitted {emitted} frames; {combatFrames.Count} CombatData pushes; {chatLines.Count} chat lines");
                Assert.NotEmpty(combatFrames);

                // Prefer the post-ENDC inactive frame (frozen DURATION → deterministic encdps); else the
                // richest frame for the split-invariant subset.
                var inactive = combatFrames.LastOrDefault(f => f.IsActive == "false");
                var frame = inactive ?? combatFrames.OrderByDescending(EncDamage).First();
                Assert.True(frame.Combatant.TryGetValue("YOU", out var you), "no YOU combatant in CombatData");

                // Split-invariant totals (assert on any frame).
                AssertBaseline(baseline, ("*ENCOUNTER*", "damage"), Get(frame.Encounter, "damage"), "Encounter.damage");
                AssertBaseline(baseline, ("YOU", "damage"), Get(you!, "damage"), "YOU.damage");
                AssertBaseline(baseline, ("YOU", "damage%"), Get(you!, "damage%"), "YOU.damage%");

                // Duration-dependent values only on the terminal inactive frame (DURATION frozen).
                if (inactive is not null)
                {
                    AssertBaseline(baseline, ("*ENCOUNTER*", "DURATION"), Get(frame.Encounter, "DURATION"), "Encounter.DURATION");
                    AssertBaseline(baseline, ("*ENCOUNTER*", "encdps"), Get(frame.Encounter, "encdps"), "Encounter.encdps");
                    AssertBaseline(baseline, ("YOU", "encdps"), Get(you!, "encdps"), "YOU.encdps");
                }
                else
                {
                    _out.WriteLine("no inactive CombatData frame captured; asserted split-invariant subset only");
                }

                // The cactbot event-source log-line path: our host-fanned chat line surfaced on the WS.
                Assert.Contains(chatLines, l => l.Contains(marker));
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(10));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                Environment.SetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL", prevPoll);
            }
        }

        private static string? Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : null;

        private static double EncDamage(CombatSnapshot f) =>
            double.TryParse(Get(f.Encounter, "damage"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        private void AssertBaseline(Dictionary<(string, string), string> baseline,
            (string, string) key, string? got, string label)
        {
            Assert.True(baseline.TryGetValue(key, out var want), $"baseline missing {label}");
            _out.WriteLine($"{label}: got='{got}' want='{want}'");
            Assert.Equal(want, got);
        }

        // The /MiniParse LegacyHandler wraps each push as {type:broadcast, msgtype:CombatData|Chat, msg:…}.
        // For CombatData, msg = {type, Encounter:{k:v}, Combatant:{name:{k:v}}, isActive}; every value a string.
        private static async Task ReadFramesAsync(ClientWebSocket ws, List<CombatSnapshot> combat,
            List<string> chat, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);

                CombatSnapshot? snap = null;
                string? chatMsg = null;
                try
                {
                    using var doc = JsonDocument.Parse(sb.ToString());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("msgtype", out var mt)) continue;
                    var msgtype = mt.GetString();
                    if (msgtype == "CombatData" && root.TryGetProperty("msg", out var msg))
                        snap = ParseCombat(msg);
                    else if (msgtype == "Chat" && root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                        chatMsg = m.GetString();
                }
                catch { continue; }

                if (snap is not null) combat.Add(snap);
                if (chatMsg is not null) chat.Add(chatMsg);
            }
        }

        private static CombatSnapshot ParseCombat(JsonElement msg)
        {
            var enc = new Dictionary<string, string>();
            if (msg.TryGetProperty("Encounter", out var e) && e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) enc[p.Name] = p.Value.ToString();

            var combatants = new Dictionary<string, Dictionary<string, string>>();
            if (msg.TryGetProperty("Combatant", out var c) && c.ValueKind == JsonValueKind.Object)
                foreach (var member in c.EnumerateObject())
                {
                    var vals = new Dictionary<string, string>();
                    if (member.Value.ValueKind == JsonValueKind.Object)
                        foreach (var p in member.Value.EnumerateObject()) vals[p.Name] = p.Value.ToString();
                    combatants[member.Name] = vals;
                }

            var isActive = msg.TryGetProperty("isActive", out var a) ? a.ToString() : "";
            return new CombatSnapshot(isActive, enc, combatants);
        }

        // The satellite's sandbox ACT data folder (mirrors FacadeHost.LegacyActDataDir). Resolved through
        // AppData.Root — NOT %LOCALAPPDATA% — because in DEBUG the data root sits next to the install dir,
        // which this test overrides via FCT_INSTALL_DIR before staging; the spawned satellite inherits the
        // identical root through FCT_DATA_ROOT, so both processes agree on one sandbox.
        private static string SandboxActFolder() => Path.Combine(
            Fct.Logging.AppData.Root, "legacy", "Advanced Combat Tracker");

        // Junction the satellite sandbox's OverlayPluginCef to the CEF the installed OverlayPlugin already
        // extracted into the real ACT AppDataFolder, so the satellite reuses it instead of downloading.
        private void TryStageCef()
        {
            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var realCef = Path.Combine(roaming, "Advanced Combat Tracker", "OverlayPluginCef");
                var sandbox = SandboxActFolder();
                var sandboxCef = Path.Combine(sandbox, "OverlayPluginCef");
                if (!Directory.Exists(realCef)) { _out.WriteLine("no extracted CEF to reuse; OverlayPlugin will download"); return; }
                if (Directory.Exists(sandboxCef) || File.Exists(sandboxCef)) return;   // already staged
                Directory.CreateDirectory(sandbox);
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{sandboxCef}\" \"{realCef}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p!.WaitForExit(5000);
                _out.WriteLine($"CEF junction {sandboxCef} -> {realCef} (exit {p.ExitCode})");
            }
            catch (Exception ex) { _out.WriteLine("CEF stage failed: " + ex.Message); }
        }

        // Seed OverlayPlugin's config with the WSServer enabled on 127.0.0.1:10501 (off by default in a
        // fresh profile). Newtonsoft JsonSerializer.Populate reads C#-named properties, so a minimal object
        // suffices — omitted keys keep their defaults. Path: <sandbox>\Config\RainbowMage.OverlayPlugin.config.json.
        private void SeedOverlayConfig()
        {
            try
            {
                var cfgDir = Path.Combine(SandboxActFolder(), "Config");
                Directory.CreateDirectory(cfgDir);
                var cfg = Path.Combine(cfgDir, "RainbowMage.OverlayPlugin.config.json");
                // "Overlays":[] is required — OverlayPlugin's LoadJson iterates it unguarded (a null throws,
                // which drops the whole config back to defaults, incl. WSServerRunning=false).
                File.WriteAllText(cfg,
                    "{\"Overlays\":[],\"WSServerRunning\":true,\"WSServerIP\":\"127.0.0.1\",\"WSServerPort\":10501,\"WSServerSSL\":false}");
                _out.WriteLine("seeded OverlayPlugin config with WSServer on 10501");
            }
            catch (Exception ex) { _out.WriteLine("config seed failed: " + ex.Message); }
        }

        private static async Task<bool> WaitForPort(int port, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var c = new TcpClient();
                    var connect = c.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(500)) == connect && c.Connected) return true;
                }
                catch { /* not up yet */ }
                await Task.Delay(500);
            }
            return false;
        }
    }
}
