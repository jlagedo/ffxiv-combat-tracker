using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
// casts (20), and an ACT-faithful combat window, then emits resolved CombatActions matching ACT's
// output (the empirical oracle) for the log-derivable swing types:
//   0/2  Damage/Autoattack   (21/22 damage effects; ungated, start/extend combat)
//   4    Heal                (21/22 heal effects; in-combat only)
//   6/7  PowerDrain/Healing  (21/22 MP-loss/-gain effects; in-combat only)
//   1    Action              (21/22 with no damage/heal/resource/status effect; in-combat only)
//   8    Status              (26 StatusAdd; in-combat only)
//   2/4/1 Cancelled cast     (23; in-combat only)
//
// Combat window matches ACT's FormActMain idle-end model: combat starts at the first SetEncounter
// (damage, heal, or a DoT tick — the swing types that can open an encounter), and because ACT calls
// SetEncounter before every AddCombatAction, every emitted swing refreshes LastHostileTime. Combat
// ends only when LastKnownTime - LastHostileTime > 6s (the default nudIdleLimit); a zone change (01)
// does NOT end combat (ACT's ChangeZone leaves InCombat untouched — the idle gap at any zone change
// closes the window first). Action(1) and Status(8) are gated on InCombat (they cannot start combat,
// since ACT could not AddCombatAction them while out of combat) but, once emitted, they extend it.
// DoT/HoT ticks (24) carry their real amount and source in the log, so they are emitted here
// using the log's own values and summed into the combatant totals as ACT does. We do NOT
// reproduce the plugin's potency *estimate* (the "(*)" per-status split) — that value is not in
// the log and is plugin logic (see ACT-OUTPUT-PARITY-GAPS.md). Damage shields (11) are plugin
// synthesis (no absorbed-amount line in the log) and are not emitted.
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

    // UTC ticks of the log line currently being processed. Stable for the duration of one line's
    // swings, so a consumer reading it right after each yielded CombatAction recovers that swing's
    // time — used to feed the aggregation harness (encounter splitting needs per-swing time).
    public long CurrentLineTicks { get; private set; }

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

    // Optional logging seam. Defaults to NullLogger, so the parser is silent (and allocation-free on
    // the logging path) unless a consumer supplies a logger. Unparseable lines log at Trace; a parse
    // summary logs at Debug. Unhandled-but-well-formed line types are dropped by design and are not
    // logged (they are not anomalies).
    public ILogger Log { get; init; } = NullLogger.Instance;

    public IEnumerable<CombatAction> Process(IEnumerable<string> lines)
    {
        long fed = 0, dropped = 0;
        foreach (var raw in lines)
        {
            fed++;
            if (!NetworkLogLine.TryParse(raw, out var line)) { dropped++; LogLineDropped(Log, raw); continue; }

            // LastKnownTime advances monotonically with each line; idle-end is checked before
            // the line is parsed (ACT runs CheckIdleEndCombat at the top of ParseRawLogLine).
            long t = line.Timestamp.UtcTicks;
            // The timestamp of the line currently being processed. Every swing yielded while this
            // line is handled shares this time, so a consumer can read it right after each yield to
            // recover the swing's time (used to drive the aggregation harness's encounter splitting).
            CurrentLineTicks = t;
            if (t > _lastKnownTicks) _lastKnownTicks = t;
            if (_inCombat && _lastKnownTicks - _lastHostileTicks > IdleLimitTicks) _inCombat = false;

            switch (line.TypeCode)
            {
                case 1: // ChangeZone — ACT's FormActMain.ChangeZone records the zone but does NOT
                        // end combat; combat ends only via the idle-end check (above). A zone change
                        // always carries a multi-minute gap, so the idle check has already closed the
                        // window by the time this line lands.
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
                case 24: // NetworkDoT — a DoT/HoT tick in the log.
                {
                    if (!TryId(line.Field(2), out var dtgt)) break;
                    bool isHoT = string.Equals(line.Field(4), "HoT", StringComparison.OrdinalIgnoreCase);
                    TryId(line.Field(5), out var dStatus);
                    TryId(line.Field(6), out var dAmount);
                    TryId(line.Field(17), out var dsrc);

                    if (dsrc != 0)
                    {
                        // Every tick in the log carries its real amount (field 6) AND its source
                        // (field 17), whether or not a specific status id is present. ACT sums these
                        // into the combatant damage/healed totals, so we emit them from the log's own
                        // values — the source attributes them correctly per combatant. We do NOT
                        // reproduce the plugin's potency *estimate* (its "(*)" per-status split): that
                        // value is not in the log, is less accurate than the logged tick, and is plugin
                        // logic. The status name (when statusId != 0) is the AttackType; statusId 0 ticks
                        // carry no name in the log, so AttackType is left empty. HoT ticks are
                        // in-combat-gated; DoT ticks can start/continue combat.
                        if (isHoT && !_inCombat) break;
                        yield return new CombatAction(isHoT, isHoT ? 5 : 3, false, dAmount, "",
                            ResolveName(dsrc), ResolveName(dtgt), ResolveStatus(dStatus, ""), "Unknown", true);
                        RefreshCombat(t);
                    }
                    else
                    {
                        // Sourceless tick (environmental): cannot attribute, not emitted. Still a combat
                        // event, so it keeps ACT's window alive (a DoT can hold it open; a HoT only while
                        // in combat).
                        if (!isHoT || _inCombat) RefreshCombat(t);
                    }
                    break;
                }
                case 26: // NetworkStatusAdd -> Status (08)
                    if (TryId(line.Field(2), out var stId)
                        && TryId(line.Field(5), out var ssrc) && TryId(line.Field(7), out var stgt))
                    {
                        if (_inCombat)
                        {
                            yield return new CombatAction(false, 8, false, 0, "",
                                ResolveName(ssrc), ResolveName(stgt), ResolveStatus(stId, line.Field(3)), "", true);
                            // ACT calls SetEncounter before AddCombatAction for every swing, so an
                            // emitted status extends the combat window (it cannot start it: the swing
                            // is only added while already in combat).
                            RefreshCombat(t);
                        }
                    }
                    break;
                case 21:
                case 22:
                    if (!TryId(line.Field(2), out var src) || !TryId(line.Field(6), out var tgt)) break;
                    TryId(line.Field(4), out var actionId);
                    string ability = ResolveAction(actionId, line.Field(5));
                    bool meaningful = false;
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
                                RefreshCombat(t); // damage starts/continues combat (SetEncounter)
                                break;

                            case EffectKind.Heal:
                                // ACT calls SetEncounter for heals too, so a heal starts/continues
                                // combat (not just extends an already-open window). This keeps the
                                // window open through healer-driven lulls so trailing heals and the
                                // status applications that follow are emitted, as in ACT.
                                RefreshCombat(t);
                                string healer = IsLimitBreak(actionId, -1) ? "Limit Break" : ResolveName(src);
                                yield return new CombatAction(true, 4, e.IsCritical, e.Amount, "",
                                    healer, ResolveName(victimId), ability, "", true);
                                _actionIsHeal[actionId] = true;
                                break;

                            case EffectKind.PowerGain:
                            case EffectKind.PowerLoss:
                                if (!_inCombat) break;
                                yield return new CombatAction(false, e.SwingType, false, e.Amount, "Power",
                                    ResolveName(src), ResolveName(victimId), ability, "", true);
                                RefreshCombat(t);
                                break;

                            // Threat / ApplyStatus produce no swing from the action line (status comes
                            // from the 26 line) but they count as meaningful.
                        }
                    }
                    if (!meaningful && _inCombat)
                    {
                        // Action with no damage/heal/resource/status effect — a bare ability marker.
                        yield return new CombatAction(false, 1, false, 0, "",
                            ResolveName(src), ResolveName(tgt), ability, "", true);
                        // ACT calls SetEncounter before AddCombatAction, so an emitted bare action
                        // extends the combat window (cannot start it — gated on _inCombat above).
                        RefreshCombat(t);
                    }
                    break;
            }
        }
        LogParseSummary(Log, fed, dropped);
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
