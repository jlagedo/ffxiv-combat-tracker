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
// pipeline so the whole stack logs to one place. Load/unload are per-satellite primitives
// (RequestLoadPlugin/RequestUnloadPluginAsync); the SatelliteRouter owns the package→satellite
// routing that ISatellitePluginChannel exposes to the installer.
public sealed class SatelliteHost
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
    private readonly string _satelliteId;      // host-assigned identity (P3): launch arg + log attribution
    private readonly IGameSession? _session;    // downstream fan-out source (P4): bus + snapshot to prime
    private readonly object _egressLock = new();
    private SatelliteEgress? _egress;           // the host→satellite fan-out for this satellite's SUBSCRIBE
    private int _firstEventLogged;

    /// <summary>The host-assigned identity this satellite hosts (P3). "ffxiv" for the single-satellite path.</summary>
    public string SatelliteId => _satelliteId;

    /// <summary>The downstream fan-out counters (P4 <see cref="SatelliteEgress"/>) for this satellite, or
    /// null before it has SUBSCRIBEd. The P9b soak reads <c>Dropped</c> (zero steady-state budget) and
    /// <c>Sent</c> off directly-constructed hosts.</summary>
    internal (long Sent, long Dropped)? EgressCounters
    {
        get { lock (_egressLock) return _egress is null ? null : (_egress.Sent, _egress.Dropped); }
    }

    /// <summary>The one-shot line-state cache (P4.1) this satellite's <c>BuildPrimeEvents</c> reads a
    /// late <c>rawlog</c> subscriber's priming lines from (P4.2), or null when no cache is wired (e.g.
    /// most existing tests construct a <see cref="SatelliteHost"/> directly without one).</summary>
    internal ILastLineCache? LastLineCache => _lastLineCache;

    /// <summary>Raised when the satellite process exits UNEXPECTEDLY (not during a requested shutdown),
    /// carrying the exit code. The <see cref="SatelliteSupervisor"/> uses it to drive restart/quarantine.</summary>
    public event Action<int>? ProcessExited;

    private NamedPipeServerStream? _server;     // satellite -> host: handshake + forwarded logs
    private NamedPipeServerStream? _cmdServer;  // host -> satellite: load/unload commands
    private StreamWriter? _cmdWriter;
    private readonly object _cmdLock = new();
    // Completes once the command pipe is connected (true) or gave up (false). The router awaits this
    // before forwarding a LOADPLUGIN so it never races the background command-pipe accept — a fresh
    // instance per (re)start means a restarted satellite gets a fresh signal automatically.
    private readonly TaskCompletionSource<bool> _cmdReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EventWaitHandle? _shutdownEvent;    // host -> satellite: graceful-shutdown signal
    private volatile bool _shuttingDown;        // suppress the "exited" notice during a requested drain
    public Process? Process { get; private set; }

    // Raised when the satellite announces (PLUGIN) or tears down (UNLOADED) a plugin after startup —
    // the shell reconciles its legacy roster live. Pending unload acks are matched by key.
    public event Action<SatellitePlugin>? PluginAnnounced;
    public event Action<string>? PluginUnloaded;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingUnloads
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _extraArgs;         // extra launch args (test seam: e.g. the --sink mode)

    // Host-routed services (P6) — the one shared singletons every satellite fans through. Audio: a
    // satellite's producer calls fan to registered sinks; its sink registration installs a terminal proxy
    // (below). Registry/rawLog carry the named-callback and custom-log-line paths (later P6 slices).
    private readonly IAudioOutput? _audio;
    private readonly IPluginRegistry? _registry;
    private readonly IRawLogLineEmitter? _rawLog;
    private readonly ILastLineCache? _lastLineCache;   // one-shot line-state priming source (P4.2 reads this)
    private readonly object _sinkLock = new();
    private IDisposable? _audioSink;            // this satellite's terminal audio sink proxy (P6)
    private bool _sinkTts, _sinkSound;          // which slots its plugin has taken over
    private readonly object _cbLock = new();
    private readonly Dictionary<string, IDisposable> _callbackProxies = new(StringComparer.Ordinal); // named-callback proxies (P6)

    internal SatelliteHost(ILoggerFactory loggerFactory, IGameEventSink sink,
        INotificationHub? notifications = null, ISatelliteNotificationText? texts = null,
        string satelliteId = "ffxiv", IGameSession? session = null, string extraArgs = "",
        IAudioOutput? audio = null, IPluginRegistry? registry = null, IRawLogLineEmitter? rawLog = null,
        ILastLineCache? lastLineCache = null)
    {
        _loggerFactory = loggerFactory;
        _satelliteId = string.IsNullOrWhiteSpace(satelliteId) ? "ffxiv" : satelliteId;
        _session = session;
        _extraArgs = extraArgs ?? "";
        _audio = audio;
        _registry = registry;
        _rawLog = rawLog;
        _lastLineCache = lastLineCache;
        _log = loggerFactory.CreateLogger<SatelliteHost>();
        // Forwarded records land under a per-satellite category so N satellites are attributable in the
        // host log (Fct.Satellite.<id>); the single-satellite path keeps Fct.Satellite.ffxiv.
        _satelliteLog = loggerFactory.CreateLogger(LogCategories.Satellite + "." + _satelliteId);
        _sink = sink;
        _notifications = notifications;
        _text = texts ?? DefaultSatelliteNotificationText.Instance;
    }

    public async Task<SatelliteStartResult> StartAsync(CancellationToken ct = default)
    {
        var pipeName = "fct-bridge-" + Guid.NewGuid().ToString("N");

        // The launch prefix — named-pipe + shutdown-event creation, the legacy-root mkdir, and
        // Process.Start — is synchronous kernel/OS work. The shell spawns satellites from the UI thread
        // (one per installed package, sequentially), so run it on the thread pool to keep startup off
        // the frame.
        await Task.Run(() => LaunchProcess(pipeName), ct).ConfigureAwait(false);

        await _server!.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _log.LogDebug(LogEvents.BridgeConnected, "Satellite connected to bridge {Pipe}", pipeName);

        // Accept the command-pipe connection in the background; the writer is ready once the satellite
        // connects. User-initiated load/unload commands happen well after startup, so this races safely.
        _ = AcceptCommandPipeAsync(ct);

        var reader = new StreamReader(_server);   // do not dispose: keeps the pipe open
        var result = new SatelliteStartResult();
        return await ReadHandshakeAsync(reader, result, ct).ConfigureAwait(false);
    }

    // Synchronous process launch: create the pipes + shutdown event and start (then lifetime-bind) the
    // satellite process. Runs on the thread pool via StartAsync so the caller's thread never blocks.
    private void LaunchProcess(string pipeName)
    {
        var exe = Path.Combine(AppData.InstallDirectory, "satellite", "Fct.LegacyHost.exe");
        if (!File.Exists(exe))
        {
            _log.LogError(LogEvents.SatelliteNotStaged, "Satellite executable not staged at {Exe}", exe);
            throw new FileNotFoundException("Satellite executable not staged", exe);
        }

        // Explicit kernel buffer sizes on both pipes: a byte-mode pipe created with the default (0)
        // buffer size has ZERO write quota — every write is a rendezvous that only completes while the
        // peer is actively blocked in a read. A real buffer decouples writer and reader, so a satellite
        // thread logging up the event pipe never stalls on a momentarily-busy host pump, and a host
        // command/fan-out write never stalls on a momentarily-busy satellite reader.
        const int PipeBufferBytes = 1 << 20;
        _server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: PipeBufferBytes, outBufferSize: 0);

        // A second, host->satellite pipe for load/unload commands. Kept separate from the log/EVT
        // stream (which stays strictly satellite->host) so a user command never interleaves with the
        // high-rate firehose. The satellite connects to it as a client after the log pipe.
        _cmdServer = new NamedPipeServerStream(
            pipeName + "-cmd", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: PipeBufferBytes);

        // Cross-process graceful-shutdown signal. A named event (not a second pipe) avoids any
        // connection rendezvous or stream contention — it is one bit the satellite waits on. Created
        // before launch so the satellite always finds it; the satellite opens the same name.
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + "-shutdown");

        _log.LogInformation(LogEvents.SatelliteLaunching, "Launching satellite {Exe} on bridge {Pipe}", exe, pipeName);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--bridge " + pipeName + " --satellite-id " + _satelliteId
                + (_extraArgs.Length == 0 ? "" : " " + _extraArgs),
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
                var code = SafeExitCode(Process);
                _log.LogWarning(LogEvents.SatelliteExited, "Satellite '{Id}' process {Pid} exited with code {ExitCode}",
                    _satelliteId, SafePid(Process), code);
                // Release host-side service registrations for a crashed satellite so its terminal audio
                // sink stops swallowing peers' audio (a restart re-registers). Safe for a clean exit too.
                DisposeServiceRegistrations();
                if (!_shuttingDown)
                {
                    _notifications?.Publish(NotificationSeverity.Warning, _text.SourceClassicEngine,
                        _text.EngineStoppedTitle, _text.EngineStoppedBody);
                    // Drive supervision (restart/backoff/quarantine) — only for an UNEXPECTED exit.
                    ProcessExited?.Invoke(code);
                }
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
    }

    // Read the satellite handshake + per-plugin window announcements off the connected pipe, then leave
    // the pipe draining for the host lifetime. Split out of StartAsync so the launch prefix can run on
    // the thread pool while this async read stays on the caller's continuation flow.
    private async Task<SatelliteStartResult> ReadHandshakeAsync(
        StreamReader reader, SatelliteStartResult result, CancellationToken ct)
    {
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
                    // Verify the process we connected is the identity we launched (v2 handshake). A v1
                    // satellite echoes no id ("") and is accepted as-is; a mismatch means crossed pipes.
                    var echoedId = SatelliteProtocol.ReadySatelliteId(line);
                    if (echoedId.Length != 0 && !string.Equals(echoedId, _satelliteId, StringComparison.Ordinal))
                        _log.LogWarning(LogEvents.BridgeHandshake,
                            "Satellite handshake id '{Echoed}' does not match expected '{Expected}'", echoedId, _satelliteId);
                    _log.LogInformation(LogEvents.BridgeHandshake, "Satellite '{Id}' handshake: {Handshake}", _satelliteId, line);
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
                else if (line.StartsWith(SatelliteProtocol.SubscribePrefix, StringComparison.Ordinal))
                {
                    HandleSubscribe(line);
                }
                else if (TryHandleServiceUpstream(line))
                {
                    // A host-routed service command (audio produce / sink registration) — P6.
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
            _cmdReady.TrySetResult(true);
            _log.LogDebug(LogEvents.BridgeConnected, "Satellite connected to command pipe");
        }
        catch (OperationCanceledException) { _cmdReady.TrySetResult(false); /* host shutting down */ }
        catch (Exception ex)
        {
            _cmdReady.TrySetResult(false);
            _log.LogWarning(LogEvents.BridgeReaderStopped, ex, "Command pipe accept faulted; live load/unload unavailable");
        }
    }

    /// <summary>Await the command pipe becoming writable (the background accept completes after the
    /// satellite connects — slow for a consumer that loads the SDK stand-in). Returns false if the pipe
    /// never connected or the wait timed out, so the caller can surface "not running yet".</summary>
    internal async Task<bool> WaitForCommandChannelAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using (cts.Token.Register(() => _cmdReady.TrySetResult(false)))
                return await _cmdReady.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
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

    // A satellite declared the downstream stream set its facade needs (P4). Build the filter, prime it
    // from the current snapshot (so a late joiner converges — the same answer ACT gives a plugin enabled
    // mid-session), then stand up the per-satellite egress that fans exactly those streams down the
    // command pipe with its own bounded ring. A re-SUBSCRIBE replaces the previous egress.
    private void HandleSubscribe(string line)
    {
        if (_session is null)
        {
            _log.LogWarning(LogEvents.BridgeFrameMalformed, "SUBSCRIBE from '{Id}' ignored: no session wired", _satelliteId);
            return;
        }
        if (!SatelliteProtocol.TryParseSubscribe(line, out var tokens)) return;
        var filter = StreamCatalog.ToFilter(tokens);
        if (filter is null)
        {
            _log.LogWarning(LogEvents.BridgeFrameMalformed,
                "SUBSCRIBE from '{Id}' matched no known streams: [{Streams}]", _satelliteId, string.Join(",", tokens));
            return;
        }

        lock (_egressLock)
        {
            _egress?.Dispose();
            // Snapshot-on-subscribe: the priming frames ride the egress ring ahead of the live fan-out,
            // so this (bridge-pump) thread never writes to the command pipe itself — a stalled satellite
            // reader must never be able to wedge the pump (it is also the event-pipe drainer).
            _egress = new SatelliteEgress(_satelliteId, _session.Events, filter, SendDownstream,
                prime: BuildPrimeEvents(tokens));
        }
        _log.LogInformation(LogEvents.BridgeHandshake,
            "Satellite '{Id}' subscribed to downstream streams [{Streams}]", _satelliteId, string.Join(",", tokens));
    }

    // Build the priming frames for a newly-subscribed satellite from the current snapshot, so it
    // converges without waiting for the next live change (IGameSnapshot → zone/party/player/combatant
    // frames). Pure — the caller hands these to the egress ring; nothing here touches the pipe.
    private List<GameEvent> BuildPrimeEvents(string[] tokens)
    {
        var events = new List<GameEvent>();
        try
        {
            var snap = _session!.Snapshot();
            var now = DateTimeOffset.UtcNow;
            bool zoneParty = Array.IndexOf(tokens, SatelliteProtocol.StreamZoneParty) >= 0;
            bool combatants = Array.IndexOf(tokens, SatelliteProtocol.StreamCombatants) >= 0;
            bool repository = Array.IndexOf(tokens, SatelliteProtocol.StreamRepository) >= 0;
            bool rawlog = Array.IndexOf(tokens, SatelliteProtocol.StreamRawLog) >= 0;

            if (zoneParty)
            {
                events.Add(new ZoneChanged(0, now, snap.Zone.Id, snap.Zone.Name));
                if (snap.Player is { } me)
                    events.Add(new PrimaryPlayerChanged(0, now, me.Id, me.Name));
                var members = new List<uint>();
                foreach (var m in snap.Party.Members) members.Add(m.Id);
                events.Add(new PartyChanged(0, now, members, snap.Party.Size));
            }
            if (combatants)
                foreach (var a in snap.Actors)
                    events.Add(new CombatantAdded(0, now, a));
            if (repository)
            {
                // The repository combatant list refreshes on the next live snapshot; the PID, env, and
                // resource dictionaries are one-shot upstream, so seed them here for a late subscriber to
                // converge (SessionStateChanged: G8/P4.2 — a late joiner otherwise never sees the env
                // state the aggregator already folded before it subscribed).
                if (snap.Client.ProcessId is int pid && pid != 0)
                    events.Add(new GameProcessChanged(0, now, pid));
                events.Add(new SessionStateChanged(0, now, snap.Client.Version, snap.Client.Language,
                    snap.Client.Region, snap.Client.ServerClockOffset, snap.Client.IsChatLogAvailable));
                events.Add(new RepositorySnapshot(0, now, snap.Actors));
                foreach (ResourceKind kind in Enum.GetValues(typeof(ResourceKind)))
                {
                    var entries = snap.Resources.All(kind);
                    if (entries.Count > 0)
                        events.Add(new ResourceDictionaryForwarded(0, now, kind, entries));
                }
            }
            if (rawlog)
                events.AddRange(BuildRawLogPrimeLines(snap, now));
        }
        catch (Exception ex)
        {
            _log.LogWarning(LogEvents.BridgeFrameMalformed, ex, "Snapshot priming for '{Id}' failed", _satelliteId);
        }
        return events;
    }

    // The rawlog priming set: a pure function of the last-line cache (P4.1) + the typed snapshot, no
    // hand-listed per-field entries beyond this stream→surface mapping. Iterates the one-shot set in ACT
    // emission order (253/249/250 -> 01 -> 02 -> 40 -> 12); for each type it replays the cached verbatim
    // line when one has ever been observed, and falls back to synthesizing only the two types the typed
    // snapshot can reconstruct (01 Territory from snap.Zone, 02 ChangePrimaryPlayer from snap.Player) —
    // strictly the fresh-boot case (no cached line exists anywhere yet). The remaining one-shot types
    // (version/settings/process/map/stats) have no typed-snapshot equivalent and simply stay absent until
    // the real line has crossed at least once.
    private IEnumerable<RawLogLine> BuildRawLogPrimeLines(IGameSnapshot snap, DateTimeOffset now)
    {
        var cached = new Dictionary<LogMessageType, RawLogLine>();
        if (_lastLineCache?.Snapshot() is { } snapshot)
            foreach (var line in snapshot) cached[line.Type] = line;

        foreach (var type in OneShotLineTypes.EmissionOrder)
        {
            if (cached.TryGetValue(type, out var cachedLine))
            {
                yield return cachedLine;
                continue;
            }
            if (type == LogMessageType.Territory && snap.Zone.Id != 0)
                yield return SynthesizeRawLogLine(type, now, snap.Zone.Id.ToString("X"), snap.Zone.Name);
            else if (type == LogMessageType.ChangePrimaryPlayer && snap.Player is { } me)
                yield return SynthesizeRawLogLine(type, now, me.Id.ToString("X"), me.Name);
        }
    }

    // A synthesized fallback line follows the real wire shape ("NN|timestamp|field1|field2|hash") so a
    // regex-based consumer (Triggernometry/cactbot) still matches it, but carries a fixed placeholder
    // hash: the real hash is the plugin's own per-line checksum, never decoded or reproduced per plan §3,
    // and no v1 consumer validates it (P0.1-P0.5).
    private static RawLogLine SynthesizeRawLogLine(LogMessageType type, DateTimeOffset now, string field1, string field2)
    {
        var line = $"{(int)type:D2}|{now:o}|{field1}|{field2}|0000000000000000";
        return new RawLogLine(0, now, type, line, line);
    }

    // Host-routed service commands from this satellite over the event pipe (P6). Point-to-point RPC that
    // must NOT touch the game-event bus: audio a plugin produced fans to the host's shared IAudioOutput
    // (reaching any peer satellite's registered sink); a slot takeover registers/releases this satellite's
    // terminal sink proxy. Returns false for any non-service line so the caller falls through.
    private bool TryHandleServiceUpstream(string line)
    {
        if (SatelliteProtocol.TryParseRegisterSink(line, out var regCaps)) { UpdateAudioSink(regCaps, register: true); return true; }
        if (SatelliteProtocol.TryParseUnregisterSink(line, out var unregCaps)) { UpdateAudioSink(unregCaps, register: false); return true; }
        if (SatelliteProtocol.TryParseSpeak(line, out var text, out var vol, out var ch, out var sync))
        {
            _audio?.Speak(text, new AudioOptions(vol) { Channel = (AudioChannel)ch, Synchronous = sync });
            return true;
        }
        if (SatelliteProtocol.TryParsePlaySound(line, out var path, out var pvol))
        {
            _audio?.Play(path, pvol);
            return true;
        }
        if (SatelliteProtocol.TryParseLogLine(line, out var logId, out var logText))
        {
            // Custom-log-line write-back (P6): re-emit as a bus RawLogLine (fresh sequence + clock), which
            // fans through the existing rawlog egress to every subscriber including the origin, in bus order.
            _rawLog?.Emit((LogMessageType)logId, logText);
            return true;
        }
        if (SatelliteProtocol.TryParseEndCombat(line, out var endExport))
        {
            // Consumer EndCombat route-up (P9a): inject EndCombatRequested onto the bus (like the LOGLINE
            // arm, not the audio arm) so the host engine ends the authoritative encounter and the swings
            // egress fans it back down to every replica including the origin, in bus order. Timestamp is
            // unused by the end operation (both the engine and the consumer fan-back read only Export).
            _sink.Emit(new EndCombatRequested(_sink.NextSequence(), DateTimeOffset.Now, endExport));
            return true;
        }
        if (SatelliteProtocol.TryParseRegisterCb(line, out var cbName, out _)) { RegisterCallbackProxy(cbName); return true; }
        if (SatelliteProtocol.TryParseUnregisterCb(line, out var ucbName)) { UnregisterCallbackProxy(ucbName); return true; }
        if (SatelliteProtocol.TryParseInvokeCb(line, out var icbName, out var icbArg))
        {
            // A satellite invoked a named callback: fan through the shared registry to every owner across
            // all satellites (each owner's proxy relays it down its own command pipe), incl. the origin.
            _registry?.InvokeCallback(icbName, icbArg);
            return true;
        }
        return false;
    }

    // Register/release this satellite's proxy for a named callback (P6). One proxy per name per satellite;
    // when the shared registry fans an invoke, the proxy relays it DOWN this satellite's command pipe so
    // its facade dispatches to the plugin's local delegate. allowDuplicate is always true at the host: each
    // satellite contributes at most one proxy per name, and peers legitimately share a callback name.
    private void RegisterCallbackProxy(string name)
    {
        if (_registry is null)
        {
            _log.LogWarning(LogEvents.BridgeFrameMalformed, "Callback registration from '{Id}' ignored: no registry wired", _satelliteId);
            return;
        }
        lock (_cbLock)
        {
            if (_callbackProxies.ContainsKey(name)) return;
            _callbackProxies[name] = _registry.RegisterCallback(name,
                arg => SendCommand(SatelliteProtocol.FormatInvokeCb(name, arg?.ToString() ?? "")),
                owner: this, allowDuplicate: true);
        }
        _log.LogInformation(LogEvents.BridgeHandshake, "Satellite '{Id}' registered named callback '{Name}'", _satelliteId, name);
    }

    private void UnregisterCallbackProxy(string name)
    {
        lock (_cbLock)
        {
            if (_callbackProxies.TryGetValue(name, out var reg)) { reg.Dispose(); _callbackProxies.Remove(name); }
        }
    }

    // Register or release this satellite's terminal audio-sink proxy (P6). One proxy per satellite carries
    // both capabilities, re-registered whenever the caps change, so the single terminal registration never
    // mis-orders against a second same-priority sink. On the host's shared IAudioOutput, priority 10 +
    // terminal reproduces ACT's route-instead-of (G3) across the bridge: a producer in a peer satellite
    // fans here and is relayed down to this satellite's plugin.
    private void UpdateAudioSink(string caps, bool register)
    {
        if (_audio is null)
        {
            _log.LogWarning(LogEvents.BridgeFrameMalformed, "Audio sink registration from '{Id}' ignored: no audio output wired", _satelliteId);
            return;
        }
        bool tts = caps == "tts" || caps == "both";
        bool sound = caps == "sound" || caps == "both";
        lock (_sinkLock)
        {
            if (tts) _sinkTts = register;
            if (sound) _sinkSound = register;
            _audioSink?.Dispose();
            _audioSink = null;
            if (_sinkTts || _sinkSound)
                _audioSink = _audio.RegisterSink(new SatelliteAudioSinkProxy(_sinkTts, _sinkSound, SendCommand), priority: 10, terminal: true);
        }
        _log.LogInformation(LogEvents.BridgeHandshake,
            "Satellite '{Id}' audio sink now tts={Tts} sound={Sound}", _satelliteId, _sinkTts, _sinkSound);
    }

    // Release this satellite's host-side service registrations (P6). Called on graceful shutdown AND on an
    // unexpected process exit, so a dead satellite's terminal sink never keeps swallowing audio meant for
    // live peers (a fresh SatelliteHost re-registers on the restarted satellite's REGISTERSINK).
    private void DisposeServiceRegistrations()
    {
        lock (_sinkLock)
        {
            _audioSink?.Dispose();
            _audioSink = null;
            _sinkTts = _sinkSound = false;
        }
        lock (_cbLock)
        {
            foreach (var reg in _callbackProxies.Values) reg.Dispose();
            _callbackProxies.Clear();
        }
    }

    // Write one downstream frame to the command pipe (quiet — the high-rate fan-out must not log per frame).
    private void SendDownstream(string? wire)
    {
        if (wire is null) return;
        lock (_cmdLock)
        {
            try { _cmdWriter?.WriteLine(wire); } catch { /* dead pipe: the egress ring absorbs + drops */ }
        }
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
                else if (line.StartsWith(SatelliteProtocol.SubscribePrefix, StringComparison.Ordinal))
                    HandleSubscribe(line);
                else if (TryHandleServiceUpstream(line))
                    continue;
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
        lock (_egressLock) { _egress?.Dispose(); _egress = null; }
        DisposeServiceRegistrations();
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
