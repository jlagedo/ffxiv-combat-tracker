using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Fct.Abstractions;
using Fct.Bridge;
using FFXIV_ACT_Plugin.Common;
using Microsoft.Extensions.Logging;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;
using Timer = System.Windows.Forms.Timer;   // UI-thread timer (driven by the satellite message loop)

namespace Fct.LegacyHost
{
    // Projects the satellite's live SDK/ACT stream into typed GameEvent records and ships them to the
    // net10 host over the bridge pipe (piece C). It is the production form of Fct.StreamProbe: same
    // discovery (reflect ActGlobals.oFormActMain.ActPlugins, exactly as OverlayPlugin's FFXIVRepository),
    // the same SDK subscriptions and ACT-hub tap — only the sink differs (the bridge, not a log file).
    //
    // Only events the SDK/ACT hub exposes post-parse are forwarded (RawLogLine, ZoneChanged, PartyChanged,
    // PrimaryPlayerChanged, CombatantAdded/Removed, ActionEffect). The plugin is the sole parser, so
    // status/cast/death/hp events — which exist only as parsed log-line fields — are NOT synthesized here;
    // consumers reach them through the RawLogLine firehose, exactly as they do in ACT today.
    //
    // Never blocks a producer: SDK/ACT callbacks enqueue into a bounded ring (drop-oldest + a dropped
    // counter) drained by one background thread that formats + writes the pipe, so pipe I/O never stalls
    // the SDK dispatch thread or the WinForms UI thread on the high-rate firehose.
    internal sealed class BridgeForwarder : IDisposable
    {
        private readonly Action<string> _send;
        private readonly Microsoft.Extensions.Logging.ILogger _log;

        private IDataSubscription _sub;
        private IDataRepository _repo;
        private Timer _discover;   // polls until the FFXIV plugin SDK is reachable
        private Timer _repoTimer;  // fixed-rate combatant-list snapshot poll (fresh HP/position/party)

        // Repository-snapshot poll cadence. The mirror's freshness must satisfy OverlayPlugin/Hojoring
        // polls without flooding the pipe (ISOLATION-PLAN §5 open question — pinned in the P9 soak).
        private const int RepositorySnapshotIntervalMs = 250;

        // Bounded ring drained by one writer thread — mirrors RingBufferDataSubscription's model.
        private readonly GameEvent[] _ring;
        private readonly int _mask;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _writer;
        private int _head, _tail, _count;
        private long _dropped, _sent;
        private volatile bool _running = true;

        public BridgeForwarder(Action<string> send, Microsoft.Extensions.Logging.ILogger log, int capacity = 4096)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            _ring = new GameEvent[capacity];
            _mask = capacity - 1;
            _writer = new Thread(WriteLoop) { Name = "Fct.BridgeForwarder", IsBackground = true };
            _writer.Start();
        }

        public long DroppedCount => Interlocked.Read(ref _dropped);
        public long SentCount => Interlocked.Read(ref _sent);

        // Tap ACT's aggregate hub immediately (it exists before any plugin), then poll for the FFXIV
        // plugin SDK (load order is not guaranteed). A synthetic frame proves the wire path headlessly.
        public void Start()
        {
            var act = ActGlobals.oFormActMain;
            if (act != null)
            {
                // Pre-aggregation tap: forward the FULL MasterSwing (every field + Tags) plus the
                // encounter lifecycle the plugin drives, so the net10 engine aggregates identically.
                act.BeforeCombatAction += OnBeforeCombatAction;
                act.EncounterSetRaised += OnEncounterSet;
                act.ZoneChangeRaised += OnZoneChange;
                act.CombatEndRaised += OnCombatEnd;
                // The verbatim line stream (PIPELINE-COMPLETENESS-PLAN P2, G14): the facade's log-tail
                // seam is the sole RawLogLine source — the same tap real ACT consumers (cactbot,
                // Triggernometry) bind, downstream of the plugin's own log writes, so every line type
                // crosses regardless of whether a parser is loaded.
                act.OnLogLineRead += OnLogLineRead;
            }

            _discover = new Timer { Interval = 500 };
            _discover.Tick += (s, e) => TryBindFfxiv();
            _discover.Start();

#if DEBUG
            // Headless proof-of-wire (Debug only, so production plugins never see a synthetic zone):
            // one frame emitted before any game data, so the host logs BridgeEventDecoded with no FFXIV.
            Enqueue(new ZoneChanged(0, DateTimeOffset.Now, 0, "Bridge Forwarder Online"));
#endif
        }

        // ---- Discovery (the same seam OverlayPlugin's FFXIVRepository / Fct.StreamProbe use) --------

        private void TryBindFfxiv()
        {
            try
            {
                var act = ActGlobals.oFormActMain;
                if (act == null) return;

                var ffxiv = act.ActPlugins.FirstOrDefault(p =>
                    p.cbEnabled.Checked && p.pluginObj != null &&
                    p.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin", StringComparison.Ordinal));
                if (ffxiv == null) return;

                var obj = ffxiv.pluginObj;
                var sub = obj.GetType().GetProperty("DataSubscription")?.GetValue(obj) as IDataSubscription;
                var repo = obj.GetType().GetProperty("DataRepository")?.GetValue(obj) as IDataRepository;
                if (sub == null) return;

                _discover.Stop(); _discover.Dispose(); _discover = null;
                _sub = sub; _repo = repo;
                SubscribeSdk(sub);
                EmitInitialRepositoryState();   // PID + one-shot resource dictionaries
                StartRepositoryTimer();          // fixed-rate combatant-list snapshots
                _log.LogInformation(Fct.Logging.LogEvents.ForwarderBound,
                    "[Forwarder] bound to FFXIV SDK; forwarding typed events over the bridge");
            }
            catch (Exception ex)
            {
                _log.LogWarning(Fct.Logging.LogEvents.ForwarderBound, ex, "[Forwarder] SDK bind failed; retrying");
            }
        }

        private void SubscribeSdk(IDataSubscription sub)
        {
            sub.ZoneChanged += OnZoneChanged;
            sub.PartyListChanged += OnPartyListChanged;
            sub.PrimaryPlayerChanged += OnPrimaryPlayerChanged;
            sub.CombatantAdded += OnCombatantAdded;
            sub.CombatantRemoved += OnCombatantRemoved;
            sub.NetworkReceived += OnNetworkReceived;
            sub.NetworkSent += OnNetworkSent;
            sub.ProcessChanged += OnProcessChanged;
        }

        // ---- Repository snapshots / resource dictionaries / game PID (ISOLATION-PLAN P5) -------------

        // The game process id — forwarded so a consumer satellite materializes GetCurrentFFXIVProcess()
        // locally (Process.GetProcessById). A null process (game closed) forwards pid 0.
        private void OnProcessChanged(System.Diagnostics.Process process) =>
            Enqueue(new GameProcessChanged(0, DateTimeOffset.Now, process?.Id ?? 0));

        // One-shot on bind: the current PID + the id→name tables consumers read via GetResourceDictionary.
        private void EmitInitialRepositoryState()
        {
            try
            {
                var p = _repo?.GetCurrentFFXIVProcess();
                if (p != null) Enqueue(new GameProcessChanged(0, DateTimeOffset.Now, p.Id));
            }
            catch { /* best-effort PID */ }

            // The combat-relevant tables (EN; the host catalog is locale-neutral). ItemList is omitted —
            // the target consumers don't read it and it is large enough to bloat a single wire line.
            ForwardResource(ResourceType.SkillList_EN, ResourceKind.Action);
            ForwardResource(ResourceType.BuffList_EN, ResourceKind.Status);
            ForwardResource(ResourceType.ZoneList_EN, ResourceKind.Zone);
            ForwardResource(ResourceType.WorldList_EN, ResourceKind.World);
        }

        private void ForwardResource(ResourceType type, ResourceKind kind)
        {
            try
            {
                var dict = _repo?.GetResourceDictionary(type);
                if (dict == null || dict.Count == 0) return;
                Enqueue(new ResourceDictionaryForwarded(0, DateTimeOffset.Now, kind, new Dictionary<uint, string>(dict)));
            }
            catch { /* best-effort table */ }
        }

        private void StartRepositoryTimer()
        {
            _repoTimer = new Timer { Interval = RepositorySnapshotIntervalMs };
            _repoTimer.Tick += (s, e) => EmitRepositorySnapshot();
            _repoTimer.Start();
        }

        // Test seam: bind directly to a supplied SDK surface (no ActPlugins discovery / message loop),
        // then run the same one-shot forward the live bind does. Lets a headless unit test exercise the
        // producer projection deterministically. EmitRepositorySnapshot is invoked explicitly by the test
        // (the live poll timer needs a message pump). Not used on any production path.
        internal void BindForTest(IDataSubscription sub, IDataRepository repo)
        {
            _sub = sub; _repo = repo;
            SubscribeSdk(sub);
            EmitInitialRepositoryState();
        }

        // The full combatant roster with fresh HP/position/party — the poll surface OverlayPlugin/Hojoring
        // consume through IDataRepository.GetCombatantList(). Enqueued onto the same drop-oldest ring.
        internal void EmitRepositorySnapshot()
        {
            try
            {
                var repo = _repo;
                if (repo == null) return;
                var list = repo.GetCombatantList();
                if (list == null) return;
                var actors = new List<Actor>(list.Count);
                foreach (var c in list) if (c != null) actors.Add(ToActor(c));
                Enqueue(new RepositorySnapshot(0, DateTimeOffset.Now, actors));
            }
            catch { /* a bad poll must not kill the timer */ }
        }

        // ---- SDK / ACT handlers → GameEvent projection (no parsing) ---------------------------------

        // The verbatim line stream (PIPELINE-COMPLETENESS-PLAN P2.1, G14): the facade's log-tail seam
        // (FormActMain.FeedLine -> Before/OnLogLineRead) is the sole RawLogLine source — every line the
        // plugin writes to Network_*.log crosses here, byte-for-byte, whether or not a parser is loaded.
        // Import/oracle replays (isImport == true) are not the live stream and are ignored.
        private void OnLogLineRead(bool isImport, LogLineEventArgs args)
        {
            if (isImport || args == null) return;
            var line = args.logLine ?? "";
            // The line's OWN parsed timestamp — never DateTimeOffset.Now (P2.1 verdict).
            var ts = args.detectedTime > DateTime.MinValue
                ? new DateTimeOffset(args.detectedTime)
                : DateTimeOffset.Now;
            Enqueue(new RawLogLine(0, ts, ParseLineType(line), line, line));
        }

        // The frame's LogMessageType, extracted from the line's leading "NN|" key (P0.2 verdict) —
        // never args.detectedType, which FeedLine always sets to 0 on the plugin-free path. Sibling in
        // shape to FormActMain.ParseLineTimestamp: parse the field before the first '|', default safely
        // on malformed/empty input.
        private static LogMessageType ParseLineType(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return default;
            int p = raw.IndexOf('|');
            var key = p >= 0 ? raw.Substring(0, p) : raw;
            return int.TryParse(key, out var n) ? (LogMessageType)n : default;
        }

        private void OnZoneChanged(uint zoneId, string zoneName) =>
            Enqueue(new ZoneChanged(0, DateTimeOffset.Now, zoneId, zoneName ?? ""));

        private void OnPartyListChanged(ReadOnlyCollection<uint> partyList, int partySize) =>
            Enqueue(new PartyChanged(0, DateTimeOffset.Now, partyList != null ? partyList.ToArray() : Array.Empty<uint>()));

        private void OnPrimaryPlayerChanged()
        {
            uint id = 0; string name = "";
            try
            {
                if (_repo != null)
                {
                    id = _repo.GetCurrentPlayerID();
                    var me = _repo.GetCombatantList()?.FirstOrDefault(c => c != null && c.ID == id);
                    if (me != null) name = me.Name ?? "";
                }
            }
            catch { /* best-effort id/name */ }
            Enqueue(new PrimaryPlayerChanged(0, DateTimeOffset.Now, id, name));
        }

        // CombatantAdded/Removed carry the model as `object` (delegate contravariance); cast to project.
        private void OnCombatantAdded(object combatant)
        {
            if (combatant is SdkModels.Combatant c)
                Enqueue(new CombatantAdded(0, DateTimeOffset.Now, ToActor(c)));
        }

        private void OnCombatantRemoved(object combatant)
        {
            if (combatant is SdkModels.Combatant c)
                Enqueue(new CombatantRemoved(0, DateTimeOffset.Now, c.ID));
        }

        // Raw packet firehose (opcode escape hatch OverlayPlugin's network processors consume). Bytes
        // cross verbatim — never decoded here. The ring's drop-oldest keeps this high rate off the SDK thread.
        private void OnNetworkReceived(string connection, long epoch, byte[] message) =>
            Enqueue(new RawPacketReceived(0, DateTimeOffset.Now, connection ?? "", epoch, PacketDirection.Received, message ?? Array.Empty<byte>()));

        private void OnNetworkSent(string connection, long epoch, byte[] message) =>
            Enqueue(new RawPacketReceived(0, DateTimeOffset.Now, connection ?? "", epoch, PacketDirection.Sent, message ?? Array.Empty<byte>()));

        // Shared empty Tags for swings the plugin tags with nothing (avoids a per-swing allocation).
        private static readonly IReadOnlyDictionary<string, object> EmptyTags = new Dictionary<string, object>();

        // The full-fidelity swing, tapped BEFORE aggregation: every MasterSwing field + Tags cross the
        // wire so the net10 engine reproduces ACT's numbers exactly (heals, DoT/HoT, jobs, direct-hit).
        // Damage is the raw Dnum value (keeps ACT's −1 Miss / −10 Death / 0 NoDamage sentinels).
        private void OnBeforeCombatAction(bool isImport, CombatActionEventArgs e)
        {
            var s = e?.combatAction;
            if (s == null) return;
            var ts = s.Time > DateTime.MinValue ? new DateTimeOffset(s.Time) : DateTimeOffset.Now;
            var tags = s.Tags != null && s.Tags.Count > 0 ? s.Tags : EmptyTags;
            Enqueue(new CombatSwing(0, ts, s.SwingType, s.Critical, s.Special ?? "none",
                (long)s.Damage, s.TimeSorter, s.AttackType ?? "", s.Attacker ?? "",
                s.DamageType ?? "", s.Victim ?? "", tags));
        }

        // The encounter lifecycle the plugin drives on the ACT facade (SetEncounter per hostile action,
        // ChangeZone, EndCombat), forwarded so the modern engine opens/refreshes/closes the same encounter.
        private void OnEncounterSet(DateTime time, string attacker, string victim)
        {
            var ts = time > DateTime.MinValue ? new DateTimeOffset(time) : DateTimeOffset.Now;
            Enqueue(new SetEncounterRequested(0, ts, attacker ?? "", victim ?? ""));
        }

        private void OnZoneChange(string zoneName) =>
            Enqueue(new ZoneChangeRequested(0, DateTimeOffset.Now, zoneName ?? ""));

        private void OnCombatEnd(bool export) =>
            Enqueue(new EndCombatRequested(0, DateTimeOffset.Now, export));

        private static Actor ToActor(SdkModels.Combatant c) => new Actor(
            Id: c.ID, OwnerId: c.OwnerID, Kind: MapKind(c.type, c.OwnerID),
            Job: c.Job, Level: c.Level, Name: c.Name ?? "",
            Hp: c.CurrentHP, MaxHp: c.MaxHP, Mp: c.CurrentMP, MaxMp: c.MaxMP,
            Cast: null, Position: new Position(c.PosX, c.PosY, c.PosZ, c.Heading),
            WorldId: c.WorldID, WorldName: c.WorldName ?? "",
            BNpcNameId: c.BNpcNameID, BNpcId: c.BNpcID,
            TargetId: c.TargetID, TargetOfTargetId: 0,
            EffectiveDistance: c.EffectiveDistance,
            Party: MapParty(c.PartyType),
            InCombat: false,
            Statuses: Array.Empty<StatusEffect>(), Enmity: Array.Empty<EnmityEntry>())
        {
            CurrentWorldId = c.CurrentWorldID,
            CurrentCp = c.CurrentCP, MaxCp = c.MaxCP,
            CurrentGp = c.CurrentGP, MaxGp = c.MaxGP,
            Order = c.Order,
        };

        // SDK `type`: 1 = player, 2 = npc. A non-zero OwnerID marks a pet/summon.
        private static ActorKind MapKind(byte type, uint ownerId)
        {
            if (type == 1) return ActorKind.Player;
            if (ownerId != 0) return ActorKind.Pet;
            if (type == 2) return ActorKind.Npc;
            return ActorKind.Unknown;
        }

        private static PartyMembership MapParty(SdkModels.PartyType p)
        {
            switch ((int)p)
            {
                case 1: return PartyMembership.Party;
                case 2: return PartyMembership.Alliance;
                default: return PartyMembership.None;
            }
        }

        // ---- Ring + writer thread -------------------------------------------------------------------

        private void Enqueue(GameEvent evt)
        {
            lock (_gate)
            {
                if (_count == _ring.Length)
                {
                    _head = (_head + 1) & _mask;   // drop oldest — never block the producer
                    _count--;
                    Interlocked.Increment(ref _dropped);
                }
                _ring[_tail] = evt;
                _tail = (_tail + 1) & _mask;
                _count++;
            }
            _signal.Set();
        }

        private bool TryDequeue(out GameEvent evt)
        {
            lock (_gate)
            {
                if (_count == 0) { evt = null; return false; }
                evt = _ring[_head];
                _ring[_head] = null;
                _head = (_head + 1) & _mask;
                _count--;
                return true;
            }
        }

        private void WriteLoop()
        {
            while (_running)
            {
                _signal.WaitOne(100);
                Drain();
            }
            Drain();   // final drain on shutdown
        }

        private void Drain()
        {
            while (TryDequeue(out var evt))
            {
                try
                {
                    var wire = GameEventFrame.ToWire(evt);
                    if (wire != null) { _send(wire); Interlocked.Increment(ref _sent); }
                }
                catch { /* a bad frame must not kill the writer or stall the stream */ }
            }
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();
            try { _writer.Join(2000); } catch { }

            try { _discover?.Stop(); _discover?.Dispose(); } catch { }
            try { _repoTimer?.Stop(); _repoTimer?.Dispose(); } catch { }
            // Isolated in its own method so Dispose itself carries no ActGlobals type reference: reading it
            // loads the facade assembly, which resolves only inside the satellite's AssemblyResolve
            // environment, and that load faults at JIT time (a headless test without it must not fault here).
            try { UnsubscribeActEvents(); } catch { }
            if (_sub != null)
            {
                try { _sub.ZoneChanged -= OnZoneChanged; } catch { }
                try { _sub.PartyListChanged -= OnPartyListChanged; } catch { }
                try { _sub.PrimaryPlayerChanged -= OnPrimaryPlayerChanged; } catch { }
                try { _sub.CombatantAdded -= OnCombatantAdded; } catch { }
                try { _sub.CombatantRemoved -= OnCombatantRemoved; } catch { }
                try { _sub.NetworkReceived -= OnNetworkReceived; } catch { }
                try { _sub.NetworkSent -= OnNetworkSent; } catch { }
                try { _sub.ProcessChanged -= OnProcessChanged; } catch { }
            }
            try { _signal.Dispose(); } catch { }
        }

        private void UnsubscribeActEvents()
        {
            var act = ActGlobals.oFormActMain;
            if (act == null) return;
            try { act.BeforeCombatAction -= OnBeforeCombatAction; } catch { }
            try { act.EncounterSetRaised -= OnEncounterSet; } catch { }
            try { act.ZoneChangeRaised -= OnZoneChange; } catch { }
            try { act.CombatEndRaised -= OnCombatEnd; } catch { }
            try { act.OnLogLineRead -= OnLogLineRead; } catch { }
        }
    }
}
