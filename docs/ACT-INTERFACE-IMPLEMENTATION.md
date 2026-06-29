# ACT Interface Implementation Guide — why each surface is used, and what to build

Implementation companion to [`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) (the *what* — every
`Advanced_Combat_Tracker` member each plugin touches) and [`COMPAT-GAPS.md`](COMPAT-GAPS.md) (the
phased backlog). This doc answers, **per interface group**: *why* the plugins use it, and *what we
must actually implement* given our philosophy below. It also maps the **non-ACT integration
surfaces** (the FFXIV_ACT_Plugin SDK seam, OverlayPlugin's web/extension layer, the Discord audio
bridge) and explains why each exists.

## Guiding principles (these decide how much of each interface we build)

1. **Impersonate ACT only enough to make the plugins bind and run.** We are *not* reproducing ACT's
   UI, options forms, encounter-history database, HTML/web export, plugin-manager, or its built-in
   spell-timer/timeline editors. For each member the question is "what does the plugin *do* with the
   return value," and we implement the smallest thing that keeps that behavior correct.
2. **Audio is owned by ACT-Discord-Triggers — the official audio stack.** ACT's real `TTS(string)` /
   `PlaySound(string)` route through the swappable `PlayTtsMethod` / `PlaySoundMethod` delegates
   (verified in the decompile, `FormActMain.cs:4237/4292`). We reproduce exactly that indirection.
   Discord-Triggers installs the real sink by swapping those delegates; everything else (cactbot
   `say`, Triggernometry TTS, Hojoring) flows into it for free. See §Audio.
3. **Live game state flows through the FFXIV_ACT_Plugin SDK seam, not ACT.** ACT only holds the
   *aggregated* DPS rollup. Combatant tables, raw/decoded packets, zone/process/player identity and
   name dictionaries live behind `IDataRepository`/`IDataSubscription`/`_iocContainer` on the FFXIV
   plugin object — served by our `WrappedFfxivPlugin`. See §Other integration surfaces.
4. **Self-update is out of scope.** We do not download or restart legacy plugins. Those members are
   inert stubs that satisfy binding.
5. **Reflection-discovered / failsafe-registered members may simply be absent or no-op** where the
   consumer guards them (`GetMethod(...) != null`, try/catch, `FailsafeRegisterHook`). We only build
   them when a consumer calls them *unguarded*.

Status legend: **DONE** implemented + functional · **GAP** missing, must build · **STUB** present but
inert (intended) · **VERIFY** present, untested on the live path.

---

## A. Plugin lifecycle, identity & discovery

**Members:** `IActPluginV1.{InitPlugin,DeInitPlugin}`, `ActGlobals.oFormActMain`, `InitActDone`,
`IsActClosing`, `Handle`, `Invoke`/`InvokeRequired`, `GetVersion()`, `AppDataFolder`,
`ActPlugins` (`List<ActPluginData>`), `ActPluginData.{pluginObj,pluginFile,lblPluginTitle,
lblPluginStatus,cbEnabled,tpPluginSpace}`, `PluginGetSelfData`.

**Why.** Every plugin enters through `IActPluginV1.InitPlugin(TabPage, Label)` and dereferences
`ActGlobals.oFormActMain` for everything else. `InitActDone`/`Handle` are the "ACT finished booting"
gate (OverlayPlugin and Hojoring busy-wait on them because the FFXIV plugin may load after them).
`ActPlugins` is the **service registry**: every consumer scans it to find itself (config/install
dir), to find the FFXIV plugin (by `lblPluginTitle.Text` starting `FFXIV_ACT_Plugin` + `cbEnabled.
Checked` + non-null `pluginObj`), and (OverlayPlugin) to find cactbot/Triggernometry/addons.
`PluginGetSelfData(this)` returns the caller's own `ActPluginData` so it can locate its DLL.
`Invoke`/`InvokeRequired`/`Handle` come free from `FormActMain : Form`.

**What to do.** **DONE.** All present and exercised in Slice 1. The load-bearing requirement is that
the host populates `ActPlugins` with a correctly-shaped `ActPluginData` per loaded plugin — in
particular the FFXIV entry's `pluginObj` must be our `WrappedFfxivPlugin` and `lblPluginStatus.Text`
must be the exact "Started" string (see §M2‑7/M4‑5 in COMPAT-GAPS — one VERIFY). `GetVersion()`
returns `3.8.5.288` (OverlayPlugin gates on ≥ 3.8.0.281). Keep `oFormActMain` a real WinForms `Form`
(Discord-Triggers/Hojoring parent dialogs to it and marshal onto its UI thread).

---

## B. The producer write-path (FFXIV_ACT_Plugin → aggregation)

**Members:** `ActGlobals.charName`, `AddCombatAction(MasterSwing)`, `SetEncounter(DateTime,att,vic)
→bool`, `ChangeZone`, `EndCombat(bool)`, `InCombat`, `GlobalTimeSorter`, `LastKnownTime`,
`MasterSwing` (10-arg ctor + `Tags/Damage/Time/Special/DamageType/AttackType/Critical`), `Dnum`
(`NoDamage/Miss/Death` + implicit `long`), static model registration on `CombatantData` /
`EncounterData` / `AttackType` / `MasterSwing` (`ColumnDefs`, `ExportVariables`,
`OutgoingDamageTypeDataObjects`, `DamageTypeDef`, `ColumnDef`, `TextExportFormatter`),
`ValidateLists`/`ValidateTableSetup`, `ActLocalization.LocalizationStrings`.

**Why.** This is the **only writer** into ACT. FFXIV_ACT_Plugin converts each parsed entry to a
`MasterSwing` and calls `AddCombatAction`, gated by `SetEncounter` (opens/continues the encounter)
and `InCombat`. `ACT_UIMods` rewrites ACT's *static* table model so the aggregated numbers expose
FFXIV-specific columns and the `ExportVariables` keys (`Job`, `DirectHit`, `OverHeal`, potency,
`Last{10,30,60}DPS`, `CurrentZoneName`) that OverlayPlugin later reads. `charName` names the local
player on every swing.

**What to do.** **DONE** and verified bit-for-bit on captured combat (Slice 1 S5; see TESTING.md
"Differential ACT-engine compat"). This is the most complete part of the facade — the aggregation
engine in `Aggregation.cs` + the model registration in `CombatTables.cs` already reproduce ACT's
rollup exactly. No new work. (The clean-room *value calculation* — simulated DoT/HoT/shield amounts
— is a separate axis tracked as Phase P; not an interface-surface concern.)

---

## C. The inbound log seam (how the FFXIV plugin feeds ACT)

**Members:** `OpenLog(bool,bool)`, `LogFilePath`/`LogFileFilter`, `TimeStampLen`,
`LogPathHasCharName`, `ZoneChangeRegex`, `GetDateTimeFromLog` (`DateTimeLogParser`),
`BeforeLogLineRead` (event), `OnLogLineRead` (event), `LogFileChanged` (event), `LogLineEventArgs.
{logLine,originalLogLine,detectedType,detectedTime,detectedZone,companionLogName}`, `ReadThreadLock`,
`Visible`, `GenerateAttackTypeGraph`, `FindControl<Button>`, `ActCommands`.

**Why.** FFXIV_ACT_Plugin owns log parsing: it points ACT at its `Network_*.log` (`OpenLog` +
`LogFilePath`/`Filter`), overrides timestamp parsing (`GetDateTimeFromLog`), sets the zone-change
regex + timestamp length, and rewrites each line inside `BeforeLogLineRead` before ACT re-broadcasts
it as `OnLogLineRead`. **`BeforeLogLineRead`/`OnLogLineRead` are the single most important consumer
surface after CombatData:** Triggernometry, Hojoring, and OverlayPlugin all tap this stream as their
trigger/parse feed. Hojoring and OverlayPlugin's `LogParseOverlay` *reflect the event's backing
field* to unhook all handlers and insert themselves first (ordering priority) — so the event must be
a real, reflectable multicast delegate field, not an add/remove-only property.

**What to do.** **DONE**, with one **VERIFY**: confirm the multicast-field reflection shape that
Hojoring/`LogParseOverlay` depend on (the field must be discoverable via
`GetField("BeforeLogLineRead", NonPublic|Instance|Public|Static)` and reassignable). The facade
already fires these via `FireBeforeLogLineRead`/`FireLogLineRead`. `GenerateAttackTypeGraph` /
`FindControl` / `ActCommands` are STUBs (graph generation + `/echo` are UI/diagnostic, not needed for
data flow) — keep inert.

---

## D. The consumer read-path (MiniParse / cactbot CombatData)

**Members:** `ActiveZone` (`ZoneData`), `ZoneData.ActiveEncounter` (`EncounterData`),
`EncounterData.{GetAllies(),Active,EncId,EndTime,ExportVariables}`, `CombatantData.{ExportVariables,
Name,Items,DamageTypeDataOutgoing{Damage,Healing}}`, `AttackType.Items`, `MasterSwing.{Tags
["overheal"],Special,Damage,DamageType}`, `EncounterData/CombatantData.TextExportFormatter.
GetExportString`, `oFormImportProgress.Visible`, `CurrentZone`, `InCombat`.

**Why.** This is how the live DPS table reaches cactbot. Each tick OverlayPlugin's
`MiniParseEventSource.CreateCombatData` gates on `oFormActMain?.ActiveZone?.ActiveEncounter` +
both static `ExportVariables` dicts non-null, then walks `GetAllies()` × `ExportVariables`, calling
`GetExportString` to build the `Encounter` and per-`Combatant` JSON. OverlayPlugin **mutates** the
static `CombatantData.ExportVariables` to add three keys (`overHeal`, `damageShield`, `absorbHeal`)
whose formatters iterate the `MasterSwing` list reading `Tags["overheal"]`/`Special`/`DamageType`/
`Damage`. `oFormImportProgress.Visible` lets it skip ticks during a log import.

**What to do.** **DONE.** The whole path is present and the static `ExportVariables` dicts are real
mutable dictionaries (so OP's `.Add` of its 3 keys works), `MasterSwing` carries the required
`Tags`/`Special`/`DamageType`/`Damage`, and `oFormImportProgress` is a null-by-default stub field so
`?.Visible` short-circuits to "not importing." No new work for the supported read path.

---

## E. Combat-state events

**Members:** `OnCombatStart`, `OnCombatEnd` (events), `CombatToggleEventArgs`.

**Why.** Triggernometry fires its own `OnCombatStart`/`OnCombatEnd` extended events off these; some
overlays use them too. Raised by the facade inside `SetEncounter`/`EndCombat`.

**What to do.** **DONE.** Bindable, fired on encounter open/close.

---

## F. Audio — TTS / PlaySound (the Discord-Triggers stack)

**Members:** `FormActMain.TTS(string)` + `PlaySound(string)` **methods** (GAP G‑1);
`PlayTtsMethod`/`PlaySoundMethod` **delegate fields** (DONE); delegate types `PlayTtsDelegate`
(`void(string)`) / `PlaySoundDelegate` (`void(string,int)`) (DONE).

**Why — and the architecture.** Real ACT exposes audio two ways that are *one mechanism*:
- the **methods** `TTS(text)` / `PlaySound(file)` — what callers invoke (OverlayPlugin/cactbot
  `say`/`play_sound` at `MiniParseEventSource.cs:65/75`; Hojoring's SoundController/TimelineController);
- the **delegate fields** `PlayTtsMethod` / `PlaySoundMethod` — the swappable *sink*. The decompiled
  methods do their bookkeeping (path-rooting, speech-correction regex, recursion guard) and then call
  `PlayTtsMethod(text)` / `PlaySoundMethod(file, vol)` (`FormActMain.cs:4237/4292`).

ACT-Discord-Triggers is built on this: on activation it saves the old delegates and sets
`oFormActMain.PlayTtsMethod = vm.SpeakText` / `PlaySoundMethod = vm.SpeakSoundFile`
(`DiscordTriggersView.xaml.cs:178/179`), restoring them on deinit. `SpeakText`/`SpeakSoundFile`
synthesize (ONNX/Piper/Kokoro) and ship audio over the named-pipe bridge to a Discord voice channel.
**Because the method routes through the delegate, anything that calls `TTS()`/`PlaySound()` — cactbot,
Triggernometry, Hojoring — automatically lands in Discord-Triggers once it's the installed sink.**
That is exactly what "Discord-Triggers is the official audio stack" means in our host.

**What to do (G‑1).** Add to the facade's `FormActMain`:

```csharp
public void TTS(string SpeechText)        => PlayTtsMethod?.Invoke(SpeechText);
public void PlaySound(string WavFilePath) => PlaySoundMethod?.Invoke(WavFilePath, 100);
```

Minimal is correct here — we deliberately skip ACT's speech-correction list, per-channel volume
sliders, and TTS-to-WAV caching (all ACT-UI/options state we don't reproduce). Keep the delegate
fields' **no-op defaults** (`PlaySoundMethod = (w,v)=>{}` / `PlayTtsMethod = t=>{}`, already present):
with Discord-Triggers not loaded/activated, audio is silently dropped rather than throwing — the
intended degrade. With it activated, the swap makes Discord the sink. The fields are already public
mutable fields of the exact delegate types (Discord-Triggers reads + reassigns them), so no change
there. This one fix closes G‑1 **and** Hojoring's M4‑1 (same methods).

> Optional, not required: if we ever want local audio without Discord-Triggers, install a default
> delegate that plays a WAV / drives SAPI. Given the official-audio-stack decision, leave it no-op
> and let Discord-Triggers own it.

---

## G. Spell-timer subsystem — `oFormSpellTimers` / `FormSpellTimers`

**Members:** `ActGlobals.oFormSpellTimers` + `FormSpellTimers.{OnSpellTimerNotify,OnSpellTimerRemoved}
(events), RebuildSpellTreeView(), TimerDefs, RemoveTimerDef(...), AddEditTimerDef(TimerData),
NotifySpell(...)}`; model family `TimerData`/`SpellTimer`/`TimelineEvent`/`TimerMod`/`TimerFrame`.

**Why.** This is ACT's *built-in* spell-timer engine (a second WinForms form). Two very different
consumers:
- **OverlayPlugin** (`SpellTimerOverlay.cs:25/50`) only *subscribes* `OnSpellTimerNotify`/
  `OnSpellTimerRemoved` — and only if the user adds a "SpellTimer" overlay type. It reads
  `timer.StartTime` to mirror ACT's native timers into a web overlay.
- **Hojoring** *drives the whole API* — `SpecialSpellTimer` rebuilds the tree, enumerates/edits/
  removes `TimerDefs`, and calls `NotifySpell` to push its own timers into ACT's native UI.

**What to do.** Two tiers, matching scope:
- **Supported stack (G‑2, BREAK):** add `ActGlobals.oFormSpellTimers` as a **non-null** stub
  `FormSpellTimers` exposing the two events (never fired). That makes OverlayPlugin's `+=` bind
  inertly — the optional SpellTimer overlay then simply shows nothing instead of NRE-ing. Minimal;
  no timer engine. This is the right altitude: we don't reproduce ACT's spell-timer feature, we just
  let the subscription resolve.
- **Hojoring (M4‑2, out-of-scope today):** would need a *working* `FormSpellTimers` (a real
  `TimerDefs` collection + add/remove/rebuild/notify) plus the `TimerData`/`SpellTimer`/`TimelineEvent`/
  `TimerMod`/`TimerFrame` model family — all currently absent. This is the single largest Hojoring
  item. Only build it if Hojoring becomes a committed target.

---

## H. Custom-trigger import — `CustomTriggers` / `CustomTrigger`

**Members:** `FormActMain.CustomTriggers` / `ActiveCustomTriggers` (`SortedList<string,CustomTrigger>`)
+ `CustomTrigger.{Category,RestrictToCategoryZone,Active,ShortRegexString,SoundData,SoundType,
TimerName,Tabbed,Timer}`.

**Why.** Triggernometry offers a one-way *import* of ACT-native custom triggers into its own model
(reads each `CustomTrigger`'s nine fields). It does **not** ask ACT to evaluate triggers — Trig runs
its own engine off the log stream.

**What to do.** **DONE** as empty `SortedList`s + an inert `CustomTrigger` model. `Count == 0` →
nothing to import; Triggernometry's own triggers still run. We never author ACT-native custom triggers
(that UI is not reproduced), so empty is the correct permanent state.

---

## I. Encounter text export & encounter log

**Members:** `GetTextExport(EncounterData,TextExportFormatOptions)` + private `defaultTextFormat`
field + `TextExportFormatOptions` type; `ZoneList` (`List<ZoneData>`, `[i].Items`); `LogLineEntry` +
`EncounterData.LogLines`.

**Why.** Triggernometry's export/last-encounter actions render an encounter to text (reflecting the
private `defaultTextFormat` by name and passing it to `GetTextExport`), walk `ZoneList`→`Items` for
the previous encounter, and append synthetic `LogLineEntry`s to `ActiveEncounter.LogLines`. Discord
has an indirect path here too but calls none of it directly.

**What to do.** **DONE (minimal).** `GetTextExport` returns `"(duration) title"` — enough to satisfy
the consumer without a formatter engine (we don't reproduce ACT's rich text/HTML export). The private
`defaultTextFormat` is non-null so the name-based reflection binds; `ZoneList` holds the live zone;
`LogLineEntry` has the 4-arg ctor and `LogLines` is correctly typed. No new work.

---

## J. Host-window chrome (corner controls, window state, notifications, DPI)

**Members:** `CornerControlAdd`/`CornerControlRemove`, private `tc1` TabControl, `WindowState`,
`CanFocus`, `IsHandleCreated`, `IsDisposed`, `NotificationAdd(string,string)`, `DpiScale`.

**Why.** Cosmetic host-UI integration. Triggernometry *reflects* `CornerControlAdd/Remove` (null-
guarded) for a corner popup and digs `tc1` to select its own tab. Hojoring calls `CornerControlAdd/
Remove` *directly* and toggles `WindowState`. OverlayPlugin reads `DpiScale` (try/catch → 1) and calls
`NotificationAdd` only on a version-too-old error.

**What to do.** Minimal, per principle 1 (we don't reproduce ACT chrome):
- **Supported stack:** `NotificationAdd` STUB (logs) and `DpiScale` → `1f` (G‑3, MINOR — removes
  OverlayPlugin's silent catch). `CornerControlAdd/Remove`/`tc1` stay **OMITTED** — Triggernometry
  guards them. All DONE/acceptable.
- **Hojoring (M4‑3/M4‑4):** `CornerControlAdd/Remove` must become real (no-op that tracks the
  control); `WindowState` settable no-op; `CanFocus`/`IsHandleCreated`/`IsDisposed` must return sane
  values (inherited from `Form` — verify the headless form reports `IsHandleCreated==true` once
  shown, `IsDisposed==false`).

---

## K. Self-update plumbing

**Members:** `PluginGetRemoteVersion(int)`, `PluginDownload(int)`, `PluginDownloadMem(int)`,
`UnZip(...)`, `GetAutomaticUpdatesAllowed()`, `UpdateCheckClicked` (event), `RestartACT(bool,string)`,
+ `TraySlider`/`ButtonLayoutEnum`/`PluginDownloadData` types.

**Why.** FFXIV_ACT_Plugin (id 73), Triggernometry (id 87), OverlayPlugin, and Discord all have an
in-app updater that checks a remote version, downloads a zip, unzips, and restarts ACT, surfacing a
`TraySlider` toast.

**What to do.** **STUB by design (principle 4).** `PluginGetRemoteVersion` → `""`, `PluginDownload`
→ null, `GetAutomaticUpdatesAllowed` → false, `RestartACT`/`UnZip` no-op, `UpdateCheckClicked` never
fired. Plugins are managed by us, not self-updated. Add inert `TraySlider`/`ButtonLayoutEnum` stubs
(G‑4) only if a consumer's construction throws at bind time (most are reflective/try-catch tolerant).

---

## L. Error / diagnostic logging

**Members:** `WriteExceptionLog(Exception,string)`, `WriteDebugLog(string)`, `WriteInfoLog(string)`.

**Why.** Universal fallback error sink across every plugin's teardown/catch paths.

**What to do.** **DONE** — route to our `ILogger`. Must exist (called unguarded inside catch blocks)
but the destination is ours.

---

## M. Cross-cutting shape completeness (no live consumer)

`FormActMain` events `ActLifecycleChanged`/`LogFileRenamed`/`XmlSnippetAdded`/`BeforeClipboardSet`/
`UrlRequest` (DONE as bindable no-op publishers); `LogLineEventArgs.companionLogName` + 6-arg ctor
(DONE); `ActPluginData.btnXButton` + `IEquatable` (MINOR, missing, unreached); the ~25 other
`oFormXxx` singletons and ~100 `FormActMain` methods real ACT exposes that nobody in the stack calls.
**What to do:** nothing now — build only when a real consumer appears (principle 1).

---

## Other integration surfaces (NOT the ACT facade) — and why they exist

The ACT facade is only one of the seams the legacy stack relies on. These three carry the data and
features ACT structurally cannot.

### 1. The FFXIV_ACT_Plugin SDK seam — the live data source

ACT is game-agnostic: it has the aggregated DPS rollup and nothing else. **Live combatant tables, raw
+ decoded network packets, zone/process/player identity, and the skill/zone/world/buff name
dictionaries do not exist on the ACT side at all** — they live on the FFXIV plugin. So every overlay/
trigger pack pulls the FFXIV plugin's `pluginObj` from `ActPlugins` and reflects three members:

| Member | Shape | Consumers | Purpose |
|---|---|---|---|
| `DataRepository` (property) | `IDataRepository` (pull API) | OverlayPlugin (~30 sites), Triggernometry, Hojoring | `GetCombatantList()`, `GetCurrentPlayerID()`, `GetPlayer()`, `GetResourceDictionary(ResourceType)`, `GetSelectedLanguageID()`, `GetCurrentTerritoryID()`, `GetCurrentFFXIVProcess()`, `GetGameVersion()`, `GetServerTimestamp()` (+ `GetGameRegion`/`IsChatLogAvailable`/`GetAntiVirusNames`, unused by the three). The live state source — party panels, target info, name→id, zone/process gating. |
| `DataSubscription` (property) | `IDataSubscription` (push API, 11 events) | OverlayPlugin, Triggernometry, Hojoring | `NetworkReceived`/`NetworkSent` (raw packets — OP's ~20 custom packet-line decoders, FATE/CE/MapEffect watchers, the `RegisterNetworkParser` escape hatch), `LogLine`/`ParsedLogLine` (Trig's feed), `ZoneChanged`, `ProcessChanged` (the "no-game" liveness signal), `PartyListChanged`, `PrimaryPlayerChanged` (+ `Combatant{Added,Removed}`/`PlayerStatsChanged`, unwired). |
| `_iocContainer` (private field, MinIoC) | `GetService`/`Resolve<T>` | OverlayPlugin, Hojoring | Resolves `FFXIV_ACT_Plugin.Logfile.ILogOutput` for the **custom-log round-trip**: OverlayPlugin injects its synthetic lines (custom IDs ≥ 256 — MapEffect/FateDirector/CEDirector/InCombat) via `ILogOutput.WriteLine(...)` so they flow through the FFXIV plugin's log pipeline and reach cactbot. Hojoring also resolves `ILogFormat`. |

**What our host provides — DONE.** `WrappedFfxivPlugin` (placed in `pluginObj`) forwards the real
plugin's `DataRepository` unchanged (so every `Get*` lands on the genuine repository), substitutes
`RingBufferDataSubscription` (implements all 11 events with exact delegate types, one ring + single
dispatch thread replacing the per-subscriber `BeginInvoke` fan-out — ~250× faster), and preserves the
`_iocContainer` field name + slot so `GetService`/`Resolve<T>` hit the real container. The
`IRawPacketSource.InjectNetworkReceived` seam adds a bridge/replay path for raw packets without the
upstream `BeginInvoke` hop, while legacy `RegisterNetworkParser` consumers still see them as normal
`NetworkReceived` events. **No gaps** against the observed consumer set. (Adjacent: OverlayPlugin
also reflects the **Machina** assembly directly for `GetMachinaRegion`/opcodes — not part of this
seam, but on the same discovery path.)

### 2. OverlayPlugin's web + extension surface — how cactbot actually integrates

cactbot, ngld addons, and browser overlays do **not** talk to ACT — they talk to OverlayPlugin, which
supplies the three things ACT cannot: a browser to run HTML/JS in, a typed FFXIV push stream
(`LogLine`/`ChangeZone`/`PartyChanged`/`JobGaugeChanged`/`CombatData`/…), and an RPC channel
(`getCombatants`/`say`/`saveData`/…). Two transports drive one `EventDispatcher`
subscribe/handler protocol:
- **In-CEF JS bridge:** `window.OverlayPluginApi.callHandler(json, cb)` + push via
  `window.__OverlayCallback(json)` (CefSharp `JavascriptObjectRepository`).
- **WebSocket server (Fleck, not Kestrel):** `ws://127.0.0.1:<port>/ws` (modern) + `/MiniParse` +
  `/BeforeLogLineRead` (legacy ACTWS envelope). HTML is loaded by CEF from `file://`/remote URLs —
  **there is no Kestrel/HTTP static-file server** in OverlayPlugin. The host injects
  `OVERLAY_WS=…`/`HOST_PORT=…` into the overlay URL so the page discovers the socket.
- **C# extension SDK:** addons implement `IOverlayAddonV2.Init()` (discovered by OverlayPlugin's own
  scan of `ActPlugins`), then register via `Registry.StartEventSource`/`RegisterOverlay`. cactbot's
  `CactbotEventSource` is an `EventSourceBase` registered exactly this way.

**What our host must do — nothing now; future work.** Because **OverlayPlugin runs unmodified**, it
brings its own CEF + Fleck + dispatcher + event sources. Our supported-stack obligation is only to
get OverlayPlugin loaded and the FFXIV seam (above) populated; OverlayPlugin self-hosts the entire web
surface. This surface matters only for the **future native `Fct.Overlays` (WebView2) layer**: to host
cactbot *without* OverlayPlugin we would have to reproduce the dispatcher protocol (`subscribe`/
`call`+`rseq`/cached replay), both transports, the injected `window.OverlayPluginApi` +
`__OverlayCallback` (via WebView2 `AddHostObjectToScript`/`PostWebMessage`), the URL param injection,
the event/handler catalog, and click-through transparent rendering. (Note: CLAUDE.md's "overlays via
Kestrel" describes that *future native* layer, not unmodified OverlayPlugin — they differ.)

### 3. The Discord-Triggers audio bridge — the official audio stack

Discord-Triggers' product is the delegate hijack in §F plus an out-of-process synthesis/transport
chain: `vm.SpeakText`/`SpeakSoundFile` → `SpeakTextCoreAsync` → a named-pipe **bridge** to a Node/
ONNX backend (Piper/Kokoro voices) → a Discord voice channel (or local device). Our facade's only
contribution is the §F indirection: expose `TTS()`/`PlaySound()` routing through the swappable
delegate fields, and a `Form` identity to host its WPF config tab + folder pickers. Everything else is
internal to Discord-Triggers. This is why making it the official audio stack costs us essentially
nothing beyond G‑1.

### 4. Our own seams (for completeness)

- **The net48↔net10 IPC bridge** (`Fct.Bridge`): not an ACT surface; it is how the satellite
  (legacy plugins + facade) and the net10 host exchange typed domain events. Plugins never see it.
- **`IRawPacketSource`** (`Fct.Abstractions`): the one opt-in escape hatch exposing raw packets to
  net10 consumers / legacy `RegisterNetworkParser`, fed by `RingBufferDataSubscription`.

---

## Net action list (interface work, by priority)

| Priority | Item | Where | Effort |
|---|---|---|---|
| **1** | **G‑1** — add `TTS(string)`/`PlaySound(string)` methods routing to the delegate fields | `Fct.Compat.Act/FormActMain.cs` | trivial (2 methods) |
| **2** | **G‑2** — add `ActGlobals.oFormSpellTimers` + inert `FormSpellTimers` (2 events) | `ActGlobals.cs` + new type | small |
| **3** | **G‑3** — `DpiScale` → `1f` | `FormActMain.cs` | trivial |
| 4 | VERIFY — `BeforeLogLineRead` multicast-field reflection shape; exact "Started" status string before Trig/OP/Hojoring init | facade + host | test |
| 5 | **G‑4** — inert `TraySlider`/`ButtonLayoutEnum` stubs (only if a bind throws) | facade | small, conditional |
| — | Hojoring M4 (full `FormSpellTimers` + `TimerData` family, real `CornerControlAdd/Remove`, `Form` lifecycle members) | facade | large — only if Hojoring is committed |
| — | No work | FFXIV SDK seam (DONE via `WrappedFfxivPlugin`), OverlayPlugin web surface (self-hosted by unmodified OP), Discord audio (covered by G‑1) | — | — |

Everything else in `ACT-INTERFACE-MAP.md` is **DONE** or **STUB-by-design**. The supported-stack
interface surface reduces to three small facade additions (G‑1/G‑2/G‑3) plus verification.
