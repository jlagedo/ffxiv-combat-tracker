# Compat Gaps — phased implementation backlog

Consolidated, verified gap list across two axes:
- **Surface compat (M0–X):** making the **unmodified** legacy stack (`FFXIV_ACT_Plugin` +
  OverlayPlugin/cactbot + Triggernometry + ACT-Discord-Triggers + ACT.Hojoring) bind and run on
  our clean-room ACT facade.
- **Parser calculation (P):** the clean-room native parser (`Fct.Parser.Native`) reproducing
  ACT's combat-value *calculation* — the simulated DoT/HoT/shield swings — bit-for-bit.

Derived from auditing the real sources (facade) and the decompiled `.parse` assembly (parser).

- **Authority:** [`docs/DATA-FLOW.md`](DATA-FLOW.md) is the data-flow + surface contract;
  [`docs/ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) is the per-plugin map of every ACT member
  consumed. This file is the *actionable backlog* extracted from them: what the unmodified plugins
  touch, what the facade already provides, and what is still missing — grouped by phase.
- **Code under audit:** `src/Fct.Compat.Act` (facade) and `src/Fct.Parser.Legacy` (plugin wrapper).
- **Consumer evidence:** all five plugin source trees under `E:\dev` (OverlayPlugin, Triggernometry,
  ACT-Discord-Triggers, ACT.Hojoring, FFXIV_ACT_Plugin — file:line) + `reference/act-decompiled/`
  (ACT shapes). See `docs/ACT-INTERFACE-MAP.md` for the full member-level map.

### How to read

| Column | Meaning |
|---|---|
| **Status** | `DONE` present in facade · `GAP` missing · `STUB` may be a no-op · `VERIFY` present but untested on the live path |
| **Sev** | `BLOCK` plugin won't bind/init · `BREAK` feature silently dead · `MINOR` cosmetic/edge |

### Phase map

| Phase | Goal | Net gap state |
|---|---|---|
| **M0** Engine | real plugin Started, `AddCombatAction`, DPS, `Network_*.log` | **Done** (Slice 1) |
| **M1** OverlayPlugin | discovery + MiniParse + NetworkProcessors against unmodified OP | **Mostly complete** — 2 new GAPs (`TTS`/`PlaySound` methods, `oFormSpellTimers`) + 1 VERIFY |
| **M2** Triggernometry | host runs no trigger engine; facade just lets the plugin **bind + degrade** | **Minimal compat done** — binding surface present, 1 VERIFY |
| **M3** Discord | trigger/encounter events post on a kill | **Surface present** — all touched members DONE |
| **M4** Hojoring | SpecialSpellTimer/TTSYukkuri/UltraScouter/XIVLog bind + run | **New scope** — needs `FormSpellTimers` API + `Form` members (see below) |
| **X** Cross-cutting | model/event shape completeness for any reflecting consumer | a few MINOR shape gaps |
| **P** Parser calc | native parser reproduces ACT's swing *values* (DoT/HoT/shield simulation) | **~95.6% DPS-sum**; per-tick gaps below |

> The M0–X phases above concern the **facade surface** — letting unmodified plugins bind and
> run. Phase **P** below is a different axis: the **clean-room native parser**
> (`Fct.Parser.Native`) reproducing ACT's combat-value *calculation* bit-for-bit. It is
> independent of M0–X.

---

## M1 — OverlayPlugin (unmodified)

The discovery → MiniParse → NetworkProcessors path is **essentially complete**. Verified present:
the `InitActDone && Handle` init gate, `IsActClosing`, `InvokeRequired`/`Invoke` (inherited —
`FormActMain : Form`), full `ActPluginData` shape (`pluginObj`/`lblPluginTitle`/`lblPluginStatus`/
`cbEnabled`/`pluginFile`/`tpPluginSpace`), `PluginGetSelfData`, `AppDataFolder`,
`ActiveZone.ActiveEncounter` + `GetAllies`, `ExportVariables`/`ColumnDefs`, and the wrapper's
`_iocContainer` → `ILogOutput` custom-log path (`WrappedFfxivPlugin.cs:78` — exact field name).

| # | Surface | Consumed by | Status | Sev | Action |
|---|---|---|---|---|---|
| M1‑1 | `ActGlobals.oFormImportProgress` (+ `FormImportProgress : Form` type) | `MiniParseEventSource.cs:189` reads `?.Visible` each CombatData tick | **DONE** | BLOCK | Static field on `ActGlobals.cs` (null in our headless host) + `FormImportProgress.cs` stub so OP's IL field-token resolves; `?.Visible` short-circuits to `false` = "not importing". |
| M1‑2 | `_iocContainer` → `GetService("…ILogOutput")` → `WriteLine` round-trip | `FFXIVRepository.cs:458/498`; NetworkProcessors custom lines 256+ | **VERIFY** | BREAK | Field name forwarded correctly; confirm a custom line (e.g. MapEffect 257) actually round-trips as a log-line event end-to-end once a live process is attached. |
| M1‑3 | `FormActMain.TTS(string)` + `PlaySound(string)` **methods** | `MiniParseEventSource.cs:65/75` (cactbot `say`/`play_sound`); `AddonExampleEventSource.cs:34` | **GAP** | BREAK | Facade defines only the `PlaySoundDelegate`/`PlayTtsDelegate` *fields* — no methods. A cactbot TTS/sound action calls the **method** → `MissingMethodException`. Add `public void TTS(string)` / `PlaySound(string)` that invoke the corresponding delegate field (matches real ACT; also wires the Discord/TTSYukkuri delegate-hijack to OP/cactbot output). |
| M1‑4 | `ActGlobals.oFormSpellTimers` field + `FormSpellTimers` type (`OnSpellTimerNotify`/`OnSpellTimerRemoved` events) | `SpellTimerOverlay.cs:25/50` (`+=` on each event) | **GAP** | BREAK | Only reached if the user adds a SpellTimer overlay, but then `+=` on a null field → NRE / bind failure. Add non-null `oFormSpellTimers` stub (`FormSpellTimers` exposing the two events, never fired) so the subscription binds inertly. Shared with M4 (Hojoring drives the full API). |

---

## M2 — Triggernometry (minimal compat — verified against real source)

**Decision: the host runs no trigger engine.** Triggernometry ships its own regex/timer/TTS engine
and drives it off the `BeforeLogLineRead`/`OnLogLineRead` stream — it does not need ACT to evaluate
triggers. So the facade's job is only to let the unmodified `TriggernometryProxy` **bind and degrade
gracefully**: every member its `ProxyPlugin` touches resolves, the trigger-import path finds an empty
set, and export/history hooks return inert values instead of throwing. Custom-trigger authoring stays
in Triggernometry's own UI, not ours.

Verified directly against `E:\dev\Triggernometry\Source\TriggernometryProxy\ProxyPlugin.cs` (real
source, not inferred). At init Triggernometry binds entirely against members that already exist:
`oFormActMain`, `BeforeLogLineRead`/`OnLogLineRead`/`OnCombatStart`/`OnCombatEnd`, `InCombat`,
`CurrentZone`, `GlobalTimeSorter`, `ActPlugins`, `ActiveZone`, `AppDataFolder`, `PluginGetSelfData`,
`PlayTtsMethod`/`PlaySoundMethod`. The remaining surfaces are only reached when a running trigger
fires an export/corner/log action; they are stubbed so those paths never throw.

| # | Surface | Consumed by (`ProxyPlugin.cs`) | Status | Sev | Action |
|---|---|---|---|---|---|
| M2‑1 | `CustomTriggers` / `ActiveCustomTriggers` (`SortedList<string,CustomTrigger>`) + `CustomTrigger` type | `HasCustomTriggers`/`GetCustomTriggers` (:398–428) read `Value.Category/RestrictToCategoryZone/Active/ShortRegexString/SoundData/SoundType/TimerName/Tabbed/Timer` | **DONE** | BLOCK | Empty `SortedList`s on `FormActMain` + inert `CustomTrigger` model (`TriggerCompat.cs`). `Count==0` → nothing imported; Trig's own triggers still run. |
| M2‑2 | `ZoneList` (`List<ZoneData>`, `[i].Items`) | `ExportLastEncounter` (:353–356) walks zone/encounter history | **DONE** | BREAK | `FormActMain.ZoneList` holds the live `ActiveZone`; `ZoneData.Items` already a `List<EncounterData>`. |
| M2‑3 | `GetTextExport(EncounterData, TextExportFormatOptions)` + private `defaultTextFormat` field + `TextExportFormatOptions` type | `ExportActiveEncounter`/`ExportLastEncounter` (:349–374) reflect `defaultTextFormat` by name, pass it to `GetTextExport` | **DONE** | BREAK | Non-null private `defaultTextFormat` (reflected by exact name) + minimal `GetTextExport` returning `(duration) title`. No formatter engine. |
| M2‑4 | `LogLineEntry` type; `ActiveEncounter.LogLines : List<LogLineEntry>` | `ACTEncounterLog` (:558) `LogLines.Add(new LogLineEntry(...))` | **DONE** | BREAK | `LogLineEntry` added (4-arg ctor); `EncounterData.LogLines` retyped from `List<object>`. |
| M2‑5 | `SetEncounter` / `EndCombat` / `ChangeZone` / `InCombat` | manual combat-state control (:315–338) | **DONE** | — | Present (`FormActMain.cs`). |
| M2‑6 | `CornerControlAdd` / `CornerControlRemove`; private `tc1` TabControl | corner notifications (:233/:256); `LocateTab` (:186) | **OMITTED** | MINOR | All three are reflected with a `!= null` guard → absence degrades silently (no corner popup, no tab locate). Not implemented by design. |
| M2‑7 | Plugin **status label string** | `GetInstance` (:444/:446) matches `lblPluginStatus.Text == "FFXIV Plugin Started."`/`"FFXIV_ACT_Plugin Started."`, version ≥ 2.0.4.6 | **VERIFY** | BLOCK | `ActPluginData` shape DONE; confirm the facade sets the **exact** status string and registers the FFXIV plugin before Trig inits. |

**Phantoms removed** (the old backlog listed this; the real source does not touch it):
`oFormActMain.CustomTriggerCheck(...)` — does not exist; Triggernometry registers its *own*
`HasCustomTriggers`/`GetCustomTriggers` as internal hooks and reads `CustomTriggers` directly.

**Correction:** an earlier revision listed `ActGlobals.oFormSpellTimers` / `SpellTimer` as a phantom
("no consumer in the supported stack"). That was wrong — Triggernometry and Discord-Triggers don't
touch it, but **OverlayPlugin** (`SpellTimerOverlay.cs`) and **Hojoring** do. It is now tracked as
**M1‑4** (OP, inert stub) and **M4** (Hojoring, full API).

---

## M3 — Discord-triggers (surface present)

Verified against `E:\dev\ACT-Discord-Triggers`: the only `oFormActMain` members it touches are
`AppDataFolder`, `Handle`, `PlaySoundMethod`, `PlayTtsMethod`, `PluginGetSelfData`,
`WriteExceptionLog` — **all DONE**. It does **not** call `GetTextExport`/`ZoneList`/`CornerControl`,
and does not require Triggernometry to be loaded (only an indirect config-level link).

| # | Surface | Consumed by | Status | Sev | Action |
|---|---|---|---|---|---|
| M3‑1 | `PlayTtsMethod` / `PlaySoundMethod` as **settable** delegate fields | Discord *replaces* them to reroute audio through its bridge | **DONE** | — | Public assignable fields (`FormActMain.cs:27`). |
| M3‑2 | `oFormActMain.PluginGetSelfData(this)` | Discord locates its own bridge dir (node.exe/bundle.js) | **DONE** | — | Returns this plugin's `ActPluginData`. |
| M3‑3 | `AppDataFolder.FullName` | Discord config/bridge path | **DONE** | — | Present. |
| M3‑4 | `WriteExceptionLog(...)` | Discord error logging | **DONE** | — | Present. |
| M3‑5 | `Handle` | Discord window-handle marshaling | **DONE** | — | Inherited via `Form`. |

The minimal `GetTextExport` from M2‑3 also covers any encounter-summary export, though no consumer in
the supported stack currently calls it.

---

## M4 — Hojoring (new scope — not yet started)

ACT.Hojoring (`SpecialSpellTimer`, `TTSYukkuri`, `UltraScouter`, `XIVLog`) uses ACT as a **service
bus + UI host**, not a data source — combat data comes from FFXIV_ACT_Plugin's `DataRepository` via
`pluginObj` reflection, which the `WrappedFfxivPlugin` seam already serves. The new facade work is
ACT's built-in **spell-timer subsystem** plus a wider set of `Form`-inherited members. Verified
against `E:\dev\ACT.Hojoring\source` (see `docs/ACT-INTERFACE-MAP.md` §5/§7c).

Already satisfied by the existing facade: `oFormActMain`, `IActPluginV1`, `InitActDone`, `ActPlugins`
+ `pluginObj` reflection, `OnLogLineRead`/`BeforeLogLineRead` (incl. reflectable multicast field for
handler-list surgery), `CurrentZone`, `InCombat`, `EndCombat`, `PlayTtsMethod`/`PlaySoundMethod`
(delegate-field hijack), `PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, `RestartACT`.

| # | Surface | Consumed by | Status | Sev | Action |
|---|---|---|---|---|---|
| M4‑1 | `FormActMain.TTS(string)` / `PlaySound(string)` **methods** | sound/TTS output across SoundController/TimelineController | **GAP** | BREAK | Same fix as **M1‑3** — add the two methods routing through the delegate fields. Closes both at once. |
| M4‑2 | `ActGlobals.oFormSpellTimers` + `FormSpellTimers` full API: `RebuildSpellTreeView()`, `TimerDefs`, `RemoveTimerDef(...)`, `AddEditTimerDef(TimerData)`, `NotifySpell(...)` | `SpellsController` drives ACT's native spell-timer tree | **GAP** | BREAK | Beyond M1‑4's inert event stub: a working `FormSpellTimers` with a `TimerDefs` collection + add/remove/notify. Plus the `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame` model family (all absent). Largest M4 item. |
| M4‑3 | `FormActMain.CornerControlAdd/Remove(Control)` as **real** methods | SpecialSpellTimer title-bar toggle button | **GAP** | MINOR | Trig only reflects these with a null guard (M2‑6, OMITTED); Hojoring calls them directly → must be real (can be no-op that tracks the control). |
| M4‑4 | `Form`-inherited `WindowState` (settable), `CanFocus`, `IsHandleCreated`, `IsDisposed` | UI marshal + lifecycle guards (`PluginCore`, `ActInvoker`, `LogParser.RaiseLog`) | **GAP** | MINOR | `FormActMain : Form` already inherits all four; verify the headless form returns sane values (`IsHandleCreated`/`CanFocus` true once shown, `IsDisposed` false). `WindowState` set is a no-op in the headless host. |
| M4‑5 | `ActPluginData.lblPluginStatus.Text == "...Started"` | `HelpViewModel` confirms FFXIV plugin running | **VERIFY** | BLOCK | Same exact-status-string requirement as M2‑7; one fix covers both. |

---

## X — Cross-cutting shape completeness

Members any reflecting/binding consumer may touch, independent of phase.

| # | Surface | Consumed by | Status | Sev | Action |
|---|---|---|---|---|---|
| X‑1 | `FormActMain` events `ActLifecycleChanged`, `LogFileRenamed`, `XmlSnippetAdded`, `BeforeClipboardSet`, `UrlRequest` | a plugin `+=` on any of these | **DONE** | MINOR | Declared as bindable no-op publishers on `FormActMain` + delegate/EventArgs types ported from the decompile (`Primitives.cs`). Never fired (no UI/clipboard/XML-share/URL path in the headless host); present so `+=` binds. |
| X‑2 | `LogLineEventArgs.companionLogName` (readonly) + 2nd ctor overload | consumers reading the field | **DONE** | MINOR | Readonly `companionLogName` field + 6‑arg ctor overload added (`Primitives.cs`), matching ACT. 5‑arg ctor defaults it to `string.Empty`. |
| X‑3 | `RestartACT(...)`, `PluginDownload(...)`, `UnZip(...)` | legacy auto-updaters | **STUB** | MINOR | Stub — we do not self-update legacy plugins. `RestartACT` present; add `PluginDownload`/`UnZip` no-ops if a consumer binds them. |

---

## P — Native parser combat-value calculation (simulated swings)

Divergences between `Fct.Parser.Native` (`CombatLogParser` + `PotencySimulator`) and the real
FFXIV_ACT_Plugin in **swing-value calculation**. Citations point at the decompiled `.parse`
assembly: `E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\…\FFXIV_ACT_Plugin.Parse\`.

**Scope.** Deterministic, log-derived swing types are already bit-exact or near it (auto 99.98%,
ability 99.85%, power 99.90%, status 97.6%, heal 91%, action 91%, real ground-AoE DoT/HoT exact).
The gaps are almost entirely in ACT's *simulated* `(*)` swings — DoT/HoT ticks and damage shields
the plugin synthesizes from bundled potency data, which depend on internal per-source/per-target
state we only partially reproduce. Current simulated parity: **~95.6% of ACT's damage sum** (the
DPS signal); per-tick bit-exact ~1.5% (DoT) / ~28% (HoT).

| # | Gap | ACT behavior (source) | Ours | Sev | Cost |
|---|---|---|---|---|---|
| **P‑0** | **RNG crit/DH per tick** (uncloseable) | `DoTSimulator.SimulateTicks` draws crit/DH with time-seeded `new Random()`; ~29% of ticks carry a bit the real plugin wouldn't reproduce on re-run | Reproduce the exact individual-crit branch; non-crit ticks deterministic, crit ticks inherently divergent | BLOCK (hard limit) | — |
| **P‑1a** | **Buff bytes not source-correlated** | `ParseStrategyAddStatus.AddOrUpdateStatus` (190–199): `EffectByte0/1/2` pulled from the specific 21/22 `ActionEffect` matched by source+target+statusId | `_applyParams` keyed `(recipient, statusId)` — source dropped, overwritten by last applier → wrong `Param0/1/2` (observed spurious ×0.85 / ×1.51) | BREAK | low |
| **P‑1b** | **One-shot buff consumption** | `PotencyStatusApplication` sets `AppliedTimestamp`; consumed buffs (Kaiten/Boost/Kassatsu/LogosBoost/Harmonized/Tingling/Life Surge/Reassemble) skipped after first use (`ApplyStatusEffectToDamageEntry`:69) | No consume tracking — keeps applying until duration ends → inflated `potMult` | BREAK | low |
| **P‑1c** | **`CalculatedPotency`-side buffs ignored** | `DamageAddPotency` adds to potency, `PotencyMultiplier` ×it (`ApplyStatusEffectToDamageEntry`:76–93); heal adds `GetHealPotency` | Use raw action/DoT potency; fold neither → biased median + amount when active | BREAK | med |
| **P‑1d** | **Zone/category-limited multipliers skipped** | Applies only when `LimitToZoneId==ZoneId`, `LimitToActionCategory==actionCategory` match (line 74) | `BuffApplies` honors action-id/damage-type limits but **drops zone- and category-limited effects** (sim tracks neither) | BREAK | med-high |
| **P‑1e** | **Duration-window asymmetry** | source buffs `Duration+1s`, target debuffs exact `Duration` (`ApplyStatusEffects`:31/43); DoT statuses don't refresh `Param1` on reapply (line 184) | `Expired` uses `Duration+1` for both; `Param1` always refreshed | MINOR | low |
| **P‑1f** | **Override-status remapping** | `GetOverrideStatusIds`: Sacred Soil, Crest of Time, Wheel of Fortune, Improvisation, Undying Flame, Standard Finish Partner take bytes from a *different* status id | Literal statusId only | MINOR | low |
| **P‑2a** | **Crit-buff accumulation + rate exclusion** | `CritDhStatusApplication.CalculateCrit` accumulates `CritBuffAmount` (Chain Stratagem +10%, Inner Chaos/Life Surge = 100%, …); running crit mean updates **only when `CritBuffAmount==0`** (`CriticalHitDamage`:29) | `Sim.CritBuff` always 0; crit mean updates on every hit → skewed tick crit rate | BREAK | med |
| **P‑2b** | **Direct-hit buff accumulation** | `CalculateDirectHit` accumulates `DirectHitBuffs` (Full Metal Field=100%), subtracts before use | None | MINOR | low |
| **P‑2c** | **Medicated/Weakness calibration exclusion** | `IgnoreSomeStatusesExceptAtStart`: with Medicated/Weakness/BrinkOfDeath + >10 swings, `CalculatedPotency=0` → hit excluded from attack-power median | No exclusion; tincture/raise hits pollute median | MINOR | low |
| **P‑3a** | **Proc hits not excluded from calibration** | `SourcePotencyDamage`/`SourcePotencyHeal`:13 skip hits with non-empty `ProcActionName` | We don't identify procs → enter median | MINOR | med |
| **P‑3b** | **Per-target-index potency** | `GetDamagePotency(TargetIndex, combo)` / `GetHealPotency(TargetIndex)` — AoE secondary targets use falloff potency | Only index-0 potency dumped; we calibrate from `TargetIndex==0` hits only (unbiased but AoE-only players contribute nothing) | MINOR | med (needs per-index dump) |
| **P‑4** | **Special simulation paths** | Kardia/Kardion (`ChooseKardionEffect`), Pneuma deferred (`ProcessPneumaTick`), deferred events (`ProcessRemovedDeferredTick`/`ProcessDeferredEvents`), per-status tweaks Kaeshi Higanbana (+15), Blade of Valor (÷2), Wildfire (instant MaxTicks) — `CreateSimulatedDamageState`:114–143 | None of these special paths modelled | MINOR | low-med each |
| **P‑5** | **Shield `Potency` type** | `DamageShieldSimulator` Potency shields = HealMedian × potency × mult | Modelled but inherits heal-calibration + buff gaps; shield-specific cases (Succor/Brutal Shell variants) not handled. `TargetHpPercent`/`HealPercent` shields **bit-exact**. Shields overall ~60% bit-exact | BREAK | med |
| **P‑6** | **Threat swings (type 10)** | `ReportCombatData` emits enmity swings from EntryType 24/25/26 (`oracle=3,655`) | Classified `EffectKind.Threat`, no `CombatAction` emitted (enmity not value-decoded). Not a DPS signal | MINOR | low |

**Close order (cheapest × highest impact first):** P‑1a → P‑1b → P‑2a → P‑2c/P‑3a → P‑1c →
P‑1d → P‑1e/P‑1f/P‑4. Everything in P‑1/P‑2 is fully present in the decompiled source — no new
game-data extraction beyond what `--dump-tables` already produces. P‑0 (RNG), P‑3b (per-index
potency), and P‑6 (threat) are uncloseable or off the DPS-parity path. Full prose +
measurement harness in [`docs/TESTING.md`](TESTING.md).

---

## Already verified present (do not re-investigate)

Facade members confirmed implemented during this audit — listed so future passes skip them:
`InitActDone`, `IsActClosing`, `InvokeRequired`, `Invoke`, `Handle` (inherited via `Form`),
`ActPluginData.{pluginObj,lblPluginTitle,lblPluginStatus,cbEnabled,pluginFile,tpPluginSpace}`,
`PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, `RestartACT`, `UpdateCheckClicked`,
`SetEncounter`, `EndCombat`, `ChangeZone`, `PlayTtsMethod`, `PlaySoundMethod`,
`ActGlobals.oFormImportProgress` (+ `FormImportProgress` stub),
`ActiveZone.ActiveEncounter`, `GetAllies`, `GetCombatantList`,
`EncounterData.{LogLines,Tags,EncId,EndTime,Active}`, `CombatantData` static damage-type tables
(`OutgoingDamageTypeDataObjects`, `DamageTypeDataOutgoingDamage`, …),
`BeforeLogLineRead`/`OnLogLineRead`/`OnCombatStart`/`OnCombatEnd`, `InCombat`, `CurrentZone`,
`GlobalTimeSorter`, `ActPlugins`, the Triggernometry minimal binding surface
(`CustomTriggers`/`ActiveCustomTriggers`/`CustomTrigger`, `ZoneList`, `GetTextExport` +
`defaultTextFormat`/`TextExportFormatOptions`, `LogLineEntry`),
`LogLineEventArgs` (incl. `companionLogName` + 6‑arg ctor)/`CombatToggleEventArgs`/
`CombatActionEventArgs`, the cross-cutting `FormActMain` events
(`ActLifecycleChanged`/`LogFileRenamed`/`XmlSnippetAdded`/`BeforeClipboardSet`/`UrlRequest`
+ their delegate/EventArgs types), and the wrapper's
`_iocContainer`/`DataRepository`/`ILogOutput` forwarding.
