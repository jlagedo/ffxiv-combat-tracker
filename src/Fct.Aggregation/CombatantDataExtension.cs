using System;
using System.Collections.Generic;
using System.Globalization;

namespace Advanced_Combat_Tracker
{
    // Ported from the FFXIV_ACT_Plugin decompile's CombatantDataExtension.cs (namespace
    // FFXIV_ACT_Plugin there) — the plugin's own CombatantData/AttackType helper math that its
    // ExportVariables formatters (ACT_UIMods.cs, registered in EngineTables.Install() from P5.2 on)
    // call through: Job, Parry/Block tallies, direct-heal/overheal sums, direct-hit/crit-direct-hit
    // counts, and LastNDPS. Lives in Fct.Aggregation (not a plugin assembly) per the pipeline
    // completeness plan's constraint 1 — this is generic aggregation math, carrying no plugin
    // identity, in the same shared engine binary every replica (host engine + both facades) runs.
    //
    // LastNDPS (CombatantDataExtension.cs:122-153 in the decompile) is the only method here that
    // reads a "current time" (ActGlobals.oFormActMain.LastKnownTime in the decompile). Our
    // runtime-neutral equivalent is AggregationGlobals.lastKnownTime — each facade wires
    // AggregationGlobals.LastKnownTimeAccessor to the EncounterLifecycle instance (or equivalent) it
    // folds swings through, the same facade-owned-state pattern as charName/blockIsHit/restrictToAll,
    // since these static extension methods have no direct handle to the owning instance.
    public static class CombatantDataExtension
    {
        // ACT_UIMods.OneOrInt(int)/(long): guards a divide-by-zero denominator (e.g.
        // BlockParryCount(), DirectHeal(), Items.Count) by substituting 1 for a 0 count; every other
        // value passes through unchanged. internal (not private, unlike the decompile's ACT_UIMods
        // — there both the math and the registration lambdas live in one class; here EngineTables.cs
        // is a separate class in the same assembly that calls these from its ParryPct/BlockPct/
        // OverHealPct ColumnDef registrations, per the P5.1 handoff).
        internal static int OneOrInt(int data) => data == 0 ? 1 : data;
        internal static long OneOrInt(long data) => data == 0L ? 1L : data;

        // The combatant's job abbreviation ("War"/"Pld"/...): the first non-blank "Job" swing tag
        // found scanning AllOut's attack-type buckets in order, each bucket's first swing only.
        // Empty when no bucket has tagged one (matches ACT_UIMods.cs:1899-1928's indirection target).
        public static string Job(this CombatantData combatant)
        {
            for (int i = 0; i < combatant.AllOut.Values.Count; i++)
            {
                var swings = combatant.AllOut.Values[i].Items;
                if (swings.Count > 0 && swings[0].Tags.ContainsKey("Job") &&
                    !string.IsNullOrWhiteSpace(swings[0].Tags["Job"].ToString()))
                {
                    return swings[0].Tags["Job"].ToString();
                }
            }
            return "";
        }

        // Count of this attack-type bucket's swings recorded as parried (MasterSwing.Special).
        public static long Parry(this AttackType attackType)
        {
            long num = 0L;
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if (attackType.Items[i].Special == "Parried")
                {
                    num++;
                }
            }
            return num;
        }

        // Count of this attack-type bucket's swings recorded as blocked (MasterSwing.Special).
        public static long Block(this AttackType attackType)
        {
            long num = 0L;
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if (attackType.Items[i].Special == "Blocked")
                {
                    num++;
                }
            }
            return num;
        }

        // Denominator ParryPct/BlockPct divide against: every swing whose MasterSwing.AttackType
        // label appears at least once as Blocked or Parried anywhere in this bucket (not just the
        // blocked/parried swings themselves — every swing sharing that label counts).
        public static long BlockParryCount(this AttackType attackType)
        {
            var list = new List<string>();
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if ((attackType.Items[i].Special == "Blocked" || attackType.Items[i].Special == "Parried") &&
                    !list.Contains(attackType.Items[i].AttackType))
                {
                    list.Add(attackType.Items[i].AttackType);
                }
            }
            int num = 0;
            for (int j = 0; j < attackType.Items.Count; j++)
            {
                if (list.Contains(attackType.Items[j].AttackType))
                {
                    num++;
                }
            }
            return num;
        }

        // Sum of outgoing-heal damage excluding damage-shield/absorb swings — the "direct heal"
        // total OverHealPct divides against.
        public static long DirectHeal(this CombatantData combatant)
        {
            long num = 0L;
            var items = combatant.Items[CombatantData.DamageTypeDataOutgoingHealing].Items["All"].Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].DamageType != "DamageShield" && items[i].DamageType != "Absorb")
                {
                    num += items[i].Damage;
                }
            }
            return num;
        }

        // Sum of the "overheal" swing tag across this attack-type bucket.
        public static long Overheal(this AttackType attackType)
        {
            long num = 0L;
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if (attackType.Items[i].Tags.ContainsKey("overheal") &&
                    long.TryParse(attackType.Items[i].Tags["overheal"].ToString(), NumberStyles.Integer, null, out var result))
                {
                    num += result;
                }
            }
            return num;
        }

        // Count of swings tagged DirectHit == "True".
        public static long DirectHitCount(this AttackType attackType)
        {
            long num = 0L;
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if (attackType.Items[i].Tags.ContainsKey("DirectHit") && attackType.Items[i].Tags["DirectHit"].ToString() == "True")
                {
                    num++;
                }
            }
            return num;
        }

        // Count of swings tagged DirectHit == "True" that were also Critical.
        public static long CritDirectHitCount(this AttackType attackType)
        {
            long num = 0L;
            for (int i = 0; i < attackType.Items.Count; i++)
            {
                if (attackType.Items[i].Tags.ContainsKey("DirectHit") && attackType.Items[i].Tags["DirectHit"].ToString() == "True" && attackType.Items[i].Critical)
                {
                    num++;
                }
            }
            return num;
        }

        // Average outgoing damage over the last N seconds up to AggregationGlobals.lastKnownTime (the
        // active encounter's own clock, advanced per folded swing — never wall-clock time). The
        // divisor quirk is faithfully reproduced: it caps at the combatant's own Duration, so a
        // combatant whose encounter is younger than N seconds isn't diluted by time it wasn't active.
        public static double LastNDPS(this CombatantData combatant, int N)
        {
            long num = 0L;
            DateTime dateTime = AggregationGlobals.lastKnownTime.Subtract(new TimeSpan(0, 0, N));
            var items = combatant.Items[CombatantData.DamageTypeDataOutgoingDamage].Items["All"].Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Time > dateTime)
                {
                    num += items[i].Damage.Number;
                }
            }
            return (double)num / ((combatant.Duration.TotalSeconds < (double)N) ? combatant.Duration.TotalSeconds : (double)N);
        }

        // Encounter-wide LastNDPS across the allied roster. The divisor quirk is faithfully
        // reproduced from the decompile: hardcoded 10.0, NOT N — Last30DPS/Last60DPS divide by the
        // same 10-second cap as Last10DPS (a genuine plugin quirk, not a transcription error).
        public static double LastNDPS(this EncounterData encounter, List<CombatantData> SelectiveAllies, int N)
        {
            long num = 0L;
            DateTime dateTime = AggregationGlobals.lastKnownTime.Subtract(new TimeSpan(0, 0, N));
            for (int i = 0; i < SelectiveAllies.Count; i++)
            {
                var items = SelectiveAllies[i].Items[CombatantData.DamageTypeDataOutgoingDamage].Items["All"].Items;
                for (int j = 0; j < items.Count; j++)
                {
                    if (items[j].Time > dateTime)
                    {
                        num += items[j].Damage.Number;
                    }
                }
            }
            return (double)num / ((encounter.Duration.TotalSeconds < 10.0) ? encounter.Duration.TotalSeconds : 10.0);
        }
    }
}
