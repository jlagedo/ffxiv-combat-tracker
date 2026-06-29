using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

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
// open for the host lifetime; it becomes the IPC bridge in later steps.
internal sealed class SatelliteHost
{
    // A single kill-on-close job for the host process: any satellite enrolled here (and the CEF
    // subprocesses it spawns) is terminated by the OS when the host exits or is killed, so no
    // Fct.LegacyHost orphan survives. Static so the job handle lives for the whole host lifetime;
    // null on non-Windows or if the job can't be created (we then just launch without enrollment).
    private static readonly ProcessJob? KillOnExitJob = ProcessJob.TryCreate();

    private NamedPipeServerStream? _server;
    public Process? Process { get; private set; }

    public async Task<SatelliteStartResult> StartAsync(CancellationToken ct = default)
    {
        var pipeName = "fct-bridge-" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(AppContext.BaseDirectory, "satellite", "Fct.LegacyHost.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Satellite executable not staged", exe);

        _server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--bridge " + pipeName,
            UseShellExecute = false,
        });

        // Tie the satellite to the host's lifetime before it spawns its own children (CEF), so
        // the whole tree dies with the host instead of orphaning.
        if (Process != null && OperatingSystem.IsWindows())
            KillOnExitJob?.AddProcess(Process);

        await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        var reader = new StreamReader(_server);   // do not dispose: keeps the pipe open
        var result = new SatelliteStartResult();

        // Read the handshake and the per-plugin window announcements until PLUGINS-END, with a
        // ceiling so a slow/failed plugin load can't hang the host forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)) != null)
            {
                if (SatelliteProtocol.IsReady(line))
                    result.Handshake = line;
                else if (line == SatelliteProtocol.PluginsEnd)
                    break;
                else if (SatelliteProtocol.TryParsePlugin(line, out var p))
                    result.Plugins.Add(new SatellitePlugin
                    {
                        Key = p.Key, Title = p.Title, Status = p.Status, Hwnd = p.Hwnd,
                    });
                else if (SatelliteProtocol.TryParseHwnd(line, out var hwnd))
                    result.WindowHandle = hwnd;   // primary window (compat); plugins drive the UI
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timed out waiting for PLUGINS-END; return whatever loaded so far.
        }

        return result;
    }
}
