using System.Globalization;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P9c dist-tree e2e gate: prove the *staged* dist\<mode>\ tree actually runs the
    // topology and holds parity — not just that its files exist (build/Build.cs only file-existence
    // checks). Opt-in via FCT_DIST_MODE (dist\ is git-ignored and must be built first with
    // `dotnet run --project build -- <mode>`); skips cleanly when the tree is absent. Points
    // FCT_INSTALL_DIR at the dist tree so SatelliteHost resolves dist\<mode>\satellite\Fct.LegacyHost.exe
    // (and, for a launched host, dist\<mode>\compat\), spawns a plugin-free --consume replica, fans the
    // committed frame corpus down to it, and asserts its YOU total equals the real-ACT oracle baseline
    // (three-way parity: oracle -> host engine -> consumer replica, from the shipped tree). No real
    // plugin needed — runs on any machine once the dist tree is built.
    public sealed class DistTreeGateTests
    {
        private readonly ITestOutputHelper _out;
        public DistTreeGateTests(ITestOutputHelper output) => _out = output;

        private static long OracleYouDamage(string root, string slice)
        {
            var lines = File.ReadAllLines(Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".aggregate.tsv"));
            int damageIdx = Array.IndexOf(lines[0].Split('\t'), "Damage");
            var you = lines.Skip(1).Select(l => l.Split('\t')).First(c => c[0] == "YOU");
            return long.Parse(you[damageIdx], CultureInfo.InvariantCulture);
        }

        [SkippableFact]
        public async Task Staged_dist_tree_runs_the_topology_and_holds_oracle_parity()
        {
            var mode = Environment.GetEnvironmentVariable("FCT_DIST_MODE");
            Skip.If(string.IsNullOrWhiteSpace(mode),
                "set FCT_DIST_MODE=debug|release (after `dotnet run --project build -- <mode>`) to run the dist-tree gate");
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");

            var distDir = Path.Combine(root!, "dist", mode!.Trim().ToLowerInvariant());
            var exe = Path.Combine(distDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"dist tree not staged at {exe} — run `dotnet run --project build -- {mode}`");

            // Drive one pass of the committed corpus (combat-slice + combat-slice2); the --consume replica
            // reports the YOU total summed across the idle-split encounters, so the expectation is the sum
            // of both slices' oracle baselines.
            long expectedYou = OracleYouDamage(root!, "combat-slice") + OracleYouDamage(root!, "combat-slice2");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, distDir);
            var dump = Path.Combine(Path.GetTempPath(), "fct-dist-consume-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "dist-consumer", session,
                $"--consume \"{dump}\" --subscribe {SatelliteProtocol.StreamSwings}");
            try
            {
                await host.StartAsync();
                await Task.Delay(1500);   // consumer subscribes + connects the command pipe

                int emitted = 0;
                foreach (var evt in FrameCorpus.Events(root!, 1))
                {
                    bus.Emit(evt);
                    if (++emitted % 50 == 0) await Task.Delay(1);
                }
                await Task.Delay(1500);   // let the fan-out + replica fold drain

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // consumer flushes its YOU total

                Assert.True(SpinUntilFile(dump, 5000), "dist consumer produced no dump");
                var youTotal = long.Parse(File.ReadAllText(dump).Trim(), CultureInfo.InvariantCulture);
                _out.WriteLine($"dist ({mode}) consumer replica YOU total = {youTotal} (emitted {emitted} frames), expected {expectedYou}");
                Assert.Equal(expectedYou, youTotal);
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
