using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Fct.App.Plugins;

/// <summary>
/// Loads native plugins at host start and unloads them at shutdown (mirrors
/// <c>SatelliteLifetime</c>). Registered before the dev event source so plugins are subscribed before
/// any synthetic events fire.
/// </summary>
internal sealed class PluginLifetime : IHostedService
{
    private readonly PluginManager _plugins;

    public PluginLifetime(PluginManager plugins) => _plugins = plugins;

    public Task StartAsync(CancellationToken cancellationToken) => _plugins.LoadAllAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _plugins.UnloadAllAsync();
}
