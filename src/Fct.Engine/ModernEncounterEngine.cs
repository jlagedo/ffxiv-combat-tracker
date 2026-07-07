using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.Engine
{
    /// <summary>
    /// The modern host-side ACT aggregation engine — the single source of truth for encounter
    /// calculations. Runs the shared <see cref="EncounterLifecycle"/> + <see cref="EncounterData"/>
    /// graph (identical to the net48 engine) fed by typed <see cref="CombatSwing"/> and encounter
    /// lifecycle events off the <see cref="IGameEventStream"/> — from the net48 bridge today, or a
    /// native parser later, with no code change. Subscribes only to the low-volume swing/lifecycle
    /// events (not the raw-log/packet firehose) so the aggregation feed is never starved by backpressure.
    /// </summary>
    public sealed class ModernEncounterEngine : IHostedService, IDisposable
    {
        // The exact-match feed: full-fidelity swings + the encounter lifecycle requests. Raw log lines
        // and the packet firehose are excluded — the idle clock is advanced by the service's tick.
        private static readonly GameEventFilter Feed = new(
            new[]
            {
                typeof(CombatSwing), typeof(SetEncounterRequested),
                typeof(ZoneChangeRequested), typeof(EndCombatRequested),
            },
            IncludeRawLogLines: false);

        private readonly IGameSession _session;
        private readonly ILogger<ModernEncounterEngine> _log;
        private readonly EncounterLifecycle _lifecycle = new();
        private IDisposable? _subscription;

        // Guards the lifecycle + encounter graph: folds happen on the single-reader bus pump; the
        // encounter service projects/advances the clock on its tick. Both take this.
        internal object Gate { get; } = new();

        public ModernEncounterEngine(IGameSession session, ILogger<ModernEncounterEngine> log)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            // Register the ExportVariables + install the FFXIV damage-type routing tables the engine
            // aggregates through (shared with the net48 replay engine via EngineTables).
            EngineTables.Install();
            // Wire the LastNDPS formatters' "now" to this engine's own lifecycle clock (advanced per
            // folded swing, EncounterLifecycle.AddCombatAction) — never wall-clock time. Mirrors the
            // facade-owned-state pattern each ACT facade already uses for charName/blockIsHit/restrictToAll.
            AggregationGlobals.LastKnownTimeAccessor = () => _lifecycle.LastKnownTime;
        }

        /// <summary>The shared state machine. Callers must hold <see cref="Gate"/> when driving it.</summary>
        public EncounterLifecycle Lifecycle => _lifecycle;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _subscription = _session.Events.Subscribe(Feed, OnEvent);
            _log.LogDebug("Modern encounter engine subscribed to the swing/lifecycle feed.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _subscription?.Dispose();
            _subscription = null;
            return Task.CompletedTask;
        }

        private void OnEvent(GameEvent e)
        {
            lock (Gate)
            {
                switch (e)
                {
                    case CombatSwing s:
                        _lifecycle.AddCombatAction(ToMasterSwing(s));
                        break;
                    case SetEncounterRequested r:
                        _lifecycle.SetEncounter(r.Timestamp.LocalDateTime, r.Attacker, r.Victim);
                        break;
                    case ZoneChangeRequested z:
                        _lifecycle.ChangeZone(z.ZoneName);
                        break;
                    case EndCombatRequested c:
                        _lifecycle.EndCombat(c.Export);
                        break;
                }
            }
        }

        // Rebuild the ACT MasterSwing from the wire record. Same-machine bridge, so the local wall value
        // round-trips; Damage carries ACT's raw Dnum sentinels (−1 Miss / −10 Death / 0 NoDamage).
        private static MasterSwing ToMasterSwing(CombatSwing s)
        {
            var ms = new MasterSwing(s.SwingType, s.Critical, s.Special, new Dnum(s.Damage),
                s.Timestamp.LocalDateTime, s.TimeSorter, s.AttackType, s.Attacker, s.DamageType, s.Victim);
            if (s.Tags.Count > 0)
                ms.Tags = new Dictionary<string, object>(s.Tags);
            return ms;
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
