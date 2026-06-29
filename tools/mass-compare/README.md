# mass-compare

Corpus-scale **ACT-engine parity**: proves our clean-room ACT engine (`Fct.Compat.Act`) reproduces
the real Advanced Combat Tracker binary's aggregation — the `ExportVariables` payload OverlayPlugin/
cactbot read — over an **entire folder of `Network_*.log` files** (months of logs), fed the identical
plugin-produced swings.

The real `FFXIV_ACT_Plugin` is the **sole parser**. We never re-parse the logs ourselves; parsing is
the plugin's job. This harness exercises only the **consumer** (our engine) and holds it to real ACT.
It is the corpus version of `Fct.Compat.Act.Tests/ExportVarsCompatTests`.

## How it runs

```powershell
./tools/mass-compare/run.ps1                      # all logs in %APPDATA%\...\FFXIVLogs
./tools/mass-compare/run.ps1 -MaxLines 200000     # quick: cap lines per file
./tools/mass-compare/run.ps1 -SkipOracle          # reuse existing oracle.tsv captures
```

Pipeline (one continuous plugin session, then two aggregations of the same swings):

1. **Capture the plugin's parse** — `Fct.LegacyHost.exe --mass-oracle <logFolder> <outFolder>` loads
   the real `FFXIV_ACT_Plugin` **once** and feeds every log through `FormActMain.BeforeLogLineRead`,
   in chronological filename order, as one continuous stream (combatant-name/combat state carries
   across day-boundary rotations, exactly as ACT sees it live). Every `MasterSwing` the plugin
   produces is written to `<name>.oracle.tsv`.
2. **Real-ACT baseline** — `tools/act-oracle ActOracle --folder <outFolder>` aggregates each
   `<name>.oracle.tsv` through the **real ACT binary**'s `EncounterData`/`CombatantData` and dumps its
   `ExportVariables` to `<name>.oracle.exports.tsv`. Needs the ACT install (default
   `E:\dev\Advanced Combat Tracker`; set `-ActDir`/`$env:ACT_DIR`).
3. **Our engine** — `Fct.LegacyHost.exe --mass-engine-exports <outFolder>` aggregates the **same**
   `<name>.oracle.tsv` swings through our `Fct.Compat.Act` engine and dumps its `ExportVariables` to
   `<name>.engine.exports.tsv`. The plugin is not loaded here — parsing already happened.
4. **Diff** — `MassCompare <outFolder>` joins the two `ExportVariables` payloads per file, per
   combatant, per key, and reports how often our engine's string matches real ACT exactly, plus the
   summed numeric magnitude per key.

```
plugin swings  ->  real ACT engine     ->  <name>.oracle.exports.tsv   (baseline)
plugin swings  ->  our Fct.Compat.Act  ->  <name>.engine.exports.tsv   (under test)
                                           \__ MassCompare diffs these __/
```

## Outputs (`tmp/mass-compare/`, gitignored)

| file | contents |
|---|---|
| `<name>.oracle.tsv` | the plugin's `MasterSwing` parse of that log (the shared input) |
| `<name>.oracle.exports.tsv` | real ACT's `ExportVariables` for those swings (baseline) |
| `<name>.engine.exports.tsv` | our engine's `ExportVariables` for the same swings (under test) |
| `exports-summary.txt` | per-key exact-match % and numeric totals, corpus-wide |
| `exports-diff.txt` | sample mismatches per key |

Outputs carry real combatant names; they are local-only and never committed.
