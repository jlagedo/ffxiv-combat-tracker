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
