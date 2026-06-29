using System.Globalization;
using System.Text;
using Fct.Parser.Native;

// Mass parse-compat run: diff our clean-room native parser against ACT's authoritative parse
// (captured by the satellite's --mass-oracle) over a whole folder of decoded Network_*.log files.
//
//   MassCompare <logFolder> <oracleFolder> <outFolder>
//
// For each Network_*.log (chronological, one parser instance so name/combat state carries across
// day-boundary rotations exactly as the oracle saw it) we write <name>.ours.tsv and bag-diff
// EVERY swing against <name>.oracle.tsv using the full MasterSwing tuple
// (swingType, crit, amount, special, attackType, attacker, damageType, victim). The headline is
// total swings reproduced; a per-swingType table shows exactly which categories still diverge.

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: MassCompare <logFolder> <oracleFolder> <outFolder>");
    return 2;
}

string logFolder = args[0], oracleFolder = args[1], outFolder = args[2];
Directory.CreateDirectory(outFolder);

// actions.full.tsv (id, name, category) supersedes skills.full.tsv; fall back if absent.
var (skills, categories) = LoadActions(Path.Combine(oracleFolder, "actions.full.tsv"));
if (skills.Count == 0) skills = LoadSkills(Path.Combine(oracleFolder, "skills.full.tsv"));
var statuses = LoadStatuses(Path.Combine(oracleFolder, "statuses.full.tsv"));
var statusDefs = CombatLogParser.LoadStatusDefs(Path.Combine(oracleFolder, "status-defs.tsv"));
var actionPotency = CombatLogParser.LoadActionPotency(Path.Combine(oracleFolder, "action-potency.tsv"));
Console.WriteLine($"skills: {skills.Count}  categories: {categories.Count}  statuses: {statuses.Count}  " +
    $"statusDefs: {statusDefs.Count}  actionPotency: {actionPotency.Count}");

// One parser instance: combatant-name and combat state persist across files, matching the
// oracle's single continuous plugin session.
var parser = new CombatLogParser
{
    Skills = skills.Count > 0 ? skills : null,
    ActionCategories = categories.Count > 0 ? categories : null,
    Statuses = statuses.Count > 0 ? statuses : null,
    StatusDefs = statusDefs.Count > 0 ? statusDefs : null,
    ActionPotency = actionPotency.Count > 0 ? actionPotency : null,
};

var files = Directory.GetFiles(logFolder, "Network_*.log")
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
Console.WriteLine($"files: {files.Length}");

var report = new StringBuilder();
report.AppendLine(string.Join('\t', "file", "oracle", "ours", "missing", "extra"));
var details = new StringBuilder();

long tOracle = 0, tOurs = 0, tMissing = 0, tExtra = 0;
long tSimOracle = 0, tSimMiss = 0, tSimExtra = 0;
long tSimAmtOracle = 0, tSimAmtOurs = 0;
int filesPerfect = 0, filesWithOracle = 0;

// Per-swingType accounting: oracle count, ours count, and the missing/extra attributable to it.
var byType = new SortedDictionary<int, long[]>(); // [oracle, ours, missing, extra]
long[] Slot(int t) => byType.TryGetValue(t, out var a) ? a : (byType[t] = new long[4]);
var sampleByType = new Dictionary<int, List<string>>();

foreach (var file in files)
{
    string name = Path.GetFileNameWithoutExtension(file);
    var ours = parser.Process(File.ReadLines(file)).ToList();
    WriteOurs(Path.Combine(outFolder, name + ".ours.tsv"), ours);

    string oraclePath = Path.Combine(oracleFolder, name + ".oracle.tsv");
    if (!File.Exists(oraclePath))
    {
        report.AppendLine($"{name}\t(no oracle)\t{ours.Count}");
        Console.WriteLine($"{name}: no oracle tsv, skipped diff");
        continue;
    }
    filesWithOracle++;

    var oracle = ReadOracle(oraclePath);
    var oursRows = ours.Select(ToRow);

    foreach (var r in oracle) Slot(r.SwingType)[0]++;
    foreach (var r in oursRows) Slot(r.SwingType)[1]++;

    var (missing, extra) = BagDiff(oursRows, oracle);
    foreach (var r in missing) { Slot(r.SwingType)[2]++; Sample(sampleByType, r, "MISS"); }
    foreach (var r in extra) { Slot(r.SwingType)[3]++; Sample(sampleByType, r, "EXTRA"); }

    // Value-parity of the simulated "(*)" ticks: ACT's crit bit/±1 amount are RNG, so compare on
    // (swingType, attacker, victim, attackType, amount) with crit normalized out.
    static Row Sim(Row r) => r with { Crit = false, Special = "", DamageType = "" };
    var oracleSim = oracle.Where(r => r.AttackType.EndsWith(" (*)")).Select(Sim);
    var oursSim = ours.Select(ToRow).Where(r => r.AttackType.EndsWith(" (*)")).Select(Sim);
    var (simMiss, simExtra) = BagDiff(oursSim, oracleSim);
    int simO = oracle.Count(r => r.AttackType.EndsWith(" (*)"));
    tSimOracle += simO; tSimMiss += simMiss.Count; tSimExtra += simExtra.Count;
    // Aggregate simulated damage (the DPS-relevant signal): sum of "(*)" amounts.
    tSimAmtOracle += oracle.Where(r => r.AttackType.EndsWith(" (*)")).Sum(r => r.Amount);
    tSimAmtOurs += ours.Select(ToRow).Where(r => r.AttackType.EndsWith(" (*)")).Sum(r => r.Amount);

    int oc = oracle.Count, uc = ours.Count;
    report.AppendLine(string.Join('\t', name, oc, uc, missing.Count, extra.Count));
    tOracle += oc; tOurs += uc; tMissing += missing.Count; tExtra += extra.Count;
    if (missing.Count == 0 && extra.Count == 0) filesPerfect++;

    if (missing.Count > 0 || extra.Count > 0)
    {
        details.AppendLine($"== {name} ==  missing={missing.Count} extra={extra.Count}");
        SampleFile(details, "MISSING (in ACT, not ours)", missing);
        SampleFile(details, "EXTRA (ours, not ACT)", extra);
    }

    Console.WriteLine($"{name}: oracle={oc} ours={uc} miss={missing.Count} extra={extra.Count}");
}

File.WriteAllText(Path.Combine(outFolder, "report.tsv"), report.ToString());
File.WriteAllText(Path.Combine(outFolder, "details.txt"), details.ToString());

var summary = new StringBuilder();
void S(string s) { summary.AppendLine(s); Console.WriteLine(s); }
S("");
S("==================== MASS PARSE COMPAT SUMMARY ====================");
S($"files compared (with oracle): {filesWithOracle}/{files.Length}");
S($"files BIT-PERFECT (all swing types): {filesPerfect}/{filesWithOracle}");
S($"total swings   ACT={tOracle:N0}  ours={tOurs:N0}  missing={tMissing:N0}  extra={tExtra:N0}");
double pct = tOracle == 0 ? 100 : 100.0 * (tOracle - tMissing) / tOracle;
S($"TOTAL reproduced: {pct:0.0000}% of ACT's swings  (extras: {tExtra:N0})");
double simPct = tSimOracle == 0 ? 0 : 100.0 * (tSimOracle - tSimMiss) / tSimOracle;
S($"simulated (*) value-parity (crit-excluded exact amount): {tSimOracle - tSimMiss:N0}/{tSimOracle:N0} = {simPct:0.00}%  (extras: {tSimExtra:N0})");
double simAmtPct = tSimAmtOracle == 0 ? 0 : 100.0 * tSimAmtOurs / tSimAmtOracle;
S($"simulated (*) damage SUM (DPS signal): ours={tSimAmtOurs:N0} / ACT={tSimAmtOracle:N0} = {simAmtPct:0.00}%");
S("");
S("per-swingType  [oracle  ours  missing  extra  reproduced%]");
foreach (var kv in byType)
{
    var a = kv.Value;
    double p = a[0] == 0 ? 100 : 100.0 * (a[0] - a[2]) / a[0];
    S($"  type {kv.Key,2} [{SwingName(kv.Key),12}]  o={a[0],10:N0}  u={a[1],10:N0}  miss={a[2],8:N0}  extra={a[3],8:N0}  {p,8:0.000}%");
}
S("==================================================================");
File.WriteAllText(Path.Combine(outFolder, "summary.txt"), summary.ToString());

// per-type sample dump for diagnosis
var stb = new StringBuilder();
foreach (var kv in sampleByType.OrderBy(k => k.Key))
{
    stb.AppendLine($"== type {kv.Key} ({SwingName(kv.Key)}) ==");
    foreach (var s in kv.Value.Take(40)) stb.AppendLine("  " + s);
}
File.WriteAllText(Path.Combine(outFolder, "type-samples.txt"), stb.ToString());

return (tMissing == 0 && tExtra == 0) ? 0 : 1;

// ---- helpers ----

static Row ToRow(CombatAction a) => new(a.SwingType, a.IsCritical, a.Amount, a.Special ?? "",
    a.AttackType ?? "", a.Attacker ?? "", a.DamageType ?? "", a.Victim ?? "");

static Dictionary<uint, string> LoadSkills(string path)
{
    var map = new Dictionary<uint, string>();
    if (!File.Exists(path)) return map;
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length >= 2 && uint.TryParse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            map[id] = c[1];
    }
    return map;
}

static (Dictionary<uint, string> names, Dictionary<uint, string> categories) LoadActions(string path)
{
    var names = new Dictionary<uint, string>();
    var cats = new Dictionary<uint, string>();
    if (!File.Exists(path)) return (names, cats);
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length >= 3 && uint.TryParse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            names[id] = c[1];
            if (c[2].Length > 0) cats[id] = c[2];
        }
    }
    return (names, cats);
}

static Dictionary<uint, string> LoadStatuses(string path)
{
    var map = new Dictionary<uint, string>();
    if (!File.Exists(path)) return map;
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length >= 2 && uint.TryParse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            map[id] = c[1];
    }
    return map;
}

static List<Row> ReadOracle(string path)
{
    var rows = new List<Row>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length < 8) continue;
        rows.Add(new Row(int.Parse(c[0], CultureInfo.InvariantCulture), c[1] == "1",
            long.Parse(c[2], CultureInfo.InvariantCulture), c[3], c[4], c[5], c[6], c[7]));
    }
    return rows;
}

static void WriteOurs(string path, List<CombatAction> actions)
{
    using var w = new StreamWriter(path);
    w.WriteLine("swingType\tcrit\tamount\tspecial\tattackType\tattacker\tdamageType\tvictim");
    foreach (var a in actions)
        w.WriteLine(string.Join('\t', a.SwingType, a.IsCritical ? "1" : "0", a.Amount,
            a.Special, a.AttackType, a.Attacker, a.DamageType, a.Victim));
}

static (List<Row> missing, List<Row> extra) BagDiff(IEnumerable<Row> ours, IEnumerable<Row> oracle)
{
    var a = new Dictionary<Row, int>();
    var b = new Dictionary<Row, int>();
    foreach (var x in ours) a[x] = a.GetValueOrDefault(x) + 1;
    foreach (var x in oracle) b[x] = b.GetValueOrDefault(x) + 1;
    var missing = new List<Row>();
    var extra = new List<Row>();
    foreach (var kv in b) for (int i = 0; i < kv.Value - a.GetValueOrDefault(kv.Key); i++) missing.Add(kv.Key);
    foreach (var kv in a) for (int i = 0; i < kv.Value - b.GetValueOrDefault(kv.Key); i++) extra.Add(kv.Key);
    return (missing, extra);
}

static void Sample(Dictionary<int, List<string>> store, Row r, string tag)
{
    var list = store.TryGetValue(r.SwingType, out var l) ? l : (store[r.SwingType] = new());
    if (list.Count < 40) list.Add($"[{tag}] {r}");
}

static void SampleFile(StringBuilder sb, string label, List<Row> items)
{
    if (items.Count == 0) return;
    sb.AppendLine($"  {label}: {items.Count}");
    foreach (var s in items.Take(12)) sb.AppendLine("    " + s);
}

static string SwingName(int t) => t switch
{
    0 => "auto", 1 => "action", 2 => "ability", 3 => "DoT", 4 => "heal", 5 => "HoT",
    6 => "powerDrain", 7 => "powerHeal", 8 => "status", 9 => "dispel", 10 => "threat",
    11 => "shield", _ => "?",
};

readonly record struct Row(int SwingType, bool Crit, long Amount,
    string Special, string AttackType, string Attacker, string DamageType, string Victim)
{
    public override string ToString() =>
        $"t{SwingType} {(Crit ? "C" : " ")} {Amount,8} {Special,-10} {AttackType,-22} {Attacker,-18} {DamageType,-12} -> {Victim}";
}
