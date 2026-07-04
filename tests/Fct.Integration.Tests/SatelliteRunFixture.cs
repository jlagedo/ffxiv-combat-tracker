using System.Diagnostics;
using System.IO.Pipes;
using Fct.Bridge;
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
        private NamedPipeServerStream? _server;
        private NamedPipeServerStream? _cmdServer;
        private StreamWriter? _cmdWriter;
        private Task? _drainTask;

        // Signaled by the drain loop as the corresponding frames arrive on the event pipe.
        private readonly TaskCompletionSource<bool> _pluginsEnd =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IntPtr> _pluginHwnd =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public static string OverlayPluginPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Advanced Combat Tracker", "Plugins", "OverlayPlugin", "OverlayPlugin.dll");

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
                        "net10.0-windows", "satellite", "Fct.LegacyHost.exe");
                    return File.Exists(exe) ? exe : null;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private void Launch(string exe)
        {
            var pipeName = "fct-itest-" + Guid.NewGuid().ToString("N");
            // The event/handshake pipe (satellite -> us) and the command pipe (us -> satellite), the
            // real host's exact shape. A clean host boots the satellite with NO plugins; we drive the
            // parser load on demand over -cmd, exactly as the real host does when a user installs
            // FFXIV_ACT_Plugin from the catalog. The event pipe must be drained CONTINUOUSLY for the
            // satellite's whole lifetime: it forwards every log record over that pipe synchronously,
            // so a reader that stops consuming wedges the satellite once the pipe buffer fills.
            _server = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _cmdServer = new NamedPipeServerStream(
                pipeName + "-cmd", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var mainConn = _server.WaitForConnectionAsync();
            var cmdConn = _cmdServer.WaitForConnectionAsync();

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--bridge " + pipeName,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("failed to start satellite");

            if (!mainConn.Wait(TimeSpan.FromSeconds(20)))
                return;
            _drainTask = Task.Run(DrainEventPipe);

            // Boot handshake: READY, then PLUGINS-END (empty roster — nothing is boot-loaded).
            _pluginsEnd.Task.Wait(TimeSpan.FromSeconds(30));
            if (!cmdConn.Wait(TimeSpan.FromSeconds(20)))
                return;
            _cmdWriter = new StreamWriter(_cmdServer) { AutoFlush = true };

            // Drive the on-demand parser load (the production catalog path) and capture the embeddable
            // window it announces. Requires the real FFXIV_ACT_Plugin.dll to be installed. Then load the
            // real OverlayPlugin too so the ring-binding diagnostics (PacketDispatchTests) can run.
            if (PluginPresent)
            {
                _cmdWriter.WriteLine(SatelliteProtocol.FormatLoadPlugin("ffxiv", FfxivPluginPath, "FFXIV_ACT_Plugin"));
                if (_pluginHwnd.Task.Wait(TimeSpan.FromSeconds(90)))
                    WindowHandle = _pluginHwnd.Task.Result;

                if (File.Exists(OverlayPluginPath))
                    _cmdWriter.WriteLine(SatelliteProtocol.FormatLoadPlugin("overlay", OverlayPluginPath, "OverlayPlugin"));
            }

            // The self-test runs at boot; "Started" follows the on-demand parser load. Poll the log.
            var logPath = Path.Combine(Path.GetDirectoryName(exe)!, "s2-ffxiv.log");
            var marker = PluginPresent ? "Started" : "[SelfTest]";
            var logDeadline = DateTime.UtcNow.AddSeconds(PluginPresent ? 90 : 8);
            while (DateTime.UtcNow < logDeadline)
            {
                LogText = ReadLogSafe(logPath);
                if (LogText.Contains(marker)) break;
                Thread.Sleep(500);
            }
            LogText = ReadLogSafe(logPath);
        }

        // Consume every line the satellite sends on the event pipe for as long as it runs, capturing
        // the handshake/PLUGIN frames and discarding the forwarded LOG firehose — mirroring the real
        // host's reader loop.
        private void DrainEventPipe()
        {
            try
            {
                using var reader = new StreamReader(_server!);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (SatelliteProtocol.IsReady(line)) Handshake = line;
                    else if (line.StartsWith(SatelliteProtocol.PluginsEnd, StringComparison.Ordinal))
                        _pluginsEnd.TrySetResult(true);
                    else if (SatelliteProtocol.TryParsePlugin(line, out var pl))
                        _pluginHwnd.TrySetResult(pl.Hwnd);
                }
            }
            catch { /* satellite exited or the fixture is disposing */ }
        }

        // Re-read the still-growing satellite log and wait for a marker to appear (the dispatcher
        // diagnostics land later than the self-test the constructor waits on). Returns the latest
        // text whether or not the marker arrived.
        public string WaitForLog(string marker, int seconds)
        {
            if (ExePath is null) return "";
            var logPath = Path.Combine(Path.GetDirectoryName(ExePath)!, "s2-ffxiv.log");
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            string text = ReadLogSafe(logPath);
            while (DateTime.UtcNow < deadline && !text.Contains(marker))
            {
                Thread.Sleep(500);
                text = ReadLogSafe(logPath);
            }
            return text;
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
            try { _cmdWriter?.Dispose(); } catch { }
            try { _cmdServer?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }
            try { _drainTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [CollectionDefinition("satellite")]
    public sealed class SatelliteCollection : ICollectionFixture<SatelliteRunFixture> { }
}
