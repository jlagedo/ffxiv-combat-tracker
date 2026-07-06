# Testing

Automated tests live under `tests/`. Run everything with:

> **The parity model has two axes.** Axis 1 — **oracle parity**: our `Fct.Aggregation` engine equals
> the real ACT binary fed identical plugin swings, bit-for-bit (mass-compare + differential compat,
> below). Axis 2 — **replica parity**: every engine instance (the host's `Fct.Engine` authority, each
> satellite's replica, the shim's) produces identical output from the identical routed stream — the
> same `Fct.Aggregation` binary held to the same oracle fixtures on every runtime. The isolation
> build-out adds a per-phase e2e gate on top (replay-driven, headless, real satellite processes fed
> recorded streams — no live game in CI): the gates live in
> [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) §4 and land in these suites as their phases land.

```powershell
./test.ps1                 # Debug; builds + stages the satellite, then runs all suites
./test.ps1 -Configuration Release
./test.ps1 -Unit           # unit/parser only — skip the satellite integration tests
./test.ps1 -Filter Dnum    # pass a --filter through to dotnet test
```

Or run a single project directly with `dotnet test tests/<project>`.

## Projects

| Project | TFM | Scope |
|---|---|---|
| `Fct.Compat.Act.Tests` | net48 | The from-scratch ACT aggregation engine: `Dnum`, `MasterSwing`, `AttackType`/`CombatantData`/`EncounterData` math, the `ExportVariables` contract OverlayPlugin/cactbot read, `SettingsSerializer` XML round-trip, and the **differential ACT-engine compat** (`AggregateCompatTests`, below). |
| `Fct.Parser.Legacy.Tests` | net48 | The parser interposition: `RingBufferDataSubscription` (bounded ring, single dispatch thread, `IRawPacketSource`) against a fake data subscription. |
| `Fct.App.Tests` | net10 | The headless `Fct.Host` runtime: the bus/registry/audio/snapshot services, the ALC plugin loader (manifest gate, classifier, installer, unload, static-graph guard), `RawPacketSource`, `ProcessJob`, the UI-token contract, and the bridge handshake/event-frame codecs (`SatelliteProtocol`, `BridgeEventFrame`). |
| `Fct.Engine.Tests` | net10 | The modern host-side engine (`Fct.Engine`): `ModernEncounterEngine` fed swings/lifecycle through an in-memory bus, projection snapshots, idle auto-end, explicit end-combat. |
| `Fct.FlowTests` | net10 | The headless legacy↔native contract suite on the `Fct.Abstractions.Testing` fakes (encounter/snapshot/raw-packet/audio flows); no satellite/CEF/live game. |
| `Fct.Compat.Shim.Tests` | net10-windows | The net10 compat shim: identity, `LegacyPluginHost` lifecycle, the `IDataSubscription`/`IDataRepository`/`Combatant` projection, the encounter driver, and cross-TFM `ExportVariables` parity. |
| `Fct.Compat.Shim.E2E.Tests` | net10-windows | The shim-free load path: `CompatRuntime` resolving the staged `compat\` package into the default ALC end-to-end. |
| `Fct.Integration.Tests` | net10 | Black-box end-to-end: launches the staged net48 satellite, checks the handshake/HWND, verifies the in-process self-test aggregation, and runs the **full live route** on a recorded slice (`--replay`, below). |

## What runs vs. skips

The unit/parser tests are self-contained and always run. The data-dependent tests skip
cleanly when their prerequisites are absent, so a clean checkout without an ACT
install stays green:

- **Satellite integration** (`Fct.Integration.Tests`) needs the satellite staged
  (`dotnet build src/Fct.App/Fct.App.csproj`); `test.ps1` does this first. The handshake and
  HWND tests run whenever the satellite is staged. The "plugin Started" and self-test
  assertions also need `FFXIV_ACT_Plugin.dll` installed under
  `%APPDATA%\Advanced Combat Tracker\Plugins`; they skip otherwise.
- **OverlayPlugin satellite gates** (`Fct.Integration.Tests`, ISOLATION-PLAN P8):
  `OverlayPacketFanoutTests` is plugin-free (a `packets`-subscribed sink satellite receives the
  host-fanned raw-packet firehose) and runs whenever the satellite is staged.
  `OverlayStandInPacketTests` (fanned packets raise the SDK stand-in's `NetworkReceived`) needs
  `FFXIV_ACT_Plugin.dll` installed. `OverlaySatelliteTests` — the MiniParse WebSocket gate: the
  real OverlayPlugin in the `overlay` consumer satellite serves `CombatData` equal to the oracle
  `ExportVariables` baseline on `ws://127.0.0.1:10501/MiniParse` — additionally needs
  `OverlayPlugin.dll` installed (`%APPDATA%\Advanced Combat Tracker\Plugins\OverlayPlugin\`); it
  reuses the installed plugin's extracted CEF via a sandbox junction, binds port 10501
  exclusively (serialized in the `satellite-p6` collection), and skips if the WS server cannot
  come up.
- **Four-satellite soak + budgets** (`Fct.Integration.Tests`, ISOLATION-PLAN P9b), two tiers.
  Plugin-free tier (runs whenever the satellite is staged): `FourSatelliteSoakTests` fans the
  looped `FrameCorpus` (`combat-slice{,2}` × N) down to four satellites (two `--consume` replicas
  + two `--sink`) and asserts **N× oracle YOU parity**, **zero steady-state egress drops**
  (`SatelliteHost.EgressCounters`), host→fold **latency p99** (QPC `FCT_MARK:` rawlog markers via
  `--verify-latency`, recorded + generous ceiling), and per-satellite **working set**;
  `AudioSinkPrecedenceTests` pins cross-satellite most-recent-sink-wins + `UNREGISTERSINK`
  fallback; `RepositoryCadenceTests` asserts the `repository` stream fans losslessly at the pinned
  250 ms cadence. Full tier **[plugin-gated]**: `FourRealPackagesTests` spawns the real parser +
  OverlayPlugin + Triggernometry + Discord-Triggers as **four distinct pids** under the production
  router, driven by the looped corpus, and asserts OverlayPlugin's MiniParse `CombatData` equals
  the oracle `ExportVariables` baseline on the terminal encounter (needs all four plugins
  installed; skips otherwise). `FCT_SOAK_ITERATIONS` overrides N for a longer soak.
- **Dist-tree e2e gate** (`Fct.Integration.Tests`, ISOLATION-PLAN P9c): `DistTreeGateTests` proves the
  *staged* `dist\<mode>\` tree runs the topology and holds parity, not just that its files exist. Opt-in
  via `FCT_DIST_MODE` (`dist\` is git-ignored, published by `dotnet run --project build -- <mode>`);
  points `FCT_INSTALL_DIR` at the dist tree so `SatelliteHost` resolves
  `dist\<mode>\satellite\Fct.LegacyHost.exe`, spawns a plugin-free `--consume` replica, fans one pass of
  the committed `FrameCorpus` down to it, and asserts its YOU total equals the real-ACT oracle baseline
  (oracle → host engine → consumer replica, from the shipped tree). Skips cleanly when `FCT_DIST_MODE`
  is unset or the tree is absent; wired behind `test.ps1 -Dist` (publish + arm in one invocation).

## Determinism note

The engine's damage-type routing tables are the ones ACT runs with for FFXIV (swing-type → bucket
links, bucket names). `Fct.Compat.Act.Tests` sets them up in a fixture so the aggregation is
exercised with no live game or plugin; the exact values are held to the real ACT binary by the
differential ACT-engine compat below (`ExportVarsCompatTests`). The
canonical regression vector is **10 hits of 1000 over 9 s, every 3rd a crit ⇒ damage 10000,
crit% 40, encdps 1111** — asserted both directly (unit) and through the live satellite
(integration).

## Corpus ACT-engine parity (mass-compare)

`tools/mass-compare` scales the differential ACT-engine compat (below) from the two committed
fixtures to an **entire folder of months of `Network_*.log` files**. The real `FFXIV_ACT_Plugin` is
the **sole parser**; this harness exercises only our **engine**, fed the plugin's own swings, and
holds its `ExportVariables` payload to the real ACT binary corpus-wide. Three stages, all over the
same logs in chronological order as one continuous plugin session (combatant-name/owner/combat state
carries across day-boundary rotations exactly as ACT sees it live):

1. `Fct.LegacyHost.exe --mass-oracle <logFolder> <outFolder>` loads the real `FFXIV_ACT_Plugin`
   **once** and writes its authoritative `MasterSwing` stream per file (`<name>.oracle.tsv`).
2. `tools/act-oracle ActOracle --folder <outFolder>` aggregates each `<name>.oracle.tsv` through the
   **real ACT binary** and dumps its `ExportVariables` to `<name>.oracle.exports.tsv` (baseline).
3. `Fct.LegacyHost.exe --mass-engine-exports <outFolder>` aggregates the **same** swings through our
   `Fct.Compat.Act` engine to `<name>.engine.exports.tsv` (under test).

`MassCompare <outFolder>` then joins the two payloads per file/combatant/key and reports the
exact-match rate plus the summed numeric magnitude per key. Run all of it with
`tools/mass-compare/run.ps1` (needs the ACT install for the baseline). Outputs (`*.oracle.tsv`, the
two `*.exports.tsv`, `exports-summary.txt`, `exports-diff.txt`) go to `tmp/mass-compare/` —
gitignored, real names, never committed.

Because both engines consume the **identical** plugin swings, this isolates our aggregation: any
divergence is a real bug in our engine, never a parser difference (the plugin's DoT/HoT/shield potency
synthesis is already baked into the swings both engines receive). On the committed fixture corpus it
diffs **100.000%** (1,452 key/value pairs, 0 ours-only / 0 act-only). The same harness run over private
play-log corpora holds corpus-wide: **460,432 key/value pairs across 208 `Network_*.log` files** (three
players, patches 27109→30203, content from solo/city downtime through dungeons, Extremes, two Savage
tiers, and Ultimate) all match exact — 0 ours-only / 0 act-only, every per-key numeric Σ bit-identical.

The baseline (`ActOracle`) scales its watchdog to the file count, so a months-long single session
(millions of swings) finishes instead of being silently truncated, and compiles into the run's
`<outFolder>` so concurrent per-corpus runs don't collide on the shared binary.

## End-to-end live route (recorded logs)

`ReplayRouteTests` exercises the whole pipeline the product runs at play time, on recorded data (no
live game): the staged satellite's `--replay <log>` mode feeds each line of an anonymized slice
through the **real FFXIV plugin**, which parses and drives our ACT facade (`SetEncounter`/
`AddCombatAction`); the parse clock advances per line so ACT's idle-end splits the stream into
encounters; each completed encounter's `ExportVariables` (the strings OverlayPlugin/cactbot read) are
dumped. The test asserts the slice splits into ≥2 encounters, each exposes the consumer strings
(`encdps`, `maxhit` as `Skill-Damage`, …), and the per-encounter damage **sums back to the
bit-perfect aggregate** (`YOU` = 2692084) — i.e. the idle-split conserves the totals. Skips cleanly
without the staged satellite or the installed plugin.

## Differential ACT-engine compat (ours vs the real ACT binary)

The layer above parsing is **aggregation**: the real plugin calls `AddCombatAction(MasterSwing)` into
ACT, which tallies `EncounterData`/`CombatantData` and exposes the `ExportVariables`/properties
OverlayPlugin/cactbot read. `Fct.Compat.Act` is our from-scratch reimplementation of that engine, held to the
**real `Advanced Combat Tracker.exe`** bit-for-bit:

- **Oracle** (`tools/act-oracle`) loads the real ACT binary in-process, installs the exact damage-type
  routing tables the FFXIV plugin registers (`ACT_UIMods`), replays the captured `MasterSwing` stream
  (`combat-slice.oracle.tsv`) through the real `EncounterData`/`CombatantData`, and dumps every
  per-combatant + encounter aggregate to `combat-slice.aggregate.tsv` (committed baseline).
- **Diff test** (`AggregateCompatTests`) feeds the same stream through our facade and asserts every
  field equals the baseline — `Damage`, `Healed`, `Hits`, `CritHits`, `CritDamPerc`, `EncDPS`,
  `EncHPS`, `MaxHit`, `DamagePercent`, the allied-party `Damage`/`NumAllies`, durations, etc. It runs
  over two independent real slices (`combat-slice`, a 2-ally mixed pull; `combat-slice2`, an 8-ally
  full-party burst with heavy healing), both bit-for-bit exact.

On top of the aggregates, `ExportVarsCompatTests` holds the **`ExportVariables` strings** themselves
— the exact text OverlayPlugin/cactbot read per combatant — to ACT. OverlayPlugin's MiniParse
iterates the *whole* `CombatantData.ExportVariables` dictionary, so we register and verify the
**full ACT-default key set** (~80 keys × 11 combatants per slice): the oracle dumps ACT's own
`FormActMain.CombatantFormatSwitch` output for each into `combat-slice*.exportvars.tsv`, and the test
asserts our `CombatTables` formatters reproduce every one. That pinned ACT's exact formatting:
`encdps` is `EncDPS.ToString("F")` (`1144.65`), `ENCDPS` rounds (`1145`), `crithit%` uses `"0'%"`,
`maxhit` is `"AttackType-Damage"` (`Primal Rend-89674`), the suffixed keys use ACT's K/M/B/T/Q
`CreateDamageString` (`damage-*=2.69M`, `DAMAGE-k=2692`), and zero-duration `dps`/`tohit` are verbatim
`NaN`. (Four EQ-legacy keys — `NAME`/`crittypes`/`threatstr`/`threatdelta` for the anchor — throw in
the headless harness and are excluded from the baseline; our facade still registers them.)

The engine reproduces several non-obvious ACT behaviours exactly: `blockIsHit` defaults **true**;
per-combatant `StartTime`/`EndTime` span all outgoing swings via the "`All … (Ref)`" bucket (not
just damage); the encounter's allied party comes from ACT's friend/foe graph (`GetAllies`, including
its index-instability quirk over the growing `SortedList`); and `DamagePercent`/`HealedPercent` read
`--` for non-allies. Regenerate the baseline (needs the ACT
install) with `tools/act-oracle/build-and-run.ps1`; the committed baseline keeps the diff test
running everywhere without ACT.

Regenerate the oracle fixture (needs the ACT install):

```powershell
bash tests/fixtures/make-slice.sh "<a Network_*.log>" tests/Fct.Compat.Act.Tests/fixtures/combat-slice.log 600
# build the satellite, then:
src/Fct.LegacyHost/bin/Debug/net48/Fct.LegacyHost.exe --parse-oracle `
  tests/Fct.Compat.Act.Tests/fixtures/combat-slice.log 100000 `
  tests/Fct.Compat.Act.Tests/fixtures/combat-slice.oracle.tsv
```
