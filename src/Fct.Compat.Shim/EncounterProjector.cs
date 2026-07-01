using System;
using System.Collections.Generic;
using System.Linq;
using Advanced_Combat_Tracker;
using Fct.Abstractions;

namespace Fct.Compat.Shim
{
    /// <summary>
    /// Projects the shim's aggregating ACT <see cref="EncounterData"/>/<see cref="CombatantData"/>
    /// into the modern typed <see cref="EncounterSnapshot"/>/<see cref="CombatantMetrics"/>. Carries
    /// the opaque cactbot <c>ExportVariables</c> bag (G1, evaluated from the shared engine's registered
    /// formatters) so a native consumer can round-trip the payload OverlayPlugin forwards verbatim.
    /// Pure — no host interaction; the caller decides when to project (e.g. on combat-state change).
    /// </summary>
    public static class EncounterProjector
    {
        /// <summary>Project the encounter and its allied combatants into a modern snapshot.</summary>
        public static EncounterSnapshot Project(EncounterData enc)
        {
            if (enc is null) throw new ArgumentNullException(nameof(enc));

            var allies = enc.GetAllies();
            var start = enc.StartTimes.Count > 0 ? new DateTimeOffset(enc.StartTimes[0]) : default;
            var combatants = allies.Select(c => Project(c, enc)).ToList();

            return new EncounterSnapshot(
                enc.Title, start, enc.Duration, enc.Active, enc.DPS, enc.Damage, combatants)
            {
                ExportVariables = EncounterExportVars(enc, allies),
                Zone = enc.ZoneName,
            };
        }

        /// <summary>Project one combatant's metrics (damage share is relative to the encounter total).</summary>
        public static CombatantMetrics Project(CombatantData c, EncounterData enc)
        {
            if (c is null) throw new ArgumentNullException(nameof(c));
            if (enc is null) throw new ArgumentNullException(nameof(enc));

            double damagePercent = enc.Damage > 0 ? (double)c.Damage / enc.Damage * 100.0 : 0.0;

            return new CombatantMetrics(
                c.Name, 0u, 0, c.EncDPS, c.Damage, damagePercent, c.Healed, c.CritDamPerc, 0.0, c.Deaths)
            {
                ExportVariables = CombatantExportVars(c),
                // G2: OverlayPlugin's overHeal/damageShield/absorbHeal come from per-swing tags the
                // FFXIV plugin sets; the shim populates them once it receives tagged swings. Absent in
                // this slice's swing stream → 0.
                Overheal = 0,
                ShieldedDamage = 0,
                Absorbed = 0,
            };
        }

        private static IReadOnlyDictionary<string, string> EncounterExportVars(EncounterData enc, List<CombatantData> allies)
        {
            var d = new Dictionary<string, string>(EncounterData.ExportVariables.Count);
            foreach (var kv in EncounterData.ExportVariables)
                d[kv.Key] = kv.Value.GetExportString(enc, allies, string.Empty);
            return d;
        }

        private static IReadOnlyDictionary<string, string> CombatantExportVars(CombatantData c)
        {
            var d = new Dictionary<string, string>(CombatantData.ExportVariables.Count);
            foreach (var kv in CombatantData.ExportVariables)
                d[kv.Key] = kv.Value.GetExportString(c, string.Empty);
            return d;
        }
    }
}
