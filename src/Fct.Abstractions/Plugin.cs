using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fct.Abstractions
{
    /// <summary>
    /// A modern plugin. The host loads the assembly into a dedicated collectible
    /// <c>AssemblyLoadContext</c> (assembly/version isolation + hot-unload), constructs the entry
    /// type named in the manifest, and calls <see cref="InitializeAsync"/> exactly once. Teardown
    /// is <see cref="IAsyncDisposable.DisposeAsync"/>. Every host→plugin call is fault-guarded; a
    /// plugin that repeatedly throws or hangs is quarantined and may be unloaded.
    /// </summary>
    public interface IPlugin : IAsyncDisposable
    {
        /// <summary>
        /// One-time initialization. Subscribe to <see cref="IGameSession.Events"/>, register audio
        /// sinks, and read settings here. The host cancels <paramref name="ct"/> if init exceeds its
        /// time budget.
        /// </summary>
        Task InitializeAsync(IPluginHost host, CancellationToken ct);
    }

    /// <summary>
    /// The host services handed to a plugin at init. Replaces the legacy global
    /// <c>ActGlobals.oFormActMain</c> hub and the reflected FFXIV_ACT_Plugin SDK.
    /// </summary>
    public interface IPluginHost
    {
        /// <summary>Live game data: the typed event stream and free-threaded state snapshots.</summary>
        IGameSession Game { get; }

        /// <summary>Combat-state read/write and encounter export (replaces SetEncounter/EndCombat/GetTextExport).</summary>
        IEncounterService Encounters { get; }

        /// <summary>Multi-sink audio output (replaces the global PlayTtsMethod/PlaySoundMethod delegate slot).</summary>
        IAudioOutput Audio { get; }

        /// <summary>Peer discovery, cross-plugin named callbacks, and plugin-published typed events.</summary>
        IPluginRegistry Plugins { get; }

        /// <summary>The plugin's private data directory + typed settings (replaces PluginGetSelfData/AppDataFolder).</summary>
        IPluginStorage Storage { get; }

        /// <summary>Structured logging (replaces WriteExceptionLog/WriteDebugLog/WriteInfoLog).</summary>
        ILogger Logger { get; }

        /// <summary>Server + local time (replaces GetServerTimestamp).</summary>
        IClock Clock { get; }

        /// <summary>
        /// Emit synthetic custom (256+) log lines onto the live event bus. Capability-gated (same
        /// posture as <see cref="IRawPacketSource"/>): the write-back path OverlayPlugin uses when it
        /// turns a decoded raw packet into a custom line other consumers then read as a
        /// <c>RawLogLine</c>.
        /// </summary>
        IRawLogLineEmitter RawLogLines { get; }

        /// <summary>This plugin's own manifest metadata.</summary>
        PluginInfo Self { get; }
    }

    /// <summary>
    /// The gated write-back hatch for synthetic custom (256+) log lines. Complements the read-only
    /// <see cref="IRawPacketSource"/>: a plugin that decodes raw packets can re-inject them as
    /// <c>RawLogLine</c> events on the live bus (OverlayPlugin's custom-line round-trip).
    /// </summary>
    public interface IRawLogLineEmitter
    {
        /// <summary>Fan a synthetic log line onto the event bus as a <c>RawLogLine</c>.</summary>
        void Emit(LogMessageType type, string line);
    }

    /// <summary>Typed metadata projected from a plugin's <c>plugin.json</c> manifest.</summary>
    public sealed record PluginInfo(string Id, string Version, string ContractVersion);

    /// <summary>Game-time clock. <see cref="ServerNow"/> tracks the FFXIV server clock.</summary>
    public interface IClock
    {
        DateTimeOffset LocalNow { get; }
        DateTimeOffset ServerNow { get; }
    }

    /// <summary>Per-plugin private storage and typed settings persistence.</summary>
    public interface IPluginStorage
    {
        /// <summary>The plugin's private, writable data directory (created on first access).</summary>
        string DataDirectory { get; }

        Task<T?> LoadSettingsAsync<T>(string name = "settings") where T : class;
        Task SaveSettingsAsync<T>(T value, string name = "settings") where T : class;
    }

    /// <summary>
    /// Peer registry. Enumerate loaded plugins (typed, not <c>ActPlugins</c> reflection),
    /// register Triggernometry-style named callbacks, and publish/consume typed events between
    /// plugins — an integration the pipe-delimited log line cannot express.
    /// </summary>
    public interface IPluginRegistry
    {
        System.Collections.Generic.IReadOnlyList<PluginInfo> LoadedPlugins { get; }

        /// <summary>
        /// Register a Triggernometry-style named callback. <paramref name="owner"/> tags the
        /// registrant (for owner-scoped bookkeeping, as <c>RealPlugin</c> tracks callback owners);
        /// unless <paramref name="allowDuplicate"/> is set, registering a name that already exists
        /// throws. Dispose the returned handle to unregister (replaces the legacy int-id unregister).
        /// </summary>
        IDisposable RegisterCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false);
        void InvokeCallback(string name, object? argument = null);

        void Publish<T>(T evt) where T : notnull;
        IDisposable Subscribe<T>(Action<T> handler) where T : notnull;

        /// <summary>
        /// Obtain a live, version-gated typed handle to a peer plugin's published service (e.g.
        /// Triggernometry's <c>BridgeFFXIV</c> reaching the Started parser peer). Returns false when no
        /// peer with <paramref name="pluginId"/> has published a service assignable to
        /// <typeparamref name="T"/>.
        /// </summary>
        bool TryGetPeerService<T>(string pluginId, out T service) where T : class;
    }
}
