using System.Collections.ObjectModel;
using System.Diagnostics;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Parser.Legacy.Tests
{
    // A stand-in for the real plugin's IDataSubscription. Unlike the real one (which fans out via
    // delegate.BeginInvoke), it raises synchronously — letting tests drive a deterministic,
    // ordered upstream event stream into RingBufferDataSubscription.AttachUpstream.
    internal sealed class FakeDataSubscription : IDataSubscription
    {
        public event NetworkReceivedDelegate NetworkReceived;
        public event NetworkSentDelegate NetworkSent;
        public event CombatantAddedDelegate CombatantAdded;
        public event CombatantRemovedDelegate CombatantRemoved;
        public event PrimaryPlayerDelegate PrimaryPlayerChanged;
        public event ZoneChangedDelegate ZoneChanged;
        public event PlayerStatsChangedDelegate PlayerStatsChanged;
        public event PartyListChangedDelegate PartyListChanged;
        public event LogLineDelegate LogLine;
        public event ParsedLogLineDelegate ParsedLogLine;
        public event ProcessChangedDelegate ProcessChanged;

        public void RaiseNetworkReceived(string c, long e, byte[] m) => NetworkReceived?.Invoke(c, e, m);
        public void RaiseNetworkSent(string c, long e, byte[] m) => NetworkSent?.Invoke(c, e, m);
        public void RaiseLogLine(uint t, uint s, string l) => LogLine?.Invoke(t, s, l);
        public void RaiseParsedLogLine(uint seq, int mt, string msg) => ParsedLogLine?.Invoke(seq, mt, msg);
        public void RaiseZoneChanged(uint id, string name) => ZoneChanged?.Invoke(id, name);
        public void RaiseCombatantAdded(object cb) => CombatantAdded?.Invoke(cb);
        public void RaiseCombatantRemoved(object cb) => CombatantRemoved?.Invoke(cb);
        public void RaisePrimaryPlayerChanged() => PrimaryPlayerChanged?.Invoke();
        public void RaisePlayerStatsChanged(object ps) => PlayerStatsChanged?.Invoke(ps);
        public void RaisePartyListChanged(ReadOnlyCollection<uint> list, int size) => PartyListChanged?.Invoke(list, size);
        public void RaiseProcessChanged(Process p) => ProcessChanged?.Invoke(p);
    }
}
