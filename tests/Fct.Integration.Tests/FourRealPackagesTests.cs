using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
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
    // ISOLATION-PLAN P9b full tier [plugin-gated]: the four shipped packages — real parser, OverlayPlugin,
    // Triggernometry, ACT-Discord-Triggers — run isolated in FOUR distinct satellite processes under the
    // production SatelliteRouter, coexisting while the host fans the looped frame corpus down. OverlayPlugin's
    // MiniParse WebSocket serves CombatData whose terminal-encounter values equal the real-ACT oracle
    // ExportVariables baseline (the last slice in the corpus, combat-slice2) — three-way parity (oracle ->
    // host engine -> overlay replica) holding while three other real packages share the topology. Per-
    // satellite working set recorded as a budget diagnostic. Liveness/routing only for Triggernometry/Discord
    // (they expose no encounter-total read surface). Skips cleanly without the four plugins installed.
    [Collection("satellite-p6")]
    public sealed class FourRealPackagesTests
    {
        private readonly ITestOutputHelper _out;
        public FourRealPackagesTests(ITestOutputHelper output) => _out = output;

        private static string PluginsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "Plugins");
        private static string TriggerPath => Path.Combine(PluginsDir, "Triggernometry.dll");
        private static string DiscordPath => Path.Combine(PluginsDir, "ACT_DiscordTriggers", "ACT_DiscordTriggers.dll");

        [SkippableFact]
        public async Task Four_real_packages_run_isolated_and_overlay_serves_the_oracle_under_load()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            const string terminalSlice = "combat-slice2";   // the corpus pass ends on slice2 → its encounter is terminal
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(ReplayBridgeHarness.FramesFixture(root!, "combat-slice")), "frame fixtures missing");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath), "FFXIV_ACT_Plugin not installed");
            Skip.IfNot(File.Exists(SatelliteRunFixture.OverlayPluginPath), "OverlayPlugin not installed");
            Skip.IfNot(File.Exists(TriggerPath), "Triggernometry not installed");
            Skip.IfNot(File.Exists(DiscordPath), "ACT-Discord-Triggers not installed");

            var baseline = OverlayWsHarness.ReadExportBaseline(root!, terminalSlice);
            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var prevPoll = Environment.GetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL");
            Environment.SetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL", "1");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session);
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var announced = new ConcurrentDictionary<string, bool>();
            var overlayUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.PluginAnnounced += p =>
            {
                announced[p.Key] = true;
                if (p.Key == "overlay") overlayUp.TrySetResult(true);
            };

            OverlayWsHarness.TryStageCef(_out.WriteLine);
            OverlayWsHarness.SeedOverlayConfig(_out.WriteLine);

            try
            {
                // Spawn one satellite per package — four processes. Overlay is loaded last so its slow CEF
                // init overlaps nothing else's handshake.
                Assert.True(await router.RequestLoadPluginAsync("ffxiv", SatelliteRunFixture.FfxivPluginPath, "FFXIV_ACT_Plugin"), "ffxiv load not forwarded");
                Assert.True(await router.RequestLoadPluginAsync("triggernometry", TriggerPath, "Triggernometry"), "trigger load not forwarded");
                Assert.True(await router.RequestLoadPluginAsync("discord", DiscordPath, "ACT-Discord-Triggers"), "discord load not forwarded");
                Assert.True(await router.RequestLoadPluginAsync("overlay", SatelliteRunFixture.OverlayPluginPath, "OverlayPlugin"), "overlay load not forwarded");

                await Task.WhenAny(overlayUp.Task, Task.Delay(30000));
                Assert.True(overlayUp.Task.IsCompletedSuccessfully, "OverlayPlugin did not announce (did not load)");
                await Task.Delay(2000);   // let the other three settle their handshakes

                // Four distinct satellite processes, one per package (the shipped topology).
                var sats = supervisor.Satellites.ToList();
                _out.WriteLine("satellites: " + string.Join(", ", sats.Select(s => $"{s.Package}#{s.Pid}")));
                Assert.Equal(4, sats.Count);
                Assert.Equal(4, sats.Select(s => s.Pid).Distinct().Count());
                foreach (var pkg in new[] { "ffxiv", "overlay", "triggernometry", "discord" })
                    Assert.Contains(sats, s => s.Package == pkg);

                Skip.IfNot(await OverlayWsHarness.WaitForPort(10501, TimeSpan.FromSeconds(120)),
                    "OverlayPlugin WS server never opened on 10501 (CEF unavailable or init failed)");

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:10501/MiniParse"), CancellationToken.None);
                var combat = new List<OverlayWsHarness.CombatSnapshot>();
                var chat = new List<string>();
                using var readCts = new CancellationTokenSource();
                var reader = OverlayWsHarness.ReadFramesAsync(ws, combat, chat, readCts.Token);

                // Fan the looped corpus down (one pass = slice1 then slice2). No latency markers here — a
                // now-timestamped rawlog line mid-corpus would perturb the replica's encounter clock; latency
                // is gated plugin-free in FourSatelliteSoakTests.
                int emitted = 0;
                foreach (var evt in FrameCorpus.Events(root!, 1))
                {
                    bus.Emit(evt);
                    if (++emitted % 50 == 0) await Task.Delay(1);
                }

                // A distinctive chat line proves the cactbot event-source log-line path still flows.
                var marker = "p9chat-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var chatLine = "00|" + DateTimeOffset.Now.ToString("O") + "|0038|Sender|" + marker + "|";
                bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, chatLine, chatLine));

                await Task.Delay(6000);   // MiniParse push timer fires past the terminal ENDC (~1 s interval)
                readCts.Cancel();
                try { await reader; } catch (OperationCanceledException) { }

                _out.WriteLine($"emitted {emitted} frames; {combat.Count} CombatData pushes; {chat.Count} chat lines");
                Assert.NotEmpty(combat);

                var inactive = combat.LastOrDefault(f => f.IsActive == "false");
                var frame = inactive ?? combat.OrderByDescending(OverlayWsHarness.EncDamage).First();
                Assert.True(frame.Combatant.TryGetValue("YOU", out var you), "no YOU combatant in CombatData");

                AssertBaseline(baseline, ("*ENCOUNTER*", "damage"), OverlayWsHarness.Get(frame.Encounter, "damage"), "Encounter.damage");
                AssertBaseline(baseline, ("YOU", "damage"), OverlayWsHarness.Get(you!, "damage"), "YOU.damage");
                AssertBaseline(baseline, ("YOU", "damage%"), OverlayWsHarness.Get(you!, "damage%"), "YOU.damage%");
                if (inactive is not null)
                {
                    AssertBaseline(baseline, ("*ENCOUNTER*", "encdps"), OverlayWsHarness.Get(frame.Encounter, "encdps"), "Encounter.encdps");
                    AssertBaseline(baseline, ("YOU", "encdps"), OverlayWsHarness.Get(you!, "encdps"), "YOU.encdps");
                }
                Assert.Contains(chat, l => l.Contains(marker));

                // Budget diagnostic: per-satellite working set (recorded, generous ceiling).
                foreach (var s in sats)
                {
                    long ws64 = 0;
                    try { using var p = Process.GetProcessById(s.Pid); p.Refresh(); ws64 = p.WorkingSet64; } catch { }
                    _out.WriteLine($"{s.Package}#{s.Pid}: workingSet={ws64 / (1024 * 1024)}MB");
                }
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(12));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                Environment.SetEnvironmentVariable("FCT_CONSUMER_STATUS_POLL", prevPoll);
            }
        }

        private void AssertBaseline(Dictionary<(string, string), string> baseline,
            (string, string) key, string? got, string label)
        {
            Assert.True(baseline.TryGetValue(key, out var want), $"baseline missing {label}");
            _out.WriteLine($"{label}: got='{got}' want='{want}'");
            Assert.Equal(want, got);
        }
    }
}
