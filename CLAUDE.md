# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

**FFXIV Combat Tracker** — a clean-slate, FFXIV-only rebuild of **ACT** (the Advanced Combat
Tracker host/engine at the center of the FFXIV_ACT_Plugin + OverlayPlugin stack), running the
real FFXIV_ACT_Plugin + OverlayPlugin **unmodified**. The two-process host builds and runs the
real legacy plugins (**Slice 1 complete** — see *Active work* below). The full design lives in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — read it before proposing changes.
[`docs/DATA-FLOW.md`](docs/DATA-FLOW.md) is the authoritative map of how data flows
through the real upstream stack (FFXIV_ACT_Plugin → ACT → OverlayPlugin) and the exact
compat seams we must reproduce to make the unmodified plugins work — the build target.
[`docs/ACT-INTERFACE-MAP.md`](docs/ACT-INTERFACE-MAP.md) is the host↔plugin contract: every
`Advanced_Combat_Tracker` member each supported plugin consumes, why, and the facade gaps.
[`docs/PLUGIN-API.md`](docs/PLUGIN-API.md) sketches the forward, typed plugin surface
(`Fct.Abstractions`/`Fct.Abstractions.UI`) and the legacy → native migration path.

The product is a host that runs the **existing plugin ecosystem unmodified**
(FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry, ACT-Discord-Triggers, ACT.Hojoring)
while exposing a typed, future-facing plugin API, with the network/opcode parser as a
swappable, independently-released component.

## Two hard directives (these gate every decision)

1. The first version must run the five legacy plugins above by **drop-in, unmodified**.
2. Built for the future with a **clear legacy → native migration path**, opt-in and
   incremental — never a flag day.

## Load-bearing architecture facts

- **Opcodes never cross the plugin boundary.** The host↔plugin contract is typed domain
  events. A game patch ships a new parser plugin only; the host and other plugins are
  untouched. One opt-in escape hatch (`IRawPacketSource`) exposes raw packets for legacy
  `RegisterNetworkParser` consumers.
- **Two runtimes, two processes.** net48 and net10 cannot share a process, and the five
  legacy plugins cannot load unmodified on .NET 10 (CefSharp's net48 build alone settles
  it). Legacy plugins run in a **real .NET Framework 4.8 satellite process**
  (`Fct.LegacyHost`); the **.NET 10 host** (`Fct.App`) runs new plugins; they bridge over
  IPC. `AssemblyLoadContext` isolates net10 plugins from each other — it is **not**
  cross-runtime.
- **We build only the ACT engine.** The FFXIV SDK and the OverlayPlugin/cactbot surface
  self-host by loading the real plugins; hosting the real FFXIV_ACT_Plugin inherits its
  per-patch opcode cadence for free.
- **What we build, and what "parity" means** (read this before touching aggregation).
  **We build only the ACT engine** (`Fct.Compat.Act`) — the consumer/aggregator that turns
  `MasterSwing`s into encounters / DPS / `ExportVariables`. **The real FFXIV_ACT_Plugin is the sole
  parser**: it decodes `Network_*.log` lines (and live packets) into `MasterSwing`s. **Reproducing the
  plugin's parse is an explicit non-goal** — we host the real plugin for it, unmodified, and inherit its
  per-patch opcode cadence for free. We **never** read, mirror, or port logic from FFXIV_ACT_Plugin, and
  we **never** decode log lines ourselves.
  - **Data flow.** Live: the plugin (packets → `MasterSwing`s, plus the `Network_*.log` record) →
    **our ACT engine** aggregates → OverlayPlugin/cactbot read `CombatantData.ExportVariables`. The
    plugin is the producer; our engine is the consumer. Every DoT/HoT/shield value is the **plugin's**
    synthesis (potency estimates not present in the log); our engine sums whatever swings it is handed
    and applies no filter — exactly as `ACT-decompiled`'s `FormActMain.AddCombatAction` does.
  - **Two ground truths**, both treating the plugin/ACT as opaque producers, never as source to copy:
    `ACT-decompiled` (ACT's own aggregation behavior) and the **empirical oracle** — the real ACT
    binary's captured output (`tools/act-oracle`).
  - **Parity = our engine == real ACT, fed identical plugin swings.** The satellite's `--mass-oracle`
    captures the real plugin's `MasterSwing` stream per log; both the real ACT binary (`tools/act-oracle`)
    and our `Fct.Compat.Act` engine (`--mass-engine-exports`) aggregate that *same* stream; `tools/mass-compare`
    diffs the `ExportVariables` payload OverlayPlugin reads. Validated bit-for-bit by
    `AggregateCompatTests`/`ExportVarsCompatTests` (fixtures) and corpus-scale by `tools/mass-compare`
    (**100.000%** exact on the fixture corpus). See [`docs/TESTING.md`](docs/TESTING.md).
- **`Fct.Abstractions` multi-targets `net48;net10`** so the same record/interface types
  exist on both sides of the bridge.

## Project map

| Project | TFM | Role |
|---|---|---|
| `Fct.Abstractions` | net48;net10 | the plugin SDK: contracts + domain records. No opcodes. |
| `Fct.Abstractions.UI` | net10 | Avalonia UI contribution surfaces (`IUiContributor`/`IUiHost`); referenced only by UI-contributing net10 plugins. |
| `Fct.App` | net10 | the **.NET 10 host**: Avalonia control panel + shell (MVVM); launches/embeds the satellite and owns the IPC bridge client (`SatelliteHost`/`SatelliteProtocol`/`SatelliteLifetime`). |
| `Fct.LegacyHost` | net48 | clean-room ACT engine host; hosts the five real plugins; the net48 end of the bridge. |
| `Fct.Parser.Legacy` | net48 | wraps the real FFXIV_ACT_Plugin. `WrappedFfxivPlugin` sits in `pluginObj` (forwards `DataRepository`/`_iocContainer`/lifecycle to the real instance) and exposes `RingBufferDataSubscription` (`IDataSubscription` + `IRawPacketSource`): a bounded ring + single dispatch thread that replaces the plugin's per-subscriber `BeginInvoke` fan-out so OverlayPlugin's ~20 handlers cost one in-order dispatch. ~250× faster on the dispatch stage. |
| `Fct.Compat.Act` | net48 | **the ACT engine** (the facade surface, hosted in LegacyHost). Its `EncounterData`/`CombatantData`/`AttackType` aggregation + `ExportVariables` reproduce the real ACT binary bit-for-bit when fed the real plugin's `MasterSwing` stream. Held to that baseline by `AggregateCompatTests`/`ExportVarsCompatTests` (fixtures) and corpus-scale by `tools/mass-compare` — the satellite's `--mass-engine-exports` aggregates each captured `oracle.tsv` through this engine, diffed against the real-ACT baseline. See `docs/TESTING.md`. |
| `Fct.StreamProbe` | net48 | diagnostic plugin loaded in the satellite; taps the parser's `MasterSwing`/raw-packet stream for inspection. |

The net10↔net48 **IPC bridge is not its own project** — the host end lives in `Fct.App`
(`SatelliteHost`/`SatelliteProtocol`/`SatelliteLifetime`) and the satellite end in `Fct.LegacyHost`.

## UI frameworks

- **net10 control panel/shell → Avalonia** (XAML + MVVM). Native desktop app.
- **Overlays → unmodified OverlayPlugin.** The host does **not** build a native overlay layer
  (no `Fct.Overlays`, no WebView2, no host-side WebSocket — now or later). OverlayPlugin runs
  unmodified in the net48 satellite, bringing its own CEF + Fleck + event sources, and renders
  overlays as its own transparent click-through windows. cactbot/ecosystem compatibility comes
  from hosting the real OverlayPlugin, not from reproducing its web stack.
- **net48 legacy UI → WinForms**, forced by `IActPluginV1.InitPlugin(TabPage, Label)`;
  quarantined in `Fct.LegacyHost`.

## Reference sources (read-only)

Hard-linked Windows directory junctions under `reference/` — searchable in place.
**Never modify anything under `reference/`**; these are external repos.

- `reference/overlayplugin/` → `E:\dev\OverlayPlugin` (ngld OverlayPlugin, net48).

**Supported-plugin source trees** (read-only, under `E:\dev`). The ACT members each consumes
are mapped in [`docs/ACT-INTERFACE-MAP.md`](docs/ACT-INTERFACE-MAP.md):

- `E:\dev\OverlayPlugin` — ngld OverlayPlugin / MiniParse / cactbot host (net48).
- `E:\dev\Triggernometry` — advanced trigger engine (net48). ACT contact isolated in
  `Source\TriggernometryProxy\ProxyPlugin.cs`.
- `E:\dev\ACT-Discord-Triggers` — Discord audio/TTS bridge (net48). Hijacks ACT's
  `PlayTtsMethod`/`PlaySoundMethod` delegates; consumes no combat data.
- `E:\dev\ACT.Hojoring` — Japanese plugin suite (SpecialSpellTimer, TTSYukkuri, UltraScouter,
  XIVLog; net48). Heaviest consumer; also drives ACT's built-in `oFormSpellTimers`.

**ACT vs FFXIV_ACT_Plugin are two separate decompiles — do not conflate them:**

- **`E:\dev\ACT-decompiled`** (= `reference/act-decompiled/`) is **Advanced Combat Tracker**
  only — the generic, game-agnostic host app: `Advanced_Combat_Tracker/`
  (`Forms/FormActMain.cs`, `ActGlobals.cs`, `IActPluginV1.cs`, `Models/`, `Events/`). It
  does the MasterSwing collection and encounter **aggregation**. It contains **no** FFXIV
  parsing or DoT/HoT/shield simulation.
- **`E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\`** is the **FFXIV_ACT_Plugin**
  decompile. It exists here for **one purpose only: understanding the legacy stack we host
  unmodified** (its load/lifecycle, the `FFXIV_ACT_Plugin.Common` SDK surface we facade, how it
  drives ACT). **It is never a source to port parsing, swing-production, or DoT/HoT/shield logic
  from.** We do not replicate FFXIV_ACT_Plugin; if any of our clean-room code was derived from it,
  that is a bug to fix.

When a compat detail is in doubt, the authority for **our** implementation is `ACT-decompiled` (host
+ aggregation behavior) and the **empirical oracle** (ACT's captured output) — match the exact
signature/shape there, not the plugin's internals.

## Logging

Both runtimes log through the **`Microsoft.Extensions.Logging` `ILogger`** surface with **Serilog**
as the backend, so one API and one `EventId` taxonomy span both sides of the bridge.

- **Host (`Fct.App`, net10):** Generic Host (`Microsoft.Extensions.Hosting`) owns the pipeline via
  `Serilog.Extensions.Hosting`. Sinks: console + a daily-rolling file at
  `%LOCALAPPDATA%\FFXIVCombatTracker\logs\host-.log`. Configured in `LoggingBootstrap`.
- **Satellite (`Fct.LegacyHost`, net48):** Serilog behind a `SerilogLoggerFactory` (`SatelliteLogging`).
  Sinks: the same `logs\satellite-.log` rolling file, a flat `s2-ffxiv.log` **verification artifact**
  next to the exe (read by the integration tests), and a custom **`BridgeLogSink`** that forwards each
  record to the host over the bridge.
- **Unified stream:** the satellite forwards records as `LOG <wire>` frames (`BridgeLogRecord`); the
  host's `SatelliteHost` keeps reading the pipe for its lifetime and **re-emits** them into its own
  pipeline under the `Fct.Satellite` category, so everything lands in the host's console + file too.
- **Legacy seam:** the ACT facade and plugin wrapper emit through an `Action<string>` (`[Info]`/
  `[Debug]`/`[Exception]`/… prefixes); `SatelliteLogging.WriteLegacy` maps the prefix to a level +
  `EventId` and routes it in. `Fct.Compat.Act` / `Fct.Parser.Legacy` take **no** logging dependency.
- **Taxonomy:** `shared/Logging/` (linked into both processes) holds the `EventId` registry
  (`LogEvents`: 1xxx host, 2xxx satellite, 3xxx parser), the bridge wire format (`BridgeLogRecord`),
  and the shared log path (`LogPaths`).
- **Level:** default Information (Debug in `DEBUG`); `FCT_LOG_LEVEL` env var overrides.
- **Packages** are centrally pinned in `Directory.Packages.props` (so the Serilog stack is identical
  on net48 and net10).

## Conventions

- **Naming:** assemblies are prefixed `Fct.` (FFXIV Combat Tracker). Facade assemblies
  that must impersonate legacy DLLs keep the **legacy** identity (e.g. `Advanced Combat
  Tracker`, `FFXIV_ACT_Plugin.Common`) — that is intentional, not a mistake.
- **Modern .NET patterns** on the net10 side: Generic Host + MEDI, `System.Threading.
  Channels` for the bus, `System.Text.Json` source-gen, `ILogger` source-gen, records +
  spans. No Newtonsoft, no hand-rolled DI containers on the net10 side.
- **This file is a facts document.** Present-tense state only — no change history or
  rationale-for-past-decisions. Design rationale belongs in `docs/ARCHITECTURE.md`.

## Active work — Slice 1

Loading the real FFXIV_ACT_Plugin + OverlayPlugin in the two-process host. Full plan +
verified findings: [`docs/SLICE-1.md`](docs/SLICE-1.md). Key locked facts:

- **Real binaries:** FFXIV_ACT_Plugin.dll at `%APPDATA%\Advanced Combat Tracker\Plugins\`;
  ACT install + OverlayPlugin 0.16.5 at `E:\dev\Advanced Combat Tracker\`.
- **ACT is strong-named** (`PublicKeyToken=a946b61e93d97868`); plugins reference it by
  strong name. The facade is supplied via `AppDomain.AssemblyResolve` (token/version not
  re-checked on that path); keep the real ACT.exe out of the satellite probe path.
- **Live DPS requires ACT's full aggregation engine** — FFXIV_ACT_Plugin calls
  `AddCombatAction`/`SetEncounter`/`ChangeZone`; ACT aggregates; OverlayPlugin MiniParse
  reads `ActiveZone.ActiveEncounter` + `CombatantData.ExportVariables`. Not just plugin loading.

## Build

Solution: `ffxiv-combat-tracker.slnx`. Projects under `src/`.

```powershell
dotnet build src\Fct.App\Fct.App.csproj   # net10 host; chains + stages the net48 satellite
```

`Fct.App` (net10, Avalonia 12) build-depends on `Fct.LegacyHost` (net48, x64, WinForms)
via a `ReferenceOutputAssembly=false` ProjectReference, and a `StageSatellite` target copies
the satellite output to `bin\<cfg>\net10.0\satellite\`. At runtime the host launches
`satellite\Fct.LegacyHost.exe --bridge <pipeName>` and they handshake over a named pipe.

Toolchain present: .NET 10 SDK (10.0.301), net48 targeting pack, WindowsDesktop runtime,
VS 2026 / MSBuild.

## Test

`./test.ps1` builds + stages the satellite, then runs all suites under `tests/`
(`Fct.Compat.Act.Tests` + `Fct.Parser.Legacy.Tests` net48, `Fct.App.Tests`,
`Fct.Integration.Tests` net10). Data-dependent tests skip when their prerequisites are absent: the
satellite integration's plugin/self-test assertions and the replay-route test need
`FFXIV_ACT_Plugin.dll` installed. Full details: [`docs/TESTING.md`](docs/TESTING.md).

Run the slice end-to-end (launches host → satellite → loads real plugins):

```powershell
dotnet build src\Fct.App\Fct.App.csproj
.\src\Fct.App\bin\Debug\net10.0\Fct.App.exe   # unified logs: %LOCALAPPDATA%\FFXIVCombatTracker\
                                              # logs\{host,satellite}-*.log (satellite records are
                                              # forwarded into the host's pipeline too). The flat
                                              # satellite\s2-ffxiv.log verification artifact remains.
```

Status: **Slice 1 complete (S0–S5), pending live-game capture.** All phases built,
tested via run logs, committed:
- S0 two-process launch + IPC handshake — verified.
- S1 cross-process `SetParent` embedding — verified (child reparented under host window).
- S2 facade + public-signed `Advanced Combat Tracker` (token a946…) + `AssemblyResolve`;
  real FFXIV_ACT_Plugin reaches **"Started"**.
- S3 real OverlayPlugin hosted; FFXIVRepository discovery works; CEF runs (overlays created).
- S4 both plugin config tabs embedded in the Avalonia window (3 tabs).
- S5 clean-room aggregation verified deterministically (self-test: 10×1000/9s →
  encdps=1111, damage=10000, crithit%=40%, exact).

**Remaining live check:** with FFXIV running, confirm `AddCombatAction` count > 0 and an
overlay shows live DPS. The data source (FFXIV plugin) is Started; the rest of the path is
verified. See `docs/SLICE-1.md`.
