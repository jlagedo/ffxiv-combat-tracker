using System;
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
    // ISOLATION-PLAN P8 gate (plugin-free): the raw-packet firehose OverlayPlugin's NetworkProcessors
    // consume must reach an isolated consumer satellite over the real pipe. A plugin-free sink satellite
    // SUBSCRIBEs to the "packets" stream (the first consumer to request it — the OverlayPlugin package's
    // distinguishing subscription); the host fans RawPacketReceived bus events down its egress; the sink
    // records the PKT frames. Proves the packets fan-out path (StreamCatalog "packets" →
    // IncludeRawPackets → GameEventFrame PKT → SatelliteEgress) end-to-end with no plugin, no SDK.
    public sealed class OverlayPacketFanoutTests
    {
        private readonly ITestOutputHelper _out;
        public OverlayPacketFanoutTests(ITestOutputHelper output) => _out = output;

        private static RawPacketReceived Packet(int i) =>
            new(i, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
                "conn", 1000 + i, PacketDirection.Received, new byte[] { 1, 2, 3, (byte)i });

        [SkippableFact]
        public async Task Host_fans_the_packet_firehose_down_to_a_subscribed_sink_satellite()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-pktsink-" + Guid.NewGuid().ToString("N") + ".tsv");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "overlay-pkt", session,
                $"--sink \"{record}\" --subscribe {SatelliteProtocol.StreamPackets}");
            try
            {
                await host.StartAsync();
                await Task.Delay(1500);   // let the sink SUBSCRIBE + connect the command pipe

                const int n = 200;
                for (int i = 0; i < n; i++)
                {
                    bus.Emit(Packet(i));
                    if (i % 25 == 0) await Task.Delay(1);
                }
                await Task.Delay(1000);   // let the egress drain to the sink

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));

                var lines = File.Exists(record) ? File.ReadAllLines(record) : Array.Empty<string>();
                int packets = lines.Count(l => l.StartsWith("EVT PKT", StringComparison.Ordinal));
                _out.WriteLine($"sink recorded {packets}/{n} downstream packets");
                Assert.True(packets >= n - 40, $"sink recorded only {packets}/{n} downstream packets");
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
