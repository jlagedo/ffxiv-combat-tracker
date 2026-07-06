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
    // ISOLATION-PLAN P9b — cross-satellite audio-sink precedence, plugin-free. Two sink satellites hijack the
    // ACT audio slots and each register a terminal sink proxy on the ONE shared host IAudioOutput. The P9a
    // semantics are most-recent-registration-wins among equal-priority terminal sinks (real ACT's
    // last-hijacker-owns-the-slot; AudioService/RecordingAudioOutput tie-break OrderByDescending(Seq)). So a
    // produced call routes to sink B (registered after A); when B unregisters (satellite shutdown ->
    // UNREGISTERSINK), the route falls back to sink A. The real-plugin TTSYukkuri variant is P10b.
    [Collection("satellite-p6")]
    public sealed class AudioSinkPrecedenceTests
    {
        private readonly ITestOutputHelper _out;
        public AudioSinkPrecedenceTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Later_registered_sink_wins_and_falls_back_on_unregister()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var recA = Temp("sinkA"); var recB = Temp("sinkB");

            // The single shared host audio both sink satellites register terminal proxies on.
            var audio = new RecordingAudioOutput();

            var sinkA = MakeSink(bus: new GameEventBus(), audio, "audio-sinkA", recA, out var sessionA);
            var sinkB = MakeSink(bus: new GameEventBus(), audio, "audio-sinkB", recB, out var sessionB);
            SatelliteHost? prod1 = null, prod2 = null;
            try
            {
                // Register A first, then B — so B carries the higher Seq and owns the slot.
                await sinkA.StartAsync();
                await Task.Delay(3000);
                await sinkB.StartAsync();
                await Task.Delay(3000);

                // Round 1: both sinks registered -> B (most recent) wins, A sees nothing.
                prod1 = Producer(new GameEventBus(), audio, "audio-prod1", "round1", out _);
                await prod1.StartAsync();
                await Task.Delay(3000);

                var a1 = ReadShared(recA); var b1 = ReadShared(recB);
                _out.WriteLine($"round1 -> A=[{string.Join(",", a1)}] B=[{string.Join(",", b1)}]");
                Assert.Contains("TTS|round1", b1);
                Assert.DoesNotContain("TTS|round1", a1);

                // Unregister B (shutdown -> its terminal proxy is disposed off the shared audio).
                await sinkB.ShutdownAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(1500);

                // Round 2: only A remains -> the route falls back to A.
                prod2 = Producer(new GameEventBus(), audio, "audio-prod2", "round2", out _);
                await prod2.StartAsync();
                await Task.Delay(3000);

                var a2 = ReadShared(recA); var b2 = ReadShared(recB);
                _out.WriteLine($"round2 -> A=[{string.Join(",", a2)}] B=[{string.Join(",", b2)}]");
                Assert.Contains("TTS|round2", a2);
                Assert.DoesNotContain("TTS|round2", b2);

                // The host itself produced nothing beyond routing: both produced calls crossed the bridge.
                Assert.Contains(audio.Speaks, s => s.Text == "round1");
                Assert.Contains(audio.Speaks, s => s.Text == "round2");
            }
            finally
            {
                if (prod2 != null) { try { await prod2.ShutdownAsync(TimeSpan.FromSeconds(3)); } catch { } }
                if (prod1 != null) { try { await prod1.ShutdownAsync(TimeSpan.FromSeconds(3)); } catch { } }
                try { await sinkB.ShutdownAsync(TimeSpan.FromSeconds(3)); } catch { }
                try { await sinkA.ShutdownAsync(TimeSpan.FromSeconds(3)); } catch { }
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(recA); } catch { }
                try { File.Delete(recB); } catch { }
            }
        }

        private static SatelliteHost MakeSink(GameEventBus bus, RecordingAudioOutput audio, string id, string record, out GameSession session)
        {
            session = new GameSession(bus, new GameSnapshotProvider());
            return new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, id, session,
                $"--audio-sink \"{record}\"", audio: audio);
        }

        private static SatelliteHost Producer(GameEventBus bus, RecordingAudioOutput audio, string id, string tts, out GameSession session)
        {
            session = new GameSession(bus, new GameSnapshotProvider());
            return new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, id, session,
                $"--audio-produce --tts \"{tts}\"", audio: audio);
        }

        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }

        private static string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"fct-{tag}-" + Guid.NewGuid().ToString("N") + ".tsv");
    }
}
