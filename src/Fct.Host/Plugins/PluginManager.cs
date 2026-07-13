using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Plugins;

/// <summary>
/// Builds the <see cref="IPlugin"/> that drives a legacy (compat-shim) plugin: given the plugin's
/// loaded assembly and the <c>IActPluginV1</c> type named in its manifest, returns the shim's
/// <c>LegacyPluginHost</c>. Supplied by the host composition root so <see cref="PluginManager"/> takes
/// no compile-time dependency on the shim (keeps the headless loader tests shim-free); null when the
/// shim is not wired, in which case legacy manifests are rejected.
/// </summary>
internal delegate IPlugin? LegacyPluginHostFactory(System.Reflection.Assembly pluginAssembly, string legacyEntry);

/// <summary>
/// Discovers, loads, initializes, and unloads native <see cref="IPlugin"/>s. Each plugin gets its own
/// collectible <see cref="PluginLoadContext"/> and a per-plugin <see cref="PluginHost"/> over the
/// shared services. Init is fault-guarded and time-boxed: a plugin that throws or overruns its budget
/// is logged, skipped, and its ALC unloaded (cooperative quarantine).
/// </summary>
internal sealed class PluginManager
{
    private readonly IGameSession _game;
    private readonly IEncounterService _encounters;
    private readonly IAudioOutput _audio;
    private readonly RegistryService _registry;
    private readonly IGameEventSink _sink;
    private readonly RawPacketSource _rawPackets;
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginManager> _log;
    private readonly LegacyPluginHostFactory? _legacyFactory;
    private readonly INotificationHub? _notifications;
    private readonly PluginClassifier _classifier;
    private readonly List<LoadedPlugin> _loaded = new();

    public PluginManager(
        IGameSession game,
        IEncounterService encounters,
        IAudioOutput audio,
        RegistryService registry,
        GameEventBus bus,
        IClock clock,
        ILoggerFactory loggerFactory,
        LegacyPluginHostFactory? legacyFactory = null,
        INotificationHub? notifications = null,
        RawPacketSource? rawPackets = null,
        PluginClassifier? classifier = null)
    {
        _game = game;
        _encounters = encounters;
        _audio = audio;
        _registry = registry;
        _sink = bus;
        // One process-wide raw-packet reader over the bus firehose; handed out gated per plugin.
        _rawPackets = rawPackets ?? new RawPacketSource(bus, loggerFactory.CreateLogger<RawPacketSource>());
        _clock = clock;
        _loggerFactory = loggerFactory;
        _notifications = notifications;
        _classifier = classifier ?? new PluginClassifier(loggerFactory.CreateLogger<PluginClassifier>());
        _log = loggerFactory.CreateLogger<PluginManager>();
        _legacyFactory = legacyFactory;
    }

    /// <summary>
    /// Root of plugin folders (each holds a <c>plugin.json</c>) that <see cref="LoadAllAsync"/> scans as a
    /// batch. Not part of the app's startup path — startup is registry-driven (see <c>PluginLifetime</c>);
    /// this is the seam tests point at their own staged folder. Overridable for tests.
    /// </summary>
    public string PluginsRoot { get; set; } = Path.Combine(AppData.InstallDirectory, "plugins");

    /// <summary>Per-plugin init budget; the cancellation token cancels a slow <c>InitializeAsync</c>.</summary>
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The plugins that loaded and initialized successfully.</summary>
    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    public async Task LoadAllAsync(CancellationToken ct)
    {
        _log.LogInformation(LogEvents.NativePluginsScanning, "Scanning for native plugins under {Root}", PluginsRoot);
        if (!Directory.Exists(PluginsRoot))
        {
            _log.LogInformation(LogEvents.NativePluginsReady, "No plugins directory; 0 native plugins loaded");
            return;
        }

        foreach (var dir in Directory.GetDirectories(PluginsRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (TryResolveManifestForInProcessLoad(dir, out var manifest))
                await LoadOneAsync(dir, manifest!, ct).ConfigureAwait(false);
        }

        UpdateRoster();
        _log.LogInformation(LogEvents.NativePluginsReady, "{Count} native plugin(s) ready", _loaded.Count);
    }

    /// <summary>
    /// Hot-load one plugin directory after startup (the install path). Classifies + loads it and
    /// refreshes the roster; a no-op if a plugin with the same id is already loaded. Returns the
    /// loaded plugin, or null if it could not load in-process (rejected, faulted, or real-legacy).
    /// </summary>
    public async Task<LoadedPlugin?> LoadDirectoryAsync(string dir, CancellationToken ct)
    {
        if (!TryResolveManifestForInProcessLoad(dir, out var manifest)) return null;
        if (_loaded.Any(p => string.Equals(p.Manifest.Id, manifest!.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogInformation(LogEvents.NativePluginLoaded, "Plugin {Id} already loaded; skipping", manifest!.Id);
            return _loaded.First(p => string.Equals(p.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
        }

        var loaded = await LoadOneAsync(dir, manifest!, ct).ConfigureAwait(false);
        UpdateRoster();
        return loaded;
    }

    // Produce the manifest to load a directory in-process: the real plugin.json (contract-gated) when
    // present, otherwise a synthetic one from metadata classification. Returns false when the dir has
    // no valid manifest, is contract-incompatible, or is a real-legacy plugin (which the satellite
    // hosts — never loaded in-process here).
    private bool TryResolveManifestForInProcessLoad(string dir, out PluginManifest? manifest)
    {
        manifest = null;
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (File.Exists(manifestPath))
        {
            if (!PluginManifest.TryLoad(manifestPath, out manifest, out var error))
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected, "Rejected manifest {Path}: {Error}", manifestPath, error);
                return false;
            }
            if (!HostContract.Accepts(manifest!.Contract))
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected,
                    "Rejected plugin {Id}: contract {Contract} incompatible with host {Host}",
                    manifest.Id, manifest.Contract, HostContract.Version);
                manifest = null;
                return false;
            }
            return true;
        }

        // Manifest-less: classify by inspecting the assembly metadata (detect-if-absent).
        PluginClassification classification;
        try { classification = _classifier.Classify(dir, manifest: null); }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected, "Could not classify plugin in {Dir}: {Error}", dir, ex.Message);
            return false;
        }

        if (classification.Kind == LoadKind.RealLegacy)
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected,
                "Plugin {Id} in {Dir} is a real net48 legacy plugin — the satellite hosts it, not the in-process loader",
                classification.Id, dir);
            return false;
        }

        manifest = ToManifest(classification);
        return true;
    }

    // A synthetic manifest for a classified, manifest-less in-process plugin. Capabilities are empty
    // (a manifest-less plugin declares none); contract is the host's own, since the kind was proven by
    // the real IPlugin/IActPluginV1 identity rather than a declared contract string.
    private static PluginManifest ToManifest(PluginClassification c) => new(
        c.Id, c.Version, HostContract.Version, c.AssemblyFile,
        Entry: c.Kind == LoadKind.Native ? c.EntryTypeName : null,
        Capabilities: Array.Empty<string>(),
        LegacyEntry: c.Kind == LoadKind.RecompiledShim ? c.EntryTypeName : null);

    private async Task<LoadedPlugin?> LoadOneAsync(string dir, PluginManifest manifest, CancellationToken ct)
    {
        var assemblyPath = Path.Combine(dir, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected,
                "Rejected plugin {Id}: entry assembly {Assembly} not found", manifest.Id, manifest.Assembly);
            return null;
        }

        PluginLoadContext? alc = null;
        IPlugin? instance = null;
        ScopedPluginRegistry? scoped = null;
        try
        {
            alc = new PluginLoadContext(assemblyPath);
            var assembly = alc.LoadFromAssemblyPath(assemblyPath);

            if (manifest.LegacyEntry is not null)
            {
                if (_legacyFactory is null)
                {
                    _log.LogWarning(LogEvents.NativePluginManifestRejected,
                        "Plugin {Id}: legacy entry {Entry} but the compat shim is not available", manifest.Id, manifest.LegacyEntry);
                    alc.Unload();
                    return null;
                }

                instance = _legacyFactory(assembly, manifest.LegacyEntry);
                if (instance is null)
                {
                    _log.LogWarning(LogEvents.NativePluginManifestRejected,
                        "Plugin {Id}: compat shim could not host legacy entry {Entry}", manifest.Id, manifest.LegacyEntry);
                    alc.Unload();
                    return null;
                }

                _log.LogInformation(LogEvents.NativePluginLoaded, "Loaded legacy plugin {Id} v{Version} ({Entry}) via compat shim",
                    manifest.Id, manifest.Version, manifest.LegacyEntry);
            }
            else
            {
                var type = assembly.GetType(manifest.Entry!, throwOnError: false);
                if (type is null || !typeof(IPlugin).IsAssignableFrom(type))
                {
                    _log.LogWarning(LogEvents.NativePluginManifestRejected,
                        "Plugin {Id}: entry type {Entry} is missing or not an IPlugin", manifest.Id, manifest.Entry);
                    alc.Unload();
                    return null;
                }

                instance = (IPlugin)Activator.CreateInstance(type)!;
                _log.LogInformation(LogEvents.NativePluginLoaded, "Loaded plugin {Id} v{Version} ({Entry})",
                    manifest.Id, manifest.Version, manifest.Entry);
            }

            var host = BuildHost(manifest, out scoped);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InitTimeout);
            await instance.InitializeAsync(host, cts.Token).ConfigureAwait(false);

            var loaded = new LoadedPlugin(manifest, alc, instance, scoped);
            _loaded.Add(loaded);
            _log.LogInformation(LogEvents.NativePluginInitialized, "Initialized plugin {Id}", manifest.Id);
            _notifications?.Publish(NotificationSeverity.Success, manifest.Id,
                $"{manifest.Id} loaded", $"Native plugin v{manifest.Version} is running.");
            return loaded;
        }
        catch (Exception ex)
        {
            _log.LogError(LogEvents.NativePluginFaulted, ex, "Plugin {Id} faulted during load/init — quarantined", manifest.Id);
            _notifications?.Publish(NotificationSeverity.Error, manifest.Id,
                $"{manifest.Id} was quarantined", ex.Message);
            if (scoped is not null)
            {
                try { scoped.Dispose(); } catch { /* best-effort */ }
            }
            if (instance is not null)
            {
                try { await instance.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
            try { alc?.Unload(); } catch { /* best-effort */ }
            return null;
        }
    }

    private PluginHost BuildHost(PluginManifest manifest, out ScopedPluginRegistry scoped)
    {
        var self = new PluginInfo(manifest.Id, manifest.Version, manifest.Contract)
        {
            Name = manifest.Name,
            Description = manifest.Description,
            Author = manifest.Author,
        };
        var storage = new PluginStorage(manifest.Id);
        var logger = _loggerFactory.CreateLogger($"Fct.Plugin.{manifest.Id}");
        bool hasRaw = manifest.HasCapability("raw");
        IRawLogLineEmitter raw = hasRaw ? new RawLogLineEmitter(_sink, _clock) : RawLogLineEmitter.Noop;
        IRawPacketSource packets = hasRaw ? _rawPackets : RawPacketSource.Noop;
        // A per-plugin registry facade that tracks this plugin's registrations so unload can force
        // them all closed — otherwise a leftover delegate pins the collectible ALC.
        scoped = new ScopedPluginRegistry(_registry, manifest.Id);
        return new PluginHost(self, _game, _encounters, _audio, scoped, storage, logger, _clock, raw, packets);
    }

    /// <summary>
    /// Hot-unload a single in-process plugin: dispose its instance, force its registrations closed,
    /// unload its collectible ALC, and wait (bounded) for the GC to collect it so its files are no
    /// longer locked. Returns true if the ALC was collected (the caller may delete its files),
    /// false if it is still alive (the caller should defer deletion). True immediately when no such
    /// plugin is loaded (e.g. a real-legacy plugin the satellite hosts).
    /// </summary>
    public async Task<bool> UnloadAsync(string id)
    {
        int idx = _loaded.FindIndex(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return true;

        var p = _loaded[idx];
        _loaded.RemoveAt(idx);
        UpdateRoster();

        try { await p.Instance.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(LogEvents.NativePluginFaulted, ex, "Plugin {Id} threw during dispose", id); }
        try { p.Scoped.Dispose(); } catch { /* best-effort */ }

        bool collected = UnloadAndWait(p.Alc);
        p = null!; // drop the last strong ref so the ALC can be collected
        _log.LogInformation(LogEvents.NativePluginUnloaded,
            collected ? "Unloaded plugin {Id} (ALC collected)" : "Unloaded plugin {Id} (ALC not yet collected)", id);
        return collected;
    }

    // Initiate collectible-ALC unload, then pump the GC a bounded number of times until the context is
    // collected (its assemblies' file handles release) or the budget is exhausted.
    private static bool UnloadAndWait(PluginLoadContext alc)
    {
        var weak = new WeakReference(alc);
        try { alc.Unload(); } catch { /* best-effort */ }
        alc = null!;
        for (int i = 0; weak.IsAlive && i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !weak.IsAlive;
    }

    public async Task UnloadAllAsync()
    {
        LoadedPlugin[] snapshot = _loaded.ToArray();
        _loaded.Clear();
        UpdateRoster();

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var p = snapshot[i];
            try { await p.Instance.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(LogEvents.NativePluginFaulted, ex, "Plugin {Id} threw during dispose", p.Manifest.Id); }
            try { p.Scoped.Dispose(); } catch { /* best-effort */ }
            try { p.Alc.Unload(); } catch { /* best-effort */ }
            _log.LogInformation(LogEvents.NativePluginUnloaded, "Unloaded plugin {Id}", p.Manifest.Id);
        }
    }

    private void UpdateRoster()
        => _registry.SetRoster(_loaded.Select(p => new PluginInfo(p.Manifest.Id, p.Manifest.Version, p.Manifest.Contract)
        {
            Name = p.Manifest.Name,
            Description = p.Manifest.Description,
            Author = p.Manifest.Author,
        }).ToArray());

    internal sealed record LoadedPlugin(PluginManifest Manifest, PluginLoadContext Alc, IPlugin Instance, ScopedPluginRegistry Scoped);
}
