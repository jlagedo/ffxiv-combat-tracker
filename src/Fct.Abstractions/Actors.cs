using System.Collections.Generic;

namespace Fct.Abstractions
{
    /// <summary>
    /// Actor classification. The FFXIV SDK ships only a raw <c>byte</c> (1=player, 2=npc); this is
    /// the typed form, defined by us.
    /// </summary>
    public enum ActorKind : byte { Unknown = 0, Player = 1, Npc = 2, Pet = 3, Object = 4 }

    /// <summary>Party membership of an actor.</summary>
    public enum PartyMembership : byte { None = 0, Party = 1, Alliance = 2 }

    /// <summary>World position + facing. Heading is radians, as the game reports it.</summary>
    public readonly record struct Position(float X, float Y, float Z, float Heading);

    /// <summary>An in-progress cast. <see cref="ActionName"/> is resolved when the catalog has it.</summary>
    public sealed record CastInfo(uint ActionId, string? ActionName, uint TargetId, float Progress, float Duration);

    /// <summary>
    /// A status effect on an actor. Stacks are the count (the SDK encodes this in <c>BuffExtra</c>);
    /// <see cref="RemainingSeconds"/> is the remaining duration; <see cref="SourceActorId"/> is who
    /// applied it.
    /// </summary>
    public sealed record StatusEffect(ushort StatusId, string? Name, ushort Stacks, float RemainingSeconds, uint SourceActorId);

    /// <summary>One entry of an actor's enmity/hate table. Not provided by the FFXIV SDK; best-effort.</summary>
    public sealed record EnmityEntry(uint ActorId, string Name, uint Enmity, float HateRate);

    /// <summary>
    /// An immutable point-in-time view of a game actor — a deliberate SUPERSET of the FFXIV SDK
    /// <c>Combatant</c> model, adding the fields the heaviest consumers (Hojoring/UltraScouter)
    /// otherwise source from Sharlayan/OverlayPlugin: <see cref="Statuses"/>, <see cref="Enmity"/>,
    /// <see cref="TargetOfTargetId"/>, and a reliable <see cref="InCombat"/> flag. Fields beyond the
    /// FFXIV plugin's reach are best-effort by data source.
    /// </summary>
    public sealed record Actor(
        uint Id,
        uint OwnerId,
        ActorKind Kind,
        int Job,
        int Level,
        string Name,
        uint Hp, uint MaxHp,
        uint Mp, uint MaxMp,
        CastInfo? Cast,
        Position Position,
        uint WorldId, string WorldName,
        uint BNpcNameId, uint BNpcId,
        uint TargetId, uint TargetOfTargetId,
        byte EffectiveDistance,
        PartyMembership Party,
        bool InCombat,
        IReadOnlyList<StatusEffect> Statuses,
        IReadOnlyList<EnmityEntry> Enmity);
}
