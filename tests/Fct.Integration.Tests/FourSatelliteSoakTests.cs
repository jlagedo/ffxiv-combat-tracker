using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    // ISOLATION-PLAN P9b — four-satellite soak + budgets (plugin-free tier). One host bus fans a long looped
    // frame corpus (FrameCorpus: combat-slice{,2} x N) down to four directly-constructed satellites — two
    // --consume parity replicas and two --sink recorders, all on swings+rawlog. The drive interleaves
    // FCT_MARK:<id> latency markers (rawlog sentinels) stamped with QPC at bus.Emit; each satellite stamps
    // QPC at fold into its --verify-latency artifact, and the test joins on <id> for host-egress→fold p99.
    // Hard gates: N x per-slice oracle YOU parity on every consume replica, and zero steady-state egress
    // drops. Recorded + generous-ceiling: latency p99 and per-satellite working set. Plugin-free.
    [Collection("satellite-p6")]
    public sealed class FourSatelliteSoakTests
    {
        private readonly ITestOutputHelper _out;
        public FourSatelliteSoakTests(ITestOutputHelper output) => _out = output;

        // Iterations of the two-slice corpus. Deliberately modest so the plugin-free gate stays CI-fast;
        // FCT_SOAK_ITERATIONS cranks it up for a real soak run.
        private static int Iterations()
            => int.TryParse(Environment.GetEnvironmentVariable("FCT_SOAK_ITERATIONS"), out var n) && n > 0 ? n : 5;

        // Generous first ceilings (record-and-assert, not micro-budgeted): catch gross regressions/hangs
        // without flaking on machine variance. Target latency budget is 10 ms; tighten once the CI machine
        // class is characterized (P9b doc note).
        private const double LatencyCeilingMs = 200.0;
        private const long MemoryCeilingBytes = 600L * 1024 * 1024;

        [SkippableFact]
        public async Task Four_satellites_hold_parity_zero_drops_and_budgets_under_looped_corpus()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(ReplayBridgeHarness.FramesFixture(root!, "combat-slice")), "frame fixtures missing");

            int n = Iterations();
            long expectedYou = n * (OracleYouDamage(root!, "combat-slice") + OracleYouDamage(root!, "combat-slice2"));

            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var dumpA = Temp("consumeA"); var dumpB = Temp("consumeB");
            var latA = Temp("latA"); var latB = Temp("latB"); var latC = Temp("latC");
            var recC = Temp("sinkC"); var recD = Temp("sinkD");
            var temps = new[] { dumpA, dumpB, latA, latB, latC, recC, recD };

            // One shared host runtime; one bus.Emit fans to all four satellites.
            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var subs = $"{SatelliteProtocol.StreamSwings},{SatelliteProtocol.StreamRawLog}";

            var cA = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-consumeA", session,
                $"--consume \"{dumpA}\" --subscribe {subs} --verify-latency \"{latA}\"");
            var cB = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-consumeB", session,
                $"--consume \"{dumpB}\" --subscribe {subs} --verify-latency \"{latB}\"");
            var sC = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-sinkC", session,
                $"--sink \"{recC}\" --subscribe {subs} --verify-latency \"{latC}\"");
            var sD = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "soak-sinkD", session,
                $"--sink \"{recD}\" --subscribe {subs}");
            var hosts = new[] { cA, cB, sC, sD };
            var latMap = new Dictionary<string, string> { [cA.SatelliteId] = latA, [cB.SatelliteId] = latB, [sC.SatelliteId] = latC };

            try
            {
                foreach (var h in hosts) await h.StartAsync();
                await Task.Delay(2500);   // all four subscribe + connect their command pipes

                // Drive the looped corpus, interleaving latency markers on the rawlog stream.
                var markerEmit = new Dictionary<string, long>();
                int emitted = 0, markerId = 0;
                const int markerEvery = 150;
                foreach (var evt in FrameCorpus.Events(root!, n))
                {
                    bus.Emit(evt);
                    if (++emitted % markerEvery == 0)
                    {
                        var id = (markerId++).ToString(CultureInfo.InvariantCulture);
                        markerEmit[id] = Stopwatch.GetTimestamp();
                        bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog, "FCT_MARK:" + id, string.Empty));
                    }
                    if (emitted % 40 == 0) await Task.Delay(1);   // pace so the egress rings never back up
                }
                _out.WriteLine($"emitted {emitted} frames ({n} iterations) + {markerId} markers; expected YOU/replica = {expectedYou}");
                await Task.Delay(4000);   // let all fan-outs + replica folds drain fully

                // Sample egress counters + memory while the egress is still alive (ShutdownAsync disposes it).
                var counters = hosts.ToDictionary(h => h.SatelliteId, h => h.EgressCounters);
                var memory = hosts.ToDictionary(h => h.SatelliteId, SampleMemory);

                // Shut the satellites down — each consume flushes its YOU total + latency artifact; sinks
                // flush their (AutoFlush) latency artifact and exit.
                foreach (var h in hosts) await h.ShutdownAsync(TimeSpan.FromSeconds(6));

                // --- Parity (hard): each consume replica's YOU total == N x (oracle1 + oracle2). ---
                foreach (var (dump, id) in new[] { (dumpA, cA.SatelliteId), (dumpB, cB.SatelliteId) })
                {
                    Assert.True(SpinUntilFile(dump, 5000), $"{id} produced no dump");
                    long you = long.Parse(File.ReadAllText(dump).Trim(), CultureInfo.InvariantCulture);
                    _out.WriteLine($"{id}: YOU={you} (expected {expectedYou})");
                    Assert.Equal(expectedYou, you);
                }

                // --- Drops (hard): zero steady-state drops, and every satellite actually received frames. ---
                foreach (var h in hosts)
                {
                    var ec = counters[h.SatelliteId];
                    Assert.True(ec.HasValue, $"{h.SatelliteId} never subscribed (no egress)");
                    _out.WriteLine($"{h.SatelliteId}: sent={ec!.Value.Sent} dropped={ec.Value.Dropped}");
                    Assert.Equal(0, ec.Value.Dropped);
                    Assert.True(ec.Value.Sent > 0, $"{h.SatelliteId} sent nothing");
                }

                // --- Latency (record + generous ceiling): host-egress→fold p99 per latency-enabled satellite. ---
                var allDeltas = new List<double>();
                foreach (var kv in latMap)
                {
                    var deltas = ReadLatency(kv.Value, markerEmit);
                    Assert.True(deltas.Count > 0, $"{kv.Key} captured no latency markers");
                    allDeltas.AddRange(deltas);
                    _out.WriteLine($"{kv.Key}: latency markers={deltas.Count} p99={Percentile(deltas, 0.99):0.00}ms max={deltas.Max():0.00}ms");
                }
                double p99 = Percentile(allDeltas, 0.99);
                _out.WriteLine($"overall latency p99={p99:0.00}ms (target 10ms, ceiling {LatencyCeilingMs}ms) over {allDeltas.Count} markers");
                Assert.True(p99 <= LatencyCeilingMs, $"latency p99 {p99:0.00}ms exceeded ceiling {LatencyCeilingMs}ms");

                // --- Memory (record + generous ceiling). ---
                foreach (var h in hosts)
                {
                    var (ws, pm) = memory[h.SatelliteId];
                    _out.WriteLine($"{h.SatelliteId}: workingSet={ws / (1024 * 1024)}MB privateBytes={pm / (1024 * 1024)}MB");
                    Assert.True(ws > 0 && ws < MemoryCeilingBytes, $"{h.SatelliteId} working set {ws} out of budget");
                }
            }
            finally
            {
                foreach (var h in hosts) { try { await h.ShutdownAsync(TimeSpan.FromSeconds(3)); } catch { } }
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                foreach (var t in temps) { try { File.Delete(t); } catch { } }
            }
        }

        // Join a satellite's --verify-latency artifact ("<id>\t<foldQpc>") to the recorded emit QPCs and
        // return per-marker latencies in milliseconds (QPC is machine-wide, so cross-process comparable).
        private static List<double> ReadLatency(string path, Dictionary<string, long> emit)
        {
            var deltas = new List<double>();
            if (!File.Exists(path)) return deltas;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            foreach (var line in ReadShared(path))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var id = line.Substring(0, tab);
                if (!emit.TryGetValue(id, out var t0)) continue;
                if (!long.TryParse(line.Substring(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t1)) continue;
                var ms = (t1 - t0) / ticksPerMs;
                if (ms >= 0) deltas.Add(ms);
            }
            return deltas;
        }

        private static double Percentile(List<double> values, double q)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int idx = (int)Math.Ceiling(q * (sorted.Count - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }

        private static long OracleYouDamage(string root, string slice)
        {
            var lines = File.ReadAllLines(Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".aggregate.tsv"));
            int damageIdx = Array.IndexOf(lines[0].Split('\t'), "Damage");
            var you = lines.Skip(1).Select(l => l.Split('\t')).First(c => c[0] == "YOU");
            return long.Parse(you[damageIdx], CultureInfo.InvariantCulture);
        }

        private static (long Ws, long Pm) SampleMemory(SatelliteHost h)
        {
            try { var p = h.Process; if (p != null) { p.Refresh(); return (p.WorkingSet64, p.PrivateMemorySize64); } }
            catch { }
            return (0, 0);
        }

        private static bool SpinUntilFile(string path, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline) { if (File.Exists(path)) return true; System.Threading.Thread.Sleep(50); }
            return File.Exists(path);
        }

        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }

        private static string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"fct-{tag}-" + Guid.NewGuid().ToString("N") + ".txt");
    }
}
