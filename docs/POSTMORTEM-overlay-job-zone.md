# Post-mortem — OverlayPlugin player-job & zone go blank in the isolated topology

**Status:** root-caused, fix pending. Discovered on a live run (2026-07-06, patch 30203 /
Dawntrail, OverlayPlugin 0.19.101 + cactbot 0.37.3 + Ember Overlay) after the P8/P9 isolation
work shipped OverlayPlugin in its own consumer satellite.

This is a **retrospective** — the one document in `docs/` that records history on purpose. The
facts it establishes about the *current* contract belong in
[`DATA-FLOW.md`](DATA-FLOW.md)/[`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md); the truth-up
tasks are listed at the end.

---

## 1. Summary

Two overlay-visible values broke when OverlayPlugin moved into its own parser-free consumer
satellite: the **player's job** rendered as the "Limit Break" placeholder icon, and the **zone
name** was blank. DPS/encounter numbers were correct throughout.

Both are the *same class of defect*: **OverlayPlugin consumes several values through data paths
that the isolation work never modeled or fed** — paths that are invisible under the two-axis
aggregation-parity gates, so every phase (P5–P9) stayed green while shipping the gap. The
aggregation authority (invariant §1) was reproduced perfectly; the **plugin-contributed
metadata layer and the transient-state log-line layer** on top of it were not.

Neither is a code regression in the sense of "a line changed and broke." They are **contract
omissions**: the consumer-side facade reproduces *part* of what the real FFXIV_ACT_Plugin does to
ACT, and the missing parts happen to be the ones no test could see.

---

## 2. What was observed (live evidence)

Queried directly against the running OverlayPlugin Fleck server (`ws://127.0.0.1:10501`) mid-combat:

| Probe | Result | Reading |
|---|---|---|
| `getCombatants` (OP's own memory scan) | player `Kothos Dullmill`, **Job 21 (WAR)**, Lv100 | OP's memory scan works; the *real* job is available in-satellite |
| `CombatData` per-combatant keys | **no `Job` key**; player row named `YOU` | the DPS table carries no job, and not the real name |
| subscribe `ChangeZone` (a cached event) | **nothing replayed** | OP has *never* received a zone → blank |
| subscribe `CombatData` / `LogLine` | both flow | aggregation + packet-derived lines are fine |

So the symptom is not "OverlayPlugin is broken" and not "memory scanning failed" — the job is
sitting right there in `getCombatants`. The overlays that render the job (cactbot/Ember) read it
from `CombatData`/state events, where it is **absent**.

---

## 3. Bug A — the player job

### 3.1 How the value is produced under real ACT

The per-combatant `Job` in MiniParse `CombatData` is a **FFXIV_ACT_Plugin contribution**, not an
ACT-core value and not an OverlayPlugin value:

1. The plugin writes the source combatant's job abbreviation into **every swing's tags** —
   `MasterSwing.Tags["Job"]` (`FFXIV_ACT_Plugin.Parse/ReportCombatData.cs:72-75`, also `:157`,
   `:239`, `:329`; `"Adv"` → `""`).
2. The plugin's `ACT_UIMods.UpdateACTTables()` registers a `CombatantData.ExportVariables["Job"]`
   `TextExportFormatter` (`FFXIV_ACT_Plugin/ACT_UIMods.cs:1918-1929`) whose body is
   `Data.GetColumnByName("Job")` → `Data.Job()`.
3. `CombatantDataExtension.Job()` (`FFXIV_ACT_Plugin/CombatantDataExtension.cs:10-20`) resolves it
   by walking the combatant's outgoing attack types and returning the first non-blank
   `Items[0].Tags["Job"]`.
4. OverlayPlugin's MiniParse loop is **generic** — `foreach (var kv in CombatantData.ExportVariables)
   valueDict.Add(kv.Key, kv.Value.GetExportString(ally, ""))`
   (`OverlayPlugin.Core/EventSources/MiniParseEventSource.cs:321,351`). It adds only three keys of
   its own (`overHeal`/`damageShield`/`absorbHeal`, `FFXIVExportVariables.cs`). It **copies** `Job`
   out; it never produces it.

### 3.2 What our topology does

- **The tag data already crosses the bridge.** The parser satellite taps `BeforeCombatAction` and
  forwards `s.Tags` verbatim (`Fct.LegacyHost/BridgeForwarder.cs:272-275`); the wire codec encodes
  typed tags (`Fct.Bridge.Contracts/GameEventFrame.cs` `EncodeSwing`/`TryDecodeSwing`); the consumer
  rebuilds `ms.Tags` (`Fct.LegacyHost/Program.cs:681-686` `FoldConsume`). So the consumer replica's
  `MasterSwing`s **carry `Tags["Job"]`.**
- **The formatter that surfaces it is not registered.** The consumer replica stands up the engine
  with `EngineTables.Install()` (`Fct.Aggregation/EngineTables.cs`), which reproduces the plugin's
  `ACT_UIMods` **damage-type routing tables** (the part that makes the DPS numbers correct) — but
  **not** its metadata columns. There is no `CombatantData.ExportVariables["Job"]` registration
  anywhere in `Fct.Aggregation`. `CombatTables.Setup()` registers only ACT-core defaults.

**Root cause A:** `EngineTables` reproduced `ACT_UIMods`'s *numeric* contribution and omitted its
*metadata* contribution. The job data flows all the way into the replica's swings and is then
dropped on the floor because no export formatter reads it → `CombatData` has no `Job` → overlays
render job 0 → the Limit Break placeholder icon.

---

## 4. Bug B — the zone (and player identity)

### 4.1 How the value is produced under real ACT

OverlayPlugin derives **zone** and **primary-player identity** from **transient parsed log lines**,
via ACT's `BeforeLogLineRead`, not from the SDK events:

- Zone ← log line **type 01** `ChangeZone` (`01|ts|zoneIdHex|zoneName|…`) —
  `OverlayPlugin.Core/EventSources/FFXIVOptionalEventSource.cs:96-108` reads `line[2]`/`line[3]`.
  The SDK `IDataSubscription.ZoneChanged` is used only to clear FATE/CE caches
  (`LineFateControl.cs:85`), never to emit the overlay `ChangeZone` event.
- Player identity ← log line **type 02** `ChangePrimaryPlayer` (same event source) — tells the
  overlay who `YOU` is, which is how it associates the DPS row with a memory-scanned job.

### 4.2 What our topology does

- The consumer re-raises fanned `RawLogLine`s through `BeforeLogLineRead`
  (`Program.cs:700-710`), so *live* type-01/02 lines work — confirmed live: **changing zone
  populated the zone name.**
- But those lines are **one-shot events on the `rawlog` firehose with no replay.** The
  snapshot-priming step (`Fct.Host/SatelliteHost.cs:388-431` `BuildPrimeEvents`) primes the
  **SDK forms** of state (`ZoneChanged`, `PrimaryPlayerChanged`, roster, repository) for a
  late-joining subscriber — the forms OverlayPlugin **doesn't** use for zone/identity — and primes
  **no** `RawLogLine`.

**Root cause B:** you log in and are already standing in a zone before the overlay satellite
subscribes, so the type-01/02 lines fired long ago. The host primes the SDK equivalents, which
OverlayPlugin ignores for these values, and never replays the log lines OverlayPlugin actually
reads → the overlay has no zone and cannot bind `YOU` to a job until the *next* live zone change.

---

## 5. The meta root cause — an incomplete consumer model

Both bugs trace to one modelling gap. [`DATA-FLOW.md`](DATA-FLOW.md) §8 maps every **SDK/ACT-hub
seam** onto a host pipe and the isolation work reproduced each faithfully. But OverlayPlugin sources
several values through paths **outside that contract model**, and those were the paths left unfed:

1. **The plugin-contribution metadata layer.** `ACT_UIMods` registers columns/exports (`Job`, and
   potentially others) that are *not* ACT-core and *not* OverlayPlugin's — they are the plugin
   decorating ACT's model. `EngineTables` reproduced only the routing subset of `ACT_UIMods`. The
   consumer replica must reproduce **all** of `ACT_UIMods` that a consumer reads, not just the part
   that moves the DPS number.
2. **Transient-state derivation from log lines.** Zone and primary-player are established once, via
   type-01/02 lines. The priming model assumed the SDK event forms were interchangeable with the
   log-line forms for late joiners. For OverlayPlugin they are not.
3. **OverlayPlugin's own game-memory scanning** (job/HP/position/enmity/target). This one happens to
   work — the parser forwards the game PID (`GameProcessChanged`), the consumer materializes
   `GetCurrentFFXIVProcess()`, and OP opens+scans the game itself — but it was never in the model
   either, and it is why `getCombatants` was correct while `CombatData` was not (two independent job
   sources, only one fed).

`DATA-FLOW.md` §4 lists OverlayPlugin's taps as *SDK discovery, SDK events, MiniParse aggregate,
raw packets*. Paths (1)–(3) are absent from that list. The build was faithful to the model; the
model was incomplete for the consumer side.

---

## 6. Why every isolation gate stayed green

This is the part that matters for "how did this ship." The isolation gates were real and passed
honestly — they were **structurally blind** to both defects:

- **The parity oracle is ACT-core, not the plugin-in-the-loop.** `tools/act-oracle` runs the
  `Advanced_Combat_Tracker` binary alone over captured swings; it never loads FFXIV_ACT_Plugin, so
  it never runs `ACT_UIMods`. The committed baseline `combat-slice.exportvars.tsv` therefore has
  **no `Job` key** (verified: 0 occurrences; its key set is the ACT-core defaults only). Our engine
  also has no `Job`. The two-axis parity ("our engine == real ACT fed identical swings") matched
  perfectly — a **false green**: the reference was blind to exactly the thing we omitted, because
  the omission was the plugin's contribution and the oracle excludes the plugin.
- **The overlay exit gate asserts a numeric subset only.** `OverlaySatelliteTests` (the P8 gate)
  and `FourRealPackagesTests` (P9b) assert `CombatData` `damage`/`damage%`/`DURATION`/`encdps` on
  `YOU` and the encounter (`OverlaySatelliteTests.cs:158-168`). They never assert the full
  key set, never assert `Job`, never assert zone, never assert player identity.
- **The fixtures cannot exercise the state path.** The gates fan aggregation-only frame fixtures
  (`combat-slice*.frames.tsv` = swings + lifecycle). These deliberately contain **no** type-01/02
  log lines (the gate comment even relies on "no rawlog frames → no idle-split"), so the
  transient-state → `BeforeLogLineRead` path is structurally un-exercisable, and priming a late
  joiner with the current zone was never a test subject.
- **The stand-in self-verify counts discovery, not output.** `StandInVerification` measures
  found/SDK-bound/log-line-count/combatant-count/packet-count/real-IoC — never a rendered
  overlay value.
- **The live acceptance bar is coarse.** `GO-LIVE.md` A1 accepts "OverlayPlugin overlays render"
  and "DPS/overlays resume" — not "the player's job icon is correct" or "the zone name shows." A
  human watching a DPS meter tick would pass A1 without noticing a blank job icon.

In short: the gates proved the **aggregation authority** (the stated invariant §1) exhaustively,
and nothing proved the **consumer's actual rendered outputs** end-to-end against a plugin-in-the-loop
reference.

---

## 7. The fixes

- **A (job):** register `CombatantData.ExportVariables["Job"]` in `EngineTables.Install()` (the
  consumer replica + host engine both call it), reading the combatant's swing `Tags["Job"]` exactly
  as `CombatantDataExtension.Job()` does (`AllOut.Values[i].Items[0].Tags["Job"]`, first non-blank).
  The tag data already flows; this only surfaces it. Keep it in `EngineTables` (the "what
  `ACT_UIMods` installs" layer), not `CombatTables` (ACT-core defaults), so core-parity fixtures are
  untouched.
- **B (zone/identity):** in `SatelliteHost.BuildPrimeEvents`, when a subscriber takes `rawlog`,
  synthesize + prime a type-01 `ChangeZone` and type-02 `ChangePrimaryPlayer` `RawLogLine` from the
  current snapshot, so a late-joining consumer converges without a manual zone change.

---

## 8. Preventive measures (truth-up + new gates)

- **Fix the oracle blind spot.** The parity oracle must include the plugin's `ACT_UIMods`
  contribution for any consumer-visible export the overlays read (at minimum `Job`), or the
  overlay gate must assert `CombatData`'s **full key set** against a plugin-in-the-loop capture —
  not the ACT-core `exportvars.tsv`. A parity oracle that excludes the plugin cannot gate a value
  the plugin produces.
- **Add a late-join priming gate.** A consumer that subscribes *after* a zone/primary-player change
  must converge to the current zone + player — assert an emitted `ChangeZone`/`ChangePrimaryPlayer`
  from priming with no live line.
- **Assert rendered outputs, not just numbers.** Extend the overlay gate to assert per-combatant
  `Job` and the `ChangeZone` event on the WS.
- **Broaden `GO-LIVE.md` A1** to name job-icon correctness and zone display explicitly.
- **Truth-up the contract docs.** [`DATA-FLOW.md`](DATA-FLOW.md) §4/§8 and
  [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) §4/§14 should record: (a) `ACT_UIMods` metadata
  columns (`Job`) as a consumer-replica responsibility distinct from the routing tables; (b)
  OverlayPlugin's zone/primary-player derivation from type-01/02 log lines (vs the SDK events);
  (c) OverlayPlugin's own game-memory scanning as a real, PID-bootstrapped data path.

---

## 9. One-line lessons

- **Reproduce the whole `ACT_UIMods`, not just the numbers** — a consumer replica that omits the
  plugin's metadata columns is silently lossy.
- **Prime the form the consumer actually reads** — SDK-event and log-line forms of the same state
  are not interchangeable for a late joiner.
- **A parity oracle that excludes the plugin cannot gate a plugin-produced value** — matching it is a
  false green.
- **Gate rendered outputs, not just the aggregation authority** — DPS being right proved the
  invariant, not the product.
