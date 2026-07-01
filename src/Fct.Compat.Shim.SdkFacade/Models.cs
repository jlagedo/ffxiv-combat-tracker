using System;

namespace FFXIV_ACT_Plugin.Common.Models
{
    /// <summary>Party membership, as the SDK reports it.</summary>
    public enum PartyType
    {
        None = 0,
        Party = 1,
        Alliance = 2,
    }

    /// <summary>
    /// An actor snapshot, re-declared from the real SDK (identity 3.0.0.0). The shim projects it
    /// from the modern <c>Actor</c> record (lossless for DoL/DoH — CP/GP/CurrentWorldID/Order).
    /// </summary>
    public sealed class Combatant
    {
        public uint ID { get; set; }
        public uint OwnerID { get; set; }
        public byte type { get; set; }
        public int Job { get; set; }
        public int Level { get; set; }
        public string Name { get; set; }
        public uint CurrentHP { get; set; }
        public uint MaxHP { get; set; }
        public uint CurrentMP { get; set; }
        public uint MaxMP { get; set; }
        public uint CurrentCP { get; set; }
        public uint MaxCP { get; set; }
        public uint CurrentGP { get; set; }
        public uint MaxGP { get; set; }
        public bool IsCasting { get; set; }
        public uint CastBuffID { get; set; }
        public uint CastTargetID { get; set; }
        public float CastDurationCurrent { get; set; }
        public float CastDurationMax { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Heading { get; set; }
        public uint CurrentWorldID { get; set; }
        public uint WorldID { get; set; }
        public string WorldName { get; set; }
        public uint BNpcNameID { get; set; }
        public uint BNpcID { get; set; }
        public uint TargetID { get; set; }
        public byte EffectiveDistance { get; set; }
        public PartyType PartyType { get; set; }
        public IntPtr Address { get; set; }
        public int Order { get; set; }

        public NetworkBuff[] NetworkBuffs;
    }

    /// <summary>The local player's job + stat block, as returned by <c>IDataRepository.GetPlayer</c>.</summary>
    public sealed class Player
    {
        public uint JobID { get; set; }
        public uint Str { get; set; }
        public uint Dex { get; set; }
        public uint Vit { get; set; }
        public uint Intel { get; set; }
        public uint Mnd { get; set; }
        public uint Pie { get; set; }
        public uint Attack { get; set; }
        public uint DirectHit { get; set; }
        public uint Crit { get; set; }
        public uint AttackMagicPotency { get; set; }
        public uint HealMagicPotency { get; set; }
        public uint Det { get; set; }
        public uint SkillSpeed { get; set; }
        public uint SpellSpeed { get; set; }
        public uint Tenacity { get; set; }
        public ulong LocalContentId { get; set; }
    }

    /// <summary>A status/buff on an actor. The shim projects it from the modern <c>StatusEffect</c>.</summary>
    public sealed class NetworkBuff
    {
        public ushort BuffID { get; set; }
        public ushort BuffExtra { get; set; }
        public DateTime Timestamp { get; set; }
        public float Duration { get; set; }
        public uint ActorID { get; set; }
        public string ActorName { get; set; }
        public uint TargetID { get; set; }
        public string TargetName { get; set; }
        public bool RefreshPending { get; set; }
    }
}
