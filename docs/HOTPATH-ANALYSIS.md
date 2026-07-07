# Hot-Path Analysis — Host↔Plugin Message Flow

A concurrency and performance review of the live event path, from the parser's fan-out in the
net48 satellite to plugin delivery in the net10 host. Findings are ranked by severity; each states
the current behavior (with code references), the failure scenario, and a recommended solution.

Scope reviewed:

```
FFXIV_ACT_Plugin (real)
  └─ RingBufferDataSubscription        src/Fct.Parser.Legacy/RingBufferDataSubscription.cs
       ├─ OverlayPlugin handlers (in-satellite, synchronous fan-out)
       └─ BridgeForwarder              src/Fct.LegacyHost/BridgeForwarder.cs
            └─ SendLine (named pipe)   src/Fct.LegacyHost/Program.cs   [shared with BridgeLogSink]
                 └─ SatelliteHost pump src/Fct.Host/SatelliteHost.cs
                      └─ GameEventBus  src/Fct.Host/Hosting/GameEventBus.cs
                           ├─ GameSnapshotAggregator (state fold → IGameSnapshot)
                           ├─ RawPacketSource (capability-gated firehose)
                           └─ Shim: FormActMain hub + DataSubscriptionAdapter
                                     src/Fct.Compat.Shim*, src/Fct.Compat.Shim.ActFacade
net48 ACT engine path (in-satellite):
  FormActMain log tail → plugin parse → AddCombatAction → Fct.Aggregation
                                     src/Fct.Compat.Act/FormActMain.cs, src/Fct.Aggregation
```

## Design properties that hold today (preserve these)

The bounded-everything / never-block-the-producer / single-writer-pipe / drop-oldest design of the
event path is described in [`ARCHITECTURE.md`](ARCHITECTURE.md) §9 (IPC bridge) — that is the
invariant set every finding below must preserve. The concrete code sites that realize it, which the
findings reference:

- **Producers never block; drainer threads own I/O.** SDK/ACT callbacks only enqueue; pipe and
  handler I/O happen on dedicated threads (`RingBufferDataSubscription._dispatch`,
  `BridgeForwarder._writer`, one pump per bus subscription).
- **Single-writer pipe discipline.** All satellite→host lines go through one locked writer
  (`Program.SendLine`), so frames never interleave; the host reads with a single pump.
- **One ordering authority.** The host re-stamps `Sequence` from its own sink
  (`SatelliteHost.TryEmitGameEvent`), so the bus keeps one coherent per-session sequence space.
- **Per-subscription pumps.** A slow or throwing plugin handler stalls only its own channel,
  never peers or the producer (`GameEventBus.CallbackSubscription`).
- **Control/firehose pipe separation.** Host→satellite commands ride a second pipe, so a user
  command never interleaves with the satellite→host stream.
- **Lifetime safety.** Kill-on-close job object + named shutdown event avoid pipe-rendezvous
  deadlocks and orphaned satellites.

---

## High severity

### H1. State-bearing events share drop-oldest lanes with the firehose

**Current behavior.** `BridgeForwarder` routes every forwarded event through one 4096-slot
drop-oldest ring (`BridgeForwarder.Enqueue`): the `RawLogLine` firehose, the `RawPacketReceived`
firehose, and the low-rate state events (`ZoneChanged`, `CombatantAdded`/`CombatantRemoved`,
`PartyChanged`, `PrimaryPlayerChanged`, `ActionEffect`). On the host, every `GameEventBus`
subscription is likewise one 1024-slot drop-oldest channel (`GameEventBus.Subscription` ctor),
so `GameSnapshotAggregator`'s subscription can also drop state events when its pump falls behind.

**Failure scenario.** Loss is asymmetric by event class. A dropped log line or packet loses one
frame; a dropped `CombatantRemoved` leaves a ghost actor in `GameSnapshotAggregator._actors`
**permanently** — there is no reconciliation pass, and the shim's
`IDataRepository.GetCombatantList()` serves that snapshot to legacy plugins indefinitely. A
dropped `ZoneChanged` mis-attributes everything after it. Zone-in is the worst case: a
combatant-add burst, a log-line burst, and packet traffic land simultaneously, exactly when the
pipe writer is slowest (see H2), so the ring is most likely to evict under exactly the burst that
carries the state events.

**Recommended solution.**
1. **Two lanes in `BridgeForwarder`, split by loss tolerance.** Keep the drop-oldest ring for
   `RawLogLine`/`RawPacketReceived`. Give state events a lane that never drops silently — they
   run at a few events/sec, so a small dedicated queue drained by the same writer thread (state
   lane first, then firehose) is sufficient and preserves the never-block-the-producer rule.
2. **Loud drops for state events on the host bus.** When a bounded channel evicts, the dropped
   item is visible to the `itemDropped` callback — log at Warning with the event type when the
   victim is state-bearing, instead of only incrementing a counter.
3. **Self-healing snapshot.** Have the satellite re-send a full combatant roster (a) periodically
   at a slow cadence, or (b) whenever its `DroppedCount` advances. `GameSnapshotAggregator`
   replaces its roster on such a frame. This bounds the damage of any residual loss to one refresh
   interval.

### H2. Satellite pipe writes are synchronous on logging threads

**Current behavior.** `Program.SendLine` writes with `AutoFlush = true` under `_sendLock`. Three
producer classes share it:

- `BridgeForwarder`'s writer thread (buffered by its ring — safe);
- protocol frames from the UI thread (`SendPlugin`, `UNLOADED` acks);
- `BridgeLogSink.Emit`, which Serilog invokes **synchronously on whatever thread logs** —
  `SatelliteLogging` registers the sink directly (`WriteTo.Sink(new BridgeLogSink())`), with no
  async wrapper.

**Failure scenario.** Named-pipe writes block when the pipe buffer fills. The host's reader is a
single pump that also decodes frames, re-emits log records, and publishes notifications
(`SatelliteHost.PumpBridgeAsync`). If the host pauses (GC, UI stall, debugger, a busy pump), the
pipe fills, the current writer blocks holding `_sendLock`, and then **every satellite thread that
logs blocks inside `BridgeLogSink`** — including the WinForms UI thread (freezing plugin UI and
the timers that drive discovery/heartbeat) and the SDK dispatch thread (stalling event delivery to
OverlayPlugin). The rings protect game events from a slow pipe; the log path has no such buffer.

Secondary effect: firehose EVT traffic and command acks contend on one lock, so `UNLOADED` /
`PLUGIN` frames queue behind bulk EVT writes. Under load the host's unload-ack timeout can fire
spuriously (priority inversion).

**Recommended solution.**
1. **No producer thread ever touches pipe I/O.** Route `BridgeLogSink` through the same
   bounded-ring-plus-writer-thread model as game events (a small dedicated queue is fine), or wrap
   the sink with Serilog's async sink. Either way, `Emit` must reduce to an enqueue.
2. **Batch flushing.** Set `AutoFlush = false` and flush once per `Drain()` batch in the writer
   thread (and after each protocol frame). One flush per drained batch instead of one syscall per
   line raises the bridge's throughput ceiling substantially and shortens lock hold times.
3. Optionally, write protocol/ack frames ahead of queued firehose frames (a two-queue writer)
   so acks stay prompt under load.

### H3. The raw-packet firehose is forwarded and decoded unconditionally

**Current behavior.** `BridgeForwarder.SubscribeSdk` always subscribes `NetworkReceived` /
`NetworkSent`. Every packet is base64-encoded into a text frame (`GameEventFrame.ToWire`, `PKT`
tag), written line-by-line to the pipe, split and base64-decoded by the host pump
(`GameEventFrame.TryDecodePacket`) — **before** any filter runs. Raw packets are opt-in on the
host (`GameEventFilter.IncludeRawPackets`; the `raw` manifest capability gates
`RawPacketSource`), so with no raw-capability plugin loaded, the entire encode → +33% pipe
bandwidth → split → decode → allocate pipeline is discarded at the bus.

**Failure scenario.** Not a correctness bug — a standing tax. It is the traffic most likely to
trigger H1 evictions and H2 stalls, paid even when provably useless.

**Recommended solution.** Demand-driven forwarding over the existing command pipe. Add
`WANTPACKETS on|off` to `SatelliteProtocol`; the host sends it when the first raw-capability
subscriber appears and when the last disappears (`RawPacketSource` knows its handler count —
expose a subscriber-count change hook). `BridgeForwarder` attaches/detaches the two SDK network
handlers accordingly. Default off. Log lines stay always-on (cheap, nearly always consumed).

---

## Medium severity

### M1. Net10 shim: one legacy plugin, three unsynchronized delivery threads with independent loss

**Current behavior.** A recompiled plugin hosted by `LegacyPluginHost` receives correlated events
from three sources, each with its own pump thread and its own bounded channel:

1. `FormActMain.OnLogLineRead` — the hub's own bus subscription
   (`Fct.Compat.Shim.ActFacade/FormActMain.cs`, `Subscribe(GameEventFilter.All, OnGameEvent)`);
2. `IDataSubscription.LogLine` etc. — `DataSubscriptionAdapter`'s separate bus subscription;
3. `NetworkReceived`/`NetworkSent` — `RawPacketSource`'s fan-out thread.

**Failure scenario.** Cross-surface ordering is not guaranteed: the same conceptual line can reach
`OnLogLineRead` before or after `DataSubscription.LogLine`. Drops are independent per channel, so
under backpressure one surface delivers an event the other silently lost — the plugin's two views
of the stream diverge. Under real ACT both surfaces are driven from one pipeline, and legacy
plugins (Hojoring especially) may bake in that ordering. Symptom shape: "trigger fired against a
stale combatant," intermittent, load-dependent, unreproducible in tests.

**Recommended solution.** One bus subscription per process for the legacy surfaces: the hub owns
the single `Subscribe(GameEventFilter.All, …)` and forwards each event synchronously to the
`DataSubscriptionAdapter` (adapter becomes a plain projector invoked by the hub, not a bus
subscriber). Ordering and loss are then shared by construction, and one pump thread per process is
saved. The raw-packet surface can stay on `RawPacketSource` (real ACT delivers packets on separate
threads too), but line-derived surfaces must share one stream.

### M2. `RingBufferDataSubscription.Dispatch` invokes the raw multicast delegate

**Current behavior.** `Dispatch` calls `_networkReceived?.Invoke(...)` with the try/catch around
the whole invocation. Multicast invocation stops at the first throw, so if OverlayPlugin handler
#3 of ~20 throws, handlers #4–20 never see that event. The per-subscriber `BeginInvoke` fan-out
this class replaces isolated each subscriber — so this is a behavioral regression versus the real
plugin, not only a robustness nit. Additionally, the backing delegate fields are written under
`_subGate` but read lock-free on the dispatch thread with no `volatile` — visibility of a newly
added subscriber currently rides on the incidental fences of the ring's `_gate`.

**Recommended solution.** Cache the invocation list on mutation: on add/remove (already under
`_subGate`) store `handler?.GetInvocationList()` into a `volatile Delegate[]` field per event.
`Dispatch` reads the array once and invokes each entry inside its own try/catch. Hot path stays
allocation-free (no per-dispatch `GetInvocationList()`), per-subscriber isolation matches the real
plugin, and the `volatile` array read fixes the visibility gap.

### M3. Net48 aggregation engine: unsynchronized collections, cross-thread readers

**Current behavior.** `Fct.Compat.Act/FormActMain` mutates the engine on the `act-logtail` thread
(`FeedLine` → plugin parse → `AddCombatAction`/`SetEncounter`/`EndCombat`), writing plain
`SortedList`/`Dictionary` state in `Fct.Aggregation` (`EncounterData.Items` is a
`SortedList<string, CombatantData>`). Concurrent readers on other threads: OverlayPlugin's
MiniParse timer, the capture heartbeat (`Program.DescribeActiveEncounter` enumerates
`enc.Items.Values`), Triggernometry's export path, and `EncounterProjector`. A `SortedList` read
during an insert can throw or return a torn view.

**Assessment.** Real ACT has the same race and OverlayPlugin tolerates it; reproducing it is
parity-correct, and locking the engine could perturb behavior the empirical oracle validates.
**Do not add locks to the engine.** The gaps that are ours to close:

**Recommended solution.**
1. Every FCT-owned reader must be torn-read-defensive. The heartbeat already wraps in try/catch;
   `EncounterProjector.Project` does not — a torn read during a live encounter throws out of
   whatever host path called it. Wrap the projection (retry-once-then-skip is what OverlayPlugin
   effectively does).
2. Record the accepted race in `docs/ARCHITECTURE.md` (parity constraint, reader obligations), so
   it is not later "fixed" into a lock that deadlocks or breaks oracle parity.

### M4. `GameEventBus.Emit` snapshots the subscriber list per event under the lock

**Current behavior.** `Emit` takes `_gate` and `_subs.ToArray()` for every event — every raw
packet and log line pays one lock acquisition plus one array allocation, scaling with subscriber
count.

**Recommended solution.** Copy-on-write: maintain a `Subscription[]` field rebuilt inside
`Add`/`Remove` (rare), and have `Emit` do a single `Volatile.Read` of the array with no lock and
no allocation. Dispose semantics are unchanged (a concurrently removed subscription still just
receives a benign `TryWrite` to a completed channel).

---

## Low severity

| # | Finding | Location | Recommended solution |
|---|---|---|---|
| L1 | Duplicate-key unload ack: `_pendingUnloads[key] = tcs` silently orphans a prior waiter for the same key until its timeout. | `SatelliteHost.RequestUnloadPluginAsync` | `TryAdd` and return false on conflict (or complete the displaced TCS with false). |
| L2 | `InvokeOnUi` fallback runs the command inline on the cmd-reader thread when the facade handle is not yet created — `_plugins` and WinForms control creation then run off the UI thread. | `Program.InvokeOnUi` | Queue commands until the handle exists (defer instead of falling through to `action()`). |
| L3 | `DateTimeOffset.Now` per event on the SDK dispatch thread — a timezone conversion per packet/line. | `BridgeForwarder` handlers | Stamp `UtcNow`; convert to local at display time. |
| L4 | `AutoResetEvent.Set()` per enqueue is a kernel transition per event; the 100 ms `WaitOne` poll adds permanent idle wakeups. | Both rings (`RingBufferDataSubscription`, `BridgeForwarder`) | `Monitor.Wait`/`Pulse` under the existing `_gate` (pulse only on empty→non-empty), removing both the syscall and the poll. |
| L5 | Host pump does decode + log re-emit + notification publish inline on the single reader thread. Correct today because `Emit` never blocks; any future blocking call added there back-pressures the pipe and triggers H2 satellite-side. | `SatelliteHost.PumpBridgeAsync` / `ReEmit` / `MaybeNotify` | Keep the pump non-blocking as an invariant (comment it); move any future slow consumer behind its own queue. |
| L6 | `Publish()` rebuilds the full actor array per state event — a zone-in with N combatant adds is O(N²). | `GameSnapshotAggregator.Publish` | Coalesce: republish at most once per pump drain (flag + trailing publish), or debounce publishes by a few ms. |

---

## Suggested priority order

1. **H2** — log sink off the pipe-writer path + batch flushing. Cheapest fix; removes the only
   whole-process-freeze hazard.
2. **H1 + H3 together** — state/firehose lane split, loud state drops, roster refresh, and
   demand-gated packet forwarding. Closes the silent state-corruption path and most wasted
   bandwidth; they share the protocol/ring work.
3. **M2** — per-subscriber isolation in the ring dispatch. Fidelity regression versus the real
   plugin; small, contained fix.
4. **M1** — single bus subscription behind the shim's legacy surfaces, before real recompiled
   plugins run on the shim.
5. **M3 (reader hardening + doc note), M4, and L-items** opportunistically.
