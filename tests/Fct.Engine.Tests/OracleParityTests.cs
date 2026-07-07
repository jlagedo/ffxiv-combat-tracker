using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Engine.Tests
{
    /// <summary>
    /// Cross-runtime engine parity (ISOLATION-PLAN P1): the committed real-ACT oracle swing stream
    /// (<c>combat-slice*.oracle.tsv</c>) is fed through <see cref="ModernEncounterEngine"/> over the
    /// event bus — the engine's real production input path (bus → OnEvent → CombatSwing→MasterSwing →
    /// EncounterLifecycle) — and every <c>ExportVariables</c> string the engine renders is asserted
    /// equal to the strings the real ACT binary produced from the identical stream
    /// (<c>combat-slice*.exportvars.tsv</c>, captured by tools/act-oracle). The net10 host engine is
    /// thus held to the SAME oracle as the net48 engine (Fct.Compat.Act.Tests/ExportVarsCompatTests),
    /// bit-for-bit — divergence fails CI.
    /// </summary>
    public sealed class OracleParityTests
    {
        private static readonly IReadOnlyDictionary<string, object> NoTags = new Dictionary<string, object>();

        // PIPELINE-COMPLETENESS-PLAN P1.2 / G1: ExportVariables keys the real FFXIV_ACT_Plugin registers
        // that EngineTables.Install() did not register yet, pending P5's port. Empty since P5.6
        // (Last10/30/60DPS, the final G1 keys) — P5.9's exit criterion (this set reaching empty,
        // P1.2 fully green) is met; kept as an empty set so a future registration regression still
        // fails loudly here rather than silently widening scope.
        private static readonly HashSet<string> PendingP5Keys = new(StringComparer.Ordinal);

        private readonly ITestOutputHelper _out;
        public OracleParityTests(ITestOutputHelper output) => _out = output;

        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        // Feed the oracle swing stream through a real ModernEncounterEngine and return the live,
        // still-active encounter it aggregated — the exact input path the bridge drives in production.
        private static EncounterData BuildThroughEngine(string slice)
        {
            var session = new FakeGameSession();
            var engine = new ModernEncounterEngine(session, NullLogger<ModernEncounterEngine>.Instance);
            // Subscribe the engine to the bus; InMemoryEventBus dispatches synchronously on Emit, so the
            // whole stream is folded deterministically with no polling.
            engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            long seq = 0;
            bool opened = false;
            foreach (var line in File.ReadLines(Fixture(slice + ".oracle.tsv")))
            {
                if (seq == 0 && line.StartsWith("swingType", StringComparison.Ordinal)) { seq++; continue; }
                if (line.Length == 0) continue;
                var c = line.Split('\t');
                int swingType = int.Parse(c[0], CultureInfo.InvariantCulture);
                bool crit = c[1] == "1";
                long dmg = long.Parse(c[2], CultureInfo.InvariantCulture);
                string special = c[3], attackType = c[4], attacker = c[5], damageType = c[6], victim = c[7];
                var time = DateTimeOffset.Parse(c[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                // ACT calls SetEncounter per hostile action before the swing; the engine's lifecycle
                // auto-opens on the first one. Mirror that so the encounter is open when swings arrive.
                if (!opened)
                {
                    session.Bus.Emit(new SetEncounterRequested(seq, time, attacker, victim));
                    opened = true;
                }
                session.Bus.Emit(new CombatSwing(seq, time, swingType, crit, special, dmg, (int)seq,
                    attackType, attacker, damageType, victim, NoTags));
                seq++;
            }

            var enc = engine.Lifecycle.ActiveZone.ActiveEncounter;
            Assert.NotNull(enc);
            return enc!;
        }

        [Theory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public void Export_variable_strings_match_real_act_through_the_host_engine(string slice)
        {
            // ACT renders numbers with the running culture; the baseline was captured under an
            // invariant-equivalent (period) culture, so pin the thread to match deterministically.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var enc = BuildThroughEngine(slice);
            var mismatches = new List<string>();
            int checked_ = 0;

            foreach (var line in File.ReadLines(Fixture(slice + ".exportvars.tsv")))
            {
                if (line.StartsWith("name\tkey", StringComparison.Ordinal)) continue;
                if (line.Length == 0) continue;
                var c = line.Split('\t');
                string name = c[0], key = c[1], want = c.Length > 2 ? c[2] : "";
                string got;
                if (name == "*ENCOUNTER*")
                {
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

            _out.WriteLine($"checked {checked_} export-variable strings through ModernEncounterEngine");
            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count} ExportVariable string(s) diverge from real ACT via the host engine:\n  " +
                string.Join("\n  ", mismatches.Take(25)));
        }

        // PIPELINE-COMPLETENESS-PLAN P1.2: the same production input path (BuildThroughEngine), diffed
        // against the plugin-in-the-loop oracle baseline (P1.1, tools/act-oracle --plugin-baseline)
        // instead of the ACT-core baseline above. Keys P5 hasn't ported yet are documented in
        // PendingP5Keys rather than left as an opaque mass-diff; the final assertion intentionally fails
        // while that list is non-empty so the gate reads red until P5 completes the registration.
        [Fact]
        public void ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5()
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            const string slice = "combat-slice";
            var enc = BuildThroughEngine(slice);
            var mismatches = new List<(string Key, string Detail)>();
            int checked_ = 0;

            foreach (var line in File.ReadLines(Fixture(slice + ".plugin.exportvars.tsv")))
            {
                if (line.StartsWith("name\tkey", StringComparison.Ordinal)) continue;
                if (line.Length == 0) continue;
                var c = line.Split('\t');
                string name = c[0], key = c[1], want = c.Length > 2 ? c[2] : "";
                string got;
                if (name == "*ENCOUNTER*")
                {
                    if (!EncounterData.ExportVariables.TryGetValue(key, out var encFmt))
                        { mismatches.Add((key, $"*ENCOUNTER*.{key}: key not registered in ours")); continue; }
                    try { got = encFmt.GetExportString(enc, enc.GetAllies(), ""); }
                    catch (Exception ex) { mismatches.Add((key, $"*ENCOUNTER*.{key}: THREW {ex.GetType().Name}: {ex.Message}")); continue; }
                }
                else
                {
                    var cd = enc.GetCombatant(name);
                    if (cd == null) { mismatches.Add((key, $"{name}: combatant missing in ours")); continue; }
                    if (!CombatantData.ExportVariables.TryGetValue(key, out var fmt))
                        { mismatches.Add((key, $"{name}.{key}: key not registered in ours")); continue; }
                    try { got = fmt.GetExportString(cd, ""); }
                    catch (Exception ex) { mismatches.Add((key, $"{name}.{key}: THREW {ex.GetType().Name}: {ex.Message}")); continue; }
                }
                checked_++;
                // CurrentZoneName (P5.7) is already registered with the plugin's identical d.ZoneName
                // formula (CombatTables.cs:220-224) — its VALUE is zone-frame provenance, not a swing-
                // stream fact, so it's excluded from strict comparison here for the same reason the
                // ACT-core diff excludes it: the P1.1 oracle harness replays swings only (no zone frame),
                // baking in "", while a real replay's zone comes from whatever ChangeZone it was fed.
                if (name == "*ENCOUNTER*" && key == "CurrentZoneName") continue;
                if (got != want) mismatches.Add((key, $"{name}.{key}: ours='{got}' oracle='{want}'"));
            }

            _out.WriteLine($"checked {checked_} plugin export-variable strings through ModernEncounterEngine");

            // Anything diverging outside the documented skip-list is a regression, not pending P5 work.
            var unexpected = mismatches.Where(m => !PendingP5Keys.Contains(m.Key)).ToList();
            Assert.True(unexpected.Count == 0,
                $"{unexpected.Count} plugin ExportVariable string(s) diverge outside the documented P5 skip-list:\n  " +
                string.Join("\n  ", unexpected.Select(m => m.Detail).Take(25)));

            // A skip-listed key that no longer diverges is stale — the list must shrink, not just grow stale entries.
            var stillMissing = mismatches.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
            var stale = PendingP5Keys.Where(k => !stillMissing.Contains(k)).ToList();
            Assert.True(stale.Count == 0,
                $"skip-listed key(s) no longer diverge from the oracle — remove from PendingP5Keys: {string.Join(", ", stale)}");

            // P5.9's exit criterion: green only once every G1 key is registered and matches (empty skip-list).
            Assert.True(PendingP5Keys.Count == 0,
                "P1.2 pending P5 registration of " + PendingP5Keys.Count + " ExportVariables key(s): " +
                string.Join(", ", PendingP5Keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        // The EncounterProjector face the UI/replica read must carry the same oracle numbers: the
        // projected snapshot's encounter total + per-ally damage equal the real-ACT aggregate baseline.
        [Theory]
        [InlineData("combat-slice")]
        [InlineData("combat-slice2")]
        public void Projected_snapshot_totals_match_the_real_act_aggregate(string slice)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var enc = BuildThroughEngine(slice);
            var snapshot = EncounterProjector.Project(enc);

            var baseline = File.ReadAllLines(Fixture(slice + ".aggregate.tsv"));
            var header = baseline[0].Split('\t').ToList();
            int damageIdx = header.IndexOf("Damage");
            var rows = baseline.Skip(1).Select(l => l.Split('\t')).ToDictionary(f => f[0], f => f);

            long encDamage = long.Parse(rows["*ENCOUNTER*"][damageIdx], CultureInfo.InvariantCulture);
            Assert.Equal(encDamage, snapshot.Damage);

            foreach (var pc in snapshot.Combatants)
            {
                Assert.True(rows.TryGetValue(pc.Name, out var b), $"{pc.Name}: projected but absent from oracle aggregate");
                long want = long.Parse(b[damageIdx], CultureInfo.InvariantCulture);
                Assert.Equal(want, pc.Damage);
            }
        }
    }
}
