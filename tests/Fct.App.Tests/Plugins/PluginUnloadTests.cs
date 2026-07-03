using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Plugins;

[Collection("Sample plugin")]
public class PluginUnloadTests
{
    private static string SampleDir => Path.Combine(AppContext.BaseDirectory, "plugins", "Fct.SamplePlugin");
    private const string SampleId = "com.fct.sample";

    [Fact]
    public async Task Hot_unload_removes_one_plugin_fires_roster_change_and_allows_reload()
    {
        Assert.True(File.Exists(Path.Combine(SampleDir, "Fct.SamplePlugin.dll")), "sample plugin not staged");

        var bus = new GameEventBus();
        var registry = new RegistryService();
        int rosterEvents = 0;
        registry.RosterChanged += () => Interlocked.Increment(ref rosterEvents);

        var manager = new PluginManager(
            new GameSession(bus, new GameSnapshotProvider()),
            new EncounterService(new SystemClock()),
            new AudioService(NullLogger<AudioService>.Instance),
            registry, bus, new SystemClock(), NullLoggerFactory.Instance);

        // Load a private copy (unload shouldn't disturb the staged sample).
        var dir = Path.Combine(Path.GetTempPath(), "fct-unload-" + Path.GetRandomFileName());
        CopyDir(SampleDir, dir);
        try
        {
            var loaded = await manager.LoadDirectoryAsync(dir, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Single(manager.Loaded);
            Assert.Contains(registry.LoadedPlugins, p => p.Id == SampleId);
            var eventsAfterLoad = rosterEvents;
            Assert.True(eventsAfterLoad >= 1);

            await manager.UnloadAsync(SampleId);
            Assert.Empty(manager.Loaded);
            Assert.Empty(registry.LoadedPlugins);
            Assert.True(rosterEvents > eventsAfterLoad);

            // Re-loading the same id succeeds (proves no id/surface collision was left behind).
            var reloaded = await manager.LoadDirectoryAsync(dir, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Single(manager.Loaded);

            await manager.UnloadAllAsync();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
    }
}
