using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Hosting;

/// <summary>
/// Diagnostic: periodically logs the modern encounter engine's live output (the <see cref="IEncounterService"/>
/// projection) so the net10 aggregation is observable in the unified host log and directly comparable to the
/// satellite's re-emitted <c>[Capture]</c> line. Quiet out of combat once steady; emits on change or while in
/// combat. Read-only — never drives the engine.
/// </summary>
internal sealed class EncounterHeartbeat : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IEncounterService _encounters;
    private readonly ILogger<EncounterHeartbeat> _log;
    private Timer? _timer;
    private string _last = string.Empty;

    public EncounterHeartbeat(IEncounterService encounters, ILogger<EncounterHeartbeat> log)
    {
        _encounters = encounters ?? throw new ArgumentNullException(nameof(encounters));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Beat(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void Beat()
    {
        try
        {
            var snap = _encounters.Active ?? _encounters.Last;
            if (snap is null) return;

            var top = snap.Combatants.Count > 0
                ? snap.Combatants.OrderByDescending(c => c.EncDps).First()
                : null;
            var line = $"[Engine] InCombat={_encounters.InCombat} EncDmg={snap.Damage} Dur={snap.Duration.TotalSeconds:0}s " +
                       $"EncDPS={snap.Dps:0.00} combatants={snap.Combatants.Count} " +
                       $"top='{top?.Name ?? "-"}' encdps={top?.EncDps ?? 0:0.00} crit%={top?.CritPercent ?? 0:0}%";

            // Only emit when something changed or combat is live, so an idle wait doesn't spam.
            if (line == _last && !_encounters.InCombat) return;
            _last = line;
            _log.LogInformation(LogEvents.EncounterHeartbeat, line);
        }
        catch (Exception ex)
        {
            _log.LogError(LogEvents.EncounterHeartbeat, ex, "[Engine] heartbeat failed");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
