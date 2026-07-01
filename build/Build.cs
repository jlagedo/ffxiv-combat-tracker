using static Bullseye.Targets;
using static SimpleExec.Command;

// FFXIV Combat Tracker build automation.
//
// Two intents, one tool:
//   debug   (default) -> Debug, loose DLLs, no compression, no ReadyToRun -> dist/debug
//                        The fast, debuggable dev drop: launch skips the single-file self-extract
//                        step, so cold start is quick and consistent.
//   release           -> Release, single-file, compressed, ReadyToRun     -> dist/release
//                        One tidy self-contained Fct.App.exe to hand out. ReadyToRun precompiles
//                        IL to native (~2x faster warm start); the first run pays a one-time
//                        self-extract + AV scan of the new bundle.
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
    "Debug, loose DLLs, no compression, no R2R -> dist/debug (fast, debuggable dev drop).",
    () => Publish("Debug", singleFile: false, readyToRun: false, mode: "debug"));

Target("release",
    "Release, single-file, compressed, ReadyToRun -> dist/release (tidy self-contained exe).",
    () => Publish("Release", singleFile: true, readyToRun: true, mode: "release"));

// No target on the command line runs "default".
Target("default", dependsOn: new[] { "debug" });

await RunTargetsAndExitAsync(args);

// Publish the host into dist/<mode>/ and the satellite into dist/<mode>/satellite/.
void Publish(string configuration, bool singleFile, bool readyToRun, string mode)
{
    var outDir = Path.Combine(root, "dist", mode);
    var satOut = Path.Combine(outDir, "satellite");

    Console.WriteLine($"==> FFXIV Combat Tracker build [{mode}]");
    Console.WriteLine($"    configuration : {configuration} (self-contained {Runtime})");
    Console.WriteLine($"    single-file   : {singleFile}");
    Console.WriteLine($"    ready-to-run  : {readyToRun}");
    Console.WriteLine($"    output        : {outDir}\n");

    if (Directory.Exists(outDir))
        Directory.Delete(outDir, recursive: true);

    // 1. Host (net10) into the output root. Portable PDBs ship alongside so the drop is debuggable.
    var singleFileArgs = singleFile
        ? "-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "
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

    // 3. Verify both entry points exist.
    string hostExe = Path.Combine(outDir, "Fct.App.exe");
    string satExe = Path.Combine(satOut, "Fct.LegacyHost.exe");
    foreach (var exe in new[] { hostExe, satExe })
        if (!File.Exists(exe))
            throw new InvalidOperationException($"expected output missing: {exe}");

    long size = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    Console.WriteLine("\nBuild complete.");
    Console.WriteLine($"  host:         {hostExe}");
    Console.WriteLine($"  satellite:    {satExe}");
    Console.WriteLine($"  size on disk: {size / 1024d / 1024d:N1} MB");
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
