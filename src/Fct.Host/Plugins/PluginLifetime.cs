using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Fct.Host.Plugins;

/// <summary>
/// Loads plugins at host start and unloads them at shutdown (mirrors <c>SatelliteLifetime</c>).
/// Startup is registry-driven only: pending file-deletes are cleared, then the persisted
/// user-installed net10 plugins load in-process — the install catalog is the sole source of truth,
/// so nothing is auto-discovered from disk. Persisted real-legacy plugins are replayed to the
/// satellite once it is online (from the shell), not here.
/// </summary>
internal sealed class PluginLifetime : IHostedService
{
    private readonly PluginManager _plugins;
    private readonly PluginInstaller _installer;

    public PluginLifetime(PluginManager plugins, PluginInstaller installer)
    {
        _plugins = plugins;
        _installer = installer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _installer.LoadPersistedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _plugins.UnloadAllAsync();
}
