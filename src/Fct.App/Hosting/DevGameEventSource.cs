#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.App.Hosting;

/// <summary>
/// TEMPORARY dev-only game-data source (Debug builds only). Pushes a couple of synthetic events
/// through the bus producer seam so a loaded plugin has something to react to before the real
/// net48→net10 bridge forwarder (piece C) exists. Delete this once piece C lands.
/// </summary>
internal sealed class DevGameEventSource : IHostedService, IDisposable
{
    private readonly IGameEventSink _sink;
    private readonly IClock _clock;
    private readonly ILogger<DevGameEventSource> _log;
    private Timer? _timer;
    private int _tick;

    public DevGameEventSource(IGameEventSink sink, IClock clock, ILogger<DevGameEventSource> log)
    {
        _sink = sink;
        _clock = clock;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation(LogEvents.HostStarted, "DevGameEventSource emitting synthetic events (Debug placeholder for piece C)");
        // Fire the first batch shortly after startup (plugins have loaded), then heartbeat.
        _timer = new Timer(Emit, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    private void Emit(object? _)
    {
        var tick = Interlocked.Increment(ref _tick);
        _sink.Emit(new ZoneChanged(_sink.NextSequence(), _clock.LocalNow, (uint)(130 + tick), $"Dev Zone {tick}"));
        _sink.Emit(new RawLogLine(_sink.NextSequence(), _clock.LocalNow, LogMessageType.ChatLog,
            $"00|{_clock.LocalNow:O}|0038||dev heartbeat {tick}|", $"dev heartbeat {tick}"));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
#endif
