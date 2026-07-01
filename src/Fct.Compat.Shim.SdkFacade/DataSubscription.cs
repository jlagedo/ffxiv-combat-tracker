using System.Collections.ObjectModel;
using System.Diagnostics;

namespace FFXIV_ACT_Plugin.Common
{
    // The event delegate shapes are re-declared verbatim from the real SDK
    // (lib/FFXIV_ACT_Plugin.Common.dll, identity 3.0.0.0). CombatantAdded/Removed and
    // PlayerStatsChanged carry `object` (the SDK does not surface the concrete model type on the
    // delegate); the shim casts inside its adapter.
    public delegate void NetworkReceivedDelegate(string connection, long epoch, byte[] message);
    public delegate void NetworkSentDelegate(string connection, long epoch, byte[] message);
    public delegate void CombatantAddedDelegate(object Combatant);
    public delegate void CombatantRemovedDelegate(object Combatant);
    public delegate void PrimaryPlayerDelegate();
    public delegate void ZoneChangedDelegate(uint ZoneID, string ZoneName);
    public delegate void PlayerStatsChangedDelegate(object playerStats);
    public delegate void PartyListChangedDelegate(ReadOnlyCollection<uint> partyList, int partySize);
    public delegate void LogLineDelegate(uint EventType, uint Seconds, string logline);
    public delegate void ParsedLogLineDelegate(uint sequence, int messagetype, string message);
    public delegate void ProcessChangedDelegate(Process process);

    /// <summary>
    /// The plugin's typed event surface. OverlayPlugin reflects a plugin's <c>DataSubscription</c>
    /// property and binds these events. The shim exposes a <see cref="IDataSubscription"/> adapter
    /// projected from the modern <c>IGameEventStream</c>.
    /// </summary>
    public interface IDataSubscription
    {
        event NetworkReceivedDelegate NetworkReceived;
        event NetworkSentDelegate NetworkSent;
        event CombatantAddedDelegate CombatantAdded;
        event CombatantRemovedDelegate CombatantRemoved;
        event PrimaryPlayerDelegate PrimaryPlayerChanged;
        event ZoneChangedDelegate ZoneChanged;
        event PlayerStatsChangedDelegate PlayerStatsChanged;
        event PartyListChangedDelegate PartyListChanged;
        event LogLineDelegate LogLine;
        event ParsedLogLineDelegate ParsedLogLine;
        event ProcessChangedDelegate ProcessChanged;
    }
}
