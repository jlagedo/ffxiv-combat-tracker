using System.Globalization;
using System.Text;

// Corpus-scale ACT-engine parity diff. The real FFXIV_ACT_Plugin (the sole parser) has already
// turned every Network_*.log into a MasterSwing stream (<name>.oracle.tsv, via the satellite's
// --mass-oracle). Those SAME swings are then aggregated two ways:
//   * real ACT engine  (tools/act-oracle)              -> <name>.oracle.exports.tsv   [baseline]
//   * our Fct.Compat.Act engine (--mass-engine-exports) -> <name>.engine.exports.tsv  [under test]
// This tool joins the two ExportVariables payloads per file, per combatant, per key, and reports how
// often our engine's string matches real ACT exactly. It is the corpus version of
// Fct.Compat.Act.Tests/ExportVarsCompatTests — the actual overlay drop-in contract, end to end.
//
//   MassCompare <folder>
//
// Completeness mode: diff our engine's FULL enumerated ExportVariables key set
// (<name>.engine.full.exports.tsv, --mass-engine-exports-full) against the plugin-in-the-loop oracle
// baseline (<name>.plugin.exports.tsv, tools/act-oracle --plugin-baseline-folder) — the corpus-scale
// sibling of Fct.Engine.Tests/OracleParityTests.ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5
// / Fct.Integration.Tests/OverlaySatelliteTests. Unlike the percentage-style diff above, completeness
// is the point here: any key/value divergence not explained by the documented fixture-provenance
// exclusions is reported by name, never folded into an aggregate percentage.
//
//   MassCompare --plugin <folder>

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: MassCompare <folder> | MassCompare --plugin <folder>");
    return 2;
}
if (args[0] == "--plugin")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: MassCompare --plugin <folder>");
        return 2;
    }
    return PluginExportsDiff(args[1]);
}
return ExportsDiff(args[0]);

// Compare our-engine vs real-ACT ExportVariables per file, per combatant, per key.
static int ExportsDiff(string folder)
{
    var baselineFiles = Directory.GetFiles(folder, "*.oracle.exports.tsv")
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
    // per key: [comparedPairs, exactMatches, numericPairs]; numeric sums kept separately.
    var keyStats = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
    var keyNum = new Dictionary<string, double[]>();          // key -> [baselineSum, oursSum]
    var keySamples = new Dictionary<string, List<string>>();
    long[] KS(string k) => keyStats.TryGetValue(k, out var a) ? a : (keyStats[k] = new long[3]);
    double[] KN(string k) => keyNum.TryGetValue(k, out var a) ? a : (keyNum[k] = new double[2]);
    void Sample(string k, string s)
    {
        var l = keySamples.TryGetValue(k, out var x) ? x : (keySamples[k] = new List<string>());
        if (l.Count < 12) l.Add(s);
    }

    int filesCompared = 0, combatBoth = 0, combatOnlyBaseline = 0, combatOnlyOurs = 0;
    long totalPairs = 0, totalExact = 0;

    foreach (var bf in baselineFiles)
    {
        string baseName = Path.GetFileName(bf);
        baseName = baseName.Substring(0, baseName.Length - ".oracle.exports.tsv".Length);
        string of = Path.Combine(folder, baseName + ".engine.exports.tsv");
        if (!File.Exists(of)) continue;
        filesCompared++;

        var b = ReadExports(bf);   // real ACT
        var o = ReadExports(of);   // our engine

        foreach (var name in b.Keys.Union(o.Keys))
        {
            bool inB = b.TryGetValue(name, out var bk), inO = o.TryGetValue(name, out var ok);
            if (inB && inO) combatBoth++;
            else if (inB) { combatOnlyBaseline++; continue; }  // real ACT shows a row we never emit
            else { combatOnlyOurs++; continue; }               // we emit a row real ACT never had

            foreach (var key in bk!.Keys.Union(ok!.Keys))
            {
                bk.TryGetValue(key, out var bv);
                ok.TryGetValue(key, out var ov);
                var a = KS(key); a[0]++; totalPairs++;
                bool exact = bv != null && ov != null && bv == ov;
                if (exact) { a[1]++; totalExact++; }
                else Sample(key, $"{baseName}/{name}: act={bv ?? "<none>"} ours={ov ?? "<none>"}");

                if (bv != null && ov != null && TryNum(bv, out var bd) && TryNum(ov, out var od2))
                {
                    a[2]++; var n = KN(key); n[0] += bd; n[1] += od2;
                }
            }
        }
    }

    var sb = new StringBuilder();
    void S(string s) { sb.AppendLine(s); Console.WriteLine(s); }
    S("");
    S("======== ACT-ENGINE PARITY (ExportVariables) — OURS vs REAL ACT ========");
    S("identical plugin swings into both engines; per file, per combatant, per key");
    S($"files compared: {filesCompared}");
    S($"combatant rows: matched={combatBoth}  act-only={combatOnlyBaseline}  ours-only={combatOnlyOurs}");
    double tp = totalPairs == 0 ? 100 : 100.0 * totalExact / totalPairs;
    S($"key/value pairs: {totalPairs:N0}  exact string match: {totalExact:N0}  ({tp:0.000}%)");
    S("");
    S("per-key  [pairs  exact  exact%   numericΣact  numericΣours  ratio%]");
    foreach (var kv in keyStats.OrderBy(k => (double)k.Value[1] / Math.Max(1, k.Value[0])).ThenBy(k => k.Key, StringComparer.Ordinal))
    {
        var a = kv.Value;
        double ex = a[0] == 0 ? 100 : 100.0 * a[1] / a[0];
        string numCol = "";
        if (keyNum.TryGetValue(kv.Key, out var n) && a[2] > 0)
        {
            double ratio = n[0] == 0 ? (n[1] == 0 ? 100 : double.PositiveInfinity) : 100.0 * n[1] / n[0];
            numCol = $"   Σact={n[0],16:N0}  Σours={n[1],16:N0}  {ratio,8:0.000}%";
        }
        S($"  {kv.Key,-16} {a[0],7:N0} {a[1],7:N0} {ex,8:0.00}%{numCol}");
    }
    S("=======================================================================");
    File.WriteAllText(Path.Combine(folder, "exports-summary.txt"), sb.ToString());

    var db = new StringBuilder();
    foreach (var kv in keySamples.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        db.AppendLine($"== {kv.Key} ==");
        foreach (var s in kv.Value) db.AppendLine("  " + s);
    }
    File.WriteAllText(Path.Combine(folder, "exports-diff.txt"), db.ToString());
    Console.WriteLine($"\nWrote {Path.Combine(folder, "exports-summary.txt")} and exports-diff.txt");
    return totalExact == totalPairs ? 0 : 1;
}

// Corpus-scale completeness diff. The plugin-in-the-loop oracle
// (<name>.plugin.exports.tsv — the FULL enumerated CombatantData/EncounterData.ExportVariables key
// set the real FFXIV_ACT_Plugin registers, tools/act-oracle --plugin-baseline-folder) is the
// completeness AUTHORITY: every key/value it carries must be reachable, and equal, through our
// engine's identically-enumerated payload (<name>.engine.full.exports.tsv,
// --mass-engine-exports-full). Mirrors Fct.Engine.Tests/OracleParityTests.
// ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5's own semantics: iterate the
// oracle's keys (never a hardcoded list, never the reverse direction — an extra key we register that
// the plugin doesn't is not a completeness gap), and report every divergence by name — no aggregate
// percentage stands in for a real port-bug report here.
static int PluginExportsDiff(string folder)
{
    var baselineFiles = Directory.GetFiles(folder, "*.plugin.exports.tsv")
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
    if (baselineFiles.Length == 0)
    {
        Console.Error.WriteLine($"no *.plugin.exports.tsv files in {folder} — run ActOracle --plugin-baseline-folder first");
        return 2;
    }

    // per key: [comparedPairs, exactMatches].
    var keyStats = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
    long[] KS(string k) => keyStats.TryGetValue(k, out var a) ? a : (keyStats[k] = new long[2]);

    var divergences = new List<string>();   // every real divergence, never capped — this is the gate.
    int filesCompared = 0, filesMissingOurs = 0, combatantMissingInOurs = 0, excludedByProvenance = 0;
    long totalPairs = 0, totalExact = 0;

    foreach (var bf in baselineFiles)
    {
        string baseName = Path.GetFileName(bf);
        baseName = baseName.Substring(0, baseName.Length - ".plugin.exports.tsv".Length);
        string of = Path.Combine(folder, baseName + ".engine.full.exports.tsv");
        if (!File.Exists(of)) { filesMissingOurs++; continue; }
        filesCompared++;

        var baseline = ReadExports(bf);   // plugin-in-the-loop oracle (the authority)
        var ours = ReadExports(of);       // our engine, full enumeration

        foreach (var (name, keys) in baseline)
        {
            if (!ours.TryGetValue(name, out var ourKeys))
            {
                combatantMissingInOurs++;
                foreach (var key in keys.Keys)
                    divergences.Add($"{baseName}/{name}.{key}: combatant/encounter row missing from our engine's payload");
                continue;
            }

            foreach (var (key, want) in keys)
            {
                // *ENCOUNTER*.CurrentZoneName is zone-frame provenance, not a swing-stream fact — the
                // identical exclusion Fct.Engine.Tests/OracleParityTests and OverlaySatelliteTests make
                // (both replay swings only / a corpus-independent zone frame, so the VALUE differs by
                // construction). Registration is still required; only the value compare is skipped.
                bool excludeValue = name == "*ENCOUNTER*" && key == "CurrentZoneName";

                var a = KS(key); a[0]++; totalPairs++;
                if (!ourKeys.TryGetValue(key, out var got))
                {
                    divergences.Add($"{baseName}/{name}.{key}: key not registered in our engine (oracle='{want}')");
                    continue;
                }
                if (excludeValue) { excludedByProvenance++; a[1]++; totalExact++; continue; }
                if (got == want) { a[1]++; totalExact++; }
                else divergences.Add($"{baseName}/{name}.{key}: ours='{got}' oracle='{want}'");
            }
        }
    }

    var sb = new StringBuilder();
    void S(string s) { sb.AppendLine(s); Console.WriteLine(s); }
    S("");
    S("======== PLUGIN-IN-THE-LOOP COMPLETENESS DIFF (P5.9) — OUR ENGINE vs REAL ACT + REAL PLUGIN ========");
    S("full enumerated ExportVariables key set; the plugin-in-the-loop oracle is the completeness authority");
    S($"files compared: {filesCompared}  (missing our-engine payload: {filesMissingOurs})");
    S($"combatant/encounter rows missing from our engine: {combatantMissingInOurs}");
    S($"key/value pairs checked: {totalPairs:N0}  divergences: {divergences.Count:N0}  (excluded by documented provenance: {excludedByProvenance:N0})");
    S("");
    S("per-key  [pairs  exact  exact%]");
    foreach (var kv in keyStats.OrderBy(k => (double)k.Value[1] / Math.Max(1, k.Value[0])).ThenBy(k => k.Key, StringComparer.Ordinal))
    {
        var a = kv.Value;
        double ex = a[0] == 0 ? 100 : 100.0 * a[1] / a[0];
        S($"  {kv.Key,-20} {a[0],9:N0} {a[1],9:N0} {ex,8:0.000}%");
    }
    S("=======================================================================================");
    File.WriteAllText(Path.Combine(folder, "plugin-exports-summary.txt"), sb.ToString());

    // Every divergence, never capped/sampled — a completeness gate must show the whole gap, not a
    // representative slice of it.
    File.WriteAllLines(Path.Combine(folder, "plugin-exports-diff.txt"), divergences);
    Console.WriteLine($"\nWrote {Path.Combine(folder, "plugin-exports-summary.txt")} and plugin-exports-diff.txt ({divergences.Count} divergence(s))");
    return divergences.Count == 0 ? 0 : 1;
}

static Dictionary<string, Dictionary<string, string>> ReadExports(string path)
{
    var m = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length < 3) continue;
        var d = m.TryGetValue(c[0], out var x) ? x : (m[c[0]] = new Dictionary<string, string>(StringComparer.Ordinal));
        d[c[1]] = c[2];
    }
    return m;
}

// Parse a leading numeric value (handles trailing %, thousands commas, and k/m/b suffixes ACT's
// CreateDamageString emits). Returns false for non-numeric strings like "Skill-12345".
static bool TryNum(string s, out double v)
{
    v = 0;
    if (string.IsNullOrEmpty(s)) return false;
    s = s.Trim();
    if (s.Length == 0) return false;
    if (s.EndsWith("%")) s = s[..^1];
    if (s.Length == 0) return false;
    double mult = 1;
    char last = char.ToLowerInvariant(s[^1]);
    if (last == 'k') { mult = 1e3; s = s[..^1]; }
    else if (last == 'm') { mult = 1e6; s = s[..^1]; }
    else if (last == 'b') { mult = 1e9; s = s[..^1]; }
    s = s.Replace(",", "");
    // NumberStyles.Any parses "NaN"/"Infinity" (literal strings ACT emits for 0/0 DPS on a
    // zero-duration combatant) as non-finite doubles — exclude them so they don't poison the sums.
    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && double.IsFinite(v))
    { v *= mult; return true; }
    v = 0;
    return false;
}
