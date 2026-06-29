using System.Globalization;

namespace Fct.Parser.Native;

// A resolved combat action, equivalent to one ACT MasterSwing: attacker/victim names are
// resolved from combatant state, and heals carry whether combat was active (ACT only reports
// in-combat heals).
public readonly record struct CombatAction(
    bool IsHeal,
    int SwingType,
    bool IsCritical,
    long Amount,
    string Special,
    string Attacker,
    string Victim,
    string AttackType,
    string DamageType,
    bool InCombat);

// Stateful, pure-log parser: tracks the primary player (02), combatant names (03/04), and
// combat state, then emits resolved CombatActions from ActionEffect (21/22) lines. Mirrors
// FFXIV_ACT_Plugin's name resolution (player -> CharName, known -> name, else "Unknown") and
// its InCombat rule (damage is ungated and starts combat; heals are reported only in combat).
public sealed class CombatLogParser
{
    private readonly Dictionary<uint, string> _names = new();
    private uint _playerId;
    private bool _hasPlayer;
    private bool _inCombat;

    public string CharName { get; init; } = "YOU";

    // Optional action id -> name table (FFXIV game data). When set, ability names are resolved
    // from it (matching ACT's attackType); otherwise the raw log name is used.
    public IReadOnlyDictionary<uint, string>? Skills { get; init; }

    public IEnumerable<CombatAction> Process(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            if (!NetworkLogLine.TryParse(raw, out var line)) continue;

            switch (line.TypeCode)
            {
                case 2: // ChangePrimaryPlayer
                    if (TryId(line.Field(2), out var pid)) { _playerId = pid; _hasPlayer = true; SetName(pid, line.Field(3)); }
                    break;
                case 3: // AddCombatant
                    if (TryId(line.Field(2), out var aid)) SetName(aid, line.Field(3));
                    break;
                case 4: // RemoveCombatant
                    if (TryId(line.Field(2), out var rid)) _names.Remove(rid);
                    break;
                case 21:
                case 22:
                    if (!TryId(line.Field(2), out var src) || !TryId(line.Field(6), out var tgt)) break;
                    TryId(line.Field(4), out var actionId);
                    string ability = ResolveAction(actionId, line.Field(5));
                    foreach (var e in ActionEffectDecoder.Decode(line))
                    {
                        // Source-attributed effects flip the victim back to the source.
                        uint victimId = e.IsSourceEntry ? src : tgt;
                        string attacker = Resolve(src);
                        string victim = Resolve(victimId);

                        if (e.IsDamage)
                        {
                            var dmgType = ActionEffectDecoder.DamageTypeText(e.DamageTypeId, e.ElementId);
                            yield return new CombatAction(false, e.SwingType, e.IsCritical, e.Amount, e.Special, attacker, victim, ability, dmgType, true);
                            _inCombat = true; // ungated damage starts/continues combat
                        }
                        else if (e.IsHeal)
                        {
                            yield return new CombatAction(true, 4, e.IsCritical, e.Amount, "", attacker, victim, ability, "", _inCombat);
                        }
                    }
                    break;
            }
        }
    }

    private string Resolve(uint id)
    {
        if (_hasPlayer && id == _playerId) return CharName;
        return _names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Unknown";
    }

    private void SetName(uint id, string? name) => _names[id] = name ?? "";

    private string ResolveAction(uint id, string? logName) =>
        Skills != null && Skills.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : (logName ?? "");

    private static bool TryId(string? s, out uint id) =>
        uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
}
