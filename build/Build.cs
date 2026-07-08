using static Bullseye.Targets;
using static SimpleExec.Command;

// FFXIV Combat Tracker build automation.
//
// Two intents, one tool:
//   debug   (default) -> Debug, single-file, compressed, no ReadyToRun     -> dist/debug
//                        A packed self-contained Fct.App.exe, but IL-only so it stays fast to
//                        build and fully debuggable. The first run self-extracts the bundle.
//   release           -> Release, single-file, compressed, ReadyToRun     -> dist/release
//                        One tidy self-contained Fct.App.exe to hand out. ReadyToRun precompiles
//                        IL to native (~2x faster warm start); the first run pays a one-time
//                        self-extract + AV scan of the new bundle.
//
// Both single-file modes extract the whole bundle to disk on first run
// (IncludeAllContentForSelfExtract): the host's plugin classifier opens a MetadataLoadContext over
// the on-disk runtime assemblies, which must exist as files.
//
// The app is two processes in one runtime tree — the net10 Avalonia host and the net48 satellite
// (Fct.LegacyHost) it launches from satellite\ at runtime — so both halves are published here.
// The real legacy plugins (FFXIV_ACT_Plugin, OverlayPlugin, ...) are NOT bundled; they load from
// the user's ACT install. Producing a build is Windows-only: the net48 satellite cannot publish
// off Windows.

const string Runtime = "win-x64";

var root = FindRoot();
var hostProj = Path.Combine(root, "src", "Fct.App", "Fct.App.csproj");
var satProj = Path.Combine(root, "src", "Fct.LegacyHost", "Fct.LegacyHost.csproj");

Target("debug",
    "Debug, single-file, compressed, no R2R -> dist/debug (packed, debuggable dev drop).",
    () => Publish("Debug", singleFile: true, readyToRun: false, mode: "debug"));

Target("release",
    "Release, single-file, compressed, ReadyToRun -> dist/release (tidy self-contained exe).",
    () => Publish("Release", singleFile: true, readyToRun: true, mode: "release"));

// No target on the command line runs "default".
Target("default", dependsOn: new[] { "debug" });

await RunTargetsAndExitAsync(args);

// Publish the host into dist/<mode>/ and the satellite into dist/<mode>/satellite/.
void Publish(string configuration, bool singleFile, bool readyToRun, string mode)
{
    var distRoot = Path.Combine(root, "dist");
    var outDir = Path.Combine(distRoot, mode);
    var satOut = Path.Combine(outDir, "satellite");

    Console.WriteLine($"==> FFXIV Combat Tracker build [{mode}]");
    Console.WriteLine($"    configuration : {configuration} (self-contained {Runtime})");
    Console.WriteLine($"    single-file   : {singleFile}");
    Console.WriteLine($"    ready-to-run  : {readyToRun}");
    Console.WriteLine($"    output        : {outDir}\n");

    // Wipe the whole dist/ tree (every mode) so each build lands a clean version with no stale files
    // from a prior debug/release drop.
    if (Directory.Exists(distRoot))
        Directory.Delete(distRoot, recursive: true);

    // 1. Host (net10) into the output root. Portable PDBs ship alongside so the drop is debuggable.
    // IncludeAllContentForSelfExtract: extract the whole bundle (managed DLLs included) to disk on
    // startup. Required because the host's plugin classifier opens a MetadataLoadContext over the
    // on-disk runtime assemblies (System.Private.CoreLib, ...) to find a core assembly — those must
    // exist as files, which a default single-file bundle (assemblies loaded from memory) does not
    // provide.
    var singleFileArgs = singleFile
        ? "-p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true "
        : "-p:PublishSingleFile=false ";
    Run("dotnet",
        $"publish \"{hostProj}\" -c {configuration} -r {Runtime} --self-contained true -o \"{outDir}\" --nologo " +
        "-p:DebugType=portable -p:DebugSymbols=true " +
        singleFileArgs +
        $"-p:PublishReadyToRun={Lower(readyToRun)}",
        workingDirectory: root);

    // The host build stages its own satellite copy into bin\; drop any that leaked into the publish
    // tree so satellite\ is exactly one clean publish.
    if (Directory.Exists(satOut))
        Directory.Delete(satOut, recursive: true);

    // 2. Satellite (net48 x64) into satellite\.
    Run("dotnet",
        $"publish \"{satProj}\" -c {configuration} -o \"{satOut}\" --nologo -p:DebugType=portable -p:DebugSymbols=true",
        workingDirectory: root);

    // 2b. Legacy compat runtime into compat\. The host's StageCompatShim target assembles this set
    // under bin\ during the publish's Build; publish itself doesn't carry those loose files into the
    // output, so copy the assembled package across. Without it CompatRuntime.Enable finds no compat\
    // and recompiled-shim plugins cannot load.
    var compatSrc = Path.Combine(root, "src", "Fct.App", "bin", configuration, "net10.0-windows", "compat");
    var compatOut = Path.Combine(outDir, "compat");
    if (!Directory.Exists(compatSrc))
        throw new InvalidOperationException($"compat package not assembled at {compatSrc} (StageCompatShim did not run)");
    CopyDirectory(compatSrc, compatOut);

    // 3. SDK packages (local-only — dist/ is git-ignored, never pushed to a feed). Consumers add
    // dist/<mode>/packages as a local NuGet folder source.
    var pkgOut = Path.Combine(outDir, "packages");
    foreach (var proj in new[]
    {
        Path.Combine(root, "src", "Fct.Abstractions", "Fct.Abstractions.csproj"),
        Path.Combine(root, "src", "Fct.Abstractions.UI", "Fct.Abstractions.UI.csproj"),
    })
        Run("dotnet", $"pack \"{proj}\" -c {configuration} -o \"{pkgOut}\" --nologo", workingDirectory: root);

    // 4. Verify entry points + packages exist.
    string hostExe = Path.Combine(outDir, "CombatTracker.exe");
    string satExe = Path.Combine(satOut, "Fct.LegacyHost.exe");
    string shimDll = Path.Combine(compatOut, "Fct.Compat.Shim.dll");
    foreach (var f in new[] { hostExe, satExe, shimDll })
        if (!File.Exists(f))
            throw new InvalidOperationException($"expected output missing: {f}");
    foreach (var pattern in new[] { "Fct.Abstractions.1.*.nupkg", "Fct.Abstractions.UI.1.*.nupkg" })
        if (Directory.GetFiles(pkgOut, pattern).Length != 1)
            throw new InvalidOperationException($"expected exactly one package matching {pattern} in {pkgOut}");

    long size = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    Console.WriteLine("\nBuild complete.");
    Console.WriteLine($"  host:         {hostExe}");
    Console.WriteLine($"  satellite:    {satExe}");
    Console.WriteLine($"  compat:       {compatOut}");
    Console.WriteLine($"  sdk packages: {pkgOut}");
    Console.WriteLine($"  size on disk: {size / 1024d / 1024d:N1} MB");
}

// Recursively copy a directory tree (used to stage the assembled compat\ package into the output).
static void CopyDirectory(string src, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(dest, Path.GetRelativePath(src, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static string Lower(bool b) => b ? "true" : "false";

// Walk up from the build output until the directory holding the solution is found, so the tool
// works regardless of the current working directory.
static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ffxiv-combat-tracker.slnx")))
        dir = dir.Parent;
    return dir?.FullName
        ?? throw new InvalidOperationException("could not locate repo root (ffxiv-combat-tracker.slnx)");
}
