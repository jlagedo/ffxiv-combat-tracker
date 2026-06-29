# ACT Interface Map & Implementation Guide

The **single source** for the host↔plugin contract our facade (`src/Fct.Compat.Act`) must
impersonate: every member each supported legacy plugin touches on the **ACT host**
(`Advanced_Combat_Tracker`, the generic `Advanced Combat Tracker.exe` — *not* FFXIV_ACT_Plugin),
*why* it's used, *what we must build* given our philosophy, the gaps, and the **non-ACT integration
surfaces** (FFXIV SDK seam, OverlayPlugin web layer, Discord audio bridge).

Companion docs:
- [`DATA-FLOW.md`](DATA-FLOW.md) — the data-flow narrative through the upstream stack.
- [`COMPAT-GAPS.md`](COMPAT-GAPS.md) — the phased implementation backlog (M0–M4 surface phases + the
  independent Parser-calculation **P** axis). The gap IDs here (G‑1…, M4) map onto its phases.

---

## Guiding principles (these decide how much of each interface we build)

1. **Impersonate ACT only enough to make the plugins bind and run.** We are *not* reproducing ACT's
   UI, options forms, encounter-history database, HTML/web export, plugin-manager, or its built-in
   spell-timer/timeline editors. For each member the question is "what does the plugin *do* with the
   return value," and we implement the smallest thing that keeps that behavior correct.
2. **Audio is owned by ACT-Discord-Triggers — the official audio stack.** ACT's real `TTS(string)` /
   `PlaySound(string)` route through the swappable `PlayTtsMethod` / `PlaySoundMethod` delegates
   (verified in the decompile, `FormActMain.cs:4237/4292`). We reproduce exactly that indirection;
   Discord-Triggers installs the real sink by swapping those delegates, and everything else (cactbot
   `say`, Triggernometry TTS, Hojoring) flows into it for free. See §8F.
3. **Live game state flows through the FFXIV_ACT_Plugin SDK seam, not ACT.** ACT only holds the
   *aggregated* DPS rollup. Combatant tables, raw/decoded packets, zone/process/player identity and
   name dictionaries live behind `IDataRepository`/`IDataSubscription`/`_iocContainer` on the FFXIV
   plugin object — served by our `WrappedFfxivPlugin`. See §9.
4. **Self-update is out of scope.** We do not download or restart legacy plugins; those members are
   inert stubs that satisfy binding.
5. **Reflection-discovered / failsafe-registered members may simply be absent or no-op** where the
   consumer guards them (`GetMethod(...) != null`, try/catch, `FailsafeRegisterHook`). We build them
   only when a consumer calls them *unguarded*.

Status legend used throughout: **DONE** implemented + functional · **GAP** missing, must build ·
**STUB** present but inert (intended) · **VERIFY** present, untested on the live path.

## Method

Each plugin source tree was swept for references into the `Advanced_Combat_Tracker` namespace
(`ActGlobals`, `FormActMain`, `IActPluginV1`, `ActPluginData`, the `Models/*` types, and the events
on `FormActMain`). Symbols that merely *look* like ACT members but are the plugin's own types
(Hojoring's `CombatantData`/`AttackTypes`, Triggernometry's `CombatantData`/`CustomTriggerProxy`,
OverlayPlugin's `MessageType`) were excluded. ACT signatures verified against the decompile at
`E:\dev\ACT-decompiled\Advanced_Combat_Tracker\`.

### Scope note

All five plugins are in scope. FFXIV_ACT_Plugin, OverlayPlugin, Triggernometry and Discord-Triggers
are the original build target; **ACT.Hojoring** is the heaviest and most unusual, so its extra
surface (chiefly the full spell-timer subsystem) is the larger, not-yet-built batch tracked as
**M4**. It is the only consumer that drives ACT's built-in spell-timer subsystem in full.

---

# Part 1 — The map (what each plugin consumes, and why)

## 1. FFXIV_ACT_Plugin — the producer

`E:\dev\FFXIV_ACT_Plugin`. The only plugin that **writes** into ACT's aggregation engine. All
engine calls funnel through `FFXIV_ACT_Plugin.Common.ACTWrapper`; `ACT_UIMods` reconfigures ACT's
static table/export model; the root type implements `IActPluginV1`.

| ACT member | dir | purpose |
|---|---|---|
| `IActPluginV1.{InitPlugin,DeInitPlugin}` | implement | plugin load contract |
| `ActGlobals.oFormActMain` | read | root host handle; every call below dereferences it |
| `ActGlobals.charName` | read | local player display name for swing source/target naming |
| `FormActMain.AddCombatAction(MasterSwing)` | call | **core write path** — pushes every parsed swing into aggregation |
| `FormActMain.SetEncounter(DateTime,attacker,victim)→bool` | call | opens/continues the encounter; gates each `AddCombatAction` |
| `FormActMain.ChangeZone(string)` | call | zone transitions |
| `FormActMain.EndCombat(bool)` | call | ends encounter (guarded by `InCombat`) |
| `FormActMain.InCombat` | read | gates heal/DoT/HoT/shield/status/death/resource emit |
| `FormActMain.OpenLog(bool,bool)` | call | points ACT at the plugin's `Network_*.log` and (re)opens it |
| `FormActMain.{LogFilePath,LogFileFilter}` | r/w | redirect ACT's active log to the network log |
| `FormActMain.GlobalTimeSorter` | read | stable per-timestamp swing ordering |
| `FormActMain.{TimeStampLen,LogPathHasCharName,ZoneChangeRegex}` | write | configure ACT's line parsing |
| `FormActMain.GetDateTimeFromLog` (`DateTimeLogParser`) | override | plugin owns timestamp parsing; saved + restored |
| `FormActMain.BeforeLogLineRead` (event) | subscribe | rewrites `logLine`/`detectedType` through ParseMediator (inbound hook) |
| `FormActMain.LogFileChanged` (event) | subscribe | import-vs-live handling |
| `FormActMain.LastKnownTime` | read | rolling-window `LastNDPS` export calc |
| `FormActMain.ActCommands(string)` | call | `/echo`-style passthrough |
| `FormActMain.{WriteExceptionLog,WriteDebugLog,RestartACT}` | call | error/debug logging, version-mismatch abort |
| `FormActMain.ReadThreadLock`, `.Visible` | read | gate log-writer thread startup |
| `FormActMain.{ValidateLists,ValidateTableSetup}` | call | commit after mutating columns/exports |
| `FormActMain.GenerateAttackTypeGraph` (`AttackTypeGraphGenerator`) | override | plugin's graph generator; restored on unload |
| `FormActMain.FindControl<Button>("…btnClear")` | call | hook ACT's Clear button → `OnClear` |
| `FormActMain.{AppDataFolder,PluginGetSelfData}` | read/call | install dir + self-data |
| `FormActMain.{PluginGetRemoteVersion,PluginDownload,PluginDownloadMem,UnZip,GetAutomaticUpdatesAllowed}` + `UpdateCheckClicked` event | call/subscribe | self-update (plugin id 73) |
| `MasterSwing` (10-arg ctor, `.Tags/.Damage/.Time/.Special/.DamageType/.AttackType/.Critical`) + `MasterSwing.ColumnDefs` (static) | construct/r/w | every swing; registers FFXIV columns |
| `Dnum` (`.NoDamage/.Miss/.Death`, ctor, implicit `long`) | construct | damage sentinels + wrap |
| `CombatantData` (static `ColumnDefs`/`ExportVariables`/`OutgoingDamageTypeDataObjects`; consts `DamageTypeDataOutgoing*`) | r/w | registers Job/DirectHit/CritDirectHit/OverHeal/potency/Last-N-DPS columns + exports |
| `EncounterData` (static `ExportVariables`; `.ZoneName/.Duration/.LastNDPS`) | r/w | registers `CurrentZoneName`, `Last{10,30,60}DPS` — **the keys OverlayPlugin reads** |
| `AttackType` (static `ColumnDefs`; `.Items/.Special/.Critical/.Tags`) | r/w | Parry/Block/OverHeal/DirectHit columns |
| `DamageTypeData`, `DamageTypeDef` (ctor `(string,int,Color)`), `ColumnDef`, `TextExportFormatter` | construct | damage-type groupings + column/export wrappers |
| `ActLocalization.LocalizationStrings["attackTypeTerm-all"].DisplayedText` | read | localized "All" bucket key |
| `TraySlider`, `ButtonLayoutEnum`, `PluginDownloadData` | construct | update-notification UI |

**Pattern.** A single wrapper seam (`ACTWrapper`) fronts the engine; parsing emits typed entries →
`MasterSwing` → `AddCombatAction` (gated by `SetEncounter`/`InCombat`). `ACT_UIMods` rewrites ACT's
*static* model (columns + `ExportVariables`) so the aggregated numbers expose FFXIV-specific keys.
It is a **pure producer** — it never reads `ActiveZone`/`GetCombatants` back out.

## 2. OverlayPlugin — the consumer (MiniParse / cactbot)

`E:\dev\OverlayPlugin`. Implements `IActPluginV1`; **reads** aggregated encounter data each tick and
serves it to web overlays. Discovers FFXIV_ACT_Plugin *through* ACT's plugin list.

| ACT member | dir | purpose |
|---|---|---|
| `IActPluginV1.{InitPlugin,DeInitPlugin}` | implement | load contract |
| `ActGlobals.oFormActMain` | read | root handle; null-checked readiness gate |
| `FormActMain.GetVersion()` | call | gate: requires ACT ≥ 3.8.0.281 |
| `FormActMain.NotificationAdd(string,string)` | call | version-too-old error |
| `FormActMain.{Invoke,InvokeRequired}` | call | marshal to ACT UI thread |
| `FormActMain.{IsActClosing,InitActDone,Handle}` | read | init / teardown phase gates |
| `FormActMain.AppDataFolder` | read | CEF / ngrok / error-report / tech-support paths |
| `FormActMain.DpiScale` | read | config-tab DPI scaling (try/catch → default 1) |
| `FormActMain.ActPlugins` (`List<ActPluginData>`) | enumerate | **discovery** — find self, FFXIV plugin, Triggernometry, addons |
| `FormActMain.PluginGetRemoteVersion(int)` | call | self-update version check |
| **`FormActMain.TTS(string)`** | call | cactbot `say` (`MiniParseEventSource.cs:65`, `AddonExample:34`) |
| **`FormActMain.PlaySound(string)`** | call | cactbot `play_sound` (`MiniParseEventSource.cs:75`) |
| `FormActMain.EndCombat(bool)` | call | force-end encounter from JS/WS/event sources |
| `FormActMain.InCombat` | read | compare ACT combat state vs memory |
| `FormActMain.CurrentZone` | read | synthetic zone log line |
| `FormActMain.ActiveZone` (`ZoneData`) | read | **gateway to encounter data** |
| `FormActMain.BeforeLogLineRead` (event) | subscribe + **reflect** | primary log tap; `LogParseOverlay` reflects the backing field, unhooks all, inserts itself first |
| `ZoneData.ActiveEncounter` (`EncounterData`) | read | the live encounter — MiniParse read root |
| `EncounterData.GetAllies()→List<CombatantData>` | call | combatants to export |
| `EncounterData.{Active,EncId,EndTime}` | read | `isActive` flag + change-detection cache keys |
| `EncounterData.ExportVariables` (static) + `TextExportFormatter.GetExportString(enc,allies,"")` | enumerate/call | build the `Encounter` JSON dict |
| `CombatantData.ExportVariables` (static) + `TextExportFormatter.GetExportString(combatant,"")` | enumerate/**write**/call | build each `Combatant` dict; **OP adds 3 keys** (`overHeal`,`damageShield`,`absorbHeal`) |
| `CombatantData.{Name,Items,DamageTypeDataOutgoing{Damage,Healing}}` | read | JSON key + `Last*DPS` "All"-bucket guard + custom formatters |
| `AttackType.Items` (`List<MasterSwing>`) | read | custom formatters iterate swings |
| `MasterSwing.{Tags["overheal"],Special,Damage,DamageType}` | read | the 3 custom export formatters |
| `oFormImportProgress.Visible` | read | detect log-import in progress (`MiniParseEventSource:189`) |
| **`ActGlobals.oFormSpellTimers.{OnSpellTimerNotify,OnSpellTimerRemoved}`** | subscribe | `SpellTimerOverlay.cs:25/50` — spell-timer overlay feed |
| `ActPluginData.{pluginObj,cbEnabled,lblPluginTitle,pluginFile,tpPluginSpace}` | read | FFXIVRepository discovery + reflection seam |
| `LogLineEventArgs.{logLine,originalLogLine,detectedType,detectedTime}` | read | log-line fields |

**Pattern.** `CheckIsActReady` gates on `oFormActMain?.ActiveZone?.ActiveEncounter` +
`EncounterData.ExportVariables` + `CombatantData.ExportVariables` non-null. Each tick it walks
`GetAllies()` × the two static `ExportVariables` dicts, calling `GetExportString` to produce the
MiniParse JSON. **FFXIVRepository** scans `ActPlugins` for `lblPluginTitle.Text` starting
`FFXIV_ACT_Plugin` with `cbEnabled.Checked` + non-null `pluginObj`, then reflects `DataRepository`,
`DataSubscription`, and the private `_iocContainer` off that object — the seam our `WrappedFfxivPlugin`
satisfies (see §9).

## 3. Triggernometry — the regex/timer engine

`E:\dev\Triggernometry`. All ACT contact is isolated in one shim class, `TriggernometryProxy.ProxyPlugin`
(`Source/TriggernometryProxy/ProxyPlugin.cs`), which translates ACT into plain delegate "Hooks" and
forwards to the ACT-free core (`Triggernometry.RealPlugin`). Missing hooks are swallowed
(`FailsafeRegisterHook`), so absent members degrade gracefully.

| ACT member | dir | purpose |
|---|---|---|
| `IActPluginV1.{InitPlugin,DeInitPlugin}` | implement | shim entry point |
| `ActGlobals.oFormActMain` | read | cached into `RealPlugin.mainform` |
| `FormActMain.{BeforeLogLineRead,OnLogLineRead}` (events) | subscribe | **central trigger feed** |
| `FormActMain.{OnCombatStart,OnCombatEnd}` (events) | subscribe | fire Trig's combat events |
| `FormActMain.{InCombat,CurrentZone,InitActDone}` | read | state hooks |
| `FormActMain.{SetEncounter,EndCombat}` | call | manual combat-state control |
| `FormActMain.{AppDataFolder,ActPlugins,PluginGetSelfData}` | read/call | config path + self/FFXIV-plugin discovery |
| `FormActMain.{ActiveZone,ZoneList}` → `EncounterData.{Duration,LogLines,Items}` | read/mutate | active/last-encounter export + encounter-log append |
| `FormActMain.GetTextExport(EncounterData,TextExportFormat)` + private `defaultTextFormat` field | call/**reflect** | render encounter to text |
| `FormActMain.GlobalTimeSorter` + `LogLineEntry` ctor | read/construct | append a log line to `ActiveEncounter.LogLines` |
| `FormActMain.{PlayTtsMethod,PlaySoundMethod}` (delegate props) | call | route TTS/sound through ACT |
| `FormActMain.CustomTriggers` (Dictionary) + `CustomTrigger.{Category,RestrictToCategoryZone,Active,ShortRegexString,SoundData,SoundType,TimerName,Tabbed,Timer}` | read | one-way import of ACT-native custom triggers |
| `FormActMain.{InvokeRequired,Invoke}` | call | UI-thread marshalling |
| `FormActMain.CornerControlAdd/Remove` | **reflect-invoke** (GetMethod, may be absent) | corner notification popups |
| `FormActMain` private field `tc1` (TabControl) | **reflect** | `LocateTab` selects Trig's tab |
| `FormActMain.{PluginGetRemoteVersion,PluginDownload,UnZip,RestartACT,WriteExceptionLog}` + `TraySlider` | call/construct | self-update (plugin id 87) + update toast |
| `ActPluginData.{pluginObj,pluginFile,lblPluginStatus,lblPluginTitle,cbEnabled,tpPluginSpace}` | read | identity / FFXIV-plugin status + checkbox scan |
| `LogLineEventArgs.{originalLogLine,logLine,detectedZone}`, `CombatToggleEventArgs` | read | event payloads |

**Pattern.** Triggers run off the `BeforeLogLineRead`/`OnLogLineRead` stream — Trig ships its own
engine and needs ACT only as a *log feed + TTS/sound output + config/discovery host*. The facade's
job is to let the shim bind and degrade, not to evaluate triggers. Per-combatant data comes from the
FFXIV plugin's `DataRepository` (via `BridgeFFXIV.cs`), **not** from ACT.

## 4. ACT-Discord-Triggers — the audio bridge (official audio stack)

`E:\dev\ACT-Discord-Triggers`. Smallest, purely host-services surface. Consumes **zero** combat /
encounter / log-line data. Its product is hijacking ACT's global TTS/sound delegates to reroute audio
to a Discord voice channel via an out-of-process Node/ONNX bridge. **In our host this is the official
audio stack** (see §8F).

| ACT member | dir | purpose |
|---|---|---|
| `IActPluginV1.{InitPlugin,DeInitPlugin}` | implement | bootstrap (then byte-loads its real closure) |
| `ActGlobals.oFormActMain` | read | root handle |
| `FormActMain.AppDataFolder` | read | install/bridge/config paths |
| `FormActMain.PluginGetSelfData(this)→ActPluginData` | call | self-locate own install dir |
| `ActPluginData.pluginFile` (`.DirectoryName`/`.FullName`) | read | install dir + config name |
| **`FormActMain.PlayTtsMethod`** (`PlayTtsDelegate` field) | read + **write** | save original, swap to `SpeakText`, restore on deinit |
| **`FormActMain.PlaySoundMethod`** (`PlaySoundDelegate` field) | read + **write** | save original, swap to `SpeakSoundFile`, restore |
| `FormActMain.{PlayTtsDelegate,PlaySoundDelegate}` (delegate types) | reference | field types / handler signatures |
| `FormActMain.WriteExceptionLog(Exception,string)` | call | fallback error sink in every teardown path |
| `FormActMain.RestartACT(bool,string)` | **reflect-call** | post-update restart (degrades if absent) |
| `FormActMain` as `Form`: `Handle`, `InvokeRequired`, `Invoke`, `ShowDialog(IWin32Window)` | call/read | WPF `ElementHost` tab + dialog parenting + UI marshal |

**Pattern.** Delegate hijack is the whole product. The facade must expose `PlayTtsMethod`/
`PlaySoundMethod` as **public mutable fields** of the exact delegate types, and ACT's own playback
paths (`TTS`/`PlaySound`) must call through whatever is currently assigned. Requires `oFormActMain`
to be a real WinForms `Form` (handle, `Invoke`, dialog owner).

## 5. ACT.Hojoring — heaviest consumer (M4 surface, not yet built)

`E:\dev\ACT.Hojoring`. Four `IActPluginV1` plugins (SpecialSpellTimer, TTSYukkuri, UltraScouter,
XIVLog). Uses ACT as a **service bus + UI host**, not a data source (combat data comes from
FFXIV_ACT_Plugin's `DataRepository` via `pluginObj` reflection).

| ACT member | dir | purpose |
|---|---|---|
| `IActPluginV1.{InitPlugin,DeInitPlugin}` ×4 | implement | four plugin entry points |
| `ActGlobals.oFormActMain` | read | central handle (~130 sites) |
| `FormActMain.InitActDone` | read | busy-wait gate before init/work |
| `FormActMain.ActPlugins` | enumerate | locate FFXIV/Overlay plugins + validate load order |
| `FormActMain.{OnLogLineRead,BeforeLogLineRead}` (events) | subscribe + **reflect** | log feed; `LogBuffer` reorders the `BeforeLogLineRead` handler list to run first |
| `FormActMain.{CurrentZone,InCombat,EndCombat(bool)}` | read/call | zone + combat state/forced wipe-end |
| `FormActMain.PlaySound(string)`, `FormActMain.TTS(string)` | call | sound/TTS output (method form) |
| `FormActMain.{PlayTtsMethod,PlaySoundMethod}` (delegate fields) | read + **write** | TTSYukkuri swaps both wholesale (save/`.Clone()`/restore) |
| `FormActMain.CornerControlAdd/Remove(Control)` | call | title-bar corner toggle button |
| `FormActMain.{WriteExceptionLog,RestartACT(reflect),WindowState(write)}` | call | host control / updater |
| `FormActMain` as `Form`: `Invoke`,`InvokeRequired`,`IsHandleCreated`,`IsDisposed`,`CanFocus`,`Visible`, `IWin32Window` owner | call/read | UI marshal + lifecycle guards + dialog parenting |
| `FormActMain.PluginGetSelfData(this)` → `ActPluginData.{pluginFile,pluginObj,lblPluginStatus}` | call/read | self-locate + FFXIV "Started" check + `pluginObj` reflection |
| **`ActGlobals.oFormSpellTimers` + `FormSpellTimers.{RebuildSpellTreeView,TimerDefs,RemoveTimerDef,AddEditTimerDef(TimerData),NotifySpell}`** | call/read | drive ACT's **built-in** spell-timer subsystem |
| `LogLineEventArgs.{logLine,detectedType}`, `LogLineEventDelegate` | read/reflect | log payloads + handler-list surgery |

**Unusual uses no other plugin needs:** the full `oFormSpellTimers`/`FormSpellTimers` API (OverlayPlugin
touches it only lightly), `BeforeLogLineRead` handler-list reflection surgery,
`WindowState`/`CanFocus`/`IsHandleCreated`/`IsDisposed`, direct `CornerControlAdd/Remove`.

## 6. Consolidated surface matrix

Which plugin touches which ACT member (● = uses, blank = does not). `FFXIV` = FFXIV_ACT_Plugin,
`OP` = OverlayPlugin, `Trig` = Triggernometry, `Disc` = Discord-Triggers, `Hojo` = Hojoring (M4).

| ACT member | FFXIV | OP | Trig | Disc | Hojo |
|---|:--:|:--:|:--:|:--:|:--:|
| `IActPluginV1` | ● | ● | ● | ● | ● |
| `ActGlobals.oFormActMain` | ● | ● | ● | ● | ● |
| `ActGlobals.charName` | ● | | | | |
| `AddCombatAction` / `SetEncounter` / `ChangeZone` | ● | | ●¹ | | |
| `EndCombat` / `InCombat` | ● | ● | ● | | ● |
| `OnLogLineRead` / `BeforeLogLineRead` | ● | ● | ● | | ● |
| `OnCombatStart` / `OnCombatEnd` | | | ● | | |
| `LogFileChanged` / `GetDateTimeFromLog` / `OpenLog` / `LogFile*` | ● | | | | |
| `ActiveZone` → `ActiveEncounter` | | ● | ● | | |
| `EncounterData.ExportVariables` / `GetAllies` | ●² | ● | | | |
| `CombatantData.ExportVariables` / `Items` | ●² | ● | | | |
| `MasterSwing` / `Dnum` / `AttackType` / `DamageTypeData` | ● | ●³ | | | |
| `CustomTriggers` + `CustomTrigger` | | | ● | | |
| `GetTextExport` + `defaultTextFormat` | | | ● | | |
| `TTS(string)` / `PlaySound(string)` (methods) | | ● | | | ● |
| `PlayTtsMethod` / `PlaySoundMethod` (delegate fields) | | | ● | ● | ● |
| `oFormSpellTimers` / `FormSpellTimers` | | ●⁴ | | | ● |
| `CornerControlAdd/Remove` | | | ●⁵ | | ● |
| `ActPlugins` enumerate | ● | ● | ● | | ● |
| `PluginGetSelfData` / `ActPluginData.*` | ● | ● | ● | ● | ● |
| `AppDataFolder` | ● | ● | ● | ● | ● |
| `Invoke` / `InvokeRequired` / `Handle` (`Form`) | ● | ● | ● | ● | ● |
| `WriteExceptionLog` | ● | | ● | ● | ● |
| `RestartACT` | ● | | ● | ●⁵ | ●⁵ |
| `GetVersion` / `NotificationAdd` / `DpiScale` | | ● | | | |
| self-update (`PluginGetRemoteVersion`/`PluginDownload`/`UnZip`/`TraySlider`) | ● | ● | ● | ● | |
| `WindowState` / `CanFocus` / `IsHandleCreated` / `IsDisposed` | | | | | ● |

¹ Trig uses `SetEncounter`/`EndCombat` for manual combat-state control, not swing production.
² registers the keys; does not read them back. ³ reads `MasterSwing` via custom export formatters
only. ⁴ optional `SpellTimerOverlay` only. ⁵ reflective / guarded — degrades if absent.

---

# Part 2 — Implementation (why each group is used, and what to build)

Baseline: `src/Fct.Compat.Act`. Everything **FFXIV_ACT_Plugin** writes is present and verified
bit-for-bit (Slice 1 S5); the open work is on the **consumer** side. Each group below states why the
plugins use it and the smallest correct implementation under the principles above.

## 8A. Plugin lifecycle, identity & discovery — **DONE**

`IActPluginV1.{InitPlugin,DeInitPlugin}`, `oFormActMain`, `InitActDone`, `IsActClosing`, `Handle`,
`Invoke`/`InvokeRequired`, `GetVersion()`, `AppDataFolder`, `ActPlugins`, `ActPluginData.*`,
`PluginGetSelfData`.

Every plugin enters via `InitPlugin(TabPage, Label)` and dereferences `oFormActMain`. `InitActDone`/
`Handle` are the "ACT booted" gate (OP/Hojoring busy-wait on them). `ActPlugins` is the **service
registry**: consumers scan it to find themselves and the FFXIV plugin (by `lblPluginTitle.Text`
starting `FFXIV_ACT_Plugin` + `cbEnabled.Checked` + non-null `pluginObj`). `Invoke`/`Handle` come
free from `FormActMain : Form`. **What to do:** nothing — all present (Slice 1). The load-bearing
requirement is that the host populates `ActPlugins` with the FFXIV entry whose `pluginObj` is our
`WrappedFfxivPlugin` and whose `lblPluginStatus.Text` is the exact "Started" string (one **VERIFY**,
shared by Triggernometry init and Hojoring). `GetVersion()` returns `3.8.5.288` (OP gates ≥ 3.8.0.281).

## 8B. The producer write-path — **DONE**

`charName`, `AddCombatAction`, `SetEncounter`, `ChangeZone`, `EndCombat`, `InCombat`,
`GlobalTimeSorter`, `LastKnownTime`, `MasterSwing`+`Dnum`, the static model registration on
`CombatantData`/`EncounterData`/`AttackType`/`MasterSwing` (`ColumnDefs`/`ExportVariables`/
`OutgoingDamageTypeDataObjects`/`DamageTypeDef`), `ValidateLists`/`ValidateTableSetup`,
`ActLocalization`.

The only writer into ACT. FFXIV_ACT_Plugin converts each parsed entry to a `MasterSwing` and calls
`AddCombatAction` (gated by `SetEncounter`/`InCombat`); `ACT_UIMods` rewrites ACT's static table model
so the rollup exposes the FFXIV columns + the `ExportVariables` keys OverlayPlugin later reads.
**What to do:** nothing — verified bit-for-bit on captured combat (`Aggregation.cs` + `CombatTables.cs`;
TESTING.md "Differential ACT-engine compat"). (The clean-room *value calculation* of simulated
DoT/HoT/shield amounts is a separate axis — Phase **P** in COMPAT-GAPS — not an interface concern.)

## 8C. The inbound log seam — **DONE** (1 VERIFY)

`OpenLog`, `LogFilePath`/`Filter`, `TimeStampLen`, `LogPathHasCharName`, `ZoneChangeRegex`,
`GetDateTimeFromLog`, `BeforeLogLineRead`, `OnLogLineRead`, `LogFileChanged`, `LogLineEventArgs.*`,
`ReadThreadLock`, `Visible`, `GenerateAttackTypeGraph`, `FindControl`, `ActCommands`.

FFXIV_ACT_Plugin owns parsing: it points ACT at its `Network_*.log`, overrides timestamp parsing, and
rewrites each line in `BeforeLogLineRead` before ACT rebroadcasts it as `OnLogLineRead`. **`Before/
OnLogLineRead` are the most important consumer surface after CombatData** — Triggernometry, Hojoring
and OP's `LogParseOverlay` all tap it, and Hojoring/`LogParseOverlay` *reflect the event's backing
field* to reorder handlers (run first). **What to do:** present + fired via `FireBeforeLogLineRead`/
`FireLogLineRead`; **VERIFY** the multicast-field reflection shape (discoverable via
`GetField("BeforeLogLineRead", NonPublic|Instance|Public|Static)` and reassignable).
`GenerateAttackTypeGraph`/`FindControl`/`ActCommands` are STUBs (UI/diagnostic) — keep inert.

## 8D. The consumer read-path (MiniParse / cactbot CombatData) — **DONE**

`ActiveZone` → `ZoneData.ActiveEncounter`, `EncounterData.{GetAllies,Active,EncId,EndTime,
ExportVariables}`, `CombatantData.{ExportVariables,Name,Items,DamageTypeDataOutgoing*}`,
`AttackType.Items`, `MasterSwing.{Tags["overheal"],Special,Damage,DamageType}`,
`TextExportFormatter.GetExportString`, `oFormImportProgress.Visible`, `CurrentZone`, `InCombat`.

How the live DPS table reaches cactbot: each tick `MiniParseEventSource.CreateCombatData` gates on
`oFormActMain?.ActiveZone?.ActiveEncounter` + both static `ExportVariables` dicts non-null, then walks
`GetAllies()` × `ExportVariables` calling `GetExportString` to build the JSON. OP **mutates** the
static `CombatantData.ExportVariables` to add `overHeal`/`damageShield`/`absorbHeal`, whose formatters
read the `MasterSwing` fields. **What to do:** nothing — the static dicts are real mutable
dictionaries (OP's `.Add` works), `MasterSwing` carries the fields, and `oFormImportProgress` is a
null-default stub so `?.Visible` → "not importing".

## 8E. Combat-state events — **DONE**

`OnCombatStart`, `OnCombatEnd`, `CombatToggleEventArgs`. Triggernometry (and some overlays) fire off
these; raised by the facade inside `SetEncounter`/`EndCombat`. **What to do:** nothing.

## 8F. Audio — TTS / PlaySound (the Discord-Triggers stack) — **GAP G‑1**

`TTS(string)` + `PlaySound(string)` **methods** (GAP); `PlayTtsMethod`/`PlaySoundMethod` delegate
**fields** (DONE); delegate types `PlayTtsDelegate`(`void(string)`)/`PlaySoundDelegate`(`void(string,
int)`) (DONE).

**The architecture.** Real ACT exposes audio two ways that are *one mechanism*:
- the **methods** `TTS(text)`/`PlaySound(file)` — what callers invoke (OP/cactbot `say`/`play_sound`
  at `MiniParseEventSource.cs:65/75`; Hojoring's SoundController/TimelineController);
- the **delegate fields** `PlayTtsMethod`/`PlaySoundMethod` — the swappable *sink*. The decompiled
  methods do bookkeeping (path-rooting, speech-correction regex, recursion guard) then call
  `PlayTtsMethod(text)` / `PlaySoundMethod(file, vol)` (`FormActMain.cs:4237/4292`).

ACT-Discord-Triggers is built on this: on activation it saves the old delegates and sets
`PlayTtsMethod = vm.SpeakText` / `PlaySoundMethod = vm.SpeakSoundFile`
(`DiscordTriggersView.xaml.cs:178/179`), restoring on deinit; `SpeakText`/`SpeakSoundFile` synthesize
(ONNX/Piper/Kokoro) and ship audio over the named-pipe bridge to Discord. **Because the method routes
through the delegate, anything that calls `TTS()`/`PlaySound()` — cactbot, Triggernometry, Hojoring —
automatically lands in Discord-Triggers once it is the installed sink.** That is what "Discord-Triggers
is the official audio stack" means here.

**What to do (G‑1, BREAK).** Add to the facade's `FormActMain`:

```csharp
public void TTS(string SpeechText)        => PlayTtsMethod?.Invoke(SpeechText);
public void PlaySound(string WavFilePath) => PlaySoundMethod?.Invoke(WavFilePath, 100);
```

Minimal is correct — we deliberately skip ACT's speech-correction list, per-channel volume sliders,
and TTS-to-WAV caching (all ACT-options state we don't reproduce). Keep the delegate fields' **no-op
defaults** (`PlaySoundMethod = (w,v)=>{}` / `PlayTtsMethod = t=>{}`, already present): without
Discord-Triggers, audio is silently dropped rather than throwing — the intended degrade. The fields
are already public mutable fields of the exact delegate types. This one fix closes G‑1 **and**
Hojoring's M4 audio need.

## 8G. Spell-timer subsystem — `oFormSpellTimers` / `FormSpellTimers` — **GAP G‑2 / M4**

`ActGlobals.oFormSpellTimers` + `FormSpellTimers.{OnSpellTimerNotify,OnSpellTimerRemoved (events),
RebuildSpellTreeView(), TimerDefs, RemoveTimerDef, AddEditTimerDef(TimerData), NotifySpell}`; model
family `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame`.

ACT's *built-in* spell-timer engine (a second WinForms form). Two very different consumers:
- **OverlayPlugin** (`SpellTimerOverlay.cs:25/50`) only *subscribes* the two events, and only if the
  user adds a "SpellTimer" overlay — it mirrors ACT's native timers into a web overlay.
- **Hojoring** *drives the whole API* — rebuilds the tree, enumerates/edits/removes `TimerDefs`, and
  calls `NotifySpell` to push its own timers into ACT's native UI.

**What to do — two tiers:**
- **Supported stack (G‑2, BREAK):** add `ActGlobals.oFormSpellTimers` as a **non-null** stub
  `FormSpellTimers` exposing the two events (never fired), so OP's `+=` binds inertly — the optional
  SpellTimer overlay shows nothing instead of NRE-ing. No timer engine. Correct altitude: we don't
  reproduce ACT's spell-timer feature, we just let the subscription resolve.
- **Hojoring (M4):** a *working* `FormSpellTimers` (real `TimerDefs` + add/remove/rebuild/notify) plus
  the `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame` model family — all absent
  today. The single largest M4 item; not yet built.

## 8H. Custom-trigger import — `CustomTriggers` / `CustomTrigger` — **DONE**

Triggernometry offers a one-way *import* of ACT-native custom triggers (reads each `CustomTrigger`'s
nine fields). It does not ask ACT to evaluate triggers. **What to do:** nothing — empty `SortedList`s
+ inert `CustomTrigger` model; `Count == 0` → nothing to import; Trig's own triggers still run. We
never author ACT-native triggers, so empty is the correct permanent state.

## 8I. Encounter text export & encounter log — **DONE (minimal)**

`GetTextExport(EncounterData,TextExportFormatOptions)` + private `defaultTextFormat` + the type;
`ZoneList`→`Items`; `LogLineEntry` + `EncounterData.LogLines`.

Triggernometry's export/last-encounter actions render an encounter to text (reflecting the private
`defaultTextFormat` by name), walk `ZoneList` for the previous encounter, and append `LogLineEntry`s.
**What to do:** nothing — `GetTextExport` returns `"(duration) title"` (enough without a formatter
engine), `defaultTextFormat` non-null so the reflection binds, `ZoneList` holds the live zone,
`LogLineEntry` ctor present.

## 8J. Host-window chrome — **mostly DONE / G‑3 / M4**

`CornerControlAdd`/`Remove`, private `tc1`, `WindowState`, `CanFocus`, `IsHandleCreated`,
`IsDisposed`, `NotificationAdd`, `DpiScale`.

Cosmetic host-UI integration. Triggernometry *reflects* `CornerControlAdd/Remove` (null-guarded) and
digs `tc1` for its tab. Hojoring calls `CornerControlAdd/Remove` *directly* and toggles `WindowState`.
OP reads `DpiScale` (try/catch → 1) and calls `NotificationAdd` only on version-too-old. **What to do:**
- Supported stack: `NotificationAdd` STUB; `DpiScale` → `1f` (**G‑3**, MINOR — removes OP's silent
  catch). `CornerControlAdd/Remove`/`tc1` stay OMITTED (Trig guards them).
- Hojoring (M4): real `CornerControlAdd/Remove` (no-op tracking the control); settable no-op
  `WindowState`; sane `CanFocus`/`IsHandleCreated`/`IsDisposed` (inherited from `Form` — verify the
  headless form reports `IsHandleCreated==true` once shown, `IsDisposed==false`).

## 8K. Self-update plumbing — **STUB by design**

`PluginGetRemoteVersion`, `PluginDownload`, `PluginDownloadMem`, `UnZip`, `GetAutomaticUpdatesAllowed`,
`UpdateCheckClicked`, `RestartACT`, `TraySlider`/`ButtonLayoutEnum`/`PluginDownloadData`.

Every plugin has an in-app updater (check remote version → download zip → unzip → restart, surfaced
via `TraySlider`). **What to do (principle 4):** inert stubs — `PluginGetRemoteVersion`→`""`,
`PluginDownload`→null, `GetAutomaticUpdatesAllowed`→false, `RestartACT`/`UnZip` no-op,
`UpdateCheckClicked` never fired. Add inert `TraySlider`/`ButtonLayoutEnum` stubs (**G‑4**) only if a
construction throws at bind time (most are reflective / try-catch tolerant).

## 8L. Error / diagnostic logging — **DONE**

`WriteExceptionLog`, `WriteDebugLog`, `WriteInfoLog` — universal fallback error sink in every plugin's
catch paths. **What to do:** route to our `ILogger`; must exist (called unguarded inside catch) but
the destination is ours.

## 8M. Cross-cutting shape completeness — **DONE / lower-priority**

`FormActMain` events `ActLifecycleChanged`/`LogFileRenamed`/`XmlSnippetAdded`/`BeforeClipboardSet`/
`UrlRequest` (DONE as bindable no-op publishers); `LogLineEventArgs.companionLogName` + 6-arg ctor
(DONE). Lower-priority shape deltas with **no supported consumer:** `ActPluginData.btnXButton` +
`IEquatable`; the ~25 other `oFormXxx` singletons (we have 2) and ~100 `FormActMain` methods real ACT
exposes; richer model members (`CombatantData.AllOut/GetCombatantType`, `EncounterData.NumEnemies`,
`GetMaxHit`/`GetMaxHeal` currently `""` stubs, `HistoryRecord` typed `object`, `ZoneData` ctor/
`AddCombatAction`/`CompareTo`, `Dnum.operator+`, the `SwingTypeEnum`/correct `AttackTypeTypeEnum`
values). Also `ActGlobals.mainTableShowCommas` defaults `false` here vs `true` in real ACT — confirm
intended. **What to do:** build only when a real consumer appears (principle 1).

---

## 8N. Gap summary & net action list

| # | Gap | Consumer | Sev | Action |
|---|---|---|:--:|---|
| **G‑1** | `TTS(string)`/`PlaySound(string)` **methods** missing | OP `MiniParseEventSource.cs:65/75` (cactbot say/play_sound); Hojoring | **BREAK** | Add the two methods routing to the delegate fields (§8F). Closes Hojoring audio too. |
| **G‑2** | `oFormSpellTimers` + `FormSpellTimers` (2 events) missing | OP `SpellTimerOverlay.cs:25/50` | **BREAK** | Add non-null inert stub so `+=` binds (§8G). |
| **G‑3** | `DpiScale` (float) missing | OP `TabControlExt.cs:17` | **MINOR** | Add property → `1f`. |
| **G‑4** | `TraySlider`/`ButtonLayoutEnum` missing | Trig + FFXIV update toast | **MINOR** | Inert stubs only if a bind throws. |
| **G‑5** | self-update returns inert | Trig/Disc/FFXIV | **MINOR** (by design) | Keep stubs inert. |
| **G‑6** | `ActPluginData.btnXButton` + `IEquatable` missing | none reached | **MINOR** | Shape only. |
| **G‑7** | `NotificationAdd` 2-arg only | OP (2-arg) | **none** | Matches consumer. |
| **VERIFY** | `BeforeLogLineRead` reflectable multicast field; exact "Started" status string before Trig/OP/Hojoring init | OP/Trig/Hojoring | — | Test on the live path. |
| **M4** | Hojoring: full `FormSpellTimers` + `TimerData` family; real `CornerControlAdd/Remove`; `Form` lifecycle members (`WindowState`/`CanFocus`/`IsHandleCreated`/`IsDisposed`) | Hojoring | — | Large; the M4 batch (not yet built). |

**Priority order:** G‑1 → G‑2 → G‑3 → VERIFY → (G‑4 conditional) → M4 (Hojoring spell-timer
subsystem — the larger batch). Everything else in this document is **DONE** or **STUB-by-design**.
The four-plugin interface work reduces to three small facade additions (G‑1/G‑2/G‑3) plus
verification; M4 adds the Hojoring spell-timer surface on top.

**Already satisfied (verified):** Discord-Triggers surface fully covered (delegate-field hijack,
`PluginGetSelfData`, `AppDataFolder`, `WriteExceptionLog`, `Form` identity). Triggernometry
bind+degrade surface covered. OverlayPlugin MiniParse read path covered except G‑1/G‑2/G‑3
(`ActiveZone.ActiveEncounter`, `GetAllies`, both static `ExportVariables` incl. OP's 3 injected keys,
the `MasterSwing` fields, `oFormImportProgress.Visible`, `ActPlugins` discovery, and the
`WrappedFfxivPlugin` reflection seam).

---

# Part 3 — Other integration surfaces (NOT the ACT facade)

The ACT facade is only one seam. These three carry the data and features ACT structurally cannot.

## 9. The FFXIV_ACT_Plugin SDK seam — the live data source — **DONE**

ACT is game-agnostic: it has the aggregated DPS rollup and nothing else. **Live combatant tables, raw
+ decoded packets, zone/process/player identity, and the skill/zone/world/buff name dictionaries do
not exist on the ACT side at all** — they live on the FFXIV plugin. So every overlay/trigger pack
pulls the FFXIV plugin's `pluginObj` from `ActPlugins` and reflects three members:

| Member | Shape | Consumers | Purpose |
|---|---|---|---|
| `DataRepository` (property) | `IDataRepository` (pull API) | OP (~30 sites), Trig, Hojoring | `GetCombatantList()`, `GetCurrentPlayerID()`, `GetPlayer()`, `GetResourceDictionary(ResourceType)`, `GetSelectedLanguageID()`, `GetCurrentTerritoryID()`, `GetCurrentFFXIVProcess()`, `GetGameVersion()`, `GetServerTimestamp()` (+ `GetGameRegion`/`IsChatLogAvailable`/`GetAntiVirusNames`, unused). Live state — party panels, target info, name→id, zone/process gating. |
| `DataSubscription` (property) | `IDataSubscription` (push API, 11 events) | OP, Trig, Hojoring | `NetworkReceived`/`NetworkSent` (raw packets — OP's ~20 custom packet-line decoders, FATE/CE/MapEffect watchers, the `RegisterNetworkParser` escape hatch), `LogLine`/`ParsedLogLine` (Trig's feed), `ZoneChanged`, `ProcessChanged` (the "no-game" liveness signal), `PartyListChanged`, `PrimaryPlayerChanged` (+ `Combatant{Added,Removed}`/`PlayerStatsChanged`, unwired). |
| `_iocContainer` (private field, MinIoC) | `GetService`/`Resolve<T>` | OP, Hojoring | Resolves `FFXIV_ACT_Plugin.Logfile.ILogOutput` for the **custom-log round-trip**: OP injects its synthetic lines (custom IDs ≥ 256 — MapEffect/FateDirector/CEDirector/InCombat) via `ILogOutput.WriteLine(...)` so they flow through the FFXIV plugin's log pipeline and reach cactbot. Hojoring also resolves `ILogFormat`. |

**What our host provides — DONE.** `WrappedFfxivPlugin` (placed in `pluginObj`) forwards the real
plugin's `DataRepository` unchanged (every `Get*` lands on the genuine repository), substitutes
`RingBufferDataSubscription` (all 11 events with exact delegate types; one ring + single dispatch
thread replacing the per-subscriber `BeginInvoke` fan-out — ~250× faster), and preserves the
`_iocContainer` field name + slot so `GetService`/`Resolve<T>` hit the real container. The
`IRawPacketSource.InjectNetworkReceived` seam adds a bridge/replay path for raw packets without the
upstream `BeginInvoke` hop, while legacy `RegisterNetworkParser` consumers still see them as normal
`NetworkReceived` events. **No gaps** against the observed consumer set. (Adjacent: OP also reflects
the **Machina** assembly directly for region/opcodes — same discovery path, not part of this seam.)

## 10. OverlayPlugin's web + extension surface — how cactbot integrates

cactbot, ngld addons, and browser overlays do **not** talk to ACT — they talk to OverlayPlugin, which
supplies the three things ACT cannot: a browser to run HTML/JS in, a typed FFXIV push stream
(`LogLine`/`ChangeZone`/`PartyChanged`/`JobGaugeChanged`/`CombatData`/…), and an RPC channel
(`getCombatants`/`say`/`saveData`/…). Two transports drive one `EventDispatcher` subscribe/handler
protocol:
- **In-CEF JS bridge:** `window.OverlayPluginApi.callHandler(json, cb)` + push via
  `window.__OverlayCallback(json)` (CefSharp `JavascriptObjectRepository`).
- **WebSocket server (Fleck, not Kestrel):** `ws://127.0.0.1:<port>/ws` (modern) + `/MiniParse` +
  `/BeforeLogLineRead` (legacy ACTWS envelope). HTML is loaded by CEF from `file://`/remote URLs —
  **there is no Kestrel/HTTP static-file server** in OverlayPlugin. The host injects
  `OVERLAY_WS=…`/`HOST_PORT=…` into the overlay URL so the page discovers the socket.
- **C# extension SDK:** addons implement `IOverlayAddonV2.Init()` (discovered by OverlayPlugin's own
  scan of `ActPlugins`), then register via `Registry.StartEventSource`/`RegisterOverlay`. cactbot's
  `CactbotEventSource` is an `EventSourceBase` registered exactly this way.

**What our host must do — nothing.** Because **OverlayPlugin runs unmodified**, it brings its own
CEF + Fleck + dispatcher + event sources and renders overlays as its own transparent, click-through
windows. Our only obligation is to get OverlayPlugin loaded (§8A) and the §9 FFXIV seam populated;
OverlayPlugin self-hosts the entire web + rendering surface. **We do not build a native overlay
layer** — there is no `Fct.Overlays` / WebView2 / host-side WebSocket path, now or later. This
surface is documented only to explain how cactbot reaches its data; reproducing it in the host is an
explicit non-goal.

## 11. The Discord-Triggers audio bridge — the official audio stack

Discord-Triggers' product is the §8F delegate hijack plus an out-of-process synthesis/transport chain:
`vm.SpeakText`/`SpeakSoundFile` → `SpeakTextCoreAsync` → a named-pipe **bridge** to a Node/ONNX backend
(Piper/Kokoro voices) → a Discord voice channel (or local device). Our facade's only contribution is
the §8F indirection — expose `TTS()`/`PlaySound()` routing through the swappable delegate fields, and a
`Form` identity to host its WPF config tab + folder pickers. Everything else is internal to
Discord-Triggers, which is why making it the official audio stack costs essentially nothing beyond G‑1.

## 12. Our own seams (for completeness)

- **The net48↔net10 IPC bridge** (`Fct.Bridge`): not an ACT surface — how the satellite (legacy
  plugins + facade) and the net10 host exchange typed domain events. Plugins never see it.
- **`IRawPacketSource`** (`Fct.Abstractions`): the one opt-in escape hatch exposing raw packets to
  net10 consumers / legacy `RegisterNetworkParser`, fed by `RingBufferDataSubscription`.

---

## Corrections to COMPAT-GAPS.md (reconciled)

This audit corrected two earlier inaccuracies, now reflected in COMPAT-GAPS.md:
1. **`oFormSpellTimers` is consumed** (OverlayPlugin `SpellTimerOverlay.cs` + Hojoring) — was wrongly
   listed as a phantom with "no consumer." Now **G‑2** / COMPAT-GAPS M1‑4 + M4.
2. **`TTS`/`PlaySound` methods are a gap** — the delegate *fields* (DONE) cover the Discord/TTSYukkuri
   hijack, but OP/cactbot call the **methods**, which the facade lacked. Now **G‑1** / COMPAT-GAPS M1‑3.
