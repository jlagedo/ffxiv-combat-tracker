using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.App.Hosting;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.App.Plugins;

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
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginManager> _log;
    private readonly INotificationHub? _notifications;
    private readonly List<LoadedPlugin> _loaded = new();

    public PluginManager(
        IGameSession game,
        IEncounterService encounters,
        IAudioOutput audio,
        RegistryService registry,
        GameEventBus bus,
        IClock clock,
        ILoggerFactory loggerFactory,
        INotificationHub? notifications = null)
    {
        _game = game;
        _encounters = encounters;
        _audio = audio;
        _registry = registry;
        _sink = bus;
        _clock = clock;
        _loggerFactory = loggerFactory;
        _notifications = notifications;
        _log = loggerFactory.CreateLogger<PluginManager>();
    }

    /// <summary>Directory scanned for plugin folders (each holds a <c>plugin.json</c>). Overridable for tests.</summary>
    public string PluginsRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "plugins");

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
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;

            if (!PluginManifest.TryLoad(manifestPath, out var manifest, out var error))
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected, "Rejected manifest {Path}: {Error}", manifestPath, error);
                continue;
            }

            if (!HostContract.Accepts(manifest!.Contract))
            {
                _log.LogWarning(LogEvents.NativePluginManifestRejected,
                    "Rejected plugin {Id}: contract {Contract} incompatible with host {Host}",
                    manifest.Id, manifest.Contract, HostContract.Version);
                continue;
            }

            await LoadOneAsync(dir, manifest, ct).ConfigureAwait(false);
        }

        UpdateRoster();
        _log.LogInformation(LogEvents.NativePluginsReady, "{Count} native plugin(s) ready", _loaded.Count);
    }

    private async Task LoadOneAsync(string dir, PluginManifest manifest, CancellationToken ct)
    {
        var assemblyPath = Path.Combine(dir, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            _log.LogWarning(LogEvents.NativePluginManifestRejected,
                "Rejected plugin {Id}: entry assembly {Assembly} not found", manifest.Id, manifest.Assembly);
            return;
        }

        PluginLoadContext? alc = null;
        IPlugin? instance = null;
        try
        {
            alc = new PluginLoadContext(assemblyPath);
            var assembly = alc.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.Entry, throwOnError: false);
            if (type is null || !typeof(IPlugin).IsAssignableFrom(type))
            {
                _log.LogWarning(LogEvents.NativePluginFaulted,
                    "Plugin {Id}: entry type {Entry} is missing or not an IPlugin", manifest.Id, manifest.Entry);
                alc.Unload();
                return;
            }

            instance = (IPlugin)Activator.CreateInstance(type)!;
            _log.LogInformation(LogEvents.NativePluginLoaded, "Loaded plugin {Id} v{Version} ({Entry})",
                manifest.Id, manifest.Version, manifest.Entry);

            var host = BuildHost(manifest);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InitTimeout);
            await instance.InitializeAsync(host, cts.Token).ConfigureAwait(false);

            _loaded.Add(new LoadedPlugin(manifest, alc, instance));
            _log.LogInformation(LogEvents.NativePluginInitialized, "Initialized plugin {Id}", manifest.Id);
            _notifications?.Publish(NotificationSeverity.Success, manifest.Id,
                $"{manifest.Id} loaded", $"Native plugin v{manifest.Version} is running.");
        }
        catch (Exception ex)
        {
            _log.LogError(LogEvents.NativePluginFaulted, ex, "Plugin {Id} faulted during load/init — quarantined", manifest.Id);
            _notifications?.Publish(NotificationSeverity.Error, manifest.Id,
                $"{manifest.Id} was quarantined", ex.Message);
            if (instance is not null)
            {
                try { await instance.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
            try { alc?.Unload(); } catch { /* best-effort */ }
        }
    }

    private PluginHost BuildHost(PluginManifest manifest)
    {
        var self = new PluginInfo(manifest.Id, manifest.Version, manifest.Contract);
        var storage = new PluginStorage(manifest.Id);
        var logger = _loggerFactory.CreateLogger($"Fct.Plugin.{manifest.Id}");
        IRawLogLineEmitter raw = manifest.HasCapability("raw")
            ? new RawLogLineEmitter(_sink, _clock)
            : RawLogLineEmitter.Noop;
        return new PluginHost(self, _game, _encounters, _audio, _registry, storage, logger, _clock, raw);
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
            try { p.Alc.Unload(); } catch { /* best-effort */ }
            _log.LogInformation(LogEvents.NativePluginUnloaded, "Unloaded plugin {Id}", p.Manifest.Id);
        }
    }

    private void UpdateRoster()
        => _registry.SetRoster(_loaded.Select(p => new PluginInfo(p.Manifest.Id, p.Manifest.Version, p.Manifest.Contract)).ToArray());

    internal sealed record LoadedPlugin(PluginManifest Manifest, PluginLoadContext Alc, IPlugin Instance);
}
