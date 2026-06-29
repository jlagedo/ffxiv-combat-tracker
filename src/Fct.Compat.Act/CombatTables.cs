namespace Advanced_Combat_Tracker
{
    // Registers the base (ACT-core) export variables OverlayPlugin/cactbot read. The plugin
    // adds FFXIV-specific extras (Job, Last10DPS, ParryPct…) on top during its own init.
    internal static class CombatTables
    {
        private static bool _done;

        public static void Setup()
        {
            if (_done) return;
            _done = true;

            void C(string key, CombatantData.ExportStringDataCallback cb) =>
                CombatantData.ExportVariables[key] = new CombatantData.TextExportFormatter(key, key, key, cb);
            void E(string key, EncounterData.ExportStringDataCallback cb) =>
                EncounterData.ExportVariables[key] = new EncounterData.TextExportFormatter(key, key, key, cb);

            // Combatant-level. Formats mirror ACT's FormActMain.CombatantFormatSwitch exactly (the
            // strings OverlayPlugin/cactbot read), including ACT's culture-default number rendering:
            // "F" for the lower-case per-second keys (two decimals), "0" for the upper-case ones,
            // and "0'%" (rounded) for the percentage keys.
            C("name", (d, e) => d.Name);
            C("NAME", (d, e) => d.Name);
            C("duration", (d, e) => d.DurationS);
            C("DURATION", (d, e) => d.Duration.TotalSeconds.ToString("0"));
            C("encdps", (d, e) => d.EncDPS.ToString("F"));
            C("ENCDPS", (d, e) => d.EncDPS.ToString("0"));
            C("dps", (d, e) => d.DPS.ToString("F"));
            C("DPS", (d, e) => d.DPS.ToString("0"));
            C("damage", (d, e) => d.Damage.ToString());
            C("damage%", (d, e) => d.DamagePercent);
            C("healed", (d, e) => d.Healed.ToString());
            C("healed%", (d, e) => d.HealedPercent);
            C("enchps", (d, e) => d.EncHPS.ToString("F"));
            C("ENCHPS", (d, e) => d.EncHPS.ToString("0"));
            C("hps", (d, e) => d.EncHPS.ToString("F"));
            C("hits", (d, e) => d.Hits.ToString());
            C("crithits", (d, e) => d.CritHits.ToString());
            C("crithit%", (d, e) => d.CritDamPerc.ToString("0'%"));
            C("critheal%", (d, e) => d.CritHealPerc.ToString("0'%"));
            C("critheals", (d, e) => d.CritHeals.ToString());
            C("heals", (d, e) => d.Heals.ToString());
            C("cures", (d, e) => d.CureDispels.ToString());
            C("swings", (d, e) => d.Swings.ToString());
            C("misses", (d, e) => d.Misses.ToString());
            C("maxhit", (d, e) => d.GetMaxHit(true, false));
            C("MAXHIT", (d, e) => d.GetMaxHit(false, false));
            C("maxheal", (d, e) => d.GetMaxHeal(true, false, false));
            C("deaths", (d, e) => d.Deaths.ToString());
            C("kills", (d, e) => d.Kills.ToString());

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
