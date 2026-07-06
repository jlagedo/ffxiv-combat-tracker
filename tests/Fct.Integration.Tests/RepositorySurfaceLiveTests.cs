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
    // no producer env tap yet (G5 — P3 not built), so there is nothing an env-forwarding event could fold
    // even live; that absence is exactly the point being gated. Every one of IDataRepository's five
    // env-scalar members (GetGameVersion/GetSelectedLanguageID/GetGameRegion/GetServerTimestamp/
    // IsChatLogAvailable) is read through the real cross-process ConsumerDataRepository (reusing the
    // `--consume --stand-in --verify-standin` harness LateJoinPrimingTests/ConsumerStandInTests already
    // established) and checked against P0.3's recorded headless verdict, never the hardcoded stub
    // (ConsumerDataSurface.cs ~156-160, G4).
    //
    // Per P0.3, three of the five have a pinned, machine-independent headless value — GameVersion=="",
    // ServerTimestamp==DateTime.MinValue, IsChatLogAvailable==true — asserted here directly.
    // GetSelectedLanguageID()/GetGameRegion() are explicitly NOT pinned by P0.3 (host-config-driven: they
    // depend on whatever ParseSettings.LanguageID/DataCollectionSettings.RegionID the installed
    // FFXIV_ACT_Plugin has saved on THIS machine), so asserting an exact value here would be a
    // machine-dependent flake rather than a real gate. They are recorded on the artifact and logged for
    // observability only; the deterministic, always-on RED assertion for that half of G4 lives in
    // Fct.Parser.Legacy.Tests
    // (ConsumerDataRepositoryStubTests.ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet),
    // which proves structurally — with no real plugin/satellite involved — that neither member has any
    // Apply()-fed backing field yet.
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

                // THE GATE (deliberately red): P0.3's hard headless verdicts. GetGameVersion() stubs "0.0"
                // unconditionally (ConsumerDataSurface.cs ~158) instead of "" (only a live game path
                // reading ffxivgame.ver ever populates a real version).
                Assert.Equal("", gameVersion);

                // GetServerTimestamp() stubs `DateTime.UtcNow` (~159); the real plugin's ServerTimeProcessor
                // is only ever assigned by a live memory scan, so headless it is DateTime.MinValue.
                Assert.Equal(DateTime.MinValue.Ticks.ToString(CultureInfo.InvariantCulture), serverTimestampTicks);

                // Passes today — NOT because it is forwarded, but because the stub's unconditional `true`
                // (~160) happens to coincide with P0.3's hard headless verdict. Kept as an explicit
                // assertion (per the plan's own framing) rather than dropped, so a future accidental stub
                // flip to `false` is still caught.
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
