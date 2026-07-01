using System;
using System.Linq;
using Fct.Abstractions;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;

namespace Fct.Compat.Shim;

/// <summary>
/// Projects the modern <see cref="Actor"/>/<see cref="StatusEffect"/> onto the FFXIV SDK
/// <see cref="SdkModels.Combatant"/>/<see cref="SdkModels.NetworkBuff"/>/<see cref="SdkModels.Player"/>
/// a recompiled plugin binds against (the pull-state shapes <c>IDataRepository</c> returns). Inverse of
/// the satellite's <c>BridgeForwarder.ToActor</c> — the same field pairs read backwards, so a value
/// round-trips SDK→modern→SDK. Pure; the caller decides when to project.
/// </summary>
public static class CombatantProjector
{
    /// <summary>Project a modern actor into the SDK combatant, including its status list as NetworkBuffs.</summary>
    public static SdkModels.Combatant ToCombatant(Actor a)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));

        return new SdkModels.Combatant
        {
            ID = a.Id,
            OwnerID = a.OwnerId,
            type = MapType(a.Kind),
            Job = a.Job,
            Level = a.Level,
            Name = a.Name,
            CurrentHP = a.Hp, MaxHP = a.MaxHp,
            CurrentMP = a.Mp, MaxMP = a.MaxMp,
            CurrentCP = a.CurrentCp ?? 0, MaxCP = a.MaxCp ?? 0,
            CurrentGP = a.CurrentGp ?? 0, MaxGP = a.MaxGp ?? 0,
            PosX = a.Position.X, PosY = a.Position.Y, PosZ = a.Position.Z, Heading = a.Position.Heading,
            CurrentWorldID = a.CurrentWorldId ?? a.WorldId,
            WorldID = a.WorldId, WorldName = a.WorldName,
            BNpcNameID = a.BNpcNameId, BNpcID = a.BNpcId,
            TargetID = a.TargetId,
            EffectiveDistance = a.EffectiveDistance,
            PartyType = MapParty(a.Party),
            Order = a.Order ?? 0,
            NetworkBuffs = a.Statuses.Select(s => ToNetworkBuff(s, a.Id)).ToArray(),
        };
    }

    /// <summary>Project a modern status onto the SDK NetworkBuff carried on the owning combatant.</summary>
    public static SdkModels.NetworkBuff ToNetworkBuff(StatusEffect s, uint targetActorId)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        return new SdkModels.NetworkBuff
        {
            BuffID = s.StatusId,
            BuffExtra = s.Stacks,
            Duration = s.RemainingSeconds,
            Timestamp = s.AppliedAt?.UtcDateTime ?? default,
            ActorID = s.SourceActorId,
            // The modern StatusEffect carries no actor/target names (derivable from ids); left empty.
            ActorName = string.Empty,
            TargetID = targetActorId,
            TargetName = string.Empty,
            RefreshPending = s.RefreshPending,
        };
    }

    /// <summary>
    /// Project the player's job block. The modern model carries no attribute stats (Str/Dex/Crit/…) —
    /// the bridge forwards none and <c>PlayerStatsChanged</c> has no source — so only <c>JobID</c> is
    /// populated; the rest stay 0 (an inherent model limit, not a stub).
    /// </summary>
    public static SdkModels.Player ToPlayer(Actor? player)
        => new SdkModels.Player { JobID = player is null ? 0u : (uint)player.Job };

    // Inverse of BridgeForwarder.MapKind: pets report as npc type (2) but carry a non-zero OwnerID.
    private static byte MapType(ActorKind kind) => kind switch
    {
        ActorKind.Player => 1,
        ActorKind.Npc => 2,
        ActorKind.Pet => 2,
        _ => 0,
    };

    private static SdkModels.PartyType MapParty(PartyMembership p) => p switch
    {
        PartyMembership.Party => SdkModels.PartyType.Party,
        PartyMembership.Alliance => SdkModels.PartyType.Alliance,
        _ => SdkModels.PartyType.None,
    };
}
