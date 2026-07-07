# net10 Plugin API — Design & Contract

The forward-facing plugin SDK: the typed, future-facing contract new plugins target, and the
compat adapter that lets ACT-era plugins migrate onto it. This is the **forward surface** sketched
in [`ARCHITECTURE.md`](ARCHITECTURE.md) §7, specified in full.

It is the counterpart to the *backward* surface: [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) and
[`DATA-FLOW.md`](DATA-FLOW.md) document the legacy ACT host surface the net48 satellites impersonate
(one satellite per plugin package — [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)) to run the five
plugins **unmodified**. This document is the **net10 contract** they migrate *to*.

> Start with [The model](#the-model--one-set-of-pipes-three-faces) and the
> [Pipe inventory](#pipe-inventory--every-pipe-three-faces) — they frame every interface below.

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
4. **The only host intelligence is the ACT-mirror aggregation engine** — the **encounter/DPS rollup**
   (`IEncounterService` + `ExportVariables`), the one ACT behavior we reproduce, as a simplification
   consumers already expect. The combatant/zone/party **repository snapshot** (`IGameSnapshot`) is
   *not* a second reproduced behavior: those tables live on the parser (`DataRepository`), not on ACT,
   so the host merely folds the forwarded, already-parsed stream into a queryable snapshot — pure host
   routing. Everything else is pure transport.

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
| **Satellite** | net48 | real ACT/SDK, unmodified | `Fct.LegacyHost` — **one satellite process per plugin package**, each with its private ACT facade; produced data crosses to the net10 bus via the [bridge forwarder](#the-bridge-data-forwarder), consumed data is fanned back and projected by the facade ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)) |
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

Every pipe the five tested plugins consume, its shape on each face, and what is present vs. missing.
"Missing" means a face a tested plugin will need once it migrates — not an unused surface.

| Pipe | Modern face | Compat-shim face | Satellite face | Coverage |
|---|---|---|---|---|
| **Raw log line** | `RawLogLine` event | `Before/OnLogLineRead` + `IDataSubscription.LogLine` | ACT `OnLogLineRead` (bridged) | present; shim `ParsedLogLine` event pending |
| **Repository snapshot** (combatant/zone/party/player) | `IGameSnapshot` | `IDataRepository.Get*` + state `IDataSubscription` events | SDK `IDataRepository` (bridged) | present; name tables empty |
| **Actor side-channel state** (statuses/enmity/target-of-target/focus/hover/reliable in-combat) | `Actor` superset fields + snapshot `Focus`/`Hover` | consumed via the snapshot | today Hojoring reads OverlayPlugin + Sharlayan directly | contract fields present; source = satellite forwards (pending) |
| **Encounter / DPS** (+`ExportVariables`) | `IEncounterService` backed by `Fct.Engine` (the aggregation authority, fed by the forwarded `CombatSwing`/lifecycle stream; live `Active` on a 500 ms projection tick) | `ActiveZone.ActiveEncounter` + `Fct.Engine.EncounterProjector` | ACT `EncounterData`/`CombatantData` off the satellite's engine replica | present (host engine live) |
| **Action event** | `ActionEffect` (+ typed records) | via events | ACT `AfterCombatAction` (bridged) | present; status/cast/death/hp → **RawLogLine only** (note below) |
| **Audio** | `IAudioOutput` | `TTS`/`PlaySound` + `PlayTts`/`PlaySound` slots | ACT `PlayTts`/`PlaySound` | present |
| **Registry / peer** | `IPluginRegistry` | `RegisterNamedCallback`/`InvokeNamedCallback` | ACT named callbacks | present |
| **Raw packets** | `IRawPacketSource` + `RawLogLines.Emit` | `RegisterNetworkParser` / `NetworkReceived` | SDK `NetworkReceived` (in-satellite) | present (satellite forwards `RawPacketReceived` → bus → `IRawPacketSource` + shim `NetworkReceived`/`Sent`; read + write-back) |
| **Process lifecycle** | process event + handle | `ProcessChanged` + `GetCurrentFFXIVProcess` | SDK `ProcessChanged` | **missing on net10 (all faces)** |
| **Resource catalog** (names) | `IResourceCatalog` | `GetResourceDictionary` | parser tables + a resource-provider plugin | pipe filled by plugins; status/buff table + producer pending |
| **Host services** (storage/logger/clock) | `IPluginHost` | `AppDataFolder`/`WriteExceptionLog`/… | ACT globals | present |
| **UI** | `IUiContributor` (Avalonia settings page + reveal/corner-control) | WinForms `TabPage` | WinForms `TabPage` in `Fct.LegacyHost` | modern: wired (`PluginUiCoordinator`); satellite HWND present |

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
| DPS / encounter reader | OP | `ActiveZone.ActiveEncounter` + scrape both `ExportVariables` per tick | `IEncounterService` typed snapshot + `ExportVariables` bag | ✅ CONFIRMED |
| Combat-state driver | Trig | `SetEncounter`/`EndCombat`/`InCombat`; text export; append line | `IEncounterService` (read+write) | ✅ CONFIRMED |
| Audio sink provider | Disc, Hojo | **hijack** global `PlayTtsMethod`/`PlaySoundMethod` (save/restore, route-instead-of) | `IAudioOutput.RegisterSink` | ✅ CONFIRMED |
| Audio producer | Trig, OP, Hojo | call `TTS()`/`PlaySound()` | `IAudioOutput.Speak`/`Play` | ✅ CONFIRMED |
| Raw packets | OP | `RegisterNetworkParser` → bytes → Machina → synthetic lines 256+ | `IRawPacketSource` (capability-gated) | ✅ CONFIRMED — read + write-back |
| Host services | all | `PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, Trig named callbacks | `IPluginHost` (Storage/Logger/Registry) | ✅ CONFIRMED |
| UI | all | one WinForms `TabPage` via `InitPlugin` | `IUiContributor` (Avalonia surfaces) | ✅ CONFIRMED |

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
   dispatch lock is **per event type**, not global (`EventDispatcher.cs:128-141`, which try/catches
   each receiver); the genuinely unguarded spots are subscribe-time cached-state replay
   (`EventDispatcher.cs:71-75`) and the raw-packet Machina handlers on the `NetworkReceived` thread
   (`NetworkParser.cs:50-73`, `LineBaseCustom.cs:69-87`). The bounded-ring, single-dispatch-thread,
   per-subscriber-try/catch, drop-oldest model (`RingBufferDataSubscription`, generalized by the host's
   `GameEventBus`) closes all of these uniformly.

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
mini plugin-host — see peer discovery below.)

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

> The current scaffold ships a representative event subset; the hierarchy is completed against the full
> taxonomy (42 native types + the custom range) additively, so consumers already written against it
> keep working.

### Game state — free-threaded immutable snapshot

`IGameSnapshot` returns immutable records callable from any thread (no caller-side `lock`, unlike the
legacy `lock (cd.Lock)`). `Actor` (`Actors.cs`) is the **superset** model — adding `Statuses`,
`Enmity`, `TargetOfTargetId`, and `InCombat` on top of the SDK fields, and the snapshot itself
carries `Focus`/`Hover` (`Session.cs:51-52`) — together closing Hojoring's Sharlayan/OverlayPlugin
side-channels. `IResourceCatalog` is the **pipe** for typed, all-locale name tables (skill/status/zone/world/item);
the host owns no tables and **plugins fill it** — the parser supplies skill/zone/world via
`GetResourceDictionary`, and a first-party resource-provider plugin supplies the status/buff table the
SDK leaves empty (the gap Hojoring patches with a private XIVAPI/CSV side-channel today, promoted to a
shared pipe).

### Encounter / combat-state

`IEncounterService` exposes `InCombat`, `StartCombat`/`EndCombat` (Trig's driver at
`ProxyPlugin.cs:323-334`), `Active`/`Last` encounter snapshots, `ExportText`, and `AppendLogLine`.
`StartCombat(string? title = null, string? zone = null)` carries Trig's player-name-as-title/zone
label. `EncounterSnapshot`/`CombatantMetrics` provide typed per-combatant metrics — the ergonomic path
for **native** consumers, replacing OverlayPlugin's whole-`ExportVariables`-dictionary scrape per tick
(`MiniParseEventSource.cs:321-360,385-414`).

> **cactbot fidelity requires more than the typed fields.** OverlayPlugin forwards the *entire*
> opaque `CombatantData`/`EncounterData.ExportVariables` dictionary verbatim; the fixed metric records
> cannot round-trip every key cactbot expects, so `EncounterSnapshot`/`CombatantMetrics` also carry an
> init-only `IReadOnlyDictionary<string,string> ExportVariables` bag (empty default) that the shim
> populates. OP's per-swing `overHeal`/`damageShield`/`absorbHeal` are typed fields
> (`Overheal`/`ShieldedDamage`/`Absorbed`) on `CombatantMetrics`.

### Audio — multi-sink

`IAudioOutput` separates producers (`Speak`/`Play`) from sink providers (`RegisterSink`, returns
`IDisposable`). Replaces the single mutable delegate slot that forced Discord-Triggers vs TTSYukkuri
into last-writer-wins. `IAudioSink` is async/fire-and-return so the host never blocks a producer on a
slow out-of-process bridge.

> Discord-Triggers and TTSYukkuri today *save the prior delegate and replace the slot*
> (`DiscordTriggersView.xaml.cs:178-185`; TTSYukkuri `PluginCore.cs:174-206`) — i.e. route audio
> **instead of** ACT's built-in speakers. The additive fan-out model plays on all sinks, so a
> **terminal-sink** flag reproduces that exclusive routing (`RegisterSink(sink, priority, terminal)`) —
> a route-instead-of sink suppresses lower-priority sinks. Per-request channel/sync options ride on
> `AudioOptions` (an `AudioChannel {Both,Left,Right}` enum + init-only `Channel`/`Synchronous`).

### Registry, peer events, raw packets

`IPluginRegistry`: enumerate peers (typed metadata), Triggernometry-style
`RegisterCallback`/`InvokeCallback`, and `Publish<T>`/`Subscribe<T>` so a plugin can put its own typed
events on the bus for others. `RegisterCallback(string name, Action<object?> cb, object? owner = null,
bool allowDuplicate = false)` carries the owner/duplicate-name semantics Trig needs (duplicate names
rejected unless opted in; `IDisposable` unregister), and `TryGetPeerService<T>(string pluginId, out T
service)` hands a live, version-gated peer *handle* to consumers that need more than metadata.

`IRawPacketSource` is the single opt-in, capability-gated raw-packet **read** hatch (the typed path
stays opcode-free for everyone else). OverlayPlugin also turns packets *back into* synthetic 256+ log
lines (`LineBaseCustom.cs:80-86` → `FFXIVRepository.cs:493-515`); that write-back round-trip is
`IRawLogLineEmitter.Emit` on `IPluginHost.RawLogLines` (capability-gated), which fans a synthetic 256+
line onto the event bus.

## Threading, performance & fault model (the explicit contract)

- **Event delivery** — each subscription owns a **bounded queue drained in order**; each handler
  call is fault-guarded (a throw can't kill the stream or starve peers); a slow/backed-up consumer
  gets **drop-oldest + a dropped counter** (never stalls the producer or other plugins). This closes
  OP's per-type dispatch stall *and* its unguarded subscribe-time replay uniformly (see finding 3
  above).
- **Snapshots/queries** are **free-threaded, immutable** — safe from any background poll thread.
- **Watchdog** — a handler over its time budget is logged; a repeatedly-throwing/hanging plugin is
  **quarantined** (unsubscribed) and may be **unloaded** (collectible ALC). Quarantine is
  **session-only and user-managed** — the faulted plugin is surfaced in the Plugins roster; there is
  no automatic persisted blocklist, and re-enabling / updating / removing it is a user action.
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
  `AddCornerControl`/`RemoveCornerControl`.

The **only contribution surfaces are the ACT-grounded ones** — a settings page (the `TabPage`
replacement, rendered in the Plugins config bay next to the legacy embeds) plus Triggernometry's
`RevealPage` and `AddCornerControl`/`RemoveCornerControl`; no nav-page / dashboard-widget / status-item
surfaces (they had no ACT precedent or tested-plugin consumer).

### Wiring & readiness (modern UI)

The host runtime (`Fct.Host`) owns the `IUiHost` surface via `PluginUiCoordinator` — the single owner
of every contributed surface — handing each plugin a per-plugin handle that attributes calls back to
its owning plugin id; the shell (`Fct.App`) supplies `AvaloniaUiDispatcher` (the `IUiDispatcher` over
`Avalonia.Threading.Dispatcher.UIThread`). `IPluginHost` itself gains no UI member —
`IUiContributor.RegisterUi` is a separate call the coordinator drives. Discovery is gated on the
manifest `"ui"` capability (checked before the `is IUiContributor` probe); because plugin
`InitializeAsync` runs before Avalonia starts, `RegisterUi` is deferred to `MainWindow.OnOpened`, once
the window is on screen. A contributed settings page renders in the Plugins config bay via
`PluginSurfaceView` (lazy, cached); `RevealPage` navigates the shell to the owning row, and corner
controls render as a transient top-right overlay. A throwing `RegisterUi`/`CreateView` is caught
per-plugin (inline error placeholder attributed to the plugin, peers still register), and hot-unload
retracts the plugin's page and corner controls so nothing is orphaned. `samples/Fct.SamplePlugin` is
the reference `IUiContributor`.

### Theme — a semantic token contract

A plugin `Control` inherits the shell's Avalonia theme automatically once attached: Avalonia is shared
to the default ALC, so a plugin's `Control` **is** the shell's `Control` type, and application-level
styles/resources (the shell's `FluentTheme` + palette) flow into any attached subtree. But the shell's
actual resource keys (its internal "Evercold" palette, fonts, style classes) are **implementation, not
contract** — so plugins do not bind them directly. Instead `Fct.Abstractions.UI` exposes a **stable
semantic token contract**, and the shell maps it onto its internal palette. This is the theming
equivalent of the typed data contract — a documented seam, never the internal implementation.

Full catalog in [`UI-TOKENS.md`](UI-TOKENS.md). `Fct.Abstractions.UI` ships three constant
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
surface the plugins compile against, **implemented entirely over the modern host**. The legacy symbols
a recompiled plugin binds map onto the modern surface as:

| Legacy symbol (recompiled against shim) | Backed by |
|---|---|
| `ActGlobals.oFormActMain` (POCO hub, not a `Form`) | `IPluginHost` |
| `IActPluginV1.InitPlugin/DeInitPlugin` | `IPlugin.InitializeAsync/DisposeAsync` (via `LegacyPluginHost`) |
| `Before/OnLogLineRead` | `IGameEventStream` → `RawLogLine` |
| `RegisterNamedCallback`/`InvokeNamedCallback` (Trig peer interop) | `IPluginRegistry.RegisterCallback`/`InvokeCallback` |
| `PlayTtsMethod`/`PlaySoundMethod` delegate slots | setting a delegate registers a terminal `IAudioSink`; `TTS()`/`PlaySound()` → `IAudioOutput` |
| `PluginGetSelfData`/`AppDataFolder`/`WriteExceptionLog` | `IPluginStorage`/`ILogger` |
| `AddCombatAction`/`SetEncounter`/`EndCombat`/`InCombat` | shared aggregation engine (`ActiveZone.ActiveEncounter`) + mirrored onto `IEncounterService` |
| `CombatantData`/`EncounterData.ExportVariables` (opaque dict cactbot reads) | shared engine's registered formatters, projected onto `EncounterSnapshot`/`CombatantMetrics` typed fields + the `ExportVariables` bag |
| `IDataSubscription` (11 events: NetworkReceived/Sent, CombatantAdded/Removed, PrimaryPlayerChanged, ZoneChanged, PlayerStatsChanged, PartyListChanged, LogLine, ParsedLogLine, ProcessChanged) | `IGameEventStream` + `IRawPacketSource` mapped to SDK delegate shapes |
| `IDataRepository.Get*` | `IGameSnapshot` + `IResourceCatalog` |
| `Combatant`/`Player`/`NetworkBuff` | projected from `Actor`/`StatusEffect` (lossless: CP/GP/world/order, refresh/timestamp) |
| `RegisterNetworkParser` / custom lines 256+ | `IRawPacketSource` (read) + `IRawLogLineEmitter` synthetic-line emit |
| WinForms `TabPage` | real WinForms TabPage embedded via Avalonia `NativeControlHost` |

Of the `IDataSubscription` events, 8 are mapped (`LogLine`/`ZoneChanged`/`PartyListChanged`/
`PrimaryPlayerChanged`/`CombatantAdded`/`CombatantRemoved` from the bus, `NetworkReceived`/`NetworkSent`
from the raw-packet firehose); `ParsedLogLine`/`ProcessChanged` await their source pipe, and
`PlayerStatsChanged` is dropped by decision (no tested consumer). `Player` carries `JobID` only — the
modern model has no attribute block.

The shim's hub *is* the modern API underneath, so a plugin can use both at once — the basis for
incremental migration (swap one call at a time, then drop the shim).

### Per-plugin migration outlook

- **Triggernometry — easiest, but not "trivial".** `RealPlugin` already never references ACT (grep
  count 0; `mainform` is typed `System.Windows.Forms.Form`, `RealPlugin.cs:366`) — it calls **19
  injected delegate hooks** via `ProxyPlugin` (`ProxyPlugin.cs:145-163`). Re-point the proxy at the
  shim hub; the regex engine consumes `RawLogLine` unchanged. The recompile is mechanical but the
  surface is real: 19 hooks + reflected ACT members (`defaultTextFormat`, `CornerControlAdd/Remove`,
  `tc1`) must all be re-homed; named callbacks use the owner-tagged registry.
- **Discord-Triggers — easy.** Pure audio sink, consumes no combat data (grep for
  LogLineRead/Encounter/CombatAction = 0). Its VM is already ACT-free and awaitable
  (`DiscordTriggersViewModel.cs:596-615`); only ~6 lines of delegate-swap glue touch ACT. Becomes an
  `IAudioSink` registration; its out-of-process Node bridge is unaffected. Terminal audio-sink routing
  preserves the route-instead-of-speakers behavior.
- **Hojoring — high value.** Recompile drops Sharlayan + OverlayPlugin side-channels because `Actor`
  carries status/enmity/in-combat and the snapshot carries focus/hover. Its poll-and-diff
  (`XIVPluginHelper.cs:904-912`) maps onto `IGameSnapshot`, or it adopts typed events and stops
  diffing. TTSYukkuri is both an audio producer and a sink provider.
- **OverlayPlugin — the last to leave its satellite.** Its `IDataSubscription`/`ExportVariables` use
  recompiles fine, but **CefSharp is net48-only** (pinned `95.7.141`, `HtmlRenderer.csproj:45-52`), so
  OP's CEF/Fleck rendering stays in a net48 satellite **until CefSharp ships a modern-.NET build it
  can adopt** — the pin is the blocker, not the design. It is **its own** satellite, fully isolated
  from the parser, fed by the host-routed streams through the facade's synthetic parser stand-in
  ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P8 — the `overlay` consumer satellite). It is also itself a
  mini plugin-host (`IOverlayAddonV2.Init`, `PluginMain.cs:493-504`). The recompile path serves managed
  consumers; OP is why a satellite persists **longest**, not forever — when the CefSharp pin lifts it
  migrates in and the net48 tier can retire. The managed-consumer surface for cactbot fidelity is
  present.
- **FFXIV_ACT_Plugin (the parser) — owner-driven, not ours to do.** The parser is on the same ladder
  as every other plugin: its *owner* migrates its source to net10 (Machina + memory scan + opcode
  decode recompiled), at which point it loads in-process as the producer and the parser satellite
  retires. What stays fixed is **we never reimplement or port its parsing** — that is the owner's
  code, on whatever runtime the owner targets. Until a net10 build exists it runs unmodified in the
  net48 parser satellite; the "sole parser" role and the zero-opcode-maintenance guarantee are
  unaffected by which runtime hosts it. This is the one migration we cannot drive ourselves, so it
  gates the final deletion of the net48 tier.

## Testing the contract

The legacy↔native contract is exercised headlessly by the flow-test suite (`Fct.FlowTests` on the
`Fct.Abstractions.Testing` fakes + the minimal `ShimStub` seam — zero satellite, zero CEF, zero live
game); the real shim's own seams (`ExportVariables` cross-TFM parity, the `IDataSubscription` event
map, the `IDataRepository`/`Combatant` projection, reflection-discovery) are covered by
`Fct.Compat.Shim.Tests`; and `MasterSwing` aggregation parity by `tools/mass-compare`. Flow tests
assert *routing*, not *parse/aggregation correctness*. See [`TESTING.md`](TESTING.md) for the
parity/oracle model.

## Versioning

`Fct.Abstractions` is **semver, additive-only**, with a `contract` version the host gates against the
manifest. New `GameEvent` records are additive (switch-and-ignore-unknown); new fields are init-only
additions — every surface addition is an additive edit.

## The net10 host & ALC loader

`Fct.Host` (a headless net10 class library the shell references) provides the production host services
behind `IPluginHost`: `GameEventBus` (a real `IGameEventStream` — bounded per-subscription channel
drained by a background pump, drop-oldest backpressure, fault-guarded handlers, generalizing
`RingBufferDataSubscription`) with an in-process `IGameEventSink` producer seam; `RegistryService`,
`AudioService` (priority fan-out + terminal sink), `EncounterService`, disk-backed `PluginStorage`,
`SystemClock`, the capability-gated `RawLogLineEmitter`, and the per-plugin `PluginHost : IPluginHost`.
`AddFctHostServices` registers the whole runtime (the shell adds only its own pieces: `MainViewModel`,
`ISatelliteNotificationText`, `LegacyPluginHostFactory`, `AvaloniaUiDispatcher`).

- **Live snapshot aggregator** (`GameSnapshotAggregator`): a hosted service that subscribes to the
  state events on `GameEventBus` (raw log lines filtered out), folds
  `CombatantAdded`/`CombatantRemoved`/`HpUpdated`/`ZoneChanged`/`PartyChanged`/`PrimaryPlayerChanged`
  into an immutable `IGameSnapshot`, and publishes it via `GameSnapshotProvider` — so
  `IGameSession.Snapshot()` (and the shim's `IDataRepository`) reads live actor/zone/party/player state
  off the forwarded stream. `Target`/`Focus`/`Hover`, the actor side-channel fields, and the resource
  catalog are not sourced yet — their producers are the satellite forwards and a resource-provider
  plugin.
- **ALC loader** (`Fct.Host/Plugins/`): `PluginManifest` (`plugin.json`, read without loading the
  assembly) + `HostContract` major-version gate; `PluginLoadContext` (collectible — `Fct.Abstractions`/
  `Fct.Abstractions.UI`/`Microsoft.Extensions.Logging.Abstractions`/`Avalonia.*`, plus the manifest-
  declared compat shim + its two facades, shared to the default context, everything else private via
  `AssemblyDependencyResolver`); `PluginManager` (discover → gate → load → time-boxed fault-guarded
  init → quarantine/unload) driven by the `PluginLifetime` hosted service.
- **Sample plugin** (`samples/Fct.SamplePlugin`) loads end-to-end and exercises events/snapshot/
  registry/audio/storage/raw-write-back.

## The bridge data forwarder

Live game data reaches the net10 bus over the satellite→host pipe:

- **Satellite (`Fct.LegacyHost/BridgeForwarder.cs`)** — reflects `ActGlobals.oFormActMain.ActPlugins`,
  subscribes the SDK + ACT-hub taps, **projects each callback into a typed `GameEvent`**, and ships it
  over a bounded ring (drop-oldest + a dropped counter) that keeps pipe I/O off the SDK dispatch/UI
  threads on the high-rate firehose.
- **Wire (`GameEventFrame` in `Fct.Bridge.Contracts`, referenced by both processes)** — a tab-delimited,
  backslash-escaped `EVT` frame alongside `LOG`, **no JSON**. `Sequence` is not on the wire — the host
  re-stamps each decoded event from its own `IGameEventSink.NextSequence()` so the bus keeps one
  coherent per-session ordering.
- **Host (`Fct.Host/SatelliteHost.cs`)** — the read loop decodes `EVT` frames and calls
  `IGameEventSink.Emit`, so bridge events flow to every subscription exactly like any in-process source.

**What the forwarder can carry is bounded by the sole-parser directive** — it may only project data the
SDK/ACT hub exposes (post-parse aggregates, or the untouched raw firehose), never re-parse a log line.
Forwarded today: `RawLogLine` (the SDK `LogLine` firehose that Trig/cactbot regex over), `ZoneChanged`,
`PartyChanged`, `PrimaryPlayerChanged`, `CombatantAdded`/`CombatantRemoved` (`Combatant`→`Actor`,
lossless for DoL/DoH), `ActionEffect` (from the ACT hub's post-aggregation `AfterCombatAction`),
`RawPacketReceived` (the SDK `NetworkReceived`/`NetworkSent` firehose — raw bytes carried base64 on the
wire, never decoded), and the **pre-aggregation feed that makes the host the calculation authority**:
`CombatSwing` (the full `MasterSwing` — every field + `Tags`, raw `Dnum` sentinels, tapped off the
facade's `BeforeCombatAction`) plus the encounter lifecycle
(`SetEncounterRequested`/`ZoneChangeRequested`/`EndCombatRequested`), which `Fct.Engine` folds into the
host's `IEncounterService` truth. Events that exist only as parsed log-line fields — `StatusApplied`/
`Removed`, `Cast*`, `DeathOccurred`, `HpUpdated` — are **not** synthesized; consumers reach them through
the `RawLogLine` firehose, exactly as in ACT today.

The same `GameEventFrame` codec runs **downstream** (host → satellite) in the isolated topology: each
consumer satellite subscribes to the stream set its facade projects (engine-replica swings, log lines,
packets, repository snapshots), with a bounded per-satellite egress queue mirroring the upstream ring.
Build phases: [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) P4–P6.

## The net10 compat shim

The compat shim (the real ACT/SDK re-projection the `ShimStub` seam seeds) ships three assemblies plus
a sample legacy plugin, loaded by the ALC loader:

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
under `compat\` next to the host (`StageCompatShim`), and `CompatRuntime.Enable` hooks
`AssemblyLoadContext.Default.Resolving` to resolve those assemblies from `compat\` (they are not on the
TPA list, so this resolver is what lets the default context *find* them). The staged folder is located
via `AppData.InstallDirectory` (the launched-exe dir, so it resolves under the single-file
self-extracting build).

The host loads a shimmed plugin through the same path as a native one: `PluginManifest` gains an
optional `legacyEntry` (mutually exclusive with `entry`); `PluginManager` routes it through an injected
`LegacyPluginHostFactory` that materializes the shim's `LegacyPluginHost` **reflectively** (so the
loader takes no compile-time shim dependency); `PluginLoadContext.IsShared` routes `Fct.Compat.Shim` +
the two facades to the default ALC so shim and plugin agree on type identity. Hosting recompiled
WinForms plugins in-process makes `Fct.App` a **net10-windows** app.

The encounter driver (`AddCombatAction`/`SetEncounter`/`EndCombat`, `ActiveZone.ActiveEncounter`) runs
over the shared, strong-named `Fct.Aggregation` engine referenced by the host's `Fct.Engine` (the
aggregation authority), the net48 `Fct.Compat.Act`, and the net10 ActFacade, so the aggregation binary
(and the `ExportVariables` bag cactbot reads) is bit-identical across runtimes
(`Fct.Engine.EncounterProjector` maps it plus the typed metrics onto `EncounterSnapshot`/
`CombatantMetrics`). `DataSubscriptionAdapter` projects the `IGameEventStream` onto the SDK delegates a
recompiled plugin binds — `LogLine`←`RawLogLine`, `ZoneChanged`, `PartyListChanged`←`PartyChanged`,
`PrimaryPlayerChanged`, `CombatantAdded`/`CombatantRemoved` (via `CombatantProjector`), plus
`NetworkReceived`/`NetworkSent` routed by direction off `IRawPacketSource`, exposed on the hub via
`FormActMain.DataSubscription`. `DataRepository` answers every `Get*` off `Host.Game.Snapshot()`, with
`CombatantProjector` mapping `Actor`→`Combatant`/`NetworkBuff` and the player's `Actor`→`Player`.
`LegacyPluginHost` publishes a `SyntheticFfxivPlugin` stand-in into `ActPlugins` so
OverlayPlugin/Hojoring discover `DataRepository`/`DataSubscription` by reflection exactly as under real
ACT.

**Honest limits (facts, not stubs):** `Player` carries `JobID` only — the attribute block +
`PlayerStatsChanged` are **dropped by decision** (no tested consumer), not a gap; `GetCurrentFFXIVProcess`
returns null (no net10 game handle — the connection lives in the satellite); `GetResourceDictionary` is
empty until `IResourceCatalog` is sourced.

**Legacy WinForms `TabPage` embedding** surfaces a recompiled plugin's own already-filled WinForms
settings tab in the app's Plugins config bay via an Avalonia `NativeControlHost` (reparenting the tab's
control under the Avalonia parent HWND, the same path the satellite embeds use). `oFormActMain` stays a
POCO, so plugins that reach `Form`-inherited members (`Handle`/`Invoke`/`InitActDone` — OverlayPlugin,
Hojoring) remain satellite-hosted regardless.

## Plugin lifecycle, packaging & SDK distribution

How a plugin is authored, packaged, installed, discovered, updated, and removed — across the three
faces.

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
- **UI opt-in** — a `"ui"` capability that gates whether the host calls `IUiContributor.RegisterUi`;
  declaring it without implementing the interface is a logged no-op, not an error.

The `contract` string is the single host gate (`HostContract.Accepts`, **major-version equality**); with
NuGet SDK distribution it equals the SDK package major the plugin built against.

### Install, discover, update, remove

`PluginInstaller` owns the runtime lifecycle end-to-end over the collectible ALC (net10 faces) and the
satellite channel (real-legacy face); `PluginRegistryStore` persists the installed set to
`%LOCALAPPDATA%\FFXIVCombatTracker\installed-plugins.json` so it survives restarts. `PluginClassifier`
routes each install to a `LoadKind` — `Native` (references `Fct.Abstractions`, implements `IPlugin`),
`RecompiledShim` (references the unsigned `Advanced Combat Tracker` facade / declares `legacyEntry`), or
`RealLegacy` (references `Advanced Combat Tracker` under the real ACT strong-name token
`a946b61e93d97868`) — manifest-first, else metadata-only reflection with a `MetadataLoadContext`.
Native/shim plugins are copied into `installed-plugins\<id>\` and loaded in-process (fresh collectible
ALC, contract gate, time-boxed fault-guarded init, quarantine on fault); real-legacy plugins install
**by reference** (loaded in place from where the user picked them — zips excepted, always copied) and
are sent to their **package** satellite as a `LOADPLUGIN` frame (`SatelliteRouter` resolves the package
and spawns its satellite on demand). Startup replays the persisted registry only (`PluginLifetime`) —
nothing is auto-discovered from disk. A real-legacy record whose entry DLL has moved shows a *Files
missing* roster row with a *Locate…* action (`RelinkLegacy` re-classifies, verifies the same plugin,
re-points the record, reloads). Uninstall is the symmetric teardown (retract UI → unload the live
instance → delete the folder → drop the record), with a `PendingDelete` list for folders still locked by
a collectible ALC / CEF, removed on the next launch.

Honest limit unchanged from [Locked decisions](#locked-decisions): a hard native crash still takes the
host; hot-unload is cooperative (quarantine + collectible ALC), not a fault boundary.

### SDK distribution — the modern contract on GitHub NuGet

The **only** published SDK is the modern contract; nothing that impersonates ACT is ever published.

- **`Fct.Abstractions`** (core contract) and **`Fct.Abstractions.UI`** (Avalonia surfaces + theme
  tokens) ship as versioned **NuGet packages on GitHub Packages**, with the **major version = the
  contract version** the host gates — so an author's `PackageReference` version and the manifest
  `contract` string are the same number. These references are marked **not copy-local** (the host shares
  them via manifest-declared sharing).
- **The ACT/SDK facades (`Advanced Combat Tracker`, `FFXIV_ACT_Plugin.Common`) are never published and
  never built against as our SDK.** They exist only so a legacy plugin *loads* — they satisfy the
  plugin's own existing ACT/SDK references by assembly identity at runtime. A recompiled legacy plugin
  compiles against the real ACT/SDK references it already has; our facade substitutes at load time. We
  do not redistribute or expose ACT's contracts.
- **Satellite face needs no SDK** — those are the *unmodified* net48 binaries loaded from the user's ACT
  install; they reference the real ACT/SDK, never ours.
