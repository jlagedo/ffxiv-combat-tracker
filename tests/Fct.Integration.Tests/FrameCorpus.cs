using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Fct.Abstractions;
using Fct.Bridge;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P9b corpus looper: concatenate the committed combat-slice{,2} frame fixtures N times
    // into one long deterministic soak corpus, rebasing each iteration's offset-from-start arithmetically
    // (add k*passSpan to the leading relativeMicros column) so the looped stream stays offset-ordered across
    // iterations — the offset-based FrameSession codec the fixtures use (FrameSession.cs). Each committed
    // slice bookends its own encounter with an explicit leading ZCHG/SETENC and a terminal ENDC, so N passes
    // fold into N per-slice encounters and the YOU total is exactly N x the per-slice oracle — no injected
    // boundary needed (splits are driven by the explicit lifecycle frames, not a wall-clock idle timer).
    internal static class FrameCorpus
    {
        // The two committed slices, concatenated once = one "pass"; the corpus is N passes.
        public static readonly string[] Slices = { "combat-slice", "combat-slice2" };

        // One pass of (offset, wire) pairs read from both slice fixtures, in order.
        private static List<(long Offset, string Wire)> ReadPass(string root)
        {
            var pass = new List<(long, string)>();
            foreach (var slice in Slices)
            {
                foreach (var line in File.ReadLines(ReplayBridgeHarness.FramesFixture(root, slice)))
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    if (!long.TryParse(line.Substring(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
                        continue;
                    pass.Add((off, line.Substring(tab + 1)));
                }
            }
            return pass;
        }

        // The looped corpus as FrameSession lines ("<rebasedMicros>\t<wire>"): N passes, each iteration's
        // offsets shifted by k*passSpan. passSpan (out) is the max offset in one pass — the per-iteration
        // stride that keeps iteration k's frames at or after iteration k-1's.
        public static IReadOnlyList<string> LoopedLines(string root, int n, out long passSpan)
        {
            var pass = ReadPass(root);
            passSpan = 0;
            foreach (var (off, _) in pass) if (off > passSpan) passSpan = off;
            var lines = new List<string>(pass.Count * n);
            for (int k = 0; k < n; k++)
            {
                long baseOff = (long)k * passSpan;
                foreach (var (off, wire) in pass)
                    lines.Add((baseOff + off).ToString(CultureInfo.InvariantCulture) + "\t" + wire);
            }
            return lines;
        }

        // The looped corpus decoded to GameEvents for in-process bus drive (offsets are ignored by the bus;
        // they are rebased anyway so the corpus stays honest for any cadence-aware driver).
        public static IEnumerable<GameEvent> Events(string root, int n)
        {
            foreach (var line in LoopedLines(root, n, out _))
                if (FrameSession.TryParseLine(line, out _, out var evt) && evt is not null)
                    yield return evt;
        }

        // Write the looped corpus to a .tsv usable by a --replay-frames producer satellite.
        public static void WriteLoopedFixture(string root, int n, string path)
        {
            using var w = new StreamWriter(path, append: false);
            w.WriteLine(FrameSession.Header);
            foreach (var line in LoopedLines(root, n, out _)) w.WriteLine(line);
        }
    }
}
