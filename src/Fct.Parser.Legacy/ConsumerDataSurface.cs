using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Fct.Abstractions;
using FFXIV_ACT_Plugin.Common;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;

namespace Fct.Parser.Legacy
{
    // The SDK typed-event surface a consumer plugin binds to, raised from host-routed GameEvent frames
    // (net48 mirror of Fct.Compat.Shim.DataSubscriptionAdapter). Raised synchronously on the consumer's
    // frame-fold thread — the in-order delivery a single-reader consumer expects. RepositorySnapshot is
    // NOT an event source: it feeds the repository mirror (the poll surface), not this event stream.
    internal sealed class ConsumerDataSubscription : IDataSubscription, IDisposable
    {
        public event NetworkReceivedDelegate NetworkReceived;
        public event NetworkSentDelegate NetworkSent;
        public event CombatantAddedDelegate CombatantAdded;
        public event CombatantRemovedDelegate CombatantRemoved;
        public event PrimaryPlayerDelegate PrimaryPlayerChanged;
        public event ZoneChangedDelegate ZoneChanged;
        public event PartyListChangedDelegate PartyListChanged;
        public event LogLineDelegate LogLine;
        public event ProcessChangedDelegate ProcessChanged;

        // Interface-required but inert (no host source raises them); explicit empty accessors so they
        // do not trip CS0067 (warnings-as-errors) for an event that is never invoked.
        public event PlayerStatsChangedDelegate PlayerStatsChanged { add { } remove { } }
        public event ParsedLogLineDelegate ParsedLogLine { add { } remove { } }

        private int _logLines;
        public int LogLinesRaised => Volatile.Read(ref _logLines);

        public void Raise(GameEvent e)
        {
            switch (e)
            {
                case RawLogLine r:
                    Interlocked.Increment(ref _logLines);
                    // The SDK's per-line seconds is not preserved on RawLogLine; synthesize best-effort
                    // from the event timestamp (the line's own embedded timestamp is authoritative).
                    LogLine?.Invoke((uint)r.Type, (uint)r.Timestamp.ToUnixTimeSeconds(), r.Line ?? "");
                    break;
                case ZoneChanged z:
                    ZoneChanged?.Invoke(z.ZoneId, z.ZoneName ?? "");
                    break;
                case PartyChanged p:
                    PartyListChanged?.Invoke(new ReadOnlyCollection<uint>(p.Members.ToList()), p.Members.Count);
                    break;
                case Fct.Abstractions.PrimaryPlayerChanged:
                    // The SDK delegate is parameterless; consumers re-poll the repository for the new player.
                    PrimaryPlayerChanged?.Invoke();
                    break;
                case CombatantAdded ca:
                    CombatantAdded?.Invoke(ConsumerCombatantProjector.ToCombatant(ca.Combatant));
                    break;
                case CombatantRemoved cr:
                    CombatantRemoved?.Invoke(new SdkModels.Combatant { ID = cr.ActorId });
                    break;
                case RawPacketReceived pkt:
                    if (pkt.Direction == PacketDirection.Sent) NetworkSent?.Invoke(pkt.Connection ?? "", pkt.Epoch, pkt.Bytes ?? Array.Empty<byte>());
                    else NetworkReceived?.Invoke(pkt.Connection ?? "", pkt.Epoch, pkt.Bytes ?? Array.Empty<byte>());
                    break;
                case GameProcessChanged gp:
                    ProcessChanged?.Invoke(SafeProcess(gp.Pid));
                    break;
            }
        }

        private static Process SafeProcess(int pid)
        {
            if (pid == 0) return null;
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
        }

        public void Dispose() { }
    }

    // The SDK pull-state surface a consumer polls, served from the host-routed repository mirror (net48
    // mirror of Fct.Compat.Shim.DataRepository). GetCombatantList projects the last RepositorySnapshot;
    // resource dictionaries + PID come from the forwarded one-shots. Apply/read race on the fold thread
    // vs. a consumer poll thread, so reference-typed fields are swapped atomically (volatile).
    internal sealed class ConsumerDataRepository : IDataRepository
    {
        private volatile IReadOnlyList<Actor> _combatants = Array.Empty<Actor>();
        private readonly object _resLock = new object();
        private readonly Dictionary<ResourceKind, IReadOnlyDictionary<uint, string>> _resources =
            new Dictionary<ResourceKind, IReadOnlyDictionary<uint, string>>();
        private int _pid;
        private uint _playerId;
        private uint _territoryId;

        public void Apply(GameEvent e)
        {
            switch (e)
            {
                case RepositorySnapshot snap:
                    _combatants = snap.Combatants;
                    break;
                case ResourceDictionaryForwarded rf:
                    lock (_resLock) _resources[rf.Kind] = rf.Entries;
                    break;
                case GameProcessChanged gp:
                    Volatile.Write(ref _pid, gp.Pid);
                    break;
                case PrimaryPlayerChanged pp:
                    Volatile.Write(ref _playerId, pp.ActorId);
                    break;
                case ZoneChanged z:
                    Volatile.Write(ref _territoryId, z.ZoneId);
                    break;
            }
        }

        public ReadOnlyCollection<SdkModels.Combatant> GetCombatantList()
            => new ReadOnlyCollection<SdkModels.Combatant>(
                _combatants.Select(ConsumerCombatantProjector.ToCombatant).ToList());

        public SdkModels.Player GetPlayer()
        {
            var me = _combatants.FirstOrDefault(c => c.Id == Volatile.Read(ref _playerId));
            return new SdkModels.Player { JobID = me is null ? 0u : (uint)me.Job };
        }

        public uint GetCurrentPlayerID() => Volatile.Read(ref _playerId);
        public uint GetCurrentTerritoryID() => Volatile.Read(ref _territoryId);

        public Process GetCurrentFFXIVProcess()
        {
            var pid = Volatile.Read(ref _pid);
            if (pid == 0) return null;
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
        }

        public IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType)
        {
            var kind = MapResource(resourceType);
            if (kind == null) return new Dictionary<uint, string>();
            lock (_resLock)
                return _resources.TryGetValue(kind.Value, out var d)
                    ? d.ToDictionary(kv => kv.Key, kv => kv.Value)
                    : new Dictionary<uint, string>();
        }

        public Language GetSelectedLanguageID() => Language.English;
        public byte GetGameRegion() => 0;
        public string GetGameVersion() => "0.0";
        public DateTime GetServerTimestamp() => DateTime.UtcNow;
        public bool IsChatLogAvailable() => true;
        public string[] GetAntiVirusNames() => Array.Empty<string>();

        // ResourceType is locale-tagged; the forwarded catalog is locale-neutral, so the suffix is
        // dropped. MountList has no ResourceKind equivalent → null (empty dictionary).
        private static ResourceKind? MapResource(ResourceType t)
        {
            switch (t)
            {
                case ResourceType.BuffList_EN: case ResourceType.BuffList_FR: case ResourceType.BuffList_DE:
                case ResourceType.BuffList_JP: case ResourceType.BuffList_KR: return ResourceKind.Status;
                case ResourceType.SkillList_EN: case ResourceType.SkillList_FR: case ResourceType.SkillList_DE:
                case ResourceType.SkillList_JP: case ResourceType.SkillList_KR: return ResourceKind.Action;
                case ResourceType.WorldList_EN: return ResourceKind.World;
                case ResourceType.ZoneList_EN: case ResourceType.TerritoryList_EN: return ResourceKind.Zone;
                case ResourceType.ItemList_EN: return ResourceKind.Item;
                default: return null;
            }
        }
    }

    // Projects a modern Actor onto the SDK Combatant a consumer polls (net48 mirror of
    // Fct.Compat.Shim.CombatantProjector). Statuses/enmity are firehose-only over the wire → empty here.
    internal static class ConsumerCombatantProjector
    {
        public static SdkModels.Combatant ToCombatant(Actor a) => new SdkModels.Combatant
        {
            ID = a.Id,
            OwnerID = a.OwnerId,
            type = MapType(a.Kind),
            Job = a.Job,
            Level = a.Level,
            Name = a.Name ?? "",
            CurrentHP = a.Hp, MaxHP = a.MaxHp,
            CurrentMP = a.Mp, MaxMP = a.MaxMp,
            CurrentCP = a.CurrentCp ?? 0, MaxCP = a.MaxCp ?? 0,
            CurrentGP = a.CurrentGp ?? 0, MaxGP = a.MaxGp ?? 0,
            PosX = a.Position.X, PosY = a.Position.Y, PosZ = a.Position.Z, Heading = a.Position.Heading,
            CurrentWorldID = a.CurrentWorldId ?? a.WorldId,
            WorldID = a.WorldId, WorldName = a.WorldName ?? "",
            BNpcNameID = a.BNpcNameId, BNpcID = a.BNpcId,
            TargetID = a.TargetId,
            EffectiveDistance = a.EffectiveDistance,
            PartyType = MapParty(a.Party),
            Order = a.Order ?? 0,
            NetworkBuffs = Array.Empty<SdkModels.NetworkBuff>(),
        };

        private static byte MapType(ActorKind kind)
        {
            switch (kind)
            {
                case ActorKind.Player: return 1;
                case ActorKind.Npc: return 2;
                case ActorKind.Pet: return 2;
                default: return 0;
            }
        }

        private static SdkModels.PartyType MapParty(PartyMembership p)
        {
            switch (p)
            {
                case PartyMembership.Party: return SdkModels.PartyType.Party;
                case PartyMembership.Alliance: return SdkModels.PartyType.Alliance;
                default: return SdkModels.PartyType.None;
            }
        }
    }
}
