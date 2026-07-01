using Advanced_Combat_Tracker;
using Fct.Compat.ActEngine.TestSupport;
using Xunit;

// The engine relies on global static routing tables (swing-type → bucket links, bucket names) that
// ACT runs with for FFXIV. These tests set up that table state so the aggregation is exercised
// deterministically without the live plugin; the exact values are held to the real ACT binary by
// ExportVarsCompatTests. All test classes share this one fixture.
//
// The tables are global mutable statics, so test parallelization is disabled to keep the
// shared setup and per-test flag tweaks (e.g. ActGlobals.blockIsHit) from racing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fct.Compat.Act.Tests
{
    public sealed class ActTablesFixture
    {
        public ActTablesFixture() => ActTables.EnsureInstalled();
    }

    [CollectionDefinition("act")]
    public sealed class ActCollection : ICollectionFixture<ActTablesFixture> { }

    // Faithful reproduction of the plugin's damage-type routing (ACT_UIMods.cs). The routing tables +
    // swing/encounter builders live in the shared ActEngineHarness (linked into the net10 shim suite
    // too, for cross-TFM ExportVariables parity); this wrapper adds the net48-only oFormActMain that
    // other tests here read (charName etc.).
    public static class ActTables
    {
        private static bool _installed;

        public static void EnsureInstalled()
        {
            if (!_installed)
            {
                _installed = true;
                // A FormActMain registers the base ExportVariables (via CombatTables.Setup) and gives
                // ActGlobals.oFormActMain a real instance the other tests read.
                FormActMain.Log = _ => { };
                ActGlobals.oFormActMain = new FormActMain();
            }
            ActEngineHarness.InstallRoutingTables();
        }

        // Build a fresh encounter wired to a zone, with the given charName as the ally anchor.
        public static EncounterData NewEncounter(string charName = "Player One", string zone = "Test Zone")
        {
            EnsureInstalled();
            return ActEngineHarness.NewEncounter(charName, zone);
        }

        // An outgoing ability-damage swing (SwingType 2 → routes into "Outgoing Damage").
        public static MasterSwing Hit(string attacker, string victim, long damage, System.DateTime time,
            int seq = 0, bool crit = false, string attackType = "Attack")
            => ActEngineHarness.Hit(attacker, victim, damage, time, seq, crit, attackType);
    }
}
