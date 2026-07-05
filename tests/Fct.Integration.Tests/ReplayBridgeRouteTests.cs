using System.Diagnostics;
using System.IO.Pipes;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Bridge;
using Fct.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P1 wire-path e2e [plugin-gated]: run the staged satellite in --replay + --bridge
    // mode over a committed combat slice so the real FFXIV_ACT_Plugin parses it and the facade forwards
    // the full swing + encounter-lifecycle stream up the REAL pipe. Decode those GameEventFrames, fold
    // them through a real ModernEncounterEngine, and assert the host engine's YOU damage — summed across
    // the encounters the forwarded lifecycle splits the stream into — equals the satellite's own captured
    // total. Proves the net10 host engine is interchangeable with the net48 engine on the identical
    // routed stream; divergence fails CI. Skips cleanly without the satellite staged or the plugin installed.
    public sealed class ReplayBridgeRouteTests
    {
        // YOU's total outgoing damage in combat-slice — the bit-perfect aggregate baseline the standalone
        // ReplayRouteTests conserves across the idle-split. The host engine, fed the bridged swings, must
        // reach the same sum.
        private const long YouDamageTotal = 2692084;

        private static string? RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ffxiv-combat-tracker.slnx"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        [SkippableFact]
        public void Host_engine_aggregates_bridged_replay_swings_to_the_satellite_total()
        {
            var root = RepoRoot();
            Skip.If(root is null, "repo root not found");

            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
                ? "Release" : "Debug";
            var exe = Path.Combine(root!, "src", "Fct.App", "bin", config, "net10.0-windows", "satellite", "Fct.LegacyHost.exe");
            var slice = Path.Combine(root!, "tests", "Fct.Compat.Act.Tests", "fixtures", "combat-slice.log");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(slice), $"slice fixture missing at {slice}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            // The host engine over an in-memory bus (InMemoryEventBus dispatches synchronously on Emit,
            // so each decoded frame folds deterministically). Sum YOU's damage from every encounter the
            // forwarded lifecycle closes on the engine.
            var session = new FakeGameSession();
            var engine = new ModernEncounterEngine(session, NullLogger<ModernEncounterEngine>.Instance);
            engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            long youDamage = 0;
            engine.Lifecycle.CombatEnded = enc =>
            {
                var you = enc.GetCombatant("YOU");
                if (you != null) youDamage += you.Damage;
            };

            var pipeName = "fct-replaybridge-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var conn = server.WaitForConnectionAsync();

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--replay \"{slice}\" 100000 --bridge {pipeName}",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("failed to start satellite");

            Assert.True(conn.Wait(TimeSpan.FromSeconds(20)), "satellite did not connect the bridge pipe");

            // Drain the event pipe as fast as possible on a background thread so the satellite's forwarder
            // ring never backs up (drop-oldest would corrupt the total). The satellite closes the pipe on
            // exit, so ReadLine returns null and the drain completes.
            var lines = new List<string>();
            var drain = Task.Run(() =>
            {
                try
                {
                    using var reader = new StreamReader(server);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        lock (lines) lines.Add(line);
                }
                catch { /* satellite exited / pipe closed */ }
            });

            Assert.True(proc.WaitForExit(120_000), "satellite did not finish the replay in time");
            drain.Wait(TimeSpan.FromSeconds(10));

            // Fold the collected frames through the host engine, in wire order.
            int frames = 0, swings = 0;
            List<string> captured;
            lock (lines) captured = new List<string>(lines);
            foreach (var line in captured)
            {
                if (!line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal)) continue; // READY/PLUGINS-END/LOG
                if (GameEventFrame.TryParse(line, out var evt) && evt is not null)
                {
                    frames++;
                    if (evt is CombatSwing) swings++;
                    session.Bus.Emit(evt);
                }
            }

            Assert.True(swings > 0, $"no swings forwarded over the bridge (decoded {frames} frames)");
            Assert.Equal(YouDamageTotal, youDamage);
        }
    }
}
