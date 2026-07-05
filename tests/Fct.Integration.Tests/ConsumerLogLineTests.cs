using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P5 gate (log-line re-raise): a plugin-free consumer satellite SUBSCRIBEs to the
    // rawlog stream; the host fans RawLogLine frames down to it; the satellite re-raises each through
    // ACT's Before/OnLogLineRead hooks (FoldConsume) so an unmodified Trig/cactbot regex consumer reads
    // them exactly as under real ACT. The satellite writes a sibling artifact "<re-raised>\t<mutated>":
    // every fanned line must be re-raised, and a Before-handler edit of the mutable logLine must be
    // visible to the On-handler (the SAME LogLineEventArgs instance flows through both). Plugin-free —
    // the lines originate on the host bus, no FFXIV_ACT_Plugin.
    public sealed class ConsumerLogLineTests
    {
        private readonly ITestOutputHelper _out;
        public ConsumerLogLineTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Consumer_re_raises_fanned_log_lines_through_the_ACT_hooks_with_shared_mutable_args()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var dump = Path.Combine(Path.GetTempPath(), "fct-consume-" + Guid.NewGuid().ToString("N") + ".txt");
            var verify = Path.Combine(Path.GetTempPath(), "fct-consume-loglines-" + Guid.NewGuid().ToString("N") + ".txt");

            const int lineCount = 24;
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "consumer", session,
                $"--consume \"{dump}\" --subscribe {SatelliteProtocol.StreamSwings},{SatelliteProtocol.StreamRawLog} --verify-loglines \"{verify}\"");
            try
            {
                await host.StartAsync();
                await Task.Delay(1500);   // consumer subscribes + connects the command pipe

                for (int i = 0; i < lineCount; i++)
                    bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog,
                        $"00|2026-07-05T12:00:{i:D2}.0000000-03:00|line-{i}", string.Empty));
                await Task.Delay(1500);   // let the fan-out + re-raise drain

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // consumer flushes its artifacts

                Assert.True(SpinUntilFile(verify, 5000), "consumer produced no log-line verify artifact");
                var parts = File.ReadAllText(verify).Trim().Split('\t');
                var reRaised = long.Parse(parts[0]);
                var mutated = long.Parse(parts[1]);
                _out.WriteLine($"re-raised {reRaised}, mutation-observed {mutated} of {lineCount} fanned lines");
                Assert.Equal(lineCount, reRaised);
                Assert.Equal(lineCount, mutated);
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
