using Fct.Abstractions;
using Microsoft.Extensions.Logging;

namespace Fct.App.Hosting;

/// <summary>
/// A per-plugin <see cref="IPluginHost"/>. Shares the process-wide game/encounter/audio/registry
/// singletons, but scopes <see cref="Self"/>, <see cref="Storage"/>, <see cref="Logger"/>, and the
/// capability-gated <see cref="RawLogLines"/>/<see cref="RawPackets"/> to the one plugin. Constructed
/// by the <see cref="PluginManager"/> at load.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    public PluginHost(
        PluginInfo self,
        IGameSession game,
        IEncounterService encounters,
        IAudioOutput audio,
        IPluginRegistry plugins,
        IPluginStorage storage,
        ILogger logger,
        IClock clock,
        IRawLogLineEmitter rawLogLines,
        IRawPacketSource rawPackets)
    {
        Self = self;
        Game = game;
        Encounters = encounters;
        Audio = audio;
        Plugins = plugins;
        Storage = storage;
        Logger = logger;
        Clock = clock;
        RawLogLines = rawLogLines;
        RawPackets = rawPackets;
    }

    public IGameSession Game { get; }
    public IEncounterService Encounters { get; }
    public IAudioOutput Audio { get; }
    public IPluginRegistry Plugins { get; }
    public IPluginStorage Storage { get; }
    public ILogger Logger { get; }
    public IClock Clock { get; }
    public IRawLogLineEmitter RawLogLines { get; }
    public IRawPacketSource RawPackets { get; }
    public PluginInfo Self { get; }
}
