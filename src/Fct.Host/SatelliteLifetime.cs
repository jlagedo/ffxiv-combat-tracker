using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Fct.Host;

// Drains every satellite during host shutdown — asks each to DeInit its plugins (so each persists
// its state) and exit — before the process ends and the kill-on-close job reaps them. Satellites are
// launched on demand by the router, so StartAsync is a no-op; StopAsync runs inside host.StopAsync()
// in Program.Main's finally, while the satellites are still alive.
internal sealed class SatelliteLifetime : IHostedService
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private readonly SatelliteRouter _router;

    public SatelliteLifetime(SatelliteRouter router) => _router = router;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _router.StopAllAsync(ShutdownTimeout);
}
