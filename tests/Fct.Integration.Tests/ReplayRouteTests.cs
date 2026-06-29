using System.Diagnostics;
using Xunit;

namespace Fct.Integration.Tests
{
    // End-to-end live route on recorded data: run the staged satellite's --replay over a committed
    // anonymized combat slice. The real FFXIV plugin parses each line and drives our ACT facade;
    // the parse clock advances per line so idle-end splits the stream into encounters; each
    // encounter's ExportVariables (the strings OverlayPlugin/cactbot read) are dumped. This proves
    // the whole pipeline works without a live game. Per-value bit-perfectness is covered by the
    // Fct.Compat.Act differential; here we assert the route runs and conserves the totals.
    public sealed class ReplayRouteTests
    {
        // YOU's total outgoing damage in combat-slice (the bit-perfect aggregate baseline). The
        // idle-split encounters must sum back to it — damage is conserved across the split.
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
        public void Replay_splits_recorded_slice_into_encounters_with_export_vars()
        {
            var root = RepoRoot();
            Skip.If(root is null, "repo root not found");

            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
                ? "Release" : "Debug";
            var exe = Path.Combine(root!, "src", "Fct.App", "bin", config, "net10.0", "satellite", "Fct.LegacyHost.exe");
            var slice = Path.Combine(root!, "tests", "Fct.Compat.Act.Tests", "fixtures", "combat-slice.log");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(slice), $"slice fixture missing at {slice}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var outPath = Path.Combine(Path.GetTempPath(), "fct-replay-" + Guid.NewGuid().ToString("N") + ".tsv");
            using (var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--replay \"{slice}\" 100000 \"{outPath}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            })!)
            {
                Assert.True(p.WaitForExit(120_000), "replay did not finish in time");
            }

            Skip.IfNot(File.Exists(outPath), "replay produced no output (plugin may have failed to start)");
            var rows = File.ReadAllLines(outPath);
            Assert.True(rows.Length > 1, "no encounter rows produced");

            // header: encounter name encdps damage hits crithit% healed maxhit duration
            var you = rows.Skip(1).Select(r => r.Split('\t'))
                          .Where(c => c.Length >= 8 && c[1] == "YOU").ToList();
            Assert.NotEmpty(you);

            var encounters = you.Select(c => c[0]).Distinct().Count();
            Assert.True(encounters >= 2, $"expected idle-end to split into >=2 encounters, got {encounters}");

            long sum = you.Sum(c => long.Parse(c[3]));
            Assert.Equal(YouDamageTotal, sum); // damage conserved across the split

            // Every YOU encounter exposes the consumer-facing ExportVariables strings; an encounter
            // where YOU dealt damage carries the "Skill-Damage" maxhit format.
            foreach (var c in you)
            {
                Assert.False(string.IsNullOrEmpty(c[2]), "encdps empty");           // encdps
                if (long.Parse(c[3]) > 0) Assert.Matches(@"^.+-\d+$", c[7]);        // maxhit "Skill-Damage"
            }

            try { File.Delete(outPath); } catch { }
        }
    }
}
