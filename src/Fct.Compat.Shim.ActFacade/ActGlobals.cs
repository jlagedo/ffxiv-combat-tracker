namespace Advanced_Combat_Tracker
{
    // Static hub the plugins reach through. oFormActMain is the canonical FormActMain — a POCO over
    // the modern IPluginHost (NOT a WinForms Form). One instance is shared across all shimmed plugins
    // in the process, mirroring real ACT where every plugin sees one FormActMain; LegacyPluginHost
    // constructs it idempotently and appends each plugin to ActPlugins.
    public static class ActGlobals
    {
        public static FormActMain oFormActMain;

        // The bulk-import progress dialog. Never instantiated in the net10 host; OverlayPlugin reads
        // oFormImportProgress?.Visible to gate live DPS pushes, so null → "not importing".
        public static FormImportProgress oFormImportProgress;

        // ACT's built-in spell-timer engine. Non-null so plugins can `+=` its events / round-trip
        // TimerDefs; kept as a behavior-free stub (the spell-timer subsystem is out of the compat
        // shim's data path).
        public static FormSpellTimers oFormSpellTimers = new FormSpellTimers();

        public static string charName = "YOU";

        // Aggregation flags (ACT-core). Defaults match ACT. Kept as real static fields to mirror the
        // net48 facade (a recompiled plugin binds ActGlobals.charName the same way real ACT exposes it).
        public static bool blockIsHit = true;
        public static bool mainTableShowCommas = false;
        public static bool restrictToAll = false;
        public static bool longDuration = false;

        // Wire the Fct.Aggregation engine's read accessors to these fields, so it reads one live source
        // of truth without referencing this facade (which would be a cycle). Runs on first ActGlobals
        // access — which precedes any encounter aggregation.
        static ActGlobals()
        {
            AggregationGlobals.CharNameAccessor = () => charName;
            AggregationGlobals.BlockIsHitAccessor = () => blockIsHit;
            AggregationGlobals.RestrictToAllAccessor = () => restrictToAll;
        }
    }
}
