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

        [SkippableFact]
        public void Host_engine_aggregates_bridged_replay_swings_to_the_satellite_total()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");

            var exe = ReplayBridgeHarness.SatelliteExe(root!);
            var slice = ReplayBridgeHarness.SliceLog(root!, "combat-slice");
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

            // Run the satellite over the slice and collect every bridged line, then fold the decoded
            // frames through the host engine in wire order.
            var captured = ReplayBridgeHarness.RunAndCollect(exe, slice);
            int frames = 0, swings = 0;
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
