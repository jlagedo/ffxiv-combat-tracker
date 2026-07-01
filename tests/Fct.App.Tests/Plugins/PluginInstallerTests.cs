using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.App.Hosting;
using Fct.App.Plugins;
using Fct.App.Plugins.Ui;
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
            var satellite = new FakeSatelliteChannel();
            Paths = new PluginInstallPaths(Base);
            Registry = new PluginRegistryStore(Path.Combine(Base, "installed-plugins.json"));
            var ui = new PluginUiCoordinator(new InlineDispatcher(), NullLoggerFactory.Instance);
            Installer = new PluginInstaller(Manager, satellite, new PluginClassifier(), Paths, Registry, ui, NullLoggerFactory.Instance);
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

    private sealed class FakeSatelliteChannel : ISatellitePluginChannel
    {
        public bool RequestLoadPlugin(string key, string dllPath, string title) => true;
        public Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout) => Task.FromResult(true);
    }
}
