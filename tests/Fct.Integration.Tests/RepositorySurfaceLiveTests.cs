using System.Globalization;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // PIPELINE-COMPLETENESS-PLAN P1.5 (repository surface gate) — the LIVE axis, complementing P1.4's
    // late-join axis (LateJoinPrimingTests.Late_stand_in_repository_never_converges_on_a_forwarded_
    // GameVersion_from_priming_alone). P1.4 proved a LATE-joining stand-in repository never converges on
    // a forwarded GameVersion from priming alone (state folded onto the bus BEFORE the consumer
    // subscribes). This gate is the companion for a NORMALLY-BOOTED, non-late-joining consumer: the
    // stand-in satellite starts and subscribes to `repository` with NO bus pre-staging at all — there is
    // this test's own boot — no producer satellite runs alongside it — so there is nothing an
    // env-forwarding event could fold even live; that absence is exactly the point being gated. Every one
    // of IDataRepository's five env-scalar members (GetGameVersion/GetSelectedLanguageID/GetGameRegion/
    // GetServerTimestamp/IsChatLogAvailable) is read through the real cross-process ConsumerDataRepository
    // (reusing the `--consume --stand-in --verify-standin` harness LateJoinPrimingTests/ConsumerStandInTests
    // already established) and checked against the forwarded-mirror design, never the hardcoded stub
    // (ConsumerDataSurface.cs deleted the five G4 stubs at P3.5).
    //
    // GameVersion=="" and IsChatLogAvailable==true are P0.3's pinned, machine-independent headless
    // values AND the mirror's own before-any-Apply() defaults (ConsumerDataSurface.cs), so they read the
    // same whether or not a producer ever forwards state — asserted here directly.
    // GetServerTimestamp() is ⚠️ RECONCILED at P3.5 (see the inline comment below): the consumer serves
    // an offset-corrected approximation (UtcNow + ServerClockOffset), not P0.3's raw real-plugin verdict
    // (DateTime.MinValue) — asserted here as "close to now", per docs/PIPELINE-COMPLETENESS-PLAN.md §3/§7.
    // GetSelectedLanguageID()/GetGameRegion() are explicitly NOT pinned by P0.3 (host-config-driven: they
    // depend on whatever ParseSettings.LanguageID/DataCollectionSettings.RegionID the installed
    // FFXIV_ACT_Plugin has saved on THIS machine), so asserting an exact value here would be a
    // machine-dependent flake rather than a real gate. They are recorded on the artifact and logged for
    // observability only; the deterministic, always-on assertion for that half of G4 lives in
    // Fct.Parser.Legacy.Tests
    // (ConsumerDataRepositoryStubTests.ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet),
    // which proves structurally — with no real plugin/satellite involved — that both members now have an
    // Apply()-fed backing field (P3.5).
    public sealed class RepositorySurfaceLiveTests
    {
        private readonly ITestOutputHelper _out;
        public RepositorySurfaceLiveTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Live_stand_in_repository_serves_forwarded_env_scalars_never_the_stubs()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var dump = Path.Combine(Path.GetTempPath(), "fct-p1-5-consume-" + Guid.NewGuid().ToString("N") + ".txt");
            var verify = Path.Combine(Path.GetTempPath(), "fct-p1-5-standin-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamCombatants, SatelliteProtocol.StreamRepository);
            var consumer = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "live-repo-consumer", session,
                $"--consume \"{dump}\" --subscribe {streams} --stand-in --verify-standin \"{verify}\"");

            try
            {
                // A normal boot — deliberately NO bus pre-staging (that is P1.4's late-join axis). There is
                // no producer env tap today (G5), so no env-forwarding event could fold even if we waited.
                await consumer.StartAsync();
                await Task.Delay(2500);   // consumer loads the SDK (Costura), registers the stand-in, subscribes

                await consumer.ShutdownAsync(TimeSpan.FromSeconds(8));

                Assert.True(SpinUntilFile(verify, 5000), "consumer produced no stand-in verify artifact");
                var f = File.ReadAllText(verify).Trim().Split('\t');
                _out.WriteLine($"stand-in verify: [{string.Join(" | ", f)}]");
                Assert.Equal("1", f[0]);   // found in ActPlugins
                Assert.Equal("1", f[1]);   // DataSubscription/DataRepository bound to real SDK types

                var gameVersion = f.Length > 8 ? f[8] : "";
                var languageRaw = f.Length > 9 ? f[9] : "";
                var regionRaw = f.Length > 10 ? f[10] : "";
                var serverTimestampTicks = f.Length > 11 ? f[11] : "";
                var isChatLogAvailable = f.Length > 12 ? f[12] : "";

                // Observability only — logged BEFORE the hard asserts below so the raw values are always
                // captured in test output even if an earlier assert aborts the method.
                _out.WriteLine($"[plugin-gated, not asserted-by-value] LanguageRaw={languageRaw} RegionRaw={regionRaw} " +
                                "(host-config-driven per P0.3 — exact value left unpinned here; see " +
                                "Fct.Parser.Legacy.Tests for the deterministic structural gate on this half)");

                // GameVersion: forwarded mirror, GREEN as of P3.5. GetGameVersion() now serves the
                // ConsumerDataRepository env mirror (ConsumerDataSurface.cs), which defaults "" until an
                // Apply()'d SessionStateChanged says otherwise — no producer runs in this test (a
                // stand-in-only boot, deliberately no bus pre-staging), so the mirror never leaves its
                // default. Either way, "" is exactly P0.3's hard headless verdict for a live producer too.
                Assert.Equal("", gameVersion);

                // ⚠️ RECONCILED (PIPELINE-COMPLETENESS-PLAN P3.5, per §3/§7's offset-corrected server-clock
                // decision): this assertion originally pinned P0.3's RAW headless verdict for the real
                // plugin's own GetServerTimestamp() — DateTime.MinValue (no live memory scan). But the
                // plan's SHIPPED CONSUMER DESIGN is not a passthrough of that raw value: P3.5's
                // ConsumerDataRepository.GetServerTimestamp() deliberately serves an offset-corrected
                // server-clock APPROXIMATION for custom-line timestamps — UtcNow + the forwarded
                // ServerClockOffset (Zero when no producer has forwarded a live server time, exactly the
                // case here: this test's stand-in boots with no producer/bus pre-staging at all). Serving
                // MinValue would defeat the whole point of the projection (§7: "acceptable for
                // custom-line timestamps"), so the gate is reconciled to assert the DESIGN — the served
                // timestamp is within a generous tolerance of "now" — rather than the old raw-passthrough
                // expectation. See docs/PIPELINE-COMPLETENESS-PLAN.md P3.5 verdict for the full citation.
                var servedTicks = long.Parse(serverTimestampTicks, CultureInfo.InvariantCulture);
                var served = new DateTime(servedTicks, DateTimeKind.Utc);
                Assert.True((DateTime.UtcNow - served) < TimeSpan.FromMinutes(1),
                    $"GetServerTimestamp() should be ~UtcNow (offset-corrected design, ServerClockOffset=Zero " +
                    $"here — no producer ran), not the real plugin's raw headless DateTime.MinValue. Got {served:o}");

                // Forwarded mirror default, GREEN as of P3.5 for the RIGHT reason now: IsChatLogAvailable()
                // reads the env mirror, which defaults true (the real plugin's own headless value, P0.3)
                // until an Apply()'d SessionStateChanged overrides it — no producer runs in this test, so
                // it stays at that default. No longer the coincidental G4 stub (`=> true;` unconditionally,
                // deleted), but the same value for a principled reason: kept as an explicit assertion so a
                // future accidental default flip to `false` is still caught.
                Assert.Equal("1", isChatLogAvailable);
            }
            finally
            {
                await consumer.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(dump); } catch { }
                try { File.Delete(verify); } catch { }
            }
        }

        private static bool SpinUntilFile(string path, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (File.Exists(path)) return true;
                Thread.Sleep(50);
            }
            return File.Exists(path);
        }
    }
}
