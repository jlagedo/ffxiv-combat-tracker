# mass-compare

Corpus-scale **ACT-engine parity**: proves our from-scratch ACT engine (`Fct.Compat.Act`) reproduces
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

## Plugin-in-the-loop completeness diff (P5.9)

`-PluginBaseline` adds a second, completeness-oriented diff over the *same* captured swings — the
corpus-scale sibling of `Fct.Engine.Tests/OracleParityTests.ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5`
and `Fct.Integration.Tests/OverlaySatelliteTests`. Where the diff above holds our engine to **ACT
core** over a hardcoded key list, this one holds it to the **real FFXIV_ACT_Plugin's own
`ACT_UIMods` registrations** over the **full enumerated** `ExportVariables` key set (the G1
`ACT_UIMods` keys: `Job`, `ParryPct`, `BlockPct`, `IncToHit`, `OverHealPct`, `DirectHit*`,
`CritDirectHit*`, `Last10/30/60DPS`, `CurrentZoneName`, …), never a fixed list.

```powershell
./tools/mass-compare/run.ps1 -PluginBaseline `
    -FfxivPluginDll "E:\path\to\FFXIV_ACT_Plugin.dll" `
    -LogFolder <corpus> -OutFolder <out>
```

Pipeline additions (steps 6–8, after the ACT-core diff):

6. **Plugin-in-the-loop baseline** — `ActOracle --plugin-baseline-folder <out>` loads the real
   `FFXIV_ACT_Plugin.dll` once (`ACT_UIMods.UpdateACTTables(false)`), replays each `<name>.oracle.tsv`
   through the real ACT engine with the plugin's tables installed, and dumps the **enumerated**
   `ExportVariables` to `<name>.plugin.exports.tsv`.
7. **Our engine, full enumeration** — `Fct.LegacyHost.exe --mass-engine-exports-full <out>`
   aggregates the same swings through our `Fct.Compat.Act`/`EngineTables.Install()` engine and dumps
   its **enumerated** `ExportVariables` to `<name>.engine.full.exports.tsv`.
8. **Completeness diff** — `MassCompare --plugin <out>` iterates the oracle's keys (the completeness
   authority), reports **every** divergence by name (never an aggregate percentage), and excludes only
   `*ENCOUNTER*.CurrentZoneName` (zone-frame provenance, not a swing-stream fact — the identical
   exclusion the committed slice gates make). Outputs: `plugin-exports-summary.txt`,
   `plugin-exports-diff.txt`.

## Outputs (`tmp/mass-compare/`, gitignored)

| file | contents |
|---|---|
| `<name>.oracle.tsv` | the plugin's `MasterSwing` parse of that log (the shared input) |
| `<name>.oracle.exports.tsv` | real ACT's `ExportVariables` for those swings (baseline) |
| `<name>.engine.exports.tsv` | our engine's `ExportVariables` for the same swings (under test) |
| `exports-summary.txt` | per-key exact-match % and numeric totals, corpus-wide |
| `exports-diff.txt` | sample mismatches per key |

Outputs carry real combatant names; they are local-only and never committed.
