# net10 Plugin API — Design & Contract

The forward-facing plugin SDK: the typed, future-facing contract new plugins target, and the
compat adapter that lets ACT-era plugins migrate onto it. This is the **forward surface** sketched
in [`ARCHITECTURE.md`](ARCHITECTURE.md) §7, specified in full.

It is the counterpart to the *backward* surface: [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) and
[`DATA-FLOW.md`](DATA-FLOW.md) document the legacy ACT host surface the net48 satellite impersonates
to run the five plugins **unmodified**. This document is the **net10 contract** they migrate *to*.

> **Status: design + scaffold.** `Fct.Abstractions` (net48;net10) and `Fct.Abstractions.UI`
> (net10 + Avalonia) exist as compiling contract stubs — interfaces and domain records, **no host
> implementation, no loader**. The shapes here are the reviewable target.

## Two interfaces, one contract

1. **Modern** (`Fct.Abstractions` + `Fct.Abstractions.UI`) — typed, clean, best-practice. What new
   plugins target.
2. **Compat** (a planned **net10** shim, not yet built — distinct from the existing **net48**
   `Fct.Compat.Act`, which is the satellite's ACT engine) — a re-projection of the ACT + FFXIV-SDK
   programming model, implemented **as an adapter over the modern contract**. An ACT-era plugin
   **recompiles** against it (minimal changes), moves off the net48 satellite into the isolated
   net10 host, then migrates compat→modern member-by-member and drops the shim.

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
replacement. (FFXIV = FFXIV_ACT_Plugin · OP = OverlayPlugin/cactbot · Trig = Triggernometry ·
Disc = ACT-Discord-Triggers · Hojo = ACT.Hojoring.)

| Archetype | Who | Today | Modern replacement |
|---|---|---|---|
| Raw log-line consumer | Trig, OP, Hojo | `Before/OnLogLineRead`, opaque `\|`-line, regex over `LogMessageType` 0–274 | `RawLogLine` event |
| Typed event consumer | OP | `IDataSubscription` (LogLine/NetworkReceived/ZoneChanged/PartyListChanged/ProcessChanged) | `IGameEventStream` typed records |
| State poller | Hojo, Trig | `IDataRepository.Get*` polled on bg threads | `IGameSnapshot` (free-threaded, immutable) |
| DPS / encounter reader | OP | `ActiveZone.ActiveEncounter` + scrape both `ExportVariables` per tick | `IEncounterService` typed snapshot |
| Combat-state driver | Trig | `SetEncounter`/`EndCombat`/`InCombat`; text export; append line | `IEncounterService` (read+write) |
| Audio sink provider | Disc, Hojo | **hijack** global `PlayTtsMethod`/`PlaySoundMethod` (last-writer-wins) | `IAudioOutput.RegisterSink` |
| Audio producer | Trig, OP, Hojo | call `TTS()`/`PlaySound()` | `IAudioOutput.Speak`/`Play` |
| Raw packets | OP | `RegisterNetworkParser` → bytes → Machina → synthetic lines 256+ | `IRawPacketSource` (capability-gated) |
| Host services | all | `PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, Trig named callbacks | `IPluginHost` (Storage/Logger/Registry) |
| UI | all | one WinForms `TabPage` via `InitPlugin` | `IUiContributor` (Avalonia surfaces) |

Three findings drove the shapes:

1. **The actor model is a superset of the SDK, not a mirror.** The FFXIV SDK `Common.Models` is just
   `Combatant`/`Player`/`NetworkBuff`/`PartyType`. But Hojoring/UltraScouter reach *past* it into
   Sharlayan + OverlayPlugin for **status lists, focus/hover/target-of-target, enmity, and a reliable
   in-combat flag** — none of which the SDK exposes. `Actor` carries these so the heaviest consumer
   can drop its side-channels.
2. **The raw line cannot be dropped on day one.** Trig and cactbot are fundamentally
   regex-over-the-pipe-line engines. `RawLogLine` is a first-class event *alongside* the typed ones —
   both produced from one decoded packet, exactly as the legacy plugin emits a log line + an SDK event.
3. **Today's fault model has fixable gaps.** OverlayPlugin holds one dispatch lock across all
   receivers (one slow `HandleEvent` stalls everyone), has unguarded subscribe-time replay, unguarded
   poll loops, and unguarded network-thread handlers. The `RingBufferDataSubscription` we already
   shipped (bounded ring + single dispatch thread + per-subscriber try/catch + drop-oldest) is the
   correct generalized model.

## Project layout

| Project | TFM | Role |
|---|---|---|
| `Fct.Abstractions` | net48;net10 | core contract: lifecycle, events, snapshot, encounter, audio, registry, raw hatch. **No UI, no opcodes.** |
| `Fct.Abstractions.UI` | net10 | Avalonia UI contribution surfaces. Referenced only by UI-contributing plugins. |
| net10 compat shim (planned) | net10 | the recompile-shim adapter over the modern contract. Distinct from the existing net48 `Fct.Compat.Act` (the satellite's ACT engine). |

net48 support in the core uses `IsExternalInit.cs` (init-accessor shim) + `Microsoft.Bcl.AsyncInterfaces`
(records / `IAsyncEnumerable` / `IAsyncDisposable`), so the identical record/interface types exist on
both sides of the bridge.

## The modern contract

### Lifecycle & identity — manifest, not reflection-by-title

A plugin ships a `plugin.json` manifest (`id`, `version`, `contract` version, `entry` type,
`capabilities`). The host reads it **without loading the assembly**, gates the contract version, then
loads the entry type into a dedicated ALC. This replaces the legacy discovery-by-reflection (matching
`lblPluginTitle.Text` and `GetProperty("DataSubscription")`).

```csharp
public interface IPlugin : IAsyncDisposable
{
    Task InitializeAsync(IPluginHost host, CancellationToken ct);   // once; ct cancels a slow init
}
```

One async entry (no split-brain `InitPlugin`+`Init()` like OverlayPlugin), `DisposeAsync` for teardown.

### The host surface

`IPluginHost` hands the plugin: `Game` (events + snapshot), `Encounters`, `Audio`, `Plugins`
(registry), `Storage`, `Logger` (MEL `ILogger`), `Clock`, and `Self` (its own `PluginInfo`).

### Game data — typed stream + raw line on one bus

`IGameSession` splits CQRS-style into `Events` (push) and `Snapshot()` (pull) — mirroring how
consumers actually work (OP/Trig stream; Hojo polls).

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
`Enmity`, `TargetOfTargetId`, and `InCombat` on top of the SDK fields. `IResourceCatalog` provides
typed, complete, all-locale name tables (skill/status/zone/world/item) — the data Hojoring patches
with bundled CSVs today because the SDK's buff dictionary is never populated.

### Encounter / combat-state

`IEncounterService` exposes `InCombat`, `StartCombat`/`EndCombat` (Trig's driver), `Active`/`Last`
encounter snapshots, `ExportText`, and `AppendLogLine`. `EncounterSnapshot`/`CombatantMetrics`
provide typed per-combatant metrics — no `ExportVariables` dictionary scrape per tick.

### Audio — multi-sink

`IAudioOutput` separates producers (`Speak`/`Play`) from sink providers (`RegisterSink`, returns
`IDisposable`). Replaces the single mutable delegate slot that forced Discord-Triggers vs TTSYukkuri
into last-writer-wins. `IAudioSink` is async/fire-and-return so the host never blocks a producer on a
slow out-of-process bridge.

### Registry, peer events, raw packets

`IPluginRegistry`: enumerate peers (typed), Triggernometry-style `RegisterCallback`/`InvokeCallback`,
and `Publish<T>`/`Subscribe<T>` so a plugin can put its own typed events on the bus for others.
`IRawPacketSource` is the single opt-in, capability-gated raw-packet hatch (the typed path stays
opcode-free for everyone else).

## Threading, performance & fault model (the explicit contract)

- **Event delivery** — each subscription owns a **bounded queue drained in order**; each handler
  call is fault-guarded (a throw can't kill the stream or starve peers); a slow/backed-up consumer
  gets **drop-oldest + a dropped counter** (never stalls the producer or other plugins). Fixes OP's
  "one dispatch lock across all receivers" stall.
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

## The compat shim (adapter)

The planned net10 compat shim (not the existing net48 `Fct.Compat.Act`) re-projects the exact ACT + FFXIV-SDK surface the plugins compile
against, **implemented entirely over the modern host**:

| Legacy symbol (recompiled against shim) | Backed by |
|---|---|
| `ActGlobals.oFormActMain` (POCO hub, not a `Form`) | `IPluginHost` |
| `IActPluginV1.InitPlugin/DeInitPlugin` | `IPlugin.InitializeAsync/DisposeAsync` |
| `Before/OnLogLineRead` | `IGameEventStream` → `RawLogLine` |
| `IDataSubscription` (11 events) | `IGameEventStream` mapped to SDK delegate shapes |
| `IDataRepository.Get*` | `IGameSnapshot` + `IResourceCatalog` |
| `Combatant`/`Player`/`NetworkBuff` | projected from `Actor`/`StatusEffect` |
| `AddCombatAction`/`SetEncounter`/`EndCombat`/`InCombat` | `IEncounterService` |
| `PlayTtsMethod`/`PlaySoundMethod` delegate slots | setting a delegate registers an `IAudioSink`; `TTS()`/`PlaySound()` → `IAudioOutput` |
| `RegisterNetworkParser` / custom lines 256+ | `IRawPacketSource` |
| `PluginGetSelfData`/`AppDataFolder`/`WriteExceptionLog` | `IPluginStorage`/`ILogger` |
| WinForms `TabPage` | real WinForms TabPage embedded via Avalonia `NativeControlHost` |

The shim's hub *is* the modern API underneath, so a plugin can use both at once — the basis for
incremental migration (swap one call at a time, then drop the shim).

### Per-plugin migration outlook

- **Triggernometry — easiest.** `RealPlugin` already never references ACT (it calls injected delegate
  hooks via `ProxyPlugin`). Re-point the proxy at the shim hub; the regex engine consumes `RawLogLine`
  unchanged. Near-trivial recompile.
- **Discord-Triggers — easy.** Pure audio sink → an `IAudioSink` registration instead of a delegate
  hijack; its out-of-process Node bridge is unaffected.
- **Hojoring — high value.** Recompile drops Sharlayan + OverlayPlugin side-channels *iff* `Actor`
  carries status/enmity/focus/hover/in-combat (it does). Its poll-and-diff maps onto `IGameSnapshot`,
  or it adopts typed events and stops diffing.
- **OverlayPlugin — stays in the satellite.** Its `IDataSubscription`/`ExportVariables` use recompiles
  fine, but **CefSharp is net48-only**, so OP's CEF/Fleck rendering stays in the net48 satellite
  (already embedded cross-process). The recompile path serves managed consumers; OP is why the
  satellite persists longest — consistent with overlays = unmodified OP, hosted.

## Versioning

`Fct.Abstractions` is **semver, additive-only**, with a `contract` version the host gates against the
manifest. New `GameEvent` records are additive (switch-and-ignore-unknown).

## Open items / next steps

- Complete the `GameEvent` hierarchy against the full 0–274 taxonomy.
- `PluginLoadContext`: manifest gate + the `Fct.Abstractions`/`Avalonia` share-to-default mechanism,
  end-to-end.
- Config-UI sub-decision detail (Avalonia control vs. WebView) — deferred per
  [`ARCHITECTURE.md`](ARCHITECTURE.md) §4a; the contract is framework-agnostic enough to defer.
- Whether `IResourceCatalog` ships our own complete name tables or proxies the plugin's
  `GetResourceDictionary` for v1.
