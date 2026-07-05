using System.Globalization;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Bridge;
using Fct.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P2 full-fabric gate: drive the plugin-free replay-satellite (`--replay-frames`)
    // over a committed FrameSession fixture so it streams the recorded frames up the REAL pipe, then
    // fold the decoded frames through a real ModernEncounterEngine and assert the host engine reproduces
    // the recorded encounter totals. Unlike the wire-path parity gate this needs NO FFXIV_ACT_Plugin and
    // NO game — only the staged satellite — so it exercises the whole process-boundary fabric in CI.
    public sealed class FrameReplaySatelliteTests
    {
        private static long OracleYouDamage(string root, string slice)
        {
            var lines = File.ReadAllLines(Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".aggregate.tsv"));
            int damageIdx = Array.IndexOf(lines[0].Split('\t'), "Damage");
            var you = lines.Skip(1).Select(l => l.Split('\t')).First(c => c[0] == "YOU");
            return long.Parse(you[damageIdx], CultureInfo.InvariantCulture);
        }

        [SkippableTheory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public void Replay_satellite_streams_a_fixture_that_the_host_engine_aggregates(string slice)
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var exe = ReplayBridgeHarness.SatelliteExe(root!);
            var fixture = ReplayBridgeHarness.FramesFixture(root!, slice);
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(fixture), $"frame fixture missing at {fixture}");

            var session = new FakeGameSession();
            var engine = new ModernEncounterEngine(session, NullLogger<ModernEncounterEngine>.Instance);
            engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            long youDamage = 0;
            engine.Lifecycle.CombatEnded = enc =>
            {
                var you = enc.GetCombatant("YOU");
                if (you != null) youDamage += you.Damage;
            };

            var captured = ReplayBridgeHarness.RunFramesAndCollect(exe, fixture);

            int frames = 0, swings = 0;
            foreach (var line in captured)
            {
                if (!line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal)) continue;
                if (GameEventFrame.TryParse(line, out var evt) && evt is not null)
                {
                    frames++;
                    if (evt is CombatSwing) swings++;
                    session.Bus.Emit(evt);
                }
            }

            Assert.True(swings > 0, $"replay-satellite forwarded no swings (decoded {frames} frames)");
            Assert.Equal(OracleYouDamage(root!, slice), youDamage);
        }
    }
}
