using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Bridge;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host;

public sealed class SatellitePlugin
{
    public string Key = "";
    public string Title = "";
    public string Status = "";
    public IntPtr Hwnd = IntPtr.Zero;
}

public sealed class SatelliteStartResult
{
    public string Handshake = "";
    public IntPtr WindowHandle = IntPtr.Zero;
    public List<SatellitePlugin> Plugins = new();
}

// Launches the net48 satellite (Fct.LegacyHost) and reads its startup messages over a
// named pipe: a READY handshake and the HWND of its window (to embed). The pipe is kept
// open for the host lifetime and becomes the live log channel: after startup the satellite
// forwards its Serilog records as LOG frames, which we re-emit into the host's logging
// pipeline so the whole stack logs to one place.
public sealed class SatelliteHost : Fct.Host.Plugins.ISatellitePluginChannel
{
    // A single kill-on-close job for the host process: any satellite enrolled here (and the CEF
    // subprocesses it spawns) is terminated by the OS when the host exits or is killed, so no
    // Fct.LegacyHost orphan survives. Static so the job handle lives for the whole host lifetime;
    // null on non-Windows or if the job can't be created (we then just launch without enrollment).
    private static readonly ProcessJob? KillOnExitJob = ProcessJob.TryCreate();

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SatelliteHost> _log;
    private readonly ILogger _satelliteLog;   // category for records forwarded from the satellite
    private readonly IGameEventSink _sink;     // decoded EVT frames land on the net10 event bus
    private readonly INotificationHub? _notifications;
    private readonly ISatelliteNotificationText _text;   // localized notification strings (Fct.App-backed)
    private int _firstEventLogged;

    private NamedPipeServerStream? _server;     // satellite -> host: handshake + forwarded logs
    private NamedPipeServerStream? _cmdServer;  // host -> satellite: load/unload commands
    private StreamWriter? _cmdWriter;
    private readonly object _cmdLock = new();
    private EventWaitHandle? _shutdownEvent;    // host -> satellite: graceful-shutdown signal
    private volatile bool _shuttingDown;        // suppress the "exited" notice during a requested drain
    public Process? Process { get; private set; }

    // Raised when the satellite announces (PLUGIN) or tears down (UNLOADED) a plugin after startup —
    // the shell reconciles its legacy roster live. Pending unload acks are matched by key.
    public event Action<SatellitePlugin>? PluginAnnounced;
    public event Action<string>? PluginUnloaded;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingUnloads
        = new(StringComparer.OrdinalIgnoreCase);

    internal SatelliteHost(ILoggerFactory loggerFactory, IGameEventSink sink,
        INotificationHub? notifications = null, ISatelliteNotificationText? texts = null)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<SatelliteHost>();
        _satelliteLog = loggerFactory.CreateLogger(LogCategories.Satellite);
        _sink = sink;
        _notifications = notifications;
        _text = texts ?? DefaultSatelliteNotificationText.Instance;
    }

    public async Task<SatelliteStartResult> StartAsync(CancellationToken ct = default)
    {
        var pipeName = "fct-bridge-" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(AppData.InstallDirectory, "satellite", "Fct.LegacyHost.exe");
        if (!File.Exists(exe))
        {
            _log.LogError(LogEvents.SatelliteNotStaged, "Satellite executable not staged at {Exe}", exe);
            throw new FileNotFoundException("Satellite executable not staged", exe);
        }

        _server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // A second, host->satellite pipe for load/unload commands. Kept separate from the log/EVT
        // stream (which stays strictly satellite->host) so a user command never interleaves with the
        // high-rate firehose. The satellite connects to it as a client after the log pipe.
        _cmdServer = new NamedPipeServerStream(
            pipeName + "-cmd", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Cross-process graceful-shutdown signal. A named event (not a second pipe) avoids any
        // connection rendezvous or stream contention — it is one bit the satellite waits on. Created
        // before launch so the satellite always finds it; the satellite opens the same name.
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + "-shutdown");

        _log.LogInformation(LogEvents.SatelliteLaunching, "Launching satellite {Exe} on bridge {Pipe}", exe, pipeName);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--bridge " + pipeName,
            UseShellExecute = false,
        };
        // Hand the satellite the host's resolved data root so both processes agree on one location
        // (the satellite sits in a satellite\ subfolder, so its own AppData.Root would differ in DEBUG).
        startInfo.Environment[AppData.RootEnvVar] = AppData.Root;

        // Sandbox the legacy plugins off the real ACT data location: point the satellite's %APPDATA%
        // at <root>\legacy so any plugin/library that reads the APPDATA env var (or expands %APPDATA%)
        // writes into the ecosystem. Note: .NET's GetFolderPath(ApplicationData) reads the shell API,
        // not this env var, so plugins that use it for their own vendor folders are only redirected
        // where they go through the ACT facade's AppDataFolder (which we point at
        // <root>\legacy\Advanced Combat Tracker). Real plugin binaries load via GetFolderPath and are
        // deliberately unaffected — they still come from the real ACT install.
        var legacyRoot = Path.Combine(AppData.Root, "legacy");
        try { Directory.CreateDirectory(legacyRoot); } catch { /* best-effort */ }
        startInfo.Environment["APPDATA"] = legacyRoot;
        Process = Process.Start(startInfo);

        if (Process != null)
        {
            Process.EnableRaisingEvents = true;
            Process.Exited += (_, _) =>
            {
                _log.LogWarning(LogEvents.SatelliteExited, "Satellite process {Pid} exited with code {ExitCode}",
                    SafePid(Process), SafeExitCode(Process));
                if (!_shuttingDown)
                    _notifications?.Publish(NotificationSeverity.Warning, _text.SourceClassicEngine,
                        _text.EngineStoppedTitle, _text.EngineStoppedBody);
            };

            // Tie the satellite to the host's lifetime before it spawns its own children (CEF), so
            // the whole tree dies with the host instead of orphaning.
            if (OperatingSystem.IsWindows())
            {
                if (KillOnExitJob != null)
                    KillOnExitJob.AddProcess(Process);
                else
                    _log.LogWarning(LogEvents.JobObjectUnavailable,
                        "Kill-on-close job unavailable; satellite {Pid} is not tied to host lifetime", Process.Id);
            }
        }

        await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _log.LogDebug(LogEvents.BridgeConnected, "Satellite connected to bridge {Pipe}", pipeName);

        // Accept the command-pipe connection in the background; the writer is ready once the satellite
        // connects. User-initiated load/unload commands happen well after startup, so this races safely.
        _ = AcceptCommandPipeAsync(ct);

        var reader = new StreamReader(_server);   // do not dispose: keeps the pipe open
        var result = new SatelliteStartResult();

        // Read the handshake and the per-plugin window announcements until PLUGINS-END, with a
        // ceiling so a slow/failed plugin load can't hang the host forever. Forwarded LOG frames
        // may already be interleaved here, so route those out as they arrive.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)) != null)
            {
                if (BridgeLogRecord.TryParse(line, out var rec) && rec is not null)
                {
                    ReEmit(rec);
                }
                else if (TryEmitGameEvent(line))
                {
                    // Live game event forwarded from the satellite (piece C).
                }
                else if (SatelliteProtocol.IsReady(line))
                {
                    result.Handshake = line;
                    _log.LogInformation(LogEvents.BridgeHandshake, "Satellite handshake: {Handshake}", line);
                }
                else if (line == SatelliteProtocol.PluginsEnd)
                {
                    break;
                }
                else if (SatelliteProtocol.TryParsePlugin(line, out var p))
                {
                    result.Plugins.Add(new SatellitePlugin
                    {
                        Key = p.Key, Title = p.Title, Status = p.Status, Hwnd = p.Hwnd,
                    });
                    _log.LogInformation(LogEvents.BridgePluginAnnounced,
                        "Plugin window: {Key} '{Title}' (status '{Status}', hwnd 0x{Hwnd:X})",
                        p.Key, p.Title, p.Status, p.Hwnd.ToInt64());
                }
                else if (SatelliteProtocol.TryParseHwnd(line, out var hwnd))
                {
                    result.WindowHandle = hwnd;   // primary window (compat); plugins drive the UI
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _log.LogWarning(LogEvents.SatelliteHandshakeTimeout,
                "Timed out after 60s waiting for satellite PLUGINS-END; continuing with {PluginCount} plugin(s)",
                result.Plugins.Count);
        }

        // Keep draining the pipe for the host lifetime so the satellite's forwarded log records
        // continue to flow into our pipeline.
        _ = Task.Run(() => PumpBridgeAsync(reader, ct), ct);

        return result;
    }

    private async Task AcceptCommandPipeAsync(CancellationToken ct)
    {
        try
        {
            if (_cmdServer is null) return;
            await _cmdServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var writer = new StreamWriter(_cmdServer) { AutoFlush = true };
            lock (_cmdLock) _cmdWriter = writer;
            _log.LogDebug(LogEvents.BridgeConnected, "Satellite connected to command pipe");
        }
        catch (OperationCanceledException) { /* host shutting down */ }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.BridgeReaderStopped, ex, "Command pipe accept faulted; live load/unload unavailable");
        }
    }

    /// <summary>Ask the satellite to load a real-legacy plugin DLL and host it. Fire-and-forget: the
    /// satellite announces the result with a PLUGIN frame (surfaced via <see cref="PluginAnnounced"/>).</summary>
    public bool RequestLoadPlugin(string key, string dllPath, string title)
        => SendCommand(SatelliteProtocol.FormatLoadPlugin(key, dllPath, title));

    /// <summary>Ask the satellite to tear down and unload a legacy plugin, awaiting its UNLOADED ack.
    /// Returns false if the satellite is unreachable or does not ack within <paramref name="timeout"/>.</summary>
    public async Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingUnloads[key] = tcs;
        try
        {
            if (!SendCommand(SatelliteProtocol.FormatUnloadPlugin(key))) return false;
            using var cts = new CancellationTokenSource(timeout);
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally { _pendingUnloads.TryRemove(key, out _); }
    }

    private bool SendCommand(string frame)
    {
        lock (_cmdLock)
        {
            if (_cmdWriter is null)
            {
                _log.LogWarning(LogEvents.BridgeReaderStopped, "Cannot send command; satellite command pipe not connected");
                return false;
            }
            try { _cmdWriter.WriteLine(frame); return true; }
            catch (Exception ex)
            {
                _log.LogWarning(LogEvents.BridgeReaderStopped, ex, "Failed to send satellite command");
                return false;
            }
        }
    }

    // Drain forwarded LOG frames for the rest of the host lifetime.
    private async Task PumpBridgeAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (BridgeLogRecord.TryParse(line, out var rec) && rec is not null)
                    ReEmit(rec);
                else if (TryEmitGameEvent(line))
                    continue;
                else if (SatelliteProtocol.TryParsePlugin(line, out var p))
                    PluginAnnounced?.Invoke(new SatellitePlugin { Key = p.Key, Title = p.Title, Status = p.Status, Hwnd = p.Hwnd });
                else if (SatelliteProtocol.TryParseUnloaded(line, out var key, out var ok))
                {
                    if (_pendingUnloads.TryGetValue(key, out var tcs)) tcs.TrySetResult(ok);
                    PluginUnloaded?.Invoke(key);
                }
                else
                    _log.LogDebug(LogEvents.BridgeFrameMalformed, "Unrecognized bridge frame after handshake: {Frame}", line);
            }
            _log.LogInformation(LogEvents.BridgeReaderStopped, "Bridge closed by satellite; log forwarding ended");
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.BridgeReaderStopped, ex, "Bridge reader faulted");
        }
    }

    // Ask the satellite to drain its plugins (DeInitPlugin -> each plugin saves its state) and
    // exit cleanly, then wait for it to do so. Process exit is the completion signal: the
    // satellite runs the deinit loop synchronously before Application.Exit(), so once the process
    // is gone the plugins have persisted. On timeout we return and let the kill-on-close job reap
    // the (still-running) satellite at host exit, as before — state may be lost in that case, but
    // the host is not blocked. Idempotent and safe if the satellite never started.
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        _shuttingDown = true;
        var process = Process;
        if (process is null || _shutdownEvent is null) return;
        try { if (process.HasExited) return; } catch { return; }

        _log.LogInformation(LogEvents.SatelliteShutdownRequested,
            "Requesting graceful satellite shutdown (pid {Pid}, timeout {Timeout:0.#}s)",
            SafePid(process), timeout.TotalSeconds);
        try
        {
            _shutdownEvent.Set();
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.SatelliteShutdownTimeout, ex,
                "Could not signal SHUTDOWN to satellite; leaving it for the kill-on-close job");
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(LogEvents.SatelliteShutdownTimeout,
                "Satellite did not exit within {Timeout:0.#}s of SHUTDOWN; the kill-on-close job will reap it",
                timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.SatelliteShutdownTimeout, ex, "Waiting for satellite exit faulted");
        }
    }

    // Re-emit a forwarded satellite record under its original category/level/EventId. The message
    // is already rendered on the satellite, so pass it as a single property (no re-templating), and
    // fold any forwarded exception text into the message (it is a string across the process boundary).
    private void ReEmit(BridgeLogRecord rec)
    {
        var logger = string.IsNullOrEmpty(rec.Category) ? _satelliteLog : _loggerFactory.CreateLogger(rec.Category);
        var message = rec.Exception is null
            ? rec.Message
            : rec.Message + Environment.NewLine + rec.Exception;
        logger.Log(rec.Level, new EventId(rec.EventId, rec.EventName), "{SatelliteMessage}", message);
        MaybeNotify(rec, message);
    }

    // Surface the satellite records a user should see: ACT's NotificationAdd (the legacy "toast"),
    // legacy exceptions, and plugin load failures — plus any genuine warning/error. Info/debug
    // chatter is left to the logs so the notification feed stays signal.
    private void MaybeNotify(BridgeLogRecord rec, string message)
    {
        if (_notifications is null) return;
        switch (rec.EventId)
        {
            case 2403: // ActNotification — ACT NotificationAdd
                _notifications.Publish(NotificationSeverity.Info, _text.SourceClassicPlugin, StripNotifyPrefix(rec.Message));
                return;
            case 2402: // ActException
                _notifications.Publish(NotificationSeverity.Error, _text.SourceClassicPlugin, _text.PluginReportedErrorTitle, message);
                return;
            case 2103: // PluginLoadFailed
            case 2104: // PluginNotFound
                _notifications.Publish(NotificationSeverity.Warning, _text.SourceClassicEngine, _text.PluginLoadFailedTitle, message);
                return;
        }

        if (rec.Level >= LogLevel.Error)
            _notifications.Publish(NotificationSeverity.Error, _text.SourceClassicEngine, _text.EngineErrorTitle, message);
        else if (rec.Level == LogLevel.Warning)
            _notifications.Publish(NotificationSeverity.Warning, _text.SourceClassicEngine, _text.EngineWarningTitle, message);
    }

    private static string StripNotifyPrefix(string message)
    {
        var m = message?.Trim() ?? string.Empty;
        return m.StartsWith("[Notify]", StringComparison.OrdinalIgnoreCase) ? m["[Notify]".Length..].Trim() : m;
    }

    // Decode an EVT frame forwarded from the satellite and publish it onto the net10 event bus. The
    // host re-stamps Sequence from its own sink so the bus keeps one coherent per-session ordering
    // (the frame carries only the timestamp). Returns false for any non-EVT line so the caller can
    // fall through to handshake/malformed handling. The first event logs at Information as headless
    // proof-of-wire; the rest at Trace so the high-rate firehose never floods the log.
    private bool TryEmitGameEvent(string line)
    {
        if (!GameEventFrame.TryParse(line, out var evt) || evt is null)
            return false;
        try
        {
            _sink.Emit(evt with { Sequence = _sink.NextSequence() });
            if (Interlocked.Exchange(ref _firstEventLogged, 1) == 0)
                _log.LogInformation(LogEvents.BridgeEventDecoded,
                    "First live game event forwarded from satellite: {EventType}", evt.GetType().Name);
            else
                _log.LogTrace(LogEvents.BridgeEventDecoded, "Bridge event {EventType}", evt.GetType().Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.BridgeEventDecodeFailed, ex, "Failed to emit decoded bridge event");
        }
        return true;
    }

    private static int SafePid(Process? p) { try { return p?.Id ?? -1; } catch { return -1; } }
    private static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }
}
