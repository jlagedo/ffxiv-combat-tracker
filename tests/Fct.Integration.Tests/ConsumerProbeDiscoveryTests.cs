using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P5 discovery-variant gate [plugin-gated]: the real, unmodified Fct.StreamProbe runs
    // as an IActPluginV1 inside a --consume --stand-in --probe satellite and discovers the synthetic parser
    // stand-in exactly as OverlayPlugin's FFXIVRepository does — a separately-compiled assembly reflecting
    // ActPlugins, casting DataSubscription/DataRepository to the real FFXIV_ACT_Plugin.Common types
    // (resolved across the assembly boundary through the facade's AssemblyResolve), and receiving the
    // host-fanned SDK events. Proves the same reflection path OverlayPlugin/Triggernometry/Hojoring use
    // binds the stand-in, over the real pipe, with no parser. Needs FFXIV_ACT_Plugin.dll + the built
    // StreamProbe; skips cleanly without either.
    public sealed class ConsumerProbeDiscoveryTests
    {
        private readonly ITestOutputHelper _out;
        public ConsumerProbeDiscoveryTests(ITestOutputHelper output) => _out = output;

        private static Actor Combatant(uint id, string name) => new(
            id, 0, ActorKind.Player, 24, 90, name, 50, 100, 0, 0, null, default,
            0, "", 0, 0, 0, 0, 0, PartyMembership.Party, false,
            System.Array.Empty<StatusEffect>(), System.Array.Empty<EnmityEntry>());

        [SkippableFact]
        public async Task StreamProbe_discovers_and_binds_the_stand_in_from_host_routed_frames()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var satelliteDir = Path.Combine(appDir, "satellite");
            var exe = Path.Combine(satelliteDir, "Fct.LegacyHost.exe");
            var probeSrc = Path.Combine(root!, "src", "Fct.StreamProbe", "bin", ReplayBridgeHarness.Config(), "net48", "Fct.StreamProbe.dll");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(probeSrc), $"Fct.StreamProbe not built at {probeSrc}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            // Stage the dev probe next to the satellite (FacadeHost.LoadProbe reads satellite\plugins\).
            var pluginsDir = Path.Combine(satelliteDir, "plugins");
            Directory.CreateDirectory(pluginsDir);
            File.Copy(probeSrc, Path.Combine(pluginsDir, "Fct.StreamProbe.dll"), overwrite: true);
            var probeLog = Path.Combine(satelliteDir, "streamprobe.log");
            try { File.Delete(probeLog); } catch { }

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var dump = Path.Combine(Path.GetTempPath(), "fct-consume-" + Guid.NewGuid().ToString("N") + ".txt");

            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamCombatants, SatelliteProtocol.StreamRepository);
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "consumer", session,
                $"--consume \"{dump}\" --subscribe {streams} --stand-in --probe");
            try
            {
                await host.StartAsync();
                await Task.Delay(3500);   // SDK load + stand-in register + probe load + discover-timer bind

                for (int i = 0; i < 12; i++)
                    bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, $"00|line-{i}", string.Empty));
                bus.Emit(new CombatantAdded(0, DateTimeOffset.Now, Combatant(0x1000, "You")));
                bus.Emit(new CombatantAdded(0, DateTimeOffset.Now, Combatant(0x2000, "Boss")));
                bus.Emit(new RepositorySnapshot(0, DateTimeOffset.Now, new[] { Combatant(0x1000, "You"), Combatant(0x2000, "Boss") }));
                await Task.Delay(1500);

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // pipe close → probe DeInit flushes its log

                Assert.True(SpinUntilFile(probeLog, 5000), "StreamProbe produced no log");
                await Task.Delay(300);
                var text = File.ReadAllText(probeLog);
                _out.WriteLine(text);
                Assert.Contains("DISCOVER", text);
                Assert.Contains("bound", text);                              // reflected + cast the stand-in's SDK surface
                Assert.Contains("subscribed to all 11", text);              // bound all IDataSubscription events
                Assert.Contains("SDK.LOG", text);                           // received a fanned log line through the SDK
                Assert.Contains("SDK.CBADD", text);                         // received a fanned CombatantAdded
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(dump); } catch { }
            }
        }

        private static bool SpinUntilFile(string path, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (File.Exists(path)) return true;
                Thread.Sleep(50);
            }
            return File.Exists(path);
        }
    }
}
