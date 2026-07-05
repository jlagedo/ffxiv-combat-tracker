# Satellite Isolation Plan — one plugin package per process, everything routed by the host

**The tracking document for the multi-satellite architecture.** It defines the isolation
invariants, the target topology, and the phased implementation path — each phase gated by
automated end-to-end tests so progress is measurable and regressions are impossible to miss.
Phase checkboxes below are the live status.

Companion docs: [`ARCHITECTURE.md`](ARCHITECTURE.md) (the design and rationale),
[`DATA-FLOW.md`](DATA-FLOW.md) (the upstream legacy contract each satellite facade reproduces),
[`PLUGIN-API.md`](PLUGIN-API.md) (the host pipes), [`TESTING.md`](TESTING.md) (the parity/oracle
harnesses the gates build on).

---

## 1. The invariants (the doctrine every decision defers to)

1. **All ACT calculations live in the .NET 10 host.** The host's `Fct.Engine`
   (`ModernEncounterEngine` + the shared `Fct.Aggregation` engine) is the **single source of
   truth** for encounter aggregation, DPS, and `ExportVariables`. Any other engine instance is
   a **compat replica**: the identical `Fct.Aggregation` binary, replaying the identical
   host-routed swing stream, existing solely so an unmodified legacy plugin can keep its
   synchronous in-process reads. Replicas are held equal to the host engine by automated parity
   tests — they are projections, never authorities.
2. **One satellite process per legacy plugin package.** Every legacy plugin package runs in its
   own `Fct.LegacyHost` (net48) process against its own private ACT facade. No two plugin
   packages ever share a managed heap.
3. **Plugins never exchange data directly. Every inter-plugin path is routed by the host.**
   A satellite's facade is host-owned code: what a plugin produces, the facade taps and ships
   to the host; what a plugin consumes, the host fans out and the facade projects. The process
   boundary makes this physically enforceable — there is no shared heap left to bypass it.
4. **The host knows no plugin.** Routing is keyed by stream and capability names, never plugin
   identity. Plugin-name knowledge is confined to (a) each satellite's *legacy discovery seam*
   (reproducing the title/status-string reflection the plugins themselves perform — see
   [`DATA-FLOW.md`](DATA-FLOW.md) §4.1) and (b) the `Fct.App` install-catalog UI. Neither
   reaches host routing or engine logic.
5. **The sole-parser directive is untouched.** The real FFXIV_ACT_Plugin remains the only
   parser; the host parses nothing, decodes nothing, and raw bytes/lines cross verbatim.

## 2. Locked decisions

- **Granularity — per plugin package.** One satellite per installed package: FFXIV_ACT_Plugin
  (the parser satellite), OverlayPlugin (cactbot lives inside it — it is an OverlayPlugin
  event source/overlay, not an ACT plugin), Triggernometry, ACT-Discord-Triggers, and the
  ACT.Hojoring suite (its assemblies share in-process state by design — one satellite).
- **No co-location exceptions.** OverlayPlugin is fully isolated from the parser; its
  synchronous `IDataRepository` polls are served by a local mirror fed from the host-routed
  repository-snapshot stream.
- **Hard cutover.** The runtime is built directly to the multi-satellite topology on `main`
  (nothing has shipped; there is no compatibility mode to preserve). The single
  multi-package satellite exists only until P7 removes it. The satellite's dev-standalone
  mode (`Fct.LegacyHost.exe` with no `--bridge`) remains a dev-only convenience.
- **E2E strategy — replay-driven, headless.** End-to-end gates run real satellite processes
  fed by recorded game streams (frame replay and `--replay` log replay); no live game in CI.
  Tests that need the real FFXIV_ACT_Plugin binary skip cleanly when it is not installed.

## 3. Target topology

```
                    ┌────────────────────────────────────────────┐
                    │        Fct.App + Fct.Host (net10)          │
                    │  Fct.Engine ◄─ THE aggregation authority   │
                    │  typed bus ◄─ THE router (streams,         │
                    │  IGameSnapshot, audio, callbacks)          │
                    └───┬──────────┬──────────┬──────────┬───────┘
              pipe (up+down)│      │          │          │   one duplex pipe per satellite
                    ┌───────▼──┐ ┌─▼────────┐ ┌▼────────┐ ┌▼─────────┐ ┌──────────┐
                    │ parser   │ │ Overlay  │ │ Trigger-│ │ Discord- │ │ Hojoring │
                    │ satellite│ │ Plugin   │ │ nometry │ │ Triggers │ │ satellite│
                    │ FFXIV_   │ │ satellite│ │ satellite│ │ satellite│ │          │
                    │ ACT_     │ │ (CEF,    │ │          │ │          │ │          │
                    │ Plugin   │ │  Fleck)  │ │          │ │          │ │          │
                    └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘
                     each: Fct.LegacyHost.exe + private ACT facade + engine replica
```

- **Upstream (satellite → host, built):** the facade taps what its plugin produces and ships
  typed `GameEventFrame`s — `RawLogLine`, `RawPacketReceived`, `ZoneChanged`, `PartyChanged`,
  `PrimaryPlayerChanged`, `CombatantAdded/Removed`, `ActionEffect`, full-fidelity `CombatSwing`
  + `SetEncounter/ZoneChange/EndCombat` lifecycle. New upstream pipes: repository snapshots
  (P5), audio requests + sink registration (P6), custom-log-line write-back (P6).
- **Downstream (host → satellite, new):** the same `GameEventFrame` codec in reverse. Each
  satellite subscribes to the stream set its facade needs; the host fans out from the bus with
  a bounded per-satellite egress queue (a slow satellite degrades alone).
- **Control (per satellite, extended):** the `SatelliteProtocol` handshake carries a satellite
  identity + hosted package; commands target one satellite (`LOADPLUGIN`, subscriptions,
  capability registration).
- **Consumer projection (new, inside each satellite):** the facade serves its plugin's reads
  from host-routed data — the local `Fct.Aggregation` replica replays the fanned swing stream
  for synchronous `ActiveZone.ActiveEncounter`/`ExportVariables` reads; `Before/OnLogLineRead`
  re-raise the fanned `RawLogLine`s; a **synthetic parser stand-in** in `ActPlugins` (title
  `FFXIV_ACT_Plugin`, status `FFXIV_ACT_Plugin Started.`) exposes `DataSubscription` /
  `DataRepository` properties and an `_iocContainer`-shaped `ILogOutput` seam, all backed by
  the host streams — so OverlayPlugin/Triggernometry/Hojoring discovery-by-reflection binds
  exactly as under real ACT ([`DATA-FLOW.md`](DATA-FLOW.md) §4).

## 4. Phases

Every phase lands with its automated gate green in CI (`test.ps1`) before the next begins.
Gates marked **[plugin-gated]** additionally need `FFXIV_ACT_Plugin.dll` installed and skip
cleanly without it.

### P0 — Doctrine & documentation ✅

- [x] Invariants + topology recorded here; `CLAUDE.md`, `ARCHITECTURE.md`, `DATA-FLOW.md`,
      `PLUGIN-API.md`, `TESTING.md` aligned (no doc frames satellite-side aggregation or
      direct plugin-to-plugin access as the end state).

### P1 — Host engine authority (complete the in-flight `Fct.Engine` work)

The host engine exists, is DI-wired, and passes unit tests; this phase makes its authority
*proven* rather than asserted.

- [x] Commit `Fct.Engine` + `EncounterLifecycle`/`EngineTables` + `BridgeForwarder` swing
      forwarding (the current uncommitted work).
- [x] Retire the orphaned placeholder `Fct.Host/Hosting/EncounterService.cs`.
- [x] **Cross-runtime engine parity test:** feed the committed oracle fixtures
      (`combat-slice*.oracle.tsv`) through `ModernEncounterEngine` and assert every
      `ExportVariables` string equals the committed real-ACT baselines
      (`combat-slice*.exportvars.tsv`) — the net10 engine held to the same oracle as the
      net48 engine, bit-for-bit. (`Fct.Engine.Tests/OracleParityTests`. Surfaced + fixed a
      cross-TFM bug: `CombatTables.Cds` rendered a NaN/Infinity DPS as `"0"` on net10 vs
      real ACT's `"NaN"`, relying on net48-only `(long)NaN==long.MinValue` overflow.)
- [x] **Wire-path e2e** (`Fct.Integration.Tests/ReplayBridgeRouteTests`): a satellite
      `--replay … --bridge` run forwards the swing/lifecycle stream over the real pipe; the
      host `ModernEncounterEngine` folds the decoded frames and its per-encounter YOU total
      equals the satellite's captured baseline, bit-for-bit. **[plugin-gated]**

**Exit:** the host engine is demonstrably interchangeable with the net48 engine on identical
input; divergence fails CI. ✅

> **Known pre-existing loose end (from the in-flight commit, resolve by P5/P7):** `Fct.App`'s
> deps.json still lists `Fct.Aggregation` because `Fct.Engine`'s `ProjectReference` ships it
> next-to-exe *and* it is staged under `compat\` — two identities, violating the one-identity
> invariant (`Fct.App.Tests/StaticGraphTests` red for `Fct.Aggregation`). The host engine must
> resolve the single `compat\` copy via `CompatRuntime`; the fix is a reliably non-transitive
> compile-only reference plus verifying the `CompatRuntime.Enable`-before-engine-construction
> ordering by launching the app. Deferred here to avoid destabilizing startup mid-P1.

### P2 — Frame-replay harness (the e2e foundation for every later phase)

- [x] `GameEventFrame` session recorder: an opt-in sink that records the decoded frame stream
      (offset-from-start + wire) to a file. `Fct.Bridge.Contracts/FrameSessionRecorder` +
      the `FrameSession` line codec (record/replay), reused by host, satellite, and tests.
- [x] Committed, anonymized frame fixtures generated from `--replay` runs:
      `tests/fixtures/frames/combat-slice{,2}.frames.tsv` (971 / 840 aggregation frames),
      produced by the env-gated `Fct.Integration.Tests/FrameFixtureGenerator` from the
      anonymized slice corpus.
- [x] Frame-replay driver, usable two ways: **in-process** (decode fixture → host bus →
      engine, `Fct.Engine.Tests/FrameReplayTests`) and as a **replay satellite** — the new
      plugin-free `Fct.LegacyHost --replay-frames <fixture> --bridge <pipe>` mode that speaks
      the satellite handshake and streams the wire frames up the pipe.
- [x] Gate: replaying a fixture reproduces its recorded encounter totals through the host
      engine, deterministically, with **no plugin installed** — in-process
      (`FrameReplayTests`) and over the real pipe via the replay satellite
      (`Fct.Integration.Tests/FrameReplaySatelliteTests`). YOU totals tied to the real-ACT
      oracle aggregate baseline (three-way parity: oracle → wire-path → frame-replay).

**Exit:** any consumer-side behavior can be exercised headlessly from a committed fixture. ✅

### P3 — Multi-satellite process fabric

- [x] Handshake v2: satellite identity + hosted package in `SatelliteProtocol` (`proto`/`id`/
      `pkg` appended to `READY`, `ProtocolVersion=2`, `ReadySatelliteId`/`ReadyPackage`/
      `ReadyProtocol` parsers; both ends). The satellite takes `--satellite-id`/`--package`;
      the host verifies the echoed id.
- [x] Host: N concurrent `SatelliteHost` instances via `SatelliteSupervisor` — per-satellite
      pipe names (already GUID-unique), per-id log category (`Fct.Satellite.<id>`), lifecycle
      supervision: exit detection (`SatelliteHost.ProcessExited`), restart with exponential
      backoff + crash-loop quarantine (`RestartPolicy`, unit-tested). *Per-satellite embedded
      UI view deferred to the Fct.App migration (a UX concern that does not gate the data plane
      — open question §5).*
- [x] Satellite: single-package hosting (`--satellite-id`, one process per identity); the
      per-satellite verification artifact is now identity-keyed (`s2-<id>.log`). *Rejecting a
      second package on one process is enforced at P7 when the packages actually split.*
- [ ] `Fct.App`: spawns one satellite per installed legacy package (catalog-driven). *Deferred
      to P7 — the App still runs one satellite until the packages are split; the supervisor it
      will drive is in place. `AppData` gained an `FCT_INSTALL_DIR` override so the host runtime
      can be driven from a staged tree.*
- [x] Gate (e2e, no plugins): `Fct.Integration.Tests/SatelliteSupervisorTests` launches three
      empty satellites with distinct identities; killing one leaves the others' processes live
      and the host healthy; the dead one restarts and re-handshakes with the same id + a new
      pid; each writes its own `s2-<id>.log`.

**Exit:** process-per-package fabric with crash isolation, supervised, observable. ✅ (data plane;
the Fct.App per-package spawn + per-satellite UI land with P7.)

### P4 — Downstream data plane (host → satellite fan-out)

- [x] `GameEventFrame` flows host→satellite over the command pipe (the same codec both
      directions); the satellite decodes downstream frames (`--sink` mode records them).
- [x] `SUBSCRIBE` control command: a satellite declares its stream set
      (`SatelliteProtocol.FormatSubscribe`/`TryParseSubscribe` + canonical `Stream*` tokens);
      the host maps tokens→`GameEventFilter` via `StreamCatalog` (routes by stream name, never
      plugin identity — invariant §4).
- [x] Per-satellite bounded egress queue + drop-oldest + sent/dropped counters (`SatelliteEgress`,
      the host-side mirror of the upstream `BridgeForwarder` ring) so one stalled satellite never
      blocks the host bus or its peers.
- [x] Snapshot-on-subscribe: `SatelliteHost.PrimeSnapshot` seeds a newly-subscribed satellite
      from `IGameSnapshot` (zone/party/player + combatants) before the live fan-out starts.
- [x] Gate: in-process router gate (`Fct.App.Tests/SatelliteEgressTests`) — two subscribers get
      identical, identically-ordered streams; a non-subscriber gets nothing; a stalled satellite
      drops alone while a fast peer stays lossless. Real downstream e2e over the pipe
      (`Fct.Integration.Tests/DownstreamFanoutTests`): a `SatelliteHost`-launched sink satellite
      subscribes and records the frames the host fans down.

**Exit:** the host is a working N-way router with per-consumer isolation and backpressure. ✅

### P5 — Consumer projection (the facade serves reads from host-routed data)

- [ ] Parser-satellite additions: fixed-rate repository snapshots (combatant list with
      HP/position/party — the poll surface OverlayPlugin/Hojoring consume), resource
      dictionaries forwarded once, game PID forwarded (`GetCurrentFFXIVProcess` materialized
      locally from the PID in consumer satellites).
- [x] Consumer facade: the `Fct.Compat.Act` facade replica folds the fanned swing/lifecycle
      stream into its own `EncounterLifecycle`/`Fct.Aggregation` graph (installing `EngineTables`
      itself, no parser) → synchronous `ActiveZone.ActiveEncounter` + `ExportVariables` reads.
      The satellite `--consume` mode is the plugin-free consumer; gated below.
- [ ] Consumer facade: `Before/OnLogLineRead` re-raised from fanned `RawLogLine`s (mutable
      `logLine`, exact `LogLineEventArgs` shape). *Next: needs `rawlog` subscription + re-raise.*
- [ ] Synthetic parser stand-in in `ActPlugins`: exact title/status strings,
      `DataSubscription`/`DataRepository` properties (SDK events re-raised from the fanned
      stream; repository served from the snapshot mirror), `_iocContainer`-shaped field whose
      `ILogOutput.WriteLine` routes to the host (write-back lands in P6). *Next — with a
      dependency wrinkle now known:* **`FFXIV_ACT_Plugin.Common` is Costura-embedded inside the
      real parser DLL, not a standalone assembly**, so a plugin-free consumer cannot load the SDK
      types the stand-in exposes (`FacadeHost.InstallAssemblyResolver`'s `Common` branch is a
      unifier, not a provider — it only returns a copy already loaded in the AppDomain). The
      stand-in must therefore (a) be reached only through an SDK-type-free seam
      (`IConsumerStandIn`) so the replica path never JITs a method touching SDK types — the
      implementation lives in `Fct.Parser.Legacy` (the SDK-referencing assembly), an assembly
      boundary rather than a JIT-granularity class boundary; (b) make the SDK resolvable by
      loading the installed parser DLL **and running its module initializer** —
      `Assembly.LoadFrom` + `RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle)`
      — because `LoadFrom` alone executes no code and Costura's `AssemblyResolve` hook is
      registered by the module initializer; no plugin type is constructed. Resolver order stays
      correct: the facade handler registers first, returns null for `Common` until it is loaded
      (falling through to Costura), then unifies every later request onto that single copy;
      and (c) be **plugin-gated** (the stand-in gate skips without `FFXIV_ACT_Plugin.dll`; the
      replica gate above stays plugin-free) — a legitimate runtime precondition, since every
      real deployment has the parser DLL installed on the machine even though a consumer
      satellite hosts it in another process. **Accepted exposure:** the attached Costura
      resolver also answers failed probes for every assembly the parser embeds (e.g. Machina),
      process-wide in the consumer satellite; none of the target consumer plugins reference
      those names. Fallback if that exposure must go (revisit at P8 for the OverlayPlugin
      satellite): extract the embedded `FFXIV_ACT_Plugin.Common` resource from the installed
      parser at catalog-install time and stage it into the consumer satellite's probe path —
      no dormant parser image, same identity — at the cost of depending on Costura's private
      resource format (the compile-time `lib\FFXIV_ACT_Plugin.Common.dll` is such an
      extraction). The raiseable `ConsumerDataSubscription`/`ConsumerDataRepository`/
      `SyntheticFfxivPlugin` shapes were prototyped and validated against the real net48 SDK
      surface (see git history around this commit) before being reverted pending that seam.
- [x] Gate (e2e, no plugin): `Fct.Integration.Tests/ConsumerProjectionTests` — the host fans a
      committed frame-replay down to a `--consume` satellite; its facade replica's YOU total
      (summed across the idle-split encounters) equals the real-ACT oracle baseline. Three-way
      parity **oracle → host engine → consumer replica**, over the real pipe. *The
      `Fct.StreamProbe`-as-plugin discovery variant lands with the stand-in above.*

**Exit:** an unmodified consumer plugin's entire read surface works in an isolated satellite.
*(Partial: the encounter-replica read surface is done + parity-gated; the SDK/log-line stand-in
that lets an unmodified plugin discover + bind lands next, ahead of P7/P8.)*

### P6 — Host-routed services (the cross-plugin pipes)

- [ ] Audio producer path: facade `TTS()`/`PlaySound()` → host `IAudioOutput`.
- [ ] Audio sink-provider path: assigning the facade's `PlayTtsMethod`/`PlaySoundMethod`
      delegate slots registers a **terminal sink** with the host (G3 semantics across the
      bridge) — the ACT-Discord-Triggers hijack becomes a routed capability; with no
      registered sink the host's default output plays.
- [ ] Custom-log-line write-back: satellite `ILogOutput.WriteLine` (lines 256+) → host
      `IRawLogLineEmitter` → fanned to every `RawLogLine` subscriber including the origin, in
      bus order.
- [ ] Named callbacks (Triggernometry peer interop): facade register/invoke → host
      `IPluginRegistry` over the control channel, cross-satellite.
- [ ] Gate (e2e): TTS produced in satellite A plays through the sink registered from satellite
      B; with no sink, the host default path is invoked; a custom line emitted in A is
      observed in B's (and A's) log-line stream in order; a named callback registered in B is
      invocable from A.

**Exit:** every known cross-plugin signal path exists as a host pipe with tests.

### P7 — Cutover: parser, Triggernometry, Discord-Triggers isolated

- [ ] Real FFXIV_ACT_Plugin runs alone in the parser satellite (already the sole producer).
- [ ] Triggernometry and ACT-Discord-Triggers each run in their own satellite on the P5/P6
      projection.
- [ ] The multi-package satellite path is **removed** (`FacadeHost` hosts exactly one
      package; dev-standalone keeps its convenience auto-load, dev-only).
- [ ] Gate (e2e) **[plugin-gated]**: a `--replay` session in the parser satellite drives the
      host; a fixture Triggernometry trigger fires in its satellite (observed on the audio
      pipe); the Discord satellite receives it through its registered terminal sink. All
      pre-existing suites green under the new topology.

**Exit:** three real plugins run isolated in production topology; M0/M2/M3 hold multi-process.

### P8 — OverlayPlugin satellite

- [ ] OverlayPlugin + cactbot isolated: `FFXIVRepository` binds the stand-in
      (title/property/`_iocContainer` seams); MiniParse reads the local replica;
      `NetworkProcessors` consume fanned `RawPacketReceived`; custom lines 257+ round-trip via
      the P6 write-back; CEF lifecycle contained in the satellite.
- [ ] Gate (e2e): against a replay session, a WebSocket client on
      `ws://127.0.0.1:10501/MiniParse` receives `CombatData` JSON whose `encdps`/`damage`/
      per-combatant values equal the oracle `ExportVariables` baseline; custom lines 257+
      appear on the log-line stream; the cactbot event-source log-line handler receives lines.

**Exit:** M1 holds with OverlayPlugin fully isolated — the deepest coupling routed.

### P9 — Hojoring satellite, full matrix, budgets, finalization

- [ ] Close the Hojoring side-channels: its embedded Sharlayan memory reads are game-process
      access and stay inside its satellite (not plugin-to-plugin); its OverlayPlugin
      consumption is investigated (open question §5) and, if in-process, becomes a host pipe
      (the actor side-channel pipe in [`PLUGIN-API.md`](PLUGIN-API.md)'s inventory).
- [ ] Full five-satellite soak on a long recorded corpus: three-way parity holds end-to-end;
      steady-state drop counters zero; added parser→consumer log-line latency within budget
      (target ≤ 10 ms p99 over the in-process baseline, pinned empirically in P4);
      per-satellite memory ceiling recorded.
- [ ] `build/` dist layout stages the satellite once and spawns per-package instances;
      `GO-LIVE.md`/`TESTING.md`/`CLAUDE.md` truth-up to the shipped topology.
- [ ] Gate: soak e2e green with budget asserts; full `test.ps1` green; the dist build runs the
      five-satellite topology end-to-end on a replay session.

**Exit:** the five target plugins run isolated; every inter-plugin path is host-routed,
parity-gated, and budget-tested.

## 5. Open questions (resolve before their phase starts)

- **Hojoring → OverlayPlugin coupling mechanism (before P9).** If Hojoring consumes
  OverlayPlugin over its WebSocket, isolation is transparent; if it binds OverlayPlugin
  in-process, that data must arrive as a host pipe (actor side-channel: statuses/enmity/
  target-of-target). Needs a source sweep of `E:\dev\ACT.Hojoring`.
- **Repository snapshot rate (P5).** The mirror's freshness must satisfy OverlayPlugin's and
  Hojoring's poll cadence without flooding the pipe; pin the rate from measured poll intervals
  and assert it in the P9 soak.
- **`ProcessChanged`/process-handle semantics in consumer satellites (P5).** Consumers get the
  PID-materialized `Process`; anything reading game memory through it (none of the tested
  consumer plugins do — Hojoring's Sharlayan opens the process itself) is out of contract.
- **Satellite WinForms UI at N processes (P3).** The shell already embeds the satellite HWND;
  it becomes one embedded view per satellite. Tab/window ergonomics are an `Fct.App` UX
  decision and do not gate the data plane.
