# net10 Plugin API — Design & Contract

The forward-facing plugin SDK: the typed, future-facing contract new plugins target, and the
compat adapter that lets ACT-era plugins migrate onto it. This is the **forward surface** sketched
in [`ARCHITECTURE.md`](ARCHITECTURE.md) §7, specified in full.

It is the counterpart to the *backward* surface: [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) and
[`DATA-FLOW.md`](DATA-FLOW.md) document the legacy ACT host surface the net48 satellites impersonate
(one satellite per plugin package — [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)) to run the five
plugins **unmodified**. This document is the **net10 contract** they migrate *to*.

> **Status.** The contract (`Fct.Abstractions` + `.UI`), the flow-test harness, the net10 host +
> ALC loader, the net48→net10 bridge forwarder, the plugin install/classification + lifecycle pipeline,
> and most of the compat shim are built and load real plugins end-to-end (**21 flow tests +
> `Fct.Compat.Shim.Tests`, all green**). What remains is a small,
> **named set of missing pipes and faces** — read [The model](#the-model--one-set-of-pipes-three-faces),
> the [Pipe inventory](#pipe-inventory--every-pipe-three-faces), and
> [What we must do](#what-we-must-do--the-work-list) first. Per-piece build state is in
> [The net10 host & ALC loader](#the-net10-host--alc-loader-built),
> [The bridge data forwarder](#the-bridge-data-forwarder-built), and
> [The net10 compat shim](#the-net10-compat-shim-in-progress); the contract gaps that the source sweep
> surfaced (**G1–G11, all shipped**) are in [Contract gaps](#contract-gaps-tracked).

## The model — one set of pipes, three faces

The host is a **router**. It knows nothing about the game and nothing about any plugin. It provides a
fixed set of **pipes** — event streams, pull snapshots, service seams — and moves data through them;
how a pipe is *filled* and how it is *consumed* is entirely a plugin's concern. Four rules gate every
decision:

1. **The host knows nothing about the game.** It never parses a log line or decodes a packet. Raw bytes
   and raw lines pass through verbatim. The sole parser (FFXIV_ACT_Plugin) is the only thing that turns
   packets into lines and swings.
2. **The host knows nothing about plugins.** No plugin identity, semantics, or data-intent reaches it.
   It loads opaque units into isolated ALCs and exposes pipes; what binds to a pipe is invisible to it.
   Shared-assembly identity is **manifest-declared**, so the loader names no plugin- or shim-specific
   assembly (see [Isolation](#isolation--assembly-loading)).
3. **The host provides every pipe the tested plugins need — on both sides of the satellite.** A pipe is
   not "done" until every face a real tested plugin will use exists.
4. **The only host intelligence is ACT-mirror aggregation.** Its one game-touching behavior is
   reproducing the two things old ACT maintains — the combatant/zone/party **repository snapshot**
   (`IGameSnapshot`) and the **encounter/DPS rollup** (`IEncounterService` + `ExportVariables`) — as a
   simplification consumers already expect. Everything else is pure transport.

Three non-negotiables gate every entry in this document:

- **Every real inter-plugin data path is a host pipe — no exception, no simplification, no deferral.**
  If two of the tested plugins exchange data or signals, the host exposes the pipe they exchange it
  through. "Nobody consumes it natively yet", "it's high-bandwidth", "it's an ugly ACT internal" are
  never grounds to omit, sample, or postpone a pipe.
- **The goal is a host that accommodates *all* the tested plugins — inside and outside the satellite,
  no exceptions.** A pipe is complete only when every face a real plugin will use exists (see the
  faces below).
- **This models reality, not a greenfield.** These plugins evolved over years and some of their
  couplings should never have existed — but we cannot change reality. We adapt each coupling into a
  clean, documented pipe rather than wish it away.

**Three faces, one set of pipes.** A plugin binds to the same host pipes through one of three faces:

| Face | Runtime | Binds to | Home |
|---|---|---|---|
| **Satellite** | net48 | real ACT/SDK, unmodified | `Fct.LegacyHost` — **one satellite process per plugin package**, each with its private ACT facade; produced data crosses to the net10 bus via the [bridge forwarder](#the-bridge-data-forwarder-built), consumed data is fanned back and projected by the facade ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)) |
| **Compat-shim (native)** | net10 | legacy ACT/SDK identity re-projected (`Fct.Compat.Shim` + its two facades, distinct from the net48 `Fct.Compat.Act` ACT engine) | in-process, ALC-per-plugin; a recompiled legacy plugin migrating **without breaking its interface** |
| **Modern (native)** | net10 | the typed `Fct.Abstractions` contract | in-process; new plugins and fully-migrated ones |

The satellites are **temporary workarounds** — the destination is native. So the fact that a consumer
runs in a satellite *today* never justifies leaving its native faces unbuilt; the migration path
requires every pipe to be reachable natively too. A shimmed plugin migrates compat→modern
member-by-member, then drops the shim. And because each package has its **own** satellite, the
satellite face routes through the host exactly like the native faces do — there is no shared heap
for two plugins to bypass the pipes with (rule enforced by process boundary; invariants in
[`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)).

## Pipe inventory — every pipe, three faces

Every pipe the five tested plugins consume, its shape on each face, and what is built vs. missing.
"Missing" means a face a tested plugin will need once it migrates — not an unused surface. This table
is the spine of [What we must do](#what-we-must-do--the-work-list).

| Pipe | Modern face | Compat-shim face | Satellite face | Status |
|---|---|---|---|---|
| **Raw log line** | `RawLogLine` event | `Before/OnLogLineRead` + `IDataSubscription.LogLine` | ACT `OnLogLineRead` (bridged) | ✅ built; shim `ParsedLogLine` event ⏳ |
| **Repository snapshot** (combatant/zone/party/player) | `IGameSnapshot` | `IDataRepository.Get*` + state `IDataSubscription` events | SDK `IDataRepository` (bridged) | ✅ built; name tables empty |
| **Actor side-channel state** (statuses/enmity/target-of-target/focus/hover/reliable in-combat) | `Actor` superset fields + snapshot `Focus`/`Hover` | consumed via the snapshot | today Hojoring reads OverlayPlugin + Sharlayan directly | contract fields ✅; **source = satellite forwards (pending)** |
| **Encounter / DPS** (+`ExportVariables`) | `IEncounterService` backed by `Fct.Engine` (the aggregation authority, fed by the forwarded `CombatSwing`/lifecycle stream; live `Active` on a 500 ms projection tick) | `ActiveZone.ActiveEncounter` + `Fct.Engine.EncounterProjector` | ACT `EncounterData`/`CombatantData` off the satellite's engine replica | ✅ built (host engine live) |
| **Action event** | `ActionEffect` (+ typed records) | via events | ACT `AfterCombatAction` (bridged) | ✅ `ActionEffect`; status/cast/death/hp → **RawLogLine only** (note below) |
| **Audio** | `IAudioOutput` | `TTS`/`PlaySound` + `PlayTts`/`PlaySound` slots | ACT `PlayTts`/`PlaySound` | ✅ built |
| **Registry / peer** | `IPluginRegistry` | `RegisterNamedCallback`/`InvokeNamedCallback` | ACT named callbacks | ✅ built |
| **Raw packets** | `IRawPacketSource` + `RawLogLines.Emit` | `RegisterNetworkParser` / `NetworkReceived` | SDK `NetworkReceived` (in-satellite) | ✅ built (satellite forwards `RawPacketReceived` → bus → `IRawPacketSource` + shim `NetworkReceived`/`Sent`; read + G4 write-back) |
| **Process lifecycle** | process event + handle | `ProcessChanged` + `GetCurrentFFXIVProcess` | SDK `ProcessChanged` | **missing on net10 (all faces)** |
| **Resource catalog** (names) | `IResourceCatalog` | `GetResourceDictionary` | parser tables + a resource-provider plugin | **pipe filled by plugins; status/buff table + producer pending** |
| **Host services** (storage/logger/clock) | `IPluginHost` | `AppDataFolder`/`WriteExceptionLog`/… | ACT globals | ✅ built |
| **UI** | `IUiContributor` (Avalonia settings page + reveal/corner-control) | WinForms `TabPage` (slice D8) | WinForms `TabPage` in `Fct.LegacyHost` | modern: ✅ wired (`PluginUiCoordinator`); shim D8 ⏳; satellite HWND ✅ |

> **Typed semantic events stay on the RawLogLine pipe.** `StatusApplied/Removed`, `Cast*`,
> `DeathOccurred`, and HP-as-event exist only as parsed log-line fields. The host **parses nothing**,
> and the sole parser emits them only as raw lines, so **no face synthesizes them** — every tested
> consumer regexes the `RawLogLine` pipe, exactly as under ACT. Any typed re-shape of that data is a
> *plugin's* job (parse `RawLogLine` → publish typed events, per rule 1), never the host's.

## Locked decisions

- **Isolation model: in-process, ALC-per-plugin.** Each plugin loads into its own collectible
  `AssemblyLoadContext` → assembly/version isolation + hot-unload. Fault containment is
  **cooperative** (fault-guarded boundaries, watchdog timeouts, quarantine/unload). Honest limit: a
  hard native crash (AccessViolation/StackOverflow/OOM) still takes the host — ALC is not a fault
  boundary.
- **Compat = net10 recompile-shim adapter** (the compat-shim face above), not a second unmodified-host. The
  existing net48 unmodified-drop-in path is unchanged and remains the zero-effort on-ramp; the shim
  is the on-ramp for authors leaving net48.

## What the legacy plugins consume (the archetypes)

Distilled from a sweep of all five trees. Each archetype is a real consumption pattern → its modern
replacement, with the source-validation verdict. (FFXIV = FFXIV_ACT_Plugin · OP =
OverlayPlugin/cactbot · Trig = Triggernometry · Disc = ACT-Discord-Triggers · Hojo = ACT.Hojoring.)

| Archetype | Who | Today | Modern replacement | Validated |
|---|---|---|---|---|
| Raw log-line consumer | Trig, OP, Hojo | `Before/OnLogLineRead`, opaque `\|`-line, regex over `LogMessageType` 0–274 | `RawLogLine` event | ✅ CONFIRMED |
| Typed event consumer | OP | `IDataSubscription` (LogLine/NetworkReceived/ZoneChanged/PartyListChanged/ProcessChanged) | `IGameEventStream` typed records | ✅ CONFIRMED |
| State poller | Hojo, Trig | `IDataRepository.Get*` polled on bg threads | `IGameSnapshot` (free-threaded, immutable) | ✅ CONFIRMED |
| DPS / encounter reader | OP | `ActiveZone.ActiveEncounter` + scrape both `ExportVariables` per tick | `IEncounterService` typed snapshot + `ExportVariables` bag | ✅ CONFIRMED ([G1](#contract-gaps-tracked)/[G2](#contract-gaps-tracked) shipped) |
| Combat-state driver | Trig | `SetEncounter`/`EndCombat`/`InCombat`; text export; append line | `IEncounterService` (read+write) | ✅ CONFIRMED ([G7](#contract-gaps-tracked)) |
| Audio sink provider | Disc, Hojo | **hijack** global `PlayTtsMethod`/`PlaySoundMethod` (save/restore, route-instead-of) | `IAudioOutput.RegisterSink` | ✅ CONFIRMED ([G3](#contract-gaps-tracked) shipped) |
| Audio producer | Trig, OP, Hojo | call `TTS()`/`PlaySound()` | `IAudioOutput.Speak`/`Play` | ✅ CONFIRMED |
| Raw packets | OP | `RegisterNetworkParser` → bytes → Machina → synthetic lines 256+ | `IRawPacketSource` (capability-gated) | ✅ CONFIRMED — read ✓; write-back [G4](#contract-gaps-tracked) shipped |
| Host services | all | `PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, Trig named callbacks | `IPluginHost` (Storage/Logger/Registry) | ✅ CONFIRMED ([G5](#contract-gaps-tracked)/[G6](#contract-gaps-tracked) shipped) |
| UI | all | one WinForms `TabPage` via `InitPlugin` | `IUiContributor` (Avalonia surfaces) | ✅ CONFIRMED ([G11](#contract-gaps-tracked)) |

Three findings drove the shapes:

1. **The actor model is a superset of the SDK, not a mirror.** The FFXIV SDK `Common.Models` is just
   `Combatant`/`Player`/`NetworkBuff`/`PartyType`
   (`FFXIV_ACT_Plugin.Common.Models/`; only these four types exist). Hojoring/UltraScouter reach *past*
   it into Sharlayan + OverlayPlugin for **status lists, focus/hover/target-of-target, enmity, and a
   reliable in-combat flag** — none of which the SDK exposes. `Actor` carries these so the heaviest
   consumer can drop its side-channels.
2. **The raw line cannot be dropped on day one.** Trig and cactbot are fundamentally
   regex-over-the-pipe-line engines (Trig: `Trigger.cs:150,615` over lines queued from
   `ProxyPlugin.cs:165-166`; OP: `FFXIVOptionalEventSource.cs:57-176`). `RawLogLine` is a first-class
   event *alongside* the typed ones — both produced from one decoded packet, exactly as the legacy
   plugin emits a log line + an SDK event.
3. **Today's fault model has fixable gaps — narrower than a naive read suggests.** OverlayPlugin's
   dispatch lock is **per event type**, not global: a slow `HandleEvent` stalls all receivers of the
   *same* event type (and blocks that type's subscribe/unsubscribe), but not unrelated types
   (`EventDispatcher.cs:128-141`, which *does* try/catch each receiver). The genuinely unguarded spot
   is **subscribe-time cached-state replay** (`EventDispatcher.cs:71-75`, no try/catch, unlike
   dispatch). Its timer poll loops and log-line handler *are* guarded
   (`EventSourceBase.cs:40-61`; `FFXIVOptionalEventSource.cs:61-84`); the **raw-packet Machina
   handlers on the `NetworkReceived` thread are not** (`NetworkParser.cs:50-73`,
   `LineBaseCustom.cs:69-87`). The `RingBufferDataSubscription` we already shipped (bounded ring +
   single dispatch thread + per-subscriber try/catch + drop-oldest) is the correct generalized model
   that closes all of these uniformly.

## Project layout

| Project | TFM | Role |
|---|---|---|
| `Fct.Abstractions` | net48;net10 | core contract: lifecycle, events, snapshot, encounter, audio, registry, raw hatch. **No UI, no opcodes.** |
| `Fct.Abstractions.UI` | net10 | Avalonia UI contribution surfaces. Referenced only by UI-contributing plugins. |
| `Fct.Abstractions.Testing` | net10 | in-memory fakes of every contract interface + the `ShimStub` seam, for the headless flow tests. |
| `Fct.Compat.Shim` | net10-windows | the recompile-shim adapter runtime over the modern contract (`LegacyPluginHost`, the SDK mappers/projectors). Distinct from the existing net48 `Fct.Compat.Act` (the satellite's ACT engine). |
| `Fct.Compat.Shim.ActFacade` | net10-windows | assembly `Advanced Combat Tracker`: the POCO `ActGlobals`/`FormActMain` surface a recompiled plugin binds to, forwarding onto `IPluginHost`. |
| `Fct.Compat.Shim.SdkFacade` | net10 | assembly `FFXIV_ACT_Plugin.Common`: re-declares the SDK surface (`IDataSubscription`, `IDataRepository`, `Combatant`/`Player`/`NetworkBuff`/`PartyType`) a recompiled plugin binds to. |

net48 support in the core uses `IsExternalInit.cs` (init-accessor shim) + `Microsoft.Bcl.AsyncInterfaces`
(records / `IAsyncEnumerable` / `IAsyncDisposable`), so the identical record/interface types exist on
both sides of the bridge.

## The modern contract

### Lifecycle & identity — manifest, not reflection-by-title

A plugin ships a `plugin.json` manifest (`id`, `version`, `contract` version, `entry` type,
`capabilities`). The host reads it **without loading the assembly**, gates the contract version, then
loads the entry type into a dedicated ALC. This replaces the legacy discovery-by-reflection (matching
`lblPluginTitle.Text` and `GetProperty("DataSubscription")` — the exact pattern OverlayPlugin uses at
`FFXIVRepository.cs:89-97,120-139`).

```csharp
public interface IPlugin : IAsyncDisposable
{
    Task InitializeAsync(IPluginHost host, CancellationToken ct);   // once; ct cancels a slow init
}
```

One async entry, `DisposeAsync` for teardown. (Note: OverlayPlugin's own entry is a single
`IActPluginV1.InitPlugin`; its *addons* get a second `IOverlayAddonV2.Init()` because OP is itself a
mini plugin-host — see [G6](#contract-gaps-tracked) on peer discovery.)

### The host surface

`IPluginHost` hands the plugin: `Game` (events + snapshot), `Encounters`, `Audio`, `Plugins`
(registry), `Storage`, `Logger` (MEL `ILogger`), `Clock`, and `Self` (its own `PluginInfo`).

### Game data — typed stream + raw line on one bus

`IGameSession` splits CQRS-style into `Events` (push) and `Snapshot()` (pull) — mirroring how
consumers actually work (OP/Trig stream; Hojo polls `DataRepository.GetCombatantList()` on a
`ThreadWorker`, `XIVPluginHelper.cs:904-912`).

```csharp
public interface IGameEventStream
{
    IAsyncEnumerable<GameEvent> Subscribe(GameEventFilter filter, CancellationToken ct); // await foreach
    IDisposable                 Subscribe(GameEventFilter filter, Action<GameEvent> handler); // low-latency
}
```

`GameEvent` is a closed record hierarchy (`Events.cs`): `RawLogLine` + typed events
(`ActionEffect`, `CastStarted/Cancelled`, `StatusApplied/Removed`, `DeathOccurred`,
`CombatantAdded/Removed`, `HpUpdated`, `ZoneChanged`, `PartyChanged`, `PrimaryPlayerChanged`, …).
Consumers switch on the concrete type and ignore unknowns, so new events are **additive** — a game
patch's packet variety never forces a breaking change. `LogMessageType` (`LogMessageType.cs`) is the
complete 0–274 taxonomy (FFXIV native + OverlayPlugin's 256+ custom range).

> The current scaffold ships a representative event subset. Completing the hierarchy against the full
> taxonomy (42 native types + the custom range) is a tracked next step.

### Game state — free-threaded immutable snapshot

`IGameSnapshot` returns immutable records callable from any thread (no caller-side `lock`, unlike the
legacy `lock (cd.Lock)`). `Actor` (`Actors.cs`) is the **superset** model — adding `Statuses`,
`Enmity`, `TargetOfTargetId`, and `InCombat` on top of the SDK fields, and the snapshot itself
carries `Focus`/`Hover` (`Session.cs:51-52`) — together closing Hojoring's Sharlayan/OverlayPlugin
side-channels. `IResourceCatalog` is the **pipe** for typed, all-locale name tables (skill/status/zone/world/item);
the host owns no tables and **plugins fill it** — the parser supplies skill/zone/world via
`GetResourceDictionary`, and a first-party resource-provider plugin supplies the status/buff table the
SDK leaves empty (the gap Hojoring patches with a private XIVAPI/CSV side-channel today, promoted to a
shared pipe — see [What we must do](#what-we-must-do--the-work-list) and its recorded decision).

### Encounter / combat-state

`IEncounterService` exposes `InCombat`, `StartCombat`/`EndCombat` (Trig's driver at
`ProxyPlugin.cs:323-334`), `Active`/`Last` encounter snapshots, `ExportText`, and `AppendLogLine`.
`EncounterSnapshot`/`CombatantMetrics` provide typed per-combatant metrics — the ergonomic path for
**native** consumers, replacing OverlayPlugin's whole-`ExportVariables`-dictionary scrape per tick
(`MiniParseEventSource.cs:321-360,385-414`).

> **cactbot fidelity requires more than the typed fields.** OverlayPlugin forwards the *entire*
> opaque `CombatantData`/`EncounterData.ExportVariables` dictionary verbatim; the fixed metric records
> cannot round-trip every key cactbot expects, so `EncounterSnapshot`/`CombatantMetrics` also carry an
> `ExportVariables` bag ([G1](#contract-gaps-tracked), shipped) that the shim populates. OP's per-swing
> `overHeal`/`damageShield`/`absorbHeal` are typed fields on `CombatantMetrics`
> ([G2](#contract-gaps-tracked), shipped).

### Audio — multi-sink

`IAudioOutput` separates producers (`Speak`/`Play`) from sink providers (`RegisterSink`, returns
`IDisposable`). Replaces the single mutable delegate slot that forced Discord-Triggers vs TTSYukkuri
into last-writer-wins. `IAudioSink` is async/fire-and-return so the host never blocks a producer on a
slow out-of-process bridge.

> Discord-Triggers and TTSYukkuri today *save the prior delegate and replace the slot*
> (`DiscordTriggersView.xaml.cs:178-185`; TTSYukkuri `PluginCore.cs:174-206`) — i.e. route audio
> **instead of** ACT's built-in speakers. The additive fan-out model plays on all sinks, so a
> **terminal-sink** flag reproduces that exclusive routing (`RegisterSink(sink, priority, terminal)`,
> [G3](#contract-gaps-tracked), shipped). Per-request channel/sync options ride on `AudioOptions`
> ([G8](#contract-gaps-tracked), shipped).

### Registry, peer events, raw packets

`IPluginRegistry`: enumerate peers (typed metadata), Triggernometry-style
`RegisterCallback`/`InvokeCallback`, and `Publish<T>`/`Subscribe<T>` so a plugin can put its own typed
events on the bus for others. `RegisterCallback` carries the owner/duplicate-name semantics Trig needs
([G5](#contract-gaps-tracked), shipped), and `TryGetPeerService<T>` hands a live, version-gated peer
*handle* to consumers that need more than metadata ([G6](#contract-gaps-tracked), shipped).

`IRawPacketSource` is the single opt-in, capability-gated raw-packet **read** hatch (the typed path
stays opcode-free for everyone else). OverlayPlugin also turns packets *back into* synthetic 256+ log
lines (`LineBaseCustom.cs:80-86` → `FFXIVRepository.cs:493-515`); that write-back round-trip is
`IPluginHost.RawLogLines.Emit` ([G4](#contract-gaps-tracked), shipped).

## Threading, performance & fault model (the explicit contract)

- **Event delivery** — each subscription owns a **bounded queue drained in order**; each handler
  call is fault-guarded (a throw can't kill the stream or starve peers); a slow/backed-up consumer
  gets **drop-oldest + a dropped counter** (never stalls the producer or other plugins). This closes
  OP's per-type dispatch stall *and* its unguarded subscribe-time replay uniformly (see finding 3
  above).
- **Snapshots/queries** are **free-threaded, immutable** — safe from any background poll thread.
- **Watchdog** — a handler over its time budget is logged; a repeatedly-throwing/hanging plugin is
  **quarantined** (unsubscribed) and may be **unloaded** (collectible ALC).
- **No UI-thread coupling** in the core contract — UI affinity is the host's concern (see below), not
  imposed on every plugin via a `Form` like ACT.
- **Alloc-conscious hot path** — record reuse, `byte[]`/span raw packets, binary internal bus (no
  `JObject`/JSON except where a compat shim demands it).

## Isolation & assembly loading

One **collectible `AssemblyLoadContext` per plugin** is the "isolate the assemblies" answer. The
host's load context resolves a **fixed contract set** — `Fct.Abstractions` / `Fct.Abstractions.UI` /
`Avalonia.*` — up to the default context so contract + UI types have one identity across the boundary;
everything else (the plugin's own Newtonsoft, etc.) stays private to its ALC via
`AssemblyDependencyResolver`/`.deps.json`. **Any assembly beyond that fixed set is shared only because
the plugin's manifest declares it** — so a shimmed plugin's manifest names `Fct.Compat.Shim` + its two
facades, and the **loader itself has zero name-knowledge of the shim** (rule 2: the host stays a
generic router/loader). No strong-name impersonation, no `AssemblyResolve` games — the legacy ACT-load
problem does not exist in this model. Plugins are pinned to the host's Avalonia version (as legacy
plugins were pinned to WinForms).

## UI strategy — plugins contribute Avalonia surfaces

UI is a **separate, opt-in contract** (`Fct.Abstractions.UI`, net10 + Avalonia) so headless plugins
take no UI dependency. A plugin opts in by also implementing `IUiContributor` and declaring `"ui"` in
its manifest — the `"ui"` capability gates whether the host calls `RegisterUi`, not whether the
contract assembly loads (`Fct.Abstractions.UI` is always shared to the default ALC, same as
`Fct.Abstractions` itself).

```csharp
public interface IUiContributor { void RegisterUi(IUiHost ui); }   // once, on the UI thread

public interface IUiHost
{
    void AddSettingsPage(UiSurface page);        // the WinForms TabPage replacement
    void RevealPage(string pageId);              // Triggernometry LocateTab
    IDisposable AddCornerControl(UiSurface ctl); // Triggernometry CornerControlAdd
    void RemoveCornerControl(string id);         // Triggernometry CornerControlRemove
    IUiDispatcher Dispatcher { get; }            // explicit UI-thread marshaling
}
```

- Content is a **lazy `Func<Control>`** — built on the UI thread when first shown; inherits the
  shell's theme/resources once attached; the plugin scopes its own styles/templates to its subtree.
- `IUiDispatcher` is the modern form of the legacy `InvokeRequired`/`Invoke` dance.
- **Fault containment:** `CreateView` and contribution calls are guarded → an inline error placeholder
  attributed to the plugin, never a dead shell; the shell attributes dispatcher-unhandled exceptions
  to the offending plugin for quarantine. Honest limit: a hard render/layout crash is still a host
  risk (same boundary as the ALC model).
- **Overlays stay OverlayPlugin's domain.** `IUiContributor` is for in-shell UI only; transparent
  click-through game overlays remain unmodified OverlayPlugin in its own net48 satellite
  ([`ARCHITECTURE.md`](ARCHITECTURE.md) §4a). Escape hatch only: a plugin may open its own top-level
  Avalonia window; the host provides no overlay scaffolding.
- Triggernometry's host-window mutations map onto `IUiHost.RevealPage` (`LocateTab`) and
  `AddCornerControl`/`RemoveCornerControl` ([G11](#contract-gaps-tracked), shipped).

### Wiring & readiness (modern UI) — ✅ built

The host runtime (`Fct.Host`) owns the `IUiHost` surface via `PluginUiCoordinator`
(`src/Fct.Host/Plugins/Ui/`) — the single owner of every contributed surface — handing each plugin a
per-plugin `PluginUiHost` handle (attributes calls back to the owning plugin id); the shell (`Fct.App`)
supplies `AvaloniaUiDispatcher` (`src/Fct.App/Plugins/Ui/`, the `IUiDispatcher` over
`Avalonia.Threading.Dispatcher.UIThread`). `IPluginHost` itself gains no UI member —
`IUiContributor.RegisterUi` is a separate call the coordinator drives directly.

- **Discovery + gating.** `PluginUiCoordinator.FlushRegisterUi` iterates every plugin `PluginManager`
  loaded; a plugin is skipped unless its manifest declares `"ui"` (checked *before* the
  `is IUiContributor` probe, per rule — a `"ui"` manifest without the interface is a logged no-op,
  never a cast failure).
- **Timing.** Plugin `InitializeAsync` runs at `host.Start()` (`Program.cs`), before Avalonia starts —
  so `RegisterUi` ("on the UI thread, after init, shell live") cannot fire from the loader itself. It
  is deferred to `MainWindow.OnOpened`, which resolves `PluginManager`/`PluginUiCoordinator` via
  `App.Services` and flushes every loaded plugin once the window is on screen.
- **Settings page.** A contributed page reaches `PluginViewModel.SettingsSurface` through the
  coordinator's `SettingsPageAdded` event (subscribed by `MainViewModel`); `PluginsView.axaml` renders
  it via `PluginSurfaceView` (`src/Fct.App/Views/`), a `ContentControl` that builds + caches the
  surface's `Control` lazily and once. A plugin with a contributed page no longer shows the read-only
  manifest details card (`PluginViewModel.ShowNativeDetails` is now that card's fallback state).
- **RevealPage / corner controls.** `PageRevealRequested` navigates the shell to the Plugins page and
  selects the owning row; `CornerControlAdded`/`Removed` drive `MainViewModel.CornerControls`,
  rendered as a transient overlay (`MainWindow.axaml`, top-right under the title bar) — reusing the
  same `PluginSurfaceView`.
- **Fault containment.** A throwing `RegisterUi` is caught per-plugin (peers still register) and
  surfaced as an inline error settings page attributed to the plugin id; a throwing `CreateView` is
  caught by `PluginSurfaceView` into an inline placeholder. Neither takes down the shell.
- **Unload retracts UI.** On hot-unload/uninstall the coordinator's `RemovePlugin` clears the plugin's
  settings page and any corner controls (`SettingsPageRemoved`/`CornerControlRemoved`), so a removed
  plugin leaves no orphaned surface. The **only contribution surfaces
  are the ACT-grounded ones** — a settings page (the `TabPage` replacement, rendered in the Plugins
  config bay next to the legacy embeds) plus Triggernometry's `RevealPage` and
  `AddCornerControl`/`RemoveCornerControl`; no nav-page / dashboard-widget / status-item surfaces.

`samples/Fct.SamplePlugin` is the reference `IUiContributor` (a settings page with a timed corner
control and a reveal button), exercised by `tests/Fct.App.Tests` (`PluginUiCoordinatorTests`, and the
extended `PluginLoaderTests`/`PluginManifestTests`). The legacy satellite-HWND embedding
(`NativeControlHost` into the config bay) and the shim D8 WinForms `TabPage` are the *other-face* UI
paths and are unchanged by this.

### Theme — a semantic token contract

A plugin `Control` inherits the shell's Avalonia theme automatically once attached: Avalonia is shared
to the default ALC, so a plugin's `Control` **is** the shell's `Control` type, and application-level
styles/resources (the shell's `FluentTheme` + palette) flow into any attached subtree. But the shell's
actual resource keys (its internal "Evercold" palette, fonts, style classes) are **implementation, not
contract** — so plugins do not bind them directly. Instead `Fct.Abstractions.UI` exposes a **stable
semantic token contract**, and the shell maps it onto its internal palette. This is the theming
equivalent of the typed data contract — a documented seam, never the internal implementation.

**Built — full catalog in [`UI-TOKENS.md`](UI-TOKENS.md).** `Fct.Abstractions.UI` ships three constant
classes: `FctTokens` (semantic brush + font resource keys — `FctSurface`/`FctAccent`/`FctText`/
`FctDanger`/…), `FctStyleClasses` (blessed `fct-*` style classes — `fct-h1`/`fct-card`/`fct-ghost`/…),
and `FctMetrics` (compile-time spacing / type-ramp / radius doubles). The shell supplies the values:
the token brushes/fonts are aliased onto the Evercold palette in `App.axaml`; the `fct-*` classes are
defined over those tokens (via `DynamicResource`) in `Styles/PluginTokens.axaml`, kept separate from
the shell's private `Controls.axaml` classes. Plugins bind tokens **dynamically** (`{DynamicResource}`
/ `GetResourceObservable`), so the shell can restyle, re-accent, or add a light variant (Dark-pinned
today, via `ThemeDictionaries` later) without breaking any plugin. `samples/Fct.SamplePlugin` is the
reference consumer; a `TokenContractTests` drift guard asserts every advertised key/class exists in the
shell XAML.

## The compat shim (adapter)

The net10 compat shim (not the existing net48 `Fct.Compat.Act`) re-projects the exact ACT + FFXIV-SDK
surface the plugins compile against, **implemented entirely over the modern host**. **Built** rows are
live on `FormActMain`/`LegacyPluginHost`; **pending** rows are the remaining work
(see [The net10 compat shim](#the-net10-compat-shim-in-progress)):

| Legacy symbol (recompiled against shim) | Backed by | Status |
|---|---|---|
| `ActGlobals.oFormActMain` (POCO hub, not a `Form`) | `IPluginHost` | ✅ built |
| `IActPluginV1.InitPlugin/DeInitPlugin` | `IPlugin.InitializeAsync/DisposeAsync` (via `LegacyPluginHost`) | ✅ built |
| `Before/OnLogLineRead` | `IGameEventStream` → `RawLogLine` | ✅ built |
| `RegisterNamedCallback`/`InvokeNamedCallback` (Trig peer interop) | `IPluginRegistry.RegisterCallback`/`InvokeCallback` ([G5](#contract-gaps-tracked)) | ✅ built |
| `PlayTtsMethod`/`PlaySoundMethod` delegate slots | setting a delegate registers a terminal `IAudioSink`; `TTS()`/`PlaySound()` → `IAudioOutput` ([G3](#contract-gaps-tracked)) | ✅ built |
| `PluginGetSelfData`/`AppDataFolder`/`WriteExceptionLog` | `IPluginStorage`/`ILogger` | ✅ built |
| `AddCombatAction`/`SetEncounter`/`EndCombat`/`InCombat` | shared aggregation engine (`ActiveZone.ActiveEncounter`) + mirrored onto `IEncounterService` | ✅ built |
| `CombatantData`/`EncounterData.ExportVariables` (opaque dict cactbot reads) | shared engine's registered formatters, projected onto `EncounterSnapshot`/`CombatantMetrics` typed fields + the `ExportVariables` bag ([G1](#contract-gaps-tracked)/[G2](#contract-gaps-tracked)) | ✅ built |
| `IDataSubscription` (11 events: NetworkReceived/Sent, CombatantAdded/Removed, PrimaryPlayerChanged, ZoneChanged, PlayerStatsChanged, PartyListChanged, LogLine, ParsedLogLine, ProcessChanged) | `IGameEventStream` + `IRawPacketSource` mapped to SDK delegate shapes | ✅ built (8 mapped: `LogLine`/`ZoneChanged`/`PartyListChanged`/`PrimaryPlayerChanged`/`CombatantAdded`/`CombatantRemoved` from the bus, `NetworkReceived`/`NetworkSent` from the raw-packet firehose; of the other 3, `ParsedLogLine`/`ProcessChanged` await their **source pipe** (see [What we must do](#what-we-must-do--the-work-list)) and `PlayerStatsChanged` is **dropped** — no tested consumer) |
| `IDataRepository.Get*` | `IGameSnapshot` + `IResourceCatalog` | ✅ built (name tables empty until `IResourceCatalog` is sourced; `GetCurrentFFXIVProcess` null — no net10 game handle) |
| `Combatant`/`Player`/`NetworkBuff` | projected from `Actor`/`StatusEffect` (lossless: CP/GP/world/order [G10](#contract-gaps-tracked), refresh/timestamp [G9](#contract-gaps-tracked)) | ✅ built (`Player` carries `JobID` only — the modern model has no attribute block) |
| `RegisterNetworkParser` / custom lines 256+ | `IRawPacketSource` (read) + `IRawLogLineEmitter` synthetic-line emit ([G4](#contract-gaps-tracked)) | ✅ built (read via `NetworkReceived`/`Sent` off `IRawPacketSource`; G4 write-back) |
| WinForms `TabPage` | real WinForms TabPage embedded via Avalonia `NativeControlHost` | ⏳ pending |

The shim's hub *is* the modern API underneath, so a plugin can use both at once — the basis for
incremental migration (swap one call at a time, then drop the shim).

### Per-plugin migration outlook

- **Triggernometry — easiest, but not "trivial".** `RealPlugin` already never references ACT (grep
  count 0; `mainform` is typed `System.Windows.Forms.Form`, `RealPlugin.cs:366`) — it calls **19
  injected delegate hooks** via `ProxyPlugin` (`ProxyPlugin.cs:145-163`). Re-point the proxy at the
  shim hub; the regex engine consumes `RawLogLine` unchanged. The recompile is mechanical but the
  surface is real: 19 hooks + reflected ACT members (`defaultTextFormat`, `CornerControlAdd/Remove`,
  `tc1`) must all be re-homed; named callbacks use [G5](#contract-gaps-tracked) (shipped).
- **Discord-Triggers — easy.** Pure audio sink, consumes no combat data (grep for
  LogLineRead/Encounter/CombatAction = 0). Its VM is already ACT-free and awaitable
  (`DiscordTriggersViewModel.cs:596-615`); only ~6 lines of delegate-swap glue touch ACT. Becomes an
  `IAudioSink` registration; its out-of-process Node bridge is unaffected. Terminal routing
  ([G3](#contract-gaps-tracked), shipped) preserves the route-instead-of-speakers behavior.
- **Hojoring — high value.** Recompile drops Sharlayan + OverlayPlugin side-channels because `Actor`
  carries status/enmity/in-combat and the snapshot carries focus/hover. Its poll-and-diff
  (`XIVPluginHelper.cs:904-912`) maps onto `IGameSnapshot`, or it adopts typed events and stops
  diffing. TTSYukkuri is both an audio producer and a sink provider ([G3](#contract-gaps-tracked)/[G8](#contract-gaps-tracked), shipped).
- **OverlayPlugin — stays in its own satellite.** Its `IDataSubscription`/`ExportVariables` use
  recompiles fine, but **CefSharp is net48-only** (pinned `95.7.141`, `HtmlRenderer.csproj:45-52`), so
  OP's CEF/Fleck rendering stays in a net48 satellite — **its own**, fully isolated from the parser,
  fed by the host-routed streams through the facade's synthetic parser stand-in
  ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P8). It is also itself a mini plugin-host
  (`IOverlayAddonV2.Init`, `PluginMain.cs:493-504`). The recompile path serves managed consumers; OP
  is why a satellite persists longest — consistent with overlays = unmodified OP, hosted. The managed-consumer surface for cactbot fidelity ([G1](#contract-gaps-tracked)/[G2](#contract-gaps-tracked)/[G4](#contract-gaps-tracked)) is shipped.

## Contract gaps (tracked)

Concrete gaps between the scaffold and what real plugin source consumes, ranked by severity. Each was
an **additive** edit to `Fct.Abstractions` (semver-safe per [Versioning](#versioning)) needing none of
the not-yet-built host/loader/shim. **All of G1–G11 are shipped** — the surface below is present in the
scaffold and each is exercised by the flow suite (G11 is UI-contract-only, verified by build).
`Focus`/`Hover` and `NetworkSent`/`PacketDirection.Sent` were already present and never gaps.

| ID | Sev | Gap | Evidence (real source) | Minimal additive fix |
|---|---|---|---|---|
| **G1** | ✅ shipped | The opaque `ExportVariables` dict cactbot consumes (`maxhit`, `tohit`, `swings`, `healstaken`, `damagetaken`, `critheal%`, `Last10/30/60/180DPS`) that the fixed metric records can't round-trip. | `MiniParseEventSource.cs:321-360,385-414,289-298` vs `Encounters.cs` | **Present:** `EncounterSnapshot` **and** `CombatantMetrics` carry an init-only `IReadOnlyDictionary<string,string> ExportVariables` (empty default); the shim populates it, native consumers use the typed fields. |
| **G2** | ✅ shipped | OP's own `overHeal`/`damageShield`/`absorbHeal` are computed from per-swing tags, absent from any typed metric. | `Integration/FFXIVExportVariables.cs:31-49,64-81,97-114` | **Present:** `CombatantMetrics` carries init-only `Overheal`/`ShieldedDamage`/`Absorbed` (0 default) alongside the G1 bag. |
| **G3** | ✅ shipped | Audio sinks today *replace* the slot (route-instead-of); `RegisterSink` only fans out → double playback. | `DiscordTriggersView.xaml.cs:178-185`; TTSYukkuri `PluginCore.cs:174-206`; `Audio.cs:18-19` | **Present:** `RegisterSink(sink, priority, bool terminal = false)` — a terminal sink stops the chain, so a route-instead-of sink suppresses lower-priority sinks. |
| **G4** | ✅ shipped | Packet → custom 256+ line → `RawLogLine` round-trip absent. `IRawPacketSource` is read-only; `AppendLogLine` targets the export log, not the live bus. | `LineBaseCustom.cs:80-86` → `FFXIVRepository.cs:493-515`; `RawPackets.cs:11-18`; `Encounters.cs:28` | **Present:** `IRawLogLineEmitter { void Emit(LogMessageType, string); }` on `IPluginHost.RawLogLines` (capability-gated) fans a synthetic 256+ line onto the event bus. |
| **G5** | ✅ shipped | Trig named callbacks carry `(int id, name, delegate, object owner, registrant, allowDuplicatedName)` and a `(owner, string val)` delegate; `RegisterCallback(string, Action<object?>)` is too thin. | `RealPlugin.cs:71-74,4176-4249`; `ProxyPlugin.cs:37-77`; `Plugin.cs:85` | **Present:** `RegisterCallback(string name, Action<object?> cb, object? owner = null, bool allowDuplicate = false)` — owner-tagged, duplicate names rejected unless opted in; `IDisposable` replaces int-id unregister. |
| **G6** | ✅ shipped | Live, version-gated typed peer *handle* — `LoadedPlugins` returns metadata only; Trig's `BridgeFFXIV` needs a Started-gated handle to the parser peer. | `ProxyPlugin.cs:431-477` (gate strings :444-446); `Plugin.cs:83` | **Present:** `bool TryGetPeerService<T>(string pluginId, out T service)` on `IPluginRegistry`. Shrinks once the parser is the host's own snapshot. |
| **G7** | ✅ shipped | `StartCombat(title)` — Trig passes player-name as both title and zone label. | `ProxyPlugin.cs:327-334`; `Encounters.cs:16` | **Present:** `StartCombat(string? title = null, string? zone = null)`; zone defaults to title. `EncounterSnapshot.Zone` carries the label. |
| **G8** | ✅ shipped | TTSYukkuri `Speak` carries channel (L/R/Both) + sync flag; `AudioOptions` has Volume/Voice/Rate only. | TTSYukkuri `PluginCore.cs:248-260`; `Audio.cs:33` | **Present:** `AudioChannel {Both,Left,Right}` enum + init-only `Channel`/`Synchronous` on `AudioOptions`. |
| **G9** | ✅ shipped | SDK `NetworkBuff` carries `RefreshPending`/`Timestamp`/`ActorName`/`TargetName`; `StatusEffect` drops them. | `Actors.cs:25` (vs `NetworkBuff`) | **Present:** init-only `RefreshPending` + `AppliedAt` on `StatusEffect` (`ActorName`/`TargetName` remain derivable from `SourceActorId`). |
| **G10** | ✅ shipped | SDK `Combatant` has `CurrentCP/MaxCP/CurrentGP/MaxGP`, distinct `CurrentWorldID` vs `WorldID`, `Order`; `Actor` has none/one → lossy for DoL/DoH. | `Combatant.cs:29-35,55,57,73`; `Actors.cs:37-55` | **Present:** init-only nullable `CurrentCp`/`MaxCp`/`CurrentGp`/`MaxGp` + `CurrentWorldId` + `Order` on `Actor` — lossless `Combatant→Actor` for DoL/DoH. |
| **G11** | ✅ shipped | Trig's `CornerControlAdd/Remove` transient notifications + `LocateTab` "reveal my page" have no `IUiHost` member. | `ProxyPlugin.cs:182-264`; `Ui.cs:21-37` | **Present:** `IUiHost.RevealPage(id)` + `AddCornerControl(UiSurface)`/`RemoveCornerControl(id)`. |

## Testing the contract — legacy↔native flow tests

The data path is **native `IPlugin` ⇄ in-process bus/registry/audio/encounter-service ⇄ net10
compat shim ⇄ recompiled legacy plugin**. The flow suite runs headless against a **thin fake host +
in-memory implementations of the shipped interfaces + a minimal shim stub** (`ShimStub`) — zero
satellite, zero CEF, zero live FFXIV — so the contract shapes are exercised without the real shim.
Legacy plugins are represented by **tiny doubles** that make the exact call the real plugin makes
(proven by the cited file:line), not by loading the real net48 DLLs. The harness lives in
`Fct.Abstractions.Testing`; the flow suite in `Fct.FlowTests`. (The real shim's own seams are covered
separately by `Fct.Compat.Shim.Tests`; see [The net10 compat shim](#the-net10-compat-shim-in-progress).)

### The harness

`Fct.Abstractions.Testing` (net10) + `Fct.FlowTests` (net10 xUnit) provide:

1. **`FakePluginHost : IPluginHost`** — wires the fakes below; `Self` = a canned `PluginInfo`.
2. **`InMemoryEventBus : IGameEventStream`** — a bounded per-subscriber ring (hand-rolled, mirroring
   `RingBufferDataSubscription`; no `System.Threading.Channels` dependency on the net48 leg), in-order
   synchronous drain, per-handler try/catch, drop-oldest + `DroppedCount`; test-only `Emit(GameEvent)`
   producer side.
3. **`InMemoryRegistry : IPluginRegistry`** — real dictionaries backing `RegisterCallback`/`InvokeCallback`
   and `Publish<T>`/`Subscribe<T>`. Fully implementable from the shipped interface today.
4. **`RecordingAudioOutput : IAudioOutput`** + **`RecordingAudioSink : IAudioSink`** — capture calls.
5. **`FakeEncounterService : IEncounterService`** — mutable `Active`/`Last`, `AppendLogLine` capture,
   `StartCombat/EndCombat` state.
6. **`FakeSnapshot : IGameSnapshot`** — builder over immutable `Actor`/records.
7. **`ShimStub`** — the seam under test: an `oFormActMain`-shaped surface exposing the legacy entry
   points a recompiled plugin calls (`TTS`, `PlayTtsMethod`/`PlaySoundMethod` slots,
   `Before/OnLogLineRead`, `SetEncounter`, `RegisterNamedCallback`) that forwards to/from the fakes.
   This one stub is what lets flow tests exist before the real shim; it is the shim's seed.

### (a) native → legacy — a new `IPlugin` produces, a shimmed legacy plugin consumes

| # | Scenario (real plugin) | Seam under test | Fixture | Assertion | Headless |
|---|---|---|---|---|---|
| A1 | native `Publish<T>`/`InvokeCallback` → **Triggernometry** named callback (`RealPlugin.cs:4147`; `ProxyPlugin.cs:37-77`) | `IPluginRegistry` ↔ shim `RegisterNamedCallback` | `InMemoryRegistry` + Trig-double | Trig handler receives payload | ✅ |
| A2 | native `Audio.Speak` → **Discord-Triggers**/**TTSYukkuri** sink (`DiscordTriggersView.xaml.cs:178`) | `IAudioOutput.Speak` → `IAudioSink.SpeakAsync` | `RecordingAudioOutput` + sink-double | Sink gets exact text+`AudioOptions`; **two sinks → G3 fan-out vs terminal** | ✅ |
| A3 | native-emitted `RawLogLine` → **Triggernometry**/**cactbot** regex (`Trigger.cs:615`; `FFXIVOptionalEventSource.cs:57`) | `IGameEventStream` `RawLogLine` ↔ shim `OnLogLineRead` | `InMemoryEventBus.Emit` | Trig-double `rex.Match` fires; wrong-type filtered out | ✅ |
| A4 | native `StartCombat` → **OverlayPlugin**/Trig encounter reader (`MiniParseEventSource.cs:300`) | `IEncounterService.StartCombat`/`Active` ↔ `ActiveZone.ActiveEncounter` | `FakeEncounterService` | Reader sees `InCombat==true` + labeled `Active`; G1 dict if testing cactbot payload | ✅ |
| A5 | native `ZoneChanged`/`PartyChanged` → **OP** typed consumer (`FFXIVRepository.cs:544,551`) | `GameEvent` records ↔ shim `IDataSubscription` delegates | `InMemoryEventBus.Emit` | OP-double `ZoneChanged`/`PartyListChanged` gets mapped args | ✅ |

### (b) legacy → native — a shimmed legacy plugin produces, a new `IPlugin` consumes

| # | Scenario (real plugin) | Seam under test | Fixture | Assertion | Headless |
|---|---|---|---|---|---|
| B1 | **Triggernometry** `TTS()`/`PlaySound()` → native `IAudioSink` (`RealPlugin.cs:1650-1683`) | shim `oFormActMain.TTS/PlaySound` → `IAudioOutput` → `IAudioSink` | Trig-double + `RecordingAudioSink` | Native sink gets text/(file,volume); volume 0–100 preserved | ✅ |
| B2 | **Trig** `SetCombatState`/`AddCombatAction`+`SetEncounter` → `IEncounterService` → native reader (`ProxyPlugin.cs:323-334`) | shim `SetEncounter/EndCombat` → `FakeEncounterService` | Trig-double + native reader | Native sees encounter open/close + label; `AppendLogLine` round-trip (G4 boundary) | ✅ |
| B3 | **Trig** external `RegisterNamedCallback` → native `Subscribe<T>` (`ProxyPlugin.cs:37-77`) | shim `RegisterNamedCallback` ↔ `IPluginRegistry` | `InMemoryRegistry` | Native handler receives invocation; exercises G5 owner/dup semantics | ✅ |
| B4 | **OP** raw packet → custom 256+ line → native `RawLogLine` consumer (`LineBaseCustom.cs:80-86`) | `IRawPacketSource` read + `IRawLogLineEmitter.Emit` (G4) | `InMemoryEventBus` + `FakeRawLogLineEmitter` | Native `RawLogLine` subscriber sees the custom line | ✅ |
| B5 | legacy poll (`GetCombatantList`) → native `IGameSnapshot` (`XIVPluginHelper.cs:904`) | shim `Combatant→Actor` → `IGameSnapshot` | `FakeSnapshot` | Native sees actors incl. `Statuses/Enmity/InCombat/Focus/Hover`; CP/GP + refresh/timestamp round-trip (G9/G10) | ✅ |

### What can and cannot be tested now

- **Implemented and green, headless (21 tests, 0 skipped):** the full A1–A5 / B1–B5 set — including
  A2 fan-out **and** the A2 terminal-sink variant (G3), B4's synthetic-line emit (G4), and the
  G2/G5/G6/G7/G8/G9/G10 round-trip checks — plus bus filter/isolation checks and the G1
  default/round-trip check. They validate every contract gap end-to-end with only the harness as new
  code. G11 (UI surfaces) is contract-only and verified by build, not a headless flow test.
- **`ExportVariables` projection fidelity is covered by the real shim:** `Fct.Compat.Shim.Tests` feeds
  the same swing vector through the net10-compiled shared aggregation engine + `EncounterProjector` and
  asserts the projected `ExportVariables` bag equals the net48 oracle values (cross-TFM parity).
- **The `IDataSubscription` event map is covered by the real shim:** beyond A5's `ShimStub`,
  `Fct.Compat.Shim.Tests` drives `DataSubscriptionAdapter` off the in-memory bus and asserts each mapped
  delegate (`LogLine`/`ZoneChanged`/`PartyListChanged`/`PrimaryPlayerChanged`/`CombatantAdded`/
  `CombatantRemoved`) fires with the projected args, and that `FormActMain.DataSubscription` delivers
  once wired.
- **The `IDataRepository` + `Combatant` projection + discovery are covered by the real shim:**
  `Fct.Compat.Shim.Tests` asserts `CombatantProjector` maps `Actor`→`Combatant`/`NetworkBuff`/`Player`
  field-by-field (incl. CP/GP/world/order and statuses→NetworkBuffs), drives `DataRepository`'s `Get*`
  off a seeded snapshot, and exercises OverlayPlugin's exact discovery path (scan `ActPlugins` by title →
  reflect `DataRepository`/`DataSubscription` off the synthetic FFXIV-plugin stand-in). The host's
  `GameSnapshotAggregator` is covered in `Fct.App.Tests` (folding combatants/zone/party/player + HP
  update + removal off the live bus).
- **Needs the net48 satellite / live game (out of scope here):** loading the *real* FFXIV_ACT_Plugin /
  OverlayPlugin (CefSharp net48-only), real Machina packet decode, and real `MasterSwing` aggregation
  parity (already covered by `tools/mass-compare`, see [`TESTING.md`](TESTING.md)). Flow tests assert
  *routing*, not *parse/aggregation correctness*.

### Build order

The suite is built in dependency order, each step adding one harness piece:
**B1** (audio fakes + `ShimStub.TTS/PlaySound`) **→ A2** (fan-out + the G3 terminal variant)
**→ A1/B3** (`InMemoryRegistry`; the Trig callback loop both ways, plus G5/G6) **→ A3/A5**
(`InMemoryEventBus`; `RawLogLine` + the typed ZoneChanged/PartyChanged mapping) **→ A4/B2**
(`FakeEncounterService`; combat-state both ways, plus G2/G7) **→ B4** (the G4 emit path via
`FakeRawLogLineEmitter`) **→ B5** (`FakeSnapshot`; G9/G10 round-trip). The fakes are complete enough
that the real net10 shim can reuse every one as its acceptance suite. Each gap landed test-first as
its scenario went RED→green.

## Versioning

`Fct.Abstractions` is **semver, additive-only**, with a `contract` version the host gates against the
manifest. New `GameEvent` records are additive (switch-and-ignore-unknown); every fix in
[Contract gaps](#contract-gaps-tracked) is an additive edit.

## The net10 host & ALC loader (built)

The real host services + assembly loader exist in `Fct.Host` (a headless net10 class library the shell
references), promoting the in-memory fakes to production and loading a real native `IPlugin` from disk:

- **Host services** (`src/Fct.Host/Hosting/`): `GameEventBus` (a real `IGameEventStream` — bounded
  per-subscription channel drained by a background pump, drop-oldest backpressure, fault-guarded
  handlers, generalizing `RingBufferDataSubscription`) with an in-process `IGameEventSink` producer
  seam; `RegistryService`, `AudioService` (priority fan-out + terminal-sink G3), `EncounterService`,
  disk-backed `PluginStorage`, `SystemClock`, the capability-gated `RawLogLineEmitter` (G4), and the
  per-plugin `PluginHost : IPluginHost`. Registered by `AddFctHostServices`
  (`src/Fct.Host/ServiceCollectionExtensions.cs`), which the shell's `Program.BuildHost` calls (it adds
  only the shell-only pieces: `MainViewModel`, `ISatelliteNotificationText`, `LegacyPluginHostFactory`,
  `AvaloniaUiDispatcher`).
- **Live snapshot aggregator** (`src/Fct.Host/Hosting/GameSnapshotAggregator.cs`): a hosted service that
  subscribes to the state events on `GameEventBus` (raw log lines filtered out), folds
  `CombatantAdded`/`CombatantRemoved`/`HpUpdated`/`ZoneChanged`/`PartyChanged`/`PrimaryPlayerChanged`
  into an immutable `IGameSnapshot`, and publishes it through `GameSnapshotProvider` — so
  `IGameSession.Snapshot()` (and the shim's `IDataRepository`) reads live actor/zone/party/player state
  off the forwarded stream. `Target`/`Focus`/`Hover`, the actor side-channel fields, and the resource
  catalog are not sourced yet — their producers are decided (satellite forward; resource-provider
  plugin) and tracked in [What we must do](#what-we-must-do--the-work-list).
- **ALC loader** (`src/Fct.Host/Plugins/`): `PluginManifest` (`plugin.json`, read without loading the
  assembly) + `HostContract` major-version gate; `PluginLoadContext` (collectible, `Fct.Abstractions`/
  `Fct.Abstractions.UI`/`Microsoft.Extensions.Logging.Abstractions`/`Avalonia.*` — plus the compat
  shim (`Fct.Compat.Shim`) and its two legacy-identity facades (`Advanced Combat Tracker`,
  `FFXIV_ACT_Plugin.Common`) — shared to the default context, everything else private via
  `AssemblyDependencyResolver`; **the shim/facade sharing is currently by-name and must move to
  manifest-declared** so the loader names only the fixed contract set, item 8 in
  [What we must do](#what-we-must-do--the-work-list)); `PluginManager` (discover → gate → load → time-boxed fault-guarded
  init → quarantine/unload) driven by the `PluginLifetime` hosted service.
- **Sample plugin** (`samples/Fct.SamplePlugin`) loads end-to-end and exercises events/snapshot/
  registry/audio/storage/raw-write-back; covered by `tests/Fct.App.Tests` (bus, registry, audio,
  manifest gate, share-to-default identity, load/init/unload from disk).

The net48→net10 **bridge data forwarder** (piece C) is built (see below); the net10 **compat shim**
(`ShimStub` → real, piece D) is in progress (see
[The net10 compat shim](#the-net10-compat-shim-in-progress)).

## The bridge data forwarder (built)

Live game data reaches the net10 bus over the existing satellite→host pipe (piece C):

- **Satellite (`src/Fct.LegacyHost/BridgeForwarder.cs`)** — the production form of `Fct.StreamProbe`:
  same discovery (reflect `ActGlobals.oFormActMain.ActPlugins`), the same SDK subscriptions and ACT-hub
  tap, but it **projects each callback into a typed `GameEvent`** and ships it to the host instead of
  logging it. A bounded ring + one writer thread (drop-oldest + a dropped counter, mirroring
  `RingBufferDataSubscription`) keeps pipe I/O off the SDK dispatch/UI threads on the high-rate firehose.
- **Wire (`GameEventFrame` in `Fct.Bridge.Contracts`, referenced by both processes)** — a new `EVT` frame kind
  alongside `LOG`: one tab-delimited, backslash-escaped line per event, **no JSON** (the
  `BridgeLogRecord` convention). `Sequence` is not on the wire — the host re-stamps each decoded event
  from its own `IGameEventSink.NextSequence()` so the bus keeps one coherent per-session ordering.
- **Host (`src/Fct.Host/SatelliteHost.cs`)** — the bridge read loop decodes `EVT` frames and calls
  `IGameEventSink.Emit`, so bridge events flow to every subscription exactly like any in-process source.

**What the forwarder can carry is bounded by the sole-parser directive** — it may only project data the
SDK/ACT hub exposes (post-parse aggregates, or the untouched raw firehose), never re-parse a log line.
Forwarded today: `RawLogLine` (the SDK `LogLine` firehose that Trig/cactbot regex over), `ZoneChanged`,
`PartyChanged`, `PrimaryPlayerChanged`, `CombatantAdded`/`CombatantRemoved` (`Combatant`→`Actor`,
lossless for DoL/DoH per [G10](#contract-gaps-tracked)), `ActionEffect` (from the ACT hub's
post-aggregation `AfterCombatAction`), `RawPacketReceived` (the SDK `NetworkReceived`/`NetworkSent`
firehose — raw bytes carried base64 on the wire, never decoded), and the **pre-aggregation feed that
makes the host the calculation authority**: `CombatSwing` (the full `MasterSwing` — every field +
`Tags`, raw `Dnum` sentinels, tapped off the facade's `BeforeCombatAction`) plus the encounter
lifecycle (`SetEncounterRequested`/`ZoneChangeRequested`/`EndCombatRequested`), which `Fct.Engine`
folds into the host's `IEncounterService` truth. Events that exist only as parsed
log-line fields — `StatusApplied`/`Removed`, `Cast*`, `DeathOccurred`, `HpUpdated` — are **not**
synthesized; consumers reach them through the `RawLogLine` firehose, exactly as in ACT today. Codec
round-trip + decode-onto-bus are covered by `tests/Fct.App.Tests/BridgeEventFrameTests.cs`.

The same `GameEventFrame` codec runs **downstream** (host → satellite) in the isolated topology:
each consumer satellite subscribes to the stream set its facade projects (engine-replica swings,
log lines, packets, repository snapshots), with a bounded per-satellite egress queue mirroring the
upstream ring. Build phases: [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P4–P6.

## The net10 compat shim (in progress)

The compat shim (piece D) is the real ACT/SDK re-projection the `ShimStub` seam seeds. It ships three
assemblies plus a sample legacy plugin, loaded by the existing ALC loader:

- **`Fct.Compat.Shim.ActFacade`** (assembly `Advanced Combat Tracker`, net10-windows) — the POCO
  `ActGlobals`/`FormActMain` a recompiled plugin binds to. `FormActMain` holds an `IPluginHost` and
  forwards to it; it is **not** a WinForms `Form`. Inert `FormImportProgress`/`FormSpellTimers` stubs
  keep the spell-timer / custom-trigger subsystems out of the data path.
- **`Fct.Compat.Shim.SdkFacade`** (assembly `FFXIV_ACT_Plugin.Common`, net10) — re-declares the SDK
  surface a recompiled plugin binds to: `IDataSubscription` (11 events + delegates), `IDataRepository`,
  and the models `Combatant`/`Player`/`NetworkBuff`/`PartyType`, matching the real DLL's identity.
- **`Fct.Compat.Shim`** (net10-windows) — the adapter runtime. `LegacyPluginHost : IPlugin` resolves a
  plugin's `IActPluginV1` (named by the manifest's `legacyEntry`), wires the process-shared
  `ActGlobals.oFormActMain` over the host, and bridges the lifecycle (`InitPlugin`⇄`InitializeAsync`,
  `DeInitPlugin`⇄`DisposeAsync`).

The shim + its two facades + `Fct.Aggregation` are an **opt-in staged package**, not a static
reference: `Fct.App` carries **no** ACT-impersonation identity in its `deps.json`. The set is staged
under `compat\` next to the host (`StageCompatShim`), and `CompatRuntime.Enable`
(`src/Fct.Host/Plugins/CompatRuntime.cs`, called from `Program.BuildHost`) hooks
`AssemblyLoadContext.Default.Resolving` to resolve those assemblies from `compat\` — they are not on
the TPA list, so this resolver is what lets the default context *find* them. The staged folder is
located via `AppData.InstallDirectory` (the launched-exe dir, so it resolves under the single-file
self-extracting build).

The host loads a shimmed plugin through the same path as a native one: `PluginManifest` gains an
optional `legacyEntry` (mutually exclusive with `entry`); `PluginManager` routes it through an injected
`LegacyPluginHostFactory` that materializes the shim's `LegacyPluginHost` **reflectively** via
`AssemblyLoadContext.Default.LoadFromAssemblyName` (so the loader takes no compile-time shim
dependency); `PluginLoadContext.IsShared` routes `Fct.Compat.Shim` + the two facades to the default
ALC so the shim and the plugin agree on type identity (this by-name sharing must move to
manifest-declared, item 8). Hosting recompiled WinForms plugins in-process makes `Fct.App` a
**net10-windows** app.
`samples/Fct.SampleLegacyPlugin` is the reference recompiled plugin; `tests/Fct.Compat.Shim.Tests`
covers identity, lifecycle, the audio/registry/raw-line seams, the encounter driver +
cross-TFM `ExportVariables` parity, the `IDataSubscription` event map, the `IDataRepository` +
`Combatant` projection, and the reflection-discovery stand-in.

**Built:** the `FormActMain` hub over `IPluginHost`; `LegacyPluginHost` lifecycle; audio
(`TTS`/`PlaySound` → `IAudioOutput`, `PlayTts`/`PlaySoundMethod` slots → terminal `IAudioSink`, G3);
named callbacks (`RegisterNamedCallback`/`InvokeNamedCallback` → `IPluginRegistry`, G5);
`Before/OnLogLineRead` re-fired from the `RawLogLine` firehose; logging + chrome
(`WriteExceptionLog`/`CornerControlAdd`/…); the encounter driver (`AddCombatAction`/`SetEncounter`/
`EndCombat`, `ActiveZone.ActiveEncounter`) over the ACT aggregation engine — the shared,
strong-named `Fct.Aggregation` project (net48;net10) referenced by the host's `Fct.Engine` (the
aggregation authority), the net48 `Fct.Compat.Act`, and the net10 ActFacade, so the aggregation
binary (and the `ExportVariables` bag cactbot reads) is bit-identical across runtimes
(`Fct.Engine.EncounterProjector` maps it plus the typed metrics onto
`EncounterSnapshot`/`CombatantMetrics`, G1/G2; combat state mirrors onto `IEncounterService`). The `IDataSubscription` event map: `DataSubscriptionAdapter` projects the
`IGameEventStream` onto the SDK delegates a recompiled plugin binds — `LogLine`←`RawLogLine`,
`ZoneChanged`, `PartyListChanged`←`PartyChanged`, `PrimaryPlayerChanged`, and
`CombatantAdded`/`CombatantRemoved` (via `CombatantProjector`) — plus `NetworkReceived`/`NetworkSent`
routed by direction off `IPluginHost.RawPackets` (`IRawPacketSource`), exposed on the hub via
`FormActMain.DataSubscription`. The `IDataRepository` pull surface: `DataRepository` answers every
`Get*` off `Host.Game.Snapshot()`, with `CombatantProjector` mapping `Actor`→`Combatant`/`NetworkBuff`
and the player's `Actor`→`Player`. `LegacyPluginHost` publishes a `SyntheticFfxivPlugin` stand-in into
`ActPlugins` so OverlayPlugin/Hojoring discover `DataRepository`/`DataSubscription` by reflection
exactly as under real ACT.

**Honest limits (facts, not stubs):** `Player` carries `JobID` only — the attribute block +
`PlayerStatsChanged` are **dropped by decision** (no tested consumer), not a gap; `GetCurrentFFXIVProcess` returns null (no net10 game
handle — the connection lives in the satellite); `GetResourceDictionary` is empty until
`IResourceCatalog` is sourced.

**Pending — each maps to a named pipe in [What we must do](#what-we-must-do--the-work-list), not to a
"no source" gap:** live per-tick metric refresh into `IEncounterService.Active` (host update seam);
`ParsedLogLine` (fire off the existing `RawLogLine` firehose, for Trig's `BridgeFFXIV`); `ProcessChanged`
(the process-lifecycle pipe); the WinForms `TabPage` embedding via Avalonia `NativeControlHost` (slice
D8, design below); and the `_iocContainer` logging-bridge seam a recompiled OverlayPlugin/Hojoring
reflects for its own log routing — **the reflected container routes to `IPluginHost.Logger`/`ILogger`**
(decided). Also under-specified: `Form`-inherited `oFormActMain` members (accepted limit — see below).

### D8 — WinForms `TabPage` embedding (planned next slice)

D8 surfaces a recompiled plugin's own WinForms settings tab in the app's existing Plugins config bay.
`LegacyPluginHost` already builds the `TabPage`/`Label` ACT hands a plugin and calls `InitPlugin`
(`LegacyPluginHost.cs:54-55,68`) — the plugin fills the tab with its own WinForms controls — but the
tab is built **detached and never shown**. D8 embeds that already-filled tab; it adds **no new UI**
(the plugin supplies its settings screen) and reuses the config bay (`PluginsView.axaml` `EmbedSlot`)
that already hosts satellite plugins' windows. It serves the migration half-step *ported to .NET 10, UI
still WinForms* (WinForms is supported on `net10.0-windows`); the forward Avalonia `IUiContributor`
surface is a separate, later slice.

Approach:

- **Shim (`Fct.Compat.Shim`)** — before `InitPlugin`, add `_tab` to a host-owned real `TabControl` (one
  per plugin instance, `Dock=Fill`) so the tab's handle is created in a live control tree and
  `pluginScreenSpace.Parent as TabControl` works (sibling-tab-adding plugins behave); mirrors real ACT's
  order (TabPage added to `tcPlugins` before `InitPlugin`). Expose it via a new WinForms-typed
  `ILegacyTabSurface { Control? TabRoot; string StatusText; event Action? StatusTextChanged }` that
  `LegacyPluginHost` implements — `TabRoot` = the owned `TabControl`; `StatusText` reads `_statusLabel`,
  hooking its `TextChanged`. Dispose the `TabControl` with `_tab`. `oFormActMain` stays a POCO.
- **App (`Fct.App`)** — a new `EmbeddedWinFormsView : NativeControlHost` hosting an **in-process** WinForms
  control we own (the plugin's `TabControl`): reparent under the Avalonia parent HWND via
  `SetParent`/`WS_CHILD` (same P/Invoke block as `EmbeddedSatelliteView`), returning
  `PlatformHandle(control.Handle,"HWND")`; on teardown **detach + hide, don't destroy** (the shim owns the
  control). Ensure WinForms init once on the UI thread (`Application.EnableVisualStyles` +
  `SetCompatibleTextRenderingDefault` + a `WindowsFormsSynchronizationContext`) so the control renders and
  `Control.Invoke` marshals under Avalonia's Win32 pump.
- **Routing** — a loaded shim plugin (`PluginManager.Loaded`, `IPlugin is ILegacyTabSurface`) becomes an
  embeddable in-process row in `MainViewModel`/`PluginViewModel` (`HasNativeConfig=true`), distinct from a
  plain `Kind.Native` row (which keeps the static `ShowNativeDetails` card). `PluginsView.axaml.cs`
  `UpdateEmbed` (`:61-78`) gains a parallel branch that puts `EmbeddedWinFormsView(surface.TabRoot)` in
  `EmbedSlot` (cached per plugin, like `_embeds`) and surfaces `StatusText`; the satellite-HWND branch is
  untouched.
- **Sample (`samples/Fct.SampleLegacyPlugin`)** — add a trivial marker control to
  `pluginScreenSpace.Controls` in `InitPlugin` so the embed has visible, assertable content.

Honest limit: `oFormActMain` stays a POCO (not a WinForms `Form`), so plugins that reach
`oFormActMain.Handle`/`Invoke`/`InitActDone` (OverlayPlugin, Hojoring) remain satellite-hosted — CEF/net48
pins them there regardless.

Tests: `Fct.Compat.Shim.Tests` — after init, `ILegacyTabSurface.TabRoot` is a `TabControl` containing the
plugin's `TabPage` with its marker control, `TabPage.Parent` is that `TabControl`, and
`StatusText`/`StatusTextChanged` reflect the label. `Fct.App.Tests` — VM routing (an `ILegacyTabSurface`
plugin yields an embeddable row; a plain native plugin does not), headless. The visual embed itself is a
run-`Fct.App` check (a `NativeControlHost` needs a real window).

Risk to watch: in-process WinForms child rendering under Avalonia's `NativeControlHost` + shared message
pump (handle timing, WinForms init, sync context) — the satellite precedent covers foreign-HWND
reparenting, not an owned in-process control. Fallback if the shared pump misbehaves: a dedicated WinForms
host window/thread for the control.

## Plugin lifecycle, packaging & SDK distribution

How a plugin is authored, packaged, installed, discovered, updated, and removed — across the three
faces. Pieces not yet built are numbered items in [What we must do](#what-we-must-do--the-work-list).

### Package format — a loose plugin folder

A plugin is a **folder** holding the entry assembly + its private dependency closure, the `*.deps.json` /
`*.runtimeconfig.json` the ALC's `AssemblyDependencyResolver` consumes, and an **optional** `plugin.json`
(read **without loading any assembly**). One root holds plugin folders: **user-installed plugins land in
`%LOCALAPPDATA%\FFXIVCombatTracker\installed-plugins\<id>\`** (one `<id>` folder per plugin) — the host
bundles no plugins and auto-discovers none from disk. `PluginInstaller` accepts either a folder **or a `.zip`** (extracted to a
staging dir, then descended to the payload root) as the install source. **No signing in v1** — install is
a folder copy; isolation and type identity are the ALC's job, not a signature's. When `plugin.json` is
absent, `PluginClassifier` inspects the entry assembly's references with a `MetadataLoadContext` (no
execution) to derive kind + identity; a present manifest is authoritative. Shared assemblies (contract,
Avalonia, shim/facades) may physically sit in the folder but are always resolved from the default ALC for
one type identity (see [Isolation](#isolation--assembly-loading)).

### Manifest

`plugin.json` carries `id`, `version`, `contract`, `assembly`, exactly one of `entry`/`legacyEntry`,
`capabilities[]`, and optional display metadata / UI opt-in:

- **Display metadata** — `name`, `description`, `author` (all optional; the Plugins roster falls back
  to `id` and a generic description when omitted). Projected onto `PluginInfo` and shown on the roster
  row.
- **UI opt-in** — a `"ui"` capability that gates whether the host calls `IUiContributor.RegisterUi`
  (see [Wiring & readiness](#wiring--readiness-modern-ui--built)); declaring it without implementing
  the interface is a logged no-op, not an error.

The `contract` string is the single host gate (`HostContract.Accepts`, **major-version equality**); with
NuGet SDK distribution it equals the SDK package major the plugin built against.

### Install, discover, update, remove — full lifecycle (built)

`PluginInstaller` owns the runtime lifecycle end-to-end over the collectible ALC (net10 faces) and the
satellite channel (real-legacy face). `PluginRegistryStore` persists the installed set to
`%LOCALAPPDATA%\FFXIVCombatTracker\installed-plugins.json` so it survives restarts.

- **Classify** — `PluginClassifier` routes each install to a `LoadKind`: `Native` (references
  `Fct.Abstractions`, implements `IPlugin`), `RecompiledShim` (references the unsigned `Advanced Combat
  Tracker` facade / declares `legacyEntry`), or `RealLegacy` (references `Advanced Combat Tracker` under
  the real ACT strong-name token `a946b61e93d97868`). Manifest-first; else metadata-only reflection with
  a `MetadataLoadContext` (no execution).
- **Install** — the shell's *Add plugin* (`MainViewModel.AddPlugin`) opens a single file picker for a
  `.zip` package or a single `.dll` and hands the pick to `PluginInstaller.InstallAsync` (which also
  accepts a directory for headless/dev callers). The installer classifies the payload and routes it:
  native/shim plugins are copied into `installed-plugins\<id>\`; real-legacy plugins **install by
  reference** — loaded in place from where the user picked them (zips are the exception: their extract
  dir is transient, so they are always copied). Either way the plugin loads live and is recorded.
- **Route load** — net10 kinds load in-process (`PluginManager.LoadDirectoryAsync` → fresh collectible
  ALC, `contract` gate, time-boxed fault-guarded init, quarantine on fault). Real-legacy kinds are sent
  to the net48 satellite as a `LOADPLUGIN` command frame (`ISatellitePluginChannel` /
  `SatelliteProtocol`) and hosted out-of-process there.
- **Startup** — `PluginLifetime` clears pending deletes, then loads every persisted net10 plugin
  (`LoadPersistedAsync`) — registry-driven only, nothing is auto-discovered from disk; persisted
  real-legacy plugins are replayed to the satellite (`ReplayLegacyToSatellite`) once it is
  online.
- **Re-locate** — `ReplayLegacyToSatellite` returns the real-legacy records whose entry DLL no longer
  exists (install-by-reference sources can move). The shell shows those as a *Files missing* roster row
  with a *Locate…* action; picking the DLL at its new home calls `PluginInstaller.RelinkLegacy`, which
  re-classifies the pick, verifies it is the same plugin, re-points the registry record, and reloads it
  on the satellite.
- **Uninstall** — a Plugins-roster *Remove* calls `UninstallAsync`: it retracts the plugin's UI, unloads
  the live instance (`PluginManager.UnloadAsync` — dispose → force-close its `ScopedPluginRegistry`
  registrations so no delegate pins the ALC → `UnloadAndWait` + GC pump; or a satellite `UNLOADPLUGIN`
  round-trip for real-legacy), deletes the install folder, and drops the record. Folders still locked
  (collectible ALC / CEF) are marked `PendingDelete` and removed on the next launch.

Honest limit unchanged from [Locked decisions](#locked-decisions): a hard native crash still takes the
host; hot-unload is cooperative (quarantine + collectible ALC), not a fault boundary.

### SDK distribution — the modern contract on GitHub NuGet

The **only** published SDK is the modern contract; nothing that impersonates ACT is ever published.

- **`Fct.Abstractions`** (core contract) and **`Fct.Abstractions.UI`** (Avalonia surfaces + theme
  tokens) ship as versioned **NuGet packages on GitHub Packages**, with the **major version *= the
  contract version*** the host gates — so an author's `PackageReference` version and the manifest
  `contract` string are the same number, closing today's hand-declared-string gap. These references are
  marked **not copy-local** (the host shares them via manifest-declared sharing, work item 8).
- **The ACT/SDK facades (`Advanced Combat Tracker`, `FFXIV_ACT_Plugin.Common`) are never published and
  never built against as our SDK.** They exist only so a legacy plugin *loads* — they satisfy the
  plugin's own existing ACT/SDK references by assembly identity at runtime. A recompiled legacy plugin
  compiles against the real ACT/SDK references it already has; our facade substitutes at load time. We
  do not redistribute or expose ACT's contracts.
- **Satellite face needs no SDK** — those are the *unmodified* net48 binaries loaded from the user's ACT
  install; they reference the real ACT/SDK, never ours.

## What we must do — the work list

The remaining work, read straight off the [Pipe inventory](#pipe-inventory--every-pipe-three-faces):
the missing pipes and faces, ordered by how directly a tested plugin is blocked. Everything is additive
to the contract or new host/shim/bridge wiring — no redesign, and nothing that makes the host parse or
know a plugin. **None of these is optional or deferrable** — each is a real inter-plugin data path, so
rule 1 requires it.

1. **Raw-packet pipe on net10 (all three faces).** ✅ **Built.** The satellite's `BridgeForwarder`
   taps the SDK `NetworkReceived`/`NetworkSent` firehose and forwards each packet as a
   `RawPacketReceived` `GameEvent` (bytes base64 on the wire); the host bus carries it opt-in
   (`GameEventFilter.IncludeRawPackets`); the capability-gated `RawPacketSource` republishes it as the
   modern `IRawPacketSource` (`IPluginHost.RawPackets`, `raw` capability), and the shim's
   `DataSubscriptionAdapter` routes it by direction to `NetworkReceived`/`NetworkSent` — so
   OverlayPlugin's `RegisterNetworkParser` binds unchanged. The pipe carries the **complete** stream —
   capability-gating controls whether a plugin *opts in* to the load, never a lossy sample at the source
   (rule 1). Pairs with the already-built write-back (`IRawLogLineEmitter`, [G4](#contract-gaps-tracked)).
2. **Process-lifecycle pipe on net10.** The satellite forwards game process attach/detach (PID) as a
   typed event; the host exposes it; the shim's `ProcessChanged` + `GetCurrentFFXIVProcess` resolve from
   it. Low-rate. OverlayPlugin's memory processors / overlay hider / FateWatcher need it to migrate.
3. **Actor side-channel state pipe** (statuses/enmity/target-of-target/focus/hover/reliable in-combat).
   **The satellite forwards these onto the bus** (decision below), folded into the `Actor` superset
   fields + snapshot `Focus`/`Hover` the contract already carries. This is Hojoring's OP + Sharlayan
   side-channel promoted to a first-class host pipe — a migrated Hojoring drops those side-channels and
   reads the snapshot. Native producers may supersede the satellite later with no contract change.
4. **Resource catalog pipe.** **Filled by plugins** (decision below): the parser supplies
   skill/zone/world via `GetResourceDictionary`; a first-party **resource-provider plugin** supplies the
   status/buff table the SDK leaves empty. The host exposes `IResourceCatalog` / the shim
   `GetResourceDictionary` as a pure pipe and owns no game data. Deliver that provider plugin + wire the
   pipe. Unblocks Hojoring's name lookups.
5. **`ParsedLogLine` shim event.** Declare it on the shim `IDataSubscription` and fire it off the
   existing `RawLogLine` firehose so Triggernometry's `BridgeFFXIV` reflection binds. (Hojoring already
   works — it reconstructs the parsed form itself off `OnLogLineRead` + `detectedType`, both built.)
6. **Per-tick encounter refresh** into `IEncounterService.Active`. ✅ **Built** — `Fct.Engine`:
   `ModernEncounterEngine` aggregates the forwarded `CombatSwing`/lifecycle stream through the shared
   `Fct.Aggregation` graph and `EngineEncounterService` republishes `Active` on a 500 ms projection
   tick with ACT's exact idle-end clock; native consumers see live DPS computed **at the host**, not
   forwarded satellite aggregates.
7. **D8 — WinForms `TabPage` embedding** (shim UI face). Fully specced in
   [§D8](#d8--winforms-tabpage-embedding-planned-next-slice); the last piece for a recompiled-but-still-
   WinForms plugin to show its settings tab.
8. **Manifest-declared shared assemblies.** Move the loader off naming `Fct.Compat.Shim` + its two
   facades: share the fixed contract set (`Fct.Abstractions*`/`Avalonia.*`) plus whatever the manifest
   declares, so the host has zero name-knowledge of the shim and stays a generic router/loader (rule 2).

**Host & authoring readiness** — not data pipes, but the machinery to *build, install, and surface*
plugins across the three faces (goal 2). Install/load/unload, the UI contribution surface, and manifest
metadata are built (items 9, 11, 13); what remains here is the stable theme seam and the published SDK
feed:

9. **Modern UI contribution surface.** ✅ **Built.** `Fct.App` implements `IUiHost` (`PluginUiCoordinator`
   + `AvaloniaUiDispatcher`), discovers `IUiContributor` on loaded plugins gated by the manifest `"ui"`
   capability, and calls `RegisterUi` on the UI thread once the shell is live (`MainWindow.OnOpened`).
   A contributed settings page renders in the Plugins config bay (alongside the legacy embeds);
   `RevealPage` + `AddCornerControl`/`RemoveCornerControl` are wired end-to-end. Details:
   [Wiring & readiness](#wiring--readiness-modern-ui--built).
10. **Semantic theme token contract.** ✅ **Built.** `Fct.Abstractions.UI` ships `FctTokens` (brush/font
    resource keys), `FctStyleClasses` (blessed `fct-*` classes), and `FctMetrics` (layout doubles); the
    shell aliases the tokens onto the Evercold palette in `App.axaml` and defines the `fct-*` classes over
    them in `Styles/PluginTokens.axaml`. Plugins bind dynamically, so the shell can restyle (incl. a light
    variant via `ThemeDictionaries`) without breaking them. Catalog: [`UI-TOKENS.md`](UI-TOKENS.md);
    reference consumer `samples/Fct.SamplePlugin`; drift-guarded by `TokenContractTests`.
11. **Plugin lifecycle — install / uninstall / hot-reload.** ✅ **Built.** `PluginInstaller` is the single
    add/remove entry point: it accepts a folder **or a `.zip`**, classifies the payload across the three
    faces (`PluginClassifier` → `LoadKind` native / recompiled-shim / real-legacy), copies it into the
    per-plugin install root, routes the load (in-process collectible ALC for net10, or a satellite
    `LOADPLUGIN` frame for real-legacy), and records it in a persisted registry that re-loads on the next
    launch. Uninstall is the symmetric teardown (unload live → delete folder → drop the record), with a
    deferred-delete list for folders still locked by a collectible ALC / CEF. Details:
    [Install, discover, update, remove](#install-discover-update-remove--full-lifecycle-built).
12. **SDK NuGet packaging.** **Partial — local pack only.** `dotnet run --project build` packs
    `Fct.Abstractions` + `Fct.Abstractions.UI` (major = contract version, `1.0.0` today) into
    `dist\<mode>\packages\*.nupkg` alongside the host/satellite publish (`build/Build.cs`); `dist/` is
    git-ignored, so this is a **local-only** feed, never pushed anywhere. Publishing to **GitHub
    Packages** is still pending. The in-repo samples keep using `ProjectReference` (they build against
    source, not the package); an external plugin repo (e.g. the Discord-Triggers native port) adds
    `dist\debug\packages` as a local NuGet folder source instead of vendoring DLLs.
13. **Manifest metadata + UI opt-in.** ✅ **Built.** `plugin.json` carries optional `name`/`description`/
    `author` (projected onto the roster's `PluginInfo`/`PluginViewModel`); the `"ui"` capability gates
    `RegisterUi` discovery (work item 9) — declaring it without implementing `IUiContributor` is a
    logged no-op, not an error.

### Decisions recorded

- **Resource catalog — filled by plugins, not the host.** `IResourceCatalog` is a pure pipe. The parser
  fills skill/zone/world (as today); a first-party **resource-provider plugin** fills the status/buff
  table the SDK leaves empty — Hojoring's private XIVAPI/CSV side-channel promoted to a shared pipe. The
  host owns no name tables, keeping rule 1 intact. (Rejected: host-owns-tables and proxy-only — the
  former makes the host own a patch-drifting game asset; the latter leaves the buff table empty and so
  fails to accommodate Hojoring, violating goal 2.)
- **Actor side-channel state — satellites forward for v1.** The parser and OverlayPlugin satellites
  host the real plugins with memory/network access, so their facade taps are the v1 producers of
  statuses/enmity/target/focus/hover/in-combat onto the bus (each satellite forwards what **its**
  plugin produces; the host fans out). A dedicated native memory-reader plugin may replace them
  later; because the data lands in the same `Actor`/snapshot fields, that swap is a producer change
  with **no contract change**.
- **Packaging & lifecycle — loose folder + full lifecycle.** The installed form of a plugin is a loose
  folder (a `.zip` is accepted only as an install source, extracted on the way in); no signing in v1. The
  host provides runtime install / hot-load / unload / reload and in-app uninstall over the collectible
  ALC (net10) and the satellite channel (real-legacy). (Rejected: a stored archive/signing format — loose
  folder is the v1 format; start-time-only loading — wastes the collectible ALC already built.)
- **SDK distribution — modern contract on GitHub NuGet; facades never published.** Only
  `Fct.Abstractions` / `Fct.Abstractions.UI` ship (GitHub Packages), package major = gated contract
  version. The ACT-impersonating facades are **never** published or built against as our SDK — they only
  substitute a legacy plugin's existing ACT references by identity at load time. We do not expose ACT's
  contracts. (Rejected: ProjectReference-only — external authors get no artifact; publishing the facades
  — we never redistribute ACT's identity.)
- **Modern plugin UI — ACT-grounded surfaces only.** The host wires `IUiHost` + `IUiContributor` for
  the surfaces real plugins use: a settings page (the `TabPage` replacement, in the Plugins config bay)
  plus Triggernometry's `RevealPage`/`AddCornerControl`/`RemoveCornerControl`. The speculative
  `AddNavPage`/`AddDashboardWidget`/`AddStatusItem` (and `UiWidget`/`UiWidgetSize`/`UiStatusItem`) had no
  ACT precedent and no tested-plugin consumer and are **removed from `Fct.Abstractions.UI`**. v1 adds no
  new UI concepts.
- **Theme sharing — semantic token contract.** `Fct.Abstractions.UI` exposes documented, stable theme
  tokens (brushes/typography/spacing + style classes) mapped onto the shell palette; plugins bind the
  tokens, never the shell's internal keys. (Rejected: document-internal-keys — couples plugins to keys
  that can't change; base-style-dict-only — no token seam for custom controls.)

- **Legacy log-routing seam — route to `ILogger`.** The `_iocContainer` a recompiled
  OverlayPlugin/Hojoring reflects for its own log routing resolves to a container whose logger routes to
  `IPluginHost.Logger`/`ILogger` — the same taxonomy as every other log source, no ACT logging internals
  impersonated.
- **Faulted-plugin quarantine — session-only, user-managed.** A plugin that faults init or the watchdog
  is quarantined for the session and surfaced in the Plugins roster; there is **no automatic persisted
  blocklist**. Re-enabling, updating, or removing it is a user action (the loose-folder + full-lifecycle
  model), not a host policy.
- **Player attribute block — dropped from v1.** No tested plugin consumes `PlayerStatsChanged` or the
  SDK `Player` attribute block (STR/DEX/derived stats); HP/MP/GP/CP/level/job flow via the `Combatant`
  projection. `Player` carries `JobID` only — by decision, not as a gap. (Re-add only if a real consumer
  appears.)
