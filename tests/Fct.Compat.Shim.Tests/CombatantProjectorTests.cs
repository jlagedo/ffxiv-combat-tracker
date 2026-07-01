using System;
using System.Collections.Generic;
using Fct.Abstractions;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D7: <see cref="CombatantProjector"/> maps the modern <see cref="Actor"/>/<see cref="StatusEffect"/>
/// onto the SDK <c>Combatant</c>/<c>NetworkBuff</c>/<c>Player</c> a recompiled plugin polls. Inverse of
/// the satellite's <c>BridgeForwarder.ToActor</c>, so the field pairs round-trip.
/// </summary>
public class CombatantProjectorTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Actor FullActor(IReadOnlyList<StatusEffect>? statuses = null) => new Actor(
        Id: 100, OwnerId: 5, Kind: ActorKind.Player, Job: 24, Level: 90, Name: "Healer",
        Hp: 40000, MaxHp: 45000, Mp: 8000, MaxMp: 10000,
        Cast: null, Position: new Position(1f, 2f, 3f, 1.5f),
        WorldId: 73, WorldName: "Phoenix",
        BNpcNameId: 0, BNpcId: 0,
        TargetId: 200, TargetOfTargetId: 300,
        EffectiveDistance: 12,
        Party: PartyMembership.Alliance,
        InCombat: true,
        Statuses: statuses ?? Array.Empty<StatusEffect>(),
        Enmity: Array.Empty<EnmityEntry>())
    {
        CurrentCp = 400, MaxCp = 500, CurrentGp = 600, MaxGp = 700,
        CurrentWorldId = 74, Order = 9,
    };

    [Fact]
    public void ToCombatant_maps_every_field()
    {
        var c = CombatantProjector.ToCombatant(FullActor());

        Assert.Equal(100u, c.ID);
        Assert.Equal(5u, c.OwnerID);
        Assert.Equal((byte)1, c.type);           // Player → 1
        Assert.Equal(24, c.Job);
        Assert.Equal(90, c.Level);
        Assert.Equal("Healer", c.Name);
        Assert.Equal(40000u, c.CurrentHP);
        Assert.Equal(45000u, c.MaxHP);
        Assert.Equal(8000u, c.CurrentMP);
        Assert.Equal(10000u, c.MaxMP);
        Assert.Equal(400u, c.CurrentCP);
        Assert.Equal(500u, c.MaxCP);
        Assert.Equal(600u, c.CurrentGP);
        Assert.Equal(700u, c.MaxGP);
        Assert.Equal(1f, c.PosX);
        Assert.Equal(2f, c.PosY);
        Assert.Equal(3f, c.PosZ);
        Assert.Equal(1.5f, c.Heading);
        Assert.Equal(74u, c.CurrentWorldID);
        Assert.Equal(73u, c.WorldID);
        Assert.Equal("Phoenix", c.WorldName);
        Assert.Equal(200u, c.TargetID);
        Assert.Equal((byte)12, c.EffectiveDistance);
        Assert.Equal(SdkModels.PartyType.Alliance, c.PartyType);
        Assert.Equal(9, c.Order);
        Assert.NotNull(c.NetworkBuffs);
        Assert.Empty(c.NetworkBuffs);
    }

    [Fact]
    public void ToCombatant_projects_statuses_as_network_buffs()
    {
        var status = new StatusEffect(1239, "Embolden", Stacks: 5, RemainingSeconds: 18f, SourceActorId: 42)
        {
            RefreshPending = true,
            AppliedAt = T0,
        };

        var c = CombatantProjector.ToCombatant(FullActor(new[] { status }));

        var buff = Assert.Single(c.NetworkBuffs);
        Assert.Equal((ushort)1239, buff.BuffID);
        Assert.Equal((ushort)5, buff.BuffExtra);
        Assert.Equal(18f, buff.Duration);
        Assert.Equal(42u, buff.ActorID);
        Assert.Equal(100u, buff.TargetID);            // the owning combatant
        Assert.Equal(T0.UtcDateTime, buff.Timestamp);
        Assert.True(buff.RefreshPending);
    }

    [Theory]
    [InlineData(ActorKind.Player, (byte)1)]
    [InlineData(ActorKind.Npc, (byte)2)]
    [InlineData(ActorKind.Pet, (byte)2)]
    [InlineData(ActorKind.Object, (byte)0)]
    public void ToCombatant_maps_kind_to_sdk_type(ActorKind kind, byte expected)
    {
        var actor = FullActor() with { Kind = kind };
        Assert.Equal(expected, CombatantProjector.ToCombatant(actor).type);
    }

    [Fact]
    public void ToPlayer_projects_job_only()
    {
        var player = CombatantProjector.ToPlayer(FullActor() with { Job = 24 });
        Assert.Equal(24u, player.JobID);
        Assert.Equal(0u, player.Str);   // no attribute source in the modern model
    }

    [Fact]
    public void ToPlayer_of_null_is_empty()
    {
        var player = CombatantProjector.ToPlayer(null);
        Assert.Equal(0u, player.JobID);
    }
}
