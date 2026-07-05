using System.Diagnostics;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P3 gate (no plugins): the SatelliteSupervisor launches three empty satellites with
    // distinct identities; killing one leaves the others' pipes live and the host healthy; the dead one
    // restarts and re-handshakes with the same id; each satellite writes its own identity-keyed
    // verification artifact (per-satellite attribution). Skips cleanly when the satellite is not staged.
    public sealed class SatelliteSupervisorTests
    {
        private readonly ITestOutputHelper _out;
        public SatelliteSupervisorTests(ITestOutputHelper output) => _out = output;

        private static bool SpinUntil(Func<bool> cond, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (cond()) return true;
                Thread.Sleep(50);
            }
            return cond();
        }

        [SkippableFact]
        public async Task Three_satellites_run_isolated_and_the_killed_one_restarts()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            // Point the host runtime's install-dir resolution at the staged app tree so SatelliteHost finds
            // satellite\Fct.LegacyHost.exe from the test host.
            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            // Fast backoff so the restart lands quickly; default threshold (4) so a single kill restarts.
            var policy = new RestartPolicy
            {
                BaseDelay = TimeSpan.FromMilliseconds(200),
                MaxDelay = TimeSpan.FromSeconds(2),
            };
            var supervisor = new SatelliteSupervisor(
                NullLoggerFactory.Instance, NullGameEventSink.Instance, policy: policy);

            try
            {
                var ids = new[] { "sat-a", "sat-b", "sat-c" };
                var sats = new List<SupervisedSatellite>();
                foreach (var id in ids)
                    sats.Add(await supervisor.LaunchAsync(new SatelliteSpec { Id = id, Package = id }));

                // All three came up Running with distinct, id-matching v2 handshakes.
                foreach (var sat in sats)
                {
                    Assert.Equal(SatelliteState.Running, sat.State);
                    Assert.True(sat.Pid > 0, $"{sat.Id} has no pid");
                    Assert.Equal(sat.Id, Fct.Bridge.SatelliteProtocol.ReadySatelliteId(sat.Handshake));
                }
                Assert.Equal(3, sats.Select(s => s.Pid).Distinct().Count());

                var a = sats[0]; var b = sats[1]; var c = sats[2];
                int aPid = a.Pid, bPid = b.Pid, cPid = c.Pid;
                _out.WriteLine($"pids a={aPid} b={bPid} c={cPid}");

                // Kill the middle satellite out from under the host (an unexpected crash).
                using (var victim = Process.GetProcessById(bPid)) { victim.Kill(); victim.WaitForExit(5000); }

                // The dead one restarts and re-handshakes with the SAME id but a NEW pid.
                Assert.True(SpinUntil(() => b.State == SatelliteState.Running && b.RestartCount >= 1 && b.Pid != bPid, 30_000),
                    $"sat-b did not restart (state={b.State}, restarts={b.RestartCount}, pid={b.Pid})");
                Assert.Equal("sat-b", Fct.Bridge.SatelliteProtocol.ReadySatelliteId(b.Handshake));

                // Peers were unaffected: same processes, still Running, still alive.
                Assert.Equal(SatelliteState.Running, a.State);
                Assert.Equal(SatelliteState.Running, c.State);
                Assert.Equal(aPid, a.Pid);
                Assert.Equal(cPid, c.Pid);
                Assert.False(Process.GetProcessById(aPid).HasExited, "peer sat-a died");
                Assert.False(Process.GetProcessById(cPid).HasExited, "peer sat-c died");

                // Per-satellite attribution: each identity wrote its own verification artifact next to the exe.
                var satDir = Path.GetDirectoryName(exe)!;
                foreach (var id in ids)
                    Assert.True(SpinUntil(() => File.Exists(Path.Combine(satDir, $"s2-{id}.log")), 10_000),
                        $"missing per-satellite artifact s2-{id}.log");
            }
            finally
            {
                await supervisor.DisposeAsync();
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
            }
        }
    }
}
