using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P7 topology gate: the production SatelliteRouter over the SatelliteSupervisor spawns
    // ONE satellite process per legacy package. Each fixture resolves (by its id/title token, via the real
    // PackageResolver) to its production package: the trigger fixture → the "triggernometry" consumer
    // satellite; the sink fixture → the "discord" consumer satellite. A rawlog line the host fans down
    // fires the trigger in the triggernometry satellite; its TTS routes UP to the single shared host audio
    // and fans DOWN to the discord satellite's registered terminal sink. Proves the whole cross-satellite
    // trigger→sink path across separate processes through the production router — Gate A is plugin-free;
    // Gate B additionally stands the real FFXIV_ACT_Plugin producer up as the third process.
    [Collection("satellite-p6")]
    public sealed class ThreeSatelliteTopologyTests
    {
        private readonly ITestOutputHelper _out;
        public ThreeSatelliteTopologyTests(ITestOutputHelper output) => _out = output;

        // The sink satellite keeps its record file open; read it with a read/write share so an open
        // writer handle never blocks the assert.
        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }

        [SkippableFact]
        public async Task Router_spawns_per_package_consumer_satellites_and_routes_trigger_to_sink()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var triggerDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.TriggerFixture");
            var sinkDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.SinkFixture");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(triggerDll), $"trigger fixture not built at {triggerDll}");
            Skip.IfNot(File.Exists(sinkDll), $"sink fixture not built at {sinkDll}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            var prevRecord = Environment.GetEnvironmentVariable("FCT_SINK_RECORD");
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-p7topo-" + Guid.NewGuid().ToString("N") + ".tsv");
            Environment.SetEnvironmentVariable("FCT_SINK_RECORD", record);

            var payload = "p7topo-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var marker = "00|" + DateTimeOffset.Now.ToString("O") + "|FCT_TRIGGER:" + payload;

            var audio = new RecordingAudioOutput();
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session, audio: audio);
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var announced = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.PluginAnnounced += p =>
            {
                announced[p.Key] = true;
                if (announced.ContainsKey("triggernometry") && announced.ContainsKey("discord")) both.TrySetResult(true);
            };

            try
            {
                // The router resolves each fixture to its production package (by id/title token), spawns that
                // package's satellite with the right role + subscription set, and forwards the load.
                var okT = await router.RequestLoadPluginAsync("triggernometry", triggerDll, "Triggernometry");
                var okD = await router.RequestLoadPluginAsync("discord", sinkDll, "ACT-Discord-Triggers");
                Assert.True(okT, "trigger consumer load was not forwarded");
                Assert.True(okD, "sink consumer load was not forwarded");

                await Task.WhenAny(both.Task, Task.Delay(20000));
                Assert.True(both.Task.IsCompletedSuccessfully, "both fixtures did not announce (did not load)");

                // One satellite process per package, distinct pids.
                var sats = supervisor.Satellites;
                Assert.Equal(2, sats.Count);
                Assert.Equal(2, sats.Select(s => s.Pid).Distinct().Count());
                Assert.Contains(sats, s => s.Package == "triggernometry");
                Assert.Contains(sats, s => s.Package == "discord");

                // Let the discord satellite's sink poll register its terminal sink on the shared host audio.
                await Task.Delay(2000);

                // Fan the marker down: it reaches the triggernometry satellite (rawlog), fires the trigger →
                // TTS up → shared host audio → fans down to the discord satellite's terminal sink.
                bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, marker, string.Empty));
                await Task.Delay(3000);

                var lines = ReadShared(record);
                _out.WriteLine($"spoke=[{string.Join(" || ", audio.Speaks.Select(s => s.Text))}] sink=[{string.Join(" || ", lines)}]");
                Assert.Contains(audio.Speaks, s => s.Text == payload);   // trigger fired → shared host audio
                Assert.Contains(lines, l => l == "TTS|" + payload);      // and fanned to the discord sink satellite
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(8));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                Environment.SetEnvironmentVariable("FCT_SINK_RECORD", prevRecord);
                try { File.Delete(record); } catch { }
            }
        }

        // ISOLATION-PLAN P9a: N>1 consumer plugins in ONE package satellite. Two fixtures loaded under
        // Hojoring entry-assembly titles both resolve (via PackageResolver) to the single "hojoring"
        // package, so the router spawns ONE satellite hosting both — the topology Hojoring's four suite
        // plugins need (they share FFXIV.Framework singletons). Killing the satellite restarts it and the
        // router replays BOTH loads (SatelliteStarted → ReplayPackageAsync, in original order — the ordered
        // _packagePlugins list; each key loads once here, so the RemoveAll+Add tail-move never reorders).
        // Plugin-free — the fixtures stand in for the suite; no Hojoring needed.
        [SkippableFact]
        public async Task Hojoring_package_hosts_multiple_plugins_in_one_satellite_and_replays_all_on_restart()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var triggerDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.TriggerFixture");
            var sinkDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.SinkFixture");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(triggerDll), $"trigger fixture not built at {triggerDll}");
            Skip.IfNot(File.Exists(sinkDll), $"sink fixture not built at {sinkDll}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session, audio: new RecordingAudioOutput());
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var announced = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            router.PluginAnnounced += p => announced.AddOrUpdate(p.Key, 1, (_, n) => n + 1);

            async Task<bool> WaitBothAnnounced(int minCount, int timeoutMs)
            {
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    if (announced.TryGetValue("ssp", out var a) && a >= minCount &&
                        announced.TryGetValue("tts", out var b) && b >= minCount) return true;
                    await Task.Delay(100);
                }
                return false;
            }

            try
            {
                // Two plugins under Hojoring titles → the one "hojoring" satellite.
                Assert.True(await router.RequestLoadPluginAsync("ssp", triggerDll, "ACT.SpecialSpellTimer"),
                    "SpecialSpellTimer load was not forwarded");
                Assert.True(await router.RequestLoadPluginAsync("tts", sinkDll, "ACT.TTSYukkuri"),
                    "TTSYukkuri load was not forwarded");

                Assert.True(await WaitBothAnnounced(1, 25000), "both hojoring plugins did not announce");

                var sat = Assert.Single(supervisor.Satellites);
                Assert.Equal("hojoring", sat.Package);
                var pid = sat.Pid;

                // Kill the satellite; the supervisor restarts it and the router replays BOTH loads.
                announced.Clear();
                System.Diagnostics.Process.GetProcessById(pid).Kill();

                Assert.True(await WaitBothAnnounced(1, 30000), "both hojoring plugins did not re-announce after restart");
                var restarted = Assert.Single(supervisor.Satellites);
                Assert.Equal("hojoring", restarted.Package);
                Assert.NotEqual(pid, restarted.Pid);   // a fresh process
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(8));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
            }
        }

        [SkippableFact]
        public async Task Uninstalling_a_packages_last_plugin_stops_and_kills_its_satellite()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var triggerDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.TriggerFixture");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(triggerDll), $"trigger fixture not built at {triggerDll}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var audio = new RecordingAudioOutput();
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session, audio: audio);
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.PluginAnnounced += _ => loaded.TrySetResult(true);

            try
            {
                Assert.True(await router.RequestLoadPluginAsync("triggernometry", triggerDll, "Triggernometry"),
                    "trigger consumer load was not forwarded");
                await Task.WhenAny(loaded.Task, Task.Delay(20000));
                Assert.True(loaded.Task.IsCompletedSuccessfully, "trigger fixture did not announce");

                var sat = Assert.Single(supervisor.Satellites);
                var pid = sat.Pid;
                var proc = System.Diagnostics.Process.GetProcessById(pid);   // throws if already gone

                // Uninstall the package's only plugin: the router tears the emptied satellite down.
                Assert.True(await router.RequestUnloadPluginAsync("triggernometry", TimeSpan.FromSeconds(8)));

                Assert.True(proc.WaitForExit(10000), "satellite process did not exit after its last plugin was uninstalled");
                Assert.Empty(supervisor.Satellites);   // dropped from the supervised set
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(8));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
            }
        }

        [SkippableFact]
        public async Task Real_parser_producer_coexists_with_trigger_and_discord_consumers()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var triggerDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.TriggerFixture");
            var sinkDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.SinkFixture");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(triggerDll), $"trigger fixture not built at {triggerDll}");
            Skip.IfNot(File.Exists(sinkDll), $"sink fixture not built at {sinkDll}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            var prevRecord = Environment.GetEnvironmentVariable("FCT_SINK_RECORD");
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-p7topo3-" + Guid.NewGuid().ToString("N") + ".tsv");
            Environment.SetEnvironmentVariable("FCT_SINK_RECORD", record);

            var payload = "p7topo3-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var marker = "00|" + DateTimeOffset.Now.ToString("O") + "|FCT_TRIGGER:" + payload;

            var audio = new RecordingAudioOutput();
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var supervisor = new SatelliteSupervisor(NullLoggerFactory.Instance, bus, session: session, audio: audio);
            var router = new SatelliteRouter(supervisor, NullLoggerFactory.Instance);

            var announced = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var consumersUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.PluginAnnounced += p =>
            {
                announced[p.Key] = true;
                if (announced.ContainsKey("triggernometry") && announced.ContainsKey("discord")) consumersUp.TrySetResult(true);
            };

            try
            {
                // Real parser as the producer satellite (own process), plus the two consumer satellites — the
                // full P7 production topology. The parser reaches Started with no live game (it forwards
                // nothing); the marker is injected on the bus so the assertion stays deterministic.
                var okP = await router.RequestLoadPluginAsync("ffxiv", SatelliteRunFixture.FfxivPluginPath, "FFXIV_ACT_Plugin");
                var okT = await router.RequestLoadPluginAsync("triggernometry", triggerDll, "Triggernometry");
                var okD = await router.RequestLoadPluginAsync("discord", sinkDll, "ACT-Discord-Triggers");
                Assert.True(okP, "parser producer load was not forwarded");
                Assert.True(okT && okD, "consumer loads were not forwarded");

                await Task.WhenAny(consumersUp.Task, Task.Delay(30000));
                Assert.True(consumersUp.Task.IsCompletedSuccessfully, "both consumer fixtures did not announce");

                // Three satellite processes, one per package (parser + trigger + discord), distinct pids.
                var sats = supervisor.Satellites;
                Assert.Equal(3, sats.Count);
                Assert.Equal(3, sats.Select(s => s.Pid).Distinct().Count());
                Assert.Contains(sats, s => s.Package == "ffxiv");
                Assert.Contains(sats, s => s.Package == "triggernometry");
                Assert.Contains(sats, s => s.Package == "discord");

                await Task.Delay(2000);
                bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, marker, string.Empty));
                await Task.Delay(3000);

                var lines = ReadShared(record);
                _out.WriteLine($"pids=[{string.Join(",", sats.Select(s => s.Pid))}] sink=[{string.Join(" || ", lines)}]");
                Assert.Contains(audio.Speaks, s => s.Text == payload);
                Assert.Contains(lines, l => l == "TTS|" + payload);
            }
            finally
            {
                await router.StopAllAsync(TimeSpan.FromSeconds(10));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                Environment.SetEnvironmentVariable("FCT_SINK_RECORD", prevRecord);
                try { File.Delete(record); } catch { }
            }
        }
    }
}
