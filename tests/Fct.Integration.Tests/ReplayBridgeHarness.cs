using System.Diagnostics;
using System.IO.Pipes;

namespace Fct.Integration.Tests
{
    // Shared plumbing for the replay-over-bridge tests (ISOLATION-PLAN P1/P2): locate the staged
    // satellite + slice corpus, run the satellite in `--replay … --bridge` mode over a committed slice,
    // and drain the event pipe as fast as possible (so the forwarder ring never drops) into the raw
    // line list. Used by the wire-path parity gate and the frame-fixture generator.
    internal static class ReplayBridgeHarness
    {
        public static string? RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ffxiv-combat-tracker.slnx"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        public static string Config() =>
            AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
                ? "Release" : "Debug";

        public static string SatelliteExe(string root) =>
            Path.Combine(root, "src", "Fct.App", "bin", Config(), "net10.0-windows", "satellite", "Fct.LegacyHost.exe");

        public static string SliceLog(string root, string slice) =>
            Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".log");

        public static string FramesFixture(string root, string slice) =>
            Path.Combine(root, "tests", "fixtures", "frames", slice + ".frames.tsv");

        // A built P7 fixture consumer plugin (net48 IActPluginV1), e.g. "Fct.Fixtures.TriggerFixture".
        // Built alongside the integration test project (build-only ProjectReference); loaded by path.
        public static string FixturePluginDll(string root, string assemblyName) =>
            Path.Combine(root, "src", assemblyName, "bin", Config(), "net48", assemblyName + ".dll");

        // Launch the satellite replaying <sliceLog> over a fresh bridge pipe, drain every line it sends
        // for the process lifetime, and return them in order. Throws on launch/handshake failure.
        public static List<string> RunAndCollect(string exe, string sliceLog, int maxLines = 100000) =>
            LaunchAndDrain(exe, $"--replay \"{sliceLog}\" {maxLines} --bridge {{0}}");

        // As above but drives the plugin-free replay-satellite (`--replay-frames`) over a committed
        // FrameSession fixture — no game, no FFXIV_ACT_Plugin, so it runs in CI wherever the satellite
        // is staged.
        public static List<string> RunFramesAndCollect(string exe, string fixturePath) =>
            LaunchAndDrain(exe, $"--replay-frames \"{fixturePath}\" --bridge {{0}}");

        private static List<string> LaunchAndDrain(string exe, string argsTemplate)
        {
            var pipeName = "fct-replaybridge-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var conn = server.WaitForConnectionAsync();

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Format(argsTemplate, pipeName),
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("failed to start satellite");

            if (!conn.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("satellite did not connect the bridge pipe");

            var lines = new List<string>();
            var drain = Task.Run(() =>
            {
                try
                {
                    using var reader = new StreamReader(server);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        lock (lines) lines.Add(line);
                }
                catch { /* satellite exited / pipe closed */ }
            });

            if (!proc.WaitForExit(120_000))
                throw new TimeoutException("satellite did not finish the replay in time");
            drain.Wait(TimeSpan.FromSeconds(10));
            lock (lines) return new List<string>(lines);
        }
    }
}
