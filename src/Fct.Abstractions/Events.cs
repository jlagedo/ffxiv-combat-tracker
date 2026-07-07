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

    /// <summary>
    /// A full-fidelity ACT <c>MasterSwing</c> — every field the aggregation engine needs to reproduce
    /// ACT's numbers bit-for-bit, carried verbatim so the modern engine folds the same swings the net48
    /// engine sees. <see cref="GameEvent.Timestamp"/> is the swing time; <see cref="Damage"/> is the raw
    /// signed <c>Dnum</c> value, keeping ACT's sentinels (−1 Miss, −10 Death, 0 NoDamage). The engine's
    /// math reads only the scalar fields; <see cref="Tags"/> are pass-through for downstream consumers
    /// (the one engine-relevant key is <c>"Job"</c>). Values in <see cref="Tags"/> are scalars only
    /// (string / boxed double / boxed uint).
    /// </summary>
    public sealed record CombatSwing(
        long Sequence, DateTimeOffset Timestamp,
        int SwingType, bool Critical, string Special, long Damage, int TimeSorter,
        string AttackType, string Attacker, string DamageType, string Victim,
        IReadOnlyDictionary<string, object> Tags)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// A request to open (or continue) an encounter — ACT's <c>SetEncounter(time, attacker, victim)</c>,
    /// which the parser issues per hostile action. The engine's lifecycle auto-starts on the first one.
    /// </summary>
    public sealed record SetEncounterRequested(long Sequence, DateTimeOffset Timestamp, string Attacker, string Victim)
        : GameEvent(Sequence, Timestamp);

    /// <summary>ACT's <c>ChangeZone(name)</c>. Distinct from the SDK <see cref="ZoneChanged"/> (id + name).</summary>
    public sealed record ZoneChangeRequested(long Sequence, DateTimeOffset Timestamp, string ZoneName)
        : GameEvent(Sequence, Timestamp);

    /// <summary>ACT's <c>EndCombat(export)</c> — the parser issues it on zone change; idle-end is the engine's own.</summary>
    public sealed record EndCombatRequested(long Sequence, DateTimeOffset Timestamp, bool Export)
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

    /// <summary>
    /// The party/alliance roster changed (PartyList). <paramref name="PartySize"/> is the SDK's
    /// <c>PartyListChanged(partyList, partySize)</c> second argument — in alliance content it is the
    /// player's 8-person party size, distinct from (and smaller than) <see cref="Members"/>'s up-to-24
    /// visible roster. Defaulted for source compatibility with existing call sites; not yet forwarded
    /// or folded anywhere (see docs/PIPELINE-COMPLETENESS-PLAN.md P1.6/P3).
    /// </summary>
    public sealed record PartyChanged(long Sequence, DateTimeOffset Timestamp, IReadOnlyList<uint> Members, int PartySize = 0)
        : GameEvent(Sequence, Timestamp);

    /// <summary>The local player identity changed (ChangePrimaryPlayer).</summary>
    public sealed record PrimaryPlayerChanged(long Sequence, DateTimeOffset Timestamp, uint ActorId, string Name)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// A raw on-wire network packet (the SDK <c>NetworkReceived</c>/<c>NetworkSent</c> firehose). The
    /// opcode-bearing escape hatch OverlayPlugin's network processors consume; gated off the bus by
    /// <see cref="GameEventFilter.IncludeRawPackets"/> and republished to plugins through
    /// <see cref="IRawPacketSource"/>. The host never decodes it — bytes cross verbatim.
    /// </summary>
    public sealed record RawPacketReceived(long Sequence, DateTimeOffset Timestamp, string Connection, long Epoch, PacketDirection Direction, byte[] Bytes)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// A fixed-rate full combatant roster with fresh HP/position/party — the poll surface
    /// OverlayPlugin/Hojoring consume through <c>IDataRepository.GetCombatantList()</c>. Unlike the
    /// incremental <see cref="CombatantAdded"/> (add-time HP, no position), this carries the whole list
    /// at snapshot time so a consumer satellite mirrors the parser's live repository.
    /// </summary>
    public sealed record RepositorySnapshot(long Sequence, DateTimeOffset Timestamp, IReadOnlyList<Actor> Combatants)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// An id→name resource dictionary forwarded once per <see cref="ResourceKind"/> (the parser's
    /// <c>GetResourceDictionary</c> tables), so a consumer satellite can serve those reads without a parser.
    /// </summary>
    public sealed record ResourceDictionaryForwarded(long Sequence, DateTimeOffset Timestamp, ResourceKind Kind, IReadOnlyDictionary<uint, string> Entries)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// The FFXIV game process id, forwarded so a consumer satellite materializes
    /// <c>GetCurrentFFXIVProcess()</c> locally via <c>Process.GetProcessById</c>. <see cref="Pid"/> 0 =
    /// no live game process.
    /// </summary>
    public sealed record GameProcessChanged(long Sequence, DateTimeOffset Timestamp, int Pid)
        : GameEvent(Sequence, Timestamp);

    /// <summary>
    /// One-shot environment state (the SDK's <c>GetGameVersion</c>/<c>GetSelectedLanguageID</c>/
    /// <c>GetGameRegion</c>/<c>GetServerTimestamp</c>/<c>IsChatLogAvailable</c>), forwarded so a consumer
    /// satellite mirrors the parser's environment without a live game process. <see cref="GameVersion"/>
    /// is <c>""</c> for an unknown version, never a placeholder; <see cref="ServerClockOffset"/> is
    /// <see cref="TimeSpan.Zero"/> when the producer has no live memory-scanned server time (see
    /// docs/PIPELINE-COMPLETENESS-PLAN.md P0.3/P3.3). Not yet produced or folded anywhere (see P3).
    /// </summary>
    public sealed record SessionStateChanged(long Sequence, DateTimeOffset Timestamp, string GameVersion, GameLanguage Language, GameRegion Region, TimeSpan ServerClockOffset, bool IsChatLogAvailable)
        : GameEvent(Sequence, Timestamp);
}
