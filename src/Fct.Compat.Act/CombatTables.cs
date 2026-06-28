using System;
using System.Globalization;

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

            string N(double d) => ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);

            // Combatant-level
            C("name", (d, e) => d.Name);
            C("NAME", (d, e) => d.Name);
            C("duration", (d, e) => d.Parent?.DurationS ?? "00:00");
            C("DURATION", (d, e) => d.Parent?.DurationS ?? "00:00");
            C("dps", (d, e) => N(d.DPS));
            C("DPS", (d, e) => N(d.DPS));
            C("encdps", (d, e) => N(d.EncDPS));
            C("ENCDPS", (d, e) => N(d.EncDPS));
            C("damage", (d, e) => d.Damage.ToString(CultureInfo.InvariantCulture));
            C("damage%", (d, e) => d.DamagePercent);
            C("healed", (d, e) => d.Healed.ToString(CultureInfo.InvariantCulture));
            C("healed%", (d, e) => d.HealedPercent);
            C("enchps", (d, e) => N(d.EncHPS));
            C("ENCHPS", (d, e) => N(d.EncHPS));
            C("hps", (d, e) => N(d.EncHPS));
            C("maxhit", (d, e) => d.MaxHit);
            C("MAXHIT", (d, e) => d.MaxHit);
            C("hits", (d, e) => d.Hits.ToString(CultureInfo.InvariantCulture));
            C("crithits", (d, e) => d.CritHits.ToString(CultureInfo.InvariantCulture));
            C("crithit%", (d, e) => ((int)d.CritDamPerc) + "%");
            C("swings", (d, e) => d.Swings.ToString(CultureInfo.InvariantCulture));
            C("misses", (d, e) => d.Misses.ToString(CultureInfo.InvariantCulture));
            C("deaths", (d, e) => d.Deaths.ToString(CultureInfo.InvariantCulture));
            C("kills", (d, e) => d.Kills.ToString(CultureInfo.InvariantCulture));

            // Encounter-level
            E("title", (d, a, e) => d.Title);
            E("duration", (d, a, e) => d.DurationS);
            E("DURATION", (d, a, e) => d.DurationS);
            E("encdps", (d, a, e) => N(d.DPS));
            E("ENCDPS", (d, a, e) => N(d.DPS));
            E("damage", (d, a, e) => d.Damage.ToString(CultureInfo.InvariantCulture));
            E("healed", (d, a, e) => d.Healed.ToString(CultureInfo.InvariantCulture));
            E("CurrentZoneName", (d, a, e) => d.ZoneName);
        }
    }
}
