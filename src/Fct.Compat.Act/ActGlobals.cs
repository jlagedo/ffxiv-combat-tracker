namespace Advanced_Combat_Tracker
{
    // Static hub the plugins reach through. oFormActMain is the canonical FormActMain.
    public static class ActGlobals
    {
        public static FormActMain oFormActMain;
        // The bulk-import progress dialog. Never instantiated in our headless host; OverlayPlugin
        // reads oFormImportProgress?.Visible to gate live DPS pushes, so null → "not importing".
        public static FormImportProgress oFormImportProgress;
        // ACT's built-in spell-timer engine. Non-null so OP's optional SpellTimerOverlay can `+=`
        // its events and Hojoring can round-trip TimerDefs; a plain class (no Form/handle), so
        // eager construction is cheap and safe (OP may touch it early in its overlay ctor).
        public static FormSpellTimers oFormSpellTimers = new FormSpellTimers();
        public static string charName = "YOU";

        // Aggregation flags (ACT-core). Defaults match ACT.
        public static bool blockIsHit = true;
        public static bool mainTableShowCommas = false;
        public static bool restrictToAll = false;
        public static bool longDuration = false;
    }
}
