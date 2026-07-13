# Plugin API reference — the forward typed surface

This is the **contract catalog** for the modern (native) plugin surface: the interfaces a plugin
consumes, the domain events it receives, and the `plugin.json` schema the host loads it by. For a
hands-on walkthrough ("write your first plugin", "recompile my ACT plugin"), read
[`PORTING.md`](PORTING.md); for UI styling read [`UI-TOKENS.md`](UI-TOKENS.md). The reference plugins
are [`samples/Fct.SamplePlugin`](../samples/Fct.SamplePlugin) (native) and
[`samples/Fct.SampleLegacyPlugin`](../samples/Fct.SampleLegacyPlugin) (recompile-shim).

- **Contract version:** `1.0` (host implements `1.0`; a plugin loads when its **major** matches — see
  [Versioning](#versioning)).
- **SDK assemblies:** `Fct.Abstractions` (`net48;net10`, `Version 1.0.0`) — contracts + domain
  records, no opcodes; `Fct.Abstractions.UI` (`net10`) — Avalonia UI surfaces + the `fct-*` token
  contract.

---

## Lifecycle

A native plugin implements **`IPlugin`** (`Fct.Abstractions`). The host loads its assembly into a
dedicated collectible `AssemblyLoadContext` (assembly/version isolation + hot-unload), constructs the
type named in the manifest's `entry`, and calls `InitializeAsync` **once**. Teardown is `DisposeAsync`.
Every host→plugin call is fault-guarded; init is time-boxed (10 s) and a plugin that throws or hangs
is quarantined and unloaded.

```csharp
public interface IPlugin : IAsyncDisposable
{
    Task InitializeAsync(IPluginHost host, CancellationToken ct);
}
```

A plugin that contributes UI also implements **`IUiContributor`** (`Fct.Abstractions.UI`); an
audio-sink provider implements **`IAudioSink`** (`Fct.Abstractions`).

---

## `IPluginHost` — the services handed in at init

Replaces the legacy global `ActGlobals.oFormActMain` hub and the reflected FFXIV_ACT_Plugin SDK.

| Member | Type | Role |
|---|---|---|
| `Game` | `IGameSession` | typed event stream (push) + free-threaded state snapshots (pull) |
| `Encounters` | `IEncounterService` | combat state read/write + encounter/DPS rollup + `ExportVariables` |
| `Audio` | `IAudioOutput` | multi-sink TTS/sound (producers call; sink providers register) |
| `Plugins` | `IPluginRegistry` | peer enumeration, named callbacks, typed `Publish`/`Subscribe`, peer services |
| `Storage` | `IPluginStorage` | private data directory + typed settings persistence |
| `Logger` | `ILogger` | structured logging (`Microsoft.Extensions.Logging`) |
| `Clock` | `IClock` | `LocalNow` / `ServerNow` |
| `RawLogLines` | `IRawLogLineEmitter` | **`raw`-gated** write-back of synthetic 256+ custom log lines |
| `RawPackets` | `IRawPacketSource` | **`raw`-gated** inbound/outbound packet firehose |
| `Self` | `PluginInfo` | this plugin's own manifest metadata |

Without the `raw` capability, `RawLogLines`/`RawPackets` are inert (no-op) — the typed event path
stays opcode-free for everyone else.

### `IGameSession` — live game data (CQRS split)

```csharp
public interface IGameSession
{
    IGameEventStream Events { get; }   // push
    IGameSnapshot Snapshot();          // pull — immutable, free-threaded
}

public interface IGameEventStream
{
    IAsyncEnumerable<GameEvent> Subscribe(GameEventFilter filter, CancellationToken ct);  // await foreach
    IDisposable                Subscribe(GameEventFilter filter, Action<GameEvent> handler); // callback
}
```

Each subscription is fed by a bounded per-subscriber queue drained in order; a throwing handler is
isolated, and a slow consumer gets drop-oldest backpressure (never stalls the producer).
`GameEventFilter(Types?, IncludeRawLogLines = true, IncludeRawPackets = false)` selects what a
subscription receives; `GameEventFilter.All` is everything except the opt-in raw-packet firehose.
`IGameSnapshot` exposes `Player`, `Actors`, `Target`, `Focus`, `Hover`, `Party`, `Zone`, `Resources`
(`IResourceCatalog`), `Client` (`GameClient`), and `Find(actorId)`.

### `IEncounterService`

```csharp
bool InCombat { get; }
void StartCombat(string? title = null, string? zone = null);   // ~ SetEncounter(now, me, me)
void EndCombat(bool export = false);
EncounterSnapshot? Active { get; }
EncounterSnapshot? Last   { get; }
string ExportText(EncounterSnapshot encounter, EncounterExportFormat format); // Plain | Markdown | Json
void AppendLogLine(string line);
```

`EncounterSnapshot` carries typed `Dps`/`Damage` + a `CombatantMetrics` list (per-combatant
`EncDps`/`Damage`/`Healing`/`CritPercent`/…); both also carry the opaque ACT `ExportVariables`
dictionary verbatim for compat consumers.

### `IAudioOutput` / `IAudioSink`

```csharp
void Speak(string text, AudioOptions? options = null);
void Play(string filePath, int volume = 100);
IDisposable RegisterSink(IAudioSink sink, int priority = 0, bool terminal = false); // higher priority first
```

Producers call `Speak`/`Play`; providers register a sink. A `terminal` sink stops the chain once it
handles a call — the additive form of Discord-Triggers/TTSYukkuri routing audio instead of the
built-in speakers.

### `IPluginRegistry`

```csharp
IReadOnlyList<PluginInfo> LoadedPlugins { get; }
IDisposable RegisterCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false);
void        InvokeCallback(string name, object? argument = null);
void        Publish<T>(T evt) where T : notnull;                 // plugin→plugin typed events
IDisposable Subscribe<T>(Action<T> handler) where T : notnull;
bool        TryGetPeerService<T>(string pluginId, out T service) where T : class;
```

Note: `Publish<T>`/`Subscribe<T>` here is **plugin-to-plugin** pub/sub over arbitrary CLR types,
distinct from the game-event bus on `IGameSession.Events`.

### `IPluginStorage`

```csharp
string DataDirectory { get; }                                   // <AppData>\plugins\<id>
Task<T?> LoadSettingsAsync<T>(string name = "settings") where T : class;   // null when absent
Task     SaveSettingsAsync<T>(T value, string name = "settings") where T : class;
```

---

## Domain events — the `GameEvent` hierarchy

All derive from `abstract record GameEvent(long Sequence, DateTimeOffset Timestamp)`. Consumers switch
on the concrete record and **ignore unknown ones** — new events are additive, never a breaking change.
`Sequence` is a monotonic per-session ordinal; `Timestamp` is server time.

| Record | Meaning | Status |
|---|---|---|
| `RawLogLine` | raw FFXIV log line (regex-engine lifeline) + `LogMessageType` | live |
| `ActionEffect` | an action resolving on one or more targets (`EffectTarget`/`EffectFlags`) | live |
| `CombatSwing` | full-fidelity ACT `MasterSwing` (every field the engine folds) | live |
| `SetEncounterRequested` | ACT `SetEncounter(time, attacker, victim)` | live |
| `ZoneChangeRequested` | ACT `ChangeZone(name)` (name only) | live |
| `EndCombatRequested` | ACT `EndCombat(export)` | live |
| `CastStarted` / `CastCancelled` | cast begins / is interrupted | live |
| `StatusApplied` / `StatusRemoved` | a status/buff is applied / falls off | live |
| `DeathOccurred` | an actor dies (`Killer` null when unknown) | live |
| `CombatantAdded` / `CombatantRemoved` | an actor enters / leaves tracking | live |
| `HpUpdated` | an actor's HP changed | live |
| `ZoneChanged` | player changed zone (id + name — the SDK event) | live |
| `PrimaryPlayerChanged` | local player identity changed | live |
| `PartyChanged` | party/alliance roster changed (`PartySize` reserved — see below) | live |
| `RawPacketReceived` | raw on-wire packet — **`raw`-gated**, off the bus unless opted in | live (gated) |
| `RepositorySnapshot` | fixed-rate full combatant roster (consumer-satellite mirror) | live |
| `ResourceDictionaryForwarded` | id→name resource table forwarded once per `ResourceKind` | live |
| `GameProcessChanged` | FFXIV game pid (0 = no live process) | live |
| `SessionStateChanged` | one-shot environment state (version/language/region/…) | **not yet wired** |

Some fields on otherwise-live records are also reserved and defaulted — **not yet wired**, treat as
reserved until a release notes otherwise: `PartyChanged.PartySize` / `PartySnapshot.Size`,
`GameClient.ServerClockOffset`, `GameClient.IsChatLogAvailable`.

**ACT-compat vs SDK-native — which do I use?** Two vocabularies coexist by design. Prefer the typed
SDK members for new code: read encounter state via `IEncounterService` (not `SetEncounterRequested`/
`EndCombatRequested`), and use `ZoneChanged` (id + name) over `ZoneChangeRequested` (name only). The
`*Requested` events exist so the compat shim can drive the engine the way the legacy parser does.

---

## The manifest — `plugin.json`

Read **without loading the assembly** (the discovery gate that replaces reflection). Parsed as
web-style JSON (camelCase, comments and trailing commas allowed).

| Field | Required | Meaning |
|---|---|---|
| `id` | ✅ | stable unique plugin id (also the storage folder name) |
| `version` | ✅ | the plugin's own version (informational) |
| `contract` | ✅ | the `Fct.Abstractions` contract built against; major-gated (see below) |
| `assembly` | ✅ | entry DLL, relative to the plugin directory |
| `entry` | ⬧ | native `IPlugin` type's full name — **native plugins** |
| `legacyEntry` | ⬧ | `IActPluginV1` type's full name — **recompile-shim plugins** |
| `capabilities` | — | string array; known: `raw`, `ui` |
| `name` | — | display name for the roster (falls back to `id`) |
| `description` | — | one-line roster description |
| `author` | — | plugin author |

⬧ **Exactly one** of `entry` / `legacyEntry` must be set — both missing or both present is rejected.
Any missing required field, an unreadable file, or a contract mismatch rejects the plugin (logged; the
plugin is not loaded).

Native example:

```json
{
  "id": "com.fct.sample",
  "version": "1.0.0",
  "contract": "1.0",
  "assembly": "Fct.SamplePlugin.dll",
  "entry": "Fct.SamplePlugin.SamplePlugin",
  "capabilities": [ "raw", "ui" ],
  "name": "Sample Plugin",
  "description": "Tutorial native plugin: config, the event stream (hot path), state, and a live UI.",
  "author": "FFXIV Combat Tracker"
}
```

### Capabilities

| Capability | Unlocks |
|---|---|
| `raw` | live `IRawPacketSource` + `IRawLogLineEmitter` (and `RawPacketReceived` on the bus). Without it, both are inert no-ops. |
| `ui` | the host calls `IUiContributor.RegisterUi`. Declaring `ui` without implementing `IUiContributor` is logged and skipped. |

### Versioning

The `contract` string is the sole compatibility gate. The host implements `1.0` and accepts any
plugin whose **major** version matches (additive minor bumps stay compatible): `1.x` loads on a `1.y`
host; `2.x` is rejected. There is no separate "target host version" field.

---

## UI surface (summary)

A plugin implementing `IUiContributor` contributes to the Avalonia shell via `IUiHost`:
`AddSettingsPage(UiSurface)`, `RevealPage(id)`, `AddCornerControl(UiSurface)` / `RemoveCornerControl(id)`,
and `Dispatcher` (`IUiDispatcher` for UI-thread marshaling). A `UiSurface(Id, Title, Func<Control>
CreateView, IconGlyph?, Order)` builds its view lazily on the UI thread. Style through the token
contract (`FctStyleClasses` / `FctTokens` / `FctMetrics`) so the page tracks the shell's look — full
catalog in [`UI-TOKENS.md`](UI-TOKENS.md).

---

## See also

- [`PORTING.md`](PORTING.md) — step-by-step: write a native plugin, or recompile an ACT plugin.
- [`UI-TOKENS.md`](UI-TOKENS.md) — the `fct-*` styling contract for in-shell UI.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) §7 (forward surface) + §10 (migration ladder).
- [`NORTH-STAR.md`](NORTH-STAR.md) — the three plugin patterns (legacy → recompile → native).
