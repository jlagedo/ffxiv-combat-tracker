using System;
using System.Globalization;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P8 gate [plugin-gated]: the packet firehose reaches OverlayPlugin's actual bind
    // point. A --consume --stand-in satellite SUBSCRIBEs to the packets stream; the host fans
    // RawPacketReceived frames down; the satellite folds them into the synthetic parser stand-in, whose
    // ConsumerDataSubscription raises IDataSubscription.NetworkReceived exactly as the real FFXIV plugin
    // does for OverlayPlugin's RegisterNetworkParser. The stand-in's SelfVerify reports the raised count
    // in the 7th column of the --verify-standin artifact. Needs FFXIV_ACT_Plugin.dll installed (the SDK
    // types the stand-in binds); skips cleanly without it.
    public sealed class OverlayStandInPacketTests
    {
        private readonly ITestOutputHelper _out;
        public OverlayStandInPacketTests(ITestOutputHelper output) => _out = output;

        private static RawPacketReceived Packet(int i) =>
            new(i, DateTimeOffset.Now, "conn", 1000 + i, PacketDirection.Received, new byte[] { 9, 8, 7, (byte)i });

        [SkippableFact]
        public async Task Stand_in_raises_NetworkReceived_from_the_fanned_packet_firehose()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var dump = Path.Combine(Path.GetTempPath(), "fct-consume-" + Guid.NewGuid().ToString("N") + ".txt");
            var verify = Path.Combine(Path.GetTempPath(), "fct-standin-pkt-" + Guid.NewGuid().ToString("N") + ".txt");

            const int n = 32;
            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamPackets);
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "overlay", session,
                $"--consume \"{dump}\" --subscribe {streams} --stand-in --verify-standin \"{verify}\"");
            try
            {
                await host.StartAsync();
                await Task.Delay(2500);   // consumer loads the SDK (Costura), registers the stand-in, subscribes

                for (int i = 0; i < n; i++)
                    bus.Emit(Packet(i));
                await Task.Delay(1500);   // fan-out + fold drain

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // consumer flushes the stand-in verify artifact

                Assert.True(SpinUntilFile(verify, 5000), "consumer produced no stand-in verify artifact");
                var f = File.ReadAllText(verify).Trim().Split('\t');
                _out.WriteLine($"stand-in verify: [{string.Join(" | ", f)}]");
                Assert.Equal("1", f[0]);                                             // found in ActPlugins
                Assert.Equal("1", f[1]);                                             // SDK types bound
                Assert.Equal(n, int.Parse(f[6], CultureInfo.InvariantCulture));      // NetworkReceived raised per fanned packet
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(dump); } catch { }
                try { File.Delete(verify); } catch { }
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
