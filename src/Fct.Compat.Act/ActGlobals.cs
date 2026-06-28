namespace Advanced_Combat_Tracker
{
    // Static hub the plugins reach through. oFormActMain is the canonical FormActMain.
    public static class ActGlobals
    {
        public static FormActMain oFormActMain;
        public static string charName = "YOU";

        // Aggregation flags (ACT-core). Defaults match ACT.
        public static bool blockIsHit = false;
        public static bool mainTableShowCommas = false;
        public static bool restrictToAll = false;
        public static bool longDuration = false;
    }
}
