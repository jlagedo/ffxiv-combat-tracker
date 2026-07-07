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
        // PIPELINE-COMPLETENESS-PLAN P3.5: the five G4 stubs are deleted. This test used to document
        // their hardcoded literal values (ConsumerDataSurface.cs ~156-160); it is REWRITTEN (not
        // deleted, per the P3.5 handoff) to document today's replacement — the forwarded-mirror
        // defaults a ConsumerDataRepository serves before any SessionStateChanged has ever been
        // Apply()'d (no producer env tap has folded yet). These are honest "not yet known" values, per
        // constraint 2 (never a satellite-local re-derivation): GameVersion "" (never a placeholder,
        // §3), Language/Region the SDK's own unnamed zero value (mirrors an unconfigured real plugin,
        // P0.3), IsChatLogAvailable true (the real plugin's headless default, P0.3 — kept true by
        // design so a consumer that boots before any producer exists still reports the common case),
        // and GetServerTimestamp() the offset-corrected design (UtcNow + ServerClockOffset, Zero here)
        // rather than the old stub's bare UtcNow or the real plugin's raw headless DateTime.MinValue.
        [Fact]
        public void ConsumerDataRepository_serves_forwarded_mirror_defaults_before_any_apply()
        {
            var repo = new ConsumerDataRepository();
            Assert.Equal("", repo.GetGameVersion());
            Assert.Equal(default(Language), repo.GetSelectedLanguageID());
            Assert.Equal((byte)0, repo.GetGameRegion());
            Assert.True(repo.IsChatLogAvailable());
            // GetServerTimestamp() == UtcNow + ServerClockOffset; with no Apply() yet, the offset mirror
            // is still TimeSpan.Zero, so this reduces to "close to now" — never DateTime.MinValue.
            Assert.True((DateTime.UtcNow - repo.GetServerTimestamp()) < TimeSpan.FromMinutes(1),
                "GetServerTimestamp() should be ~UtcNow (offset-corrected design), not DateTime.MinValue");
        }

        // THE STRUCTURAL GATE for the Language/Region half of P1.5 (G4) — GREEN as of P3.5. P0.3 could
        // not pin an exact headless value for GetSelectedLanguageID()/GetGameRegion() — both are
        // host-config-driven (ParseSettings.LanguageID / DataCollectionSettings.RegionID) and depend on
        // whatever the installed FFXIV_ACT_Plugin has saved on the running machine, so a cross-process
        // gate asserting an exact value would be a machine-dependent flake (see RepositorySurfaceLiveTests,
        // which deliberately leaves these two unasserted-by-value for the same reason). This test asserts
        // the STRUCTURAL claim instead: every forwarded member (_pid/_playerId/_territoryId/_combatants/
        // _resources/Language/Region) has a real Apply()-fed backing field. P3.5 added the missing
        // Language (SDK `Language`-typed) and region (`byte`-typed, name-matched) fields
        // (ConsumerDataSurface.cs ~112-116), fed from the forwarded SessionStateChanged in Apply()
        // (~137-143) — this flips the assertion green.
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
