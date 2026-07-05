using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Advanced_Combat_Tracker;
using Fct.Abstractions;

namespace Fct.Engine
{
    /// <summary>
    /// The <see cref="IEncounterService"/> face over <see cref="ModernEncounterEngine"/>: it projects
    /// the engine's live <see cref="EncounterData"/> into immutable <see cref="EncounterSnapshot"/>s the
    /// UI polls (<see cref="Active"/>/<see cref="Last"/>), advances the idle clock so combat auto-ends
    /// during a lull (matching ACT's log-time watchdog), and lets an in-process driver (the compat shim)
    /// open/close combat on the same engine. Projections are computed on a tick under the engine's gate,
    /// then published to volatile fields the UI reads lock-free.
    /// </summary>
    public sealed class EngineEncounterService : IEncounterService, IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

        private readonly ModernEncounterEngine _engine;
        private readonly IClock _clock;
        private readonly List<string> _log = new();
        private readonly Timer _tick;

        private volatile EncounterSnapshot? _active;
        private volatile EncounterSnapshot? _last;

        public EngineEncounterService(ModernEncounterEngine engine, IClock clock)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            // Capture the final numbers the instant combat ends (idle-out or explicit), before the next
            // encounter reuses the graph. Fires under the engine gate (bus pump or our tick) — the lock
            // below is re-entrant on that thread.
            _engine.Lifecycle.CombatEnded = enc =>
            {
                lock (_engine.Gate) _last = EncounterProjector.Project(enc);
            };

            _tick = new Timer(_ => Tick(), null, TickInterval, TickInterval);
        }

        public bool InCombat => _engine.Lifecycle.InCombat;
        public EncounterSnapshot? Active => _active;
        public EncounterSnapshot? Last => _last;

        // In-process driver path (the compat shim's SetEncounter/EndCombat) — drives the SAME engine the
        // bridge feeds, so native and legacy sources converge on one source of truth.
        public void StartCombat(string? title = null, string? zone = null)
        {
            lock (_engine.Gate)
            {
                _log.Clear();
                if (zone != null || title != null) _engine.Lifecycle.ChangeZone(zone ?? title ?? string.Empty);
                _engine.Lifecycle.SetEncounter(_clock.LocalNow.LocalDateTime, title ?? string.Empty, title ?? string.Empty);
            }
        }

        public void EndCombat(bool export = false)
        {
            lock (_engine.Gate) _engine.Lifecycle.EndCombat(export);
        }

        public void AppendLogLine(string line)
        {
            lock (_log) _log.Add(line ?? string.Empty);
        }

        // Advance the idle clock by real elapsed time and republish the active projection. During combat
        // swings advance the clock to their own (log) time; during a lull this keeps it moving so the 6 s
        // idle-end fires exactly as ACT's watchdog does.
        internal void Tick()
        {
            try
            {
                lock (_engine.Gate)
                {
                    _engine.Lifecycle.AdvanceClock(_clock.LocalNow.LocalDateTime);
                    var enc = _engine.Lifecycle.ActiveZone.ActiveEncounter;
                    _active = _engine.Lifecycle.InCombat && enc != null && enc.Active
                        ? EncounterProjector.Project(enc)
                        : null;
                }
            }
            catch
            {
                // A projection hiccup must never kill the tick loop; the next tick recovers.
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

        public void Dispose() => _tick.Dispose();
    }
}
