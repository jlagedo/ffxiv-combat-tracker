using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.App;

internal sealed class SatellitePlugin
{
    public string Key = "";
    public string Title = "";
    public string Status = "";
    public IntPtr Hwnd = IntPtr.Zero;
}

internal sealed class SatelliteStartResult
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
internal sealed class SatelliteHost
{
    // A single kill-on-close job for the host process: any satellite enrolled here (and the CEF
    // subprocesses it spawns) is terminated by the OS when the host exits or is killed, so no
    // Fct.LegacyHost orphan survives. Static so the job handle lives for the whole host lifetime;
    // null on non-Windows or if the job can't be created (we then just launch without enrollment).
    private static readonly ProcessJob? KillOnExitJob = ProcessJob.TryCreate();

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SatelliteHost> _log;
    private readonly ILogger _satelliteLog;   // category for records forwarded from the satellite

    private NamedPipeServerStream? _server;
    public Process? Process { get; private set; }

    public SatelliteHost(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<SatelliteHost>();
        _satelliteLog = loggerFactory.CreateLogger(LogCategories.Satellite);
    }

    public async Task<SatelliteStartResult> StartAsync(CancellationToken ct = default)
    {
        var pipeName = "fct-bridge-" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(AppContext.BaseDirectory, "satellite", "Fct.LegacyHost.exe");
        if (!File.Exists(exe))
        {
            _log.LogError(LogEvents.SatelliteNotStaged, "Satellite executable not staged at {Exe}", exe);
            throw new FileNotFoundException("Satellite executable not staged", exe);
        }

        _server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _log.LogInformation(LogEvents.SatelliteLaunching, "Launching satellite {Exe} on bridge {Pipe}", exe, pipeName);
        Process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--bridge " + pipeName,
            UseShellExecute = false,
        });

        if (Process != null)
        {
            Process.EnableRaisingEvents = true;
            Process.Exited += (_, _) =>
                _log.LogWarning(LogEvents.SatelliteExited, "Satellite process {Pid} exited with code {ExitCode}",
                    SafePid(Process), SafeExitCode(Process));

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
                if (BridgeLogRecord.TryParse(line, out var rec))
                {
                    ReEmit(rec);
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

    // Drain forwarded LOG frames for the rest of the host lifetime.
    private async Task PumpBridgeAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (BridgeLogRecord.TryParse(line, out var rec))
                    ReEmit(rec);
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
    }

    private static int SafePid(Process? p) { try { return p?.Id ?? -1; } catch { return -1; } }
    private static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }
}
