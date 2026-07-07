# ACT-layer differential oracle

Holds our from-scratch ACT engine (`Fct.Compat.Act`) to **bit-perfect parity with the real
Advanced Combat Tracker** at the aggregation layer the consumers (OverlayPlugin/cactbot) read.

## What it does

`ActOracle.cs` loads the **real `Advanced Combat Tracker.exe`** in-process, registers the exact
damage-type routing tables the FFXIV plugin installs at runtime (`ACT_UIMods`), replays a captured
`MasterSwing` stream through the real `EncounterData`/`CombatantData` aggregation, and dumps the
per-combatant + encounter aggregates to a TSV. That TSV is the **gold baseline**.

The input stream is the real plugin's own authoritative parse, captured separately by the
satellite's `--parse-oracle`/`--mass-oracle` mode (`tests/Fct.Compat.Act.Tests/fixtures/combat-slice.oracle.tsv`).
So the pipeline under test is end-to-end real:

```
real plugin parse  →  real ACT aggregation   (this tool → baseline)
real plugin parse  →  our Fct.Compat.Act      (AggregateCompatTests → asserted equal)
```

The real `FormActMain` is never started; `ActGlobals.oFormActMain` is an uninitialized instance
used only as the `WriteDebugLog`/`SelectiveListGetSelected` sink, and ACT's default English
localization is seeded (`ActLocalization.Init`) so the `"All"` AttackType bucket is keyed correctly.

## Regenerate the baseline (dev-only; needs the ACT install)

```powershell
./build-and-run.ps1                       # uses E:\dev\Advanced Combat Tracker by default
./build-and-run.ps1 -ActDir "C:\path\to\Advanced Combat Tracker"
```

The committed baseline (`tests/Fct.Compat.Act.Tests/fixtures/combat-slice.aggregate.tsv`) means the
normal test suite needs **neither this tool nor an ACT install** — only the assertion test, which
runs everywhere.

## Plugin-in-the-loop baseline (ExportVariables completeness)

The same command also loads the **real `FFXIV_ACT_Plugin.dll`** and reflection-invokes its own
`ACT_UIMods.UpdateACTTables(false)` — the exact registration the plugin runs at startup — then
enumerates every key it registers on `CombatantData.ExportVariables`/`EncounterData.ExportVariables`
(never a hardcoded key list) to produce `combat-slice.plugin.exportvars.tsv`, the superset baseline
used to prove `ExportVariables` completeness (see [`docs/TESTING.md`](../../docs/TESTING.md)).

```powershell
$env:FFXIV_PLUGIN_DLL = "C:\path\to\FFXIV_ACT_Plugin.dll"   # defaults to E:\tmp\plugins\FFXIV_ACT_Plugin_3.0.2.3\...
./build-and-run.ps1
```

If no plugin DLL is found at the configured path, this step is skipped with a warning — the
ACT-core baselines above still regenerate normally. `ActGlobals.oFormActMain.LastKnownTime` is set
from the last replayed swing's own timestamp (via the private backing field, since the public
setter restarts a `Stopwatch` never initialized on this constructor-bypassed instance) so the
`Last10/30/60DPS` formatters have a sane clock to compute against.
