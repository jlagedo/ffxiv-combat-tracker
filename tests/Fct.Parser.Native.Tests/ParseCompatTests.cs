using Fct.Parser.Native;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Parser.Native.Tests
{
    // Differential test: our clean-room ActionEffect decode vs ACT's authoritative parse.
    //
    // The oracle (combat-slice.oracle.tsv) is the set of MasterSwings the REAL FFXIV_ACT_Plugin
    // produced from combat-slice.log (captured by the satellite's --parse-oracle mode). Here we
    // decode the same slice and assert our damage/heal VALUES match ACT's exactly — as
    // order-independent multisets, so a single missing/extra entry is reported precisely.
    //
    // Scope: the value decode of direct damage/heal effects (amount incl. the >65535 transform,
    // crit flag, miss/block/parry). Swing-type (auto vs ability), names, DoT/HoT simulation,
    // status and resource lines are out of scope for this milestone and excluded on both sides.
    public class ParseCompatTests
    {
        private readonly ITestOutputHelper _out;
        public ParseCompatTests(ITestOutputHelper output) => _out = output;

        private static string Fixture(string name) =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private sealed record OracleRow(int SwingType, bool Crit, long Damage, string Special);

        private static List<OracleRow> ReadOracle()
        {
            var rows = new List<OracleRow>();
            foreach (var line in File.ReadLines(Fixture("combat-slice.oracle.tsv")).Skip(1))
            {
                var c = line.Split('\t');
                rows.Add(new OracleRow(int.Parse(c[0]), c[1] == "1", long.Parse(c[2]), c[3]));
            }
            return rows;
        }

        private static List<CombatEffect> DecodeSlice()
        {
            var effects = new List<CombatEffect>();
            foreach (var raw in File.ReadLines(Fixture("combat-slice.log")))
            {
                if (!NetworkLogLine.TryParse(raw, out var line) || !line.IsAbility) continue;
                effects.AddRange(ActionEffectDecoder.Decode(line));
            }
            return effects;
        }

        // Multiset diff: returns (onlyInA, onlyInB) as flattened, counted lists.
        private static (List<string> missing, List<string> extra) BagDiff<T>(
            IEnumerable<T> ours, IEnumerable<T> oracle)
        {
            var a = new Dictionary<T, int>();
            var b = new Dictionary<T, int>();
            foreach (var x in ours) a[x] = a.GetValueOrDefault(x) + 1;
            foreach (var x in oracle) b[x] = b.GetValueOrDefault(x) + 1;
            var missing = new List<string>(); // in oracle, not (enough) in ours
            var extra = new List<string>();    // in ours, not (enough) in oracle
            foreach (var kv in b)
            {
                int diff = kv.Value - a.GetValueOrDefault(kv.Key);
                for (int i = 0; i < diff; i++) missing.Add(kv.Key!.ToString()!);
            }
            foreach (var kv in a)
            {
                int diff = kv.Value - b.GetValueOrDefault(kv.Key);
                for (int i = 0; i < diff; i++) extra.Add(kv.Key!.ToString()!);
            }
            return (missing, extra);
        }

        [Fact]
        public void Damage_values_match_act_exactly()
        {
            var oracle = ReadOracle();
            var effects = DecodeSlice();

            // The pure byte-level decode: amount (incl. the >65535 transform), crit, and
            // miss/block/parry. Fully determined by the line — no game-data tables needed.
            var oracleDmg = oracle.Where(r => r.SwingType is 0 or 2)
                                  .Select(r => (r.Crit, r.Damage, r.Special));
            var oursDmg = effects.Where(e => e.IsDamage)
                                 .Select(e => (e.IsCritical, e.Amount, e.Special));

            var (missing, extra) = BagDiff(oursDmg, oracleDmg);
            _out.WriteLine($"oracle damage entries={oracle.Count(r => r.SwingType is 0 or 2)} " +
                           $"ours={effects.Count(e => e.IsDamage)}");
            if (missing.Count > 0) _out.WriteLine("MISSING (ACT had, we didn't):\n  " + string.Join("\n  ", missing.Take(20)));
            if (extra.Count > 0) _out.WriteLine("EXTRA (we had, ACT didn't):\n  " + string.Join("\n  ", extra.Take(20)));

            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"damage decode diverges from ACT: {missing.Count} missing, {extra.Count} extra");
        }

        [Fact]
        public void Swing_type_classification_is_conservative_and_correct()
        {
            // Player auto-attacks (action id 0x07) are detectable from the line; NPC auto-attacks
            // use other ids that ACT only knows from its bundled action table. So our id-0x07
            // classification must be CONSERVATIVE: never call something auto that ACT calls an
            // ability (no false positives). The only allowed mismatches are ACT-auto/ours-ability
            // (NPC autos), and they must carry identical values — i.e. only the swing-type label
            // differs, never a value.
            var oracle = ReadOracle();
            var effects = DecodeSlice();

            var oracleDmg = oracle.Where(r => r.SwingType is 0 or 2)
                                  .Select(r => (r.SwingType, r.Crit, r.Damage, r.Special));
            var oursDmg = effects.Where(e => e.IsDamage)
                                 .Select(e => (e.SwingType, e.IsCritical, e.Amount, e.Special));

            var (missing, extra) = BagDiff(oursDmg, oracleDmg); // keyed by full tuple incl. swing
            _out.WriteLine($"swing-type mismatches: {missing.Count} (each is an NPC auto-attack " +
                           "whose id needs ACT's action table)");

            // No false positives: everything we labelled auto (0) ACT also labelled auto.
            Assert.All(extra, t => Assert.StartsWith("(2,", t));   // our extras are all ability(2)
            Assert.All(missing, t => Assert.StartsWith("(0,", t)); // ACT's were auto(0)
            // And they pair up 1:1 — same count, i.e. only the label differs, no value drift.
            Assert.Equal(missing.Count, extra.Count);
        }

        [Fact]
        public void Heal_decode_reproduces_every_act_reported_heal()
        {
            // ACT only REPORTS heals while InCombat (ReportCombatData.AddHealEntry), whereas our
            // pure decode has no combat state yet. So our heal set is a superset: we must
            // reproduce every heal ACT reported (0 missing); the extras are out-of-combat heals
            // ACT suppresses. Exact heal-count parity is the next milestone (InCombat tracking).
            var oracle = ReadOracle();
            var effects = DecodeSlice();

            var oracleHeal = oracle.Where(r => r.SwingType == 4).Select(r => (r.Crit, r.Damage));
            var oursHeal = effects.Where(e => e.IsHeal).Select(e => (e.IsCritical, e.Amount));

            var (missing, extra) = BagDiff(oursHeal, oracleHeal);
            _out.WriteLine($"oracle heal entries={oracle.Count(r => r.SwingType == 4)} ours={effects.Count(e => e.IsHeal)} " +
                           $"(extra={extra.Count} are out-of-combat heals ACT suppresses)");
            if (missing.Count > 0) _out.WriteLine("MISSING (ACT had, we didn't):\n  " + string.Join("\n  ", missing.Take(20)));

            Assert.True(missing.Count == 0,
                $"heal decode failed to reproduce {missing.Count} heal(s) ACT reported");
        }

        [Fact]
        public void Slice_has_meaningful_coverage()
        {
            var effects = DecodeSlice();
            Assert.True(effects.Count(e => e.IsDamage) > 100, "expected a substantial number of damage effects");
            Assert.Contains(effects, e => e.IsCritical);          // crits exercised
        }
    }
}
