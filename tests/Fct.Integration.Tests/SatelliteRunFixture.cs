using System.Diagnostics;
using System.IO.Pipes;
using Fct.App;
using Xunit;

namespace Fct.Integration.Tests
{
    // Launches the staged satellite once for the whole test class, captures the bridge
    // handshake (READY + HWND), and waits (best-effort) for the in-process self-test to land
    // in the satellite's log. Tests then assert against the captured state. Disposing kills
    // the satellite and its CEF child processes.
    public sealed class SatelliteRunFixture : IDisposable
    {
        public bool ExeStaged { get; }
        public bool PluginPresent { get; }
        public string Handshake { get; private set; } = "";
        public IntPtr WindowHandle { get; private set; }
        public string LogText { get; private set; } = "";
        public string? ExePath { get; }

        private Process? _process;

        public SatelliteRunFixture()
        {
            ExePath = LocateSatellite();
            ExeStaged = ExePath is not null && File.Exists(ExePath);
            PluginPresent = File.Exists(FfxivPluginPath);

            if (ExeStaged)
            {
                try { Launch(ExePath!); }
                catch { /* leave state empty; tests will surface the missing data */ }
            }
        }

        public static string FfxivPluginPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Advanced Combat Tracker", "Plugins", "FFXIV_ACT_Plugin.dll");

        // Walk up to the repo root (marked by the .slnx) and resolve the staged satellite for
        // whichever configuration this test assembly was built as.
        private static string? LocateSatellite()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
                ? "Release" : "Debug";
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ffxiv-combat-tracker.slnx")))
                {
                    var exe = Path.Combine(dir.FullName, "src", "Fct.App", "bin", config,
                        "net10.0", "satellite", "Fct.LegacyHost.exe");
                    return File.Exists(exe) ? exe : null;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private void Launch(string exe)
        {
            var pipeName = "fct-itest-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--bridge " + pipeName,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("failed to start satellite");

            // Handshake: READY + HWND, with a hard timeout.
            if (!server.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(20)))
                return;

            using var reader = new StreamReader(server);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var readTask = reader.ReadLineAsync();
                if (!readTask.Wait(TimeSpan.FromSeconds(20))) break;
                var line = readTask.Result;
                if (line is null) break;
                if (SatelliteProtocol.IsReady(line)) Handshake = line;
                else if (SatelliteProtocol.TryParseHwnd(line, out var h)) { WindowHandle = h; break; }
            }

            // The self-test runs after the plugins load; poll the satellite log for it.
            var logPath = Path.Combine(Path.GetDirectoryName(exe)!, "s2-ffxiv.log");
            var logDeadline = DateTime.UtcNow.AddSeconds(PluginPresent ? 90 : 8);
            while (DateTime.UtcNow < logDeadline)
            {
                LogText = ReadLogSafe(logPath);
                if (LogText.Contains("[SelfTest]")) break;
                Thread.Sleep(500);
            }
            LogText = ReadLogSafe(logPath);
        }

        private static string ReadLogSafe(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch { }
            _process?.Dispose();
        }
    }

    [CollectionDefinition("satellite")]
    public sealed class SatelliteCollection : ICollectionFixture<SatelliteRunFixture> { }
}
