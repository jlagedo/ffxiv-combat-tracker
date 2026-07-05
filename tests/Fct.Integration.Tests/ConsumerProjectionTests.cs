using System.Globalization;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P5 gate: a plugin-free consumer satellite SUBSCRIBEs to the swing/lifecycle stream;
    // the host fans a committed frame-replay down to it; the satellite folds every frame into its OWN
    // ACT-facade replica (Fct.Compat.Act) and reports the YOU total summed across the idle-split
    // encounters. That total must equal the real-ACT oracle baseline — the consumer facade serves the
    // same encounter numbers as the host engine, from host-routed data alone (three-way parity:
    // oracle -> host engine -> consumer replica). Needs only the staged satellite (no FFXIV_ACT_Plugin).
    public sealed class ConsumerProjectionTests
    {
        private readonly ITestOutputHelper _out;
        public ConsumerProjectionTests(ITestOutputHelper output) => _out = output;

        private static long OracleYouDamage(string root, string slice)
        {
            var lines = File.ReadAllLines(Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".aggregate.tsv"));
            int damageIdx = Array.IndexOf(lines[0].Split('\t'), "Damage");
            var you = lines.Skip(1).Select(l => l.Split('\t')).First(c => c[0] == "YOU");
            return long.Parse(you[damageIdx], CultureInfo.InvariantCulture);
        }

        [SkippableTheory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public async Task Consumer_facade_replica_reproduces_the_oracle_total_from_host_routed_frames(string slice)
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var fixture = ReplayBridgeHarness.FramesFixture(root!, slice);
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(fixture), $"frame fixture missing at {fixture}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var dump = Path.Combine(Path.GetTempPath(), "fct-consume-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "consumer", session,
                $"--consume \"{dump}\" --subscribe {SatelliteProtocol.StreamSwings}");
            try
            {
                await host.StartAsync();
                await Task.Delay(1500);   // consumer subscribes + connects the command pipe

                // Drive the host bus with the committed frame-replay; the host fans it down to the consumer.
                int emitted = 0;
                foreach (var line in File.ReadLines(fixture))
                {
                    if (!FrameSession.TryParseLine(line, out _, out var evt) || evt is null) continue;
                    bus.Emit(evt);
                    if (++emitted % 50 == 0) await Task.Delay(1);
                }
                await Task.Delay(1500);   // let the fan-out + replica fold drain

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // consumer flushes its YOU total

                Assert.True(SpinUntilFile(dump, 5000), "consumer produced no dump");
                var youTotal = long.Parse(File.ReadAllText(dump).Trim(), CultureInfo.InvariantCulture);
                _out.WriteLine($"consumer replica YOU total = {youTotal} (emitted {emitted} frames)");
                Assert.Equal(OracleYouDamage(root!, slice), youTotal);
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
