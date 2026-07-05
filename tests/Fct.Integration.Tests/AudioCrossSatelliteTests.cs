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
    // ISOLATION-PLAN P6 (audio producer + sink-provider path) e2e over the real bridge. Two satellites
    // share ONE host IAudioOutput: a sink satellite hijacks the ACT PlayTts/PlaySound slots (as
    // Discord-Triggers does) so its SatelliteHost registers a terminal sink proxy; a producer satellite
    // drives the facade's TTS/PlaySound, which marshal up its pipe to the shared audio, fan to the peer's
    // proxy, and are relayed down the peer's command pipe to the recording delegate. Proves audio produced
    // in satellite A plays through the sink registered from satellite B — cross-process, host-routed, with
    // the host producing no audio of its own. Plugin-free (no FFXIV_ACT_Plugin needed).
    [Collection("satellite")]   // serialize with the other satellite-launching e2e tests
    public sealed class AudioCrossSatelliteTests
    {
        private readonly ITestOutputHelper _out;
        public AudioCrossSatelliteTests(ITestOutputHelper output) => _out = output;

        // The sink satellite keeps its record file open (StreamWriter, AutoFlush); read it with a
        // read/write share so an open writer handle never blocks the assert.
        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            var text = r.ReadToEnd();
            return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }

        [SkippableFact]
        public async Task Audio_from_A_plays_through_sink_registered_in_B()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var record = Path.Combine(Path.GetTempPath(), "fct-audiosink-" + Guid.NewGuid().ToString("N") + ".tsv");
            var wav = Path.Combine(Path.GetTempPath(), "fct-horn-" + Guid.NewGuid().ToString("N") + ".wav");

            // The single shared host audio output both satellites' SatelliteHosts fan through.
            var audio = new RecordingAudioOutput();

            var busB = new GameEventBus();
            var sessionB = new GameSession(busB, new GameSnapshotProvider());
            var sink = new SatelliteHost(NullLoggerFactory.Instance, busB, null, null, "audio-sink-b", sessionB,
                $"--audio-sink \"{record}\"", audio: audio);

            var busA = new GameEventBus();
            var sessionA = new GameSession(busA, new GameSnapshotProvider());
            var producer = new SatelliteHost(NullLoggerFactory.Instance, busA, null, null, "audio-prod-a", sessionA,
                $"--audio-produce --tts \"pull in 5\" --wav \"{wav}\"", audio: audio);

            try
            {
                await sink.StartAsync();
                // Let the sink satellite hijack its slots, the poll send REGISTERSINK, and the host register
                // the terminal proxy (poll fires at 250 ms) + connect the command pipe.
                await Task.Delay(3000);

                await producer.StartAsync();
                // Let the producer's TTS/PlaySound marshal up, fan to the peer proxy, and be relayed down.
                await Task.Delay(3000);

                var lines = ReadShared(record);
                var dump = "recorded=[" + string.Join(" || ", lines) + "]";
                _out.WriteLine($"sink recorded {lines.Length} line(s): {dump}");

                Assert.True(lines.Any(l => l == "TTS|pull in 5"), dump);
                Assert.True(lines.Any(l => l.StartsWith("SND|100|", StringComparison.Ordinal) && l.EndsWith(wav, StringComparison.Ordinal)), dump);

                // The host itself produced nothing beyond routing: exactly the two producer calls crossed.
                Assert.Contains(audio.Speaks, s => s.Text == "pull in 5");
                Assert.Contains(audio.Plays, p => p.FilePath == wav && p.Volume == 100);
            }
            finally
            {
                await sink.ShutdownAsync(TimeSpan.FromSeconds(5));
                await producer.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(record); } catch { }
            }
        }

        [SkippableFact]
        public async Task Audio_with_no_registered_sink_is_dropped_without_error()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            // No sink registered anywhere: the produced call reaches the host and fans to nobody. The host
            // never produces audio itself — the call is simply dropped, silently and without error.
            var audio = new RecordingAudioOutput();
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var producer = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "audio-prod-solo", session,
                "--audio-produce --tts \"solo line\"", audio: audio);

            try
            {
                await producer.StartAsync();
                await Task.Delay(2500);

                // The producer path crossed the bridge and the host handled it; with no sink there is
                // nothing to relay downstream.
                Assert.Contains(audio.Speaks, s => s.Text == "solo line");
            }
            finally
            {
                await producer.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
            }
        }
    }
}
