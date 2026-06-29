using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using FFXIV_ACT_Plugin.Common;

namespace Fct.Parser.Legacy
{
    // A drop-in IDataSubscription that replaces the real plugin's per-subscriber
    // delegate.BeginInvoke fan-out (FFXIV_ACT_Plugin.Memory/DataSubscription.cs) with a bounded
    // ring buffer drained by ONE dispatch thread. OverlayPlugin's ~20 NetworkReceived handlers
    // register here; each real event is enqueued once and fanned out synchronously, in order,
    // with no BeginInvoke and no per-call reflection.
    //
    // Ordering: the dispatch stage is deterministic (subscribers run in registration order on a
    // single thread). Events ARRIVING from the wrapped real subscription cross one upstream
    // BeginInvoke hop we cannot remove (the real plugin always BeginInvokes its subscribers) —
    // but that is one hop instead of twenty, and the injected/bridge path (InjectNetworkReceived)
    // has no upstream hop, so it is fully in-order.
    public sealed class RingBufferDataSubscription : IDataSubscription, IRawPacketSource, IDisposable
    {
        private enum Kind : byte
        {
            NetworkReceived, NetworkSent, LogLine, ParsedLogLine, ZoneChanged,
            CombatantAdded, CombatantRemoved, PrimaryPlayerChanged, PlayerStatsChanged,
            PartyListChanged, ProcessChanged,
        }

        // One tagged slot covering every event shape — no boxing for the hot raw-packet path.
        private struct Slot
        {
            public Kind Kind;
            public string Str;     // connection / zoneName / logline / message
            public long Long;      // epoch
            public byte[] Bytes;   // message
            public uint U1, U2;    // EventType/Seconds/sequence / ZoneID
            public int I1;         // messagetype / partySize
            public object Obj;     // Combatant / playerStats / partyList / Process
        }

        private readonly Slot[] _ring;
        private readonly int _mask;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _dispatch;
        private int _head, _tail, _count;
        private long _dropped;
        private volatile bool _running = true;

        private readonly Action<string> _log;

        // Backing multicast delegates — public events add/remove under _subGate; the dispatch
        // thread snapshots and Invokes them directly (multicast = in-order synchronous fan-out).
        private readonly object _subGate = new object();
        private NetworkReceivedDelegate _networkReceived;
        private NetworkSentDelegate _networkSent;
        private LogLineDelegate _logLine;
        private ParsedLogLineDelegate _parsedLogLine;
        private ZoneChangedDelegate _zoneChanged;
        private CombatantAddedDelegate _combatantAdded;
        private CombatantRemovedDelegate _combatantRemoved;
        private PrimaryPlayerDelegate _primaryPlayerChanged;
        private PlayerStatsChangedDelegate _playerStatsChanged;
        private PartyListChangedDelegate _partyListChanged;
        private ProcessChangedDelegate _processChanged;

        public RingBufferDataSubscription(int capacity = 4096, Action<string> log = null)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
            _ring = new Slot[capacity];
            _mask = capacity - 1;
            _log = log ?? (_ => { });
            _dispatch = new Thread(DispatchLoop)
            {
                Name = "Fct.RingBufferDataSubscription",
                IsBackground = true,
            };
            _dispatch.Start();
        }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        // Handlers registered on NetworkReceived. OverlayPlugin defers these until a live FFXIV
        // process appears (it hooks packet capture on ProcessChanged), so this stays 0 with no game
        // running — the live packet path is exercised by the Tier 3 capture/replay test.
        public int NetworkReceivedSubscriberCount
        {
            get { var h = _networkReceived; return h?.GetInvocationList().Length ?? 0; }
        }

        // Handlers registered on ProcessChanged. OverlayPlugin subscribes this at init to learn when
        // the game starts, so >0 is the no-game live proof that the real OverlayPlugin discovered our
        // wrapper, cast our IDataSubscription, and bound onto the ring (not the plugin's BeginInvoke).
        public int ProcessChangedSubscriberCount
        {
            get { var h = _processChanged; return h?.GetInvocationList().Length ?? 0; }
        }

        // ---- IDataSubscription events ----------------------------------------------------

        public event NetworkReceivedDelegate NetworkReceived
        {
            add { lock (_subGate) _networkReceived = (NetworkReceivedDelegate)Delegate.Combine(_networkReceived, value); }
            remove { lock (_subGate) _networkReceived = (NetworkReceivedDelegate)Delegate.Remove(_networkReceived, value); }
        }
        public event NetworkSentDelegate NetworkSent
        {
            add { lock (_subGate) _networkSent = (NetworkSentDelegate)Delegate.Combine(_networkSent, value); }
            remove { lock (_subGate) _networkSent = (NetworkSentDelegate)Delegate.Remove(_networkSent, value); }
        }
        public event LogLineDelegate LogLine
        {
            add { lock (_subGate) _logLine = (LogLineDelegate)Delegate.Combine(_logLine, value); }
            remove { lock (_subGate) _logLine = (LogLineDelegate)Delegate.Remove(_logLine, value); }
        }
        public event ParsedLogLineDelegate ParsedLogLine
        {
            add { lock (_subGate) _parsedLogLine = (ParsedLogLineDelegate)Delegate.Combine(_parsedLogLine, value); }
            remove { lock (_subGate) _parsedLogLine = (ParsedLogLineDelegate)Delegate.Remove(_parsedLogLine, value); }
        }
        public event ZoneChangedDelegate ZoneChanged
        {
            add { lock (_subGate) _zoneChanged = (ZoneChangedDelegate)Delegate.Combine(_zoneChanged, value); }
            remove { lock (_subGate) _zoneChanged = (ZoneChangedDelegate)Delegate.Remove(_zoneChanged, value); }
        }
        public event CombatantAddedDelegate CombatantAdded
        {
            add { lock (_subGate) _combatantAdded = (CombatantAddedDelegate)Delegate.Combine(_combatantAdded, value); }
            remove { lock (_subGate) _combatantAdded = (CombatantAddedDelegate)Delegate.Remove(_combatantAdded, value); }
        }
        public event CombatantRemovedDelegate CombatantRemoved
        {
            add { lock (_subGate) _combatantRemoved = (CombatantRemovedDelegate)Delegate.Combine(_combatantRemoved, value); }
            remove { lock (_subGate) _combatantRemoved = (CombatantRemovedDelegate)Delegate.Remove(_combatantRemoved, value); }
        }
        public event PrimaryPlayerDelegate PrimaryPlayerChanged
        {
            add { lock (_subGate) _primaryPlayerChanged = (PrimaryPlayerDelegate)Delegate.Combine(_primaryPlayerChanged, value); }
            remove { lock (_subGate) _primaryPlayerChanged = (PrimaryPlayerDelegate)Delegate.Remove(_primaryPlayerChanged, value); }
        }
        public event PlayerStatsChangedDelegate PlayerStatsChanged
        {
            add { lock (_subGate) _playerStatsChanged = (PlayerStatsChangedDelegate)Delegate.Combine(_playerStatsChanged, value); }
            remove { lock (_subGate) _playerStatsChanged = (PlayerStatsChangedDelegate)Delegate.Remove(_playerStatsChanged, value); }
        }
        public event PartyListChangedDelegate PartyListChanged
        {
            add { lock (_subGate) _partyListChanged = (PartyListChangedDelegate)Delegate.Combine(_partyListChanged, value); }
            remove { lock (_subGate) _partyListChanged = (PartyListChangedDelegate)Delegate.Remove(_partyListChanged, value); }
        }
        public event ProcessChangedDelegate ProcessChanged
        {
            add { lock (_subGate) _processChanged = (ProcessChangedDelegate)Delegate.Combine(_processChanged, value); }
            remove { lock (_subGate) _processChanged = (ProcessChangedDelegate)Delegate.Remove(_processChanged, value); }
        }

        // ---- Wiring: subscribe once to the real subscription -----------------------------

        // Funnel every event of the wrapped real IDataSubscription through our ring. The real
        // plugin then has exactly one subscriber per event (us) instead of OverlayPlugin's ~20.
        public void AttachUpstream(IDataSubscription real)
        {
            if (real == null) throw new ArgumentNullException(nameof(real));
            real.NetworkReceived += OnNetworkReceived;
            real.NetworkSent += OnNetworkSent;
            real.LogLine += OnLogLine;
            real.ParsedLogLine += OnParsedLogLine;
            real.ZoneChanged += OnZoneChanged;
            real.CombatantAdded += OnCombatantAdded;
            real.CombatantRemoved += OnCombatantRemoved;
            real.PrimaryPlayerChanged += OnPrimaryPlayerChanged;
            real.PlayerStatsChanged += OnPlayerStatsChanged;
            real.PartyListChanged += OnPartyListChanged;
            real.ProcessChanged += OnProcessChanged;
        }

        private void OnNetworkReceived(string c, long e, byte[] m) =>
            Enqueue(new Slot { Kind = Kind.NetworkReceived, Str = c, Long = e, Bytes = m });
        private void OnNetworkSent(string c, long e, byte[] m) =>
            Enqueue(new Slot { Kind = Kind.NetworkSent, Str = c, Long = e, Bytes = m });
        private void OnLogLine(uint t, uint s, string l) =>
            Enqueue(new Slot { Kind = Kind.LogLine, U1 = t, U2 = s, Str = l });
        private void OnParsedLogLine(uint seq, int mt, string msg) =>
            Enqueue(new Slot { Kind = Kind.ParsedLogLine, U1 = seq, I1 = mt, Str = msg });
        private void OnZoneChanged(uint id, string name) =>
            Enqueue(new Slot { Kind = Kind.ZoneChanged, U1 = id, Str = name });
        private void OnCombatantAdded(object cb) =>
            Enqueue(new Slot { Kind = Kind.CombatantAdded, Obj = cb });
        private void OnCombatantRemoved(object cb) =>
            Enqueue(new Slot { Kind = Kind.CombatantRemoved, Obj = cb });
        private void OnPrimaryPlayerChanged() =>
            Enqueue(new Slot { Kind = Kind.PrimaryPlayerChanged });
        private void OnPlayerStatsChanged(object ps) =>
            Enqueue(new Slot { Kind = Kind.PlayerStatsChanged, Obj = ps });
        private void OnPartyListChanged(ReadOnlyCollection<uint> list, int size) =>
            Enqueue(new Slot { Kind = Kind.PartyListChanged, Obj = list, I1 = size });
        private void OnProcessChanged(Process p) =>
            Enqueue(new Slot { Kind = Kind.ProcessChanged, Obj = p });

        // ---- IRawPacketSource ------------------------------------------------------------

        public void InjectNetworkReceived(string connection, long epoch, byte[] message) =>
            Enqueue(new Slot { Kind = Kind.NetworkReceived, Str = connection, Long = epoch, Bytes = message });

        // ---- Ring --------------------------------------------------------------------------

        private void Enqueue(Slot s)
        {
            lock (_gate)
            {
                if (_count == _ring.Length)
                {
                    // Drop oldest — never block the producer (the real capture thread).
                    _head = (_head + 1) & _mask;
                    _count--;
                    Interlocked.Increment(ref _dropped);
                }
                _ring[_tail] = s;
                _tail = (_tail + 1) & _mask;
                _count++;
            }
            _signal.Set();
        }

        private bool TryDequeue(out Slot s)
        {
            lock (_gate)
            {
                if (_count == 0) { s = default; return false; }
                s = _ring[_head];
                _ring[_head] = default; // release byte[]/object refs promptly
                _head = (_head + 1) & _mask;
                _count--;
                return true;
            }
        }

        private void DispatchLoop()
        {
            while (_running)
            {
                _signal.WaitOne(100);
                while (TryDequeue(out var s))
                    Dispatch(s);
            }
            // Final drain on shutdown.
            while (TryDequeue(out var s))
                Dispatch(s);
        }

        private void Dispatch(Slot s)
        {
            try
            {
                switch (s.Kind)
                {
                    case Kind.NetworkReceived: _networkReceived?.Invoke(s.Str, s.Long, s.Bytes); break;
                    case Kind.NetworkSent: _networkSent?.Invoke(s.Str, s.Long, s.Bytes); break;
                    case Kind.LogLine: _logLine?.Invoke(s.U1, s.U2, s.Str); break;
                    case Kind.ParsedLogLine: _parsedLogLine?.Invoke(s.U1, s.I1, s.Str); break;
                    case Kind.ZoneChanged: _zoneChanged?.Invoke(s.U1, s.Str); break;
                    case Kind.CombatantAdded: _combatantAdded?.Invoke(s.Obj); break;
                    case Kind.CombatantRemoved: _combatantRemoved?.Invoke(s.Obj); break;
                    case Kind.PrimaryPlayerChanged: _primaryPlayerChanged?.Invoke(); break;
                    case Kind.PlayerStatsChanged: _playerStatsChanged?.Invoke(s.Obj); break;
                    case Kind.PartyListChanged: _partyListChanged?.Invoke((ReadOnlyCollection<uint>)s.Obj, s.I1); break;
                    case Kind.ProcessChanged: _processChanged?.Invoke((Process)s.Obj); break;
                }
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not kill the dispatch thread or stall the stream.
                _log($"[RingDispatch] subscriber threw on {s.Kind}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();
            try { _dispatch.Join(2000); } catch { }
            _signal.Dispose();
        }
    }
}
