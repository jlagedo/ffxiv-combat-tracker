using System.Globalization;

namespace Fct.Parser.Native;

// A resolved combat action, equivalent to one ACT MasterSwing: attacker/victim names are
// resolved from combatant state. Only emitted when ACT would emit it (the InCombat gate is
// applied during parsing, not deferred to the consumer).
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

// Stateful, pure-log parser. Tracks the primary player (02), combatant names/owners/jobs (03/04),
// casts (20), and an ACT-faithful combat window, then emits resolved CombatActions mirroring
// FFXIV_ACT_Plugin's ReportCombatData for the log-derivable swing types:
//   0/2  Damage/Autoattack   (21/22 damage effects; ungated, start/extend combat)
//   4    Heal                (21/22 heal effects; in-combat only)
//   6/7  PowerDrain/Healing  (21/22 MP-loss/-gain effects; in-combat only)
//   1    Action              (21/22 with no damage/heal/resource/status effect; in-combat only)
//   8    Status              (26 StatusAdd; in-combat only)
//   2/4/1 Cancelled cast     (23; in-combat only)
//
// Combat window matches ACT's FormActMain idle-end model exactly: combat starts at the first
// SetEncounter (damage), every emitted swing that runs through SetEncounter refreshes
// LastHostileTime, and combat ends when LastKnownTime - LastHostileTime > 6s (the default
// nudIdleLimit) or on a zone change (01). Action(1) and Status(8) are gated on InCombat but do
// NOT refresh it (ACT reports them via _actWrapper.AddCombatAction directly, bypassing
// SetEncounter). Real ground-AoE DoT/HoT ticks (24, statusId != 0) carry log amounts and are
// emitted here; ACT's simulated (statusId == 0) DoT/HoT (3/5) and damage shields (11) are
// reproduced by the potency simulator (PotencySimulator.cs), active when StatusDefs +
// ActionPotency are supplied.
public sealed partial class CombatLogParser
{
    private readonly Dictionary<uint, string> _names = new();
    private readonly Dictionary<uint, uint> _owners = new(); // pet/summon id -> owner id
    private readonly HashSet<uint> _petLike = new();         // owned combatants with job 0 (true pets)
    private readonly Dictionary<(uint src, uint action), uint> _casts = new(); // last cast target
    private readonly Dictionary<uint, bool> _actionIsHeal = new();   // action id -> seen as heal
    private readonly Dictionary<uint, bool> _actionIsDamage = new(); // action id -> seen as damage
    private uint _playerId;
    private bool _hasPlayer;

    // Combat window (ACT FormActMain idle-end model). Ticks are UTC ticks of the log timestamps.
    private bool _inCombat;
    private long _lastKnownTicks = long.MinValue;
    private long _lastHostileTicks;
    private const long IdleLimitTicks = 6 * TimeSpan.TicksPerSecond;

    public string CharName { get; init; } = "YOU";

    // Optional action id -> name table (FFXIV game data). When set, ability names are resolved
    // from it; otherwise the raw log name is used.
    public IReadOnlyDictionary<uint, string>? Skills { get; init; }

    // Optional status id -> name table (ACT's StatusList). When set, status (08) names resolve
    // from it; otherwise the raw log name is used.
    public IReadOnlyDictionary<uint, string>? Statuses { get; init; }

    // Optional action id -> ActionCategory table (FFXIV game data). When set, auto-attack (00) vs
    // ability (02) typing and Limit Break attribution use the category (ACT's authority);
    // otherwise the action-id/damage-type-nibble heuristics are used.
    public IReadOnlyDictionary<uint, string>? ActionCategories { get; init; }

    // Optional status/action potency definitions (ACT's IDefinitionRepository data). When both are
    // set, the potency simulator reproduces ACT's simulated DoT/HoT amounts and damage shields.
    public IReadOnlyDictionary<uint, StatusDef>? StatusDefs { get; init; }
    public IReadOnlyDictionary<uint, (int Pot, int PotCombo, int HealPot)>? ActionPotency { get; init; }
    private bool SimEnabled => StatusDefs != null && ActionPotency != null;

    public IEnumerable<CombatAction> Process(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            if (!NetworkLogLine.TryParse(raw, out var line)) continue;

            // LastKnownTime advances monotonically with each line; idle-end is checked before
            // the line is parsed (ACT runs CheckIdleEndCombat at the top of ParseRawLogLine).
            long t = line.Timestamp.UtcTicks;
            if (t > _lastKnownTicks) _lastKnownTicks = t;
            if (_inCombat && _lastKnownTicks - _lastHostileTicks > IdleLimitTicks) _inCombat = false;

            switch (line.TypeCode)
            {
                case 1: // ChangeZone — the plugin calls StopCombat on every zone change.
                    _inCombat = false;
                    break;
                case 2: // ChangePrimaryPlayer
                    if (TryId(line.Field(2), out var pid)) { _playerId = pid; _hasPlayer = true; SetName(pid, line.Field(3)); }
                    break;
                case 3: // AddCombatant
                    if (TryId(line.Field(2), out var aid))
                    {
                        SetName(aid, line.Field(3));
                        if (TryId(line.Field(6), out var owner) && owner != 0) _owners[aid] = owner;
                        else _owners.Remove(aid);
                        TryId(line.Field(4), out var job);
                        if (job == 0) _petLike.Add(aid); else _petLike.Remove(aid);
                        if (SimEnabled) SimAddCombatant(aid, (byte)job, line.Field(5));
                    }
                    break;
                case 4: // RemoveCombatant
                    if (TryId(line.Field(2), out var rid)) { _names.Remove(rid); _owners.Remove(rid); _petLike.Remove(rid); }
                    break;
                case 20: // NetworkStartsCasting — remember the cast target so a later cancel can name its victim.
                    if (TryId(line.Field(2), out var cs) && TryId(line.Field(4), out var ca) && TryId(line.Field(6), out var ct))
                        _casts[(cs, ca)] = ct;
                    break;
                case 23: // NetworkCancelAbility — ACT emits a zero-damage swing (Special = the reason).
                    if (_inCombat && TryId(line.Field(2), out var xs) && TryId(line.Field(4), out var xa))
                    {
                        string reason = line.Field(6) ?? "";
                        if (reason.Length == 0) break;
                        bool xheal = _actionIsHeal.GetValueOrDefault(xa);
                        bool xdmg = _actionIsDamage.GetValueOrDefault(xa);
                        int xtype = xdmg ? 2 : (xheal ? 4 : 1);
                        uint xt = _casts.TryGetValue((xs, xa), out var ctgt) ? ctgt : 0;
                        yield return new CombatAction(xtype == 4, xtype, false, 0, reason,
                            ResolveName(xs), ResolveName(xt), ResolveAction(xa, line.Field(5)), "", true);
                        RefreshCombat(t);
                    }
                    break;
                case 24: // NetworkDoT — a DoT/HoT tick (deferred in ACT; emitted here in order).
                {
                    if (!TryId(line.Field(2), out var dtgt)) break;
                    bool isHoT = string.Equals(line.Field(4), "HoT", StringComparison.OrdinalIgnoreCase);
                    TryId(line.Field(5), out var dStatus);
                    TryId(line.Field(6), out var dAmount);
                    TryId(line.Field(17), out var dsrc);

                    // ACT runs SetEncounter for every emitted tick, so the tick keeps the combat
                    // window alive exactly like a damage/heal. HoT ticks are in-combat-gated; DoT
                    // ticks are not (a DoT can start/continue combat).
                    if (dStatus != 0 && dsrc != 0)
                    {
                        // Real ground-AoE tick (Salted Earth, party regen HoTs, …): the log carries
                        // the true amount, which ACT emits verbatim (no "(*)" estimate).
                        if (isHoT && !_inCombat) break;
                        yield return new CombatAction(isHoT, isHoT ? 5 : 3, false, dAmount, "",
                            ResolveName(dsrc), ResolveName(dtgt), ResolveStatus(dStatus, ""), "Unknown", true);
                        RefreshCombat(t);
                    }
                    else if (SimEnabled)
                    {
                        // Simulated tick (statusId == 0): reproduce ACT's potency estimate.
                        bool emitted = false;
                        foreach (var s in SimTick(isHoT, dtgt, dAmount, t)) { emitted = true; yield return s; }
                        if (emitted || !isHoT) RefreshCombat(t);
                        else if (_inCombat) RefreshCombat(t);
                    }
                    else
                    {
                        // No simulator data: the tick still refreshed ACT's combat window.
                        if (!isHoT || _inCombat) RefreshCombat(t);
                    }
                    break;
                }
                case 30: // NetworkStatusRemove — drop the buff/sim state from tracking.
                    if (SimEnabled && TryId(line.Field(2), out var rmId) && TryId(line.Field(7), out var rmTgt))
                        SimRemoveStatus(rmId, rmTgt);
                    break;
                case 26: // NetworkStatusAdd -> Status (08)
                    if (TryId(line.Field(2), out var stId)
                        && TryId(line.Field(5), out var ssrc) && TryId(line.Field(7), out var stgt))
                    {
                        if (_inCombat)
                            yield return new CombatAction(false, 8, false, 0, "",
                                ResolveName(ssrc), ResolveName(stgt), ResolveStatus(stId, line.Field(3)), "", true);
                        // Status reports via AddCombatAction directly — does NOT refresh combat.
                        // Register the status for buff tracking + create the DoT/HoT sim state or
                        // emit the damage shield (these run regardless of the in-combat status swing).
                        if (SimEnabled)
                        {
                            double dur = double.TryParse(line.Field(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                            TryId(line.Field(9), out var sparam);
                            uint maxHp = uint.TryParse(line.Field(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mh) ? mh : 0;
                            foreach (var s in SimStatusAdd(stId, ssrc, stgt, dur, (ushort)sparam, maxHp, t)) yield return s;
                        }
                    }
                    break;
                case 21:
                case 22:
                    if (!TryId(line.Field(2), out var src) || !TryId(line.Field(6), out var tgt)) break;
                    TryId(line.Field(4), out var actionId);
                    string ability = ResolveAction(actionId, line.Field(5));
                    // Field 45 = TargetIndex (the AoE target ordinal); only index 0 carries full potency.
                    int targetIndex = int.TryParse(line.Field(45), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ti) ? ti : 0;
                    bool meaningful = false;
                    Dictionary<uint, long>? lineHeals = null; // heals this line, for heal-percent shields
                    foreach (var e in ActionEffectDecoder.DecodeFull(line))
                    {
                        meaningful = true; // any classified effect suppresses the no-effect Action(1)
                        uint victimId = e.IsSourceEntry ? src : tgt;

                        switch (e.Kind)
                        {
                            case EffectKind.Damage:
                                // ACT's party filter drops a self-targeted effect from an unowned NPC
                                // (a demi-summon's own auto-attack, flagged as a source entry).
                                if (e.IsSourceEntry && IsUnownedNpc(src)) continue;
                                string attacker = IsLimitBreak(actionId, e.DamageTypeId) ? "Limit Break" : ResolveName(src);
                                string victim = ResolveName(victimId);
                                int swing = SwingForDamage(actionId, e.SwingType);
                                yield return new CombatAction(false, swing, e.IsCritical, e.Amount, e.Special,
                                    attacker, victim, ability, ActionEffectDecoder.DamageTypeText(e.DamageTypeId, e.ElementId), true);
                                _actionIsDamage[actionId] = true;
                                if (SimEnabled) SimCalibrateDamage(src, victimId, actionId, targetIndex, e, t);
                                RefreshCombat(t); // damage starts/continues combat (SetEncounter)
                                break;

                            case EffectKind.Heal:
                                if (SimEnabled) { (lineHeals ??= new())[victimId] = e.Amount;
                                    SimCalibrateHeal(src, victimId, actionId, targetIndex, e, t); }
                                if (!_inCombat) break;
                                string healer = IsLimitBreak(actionId, -1) ? "Limit Break" : ResolveName(src);
                                yield return new CombatAction(true, 4, e.IsCritical, e.Amount, "",
                                    healer, ResolveName(victimId), ability, "", true);
                                _actionIsHeal[actionId] = true;
                                RefreshCombat(t);
                                break;

                            case EffectKind.PowerGain:
                            case EffectKind.PowerLoss:
                                if (!_inCombat) break;
                                yield return new CombatAction(false, e.SwingType, false, e.Amount, "Power",
                                    ResolveName(src), ResolveName(victimId), ability, "", true);
                                RefreshCombat(t);
                                break;

                            // Threat / ApplyStatus produce no swing from the action line (threat is the
                            // simulator's; status comes from the 26 line) but they count as meaningful.
                        }
                    }
                    if (!meaningful && _inCombat)
                    {
                        // Action with no damage/heal/resource/status effect — a bare ability marker.
                        yield return new CombatAction(false, 1, false, 0, "",
                            ResolveName(src), ResolveName(tgt), ability, "", true);
                        // Action reports via AddCombatAction directly — does NOT refresh combat.
                    }
                    // Remember the low bytes the status applications carry (so a later DoT/HoT can
                    // anchor its amount) and the companion heal (so a heal-percent shield can size).
                    if (SimEnabled)
                        foreach (var a in ActionEffectDecoder.DecodeApplyStatus(line))
                        {
                            uint recip = a.ToSource ? src : tgt;
                            _applyParams[(recip, a.StatusId)] = (a.P0, a.P1, a.P2);
                            if (lineHeals != null && lineHeals.TryGetValue(recip, out var hv))
                                _shieldHeal[(recip, a.StatusId)] = hv;
                        }
                    break;
            }
        }
    }

    // SetEncounter: start/continue combat and stamp the last hostile-action time.
    private void RefreshCombat(long t) { _inCombat = true; _lastHostileTicks = t; }

    // ACT's GetCombatantName, used for both attacker and victim: a player is the char name; a true
    // pet (owned + job 0) is credited to the bare owner name; an owned non-pet is "name (owner)";
    // anything else is its own name (or "Unknown").
    private string ResolveName(uint id)
    {
        if (_hasPlayer && id == _playerId) return CharName;
        if (_owners.TryGetValue(id, out var owner))
        {
            string ownerName = Resolve(owner);
            return _petLike.Contains(id) ? ownerName : $"{Resolve(id)} ({ownerName})";
        }
        return Resolve(id);
    }

    private string Resolve(uint id)
    {
        if (_hasPlayer && id == _playerId) return CharName;
        return _names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Unknown";
    }

    // FFXIV entity ids: players are 0x10xxxxxx, NPCs/pets/summons are 0x40xxxxxx. A combatant is
    // an "unowned NPC" if it is in the NPC range and has no recorded owner (so not an owned pet).
    private bool IsUnownedNpc(uint id) =>
        id >= 0x40000000u && !(_hasPlayer && id == _playerId) && !_owners.ContainsKey(id);

    // The LimitBreak damage-type nibble (FFXIV action-effect Param1 low nibble == 8) — the
    // log-derivable fallback when no ActionCategory table is supplied.
    private const int LimitBreakDamageType = 8;

    // Auto-attack (00) vs ability (02): the action's ActionCategory is ACT's authority. Without
    // the table, fall back to the decoder's action-id heuristic (player auto only).
    private int SwingForDamage(uint actionId, int decoderSwing) =>
        ActionCategories == null ? decoderSwing
        : (ActionCategories.TryGetValue(actionId, out var c) && c == "AutoAttack" ? 0 : 2);

    // Limit Break is attributed to a synthetic "Limit Break" combatant. ACT keys this off the
    // action's LimitBreak category; without the table, fall back to the damage-type nibble.
    private bool IsLimitBreak(uint actionId, int damageTypeId) =>
        ActionCategories != null
            ? ActionCategories.TryGetValue(actionId, out var c) && c == "LimitBreak"
            : damageTypeId == LimitBreakDamageType;

    private void SetName(uint id, string? name) => _names[id] = name ?? "";

    private string ResolveAction(uint id, string? logName) =>
        Skills != null && Skills.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : (logName ?? "");

    private string ResolveStatus(uint id, string? logName) =>
        Statuses != null && Statuses.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : (logName ?? "");

    private static bool TryId(string? s, out uint id) =>
        uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
}
