# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

**FFXIV Combat Tracker** — a clean-slate, FFXIV-only rebuild of the
ACT + FFXIV_ACT_Plugin + OverlayPlugin stack. It is currently in the **design
phase**: no application code yet. The full design lives in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — read it before proposing changes.
[`docs/DATA-FLOW.md`](docs/DATA-FLOW.md) is the authoritative map of how data flows
through the real upstream stack (FFXIV_ACT_Plugin → ACT → OverlayPlugin) and the exact
compat seams we must reproduce to make the unmodified plugins work — the build target.

The product is a host that runs the **existing plugin ecosystem unmodified**
(FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry, Discord triggers) while
exposing a typed, future-facing plugin API, with the network/opcode parser as a
swappable, independently-released component.

## Two hard directives (these gate every decision)

1. The first version must run the four legacy plugins above by **drop-in, unmodified**.
2. Built for the future with a **clear legacy → native migration path**, opt-in and
   incremental — never a flag day.

## Load-bearing architecture facts

- **Opcodes never cross the plugin boundary.** The host↔plugin contract is typed domain
  events. A game patch ships a new parser plugin only; the host and other plugins are
  untouched. One opt-in escape hatch (`IRawPacketSource`) exposes raw packets for legacy
  `RegisterNetworkParser` consumers.
- **Two runtimes, two processes.** net48 and net10 cannot share a process, and the four
  legacy plugins cannot load unmodified on .NET 10 (CefSharp's net48 build alone settles
  it). Legacy plugins run in a **real .NET Framework 4.8 satellite process**
  (`Fct.LegacyHost`); the **.NET 10 host** (`Fct.Host`) runs new plugins; they bridge over
  IPC. `AssemblyLoadContext` isolates net10 plugins from each other — it is **not**
  cross-runtime.
- **We build only the ACT engine.** The FFXIV SDK and the OverlayPlugin/cactbot surface
  self-host by loading the real plugins; hosting the real FFXIV_ACT_Plugin inherits its
  per-patch opcode cadence for free.
- **`Fct.Abstractions` multi-targets `net48;net10`** so the same record/interface types
  exist on both sides of the bridge.

## Project map (target — not yet created)

| Project | TFM | Role |
|---|---|---|
| `Fct.Abstractions` | net48;net10 | the plugin SDK: contracts + domain records. No opcodes. |
| `Fct.Host` | net10 | microkernel: ALC plugin loader, typed bus, Generic Host. |
| `Fct.LegacyHost` | net48 | clean-room ACT engine; hosts the four real plugins. |
| `Fct.Bridge` | net48;net10 | IPC transport + versioned wire protocol. |
| `Fct.Parser.Legacy` | net48 | wraps the real FFXIV_ACT_Plugin. `WrappedFfxivPlugin` sits in `pluginObj` (forwards `DataRepository`/`_iocContainer`/lifecycle to the real instance) and exposes `RingBufferDataSubscription` (`IDataSubscription` + `IRawPacketSource`): a bounded ring + single dispatch thread that replaces the plugin's per-subscriber `BeginInvoke` fan-out so OverlayPlugin's ~20 handlers cost one in-order dispatch. ~250× faster on the dispatch stage. |
| `Fct.Parser.Native` | net10 | clean-room parser. `NetworkLogLine` (structure) + `ActionEffectDecoder` (effect byte decode) + `CombatLogParser` (stateful: names, combat window, all swing types) + `PotencySimulator` (ACT's simulated DoT/HoT/shield amounts). Reproduces every swing type ACT emits; deterministic log-derived types are bit-exact. The simulator mirrors ACT's `DoTSimulator`/`DamageShieldSimulator`/`PotencyStatusApplication` exactly — the per-source attack-power median (calibrated from primary-target hits only), the buff-multiplier engine, and the individual-crit tick branch (RNG crit/DH bits, `Random`-seeded). Simulated `(*)` DoT/HoT/shield amounts reach ~95.6% of ACT's damage sum (the DPS signal); per-tick bit-exact is bounded by ACT's RNG crits (~29% of ticks) and per-application buff-snapshot precision. Game-data tables (action categories, status names, DoT/heal potencies, shield/buff defs) are dumped from the real plugin via `--dump-tables`. See `docs/TESTING.md`. Live capture + memory later. |
| `Fct.App` | net10 | Avalonia control panel + shell (MVVM). |
| `Fct.Overlays` | net10 | native WebView2 overlay layer (later). |
| `Fct.Compat.Act` | net48 | the ACT facade surface (in LegacyHost). Its `EncounterData`/`CombatantData`/`AttackType` aggregation reproduces the real ACT binary bit-for-bit on captured combat (see Differential ACT-engine compat in `docs/TESTING.md`). |

## UI frameworks

- **net10 control panel/shell → Avalonia** (XAML + MVVM). Native desktop app.
- **Overlays → web** (HTML/JS via Kestrel, rendered in WebView2 hosted inside Avalonia).
  Two deliberate rendering stacks: Avalonia for app chrome, web for overlays (overlay
  web rendering is required for cactbot/ecosystem compatibility).
- **net48 legacy UI → WinForms**, forced by `IActPluginV1.InitPlugin(TabPage, Label)`;
  quarantined in `Fct.LegacyHost`.

## Reference sources (read-only)

Hard-linked Windows directory junctions under `reference/` — searchable in place.
**Never modify anything under `reference/`**; these are external repos.

- `reference/overlayplugin/` → `E:\dev\OverlayPlugin` (ngld OverlayPlugin, net48).

**ACT vs FFXIV_ACT_Plugin are two separate decompiles — do not conflate them:**

- **`E:\dev\ACT-decompiled`** (= `reference/act-decompiled/`) is **Advanced Combat Tracker**
  only — the generic, game-agnostic host app: `Advanced_Combat_Tracker/`
  (`Forms/FormActMain.cs`, `ActGlobals.cs`, `IActPluginV1.cs`, `Models/`, `Events/`). It
  does the MasterSwing collection and encounter **aggregation**. It contains **no** FFXIV
  parsing or DoT/HoT/shield simulation.
- **`E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\`** is the **FFXIV_ACT_Plugin**
  decompile — all FFXIV-specific log parsing. The combat logic lives in the `.parse`
  assembly: `…\ffxiv_act_plugin.parse\FFXIV_ACT_Plugin.Parse\` — `DoTSimulator.cs`,
  `DamageShieldSimulator.cs`, `PotencyStatusApplication.cs`, the `ParseStrategy*` line
  handlers, `ReportCombatData.cs`, `SwingType.cs`. Sibling assemblies: `.common`,
  `.memory`, `.config`, `.logfile`. **This is the authority for how swings are produced.**

When a compat detail is in doubt, match the exact signature/shape in the decompile, not an
approximation — and pick the right tree: aggregation → ACT; swing production → FFXIV_ACT_Plugin.

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

Solution: `ffxiv-combat-tracker.sln`. Projects under `src/`.

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
`Fct.Parser.Native.Tests`, `Fct.Integration.Tests` net10). 103 tests. Data-dependent tests skip when their
prerequisites are absent: the real-log smoke needs a `Network_*.log` (`%APPDATA%\Advanced
Combat Tracker\FFXIVLogs`, or `FCT_FFXIV_LOGS`); the satellite integration's plugin/self-test
assertions need `FFXIV_ACT_Plugin.dll` installed. CI (`.github/workflows/ci.yml`) runs the
suite on `windows-latest`. Full details: [`docs/TESTING.md`](docs/TESTING.md).

Run the slice end-to-end (launches host → satellite → loads real plugins):

```powershell
dotnet build src\Fct.App\Fct.App.csproj
.\src\Fct.App\bin\Debug\net10.0\Fct.App.exe   # verification logs land in
                                              # bin\Debug\net10.0\satellite\s2-ffxiv.log
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
