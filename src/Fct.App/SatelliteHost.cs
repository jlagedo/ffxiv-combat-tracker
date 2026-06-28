using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.App;

// Launches the net48 satellite (Fct.LegacyHost) and performs the S0 IPC handshake
// over a named pipe. The host is the pipe server; the satellite connects and sends
// one READY line. Grows into the full bridge in later steps.
internal sealed class SatelliteHost
{
    public Process? Process { get; private set; }

    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        var pipeName = "fct-bridge-" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(AppContext.BaseDirectory, "satellite", "Fct.LegacyHost.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Satellite executable not staged", exe);

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--bridge " + pipeName,
            UseShellExecute = false,
        });

        using (ct.Register(() => { try { server.Dispose(); } catch { } }))
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
        }

        using var reader = new StreamReader(server);
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return line ?? "(no handshake line)";
    }
}
