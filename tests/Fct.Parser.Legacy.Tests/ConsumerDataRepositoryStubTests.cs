using System;
using System.Linq;
using System.Reflection;
using FFXIV_ACT_Plugin.Common;
using Xunit;

namespace Fct.Parser.Legacy.Tests
{
    // PIPELINE-COMPLETENESS-PLAN P1.5 (repository surface gate) — the in-process, no-satellite-required
    // half of the gate. Asserts directly against the real internal ConsumerDataRepository
    // (ConsumerDataSurface.cs ~93-179, InternalsVisibleTo granted to this project for exactly this
    // purpose) rather than the cross-process --stand-in path Fct.Integration.Tests exercises — no
    // satellite process, no real FFXIV_ACT_Plugin.dll install required, always runs.
    public sealed class ConsumerDataRepositoryStubTests
    {
        // Documents today's five hardcoded env-scalar stubs (G4) so a future accidental edit to the
        // literal values themselves is caught here first. NOT the red gate — these values are exactly
        // what ConsumerDataSurface.cs ~156-160 returns today; the gate that these must change once P3
        // forwards real values lives in the cross-process Fct.Integration.Tests
        // RepositorySurfaceLiveTests (GameVersion/ServerTimestamp/IsChatLogAvailable) and in
        // ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet below
        // (Language/Region).
        [Fact]
        public void ConsumerDataRepository_serves_hardcoded_env_stubs_today_documenting_G4()
        {
            var repo = new ConsumerDataRepository();
            Assert.Equal("0.0", repo.GetGameVersion());
            Assert.Equal(Language.English, repo.GetSelectedLanguageID());
            Assert.Equal((byte)0, repo.GetGameRegion());
            Assert.True(repo.IsChatLogAvailable());
            // The stub is literally `=> DateTime.UtcNow;` (not DateTime.MinValue) — assert it lands within
            // a generous window of "now" rather than pinning an exact instant.
            Assert.True((DateTime.UtcNow - repo.GetServerTimestamp()) < TimeSpan.FromMinutes(1),
                "GetServerTimestamp() stub is expected to track UtcNow, not DateTime.MinValue");
        }

        // THE RED GATE for the Language/Region half of P1.5 (G4). P0.3 could not pin an exact headless
        // value for GetSelectedLanguageID()/GetGameRegion() — both are host-config-driven
        // (ParseSettings.LanguageID / DataCollectionSettings.RegionID) and depend on whatever the
        // installed FFXIV_ACT_Plugin has saved on the running machine, so a cross-process gate asserting
        // an exact value would be a machine-dependent flake (see RepositorySurfaceLiveTests, which
        // deliberately leaves these two unasserted-by-value for the same reason). This test asserts the
        // STRUCTURAL claim instead: every OTHER forwarded member (_pid/_playerId/_territoryId/
        // _combatants/_resources) has a real Apply()-fed backing field; Language/Region have none — both
        // GetSelectedLanguageID() and GetGameRegion() are unconditional literal expression bodies
        // (`=> Language.English;` / `=> 0;`, ConsumerDataSurface.cs ~156-157). Deliberately red today;
        // P3.5 ("ConsumerDataRepository ... Apply stores the forwarded env ... delete the stubs") adds
        // exactly the missing fields, at which point this flips green.
        [Fact]
        public void ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet()
        {
            var fields = typeof(ConsumerDataRepository).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

            var hasLanguageField = fields.Any(f => f.FieldType == typeof(Language));
            Assert.True(hasLanguageField,
                "expected ConsumerDataRepository to hold an Apply()-fed Language backing field (P3.5) — none exists yet");

            var hasRegionField = fields.Any(f =>
                f.FieldType == typeof(byte) && f.Name.IndexOf("region", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.True(hasRegionField,
                "expected ConsumerDataRepository to hold an Apply()-fed region backing field (P3.5) — none exists yet");
        }
    }
}
