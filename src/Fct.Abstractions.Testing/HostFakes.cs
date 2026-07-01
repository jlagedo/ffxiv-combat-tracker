using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.Abstractions.Testing
{
    /// <summary>A settable <see cref="IClock"/> fixed to a canned instant by default.</summary>
    public sealed class FakeClock : IClock
    {
        public FakeClock()
        {
            var t = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            LocalNow = t;
            ServerNow = t;
        }

        public DateTimeOffset LocalNow { get; set; }
        public DateTimeOffset ServerNow { get; set; }
    }

    /// <summary>In-memory <see cref="IPluginStorage"/> — settings live in a dictionary; no disk I/O.</summary>
    public sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object> _settings = new Dictionary<string, object>();

        public FakeStorage(string? dataDirectory = null)
            => DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "fct-flowtests");

        public string DataDirectory { get; }

        public Task<T?> LoadSettingsAsync<T>(string name = "settings") where T : class
            => Task.FromResult(_settings.TryGetValue(name, out var v) ? (T?)v : null);

        public Task SaveSettingsAsync<T>(T value, string name = "settings") where T : class
        {
            _settings[name] = value;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A composable <see cref="IPluginHost"/> for flow tests. Each service defaults to its in-memory
    /// fake; a test supplies only the ones it needs. The concrete fakes stay reachable via the typed
    /// convenience properties (<see cref="Bus"/>, <see cref="Registry"/>, etc.) for assertions.
    /// </summary>
    public sealed class FakePluginHost : IPluginHost
    {
        public FakePluginHost(
            IGameSession? game = null,
            IEncounterService? encounters = null,
            IAudioOutput? audio = null,
            IPluginRegistry? plugins = null,
            IPluginStorage? storage = null,
            ILogger? logger = null,
            IClock? clock = null,
            PluginInfo? self = null)
        {
            Game = game ?? new FakeGameSession();
            Encounters = encounters ?? new FakeEncounterService();
            Audio = audio ?? new RecordingAudioOutput();
            Plugins = plugins ?? new InMemoryRegistry();
            Storage = storage ?? new FakeStorage();
            Logger = logger ?? NullLogger.Instance;
            Clock = clock ?? new FakeClock();
            Self = self ?? new PluginInfo("test.plugin", "1.0.0", "1.0");
            RawLogLines = Bus != null
                ? new FakeRawLogLineEmitter(Bus, Clock)
                : (IRawLogLineEmitter)NoopRawLogLineEmitter.Instance;
        }

        public IGameSession Game { get; }
        public IEncounterService Encounters { get; }
        public IAudioOutput Audio { get; }
        public IPluginRegistry Plugins { get; }
        public IPluginStorage Storage { get; }
        public ILogger Logger { get; }
        public IClock Clock { get; }
        public IRawLogLineEmitter RawLogLines { get; }
        public PluginInfo Self { get; }

        /// <summary>The concrete event bus, when <see cref="Game"/> is the default <see cref="FakeGameSession"/>.</summary>
        public InMemoryEventBus? Bus => (Game as FakeGameSession)?.Bus;
    }

    /// <summary>
    /// In-memory <see cref="IRawLogLineEmitter"/>: builds a <c>RawLogLine</c> (monotonic sequence +
    /// clock timestamp) and fans it onto the event bus, modeling OverlayPlugin's packet→custom-line
    /// write-back.
    /// </summary>
    public sealed class FakeRawLogLineEmitter : IRawLogLineEmitter
    {
        private readonly InMemoryEventBus _bus;
        private readonly IClock _clock;
        private long _seq;

        public FakeRawLogLineEmitter(InMemoryEventBus bus, IClock clock)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public void Emit(LogMessageType type, string line)
        {
            line ??= string.Empty;
            var seq = System.Threading.Interlocked.Increment(ref _seq);
            _bus.Emit(new RawLogLine(seq, _clock.LocalNow, type, line, line));
        }
    }

    /// <summary>No-op emitter used when the host's game session is not the default event-bus fake.</summary>
    internal sealed class NoopRawLogLineEmitter : IRawLogLineEmitter
    {
        public static readonly NoopRawLogLineEmitter Instance = new NoopRawLogLineEmitter();
        private NoopRawLogLineEmitter() { }
        public void Emit(LogMessageType type, string line) { }
    }
}
