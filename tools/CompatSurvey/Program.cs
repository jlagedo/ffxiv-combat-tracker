using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// CompatSurvey — deterministic demand-surface analyzer.
// Reads compiled IL of the ACT-ecosystem binaries and emits every member each
// plugin binds INTO an ACT-ecosystem provider (ACT host, FFXIV_ACT_Plugin, or
// another plugin). No plugin is executed. Third-party targets are filtered out.

var root = args.Length > 0 ? args[0] : @"E:\tmp\plugins";
var outDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);

// ---- IL operand-size table, built from the runtime's own opcode metadata ----
var opByValue = new Dictionary<short, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    if (f.GetValue(null) is OpCode oc) opByValue[oc.Value] = oc;
}

// ---- Ecosystem classification -------------------------------------------------
static string Categorize(string asmName)
{
    string n = asmName;
    if (n.Equals("Advanced Combat Tracker", StringComparison.OrdinalIgnoreCase)) return "ACT";
    if (n.StartsWith("FFXIV_ACT_Plugin", StringComparison.OrdinalIgnoreCase)) return "Parser";
    if (n.StartsWith("OverlayPlugin", StringComparison.OrdinalIgnoreCase)) return "OverlayPlugin";
    if (n.StartsWith("Cactbot", StringComparison.OrdinalIgnoreCase)) return "cactbot";
    if (n.Equals("Triggernometry", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("TriggernometryProxy", StringComparison.OrdinalIgnoreCase)) return "Triggernometry";
    if (n.StartsWith("ACT.Hojoring", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("ACT.SpecialSpellTimer", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("ACT.TTSYukkuri", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("ACT.UltraScouter", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("ACT.XIVLog", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("FFXIV.Framework", StringComparison.OrdinalIgnoreCase)) return "Hojoring";
    if (n.StartsWith("ACT_DiscordTriggers", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("Fct.Plugins.DiscordTriggers", StringComparison.OrdinalIgnoreCase)) return "DiscordTriggers";
    return "ThirdParty";
}
static bool IsEcosystem(string asmName) => Categorize(asmName) != "ThirdParty";

// Reflection API surface we treat as a dynamic escape hatch.
var reflTypes = new HashSet<string>(StringComparer.Ordinal)
{
    "System.Type", "System.Activator", "System.AppDomain", "System.Delegate",
    "System.Reflection.Assembly", "System.Reflection.MethodBase", "System.Reflection.MethodInfo",
    "System.Reflection.FieldInfo", "System.Reflection.PropertyInfo", "System.Reflection.MemberInfo",
    "System.Reflection.ConstructorInfo", "System.RuntimeReflectionExtensions",
};
var reflMethods = new HashSet<string>(StringComparer.Ordinal)
{
    "GetType", "GetMethod", "GetMethods", "GetField", "GetFields", "GetProperty", "GetProperties",
    "GetMember", "GetMembers", "InvokeMember", "Invoke", "CreateInstance", "CreateDelegate",
    "Load", "LoadFrom", "LoadFile", "GetTypes", "GetExportedTypes",
};
// Well-known ACT-surface names reached by string (seed vocabulary).
var seedVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "PlayTtsMethod", "PlaySoundMethod", "oFormActMain", "oFormSpellTimers", "ActGlobals",
    "ExportVariables", "AddCombatAction", "SetEncounter", "EndEncounter", "ChangeZone",
    "GetCombatants", "ActiveZone", "ActiveEncounter", "LogParser", "PlayTts", "PlaySound",
};

// ---- Records ------------------------------------------------------------------
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    .Where(p => !p.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
    .Where(p => !p.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

var nodes = new List<Node>();
var staticEdges = new Dictionary<string, StaticEdge>();      // key -> edge (with count)
var reflSites = new Dictionary<string, DynEdge>();           // reflection call sites
var strLits = new List<(string consumer, string method, string literal)>();
var vocab = new HashSet<string>(seedVocab, StringComparer.OrdinalIgnoreCase);
// Names too generic to be useful dynamic-string signals (would match everything).
var vocabStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "tostring", "equals", "gethashcode", "dispose", "invoke", "value", "values", "count",
    "index", "length", "items", "clone", "reset", "clear", "close", "enabled", "visible",
    "parent", "owner", "format", "message", "result", "state", "status", "handle", "buffer",
    "target", "source", "action", "object", "string", "system", "default", "current", "empty",
};

// ---- Phase 0: process on-disk binaries, then the SDK assemblies the parser embeds ----
int embeddedCount = 0;
var embedded = new List<(string rel, byte[] bytes)>();

foreach (var path in files)
{
    byte[] bytes = File.ReadAllBytes(path);
    string rel = Path.GetRelativePath(root, path);
    string cat = Process(rel, bytes, "disk");
    // The parser ships its SDK (FFXIV_ACT_Plugin.Common/.Network/...) as embedded
    // resources — one file on disk, many assemblies in memory. Pull them out.
    if (cat == "Parser")
        foreach (var (name, data) in ExtractEmbeddedEcosystem(bytes))
            embedded.Add(($"{rel}!{name}", data));
}

foreach (var (rel, bytes) in embedded
             .GroupBy(e => e.rel).Select(g => g.First())
             .OrderBy(e => e.rel, StringComparer.Ordinal))
{
    Process(rel, bytes, "embedded");
    embeddedCount++;
}

// Node registration + provider-surface harvest + demand edges + IL scan for one input.
string Process(string rel, byte[] bytes, string origin)
{
    string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    PEReader pe;
    MetadataReader mr;
    try
    {
        pe = new PEReader(new MemoryStream(bytes));
        if (!pe.HasMetadata) { nodes.Add(new Node(rel, sha, "(native)", "", "", "ThirdParty", bytes.Length, origin)); return "ThirdParty"; }
        mr = pe.GetMetadataReader();
    }
    catch { nodes.Add(new Node(rel, sha, "(unreadable)", "", "", "ThirdParty", bytes.Length, origin)); return "ThirdParty"; }

    var asmDef = mr.GetAssemblyDefinition();
    string asmName = mr.GetString(asmDef.Name);
    string ver = asmDef.Version.ToString();
    string pkt = PublicKeyToken(mr, asmDef);
    string cat = Categorize(asmName);
    nodes.Add(new Node(rel, sha, asmName, ver, pkt, cat, bytes.Length, origin));

    if (cat == "ThirdParty") return cat; // only ecosystem assemblies are analyzed

    // Provider-surface harvest: the host + parser SDK are the surfaces plugins reflect
    // into by name and the surfaces this project must facade, so seed the dynamic-string
    // vocabulary from their declared type/member names. Harvesting every ecosystem
    // assembly would drown the string ledger in generic-name noise.
    if (cat is "ACT" or "Parser") HarvestSurface(mr, vocab, vocabStop);

    var sig = new SigTypeProvider();

    // ---- TypeRef edges (type-level demand) ----
    foreach (var h in mr.TypeReferences)
    {
        var tr = mr.GetTypeReference(h);
        string tAsm = ResolveScopeAssembly(mr, tr.ResolutionScope);
        if (tAsm.Length == 0 || !IsEcosystem(tAsm)) continue;
        string full = Full(mr.GetString(tr.Namespace), mr.GetString(tr.Name));
        Bump(staticEdges, new StaticEdge(asmName, cat, "Type", tAsm, Categorize(tAsm), full, ""));
        vocab.Add(mr.GetString(tr.Name));
    }

    // ---- MemberRef edges (member-level demand) ----
    foreach (var h in mr.MemberReferences)
    {
        var m = mr.GetMemberReference(h);
        string member = mr.GetString(m.Name);
        (string tAsm, string tFull) = ResolveParent(mr, m.Parent, sig);
        if (tAsm.Length == 0 || !IsEcosystem(tAsm)) continue;
        string kind = m.GetKind() == MemberReferenceKind.Field ? "Field" : "Method";
        Bump(staticEdges, new StaticEdge(asmName, cat, kind, tAsm, Categorize(tAsm), tFull, member));
        vocab.Add(member);
    }

    // ---- IL scan: reflection call sites + string literals ----
    foreach (var mh in mr.MethodDefinitions)
    {
        var md = mr.GetMethodDefinition(mh);
        if (md.RelativeVirtualAddress == 0) continue;
        MethodBodyBlock body;
        try { body = pe.GetMethodBody(md.RelativeVirtualAddress); }
        catch { continue; }
        string owner = OwnerName(mr, md);
        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            int b0 = il.ReadByte();
            short val = (short)(b0 == 0xFE ? (0xFE00 | il.ReadByte()) : b0);
            if (!opByValue.TryGetValue(val, out var oc)) break; // unknown -> stop this body safely
            switch (oc.OperandType)
            {
                case OperandType.InlineString:
                {
                    int tok = il.ReadInt32();
                    try
                    {
                        var us = MetadataTokens.UserStringHandle(tok & 0x00FFFFFF);
                        string s = mr.GetUserString(us);
                        if (s.Length is > 0 and < 128) strLits.Add((asmName, owner, s));
                    }
                    catch { }
                    break;
                }
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineField:
                {
                    int tok = il.ReadInt32();
                    if (oc == OpCodes.Call || oc == OpCodes.Callvirt || oc == OpCodes.Newobj)
                        TryReflection(mr, tok, asmName, owner, reflTypes, reflMethods, reflSites);
                    break;
                }
                case OperandType.InlineSwitch:
                {
                    int n = il.ReadInt32();
                    il.Offset += 4 * n;
                    break;
                }
                default:
                    il.Offset += OperandSize(oc.OperandType);
                    break;
            }
        }
    }
    return cat;
}

// ---- Filter string literals against the global ecosystem vocabulary ----
var dynStrings = strLits
    .Where(x => vocab.Contains(x.literal))
    .GroupBy(x => (x.consumer, x.method, x.literal))
    .Select(g => (g.Key.consumer, g.Key.method, g.Key.literal, count: g.Count()))
    .OrderBy(x => x.consumer, StringComparer.Ordinal).ThenBy(x => x.method, StringComparer.Ordinal).ThenBy(x => x.literal, StringComparer.Ordinal)
    .ToList();

// ---- Emit ----
WriteNodes(Path.Combine(outDir, "00-nodes.json"), nodes);
WriteStatic(Path.Combine(outDir, "10-edges-static.csv"), staticEdges.Values);
WriteRefl(Path.Combine(outDir, "20-dynamic-reflection.csv"), reflSites.Values);
WriteStrings(Path.Combine(outDir, "21-dynamic-strings.csv"), dynStrings);
WriteLedger(Path.Combine(outDir, "30-ledger.csv"), reflSites.Values, dynStrings);
WriteSummary(Path.Combine(outDir, "COMPAT-SURFACE.md"), nodes, staticEdges.Values, reflSites.Values, dynStrings, root, embeddedCount, vocab.Count);

Console.WriteLine($"Nodes: {nodes.Count}  (ecosystem: {nodes.Count(n => n.Category != "ThirdParty")}, embedded SDK extracted: {embeddedCount})");
Console.WriteLine($"Vocabulary (harvested provider surface): {vocab.Count} names");
Console.WriteLine($"Static ecosystem edges: {staticEdges.Count}");
Console.WriteLine($"Dynamic reflection sites: {reflSites.Count}");
Console.WriteLine($"Dynamic string candidates: {dynStrings.Count}");
Console.WriteLine($"Output -> {outDir}");

// ============================ helpers ============================
static void Bump(Dictionary<string, StaticEdge> d, StaticEdge e)
{
    string k = $"{e.Consumer}|{e.Kind}|{e.ProviderAssembly}|{e.Type}|{e.Member}";
    if (d.TryGetValue(k, out var ex)) d[k] = ex with { Count = ex.Count + 1 };
    else d[k] = e with { Count = 1 };
}

static void AddVocab(HashSet<string> vocab, string name, HashSet<string> stop)
{
    if (name.Length < 5) return;                                  // too short to be a distinctive signal
    if (name[0] is '<' or '.' or '$' or '_') return;             // compiler-generated / ctor / backing
    if (name.StartsWith("get_") || name.StartsWith("set_") ||
        name.StartsWith("add_") || name.StartsWith("remove_")) return;
    if (stop.Contains(name)) return;
    vocab.Add(name);
}

// Seed the vocabulary with an ecosystem provider's own declared type + member names.
static void HarvestSurface(MetadataReader mr, HashSet<string> vocab, HashSet<string> stop)
{
    foreach (var th in mr.TypeDefinitions)
    {
        var td = mr.GetTypeDefinition(th);
        AddVocab(vocab, mr.GetString(td.Name), stop);
        foreach (var mh in td.GetMethods()) AddVocab(vocab, mr.GetString(mr.GetMethodDefinition(mh).Name), stop);
        foreach (var fh in td.GetFields()) AddVocab(vocab, mr.GetString(mr.GetFieldDefinition(fh).Name), stop);
        foreach (var ph in td.GetProperties()) AddVocab(vocab, mr.GetString(mr.GetPropertyDefinition(ph).Name), stop);
    }
}

// Pull embedded assemblies that belong to the ACT ecosystem out of a carrier DLL.
static IEnumerable<(string name, byte[] bytes)> ExtractEmbeddedEcosystem(byte[] carrier)
{
    var results = new List<(string, byte[])>();
    PEReader pe;
    MetadataReader mr;
    try
    {
        pe = new PEReader(new MemoryStream(carrier));
        if (!pe.HasMetadata) return results;
        mr = pe.GetMetadataReader();
    }
    catch { return results; }

    var cor = pe.PEHeaders.CorHeader;
    if (cor is null || cor.ResourcesDirectory.Size == 0) return results;
    PEMemoryBlock resBlock;
    try { resBlock = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress); }
    catch { return results; }

    foreach (var rh in mr.ManifestResources)
    {
        var res = mr.GetManifestResource(rh);
        if (!res.Implementation.IsNil) continue;                 // linked elsewhere, not embedded here
        int off = (int)res.Offset;
        if (off < 0 || off + 4 > resBlock.Length) continue;
        var rdr = resBlock.GetReader(off, resBlock.Length - off);
        int size = rdr.ReadInt32();
        if (size <= 0 || size > rdr.RemainingBytes) continue;
        byte[] raw = rdr.ReadBytes(size);

        byte[]? asm = TryAsAssembly(raw);
        if (asm is null) continue;
        string? name = AssemblyIdentityIfEcosystem(asm);
        if (name is null) continue;
        results.Add(($"{name}.dll", asm));
    }
    return results;
}

// Recognize a resource blob as a managed assembly, decompressing if needed.
static byte[]? TryAsAssembly(byte[] raw)
{
    static bool IsPE(byte[] b) => b.Length > 1 && b[0] == 0x4D && b[1] == 0x5A; // 'MZ'
    if (IsPE(raw)) return raw;
    try   // gzip
    {
        if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            var b = outMs.ToArray();
            if (IsPE(b)) return b;
        }
    }
    catch { }
    try   // raw deflate (the scheme FFXIV_ACT_Plugin uses)
    {
        using var df = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        df.CopyTo(outMs);
        var b = outMs.ToArray();
        if (IsPE(b)) return b;
    }
    catch { }
    return null;
}

static string? AssemblyIdentityIfEcosystem(byte[] asm)
{
    try
    {
        using var pe = new PEReader(new MemoryStream(asm));
        if (!pe.HasMetadata) return null;
        var mr = pe.GetMetadataReader();
        string name = mr.GetString(mr.GetAssemblyDefinition().Name);
        return Categorize(name) != "ThirdParty" ? name : null;
    }
    catch { return null; }
}

static void TryReflection(MetadataReader mr, int tok, string consumer, string owner,
    HashSet<string> reflTypes, HashSet<string> reflMethods, Dictionary<string, DynEdge> sink)
{
    try
    {
        var h = MetadataTokens.EntityHandle(tok);
        MemberReference m;
        if (h.Kind == HandleKind.MemberReference) m = mr.GetMemberReference((MemberReferenceHandle)h);
        else if (h.Kind == HandleKind.MethodSpecification)
        {
            var ms = mr.GetMethodSpecification((MethodSpecificationHandle)h);
            if (ms.Method.Kind != HandleKind.MemberReference) return;
            m = mr.GetMemberReference((MemberReferenceHandle)ms.Method);
        }
        else return;

        string name = mr.GetString(m.Name);
        if (m.Parent.Kind != HandleKind.TypeReference) return;
        var tr = mr.GetTypeReference((TypeReferenceHandle)m.Parent);
        string tFull = Full(mr.GetString(tr.Namespace), mr.GetString(tr.Name));
        bool hit = reflMethods.Contains(name) && (reflTypes.Contains(tFull) || tFull.StartsWith("System.Reflection", StringComparison.Ordinal));
        if (!hit) return;
        string key = $"{consumer}|{owner}|{tFull}|{name}";
        if (sink.TryGetValue(key, out var ex)) sink[key] = ex with { Count = ex.Count + 1 };
        else sink[key] = new DynEdge(consumer, owner, tFull, name, 1);
    }
    catch { }
}

static string ResolveScopeAssembly(MetadataReader mr, EntityHandle scope)
{
    switch (scope.Kind)
    {
        case HandleKind.AssemblyReference:
            return mr.GetString(mr.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        case HandleKind.TypeReference:
            var tr = mr.GetTypeReference((TypeReferenceHandle)scope);
            return ResolveScopeAssembly(mr, tr.ResolutionScope);
        default:
            return ""; // ModuleDef/ModuleRef/Nil -> internal to this assembly
    }
}

static (string asm, string full) ResolveParent(MetadataReader mr, EntityHandle parent, SigTypeProvider sig)
{
    switch (parent.Kind)
    {
        case HandleKind.TypeReference:
        {
            var tr = mr.GetTypeReference((TypeReferenceHandle)parent);
            return (ResolveScopeAssembly(mr, tr.ResolutionScope), Full(mr.GetString(tr.Namespace), mr.GetString(tr.Name)));
        }
        case HandleKind.TypeSpecification:
        {
            try
            {
                var ts = mr.GetTypeSpecification((TypeSpecificationHandle)parent);
                var r = ts.DecodeSignature(sig, null);
                return (r.Assembly, r.FullName);
            }
            catch { return ("", ""); }
        }
        default:
            return ("", "");
    }
}

static string OwnerName(MetadataReader mr, MethodDefinition md)
{
    var td = mr.GetTypeDefinition(md.GetDeclaringType());
    return Full(mr.GetString(td.Namespace), mr.GetString(td.Name)) + "::" + mr.GetString(md.Name);
}

static string Full(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;

static string PublicKeyToken(MetadataReader mr, AssemblyDefinition asm)
{
    if (asm.PublicKey.IsNil) return "";
    var pk = mr.GetBlobBytes(asm.PublicKey);
    if (pk.Length == 0) return "";
    var hash = SHA1.HashData(pk);
    var tok = hash.AsSpan(hash.Length - 8, 8).ToArray();
    Array.Reverse(tok);
    return Convert.ToHexString(tok).ToLowerInvariant();
}

static int OperandSize(OperandType t) => t switch
{
    OperandType.InlineNone => 0,
    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineBrTarget or OperandType.InlineI or OperandType.ShortInlineR
        or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig
        or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    _ => 4,
};

// ---- writers (stable-sorted, deterministic) ----
static void WriteNodes(string path, List<Node> nodes)
{
    var ordered = nodes.OrderBy(n => n.Category, StringComparer.Ordinal).ThenBy(n => n.RelPath, StringComparer.Ordinal).ToList();
    var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}
static void WriteStatic(string path, IEnumerable<StaticEdge> edges)
{
    var sb = new StringBuilder("consumer,consumerCat,kind,providerAssembly,providerCat,type,member,count,intraSuite\n");
    foreach (var e in edges.OrderBy(e => e.Consumer, StringComparer.Ordinal).ThenBy(e => e.ProviderAssembly, StringComparer.Ordinal)
                 .ThenBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Member, StringComparer.Ordinal))
        sb.Append($"{Csv(e.Consumer)},{e.ConsumerCat},{e.Kind},{Csv(e.ProviderAssembly)},{e.ProviderCat},{Csv(e.Type)},{Csv(e.Member)},{e.Count},{(e.ConsumerCat == e.ProviderCat)}\n");
    File.WriteAllText(path, sb.ToString());
}
static void WriteRefl(string path, IEnumerable<DynEdge> sites)
{
    var sb = new StringBuilder("consumer,containingMethod,targetType,targetApi,count\n");
    foreach (var e in sites.OrderBy(e => e.Consumer, StringComparer.Ordinal).ThenBy(e => e.Owner, StringComparer.Ordinal).ThenBy(e => e.TargetType, StringComparer.Ordinal).ThenBy(e => e.Api, StringComparer.Ordinal))
        sb.Append($"{Csv(e.Consumer)},{Csv(e.Owner)},{Csv(e.TargetType)},{e.Api},{e.Count}\n");
    File.WriteAllText(path, sb.ToString());
}
static void WriteStrings(string path, List<(string consumer, string method, string literal, int count)> rows)
{
    var sb = new StringBuilder("consumer,containingMethod,literal,count\n");
    foreach (var r in rows) sb.Append($"{Csv(r.consumer)},{Csv(r.method)},{Csv(r.literal)},{r.count}\n");
    File.WriteAllText(path, sb.ToString());
}
static void WriteLedger(string path, IEnumerable<DynEdge> refl, List<(string consumer, string method, string literal, int count)> strs)
{
    var sb = new StringBuilder("id,class,consumer,site,detail,count,disposition\n");
    int i = 1;
    foreach (var e in refl.OrderBy(e => e.Consumer, StringComparer.Ordinal).ThenBy(e => e.Owner, StringComparer.Ordinal))
        sb.Append($"D{i++:0000},reflection-callsite,{Csv(e.Consumer)},{Csv(e.Owner)},{Csv(e.TargetType + "." + e.Api)},{e.Count},UNREVIEWED\n");
    foreach (var r in strs)
        sb.Append($"D{i++:0000},dynamic-string,{Csv(r.consumer)},{Csv(r.method)},{Csv(r.literal)},{r.count},UNREVIEWED\n");
    File.WriteAllText(path, sb.ToString());
}
static void WriteSummary(string path, List<Node> nodes, IEnumerable<StaticEdge> edges, IEnumerable<DynEdge> refl, List<(string consumer, string method, string literal, int count)> strs, string root, int embeddedCount, int vocabCount)
{
    var eco = nodes.Where(n => n.Category != "ThirdParty").ToList();
    var el = edges.ToList();
    var sb = new StringBuilder();
    sb.AppendLine("# ACT-ecosystem compatibility demand surface");
    sb.AppendLine();
    sb.AppendLine($"Source: `{root}`  ·  static IL analysis, no plugin executed.");
    sb.AppendLine();
    sb.AppendLine("## Ecosystem nodes (pinned by SHA-256)");
    sb.AppendLine();
    sb.AppendLine($"`disk` = a file in the source folder; `embedded` = an SDK assembly extracted from the parser DLL ({embeddedCount} extracted).");
    sb.AppendLine();
    sb.AppendLine("| Category | Assembly | Version | Origin | Rel path | SHA-256 (first 12) |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (var n in eco.OrderBy(n => n.Category, StringComparer.Ordinal).ThenBy(n => n.AssemblyName, StringComparer.Ordinal))
        sb.AppendLine($"| {n.Category} | {n.AssemblyName} | {n.Version} | {n.Origin} | `{n.RelPath}` | `{n.Sha[..12]}` |");
    sb.AppendLine();
    sb.AppendLine("## Demand edges by consumer → provider category");
    sb.AppendLine();
    sb.AppendLine("| Consumer | → Provider cat | Members |");
    sb.AppendLine("|---|---|---|");
    foreach (var g in el.Where(e => e.ConsumerCat != e.ProviderCat)
                 .GroupBy(e => (e.Consumer, e.ProviderCat))
                 .OrderBy(g => g.Key.Consumer, StringComparer.Ordinal).ThenBy(g => g.Key.ProviderCat, StringComparer.Ordinal))
        sb.AppendLine($"| {g.Key.Consumer} | {g.Key.ProviderCat} | {g.Count()} |");
    sb.AppendLine();
    sb.AppendLine("## Totals");
    sb.AppendLine();
    sb.AppendLine($"- Ecosystem assemblies analyzed: **{eco.Count}** (incl. **{embeddedCount}** SDK assemblies extracted from the parser)");
    sb.AppendLine($"- Provider-surface vocabulary harvested: **{vocabCount}** distinct names");
    sb.AppendLine($"- Static demand edges (cross-category): **{el.Count(e => e.ConsumerCat != e.ProviderCat)}**");
    sb.AppendLine($"- Static demand edges (incl. intra-suite): **{el.Count}**");
    sb.AppendLine($"- Dynamic reflection call sites (ledger): **{refl.Count()}**");
    sb.AppendLine($"- Dynamic string candidates (ledger): **{strs.Count}**");
    sb.AppendLine();
    sb.AppendLine("Full detail: `10-edges-static.csv`, `20-dynamic-reflection.csv`, `21-dynamic-strings.csv`, `30-ledger.csv`, `00-nodes.json`.");
    File.WriteAllText(path, sb.ToString());
}
static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

// ============================ types ============================
record Node(string RelPath, string Sha, string AssemblyName, string Version, string Pkt, string Category, long Size, string Origin);
record StaticEdge(string Consumer, string ConsumerCat, string Kind, string ProviderAssembly, string ProviderCat, string Type, string Member, int Count = 0);
record DynEdge(string Consumer, string Owner, string TargetType, string Api, int Count);

// Minimal signature type provider: resolves the primary type behind a TypeSpec
// (e.g. a generic instantiation of a provider type) to its (assembly, fullname).
sealed class SigTypeProvider : ISignatureTypeProvider<SigTypeProvider.R, object?>
{
    public readonly record struct R(string Assembly, string FullName);
    static readonly R Empty = new("", "");
    public R GetPrimitiveType(PrimitiveTypeCode c) => new("", "System." + c);
    public R GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
    { var td = r.GetTypeDefinition(h); return new("", (r.GetString(td.Namespace) is var ns && ns.Length > 0 ? ns + "." : "") + r.GetString(td.Name)); }
    public R GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
    {
        var tr = r.GetTypeReference(h);
        string asm = ResolveScope(r, tr.ResolutionScope);
        string ns = r.GetString(tr.Namespace);
        return new(asm, (ns.Length > 0 ? ns + "." : "") + r.GetString(tr.Name));
    }
    public R GetTypeFromSpecification(MetadataReader r, object? ctx, TypeSpecificationHandle h, byte k)
    { try { return r.GetTypeSpecification(h).DecodeSignature(this, ctx); } catch { return Empty; } }
    public R GetSZArrayType(R e) => e;
    public R GetArrayType(R e, ArrayShape s) => e;
    public R GetByReferenceType(R e) => e;
    public R GetPointerType(R e) => e;
    public R GetGenericInstantiation(R g, ImmutableArray<R> a) => g;
    public R GetGenericMethodParameter(object? ctx, int i) => Empty;
    public R GetGenericTypeParameter(object? ctx, int i) => Empty;
    public R GetModifiedType(R m, R u, bool isRequired) => u;
    public R GetPinnedType(R e) => e;
    public R GetFunctionPointerType(MethodSignature<R> s) => Empty;
    static string ResolveScope(MetadataReader r, EntityHandle scope)
    {
        if (scope.Kind == HandleKind.AssemblyReference) return r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        if (scope.Kind == HandleKind.TypeReference) return ResolveScope(r, r.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope);
        return "";
    }
}
