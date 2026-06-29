using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Fct.App;

// Drains the satellite during host shutdown — asks it to DeInit its plugins (so each persists
// its state) and exit — before the process ends and the kill-on-close job reaps it. The
// satellite is launched by MainWindow, so StartAsync is a no-op; StopAsync runs inside
// host.StopAsync() in Program.Main's finally, while the satellite is still alive.
internal sealed class SatelliteLifetime : IHostedService
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private readonly SatelliteHost _satellite;

    public SatelliteLifetime(SatelliteHost satellite) => _satellite = satellite;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _satellite.ShutdownAsync(ShutdownTimeout);
}
