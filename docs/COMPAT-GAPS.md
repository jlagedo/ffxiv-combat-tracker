# Compat Surface Gaps — phased implementation backlog

Consolidated, verified gap list for making the **unmodified** legacy stack
(`FFXIV_ACT_Plugin` + OverlayPlugin/cactbot + Triggernometry + Discord-triggers) run on our
clean-room ACT facade. Derived from auditing the real sources against the current facade code.

- **Authority:** [`docs/DATA-FLOW.md`](DATA-FLOW.md) is the data-flow + surface contract. This
  file is the *actionable backlog* extracted from it: what the unmodified plugins touch, what the
  facade already provides, and what is still missing — grouped by phase.
- **Code under audit:** `src/Fct.Compat.Act` (facade) and `src/Fct.Parser.Legacy` (plugin wrapper).
- **Consumer evidence:** `reference/overlayplugin/` (OP, file:line), `reference/act-decompiled/`
  (ACT/plugin shapes), Triggernometry/Discord (behaviour — marked *inferred* where no local source).

### How to read

| Column | Meaning |
|---|---|
| **Status** | `DONE` present in facade · `GAP` missing · `STUB` may be a no-op · `VERIFY` present but untested on the live path |
| **Sev** | `BLOCK` plugin won't bind/init · `BREAK` feature silently dead · `MINOR` cosmetic/edge |

### Phase map

| Phase | Goal | Net gap state |
|---|---|---|
| **M0** Engine | real plugin Started, `AddCombatAction`, DPS, `Network_*.log` | **Done** (Slice 1) |
| **M1** OverlayPlugin | discovery + MiniParse + NetworkProcessors against unmodified OP | **Code complete** — 1 VERIFY (live custom-log round-trip) |
| **M2** Triggernometry | host runs no trigger engine; facade just lets the plugin **bind + degrade** | **Minimal compat done** — binding surface present, 1 VERIFY |
| **M3** Discord | trigger/encounter events post on a kill | **Surface present** — all touched members DONE |
| **X** Cross-cutting | model/event shape completeness for any reflecting consumer | a few MINOR shape gaps |

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

**Phantoms removed** (the old backlog listed these; the real source touches neither):
`oFormActMain.CustomTriggerCheck(...)` — does not exist; Triggernometry registers its *own*
`HasCustomTriggers`/`GetCustomTriggers` as internal hooks and reads `CustomTriggers` directly.
`ActGlobals.oFormSpellTimers` / `SpellTimer` — not referenced by Triggernometry **or**
Discord-Triggers; no consumer in the supported stack.

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

## X — Cross-cutting shape completeness

Members any reflecting/binding consumer may touch, independent of phase.

| # | Surface | Consumed by | Status | Sev | Action |
|---|---|---|---|---|---|
| X‑1 | `FormActMain` events `ActLifecycleChanged`, `LogFileRenamed`, `XmlSnippetAdded`, `BeforeClipboardSet`, `UrlRequest` | a plugin `+=` on any of these | **DONE** | MINOR | Declared as bindable no-op publishers on `FormActMain` + delegate/EventArgs types ported from the decompile (`Primitives.cs`). Never fired (no UI/clipboard/XML-share/URL path in the headless host); present so `+=` binds. |
| X‑2 | `LogLineEventArgs.companionLogName` (readonly) + 2nd ctor overload | consumers reading the field | **DONE** | MINOR | Readonly `companionLogName` field + 6‑arg ctor overload added (`Primitives.cs`), matching ACT. 5‑arg ctor defaults it to `string.Empty`. |
| X‑3 | `RestartACT(...)`, `PluginDownload(...)`, `UnZip(...)` | legacy auto-updaters | **STUB** | MINOR | Stub — we do not self-update legacy plugins. `RestartACT` present; add `PluginDownload`/`UnZip` no-ops if a consumer binds them. |

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
