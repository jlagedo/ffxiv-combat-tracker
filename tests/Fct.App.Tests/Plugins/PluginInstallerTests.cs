using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Fct.Host.Plugins.Ui;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Plugins;

[Collection("Sample plugin")]
public class PluginInstallerTests
{
    private static string SampleDir => Path.Combine(AppContext.BaseDirectory, "plugins", "Fct.SamplePlugin");
    private const string SampleId = "com.fct.sample";

    [Fact]
    public async Task Install_from_zip_classifies_loads_persists_then_uninstall_reverses_it()
    {
        Assert.True(File.Exists(Path.Combine(SampleDir, "Fct.SamplePlugin.dll")), "sample plugin not staged");
        using var h = new Harness();

        var zip = Path.Combine(h.Base, "sample.zip");
        ZipFile.CreateFromDirectory(SampleDir, zip);

        var result = await h.Installer.InstallAsync(zip, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(LoadKind.Native, result.Kind);
        Assert.Equal(SampleId, result.Id);
        Assert.Single(h.Manager.Loaded);
        Assert.NotNull(h.Registry.Find(SampleId));
        Assert.True(Directory.Exists(h.Paths.DirFor(SampleId)));

        // Unload = uninstall: torn down live, dropped from the registry.
        Assert.True(await h.Installer.UninstallAsync(SampleId, CancellationToken.None));
        Assert.Empty(h.Manager.Loaded);
        Assert.Null(h.Registry.Find(SampleId));
    }

    private static string FfxivPluginPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Advanced Combat Tracker", "Plugins", "FFXIV_ACT_Plugin.dll");

    [Fact]
    public void Replay_loads_present_legacy_records_and_reports_the_missing_ones()
    {
        using var h = new Harness();

        // A record whose DLL exists (replay only resolves the file; it never classifies) …
        var present = Path.Combine(h.Base, "present");
        Directory.CreateDirectory(present);
        File.Copy(Path.Combine(SampleDir, "Fct.SamplePlugin.dll"), Path.Combine(present, "Some_Legacy.dll"));
        h.Registry.Add(new InstalledPluginRecord("some_legacy", LoadKind.RealLegacy, present, null, "1.0")
        { Title = "Some Legacy", AssemblyFile = "Some_Legacy.dll" });

        // … and one whose install-by-reference source folder is gone.
        h.Registry.Add(new InstalledPluginRecord("gone_legacy", LoadKind.RealLegacy,
            Path.Combine(h.Base, "nowhere"), null, "1.0")
        { Title = "Gone Legacy", AssemblyFile = "Gone_Legacy.dll" });

        var missing = h.Installer.ReplayLegacyToSatellite();

        Assert.Single(missing);
        Assert.Equal("gone_legacy", missing[0].Id);
        var request = Assert.Single(h.Satellite.LoadRequests);
        Assert.Equal("some_legacy", request.Key);
        Assert.Equal(Path.Combine(present, "Some_Legacy.dll"), request.DllPath);
    }

    [Fact]
    public void Relink_rejects_a_dll_that_is_not_a_classic_plugin()
    {
        using var h = new Harness();
        var dir = Path.Combine(h.Base, "old-home");
        h.Registry.Add(new InstalledPluginRecord("fct.sampleplugin", LoadKind.RealLegacy, dir, null, "1.0")
        { Title = "Sample", AssemblyFile = "Fct.SamplePlugin.dll" });

        // The sample plugin classifies as Native, so relinking to it must be refused.
        var result = h.Installer.RelinkLegacy("fct.sampleplugin", Path.Combine(SampleDir, "Fct.SamplePlugin.dll"));

        Assert.False(result.Success);
        Assert.Equal(dir, h.Registry.Find("fct.sampleplugin")!.Dir);   // record untouched
        Assert.Empty(h.Satellite.LoadRequests);
    }

    [Fact]
    public void Relink_rejects_an_unknown_plugin_id()
    {
        using var h = new Harness();
        var result = h.Installer.RelinkLegacy("nope", Path.Combine(SampleDir, "Fct.SamplePlugin.dll"));
        Assert.False(result.Success);
    }

    [SkippableFact]
    public void Relink_repoints_the_record_and_requests_a_load()
    {
        Skip.IfNot(File.Exists(FfxivPluginPath), $"FFXIV_ACT_Plugin not installed at {FfxivPluginPath}.");
        using var h = new Harness();

        // The plugin was installed by reference from a folder that no longer exists; the user picks
        // its DLL at the new home.
        var newHome = Path.Combine(h.Base, "new-home");
        Directory.CreateDirectory(newHome);
        var dll = Path.Combine(newHome, "FFXIV_ACT_Plugin.dll");
        File.Copy(FfxivPluginPath, dll);
        h.Registry.Add(new InstalledPluginRecord("ffxiv_act_plugin", LoadKind.RealLegacy,
            Path.Combine(h.Base, "old-home"), null, "0.0.0")
        { Title = "FFXIV_ACT_Plugin", AssemblyFile = "FFXIV_ACT_Plugin.dll" });

        var result = h.Installer.RelinkLegacy("ffxiv_act_plugin", dll);

        Assert.True(result.Success, result.Error);
        Assert.Equal(LoadKind.RealLegacy, result.Kind);
        var record = h.Registry.Find("ffxiv_act_plugin")!;
        Assert.Equal(newHome, record.Dir);
        Assert.Equal("FFXIV_ACT_Plugin.dll", record.AssemblyFile);
        var request = Assert.Single(h.Satellite.LoadRequests);
        Assert.Equal("ffxiv_act_plugin", request.Key);
        Assert.Equal(dll, request.DllPath);
    }

    [Fact]
    public async Task Install_from_a_manifest_less_directory_detects_native_and_loads_it()
    {
        using var h = new Harness();

        // A directory with the sample's assemblies but no plugin.json — the installer must classify it.
        var src = Path.Combine(h.Base, "loose");
        Directory.CreateDirectory(src);
        foreach (var dll in Directory.GetFiles(SampleDir, "*.dll"))
            File.Copy(dll, Path.Combine(src, Path.GetFileName(dll)));
        File.Copy(Path.Combine(SampleDir, "Fct.SamplePlugin.deps.json"),
                  Path.Combine(src, "Fct.SamplePlugin.deps.json"));

        var result = await h.Installer.InstallAsync(src, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(LoadKind.Native, result.Kind);
        Assert.Equal("fct.sampleplugin", result.Id);          // synthesized id
        Assert.Single(h.Manager.Loaded);
    }

    private sealed class Harness : IDisposable
    {
        public readonly string Base = Path.Combine(Path.GetTempPath(), "fct-install-" + Path.GetRandomFileName());
        public readonly PluginManager Manager;
        public readonly PluginRegistryStore Registry;
        public readonly PluginInstallPaths Paths;
        public readonly PluginInstaller Installer;
        public readonly FakeSatelliteChannel Satellite;

        public Harness()
        {
            Directory.CreateDirectory(Base);
            var bus = new GameEventBus();
            var registry = new RegistryService();
            Manager = new PluginManager(
                new GameSession(bus, new GameSnapshotProvider()),
                new EncounterService(new SystemClock()),
                new AudioService(NullLogger<AudioService>.Instance),
                registry, bus, new SystemClock(), NullLoggerFactory.Instance);
            Satellite = new FakeSatelliteChannel();
            Paths = new PluginInstallPaths(Base);
            Registry = new PluginRegistryStore(Path.Combine(Base, "installed-plugins.json"));
            var ui = new PluginUiCoordinator(new InlineDispatcher(), NullLoggerFactory.Instance);
            Installer = new PluginInstaller(Manager, Satellite, new PluginClassifier(), Paths, Registry, ui, NullLoggerFactory.Instance);
        }

        public void Dispose()
        {
            try { Manager.UnloadAllAsync().GetAwaiter().GetResult(); } catch { }
            try { Directory.Delete(Base, true); } catch { }
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    }

    internal sealed class FakeSatelliteChannel : ISatellitePluginChannel
    {
        public readonly System.Collections.Generic.List<(string Key, string DllPath, string Title)> LoadRequests = new();

        public bool RequestLoadPlugin(string key, string dllPath, string title)
        {
            LoadRequests.Add((key, dllPath, title));
            return true;
        }

        public Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout) => Task.FromResult(true);
    }
}
