using System.IO.Pipes;
using Fct.Abstractions;
using Fct.Bridge;
using Xunit;

namespace Fct.Integration.Tests
{
    // PIPELINE-COMPLETENESS-PLAN P3.3 done-when [plugin-gated]: boot a REAL producer satellite through
    // the production catalog path (empty boot -> LOADPLUGIN, exactly SatelliteRunFixture's shape) and
    // observe, over the REAL bridge event pipe, that BridgeForwarder's new producer taps land — a STATE
    // frame (SessionStateChanged, from EmitInitialRepositoryState once the real FFXIV_ACT_Plugin's SDK
    // binds). Deliberately NOT `--replay` mode (ReplayBridgeHarness.RunAndCollect): that driver blocks the
    // satellite's UI thread synchronously feeding log lines, so BridgeForwarder's SDK-discovery Timer
    // (a WinForms Timer, ticked only by the message loop) never gets a turn and the SDK never binds —
    // confirmed empirically (only ACT-hub taps: ZONE/ZCHG/SETENC/SWING/ENDC ever appeared that way).
    // Skips cleanly without the satellite staged or the plugin installed.
    public sealed class SessionStateReplayTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public SessionStateReplayTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

        [SkippableFact]
        public void Real_producer_satellite_emits_a_STATE_frame_once_the_plugin_SDK_binds()
        {
            var root = ReplayBridgeHarness.RepoRoot();
            Skip.If(root is null, "repo root not found");
            var exe = ReplayBridgeHarness.SatelliteExe(root!);
            Skip.IfNot(File.Exists(exe), $"satellite not staged at {exe}");
            Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath),
                $"FFXIV_ACT_Plugin not installed at {SatelliteRunFixture.FfxivPluginPath}");

            var pipeName = "fct-p33-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var cmdServer = new NamedPipeServerStream(
                pipeName + "-cmd", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var mainConn = server.WaitForConnectionAsync();
            var cmdConn = cmdServer.WaitForConnectionAsync();

            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--bridge " + pipeName,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("failed to start satellite");

            try
            {
                Assert.True(mainConn.Wait(TimeSpan.FromSeconds(20)), "satellite did not connect the event pipe");

                var lines = new List<string>();
                var pluginsEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var drain = Task.Run(() =>
                {
                    try
                    {
                        using var reader = new StreamReader(server);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lock (lines) lines.Add(line);
                            if (line.StartsWith(SatelliteProtocol.PluginsEnd, StringComparison.Ordinal))
                                pluginsEnd.TrySetResult(true);
                        }
                    }
                    catch { /* satellite exited / pipe closed */ }
                });

                Assert.True(pluginsEnd.Task.Wait(TimeSpan.FromSeconds(30)), "boot handshake (PLUGINS-END) never arrived");
                Assert.True(cmdConn.Wait(TimeSpan.FromSeconds(20)), "satellite did not connect the command pipe");
                using var cmdWriter = new StreamWriter(cmdServer) { AutoFlush = true };

                // The production on-demand catalog path: load the real parser, exactly as the host does
                // when a user installs FFXIV_ACT_Plugin. This drives BridgeForwarder.Start() ->
                // OnParserLoaded() -> the discovery Timer -> TryBindFfxiv() -> EmitInitialRepositoryState().
                cmdWriter.WriteLine(SatelliteProtocol.FormatLoadPlugin(
                    "ffxiv", SatelliteRunFixture.FfxivPluginPath, "FFXIV_ACT_Plugin"));

                bool sawState() { lock (lines) return lines.Any(l => l.StartsWith("EVT STATE\t", StringComparison.Ordinal)); }
                var deadline = DateTime.UtcNow.AddSeconds(90);   // the real plugin's async init can be slow
                while (DateTime.UtcNow < deadline && !sawState())
                    Thread.Sleep(250);

                List<string> captured;
                lock (lines) captured = new List<string>(lines);

                var rawStateLines = captured.Where(l => l.StartsWith("EVT STATE\t", StringComparison.Ordinal)).ToList();
                var tagCounts = captured
                    .Where(l => l.StartsWith(GameEventFrame.Prefix, StringComparison.Ordinal))
                    .GroupBy(l => l.Split('\t')[0])
                    .Select(g => $"{g.Key}={g.Count()}");
                Assert.True(rawStateLines.Count > 0,
                    $"no raw 'EVT STATE' frame observed on the bridge (captured {captured.Count} lines; tags: {string.Join(", ", tagCounts)})");
                foreach (var l in rawStateLines) _out.WriteLine("STATE frame: " + l);
                _out.WriteLine("All tag counts: " + string.Join(", ", tagCounts));

                var decoded = new List<GameEvent>();
                foreach (var line in rawStateLines)
                    if (GameEventFrame.TryParse(line, out var evt) && evt is not null) decoded.Add(evt);
                var states = decoded.OfType<SessionStateChanged>().ToList();
                Assert.NotEmpty(states);   // may legitimately be >1 (bind + any ProcessChanged re-emit)

                // GameVersion is "" for a headless/unknown version (§3/P0.3) — never the "0.0" placeholder.
                Assert.All(states, s => Assert.NotEqual("0.0", s.GameVersion));
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }
}
