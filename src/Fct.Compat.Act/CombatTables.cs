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
            C("crittypes", (d, e) => "");
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

            // Encounter-level (the raid totals cactbot reads off the active encounter).
            E("title", (d, a, e) => d.Title);
            E("duration", (d, a, e) => d.DurationS);
            E("DURATION", (d, a, e) => d.Duration.TotalSeconds.ToString("0"));
            E("encdps", (d, a, e) => d.DPS.ToString("F"));
            E("ENCDPS", (d, a, e) => d.DPS.ToString("0"));
            E("damage", (d, a, e) => d.Damage.ToString());
            E("healed", (d, a, e) => d.Healed.ToString());
            E("CurrentZoneName", (d, a, e) => d.ZoneName);
        }
    }
}
