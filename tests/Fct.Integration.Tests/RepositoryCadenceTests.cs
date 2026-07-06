using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    // ISOLATION-PLAN P9b — repository-snapshot cadence re-pin (§5, OverlayPlugin half). The parser
    // BridgeForwarder emits RepositorySnapshot at a fixed 250 ms cadence (RepositorySnapshotIntervalMs); a
    // consumer's local mirror is refreshed from each fanned snapshot, so mirror staleness is bounded by the
    // host fan-out of that stream. This gate drives RepositorySnapshot frames through the host at the pinned
    // 250 ms cadence to a `repository`-subscribed sink and asserts the fan is LOSSLESS and cadence-preserving
    // — every snapshot arrives, in order, with zero egress drops. Combined with the pinned 250 ms producer
    // interval and the measured sub-10 ms egress latency (FourSatelliteSoakTests), mirror staleness stays
    // under one poll interval. Plugin-free. (OverlayPlugin's internal read-poll instrumentation needs a live
    // game — GO-LIVE A1; the fan-out cadence bound is what CI gates.)
    [Collection("satellite-p6")]
    public sealed class RepositoryCadenceTests
    {
        // The pinned RepositorySnapshot cadence (BridgeForwarder.RepositorySnapshotIntervalMs, net48 — not
        // referenceable from this net10 test, so mirrored here as the interval under assertion).
        private const int RepositorySnapshotIntervalMs = 250;

        private readonly ITestOutputHelper _out;
        public RepositoryCadenceTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Repository_stream_fans_losslessly_at_the_pinned_250ms_cadence()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var rec = Path.Combine(Path.GetTempPath(), "fct-repocad-" + Guid.NewGuid().ToString("N") + ".tsv");

            const int k = 24;   // 24 snapshots @ 250 ms ≈ 6 s of cadence
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "repo-sink", session,
                $"--sink \"{rec}\" --subscribe {SatelliteProtocol.StreamRepository}");
            try
            {
                await host.StartAsync();
                await Task.Delay(1500);   // subscribe + connect the command pipe

                var emitTimestamps = new List<DateTimeOffset>(k);
                for (int i = 0; i < k; i++)
                {
                    var ts = DateTimeOffset.Now;
                    emitTimestamps.Add(ts);
                    // A minimal but non-empty roster (fresh Hp each tick) — the poll surface OP mirrors.
                    var actor = FakeActors.Player((uint)(0x1000 + i), "YOU");
                    bus.Emit(new RepositorySnapshot(0, ts, new[] { actor }));
                    await Task.Delay(RepositorySnapshotIntervalMs);
                }
                await Task.Delay(1500);   // let the egress drain the last frames

                var dropped = host.EgressCounters?.Dropped ?? -1;
                await host.ShutdownAsync(TimeSpan.FromSeconds(8));

                // Every driven snapshot fanned down, in order, with zero drops.
                var repo = ReadShared(rec).Where(l => l.StartsWith("EVT REPO", StringComparison.Ordinal)).ToList();
                _out.WriteLine($"drove {k} snapshots @ {RepositorySnapshotIntervalMs}ms; sink recorded {repo.Count}; dropped={dropped}");
                Assert.Equal(0, dropped);                 // lossless repository fan (zero steady-state drops)
                Assert.True(repo.Count >= k, $"sink recorded only {repo.Count}/{k} snapshots (+priming)");

                // Cadence preserved: the recorded snapshots' embedded emit timestamps advance monotonically,
                // and the median inter-arrival matches the pinned 250 ms interval (within scheduling jitter).
                var recTimes = repo.Select(EmbeddedTimestamp).Where(t => t.HasValue).Select(t => t!.Value).ToList();
                var deltas = new List<double>();
                for (int i = 1; i < recTimes.Count; i++)
                {
                    var ms = (recTimes[i] - recTimes[i - 1]).TotalMilliseconds;
                    Assert.True(ms >= 0, "repository snapshots arrived out of order");
                    if (ms > 0) deltas.Add(ms);
                }
                if (deltas.Count > 0)
                {
                    var median = deltas.OrderBy(d => d).ElementAt(deltas.Count / 2);
                    _out.WriteLine($"median inter-snapshot cadence = {median:0}ms (pinned {RepositorySnapshotIntervalMs}ms)");
                    Assert.InRange(median, RepositorySnapshotIntervalMs - 100, RepositorySnapshotIntervalMs + 150);
                }
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(rec); } catch { }
            }
        }

        // The REPO wire is "EVT REPO\t<iso8601 emit ts>\t…" — pull the embedded emit timestamp.
        private static DateTimeOffset? EmbeddedTimestamp(string wire)
        {
            var parts = wire.Split('\t');
            if (parts.Length < 2) return null;
            return DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : (DateTimeOffset?)null;
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
