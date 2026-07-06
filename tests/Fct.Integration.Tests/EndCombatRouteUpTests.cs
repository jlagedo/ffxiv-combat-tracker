using System;
using System.IO;
using System.Linq;
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
    // ISOLATION-PLAN P9a gate (plugin-free e2e): a consumer facade's EndCombat routes UP the bridge, the
    // host injects EndCombatRequested onto the bus, and it fans DOWN the swings stream to a peer subscriber.
    // Proves the whole facade -> ENDCOMBAT -> host bus-injection -> fan-down chain over the real pipe. The
    // engine-fold half (EndCombatRequested closes the encounter with the oracle YOU total) is covered by
    // Fct.Engine.Tests / ConsumerProjectionTests — this gate proves the routing the P9a change adds.
    public sealed class EndCombatRouteUpTests
    {
        private readonly ITestOutputHelper _out;
        public EndCombatRouteUpTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Consumer_EndCombat_routes_up_the_host_injects_it_on_the_bus_and_fans_it_down()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-endcombat-" + Guid.NewGuid().ToString("N") + ".tsv");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());

            // In-process probe: count EndCombatRequested the host injects onto the bus.
            int onBus = 0;
            using var probe = bus.Subscribe(GameEventFilter.All,
                e => { if (e is EndCombatRequested) Interlocked.Increment(ref onBus); });

            // A peer consumer subscribed to swings records the frames the host fans down (the real-pipe
            // fan-down of the injected EndCombatRequested).
            var peer = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "peer", session,
                $"--sink \"{record}\" --subscribe {SatelliteProtocol.StreamSwings}");
            // The driver consumer installs the service route + RouteEndCombatUp and routes one EndCombat up.
            var driver = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "driver", session,
                "--emit-endcombat");
            try
            {
                await peer.StartAsync();
                await driver.StartAsync();

                // The driver routes EndCombat up ~2 s after connect; wait past that plus fan-out drain.
                var deadline = Environment.TickCount64 + 12000;
                while (Environment.TickCount64 < deadline && Volatile.Read(ref onBus) == 0)
                    await Task.Delay(100);

                await driver.ShutdownAsync(TimeSpan.FromSeconds(5));
                await peer.ShutdownAsync(TimeSpan.FromSeconds(5));

                Assert.True(Volatile.Read(ref onBus) >= 1, "host did not inject EndCombatRequested onto the bus");

                var fanned = File.Exists(record)
                    ? File.ReadLines(record)
                        .Where(l => l.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal))
                        .Count(l => GameEventFrame.TryParse(l, out var e) && e is EndCombatRequested)
                    : 0;
                _out.WriteLine($"onBus={onBus} fannedEndCombat={fanned}");
                Assert.True(fanned >= 1, "the injected EndCombatRequested was not fanned down to the peer");
            }
            finally
            {
                await driver.ShutdownAsync(TimeSpan.FromSeconds(3));
                await peer.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(record); } catch { }
            }
        }
    }
}
