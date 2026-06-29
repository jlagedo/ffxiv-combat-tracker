# ACT Interface Map — what the legacy plugins consume from Advanced Combat Tracker

Authoritative map of every member each supported legacy plugin touches on the **ACT host**
(`Advanced_Combat_Tracker`, the generic `Advanced Combat Tracker.exe` — *not* FFXIV_ACT_Plugin),
why it's used, and where our compatibility facade (`src/Fct.Compat.Act`) stands against it.

This is the **host↔plugin contract** our facade must impersonate. It complements:
- [`DATA-FLOW.md`](DATA-FLOW.md) — the data-flow narrative.
- [`COMPAT-GAPS.md`](COMPAT-GAPS.md) — the phased backlog (this document corrects two errors in it; see
  *Corrections* at the end).

## Method

Each plugin source tree was swept for references into the `Advanced_Combat_Tracker` namespace
(`ActGlobals`, `FormActMain`, `IActPluginV1`, `ActPluginData`, the `Models/*` types, and the events
on `FormActMain`). Symbols that merely *look* like ACT members but are the plugin's own types
(Hojoring's `CombatantData`/`AttackTypes`, Triggernometry's `CombatantData`/`CustomTriggerProxy`,
OverlayPlugin's `MessageType`) were excluded. ACT signatures verified against the decompile at
`E:\dev\ACT-decompiled\Advanced_Combat_Tracker\`.

### Scope note

Of the five plugins requested, **four are the locked build target** (FFXIV_ACT_Plugin,
OverlayPlugin, Triggernometry, Discord-Triggers). **ACT.Hojoring is a fifth, out-of-scope today** —
mapped here so its requirements are known, but it is *not* part of the supported stack in
`CLAUDE.md`. It is the heaviest and most unusual ACT consumer.

---

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

---

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
satisfies.

---

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

---

## 4. ACT-Discord-Triggers — the audio bridge

`E:\dev\ACT-Discord-Triggers`. Smallest, purely host-services surface. Consumes **zero** combat /
encounter / log-line data. Its product is hijacking ACT's global TTS/sound delegates to reroute audio
to a Discord voice channel via an out-of-process Node bridge.

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
paths must actually call through whatever is currently assigned. Requires `oFormActMain` to be a real
WinForms `Form` (handle, `Invoke`, dialog owner).

---

## 5. ACT.Hojoring — out-of-scope, heaviest consumer

`E:\dev\ACT.Hojoring`. Four `IActPluginV1` plugins (SpecialSpellTimer, TTSYukkuri, UltraScouter,
XIVLog). Uses ACT as a **service bus + UI host**, not a data source (combat data comes from
FFXIV_ACT_Plugin's `DataRepository` via `pluginObj` reflection). **Not in the supported stack** — listed
for gap visibility.

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

**Unusual uses no supported plugin needs:** the whole `oFormSpellTimers`/`FormSpellTimers` API (also
touched lightly by OverlayPlugin), `BeforeLogLineRead` handler-list reflection surgery,
`WindowState`/`CanFocus`/`IsHandleCreated`/`IsDisposed`, `CornerControlAdd/Remove`.

---

## 6. Consolidated surface matrix

Which plugin touches which ACT member (● = uses, blank = does not). `FFXIV` = FFXIV_ACT_Plugin,
`OP` = OverlayPlugin, `Trig` = Triggernometry, `Disc` = Discord-Triggers, `Hojo` = Hojoring (OOS).

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

## 7. Comparison with our facade + gaps

Baseline: `src/Fct.Compat.Act`. Everything **FFXIV_ACT_Plugin** touches is present and functional
(the `ACTWrapper` seam + model registration), and Slice 1 verified the producer path. The gaps below
are on the **consumer** side. Severity: **BLOCK** = won't bind/load · **BREAK** = feature throws or
silently dead when exercised · **MINOR** = cosmetic/edge.

### 7a. Supported-stack gaps (must fix for the build target)

| # | Gap | Consumer | Sev | Detail |
|---|---|---|:--:|---|
| **G‑1** | `FormActMain.TTS(string)` + `PlaySound(string)` **methods** missing | OverlayPlugin `MiniParseEventSource.cs:65/75` (cactbot `say`/`play_sound`) | **BREAK** | Facade defines only the `PlaySoundDelegate` type + `PlayTtsMethod`/`PlaySoundMethod` *fields*. A cactbot TTS/sound action calls the **method** → `MissingMethodException`. Fix: add `public void TTS(string)` / `public void PlaySound(string)` that invoke the corresponding delegate field (matching real ACT, where the method routes through the swappable delegate — this also wires Discord/TTSYukkuri's hijack to OP/cactbot output). |
| **G‑2** | `ActGlobals.oFormSpellTimers` field + `FormSpellTimers` type (`OnSpellTimerNotify`/`OnSpellTimerRemoved` events) missing | OverlayPlugin `SpellTimerOverlay.cs:25/50` | **BREAK** | Only triggered if the user adds a SpellTimer overlay, but then `+=` on a null field → NRE / bind failure. Fix: add `ActGlobals.oFormSpellTimers` (non-null stub `FormSpellTimers` exposing the two events, never fired) so the subscription binds inertly. `COMPAT-GAPS.md` incorrectly states this has no consumer. |
| **G‑3** | `FormActMain.DpiScale` (float prop) missing | OverlayPlugin `TabControlExt.cs:17` | **MINOR** | Read inside try/catch defaulting to `1`, so tolerated — but add the property (return `1f`) to remove the silent catch. |
| **G‑4** | `TraySlider` + `ButtonLayoutEnum` types missing | Triggernometry + FFXIV self-update toast | **MINOR** | Update-notification UI. Self-update is out of scope; `FailsafeRegisterHook`/try-catch tolerate absence. Add inert stubs only if a bind throws. |
| **G‑5** | `PluginGetRemoteVersion` returns `""`; `PluginDownload` returns null; `RestartACT`/`UnZip` no-op | Trig/Disc/FFXIV updaters | **MINOR** (by design) | We do not self-update legacy plugins. Stubs present; keep them inert. |
| **G‑6** | `ActPluginData.btnXButton` + `IEquatable<ActPluginData>` missing | none reached (OP reflects only `cbEnabled`/`lblPluginTitle`/`pluginObj`) | **MINOR** | Shape completeness only. |
| **G‑7** | `NotificationAdd` 2-arg only; real ACT has a 4-arg overload | OverlayPlugin calls the 2-arg form | **none** | 2-arg matches the consumer; note for completeness. |

### 7b. Already satisfied (verified)

The Discord-Triggers surface is **fully covered** (delegate-field hijack, `PluginGetSelfData`,
`AppDataFolder`, `WriteExceptionLog`, `Form` identity). The Triggernometry bind+degrade surface is
covered (`BeforeLogLineRead`/`OnLogLineRead`/combat events, `CustomTriggers` empty set,
`GetTextExport`+`defaultTextFormat`, `LogLineEntry`, `ZoneList`, corner-control reflective no-ops).
OverlayPlugin's MiniParse read path is covered **except G‑1/G‑2/G‑3**: `ActiveZone.ActiveEncounter`,
`GetAllies`, both static `ExportVariables` dicts (incl. OP's 3 injected keys), `MasterSwing`
`Tags`/`Special`/`DamageType`/`Damage`, `oFormImportProgress.Visible`, `ActPlugins` discovery, and
the `WrappedFfxivPlugin` reflection seam (`DataRepository`/`DataSubscription`/`_iocContainer`).

### 7c. Hojoring gaps (out-of-scope — for future scoping only)

If Hojoring is ever added to the supported stack, beyond G‑1/G‑2 it additionally needs:

- **Full `FormSpellTimers` API** (`RebuildSpellTreeView`, `TimerDefs`, `RemoveTimerDef`,
  `AddEditTimerDef(TimerData)`, `NotifySpell`) + the `TimerData`/`SpellTimer`/`TimelineEvent`/
  `TimerMod`/`TimerFrame` model family — entirely absent from the facade.
- `FormActMain.CornerControlAdd/Remove` as **real** methods (Trig only reflects them with a null
  guard; Hojoring calls them directly).
- `Form`-inherited `WindowState` (settable), `CanFocus`, `IsHandleCreated`, `IsDisposed`.
- `BeforeLogLineRead` as a reflectable multicast delegate field (already is — used for handler-list
  surgery; verify reflection shape).

### 7d. Lower-priority shape deltas (no supported consumer)

Real ACT exposes ~25 `oFormXxx` singletons (we have 2), ~100 `FormActMain` methods (we implement the
consumed subset), and richer model members (`CombatantData.AllOut/GetCombatantType`,
`EncounterData.NumEnemies/GetMaxHit` — currently `""` stubs, `HistoryRecord` typed `object`,
`ZoneData` ctor/`AddCombatAction`/`CompareTo`, `Dnum.operator+`, the `SwingTypeEnum`/correct
`AttackTypeTypeEnum` values). None are reached by the four supported plugins; track as cross-cutting
shape completeness. Also `ActGlobals.mainTableShowCommas` defaults `false` in the facade vs `true`
in real ACT — confirm intended.

---

## Corrections to COMPAT-GAPS.md

This audit found two inaccuracies in the existing backlog:

1. **`oFormSpellTimers` is consumed.** COMPAT-GAPS.md "Phantoms removed" claims
   `ActGlobals.oFormSpellTimers`/`SpellTimer` has "no consumer in the supported stack." OverlayPlugin's
   `SpellTimerOverlay.cs` subscribes its events (and Hojoring uses the full API). It is **G‑2** above.
2. **`TTS`/`PlaySound` methods are a gap.** COMPAT-GAPS.md lists TTS/sound as DONE via the
   `PlayTtsMethod`/`PlaySoundMethod` *delegate fields* (M3). Those cover the Discord/TTSYukkuri
   *hijack*, but OverlayPlugin/cactbot call the **methods** `oFormActMain.TTS(...)`/`.PlaySound(...)`,
   which the facade does not define. It is **G‑1** above.
