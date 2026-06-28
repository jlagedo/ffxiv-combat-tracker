# FFXIV Combat Tracker — Architecture

A clean-slate, **FFXIV-only** rebuild of the ACT + FFXIV_ACT_Plugin + OverlayPlugin
stack. The goal: a modern .NET host that runs the **existing plugin ecosystem
unmodified** today, and unlocks a typed, future-facing plugin API tomorrow — with
the network/opcode parser living as a swappable, independently-released component.

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

The reference decompiles for all of this live under `reference/` (see §11).

---

## 2. Non-negotiable directives

1. **It must run the legacy ecosystem unmodified.** The first version succeeds only
   if these four real plugins work by drop-in, with no recompilation:
   - **FFXIV_ACT_Plugin** (the parser)
   - **OverlayPlugin** (ngld) + cactbot
   - **Triggernometry** (triggers/timelines/TTS)
   - **Discord-trigger plugin**
2. **Built for the future, with a clear legacy → native migration path** that unlocks
   capabilities the legacy log-line format cannot express. Migration is opt-in and
   incremental — never a flag day.

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

Therefore: **legacy plugins run in a real .NET Framework 4.8 process; only data
crosses to the .NET 10 host, over IPC.** `AssemblyLoadContext` isolates net10
plugins from each other but is **not** cross-runtime — don't conflate the two.

### 3c. Host only the ACT engine; let the rest self-host
Directive 1 requires honoring **three** legacy contracts, but we only *build* one:

| # | Contract | Provided in v1 by | Our build cost |
|---|---|---|---|
| 1 | **ACT host surface** (`ActGlobals.oFormActMain`, `IActPluginV1`, encounter pipeline, events, TTS, `CustomTrigger`, models) | **us** — clean-room from the decompile | The v1 build |
| 2 | **FFXIV SDK** (`IDataSubscription`/`IDataRepository`, `RegisterNetworkParser`, custom log lines) | the **real FFXIV_ACT_Plugin**, hosted unmodified | ~0 |
| 3 | **OverlayPlugin surface** (WebSocket protocol, `IOverlayAddonV2`, cactbot) | the **real OverlayPlugin**, hosted unmodified | ~0 |

Hosting the real FFXIV_ACT_Plugin means we **inherit Ravahn's per-patch cadence for
free** in v1 — we sign up for zero opcode maintenance until the native parser exists.

---

## 4. Target architecture

```
┌─────────────────────────────────┐        ┌──────────────────────────────────┐
│  Fct.LegacyHost  (.NET Fx 4.8)   │  IPC   │  Fct.Host  (.NET 10)              │
│                                  │◄──────►│                                  │
│  • clean-room ACT engine         │ pipe / │  • typed bus (Fct.Abstractions)   │
│  • real FFXIV_ACT_Plugin         │ shared │  • ALC-isolated NEW plugins       │
│  • real OverlayPlugin + CEF      │ memory │  • native parser (later)          │
│  • Triggernometry, Discord       │        │  • native overlays (WebView2)     │
│  ← all net48, runs natively      │        │  ← all net10, runs natively       │
└─────────────────────────────────┘        └──────────────────────────────────┘
        the OS process boundary IS the runtime boundary; only DATA crosses
```

`Fct.Abstractions` **multi-targets `net48;net10`** — records + interfaces compile for
both runtimes, so the *same* event/contract types exist on each side of the bridge.
A plugin author writes against it once; the binary runs in whichever host matches its
runtime. The IPC layer serializes those records across.

### Project map (target)

```
Fct.Abstractions   net48;net10  the SDK. IPlugin, IGameEventStream (bus),
                                IGameDataProvider (query), IGameDataSource (parser),
                                IRawPacketSource (opt-in raw hatch), domain records.
                                Semver'd, additive-only. NO opcodes/packets/Machina.

Fct.Host           net10        microkernel: ALC-per-plugin loader, manifest +
                                contract-version gate, bus impl (Channels),
                                Generic Host, config, logging, lifecycle.

Fct.LegacyHost     net48        clean-room ACT engine + IActPluginV1 loader; hosts
                                the four real plugins; bridge client.

Fct.Bridge         net48;net10  IPC transport + wire protocol (named pipe +
                                shared-memory ring for the combat hot path).

Fct.Parser.Legacy  net48        wraps the real FFXIV_ACT_Plugin as an IGameDataSource
                                (+ IRawPacketSource). Ships day one.
Fct.Parser.Native  net10        clean-room source-gen capture+opcodes+memory. Later,
                                its own per-patch cadence, hot-swapped in its own ALC.

Fct.App            net10        Avalonia control panel + shell (MVVM). The net10
                                user-facing UI. See §4a.
Fct.Overlays       net10        native overlay layer (WebView2 + WS hub + addon host).
                                Replaces CEF. Later.

Fct.Compat.Act     net48        the ACT facade surface (lives in LegacyHost).
```

### 4a. UI framework

- **net10 control panel + shell → Avalonia (XAML + MVVM).** A native .NET desktop app:
  modern, themeable, no packaging/runtime friction. The user-facing face of `Fct.Host`.
- **Overlays remain web** (HTML/JS served by Kestrel, rendered in WebView2). Avalonia
  hosts the WebView2 surface for native overlay windows (transparent, click-through).
  This is the deliberate two-rendering-stack split: **Avalonia for app chrome, web for
  overlays** (overlays must stay web for cactbot/ecosystem compatibility).
- **net48 legacy UI is WinForms** — forced, not a choice: `IActPluginV1.InitPlugin`
  hands each legacy plugin a WinForms `TabPage`. It is quarantined in `Fct.LegacyHost`.

**Open sub-decision — native plugin config UI:** Avalonia controls shipped as a Razor
Class Library (couples authors to Avalonia) **vs.** web config pages loaded in a WebView
(decoupled, matches overlay skillset). Defer until the first native plugin needs config.

---

## 5. v1 dataflow (everything is a real, unmodified plugin)

```
ffxiv_dx11.exe
   ▼
real FFXIV_ACT_Plugin            ← hosted as IActPluginV1, given our ActGlobals facade
   │  ParseRawLogLine(...)        (pipe-delimited lines, as in real ACT)
   │  IDataSubscription + RegisterNetworkParser (raw packets) + custom log lines
   ▼
clean-room ACT engine (Fct.LegacyHost)
   • ParseRawLogLine → MasterSwing → CombatantData → EncounterData
   • raises Before/OnLogLineRead, OnCombatStart/End, Before/AfterCombatAction
   • CustomTrigger eval → TTS(...) / PlaySound(...)
   • ActGlobals.oFormActMain.ActPlugins holds loaded plugin instances
        ↑ OverlayPlugin's FFXIVRepository reflects over THIS to find the parser
   • ── parallel tap ──► Fct.Bridge ──► typed bus on Fct.Host (forward surface)
   ▼
real OverlayPlugin ──► its WebSocket server ──► cactbot, addons
Triggernometry     ──► CustomTriggers / encounter events / TTS
Discord plugin     ──► trigger+encounter events ──► HTTP→Discord
```

Nothing here is reimplemented FFXIV logic. We host the real parser and overlay stack;
we supply the ACT engine they plug into.

---

## 6. The v1 compat surface (the build scope, from the decompile)

Reproduce faithfully (signatures verified against `reference/act-decompiled`):

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
  `FFXIV_ACT_Plugin.Common` so the unmodified DLLs bind.

---

## 7. Forward surface (`Fct.Abstractions`)

The ACT engine is a compat *projection*. The **typed bus is the source of truth and
the forward API**, tapped off the same engine and fed across the bridge from day one.

```
IPlugin               lifecycle
IGameEventStream      typed pub/sub (IAsyncEnumerable<GameEvent> — no forced Rx dep)
IGameDataProvider     query current state (replaces IDataRepository)
IGameDataSource       a plugin that PRODUCES events (the parser implements this)
IRawPacketSource      opt-in: (opcode, ReadOnlySpan<byte>, direction, timestamp)
domain records        Combatant, ActionEffect, StatusChange, ZoneChange, PartyChange, …
```

Design rule: the record set must cover the full FFXIV domain (validate against the 42
`LogMessageType`s + 23 packet handlers + OverlayPlugin's custom lines — MapEffect,
CEDirector, FateWatcher) so routine packet variety never forces a contract change.

---

## 8. Pluggable parser — two implementations, one contract

| | `Fct.Parser.Legacy` | `Fct.Parser.Native` |
|---|---|---|
| what | wraps the **real FFXIV_ACT_Plugin** | clean-room source-gen capture+opcodes+memory |
| ships | day one — inherits Ravahn's cadence | later, **its own per-patch cadence** |
| isolation | the net48 satellite process | own collectible `AssemblyLoadContext` |
| opcodes | hidden inside | hidden inside; raw via `IRawPacketSource` |

Both satisfy `IGameDataSource`; nothing downstream can tell which is loaded. Native can
take over packet-by-packet during migration. OverlayPlugin's `NetworkProcessors/`
(custom opcode parsing) eventually folds **into** `Fct.Parser.Native` as typed events,
collapsing the triplicate opcode tables into one.

---

## 9. IPC bridge

- **Transport:** duplex **named pipe** for control + events; a **shared-memory ring
  buffer** for the combat hot path (action effects burst at high rate — avoid
  per-message pipe overhead there).
- **Serialization:** MessagePack or a hand-rolled binary layout over the records (not
  Newtonsoft). The wire protocol is explicitly versioned.
- **Fault isolation (free bonus):** a CEF crash or a misbehaving legacy plugin cannot
  take down the net10 host, and vice versa.
- **Latency rule:** keep producer + latency-sensitive consumer **co-located**. In v1,
  FFXIV_ACT_Plugin and the trigger plugins are both in the net48 satellite → no
  cross-process hop on the trigger path. The bridge only feeds net10 *observers*.

---

## 10. Migration ladder + sequencing

**Per plugin (opt-in, incremental):**
1. **Drop-in** — unmodified DLL runs via the ACT/FFXIV/Overlay surfaces. *v1 success test.*
2. **Augment** — author adds a `Fct.Abstractions` integration for typed access, still
   shipping the ACT entrypoint.
3. **Native** — drops the ACT entrypoint; ships as a first-class net10 plugin; hops from
   the net48 satellite into the net10 host.

**Process sequencing:** v1 *is* the net48 satellite (the whole functional stack already
lives there — zero cross-runtime risk). The .NET 10 host grows alongside it via the
bridge. Center of gravity migrates across the bridge as plugins go native; when the
satellite is empty, delete it.

**Feature unlocks that pull authors forward** (impossible via the pipe-delimited line):
- Full position/heading/velocity at packet rate (log lines round/drop these).
- Complete status params/stacks/source as typed records.
- High-resolution sequence + sub-frame timestamps.
- Plugins publishing their *own* typed events for other plugins.
- Direct state queries with no string parsing; no 6× round-trip per action.
- Hot-swap native parser on patch day, no host restart.

---

## 11. Milestones (each a measurable drop-in test)

- **M0 — Engine core:** host loads the real FFXIV_ACT_Plugin; `ParseRawLogLine` feeds
  the encounter pipeline; encounters/DPS show; `Network_*.log` written.
  *Test: numbers match real ACT on a fight.*
- **M1 — OverlayPlugin + cactbot:** real OverlayPlugin loads; `FFXIVRepository` finds the
  parser; WS feed drives cactbot. *Test: cactbot raidboss/timeline works.*
- **M2 — Triggernometry + TTS:** custom triggers match; timers + TTS/sound fire.
  *Test: a known Triggernometry pack triggers.*
- **M3 — Discord triggers:** trigger/encounter events post to Discord. *Test: a kill posts.*
- **M4 — Forward surface:** the parallel typed bus + a demo native plugin reads typed
  events alongside the legacy four.

---

## 12. Open questions / first things to verify

1. **The MasterSwing boundary (gates the engine design).** For FFXIV, who builds the
   `MasterSwing`s — the FFXIV_ACT_Plugin (`Parse.dll`, via `AddCombatAction`) or ACT's
   own regex in `FormActMain`? This decides how much encounter pipeline we reproduce vs.
   inherit. The decompile is the oracle — confirm before designing the engine.
2. **`FFXIVRepository` reflection shape.** OverlayPlugin discovers the parser by
   reflecting over `ActGlobals.oFormActMain.ActPlugins` and into plugin-instance fields.
   The facade must match the shape it reflects against, not just the public API.
3. **Assembly identity.** Strong-name / type-forward the facade assemblies to the exact
   identities the four plugins are compiled against, or binds fail.
4. **`ActGlobals` surface bound.** Reproduce the *measured* subset these four use; publish
   a "supported legacy surface"; accept a long tail.
5. **Native deps in collectible ALCs.** Machina sockets, Deucalion injection,
   WebView2 — expect *stop → unload → reload* for parser swap, not live hot-reload.

---

## 13. Reference sources

Hard-linked (Windows directory junctions) under `reference/` — searchable in-place,
**read-only references, never modified**:

- `reference/overlayplugin/` → `E:\dev\OverlayPlugin` — the current ngld OverlayPlugin
  source (net48). Key: `OverlayPlugin.Core/NetworkProcessors/`,
  `Integration/FFXIVRepository.cs`, `WebSocket/WSServer.cs`,
  `resources/opcodes.jsonc`.
- `reference/act-decompiled/` → `E:\dev\ACT-decompiled` — clean decompiles:
  - `Advanced_Combat_Tracker/` — ACT itself (the compat-surface oracle):
    `Forms/FormActMain.cs`, `ActGlobals.cs`, `IActPluginV1.cs`, `Models/`, `Events/`.
  - `.audit/ffxiv_act_plugin/decompiled/` — FFXIV_ACT_Plugin sub-assemblies
    (`...network`, `...parse`, `...memory`, `...common`, `...logfile`, `machina.ffxiv`).
  - `FFXIV_ACT_Plugin_ARCHITECTURE.md` — narrative of how the parser feeds ACT.
