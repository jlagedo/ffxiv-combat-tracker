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
  (nothing has shipped; there is no compatibility mode to preserve). Production runs one satellite
  per package (the `SatelliteRouter`, P7); the no-role permissive path survives only for in-process
  diagnostic harnesses, and the satellite's dev-standalone mode (`Fct.LegacyHost.exe` with no
  `--bridge`) remains a dev-only convenience.
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
- [x] `Fct.App`: spawns one satellite per installed legacy package (catalog-driven). *Landed in P7:
      `SatelliteRouter` over the supervisor spawns per-package satellites on demand from the catalog +
      `PackageResolver`. `AppData` gained an `FCT_INSTALL_DIR` override so the host runtime can be
      driven from a staged tree.*
- [x] Gate (e2e, no plugins): `Fct.Integration.Tests/SatelliteSupervisorTests` launches three
      empty satellites with distinct identities; killing one leaves the others' processes live
      and the host healthy; the dead one restarts and re-handshakes with the same id + a new
      pid; each writes its own `s2-<id>.log`.

**Exit:** process-per-package fabric with crash isolation, supervised, observable. ✅ (the Fct.App
per-package spawn landed with P7; the roster is flat — one row per plugin, aggregated across
satellites — with each plugin's config window embedded by its own HWND regardless of host process.)

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

- [x] Parser-satellite additions: fixed-rate repository snapshots (`RepositorySnapshot` — the full
      combatant list with fresh HP/position/party the poll surface OverlayPlugin/Hojoring consume, at
      a 250 ms `BridgeForwarder` cadence), resource dictionaries forwarded once
      (`ResourceDictionaryForwarded`, EN skill/buff/zone/world), and the game PID forwarded
      (`GameProcessChanged` → `GetCurrentFFXIVProcess` materialized locally via `Process.GetProcessById`).
      Folded into the host `IGameSnapshot` (`GameSnapshotAggregator`, live `IResourceCatalog` +
      `GameClient.ProcessId`) and seeded to late subscribers by `SatelliteHost.PrimeSnapshot`. New
      `repository` stream token in `StreamCatalog`.
- [x] Consumer facade: the `Fct.Compat.Act` facade replica folds the fanned swing/lifecycle
      stream into its own `EncounterLifecycle`/`Fct.Aggregation` graph (installing `EngineTables`
      itself, no parser) → synchronous `ActiveZone.ActiveEncounter` + `ExportVariables` reads.
      The satellite `--consume` mode is the plugin-free consumer; gated below.
- [x] Consumer facade: `Before/OnLogLineRead` re-raised from fanned `RawLogLine`s — the same mutable
      `LogLineEventArgs` flows through both hooks (`FoldConsume`), so a Before-handler edit of
      `logLine`/`detectedType` is visible to `OnLogLineRead`.
- [x] Synthetic parser stand-in in `ActPlugins` (`Fct.Parser.Legacy`: `SyntheticFfxivPlugin` +
      `ConsumerDataSubscription`/`ConsumerDataRepository`): exact title `FFXIV_ACT_Plugin` / status
      `FFXIV_ACT_Plugin Started.`, public `DataSubscription`/`DataRepository` (SDK events re-raised from
      the fanned stream; repository served from the snapshot mirror), a non-public `_iocContainer` field
      (name is the contract; the `ILogOutput.WriteLine` write-back lands in P6). Reached only through the
      SDK-type-free `IConsumerStandIn` seam, so the plugin-free replica path never JITs a method touching
      `FFXIV_ACT_Plugin.Common` — an assembly boundary (the implementation lives in the SDK-referencing
      `Fct.Parser.Legacy`), not a JIT-granularity class boundary. `Common` is Costura-embedded in the
      real parser DLL (`FacadeHost.InstallAssemblyResolver`'s `Common` branch is a unifier, not a
      provider), so the consumer makes it resolvable by loading the installed parser DLL **and running
      its module initializer** (`Assembly.LoadFrom` +
      `RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle)` — `LoadFrom` alone runs no
      code and Costura's `AssemblyResolve` hook is registered by the module initializer); no plugin type
      is constructed. Resolver order: the facade handler registers first and returns null for `Common`
      until it is loaded (falling through to Costura), then unifies every later request onto that one
      copy. Plugin-gated (the stand-in needs `FFXIV_ACT_Plugin.dll` installed — a legitimate runtime
      precondition; the replica gate stays plugin-free). **Accepted exposure:** the attached Costura
      resolver also answers failed probes for every assembly the parser embeds (e.g. Machina),
      process-wide in the consumer satellite; none of the target consumer plugins reference those names.
      Fallback if that exposure must go (revisit at P8 for the OverlayPlugin satellite): extract the
      embedded `FFXIV_ACT_Plugin.Common` resource at catalog-install time and stage it into the consumer
      probe path — no dormant parser image, same identity — at the cost of depending on Costura's private
      resource format (the compile-time `lib\FFXIV_ACT_Plugin.Common.dll` is such an extraction).
- [x] Gate (e2e, no plugin): `Fct.Integration.Tests/ConsumerProjectionTests` — the host fans a
      committed frame-replay down to a `--consume` satellite; its facade replica's YOU total
      (summed across the idle-split encounters) equals the real-ACT oracle baseline. Three-way
      parity **oracle → host engine → consumer replica**, over the real pipe. Companion no-plugin gates:
      `ConsumerLogLineTests` (log-line re-raise + shared mutable args), `GameSnapshotAggregatorTests`
      (repository/resource/PID fold), `BridgeEventFrameTests` (the new wire frames round-trip),
      `BridgeForwarderProducerTests` (the producer projection).
- [x] Gate (e2e) **[plugin-gated]**: `ConsumerStandInTests` — a `--consume --stand-in` satellite
      registers the stand-in and, folding host-fanned frames, reflects it exactly as OverlayPlugin does
      (title scan → cast `DataSubscription`/`DataRepository` to the real SDK types → poll
      `GetCombatantList`); `ConsumerProbeDiscoveryTests` runs the real, unmodified `Fct.StreamProbe` as
      an `IActPluginV1` in a `--consume --stand-in --probe` satellite so a separately-compiled assembly
      binds `Common` across the `AssemblyResolve` boundary and receives the fanned SDK events.

**Exit:** an unmodified consumer plugin's entire read surface works in an isolated, parser-free
satellite — the encounter replica, the log-line re-raise, the repository/resource/PID mirror, and the
discover-and-bind stand-in, each parity- or discovery-gated. ✅

### P6 — Host-routed services (the cross-plugin pipes) ✅

The three host services already existed in-process (`IAudioOutput`/`AudioService`,
`IRawLogLineEmitter`/`RawLogLineEmitter`, `IPluginRegistry`/`RegistryService`); P6 is the bridge
wiring that carries the net48 facade's traffic to them. Audio/sink/callback messages ride
`SatelliteProtocol` **control commands** (point-to-point RPC — never the re-sequenced game-event
bus); the custom-log-line write-back rides a control command upstream that the host converts to a bus
`RawLogLine`. `SatelliteHost`/`SatelliteSupervisor` gained `IAudioOutput`/`IPluginRegistry`/
`IRawLogLineEmitter` refs (one shared singleton each, so every satellite fans through the same host).

- [x] Audio producer path: facade `TTS()`/`PlaySound()` → `SPEAK`/`PLAYSND` up → host `IAudioOutput`.
      The facade routes through `IHostServiceRoute` (`FormActMain.ServiceRoute`, installed by the
      satellite) or, with none, falls back to the local delegate slots.
- [x] Audio sink-provider path: the facade's `PlayTtsMethod`/`PlaySoundMethod` stay plain fields
      (precompiled Discord-Triggers reads + restores them by `ldfld`/`stfld`); a no-op sentinel makes a
      takeover detectable by reference, and a 1 s satellite poll sends `REGISTERSINK`/`UNREGISTERSINK`.
      The host registers a terminal `SatelliteAudioSinkProxy` (priority 10) that relays a produced call
      DOWN the owning satellite's command pipe — the ACT-Discord-Triggers hijack becomes a routed
      capability (G3 across the bridge). **The host produces no audio itself**: with no registered sink,
      a produced call fans to nobody (silence). Sink implementation is entirely the plugin's job.
- [x] Custom-log-line write-back: the stand-in's `_iocContainer.GetService(ILogOutput)` →
      `WriteLine` (the exact shape OverlayPlugin reflects) → `LOGLINE` up → host `IRawLogLineEmitter`
      → bus `RawLogLine` fanned through the `rawlog` egress to every subscriber including the origin, in
      bus order.
- [x] Named callbacks (Triggernometry peer interop): facade `RegisterNamedCallback`/`InvokeNamedCallback`/
      `UnregisterNamedCallback` (int-id, ACT convention) → `REGISTERCB`/`INVOKECB`/`UNREGISTERCB` →
      host `IPluginRegistry`. The host is the single fan point: an invoke reaches every owner's per-name
      proxy (across satellites, incl. the origin), each relaying it down its own command pipe. Only
      string args cross.
- [x] Gate (e2e, plugin-free): `AudioCrossSatelliteTests` (TTS+sound from satellite A play through the
      sink registered in B; no-sink drops silently), `LogWriteBackTests` (a written line observed in both
      a peer and the origin, in order — plus a plugin-gated variant driving the real OverlayPlugin
      `_iocContainer`→`ILogOutput` reflection), `NamedCallbackTests` (a callback registered in B invocable
      from A; a self-invoke returns to the origin), and `P6SoakTests` (all three concerns across two
      satellites in one run, N of each, zero drops). Protocol round-trips + facade local-fallback units.

**Exit:** every known cross-plugin signal path exists as a host pipe with tests. ✅

### P7 — Cutover: parser, Triggernometry, Discord-Triggers isolated ✅

The host-side package/role resolver (`Fct.Host/Plugins/PackageResolver`: classified plugin → package
identity + producer/consumer role + subscription set; `InstalledPluginRecord.Package`;
`PackageResolverTests`), the CI fixture consumer plugins (`Fct.Fixtures.TriggerFixture` — OnLogLineRead
marker → `TTS()`; `Fct.Fixtures.SinkFixture` — PlayTts/PlaySound slot hijack recorder), and the
production consumer-package satellite mode (`--role consumer`: parser-free facade replica +
`EngineTables.Install()` + optional SDK stand-in + `ConnectBridge` service route + audio-sink poll +
the unified `-cmd` reader that folds fanned frames and dispatches control commands, with the
role-gated one-package-per-process `LOADPLUGIN` guard). The bridge pipes carry explicit 1 MB kernel
buffers, and snapshot priming rides the `SatelliteEgress` ring so the bridge-pump thread never writes
to the command pipe itself. The production wiring: `SatelliteRouter` (`Fct.Host/SatelliteRouter`)
composes over `SatelliteSupervisor`, mapping each installed plugin to its package, spawning that
package's satellite on demand with the resolved role + subscriptions (`SatelliteSpec.Role/Subscriptions`
→ `--role`/`--subscribe` launch args), forwarding its `LOADPLUGIN`, aggregating the per-satellite
roster, replaying loads onto a restarted satellite (`SatelliteSupervisor.SatelliteStarted`), and
stopping+killing a package's satellite when its last plugin is uninstalled
(`SatelliteSupervisor.StopOneAsync`). The command-pipe race is closed deterministically
(`SatelliteHost.WaitForCommandChannelAsync`); `ISatellitePluginChannel.RequestLoadPluginAsync` is async.
`AddFctHostServices` registers the supervisor + router (`ISatellitePluginChannel` → router); the shell
drives the router (flat roster aggregated across satellites; replay spawns one satellite per installed
package; `StopAllAsync` on close).

- [x] Real FFXIV_ACT_Plugin runs alone in the parser satellite (already the sole producer; the router
      spawns it as the `ffxiv` producer only when the parser is installed).
- [x] Triggernometry and ACT-Discord-Triggers each run in their own satellite on the P5/P6
      projection (the router resolves each to its package and launches a `--role consumer` satellite
      with its subscription set).
- [x] The multi-package path is **removed in production**: the host runs one satellite per package
      (router + supervisor), and the satellite's role-gated `LOADPLUGIN` guard is always active there
      because the router always passes `--role`. Dev-standalone keeps its convenience auto-load, and
      the no-role bridged path stays permissive **only** for in-process diagnostic harnesses
      (`SatelliteRunFixture` co-loads parser + OverlayPlugin) — the same dev/test carve-out.
- [x] Gate (e2e): `Fct.Integration.Tests/ThreeSatelliteTopologyTests` — the production router spawns
      one satellite per package; a host-fanned rawlog line fires a fixture trigger in the
      `triggernometry` satellite, whose `TTS()` routes up to the shared host audio and fans down to the
      `discord` satellite's registered terminal sink. Gate A is plugin-free (two consumer satellites,
      marker injected on the bus); Gate B **[plugin-gated]** stands the real FFXIV_ACT_Plugin producer
      up as the third process (three distinct pids). All pre-existing suites green under the new topology.

**Exit:** three real plugins run isolated in production topology; M0/M2/M3 hold multi-process. ✅

> **Known follow-up (pre-existing, from the P1 note):** `Fct.App`'s `deps.json` still lists
> `Fct.Aggregation` (via `Fct.Engine`'s `ProjectReference`) *and* it is staged under `compat\` — two
> identities (`Fct.App.Tests/StaticGraphTests` red for `Fct.Aggregation`). Making `Fct.Engine`'s
> reference non-shipping (resolve the single `compat\` copy via `CompatRuntime`) is a separable
> build-graph/startup-ordering change, tracked into P9 finalization.

### P8 — OverlayPlugin satellite ✅

The P5–P7 consumer fabric carried OverlayPlugin as-is; the only production change is the
`PackageResolver` `overlay` arm — a `Consumer` package subscribing the full stream set
(`swings,rawlog,packets,zoneparty,combatants,repository`; `packets` is the distinguishing
subscription, the raw firehose OverlayPlugin's `NetworkProcessors` bind). The router spawns the
`overlay` satellite like any other consumer package; OverlayPlugin loads unmodified against the
parser-free facade replica, `FFXIVRepository` discovers + binds the synthetic stand-in across the
`AssemblyResolve` boundary, and its whole CEF/Fleck stack lives and dies inside the satellite
process. The stand-in's `ConsumerDataSubscription` counts `NetworkReceived` raises
(`StandInVerification.Packets`, the 7th `--verify-standin` column) so the packet bind point is
gateable headlessly. The Costura resolver exposure accepted in P5 is now **usefully consumed**:
OverlayPlugin's `Assembly.Load("Machina.FFXIV")` (and peers) resolves from the parser's embedded
copies through the resolver the stand-in attaches — no fallback extraction needed.

- [x] OverlayPlugin + cactbot isolated: `FFXIVRepository` binds the stand-in
      (title/property/`_iocContainer` seams); MiniParse reads the local replica;
      `NetworkProcessors`' bind point (`IDataSubscription.NetworkReceived`) raises per fanned
      `RawPacketReceived`; the custom-line write-back mechanism is the P6 `ILogOutput` pipe; CEF
      lifecycle contained in the satellite. **Live-game scope:** end-to-end packet-decode →
      custom-line-257 emission needs a live FFXIV process — OverlayPlugin defers `NetworkParser`
      binding until `ProcessChanged` yields a live PID (the same limit `PacketDispatchTests`
      documents); the fan-out to the bind point and the write-back round-trip are each gated
      headlessly.
- [x] Gate (e2e): `Fct.Integration.Tests/OverlaySatelliteTests` **[plugin-gated]** — the production
      router spawns the `overlay` consumer satellite with the real, unmodified OverlayPlugin; against
      the committed `combat-slice` frame replay, a WebSocket client on
      `ws://127.0.0.1:10501/MiniParse` receives `CombatData` whose `Encounter.damage`/`encdps`/
      `DURATION` and per-combatant `YOU` values equal the real-ACT oracle `ExportVariables` baseline
      (read from `combat-slice.exportvars.tsv` at test time — single-sourced, exact string equality),
      and a host-fanned chat line surfaces on the WS (the cactbot event-source log-line handler).
      Companion gates: `OverlayPacketFanoutTests` (plugin-free: the host fans `RawPacketReceived`
      down to a `packets`-subscribed sink over the real pipe) and `OverlayStandInPacketTests`
      **[plugin-gated]** (fanned packets raise the stand-in's `NetworkReceived`, per-packet exact).
      Test-env note: the WS gate seeds OverlayPlugin's sandbox (`AppData.Root`-resolved —
      in DEBUG the data root sits next to the install dir, not `%LOCALAPPDATA%`) with a
      `WSServerRunning=true` config (`Overlays:[]` present — `LoadJson` iterates it unguarded and a
      failed load silently resets to WSServer-off defaults) and junctions the installed
      OverlayPlugin's extracted CEF into the sandbox so no first-run download happens; it skips
      cleanly when OverlayPlugin/the parser aren't installed.

**Exit:** M1 holds with OverlayPlugin fully isolated — the deepest coupling routed. ✅

### P9 — Hojoring satellite, full matrix, budgets, finalization

Split into four gated sub-phases: **P9a** closes the contract gaps the Hojoring source sweep
found (plugin-free where possible), **P9b** stands the real suite up in its satellite
(plugin-gated), **P9c** is the five-satellite soak with the budget harness, **P9d** is
finalization (single-identity fix, dist gate, doc truth-up). P9a and P9d-single-identity are
independent and can start in parallel; P9b needs P9a; P9c needs P9b for its plugin-gated tier.

**What the Hojoring source sweep established** (`E:\dev\ACT.Hojoring`, read-only; drives every
task below):

- **Suite shape — confirms the §2 one-satellite decision.** Four `IActPluginV1` entry points
  (`ACT.SpecialSpellTimer`, `ACT.UltraScouter`, `ACT.TTSYukkuri`, `ACT.XIVLog`) over
  process-wide singletons in the shared `FFXIV.Framework` (`XIVPluginHelper`/`SharlayanHelper`/
  `CombatantsManager`/`Config`). Splitting them would duplicate memory readers and break shared
  state; they must land in **one** `hojoring` satellite.
- **The SDK contract is pull, not push.** `SubscribeXIVPluginEvents()` is empty — Hojoring
  consumes **no** `IDataSubscription` events (corrects `ACT-INTERFACE-MAP.md` §14, which lists
  Hojoring among the event consumers). It binds by **filename-only** `ActPlugins` scan
  (`pluginFile.Name.Contains("FFXIV_ACT_Plugin")` — no status-text gate), takes the
  `DataRepository`/`DataSubscription` properties, reflects the private `_iocContainer`, **casts
  it to `Microsoft.MinIoC.Container`**, and hard-gates attach on `Resolve<ILogFormat>()` **and**
  `Resolve<ILogOutput>()` returning non-null (`XIVPluginHelper.cs:321-373`). Its log feed is
  ACT's `OnLogLineRead` plus the reflected `BeforeLogLineRead` backing field
  (unhook-all-and-insert-first, `LogBuffer.cs:102-129`); its state feed is **polling**
  `GetCombatantList`/`GetPlayer`/`GetCurrentTerritoryID`/`GetCurrentFFXIVProcess`/
  `GetSelectedLanguageID`/`GetResourceDictionary`, deriving zone/party/player changes by diff.
- **ACT surface beyond the common set:** `TTS()`/`PlaySound()`; TTSYukkuri clone-saves and
  swaps `PlayTtsMethod`/`PlaySoundMethod` (a **second** audio-slot hijacker beside
  Discord-Triggers); `oFormSpellTimers` (`TimerDefs`/`AddEditTimerDef`/`RemoveTimerDef`/
  `NotifySpell` 5-arg — Hojoring authors timers and renders its **own** WPF overlays, so the
  facade's declared-but-never-raised spell-timer events suffice); `CornerControlAdd`
  (unguarded call — facade has it); `EndCombat(true)` (wipeout detection,
  `PluginMainWorker.cs:664`); `InCombat`; `WriteExceptionLog`;
  `PluginGetSelfData(this).pluginFile.DirectoryName` for its install dir.
- **Game memory stays in-satellite.** Sharlayan opens its **own** process handle from
  `CurrentFFXIVProcess` (= `GetCurrentFFXIVProcess`, materialized from the fanned
  `GameProcessChanged` PID) — game-process access, not plugin-to-plugin; no host pipe needed.
  Headless (no live PID) it stays dormant — the same live-game scope limit P8 documents for
  OverlayPlugin's `NetworkParser`.
- **OverlayPlugin coupling — resolved, accepted degradation (§5).** In-process reflection into
  OverlayPlugin.Core's `PluginLoader.Container` (`IEnmityMemory`/`ICombatantMemory`/
  `ITargetMemory` → enmity list, focus/hover target IDs; `OverlayPluginHelper.cs`). In the
  split topology the scan finds no OverlayPlugin, `Attach()` fails **soft** (full try/catch →
  `IsAttached=false`, getters return `0`/empty) — identical to running Hojoring without
  OverlayPlugin installed under real ACT, a supported configuration. Sharlayan is Hojoring's
  primary enmity/target path and keeps working. No cross-plugin data path survives the split.
- **Environment:** WPF over an STA thread with a WinForms message pump is exactly ACT's own
  substrate — the satellite already matches it (`[STAThread]` `Main` + `Application.Run`);
  `WPFHelper.Start()` bootstraps its own `Application` (SoftwareOnly rendering) when
  `Application.Current` is null. `Assembly.GetEntryAssembly()` paths resolve to the satellite
  exe exactly as they resolve to `ACT.exe` under real ACT (same missing-`resources\wav`
  behavior — verify, don't fix). The updater/splash touch the network at init — the e2e
  sandbox must configure them off.

**Version matrix — pinned for P9.** Release binaries are staged at `E:\tmp\plugins`; the
`E:\dev` reference source trees are checked against them so every sweep fact above maps to the
exact binary under test:

| Package | Staged release | Installed (`%APPDATA%\...\Plugins`) | `E:\dev` source tag | Aligned? |
|---|---|---|---|---|
| FFXIV_ACT_Plugin | 3.0.2.3 | 3.0.2.3 | (decompile, no tag) | ✅ |
| OverlayPlugin | 0.19.101 (+ cactbot 0.37.3) | 0.19.101 | `v0.19.101` | ✅ |
| Triggernometry | 1.2.0.7 | 1.2.0.7 | `v1.2.0.7`+1 commit | ✅ |
| ACT.Hojoring | v11.0.8 | **not installed** | `v11.0.8` | ✅ source==release; **install from staging is the P9b precondition** |
| ACT-Discord-Triggers | **none staged** | 2.0.2.0 (pinned for P9) | `v2.1.2`+7, on `feature/net10-native-migration` | pinned — port owned outside this plan |

The Hojoring and OverlayPlugin sweep/interface facts were read from source trees whose tags
**exactly match** the staged releases — the evidence is valid for the binaries P9 ships against.
Two notes the matrix surfaces: (a) **Discord-Triggers is deliberately out of P9 scope** — its
native port is in progress separately (the `feature/net10-native-migration` branch); P9 pins
the installed 2.0.2.0, which the P6/P7 audio gates already cover, and it participates in the
P9c soak at that version. No P9 task touches it. (b) **Stale doc fact** — `CLAUDE.md` still
says "OverlayPlugin 0.16.5" for the `E:\dev\Advanced Combat Tracker` install; the
tested/installed version is 0.19.101 (P9d truth-up).

#### P9a — Hojoring contract closure (facade/stand-in/router gaps; plugin-free unless noted)

- [ ] **`hojoring` PackageResolver arm** — match the suite's entry assemblies
      (`ACT.SpecialSpellTimer`, `ACT.UltraScouter`, `ACT.TTSYukkuri`, `ACT.XIVLog`, plus the
      `hojoring` signal) → package `hojoring`, `Consumer`, subscriptions
      `swings,rawlog,zoneparty,combatants,repository` (the overlay set minus `packets` — no
      `RegisterNetworkParser` consumer in the suite). **Risk closed:** without the arm the
      generic fallback sanitizes each of the four DLLs into its **own** package → four
      satellites → `FFXIV.Framework`'s shared statics split across processes. Extend
      `PackageResolverTests` (all four ids resolve to one `hojoring` descriptor).
- [ ] **Four plugins, one satellite, verified end-to-end.** The role-gated `LOADPLUGIN` guard
      already admits N consumer plugins per process (it rejects only parser/consumer
      cross-loads, `Program.cs:1300-1312`); verify the router path at N>1: four `LOADPLUGIN`s
      forward to the one `hojoring` satellite, the roster aggregates four rows, and
      `SatelliteStarted` restart-replay reloads **all four in original load order** (Hojoring's
      in-suite init order matters — `FFXIV.Framework` singletons start on first toucher). Gate
      with fixture plugins (two-plugin package), no Hojoring needed.
- [ ] **Real-MinIoC stand-in container** — the P9 landmine, closed first. Today's
      `StandInIocContainer` is a synthetic type; Hojoring's cast to `Microsoft.MinIoC.Container`
      throws and attach never completes → the whole suite stays dead. Replace the seam's backing
      with a **real** `Microsoft.MinIoC.Container` instance, constructed reflectively from the
      parser's Costura-embedded assembly (the same load-parser-DLL + run-module-constructor path
      P5 built for `FFXIV_ACT_Plugin.Common`), registering the existing `ILogOutput` write-back
      plus a minimal `ILogFormat`. Keep OverlayPlugin's `GetService` reflection working against
      the same object (MinIoC's `Resolve` satisfies it, or a thin subclass). **Derisk step
      first:** sweep Hojoring's actual `ILogFormat` member usage and implement only that
      surface; if MinIoC's generic `Resolve<T>()` can't be satisfied cross-`AssemblyResolve`
      in a spike, fallback is registering factories via reflection over
      `Container.Register(Type, Func<object>)` — spike before committing the design.
      Plugin-gated (the container type lives in the parser DLL — same precondition as the P5
      stand-in itself).
- [ ] **`EndCombat` route-up** — a consumer facade's `EndCombat(true)` is local-only today
      (`FormActMain.cs:484` → `_lifecycle.EndCombat`), so a Hojoring wipeout-end would fork its
      replica from the host engine — a parity break, not a feature gap. Route it like P6 audio:
      facade `EndCombat` → `IHostServiceRoute` → `ENDCOMBAT` control command up → host
      lifecycle `EndCombat` → `EndCombatRequested` fans down the `swings` stream, bus-ordered,
      to every replica **including the origin**, which applies it on the fan-back (no local
      apply — replicas stay host-ordered; `EndCombat` is idempotent so the producer-side path
      is unaffected). Local fallback (no service route) keeps today's behavior for
      dev-standalone. Gate (plugin-free e2e): a consumer satellite calls `EndCombat`; host
      engine and a **peer** replica both end the encounter; YOU totals stay three-way equal.
- [ ] **Multi-sink audio semantics pinned** — with TTSYukkuri and Discord-Triggers both
      registered, two `SatelliteAudioSinkProxy`s sit at priority 10/terminal, and
      `AudioService`'s equal-priority order is registration order — first-registered wins,
      the **opposite** of real ACT's last-hijacker-owns-the-slot. Pin **most-recent
      registration wins among equal-priority terminal sinks** (matches ACT), unit-test it, and
      assert the precedence in the P9c soak (Hojoring TTS lands in TTSYukkuri's sink when both
      are registered; falls back to the other on `UNREGISTERSINK`).
- [ ] **`BeforeLogLineRead` reflection compat gate** — Hojoring rebuilds the facade's private
      event backing field via reflection and casts each handler to `LogLineEventDelegate`. The
      facade's field-like event (`FormActMain.cs:243`) has the right shape; pin it with a
      plugin-free test that reproduces `LogBuffer.cs`'s exact unhook-all-and-insert-first
      sequence against the facade and asserts delivery order.
- [ ] **`ACT-INTERFACE-MAP.md` corrections** from the sweep: Hojoring consumes no
      `IDataSubscription` events (§14), discovery is filename-only (no `lblPluginStatus` gate),
      and the Hojoring→OverlayPlugin reflection path + accepted degradation are recorded.

**Exit P9a:** every contract gap between the P5-P8 consumer fabric and Hojoring's measured
surface is closed and individually gated; no task below discovers a new seam.

#### P9b — Hojoring satellite e2e **[plugin-gated: ACT.Hojoring + FFXIV_ACT_Plugin installed]**

- [ ] **Precondition:** install ACT.Hojoring v11.0.8 from the staged release
      (`E:\tmp\plugins\ACT.Hojoring-v11.0.8`) into the ACT plugins dir — the version the
      sweep facts and the `E:\dev\ACT.Hojoring` tag (`v11.0.8`) correspond to.
      (Discord-Triggers stays at the installed 2.0.2.0 — out of P9 scope per the matrix.)
- [ ] The production router spawns the `hojoring` satellite; the four real, unmodified suite
      plugins load in it; `XIVPluginHelper` attach **completes** (the P9a container makes the
      `ILogFormat`/`ILogOutput` resolves succeed) — observable headlessly via the roster status
      labels + Hojoring's own logs; the satellite survives a full `combat-slice` frame replay
      with zero satellite faults.
- [ ] TTSYukkuri's slot takeover is detected by the audio-sink poll and `REGISTERSINK` reaches
      the host; a facade `TTS()` from a peer satellite routes down into TTSYukkuri's sink
      (the P6 pipe, now with the real plugin).
- [ ] Test sandbox: Hojoring config/data roots seeded into an isolated sandbox (the
      `OverlaySatelliteTests` pattern), updater/splash/network-touching features configured
      off, resource-path fallbacks observed (not fixed) — the suite must come up headless
      without network.
- [ ] **Live-game scope recorded:** Sharlayan reads, WPF overlay rendering, and spell-timer
      display need a live FFXIV process/desktop session — they are `GO-LIVE.md` A1 manual
      acceptance, not CI. CI proves load/attach/route; A1 proves pixels.

**Exit P9b:** M1 holds for the whole suite — four unmodified Hojoring plugins run isolated,
discovered, attached, and routed, with the audio hijack a working cross-satellite capability.

#### P9c — Five-satellite soak + budgets

- [ ] **Long deterministic corpus without the game:** a fixture looper that concatenates the
      committed `combat-slice{,2}.frames.tsv` N times, rebasing each iteration's
      offset-from-start (the `FrameSession` codec is offset-based, so rebasing is arithmetic) —
      encounter totals assert as N× the per-slice oracle baselines (idle-split guarantees
      per-iteration encounters). The env-gated `FrameFixtureGenerator` can still record a richer
      corpus locally; CI never needs it.
- [ ] **Latency harness (new — the "pinned in P4" baseline never materialized in code; pin it
      here):** host records `Stopwatch.GetTimestamp()` (QPC — cross-process comparable on one
      machine) at egress enqueue for marker frames; each satellite records it at fold into its
      verification artifact; the test joins on marker id and computes p99. Budget:
      host-egress→satellite-fold p99 ≤ 10 ms with five satellites under corpus load, pinned
      empirically on the CI machine class first, then asserted.
- [ ] **Drop + memory budgets:** per-satellite `SatelliteEgress` `Sent`/`Dropped` surfaced to
      the test (heartbeat or exit artifact); soak asserts **zero steady-state drops** across
      all satellites. Per-satellite `WorkingSet64`/`PrivateMemorySize64` sampled at soak end,
      recorded in the test output, and asserted against a generous first ceiling (tighten
      later; the point is catching leaks/regressions, not micro-budgeting).
- [ ] **Repository-snapshot cadence re-pinned (closes the §5 item):** measure
      OverlayPlugin's/Hojoring's actual poll intervals in the soak and assert the 250 ms
      `RepositorySnapshot` cadence keeps mirror staleness under one poll interval.
- [ ] **Gate, two tiers.** Plugin-free tier: replay-frames producer + four consumer satellites
      (fixture plugins) — parity, ordering, drops, latency budgets all assert without any real
      plugin. Full tier **[plugin-gated]**: real parser + OverlayPlugin + Triggernometry +
      Discord-Triggers + Hojoring, five distinct pids, three-way parity (oracle → host engine →
      every consumer replica) on the looped corpus, budgets green, audio-sink precedence
      (P9a) observed.

**Exit P9c:** the shipped topology is measured, not assumed — parity, loss, latency, and
memory all have asserted numbers.

#### P9d — Finalization: single identity, dist gate, doc truth-up

- [ ] **`Fct.Aggregation` single identity (the P1/P7 carried note, closed):** make
      `Fct.Engine`'s `Fct.Aggregation` `ProjectReference` compile-only
      (`Private="false" ExcludeAssets="runtime"`), so it leaves `Fct.App`'s deps.json and
      next-to-exe output; at runtime the default ALC misses and `CompatRuntime`'s `Resolving`
      hook serves the single `compat\` copy. Ordering is the risk: `CompatRuntime.Enable` must
      run before any `Fct.Engine` type touching `Fct.Aggregation` JITs — verify by launching
      the app (a startup log line proving the compat-dir resolve fired), and add direct
      `Fct.Aggregation` references to test projects that execute engine code without
      `CompatRuntime`. `StaticGraphTests`' `Fct.Aggregation` case goes green. **Fallback if
      single-file resolve order proves brittle:** invert — ship `Fct.Aggregation` next-to-exe
      as the one copy, drop it from the `compat\` staging, and let the shim bind the app-dir
      identity via the default ALC (still one identity; `StaticGraphTests` updated to assert
      the chosen location). Either way the invariant is **one loaded identity**, asserted.
- [ ] **Dist-tree e2e gate:** an env-gated integration test (skips unless `dist\<mode>` exists)
      sets `FCT_INSTALL_DIR` to the dist tree and drives the production router from it — the
      staged `satellite\Fct.LegacyHost.exe` spawns per-package, `compat\` resolves, a corpus
      replay holds parity. `build/` already stages the satellite once (single publish into
      `dist\<mode>\satellite\`) — the gate proves the *staged* tree runs the topology, which
      today is only file-existence-checked. Wire as an opt-in `test.ps1` stage after
      `dotnet run --project build`.
- [ ] **Doc truth-up to the shipped topology:** `GO-LIVE.md` (A1 state + retire-on-ship),
      `TESTING.md` (P9 suites: soak tiers, budgets, dist gate), `CLAUDE.md` ("Hojoring
      isolation lands in P9" → present-tense isolated; project-map rows; the stale
      "OverlayPlugin 0.16.5" fact → 0.19.101 per the P9 version matrix), and this document's
      checkboxes/§5.
- [ ] Gate: full `test.ps1` green including both soak tiers (plugin-gated tier on a
      plugin-equipped machine); the dist gate green after a fresh `dotnet run --project build`.

**Exit:** the five target plugins run isolated; every inter-plugin path is host-routed,
parity-gated, and budget-tested; the tracker's remaining checkboxes are all above this line.

## 5. Open questions (resolve before their phase starts)

- **Hojoring → OverlayPlugin coupling mechanism (before P9).** *Resolved by the P9 source
  sweep:* neither WebSocket nor a documented interface — `OverlayPluginHelper` reflects into
  OverlayPlugin.Core's `PluginLoader.Container` in-process and resolves `IEnmityMemory`/
  `ICombatantMemory`/`ITargetMemory` for the enmity list and focus/hover target IDs. The path
  fails **soft** (full try/catch → `IsAttached=false`, getters return `0`/empty), and
  Hojoring's own Sharlayan reader is its primary source for the same data. **Decision:
  accepted degradation, no actor side-channel pipe in v1** — in the split topology Hojoring
  behaves exactly as it does under real ACT with OverlayPlugin not installed, a supported
  configuration; the underlying data is OverlayPlugin's own live-game memory reads, which the
  host cannot compute and will not proxy. If users need the OP-sourced enmity/mouseover
  features back, the v2 shape is a routed capability (OverlayPlugin satellite publishes, host
  fans) — out of P9 scope, recorded in [`PLUGIN-API.md`](PLUGIN-API.md)'s inventory.
- **Repository snapshot rate (P5).** *Resolved:* `BridgeForwarder` emits `RepositorySnapshot` at a
  250 ms cadence (`RepositorySnapshotIntervalMs`); the drop-oldest ring absorbs any burst. Re-pin
  against measured OverlayPlugin/Hojoring poll intervals and assert it in the P9c soak (tracked
  as a P9c task).
- **`ProcessChanged`/process-handle semantics in consumer satellites (P5).** *Resolved:* the parser
  satellite forwards `GameProcessChanged` (PID); consumers materialize `GetCurrentFFXIVProcess` locally
  via `Process.GetProcessById`. Anything reading game memory through the handle (none of the tested
  consumer plugins do — Hojoring's Sharlayan opens the process itself) is out of contract.
- **Satellite WinForms UI at N processes (P3).** The shell already embeds the satellite HWND;
  it becomes one embedded view per satellite. Tab/window ergonomics are an `Fct.App` UX
  decision and do not gate the data plane.
