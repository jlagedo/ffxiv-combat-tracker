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
    // ISOLATION-PLAN P5 stand-in gate [plugin-gated]: a plugin-free consumer satellite runs --stand-in,
    // which registers a synthetic FFXIV_ACT_Plugin in ActPlugins (exact title/status) exposing the SDK
    // DataSubscription/DataRepository — all backed by host-routed frames. The host fans synthetic log-line
    // + repository frames down; the satellite folds them into the stand-in, then reflects it exactly as
    // OverlayPlugin's FFXIVRepository does (title scan → cast DataSubscription/DataRepository to the real
    // SDK types → poll GetCombatantList) and records the result. Proves an unmodified consumer can
    // discover + bind the parser in an isolated, parser-free satellite (Costura SDK-resolution included).
    // Requires FFXIV_ACT_Plugin.dll installed (the SDK types) — skips cleanly without it.
    public sealed class ConsumerStandInTests
    {
        private readonly ITestOutputHelper _out;
        public ConsumerStandInTests(ITestOutputHelper output) => _out = output;

        private static Actor Combatant(uint id, string name, uint hp, uint maxHp) => new(
            id, 0, ActorKind.Player, 24, 90, name, hp, maxHp, 0, 0, null, default,
            0, "", 0, 0, 0, 0, 0, PartyMembership.Party, false,
            System.Array.Empty<StatusEffect>(), System.Array.Empty<EnmityEntry>());

        [SkippableFact]
        public async Task Stand_in_is_discovered_and_binds_the_SDK_surface_from_host_routed_frames()
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
            var dump = Path.Combine(Path.GetTempPath(), "fct-consume-" + Guid.NewGuid().ToString("N") + ".txt");
            var verify = Path.Combine(Path.GetTempPath(), "fct-standin-" + Guid.NewGuid().ToString("N") + ".txt");

            const int lineCount = 16;
            var streams = string.Join(",", SatelliteProtocol.StreamSwings, SatelliteProtocol.StreamRawLog,
                SatelliteProtocol.StreamCombatants, SatelliteProtocol.StreamRepository);

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "consumer", session,
                $"--consume \"{dump}\" --subscribe {streams} --stand-in --verify-standin \"{verify}\"");
            try
            {
                await host.StartAsync();
                await Task.Delay(2500);   // consumer loads the SDK (Costura), registers the stand-in, subscribes

                for (int i = 0; i < lineCount; i++)
                    bus.Emit(new RawLogLine(0, DateTimeOffset.Now, LogMessageType.ChatLog,
                        $"00|line-{i}", string.Empty));
                bus.Emit(new RepositorySnapshot(0, DateTimeOffset.Now, new[]
                {
                    Combatant(0x1000, "You", 50, 100),
                    Combatant(0x2000, "Boss", 900000, 1000000),
                }));
                await Task.Delay(1500);   // fan-out + fold drain

                await host.ShutdownAsync(TimeSpan.FromSeconds(8));   // consumer flushes the stand-in verify artifact

                Assert.True(SpinUntilFile(verify, 5000), "consumer produced no stand-in verify artifact");
                var f = File.ReadAllText(verify).Trim().Split('\t');
                _out.WriteLine($"stand-in verify: [{string.Join(" | ", f)}]");
                Assert.Equal("1", f[0]);                                    // found in ActPlugins
                Assert.Equal("1", f[1]);                                    // DataSubscription/DataRepository bound to real SDK types
                Assert.Equal(lineCount, int.Parse(f[2], CultureInfo.InvariantCulture)); // LogLine raised per fanned line
                Assert.Equal(2, int.Parse(f[3], CultureInfo.InvariantCulture));         // GetCombatantList = the snapshot mirror
                Assert.Equal("FFXIV_ACT_Plugin", f[4]);
                Assert.Equal("FFXIV_ACT_Plugin Started.", f[5]);
                // P9a: _iocContainer is the real parser Microsoft.MinIoC.Container resolving ILogFormat +
                // ILogOutput — Hojoring's XIVPluginHelper.Attach() gate passes against the stand-in.
                Assert.Equal("1", f[7]);
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
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
