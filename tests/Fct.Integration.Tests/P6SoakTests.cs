using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fct.Abstractions.Testing;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // Serializes the P6 host-routed-service e2e tests with each other (each launches its own satellites),
    // WITHOUT joining the real-plugin "satellite" collection — so those tests' shared real-plugin fixture
    // stays short-lived. No fixture: this collection only controls parallelism.
    [CollectionDefinition("satellite-p6")]
    public sealed class SatelliteP6Collection { }

    // ISOLATION-PLAN P6 capstone soak: all three host-routed concerns at once across two satellites, sharing
    // one host audio/registry/emitter. A sink satellite registers an audio sink + a named callback + a
    // rawlog subscription; a driver satellite produces audio, invokes the callback, and writes log lines in
    // a loop. Proves the three routes coexist without interference and every message arrives (no drops at
    // the count level). Plugin-free.
    [Collection("satellite-p6")]
    public sealed class P6SoakTests
    {
        private const int N = 20;
        private readonly ITestOutputHelper _out;
        public P6SoakTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task All_three_host_routed_concerns_coexist_across_two_satellites()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var recSink = Path.Combine(Path.GetTempPath(), "fct-soakSink-" + Guid.NewGuid().ToString("N") + ".txt");
            var recDrive = Path.Combine(Path.GetTempPath(), "fct-soakDrive-" + Guid.NewGuid().ToString("N") + ".txt");

            // One shared host runtime both satellites fan through.
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var audio = new RecordingAudioOutput();
            var registry = new RegistryService();
            var rawLog = new RawLogLineEmitter(bus, new SystemClock());

            var sink = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-sink", session,
                $"--p6-sink \"{recSink}\"", audio: audio, registry: registry, rawLog: rawLog);
            var drive = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-drive", session,
                $"--p6-drive \"{recDrive}\" --count {N}", audio: audio, registry: registry, rawLog: rawLog);
            try
            {
                await sink.StartAsync();
                await Task.Delay(3000);   // sink registers audio sink + named callback + rawlog subscription
                await drive.StartAsync();
                await Task.Delay(7000);   // drive settles (2 s), drives N of each, fan drains

                var s = ReadShared(recSink);
                var d = ReadShared(recDrive);
                int tts = s.Count(l => l.StartsWith("TTS|", StringComparison.Ordinal));
                int snd = s.Count(l => l.StartsWith("SND|", StringComparison.Ordinal));
                int cb = s.Count(l => l.StartsWith("CB|", StringComparison.Ordinal));
                int rawSink = s.Count(l => l.StartsWith("RAW|", StringComparison.Ordinal));
                int rawDrive = d.Count(l => l.StartsWith("RAW|", StringComparison.Ordinal));
                _out.WriteLine($"sink: tts={tts} snd={snd} cb={cb} raw={rawSink}; drive raw(origin)={rawDrive} (N={N})");

                // Every produced message of each concern arrived at the sink (no drops).
                Assert.Equal(N, tts);
                Assert.Equal(N, snd);
                Assert.Equal(N, cb);
                Assert.Equal(N, rawSink);
                // The origin sees its own written log lines (rawlog fan includes origin).
                Assert.Equal(N, rawDrive);
            }
            finally
            {
                await drive.ShutdownAsync(TimeSpan.FromSeconds(4));
                await sink.ShutdownAsync(TimeSpan.FromSeconds(4));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(recSink); } catch { }
                try { File.Delete(recDrive); } catch { }
            }
        }

        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }
    }
}
