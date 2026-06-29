# ACT Interface Map & Implementation Guide

The **single source** for the host↔plugin contract our facade (`src/Fct.Compat.Act`) must
impersonate: every member each supported legacy plugin touches on the **ACT host**
(`Advanced_Combat_Tracker`, the generic `Advanced Combat Tracker.exe` — *not* FFXIV_ACT_Plugin).
Each interface is documented **once**, grouped by function, with its members, consumers, why it's
used, status, and strategy. The non-ACT integration surfaces (FFXIV SDK seam, OverlayPlugin web
layer, Discord audio bridge) are the last sections.

This document is the authority for the **surface/binding axis** (making the unmodified plugins load
and run). Companion docs:
- [`DATA-FLOW.md`](DATA-FLOW.md) — the data-flow narrative through the upstream stack.
- [`DPS-CALCULATION-GAPS.md`](DPS-CALCULATION-GAPS.md) — the independent **calculation axis**:
  divergences in the native parser's combat-value calculation (simulated DoT/HoT/shield values).

## Status legend

| Glyph | Meaning |
|:--:|---|
| ✅ | **DONE** — implemented + functional |
| 🟡 | **STUB / VERIFY** — present but inert *by design*, or present but untested on the live path |
| ❌ | **GAP** — missing; must build for the four-plugin target |
| ⬜ | **M4** — Hojoring-only batch; not yet built |

## Status board — remaining work (the trackable checklist)

**Four-plugin target — must build:**
- [ ] **G‑1** ❌ `TTS(string)` / `PlaySound(string)` **methods** → route to delegate fields — §6 — BREAK
- [ ] **G‑2** ❌ `ActGlobals.oFormSpellTimers` + inert `FormSpellTimers` (2 events) — §7 — BREAK
- [ ] **G‑3** ❌ `FormActMain.DpiScale` property → `1f` — §10 — MINOR
- [ ] **VERIFY** 🟡 `BeforeLogLineRead` reflectable multicast field; exact "…Started" status string set before Trig/OP/Hojoring init — §3 / §1
- [ ] **G‑4** 🟡 `TraySlider` / `ButtonLayoutEnum` inert stubs — only if a bind throws — §11

**M4 — Hojoring (not yet built):**
- [ ] **M4‑1** ⬜ Hojoring audio — closed automatically by **G‑1** (same methods) — §6
- [ ] **M4‑2** ⬜ working `FormSpellTimers` + `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame` — §7
- [ ] **M4‑3** ⬜ real `CornerControlAdd`/`CornerControlRemove` methods — §10
- [ ] **M4‑4** ⬜ `Form` lifecycle members `WindowState`/`CanFocus`/`IsHandleCreated`/`IsDisposed` — §10

**Done — no action (checked = implemented):**
- [x] §1 Lifecycle, identity & discovery · [x] §2 Producer write-path · [x] §3 Inbound log seam *(pending VERIFY)*
- [x] §4 Consumer read-path · [x] §5 Combat-state events · [x] §8 Custom-trigger import · [x] §9 Encounter export
- [x] §11 Self-update (inert stubs) · [x] §12 Logging · [x] §13 Cross-cutting shape · [x] §14 FFXIV SDK seam · [x] §15 OverlayPlugin web (non-goal)

> **Net:** the four-plugin interface work is **G‑1 + G‑2 + G‑3** plus verification. M4 (Hojoring
> spell-timer subsystem) is the larger batch on top. Everything else is DONE or STUB-by-design.

## Guiding principles (these decide how much of each interface we build)

1. **Impersonate ACT only enough to make the plugins bind and run.** We are *not* reproducing ACT's
   UI, options forms, encounter-history database, HTML/web export, plugin-manager, or its built-in
   spell-timer/timeline editors. For each member the question is "what does the plugin *do* with the
   return value," and we implement the smallest thing that keeps that behavior correct.
2. **Audio is owned by ACT-Discord-Triggers — the official audio stack.** ACT's real `TTS(string)` /
   `PlaySound(string)` route through the swappable `PlayTtsMethod` / `PlaySoundMethod` delegates
   (decompile `FormActMain.cs:4237/4292`). We reproduce that indirection; Discord-Triggers installs
   the real sink by swapping those delegates, and everything else flows into it for free. See §6.
3. **Live game state flows through the FFXIV_ACT_Plugin SDK seam, not ACT.** ACT only holds the
   *aggregated* DPS rollup. Combatant tables, raw/decoded packets, identity and name dictionaries
   live behind `IDataRepository`/`IDataSubscription`/`_iocContainer` on the FFXIV plugin — see §14.
4. **Self-update is out of scope.** We do not download or restart legacy plugins; those members are
   inert stubs that satisfy binding.
5. **Reflection-discovered / failsafe-registered members may simply be absent or no-op** where the
   consumer guards them (`GetMethod(...) != null`, try/catch, `FailsafeRegisterHook`). We build them
   only when a consumer calls them *unguarded*.

## Method & scope

Each plugin source tree was swept for references into the `Advanced_Combat_Tracker` namespace;
symbols that merely *look* like ACT members but are the plugin's own types were excluded. ACT
signatures verified against the decompile at `E:\dev\ACT-decompiled\Advanced_Combat_Tracker\`.

All five plugins are in scope. FFXIV_ACT_Plugin, OverlayPlugin, Triggernometry and Discord-Triggers
are the original build target; **ACT.Hojoring** is the heaviest — its extra surface (chiefly the
full spell-timer subsystem) is the not-yet-built batch tracked as **M4**.

## Plugins at a glance

Consumers used in the tables: `FFXIV` = FFXIV_ACT_Plugin · `OP` = OverlayPlugin/cactbot ·
`Trig` = Triggernometry · `Disc` = ACT-Discord-Triggers · `Hojo` = ACT.Hojoring.

| Plugin | Role | Sections it touches | Status |
|---|---|---|---|
| **FFXIV** | producer — writes parsed swings into ACT's aggregation | §1 §2 §3 §11 §12 | ✅ |
| **OP** | consumer — reads CombatData, serves web overlays | §1 §3 §4 §6❌ §7❌ §10❌ §11 §14 | ✅ except G‑1/G‑2/G‑3 |
| **Trig** | regex/timer engine off the log stream | §1 §3 §5 §8 §9 §10 §11 §12 §14 | ✅ binds + degrades |
| **Disc** | the official audio sink (delegate hijack) | §1 §6 §11 §12 | ✅ |
| **Hojo** | spell-timer / TTS / scouter suite | §1 §3 §4 §6❌ §7⬜ §10⬜ §12 §14 | ⬜ M4 surface |

---

# Interface sections

Each table: **St** = facade status of that member · **member** · **consumers** · **why**.

## 1. Plugin lifecycle, identity & discovery — ✅ DONE

How every plugin loads, finds the host, and discovers the other plugins. `ActPlugins` is the
**service registry** — consumers scan it to find themselves and the FFXIV plugin (matched by
`lblPluginTitle.Text` starting `FFXIV_ACT_Plugin` + `cbEnabled.Checked` + non-null `pluginObj`),
then reflect the FFXIV `pluginObj` for live data (§14).

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `IActPluginV1.{InitPlugin(TabPage,Label),DeInitPlugin}` | all | the load contract / entry point |
| ✅ | `ActGlobals.oFormActMain` | all | root host handle; everything dereferences it |
| ✅ | `InitActDone` | OP, Trig, Hojo | "ACT booted" gate (busy-waited before phase-2 init) |
| ✅ | `IsActClosing` | OP | skip teardown on shutdown |
| ✅ | `Handle` (`Form`) | OP, Disc, Hojo | window HWND for parenting / marshaling |
| ✅ | `Invoke` / `InvokeRequired` (`Form`) | OP, Trig, Disc, Hojo, FFXIV | marshal onto ACT's UI thread |
| ✅ | `GetVersion()` | OP | gate: requires ACT ≥ 3.8.0.281 |
| ✅ | `AppDataFolder` | all | config / install / cache / bridge paths |
| ✅ | `ActPlugins` (`List<ActPluginData>`) | OP, Trig, Hojo | the service registry (discover self + FFXIV plugin) |
| ✅ | `ActPluginData.{pluginObj,pluginFile,lblPluginTitle,lblPluginStatus,cbEnabled,tpPluginSpace}` | OP, Trig, Disc, Hojo, FFXIV | identity, FFXIV-plugin status/checkbox/tab, the reflection seam |
| ✅ | `PluginGetSelfData(this)` | all | self-locate own `ActPluginData` / install dir |

**Strategy.** All present (Slice 1). `Invoke`/`Handle` come free from `FormActMain : Form`. The
load-bearing requirement is that the host populates `ActPlugins` with the FFXIV entry whose
`pluginObj` is our `WrappedFfxivPlugin` and whose `lblPluginStatus.Text` is the exact "…Started"
string (🟡 **VERIFY** — shared by Trig/OP/Hojoring init). `GetVersion()` returns `3.8.5.288`.

## 2. Producer write-path (aggregation) — ✅ DONE

The only writer into ACT. FFXIV_ACT_Plugin converts each parsed entry to a `MasterSwing` and calls
`AddCombatAction`, gated by `SetEncounter`/`InCombat`; `ACT_UIMods` rewrites ACT's *static* table
model so the rollup exposes the FFXIV columns + the `ExportVariables` keys OP later reads (§4).

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `ActGlobals.charName` | FFXIV | local player name on swing source/target |
| ✅ | `AddCombatAction(MasterSwing)` | FFXIV | **core write path** — every parsed swing |
| ✅ | `SetEncounter(DateTime,att,vic)→bool` | FFXIV, Trig | open/continue encounter; gates each swing (Trig: manual combat-state) |
| ✅ | `ChangeZone(string)` | FFXIV | zone transitions |
| ✅ | `EndCombat(bool)` | FFXIV, OP, Trig, Hojo | end the encounter |
| ✅ | `InCombat` | FFXIV, OP, Trig, Hojo | combat-state gate / read |
| ✅ | `GlobalTimeSorter` | FFXIV, Trig | stable per-timestamp swing ordering |
| ✅ | `LastKnownTime` | FFXIV | rolling-window `LastNDPS` calc |
| ✅ | `MasterSwing` (10-arg ctor, `.Tags/.Damage/.Time/.Special/.DamageType/.AttackType/.Critical`) + static `ColumnDefs` | FFXIV (construct), OP (read, §4) | every swing; registers FFXIV columns |
| ✅ | `Dnum` (`.NoDamage/.Miss/.Death`, implicit `long`) | FFXIV | damage sentinels + wrap |
| ✅ | static model registration: `CombatantData`/`EncounterData`/`AttackType` (`ColumnDefs`, `ExportVariables`, `OutgoingDamageTypeDataObjects`) + `DamageTypeData`/`DamageTypeDef`/`ColumnDef`/`TextExportFormatter` | FFXIV | register FFXIV columns + the `ExportVariables` keys OP reads |
| ✅ | `ValidateLists` / `ValidateTableSetup` | FFXIV | commit after mutating the model |
| ✅ | `ActLocalization.LocalizationStrings["attackTypeTerm-all"]` | FFXIV | localized "All" bucket key |

**Strategy.** Done — verified bit-for-bit on captured combat (`Aggregation.cs` + `CombatTables.cs`;
Slice 1 S5; TESTING.md "Differential ACT-engine compat"). The clean-room *value calculation* of
simulated DoT/HoT/shield amounts is the separate axis in
[`DPS-CALCULATION-GAPS.md`](DPS-CALCULATION-GAPS.md) — not an interface concern.

## 3. Inbound log seam — ✅ DONE (🟡 1 VERIFY)

FFXIV_ACT_Plugin owns parsing: it points ACT at its `Network_*.log`, overrides timestamp parsing,
and rewrites each line in `BeforeLogLineRead` before ACT rebroadcasts it as `OnLogLineRead`. This
stream is the **most important consumer surface after CombatData** — Trig, Hojoring and OP's
`LogParseOverlay` all tap it, and Hojoring/`LogParseOverlay` *reflect the event's backing field* to
reorder handlers (run first).

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `OpenLog(bool,bool)` | FFXIV | point ACT at the plugin's `Network_*.log` and (re)open |
| ✅ | `LogFilePath` / `LogFileFilter` | FFXIV | redirect ACT's active log |
| ✅ | `TimeStampLen` / `LogPathHasCharName` / `ZoneChangeRegex` | FFXIV | configure ACT's line parsing |
| ✅ | `GetDateTimeFromLog` (`DateTimeLogParser`) | FFXIV | plugin owns timestamp parsing (saved/restored) |
| 🟡 | `BeforeLogLineRead` (event; **reflectable multicast field**) | FFXIV, OP, Trig, Hojo | inbound hook; OP/Hojo unhook-all-and-insert-first via reflection — **VERIFY** shape |
| ✅ | `OnLogLineRead` (event) | Trig, Hojo, OP | rebroadcast parsed lines — the trigger/parse feed |
| ✅ | `LogFileChanged` (event) | FFXIV | import-vs-live handling |
| ✅ | `LogLineEventArgs.{logLine,originalLogLine,detectedType,detectedTime,detectedZone,companionLogName}` | FFXIV, OP, Trig, Hojo | line payload fields |
| ✅ | `ReadThreadLock`, `.Visible` | FFXIV | gate the log-writer thread startup |
| 🟡 | `GenerateAttackTypeGraph` / `FindControl<Button>` / `ActCommands(string)` | FFXIV | graph generator / Clear-button hook / `/echo` — STUB (UI/diagnostic) |

**Strategy.** Present + fired via `FireBeforeLogLineRead`/`FireLogLineRead`. 🟡 **VERIFY** the
`BeforeLogLineRead` reflectable multicast-field shape (discoverable via
`GetField("BeforeLogLineRead", NonPublic|Instance|Public|Static)` and reassignable). The three
graph/clear/echo members are inert stubs by design.

## 4. Consumer read-path (MiniParse / cactbot CombatData) — ✅ DONE

How the live DPS table reaches cactbot. Each tick `MiniParseEventSource.CreateCombatData` gates on
`oFormActMain?.ActiveZone?.ActiveEncounter` + both static `ExportVariables` dicts non-null, then
walks `GetAllies()` × `ExportVariables` calling `GetExportString` to build the JSON. OP **mutates**
the static `CombatantData.ExportVariables` to add three keys whose formatters read `MasterSwing`.

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `ActiveZone` (`ZoneData`) → `ZoneData.ActiveEncounter` (`EncounterData`) | OP, Trig | gateway to the live encounter |
| ✅ | `EncounterData.{GetAllies(),Active,EncId,EndTime,ExportVariables}` | OP | combatants + `isActive` + change-detection + the `Encounter` dict |
| ✅ | `CombatantData.{ExportVariables,Name,Items,DamageTypeDataOutgoing{Damage,Healing}}` | OP | per-`Combatant` dict; OP injects `overHeal`/`damageShield`/`absorbHeal` |
| ✅ | `AttackType.Items` (`List<MasterSwing>`) | OP | custom formatters iterate swings |
| ✅ | `MasterSwing.{Tags["overheal"],Special,Damage,DamageType}` (read) | OP | the 3 custom export formatters |
| ✅ | `EncounterData/CombatantData.TextExportFormatter.GetExportString(...)` | OP | produce each export value string |
| ✅ | `oFormImportProgress.Visible` | OP | skip ticks during a log import |
| ✅ | `CurrentZone` | OP, Trig, Hojo | current zone name |

**Strategy.** Done — the static `ExportVariables` are real mutable dictionaries (OP's `.Add` of its
3 keys works), `MasterSwing` carries the read fields, and `oFormImportProgress` is a null-default
stub so `?.Visible` → "not importing".

## 5. Combat-state events — ✅ DONE

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `OnCombatStart` / `OnCombatEnd` (events) | Trig | Triggernometry fires its own combat events off these |
| ✅ | `CombatToggleEventArgs` | Trig | event payload type |

**Strategy.** Raised by the facade inside `SetEncounter`/`EndCombat`. Nothing to build.

## 6. Audio — TTS / PlaySound (the Discord-Triggers stack) — ❌ GAP G‑1

Real ACT exposes audio two ways that are *one mechanism*: callers invoke the **methods**
`TTS(text)`/`PlaySound(file)`, which do bookkeeping then call the swappable **delegate fields**
`PlayTtsMethod(text)` / `PlaySoundMethod(file,vol)` (`FormActMain.cs:4237/4292`). Discord-Triggers
saves the old delegates and installs `vm.SpeakText`/`vm.SpeakSoundFile` (`DiscordTriggersView.xaml.cs:178/179`),
which synthesize (ONNX/Piper/Kokoro) and ship audio to Discord. **Because the method routes through
the delegate, anything that calls `TTS()`/`PlaySound()` — cactbot, Trig, Hojoring — lands in
Discord-Triggers once it's the installed sink.** That is the "official audio stack."

| St | member | consumers | why |
|:--:|---|---|---|
| ❌ | `TTS(string)` / `PlaySound(string)` **methods** | OP, Hojo | cactbot `say`/`play_sound` (`MiniParseEventSource.cs:65/75`); Hojoring SoundController/TimelineController — **G‑1** |
| ✅ | `PlayTtsMethod` / `PlaySoundMethod` (public mutable delegate fields) | Trig, Disc, Hojo | the swappable sink; Disc/TTSYukkuri save→swap→restore |
| ✅ | `PlayTtsDelegate`(`void(string)`) / `PlaySoundDelegate`(`void(string,int)`) (types) | Disc | field types / handler signatures |

**Strategy (G‑1, BREAK).** Add the two methods routing to the delegate fields:

```csharp
public void TTS(string SpeechText)        => PlayTtsMethod?.Invoke(SpeechText);
public void PlaySound(string WavFilePath) => PlaySoundMethod?.Invoke(WavFilePath, 100);
```

Minimal is correct — we skip ACT's speech-correction list, volume sliders, and TTS-to-WAV caching
(ACT-options state we don't reproduce). Keep the delegate fields' **no-op defaults** (already
present) so audio degrades to silence without Discord-Triggers rather than throwing. This one fix
closes G‑1 **and** Hojoring's audio (M4‑1).

## 7. Spell-timer subsystem — `oFormSpellTimers` / `FormSpellTimers` — ❌ G‑2 / ⬜ M4‑2

ACT's *built-in* spell-timer engine (a second WinForms form). Two very different consumers, so two
tiers of implementation.

| St | member | consumers | why |
|:--:|---|---|---|
| ❌ | `ActGlobals.oFormSpellTimers.{OnSpellTimerNotify,OnSpellTimerRemoved}` (events) | OP | `SpellTimerOverlay.cs:25/50` mirrors ACT's native timers into a web overlay (only if that overlay is added) — **G‑2** |
| ⬜ | `FormSpellTimers.{RebuildSpellTreeView(),TimerDefs,RemoveTimerDef,AddEditTimerDef(TimerData),NotifySpell}` + model family `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame` | Hojo | SpecialSpellTimer rebuilds the tree, edits `TimerDefs`, pushes timers into ACT's native UI — **M4‑2** |

**Strategy.**
- ❌ **G‑2 (BREAK):** add `ActGlobals.oFormSpellTimers` as a **non-null** stub `FormSpellTimers`
  exposing the two events (never fired), so OP's `+=` binds inertly — the optional SpellTimer
  overlay shows nothing instead of NRE-ing. No timer engine (we don't reproduce ACT's feature).
- ⬜ **M4‑2:** a *working* `FormSpellTimers` (real `TimerDefs` + add/remove/rebuild/notify) plus the
  model family — all absent today. The single largest M4 item; not yet built.

## 8. Custom-trigger import — ✅ DONE

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `CustomTriggers` / `ActiveCustomTriggers` (`SortedList<string,CustomTrigger>`) + `CustomTrigger.{Category,RestrictToCategoryZone,Active,ShortRegexString,SoundData,SoundType,TimerName,Tabbed,Timer}` | Trig | one-way *import* of ACT-native custom triggers (reads the 9 fields) |

**Strategy.** Empty `SortedList`s + inert `CustomTrigger` model; `Count == 0` → nothing to import,
Trig's own triggers still run. We never author ACT-native triggers, so empty is the permanent state.

## 9. Encounter text export & log — ✅ DONE (minimal)

Triggernometry's export/last-encounter actions render an encounter to text and append synthetic log
lines. It does not need a real formatter engine.

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `GetTextExport(EncounterData,TextExportFormatOptions)` + private `defaultTextFormat` field + `TextExportFormatOptions` | Trig | render encounter to text (reflects `defaultTextFormat` by name) |
| ✅ | `ZoneList` (`List<ZoneData>`, `[i].Items`) | Trig | walk last-encounter history |
| ✅ | `LogLineEntry` + `EncounterData.{LogLines,Duration,Items}` | Trig | append a synthetic line; duration/items for export |

**Strategy.** `GetTextExport` returns `"(duration) title"` (enough without a formatter engine);
`defaultTextFormat` non-null so the reflection binds; `ZoneList` holds the live zone; `LogLineEntry`
ctor present.

## 10. Host-window chrome — ❌ G‑3 / 🟡 / ⬜ M4

Cosmetic host-UI integration. We don't reproduce ACT chrome; build only what an *unguarded* caller
needs.

| St | member | consumers | why |
|:--:|---|---|---|
| ❌ | `DpiScale` (float) | OP | config-tab DPI scaling (read in try/catch → default 1) — **G‑3** |
| 🟡 | `NotificationAdd(string,string)` | OP | version-too-old toast — STUB (logs only); 2-arg matches consumer |
| 🟡⬜ | `CornerControlAdd` / `CornerControlRemove(Control)` | Trig, Hojo | corner button — Trig reflects null-guarded (OMITTED); Hojo calls directly → **M4‑3** (must be real) |
| 🟡 | private `tc1` (TabControl) | Trig | `LocateTab` selects Trig's tab — OMITTED (reflected, guarded) |
| ⬜ | `WindowState` (settable) / `CanFocus` / `IsHandleCreated` / `IsDisposed` (`Form`) | Hojo | UI marshal + lifecycle guards — **M4‑4** |

**Strategy.** ❌ **G‑3:** add `DpiScale` → `1f` (removes OP's silent catch). 🟡 `NotificationAdd`
stub + Trig-reflected `CornerControlAdd/Remove`/`tc1` stay omitted (guarded). ⬜ Hojoring: real
`CornerControlAdd/Remove` (no-op tracking the control, **M4‑3**); settable no-op `WindowState` +
sane `CanFocus`/`IsHandleCreated`/`IsDisposed` (**M4‑4**; inherited from `Form` — verify the
headless form reports `IsHandleCreated==true` once shown, `IsDisposed==false`).

## 11. Self-update plumbing — ✅ STUB by design

Every plugin has an in-app updater (check remote version → download zip → unzip → restart, surfaced
via `TraySlider`). All out of scope (principle 4).

| St | member | consumers | why |
|:--:|---|---|---|
| 🟡 | `PluginGetRemoteVersion(int)` / `PluginDownload(int)` / `PluginDownloadMem(int)` / `UnZip(...)` / `GetAutomaticUpdatesAllowed()` / `UpdateCheckClicked` (event) / `RestartACT(bool,string)` | FFXIV, OP, Trig, Disc, Hojo | self-update flow — inert stubs (G‑5) |
| 🟡 | `TraySlider` / `ButtonLayoutEnum` / `PluginDownloadData` | FFXIV, Trig | update toast — **G‑4** (add inert stubs only if a bind throws) |

**Strategy.** `PluginGetRemoteVersion`→`""`, `PluginDownload`→null, `GetAutomaticUpdatesAllowed`→false,
`RestartACT`/`UnZip` no-op, `UpdateCheckClicked` never fired. Most `TraySlider` uses are reflective /
try-catch tolerant, so add those stubs only if a bind actually throws.

## 12. Error / diagnostic logging — ✅ DONE

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `WriteExceptionLog(Exception,string)` / `WriteDebugLog(string)` / `WriteInfoLog(string)` | all | universal fallback error sink in every plugin's catch paths |

**Strategy.** Route to our `ILogger`. Must exist (called unguarded inside catch); the destination is ours.

## 13. Cross-cutting shape completeness — ✅ DONE / lower-priority

| St | member | consumers | why |
|:--:|---|---|---|
| ✅ | `FormActMain` events `ActLifecycleChanged`/`LogFileRenamed`/`XmlSnippetAdded`/`BeforeClipboardSet`/`UrlRequest` | any (`+=`) | bindable no-op publishers (never fired in the headless host) |
| ✅ | `LogLineEventArgs.companionLogName` + 6-arg ctor | any | shape completeness |
| 🟡 | `ActPluginData.btnXButton` + `IEquatable`; the ~25 other `oFormXxx` singletons (we have 2); ~100 other `FormActMain` methods; richer model members (`CombatantData.AllOut/GetCombatantType`, `EncounterData.NumEnemies`, `GetMaxHit`/`GetMaxHeal` `""`-stubs, `HistoryRecord` as `object`, `ZoneData` ctor/`AddCombatAction`/`CompareTo`, `Dnum.operator+`, `SwingTypeEnum`/`AttackTypeTypeEnum` values); `mainTableShowCommas` default | none reached | full ACT public surface — **G‑6**, no supported consumer |

**Strategy.** Build only when a real consumer appears (principle 1). Confirm `mainTableShowCommas`
default (`false` here vs `true` in real ACT) is intended.

---

# Non-ACT integration surfaces

ACT is one seam. These carry the data and features ACT structurally cannot.

## 14. FFXIV_ACT_Plugin SDK seam — the live data source — ✅ DONE

ACT is game-agnostic: it has the aggregated DPS rollup and nothing else. **Live combatant tables,
raw + decoded packets, zone/process/player identity, and the skill/zone/world/buff name dictionaries
do not exist on the ACT side** — they live on the FFXIV plugin. So every overlay/trigger pack pulls
the FFXIV `pluginObj` from `ActPlugins` (§1) and reflects three members:

| St | member | shape | consumers | why |
|:--:|---|---|---|---|
| ✅ | `DataRepository` (property) | `IDataRepository` (pull) | OP (~30 sites), Trig, Hojo | `GetCombatantList`, `GetCurrentPlayerID`, `GetPlayer`, `GetResourceDictionary`, `GetSelectedLanguageID`, `GetCurrentTerritoryID`, `GetCurrentFFXIVProcess`, `GetGameVersion`, `GetServerTimestamp` — party panels, target info, name→id, zone/process gating |
| ✅ | `DataSubscription` (property) | `IDataSubscription` (11 events) | OP, Trig, Hojo | `NetworkReceived`/`NetworkSent` (raw packets — OP's ~20 decoders + `RegisterNetworkParser`), `LogLine`/`ParsedLogLine` (Trig feed), `ZoneChanged`, `ProcessChanged` (no-game liveness), `PartyListChanged`, `PrimaryPlayerChanged` |
| ✅ | `_iocContainer` (private field, MinIoC) | `GetService`/`Resolve<T>` | OP, Hojo | resolves `ILogOutput` for OP's **custom-log round-trip** (synthetic IDs ≥ 256 — MapEffect/FateDirector/CEDirector/InCombat); Hojo also resolves `ILogFormat` |

**Strategy — DONE.** `WrappedFfxivPlugin` (placed in `pluginObj`) forwards the real plugin's
`DataRepository` unchanged (every `Get*` lands on the genuine repository), substitutes
`RingBufferDataSubscription` (all 11 events with exact delegate types; one ring + single dispatch
thread replacing the per-subscriber `BeginInvoke` fan-out — ~250× faster), and preserves the
`_iocContainer` field name + slot so `GetService`/`Resolve<T>` hit the real container.
`IRawPacketSource.InjectNetworkReceived` adds a bridge/replay path; legacy `RegisterNetworkParser`
consumers still see `NetworkReceived` normally. **No gaps** against the observed consumer set.
(Adjacent: OP also reflects the **Machina** assembly directly for region/opcodes — same discovery
path, not part of this seam.)

## 15. OverlayPlugin's web + extension surface — ✅ self-hosted (non-goal for us)

cactbot, ngld addons, and browser overlays talk to **OverlayPlugin**, not ACT — it supplies a
browser (CEF), a typed FFXIV push stream, and an RPC channel. Two transports drive one
`EventDispatcher` subscribe/handler protocol: the in-CEF JS bridge (`window.OverlayPluginApi.callHandler`
+ `window.__OverlayCallback`) and a **Fleck WebSocket** server (`/ws` + legacy `/MiniParse`); HTML is
loaded by CEF from `file://`/remote URLs — **no Kestrel/HTTP file server**. Addons register via
`IOverlayAddonV2.Init()` → `Registry.StartEventSource`/`RegisterOverlay` (cactbot's
`CactbotEventSource` is one such `EventSourceBase`).

**Strategy — nothing to build.** OverlayPlugin runs **unmodified**, bringing its own CEF + Fleck +
dispatcher + event sources and rendering overlays as its own transparent click-through windows. Our
only obligation is §1 (load it) + §14 (FFXIV seam). **We do not build a native overlay layer** — no
`Fct.Overlays` / WebView2 / host-side WebSocket, now or later. This section exists only to explain
how cactbot reaches its data; reproducing it is an explicit non-goal.

## 16. Discord-Triggers audio bridge — ✅ the official audio stack

Discord-Triggers' product is the §6 delegate hijack plus an out-of-process chain:
`vm.SpeakText`/`SpeakSoundFile` → `SpeakTextCoreAsync` → a named-pipe **bridge** to a Node/ONNX
backend (Piper/Kokoro voices) → a Discord voice channel (or local device). Our facade's only
contribution is the §6 indirection plus a `Form` identity to host its WPF config tab + folder
pickers. Everything else is internal to Discord-Triggers — which is why making it the official audio
stack costs essentially nothing beyond G‑1.

## 17. Our own seams (for completeness)

- **The net48↔net10 IPC bridge** (`Fct.Bridge`): not an ACT surface — how the satellite (legacy
  plugins + facade) and the net10 host exchange typed domain events. Plugins never see it.
- **`IRawPacketSource`** (`Fct.Abstractions`): the one opt-in escape hatch exposing raw packets to
  net10 consumers / legacy `RegisterNetworkParser`, fed by `RingBufferDataSubscription`.
