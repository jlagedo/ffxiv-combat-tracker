using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Bridge;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // Late-join convergence gate: the P1.3 gate proved a facade-tailed
    // line never reaches a rawlog consumer LIVE, today (G14 — no producer tap; that is P2's fix). This
    // gate is deliberately a DIFFERENT axis: it proves that even where the one-shot state (zone/map/
    // player/version/settings/process/stats) HAS already folded into host session state before a
    // consumer exists — i.e. pretending P2 already forwards it — a LATE-JOINING rawlog subscriber still
    // converges on NONE of it, because BuildPrimeEvents (SatelliteHost.cs ~388-431) has no branch for the
    // "rawlog" token at all (G8): it primes typed zone/party/player/repository forms, never a cached
    // one-shot RawLogLine. There is also no last-line cache anywhere upstream (P4.1's job), so a
    // RawLogLine emitted before any rawlog subscriber exists is simply gone — GameEventBus.Emit fans only
    // to the CURRENT subscriber snapshot (Hosting/GameEventBus.cs Emit), never replayed to a future one.
    //
    // The one-shot lines are folded directly onto the REAL shared GameEventBus/GameSession (the exact
    // technique ConsumerStandInTests already uses to seed host state for a satellite under test) — the
    // real host-side BuildPrimeEvents/SatelliteEgress/StreamCatalog code runs unmodified; only the INPUT
    // (state already having happened) is synthesized, standing in for P2's not-yet-built tap.
    //
    // Serialized with the heavy multi-satellite gates (satellite-p6): the stand-in repository half loads the
    // SDK (Costura) and waits a fixed budget for the subscribe→prime→Apply round-trip to converge. Run in
    // parallel with the four-satellite tests, that budget was starved by CPU oversubscription (the GameVersion
    // never converged in time); serializing removes the contention. The collection carries no fixture.
    [Collection("satellite-p6")]
    public sealed class LateJoinPrimingTests
    {
        private readonly ITestOutputHelper _out;
        public LateJoinPrimingTests(ITestOutputHelper output) => _out = output;

        // The P0.1/P1.3 one-shot line-state set {01,02,12,40,249,250,253} as the character's FIRST
        // (initial-load) instance of each type...
        private static readonly (LogMessageType Type, string Line)[] FirstOneShot =
        {
            (LogMessageType.Version, "253|2026-01-03T22:50:01.0445077-03:00|FFXIV_ACT_Plugin Version: 2.7.4.9 (50BCD605C50A749F)|a7996fe26936a886"),
            (LogMessageType.Settings, "249|2026-01-03T22:50:01.0445077-03:00|Selected Language ID: English, Disable Damage Shield: False, Disable Combine Pets: False, Parse Filter: None, DoTCrits: False|deadbeefdeadbeef"),
            (LogMessageType.Process, "250|2026-01-03T22:50:04.0117130-03:00|Detected Process ID: 11132, Client Mode: FFXIV_64, IsAdmin: True, Game Version: 2025.12.23.0000.0000|98a6818603a94887"),
            (LogMessageType.ChangeMap, "40|2026-01-03T22:50:05.6670000-03:00|1095|Unlost World|Mistwake|Quan Caverns|bb44e3219ed799a0"),
            (LogMessageType.Territory, "01|2026-01-03T22:50:05.6670000-03:00|522|Mistwake|2d5ef64f295c1d6b"),
            (LogMessageType.ChangePrimaryPlayer, "02|2026-01-03T22:50:05.6670000-03:00|106D3875|Leon Lanceloth|545d749f19bea137"),
            (LogMessageType.PlayerStats, "12|2026-01-03T22:50:05.6670000-03:00|25|208|460|6055|6170|342|440|208|1999|2286|6170|342|1791|420|2687|420|4000174A6252A4|7f5bd22c7ec874f1"),
        };

        // ...and a SECOND, LATER instance of each type (a relog / zone move) — priming (once P4 lands)
        // must replay THESE, never the First ones, in ACT emission order per P4.2 ("253/249/250 -> 01 ->
        // 02 -> 40 -> 12", the declaration order already used here).
        private static readonly (LogMessageType Type, string Line)[] LastOneShot =
        {
            (LogMessageType.Version, "253|2026-01-03T23:10:01.0000000-03:00|FFXIV_ACT_Plugin Version: 2.7.4.9 (50BCD605C50A749F)|1111111111111111"),
            (LogMessageType.Settings, "249|2026-01-03T23:10:01.0000000-03:00|Selected Language ID: English, Disable Damage Shield: False, Disable Combine Pets: False, Parse Filter: None, DoTCrits: False|2222222222222222"),
            (LogMessageType.Process, "250|2026-01-03T23:10:04.0000000-03:00|Detected Process ID: 22244, Client Mode: FFXIV_64, IsAdmin: True, Game Version: 2025.12.23.0000.0000|3333333333333333"),
            (LogMessageType.ChangeMap, "40|2026-01-03T23:10:05.0000000-03:00|1200|Unlost World|Kugane|Shirogane|4444444444444444"),
            (LogMessageType.Territory, "01|2026-01-03T23:10:05.0000000-03:00|999|Kugane|5555555555555555"),
            (LogMessageType.ChangePrimaryPlayer, "02|2026-01-03T23:10:05.0000000-03:00|AABBCCDD|Leon Lanceloth|6666666666666666"),
            (LogMessageType.PlayerStats, "12|2026-01-03T23:10:05.0000000-03:00|30|208|460|6055|6170|342|440|208|1999|2286|6170|342|1791|420|2687|420|4000174A6252A4|7777777777777777"),
        };

        [SkippableFact]
        public async Task Late_rawlog_subscriber_converges_on_none_of_the_one_shot_lines_from_priming_alone()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");

            var prevInstall = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);

            var recordPath = Path.Combine(Path.GetTempPath(), "fct-p1-4-sink-" + Guid.NewGuid().ToString("N") + ".tsv");

            var bus = new GameEventBus();
            var provider = new GameSnapshotProvider();
            var session = new GameSession(bus, provider);
            // The real host-side snapshot fold (GameSnapshotAggregator), so the typed zone/player/party
            // fold genuinely converges session state BEFORE the late consumer exists — exactly the
            // "typed zone/player fold" convergence mechanism the plan calls out as already existing.
            var aggregator = new GameSnapshotAggregator(bus, provider, NullLogger<GameSnapshotAggregator>.Instance);
            await aggregator.StartAsync(CancellationToken.None);
            // P4.1's last-line cache, wired onto the SAME bus exactly as production DI wires it
            // (ServiceCollectionExtensions.cs) — this test constructs GameEventBus/GameSession/
            // SatelliteHost directly rather than through AddFctHostServices, so it must wire this
            // sibling singleton itself for BuildPrimeEvents (P4.2) to have anything to read.
            var lastLineCache = new LastLineCache(bus, NullLogger<LastLineCache>.Instance);
            await lastLineCache.StartAsync(CancellationToken.None);

            var consumer = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "late-consumer", session,
                $"--sink \"{recordPath}\" --subscribe {SatelliteProtocol.StreamRawLog},{SatelliteProtocol.StreamZoneParty}",
                lastLineCache: lastLineCache);

            try
            {
                var now = DateTimeOffset.UtcNow;

                // 1) Fold the typed zone/player/party/process state BEFORE any consumer exists — the
                // "producer folds one-shot state into host session state first" step. Uses the LAST
                // occurrence's values (the character has already relogged/moved zone by the time the
                // late consumer joins).
                bus.Emit(new ZoneChanged(0, now, 999, "Kugane"));
                bus.Emit(new PrimaryPlayerChanged(0, now, 0xAABBCCDDu, "Leon Lanceloth"));
                bus.Emit(new PartyChanged(0, now, new uint[] { 0xAABBCCDDu }));
                bus.Emit(new GameProcessChanged(0, now, 22244));

                // 2) Fold the one-shot RAW LOG LINES (First then Last per type) directly onto the bus —
                // standing in for P2's not-yet-built tap. No rawlog subscriber exists yet, so today these
                // simply vanish (GameEventBus.Emit fans only to the current subscriber snapshot) — and
                // there is no last-line cache anywhere to remember them for later (G8).
                foreach (var (type, line) in FirstOneShot) bus.Emit(new RawLogLine(0, now, type, line, line));
                foreach (var (type, line) in LastOneShot) bus.Emit(new RawLogLine(0, now, type, line, line));

                // 3) NOW the late consumer subscribes (rawlog + zoneparty). HandleSubscribe ->
                // BuildPrimeEvents runs against the CURRENT (already-folded) session snapshot.
                await consumer.StartAsync();
                // Priming is async: SUBSCRIBE → host BuildPrimeEvents → forwarded one-shot frames land in the
                // AutoFlush sink artifact (RunSink writes each frame live). Poll it until the primed one-shot
                // RawLogLines arrive rather than a fixed delay — under parallel satellite load a fixed wait
                // races the round-trip (this was the flake). This is a snapshot of PRIMING ALONE, so no later
                // live event backfills it; the poll falls through on timeout so the assertion below still
                // reports exactly what did/didn't arrive.
                SpinUntilPrimedRawLines(recordPath, LastOneShot.Length, TimeSpan.FromSeconds(15));

                // No live line/event follows — the artifact reflects PRIMING ALONE.
                await consumer.ShutdownAsync(TimeSpan.FromSeconds(8));

                Assert.True(SpinUntilFile(recordPath, 5000), "consumer produced no sink artifact");
                var wireLines = ReadShared(recordPath);
                var frames = new List<GameEvent>();
                foreach (var line in wireLines)
                    if (GameEventFrame.TryParse(line, out var evt) && evt is not null)
                        frames.Add(evt);

                var primedRaw = frames.OfType<RawLogLine>()
                    .Select(r => (Type: (int)r.Type, Line: r.Line))
                    .ToList();
                _out.WriteLine($"primed RawLogLine count: {primedRaw.Count} of {LastOneShot.Length} expected " +
                                $"(zoneparty control frames: {frames.Count(e => e is ZoneChanged or PartyChanged or PrimaryPlayerChanged)})");

                // Control (not the gate's point, but proves this harness isolates the RIGHT gap): the
                // EXISTING typed zone/player-fold priming mechanism DOES converge a late joiner today.
                var primedZone = frames.OfType<ZoneChanged>().FirstOrDefault();
                Assert.NotNull(primedZone);
                Assert.Equal(999u, primedZone!.ZoneId);
                Assert.Equal("Kugane", primedZone.ZoneName);

                // THE GATE: priming replays the LAST instance of each one-shot type, in ACT emission
                // order (253/249/250 -> 01 -> 02 -> 40 -> 12) — P4.1's OneShotLineTypes.EmissionOrder is
                // the single canonical declaration of that order (LastLineCache.Snapshot() returns
                // entries in it, no re-sorting), so the expected order here is projected from THAT list
                // rather than duplicated as a second hand-ordered literal (this array's own declaration
                // order — ChangeMap before Territory/ChangePrimaryPlayer — does not match it).
                var lastByType = LastOneShot.ToDictionary(kv => kv.Type, kv => kv.Line);
                var expected = OneShotLineTypes.EmissionOrder
                    .Select(t => (Type: (int)t, Line: lastByType[t]))
                    .ToList();
                Assert.Equal(expected, primedRaw);
            }
            finally
            {
                await consumer.ShutdownAsync(TimeSpan.FromSeconds(3));
                await aggregator.StopAsync(CancellationToken.None);
                await lastLineCache.StopAsync(CancellationToken.None);
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prevInstall);
                try { File.Delete(recordPath); } catch { }
            }
        }

        // [plugin-gated] companion: the repository/GameVersion half of P1.4's assertion. Requires the
        // real FFXIV_ACT_Plugin.dll (the stand-in's SDK-typed ConsumerDataRepository only materializes
        // once the SDK is resolvable) — skips cleanly without it, per P0.3/P1.3's precedent.
        [SkippableFact]
        public async Task Late_stand_in_repository_never_converges_on_a_forwarded_GameVersion_from_priming_alone()
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

            var dump = Path.Combine(Path.GetTempPath(), "fct-p1-4-consume-" + Guid.NewGuid().ToString("N") + ".txt");
            var verify = Path.Combine(Path.GetTempPath(), "fct-p1-4-standin-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var provider = new GameSnapshotProvider();
            var session = new GameSession(bus, provider);
            var aggregator = new GameSnapshotAggregator(bus, provider, NullLogger<GameSnapshotAggregator>.Instance);
            await aggregator.StartAsync(CancellationToken.None);
            // Same production-mirroring wiring as the rawlog gate above — this subscriber's stream set
            // includes rawlog too, so BuildPrimeEvents needs the same sibling singleton wired.
            var lastLineCache = new LastLineCache(bus, NullLogger<LastLineCache>.Instance);
            await lastLineCache.StartAsync(CancellationToken.None);

            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamCombatants, SatelliteProtocol.StreamRepository);
            var consumer = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "late-standin-consumer", session,
                $"--consume \"{dump}\" --subscribe {streams} --stand-in --verify-standin \"{verify}\"",
                lastLineCache: lastLineCache);

            try
            {
                // Fold process/zone state BEFORE the consumer exists (as above) so the repository
                // priming branch has a live pid to seed, same as a real late join would see.
                bus.Emit(new ZoneChanged(0, DateTimeOffset.UtcNow, 999, "Kugane"));
                bus.Emit(new GameProcessChanged(0, DateTimeOffset.UtcNow, 22244));
                // SessionStateChanged now exists and folds into the
                // aggregator's snapshot (P3.4's GameSnapshotAggregator.OnEvent), so fold a REAL,
                // distinctive GameVersion here (never "" or "0.0" — a value no default could coincide
                // with) BEFORE the consumer subscribes. This keeps the gate honest: since P3.5 deleted
                // ConsumerDataRepository's hardcoded stubs, GetGameVersion() now defaults to "" even with
                // NO priming at all (an honest "not yet known" value, ConsumerDataSurface.cs), which would
                // make the OLD `Assert.Equal("", gameVersion)` pass for the wrong reason — coincidence, not
                // a proof of convergence (the same coincidence RepositorySurfaceLiveTests documents for
                // IsChatLogAvailable). Asserting against a distinctive primed value instead proves the real
                // point: a late-joining consumer does NOT converge on host-primed env state, because
                // BuildPrimeEvents has no `repository`-stream SessionStateChanged branch yet (G8/P4.2).
                const string PrimedVersion = "9.9.9.9-primed-not-forwarded";
                bus.Emit(new SessionStateChanged(0, DateTimeOffset.UtcNow, PrimedVersion,
                    GameLanguage.English, GameRegion.Global, TimeSpan.Zero, true));

                await consumer.StartAsync();
                await Task.Delay(2500);   // consumer loads the SDK (Costura), registers the stand-in, subscribes

                // No live event follows — this is the repository surface AS PRIMED, alone.
                await consumer.ShutdownAsync(TimeSpan.FromSeconds(8));

                Assert.True(SpinUntilFile(verify, 5000), "consumer produced no stand-in verify artifact");
                var f = File.ReadAllText(verify).Trim().Split('\t');
                _out.WriteLine($"stand-in verify: [{string.Join(" | ", f)}]");
                Assert.Equal("1", f[0]);   // found in ActPlugins
                Assert.Equal("1", f[1]);   // DataSubscription/DataRepository bound to real SDK types
                var gameVersion = f.Length > 8 ? f[8] : "";

                // THE GATE (deliberately red): the host-side snapshot already holds PrimedVersion (folded
                // above, per P3.4), but BuildPrimeEvents (SatelliteHost.cs ~388-431) has no branch that
                // emits a SessionStateChanged to a late `repository` subscriber (G8) — that is P4.2's job.
                // So the late-joining stand-in's ConsumerDataRepository never Apply()'s one, and
                // GetGameVersion() reads its own before-any-Apply() default ("") instead of the primed value.
                Assert.Equal(PrimedVersion, gameVersion);
            }
            finally
            {
                await consumer.ShutdownAsync(TimeSpan.FromSeconds(3));
                await aggregator.StopAsync(CancellationToken.None);
                await lastLineCache.StopAsync(CancellationToken.None);
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

        // Poll the AutoFlush sink artifact until at least `expected` primed RawLogLine frames have been
        // written (or the timeout elapses). RunSink flushes each forwarded frame live, so this converges as
        // soon as priming completes instead of waiting a fixed, load-fragile budget.
        private static void SpinUntilPrimedRawLines(string path, int expected, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var count = ReadShared(path)
                    .Count(l => GameEventFrame.TryParse(l, out var e) && e is RawLogLine);
                if (count >= expected) return;
                Thread.Sleep(50);
            }
        }

        // The satellite keeps the artifact open (AutoFlush StreamWriter) until process exit; read with a
        // read/write share so a just-closed writer handle never blocks the assert.
        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }
    }
}
