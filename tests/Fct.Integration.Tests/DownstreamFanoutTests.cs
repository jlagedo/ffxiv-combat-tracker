using System.Collections.Generic;
using System.Linq;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P4 e2e: the host stands up a real SatelliteHost that launches a plugin-free sink
    // satellite; the satellite SUBSCRIBEs to the "swings" stream; the host fans bus events down the real
    // command pipe via its per-satellite egress; the sink records what it receives. Proves the downstream
    // wire path end-to-end (SatelliteHost.HandleSubscribe → SatelliteEgress → command pipe → satellite).
    // The identical/unsubscribed/stalled router behavior is gated in-process by Fct.App.Tests.
    public sealed class DownstreamFanoutTests
    {
        private static readonly IReadOnlyDictionary<string, object> NoTags = new Dictionary<string, object>();
        private readonly ITestOutputHelper _out;
        public DownstreamFanoutTests(ITestOutputHelper output) => _out = output;

        private static CombatSwing Swing(int i) =>
            new(i, new System.DateTimeOffset(2024, 1, 1, 0, 0, 0, System.TimeSpan.Zero).AddSeconds(i),
                2, false, "none", 100 + i, i, "Attack", "You", "", "Dummy", NoTags);

        [SkippableFact]
        public async Task Host_fans_a_subscribed_stream_down_to_a_sink_satellite()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-sink-" + Guid.NewGuid().ToString("N") + ".tsv");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "sink-a", session,
                $"--sink \"{record}\" --subscribe {SatelliteProtocol.StreamSwings}");
            try
            {
                await host.StartAsync();
                // Let the sink complete SUBSCRIBE + connect the command pipe before driving the bus.
                await Task.Delay(1500);

                const int n = 300;
                for (int i = 0; i < n; i++)
                {
                    bus.Emit(Swing(i));
                    if (i % 25 == 0) await Task.Delay(1);
                }
                await Task.Delay(1000);   // let the egress drain to the sink

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));

                var lines = File.Exists(record) ? File.ReadAllLines(record) : Array.Empty<string>();
                int swings = lines.Count(l => l.StartsWith("EVT SWING", StringComparison.Ordinal));
                _out.WriteLine($"sink recorded {swings}/{n} downstream swings");
                Assert.True(swings >= n - 50, $"sink recorded only {swings}/{n} downstream swings");
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(record); } catch { }
            }
        }
    }
}
