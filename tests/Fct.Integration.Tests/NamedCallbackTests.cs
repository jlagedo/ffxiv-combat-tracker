using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fct.Host;
using Fct.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Fct.Integration.Tests
{
    // ISOLATION-PLAN P6 (named callbacks / Triggernometry peer interop) e2e over the real bridge. A facade
    // callback registered in one satellite is invocable from another: register routes REGISTERCB up to a
    // per-name proxy in the host's shared IPluginRegistry; invoke routes INVOKECB up, the host fans it to
    // every owner (including the origin), each proxy relays it down to its satellite's facade. Two
    // satellites share one host registry so the fan is genuinely cross-satellite. Plugin-free.
    [Collection("satellite-p6")]   // serialize the P6 host-routed-service e2e tests with each other (see SatelliteP6Collection)
    public sealed class NamedCallbackTests
    {
        private readonly ITestOutputHelper _out;
        public NamedCallbackTests(ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public async Task Callback_registered_in_B_is_invocable_from_A()
        {
            var (root, appDir) = SkipUnlessStaged();
            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var recB = Path.Combine(Path.GetTempPath(), "fct-cbB-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var registry = new RegistryService();   // the one shared host registry both satellites fan through

            var reg = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "cb-reg-b", session,
                $"--cb-register peer.spawn \"{recB}\"", registry: registry);
            var inv = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "cb-inv-a", session,
                "--cb-invoke peer.spawn hello", registry: registry);
            try
            {
                await reg.StartAsync();
                await Task.Delay(2500);   // let B register its callback (REGISTERCB reaches the host)
                await inv.StartAsync();
                await Task.Delay(3000);   // let A invoke (after its ~1.5 s settle) and the fan relay down

                var lines = ReadShared(recB);
                _out.WriteLine($"B recorded [{string.Join(",", lines)}]");
                Assert.Contains("hello", lines);
            }
            finally
            {
                await inv.ShutdownAsync(TimeSpan.FromSeconds(3));
                await reg.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(recB); } catch { }
            }
        }

        [SkippableFact]
        public async Task Callback_self_invoke_returns_to_origin()
        {
            var (root, appDir) = SkipUnlessStaged();
            var prev = Environment.GetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar);
            Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, appDir);
            var rec = Path.Combine(Path.GetTempPath(), "fct-cbself-" + Guid.NewGuid().ToString("N") + ".txt");

            var bus = new GameEventBus();
            var session = new GameSession(bus, new GameSnapshotProvider());
            var registry = new RegistryService();

            // One satellite registers AND invokes: the invoke goes up to the host and fans back down to the
            // same satellite (the host is the single fan point), proving origin inclusion.
            var host = new SatelliteHost(NullLoggerFactory.Instance, bus, null, null, "cb-self", session,
                $"--cb-selftest peer.echo self-arg \"{rec}\"", registry: registry);
            try
            {
                await host.StartAsync();
                await Task.Delay(4000);
                var lines = ReadShared(rec);
                _out.WriteLine($"origin recorded [{string.Join(",", lines)}]");
                Assert.Contains("self-arg", lines);
            }
            finally
            {
                await host.ShutdownAsync(TimeSpan.FromSeconds(3));
                Environment.SetEnvironmentVariable(Fct.Logging.AppData.InstallDirEnvVar, prev);
                try { File.Delete(rec); } catch { }
            }
        }

        private static (string root, string appDir) SkipUnlessStaged()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var appDir = Path.Combine(root!, "src", "Fct.App", "bin", ReplayBridgeHarness.Config(), "net10.0-windows");
            var exe = Path.Combine(appDir, "satellite", "Fct.LegacyHost.exe");
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            return (root!, appDir);
        }

        private static string[] ReadShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        }
    }
}
