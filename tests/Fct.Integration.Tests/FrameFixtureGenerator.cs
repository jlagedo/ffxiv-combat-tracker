using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Bridge;
using Xunit;

namespace Fct.Integration.Tests
{
    // Generates the committed, anonymized frame fixtures the P2 harness replays (ISOLATION-PLAN P2).
    // Runs the staged satellite in `--replay … --bridge` over each committed slice, records the decoded
    // aggregation-feed frames (swings + encounter lifecycle) to tests/fixtures/frames/<slice>.frames.tsv
    // through the real FrameSessionRecorder. Deliberately NOT run in normal CI — it needs the real
    // FFXIV_ACT_Plugin and writes source fixtures; set FCT_GENERATE_FIXTURES=1 to (re)generate after the
    // frame codec or slice corpus changes. The fixtures it produces are replayed with no plugin by the
    // deterministic gate (Fct.Engine.Tests/FrameReplayTests).
    public sealed class FrameFixtureGenerator
    {
        // The aggregation feed the host engine consumes — excludes the Debug-only synthetic "forwarder
        // online" ZoneChanged so the recorded offsets track the slice's own log timestamps.
        private static readonly GameEventFilter Feed = new(
            new[]
            {
                typeof(CombatSwing), typeof(SetEncounterRequested),
                typeof(ZoneChangeRequested), typeof(EndCombatRequested),
            },
            IncludeRawLogLines: false);

        [SkippableFact]
        public void Generate_frame_fixtures_from_replay()
        {
            Skip.If(Environment.GetEnvironmentVariable("FCT_GENERATE_FIXTURES") != "1",
                "set FCT_GENERATE_FIXTURES=1 (with FFXIV_ACT_Plugin installed) to (re)generate the P2 frame fixtures");

            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var exe = ReplayBridgeHarness.SatelliteExe(root!);
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var outDir = Path.Combine(root!, "tests", "fixtures", "frames");
            Directory.CreateDirectory(outDir);

            foreach (var slice in new[] { "combat-slice", "combat-slice2" })
            {
                var sliceLog = ReplayBridgeHarness.SliceLog(root!, slice);
                Skip.IfNot(File.Exists(sliceLog), $"slice fixture missing at {sliceLog}");

                var captured = ReplayBridgeHarness.RunAndCollect(exe, sliceLog);

                var bus = new InMemoryEventBus();
                var outPath = Path.Combine(outDir, slice + ".frames.tsv");
                long recorded;
                using (var writer = new StreamWriter(outPath, append: false))
                using (var recorder = new FrameSessionRecorder(bus, writer, Feed))
                {
                    foreach (var line in captured)
                    {
                        if (!line.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal)) continue;
                        if (GameEventFrame.TryParse(line, out var evt) && evt is not null)
                            bus.Emit(evt);
                    }
                    recorded = recorder.Count;
                }

                Assert.True(recorded > 0, $"no aggregation frames recorded for {slice}");
            }
        }
    }
}
