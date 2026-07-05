using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P6 (custom-log-line write-back) e2e over the real bridge. A plugin-free satellite
    // writes custom lines (ILOGLINE-style write-back → LOGLINE up the event pipe); the host re-emits each
    // as a bus RawLogLine (fresh sequence + clock) and fans it through the rawlog egress to BOTH a peer
    // sink satellite and back to the origin, in bus order. Two satellites share one host bus/session/
    // emitter so the fan is genuinely cross-satellite. Plugin-free (no FFXIV_ACT_Plugin needed).
    [Collection("satellite-p6")]   // serialize the P6 host-routed-service e2e tests with each other (see SatelliteP6Collection)
    public sealed class LogWriteBackTests
    {
        private readonly ITestOutputHelper _out;
        public LogWriteBackTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Custom_line_from_A_observed_in_both_A_and_B_in_order()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var recA = Path.Combine(Path.GetTempPath(), "fct-logA-" + Guid.NewGuid().ToString("N") + ".tsv");
            var recB = Path.Combine(Path.GetTempPath(), "fct-logB-" + Guid.NewGuid().ToString("N") + ".tsv");
            const int n = 10;

            // One shared bus/session/emitter so both satellites' egresses read the same RawLogLine emits.
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var rawLog = new RawLogLineEmitter(bus, new SystemClock());

            var sink = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "log-sink-b", session,
                $"--sink \"{recB}\" --subscribe {SatelliteProtocol.StreamRawLog}", rawLog: rawLog);
            var emitter = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "log-emit-a", session,
                $"--emit-logline \"{recA}\" --count {n}", rawLog: rawLog);

            try
            {
                await sink.StartAsync();
                await emitter.StartAsync();
                // Let both subscribe, the emitter write (after its ~1.5 s settle), and the fan drain.
                await Task.Delay(5000);

                var linesA = Decode(ReadShared(recA));
                var linesB = Decode(ReadShared(recB));
                _out.WriteLine($"origin A recorded [{string.Join(",", linesA)}]; peer B recorded [{string.Join(",", linesB)}]");

                var expected = Enumerable.Range(0, n).Select(i => "P6LINE|" + i).ToArray();
                Assert.Equal(expected, linesA);   // origin sees its own written lines, in order
                Assert.Equal(expected, linesB);   // peer sees them too, same order
            }
            finally
            {
                await emitter.ShutdownAsync(TimeSpan.FromSeconds(4));
                await sink.ShutdownAsync(TimeSpan.FromSeconds(4));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(recA); } catch { }
                try { File.Delete(recB); } catch { }
            }
        }

        [SkippableFact]
        public async Task StandIn_ILogOutput_writeback_round_trips_through_the_host()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var rec = Path.Combine(Path.GetTempPath(), "fct-standinwb-" + Guid.NewGuid().ToString("N") + ".tsv");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var rawLog = new RawLogLineEmitter(bus, new SystemClock());
            // The satellite registers the synthetic parser stand-in, then writes a custom line back through
            // the exact OverlayPlugin reflection (_iocContainer → GetService(ILogOutput) → WriteLine); the
            // host re-emits it and fans it back to the origin's rawlog subscription.
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "standin-wb", session,
                $"--standin-writeback \"{rec}\"", rawLog: rawLog);
            try
            {
                await host.StartAsync();
                await Task.Delay(5000);

                var lines = Decode(ReadShared(rec));
                _out.WriteLine($"stand-in write-back recorded [{string.Join(",", lines)}]");
                Assert.Contains("STANDIN|hi", lines);
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(4));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(rec); } catch { }
            }
        }

        // Decode recorded EVT frames to the RawLogLine text, keeping only our custom (id 256) lines in order.
        private static string[] Decode(string[] wire)
        {
            var list = new List<string>();
            foreach (var w in wire)
                if (GameEventFrame.TryParse(w, out var evt) && evt is RawLogLine r && (int)r.Type == 256)
                    list.Add(r.Line);
            return list.ToArray();
        }

        // The satellites keep their record files open (StreamWriter, AutoFlush); read with a read/write
        // share so an open writer handle never blocks the assert.
        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }
    }
}
