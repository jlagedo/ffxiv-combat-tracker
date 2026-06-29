using System.Collections.Generic;
using System.Linq;

namespace Advanced_Combat_Tracker
{
    // Registers the ExportVariables OverlayPlugin/cactbot read. MiniParse iterates the WHOLE
    // CombatantData.ExportVariables dictionary, so the full ACT-default key set is registered here,
    // each format mirroring ACT's FormActMain.CombatantFormatSwitch exactly. (The FFXIV plugin and
    // OverlayPlugin add a few extras — Job, ParryPct… — on top during their own init.)
    internal static class CombatTables
    {
        private static bool _done;

        // ACT's suffixed number renderer (the "-*"/"-k|m|b" keys).
        private static string Cds(double v, bool dec) =>
            ActGlobals.oFormActMain.CreateDamageString((long)v, true, dec);
        // ACT's NAME{n}: the name truncated to n characters (trimmed).
        private static string Short(string n, int k) =>
            n != null && n.Length > k ? n.Substring(0, k).Trim() : n;

        // ACT's crittypes (FormActMain.AttackTypeGetCritTypes): "-" when the combatant landed no
        // crits, else the share of crits that were Legendary/Fabled/Mythical. FFXIV carries no
        // CriticalStr tag, so every crit buckets to none — ACT renders "0.0%L - 0.0%F - 0.0%M".
        private static string CritTypes(CombatantData d)
        {
            var at = d.GetAttackType("All", CombatantData.DamageTypeDataOutgoingDamage);
            if (at == null || at.CritHits == 0) return "-";
            return "0.0%L - 0.0%F - 0.0%M";
        }

        public static void Setup()
        {
            if (_done) return;
            _done = true;

            void C(string key, CombatantData.ExportStringDataCallback cb) =>
                CombatantData.ExportVariables[key] = new CombatantData.TextExportFormatter(key, key, key, cb);
            void E(string key, EncounterData.ExportStringDataCallback cb) =>
                EncounterData.ExportVariables[key] = new EncounterData.TextExportFormatter(key, key, key, cb);

            // Combatant-level — the full ACT-default ExportVariables set, formats per CombatantFormatSwitch.
            C("n", (d, e) => "\n");
            C("t", (d, e) => "\t");
            C("name", (d, e) => d.Name);
            C("NAME", (d, e) => d.Name);
            for (int k = 3; k <= 15; k++) { int kk = k; C("NAME" + kk, (d, e) => Short(d.Name, kk)); }
            C("duration", (d, e) => d.DurationS);
            C("DURATION", (d, e) => d.Duration.TotalSeconds.ToString("0"));

            C("damage", (d, e) => d.Damage.ToString());
            C("damage-m", (d, e) => (d.Damage / 1000000.0).ToString("0.00"));
            C("damage-b", (d, e) => (d.Damage / 1000000000.0).ToString("0.00"));
            C("damage-*", (d, e) => Cds(d.Damage, true));
            C("DAMAGE-k", (d, e) => (d.Damage / 1000.0).ToString("0"));
            C("DAMAGE-m", (d, e) => (d.Damage / 1000000.0).ToString("0"));
            C("DAMAGE-b", (d, e) => (d.Damage / 1000000000.0).ToString("0"));
            C("DAMAGE-*", (d, e) => Cds(d.Damage, false));
            C("damage%", (d, e) => d.DamagePercent);

            C("dps", (d, e) => d.DPS.ToString("F"));
            C("dps-*", (d, e) => Cds(d.DPS, true));
            C("DPS", (d, e) => d.DPS.ToString("0"));
            C("DPS-k", (d, e) => (d.DPS / 1000.0).ToString("0"));
            C("DPS-m", (d, e) => (d.DPS / 1000000.0).ToString("0"));
            C("DPS-*", (d, e) => Cds(d.DPS, false));

            C("encdps", (d, e) => d.EncDPS.ToString("F"));
            C("encdps-*", (d, e) => Cds(d.EncDPS, true));
            C("ENCDPS", (d, e) => d.EncDPS.ToString("0"));
            C("ENCDPS-k", (d, e) => (d.EncDPS / 1000.0).ToString("0"));
            C("ENCDPS-m", (d, e) => (d.EncDPS / 1000000.0).ToString("0"));
            C("ENCDPS-*", (d, e) => Cds(d.EncDPS, false));

            C("hits", (d, e) => d.Hits.ToString());
            C("crithits", (d, e) => d.CritHits.ToString());
            C("crithit%", (d, e) => d.CritDamPerc.ToString("0'%"));
            C("crittypes", (d, e) => CritTypes(d));
            C("misses", (d, e) => d.Misses.ToString());
            C("hitfailed", (d, e) => d.Blocked.ToString());
            C("swings", (d, e) => d.Swings.ToString());
            C("tohit", (d, e) => d.ToHit.ToString("F"));
            C("TOHIT", (d, e) => d.ToHit.ToString("0"));

            C("maxhit", (d, e) => d.GetMaxHit(true, false));
            C("MAXHIT", (d, e) => d.GetMaxHit(false, false));
            C("maxhit-*", (d, e) => d.GetMaxHit(true, true));
            C("MAXHIT-*", (d, e) => d.GetMaxHit(false, true));

            C("healed", (d, e) => d.Healed.ToString());
            C("healed%", (d, e) => d.HealedPercent);
            C("enchps", (d, e) => d.EncHPS.ToString("F"));
            C("enchps-*", (d, e) => Cds(d.EncHPS, true));
            C("ENCHPS", (d, e) => d.EncHPS.ToString("0"));
            C("ENCHPS-k", (d, e) => (d.EncHPS / 1000.0).ToString("0"));
            C("ENCHPS-m", (d, e) => (d.EncHPS / 1000000.0).ToString("0"));
            C("ENCHPS-*", (d, e) => Cds(d.EncHPS, false));
            C("hps", (d, e) => d.EncHPS.ToString("F"));

            C("critheals", (d, e) => d.CritHeals.ToString());
            C("critheal%", (d, e) => d.CritHealPerc.ToString("0'%"));
            C("heals", (d, e) => d.Heals.ToString());
            C("cures", (d, e) => d.CureDispels.ToString());

            C("maxheal", (d, e) => d.GetMaxHeal(true, false, false));
            C("MAXHEAL", (d, e) => d.GetMaxHeal(false, false, false));
            C("maxhealward", (d, e) => d.GetMaxHeal(true, true, false));
            C("MAXHEALWARD", (d, e) => d.GetMaxHeal(false, true, false));
            C("maxheal-*", (d, e) => d.GetMaxHeal(true, false, true));
            C("MAXHEAL-*", (d, e) => d.GetMaxHeal(false, false, true));
            C("maxhealward-*", (d, e) => d.GetMaxHeal(true, true, true));
            C("MAXHEALWARD-*", (d, e) => d.GetMaxHeal(false, true, true));

            C("damagetaken", (d, e) => d.DamageTaken.ToString());
            C("damagetaken-*", (d, e) => Cds(d.DamageTaken, true));
            C("healstaken", (d, e) => d.HealsTaken.ToString());
            C("healstaken-*", (d, e) => Cds(d.HealsTaken, true));
            C("powerdrain", (d, e) => d.PowerDamage.ToString());
            C("powerdrain-*", (d, e) => Cds(d.PowerDamage, true));
            C("powerheal", (d, e) => d.PowerReplenish.ToString());
            C("powerheal-*", (d, e) => Cds(d.PowerReplenish, true));

            C("kills", (d, e) => d.Kills.ToString());
            C("deaths", (d, e) => d.Deaths.ToString());
            C("threatstr", (d, e) => d.GetThreatStr("Threat (Out)"));
            C("threatdelta", (d, e) => d.GetThreatDelta("Threat (Out)").ToString());

            // Encounter-level — the raid totals cactbot reads off the active encounter (the
            // "Encounter" object OverlayPlugin builds). The full ACT-core EncounterData.ExportVariables
            // set, each arm mirroring FormActMain.EncounterFormatSwitch exactly: every numeric key sums
            // a CombatantData field over the SelectiveAllies list `a` (== the encounter's allies), then
            // formats. Two ACT quirks preserved: `critheals` sums CritHits (not CritHeals), and
            // `tohit`/`TOHIT` are the arithmetic mean of per-ally ToHit over the ally count.
            // (CurrentZoneName / Last{N}DPS / Job / DirectHit* are added live by the FFXIV plugin and
            // OverlayPlugin, not ACT core, so the facade does not register them.)
            long Dmg(List<CombatantData> a) => a.Sum(c => c.Damage);
            long Heal(List<CombatantData> a) => a.Sum(c => c.Healed);
            double DpsOf(EncounterData d, List<CombatantData> a) => (double)Dmg(a) / d.Duration.TotalSeconds;
            double HpsOf(EncounterData d, List<CombatantData> a) => (double)Heal(a) / d.Duration.TotalSeconds;

            E("n", (d, a, e) => "\n");
            E("t", (d, a, e) => "\t");
            E("title", (d, a, e) => d.Title);
            E("duration", (d, a, e) => d.DurationS);
            E("DURATION", (d, a, e) => d.Duration.TotalSeconds.ToString("0"));

            E("damage", (d, a, e) => Dmg(a).ToString());
            E("damage-m", (d, a, e) => (Dmg(a) / 1000000.0).ToString("0.00"));
            E("damage-*", (d, a, e) => Cds(Dmg(a), true));
            E("DAMAGE-k", (d, a, e) => (Dmg(a) / 1000.0).ToString("0"));
            E("DAMAGE-m", (d, a, e) => (Dmg(a) / 1000000.0).ToString("0"));
            E("DAMAGE-b", (d, a, e) => (Dmg(a) / 1000000000.0).ToString("0"));
            E("DAMAGE-*", (d, a, e) => Cds(Dmg(a), false));

            E("dps", (d, a, e) => DpsOf(d, a).ToString("F"));
            E("dps-*", (d, a, e) => "dps-*");   // ACT registers this key but its switch echoes the name
            E("DPS", (d, a, e) => DpsOf(d, a).ToString("0"));
            E("DPS-k", (d, a, e) => (DpsOf(d, a) / 1000.0).ToString("0"));
            E("DPS-m", (d, a, e) => "DPS-m");   // ACT registers this key but its switch echoes the name
            E("DPS-*", (d, a, e) => Cds((long)DpsOf(d, a), false));
            E("encdps", (d, a, e) => DpsOf(d, a).ToString("F"));
            E("encdps-*", (d, a, e) => Cds((long)DpsOf(d, a), true));
            E("ENCDPS", (d, a, e) => DpsOf(d, a).ToString("0"));
            E("ENCDPS-k", (d, a, e) => (DpsOf(d, a) / 1000.0).ToString("0"));
            E("ENCDPS-m", (d, a, e) => (DpsOf(d, a) / 1000000.0).ToString("0"));
            E("ENCDPS-*", (d, a, e) => Cds((long)DpsOf(d, a), false));

            E("hits", (d, a, e) => a.Sum(c => c.Hits).ToString());
            E("crithits", (d, a, e) => a.Sum(c => c.CritHits).ToString());
            E("crithit%", (d, a, e) => ((float)a.Sum(c => c.CritHits) / a.Sum(c => c.Hits)).ToString("0'%"));
            E("misses", (d, a, e) => a.Sum(c => c.Misses).ToString());
            E("hitfailed", (d, a, e) => a.Sum(c => c.Blocked).ToString());
            E("swings", (d, a, e) => a.Sum(c => c.Swings).ToString());
            E("tohit", (d, a, e) => (a.Sum(c => c.ToHit) / a.Count).ToString("F"));
            E("TOHIT", (d, a, e) => (a.Sum(c => c.ToHit) / a.Count).ToString("0"));

            E("maxhit", (d, a, e) => d.GetMaxHit(true, false));
            E("MAXHIT", (d, a, e) => d.GetMaxHit(false, false));
            E("maxhit-*", (d, a, e) => d.GetMaxHit(true, true));
            E("MAXHIT-*", (d, a, e) => d.GetMaxHit(false, true));

            E("healed", (d, a, e) => Heal(a).ToString());
            E("enchps", (d, a, e) => HpsOf(d, a).ToString("F"));
            E("enchps-*", (d, a, e) => Cds((long)HpsOf(d, a), true));
            E("ENCHPS", (d, a, e) => HpsOf(d, a).ToString("0"));
            E("ENCHPS-k", (d, a, e) => (HpsOf(d, a) / 1000.0).ToString("0"));
            E("ENCHPS-m", (d, a, e) => (HpsOf(d, a) / 1000000.0).ToString("0"));
            E("ENCHPS-*", (d, a, e) => Cds((long)HpsOf(d, a), false));

            E("heals", (d, a, e) => a.Sum(c => c.Heals).ToString());
            E("critheals", (d, a, e) => a.Sum(c => c.CritHits).ToString());   // ACT quirk: sums CritHits
            E("critheal%", (d, a, e) => ((float)a.Sum(c => c.CritHeals) / a.Sum(c => c.Heals)).ToString("0'%"));
            E("cures", (d, a, e) => a.Sum(c => c.CureDispels).ToString());

            E("maxheal", (d, a, e) => d.GetMaxHeal(true, false, false));
            E("MAXHEAL", (d, a, e) => d.GetMaxHeal(false, false, false));
            E("maxheal-*", (d, a, e) => d.GetMaxHeal(true, false, true));
            E("MAXHEAL-*", (d, a, e) => d.GetMaxHeal(false, false, true));
            E("maxhealward", (d, a, e) => d.GetMaxHeal(true, true, false));
            E("MAXHEALWARD", (d, a, e) => d.GetMaxHeal(false, true, false));
            E("maxhealward-*", (d, a, e) => d.GetMaxHeal(true, true, true));
            E("MAXHEALWARD-*", (d, a, e) => d.GetMaxHeal(false, true, true));

            E("damagetaken", (d, a, e) => a.Sum(c => c.DamageTaken).ToString());
            E("damagetaken-*", (d, a, e) => Cds(a.Sum(c => c.DamageTaken), true));
            E("healstaken", (d, a, e) => a.Sum(c => c.HealsTaken).ToString());
            E("healstaken-*", (d, a, e) => Cds(a.Sum(c => c.HealsTaken), true));
            E("powerdrain", (d, a, e) => a.Sum(c => c.PowerDamage).ToString());
            E("powerdrain-*", (d, a, e) => Cds(a.Sum(c => c.PowerDamage), true));
            E("powerheal", (d, a, e) => a.Sum(c => c.PowerReplenish).ToString());
            E("powerheal-*", (d, a, e) => Cds(a.Sum(c => c.PowerReplenish), true));

            E("kills", (d, a, e) => a.Sum(c => c.Kills).ToString());
            E("deaths", (d, a, e) => a.Sum(c => c.Deaths).ToString());

            // Not ACT core — the FFXIV plugin/OverlayPlugin add CurrentZoneName live. Registered here
            // as a fallback so cactbot's Encounter.CurrentZoneName resolves even before the plugin
            // writes it. Excluded from the ExportVariables parity set (the real-ACT oracle has no such
            // key), so it never enters the mass-compare diff.
            E("CurrentZoneName", (d, a, e) => d.ZoneName);
        }
    }
}
