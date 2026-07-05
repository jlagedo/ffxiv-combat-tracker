using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using Fct.Host.Hosting;
using Fct.Host.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Plugins;

[Collection("Sample plugin")]
public class PluginLoaderTests
{
    private const string SampleId = "com.fct.sample";
    private static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");
    private static string SampleDll => Path.Combine(PluginsRoot, "Fct.SamplePlugin", "Fct.SamplePlugin.dll");

    [Fact]
    public void LoadContext_shares_abstractions_but_isolates_the_plugin()
    {
        Assert.True(File.Exists(SampleDll), $"sample plugin not staged at {SampleDll}");

        var alc1 = new PluginLoadContext(SampleDll);
        var alc2 = new PluginLoadContext(SampleDll);
        try
        {
            var t1 = alc1.LoadFromAssemblyPath(SampleDll).GetType("Fct.SamplePlugin.SamplePlugin", throwOnError: true)!;
            var t2 = alc2.LoadFromAssemblyPath(SampleDll).GetType("Fct.SamplePlugin.SamplePlugin", throwOnError: true)!;

            // Two ALCs → two distinct plugin types (isolation).
            Assert.NotSame(t1, t2);

            // …but the contract interface has ONE identity, shared up to the default context.
            Assert.True(typeof(IPlugin).IsAssignableFrom(t1));
            var iface = Array.Find(t1.GetInterfaces(), i => i.Name == nameof(IPlugin));
            Assert.Same(typeof(IPlugin), iface);
        }
        finally
        {
            alc1.Unload();
            alc2.Unload();
        }
    }

    [Theory]
    [InlineData("Fct.Abstractions")]
    [InlineData("Fct.Abstractions.UI")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    [InlineData("Avalonia.Controls")]
    [InlineData("Fct.Compat.Shim")]
    [InlineData("Advanced Combat Tracker")]
    [InlineData("FFXIV_ACT_Plugin.Common")]
    public void Shared_assemblies_resolve_up_to_the_default_context(string name)
        => Assert.True(PluginLoadContext.IsShared(name));

    [Theory]
    [InlineData("Fct.SamplePlugin")]
    [InlineData("Newtonsoft.Json")]
    [InlineData(null)]
    public void Private_assemblies_stay_isolated_in_the_plugin_alc(string? name)
        => Assert.False(PluginLoadContext.IsShared(name));

    [Fact]
    public async Task Manager_loads_inits_and_unloads_the_sample_plugin()
    {
        Assert.True(File.Exists(SampleDll), $"sample plugin not staged at {SampleDll}");

        var bus = new GameEventBus();
        var game = new GameSession(bus, new GameSnapshotProvider());
        var clock = new SystemClock();
        var registry = new RegistryService();
        var audio = new AudioService(NullLogger<AudioService>.Instance);
        var encounters = new FakeEncounterService();

        // Observe the plugin's outputs: an audio sink and a bus subscriber for its synthetic 257 line.
        var sink = new RecordingAudioSink();
        audio.RegisterSink(sink);
        var rawLines = new List<RawLogLine>();
        using var sub = bus.Subscribe(GameEventFilter.All, e => { if (e is RawLogLine r) lock (rawLines) rawLines.Add(r); });

        var manager = new PluginManager(game, encounters, audio, registry, bus, clock, NullLoggerFactory.Instance)
        {
            PluginsRoot = PluginsRoot,
        };

        await manager.LoadAllAsync(CancellationToken.None);

        // Loaded + initialized, and on the registry roster with its manifest display metadata.
        Assert.Single(manager.Loaded);
        Assert.Equal(SampleId, manager.Loaded[0].Manifest.Id);
        Assert.Contains(registry.LoadedPlugins, p => p.Id == SampleId);
        Assert.Equal("Sample Plugin", Assert.Single(registry.LoadedPlugins, p => p.Id == SampleId).Name);

        // Its InitializeAsync drove the audio + write-back paths end-to-end.
        Assert.Contains(sink.Speaks, s => s.Text == "Sample plugin online");
        Assert.True(TestWait.Until(() => { lock (rawLines) return rawLines.Exists(r => r.Line == "257|sample-online"); }));

        // Read hatch: a raw packet on the bus reaches the (raw-capability) plugin's IRawPacketSource
        // subscriber, which re-emits a synthetic 258 line — proving gating handed it a live source.
        bus.Emit(new RawPacketReceived(bus.NextSequence(), clock.LocalNow, "tcp-1", 1L,
            PacketDirection.Received, new byte[] { 0xAB, 0xCD, 0xEF }));
        Assert.True(TestWait.Until(() => { lock (rawLines) return rawLines.Exists(r => r.Line == "258|packet-seen|3"); }));

        // And persisted a setting to its private storage dir (under the shared app-data root).
        var settings = Path.Combine(
            Fct.Logging.AppData.Root, "plugins", SampleId, "settings.json");
        Assert.True(File.Exists(settings));

        await manager.UnloadAllAsync();
        Assert.Empty(manager.Loaded);
        Assert.Empty(registry.LoadedPlugins);
    }

    [Fact]
    public async Task Manager_quarantines_a_plugin_with_a_bad_entry_type()
    {
        // A manifest pointing at the real assembly but a non-existent entry type: the loader must log,
        // skip, and unload — never load it, never throw.
        var dir = Path.Combine(Path.GetTempPath(), "fct-badplugin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(SampleDll, Path.Combine(dir, "Fct.SamplePlugin.dll"));
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            {
              "id": "com.test.bad",
              "version": "1.0.0",
              "contract": "1.0",
              "assembly": "Fct.SamplePlugin.dll",
              "entry": "Fct.SamplePlugin.DoesNotExist",
              "capabilities": []
            }
            """);

            var bus = new GameEventBus();
            var manager = new PluginManager(
                new GameSession(bus, new GameSnapshotProvider()),
                new FakeEncounterService(),
                new AudioService(NullLogger<AudioService>.Instance),
                new RegistryService(),
                bus,
                new SystemClock(),
                NullLoggerFactory.Instance)
            {
                PluginsRoot = dir,
            };

            await manager.LoadAllAsync(CancellationToken.None);

            Assert.Empty(manager.Loaded);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Manager_routes_legacy_manifests_to_the_shim_factory()
    {
        // A `legacyEntry` manifest goes through the injected shim factory rather than the native
        // entry-type path. Uses a stub factory (any loadable DLL as the plugin assembly) so the routing
        // is proven without a compile dependency on the real shim.
        var root = Path.Combine(Path.GetTempPath(), "fct-legacy-" + Path.GetRandomFileName());
        var dir = Path.Combine(root, "legacy");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(SampleDll, Path.Combine(dir, "Fct.SamplePlugin.dll"));
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            {
              "id": "com.test.legacy",
              "version": "1.0.0",
              "contract": "1.0",
              "assembly": "Fct.SamplePlugin.dll",
              "legacyEntry": "Some.Legacy.PluginType",
              "capabilities": []
            }
            """);

            string? capturedEntry = null;
            var fake = new RecordingPlugin();
            LegacyPluginHostFactory factory = (assembly, legacyEntry) =>
            {
                capturedEntry = legacyEntry;
                return fake;
            };

            var bus = new GameEventBus();
            var registry = new RegistryService();
            var manager = new PluginManager(
                new GameSession(bus, new GameSnapshotProvider()),
                new FakeEncounterService(),
                new AudioService(NullLogger<AudioService>.Instance),
                registry,
                bus,
                new SystemClock(),
                NullLoggerFactory.Instance,
                factory)
            {
                PluginsRoot = root,
            };

            await manager.LoadAllAsync(CancellationToken.None);

            // The factory was handed the manifest's legacyEntry, and the returned IPlugin was initialized.
            Assert.Equal("Some.Legacy.PluginType", capturedEntry);
            Assert.True(fake.Initialized);
            Assert.Single(manager.Loaded);
            Assert.Contains(registry.LoadedPlugins, p => p.Id == "com.test.legacy");

            await manager.UnloadAllAsync();
            Assert.True(fake.Disposed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class RecordingPlugin : IPlugin
    {
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }
}
