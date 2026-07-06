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

> **Follow-up (pre-existing, from the P1 note) — closed in P9c:** `Fct.App`'s `deps.json` no longer
> lists `Fct.Aggregation`; `Fct.Engine`'s reference is compile-only and non-transitive
> (`Private="false" PrivateAssets="all"`), and the single `compat\` copy resolves via `CompatRuntime`.
> `Fct.App.Tests/StaticGraphTests`' `Fct.Aggregation` row is green.

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

### P9 — Isolation completion: soak, budgets, finalization (Hojoring deferred to P10)

Split into three gated sub-phases: **P9a** (complete) closed the contract gaps the Hojoring
source sweep found, **P9b** (complete) is the four-satellite soak with the budget harness (parser +
OverlayPlugin + Triggernometry + Discord-Triggers — the packages isolated through P8), **P9c**
(complete) is finalization (single-identity fix, dist gate, doc truth-up). Standing the real
Hojoring suite up is **deferred to P10**, which builds on the completed P9 fabric — its contract
gaps are already closed and gated (P9a).

**What the Hojoring source sweep established** (`E:\dev\ACT.Hojoring`, read-only; drove P9a and
drives the P10 tasks):

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

**Version matrix — pinned for P9/P10.** Release binaries are staged at `E:\tmp\plugins`; the
`E:\dev` reference source trees are checked against them so every sweep fact above maps to the
exact binary under test:

| Package | Staged release | Installed (`%APPDATA%\...\Plugins`) | `E:\dev` source tag | Aligned? |
|---|---|---|---|---|
| FFXIV_ACT_Plugin | 3.0.2.3 | 3.0.2.3 | (decompile, no tag) | ✅ |
| OverlayPlugin | 0.19.101 (+ cactbot 0.37.3) | 0.19.101 | `v0.19.101` | ✅ |
| Triggernometry | 1.2.0.7 | 1.2.0.7 | `v1.2.0.7`+1 commit | ✅ |
| ACT.Hojoring | v11.0.8 | **not installed** | `v11.0.8` | ✅ source==release; **install from staging is the P10a precondition** |
| ACT-Discord-Triggers | **none staged** | 2.0.2.0 (pinned for P9) | `v2.1.2`+7, on `feature/net10-native-migration` | pinned — port owned outside this plan |

The Hojoring and OverlayPlugin sweep/interface facts were read from source trees whose tags
**exactly match** the staged releases — the evidence is valid for the binaries P9/P10 ship against.
Two notes the matrix surfaces: (a) **Discord-Triggers is deliberately out of P9 scope** — its
native port is in progress separately (the `feature/net10-native-migration` branch); P9 pins
the installed 2.0.2.0, which the P6/P7 audio gates already cover, and it participates in the
P9b soak at that version. No P9 task touches it. (b) **Doc fact corrected in P9c** — `CLAUDE.md`'s
`E:\dev\Advanced Combat Tracker` install line now reads "OverlayPlugin 0.19.101", the
tested/installed version.

#### P9a — Hojoring contract closure (facade/stand-in/router gaps; plugin-free unless noted)

- [x] **`hojoring` PackageResolver arm** — matches the suite's entry assemblies
      (`ACT.SpecialSpellTimer`, `ACT.UltraScouter`, `ACT.TTSYukkuri`, `ACT.XIVLog`, plus the
      `hojoring` signal) → package `hojoring`, `Consumer`, subscriptions
      `swings,rawlog,zoneparty,combatants,repository` (the overlay set minus `packets` — no
      `RegisterNetworkParser` consumer in the suite). **Risk closed:** without the arm the
      generic fallback sanitizes each of the four DLLs into its **own** package → four
      satellites → `FFXIV.Framework`'s shared statics split across processes.
      `PackageResolverTests` asserts all four entry assemblies + the bare signal resolve to one
      `hojoring` descriptor (and `DoesNotContain(packets)`).
- [x] **Four plugins, one satellite, verified end-to-end.** The role-gated `LOADPLUGIN` guard
      already admits N consumer plugins per process (it rejects only parser/consumer
      cross-loads, `Program.cs`); the router path at N>1 is gated in
      `ThreeSatelliteTopologyTests`: two fixture plugins under Hojoring titles resolve to the one
      `hojoring` satellite, both announce on the aggregated roster, and killing the satellite
      restarts it and `SatelliteStarted`→`ReplayPackageAsync` replays **both** loads in original
      order (the ordered `_packagePlugins` list). Plugin-free (fixtures stand in for the suite).
- [x] **Real-MinIoC stand-in container** — the P9 landmine, closed. The synthetic
      `StandInIocContainer` is replaced (in the stand-in path) by a **real**
      `Microsoft.MinIoC.Container`. **Spike-corrected mechanism:** `Microsoft.MinIoC.Container` is
      **compiled directly into `FFXIV_ACT_Plugin.dll`** (not a separate Costura assembly — the
      spike proved `Assembly.Load("Microsoft.MinIoC")` fails while `parserAsm.GetType(
      "Microsoft.MinIoC.Container")` resolves), so `MinIocStandIn.TryBuildRealContainer`
      constructs it reflectively from the loaded parser assembly and registers `ILogOutput`
      (write-back) + `ILogFormat` via `Register<T>(container, Func<T>)` (the only factory
      overload — invoked with a `Func<T>` built by a generic helper). The two log services are
      **`RealProxy`** instances over the runtime interfaces (net48-native — no compile-time
      Logfile reference, no `DispatchProxy` package; ILogFormat's ~35 members are covered by one
      `Invoke`); the ILogOutput proxy answers `GetType()` with the interface type so OverlayPlugin's
      `logOutput.GetType().GetMethod("WriteLine")` reflection resolves and routes to the write-back.
      Falls back to the synthetic container when the parser isn't loaded (OP's `GetService` path
      still works; Hojoring is plugin-gated). Gate: `ConsumerStandInTests` asserts the stand-in's
      `SelfVerify` `RealIocContainer` column (`_iocContainer` is the real `Microsoft.MinIoC.Container`
      resolving `ILogFormat`+`ILogOutput`); `ConsumerProbeDiscoveryTests` confirms the OP
      `GetService`→`WriteLine` round-trip still works. Plugin-gated.
- [x] **`EndCombat` route-up** — a consumer facade's `EndCombat(true)` routed like P6 audio:
      facade `EndCombat` → `IHostServiceRoute.EndCombat` → `ENDCOMBAT` control command up → host
      injects `EndCombatRequested` on the bus → fans down the `swings` stream, bus-ordered, to
      every replica **including the origin**, which applies it via `EndCombatLocal` on the
      fan-back (no local apply — replicas stay host-ordered; `EncounterLifecycle.EndCombat` is
      idempotent). **Role-gated** by a static `FormActMain.RouteEndCombatUp` flag set only on
      consumer satellites: the producer keeps its in-band `CombatEndRaised`→`BridgeForwarder`
      path (so parser-driven `EndCombat` stays swing-ordered), and dev-standalone keeps the local
      path. Gates: `EndCombatRouteUpTests` (plugin-free e2e — a consumer routes `EndCombat` up,
      the host injects it on the bus and fans it down to a peer, over the real pipe) +
      `EndCombatRouteTests` (facade unit, both flag states). Engine-fold parity is covered by the
      existing `Fct.Engine.Tests`/`ConsumerProjectionTests`.
- [x] **Multi-sink audio semantics pinned** — **most-recent registration wins among
      equal-priority terminal sinks** (matches real ACT's last-hijacker-owns-the-slot): a
      monotonic `Seq` tie-break (`OrderByDescending(Priority).ThenByDescending(Seq)`) in both
      `AudioService` (production) and `RecordingAudioOutput` (the testing fake the
      integration/soak tests register the terminal proxy on). Unit-tested
      (`AudioServiceTests.Most_recent_terminal_sink_wins_among_equal_priority`). The P9b soak
      pins the cross-satellite precedence plugin-free (`SinkFixture` satellites); the real-plugin
      variant (Hojoring TTS → TTSYukkuri's sink when both registered; falls back on
      `UNREGISTERSINK`) is asserted in the P10b soak extension.
- [x] **`BeforeLogLineRead` reflection compat gate** — pinned plugin-free
      (`BeforeLogLineReadReflectionTests`): the facade's field-like `BeforeLogLineRead` event
      exposes a backing field of the exact name; the test reproduces `LogBuffer.cs`'s exact
      `GetField`→`GetInvocationList`→unhook-all→insert-first→re-add sequence and asserts the
      inserted handler runs first with the originals' relative order preserved.
- [x] **`ACT-INTERFACE-MAP.md` corrections** from the sweep, applied: Hojoring consumes no
      `IDataSubscription` events (§14 — pull-only), discovery is filename-only (§1 — no
      `lblPluginStatus` gate), the `_iocContainer` is the parser-embedded
      `Microsoft.MinIoC.Container` (§14), the isolated-satellite stand-in seam is recorded (§14),
      the `BeforeLogLineRead` VERIFY row is flipped ✅ (§3), and the Hojoring→OverlayPlugin
      reflection path + accepted degradation are recorded (§14).

**Exit P9a:** every contract gap between the P5-P8 consumer fabric and Hojoring's measured
surface is closed and individually gated. ✅ (One sweep fact was corrected during execution:
`Microsoft.MinIoC.Container` is compiled into `FFXIV_ACT_Plugin.dll`, not a separate
Costura-embedded assembly — the container is built reflectively from the parser assembly and the
log services are `RealProxy` instances; see the container task above.)

#### P9b — Four-satellite soak + budgets

- [x] **Long deterministic corpus without the game:** `Fct.Integration.Tests/FrameCorpus`
      concatenates the committed `combat-slice{,2}.frames.tsv` N times, rebasing each iteration's
      offset-from-start by `k·passSpan` (the `FrameSession` codec is offset-based, so rebasing is
      arithmetic) — each slice's explicit leading `ZCHG`/`SETENC` + terminal `ENDC` bookend its
      encounter, so N passes fold into N per-slice encounters and consume-replica YOU totals
      assert as N× the per-slice oracle baselines. `FrameCorpus.Events` (in-process bus drive) +
      `WriteLoopedFixture` (for a `--replay-frames` producer). The env-gated
      `FrameFixtureGenerator` still records a richer corpus locally; CI never needs it.
- [x] **Latency harness:** markers ride the `rawlog` stream as sentinel `RawLogLine`s
      (`FCT_MARK:<id>`); the driving test stamps `Stopwatch.GetTimestamp()` (QPC — machine-wide,
      cross-process comparable) at `bus.Emit`, each satellite stamps QPC at fold into its
      `--verify-latency` artifact (`Program.RecordMarker`, both `--consume` and `--sink`), and the
      test joins on `<id>` for host→fold p99. Budget: p99 ≤ 10 ms — recorded every run + asserted
      against a generous safety-net ceiling (measured p99 ≈ 5 ms at N=5 on the dev box; tighten
      once the CI machine class is characterized). `FourSatelliteSoakTests`.
- [x] **Drop + memory budgets:** `SatelliteHost.EgressCounters` surfaces the P4 `SatelliteEgress`
      `Sent`/`Dropped`; the soak asserts **zero steady-state drops** and `Sent > 0` across all four
      satellites (hard). Per-satellite `WorkingSet64`/`PrivateMemorySize64` sampled at soak end,
      recorded, and asserted against a generous first ceiling (≈ 40 MB observed; catches
      leaks/regressions, not micro-budgeting). `FourSatelliteSoakTests`.
- [x] **Repository-snapshot cadence re-pinned (the OverlayPlugin half of the §5 item):**
      `RepositoryCadenceTests` drives `RepositorySnapshot` frames through the host at the pinned
      250 ms `RepositorySnapshotIntervalMs` to a `repository`-subscribed sink and asserts the fan
      is **lossless** (zero egress drops, every snapshot in order) and cadence-preserving (median
      inter-arrival ≈ 250 ms). Combined with the pinned producer interval and the measured
      sub-10 ms egress latency, mirror staleness stays under one poll interval. (OverlayPlugin's
      internal read-poll is live-game only — `GO-LIVE` A1; the `--replay` producer can't measure
      it headlessly because the synchronous replay starves the pump's 250 ms timer. The Hojoring
      half re-pins in the P10b soak extension.)
- [x] **Audio-sink precedence asserted plugin-free:** `AudioSinkPrecedenceTests` pins the P9a
      most-recent-wins semantics cross-satellite with two `--audio-sink` satellites — the
      later-registered sink (higher `Seq`) takes the route; shutting it down (`UNREGISTERSINK` /
      proxy dispose) falls the route back to the earlier one. (The real-plugin TTSYukkuri variant
      is P10b.)
- [x] **Gate, two tiers.** Plugin-free tier (`FourSatelliteSoakTests`): four directly-constructed
      satellites (two `--consume` replicas + two `--sink`) on one host bus fanning the looped
      corpus — N× parity, zero drops, latency p99, and memory all assert without any real plugin;
      `AudioSinkPrecedenceTests` + `RepositoryCadenceTests` complete the plugin-free tier. Full
      tier **[plugin-gated]** (`FourRealPackagesTests`): real parser + OverlayPlugin +
      Triggernometry + Discord-Triggers spawned by the production router as **four distinct pids**,
      driven by the looped corpus; OverlayPlugin's MiniParse WS `CombatData` equals the real-ACT
      oracle `ExportVariables` baseline on the terminal encounter (three-way parity where a read
      seam exists), per-satellite working set recorded, and the cactbot log-line path confirmed —
      liveness/routing for Triggernometry/Discord (no encounter-total read surface).

**Exit P9b:** the shipped four-package topology is measured, not assumed — parity, loss,
latency, and memory all have asserted numbers. ✅

#### P9c — Finalization: single identity, dist gate, doc truth-up

- [x] **`Fct.Aggregation` single identity (the P1/P7 carried note, closed):** `Fct.Engine`'s
      `Fct.Aggregation` `ProjectReference` is compile-only and non-transitive
      (`Private="false" PrivateAssets="all"`), so it leaves `Fct.App`'s deps.json and next-to-exe
      output; at runtime the default ALC misses and `CompatRuntime`'s `Resolving` hook serves the
      single `compat\` copy. **Mechanism-corrected:** the note's originally-prescribed
      `ExcludeAssets="runtime"` did **not** cut the transitive runtime dependency (the aggregation
      DLL still surfaced next-to-exe as a `type: reference` swept from `Fct.Engine`'s copy-local
      output); `PrivateAssets="all"` cuts the asset-graph edge to consumers and `Private="false"`
      stops the copy-local into `Fct.Engine`'s own bin. Engine-executing test projects that JIT
      engine code without `CompatRuntime` (`Fct.Engine.Tests`, `Fct.Integration.Tests`) carry a
      direct `Fct.Aggregation` reference. Ordering verified by launching the app: the startup log
      shows `Compat runtime enabled …` then `Resolving Fct.Aggregation from compat runtime` before
      the engine subscribes, healthy start, no `FileNotFoundException`. `StaticGraphTests`'
      `Fct.Aggregation` case is green (all four rows). The single identity holds in the `dist\`
      tree too (no next-to-exe copy; only `compat\`).
- [x] **Dist-tree e2e gate:** `Fct.Integration.Tests/DistTreeGateTests` — opt-in via `FCT_DIST_MODE`
      (skips unless set + `dist\<mode>` staged), points `FCT_INSTALL_DIR` at the dist tree so
      `SatelliteHost` resolves `dist\<mode>\satellite\Fct.LegacyHost.exe`, spawns a plugin-free
      `--consume` replica, fans one pass of the committed `FrameCorpus` down to it, and asserts its
      YOU total equals the real-ACT oracle baseline (oracle → host engine → consumer replica, from
      the *staged* tree — which `build/` today only file-existence-checks). Wired as the opt-in
      `test.ps1 -Dist` stage (publish via `dotnet run --project build -- <mode>` + arm
      `FCT_DIST_MODE`, one invocation).
- [x] **Doc truth-up to the shipped topology:** `GO-LIVE.md` (five→four ship-gate + A1 accept-bar,
      Hojoring→P10a, deferred list), `TESTING.md` (dist-gate bullet; four-package soak tiers +
      budgets already present), `CLAUDE.md` (the stale "OverlayPlugin 0.16.5" fact → 0.19.101 per
      the version matrix; the Hojoring-deferred-to-P10 project-map line was already correct), and
      this document's checkboxes/§5.
- [x] Gate: full `test.ps1` green (plugin-gated tiers skip cleanly without the real plugins); the
      dist gate green after a fresh `dotnet run --project build`.

**Exit P9 — isolation complete for the shipped set:** ✅ four of the five target packages
(parser, OverlayPlugin, Triggernometry, Discord-Triggers) run isolated in the production
topology; every inter-plugin path is host-routed, parity-gated, and budget-tested. Hojoring
support is deferred to P10 with its contract gaps already closed and gated (P9a).

### P10 — Hojoring satellite (deferred)

Picks Hojoring back up on the completed P9 fabric. The source-sweep facts and version matrix
above (§P9) are the evidence base; the facade/stand-in/router gaps are already closed (P9a).
P10a stands the real suite up in its satellite; P10b extends the P9b soak to the full
five-package matrix.

#### P10a — Hojoring satellite e2e **[plugin-gated: ACT.Hojoring + FFXIV_ACT_Plugin installed]**

- [ ] **Precondition:** install ACT.Hojoring v11.0.8 from the staged release
      (`E:\tmp\plugins\ACT.Hojoring-v11.0.8`) into the ACT plugins dir — the version the
      sweep facts and the `E:\dev\ACT.Hojoring` tag (`v11.0.8`) correspond to.
      (Discord-Triggers stays at the installed 2.0.2.0 — out of scope per the matrix.)
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

**Exit P10a:** M1 holds for the whole suite — four unmodified Hojoring plugins run isolated,
discovered, attached, and routed, with the audio hijack a working cross-satellite capability.

#### P10b — Five-satellite soak extension + truth-up

- [ ] Extend the P9b full tier to five satellites: real parser + OverlayPlugin + Triggernometry
      + Discord-Triggers + Hojoring, five distinct pids, three-way parity (oracle → host engine
      → every consumer replica, incl. the Hojoring replica) on the looped corpus; drop, latency,
      and memory budgets re-asserted at N=5.
- [ ] Audio-sink precedence with the real plugins (the P9a semantics, real-plugin variant):
      Hojoring TTS routes to TTSYukkuri's sink when both TTSYukkuri and Discord-Triggers have
      registered (most-recent-wins); falls back on `UNREGISTERSINK`.
- [ ] Repository-snapshot cadence re-pinned for Hojoring (completes the §5 item): measure the
      suite's actual poll intervals in the soak and assert the 250 ms `RepositorySnapshot`
      cadence keeps mirror staleness under one poll interval.
- [ ] Doc truth-up: `CLAUDE.md` "Hojoring isolation is deferred to P10" → present-tense
      isolated; `TESTING.md`/`GO-LIVE.md` extended to the five-package matrix; this document's
      checkboxes.

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
  250 ms cadence (`RepositorySnapshotIntervalMs`); the drop-oldest ring absorbs any burst. The
  OverlayPlugin half is re-pinned and asserted (P9b `RepositoryCadenceTests`: lossless fan, median
  inter-arrival ≈ 250 ms); the Hojoring half re-pins in the P10b soak (tracked there).
- **`ProcessChanged`/process-handle semantics in consumer satellites (P5).** *Resolved:* the parser
  satellite forwards `GameProcessChanged` (PID); consumers materialize `GetCurrentFFXIVProcess` locally
  via `Process.GetProcessById`. Anything reading game memory through the handle (none of the tested
  consumer plugins do — Hojoring's Sharlayan opens the process itself) is out of contract.
- **Satellite WinForms UI at N processes (P3).** The shell already embeds the satellite HWND;
  it becomes one embedded view per satellite. Tab/window ergonomics are an `Fct.App` UX
  decision and do not gate the data plane.
