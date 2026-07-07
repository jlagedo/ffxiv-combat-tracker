# Pipeline Completeness Plan — complete surfaces, differential gates

**The tracking document for closing the consumer data-completeness gaps** found by
[`POSTMORTEM-overlay-job-zone.md`](POSTMORTEM-overlay-job-zone.md) and the follow-up full-surface
audit. Phase checkboxes are the live status.

Companion docs: [`DATA-FLOW.md`](DATA-FLOW.md) (the upstream contract), [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md)
(the binding surface), [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md) (topology invariants — this plan
inherits all of them), [`TESTING.md`](TESTING.md) (the parity harnesses the new gates extend).

---

## 1. The design principle: carry complete surfaces, curate only at the views

Field-by-field completeness work does not converge: a datum crosses the pipe only if someone
hand-builds a path for it, so every miss is discovered by a consumer breaking, and each fix adds a
bespoke frame + codec case + prime entry + curated gate. This plan replaces that shape with three
**complete carriers** and two **differential gates**:

| Carrier | What crosses | Why it is complete by construction |
|---|---|---|
| **The verbatim line stream** | every log line the plugin writes, byte-for-byte, from **one tap** on the producer facade's log-read seam | the tap sits on the identical seam real ACT consumers bind (`FeedLine → Before/OnLogLineRead`), downstream of the plugin's own log writes — any line type the plugin emits, today or after a future patch, crosses automatically |
| **The swing/lifecycle stream + shared engine binary** | every `MasterSwing` field + `Tags` verbatim, plus encounter lifecycle | already the design (see CLAUDE.md load-bearing facts); the only work is registering the plugin-contributed `ExportVariables` in the shared binary (§5 P5) |
| **The session-state record** | one `SessionStateChanged` record (env: version/language/region/clock/chat-log) + party size on `PartyChanged` | one additive key=value frame; adding a state field is a record property + a codec key, never a new frame/stream/prime path |

Completeness is **proven by diffing whole surfaces against the real stack**, not by enumerating
fields:

1. **Line-stream diff** — a `rawlog` consumer's `OnLogLineRead` must receive the replayed corpus
   lines byte-for-byte, in order.
2. **ExportVariables/repository diff** — the engine's **enumerated** `ExportVariables` key set +
   values must equal the plugin-in-the-loop oracle baseline; the consumer `IDataRepository` scalars
   must be forwarded values, never stubs.

**Success criterion: a future missing datum is a red diff in CI, not a postmortem.**

Typed events (`ZoneChanged`, `PartyChanged`, …) remain the modern-API views and the state carriers —
they are derived conveniences layered on the complete carriers, never the completeness mechanism.
Typed state always comes from SDK taps; log lines stay opaque (we never decode them — the one-shot
line cache keys on the frame's already-typed `LogMessageType` field only).

## 2. The three constraints (every phase is checked against all three)

1. **The host knows no plugin.** Nothing FFXIV_ACT_Plugin-specific enters `Fct.Host`/`Fct.Engine`:
   every datum ships as a typed domain event/record in `Fct.Abstractions` (or an opaque routed frame
   in `Fct.Bridge.Contracts`), routed by stream name. The `LogMessageType` wire taxonomy in
   `Fct.Abstractions/LogMessageType.cs` is a wire contract, not plugin identity.
2. **The host pipe is the source of truth.** Every datum a consumer reads crosses the host —
   parser satellite → typed frame → host bus/session state → fan-out → consumer-facade projection.
   No fix may satisfy a consumer satellite-locally from data that is not on the pipe; the facade is
   a projection, never an origin. Late joiners converge from host-primed state, not from luck.
3. **Modern-API parity.** Every datum added for a legacy consumer must be reachable by a native
   net10 plugin: a `Fct.Abstractions` event/record on the bus, host session state exposed through
   the snapshot/service surface, or (for opaque legacy lines) the `RawLogLine` bus stream.

**Per-phase exit checklist:** ☐ no plugin identity in host code · ☐ datum observable on the host
bus · ☐ datum observable from a native plugin · ☐ consumer facade projects it · ☐ late joiner
converges (where stateful).

## 3. Locked decisions

- **The producer facade's log-read seam is the sole `RawLogLine` source.** The facade tails the
  plugin's `Network_*.log` and raises `Before/OnLogLineRead` per line
  (`Fct.Compat.Act/FormActMain.cs:329-429` — `OpenLog → StartLogTail → FeedLine`), exactly as real
  ACT does. `BridgeForwarder` subscribes `OnLogLineRead` like any ACT consumer and forwards
  `args.logLine` **verbatim** — no prefix reconstruction, no fabricated timestamps, and 249/250/253
  cross through the same tap as everything else. The SDK `LogLine` tap
  (`BridgeForwarder.cs:130,215-216` — ChatLog-only, chat-channel code miscast to `LogMessageType`,
  malformed body) is **removed**; the SDK `ParsedLogLine` event is **not** used (it delivers a
  body-only `message` that would need prefix/timestamp reconstruction, and misses 249/250/253).
- **One additive `STATE` frame carries all one-shot environment state.** Wire form is `key=value`
  pairs using the codec's existing `Enc`/`Dec` escaping (the codec stays JSON-free by design,
  `GameEventFrame.cs:15`); the decoder ignores unknown keys and defaults missing ones, so adding a
  state field never breaks recorded fixtures or needs a new tag/stream/prime path.
- **`PartySize` rides the `PARTY` frame**, not `STATE` — it must stay atomic with the member list
  it arrives with (SDK `PartyListChanged(partyList, partySize)`).
- **Plugin-contributed `ExportVariables` register in `EngineTables.Install()`, never
  `CombatTables.Setup()`.** `Install()` calls `CombatTables.Setup()` (the ACT-core key set) then
  assigns the FFXIV damage-type routing statics; the `ACT_UIMods` keys go in a new block after that
  call (`EngineTables.cs:22`), by direct
  `CombatantData.ExportVariables[key] = new CombatantData.TextExportFormatter(...)` assignment
  (same shape as the `C(...)`/`E(...)` helpers, `CombatTables.cs:41-44`). The ACT-core parity
  fixtures (`ExportVarsCompatTests`/`OracleParityTests`) stand the engine up via
  `new FormActMain(...)` → `CombatTables.Setup()` only, so they never observe the new keys and stay
  bit-for-bit green. Do **not** edit `CombatTables.cs`.
- **The one-shot line-state set is a single literal in one place:**
  `{01 ChangeZone, 02 ChangePrimaryPlayer, 12 PlayerStats, 40 ChangeMap, 249 Settings, 250 Process,
  253 Version}`. The host caches the last-seen verbatim `RawLogLine` per type in that set (keyed by
  the frame's typed `LogMessageType` — zero decoding) and priming replays the exact bytes. Typed
  projections of these lines are deferred until a native consumer needs them.
- **Parity baselines come from the real-stack replay of the `E:\tmp\logs` corpus** (~200 raw
  `Network_*.log` files, ~15 GB, three characters). The plugin-in-the-loop baseline (real ACT +
  real FFXIV_ACT_Plugin, full enumerated `ExportVariables` key set + values) is generated from this
  corpus; a small slice is committed as the gate fixture, the full corpus run stays a local/manual
  mass check (`tools/mass-compare`). The oracle baseline is generatable headlessly:
  `tools/act-oracle/ActOracle.cs` loads the plugin into the same AppDomain (its `AssemblyResolve`
  binds `Advanced_Combat_Tracker` from `ACT_DIR`, `ActOracle.cs:27-34`) and reflection-invokes
  `ACT_UIMods.UpdateACTTables(false)` — `public static`, mutates only the static aggregation
  dictionaries, touches no `oFormActMain`/process/memory (`ACT_UIMods.cs:1144,1582-2519`).
- **The line-stream diff needs no real ACT run.** Real ACT delivers the tailed file's lines to
  `OnLogLineRead` verbatim, so the reference side of the diff **is the corpus slice file itself**;
  the assertion is that our consumer's `OnLogLineRead` receives exactly those bytes in order. The
  gate also needs no real plugin: the facade tail → `FeedLine` → `Fire*` path runs plugin-free.
- **An unknown game version is served as `""`, never a placeholder.** OverlayPlugin self-heals an
  empty version (falls back to the latest opcode table); a non-empty `"0.0"` misses the table and
  silently kills custom lines 257–274 while still announcing 256.
- **Wire changes need no cross-version compat.** Host and satellite stage from the same build and
  ship together; frame/protocol additions require regenerating recorded frame fixtures
  (`tests/fixtures/frames/*.frames.tsv` via the env-gated `Fct.Integration.Tests/FrameFixtureGenerator`,
  never hand-edited), nothing else.

## 4. Gap register (what this plan closes, and where)

Verified against the two decompiles (`E:\dev\ACT-decompiled`,
`E:\dev\FFXIV_ACT_Plugin\...\decompiled`) and the reference plugin trees. Coordinates in
`ACT_UIMods.cs`/`CombatantDataExtension.cs` are in the FFXIV_ACT_Plugin decompile; the rest are
local `src/`.

| Id | Gap | Resolution |
|---|---|---|
| **G14** | Live rawlog is ChatLog-only: the sole producer source is the SDK `LogLine` tap; types 01/02/40 and every combat line (21/22/26/…) never cross — cactbot/Triggernometry trigger engines are deaf live | **P2** — facade-seam tap forwards every line verbatim |
| G1 | 9 combatant + 3 encounter `ACT_UIMods` `ExportVariables` missing (`Job`, `ParryPct`, `BlockPct`, `IncToHit`, `OverHealPct`, `DirectHit*`, `CritDirectHit*`; `Last10/30/60DPS` combatant **and** encounter) | **P5** — port + register in `EngineTables.Install()` |
| G2 | All 5 nested `ColumnDefs` dictionaries empty (`Aggregation.cs:178,431,35`; `Models.cs:83`); every `ACT_UIMods` export body is `Data.GetColumnByName(...)`, which returns `""` when absent | **P5** — register the full ColumnDef chains |
| G3 | `LastNDPS` absent from `Fct.Aggregation` (plugin extension, `CombatantDataExtension.cs:122-153`; encounter divisor is hardcoded `min(Duration,10.0)`, not N) | **P5.6** |
| G4 | `ConsumerDataRepository` env stubs (`"0.0"` version, EN, region 0, `UtcNow`) — `ConsumerDataSurface.cs:156-160` | **P3** — forwarded env, stubs deleted |
| G5 | No producer env tap (version/language/region/server-clock/chat-log) | **P3.3** |
| G6 | OverlayPlugin reads region from **Machina `OpcodeManager.Instance.GameRegion`** (reflection), not `IDataRepository.GetGameRegion()`; default `Global` is correct for the primary audience | **P3.6** — reflective `SetRegion`, KR/CN only, non-blocking |
| G7 | `partySize` dropped at `BridgeForwarder.cs:221-222`; consumer re-raises `Members.Count` — misclassifies alliance content | **P3** |
| G8 | No rawlog replay for late joiners; `BuildPrimeEvents` primes only typed forms (`SatelliteHost.cs:388-431`) — blank zone/job on late join (postmortem bug B) | **P4** |
| G9 | SDK `PlayerStatsChanged` never tapped | **Dropped for v1** — the `12` line crosses via P2 and primes via P4; a typed stats frame waits for a native consumer |
| G10 | `RawLogLine` stamped `DateTimeOffset.Now`; per-line `seconds` discarded | **Dissolved by P2** — verbatim lines are self-timestamped; the tap stamps the frame from the line's own parsed time |
| G11 | 249/250/253 reach neither SDK event — they exist only in the plugin's log-file writes | **Dissolved by P2** — the facade seam is downstream of the log write, so they cross through the same tap |
| G12 | `ParsedLogLine` consumer re-raise | **Dropped** — no target consumer binds it (Triggernometry's only reference is dead unsubscribe code, `BridgeFFXIV.cs:145-172`) |
| G13 | `EncounterData.LogLines` never appended on the consumer fold | **Deferred post-v1** (§6) — ACT-core `GetTextExport` never renders `LogLines` (`ACT-decompiled FormActMain.cs:15545`), so no live Trig trigger path depends on it |

Verified non-gaps (no action): tag value types match the codec; `overHeal`/`damageShield`/
`absorbHeal` swing inputs cross (`CombatSwing.Tags` verbatim, `BridgeForwarder.cs:267-276`);
`PartyType` crosses per-actor; PID → memory-scan bootstrap; `detectedType` pre-set on the consumer
re-raise (`Program.cs:706-709`, shared mutable `LogLineEventArgs`). The modern surface already has
homes for the new state: `GameClient` (`Session.cs:76`) for env, `PartySnapshot` (`Session.cs:64`)
for party size, `EncounterSnapshot/CombatantMetrics.ExportVariables` (populated by
`EncounterProjector.cs:55-69` iterating the registered formatters) for the engine keys.

## 5. Phases

### v1 ship scope

v1 ships **FFXIV_ACT_Plugin (parser), OverlayPlugin (+cactbot), Triggernometry,
ACT-Discord-Triggers** with their full contracts working. Hojoring is the deferred fifth
(ISOLATION-PLAN P10). All phases below are v1-required except the deferred items in §6.

**Ordering is the only dependency between tasks.** Each task is self-contained: files, exact
change, source-to-port, and a done-when. Execute strictly in order; stop after any task and the
tree still builds and all green gates stay green.

---

### P0 — Derisk: answer every open question before production code ☑

Each task produces a recorded verdict (fill in the checkbox line). No production code changes.

- [x] **P0.1 — Line-seam coverage.** Prove the facade tail delivers every line type at
      `OnLogLineRead`. Write a slice of `E:\tmp\logs` lines (must include types 00, 01, 02, 21, 40,
      249, 250, 253 — pick a file that has them or concatenate) into a temp file, point the facade
      at it (`LogFilePath` + `OpenLog`, or a direct `StartLogTail`-shaped harness in
      `Fct.LegacyHost` tests), subscribe `OnLogLineRead`, and count per leading type key.
      *Done when:* every type in the slice arrives byte-identical, in order. *Fallback if a type is
      missing:* that type is not written to `Network_*.log` by the plugin — cover it with a
      type-filtered SDK `ParsedLogLine` tap for exactly the missing types (do not change the
      primary design).
      **Verdict:** ✅ CONFIRMED (empirical). `LineSeamCoverageTests.Facade_tail_delivers_every_line_type_verbatim_and_in_order`
      (`tests/Fct.Compat.Act.Tests`, passing) drives the real tail
      (`OpenLog → StartLogTail → TailLoop → FeedLine → FireLogLineRead`) over a 12-line corpus slice
      (types 00, 01, 02, 12, 21, 22, 24, 26, 40, 249, 250, 253); every line reaches `OnLogLineRead`
      byte-identical, in order, exactly one per type. `FeedLine` is type-agnostic by construction
      (byte-level `\n` split → verbatim UTF-8 string, `\r` stripped, **no** type filter —
      `FormActMain.cs:395-429`), so any line the plugin writes to `Network_*.log` crosses. No missing
      type; the `ParsedLogLine` fallback is not needed. (Harness lands early as the plugin-free seed of
      the P1.3 from-start line-stream diff.)
- [x] **P0.2 — `detectedType` at the tap.** `FeedLine` raises with `detectedType: 0`
      (`FormActMain.cs:422`). Determine whether the real plugin's `ParseStrategy`
      (subscribed to `BeforeLogLineRead`) sets `args.detectedType` before `OnLogLineRead` fires —
      check the FFXIV_ACT_Plugin decompile's log-parse handler, or observe in P0.1's harness with
      the plugin loaded. *Done when:* decided how P2.1 obtains the frame's `LogMessageType`: from
      `args.detectedType` (if set), else extracted from the line's leading `NN|` key (key
      extraction, same class as the existing `ParseLineTimestamp` — no semantic decoding).
      **Verdict:** ✅ **Extract from the leading `NN|` key** — `int.Parse` of the substring before the
      first `|` cast to `LogMessageType` (a sibling of `ParseLineTimestamp`, no semantic decoding). Do
      **not** read `args.detectedType`. Rationale: (1) the from-start gate (P1.3) is plugin-free, where
      `FeedLine` fixes `detectedType = 0` (`FormActMain.cs:422`), so it is unusable there; (2) the
      `IActPluginV1` class that would write `args.detectedType` lives in the main `FFXIV_ACT_Plugin.dll`,
      which is **not** in the decompile, so a live write is unverifiable; (3) the leading key *is* the
      type — the plugin's own `ParseMediator.BeforeLogLineRead` parses field 0 via
      `Enum.TryParse<LogMessageType>(data[0])` (`ffxiv_act_plugin.parse/ParseMediator.cs:53`) and that
      enum is value-identical to `Fct.Abstractions/LogMessageType`. Separately confirmed the line crosses
      **verbatim**: cactbot/Triggernometry match pipe-format regexes (`^NN\|…`) on `OnLogLineRead`, so the
      loaded plugin does not rewrite `args.logLine` (the P2 soak reconfirms on the live producer path).
- [x] **P0.3 — Headless env values.** Determine what
      `GetGameVersion()`/`GetSelectedLanguageID()`/`GetGameRegion()`/`GetServerTimestamp()` return
      from the real plugin under `--replay` with no live game (`GetGameVersion` reads
      `ffxivgame.ver` from the game path — expected `""` headless). *Done when:* the P1.5 gate
      assertions are written against known headless values, with the live-value assertion
      `[plugin-gated]`. **Verdict:** ✅ From `FFXIV_ACT_Plugin.Memory.DataRepository`:
      - `GetGameVersion()` → **`""`** (`ProcessManager.ClearCurrent` sets `GameVersion = ""`; only a
        live game path reading `ffxivgame.ver` populates it) — serve `""` per §3. *Hard headless assertion.*
      - `IsChatLogAvailable()` → **`true`** (`DisableCombatLog` defaults `false`). *Hard headless assertion.*
      - `GetServerTimestamp()` → **`DateTime.MinValue`** headless (`ServerTimeProcessor.ServerTime` is
        assigned **only** by live memory scan, never from the log — not `UtcNow`). ⚠️ **This changes
        P3.3:** `ServerClockOffset = GetServerTimestamp() − UtcNow` would be a ~2000-year garbage span;
        the producer tap **must guard** — when `GetServerTimestamp()` is default / pre-2000, forward
        `ServerClockOffset = TimeSpan.Zero`.
      - `GetSelectedLanguageID()` / `GetGameRegion()` → host-config-driven (`ParseSettings.LanguageID` /
        `DataCollectionSettings.RegionID`), defaulting to `(Language)0` / `(byte)0` when unconfigured.
        P1.5 therefore asserts the **forwarded value == the repo's returned value** (identity, no stub);
        the exact language/region value stays `[plugin-gated]`.
- [x] **P0.4 — Swing-tag completeness.** Confirm forwarded `CombatSwing.Tags` carry `Job`,
      `DirectHit`, and `overheal` for a replayed slice (capture frames at the host, or grep a
      recorded `*.frames.tsv`). The producer tap forwards `MasterSwing.Tags` verbatim
      (`BridgeForwarder.cs:267-276`), so a missing tag is a plugin/facade producer gap, not an
      engine gap — P5's math depends on this answer.
      **Verdict:** ✅ CONFIRMED (decompile + recorded frames). The plugin sets these in
      `ffxiv_act_plugin.parse/ReportCombatData.cs`: **`Job`** = string job-name (`"War"`/`"Pld"`; `"Adv"`
      and null → `""`); **`DirectHit`** = string `"True"`, present **only on a direct hit**
      (`AddDamageEntry`/`AddDoTEntry`); **`overheal`** = string, on every heal / on HoT DoT.
      `combat-slice.frames.tsv` confirms live `SWING` frames carrying `Job s War`, `DirectHit s True`
      (133×, conditional), `overheal s 0`, plus `potency` (as **`d` double**), `StatusEffects`/`CritRate`/
      `DHRate` (string). Full producer tag set: `potency`(d), `Job`/`SourceId`/`TargetId`/`StatusEffects`/
      `CritRate`/`DHRate`/`CritEffects`/`DHEffects`/`DirectHit`/`overheal`/`dotPotency`/`BuffByte1-3`/
      `Params`(s), `dotbase`(numeric), `StatusDuration`(d). **No `CritDirectHit` tag** — crit-direct-hit
      is derived from the `Critical` bool + `DirectHit` presence, matching
      `CombatantDataExtension.DirectHitCount/CritDirectHitCount`. Codec note for P5.5: the wire preserves
      `s`/`d`/`u` (`GameEventFrame.cs:398-418`); an int-boxed tag (`dotbase`) would cross as string `s`,
      but the P5 headline math (`Job`/`Parry`/`Block`/`DirectHit`/`Overheal`/`LastNDPS`) reads only
      string/presence tags, so it is unaffected.
- [x] **P0.5 — SDK `LogLine` has no target consumer.** Confirm none of the four v1 plugins binds
      the SDK `IDataSubscription.LogLine` event (grep the reference trees; Triggernometry and
      cactbot/OverlayPlugin consume ACT's `Before/OnLogLineRead`; Discord-Triggers consumes no
      combat data). *Done when:* recorded here — this licenses P2.2's tap removal and pins the
      consumer-side `LogLine` re-raise semantics (`ConsumerDataSurface.cs:50`).
      **Verdict:** ✅ CONFIRMED. SDK `IDataSubscription.LogLine` has exactly one binder across the
      reference trees — OverlayPlugin's `FFXIVRepository.RegisterLogLineHandler`
      (`FFXIVRepository.cs:518-522`) — and **nothing calls `RegisterLogLineHandler`** anywhere in
      `E:\dev` (grep: only the definition + one `DATA-FLOW.md` doc reference). OverlayPlugin's live log
      source is ACT's `BeforeLogLineRead` (`MiniParseEventSource.cs:141`, `FFXIVOptionalEventSource.cs:46`,
      `LogParseOverlay.cs:56`, `LineContentFinderSettings.cs:38`). Triggernometry binds
      `BeforeLogLineRead`+`OnLogLineRead` (`ProxyPlugin.cs:165-166`); its only `ParsedLogLine` reference
      is dead unsubscribe code (`BridgeFFXIV.cs:164`, G12). Discord-Triggers: no combat-data taps (no
      matches). ⇒ No v1 plugin's active path binds SDK `LogLine` — **P2.2's tap removal is licensed.** The
      consumer-side `LogLine` re-raise (`ConsumerDataSubscription.cs:46-51`) stays (harmless — no target
      binder); after P2 its `seconds` derives from the line-accurate frame timestamp.

### P1 — Differential gates (land red or explicitly skipped) ☑

All gates live in the suites named in [`TESTING.md`](TESTING.md); plugin-in-the-loop gates are
`[plugin-gated]` and skip cleanly without the real plugins.

- [x] **P1.1 — Plugin-in-the-loop oracle baseline.** Extend `tools/act-oracle/ActOracle.cs`: after
      `SetupAct()`, load `FFXIV_ACT_Plugin.dll` into the same AppDomain and reflection-invoke
      `ACT_UIMods.UpdateACTTables(false)` (see §3). Then **enumerate**
      `CombatantData.ExportVariables`/`EncounterData.ExportVariables` (never hardcode the key list)
      and `GetExportString(cd,"")` — the pattern at `ParseOracle.cs:235-236`. Commit one slice
      baseline `tests/fixtures/combat-slice.plugin.exportvars.tsv` (name distinct from the ACT-core
      `combat-slice.exportvars.tsv`). *Done when:* the fixture is committed and regenerable by one
      documented command.
      **Verdict:** ✅ DONE. New additive mode `ActOracle --plugin-baseline <swings.tsv> <out.tsv>`
      (`tools/act-oracle/ActOracle.cs`): `LoadPluginAndInitACT_UIMods()` (name deliberately contains
      `InitACT` — `UpdateACTTables` ends by calling `oFormActMain.ValidateLists()`/
      `ValidateTableSetup()`, both guarded by `Environment.StackTrace.Contains("InitACT")` against
      NREs on our constructor-bypassed `FormActMain`, confirmed by direct read of
      `FormActMain.cs:14492-14502`/`:22292-22302`) loads `FFXIV_ACT_Plugin.dll` via
      `Assembly.LoadFrom` + reflection and invokes `ACT_UIMods.UpdateACTTables(false)`.
      `DumpAllExportVariables` enumerates `CombatantData.ExportVariables`/`EncounterData.ExportVariables`
      directly (`foreach`, zero hardcoded keys) calling `GetExportString(cd,"")` /
      `GetExportString(enc, enc.GetAllies(), "")`. One real-machine gotcha found and fixed: the public
      `FormActMain.LastKnownTime` setter (needed by the `Last10/30/60DPS` formatters) restarts a
      `Stopwatch` field whose initializer never ran on the uninitialized instance — set the private
      backing field via reflection instead. Smoke-tested end-to-end against the real
      `E:\tmp\plugins\FFXIV_ACT_Plugin_3.0.2.3\FFXIV_ACT_Plugin.dll`: produced exactly **12 combatant +
      4 encounter keys** (Job/ParryPct/BlockPct/IncToHit/OverHealPct/DirectHitPct/DirectHitCount/
      CritDirectHitCount/CritDirectHitPct + Last10/30/60DPS combatant-side; CurrentZoneName +
      Last10/30/60DPS encounter-side) — an exact match to G1's gap description. Committed
      `tests/Fct.Compat.Act.Tests/fixtures/combat-slice.plugin.exportvars.tsv` (the real fixture
      location — the sibling ACT-core baselines live here too, not under a top-level `tests/fixtures/`
      as this task's literal path suggested). Regenerable by the single documented command
      `tools/act-oracle/build-and-run.ps1` (extended, additive-only — the existing 2/3-arg and
      `--folder` code paths, `RegisterTables()`, and the two existing committed fixtures are
      unchanged, verified by byte-diff before/after the `ReplaySwings` extraction refactor).
- [x] **P1.2 — ExportVariables diff gate.** New gate (extends `OverlaySatelliteTests` + a headless
      engine variant): drive the **consumer-replica / host-engine path** (`--consume` satellite or
      `ModernEncounterEngine` — **not** a bare `new FormActMain`, which never calls
      `EngineTables.Install()`) over the slice, enumerate the full key set, diff keys **and**
      values against P1.1's fixture. Carry an explicit skip-list of keys pending P5 that must
      shrink to empty. *Done when:* gate runs red with the skip-list documenting exactly the
      missing keys.
      **Verdict:** ✅ DONE, red as designed. Two gates, both enumerating the full
      `CombatantData.ExportVariables`/`EncounterData.ExportVariables` key set (never a hardcoded
      list) and diffing against P1.1's `combat-slice.plugin.exportvars.tsv`:
      - **Headless engine variant** —
        `Fct.Engine.Tests/OracleParityTests.ExportVariables_g1_keys_match_the_plugin_oracle_baseline_pending_P5`:
        reuses `BuildThroughEngine` (the same `ModernEncounterEngine` production input path as the
        ACT-core parity test) and diffs its enumerated keys/values against the plugin fixture.
      - **Satellite variant** — `Fct.Integration.Tests/OverlaySatelliteTests` extended in place: the
        already-collected MiniParse `CombatData` frame (OverlayPlugin's `GetCombatantList`/
        `GetEncounterList` already enumerate the full `ExportVariables` dictionaries, confirmed by
        reading `MiniParseEventSource.cs:321,385`) is diffed against the same plugin fixture on the
        real `--consume` satellite path.
      - **Skip-list mechanics:** `PendingP5Keys` (duplicated per-project, same 12 literal G1
        combatant keys — `Job/ParryPct/BlockPct/IncToHit/OverHealPct/DirectHitPct/DirectHitCount/
        CritDirectHitCount/CritDirectHitPct/Last10DPS/Last30DPS/Last60DPS`) is used three ways: (1)
        any mismatch whose key is **not** in the list fails immediately (regression guard); (2) any
        skip-listed key that stops mismatching fails (staleness guard — the list must shrink, never
        grow stale entries); (3) the headline assertion `PendingP5Keys.Count == 0` fails
        deliberately — empirically confirmed both gates currently fail on exactly this assertion,
        with the error message enumerating the same 12 keys and zero unexpected/stale entries. This
        is P5.9's exit criterion in reverse: the list reaching empty is what turns both gates green.
      - **`*ENCOUNTER*.CurrentZoneName` excluded from strict value comparison** (both gates, key
        registration still checked): already registered per P5.7 with the plugin's identical
        `d.ZoneName` formula (`CombatTables.cs:220-224`), but its *value* is zone-frame provenance —
        P1.1's oracle harness (`ActOracle.PluginBaselineAndDump`) replays swings only, hardcoding
        `""`, while the satellite gate replays a real `combat-slice.frames.tsv` `ChangeZone` frame
        (`"Shirogane"`). Empirically confirmed as the only satellite-side divergence before the
        exclusion; the ACT-core diff excludes this same key for the identical reason
        (`ExportVarsCompatTests`/`OracleParityTests` never see it — real ACT has no such key).
      - Verified: both new tests fail today with only the documented 12-key gap (no unexpected
        mismatches, no stale skip-list entries); the full `Fct.Engine.Tests` suite (9/10 passing, 1
        intentional red) and `Fct.Integration.Tests` suite (34/36 passing + the 1 intentional red;
        the 40th, `FourRealPackagesTests`, is pre-existing run-ordering flakiness — confirmed green
        in isolation, unrelated to this change) show no regressions.
- [x] **P1.3 — Line-stream diff gate (from-start).** New `Fct.Integration.Tests` gate: feed the
      P0.1 slice through the facade tail seam (plugin-free) → producer forwarder → wire → host →
      a `rawlog`-subscribed consumer; capture the consumer's `OnLogLineRead` lines; byte-diff
      content **and order** against the slice file. Assert the frame's `LogMessageType` is correct
      per line. Add a `[plugin-gated]` variant with the real plugin loaded (same assertion). *Done
      when:* gate runs red (rawlog is ChatLog-only today).
      **Verdict:** ✅ DONE, red as designed — and red for a starker reason than "ChatLog-only":
      empirically, **zero** lines cross today, of any type, whether or not the real plugin is loaded.
      - **Gate:** `tests/Fct.Integration.Tests/LineStreamDiffTests.cs` —
        `Plugin_free_facade_tail_lines_do_not_reach_a_rawlog_consumer_today` and
        `Plugin_gated_facade_tail_lines_do_not_reach_a_rawlog_consumer_today` (`[SkippableFact]`,
        the latter `Skip.IfNot(File.Exists(SatelliteRunFixture.FfxivPluginPath), …)`). Both drive the
        same `RunGate` helper: write the P0.1 12-line slice's *target file* empty, start a REAL
        producer satellite subprocess and a REAL consumer satellite subprocess sharing one
        `GameEventBus`/`GameSession` (the `SatelliteHost` production class, exactly the
        `LogWriteBackTests`/`ConsumerLogLineTests` two-satellite pattern), let the producer's tail
        bind, append the slice bytes to the tailed file (the P0.1 cross-process trick), wait, shut
        both down, then byte-diff the consumer's recorded `OnLogLineRead` lines (content + order)
        against the slice and assert each arrived line's frame `LogMessageType` (leading `NN|` key,
        per P0.2) against what did cross.
      - **Test-only harness additions (no change to any existing tap or forwarding behavior):**
        - `Fct.LegacyHost/Program.cs`: new CLI driver `--replay-tail <logPath> [--load-parser]
          --bridge <pipe>` → `RunReplayTail`. Connects the bridge, stands up the ACT facade,
          optionally loads the real `FFXIV_ACT_Plugin` (`--load-parser`, mirroring
          `LoadStandalonePlugins`' exact `OnParserLoaded()` wiring so the real `BridgeForwarder`
          attaches precisely as production does once a producer's `LOADPLUGIN` completes), then
          calls `act.OpenLog` on the given path — the same tail `LineSeamCoverageTests` (P0.1)
          proves delivers every line type to `OnLogLineRead`. Adds no new tap; exercises only the
          existing facade + `BridgeForwarder`.
        - `Fct.LegacyHost/Program.cs` `RunConsume`: new optional `--verify-loglines-full <path>`,
          additive alongside the existing count-only `--verify-loglines` (unchanged, still used by
          `ConsumerLogLineTests`). Subscribes `OnLogLineRead` (no mutation) and records one
          `"<detectedType>\t<logLine>"` row per re-raised event, in receipt order — the byte-diff
          input this gate needs, vs. the prior artifact's bare counts.
      - **Empirical red result (both variants, identical):** `Assert.Equal(Slice, got)` fails with
        `Actual: List<string> []` — **0 of 12** slice lines ever reach the consumer's
        `OnLogLineRead`, not a subset (confirming this is a clean, deterministic assertion failure,
        never a harness exception or a bare zero-line timeout — both runs complete in ~5–10s and
        the verify-loglines-full artifact is always written, just empty).
      - **Root cause, confirmed two independent ways:**
        1. **By reading the decompile:** `BridgeForwarder`'s only line-related producer tap today is
           the SDK `IDataSubscription.LogLine` event (`BridgeForwarder.cs` `OnLogLine`, bound only
           once a parser is loaded via `TryBindFfxiv`/`OnParserLoaded`) — **not**
           `FormActMain.OnLogLineRead` (the facade tail seam; that tap is P2.1). The SDK `LogLine`
           event itself is raised **only** by `FFXIV_ACT_Plugin.Memory.DataEventProcessor.OnLogLine`
           (`ffxiv_act_plugin.memory` decompile), which fires from the plugin's **live
           memory-scanning** pipeline (`LogProcessor.cs`) — never from `BeforeLogLineRead`/log-file
           replay. So a facade-tailed line can never reach the SDK tap regardless of whether the
           plugin is loaded, absent a live game process.
        2. **Empirically, from the plugin-gated run's own satellite log:** once the real
           `FFXIV_ACT_Plugin` finishes its async init, it calls `ActGlobals.oFormActMain.OpenLog`
           itself — `path=…\FFXIVLogs\Network_<pid>_<date>.log filter=Network_*.log getZone=True` —
           reclaiming the facade's file tail onto **its own** expected log path and away from this
           gate's slice file (`StartLogTail`'s same-path guard cancels the previous tail on a
           different path). This is a second, independent reason the `[plugin-gated]` variant sees
           nothing — distinct from (1), and worth remembering for whoever revisits this tail-sharing
           behavior later.
      - **Skip behavior:** `[plugin-gated]` skips cleanly (`Skip.IfNot`) when
        `FFXIV_ACT_Plugin.dll` isn't installed; on the machine this was authored on it **was**
        installed, so the variant ran for real (confirmed via the satellite log: `PluginInstantiated`
        → `RealPluginBound` → `ForwarderBound`, i.e. the SDK tap genuinely bound) and still produced
        the identical 0-of-12 red result.
      - **No regressions:** full `Fct.Integration.Tests` run — 42 tests, 37 passed, 3 failed (this
        gate's 2 intentional reds + the pre-existing P1.2 `OverlaySatelliteTests` intentional red,
        unchanged), 2 skipped (env-gated `DistTreeGateTests`/`FrameFixtureGenerator`, unrelated).
        `FourRealPackagesTests` passed in this run (its documented run-ordering flakiness did not
        reproduce).
- [x] **P1.4 — Late-join convergence gate.** Same harness as P1.3, but the consumer subscribes
      **after** the slice's zone/map/player/version lines have been folded: assert it receives the
      one-shot state lines (`01/02/40/12/249/250/253`, last-seen instance each) and a
      `GameVersion`-bearing repository value from **priming alone** (no live line). *Done when:*
      gate runs red.
      **Verdict:** ✅ DONE, red as designed — and deliberately a DIFFERENT axis from P1.3 (which
      already proved live rawlog never crosses at all, G14). P1.4 isolates G8 specifically: even
      where the one-shot state has already folded into host session state before a consumer exists
      (standing in for P2's not-yet-built tap), a **late-joining** rawlog subscriber still converges
      on none of it, because `BuildPrimeEvents` (`SatelliteHost.cs` ~388-431) has no branch for the
      `rawlog` token at all — it primes typed zone/party/player/repository forms only, never a cached
      one-shot `RawLogLine` (no last-line cache exists anywhere upstream; that is P4.1). Two gates,
      both in `tests/Fct.Integration.Tests/LateJoinPrimingTests.cs`:
      - **`Late_rawlog_subscriber_converges_on_none_of_the_one_shot_lines_from_priming_alone`**
        (plugin-free, always runs): a real `GameEventBus`/`GameSession` + a real, running
        `GameSnapshotAggregator` (the exact production host wiring) fold the typed zone/player/party/
        process state, then the test folds the P0.1 one-shot line set (`01/02/12/40/249/250/253`)
        **twice** per type (an earlier "first load" instance, then a later "relog/zone move" instance)
        directly onto the shared bus — standing in for P2's tap, since `GameEventBus.Emit` fans only
        to the CURRENT subscriber snapshot and has no history/replay of its own. Only THEN does a late
        `SatelliteHost` (the real production class) subscribe, via the existing `--sink
        <path> --subscribe rawlog,zoneparty` driver (unmodified — no new `Fct.LegacyHost` code needed
        for this gate), so `HandleSubscribe`→`BuildPrimeEvents`→`SatelliteEgress` runs for real against
        the already-folded snapshot. No live event follows the subscribe. The recorded wire frames are
        decoded with the real `GameEventFrame.TryParse`. **Control** (not the gate's point, but proves
        the harness isolates the right gap): the late joiner DOES receive a primed `ZoneChanged`
        (`ZoneId=999, ZoneName="Kugane"`) from the existing typed-fold priming mechanism — confirming
        this is specifically a rawlog gap, not a total priming failure. **The gate:** priming should
        replay the LAST instance of each one-shot type in ACT emission order
        (`253/249/250 → 01 → 02 → 40 → 12`, P4.2) — `Assert.Equal(expectedLastInstances, primedRaw)`
        fails with `Actual: []`; zero of the 7 expected lines arrive, confirming G8 exactly (not a
        subset, not a harness exception — the control assertions immediately above it passed).
      - **`Late_stand_in_repository_never_converges_on_a_forwarded_GameVersion_from_priming_alone`**
        (`[SkippableFact]`, requires `FFXIV_ACT_Plugin.dll` — skips cleanly without it): same fold,
        then a late `--consume --stand-in --verify-standin` satellite (the real
        `ConsumerStandIn`/`ConsumerDataRepository`) subscribes with no live event after. Per P0.3, a
        headless real plugin serves `GetGameVersion() == ""`; the gate asserts the repository
        converges on that forwarded value from priming alone. Deliberately red today:
        `ConsumerDataRepository.GetGameVersion()` (`ConsumerDataSurface.cs` ~158) hardcodes `"0.0"`
        unconditionally (G4) and no wire path forwards any env at all yet (G5/P3 not built), so the
        assertion fails with `Actual: "0.0"`.
      - **Test-only/harness-adjacent additions (no forwarding-behavior change):** `StandInVerification`
        (`Fct.Parser.Legacy/IConsumerStandIn.cs`) gained one appended `GameVersion` field (index 8,
        after the existing `RealIocContainer` at index 7 — verified non-breaking: `ConsumerStandInTests`
        indexes `f[0..7]` positionally and never asserts array length); `ConsumerStandIn.SelfVerify()`
        populates it by reading (never writing) `ConsumerDataRepository.GetGameVersion()`;
        `Fct.LegacyHost/Program.cs`'s `--verify-standin` artifact writer appends the same field. Purely
        additive observability, exactly the class's own precedent ("the in-satellite discovery gate").
      - **No regressions:** full `Fct.Integration.Tests` run — 44 tests (42 + these 2 new), 37 passed,
        5 failed (P1.4's 2 new intentional reds + the pre-existing P1.2 `OverlaySatelliteTests` red +
        P1.3's 2 `LineStreamDiffTests` reds, all unchanged), 2 skipped (env-gated, unrelated).
        `FourRealPackagesTests` passed in this run (no flake reproduced). `Fct.Parser.Legacy.Tests`
        (8/8) and the full solution build stay green.
      - **Narrowing:** the GameVersion assertion is asserted through the real cross-process
        `ConsumerDataRepository` (not narrowed to an in-process host-snapshot shortcut) — chosen
        because P1.5 is specifically "the repository surface gate" and this reuses that exact seam
        (`StandInVerification`), so P1.5 inherits the artifact format directly. The rawlog gate
        folds one-shot lines onto the bus directly (bypassing a real facade/tap) rather than via a
        producer satellite process, since P1.3 already proved no tap exists to produce them live —
        the point here is priming, not production; using the real `GameSnapshotAggregator` +
        `SatelliteHost`/`BuildPrimeEvents`/`SatelliteEgress` keeps every OTHER link in the chain real.
      - **Handoff for P1.5:** `StandInVerification.GameVersion` (and the `--verify-standin` artifact's
        appended 9th column) is reusable as-is — P1.5 can extend the same struct/artifact with
        `Language`/`Region`/`ServerTimestamp`/`IsChatLogAvailable` following the identical
        append-only pattern, and reuse `LateJoinPrimingTests`' `--consume --stand-in` harness shape
        (fold state, subscribe, no live event, read `f[N]`) for a LIVE (not late-join) variant.
- [x] **P1.5 — Repository surface gate.** `Fct.Parser.Legacy.Tests`/`Fct.Integration.Tests`:
      consumer `IDataRepository.GetGameVersion()/GetSelectedLanguageID()/GetGameRegion()/
      GetServerTimestamp()/IsChatLogAvailable()` return **forwarded** values (per P0.3's verdict),
      never the stubs. *Done when:* gate runs red.
      **Verdict:** ✅ DONE, red as designed. Two complementary gates plus the append-only artifact
      extension P1.4 handed off:
      - **Artifact/struct extension (append-only, non-breaking):** `StandInVerification`
        (`src/Fct.Parser.Legacy/IConsumerStandIn.cs`) gained four fields appended after the existing
        `GameVersion` (index 8): `Language` (int — the SDK `Language` enum cast to int; the SDK enum
        has no defined `0` member, so a plain int survives the TSV round-trip unambiguously), `Region`
        (byte), `ServerTimestampTicks` (long — `DateTime` does not survive one TSV field losslessly
        otherwise), `IsChatLogAvailable` (bool). `ConsumerStandIn.SelfVerify()` populates them by
        reading (never writing) the four remaining `ConsumerDataRepository` members. The
        `--verify-standin` artifact writer (`src/Fct.LegacyHost/Program.cs`, `FlushConsume`) appends
        them as columns 9–12. Verified non-breaking: `ConsumerStandInTests` indexes `f[0..7]` and
        `LateJoinPrimingTests` indexes `f[0..8]` positionally, neither asserts array length.
      - **Live cross-process gate (the primary gate, constraint 2 — host pipe is source of truth):**
        `tests/Fct.Integration.Tests/RepositorySurfaceLiveTests.cs` —
        `Live_stand_in_repository_serves_forwarded_env_scalars_never_the_stubs` (`[SkippableFact]`,
        requires `FFXIV_ACT_Plugin.dll` installed — skips cleanly without it, same guard as
        `ConsumerStandInTests`/`LateJoinPrimingTests`). Reuses the `--consume --stand-in
        --verify-standin` harness shape, deliberately as the LIVE axis complementing P1.4's late-join
        axis: the stand-in satellite boots and subscribes to `repository` with **no** bus pre-staging
        at all (no producer env tap exists yet — G5 — so there is nothing an env-forwarding event
        could fold even live; that absence is the point). Asserts the three P0.3 **hard**, machine-
        independent headless verdicts directly against the real cross-process
        `ConsumerDataRepository`: `GetGameVersion() == ""` (red — stub returns `"0.0"`),
        `GetServerTimestamp().Ticks == DateTime.MinValue.Ticks` (red — stub returns `DateTime.UtcNow`),
        `IsChatLogAvailable() == true` (passes today — documented explicitly as coincidence, not
        forwarding: the stub's unconditional `true` happens to equal P0.3's verdict; kept as an
        assertion anyway per the plan's own framing, so a future accidental stub flip to `false` is
        still caught). Empirically confirmed on this machine (real `FFXIV_ACT_Plugin.dll` installed):
        artifact `[1 | 1 | 0 | 0 | FFXIV_ACT_Plugin | FFXIV_ACT_Plugin Started. | 0 | 1 | 0.0 | 1 | 0 |
        639189777964643793 | 1]` — the gate fails on the very first assertion,
        `Assert.Equal("", gameVersion)` → `Actual: "0.0"` (the `ServerTimestampTicks` column,
        `639189777964643793`, independently confirms the second assertion would also fail — nowhere
        near `DateTime.MinValue.Ticks` (`0`) — though xUnit's terminal-assert semantics mean only the
        first failure surfaces per run, same pattern as P1.4's gates).
      - **Language/Region — the documented compromise:** P0.3 explicitly left these two
        *not* hard-pinned (`ParseSettings.LanguageID`/`DataCollectionSettings.RegionID` are
        host-config-driven — whatever the installed plugin has saved on the running machine).
        Empirically on this machine the real plugin returns `Language.English` (`1`) / region `0` —
        **identical to the current stub's hardcoded values** — so a same-machine exact-value assertion
        in the live cross-process gate would misleadingly *pass* today, masking the gap rather than
        gating it (the identical coincidence already holds for `IsChatLogAvailable`). Per the plan's
        explicit escape hatch ("mark the exact-value assertion `[plugin-gated]`/skippable while keeping
        a non-stub structural assertion red now"), the live gate logs `LanguageRaw`/`RegionRaw` for
        observability only (not asserted-by-value) and defers the deterministic red assertion to a
        second, in-process, no-plugin-required gate:
      - **Structural gate (deterministic, always runs, no satellite/plugin needed):**
        `tests/Fct.Parser.Legacy.Tests/ConsumerDataRepositoryStubTests.cs` —
        `ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet` asserts (via
        reflection against the real internal `ConsumerDataRepository`, `InternalsVisibleTo` added
        test-only to `Fct.Parser.Legacy.csproj`) that an `Apply()`-fed backing field exists for
        `Language`/region, mirroring every other forwarded member (`_pid`/`_playerId`/`_territoryId`/
        `_combatants`/`_resources`). Deliberately red today — neither field exists;
        `GetSelectedLanguageID()`/`GetGameRegion()` are unconditional literal expression bodies
        (`ConsumerDataSurface.cs` ~156-157). Confirmed failing:
        `expected ConsumerDataRepository to hold an Apply()-fed Language backing field (P3.5) — none
        exists yet`. A sibling documenting-only fact,
        `ConsumerDataRepository_serves_hardcoded_env_stubs_today_documenting_G4` (green), pins today's
        five literal stub values so an incidental future edit to the stub itself is caught first.
      - **No regressions:** `Fct.Parser.Legacy.Tests` 10/10 run, 9 passed + this 1 new intentional red
        (was 8/8 before). `Fct.Integration.Tests` full run: 45 tests, 36–37 passed, 6 intentional reds
        (P1.2 `OverlaySatelliteTests` + P1.3 `LineStreamDiffTests`×2 + P1.4 `LateJoinPrimingTests`×2 +
        this phase's new `RepositorySurfaceLiveTests`), 2 skipped (env-gated, unrelated).
        `FourRealPackagesTests` failed on multiple runs in this session, including in isolation, with a
        partial (nondeterministic) damage total each time — confirmed unrelated to this change: it
        never touches `--stand-in`/`--verify-standin` (grepped), and its flakiness is already
        documented as pre-existing/run-ordering-sensitive in the P1.2/P1.3/P1.4 verdicts above. Full
        solution build (`dotnet build ffxiv-combat-tracker.slnx`) and the `Fct.App.csproj` stage build
        stay green.
      - **Handoff for P1.6:** unrelated surface (`PartyChanged`/`PartySize`) — no dependency on this
        phase's artifact columns.
      - **Handoff for P3:** P3.5 must (1) add `ConsumerDataRepository` backing fields for `Language`
        and `Region` fed from `Apply()`, flipping
        `ConsumerDataRepository_has_no_forwarded_backing_field_for_language_or_region_yet` green; (2)
        replace the `GetGameVersion`/`GetServerTimestamp` stubs with the forwarded mirror, flipping
        `RepositorySurfaceLiveTests`'s `GameVersion`/`ServerTimestamp` assertions green; (3) delete the
        five stubs (`ConsumerDataSurface.cs` ~156-160) per the plan, at which point
        `ConsumerDataRepository_serves_hardcoded_env_stubs_today_documenting_G4` must be deleted or
        rewritten (it currently pins the stub literals and will fail once they are gone — expected,
        not a regression). The `--verify-standin` artifact's columns 9–12
        (`Language`/`Region`/`ServerTimestampTicks`/`IsChatLogAvailable`) are ready to observe the real
        forwarded values once P3 lands; no further harness changes are needed on the observability side.
- [x] **P1.6 — Alliance party gate.** `Fct.FlowTests` `EventMappingFlowTests` + a satellite gate:
      a `PartyChanged` with `PartySize < Members.Count` must preserve **both** end-to-end (today
      `EventMappingFlowTests.cs:14-29` asserts a size derived from `Members.Count`). *Done when:*
      gate runs red.
      **Verdict:** ✅ DONE, red as designed. Two scaffolding-only record additions plus two gates:
      - **Inert record-shape additions (no forwarding/fold/consumer wiring):**
        `src/Fct.Abstractions/Events.cs:112` `PartyChanged` gained a trailing defaulted
        `int PartySize = 0`; `src/Fct.Abstractions/Session.cs:64` `PartySnapshot` gained a trailing
        defaulted `int Size = 0`. Both are pure source-compat additions — every existing positional
        call site (`Fct.FlowTests`, `Fct.Integration.Tests/LateJoinPrimingTests.cs:100`,
        `Fct.App.Tests/Hosting/GameSnapshotAggregatorTests.cs:44`,
        `Fct.App.Tests/Hosting/GameEventBusTests.cs:52`,
        `Fct.Compat.Shim.Tests/DataSubscriptionTests.cs:57`, `Fct.Abstractions.Testing/GameStateFakes.cs:14`,
        `Fct.App.Tests/BridgeEventFrameTests.cs:54,57`, `Fct.Bridge.Contracts/GameEventFrame.cs:149`,
        `Fct.Host/SatelliteHost.cs:406`, `Fct.LegacyHost/BridgeForwarder.cs:222`,
        `Fct.Host/Hosting/GameSnapshotProvider.cs:33`, `Fct.Host/Hosting/GameSnapshotAggregator.cs:127`)
        keeps compiling unmodified — grepped and confirmed every one omits the new trailing param.
        **No** producer/fold/consumer path (`BridgeForwarder`, `GameSnapshotAggregator`,
        `ShimStub`/`ConsumerDataSubscription`/`DataSubscriptionAdapter`) was touched — every one still
        derives size from `Members.Count`, exactly as today.
      - **Gate 1 — FlowTests (shim mapping):**
        `tests/Fct.FlowTests/EventMappingFlowTests.cs`
        `A5b_AllianceGathering_PartySizeDistinctFromMemberCount_PendingP3`: emits
        `new PartyChanged(1, Flow.T0, allianceRoster24, PartySize: 8)` through `ShimStub`/
        `OpTypedConsumerDouble` (the same A5 seam). Asserts `op.PartyList!.Count == 24` (passes — the
        full alliance roster crosses intact) then `op.PartySize == 8`. **Red today:**
        `Assert.Equal() Failure: Expected: 8 Actual: 24` — `ShimStub.cs:35`
        (`PartyListChanged?.Invoke(p.Members, p.Members.Count)`) still derives the SDK `partySize`
        argument from `Members.Count`, ignoring the new field entirely. The pre-existing
        `A5_TypedZonePartyEvents_ReachMappedConsumer` (3-member, non-alliance case) is untouched and
        stays green — it never sets `PartySize`, so the default-0 field changes nothing for it.
      - **Gate 2 — host-fold (chosen over a real satellite subprocess):**
        `tests/Fct.App.Tests/Hosting/GameSnapshotAggregatorTests.cs`
        `Alliance_party_size_survives_the_host_fold_distinct_from_member_count_pending_P3`: adds 24
        combatants (`PartyMembership.Alliance`) then emits
        `new PartyChanged(100, T0, allianceRoster24, PartySize: 8)` onto a real `GameEventBus` +
        running `GameSnapshotAggregator` (the exact production host-fold class). Asserts
        `snap.Party.Members.Count == 24` (passes) then `snap.Party.Size == 8`. **Red today:**
        `Assert.Equal() Failure: Expected: 8 Actual: 0` — `GameSnapshotAggregator.Publish()`
        (`GameSnapshotAggregator.cs:127`) constructs `new PartySnapshot(members, composition)`,
        never reading `PartyChanged.PartySize`, so `PartySnapshot.Size` stays its default `0`.
      - **Why host-fold, not a real satellite subprocess:** the plan's own escape hatch offered
        either; `GameSnapshotAggregator` is the faithful, deterministic home a real satellite
        ultimately routes through (constraint 2 — the datum crosses the host pipe and folds into
        `IGameSnapshot`, the exact seam a native plugin reads per constraint 3), and it is
        fully deterministic (no subprocess, no timing-sensitive wire round-trip) — matching the
        pattern already used by the suite's other `GameSnapshotAggregatorTests` fold assertions. A
        real two-satellite subprocess gate would exercise the same fold one layer further out for no
        additional coverage of the constraint P1.6 gates (the size-preserved claim), at the cost of
        the ~5-10s-per-run subprocess weight `LineStreamDiffTests`/`LateJoinPrimingTests` already carry.
      - **Verification:** both gates fail with the exact size-preserved assertion (`8` vs `24`/`0`),
        never a compile error or harness exception. Whole-solution build
        (`dotnet build ffxiv-combat-tracker.slnx`) and `dotnet build src\Fct.App\Fct.App.csproj`
        both clean, 0 warnings/errors. No regressions beyond the two new intentional reds: full
        `Fct.FlowTests` run 22 tests (21 passed + this 1 new red, was 21/21 before);
        `Fct.App.Tests` run 173 tests (172 passed + this 1 new red, was 172/172 before);
        `Fct.Engine.Tests` (9 passed + the pre-existing P1.2 red, unchanged);
        `Fct.Parser.Legacy.Tests` (9 passed + the pre-existing P1.5 red, unchanged);
        `Fct.Compat.Act.Tests` (67/67) and `Fct.Compat.Shim.Tests` (56/56) both fully green;
        `Fct.Integration.Tests` run 45 tests, 35 passed, 3 skipped (env-gated
        `FrameFixtureGenerator`/`DistTreeGateTests`/`PacketDispatchTests`, unrelated), 7 failed —
        exactly the documented pre-existing reds (P1.2 `OverlaySatelliteTests` + P1.3
        `LineStreamDiffTests`×2 + P1.4 `LateJoinPrimingTests`×2 + P1.5 `RepositorySurfaceLiveTests`)
        plus the known load-sensitive `FourRealPackagesTests` flake — none of these six suites'
        code was touched by this task, and none regressed.
      - **Handoff for P3:** P3.3 must forward the real SDK `partySize` in `OnPartyListChanged`
        (`BridgeForwarder.cs:221-222`, currently discarded per G7); P3.4 must fold
        `PartyChanged.PartySize` into `PartySnapshot.Size` in `GameSnapshotAggregator.Publish()`
        (`:127`) — flips Gate 2 green; P3.5 must rewire `ShimStub.cs:35`,
        `ConsumerDataSubscription.Raise` (`:56`), and the net10 shim's
        `Fct.Compat.Shim/DataSubscriptionAdapter.cs:77` to re-raise `p.PartySize` instead of
        `Members.Count` — flips Gate 1 green. No existing test needs updating/deleting for this
        flip: the old `A5_TypedZonePartyEvents_ReachMappedConsumer` never sets `PartySize`
        (defaults to 0, and 0 never equals `Members.Count` for its 3-member case — wait, it asserts
        `op.PartySize == 3` today via the `Members.Count` derivation; once P3.5 switches to
        `p.PartySize`, that call site must pass `PartySize: 3` explicitly or the assertion goes red
        for the *wrong* reason. P3.5 must update `A5_TypedZonePartyEvents_ReachMappedConsumer`'s
        `new PartyChanged(2, Flow.T0, ...)` call to add `PartySize: 3` alongside the wiring change).

### P2 — The verbatim line stream: one tap ☑

The G14 headline fix. No wire-codec change (the `RAW` frame already carries type + line + original
— `GameEventFrame.cs:57-61`), so **no frame-fixture regeneration** in this phase.

- [x] **P2.1 — Add the facade-seam tap.** In `BridgeForwarder.Start()` (`BridgeForwarder.cs:71-93`),
      alongside the existing ACT-hub taps: subscribe `act.OnLogLineRead`; in the handler, ignore
      `isImport == true` (oracle/import replays are not the live stream), then
      `Enqueue(new RawLogLine(0, ts, type, args.logLine, args.logLine))` where `ts` is
      `args.detectedTime` (the line's own parsed timestamp — never `DateTimeOffset.Now`) and `type`
      per P0.2's verdict. Unsubscribe in `UnsubscribeActEvents()` (`BridgeForwarder.cs:412-420`).
      *Done when:* a satellite replay shows non-chat `RAW` frames at the host with correct types.
      **Verdict:** ✅ DONE. `src/Fct.LegacyHost/BridgeForwarder.cs`: `Start()` adds
      `act.OnLogLineRead += OnLogLineRead;` alongside the existing ACT-hub taps (`:71-93`); new handler
      `OnLogLineRead(bool isImport, LogLineEventArgs args)` ignores `isImport`/null args, then
      `Enqueue(new RawLogLine(0, ts, ParseLineType(line), line, line))` where `line = args.logLine ?? ""`
      and `ts = args.detectedTime > DateTime.MinValue ? new DateTimeOffset(args.detectedTime) :
      DateTimeOffset.Now` (the line's own parsed timestamp, mirroring the existing
      `OnBeforeCombatAction`/`OnEncounterSet` fallback shape — never a bare `DateTimeOffset.Now` when a
      real timestamp exists). New sibling helper `ParseLineType(string raw)` (private static, next to the
      handler): `int.TryParse` of the substring before the first `|`, cast to `LogMessageType`, default
      (`ChatLog = 0`) on empty/malformed input — never reads `args.detectedType` (always 0 on the facade
      path per P0.2). `UnsubscribeActEvents()` (`:412-420`) gained
      `try { act.OnLogLineRead -= OnLogLineRead; } catch { }`.
- [x] **P2.2 — Remove the SDK `LogLine` tap.** Delete the subscription (`BridgeForwarder.cs:130`),
      the `OnLogLine` handler (`:215-216`), and the unsubscribe (`:399`). The consumer-side SDK
      `LogLine` re-raise (`ConsumerDataSurface.cs:46-51`) stays as-is per P0.5 (no target consumer
      binds it; its `seconds` now derives from the line-accurate frame timestamp). *Done when:*
      build green; no `RawLogLine` is produced from the SDK tap anywhere.
      **Verdict:** ✅ DONE. `src/Fct.LegacyHost/BridgeForwarder.cs`: removed `sub.LogLine += OnLogLine;`
      from `SubscribeSdk` (was `:130`), the `OnLogLine(uint eventType, uint seconds, string line)` handler
      (was `:215-216`), and `_sub.LogLine -= OnLogLine;` from `Dispose()` (was `:399`). Grepped
      `BridgeForwarder.cs` post-edit: zero references to `OnLogLine`/SDK `LogLine` remain; the only
      `RawLogLine` construction left in the file is P2.1's facade-seam tap. `ConsumerDataSurface.cs`
      untouched, per the licensing verdict (P0.5).
- [x] **P2.3 — Gate flip.** P1.3 green, both variants: every slice line arrives at the consumer's
      `OnLogLineRead` byte-identical, in order, with correct `LogMessageType` — this is what real
      ACT delivers, reproduced by construction.
      **Verdict:** ✅ DONE — empirically green. Command:
      `dotnet test tests\Fct.Integration.Tests\Fct.Integration.Tests.csproj --filter "FullyQualifiedName~LineStreamDiffTests"`.
      - **Plugin-free variant** (`Plugin_free_facade_tail_lines_do_not_reach_a_rawlog_consumer_today`):
        **PASS**. All 12 slice lines arrive at the consumer's `OnLogLineRead` byte-identical, in order,
        each with the correct `LogMessageType` — `Assert.Equal(Slice, got)` now passes where it
        previously asserted against an empty `Actual`.
      - **Plugin-gated variant** (`Plugin_gated_facade_tail_lines_do_not_reach_a_rawlog_consumer_today`):
        **clean SKIP**, not a false green. Empirically, once the real `FFXIV_ACT_Plugin` finishes its
        async init it calls `ActGlobals.oFormActMain.OpenLog` itself against its own live
        `Network_<pid>_<date>.log` path; `StartLogTail`'s same-path guard cancels the gate's tail on the
        injected slice file and re-tails the plugin's own log — exactly the P1.3 verdict's documented
        "root cause 2". Observed directly: the consumer received 36 real (non-fixture) lines — the
        plugin's own init/debug/territory/combatant lines from a live game process on this machine, not
        the 12-line fixture — proving the P2.1 tap itself forwards verbatim lines correctly (real content
        crossed byte-for-byte) while making this specific gate's byte-diff assertion an environment race
        this task does not control (whether a live game wins the tail before the injected slice is fed).
        `tests/Fct.Integration.Tests/LineStreamDiffTests.cs` gained a documented reclaim-detection guard
        immediately before the headline `Assert.Equal(Slice, got)`: when `loadParser` is true and
        `got.Count > 0` but `got` does not start with `Slice` (content diverges from the fixture),
        `Skip.If(true, "...")` fires with the reclaim explained; a `got.Count == 0` result (no reclaim,
        tap genuinely not forwarding) still falls through to the hard assertion and fails as a real
        regression. This is the task's licensed fallback ("plugin-gated green or clean-skip if the plugin
        path reclaims the tail... keep it a clean skip rather than a false green").
      - **Regression check:** full `Fct.Integration.Tests` run — 45 tests, 35 passed (was 34; the
        plugin-free `LineStreamDiffTests` flip), 5 skipped (the 4 pre-existing env-gated skips +
        this phase's new plugin-gated clean skip, was 4), 5 failed (the pre-existing P1.2
        `OverlaySatelliteTests` + P1.4 `LateJoinPrimingTests`×2 + P1.5 `RepositorySurfaceLiveTests` +
        the known `FourRealPackagesTests` load-sensitive flake — was 7, now 5 with `LineStreamDiffTests`'s
        two former reds removed). `OverlaySatelliteTests`/`FourRealPackagesTests` fail on this machine on
        an `Encounter.damage` mismatch (`got='741240'` vs the oracle baseline) rather than the documented
        P1.2 key-set skip-list assertion; confirmed via `git stash` (reverting both P2 edits and re-running
        the identical two tests) that this exact mismatch — same `got`/`want` values — reproduces
        byte-for-byte on pre-P2 `main`, so it is pre-existing environmental contamination (a live game
        process on this dev machine feeding the plugin's memory-scan pipeline during the replay,
        unrelated to the log-line tap), not a P2 regression.
      - **Full-suite confirmation (no other regressions):** `dotnet build ffxiv-combat-tracker.slnx`
        clean (0 warnings/errors); `Fct.Compat.Act.Tests` 67/67 (incl. P0.1 `LineSeamCoverageTests`);
        `ConsumerLogLineTests`/`LogWriteBackTests`/`ThreeSatelliteTopologyTests`-filtered run 7/7;
        `Fct.Parser.Legacy.Tests` 9/10 (unchanged P1.5 red); `Fct.App.Tests` 172/173 (unchanged P1.6
        red); `Fct.FlowTests` 21/22 (unchanged P1.6 red); `Fct.Engine.Tests` 9/10 (unchanged P1.2 red,
        same 12-key skip-list message as before); `Fct.Compat.Shim.Tests` 56/56.

### P3 — Session state: one additive frame ☐

- [x] **P3.1 — Abstractions.** In `src/Fct.Abstractions/Events.cs`: add
      `sealed record SessionStateChanged(long Sequence, DateTimeOffset Timestamp, string GameVersion,
      GameLanguage Language, GameRegion Region, TimeSpan ServerClockOffset, bool IsChatLogAvailable)
      : GameEvent(Sequence, Timestamp)`. In `Session.cs`: `PartyChanged` gains
      `int PartySize` (`Events.cs:112`); `PartySnapshot` gains `int Size` (`Session.cs:64`);
      `GameClient` gains `TimeSpan ServerClockOffset` + `bool IsChatLogAvailable` init properties
      (`Session.cs:76`). *Done when:* solution builds (all constructors updated).
      **Verdict:** ✅ DONE. `PartyChanged.PartySize`/`PartySnapshot.Size` were already added inert by
      P1.6 — untouched here. Added: `src/Fct.Abstractions/Events.cs` — `SessionStateChanged(long
      Sequence, DateTimeOffset Timestamp, string GameVersion, GameLanguage Language, GameRegion
      Region, TimeSpan ServerClockOffset, bool IsChatLogAvailable) : GameEvent(Sequence, Timestamp)`,
      appended after `GameProcessChanged`, exactly the plan's signature (not yet produced or folded
      anywhere). `src/Fct.Abstractions/Session.cs` — `GameClient` (record with existing 5 positional
      params + the pre-existing `ProcessId` init property) gained two more init properties,
      `TimeSpan ServerClockOffset { get; init; }` and `bool IsChatLogAvailable { get; init; }`, same
      pattern as `ProcessId` — every existing 5-arg positional `new GameClient(...)` call site
      (`GameSnapshotAggregator.cs:173`, `GameSnapshotProvider.cs:36`,
      `Fct.Abstractions.Testing/GameStateFakes.cs:17`, `Fct.Compat.Shim.Tests/DataRepositoryTests.cs:102,113`)
      keeps compiling unmodified — grepped and confirmed none of them break. Whole-solution build
      (`dotnet build ffxiv-combat-tracker.slnx`) clean, 0 warnings/errors. No call site needed
      updating (pure additive init properties + a brand-new unreferenced record). P1.4/P1.5/P1.6
      gates confirmed still red, unchanged: `Fct.Engine.Tests.OracleParityTests` (4/5, same 12-key
      message), `Fct.Integration.Tests` `OverlaySatelliteTests`/`LateJoinPrimingTests`×2/
      `RepositorySurfaceLiveTests` (4 failed, same assertions/messages),
      `Fct.Parser.Legacy.Tests.ConsumerDataRepositoryStubTests` (1/2, same message),
      `Fct.App.Tests.GameSnapshotAggregatorTests` (172/173, same 8-vs-0 assertion),
      `Fct.FlowTests` (21/22, same 8-vs-24 assertion) — every red is byte-identical to its P1.x
      verdict, no flip, no new red.
- [ ] **P3.2 — Codec.** In `src/Fct.Bridge.Contracts/GameEventFrame.cs`: add `TagState = "STATE"`
      encoding `key=value` fields (`ver=`, `lang=`, `region=`, `clockoffms=`, `chat=`) with the
      existing `Enc`/`Dec` escaping; the decoder iterates pairs, ignores unknown keys, defaults
      missing ones. Append `PartySize` as a fourth `PARTY` field; decode falls back to
      `Members.Count` when absent. Regenerate `tests/fixtures/frames/*.frames.tsv` via
      `FrameFixtureGenerator` (never hand-edit) and round-trip in `Fct.App.Tests`
      `BridgeEventFrameTests`. *Done when:* round-trip tests green, including an
      unknown-key-ignored case.
- [ ] **P3.3 — Producer taps.** In `BridgeForwarder`: emit `SessionStateChanged` once from
      `EmitInitialRepositoryState` (`BridgeForwarder.cs:149-164`) and re-emit on `OnProcessChanged`
      (`:145-146` — version can change on relog/patch). Map SDK `Language`→`GameLanguage`, `byte`
      region→`GameRegion`, `ServerClockOffset = _repo.GetServerTimestamp() - DateTime.UtcNow`
      — **but guard the offset (per P0.3):** `GetServerTimestamp()` returns `DateTime.MinValue` with no
      live memory scan, so when it is default / pre-2000, forward `ServerClockOffset = TimeSpan.Zero`
      instead of a ~2000-year garbage span.
      Serve `""` for an unknown version (§3, never a placeholder). Forward the real `partySize` in
      `OnPartyListChanged` (`:221-222`). *Done when:* both frames observed on a replay run.
- [ ] **P3.4 — Host fold + routing.** `src/Fct.Host/StreamCatalog.cs`: route `SessionStateChanged`
      on the existing `repository` stream (`StreamCatalog.cs:48-52`).
      `src/Fct.Host/Hosting/GameSnapshotAggregator.cs`: fold `SessionStateChanged` into
      `IGameSnapshot.Client` and `PartyChanged.PartySize` into `PartySnapshot.Size`. *Done when:* a
      flow test observes the env on `IGameSnapshot.Client` (native-plugin reachability, constraint 3).
- [ ] **P3.5 — Consumer projections.** `ConsumerDataRepository` (`ConsumerDataSurface.cs`): `Apply`
      stores the forwarded env; serve
      `GetGameVersion/GetSelectedLanguageID/GetGameRegion/GetServerTimestamp` (offset-corrected:
      `UtcNow + offset`) and `IsChatLogAvailable` from the mirror; **delete the stubs (`:156-160`)**.
      `ConsumerDataSubscription.Raise` passes `p.PartySize` (`:56`). Net10 shim: same two fixes in
      `Fct.Compat.Shim/DataSubscriptionAdapter.cs:77` + the repository adapter.
      `Fct.Abstractions.Testing/ShimStub.cs` maps `PartyChanged.PartySize` → `op.PartySize`. *Done
      when:* build green; `EventMappingFlowTests` compiles against the real-size semantics.
- [ ] **P3.6 — Machina region (KR/CN only, non-blocking).** In the consumer stand-in bring-up:
      reflectively call `Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.SetRegion(region)`
      from the forwarded region. Default `Global` is already correct for the primary audience —
      implement, but it gates nothing. Where the SDK exposes them, forward the **selected
      language's** resource dictionaries instead of hard-coded `*_EN`
      (`EmitInitialRepositoryState`, `:160-163`).
- [ ] **P3.7 — Gate flips.** P1.5 green (forwarded env) and P1.6 green (alliance size preserved
      end-to-end). Prime-path convergence for this state lands in P4.

### P4 — Generic priming: snapshot-then-stream ☐

- [ ] **P4.1 — Last-line cache.** Host session: cache the last-seen verbatim `RawLogLine` per
      `LogMessageType` in the one-shot set (§3 literal), updated from the rawlog fan-in — no
      decoding, keyed on the typed frame field. Lives with the session state `SatelliteHost` reads
      (alongside `GameSnapshotAggregator`); host-internal (native plugins read typed state from
      `IGameSnapshot`). *Done when:* unit test — fold lines, observe the cache hold the last
      instance per type.
- [ ] **P4.2 — Prime from the snapshot + cache.** `BuildPrimeEvents`
      (`SatelliteHost.cs:388-431`): when the subscriber takes `rawlog` (currently excluded from
      priming), replay the cached lines in ACT emission order (`253/249/250 → 01 → 02 → 40 → 12`)
      ahead of live fan-out (they ride the `SatelliteEgress` prime list, `SatelliteHost.cs:378-379`).
      Synthesize `01`/`02` from the typed snapshot (`ZoneChanged`/`PrimaryPlayerChanged` forms)
      **only** when no cached line exists (fresh-boot fallback). When the subscriber takes
      `repository`, also emit a `SessionStateChanged` built from `snap.Client` (one-shot state — a
      late joiner never sees it otherwise). `zoneParty` priming carries `PartySnapshot.Size` on the
      primed `PartyChanged`. *Done when:* priming is a pure function of snapshot + cache — no
      hand-listed per-field entries beyond the stream→surface mapping.
- [ ] **P4.3 — Gate flip.** P1.4 green: a late joiner converges on zone/map/player/stats/version
      lines and env values from priming alone.

### P5 — Engine surface: the `ACT_UIMods` keys (the irreducible port) ☐

The only phase that edits the parity-gated shared `Fct.Aggregation` binary — it lands last among
code phases so P1's baseline catches any regression, and the ACT-core oracle suite
(`ExportVarsCompatTests`/`OracleParityTests`) must stay green untouched throughout. Port faithfully
and let the P1.2 diff catch divergence; the diff (enumerated keys + values), not the task list, is
the completeness authority.

- [ ] **P5.1 — Port the `CombatantDataExtension` helpers** into `Fct.Aggregation` (new
      `src/Fct.Aggregation/CombatantDataExtension.cs`, namespace `Advanced_Combat_Tracker`), reading
      our `LastKnownTime` (`EncounterLifecycle.cs:19`, surfaced `FormActMain.cs:210`) instead of
      `ActGlobals.oFormActMain`: `Job()` (`:10-20`), `Parry()` (`:22-33`), `Block()` (`:35-46`),
      `BlockParryCount()`, `DirectHeal()` (`:69-81`), `Overheal()` (`:83-94`), `DirectHitCount()`
      (`:96-107`), `CritDirectHitCount()` (`:109-120`), plus `OneOrInt(int)`. Source: FFXIV_ACT_Plugin
      decompile `CombatantDataExtension.cs`.
- [ ] **P5.2 — `Job`: ColumnDef + ExportVar.** Register `CombatantData.ColumnDefs["Job"]` (cell
      `= d.Job()`, `ACT_UIMods.cs:1899-1916`) **and** `CombatantData.ExportVariables["Job"]` (body
      `= d.GetColumnByName("Job")`, `:1918-1928`) — the real formatter's indirection, so direct
      `GetColumnByName("Job")` callers also work.
- [ ] **P5.3 — `ParryPct`, `BlockPct`, `IncToHit`, `OverHealPct`:** register the **full ColumnDef
      chain** each resolves through (`CombatantData.ColumnDefs` →
      `Data.Items[DamageTypeDataIncomingDamage / DamageTypeDataOutgoingHealing].GetColumnByName(...)`
      → `AttackType.ColumnDefs` / `DamageTypeData.ColumnDefs`) **and** the wrapping
      `ExportVariables` entry. Bodies: `ParryPct` `ACT_UIMods.cs:1930-1959` +
      `AttackType.ColumnDefs["ParryPct"]:2080-2097`; `BlockPct` `:1961-1990` + `:2118-2135`;
      `IncToHit` `:1992-2021`; `OverHealPct` `:2137-2166` + `AttackType["OverHeal"]:2193` + the
      `DamageTypeData`/`MasterSwing` OverHeal columns `:2168-2223`.
- [ ] **P5.4 — `DirectHitPct`, `DirectHitCount`, `CritDirectHitCount`, `CritDirectHitPct`:** same
      chain pattern (`CombatantData.ColumnDefs` → `Items[DamageTypeDataOutgoingDamage]` →
      `AttackType.ColumnDefs`) + ExportVar. Bodies: `:2250,2269,2288,2301-2311`;
      `:2319,2357,2370-2380`; `:2388,2426,2439-2449`; `:2457,2495,2508-2518`.
- [ ] **P5.5 — `MasterSwing.ColumnDefs`:** register `StatusDuration` and the plugin's swing columns
      (`Potency`, `StatusEffects`, `DoTBase`, `BuffByte1-3`, `CritRate`, `CritEffects`, `DHRate`,
      `DHEffects`) for any consumer reading `MasterSwing.GetColumnByName`. Source: the
      `MasterSwing.ColumnDefs.Add(...)` block in `ACT_UIMods.cs`. Lower value — gate against the
      baseline.
- [ ] **P5.6 — `LastNDPS`:** port the two extension overloads (`CombatantDataExtension.cs:122-153`),
      reading `LastKnownTime` from `EncounterLifecycle` (advanced per folded swing —
      `FormActMain.cs:453`). **Reproduce the divisor quirk exactly:** combatant
      `= sum / min(Duration, N)`; encounter `= sum / min(Duration, 10.0)` — hardcoded 10, not N.
      Register combatant **and** encounter `Last10/30/60DPS` with the plugin's exact formatter
      bodies (`Data.LastNDPS([SelectiveAllies,] N).ToString("0", CultureInfo.InvariantCulture)`,
      `ACT_UIMods.cs:1808-1879`).
- [ ] **P5.7 — `CurrentZoneName`:** already registered encounter-level (`CombatTables.cs:224`,
      `d.ZoneName`; `ACT_UIMods.cs:1802` registers the identical body). Verify it appears in the
      P1.1 baseline's key set; no code change if the existing registration matches, otherwise move
      it into the `EngineTables` block.
- [ ] **P5.8 — Modern-API assertion.** `EncounterProjector` (`:55-69`) iterates the registered
      formatters, so the keys appear in `EncounterSnapshot/CombatantMetrics.ExportVariables`
      automatically once `EngineTables.Install()` registers them (`ModernEncounterEngine.cs:47`).
      Add a `Fct.FlowTests`/`Fct.Engine.Tests` assertion that `ExportVariables["Job"]` is non-empty
      on a native-plugin `EncounterSnapshot`.
- [ ] **P5.9 — Gate flip + mass check.** P1.2 fully green with an **empty skip-list**. Run the
      corpus-wide mass check once locally (`tools/mass-compare` extended with the
      plugin-in-the-loop baseline); record the result here.

### P6 — Truth-up ☐

- [ ] **P6.1** [`DATA-FLOW.md`](DATA-FLOW.md) §4/§8: the rawlog source (facade log-read seam, one
      tap, verbatim), the plugin metadata layer, the one-shot line-state set + last-line priming,
      the environment/party-size state, OverlayPlugin memory scanning.
- [ ] **P6.2** [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) §2/§4/§14: `ACT_UIMods` metadata as a
      shared-engine responsibility; `ConsumerDataRepository` no longer lists stubbed members;
      OverlayPlugin's region comes from Machina `OpcodeManager` (not the repo); SDK
      `LogLine`=ChatLog-only, `ParsedLogLine`=parsed-except-249/250/253 — neither is a production
      source.
- [ ] **P6.3** [`TESTING.md`](TESTING.md): the two differential gates as first-class parity axes
      (line-stream diff, plugin-in-the-loop ExportVariables diff) + the late-join variants.
- [ ] **P6.4** `GO-LIVE.md` A1: name job-icon correctness, zone display, map-dependent overlay
      features, and one custom-line-driven feature explicitly.
- [ ] **P6.5** Postmortem: mark fixed, link this plan.

## 6. Deferred (post-v1) — kept for the record, do not build for the four-plugin ship

- **Triggernometry `EncounterData.LogLines` append (G13):** append folded `RawLogLine`s to
  `ActiveEncounter.LogLines` in the consumer fold (`Program.cs FoldConsume`), reproducing ACT-core's
  gate (Record-Logs + `InCombat`, `ACT-decompiled FormActMain.cs:19984-19988`). XML-export /
  log-viewer parity only — ACT-core `GetTextExport` never renders `LogLines`, so no measured Trig
  trigger path depends on it.
- **Typed `PlayerStatsChanged` frame (G9):** the `12` line (P2 + P4) covers the log-line consumers;
  build the typed frame when a native consumer needs structured stats.
- **Typed projections of one-shot lines** (`MapChanged`, typed stats): native plugins read these
  via the `RawLogLine` bus stream meanwhile.

## 7. Risks & watch items

- **P5 is the only phase that can regress ACT-core parity** — it edits the shared engine binary
  every replica runs. Mitigation: it lands last among code phases, behind P1's baseline; the
  ACT-core oracle suite (which drives `new FormActMain` → `CombatTables.Setup()` only, never
  `EngineTables.Install()`) stays green untouched.
- **P2 changes live consumer behavior broadly** — consumers start receiving every line type instead
  of malformed chat only. Intended, but the largest behavioral delta: gate on P1.3's byte-diff
  before flipping, and soak one full replay with OverlayPlugin + Triggernometry loaded.
- **The tail is the product seam — keep it healthy.** The line stream now rides the facade's file
  tail (`FormActMain.cs:374-413`, 50 ms poll, byte-level line splitting). A tail stall silences
  triggers *and* combat parsing alike (both hang off `FeedLine`), so the P9-style soak budgets
  should watch line-latency through the tap.
- **Frame-fixture regeneration happens exactly once (P3.2).** P2 adds no wire change; P4 adds no
  wire change. Regenerate via `FrameFixtureGenerator`, never hand-edit.
- **The `STATE` codec must stay additive:** unknown keys ignored, missing keys defaulted — enforced
  by a pinning round-trip test (P3.2), so future state fields never invalidate fixtures again.
- **Server-clock offset** is an approximation (snapshot vs host clock at forward time); acceptable
  for custom-line timestamps, revisit only if a consumer proves offset-sensitive.
- **G6 region is Global-safe, KR/CN-required:** `OpcodeManager` defaults to `Global`; the explicit
  `SetRegion` matters only for KR/CN clients and gates nothing (P3.6).
