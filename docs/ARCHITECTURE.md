# FFXIV Combat Tracker — Architecture

**FFXIV Combat Tracker modernizes the stack under the FFXIV ACT plugin ecosystem.** It runs
today's ACT plugins unmodified on current .NET, and opens an incremental, opt-in path to migrate
them onto a typed modern API. **It is not a new ACT and not a replacement** — the goal is to
carry the community's existing plugins forward. To do that it reproduces ACT's plugin-host
surface as a from-scratch compatibility facade (**FFXIV-only**), with the network/opcode parser
living as a swappable, independently-released component.

**.NET 4.x is a transitional compatibility layer, not the destination.** The net48 satellite leg
exists only to run today's plugins unmodified on day 1; the terminal goal is the whole ecosystem —
the parser included — native on .NET 10, with .NET 4.x, the satellites, and the bridge **deleted**.
The host is an **enabler** of that migration: from day 1 it ships the forward path (§7, §10) so each
plugin's *owner* can port their own source onto the typed modern API at their own pace. Every net48
component below is transitional scaffolding slated for removal — retired per plugin as its owner
migrates, and deleted only once the last plugin has crossed — not permanent architecture.

---

## 1. Why this exists

Today's stack is three independently-evolved layers that each re-solve the same
problems, glued together by a stringly-typed pipe-delimited log line:

| Concern | ACT | FFXIV_ACT_Plugin | OverlayPlugin |
|---|---|---|---|
| Memory scanning | — | `Memory.dll` (sig scan) | `MemoryProcessors/` (its own sig scan) |
| Opcode table | — | `Resource.dll` | `opcodes.jsonc` **+** Machina.FFXIV |
| DI container | — | MinIoC | TinyIoC |
| Event model | regex over log strings | `IDataSubscription` | `EventDispatcher` + WebSocket |
| Combat aggregation | `FormActMain` (multi-game god-form) | `Parse.dll` | partial (MiniParse) |

The pipe-delimited log line exists only because ACT had to be game-agnostic. Drop
multi-game support and that whole string round-trip
(packet → struct → string → regex → object → json) becomes optional.

The read-only reference sources for all of this are listed in §13.

---

## 2. Non-negotiable directives

1. **It must run the legacy ecosystem unmodified.** The first version succeeds only
   if these five real plugins work by drop-in, with no recompilation:
   - **FFXIV_ACT_Plugin** (the parser)
   - **OverlayPlugin** (ngld) + cactbot
   - **Triggernometry** (triggers/timelines/TTS)
   - **ACT-Discord-Triggers** (the official audio stack)
   - **ACT.Hojoring** (SpecialSpellTimer / TTSYukkuri / UltraScouter / XIVLog)
2. **Built for the future, with a clear legacy → native migration path** that unlocks
   capabilities the legacy log-line format cannot express. Migration is opt-in and
   incremental — never a flag day. **Shipping is gated on this path existing from day 1**: the end
   state is every plugin — the parser included — native on net10 and the net48 runtime removed. The
   migration is a first-class goal, not a bonus, and the host exists to enable it — but not *only*
   that: serving end users is equally non-optional. `Fct.App` ships a polished Avalonia shell that is
   a clear step up from legacy ACT's cluttered WinForms UI — a product users choose on its own merits,
   not a thin wrapper excused by the developer story (see [`NORTH-STAR.md`](NORTH-STAR.md)).
3. **The host owns the calculations and the routing; processes enforce it.** All ACT
   calculations (encounter aggregation, DPS, `ExportVariables`) live in the .NET 10 host's
   engine — the single source of truth. Each legacy plugin package runs in its **own**
   satellite process, so no two plugins share a heap: every inter-plugin data path is
   physically forced through the host, which routes by stream/capability name and never by
   plugin identity. Invariants + phased path: [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md).

---

## 3. The pivotal decisions

### 3a. Opcodes never cross the plugin boundary
The host↔world contract speaks **typed domain events** ("ActionEffect: source X hit
target Y for Z"), never opcodes. The fact that an event came from *opcode 0x0234,
struct layout v7.52* is sealed **inside the parser plugin**. Consequence: a game patch
ships a **new parser plugin only** — the host and every other plugin are untouched.

A single, explicit, **opt-in escape hatch** (`IRawPacketSource`) still exposes raw
`(opcode, bytes, direction, timestamp)` — used solely by the ACT shim's
`RegisterNetworkParser` emulation, so legacy raw-packet consumers (OverlayPlugin's
`NetworkProcessors/`) keep working. The typed path stays pure for everyone else.

### 3b. Runtime split — the legacy plugins must run on real .NET Framework 4.8
One OS process hosts exactly one CLR; net48 and net10 cannot coexist in a process.
The four target plugins **cannot** be loaded unmodified into a .NET 10 process:
- **CefSharp** (OverlayPlugin's renderer) ships *different binaries* for net48 vs
  .NET-Core; the net48 build will not load on .NET 10. *Hard blocker.*
- `BinaryFormatter`, AppDomains, Remoting, CAS, the framework config system are
  removed/changed in .NET 10; ACT-era plugins depend on them.

Therefore: **legacy plugins run in real .NET Framework 4.8 processes; only data
crosses to the .NET 10 host, over IPC.** `AssemblyLoadContext` isolates net10
plugins from each other but is **not** cross-runtime — don't conflate the two.

This split is a property of *unmodified* legacy binaries, not a permanent architecture: it lasts
exactly as long as a plugin has no net10 build. As owners migrate their source (§10), each plugin
moves in-process and the net48 leg shrinks toward deletion — the CefSharp/etc. blockers gate *when*
a given plugin can cross, not *whether* the destination is native.

### 3c. One satellite process per plugin package — the boundary that enforces routing
Under real ACT the plugins share one managed heap and reach each other through
`ActGlobals.oFormActMain` — the parser is read by reflection, the DPS rollup is read
in-process, audio delegates are hijacked globally. Reproducing that shared heap would make
"everything routes through the host" unenforceable. So **each legacy plugin package gets its
own satellite process** with its own private ACT facade: what its plugin produces, the facade
taps and ships to the host; what its plugin consumes, the host fans out and the facade
projects — including a local replica of the host's aggregation engine (same `Fct.Aggregation`
binary, same routed swing stream, parity-gated) to serve the plugins' synchronous
`ActiveEncounter`/`ExportVariables` reads, and a synthetic parser stand-in so
discovery-by-reflection binds exactly as under real ACT. Granularity is the **package**:
cactbot lives inside OverlayPlugin; the Hojoring suite shares one process. Bonus: a CEF crash
kills one plugin, not the ecosystem. Topology + phases: [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md).

### 3d. Host only the ACT engine; let the rest self-host
Directive 1 requires honoring **three** legacy contracts, but we only *build* one:

| # | Contract | Provided in v1 by | Our build cost |
|---|---|---|---|
| 1 | **ACT host surface** (`ActGlobals.oFormActMain`, `IActPluginV1`, encounter pipeline, events, TTS, `CustomTrigger`, models) | **us** — from-scratch, behavior reproduced from the decompile | The v1 build |
| 2 | **FFXIV SDK** (`IDataSubscription`/`IDataRepository`, `RegisterNetworkParser`, custom log lines) | the **real FFXIV_ACT_Plugin**, hosted unmodified | ~0 |
| 3 | **OverlayPlugin surface** (WebSocket protocol, `IOverlayAddonV2`, cactbot) | the **real OverlayPlugin**, hosted unmodified | ~0 |

Hosting the real FFXIV_ACT_Plugin means we **inherit Ravahn's per-patch cadence for
free** — we sign up for **zero opcode maintenance, permanently**. Reimplementing the FFXIV
parser natively is an explicit **non-goal**: FFXIV_ACT_Plugin stays *the* parser and we never
rewrite, mirror, or port its logic. That non-goal constrains *us*, not the *runtime* — its owner
is free to migrate the plugin's own source to net10 (§10), at which point it stops needing a
satellite but is still the same sole parser (still owner-maintained, still zero opcode work for us).
We build only the consume/aggregate side (the ACT engine).

---

## 4. Target architecture

```
                     ┌───────────────────────────────────────────────┐
                     │        Fct.App + Fct.Host  (.NET 10)          │
                     │  • Fct.Engine — THE aggregation authority     │
                     │  • typed bus (Channels) — THE router          │
                     │  • ALC-isolated NEW plugins, typed API        │
                     │  • Fct.App: Avalonia shell                    │
                     └──┬─────────┬─────────┬─────────┬─────────┬────┘
        one duplex pipe │         │         │         │         │  (control + events,
        per satellite ▼ ▼         ▼         ▼         ▼         ▼   both directions)
                 ┌────────┐ ┌─────────┐ ┌─────────┐ ┌────────┐ ┌────────┐
                 │ parser │ │ Overlay │ │ Trigger-│ │Discord-│ │Hojoring│
                 │ FFXIV_ │ │ Plugin  │ │ nometry │ │Triggers│ │ suite  │
                 │ ACT_   │ │ + CEF   │ │         │ │        │ │        │
                 │ Plugin │ │ + Fleck │ │         │ │        │ │        │
                 └────────┘ └─────────┘ └─────────┘ └────────┘ └────────┘
                  Fct.LegacyHost (.NET Fx 4.8) × N — one plugin package each,
                  each with its private ACT facade + parity-gated engine replica

     the OS process boundary IS both the runtime boundary and the isolation boundary;
     only DATA crosses, and every inter-plugin path crosses THROUGH the host
```

This is the **interim** target — the compat tier that carries plugins that have **not yet
migrated**. The satellite row is scaffolding: each satellite is deleted as its plugin goes native
(§10), and when the last one is empty the whole net48 runtime + bridge is removed. The **permanent**
target is the net10 host with every plugin — the parser included — in-process on the typed API.

`Fct.Abstractions` **multi-targets `net48;net10`** — records + interfaces compile for
both runtimes, so the *same* event/contract types exist on each side of the bridge.
A plugin author writes against it once; the binary runs in whichever host matches its
runtime. The IPC layer serializes those records across.

### Project map (target)

The full per-project map — every assembly, its TFM, and its role — lives in
[`CLAUDE.md`](../CLAUDE.md) ("Project map"), the single canonical source; it is **not** duplicated
here. At a glance the layers are: the multi-targeted **contracts** (`Fct.Abstractions` SDK,
`Fct.Bridge.Contracts`, `Fct.Logging.Contracts`) that compile identically on both sides of the
bridge; the **net10 host** — `Fct.Host` (headless runtime: `IPluginHost` services, typed bus,
ALC-per-plugin loader, IPC bridge client) and `Fct.App` (Avalonia shell + composition root), with
`Fct.Engine` as the aggregation authority; the shared **engine binary** `Fct.Aggregation` (one
strong-named `net48;net10` identity run identically by the authority and every replica); the
**net48 satellite** leg — `Fct.LegacyHost` (one unmodified plugin package per process),
`Fct.Parser.Legacy` (wraps the real FFXIV_ACT_Plugin), and the `Fct.Compat.Act` ACT facade; and
the **net10 in-process legacy path** — `Fct.Compat.Shim` plus its `ActFacade`/`SdkFacade` for
recompiled plugins. `Fct.Abstractions.UI`/`.Testing` and the dev-only `Fct.StreamProbe` round it
out.

The net48↔net10 IPC bridge (named pipe + wire protocol) is **not its own project**: the
host end lives in `Fct.Host` (loaded by the `Fct.App` process), the satellite end in
`Fct.LegacyHost`. Its wire contracts are the shared `Fct.Bridge.Contracts` +
`Fct.Logging.Contracts` libraries, referenced by both ends.

**`Fct.Parser.Legacy` → `Fct.Compat.Act` is deliberate, not backwards.** The parser wrapper is
placed in `ActPluginData.pluginObj` in the facade's plugin list, so `WrappedFfxivPlugin` **is** an
`IActPluginV1` and must carry the facade's ACT host contract. That contract — `IActPluginV1` and the
`IActPluginAlias` seam `PluginGetSelfData` resolves through — lives in the `Advanced Combat Tracker`
identity **by requirement**: the real precompiled plugins implement
`Advanced_Combat_Tracker.IActPluginV1, Advanced Combat Tracker`, and the wrapper composes over a real
plugin's `IActPluginV1`, which resolves to exactly that facade type. The contract therefore **cannot**
be relocated to a thinner shared surface without breaking the impersonation identity, so the reference
is accepted as-is. It is a host-surface reference only — the parser consumes no engine or aggregation
types from the facade (those live in `Fct.Aggregation`).

### 4a. UI framework

- **net10 control panel + shell → Avalonia (XAML + MVVM).** A native .NET desktop app:
  modern, themeable, no packaging/runtime friction. The user-facing face of `Fct.App`.
- **Overlays → unmodified OverlayPlugin.** The host does **not** build a native overlay
  layer (no `Fct.Overlays`, no WebView2, no host-side WebSocket/Kestrel — now or later).
  OverlayPlugin runs unmodified in its own net48 satellite, bringing its own CEF + Fleck +
  event sources, and renders overlays as its own transparent click-through windows.
  cactbot/ecosystem compatibility comes from hosting the real OverlayPlugin, not from
  reproducing its web stack.
- **net48 legacy UI is WinForms** — forced, not a choice: `IActPluginV1.InitPlugin`
  hands each legacy plugin a WinForms `TabPage`. It is quarantined in `Fct.LegacyHost`.

**Native plugin config UI → Avalonia surfaces.** A net10 plugin contributes its settings UI
through the opt-in `IUiContributor`/`IUiHost` contract (`Fct.Abstractions.UI`), rendered in the
shell's Plugins config bay. Overlays stay OverlayPlugin's domain. Full surface + migration path:
[`PLUGIN-API.md`](PLUGIN-API.md).

---

## 5. Dataflow (everything is a real, unmodified plugin; every path crosses the host)

```
ffxiv_dx11.exe
   ▼
PARSER SATELLITE — real FFXIV_ACT_Plugin (hosted as IActPluginV1 on its private facade)
   │  drives the facade: AddCombatAction / SetEncounter / ChangeZone / log lines
   │  facade tap → typed GameEventFrames: CombatSwing + lifecycle, RawLogLine,
   │  RawPacketReceived, combatants/zone/party/player, repository snapshots
   ▼ (pipe, upstream)
HOST (Fct.App + Fct.Host, net10)
   • Fct.Engine folds the swing/lifecycle feed → THE encounter/DPS/ExportVariables truth
   • typed bus fans every subscribed stream out — to native plugins in-process and to
     each consumer satellite (pipe, downstream); audio/callback services route the same way
   ▼ (pipe, downstream — one per consumer satellite)
CONSUMER SATELLITE (one per package) — its private facade projects the host streams:
   • engine replica replays swings → synchronous ActiveZone.ActiveEncounter reads
   • Before/OnLogLineRead re-raised; CustomTrigger eval → TTS/PlaySound → host audio pipe
   • synthetic parser stand-in in ActPlugins (DataSubscription/DataRepository off the
     host streams) ← OverlayPlugin's FFXIVRepository reflects over THIS, as under real ACT
   ▼
real OverlayPlugin ──► its WebSocket server ──► cactbot, addons
Triggernometry     ──► CustomTriggers / encounter events / TTS  ──► host audio pipe
Discord plugin     ──► registers the terminal audio sink with the host ──► HTTP→Discord
```

Nothing here is reimplemented FFXIV logic. We host the real parser and overlay stack; we
supply the ACT engine (in the host) plus the per-satellite facade that routes their world
through it.

---

## 6. The v1 compat surface (the build scope)

Reproduce faithfully (signatures verified against the Advanced Combat Tracker reference decompilation):

- **Plugin host:** `IActPluginV1.InitPlugin(TabPage, Label)` / `DeInitPlugin()`;
  assembly scan + load; `ActPluginData` entries in `ActGlobals.oFormActMain.ActPlugins`.
- **Ingestion:** `FormActMain.ParseRawLogLine(bool isImport, DateTime, string)`;
  `AddCombatAction(MasterSwing)` / the multi-arg overload.
- **Events:** `BeforeLogLineRead`/`OnLogLineRead` (`LogLineEventArgs`, **mutable**
  `logLine`), `OnCombatStart`/`OnCombatEnd` (`CombatToggleEventArgs`),
  `BeforeCombatAction`/`AfterCombatAction` (`CombatActionEventArgs`, `cancelAction`),
  `LogFileChanged`.
- **Models:** `EncounterData`, `CombatantData`, `MasterSwing`, `Dnum`, including the
  static `ExportVariables` / `ColumnDefs` tables (cactbot + Triggernometry read these).
- **Triggers + alerts:** `CustomTrigger` (regex, `SoundType` 1=Beep/2=Sound/3=TTS),
  `SpellTimer`, `PlaySound(string)`, `TTS(string)`, `PlaySoundMethod`/`PlayTtsMethod`.
- **State:** `CurrentZone`, `InCombat`, `LastEstimatedTime`/`LastKnownTime`,
  `CustomTriggers`.
- **Identity:** facade assemblies named/strong-named as `Advanced Combat Tracker` /
  `FFXIV_ACT_Plugin.Common` so the unmodified DLLs bind. The aggregation engine is one strong-named
  `Fct.Aggregation` (net48;net10) shared by both facades. Each facade type-forwards the engine's
  `Advanced_Combat_Tracker.*` types into its own facade identity, and wires accessors for the
  `ldsfld` ACT-core flags (`ActGlobals.charName`, …), which it keeps as real fields
  (see the `Fct.Compat.Act` / `Fct.Aggregation` entries in [`CLAUDE.md`](../CLAUDE.md)).

---

## 7. Forward surface (`Fct.Abstractions`)

The ACT engine is a compat *projection*. The **typed bus is the source of truth and
the forward API**, tapped off the same engine and fed across the bridge from day one.

```
IPlugin               lifecycle (InitializeAsync / DisposeAsync)
IGameSession          Events (IGameEventStream push) + Snapshot() (IGameSnapshot pull)
IEncounterService     encounter / DPS rollup + ExportVariables
IAudioOutput          multi-sink audio (producers + sink providers)
IPluginRegistry       peer enumeration, named callbacks, typed Publish/Subscribe
IRawPacketSource      opt-in: (opcode, ReadOnlySpan<byte>, direction, timestamp)
domain records        Actor, ActionEffect, StatusApplied/Removed, ZoneChanged, PartyChanged, …
```

Design rule: the record set must cover the full FFXIV domain (validate against the 42
`LogMessageType`s + 23 packet handlers + OverlayPlugin's custom lines — MapEffect,
CEDirector, FateWatcher) so routine packet variety never forces a contract change. The full
contract is specified in [`PLUGIN-API.md`](PLUGIN-API.md).

---

## 8. The parser is the sole, hosted parser

The **real FFXIV_ACT_Plugin is the only parser** — today hosted unmodified in the net48 parser
satellite. `Fct.Parser.Legacy` wraps it: `WrappedFfxivPlugin` forwards `DataRepository`/
`_iocContainer`/lifecycle to the real instance, and `RingBufferDataSubscription` funnels its events
through one bounded ring + single dispatch thread and exposes the `IRawPacketSource` hatch. A game
patch ships a new `FFXIV_ACT_Plugin.dll` and nothing else changes — we inherit Ravahn's cadence for
free. Its post-parse data reaches the net10 bus as typed `GameEvent`s over the bridge forwarder.

**Reimplementing the FFXIV parser natively is an explicit non-goal.** Opcode/packet/memory decode
stays *the plugin's* job — we never read, mirror, or port its logic. But "the plugin's job" is a
statement about ownership, not runtime: the parser is a first-class citizen of the migration ladder
(§10) like any other plugin. When its owner recompiles it to net10 it moves in-process, retires the
parser satellite, and remains the sole, owner-maintained parser — we still write zero parsing code.
We build only the **consume/aggregate** side — the ACT aggregation engine (`Fct.Aggregation`, fed
through the `Fct.Compat.Act` facade) — held to the real ACT binary corpus-wide (see
[`tools/mass-compare`](../tools/mass-compare/README.md) and the `Fct.Compat.Act.Tests` fixture suite).

---

## 9. IPC bridge

- **Transport:** one duplex **named pipe per satellite** carries control and the typed-event
  streams, **both directions**. Each end keeps a bounded ring + single writer thread
  (drop-oldest + counters): upstream so high-rate events never stall the SDK/UI threads,
  downstream (per-satellite egress in the host) so one stalled satellite never blocks the bus
  or its peers.
- **Wire format:** one **tab-delimited, backslash-escaped line per event** (an `EVT` frame
  alongside the `LOG` frame) — **no JSON**; the same `GameEventFrame` codec both directions.
  The host re-stamps each decoded event's sequence from its own `IGameEventSink` so the bus
  keeps one coherent ordering, and fan-out preserves that order per satellite. See
  [`PLUGIN-API.md`](PLUGIN-API.md) ("The bridge data forwarder").
- **Subscriptions:** a satellite declares the stream set its facade needs; the host fans out
  only those streams and primes late subscribers from `IGameSnapshot`. Routing is keyed by
  stream/capability name — the host never routes by plugin identity.
- **Fault isolation (a design goal, not a bonus):** a CEF crash kills the OverlayPlugin
  satellite only; the host supervises and restarts it. No plugin can take down the host or a
  peer.
- **Latency budget:** the trigger and overlay paths cross two pipe hops
  (parser satellite → host → consumer satellite). The budget is ≤ 10 ms p99 added over the
  in-process baseline — pinned empirically and enforced by the soak gate
  ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P4/P9). Co-location is **not** the escape hatch;
  the budget is met by the bounded-ring design, not by moving consumers next to the producer.

The hot-path concerns — the drop-oldest lanes, single-writer pipe discipline, and
per-subscription pumps — are handled by the bounded-ring design above and gated empirically by
the four-satellite soak ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P9b).

---

## 10. Migration ladder + sequencing

**Per plugin (opt-in, incremental).** Each plugin travels the North Star's three-stop 1 → 2 → 3 path
— **drop-in (legacy)** in its own net48 satellite, **recompile (net10 legacy)** in-process on the
compat shim, **native (fully ported)** on the typed API — defined in
[`NORTH-STAR.md`](NORTH-STAR.md) ("The three plugin patterns").

The architecturally load-bearing point here: reaching the in-process net10 host (stop 2) does
**not** require a full port — the compat shim (`Fct.Compat.Shim` + its facades) carries the legacy
ACT/WinForms programming model in-process, so an author can leave the satellite without rewriting
their UI, then move to stop 3 when they choose.

**Process sequencing:** the host is the center of gravity from day one — it owns the
aggregation truth and routes every stream; the satellites are per-package compat shells
around unmodified plugins. The build order (fabric → fan-out → projection → per-plugin
cutover, each e2e-gated) is [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md). As a plugin goes
native its satellite disappears; when the last one is empty, the satellite runtime is
deleted.

This ladder applies to **every** plugin, the parser included: FFXIV_ACT_Plugin migrates onto net10
the same way — still the sole, owner-maintained parser, still zero parsing code from us — retiring
the parser satellite. **Killing .NET 4.x entirely is the terminal milestone**: once the last net48
plugin has crossed, `Fct.LegacyHost`, the bridge, the per-satellite engine replicas, and
`Fct.Compat.Act` are removed, and every plugin runs in-process on the typed API. The satellite tier
is a means to that end, never the end.

**Feature unlocks that pull authors forward** (impossible via the pipe-delimited line):
- Full position/heading/velocity at packet rate (log lines round/drop these).
- Complete status params/stacks/source as typed records.
- High-resolution sequence + sub-frame timestamps.
- Plugins publishing their *own* typed events for other plugins.
- Direct state queries with no string parsing; no 6× round-trip per action.

---

## 11. Milestones (each a measurable drop-in test)

The arc is compat-first, then migration: M0–M3 prove the existing ecosystem runs **unmodified**
on the modern host; M4 opens the **opt-in** path for new plugins to consume typed events
**alongside** the legacy ones. Nothing is replaced at any step — the legacy plugins keep working
exactly as before; the forward surface is additive.

- **M0 — Engine core:** host loads the real FFXIV_ACT_Plugin; `ParseRawLogLine` feeds
  the encounter pipeline; encounters/DPS show; `Network_*.log` written.
  *Test: numbers match real ACT on a fight.*
- **M1 — OverlayPlugin + cactbot:** real OverlayPlugin loads; `FFXIVRepository` finds the
  parser; WS feed drives cactbot. *Test: cactbot raidboss/timeline works.*
- **M2 — Triggernometry + TTS:** custom triggers match; timers + TTS/sound fire.
  *Test: a known Triggernometry pack triggers.*
- **M3 — Discord triggers:** trigger/encounter events post to Discord. *Test: a kill posts.*
- **M4 — Forward surface:** the parallel typed bus + a demo native plugin reads typed
  events alongside the legacy four — the migration path proven, opt-in, nothing replaced.

Each milestone must hold in the **isolated topology** — one satellite per package, every
path host-routed. The build order and per-phase e2e gates that get there are
[`ISOLATION-PLAN.md`](ISOLATION-PLAN.md).

---

## 12. Load-bearing compat seams

The seams that make the unmodified drop-in work — each reproduced by the facade **inside every
satellite that needs it**, its *shape* legacy but its *data* always crossing through the host.
Every upstream coupling (MasterSwing boundary, `FFXIVRepository` reflection shape, log-line events,
repository polls, raw packets, TTS, named callbacks) is member-mapped in
[`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md), and the host pipe each routes through is the
dataflow in §5 above. The seams that carry architectural weight beyond that routing:

1. **Assembly identity.** The facades carry the legacy strong-name identities the five plugins
   compile against, supplied via `AppDomain.AssemblyResolve`
   ([`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) §17).
2. **`ActGlobals` surface bound.** We reproduce the *measured* subset the five plugins use; the full
   member-by-member map is [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md).
3. **Native deps in collectible ALCs.** Machina sockets, Deucalion injection, and CEF make a parser
   swap a *stop → unload → reload*, not a live hot-reload.

---

## 13. Reference sources

**Read-only references, never modified** (local sibling checkouts — two separate reference surfaces,
do not conflate):

- **OverlayPlugin** — the current ngld OverlayPlugin source (net48). Key:
  `OverlayPlugin.Core/NetworkProcessors/`, `Integration/FFXIVRepository.cs`,
  `WebSocket/WSServer.cs`, `resources/opcodes.jsonc`.
- **Advanced Combat Tracker** (the compat-surface oracle):
  `Advanced_Combat_Tracker/{Forms/FormActMain.cs, ActGlobals.cs, IActPluginV1.cs, Models/, Events/}`,
  plus a narrative of how the parser feeds ACT.
- **FFXIV_ACT_Plugin** (`...common`, `...network`, `...parse`, `...memory`, `...logfile`,
  `machina.ffxiv`). For understanding the legacy stack we host unmodified **only** — never a source
  to port parsing, swing-production, or DoT/HoT/shield logic from.
