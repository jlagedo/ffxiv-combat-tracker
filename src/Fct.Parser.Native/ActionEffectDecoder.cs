using System.Globalization;

namespace Fct.Parser.Native;

// Effect-entry types as they appear at byte offset 3 of each 8-byte effect slot in an
// ActionEffect (21/22) line. Mirrors the FFXIV_ACT_Plugin EffectEntryType enum for the
// values this decoder classifies.
public enum EffectEntryType : byte
{
    Nothing = 0,
    Miss = 1,
    FullResist = 2,
    Damage = 3,
    Heal = 4,
    BlockedDamage = 5,
    ParriedDamage = 6,
}

// One decoded damage/heal effect from an ActionEffect line, carrying exactly the values ACT
// derives into a MasterSwing: the amount (with the >65535 transform), crit/direct-hit flags,
// the special-damage marker, and the raw damage-type/element nibbles. Names/swing-type
// (auto vs ability) are not decoded here — those need combatant/action state.
public readonly record struct CombatEffect(
    int Slot,
    EffectEntryType EntryType,
    bool IsDamage,
    bool IsHeal,
    int SwingType,
    long Amount,
    bool IsCritical,
    bool IsDirectHit,
    int DamageTypeId,
    int ElementId,
    string Special);

// Clean-room decode of the 8 effect slots of an ActionEffect (21/22) line. The formulas
// mirror FFXIV_ACT_Plugin's ParseStrategyActionEffect + DamageEffectEntry/HealEffectEntry:
// each slot is data[b].PadLeft(8,'0') + data[b+1].PadLeft(8,'0') (16 hex chars), laid out
// Param2 Param1 Param0 EntryType | Value(2) Flags2 Flags1.
public static class ActionEffectDecoder
{
    public const int MinFieldCount = 46;

    public static bool IsActionEffectLine(in NetworkLogLine line) => line.IsAbility;

    // FFXIV auto-attack action id ("attack"). ACT classifies it via ActionCategory; the id
    // is stable, so we match on it directly. Pet/ranged autos resolve through the action
    // table (not yet embedded) and are out of scope.
    public const uint AutoAttackActionId = 0x07;

    public static IEnumerable<CombatEffect> Decode(NetworkLogLine line)
    {
        if (!line.IsAbility || line.FieldCount < MinFieldCount)
            yield break;

        bool isAuto = TryHex(line.Field(4), out var actionId) && actionId == AutoAttackActionId;
        int dmgSwing = isAuto ? 0 : 2; // SwingType.Autoattack : SwingType.Damage

        for (int slot = 0; slot < 8; slot++)
        {
            int b = 8 + slot * 2;
            string hi = Pad8(line.Field(b));
            string lo = Pad8(line.Field(b + 1));
            string hex = hi + lo; // 16 chars

            byte entryType = HexByte(hex, 6);
            byte param0 = HexByte(hex, 4);
            byte param1 = HexByte(hex, 2);
            byte flags2 = HexByte(hex, 12);
            byte flags1 = HexByte(hex, 14);
            ushort value = (ushort)HexUInt(hex.Substring(8, 4));

            // value + (Flags2&0x40 ? Flags1<<16 : 0) — the >65535 damage/heal transform.
            long amount = value + (((flags2 & 0x40) != 0) ? (long)flags1 << 16 : 0);
            int damageType = param1 & 0xF;
            int element = param1 >> 4;

            switch ((EffectEntryType)entryType)
            {
                case EffectEntryType.Miss:
                    yield return new CombatEffect(slot, EffectEntryType.Miss, true, false,
                        dmgSwing, -1, false, false, damageType, element, "Miss");
                    break;
                case EffectEntryType.Damage:
                case EffectEntryType.BlockedDamage:
                case EffectEntryType.ParriedDamage:
                    yield return new CombatEffect(slot, (EffectEntryType)entryType, true, false,
                        dmgSwing, amount, (param0 & 0x20) != 0, (param0 & 0x40) != 0, damageType, element,
                        Special((EffectEntryType)entryType));
                    break;
                case EffectEntryType.Heal:
                    // Heal crit comes from Param1's 0x20 bit (not Param0 as for damage).
                    yield return new CombatEffect(slot, EffectEntryType.Heal, false, true,
                        4, amount, (param1 & 0x20) != 0, false, 0, 0, "");
                    break;
                default:
                    break; // status / resource / nothing — not a value effect
            }
        }
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
