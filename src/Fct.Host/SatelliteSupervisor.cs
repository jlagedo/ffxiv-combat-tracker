using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Host.Hosting;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.Host
{
    /// <summary>One package a satellite hosts. The id is the routing/identity key; the package names the
    /// legacy plugin set. <see cref="Role"/> + <see cref="Subscriptions"/> become the satellite's launch
    /// args (P7): a producer forwards its stream and never subscribes; a consumer declares the downstream
    /// stream set its facade reads.</summary>
    internal sealed class SatelliteSpec
    {
        public string Id { get; init; } = "";
        public string Package { get; init; } = "";
        public string Role { get; init; } = "producer";
        public string[] Subscriptions { get; init; } = Array.Empty<string>();
    }

    internal enum SatelliteState { Starting, Running, Restarting, Quarantined, Stopped }

    /// <summary>Live supervision state for one satellite. Mutated by the supervisor under its lock.</summary>
    internal sealed class SupervisedSatellite
    {
        internal SupervisedSatellite(SatelliteSpec spec)
        {
            Id = spec.Id;
            Package = spec.Package;
            ExtraArgs = BuildExtraArgs(spec);
        }

        public string Id { get; }
        public string Package { get; }

        /// <summary>The launch args derived from the spec (role + optional subscription set), handed to
        /// each <see cref="SatelliteHost"/> so a restart relaunches with the same identity + role.</summary>
        internal string ExtraArgs { get; }

        // --package/--role are always present; a consumer also declares its stream set. Stream tokens are
        // bare identifiers joined by commas (no spaces), so the raw append is shell-safe.
        private static string BuildExtraArgs(SatelliteSpec spec)
        {
            var args = "--package " + spec.Package + " --role " + spec.Role;
            if (spec.Subscriptions.Length > 0)
                args += " --subscribe " + string.Join(",", spec.Subscriptions);
            return args;
        }

        private readonly object _gate = new();
        private readonly List<DateTimeOffset> _failures = new();
        internal SatelliteHost? Host;

        private SatelliteState _state = SatelliteState.Starting;
        private int _restartCount;
        private int _pid = -1;
        private string _handshake = "";

        public SatelliteState State { get { lock (_gate) return _state; } }
        public int RestartCount { get { lock (_gate) return _restartCount; } }
        public int Pid { get { lock (_gate) return _pid; } }
        public string Handshake { get { lock (_gate) return _handshake; } }

        internal object Gate => _gate;
        internal List<DateTimeOffset> Failures => _failures;
        internal void Set(SatelliteState state) { lock (_gate) _state = state; }
        internal void OnStarted(int pid, string handshake, bool isRestart)
        {
            lock (_gate) { _pid = pid; _handshake = handshake; _state = SatelliteState.Running; if (isRestart) _restartCount++; }
        }
    }

    /// <summary>
    /// The multi-satellite process fabric (ISOLATION-PLAN P3): launches and supervises N concurrent
    /// <see cref="SatelliteHost"/> instances, one per <see cref="SatelliteSpec"/>. Each satellite runs on
    /// its own duplex pipe with its own identity; an unexpected process exit triggers restart with
    /// exponential backoff (<see cref="RestartPolicy"/>) and quarantine on a crash loop, so one crashing
    /// satellite never takes down its peers or the host. Requested stops (<see cref="StopAllAsync"/>) are
    /// suppressed from the restart path.
    /// </summary>
    internal sealed class SatelliteSupervisor : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;
        private readonly IGameEventSink _sink;
        private readonly IGameSession? _session;   // downstream fan-out source (P4)
        private readonly INotificationHub? _notifications;
        private readonly ISatelliteNotificationText? _texts;
        private readonly IAudioOutput? _audio;        // host-routed services shared across satellites (P6)
        private readonly IPluginRegistry? _registry;
        private readonly IRawLogLineEmitter? _rawLog;
        private readonly RestartPolicy _policy;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly CancellationTokenSource _cts = new();

        private readonly object _listLock = new();
        private readonly List<SupervisedSatellite> _satellites = new();

        /// <summary>Raised at the end of every (re)start of a satellite, carrying whether this was a
        /// restart. The <see cref="SatelliteRouter"/> uses it to (re)wire the new <see cref="SatelliteHost"/>'s
        /// roster events and, on a restart, replay the package's LOADPLUGIN commands onto the fresh process.</summary>
        public event Action<SupervisedSatellite, bool>? SatelliteStarted;

        public SatelliteSupervisor(
            ILoggerFactory loggerFactory,
            IGameEventSink sink,
            INotificationHub? notifications = null,
            ISatelliteNotificationText? texts = null,
            RestartPolicy? policy = null,
            Func<DateTimeOffset>? now = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            IGameSession? session = null,
            IAudioOutput? audio = null,
            IPluginRegistry? registry = null,
            IRawLogLineEmitter? rawLog = null)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _log = loggerFactory.CreateLogger("Fct.Satellite.Supervisor");
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _session = session;
            _notifications = notifications;
            _texts = texts;
            _audio = audio;
            _registry = registry;
            _rawLog = rawLog;
            _policy = policy ?? new RestartPolicy();
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        }

        public IReadOnlyList<SupervisedSatellite> Satellites
        {
            get { lock (_listLock) return _satellites.ToArray(); }
        }

        /// <summary>Launch and begin supervising a satellite. Throws if the initial launch fails (e.g. the
        /// satellite exe is not staged); later crashes are handled by supervision, not thrown.</summary>
        public async Task<SupervisedSatellite> LaunchAsync(SatelliteSpec spec, CancellationToken ct = default)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            var sat = new SupervisedSatellite(spec);
            lock (_listLock) _satellites.Add(sat);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            await StartOneAsync(sat, isRestart: false, linked.Token).ConfigureAwait(false);
            _log.LogInformation(LogEvents.SatelliteLaunching,
                "Supervisor launched satellite '{Id}' (pkg '{Pkg}', pid {Pid})", sat.Id, sat.Package, sat.Pid);
            return sat;
        }

        private async Task StartOneAsync(SupervisedSatellite sat, bool isRestart, CancellationToken ct)
        {
            var host = new SatelliteHost(_loggerFactory, _sink, _notifications, _texts, sat.Id, _session,
                extraArgs: sat.ExtraArgs, audio: _audio, registry: _registry, rawLog: _rawLog);
            host.ProcessExited += code => OnExited(sat, code);
            sat.Host = host;
            sat.Set(isRestart ? SatelliteState.Restarting : SatelliteState.Starting);

            var result = await host.StartAsync(ct).ConfigureAwait(false);
            sat.OnStarted(host.Process?.Id ?? -1, result.Handshake, isRestart);
            SatelliteStarted?.Invoke(sat, isRestart);
        }

        // An unexpected process exit (Process.Exited fired outside a requested shutdown). Record the
        // failure, consult the policy, and either quarantine or schedule a backed-off restart. Serialized
        // per satellite by its gate; the actual restart runs off-thread so we never block the exit callback.
        private void OnExited(SupervisedSatellite sat, int exitCode)
        {
            TimeSpan delay;
            lock (sat.Gate)
            {
                if (sat.State is SatelliteState.Stopped or SatelliteState.Quarantined) return;

                sat.Failures.Add(_now());
                var decision = _policy.Decide(sat.Failures, _now());
                if (decision.Quarantine)
                {
                    sat.Set(SatelliteState.Quarantined);
                    _log.LogError(LogEvents.SatelliteExited,
                        "Satellite '{Id}' quarantined after {Failures} failures in the crash-loop window (last exit {Code})",
                        sat.Id, sat.Failures.Count, exitCode);
                    return;
                }
                sat.Set(SatelliteState.Restarting);
                delay = decision.Delay;
            }

            _log.LogWarning(LogEvents.SatelliteExited,
                "Satellite '{Id}' exited (code {Code}); restarting in {Delay:0.###}s", sat.Id, exitCode, delay.TotalSeconds);
            _ = RestartAfterAsync(sat, delay);
        }

        private async Task RestartAfterAsync(SupervisedSatellite sat, TimeSpan delay)
        {
            try
            {
                if (delay > TimeSpan.Zero) await _delay(delay, _cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested || sat.State == SatelliteState.Stopped) return;
                await StartOneAsync(sat, isRestart: true, _cts.Token).ConfigureAwait(false);
                _log.LogInformation(LogEvents.SatelliteLaunching,
                    "Satellite '{Id}' restarted (pid {Pid}, restart #{Count})", sat.Id, sat.Pid, sat.RestartCount);
            }
            catch (OperationCanceledException) { /* supervisor stopping */ }
            catch (Exception ex)
            {
                // The restart itself failed (e.g. handshake timeout): treat it as another failure so the
                // crash-loop counter advances toward quarantine instead of wedging in Restarting.
                _log.LogWarning(LogEvents.SatelliteExited, ex, "Satellite '{Id}' restart failed; re-evaluating", sat.Id);
                OnExited(sat, -1);
            }
        }

        /// <summary>Stop supervising and gracefully drain every satellite. Idempotent.</summary>
        public async Task StopAllAsync(TimeSpan timeout)
        {
            _cts.Cancel();
            SupervisedSatellite[] all;
            lock (_listLock) all = _satellites.ToArray();
            foreach (var sat in all) sat.Set(SatelliteState.Stopped);   // suppress the restart path
            foreach (var sat in all)
            {
                var host = sat.Host;
                if (host != null)
                {
                    try { await host.ShutdownAsync(timeout).ConfigureAwait(false); } catch { /* best-effort */ }
                }
            }
        }

        /// <summary>Stop supervising and gracefully drain a single satellite, then forget it (used when a
        /// package's last plugin is uninstalled — the emptied satellite is torn down). Suppresses its
        /// restart path and removes it from the supervised set. Idempotent.</summary>
        public async Task StopOneAsync(SupervisedSatellite sat, TimeSpan timeout)
        {
            if (sat is null) return;
            sat.Set(SatelliteState.Stopped);   // suppress the restart path before we drain
            lock (_listLock) _satellites.Remove(sat);
            var host = sat.Host;
            if (host != null)
            {
                try { await host.ShutdownAsync(timeout).ConfigureAwait(false); } catch { /* best-effort */ }
            }
            _log.LogInformation(LogEvents.SatelliteExited, "Supervisor stopped satellite '{Id}' (pkg '{Pkg}')", sat.Id, sat.Package);
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}
