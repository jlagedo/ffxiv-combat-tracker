using Fct.Parser.Native;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Parser.Native.Tests
{
    // Robustness smoke test: stream a slice of a REAL ACT Network_*.log and assert the
    // structural parser handles every line without throwing and recognizes the expected
    // shape of live data. Skips when no logs are present (e.g. CI without an ACT install),
    // so it never breaks a clean checkout but exercises 100MB-class real data where available.
    public class RealLogSmokeTests
    {
        private const int MaxLines = 300_000;
        private readonly ITestOutputHelper _out;
        public RealLogSmokeTests(ITestOutputHelper output) => _out = output;

        private static string? NewestLog()
        {
            var dir = Environment.GetEnvironmentVariable("FCT_FFXIV_LOGS");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Advanced Combat Tracker", "FFXIVLogs");
            if (!Directory.Exists(dir))
                return null;

            return new DirectoryInfo(dir)
                .GetFiles("Network_*.log")
                .Where(f => f.Length > 1_000_000)        // skip stub/aborted captures
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        [SkippableFact]
        public void Real_log_slice_parses_cleanly()
        {
            var path = NewestLog();
            Skip.If(path is null, "No FFXIV Network_*.log present (set FCT_FFXIV_LOGS to point at one).");

            long total = 0, parsed = 0, abilities = 0;
            var byType = new Dictionary<int, long>();
            DateTimeOffset? first = null, last = null;
            long outOfOrder = 0;
            DateTimeOffset prev = DateTimeOffset.MinValue;

            foreach (var raw in File.ReadLines(path!).Take(MaxLines))
            {
                if (raw.Length == 0) continue;
                total++;
                if (!NetworkLogLine.TryParse(raw, out var line))
                    continue;
                parsed++;

                byType[line.TypeCode] = byType.GetValueOrDefault(line.TypeCode) + 1;
                if (line.IsAbility) abilities++;

                first ??= line.Timestamp;
                last = line.Timestamp;
                if (line.Timestamp < prev) outOfOrder++;
                prev = line.Timestamp;
            }

            var topTypes = string.Join(", ", byType.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
            _out.WriteLine($"file={Path.GetFileName(path)} lines={total} parsed={parsed} " +
                           $"abilities={abilities} span={first}..{last} outOfOrder={outOfOrder}");
            _out.WriteLine("top types: " + topTypes);

            Assert.True(total > 0, "log slice was empty");
            // The format is rigid; essentially every non-empty line must parse.
            Assert.True(parsed >= total * 0.999, $"only {parsed}/{total} lines parsed");
            // A real capture has combat — ability lines must be present.
            Assert.True(abilities > 0, "no ability (21/22) lines found in the slice");
            // Sanity on the timestamps (they are NOT globally ordered — many line types
            // interleave — so order is reported as a diagnostic, not asserted).
            Assert.True(first!.Value.Year >= 2013, $"implausible timestamp {first}");
            Assert.True((last!.Value - first.Value).Duration() < TimeSpan.FromDays(2),
                $"slice spans an implausible range {first}..{last}");
        }

        [SkippableFact]
        public void Ability_lines_project_source_and_target_names()
        {
            var path = NewestLog();
            Skip.If(path is null, "No FFXIV Network_*.log present.");

            int chec99 = 0;
            foreach (var raw in File.ReadLines(path!).Take(MaxLines))
            {
                if (!NetworkLogLine.TryParse(raw, out var line) || !line.IsAbility) continue;
                var a = line.Ability;
                if (a is null) continue;                 // very short ability lines are allowed to project null
                Assert.False(string.IsNullOrEmpty(a.Value.Source.Id), "ability missing source id");
                Assert.False(string.IsNullOrEmpty(a.Value.AbilityName), "ability missing name");
                if (++chec99 >= 5000) break;             // a representative sample is enough
            }

            Skip.If(chec99 == 0, "no projectable ability lines in slice");
            _out.WriteLine($"validated {chec99} ability projections");
        }
    }
}
