using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.App;

internal sealed class SatelliteStartResult
{
    public string Handshake = "";
    public IntPtr WindowHandle = IntPtr.Zero;
}

// Launches the net48 satellite (Fct.LegacyHost) and reads its startup messages over a
// named pipe: a READY handshake and the HWND of its window (to embed). The pipe is kept
// open for the host lifetime; it becomes the IPC bridge in later steps.
internal sealed class SatelliteHost
{
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

        await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        var reader = new StreamReader(_server);   // do not dispose: keeps the pipe open
        var result = new SatelliteStartResult();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (SatelliteProtocol.IsReady(line))
                result.Handshake = line;
            else if (SatelliteProtocol.TryParseHwnd(line, out var hwnd))
            {
                result.WindowHandle = hwnd;
                break;
            }
        }
        return result;
    }
}
