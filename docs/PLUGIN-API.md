# net10 Plugin API — Design & Contract

The forward-facing plugin SDK: the typed, future-facing contract new plugins target, and the
compat adapter that lets ACT-era plugins migrate onto it. This is the **forward surface** sketched
in [`ARCHITECTURE.md`](ARCHITECTURE.md) §7, specified in full.

It is the counterpart to the *backward* surface: [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) and
[`DATA-FLOW.md`](DATA-FLOW.md) document the legacy ACT host surface the net48 satellite impersonates
to run the five plugins **unmodified**. This document is the **net10 contract** they migrate *to*.

> **Status: contract + flow-test harness + net10 host & ALC loader (slice A+B) + the net48→net10
> bridge data forwarder (C) built; the net10 compat shim (D) is in progress.** The shim's two
> legacy-impersonating facades, the plugin lifecycle, the audio / registry / raw-line seams, and the
> encounter driver over the shared aggregation engine (`ExportVariables` included) are built and load a
> recompiled legacy plugin end-to-end; the `IDataSubscription`/`IDataRepository` mapping onto the bus +
> snapshot and the UI embedding are pending (see
> [The net10 compat shim](#the-net10-compat-shim-in-progress)). `Fct.Abstractions`
> (net48;net10) and `Fct.Abstractions.UI` (net10 + Avalonia) are the compiling contract; a headless
> flow-test harness exercises the contract shapes end-to-end: `Fct.Abstractions.Testing` supplies
> in-memory fakes of every interface plus the `ShimStub` seam, and `Fct.FlowTests` (net10) runs the
> legacy↔native flow suite (**18 tests, all green, 0 skipped**). The **real host services +
> ALC-per-plugin loader now exist in `Fct.App`** and load a sample native plugin end-to-end (see
> [The net10 host & ALC loader](#the-net10-host--alc-loader-built)). The archetype shapes below are
> cross-checked against the actual source of all five supported plugins on `E:\dev`; the gaps that
> check surfaced (**G1–G11, all now shipped**) are tracked in [Contract gaps](#contract-gaps-tracked),
> and the flow suite is documented in
> [Testing the contract](#testing-the-contract--legacynative-flow-tests).

## Two interfaces, one contract

1. **Modern** (`Fct.Abstractions` + `Fct.Abstractions.UI`) — typed, clean, best-practice. What new
   plugins target.
2. **Compat** (the **net10** shim `Fct.Compat.Shim` + its two legacy-impersonating facades — distinct
   from the existing **net48** `Fct.Compat.Act`, which is the satellite's ACT engine) — a re-projection
   of the ACT + FFXIV-SDK programming model, implemented **as an adapter over the modern contract**. An
   ACT-era plugin **recompiles** against it (minimal changes), moves off the net48 satellite into the
   isolated net10 host, then migrates compat→modern member-by-member and drops the shim.

## Locked decisions

- **Isolation model: in-process, ALC-per-plugin.** Each plugin loads into its own collectible
  `AssemblyLoadContext` → assembly/version isolation + hot-unload. Fault containment is
  **cooperative** (fault-guarded boundaries, watchdog timeouts, quarantine/unload). Honest limit: a
  hard native crash (AccessViolation/StackOverflow/OOM) still takes the host — ALC is not a fault
  boundary. The contract is callback/record-based and **location-transparent**, so an out-of-process
  tier can sit behind the same interfaces later with no plugin code change (designed-for, not built).
- **Compat = net10 recompile-shim adapter** (option above), not a second unmodified-host. The
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
| `Fct.Abstractions.Testing` | net48;net10 | in-memory fakes of every contract interface + the `ShimStub` seam, for the headless flow tests. |
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
side-channels. `IResourceCatalog` provides typed, complete, all-locale name tables
(skill/status/zone/world/item) — the data Hojoring patches with bundled CSVs today because the SDK's
buff dictionary is never populated. (A crafter/gatherer projection is still lossy — see
[G10](#contract-gaps-tracked).)

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
host's load context resolves **`Fct.Abstractions` / `Fct.Abstractions.UI` / `Avalonia.*` up to the
default context** so contract + UI types have one identity across the boundary; everything else (the
plugin's own Newtonsoft, etc.) stays private to its ALC via `AssemblyDependencyResolver`/`.deps.json`.
No strong-name impersonation, no `AssemblyResolve` games — the legacy ACT-load problem does not exist
in this model. Plugins are pinned to the host's Avalonia version (as legacy plugins were pinned to
WinForms).

## UI strategy — plugins contribute Avalonia surfaces

UI is a **separate, opt-in contract** (`Fct.Abstractions.UI`, net10 + Avalonia) so headless plugins
take no UI dependency. A plugin opts in by also implementing `IUiContributor` and declaring `"ui"` in
its manifest (the host lazy-loads the UI assembly only when a surface is shown).

```csharp
public interface IUiContributor { void RegisterUi(IUiHost ui); }   // once, on the UI thread

public interface IUiHost
{
    void AddSettingsPage(UiSurface page);     // the WinForms TabPage replacement
    void AddNavPage(UiSurface page);          // its own left-nav entry
    void AddDashboardWidget(UiWidget widget); // a dashboard tile
    void AddStatusItem(UiStatusItem item);    // status-bar / toolbar item
    IUiDispatcher Dispatcher { get; }         // explicit UI-thread marshaling
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
  click-through game overlays remain unmodified OverlayPlugin in the net48 satellite
  ([`ARCHITECTURE.md`](ARCHITECTURE.md) §4a). Escape hatch only: a plugin may open its own top-level
  Avalonia window; the host provides no overlay scaffolding.
- Triggernometry's host-window mutations map onto `IUiHost.RevealPage` (`LocateTab`) and
  `AddCornerControl`/`RemoveCornerControl` ([G11](#contract-gaps-tracked), shipped).

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
| `IDataSubscription` (11 events: NetworkReceived/Sent, CombatantAdded/Removed, PrimaryPlayerChanged, ZoneChanged, PlayerStatsChanged, PartyListChanged, LogLine, ParsedLogLine, ProcessChanged) | `IGameEventStream` mapped to SDK delegate shapes | ⏳ pending (interface declared) |
| `IDataRepository.Get*` | `IGameSnapshot` + `IResourceCatalog` | ⏳ pending (interface declared) |
| `Combatant`/`Player`/`NetworkBuff` | projected from `Actor`/`StatusEffect` (lossless: CP/GP/world/order [G10](#contract-gaps-tracked), refresh/timestamp [G9](#contract-gaps-tracked)) | ⏳ pending (types declared) |
| `RegisterNetworkParser` / custom lines 256+ | `IRawPacketSource` (read) + `IRawLogLineEmitter` synthetic-line emit ([G4](#contract-gaps-tracked)) | ⏳ pending (host emit path built) |
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
- **OverlayPlugin — stays in the satellite.** Its `IDataSubscription`/`ExportVariables` use recompiles
  fine, but **CefSharp is net48-only** (pinned `95.7.141`, `HtmlRenderer.csproj:45-52`), so OP's
  CEF/Fleck rendering stays in the net48 satellite (already embedded cross-process). It is also itself
  a mini plugin-host (`IOverlayAddonV2.Init`, `PluginMain.cs:493-504`). The recompile path serves
  managed consumers; OP is why the satellite persists longest — consistent with overlays = unmodified
  OP, hosted. The managed-consumer surface for cactbot fidelity ([G1](#contract-gaps-tracked)/[G2](#contract-gaps-tracked)/[G4](#contract-gaps-tracked)) is shipped.

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

`Fct.Abstractions.Testing` (net48;net10) + `Fct.FlowTests` (net10 xUnit) provide:

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

- **Implemented and green, headless (18 tests, 0 skipped):** the full A1–A5 / B1–B5 set — including
  A2 fan-out **and** the A2 terminal-sink variant (G3), B4's synthetic-line emit (G4), and the
  G2/G5/G6/G7/G8/G9/G10 round-trip checks — plus bus filter/isolation checks and the G1
  default/round-trip check. They validate every contract gap end-to-end with only the harness as new
  code. G11 (UI surfaces) is contract-only and verified by build, not a headless flow test.
- **Cannot be tested until built:** *fidelity* of the real `Combatant→Actor` /
  `ExportVariables→CombatData` projection — that logic lives in the not-yet-existent net10 shim; the
  stub only proves the seam exists, not that mapping is bit-correct.
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

The real host services + assembly loader exist in `Fct.App` (net10), promoting the in-memory fakes to
production and loading a real native `IPlugin` from disk:

- **Host services** (`src/Fct.App/Hosting/`): `GameEventBus` (a real `IGameEventStream` — bounded
  per-subscription channel drained by a background pump, drop-oldest backpressure, fault-guarded
  handlers, generalizing `RingBufferDataSubscription`) with an in-process `IGameEventSink` producer
  seam; `RegistryService`, `AudioService` (priority fan-out + terminal-sink G3), `EncounterService`,
  disk-backed `PluginStorage`, `SystemClock`, the capability-gated `RawLogLineEmitter` (G4), and the
  per-plugin `PluginHost : IPluginHost`. Registered in `Program.BuildHost`.
- **ALC loader** (`src/Fct.App/Plugins/`): `PluginManifest` (`plugin.json`, read without loading the
  assembly) + `HostContract` major-version gate; `PluginLoadContext` (collectible, `Fct.Abstractions`/
  `Fct.Abstractions.UI`/`Microsoft.Extensions.Logging.Abstractions`/`Avalonia.*` — plus the compat
  shim (`Fct.Compat.Shim`) and its two legacy-identity facades (`Advanced Combat Tracker`,
  `FFXIV_ACT_Plugin.Common`) — shared to the default context, everything else private via
  `AssemblyDependencyResolver`); `PluginManager` (discover → gate → load → time-boxed fault-guarded
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
- **Wire (`shared/Bridge/GameEventFrame.cs`, linked into both processes)** — a new `EVT` frame kind
  alongside `LOG`: one tab-delimited, backslash-escaped line per event, **no JSON** (the
  `BridgeLogRecord` convention). `Sequence` is not on the wire — the host re-stamps each decoded event
  from its own `IGameEventSink.NextSequence()` so the bus keeps one coherent per-session ordering.
- **Host (`src/Fct.App/SatelliteHost.cs`)** — the bridge read loop decodes `EVT` frames and calls
  `IGameEventSink.Emit`, so bridge events flow to every subscription exactly like any in-process source.

**What the forwarder can carry is bounded by the sole-parser directive** — it may only project data the
SDK/ACT hub exposes *post-parse*, never re-parse a log line. Forwarded today: `RawLogLine` (the SDK
`LogLine` firehose that Trig/cactbot regex over), `ZoneChanged`, `PartyChanged`,
`PrimaryPlayerChanged`, `CombatantAdded`/`CombatantRemoved` (`Combatant`→`Actor`, lossless for
DoL/DoH per [G10](#contract-gaps-tracked)), and `ActionEffect` (from the ACT hub's post-aggregation
`AfterCombatAction`). Events that exist only as parsed log-line fields — `StatusApplied`/`Removed`,
`Cast*`, `DeathOccurred`, `HpUpdated` — are **not** synthesized; consumers reach them through the
`RawLogLine` firehose, exactly as in ACT today. Codec round-trip + decode-onto-bus are covered by
`tests/Fct.App.Tests/BridgeEventFrameTests.cs`.

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

The host loads a shimmed plugin through the same path as a native one: `PluginManifest` gains an
optional `legacyEntry` (mutually exclusive with `entry`); `PluginManager` routes it through an injected
`LegacyPluginHostFactory` (so the loader takes no compile-time shim dependency); `PluginLoadContext`
shares `Fct.Compat.Shim` + the two facades to the default ALC so the shim and the plugin agree on type
identity. Hosting recompiled WinForms plugins in-process makes `Fct.App` a **net10-windows** app.
`samples/Fct.SampleLegacyPlugin` is the reference recompiled plugin; `tests/Fct.Compat.Shim.Tests`
covers identity, lifecycle, the audio/registry/raw-line seams, and the encounter driver +
cross-TFM `ExportVariables` parity.

**Built:** the `FormActMain` hub over `IPluginHost`; `LegacyPluginHost` lifecycle; audio
(`TTS`/`PlaySound` → `IAudioOutput`, `PlayTts`/`PlaySoundMethod` slots → terminal `IAudioSink`, G3);
named callbacks (`RegisterNamedCallback`/`InvokeNamedCallback` → `IPluginRegistry`, G5);
`Before/OnLogLineRead` re-fired from the `RawLogLine` firehose; logging + chrome
(`WriteExceptionLog`/`CornerControlAdd`/…); the encounter driver (`AddCombatAction`/`SetEncounter`/
`EndCombat`, `ActiveZone.ActiveEncounter`) over the ACT aggregation engine — factored into
`shared/Aggregation/` and linked identically into the net48 `Fct.Compat.Act` and the net10 ActFacade,
so the `ExportVariables` bag cactbot reads is bit-identical across runtimes (`EncounterProjector` maps
it plus the typed metrics onto `EncounterSnapshot`/`CombatantMetrics`, G1/G2; combat state mirrors onto
`IEncounterService`).

**Pending:** the `IDataSubscription` 11-event mapping onto `IGameEventStream`; the
`IDataRepository.Get*` + `Combatant`/`Player`/`NetworkBuff` projection over `IGameSnapshot` (partially
blocked on the snapshot projection below); live per-tick metric refresh into `IEncounterService.Active`
(needs a host update seam — deferred with the snapshot projection); and the WinForms `TabPage`
embedding via Avalonia `NativeControlHost`. Also under-specified: `Form`-inherited `oFormActMain`
members.

## Open items / next steps

- Snapshot/encounter **projection** off the forwarded stream: wire `CombatantAdded`/`Removed` into a
  live `IGameSnapshot` and the encounter `ExportVariables` rollup into `IEncounterService` (piece C
  forwards the event *stream*; the projection is the next increment). The shim's `IDataRepository`
  projection depends on this.
- Complete the `GameEvent` hierarchy against the full 0–274 taxonomy.
- Config-UI sub-decision detail (Avalonia control vs. WebView) — deferred per
  [`ARCHITECTURE.md`](ARCHITECTURE.md) §4a; the contract is framework-agnostic enough to defer.
- Whether `IResourceCatalog` ships our own complete name tables or proxies the plugin's
  `GetResourceDictionary` for v1.
