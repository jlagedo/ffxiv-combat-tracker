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
    // ISOLATION-PLAN P7.3 gate (plugin-free): a production consumer-package satellite (--role consumer)
    // hosts a REAL net48 IActPluginV1 (the trigger fixture) on the host-fanned projection, with no parser.
    // The host fans a rawlog line down; the consumer folds it into its facade replica and re-raises it
    // through OnLogLineRead; the fixture matches the marker and calls ActGlobals.oFormActMain.TTS(...),
    // which routes UP the bridge as SPEAK and lands on the shared host IAudioOutput. Proves the multiplexed
    // -cmd pipe (LOADPLUGIN + downstream frames), the facade log-line re-raise, and the P6 audio up-route
    // all work together in the real consumer-satellite code path — no third-party binary.
    [Collection("satellite-p6")]
    public sealed class ConsumerPackageLoadTests
    {
        private readonly ITestOutputHelper _out;
        public ConsumerPackageLoadTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task A_consumer_satellite_hosts_a_real_plugin_that_fires_a_trigger_from_a_fanned_log_line()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            var fixtureDll = ReplayBridgeHarness.FixturePluginDll(root!, "Fct.Fixtures.TriggerFixture");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(fixtureDll), $"trigger fixture not built at {fixtureDll}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var payload = "hello-p7-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var marker = "00|" + DateTimeOffset.Now.ToString("O") + "|FCT_TRIGGER:" + payload;

            var audio = new RecordingAudioOutput();
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamRepository);
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "triggernometry", session,
                $"--role consumer --subscribe {streams}", audio: audio);

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.PluginAnnounced += _ => loaded.TrySetResult(true);

            try
            {
                await host.StartAsync();

                // The consumer satellite connects its command pipe only after its (slow) stand-in SDK load,
                // so RequestLoadPlugin can briefly return false; retry until the pipe is up.
                var reqOk = false;
                for (int i = 0; i < 30 && !reqOk; i++)
                {
                    reqOk = host.RequestLoadPlugin("triggerfixture", fixtureDll, "TriggerFixture");
                    if (!reqOk) await Task.Delay(500);
                }
                Assert.True(reqOk, "satellite command pipe never came up");
                await Task.WhenAny(loaded.Task, Task.Delay(15000));
                var announced = loaded.Task.IsCompletedSuccessfully;
                await Task.Delay(500);

                // Fan the marker log line down; the consumer re-raises it → the fixture fires TTS → SPEAK up.
                bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, marker, string.Empty));
                await Task.Delay(2500);

                _out.WriteLine($"announced={announced} spoke=[" + string.Join(" || ", audio.Speaks.Select(s => s.Text)) + "]");
                Assert.True(announced, "trigger fixture did not announce (did not load)");
                Assert.Contains(audio.Speaks, s => s.Text == payload);
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(8));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
            }
        }
    }
}
