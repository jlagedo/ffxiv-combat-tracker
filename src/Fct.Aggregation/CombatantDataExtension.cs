using System.Collections.Generic;
using System.Globalization;

namespace Advanced_Combat_Tracker
{
    // Ported from the FFXIV_ACT_Plugin decompile's CombatantDataExtension.cs (namespace
    // FFXIV_ACT_Plugin there) — the plugin's own CombatantData/AttackType helper math that its
    // ExportVariables formatters (ACT_UIMods.cs, registered in EngineTables.Install() from P5.2 on)
    // call through: Job, Parry/Block tallies, direct-heal/overheal sums, direct-hit/crit-direct-hit
    // counts. Lives in Fct.Aggregation (not a plugin assembly) per the pipeline completeness plan's
    // constraint 1 — this is generic aggregation math, carrying no plugin identity, in the same
    // shared engine binary every replica (host engine + both facades) runs.
    //
    // LastNDPS (CombatantDataExtension.cs:122-153 in the decompile) is deferred to P5.6 — it is the
    // only method in the decompile source file that reads ActGlobals.oFormActMain.LastKnownTime; none
    // of the methods ported here need current-time at all. When P5.6 ports it, our runtime-neutral
    // equivalent of that read is EncounterLifecycle.LastKnownTime (src/Fct.Aggregation/EncounterLifecycle.cs:19,
    // surfaced on the net48 facade as FormActMain.LastKnownTime, FormActMain.cs:210) — but these static
    // extension methods have no direct handle to the owning EncounterLifecycle instance, so P5.6 should
    // reach it the same way AggregationGlobals already exposes other facade-owned ACT-core state
    // (charName/blockIsHit/restrictToAll, AggregationGlobals.cs) to this engine: add a
    // `Func<DateTime> LastKnownTimeAccessor` there, wired by each facade at startup, rather than a new
    // static/global on this class.
    public static class CombatantDataExtension
    {
        // ACT_UIMods.OneOrInt(int): guards a divide-by-zero denominator (e.g. BlockParryCount(),
        // Items.Count) by substituting 1 for a 0 count; every other value passes through unchanged.
        private static int OneOrInt(int data) => data == 0 ? 1 : data;

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
    }
}
