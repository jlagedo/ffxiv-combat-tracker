using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Advanced_Combat_Tracker;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Compat.Act.Tests
{
    // P0.1 (PIPELINE-COMPLETENESS-PLAN) line-seam coverage derisk: prove the facade's file tail
    // (OpenLog -> StartLogTail -> TailLoop -> FeedLine -> OnLogLineRead) delivers EVERY line type
    // byte-identical and in order, with no per-type filtering. Plugin-free: the tail fires the ACT
    // hooks on whatever complete lines it reads. This is the from-start line-stream diff (P1.3) in
    // miniature; the slice is the reference side (real ACT delivers the tailed file verbatim).
    public sealed class LineSeamCoverageTests
    {
        private readonly ITestOutputHelper _out;
        public LineSeamCoverageTests(ITestOutputHelper output) => _out = output;

        // One real Network_*.log line of each of the twelve types in the P0.1 required set
        // {00,01,02,21,40,249,250,253} plus 12/22/24/26, captured from the E:\tmp\logs corpus.
        private static readonly string[] Slice =
        {
            "253|2026-01-03T22:50:01.0445077-03:00|FFXIV_ACT_Plugin Version: 2.7.4.9 (50BCD605C50A749F)|a7996fe26936a886",
            "249|2026-01-03T22:50:01.0445077-03:00|Selected Language ID: English, Disable Damage Shield: False, Disable Combine Pets: False, Parse Filter: None, DoTCrits: False|deadbeefdeadbeef",
            "250|2026-01-03T22:50:04.0117130-03:00|Detected Process ID: 11132, Client Mode: FFXIV_64, IsAdmin: True, Game Version: 2025.12.23.0000.0000|98a6818603a94887",
            "40|2026-01-03T22:50:05.6670000-03:00|1095|Unlost World|Mistwake|Quan Caverns|bb44e3219ed799a0",
            "01|2026-01-03T22:50:05.6670000-03:00|522|Mistwake|2d5ef64f295c1d6b",
            "02|2026-01-03T22:50:05.6670000-03:00|106D3875|Leon Lanceloth|545d749f19bea137",
            "12|2026-01-03T22:50:05.6670000-03:00|25|208|460|6055|6170|342|440|208|1999|2286|6170|342|1791|420|2687|420|4000174A6252A4|7f5bd22c7ec874f1",
            "00|2026-01-03T22:50:22.0000000-03:00|102B||Gyozori DreamscapeHyperion uses Sprint.|64eb030a67204fb0",
            "21|2026-02-10T21:37:01.3840000-03:00|1095FC1A|Lucifer Morningstar|03|Sprint|1095FC1A|Lucifer Morningstar|1E00000E|320000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|185913|185913|10000|10000|||-599.28|-839.18|30.02|0",
            "22|2026-02-10T21:41:20.8350000-03:00|107EA17D|Aeria Moon|1D0F|Earthly Star|107EA17D|Aeria Moon|F|4C88000|3E|9D8000|0|0|0|0|0|0|0|0|0|0|0|0|194913|194913|10000|10000|||98.25|107.90|0.00|3.11",
            "24|2026-02-10T21:38:22.6020000-03:00|40000301|Striking Dummy|DoT|0|21E3|44|44|0|10000|||-591.40|-847.92|32.09|2.36|1095FC1A|Lucifer Morningstar|FFFFFFFF|185913|185913|10000|10000",
            "26|2026-02-10T21:37:01.5170000-03:00|32|Sprint|20.00|1095FC1A|Lucifer Morningstar|1095FC1A|Lucifer Morningstar|1E|185913|185913|052be707c0f269d2",
        };

        [Fact]
        public void Facade_tail_delivers_every_line_type_verbatim_and_in_order()
        {
            var path = Path.Combine(Path.GetTempPath(), "fct-p0-1-" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(path, "");   // start empty: the tail begins at end-of-file, reads only appends

            var received = new List<string>();
            var gate = new object();

            var act = new FormActMain { LogFilePath = path };
            LogLineEventDelegate capture = (isImport, args) =>
            {
                lock (gate) received.Add(args.logLine);
            };
            act.OnLogLineRead += capture;

            try
            {
                act.OpenLog(false, false);          // spawns the tail thread on `path`
                SpinWaitTail();                     // let the thread open the stream at length 0

                // Append the slice exactly as the plugin appends to Network_*.log (LF-terminated).
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    foreach (var line in Slice) w.Write(line + "\n");
                }

                // Spin until all lines arrive (or timeout).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000)
                {
                    lock (gate) if (received.Count >= Slice.Length) break;
                    Thread.Sleep(25);
                }
            }
            finally
            {
                act.StopLogTail();
                act.OnLogLineRead -= capture;
                try { File.Delete(path); } catch { }
            }

            string[] got;
            lock (gate) got = received.ToArray();

            // Per-type arrival count (leading NN| key), for the recorded verdict.
            var byType = got
                .Select(l => l.Split('|')[0])
                .GroupBy(t => t)
                .ToDictionary(g => g.Key, g => g.Count());
            _out.WriteLine("received " + got.Length + " of " + Slice.Length + " lines");
            foreach (var kv in byType.OrderBy(k => int.Parse(k.Key)))
                _out.WriteLine($"  type {kv.Key}: {kv.Value}");

            // Byte-identical, in order, one of each type.
            Assert.Equal(Slice, got);
            Assert.Equal(Slice.Length, byType.Count);   // every type distinct, arrived exactly once
            Assert.All(byType.Values, c => Assert.Equal(1, c));
        }

        // The tail thread waits up to 20 s for the file to exist, then opens it and records length.
        // The file already exists (empty) here, so a short spin is enough for it to reach the read loop.
        private static void SpinWaitTail() => Thread.Sleep(400);
    }
}
