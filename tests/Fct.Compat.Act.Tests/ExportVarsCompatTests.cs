using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Advanced_Combat_Tracker;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Compat.Act.Tests
{
    // ExportVariables differential: the strings OverlayPlugin/cactbot read off each CombatantData
    // must match the real ACT binary exactly. The baseline (combat-slice*.exportvars.tsv) is the
    // output of ACT's own FormActMain.CombatantFormatSwitch for every key (captured by
    // tools/act-oracle); here we assert our CombatTables formatters reproduce each string.
    [Collection("act")]
    public sealed class ExportVarsCompatTests
    {
        private readonly ITestOutputHelper _out;
        public ExportVarsCompatTests(ITestOutputHelper output) => _out = output;

        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        [Theory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public void Export_variable_strings_match_real_act_exactly(string slice)
        {
            // ACT renders numbers with the running culture; the baseline was captured under an
            // invariant-equivalent (period) culture, so pin the test thread to match deterministically.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var enc = AggregateCompatTests.BuildOurEncounter(slice);
            var mismatches = new List<string>();
            int checked_ = 0;

            foreach (var line in File.ReadLines(Fixture(slice + ".exportvars.tsv")))
            {
                if (line.StartsWith("name\tkey")) continue;
                var c = line.Split('\t');
                string name = c[0], key = c[1], want = c.Length > 2 ? c[2] : "";
                string got;
                if (name == "*ENCOUNTER*")
                {
                    // The encounter-level "Encounter" object cactbot reads off EncounterData.ExportVariables.
                    if (!EncounterData.ExportVariables.TryGetValue(key, out var encFmt))
                        { mismatches.Add($"*ENCOUNTER*.{key}: key not registered in ours"); continue; }
                    try { got = encFmt.GetExportString(enc, enc.GetAllies(), ""); }
                    catch (Exception ex) { mismatches.Add($"*ENCOUNTER*.{key}: THREW {ex.GetType().Name}: {ex.Message}"); continue; }
                }
                else
                {
                    var cd = enc.GetCombatant(name);
                    if (cd == null) { mismatches.Add($"{name}: combatant missing in ours"); continue; }
                    if (!CombatantData.ExportVariables.TryGetValue(key, out var fmt))
                        { mismatches.Add($"{name}.{key}: key not registered in ours"); continue; }
                    try { got = fmt.GetExportString(cd, ""); }
                    catch (Exception ex) { mismatches.Add($"{name}.{key}: THREW {ex.GetType().Name}: {ex.Message}"); continue; }
                }
                checked_++;
                if (got != want) mismatches.Add($"{name}.{key}: ours='{got}' oracle='{want}'");
            }

            _out.WriteLine($"checked {checked_} export-variable strings");
            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count} ExportVariable string(s) diverge from real ACT:\n  " +
                string.Join("\n  ", mismatches.Take(25)));
        }
    }
}
