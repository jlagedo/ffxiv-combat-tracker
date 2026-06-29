using System.Globalization;

namespace Fct.Parser.Native;

// One status definition row from ACT's IDefinitionRepository (dumped via the satellite).
public sealed class StatusDef
{
    public string TickType = "";     // DoT / HoT / GroundDamage / GroundHeal / ""
    public double Potency;
    public string DamageType = "";
    public int MaxTicks;
    public string ShieldType = "";   // TargetHpPercent / HealPercent / Potency / ""
    public double ShieldAmount;
    public List<PotencyEff> Potencies = new();
    public List<MultiplierDef> Multipliers = new();
}

public sealed class PotencyEff
{
    public string Type = "";         // DamageDoneMultiplier / DamageReceivedMultiplier / PotencyMultiplier / DamageAddPotency / Heal*
    public double Amount;
    public int? AmountByte;
    public bool IsStacked;
    public uint? Zone;
    public string Cat = "";
    public string DmgType = "";
    public uint[] ActionIds = System.Array.Empty<uint>();
}

public sealed class MultiplierDef
{
    public string Type = "";         // CriticalHit / DirectHit
    public double Amount;
    public uint[] ActionIds = System.Array.Empty<uint>();
}

// The potency simulator: reproduces ACT's simulated DoT/HoT amounts and damage shields. It mirrors
// FFXIV_ACT_Plugin's DoTSimulator + DamageShieldSimulator + PotencyStatusApplication: it calibrates
// a per-source attack-power proxy from observed direct hits (the median of amount/(crit/dh)/potency/
// buffMult), tracks active buffs for the damage-done multiplier, and multiplies the DoT/HoT/shield
// potency by both. The simulated tick's crit flag and ±1 rounding are ACT RNG and not reproduced.
public sealed partial class CombatLogParser
{
    private sealed class Active
    {
        public uint StatusId, SourceId;
        public long Begin;
        public double Duration;
        public ushort Param1;
        public byte B0, B1, B2;       // EffectByte0/1/2 = apply-status Param2/Param1/Param0
        public StatusDef Def = null!;
    }

    private sealed class Track
    {
        public readonly double?[] Dmg = new double?[1000];
        public int DmgIdx = -1;
        public readonly double?[] Heal = new double?[1000];
        public int HealIdx = -1;
        public double CritRate; public int CritSwings;
        public double DhRate; public int DhSwings;

        public double? DmgMedian => Median(Dmg);
        public double? HealMedian => Median(Heal);

        public void Store(double?[] hist, ref int idx, double v)
        {
            idx = idx >= hist.Length - 1 ? 0 : idx + 1;
            hist[idx] = v;
        }

        private static double? Median(double?[] hist)
        {
            var list = new List<double>(hist.Length);
            foreach (var x in hist) if (x is > 0.0) list.Add(x.Value);
            if (list.Count == 0) return null;
            list.Sort();
            int n = list.Count, m = n / 2;
            return n % 2 != 0 ? list[m] : (list[m] + list[m - 1]) / 2.0;
        }
    }

    private sealed class Sim
    {
        public uint StatusId, SourceId;
        public bool IsHoT;
        public byte? LowAmt, LowCrit;
        public int MaxTicks, UsedTicks;
        public long SimAmount;
        public double CritRate, DhRate, CritBuff;
        public long LastTick;   // 0 = never ticked (real timestamps are large positives)
    }

    private sealed class SimC
    {
        public byte Job, Level;
        public readonly List<Active> Statuses = new();
        public readonly Track Track = new();
        public readonly List<Sim> Sims = new();   // sim states where this combatant is the DoT target
    }

    private readonly Dictionary<uint, SimC> _sc = new();
    private readonly Dictionary<(uint recipient, uint statusId), (byte P0, byte P1, byte P2)> _applyParams = new();
    private readonly Dictionary<(uint recipient, uint statusId), long> _shieldHeal = new(); // heal-percent shield base

    private SimC GetSC(uint id) => _sc.TryGetValue(id, out var c) ? c : (_sc[id] = new SimC());

    private void SimAddCombatant(uint id, byte job, string? levelField)
    {
        var c = GetSC(id);
        c.Job = job;
        if (uint.TryParse(levelField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lvl) && lvl <= 255)
            c.Level = (byte)lvl;
    }

    // --- direct-hit calibration: build the per-source attack-power proxy median ------------------

    private void SimCalibrateDamage(uint srcId, uint tgtId, uint actionId, in CombatEffect e, long t)
    {
        if (e.Amount <= 0) return;
        if (ActionPotency == null || !ActionPotency.TryGetValue(actionId, out var pot)) return;
        double potency = e.Combo != 0 ? pot.PotCombo : pot.Pot;
        if (potency <= 0) return;

        var sc = GetSC(srcId);
        // Crit rate (CriticalHitDamage): running mean of crit, then this hit's source rate.
        sc.Track.CritRate = (sc.Track.CritRate * sc.Track.CritSwings + (e.IsCritical ? 1.0 : 0.0)) / (sc.Track.CritSwings + 1);
        sc.Track.CritSwings++;
        double critRate = sc.Track.CritSwings > 10 ? sc.Track.CritRate : 0.15;
        if (critRate < 0.05) critRate = 0.05;
        // DH rate (SourceDirectHit): running mean of 1.25*dh.
        sc.Track.DhRate = (sc.Track.DhRate * sc.Track.DhSwings + (e.IsDirectHit ? 1.25 : 0.0)) / (sc.Track.DhSwings + 1);
        sc.Track.DhSwings++;

        double mult = DamageBuffMult(srcId, tgtId, actionId, e.DamageTypeId, t);
        if (mult == 0) return;

        double am = e.Amount;
        if (e.EntryType == EffectEntryType.ParriedDamage) am /= 1 + e.Combo / 100; // integer combo (block %)
        if (e.IsDirectHit) am /= 1.25;
        if (e.IsCritical) am /= 1.35 + critRate;
        am /= potency;
        am /= mult;
        sc.Track.Store(sc.Track.Dmg, ref sc.Track.DmgIdx, am);
    }

    // CalculatedPotencyMultiplier: 1 + sum of source damage-done buffs + target damage-received
    // debuffs whose limits (action id / damage type) match. Zone/category-limited effects are not
    // applied (rare; would need full zone/category state).
    private double DamageBuffMult(uint srcId, uint tgtId, uint actionId, int damageTypeId, long t)
    {
        double m = 1.0;
        string dmgType = DamageName(damageTypeId);
        if (_sc.TryGetValue(srcId, out var s))
            foreach (var st in s.Statuses)
            {
                if (Expired(st, t)) continue;
                foreach (var p in st.Def.Potencies)
                    if (p.Type == "DamageDoneMultiplier" && BuffApplies(p, actionId, dmgType))
                        m += BuffAmount(p, st) / 100.0;
            }
        if (_sc.TryGetValue(tgtId, out var g))
            foreach (var st in g.Statuses)
            {
                if (Expired(st, t)) continue;
                foreach (var p in st.Def.Potencies)
                    if (p.Type == "DamageReceivedMultiplier" && BuffApplies(p, actionId, dmgType))
                        m += BuffAmount(p, st) / 100.0;
            }
        return m < 0 ? 1.0 : m;
    }

    private static double BuffAmount(PotencyEff p, Active st)
    {
        if (p.AmountByte is { } bi)
        {
            sbyte b = (sbyte)(bi == 0 ? st.B0 : bi == 1 ? st.B1 : st.B2);
            return b * (p.IsStacked ? st.Param1 : 1);
        }
        return p.Amount * (p.IsStacked ? st.Param1 : 1);
    }

    private static bool BuffApplies(PotencyEff p, uint actionId, string dmgType)
    {
        if (p.Zone.HasValue) return false;                 // zone-limited: not modelled
        if (!string.IsNullOrEmpty(p.Cat) && p.Cat != "Unknown") return false; // category-limited: not modelled
        if (p.ActionIds.Length > 0 && System.Array.IndexOf(p.ActionIds, actionId) < 0) return false;
        if (!string.IsNullOrEmpty(p.DmgType) && p.DmgType != "Unknown")
        {
            if (p.DmgType == "Physical")
            {
                if (dmgType != "Blunt" && dmgType != "Piercing" && dmgType != "Slashing") return false;
            }
            else if (p.DmgType != dmgType) return false;
        }
        return true;
    }

    private static bool Expired(Active st, long t) =>
        st.Duration != 0 && st.Begin + (long)((st.Duration + 1.0) * TimeSpan.TicksPerSecond) < t;

    // --- status application: register buffs, create DoT/HoT sim state, emit damage shields --------

    private IEnumerable<CombatAction> SimStatusAdd(uint statusId, uint srcId, uint tgtId, double dur, ushort param1, uint targetMaxHp, long t)
    {
        if (StatusDefs == null || !StatusDefs.TryGetValue(statusId, out var def)) yield break;

        var tc = GetSC(tgtId);
        _applyParams.TryGetValue((tgtId, statusId), out var pp);
        // EffectByte0/1/2 = apply-status Param2/Param1/Param0.
        var act = new Active { StatusId = statusId, SourceId = srcId, Begin = t, Duration = dur,
            Param1 = param1, B0 = pp.P2, B1 = pp.P1, B2 = pp.P0, Def = def };
        tc.Statuses.RemoveAll(x => x.StatusId == statusId && x.SourceId == srcId);
        tc.Statuses.Add(act);
        tc.Statuses.RemoveAll(x => Expired(x, t));

        // Damage shield (emitted immediately, in-combat-gated).
        if (def.ShieldType.Length > 0 && _inCombat)
        {
            long amt = ShieldAmount(def, statusId, srcId, tgtId, targetMaxHp, t);
            if (amt > 0)
                yield return new CombatAction(false, 11, false, amt, "DamageShield",
                    ResolveName(srcId), ResolveName(tgtId), ResolveStatus(statusId, "") + " (*)", "", true);
        }

        // DoT / HoT simulated-tick state.
        if (def.TickType is "DoT" or "HoT")
        {
            bool isHoT = def.TickType == "HoT";
            var src = GetSC(srcId);
            double appliedMult = (isHoT ? src.Track.HealMedian : src.Track.DmgMedian)
                                 ?? (isHoT ? src.Track.DmgMedian : src.Track.HealMedian) ?? 1.0;
            double potMult = DamageBuffMult(srcId, tgtId, 0, DamageId(def.DamageType), t);
            byte? lowAmt = _applyParams.ContainsKey((tgtId, statusId)) ? pp.P0 : (byte?)null;
            long simAmount = ComputeSimAmount(def.Potency, potMult, appliedMult, lowAmt);
            double critRate = pp.P1 != 0 ? pp.P1 / 1000.0 : (src.Track.CritSwings > 10 ? src.Track.CritRate : 0.0);
            double dhRate = src.Track.DhSwings > 10 ? src.Track.DhRate : 0.05;

            tc.Sims.RemoveAll(x => x.StatusId == statusId && x.SourceId == srcId);
            tc.Sims.Add(new Sim { StatusId = statusId, SourceId = srcId, IsHoT = isHoT,
                LowAmt = pp.P0, LowCrit = pp.P1, MaxTicks = def.MaxTicks, SimAmount = simAmount,
                CritRate = critRate, DhRate = dhRate });
        }
    }

    private static long ComputeSimAmount(double potency, double potMult, double appliedMult, byte? low)
    {
        if (potency <= 0) return low ?? 0;
        uint num = (uint)(potency * potMult * appliedMult);
        long amount;
        if (!low.HasValue || num < low.Value) amount = num;
        else
        {
            byte b = (byte)Math.Round(((decimal)num - low.Value) / 256m);
            amount = (b << 8) + low.Value;
        }
        if (amount > 4096) amount += 256;
        return amount;
    }

    private long ShieldAmount(StatusDef def, uint statusId, uint srcId, uint tgtId, uint targetMaxHp, long t)
    {
        switch (def.ShieldType)
        {
            case "TargetHpPercent":
                // Log-derived: a fraction of the target's max HP (Manaward, Blackest Night, …).
                return (long)(targetMaxHp * def.ShieldAmount / 100.0);
            case "HealPercent":
                // Log-derived: a fraction of the companion heal from the action that applied it
                // (Galvanize from Adloquium, Eukrasian Prognosis, …).
                if (_shieldHeal.TryGetValue((tgtId, statusId), out var heal))
                    return (long)((double)heal * def.ShieldAmount / 100.0);
                return 0;
            case "Potency":
                var src = GetSC(srcId);
                double hp = src.Track.HealMedian ?? src.Track.DmgMedian ?? 0;
                double mult = DamageBuffMult(srcId, tgtId, 0, 0, t);
                return (long)Math.Round(hp * def.ShieldAmount * mult);
            default:
                return 0;
        }
    }

    private void SimRemoveStatus(uint statusId, uint tgtId)
    {
        if (_sc.TryGetValue(tgtId, out var c))
        {
            c.Statuses.RemoveAll(x => x.StatusId == statusId);
            c.Sims.RemoveAll(x => x.StatusId == statusId);
        }
    }

    // --- simulated tick (type-24 line with statusId == 0): emit the estimated DoT/HoT swing -------

    private IEnumerable<CombatAction> SimTick(bool isHoT, uint targetId, uint logAmount, long t)
    {
        if (!_sc.TryGetValue(targetId, out var tc)) yield break;
        const long Window = (long)(2700 * TimeSpan.TicksPerMillisecond);
        var matches = tc.Sims.Where(x => x.IsHoT == isHoT && t - x.LastTick > Window &&
            tc.Statuses.Any(y => y.SourceId == x.SourceId && y.StatusId == x.StatusId)).ToList();
        if (matches.Count > 1) matches = matches.Where(x => x.MaxTicks > x.UsedTicks).ToList();
        if (matches.Count == 0) yield break;

        foreach (var sim in matches)
        {
            double critMult = sim.CritRate - sim.CritBuff / 100.0 + 1.35;
            if (critMult < 1.4) critMult = 1.4;
            double num3 = sim.SimAmount * (1.0 + (critMult - 1.0) * sim.CritRate) * (1.0 + 0.25 * sim.DhRate);
            long amount = logAmount == 0 ? 0 : (long)Math.Floor(num3);
            yield return new CombatAction(isHoT, isHoT ? 5 : 3, false, amount, "",
                ResolveName(sim.SourceId), ResolveName(targetId), ResolveStatus(sim.StatusId, "") + " (*)", "Unknown", true);
            sim.UsedTicks++;
            sim.LastTick = t;
        }
    }

    // --- damage-type name <-> id helpers (match ActionEffectDecoder.DamageTypeText) ---------------

    private static string DamageName(int id) => id switch
    {
        1 => "Slashing", 2 => "Piercing", 3 => "Blunt", 4 => "Shot", 5 => "Magic",
        6 => "Breath", 7 => "Physical", 8 => "LimitBreak", _ => "Unknown",
    };
    private static int DamageId(string name) => name switch
    {
        "Slashing" => 1, "Piercing" => 2, "Blunt" => 3, "Shot" => 4, "Magic" => 5,
        "Breath" => 6, "Physical" => 7, "LimitBreak" => 8, _ => 0,
    };

    // --- table loaders ----------------------------------------------------------------------------

    public static Dictionary<uint, StatusDef> LoadStatusDefs(string path)
    {
        var map = new Dictionary<uint, StatusDef>();
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split('\t');
            if (c.Length < 10 || !uint.TryParse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                continue;
            var d = new StatusDef
            {
                TickType = c[2],
                Potency = Dbl(c[3]),
                DamageType = c[4],
                MaxTicks = (int)Dbl(c[5]),
                ShieldType = c[6],
                ShieldAmount = Dbl(c[7]),
                Potencies = ParsePotencies(c[8]),
                Multipliers = ParseMultipliers(c[9]),
            };
            map[id] = d;
        }
        return map;
    }

    private static List<PotencyEff> ParsePotencies(string s)
    {
        var list = new List<PotencyEff>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var part in s.Split(';'))
        {
            var f = part.Split('|');
            if (f.Length < 8) continue;
            list.Add(new PotencyEff
            {
                Type = f[0], Amount = Dbl(f[1]),
                AmountByte = f[2].Length > 0 && int.TryParse(f[2], out var ab) ? ab : (int?)null,
                IsStacked = f[3] == "True",
                Zone = f[4].Length > 0 && uint.TryParse(f[4], out var z) ? z : (uint?)null,
                Cat = f[5], DmgType = f[6], ActionIds = ParseIds(f[7]),
            });
        }
        return list;
    }

    private static List<MultiplierDef> ParseMultipliers(string s)
    {
        var list = new List<MultiplierDef>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var part in s.Split(';'))
        {
            var f = part.Split('|');
            if (f.Length < 3) continue;
            list.Add(new MultiplierDef { Type = f[0], Amount = Dbl(f[1]), ActionIds = ParseIds(f[2]) });
        }
        return list;
    }

    public static Dictionary<uint, (int Pot, int PotCombo)> LoadActionPotency(string path)
    {
        var map = new Dictionary<uint, (int, int)>();
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split('\t');
            if (c.Length < 3 || !uint.TryParse(c[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                continue;
            int p0 = int.TryParse(c[1], out var a) ? a : 0;
            int p0c = int.TryParse(c[2], out var b) ? b : 0;
            map[id] = (p0, p0c == 0 ? p0 : p0c);
        }
        return map;
    }

    private static uint[] ParseIds(string s) => s.Length == 0 ? System.Array.Empty<uint>()
        : s.Split(',').Select(x => uint.TryParse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();

    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
