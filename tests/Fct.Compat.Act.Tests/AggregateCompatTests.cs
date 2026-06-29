using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Advanced_Combat_Tracker;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Compat.Act.Tests
{
    // ACT-layer differential compat: feed the captured real-plugin MasterSwing stream
    // (combat-slice.oracle.tsv) through OUR clean-room Fct.Compat.Act aggregation and assert every
    // per-combatant + encounter aggregate matches the REAL Advanced Combat Tracker binary's output
    // (combat-slice.aggregate.tsv, produced by tools/act-oracle). Values are formatted exactly as
    // the oracle wrote them, so this is a bit-for-bit comparison of the numbers consumers read.
    [Collection("act")]
    public sealed class AggregateCompatTests
    {
        private readonly ITestOutputHelper _out;
        public AggregateCompatTests(ITestOutputHelper output) => _out = output;

        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        private static string I(long d) => d.ToString(CultureInfo.InvariantCulture);

        internal static EncounterData BuildOurEncounter()
        {
            ActTables.EnsureInstalled();
            ActGlobals.charName = "YOU";
            var enc = ActTables.NewEncounter("YOU", "");
            int n = 0;
            foreach (var line in File.ReadLines(Fixture("combat-slice.oracle.tsv")))
            {
                if (n++ == 0 && line.StartsWith("swingType")) continue;
                var c = line.Split('\t');
                int swingType = int.Parse(c[0]);
                bool crit = c[1] == "1";
                long dmg = long.Parse(c[2], CultureInfo.InvariantCulture);
                string special = c[3], attackType = c[4], attacker = c[5], damageType = c[6], victim = c[7];
                var time = DateTime.Parse(c[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                enc.AddCombatAction(new MasterSwing(swingType, crit, special, new Dnum(dmg), time, n, attackType, attacker, damageType, victim));
            }
            return enc;
        }

        // name -> (field -> value) from the oracle baseline.
        private static (List<string> header, Dictionary<string, string[]> rows) ReadBaseline()
        {
            var lines = File.ReadAllLines(Fixture("combat-slice.aggregate.tsv"));
            var header = lines[0].Split('\t').ToList();
            var rows = new Dictionary<string, string[]>();
            foreach (var line in lines.Skip(1))
            {
                var c = line.Split('\t');
                rows[c[0]] = c;
            }
            return (header, rows);
        }

        [Fact]
        public void Per_combatant_aggregates_match_real_act_exactly()
        {
            var enc = BuildOurEncounter();
            var (header, baseline) = ReadBaseline();
            var mismatches = new List<string>();

            foreach (var cd in enc.Items.Values)
            {
                if (!baseline.TryGetValue(cd.Name, out var b))
                {
                    mismatches.Add($"{cd.Name}: present in ours, absent in oracle");
                    continue;
                }
                var atAll = cd.GetAttackType("All", CombatantData.DamageTypeDataOutgoingDamage);
                long maxHit = atAll?.MaxHit ?? 0L;

                void Check(string field, string ours)
                {
                    int idx = header.IndexOf(field);
                    string want = b[idx];
                    if (ours != want) mismatches.Add($"{cd.Name}.{field}: ours={ours} oracle={want}");
                }

                Check("Damage", I(cd.Damage));
                Check("Healed", I(cd.Healed));
                Check("DamageTaken", I(cd.DamageTaken));
                Check("HealsTaken", I(cd.HealsTaken));
                Check("Hits", I(cd.Hits));
                Check("CritHits", I(cd.CritHits));
                Check("Swings", I(cd.Swings));
                Check("Misses", I(cd.Misses));
                Check("CritDamPerc", F(cd.CritDamPerc));
                Check("CritHealPerc", F(cd.CritHealPerc));
                Check("Heals", I(cd.Heals));
                Check("CritHeals", I(cd.CritHeals));
                Check("EncDPS", F(cd.EncDPS));
                Check("EncHPS", F(cd.EncHPS));
                Check("MaxHit", I(maxHit));
                Check("Deaths", I(cd.Deaths));
                Check("Kills", I(cd.Kills));
                Check("DamagePercent", cd.DamagePercent);
                Check("HealedPercent", cd.HealedPercent);
                Check("DurationSec", F(cd.Duration.TotalSeconds));
            }

            // Every oracle combatant must exist in ours too.
            foreach (var name in baseline.Keys)
                if (name != "*ENCOUNTER*" && enc.GetCombatant(name) == null)
                    mismatches.Add($"{name}: present in oracle, absent in ours");

            _out.WriteLine($"combatants ours={enc.Items.Count} oracle={baseline.Count - 1}");
            if (mismatches.Count > 0) _out.WriteLine("MISMATCHES:\n  " + string.Join("\n  ", mismatches.Take(60)));
            Assert.True(mismatches.Count == 0, $"{mismatches.Count} field mismatch(es) vs real ACT");
        }

        [Fact]
        public void Encounter_aggregates_match_real_act_exactly()
        {
            var enc = BuildOurEncounter();
            var (_, baseline) = ReadBaseline();
            var e = baseline["*ENCOUNTER*"];
            // *ENCOUNTER*  Damage Healed DPS Duration NumCombatants NumAllies
            var mismatches = new List<string>();
            void Check(string field, int idx, string ours) { if (ours != e[idx]) mismatches.Add($"ENC.{field}: ours={ours} oracle={e[idx]}"); }
            Check("Damage", 1, I(enc.Damage));
            Check("Healed", 2, I(enc.Healed));
            Check("DPS", 3, F(enc.DPS));
            Check("Duration", 4, F(enc.Duration.TotalSeconds));
            Check("NumCombatants", 5, I(enc.NumCombatants));
            Check("NumAllies", 6, I(enc.NumAllies));

            if (mismatches.Count > 0) _out.WriteLine("MISMATCHES:\n  " + string.Join("\n  ", mismatches));
            Assert.True(mismatches.Count == 0, $"{mismatches.Count} encounter mismatch(es) vs real ACT");
        }
    }
}
