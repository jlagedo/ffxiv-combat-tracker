using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.Compat.Shim.E2E.Tests;

/// <summary>
/// Phase 5 exit-gate proof, end-to-end: with NO compile-time reference to the compat shim, the host's
/// <see cref="PluginManager"/> loads a recompiled-legacy plugin (<c>legacyEntry</c> manifest) through
/// the shim — where the shim + its two impersonation facades (<c>Advanced Combat Tracker</c> /
/// <c>FFXIV_ACT_Plugin.Common</c>) are resolved into the default ALC solely from the staged
/// <c>compat\</c> package by <see cref="CompatRuntime"/>. This is exactly the wiring
/// <c>Fct.App.Program</c> ships, exercised without the impersonation identities on the test's own graph.
/// </summary>
public class CompatRuntimeE2ETests
{
    private const string LegacyId = "com.fct.sample-legacy";
    private const string PluginTypeName = "Fct.SampleLegacyPlugin.SampleLegacyPlugin";

    private static string CompatDir => Path.Combine(AppContext.BaseDirectory, "compat");
    private static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    // The exact reflective factory Fct.App.Program registers — no compile-time shim dependency.
    private static readonly LegacyPluginHostFactory ReflectiveShimFactory = (assembly, legacyEntry) =>
    {
        var shim = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Fct.Compat.Shim"));
        var hostType = shim.GetType("Fct.Compat.Shim.LegacyPluginHost", throwOnError: true)!;
        return (IPlugin)Activator.CreateInstance(hostType, assembly, legacyEntry)!;
    };

    [Fact]
    public async Task Legacy_plugin_loads_and_runs_through_the_staged_compat_runtime()
    {
        Assert.True(File.Exists(Path.Combine(CompatDir, "Fct.Compat.Shim.dll")),
            $"compat runtime not staged at {CompatDir}");
        Assert.True(File.Exists(Path.Combine(PluginsRoot, "Fct.SampleLegacyPlugin", "Fct.SampleLegacyPlugin.dll")),
            $"sample legacy plugin not staged under {PluginsRoot}");

        // Opt in to the compat runtime (idempotent) so the default ALC resolves the shim/facades from compat\.
        CompatRuntime.Enable(CompatDir, NullLoggerFactory.Instance);

        var bus = new GameEventBus();
        var clock = new SystemClock();
        var registry = new RegistryService();
        var manager = new PluginManager(
            new GameSession(bus, new GameSnapshotProvider()),
            new FakeEncounterService(),
            new AudioService(NullLogger<AudioService>.Instance),
            registry, bus, clock, NullLoggerFactory.Instance, ReflectiveShimFactory)
        {
            PluginsRoot = PluginsRoot,
        };

        await manager.LoadAllAsync(CancellationToken.None);

        // The legacy plugin loaded through the shim and is on the roster.
        var loaded = Assert.Single(manager.Loaded, p => p.Manifest.Id == LegacyId);
        Assert.Contains(registry.LoadedPlugins, p => p.Id == LegacyId);

        // Read the plugin's static counters from ITS OWN ALC: InitPlugin ran across the boundary, and the
        // status label was set through the shared ActGlobals hub — all with the shim + Advanced Combat
        // Tracker facade resolved only from compat\. Hold the type across unload so the (collectible) ALC
        // is not collected before we read DeInitCount.
        var pluginType = loaded.Alc.Assemblies
            .Single(a => a.GetName().Name == "Fct.SampleLegacyPlugin")
            .GetType(PluginTypeName, throwOnError: true)!;

        Assert.Equal(1, ReadStaticInt(pluginType, "InitCount"));
        Assert.Equal("Sample legacy plugin online", ReadStaticString(pluginType, "LastStatus"));

        await manager.UnloadAllAsync();

        // DisposeAsync drove DeInitPlugin, and the loader tore the plugin off the roster.
        Assert.Equal(1, ReadStaticInt(pluginType, "DeInitCount"));
        Assert.Empty(manager.Loaded);
        Assert.DoesNotContain(registry.LoadedPlugins, p => p.Id == LegacyId);
    }

    private static int ReadStaticInt(Type t, string field) =>
        (int)t.GetField(field, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static string ReadStaticString(Type t, string field) =>
        (string)t.GetField(field, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
}
