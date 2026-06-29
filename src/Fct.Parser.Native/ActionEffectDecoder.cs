using System.Globalization;

namespace Fct.Parser.Native;

// Effect-entry types as they appear at byte offset 3 of each 8-byte effect slot in an
// ActionEffect (21/22) line — the FFXIV game's packet/log effect format. Only the values
// this decoder classifies are named.
public enum EffectEntryType : byte
{
    Nothing = 0,
    Miss = 1,
    FullResist = 2,
    Damage = 3,
    Heal = 4,
    BlockedDamage = 5,
    ParriedDamage = 6,
    MpLoss = 10,
    MpGain = 11,
    ApplyStatusEffectTarget = 14,
    ApplyStatusEffectSource = 15,
    ThreatPosition = 24,
    EnmityAmountUp = 25,
    EnmityAmountDown = 26,
}

// The category of a decoded effect slot — the partition ACT uses to route an effect to a
// swing (or to decide an action produced no swing at all). Only the kinds that matter for
// swing production / the no-effect Action(1) decision are named.
public enum EffectKind
{
    None,
    Damage,      // EntryType 3/5/6 (and Miss=1) -> SwingType 0/2
    Heal,        // EntryType 4    -> SwingType 4
    PowerGain,   // EntryType 11   -> SwingType 7 (PowerHealing)
    PowerLoss,   // EntryType 10   -> SwingType 6 (PowerDrain)
    Threat,      // EntryType 24/25/26 -> SwingType 10 (Threat, enmity; not value-decodable)
    ApplyStatus, // EntryType 14/15 — no swing here (status comes from the 26 line), but it
                 // suppresses the no-effect Action(1) for the source action.
}

// One decoded effect from an ActionEffect line, carrying exactly the values ACT derives into a
// MasterSwing: the amount (with the >65535 transform), crit/direct-hit flags, the special
// marker, and the raw damage-type/element nibbles. Names are not decoded here.
public readonly record struct CombatEffect(
    int Slot,
    EffectEntryType EntryType,
    EffectKind Kind,
    bool IsDamage,
    bool IsHeal,
    int SwingType,
    long Amount,
    bool IsCritical,
    bool IsDirectHit,
    int DamageTypeId,
    int ElementId,
    bool IsSourceEntry,
    string Special,
    byte Combo = 0);

// Clean-room decode of the 8 effect slots of an ActionEffect (21/22) line. The byte layout is the
// FFXIV game's ActionEffect packet/log format (it appears verbatim in the log line): each slot is
// data[b].PadLeft(8,'0') + data[b+1].PadLeft(8,'0') (16 hex chars), laid out Param2 Param1 Param0
// EntryType | Value(2) Flags2 Flags1. Decoded values are validated against ACT's output (the oracle).
public static class ActionEffectDecoder
{
    public const int MinFieldCount = 46;

    public static bool IsActionEffectLine(in NetworkLogLine line) => line.IsAbility;

    // FFXIV auto-attack action id ("attack").
    public const uint AutoAttackActionId = 0x07;

    // Damage/heal effects only — the value-bearing swings the structural compat tests check.
    public static IEnumerable<CombatEffect> Decode(NetworkLogLine line)
    {
        foreach (var e in DecodeFull(line))
            if (e.Kind is EffectKind.Damage or EffectKind.Heal)
                yield return e;
    }

    // Every classified effect slot: damage/heal plus the power/threat/apply-status markers the
    // parser needs to emit resource swings and to decide the no-effect Action(1).
    public static IEnumerable<CombatEffect> DecodeFull(NetworkLogLine line)
    {
        if (!line.IsAbility || line.FieldCount < MinFieldCount)
            yield break;

        bool isAuto = TryHex(line.Field(4), out var actionId) && actionId == AutoAttackActionId;
        int dmgSwing = isAuto ? 0 : 2; // SwingType.Autoattack : SwingType.Damage

        for (int slot = 0; slot < 8; slot++)
        {
            int b = 8 + slot * 2;
            string hex = Pad8(line.Field(b)) + Pad8(line.Field(b + 1)); // 16 chars

            byte entryType = HexByte(hex, 6);
            byte param0 = HexByte(hex, 4);
            byte param1 = HexByte(hex, 2);
            byte param2 = HexByte(hex, 0);
            byte flags2 = HexByte(hex, 12);
            byte flags1 = HexByte(hex, 14);
            ushort value = (ushort)HexUInt(hex.Substring(8, 4));

            // value + (Flags2&0x40 ? Flags1<<16 : 0) — the >65535 amount transform.
            long amount = value + (((flags2 & 0x40) != 0) ? (long)flags1 << 16 : 0);
            int damageType = param1 & 0xF;
            int element = param1 >> 4;
            bool isSourceEntry = (flags2 & 0x80) != 0;

            switch ((EffectEntryType)entryType)
            {
                case EffectEntryType.Miss:
                    yield return new CombatEffect(slot, EffectEntryType.Miss, EffectKind.Damage, true, false,
                        dmgSwing, -1, false, false, damageType, element, isSourceEntry, "Miss");
                    break;
                case EffectEntryType.Damage:
                case EffectEntryType.BlockedDamage:
                case EffectEntryType.ParriedDamage:
                    yield return new CombatEffect(slot, (EffectEntryType)entryType, EffectKind.Damage, true, false,
                        dmgSwing, amount, (param0 & 0x20) != 0, (param0 & 0x40) != 0, damageType, element,
                        isSourceEntry, Special((EffectEntryType)entryType), param2);
                    break;
                case EffectEntryType.Heal:
                    // Heal crit comes from Param1's 0x20 bit (not Param0 as for damage).
                    yield return new CombatEffect(slot, EffectEntryType.Heal, EffectKind.Heal, false, true,
                        4, amount, (param1 & 0x20) != 0, false, 0, 0, isSourceEntry, "");
                    break;
                case EffectEntryType.MpGain:
                    yield return new CombatEffect(slot, EffectEntryType.MpGain, EffectKind.PowerGain, false, false,
                        7, amount, false, false, 0, 0, isSourceEntry, "Power");
                    break;
                case EffectEntryType.MpLoss:
                    yield return new CombatEffect(slot, EffectEntryType.MpLoss, EffectKind.PowerLoss, false, false,
                        6, -amount, false, false, 0, 0, isSourceEntry, "Power");
                    break;
                case EffectEntryType.ThreatPosition:
                case EffectEntryType.EnmityAmountUp:
                case EffectEntryType.EnmityAmountDown:
                    yield return new CombatEffect(slot, (EffectEntryType)entryType, EffectKind.Threat, false, false,
                        10, amount, false, false, 0, 0, isSourceEntry, "Threat");
                    break;
                case EffectEntryType.ApplyStatusEffectTarget:
                case EffectEntryType.ApplyStatusEffectSource:
                    yield return new CombatEffect(slot, (EffectEntryType)entryType, EffectKind.ApplyStatus, false, false,
                        8, 0, false, false, 0, 0, isSourceEntry, "");
                    break;
                default:
                    break; // Nothing / FullResist / Invulnerable / VFX / Gauge … — no swing, not meaningful
            }
        }
    }

    // The ApplyStatusEffect slots of a 21/22 line: (statusId, param0, param1, param2, toSource).
    // ACT carries the low bytes of a DoT/HoT's eventual amount/crit in these params, so the
    // simulator can anchor its estimate to them (Param0 -> amount low byte, Param1 -> crit low byte).
    public static IEnumerable<(uint StatusId, byte P0, byte P1, byte P2, bool ToSource)> DecodeApplyStatus(NetworkLogLine line)
    {
        if (!line.IsAbility || line.FieldCount < MinFieldCount) yield break;
        for (int slot = 0; slot < 8; slot++)
        {
            int b = 8 + slot * 2;
            string hex = Pad8(line.Field(b)) + Pad8(line.Field(b + 1));
            byte entryType = HexByte(hex, 6);
            if (entryType != (byte)EffectEntryType.ApplyStatusEffectTarget &&
                entryType != (byte)EffectEntryType.ApplyStatusEffectSource) continue;
            yield return (
                HexUInt(hex.Substring(8, 4)),               // Value = status id
                HexByte(hex, 4),                            // Param0
                HexByte(hex, 2),                            // Param1
                HexByte(hex, 0),                            // Param2
                entryType == (byte)EffectEntryType.ApplyStatusEffectSource);
        }
    }

    // ACT's damage-type display string: DamageType name, plus " (Element)" unless the element
    // is Unknown(0) or Unaspected(7). The DamageType / ElementType names are FFXIV game-data enums
    // (read as data, not guessed); the rendered string matches ACT's output.
    public static string DamageTypeText(int damageType, int element)
    {
        string name = damageType switch
        {
            0 => "Unknown", 1 => "Slashing", 2 => "Piercing", 3 => "Blunt", 4 => "Shot",
            5 => "Magic", 6 => "Breath", 7 => "Physical", 8 => "LimitBreak", _ => "Unknown",
        };
        string? elem = element switch
        {
            1 => "Fire", 2 => "Ice", 3 => "Air", 4 => "Earth", 5 => "Lightning", 6 => "Water",
            _ => null, // 0 = Unknown, 7 = Unaspected → no suffix
        };
        return elem is null ? name : $"{name} ({elem})";
    }

    private static string Special(EffectEntryType t) => t switch
    {
        EffectEntryType.BlockedDamage => "Blocked",
        EffectEntryType.ParriedDamage => "Parried",
        EffectEntryType.Miss => "Miss",
        _ => "",
    };

    private static string Pad8(string? field) => (field ?? "").PadLeft(8, '0');

    private static byte HexByte(string hex, int offset) =>
        byte.Parse(hex.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static uint HexUInt(string hex) =>
        uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static bool TryHex(string? s, out uint value) =>
        uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}
