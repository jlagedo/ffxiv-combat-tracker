using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Fct.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>
/// Minimal real <see cref="IEncounterService"/> for slice A+B: it holds combat state
/// (<see cref="InCombat"/>, <see cref="Active"/>/<see cref="Last"/>) that a plugin can drive and read,
/// and renders <see cref="ExportText"/>. Live aggregation — building
/// <see cref="EncounterSnapshot"/>/<see cref="CombatantMetrics"/> from real swings — arrives with the
/// bridge/parser (pieces C/D); until then the metrics are whatever a driver supplies.
/// </summary>
internal sealed class EncounterService : IEncounterService
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly List<string> _log = new();

    public EncounterService(IClock clock) => _clock = clock;

    public bool InCombat { get; private set; }
    public EncounterSnapshot? Active { get; private set; }
    public EncounterSnapshot? Last { get; private set; }

    public void StartCombat(string? title = null, string? zone = null)
    {
        lock (_gate)
        {
            InCombat = true;
            _log.Clear();
            Active = new EncounterSnapshot(
                title ?? string.Empty,
                _clock.LocalNow,
                TimeSpan.Zero,
                Active: true,
                Dps: 0,
                Damage: 0,
                Combatants: Array.Empty<CombatantMetrics>())
            {
                Zone = zone ?? title,
            };
        }
    }

    public void EndCombat(bool export = false)
    {
        lock (_gate)
        {
            InCombat = false;
            if (Active is not null)
            {
                Last = Active with { Active = false, Duration = _clock.LocalNow - Active.Start };
                Active = null;
            }
        }
    }

    public string ExportText(EncounterSnapshot encounter, EncounterExportFormat format)
    {
        if (encounter is null) throw new ArgumentNullException(nameof(encounter));
        return format switch
        {
            EncounterExportFormat.Json => Json(encounter),
            EncounterExportFormat.Markdown => Markdown(encounter),
            _ => Plain(encounter),
        };
    }

    public void AppendLogLine(string line)
    {
        lock (_gate) _log.Add(line ?? string.Empty);
    }

    private static string Plain(EncounterSnapshot e)
        => $"{e.Title} — {e.Dps.ToString("0", CultureInfo.InvariantCulture)} DPS, {e.Damage} damage over {e.Duration:mm\\:ss}";

    private static string Markdown(EncounterSnapshot e)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(e.Title).Append("** — ")
          .Append(e.Dps.ToString("0", CultureInfo.InvariantCulture)).Append(" DPS\n");
        foreach (var c in e.Combatants)
            sb.Append("- ").Append(c.Name).Append(": ")
              .Append(c.EncDps.ToString("0", CultureInfo.InvariantCulture)).Append(" dps\n");
        return sb.ToString();
    }

    private static string Json(EncounterSnapshot e)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            e.Title,
            e.Zone,
            e.Dps,
            e.Damage,
            DurationSeconds = e.Duration.TotalSeconds,
            Combatants = e.Combatants,
        });
}
