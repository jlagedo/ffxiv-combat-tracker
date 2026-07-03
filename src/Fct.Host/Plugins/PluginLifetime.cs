using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Fct.Host.Plugins;

/// <summary>
/// Loads plugins at host start and unloads them at shutdown (mirrors <c>SatelliteLifetime</c>).
/// Startup is registry-driven: pending file-deletes are cleared, the persisted user-installed net10
/// plugins load in-process, then the build-staged sample folder is scanned. Persisted real-legacy
/// plugins are replayed to the satellite once it is online (from the shell), not here.
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
        await _plugins.LoadAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _plugins.UnloadAllAsync();
}
