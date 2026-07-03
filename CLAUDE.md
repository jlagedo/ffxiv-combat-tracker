# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

**FFXIV Combat Tracker runs today's ACT plugin ecosystem unmodified on current .NET, with an
opt-in migration path onto a typed modern API.** It is **not a new ACT and not a replacement** — it
carries the community's existing plugins forward by reproducing ACT's plugin-host surface as a
from-scratch, FFXIV-only compatibility facade that hosts the real plugins unmodified. Target
plugins: FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry, ACT-Discord-Triggers, ACT.Hojoring.

Design docs — **read before proposing changes:**

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — the full design.
- [`docs/DATA-FLOW.md`](docs/DATA-FLOW.md) — how data flows through the real upstream stack
  (FFXIV_ACT_Plugin → ACT → OverlayPlugin) and the compat seams we reproduce.
- [`docs/ACT-INTERFACE-MAP.md`](docs/ACT-INTERFACE-MAP.md) — the host↔plugin contract: every
  `Advanced_Combat_Tracker` member each plugin consumes, and the facade gaps.
- [`docs/PLUGIN-API.md`](docs/PLUGIN-API.md) — the forward, typed plugin surface and migration path.
- [`docs/TESTING.md`](docs/TESTING.md) — the parity/oracle testing model.

## Two hard directives (these gate every decision)

1. The first version must run the five legacy plugins above by **drop-in, unmodified**.
2. Built for the future with a **clear legacy → native migration path**, opt-in and
   incremental — never a flag day.

## Load-bearing architecture facts

- **Opcodes never cross the plugin boundary.** The host↔plugin contract is typed domain events.
  A game patch ships a new parser plugin only; the host and other plugins are untouched. One opt-in
  escape hatch (`IRawPacketSource`) exposes raw packets for legacy `RegisterNetworkParser` consumers.
- **Two runtimes, two processes.** net48 and net10 cannot share a process, and the legacy plugins
  cannot load unmodified on .NET 10 (CefSharp's net48 build settles it). Legacy plugins run in a real
  .NET Framework 4.8 satellite process (`Fct.LegacyHost`); the .NET 10 host (`Fct.App`) runs new
  plugins; they bridge over a named pipe. `AssemblyLoadContext` isolates net10 plugins from each
  other — it is **not** cross-runtime.
- **The net10 host runtime lives in `Fct.Host`** (a plain net10 class library); `Fct.App` is the
  Avalonia shell + composition root that references it. Native plugins load in-process, one collectible
  `AssemblyLoadContext` each. `Fct.Host/Plugins` discovers + loads (discover → `PluginManifest` +
  `HostContract` major-version gate → fault-guarded init → quarantine/unload); `Fct.Host/Hosting`
  implements the `IPluginHost` services (bus, registry, audio, encounter, disk storage); `Fct.Host`
  also owns the IPC bridge client (`SatelliteHost`/`SatelliteLifetime`). `AddFctHostServices` registers
  the whole runtime; the shell supplies the Avalonia-bound pieces (`IUiDispatcher`,
  `LegacyPluginHostFactory`, localized `ISatelliteNotificationText`). ALC is isolation, **not** a fault
  boundary — a hard native crash still takes the host. `samples/Fct.SamplePlugin` is the reference
  native plugin. Design: [`docs/PLUGIN-API.md`](docs/PLUGIN-API.md).
- **The net10 legacy compat runtime is an opt-in staged package, not a static reference.** `Fct.App`
  carries **no** ACT-impersonation identity in its `deps.json`: `Fct.Compat.Shim` + its two facades
  (`Advanced Combat Tracker`, `FFXIV_ACT_Plugin.Common`) + `Fct.Aggregation` are staged under `compat\`
  next to the host, and `Fct.Host/Plugins/CompatRuntime.Enable` hooks `AssemblyLoadContext.Default`
  to resolve them from there. `LegacyPluginHostFactory` materializes the shim's `LegacyPluginHost`
  reflectively (no compile-time shim dependency). `PluginLoadContext.IsShared` still routes those names
  to the default context for a single identity across the boundary.
- **We build only the ACT engine** (`Fct.Compat.Act`) — the consumer that aggregates the plugin's
  `MasterSwing`s into encounters / DPS / `ExportVariables`. The real FFXIV_ACT_Plugin is the **sole
  parser**; we **never** decode log lines or port its parsing/swing/DoT-HoT-shield logic (see
  "Reference sources"). Parity = our engine == real ACT fed identical plugin swings, validated
  bit-for-bit against the empirical oracle. Mechanism: [`docs/DATA-FLOW.md`](docs/DATA-FLOW.md) +
  [`docs/TESTING.md`](docs/TESTING.md).
- **`Fct.Abstractions` multi-targets `net48;net10`** so the same record/interface types exist on
  both sides of the bridge.

## Project map

| Project | TFM | Role |
|---|---|---|
| `Fct.Abstractions` | net48;net10 | plugin SDK: contracts + domain records. No opcodes. |
| `Fct.Abstractions.UI` | net10 | Avalonia UI contribution surfaces (`IUiContributor`/`IUiHost`); referenced by UI-contributing net10 plugins and by `Fct.App` itself (the shell's `IUiHost` implementation). |
| `Fct.Abstractions.Testing` | net10 | in-memory fakes of every plugin-contract interface + the `ShimStub` seam; backs the headless flow tests. |
| `Fct.Logging.Contracts` | net48;net10 | shared logging contract: the `LogEvents`/`LogCategories` EventId taxonomy, `LogPaths`, and the `BridgeLogRecord` log wire record — one identity on both ends of the bridge. |
| `Fct.Bridge.Contracts` | net48;net10 | shared bridge contract: the `SatelliteProtocol` handshake/command line protocol and the `GameEventFrame` typed-event wire codec — formatted/parsed by one implementation on both ends. |
| `Fct.Aggregation` | net48;net10 | **the ACT aggregation engine** (namespace `Advanced_Combat_Tracker`): `EncounterData`/`CombatantData`/`AttackType`/`MasterSwing`/`Dnum` + `ExportVariables`/`ColumnDefs`. One strong-named identity referenced by both facades (`Fct.Compat.Act` net48, `Fct.Compat.Shim.ActFacade` net10) so the aggregation binary is provably identical on both runtimes. Reads its ACT-core flags (`charName`/`blockIsHit`/`restrictToAll`) through accessors each facade's `ActGlobals` wires — the facade keeps those as real fields (precompiled plugins bind them via `ldsfld`). |
| `Fct.Host` | net10 | the **.NET 10 host runtime**: `Hosting/` `IPluginHost` services, `Plugins/` ALC loader (incl. `CompatRuntime`, the default-ALC resolver for the staged `compat\` legacy package), and the IPC bridge client (`SatelliteHost`/`SatelliteLifetime`/`ProcessJob`). Headless (no Avalonia shell/WinForms); registered via `AddFctHostServices`. `InternalsVisibleTo` `Fct.App` + `Fct.App.Tests` + `Fct.Compat.Shim.E2E.Tests`. |
| `Fct.App` | net10 | the **Avalonia shell + composition root** (MVVM): control panel, `Views/ViewModels`, localization (`Lang/`), Serilog bootstrap, and the DI wiring that binds `Fct.Host` to the UI (`IUiDispatcher`, the reflective `LegacyPluginHostFactory`, `ISatelliteNotificationText`, `EmbeddedSatelliteView`). Enables the compat runtime (`CompatRuntime.Enable`) and stages the legacy compat package under `compat\` + the two dev samples under `plugins\` — it holds **no** static reference to `Fct.Compat.Shim`. |
| `Fct.LegacyHost` | net48 | from-scratch ACT engine host; hosts the five real plugins; the net48 end of the bridge. |
| `Fct.Parser.Legacy` | net48 | wraps the real FFXIV_ACT_Plugin. `WrappedFfxivPlugin` forwards `DataRepository`/`_iocContainer`/lifecycle to the real instance; `RingBufferDataSubscription` (`IDataSubscription` + `IRawPacketSource`) replaces the plugin's per-subscriber `BeginInvoke` fan-out with one bounded ring + single dispatch thread (~250× faster dispatch). |
| `Fct.Compat.Act` | net48 | **the ACT facade** (host surface, hosted in LegacyHost): `FormActMain`/`ActGlobals`/`SettingsSerializer`/`ActPluginData` WinForms shims + the fake-`Advanced Combat Tracker` identity. References `Fct.Aggregation` for the engine and **type-forwards** its aggregation types (`EncounterData`/`CombatantData`/…) into the `Advanced Combat Tracker` identity so the real precompiled plugins' assembly-qualified type references resolve. Parity: see load-bearing facts + `docs/TESTING.md`. |
| `Fct.Compat.Shim` | net10-windows | recompile-shim adapter runtime re-projecting the legacy ACT/SDK programming model onto `IPluginHost`; hosts a recompiled legacy plugin (`LegacyPluginHost : IPlugin`), wires `ActGlobals.oFormActMain`, maps the SDK `IDataSubscription`/`IDataRepository`/`Combatant` surface. The in-process net10 legacy path — distinct from the net48 `Fct.Compat.Act`. Not statically referenced by `Fct.App`: staged under `compat\` and resolved into the default ALC by `CompatRuntime`. Design: [`docs/PLUGIN-API.md`](docs/PLUGIN-API.md). |
| `Fct.Compat.Shim.ActFacade` | net10-windows | assembly `Advanced Combat Tracker`: the POCO `ActGlobals`/`FormActMain` host surface a recompiled plugin binds to (net10 counterpart of `Fct.Compat.Act`), forwarding onto `IPluginHost`. References `Fct.Aggregation` for the engine (recompiled plugins bind the engine types via the transitive reference, so no type-forwarders are needed on this side). |
| `Fct.Compat.Shim.SdkFacade` | net10 | assembly `FFXIV_ACT_Plugin.Common`: re-declares the SDK surface (`IDataSubscription`/`IDataRepository` + `Combatant`/`Player`/`NetworkBuff`/`PartyType`) a recompiled plugin binds to. No behavior — the `Fct.Compat.Shim` adapters map it onto the host. |
| `Fct.StreamProbe` | net48 | diagnostic plugin in the satellite; taps the `MasterSwing`/raw-packet stream. |

The net10↔net48 **IPC bridge is not its own project** — host end in `Fct.Host` (the client, loaded by
the `Fct.App` process), satellite end in `Fct.LegacyHost`. Its **wire contracts** are the
multi-targeted `Fct.Bridge.Contracts` + `Fct.Logging.Contracts` libraries, referenced by both ends
(never linked source).

## UI frameworks

- **net10 control panel/shell → Avalonia** (XAML + MVVM).
- **Overlays → unmodified OverlayPlugin.** The host builds **no** native overlay layer (no
  `Fct.Overlays`, no WebView2, no host-side WebSocket — now or later). OverlayPlugin runs unmodified
  in the net48 satellite with its own CEF + Fleck + event sources.
- **net48 legacy UI → WinForms**, forced by `IActPluginV1.InitPlugin(TabPage, Label)`; quarantined in `Fct.LegacyHost`.

## Reference sources (read-only)

**Never modify anything under** the `E:\dev` source trees below. The ACT members each plugin consumes
are mapped in [`docs/ACT-INTERFACE-MAP.md`](docs/ACT-INTERFACE-MAP.md).

- `E:\dev\OverlayPlugin` — ngld OverlayPlugin / MiniParse / cactbot host (net48).
- `E:\dev\Triggernometry` — trigger engine (net48). ACT contact isolated in `Source\TriggernometryProxy\ProxyPlugin.cs`.
- `E:\dev\ACT-Discord-Triggers` — Discord audio/TTS bridge (net48). Hijacks ACT's `PlayTtsMethod`/`PlaySoundMethod`; consumes no combat data.
- `E:\dev\ACT.Hojoring` — Japanese plugin suite (net48). Heaviest consumer; also drives ACT's built-in `oFormSpellTimers`.

**Two separate decompiles — do not conflate:**

- **`E:\dev\ACT-decompiled`** is **Advanced Combat Tracker** only —
  the generic host that does MasterSwing collection + encounter **aggregation**; no FFXIV parsing.
  With the empirical oracle (ACT's captured output), it is the authority for **our** aggregation behavior.
- **`E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\`** is the **FFXIV_ACT_Plugin** decompile,
  here **only** to understand the legacy stack we host (load/lifecycle, the `FFXIV_ACT_Plugin.Common`
  surface we facade). **Never** a source to port parsing/swing/DoT-HoT-shield logic from.

## Conventions

- **Naming:** assemblies are prefixed `Fct.`. Facade assemblies that must impersonate legacy DLLs
  keep the **legacy** identity (e.g. `Advanced Combat Tracker`, `FFXIV_ACT_Plugin.Common`) — intentional.
  Two assembly-name collisions are deliberate — **do not "fix" them:** `Advanced Combat Tracker` is the
  assembly name of both `Fct.Compat.Act` (net48) and `Fct.Compat.Shim.ActFacade` (net10), one facade
  per runtime side; `FFXIV_ACT_Plugin.Common` (`Fct.Compat.Shim.SdkFacade`, net10) impersonates the
  identity of the real embedded SDK DLL used on the net48 side (no net48 Fct project carries that name).
- **Modern .NET on the net10 side:** Generic Host + MEDI, `System.Threading.Channels` for the bus,
  `System.Text.Json` source-gen, `ILogger` source-gen, CommunityToolkit.Mvvm source-gen for view
  models, records + spans. No Newtonsoft, no hand-rolled DI, no ReactiveUI.
- **This file is a facts document.** Present-tense state only — no change history or
  rationale-for-past-decisions. Rationale belongs in `docs/ARCHITECTURE.md`.

## Real binaries & the ACT facade

- **Real binaries:** FFXIV_ACT_Plugin.dll loads from `%APPDATA%\Advanced Combat Tracker\Plugins\`;
  the ACT install + OverlayPlugin 0.16.5 live at `E:\dev\Advanced Combat Tracker\`. The satellite
  loads the real plugins from there — they are never bundled.
- **ACT is strong-named** (`PublicKeyToken=a946b61e93d97868`); plugins reference it by strong name.
  The `Advanced Combat Tracker` facade is supplied via `AppDomain.AssemblyResolve` (token/version not
  re-checked on that path); the real ACT.exe stays out of the satellite probe path.
- **Live DPS needs the full aggregation engine:** FFXIV_ACT_Plugin calls `AddCombatAction`/
  `SetEncounter`/`ChangeZone`, `Fct.Compat.Act` aggregates, OverlayPlugin MiniParse reads
  `ActiveZone.ActiveEncounter` + `CombatantData.ExportVariables`.

## Logging

Both runtimes log through **`Microsoft.Extensions.Logging` `ILogger`** with **Serilog** as the
backend — one API and one `EventId` taxonomy across the bridge.

- **Host (`Fct.App`, net10):** Generic Host owns the pipeline (`LoggingBootstrap`). Sinks: console +
  daily-rolling `%LOCALAPPDATA%\FFXIVCombatTracker\logs\host-.log`.
- **Satellite (`Fct.LegacyHost`, net48):** `SatelliteLogging`. Sinks: `logs\satellite-.log`, a flat
  `s2-ffxiv.log` verification artifact next to the exe (read by integration tests), and a
  `BridgeLogSink` that forwards each record to the host. The host re-emits forwarded records under
  the `Fct.Satellite` category, so everything lands in the host sinks too.
- **Legacy seam:** the ACT facade / plugin wrapper emit via `Action<string>` (`[Info]`/`[Debug]`/…
  prefixes); `SatelliteLogging.WriteLegacy` maps prefix → level + `EventId`. `Fct.Compat.Act` /
  `Fct.Parser.Legacy` take **no** logging dependency.
- **Taxonomy:** `Fct.Logging.Contracts` (net48;net10, referenced by both processes) holds the
  `EventId` registry (`LogEvents`: 1xxx host, 2xxx satellite, 3xxx parser), the wire format
  (`BridgeLogRecord`), and the shared path (`LogPaths`). Level: default Information (Debug in
  `DEBUG`); `FCT_LOG_LEVEL` overrides. Packages pinned in `Directory.Packages.props`.

## Build & test

Solution: `ffxiv-combat-tracker.slnx`. Projects under `src/`. Toolchain: .NET 10 SDK (10.0.301),
net48 targeting pack, WindowsDesktop runtime, VS 2026 / MSBuild.

**"Build" means `dotnet run --project build`** — the distributable build project (see below), not
a bare `dotnet build`.

```powershell
dotnet build src\Fct.App\Fct.App.csproj   # net10 host; a StageSatellite target chains + stages the
                                          #   net48 satellite into bin\<cfg>\net10.0-windows\satellite\.
.\src\Fct.App\bin\Debug\net10.0-windows\Fct.App.exe   # run e2e: host launches satellite\Fct.LegacyHost.exe
                                              #   --bridge <pipe>, loads the real plugins. Unified logs
                                              #   under %LOCALAPPDATA%\FFXIVCombatTracker\logs\.
.\test.ps1                                    # build + stage + all suites under tests\.
```

`Fct.App` (net10, Avalonia 12) build-depends on `Fct.LegacyHost` (net48, x64, WinForms) via a
`ReferenceOutputAssembly=false` ProjectReference. `Fct.FlowTests` is the headless legacy↔native
contract suite on the `Fct.Abstractions.Testing` fakes (no satellite/CEF/live game). Data-dependent
tests skip without `FFXIV_ACT_Plugin.dll` installed. Details: [`docs/TESTING.md`](docs/TESTING.md).

### Distributable builds — `build/` (C# / Bullseye)

`build/` is a C# console project (Bullseye + SimpleExec) that publishes the two-process tree
(host + `satellite\`) into `dist\<mode>\`, self-contained `win-x64` with portable PDBs, and packs the
plugin SDK (`Fct.Abstractions` + `Fct.Abstractions.UI`) into `dist\<mode>\packages\*.nupkg` — a
local-only feed (`dist/` is git-ignored; never published to GitHub Packages by this step). Legacy
plugins are not bundled. **Windows-only** (the net48 satellite can't publish off Windows). The
project opts out of central package management and is not in the solution.

```powershell
dotnet run --project build            # debug (default): Debug, loose DLLs, no R2R -> dist\debug.
dotnet run --project build -- release # Release, single-file, compressed, ReadyToRun -> dist\release.
```
