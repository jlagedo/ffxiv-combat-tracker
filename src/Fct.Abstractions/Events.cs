using System;
using System.Collections.Generic;

namespace Fct.Abstractions
{
    /// <summary>
    /// Base of the typed game-event hierarchy. Consumers switch on the concrete record type and
    /// ignore unknown ones, so new event types are additive (never a breaking contract change).
    /// <see cref="Sequence"/> is a monotonic per-session ordinal; <see cref="Timestamp"/> is server time.
    /// </summary>
    public abstract record GameEvent(long Sequence, DateTimeOffset Timestamp);

    /// <summary>A lightweight actor reference embedded in events (id + name; no full snapshot).</summary>
    public readonly record struct ActorRef(uint Id, string Name);

    /// <summary>Per-target outcome flags on an <see cref="ActionEffect"/>.</summary>
    [Flags]
    public enum EffectFlags
    {
        None = 0,
        Critical = 1,
        DirectHit = 2,
        Dot = 4,
        Hot = 8,
        Blocked = 16,
        Parried = 32,
        Miss = 64,
    }

    /// <summary>One target's result within an <see cref="ActionEffect"/> (damage/heal amount + flags).</summary>
    public sealed record EffectTarget(ActorRef Target, long Amount, EffectFlags Flags);

    /// <summary>
    /// The raw FFXIV log line — the migration lifeline for regex-based engines (Triggernometry,
    /// cactbot). Carried alongside the typed events below; both are produced from one decoded packet.
    /// </summary>
    public sealed record RawLogLine(long Sequence, DateTimeOffset Timestamp, LogMessageType Type, string Line, string OriginalLine)
        : GameEvent(Sequence, Timestamp);

    /// <summary>An action resolving on one or more targets (covers ActionEffect/AOEActionEffect/DoTHoT).</summary>
    public sealed record ActionEffect(long Sequence, DateTimeOffset Timestamp, ActorRef Source, uint ActionId, string? ActionName, IReadOnlyList<EffectTarget> Targets)
        : GameEvent(Sequence, Timestamp);

    /// <summary>An actor begins casting (StartsCasting).</summary>
    public sealed record CastStarted(long Sequence, DateTimeOffset Timestamp, ActorRef Source, uint ActionId, ActorRef Target, float DurationSeconds)
        : GameEvent(Sequence, Timestamp);

    /// <summary>An in-progress cast is interrupted/cancelled (CancelAction).</summary>
    public sealed record CastCancelled(long Sequence, DateTimeOffset Timestamp, ActorRef Source, uint ActionId)
        : GameEvent(Sequence, Timestamp);

    /// <summary>A status/buff is applied (StatusAdd).</summary>
    public sealed record StatusApplied(long Sequence, DateTimeOffset Timestamp, ActorRef Source, ActorRef Target, ushort StatusId, ushort Stacks, float DurationSeconds)
        : GameEvent(Sequence, Timestamp);

    /// <summary>A status/buff falls off (StatusRemove).</summary>
    public sealed record StatusRemoved(long Sequence, DateTimeOffset Timestamp, ActorRef Source, ActorRef Target, ushort StatusId)
        : GameEvent(Sequence, Timestamp);

    /// <summary>An actor dies (Death). <see cref="Killer"/> is null when unknown.</summary>
    public sealed record DeathOccurred(long Sequence, DateTimeOffset Timestamp, ActorRef Victim, ActorRef? Killer)
        : GameEvent(Sequence, Timestamp);

    /// <summary>A combatant enters tracking (AddCombatant).</summary>
    public sealed record CombatantAdded(long Sequence, DateTimeOffset Timestamp, Actor Combatant)
        : GameEvent(Sequence, Timestamp);

    /// <summary>A combatant leaves tracking (RemoveCombatant).</summary>
    public sealed record CombatantRemoved(long Sequence, DateTimeOffset Timestamp, uint ActorId)
        : GameEvent(Sequence, Timestamp);

    /// <summary>An actor's HP changed (UpdateHp).</summary>
    public sealed record HpUpdated(long Sequence, DateTimeOffset Timestamp, uint ActorId, uint Hp, uint MaxHp)
        : GameEvent(Sequence, Timestamp);

    /// <summary>The player changed zone (Territory/ChangeMap).</summary>
    public sealed record ZoneChanged(long Sequence, DateTimeOffset Timestamp, uint ZoneId, string ZoneName)
        : GameEvent(Sequence, Timestamp);

    /// <summary>The party/alliance roster changed (PartyList).</summary>
    public sealed record PartyChanged(long Sequence, DateTimeOffset Timestamp, IReadOnlyList<uint> Members)
        : GameEvent(Sequence, Timestamp);

    /// <summary>The local player identity changed (ChangePrimaryPlayer).</summary>
    public sealed record PrimaryPlayerChanged(long Sequence, DateTimeOffset Timestamp, uint ActorId, string Name)
        : GameEvent(Sequence, Timestamp);
}
