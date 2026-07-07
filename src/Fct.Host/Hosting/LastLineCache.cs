using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fct.Host.Hosting;

/// <summary>
/// The one-shot line-state set (plan §3 — the single literal for this set; do not redeclare it
/// elsewhere). These are the <see cref="LogMessageType"/> values whose LATEST instance carries
/// environment/zone/map/player/stats state a late-joining <c>rawlog</c> subscriber needs to converge
/// on, as opposed to a transient combat line that only matters live. <see cref="EmissionOrder"/> is
/// ACT's own emission order (version/settings/process, then zone, player, map, stats) — P4.2 replays
/// <see cref="LastLineCache.Snapshot"/> as-is in this order, no re-sorting needed.
/// </summary>
internal static class OneShotLineTypes
{
    /// <summary>ACT emission order: 253 Version, 249 Settings, 250 Process, 01 Territory (the plan's
    /// "ChangeZone" shorthand — see <see cref="LogMessageType.Territory"/>), 02 ChangePrimaryPlayer,
    /// 40 ChangeMap, 12 PlayerStats.</summary>
    public static readonly IReadOnlyList<LogMessageType> EmissionOrder = new[]
    {
        LogMessageType.Version,
        LogMessageType.Settings,
        LogMessageType.Process,
        LogMessageType.Territory,
        LogMessageType.ChangePrimaryPlayer,
        LogMessageType.ChangeMap,
        LogMessageType.PlayerStats,
    };

    private static readonly HashSet<LogMessageType> Set = new(EmissionOrder);

    /// <summary>True when <paramref name="type"/> is a member of the one-shot set.</summary>
    public static bool Contains(LogMessageType type) => Set.Contains(type);
}

/// <summary>The read API <see cref="SatelliteHost"/> (P4.2) primes a late-joining <c>rawlog</c>
/// subscriber from.</summary>
internal interface ILastLineCache
{
    /// <summary>The cached last-seen verbatim <see cref="RawLogLine"/> for each one-shot type observed
    /// so far, in <see cref="OneShotLineTypes.EmissionOrder"/>. A type never observed is simply absent
    /// (never a synthesized placeholder) — the caller decides the fresh-boot fallback.</summary>
    IReadOnlyList<RawLogLine> Snapshot();
}

/// <summary>
/// Caches the last-seen verbatim <see cref="RawLogLine"/> per <see cref="LogMessageType"/> in the
/// one-shot set (<see cref="OneShotLineTypes"/>), fed from the rawlog fan-in on the live event bus.
/// Zero decoding — keyed purely on the frame's already-typed <see cref="RawLogLine.Type"/> field, the
/// verbatim line record is stored as-is. Lives alongside <see cref="GameSnapshotAggregator"/>: both are
/// host-internal session state a late-joining satellite reads to converge (constraint 2 — the host
/// pipe is the source of truth). Host-internal by design (constraint 1/3): a native plugin never reads
/// this cache — it subscribes <see cref="RawLogLine"/> on the bus directly and sees every line live;
/// this cache exists solely to replay history to a subscriber that joins after the fact.
/// </summary>
internal sealed class LastLineCache : IHostedService, ILastLineCache
{
    // Matches ONLY RawLogLine events. GameEventBus.Matches checks IncludeRawLogLines first and returns
    // immediately for a RawLogLine, so Types is irrelevant on that path; for every other event type,
    // Types must be non-empty and contain nothing assignable from it, or an empty/null Types list would
    // match everything (see GameEventBus.Matches / StreamCatalog's own doc note on that pitfall). Naming
    // RawLogLine itself as the (sole, never-matched-by-anything-else) type starves out all typed events.
    private static readonly GameEventFilter RawLogOnlyFilter =
        new(new[] { typeof(RawLogLine) }, IncludeRawLogLines: true, IncludeRawPackets: false);

    private readonly GameEventBus _bus;
    private readonly ILogger<LastLineCache> _log;
    private readonly object _gate = new();
    private readonly Dictionary<LogMessageType, RawLogLine> _lastByType = new();
    private IDisposable? _subscription;

    public LastLineCache(GameEventBus bus, ILogger<LastLineCache> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(RawLogOnlyFilter, OnEvent);
        _log.LogDebug("Last-line cache subscribed to the raw log-line bus stream.");
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
        if (e is not RawLogLine line) return;
        if (!OneShotLineTypes.Contains(line.Type)) return;

        lock (_gate) _lastByType[line.Type] = line;
    }

    public IReadOnlyList<RawLogLine> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<RawLogLine>(OneShotLineTypes.EmissionOrder.Count);
            foreach (var type in OneShotLineTypes.EmissionOrder)
                if (_lastByType.TryGetValue(type, out var line))
                    result.Add(line);
            return result;
        }
    }
}
