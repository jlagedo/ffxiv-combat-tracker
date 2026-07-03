using System;

namespace Advanced_Combat_Tracker
{
    // ACT-core aggregation state the engine reads. It physically lives on each facade's ActGlobals —
    // where it MUST stay a real static field, because precompiled plugins bind it as `ldsfld
    // ActGlobals::charName` (e.g. FFXIV_ACT_Plugin.Common's ACTWrapper.CharName). The engine can't
    // reference the facade (that would be a cycle), so the facade wires these accessors to its
    // ActGlobals fields at startup and the engine reads a single live source of truth through them.
    // Defaults match ACT, so a standalone engine (or a read before the facade is touched) still
    // behaves correctly.
    public static class AggregationGlobals
    {
        public static Func<string> CharNameAccessor = static () => "YOU";
        public static Func<bool> BlockIsHitAccessor = static () => true;
        public static Func<bool> RestrictToAllAccessor = static () => false;

        // The local player's name; seeds each new EncounterData's "you" allocation.
        public static string charName => CharNameAccessor();
        // Count zero-damage swings as hits (ACT default true).
        public static bool blockIsHit => BlockIsHitAccessor();
        // Aggregate only into the "All" attack-type bucket (skip per-type tables).
        public static bool restrictToAll => RestrictToAllAccessor();
    }
}
