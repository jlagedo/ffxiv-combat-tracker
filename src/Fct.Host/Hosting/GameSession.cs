using Fct.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>
/// The shared <see cref="IGameSession"/> — CQRS split into the push <see cref="Events"/> bus and the
/// pull <see cref="Snapshot"/>. Both are process-wide singletons handed to every plugin's
/// <c>IPluginHost.Game</c>.
/// </summary>
internal sealed class GameSession : IGameSession
{
    private readonly GameSnapshotProvider _snapshot;

    public GameSession(GameEventBus bus, GameSnapshotProvider snapshot)
    {
        Events = bus;
        _snapshot = snapshot;
    }

    public IGameEventStream Events { get; }

    public IGameSnapshot Snapshot() => _snapshot.Current;
}
