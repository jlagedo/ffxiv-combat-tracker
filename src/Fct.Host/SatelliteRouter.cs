using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Host.Plugins;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host;

/// <summary>
/// The production routing seam over the <see cref="SatelliteSupervisor"/> (ISOLATION-PLAN P7): maps each
/// installed legacy plugin to its package (<see cref="PackageResolver"/>), spawns one satellite per
/// package on demand with the resolved role + subscription set, and forwards its LOADPLUGIN/UNLOADPLUGIN
/// to the owning satellite. The supervisor stays a pure process fabric; this class owns all package
/// knowledge (the install-catalog seam allowed by invariant §4) and aggregates the per-satellite roster
/// events into one stream the shell consumes. Implements <see cref="ISatellitePluginChannel"/> so the
/// installer is unaware there are N satellites.
/// </summary>
internal sealed class SatelliteRouter : ISatellitePluginChannel, IAsyncDisposable
{
    // Ensuring a satellite awaits a process handshake and the consumer stand-in load, which is slow; the
    // command channel then connects in the background. This bounds both waits generously.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);

    private readonly SatelliteSupervisor _supervisor;
    private readonly ILogger<SatelliteRouter> _log;

    private readonly object _maps = new();
    private readonly Dictionary<string, SupervisedSatellite> _byPackage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Key, string Dll, string Title)>> _packagePlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _keyToPackage = new(StringComparer.OrdinalIgnoreCase);
    // Serializes the async launch so two concurrent installs of the same package never double-spawn.
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    /// <summary>The roster of every satellite, aggregated: a plugin announced/unloaded on any package's
    /// satellite surfaces here so the shell keeps one flat legacy roster (ISOLATION-PLAN P7 — flat UI).</summary>
    public event Action<SatellitePlugin>? PluginAnnounced;
    public event Action<string>? PluginUnloaded;

    /// <summary>A LOADPLUGIN was just requested for (key, title): the satellite hasn't announced the
    /// plugin yet (that arrives later via <see cref="PluginAnnounced"/>). The shell shows a "loading"
    /// placeholder row so the gap between the install and the announce isn't a silent roster.</summary>
    public event Action<string, string>? PluginLoadPending;

    /// <summary>The load request for this key could not even be dispatched (satellite wouldn't launch or
    /// its command channel never came up) — no <see cref="PluginAnnounced"/> will follow, so the shell
    /// removes the pending placeholder.</summary>
    public event Action<string>? PluginLoadFailed;

    public SatelliteRouter(SatelliteSupervisor supervisor, ILoggerFactory loggerFactory)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _log = loggerFactory.CreateLogger<SatelliteRouter>();
        _supervisor.SatelliteStarted += OnSatelliteStarted;
    }

    public async Task<bool> RequestLoadPluginAsync(string key, string dllPath, string title)
    {
        // The plugin is now in-flight: the satellite loads it asynchronously and announces it later via
        // PluginAnnounced. Surface a pending signal up front so the shell can show a loading placeholder;
        // every early-out below is a load that will never announce, so it fires PluginLoadFailed.
        PluginLoadPending?.Invoke(key, title);

        var d = PackageResolver.Resolve(dllPath, key, title);
        lock (_maps)
        {
            _keyToPackage[key] = d.Package;
            if (!_packagePlugins.TryGetValue(d.Package, out var list))
                _packagePlugins[d.Package] = list = new List<(string, string, string)>();
            list.RemoveAll(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            list.Add((key, dllPath, title));
        }

        SupervisedSatellite sat;
        try { sat = await EnsurePackageAsync(d).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.SatelliteLaunchFailed, ex,
                "Could not ensure satellite for package '{Package}' (plugin '{Key}')", d.Package, key);
            PluginLoadFailed?.Invoke(key);
            return false;
        }

        var host = sat.Host;
        if (host is null) { PluginLoadFailed?.Invoke(key); return false; }
        if (!await host.WaitForCommandChannelAsync(LoadTimeout).ConfigureAwait(false))
        {
            _log.LogWarning(LogEvents.BridgeReaderStopped,
                "Satellite '{Package}' command channel not ready; deferring load of '{Key}'", d.Package, key);
            PluginLoadFailed?.Invoke(key);
            return false;
        }
        var sent = host.RequestLoadPlugin(key, dllPath, title);
        if (!sent) PluginLoadFailed?.Invoke(key);
        return sent;
    }

    public async Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout)
    {
        string? package;
        lock (_maps) _keyToPackage.TryGetValue(key, out package);
        if (package is null) return false;

        SupervisedSatellite? sat;
        lock (_maps) _byPackage.TryGetValue(package, out sat);

        bool ok = false;
        if (sat?.Host is { } host)
            ok = await host.RequestUnloadPluginAsync(key, timeout).ConfigureAwait(false);

        bool packageEmpty;
        lock (_maps)
        {
            _keyToPackage.Remove(key);
            if (_packagePlugins.TryGetValue(package, out var list))
            {
                list.RemoveAll(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                packageEmpty = list.Count == 0;
                if (packageEmpty) _packagePlugins.Remove(package);
            }
            else packageEmpty = true;
            if (packageEmpty) _byPackage.Remove(package);
        }

        // Its last plugin is gone: tear the emptied package satellite down (stop + kill the process).
        if (packageEmpty && sat is not null)
            await _supervisor.StopOneAsync(sat, StopTimeout).ConfigureAwait(false);
        return ok;
    }

    // Return the package's live satellite or launch one. Serialized so concurrent installs of the same
    // package don't race two processes into existence.
    private async Task<SupervisedSatellite> EnsurePackageAsync(PackageDescriptor d)
    {
        lock (_maps)
            if (_byPackage.TryGetValue(d.Package, out var existing))
                return existing;

        await _launchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_maps)
                if (_byPackage.TryGetValue(d.Package, out var existing))
                    return existing;

            var spec = new SatelliteSpec
            {
                Id = d.Package,   // one identity per package -> per-id log category Fct.Satellite.<package>
                Package = d.Package,
                Role = d.Role == SatelliteRole.Producer ? "producer" : "consumer",
                Subscriptions = d.Subscriptions,
            };
            var sat = await _supervisor.LaunchAsync(spec).ConfigureAwait(false);
            lock (_maps) _byPackage[d.Package] = sat;
            return sat;
        }
        finally { _launchGate.Release(); }
    }

    // A satellite (re)started: wire the FRESH SatelliteHost's roster events into the aggregated stream,
    // and on a restart replay the package's LOADPLUGIN commands so the crash-restarted process comes back
    // with its plugin (the satellite re-sends its own SUBSCRIBE on boot).
    private void OnSatelliteStarted(SupervisedSatellite sat, bool isRestart)
    {
        var host = sat.Host;
        if (host is null) return;
        host.PluginAnnounced += p => PluginAnnounced?.Invoke(p);
        host.PluginUnloaded += k => PluginUnloaded?.Invoke(k);
        if (isRestart) _ = ReplayPackageAsync(sat);
    }

    // Fire-and-forget from OnSatelliteStarted on the crash-restart path — so it must swallow nothing
    // silently: any unexpected throw here is caught and logged rather than becoming an unobserved task
    // exception on a recovery path (a crash-looping satellite could otherwise fail to replay in silence).
    private async Task ReplayPackageAsync(SupervisedSatellite sat)
    {
        try
        {
            var host = sat.Host;
            if (host is null) return;
            if (!await host.WaitForCommandChannelAsync(LoadTimeout).ConfigureAwait(false)) return;

            List<(string Key, string Dll, string Title)> loads;
            lock (_maps)
                loads = _packagePlugins.TryGetValue(sat.Package, out var list)
                    ? list.ToList() : new List<(string, string, string)>();

            foreach (var (key, dll, title) in loads)
                host.RequestLoadPlugin(key, dll, title);
            if (loads.Count > 0)
                _log.LogInformation(LogEvents.SatelliteLaunching,
                    "Replayed {Count} plugin(s) onto restarted satellite '{Package}'", loads.Count, sat.Package);
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.SatelliteLaunchFailed, ex,
                "Replay of package '{Package}' onto the restarted satellite failed", sat.Package);
        }
    }

    public Task StopAllAsync(TimeSpan timeout) => _supervisor.StopAllAsync(timeout);

    public async ValueTask DisposeAsync()
    {
        _supervisor.SatelliteStarted -= OnSatelliteStarted;
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _launchGate.Dispose();
    }
}
