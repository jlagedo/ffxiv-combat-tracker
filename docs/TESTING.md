# Testing

Automated tests live under `tests/`. Run everything with:

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
| `Fct.Compat.Act.Tests` | net48 | The clean-room ACT aggregation engine: `Dnum`, `MasterSwing`, `AttackType`/`CombatantData`/`EncounterData` math, the `ExportVariables` contract OverlayPlugin/cactbot read, `SettingsSerializer` XML round-trip, and the **differential ACT-engine compat** (`AggregateCompatTests`, below). |
| `Fct.App.Tests` | net10 | The bridge handshake parser (`SatelliteProtocol`): READY detection, x64 gating, HWND hex parsing. |
| `Fct.Parser.Native.Tests` | net10 | The structural `NetworkLogLine` parser (unit tests) plus a real-log smoke test over an installed `Network_*.log`. |
| `Fct.Integration.Tests` | net10 | Black-box end-to-end: launches the staged net48 satellite, checks the handshake/HWND, verifies the in-process self-test aggregation, and runs the **full live route** on a recorded slice (`--replay`, below). |

## What runs vs. skips

The unit/parser tests are self-contained and always run. The data-dependent tests skip
cleanly when their prerequisites are absent, so a clean checkout (and CI without an ACT
install) stays green:

- **Real-log smoke** (`Fct.Parser.Native.Tests`) needs a `Network_*.log`. It reads the newest
  one from `%APPDATA%\Advanced Combat Tracker\FFXIVLogs`, or from the path in the
  `FCT_FFXIV_LOGS` environment variable. Skips if none is found.
- **Satellite integration** (`Fct.Integration.Tests`) needs the satellite staged
  (`dotnet build src/Fct.App/Fct.App.csproj`); `test.ps1` does this first. The handshake and
  HWND tests run whenever the satellite is staged. The "plugin Started" and self-test
  assertions also need `FFXIV_ACT_Plugin.dll` installed under
  `%APPDATA%\Advanced Combat Tracker\Plugins`; they skip otherwise.

## Determinism note

The engine's damage-type routing tables are the ones ACT runs with for FFXIV (swing-type → bucket
links, bucket names). `Fct.Compat.Act.Tests` sets them up in a fixture so the aggregation is
exercised with no live game or plugin; the exact values are held to the real ACT binary by the
differential ACT-engine compat below (`ExportVarsCompatTests`). The
canonical regression vector is **10 hits of 1000 over 9 s, every 3rd a crit ⇒ damage 10000,
crit% 40, encdps 1111** — asserted both directly (unit) and through the live satellite
(integration).

## Differential parser compat (ours vs ACT)

`Fct.Parser.Native.ActionEffectDecoder` decodes FFXIV ActionEffect (21/22) lines into
damage/heal values. It is validated **directly against ACT's own parse** of the same lines:

- **Oracle capture** — the satellite's `--parse-oracle` mode replays a real log through the
  real FFXIV_ACT_Plugin (which subscribes to `FormActMain.BeforeLogLineRead`) and records
  every `MasterSwing` the plugin produces. That is ACT's authoritative parse.
- **Fixtures** — `tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log` is an anonymized
  real combat slice (name fields blanked; the decode reads only the effect bytes), and
  `combat-slice.oracle.tsv` is ACT's captured parse of it. Regenerate with
  `tests/fixtures/make-slice.sh` + the satellite (see below).
- **Diff test** (`ParseCompatTests`) decodes the slice and compares value multisets to the
  oracle.

Two layers are tested:

`ActionEffectDecoder` is the pure byte decode (one line → effect values). `CombatLogParser`
is stateful: it tracks the primary player (`02`), combatant names (`03`/`04`) and combat
state, resolving whole `MasterSwing`s (attacker/victim names, `InCombat`).

Current compat on the slice:
- **Damage `MasterSwing`s: full-field exact (100%).** Every damage swing ACT produces matches on
  **amount** (incl. the `>65535` `Flags2&0x40 ? Flags1<<16` transform), **crit**, **miss/block/
  parry**, **attacker/victim names**, **ability name**, and **damage-type string** — 443/443 on
  the fixture. The only excluded field is swing-type for NPC auto-attacks (below).
- **Swing-type: conservative and correct.** Player auto-attacks (action id `0x07`) classify as
  auto vs ability with no false positives. NPC auto-attacks use ids whose *category* ACT knows
  only from its bundled action-category data (3/443) — pinned as the sole swing-type gap.
- **Heals: every ACT-reported heal reproduced (0 missing)** on value + crit. Exact heal *count*
  needs ACT's combat-end detection (`StopCombat` → `InCombat` false mid-fight); a few heals are
  proc-attributed (proc-source decoding pending), so heal names are not yet asserted.

Ability names and the damage-type/element enums are FFXIV game data, not derived: the skill
table is dumped from the plugin's resource via `Fct.LegacyHost.exe --dump-skills`
(`IDataRepository.GetResourceDictionary`), committed as `fixtures/skills.tsv`; the
`DamageType`/`ElementType` enums are read from `ffxiv_act_plugin.resource.dll`.

Remaining toward full parity (tracked): ACT's **action-category** data (NPC auto-attack
swing-type), **combat-end detection** for exact heal/resource counts, **proc-source** decoding, and
**damage shields** (log type `11`, routed into "Healed (Out)" — not yet emitted). The differential
harness measures each as it lands.

## Massive parse compat (every log, ACT vs ours)

`tools/mass-compare` scales the slice-level parser diff to an **entire folder of months of
`Network_*.log` files**. Two stages, both over the same logs in chronological order as one
continuous stream (combatant-name/owner/combat state carries across day-boundary rotations exactly
as ACT sees it):

1. `Fct.LegacyHost.exe --mass-oracle <logFolder> <outFolder>` loads the real `FFXIV_ACT_Plugin`
   **once** and writes ACT's authoritative `MasterSwing` stream per file (`<name>.oracle.tsv`).
2. `Fct.LegacyHost.exe --dump-tables <outFolder>` exports the FFXIV **game-data** tables the public
   API does not expose: `actions.full.tsv` (id → name + `ActionCategory`) and `statuses.full.tsv`
   (id → name).
3. `MassCompare <logFolder> <oracleFolder> <outFolder>` parses the same logs with the clean-room
   `CombatLogParser` and bag-diffs **every** swing on the full tuple
   `(swingType, crit, amount, special, attackType, attacker, damageType, victim)`, reporting a
   per-swingType breakdown.

Run all three with `tools/mass-compare/run.ps1`. Outputs (per-file oracle/ours TSVs, `report.tsv`,
`details.txt`, `summary.txt`, `type-samples.txt`) go to `tmp/mass-compare/` — gitignored, real
names, never committed.

`CombatLogParser` reproduces every swing type ACT emits, not just damage/heals:

- **Damage / auto-attack (0/2)** — auto-vs-ability typing and **Limit Break** attribution use the
  dumped `ActionCategory` table (ACT's authority: `GetActionCategory == AutoAttack` / `LimitBreak`),
  covering NPC/pet/ranged autos whose ids are absent from the display-name list.
- **Heals (4), Power/MP (7), Status (8), Action (1), Cancelled casts (2/4/1)** — emitted from the
  `21/22` effect entries / `26` status-add / `23` cancel lines, matched to ACT's output (the oracle).
- **Combat window** — ACT's exact `FormActMain` idle-end model: `LastKnownTime - LastHostileTime >
  6s` (default `nudIdleLimit`) ends combat, checked per line against log timestamps. Every emitted
  swing that runs `SetEncounter` (damage, heal, DoT/HoT, shield) refreshes the window; Status(8) and
  Action(1) are in-combat-gated but bypass it. This is deterministic from the log and gates the
  ~2M status / ~0.9M HoT swings correctly.
- **Pet/summon owner attribution** — `03` field 6 `OwnerID`, field 4 `Job`: a true pet (job 0)
  collapses to the bare owner name, an owned non-pet (e.g. `G'raha Tia`) renders `"name (owner)"`.

**ACT does not parse or estimate DoTs — the producer does.** `ACT-decompiled` has no DoT/potency/tick
logic and `FormActMain.AddCombatAction` filters nothing; it sums every swing. Every DoT/HoT/shield
*value* in the live output is the plugin's (producer) synthesis, not ACT's — so those divergences are
producer differences, not ACT gaps. Our parser emits each type-`24` tick from the log's own combined
amount (one tick per target per server tick), dropping ticks that are not player-outgoing: the
`0xE0000000` null actor, sourceless ticks, and **DoTs on a player victim** (`0x10xxxxxx` target —
incoming enemy/environment damage whose combined-tick source is a misleading rotating player id; the
plugin attributes 109 such swings out of millions). Corpus output parity (68 logs): auto **100.000%**,
ability **99.993%**, **`Damage` bucket (auto+ability+DoT = player DPS) 99.917%**, heal **100.258%**,
HoT total **99.681%**, healed-excl-shields **99.975%**, DoT **97.327%**. The residual DoT value is
per-tick log-real-amount vs the plugin's flat potency estimate — small noise centered on parity (where
they diverge the plugin usually *under*-counts fast multi-source ticks, so our log value is the more
accurate one). **Damage shields** (`11`, `maxHP × potency`) are not logged and not emitted (the entire
healing residual). Per-swing bag-diff is the wrong yardstick for DoT/HoT/shield; use the output
comparison (see [`ACT-OUTPUT-PARITY-GAPS.md`](ACT-OUTPUT-PARITY-GAPS.md)). The old `PotencySimulator.cs`
(a port of the plugin's DoT/shield synthesis) stays removed.

Over the local corpus (67 logs, ~5.9M swings) the parser reproduces ACT's output, bit-for-bit on the
strict tuple: auto **99.98%**, ability **99.85%**, power **99.9%**, status **99.4%**, heal **95.1%**,
action **92.4%**, and the real (log-amount) ground-AoE DoT/HoT exactly. The remaining gaps are
**combat-window boundary** precision (ACT's idle-end timing) — `ACT-decompiled` behavior, which is
where any further in-scope work lives.

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
OverlayPlugin/cactbot read. `Fct.Compat.Act` is our clean-room rebuild of that engine, held to the
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

This pinned several behaviours our first-cut engine got wrong, each now matched exactly: `blockIsHit`
defaults **true**; per-combatant `StartTime`/`EndTime` span all outgoing swings via the
"`All … (Ref)`" bucket (not just damage); the encounter's allied party comes from ACT's friend/foe
graph (`GetAllies`, including its index-instability quirk over the growing `SortedList`); and
`DamagePercent`/`HealedPercent` read `--` for non-allies. Regenerate the baseline (needs the ACT
install) with `tools/act-oracle/build-and-run.ps1`; the committed baseline keeps the diff test
running everywhere without ACT.

Regenerate the oracle fixture (needs the ACT install):

```powershell
bash tests/fixtures/make-slice.sh "<a Network_*.log>" tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 600
# build the satellite, then:
src/Fct.LegacyHost/bin/Debug/net48/Fct.LegacyHost.exe --parse-oracle `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 100000 `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.oracle.tsv
```
