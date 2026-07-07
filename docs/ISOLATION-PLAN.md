# Satellite Isolation Plan — one plugin package per process, everything routed by the host

**The tracking document for the multi-satellite architecture.** It defines the isolation
invariants, the target topology, and the phased implementation path — each phase gated by
automated end-to-end tests so progress is measurable and regressions are impossible to miss.
Phase checkboxes below are the live status.

Companion docs: [`ARCHITECTURE.md`](ARCHITECTURE.md) (the design and rationale),
[`DATA-FLOW.md`](DATA-FLOW.md) (the upstream legacy contract each satellite facade reproduces),
[`PLUGIN-API.md`](PLUGIN-API.md) (the host pipes), [`TESTING.md`](TESTING.md) (the parity/oracle
harnesses the gates build on).

**The satellite topology is transitional, not terminal.** net48 + the satellites are a
compatibility shim that runs today's plugins unmodified from day 1; the destination is every plugin
— the parser included — native on .NET 10, with the net48 leg, the bridge, and the satellites
**deleted** ([`ARCHITECTURE.md`](ARCHITECTURE.md) §10). This plan builds and hardens that shim so
plugin owners have a stable, well-routed base to migrate *off* — the isolation invariants below hold
for as long as any net48 plugin remains, then the whole tier retires. The host is the enabler of
that migration, not a permanent net48 host.

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
4. **The host knows no plugin.** Runtime routing is keyed by stream and capability names, never
   plugin identity — the bus fans out strictly by subscribed stream token and never inspects which
   plugin is on the other end. Plugin-name knowledge is confined to (a) each satellite's *legacy
   discovery seam* (reproducing the title/status-string reflection the plugins themselves perform —
   see [`DATA-FLOW.md`](DATA-FLOW.md) §4.1), (b) the `Fct.App` install-catalog UI, and (c) the
   host-side catalog classifier (`PluginClassifier`/`PackageResolver`), which at install/launch time
   derives a package's default *capability/subscription profile* — role + the stream set its
   satellite subscribes to — for an unmodified legacy plugin that cannot self-declare one. That is
   classification, not routing: it picks which streams a satellite asks for, then the runtime bus
   routes those streams by token alone. None of the three reaches per-frame routing or engine logic.
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

*This is the **interim** topology — the compat tier that hosts plugins that have not yet migrated. It
is scaffolding with a demolition date ([`ARCHITECTURE.md`](ARCHITECTURE.md) §10): as each plugin's
owner ships a net10 build the plugin moves in-process on the typed API and its satellite is deleted;
when the last net48 plugin is gone, the satellite runtime, the bridge, and the net48 projects are
removed. The permanent topology is the net10 host box alone, every plugin in-process.*

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

Every phase landed with its automated gate green in CI (`test.ps1`); each began only once the
prior gate held. Gates marked **[plugin-gated]** additionally need `FFXIV_ACT_Plugin.dll`
installed and skip cleanly without it. **P0–P9 are complete** (ledger below); **P10 (the Hojoring
satellite) is the only open work.**

### Completed phases (ledger)

One line per completed phase — the durable outcome only. Sub-phase anchors (P9a/b/c) are kept so
inbound doc references still land. Full phase bodies live in git history.

- **P0 — Doctrine & documentation: ✅ done.** Invariants + topology (§1–§3) recorded here and aligned
  across `CLAUDE.md`/`ARCHITECTURE.md`/`DATA-FLOW.md`/`PLUGIN-API.md`/`TESTING.md`.
- **P1 — Host engine authority: ✅ done.** `Fct.Engine` (`ModernEncounterEngine` +
  `EncounterLifecycle`/`EngineTables`) is the proven aggregation authority, held bit-for-bit equal to
  the net48 engine by an oracle-fixture parity test and an over-the-pipe wire-path e2e.
- **P2 — Frame-replay harness: ✅ done.** A `GameEventFrame` recorder + committed anonymized fixtures +
  a plugin-free `Fct.LegacyHost --replay-frames … --bridge` mode exercise any consumer behavior
  headlessly, three-way parity oracle → wire-path → frame-replay.
- **P3 — Multi-satellite process fabric: ✅ done.** Handshake v2 (satellite identity + package,
  `ProtocolVersion=2`) + `SatelliteSupervisor` run N supervised satellites with per-id logs,
  restart-with-backoff, and crash-loop quarantine.
- **P4 — Downstream data plane (host → satellite fan-out): ✅ done.** `GameEventFrame` fans host→satellite
  over the same codec; `SUBSCRIBE`/`StreamCatalog` route strictly by stream token, a bounded
  `SatelliteEgress` (drop-oldest) isolates a slow consumer, and `PrimeSnapshot` seeds late subscribers.
- **P5 — Consumer projection: ✅ done.** The parser-free `Fct.Compat.Act` replica serves synchronous
  `ActiveZone.ActiveEncounter`/`ExportVariables`, re-raises `Before/OnLogLineRead`, mirrors
  repository/resource/PID snapshots, and exposes a synthetic parser stand-in (title `FFXIV_ACT_Plugin` /
  status `FFXIV_ACT_Plugin Started.`) that binds discovery-by-reflection exactly as real ACT — the
  stand-in loads the parser DLL **and runs its module initializer** so Costura serves the embedded
  `FFXIV_ACT_Plugin.Common`, an **accepted process-wide Costura exposure** (also answers the parser's
  other embedded assemblies, e.g. Machina).
- **P6 — Host-routed services: ✅ done.** Audio (`SPEAK`/`PLAYSND` up, terminal `SatelliteAudioSinkProxy`
  down), custom-log-line write-back (`_iocContainer`→`ILogOutput.WriteLine` → `LOGLINE` → bus
  `RawLogLine`), and named callbacks (`…CB` → `IPluginRegistry`) each ride point-to-point control
  commands with the host as sole fan point. **The host produces no audio itself** — with no registered
  sink a produced call fans to nobody (silence); the sink is entirely the plugin's job.
- **P7 — Cutover (parser, Triggernometry, Discord-Triggers isolated): ✅ done.** `SatelliteRouter` over
  `SatelliteSupervisor` maps each plugin to its package (`PackageResolver` role + subscriptions), spawns
  one `--role`-gated satellite per package, forwards `LOADPLUGIN`, and replays loads on restart; the
  multi-package-per-process path is **removed in production** (in-process diagnostic harnesses only).
- **P8 — OverlayPlugin satellite: ✅ done.** The `overlay` `Consumer` package (subscribing `packets` —
  the raw firehose `NetworkProcessors` bind — atop the standard consumer set) runs OverlayPlugin+cactbot
  unmodified against the parser-free replica with its whole CEF/Fleck stack in-satellite; MiniParse WS
  `CombatData` equals the real-ACT oracle `ExportVariables` baseline, and the P5 Costura exposure is
  usefully consumed (OverlayPlugin's `Assembly.Load("Machina.FFXIV")` resolves from the parser's copies).
- **P9 — Isolation completion (soak, budgets, finalization): ✅ done.** Three gated sub-phases closed
  isolation for the shipped four-package set; Hojoring stand-up is deferred to P10 with its contract gaps
  already closed and gated (P9a):
  - **P9a — Hojoring contract closure: ✅ done.** `hojoring` PackageResolver arm keeps the four suite
    entry assemblies in **one** satellite (no `packets`); the stand-in `_iocContainer` is a **real**
    `Microsoft.MinIoC.Container` (compiled into `FFXIV_ACT_Plugin.dll`, built reflectively,
    `ILogFormat`/`ILogOutput` as net48 `RealProxy`s); `EndCombat(true)` routes up (`ENDCOMBAT`,
    role-gated by `FormActMain.RouteEndCombatUp`); multi-sink audio is
    **most-recent-registration-wins among equal-priority terminal sinks** (`Seq` tie-break); and
    `ACT-INTERFACE-MAP.md` was corrected (Hojoring is pull-only, filename-only discovery).
  - **P9b — Four-satellite soak + budgets: ✅ done.** A looped-corpus soak asserts N× replica parity,
    **zero steady-state egress drops**, host→fold latency **p99 ≤ 10 ms**, per-satellite memory ceilings,
    lossless 250 ms repository-snapshot cadence, and cross-satellite audio-sink precedence; full tier
    **[plugin-gated]** runs the four real packages as four distinct pids.
  - **P9c — Finalization (single identity, dist gate, doc truth-up): ✅ done.** `Fct.Aggregation` is a
    **single identity** — `Fct.Engine`'s reference is compile-only + non-transitive
    (`Private="false" PrivateAssets="all"`) so the one `compat\` copy resolves via `CompatRuntime`; the
    opt-in `DistTreeGateTests` (`test.ps1 -Dist`) proves oracle parity from the *staged* dist tree.

### Hojoring source sweep & version matrix — the P9→P10 evidence base

*The P9a source sweep of `E:\dev\ACT.Hojoring` (read-only) drove the P9a contract closure and is the
standing evidence base for the still-open P10 work below; P10 cites it as "§P9". Kept in full.*

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
