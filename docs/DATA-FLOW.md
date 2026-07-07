# Stack Data Flow & Compat Contract — the legacy contract we reproduce

**This documents the upstream legacy stack, not our target topology.** It is the
authoritative map of how data flows through the real upstream stack today — one shared
process, plugins reaching each other through `ActGlobals` — and the exact integration seams
`Fct.LegacyHost` / `Fct.Compat.Act` must reproduce so the unmodified plugins "just work."
**Our topology is different by design:** each plugin package runs in its own satellite, and
every seam below is reproduced *inside that satellite's facade*, backed by host-routed
streams — the seam's shape is legacy, its data always crosses through the host. Aggregation
truth lives in the host's `Fct.Engine`; each satellite's engine is a parity-gated replica.
Topology, invariants, and mapping: [`ARCHITECTURE.md`](ARCHITECTURE.md) §3c/§5 +
[`ISOLATION-PLAN.md`](ISOLATION-PLAN.md); §8 below maps each seam onto its host pipe.

It is reconstructed from the real sources (two separate decompiles — do not conflate):

- `E:\dev\ACT-decompiled` — **Advanced Combat Tracker only** (ACT's compat surface + aggregation).
- `E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\` — the **FFXIV_ACT_Plugin** decompile (the
  legacy stack we host unmodified; never a source to port parsing logic from).
- `E:\dev\OverlayPlugin` — ngld OverlayPlugin (net48).
- `E:\dev\IINACT` — a working in-process re-host of the same stack (its `NotACT` is the
  independent proof that our from-scratch ACT approach is correct).

> Rule: when a compat detail is in doubt, the decompile is the authority. Match the exact
> signature / string name / property shape there, not an approximation. OverlayPlugin's
> discovery is *stringly-typed reflection* — an approximate facade silently fails to bind.

---

## 0. The one-paragraph model

`FFXIV_ACT_Plugin`, ACT, and OverlayPlugin run **in one OS process, sharing one managed
heap**, with `ActGlobals.oFormActMain` as the global hub every component reaches through.
Combat data leaves the FFXIV plugin by **two parallel channels** — a pipe-delimited
*log-line* stream (file + event) and a typed *SDK* (`IDataSubscription` / `IDataRepository`).
ACT consumes the log lines and aggregates them into encounters/DPS. OverlayPlugin consumes
from **both** plugin channels *and* from ACT's aggregated output — it reflects *through* ACT
to reach the plugin's SDK directly, and reads ACT's `EncounterData.ExportVariables` for DPS.

That one-heap arrangement is the **upstream reality**, not ours. Our job: **host the real
`FFXIV_ACT_Plugin` unmodified (it owns the two channels for free), and build a from-scratch
ACT (`Fct.Compat.Act`) whose shape matches exactly what OverlayPlugin and the other plugins
reflect against** — instantiated **once per satellite**, with the data behind every shape
arriving over the host's routed streams instead of a shared heap (§8).

---

## 1. The big picture

```
ffxiv_dx11.exe
  │ raw TCP packets (Machina: raw socket / Npcap / Deucalion)  +  process memory (sig scan)
  ▼
╔══════════════════ FFXIV_ACT_Plugin  (real, hosted unmodified) ══════════════════╗
║  Network.dll : opcode → typed event   (opcode table from Resource.dll)           ║
║  Memory.dll  : player id / territory / party / target / HP   (read-only scan)    ║
║  Parse.dll   : higher-level shaping                                              ║
║                                                                                  ║
║        one typed event  ── fans out to TWO outputs ──                            ║
║   ┌────────────────────────────────┐   ┌──────────────────────────────────────┐ ║
║   │ (A) Logfile.dll : LogOutput    │   │ (B) Common.dll : the SDK              │ ║
║   │   formats pipe-delimited line  │   │   IDataSubscription  (events)         │ ║
║   │   "21|<t>|<id>|<name>|..."     │   │   IDataRepository    (lookups)        │ ║
║   │   → Network_*.log  +  in-queue │   │   exposed as PROPERTIES named         │ ║
║   │   → ActGlobals...OpenLog()     │   │   "DataSubscription"/"DataRepository" │ ║
║   └───────────────┬────────────────┘   └───────────────┬──────────────────────┘ ║
╚═══════════════════│════════════════════════════════════│════════════════════════╝
                    │ log lines (file/queue)              │  (reflected into directly)
                    ▼                                     │
╔═══════════════ ACT  (we build from-scratch: Fct.Compat.Act) ═══════════════════════╗
║  FormActMain tails the log → regex → MasterSwing                                  ║
║     → AddCombatAction → CombatantData → EncounterData                             ║
║     → EncounterData.ExportVariables / CombatantData.ExportVariables               ║
║                                                                                  ║
║  ActGlobals.oFormActMain  ── THE HUB ──                                           ║
║     ├─ .ActPlugins : List<ActPluginData>   ◄──────── discovery target            ║
║     ├─ .ActiveZone.ActiveEncounter : EncounterData ◄ DPS read target             ║
║     ├─ event BeforeLogLineRead / OnLogLineRead     ◄ log-line event target        ║
║     ├─ event OnCombatStart / OnCombatEnd / Before+AfterCombatAction               ║
║     └─ CustomTrigger eval → TTS()/PlaySound()      (Triggernometry path)          ║
╚═══════════════════│═════════════════════│════════════════════│═══════════════════╝
        log-line events │       SDK events │        aggregated DPS │
                    ▼                 ▼                       ▼
╔═══════════════ OverlayPlugin  (real, hosted unmodified) ═════════════════════════╗
║  FFXIVRepository    → reflect ActPlugins → grab DataSubscription/DataRepository   ║
║  EventSources/      → ACT BeforeLogLineRead  +  IDataSubscription events          ║
║  MiniParseEventSource → read ActiveEncounter + ExportVariables → CombatData JSON  ║
║  NetworkProcessors/ → RegisterNetworkParser (raw opcodes) → custom log lines      ║
║  WSServer           → JSON over ws://127.0.0.1:10501  (/ws, /MiniParse, ...)      ║
╚════════════════════════════════════│═════════════════════════════════════════════╝
                                      ▼
                       cactbot / Next-UI / Browsingway / custom overlays
```

---

## 2. Leg 1 — `FFXIV_ACT_Plugin` produces data (inherited; we host, don't build)

The shipped `FFXIV_ACT_Plugin.dll` is a thin **Costura.Fody** wrapper that unpacks 9
sub-assemblies at load. Roles:

| Assembly | Role |
|---|---|
| `FFXIV_ACT_Plugin.Common` | **Public SDK** — `IDataSubscription`, `IDataRepository`, `IActWrapper`, models. The only stable third-party surface. |
| `FFXIV_ACT_Plugin.Config` | Settings persistence / config mediator |
| `FFXIV_ACT_Plugin.Logfile` | `LogOutput` — formats events → pipe-delimited lines → `Network_*.log` |
| `FFXIV_ACT_Plugin.Memory` | Read-only process memory scan (player id, zone, party, target, HP) |
| `FFXIV_ACT_Plugin.Network` | raw packets → typed events; opcode dispatch table |
| `FFXIV_ACT_Plugin.Parse` | higher-level parsing (action effects, encounter shaping) |
| `FFXIV_ACT_Plugin.Resource` | ~8 MB embedded tables — opcodes per region, action/skill metadata, world list |
| `Machina` / `Machina.FFXIV` | generic TCP capture + FFXIV layer + Deucalion IPC client |
| `Newtonsoft.Json` | config JSON |

### 2.1 Load + lifecycle

ACT calls `FFXIV_ACT_Plugin.InitPlugin(TabPage, Label)` (the `IActPluginV1` contract). The
plugin then, via `Microsoft.MinIoC`:

```csharp
VerifyAssemblyVersions();
ConfigureIOC();
OpcodeManager.Instance.SetRegion(GameRegion.Global);
_iocContainer.Resolve<ResourceManager>().LoadResources();
DataSubscription = _iocContainer.Resolve<DataSubscription>();   // ← public property
DataRepository  = _iocContainer.Resolve<IDataRepository>();     // ← public property
_dataCollection = _iocContainer.Resolve<DataCollection>();
_dataCollection.StartMemory();   // starts 3 background threads
```

`DataCollection.StartMemory()` starts three plugin-owned threads:

| Thread | Job |
|---|---|
| `LogOutput.ScanThread` | drains the formatted-log-line queue, appends to disk, notifies ACT |
| `ScanMemory.ScanThread` | periodic read-only memory scan for state packets don't carry |
| `MonitorNetwork.ScanThread` | supervisor — restarts capture if the game exits/respawns or goes silent |

### 2.2 Data acquisition — two sources

- **Network capture** (`Machina` + `Machina.FFXIV`): three selectable modes — **raw socket**
  (admin), **WinPcap/Npcap** (driver), **Deucalion** (injected DLL hooks `recv`, pipes bytes
  over a named pipe). All three yield TCP segments → Machina reassembles → `Network.dll`
  looks up the opcode in the region table (`Resource.dll`) → dispatches to a typed handler
  (one class per packet type).
- **Memory reading** (`Memory.dll`): signature-scans the FFXIV process for stable structures
  (own player id, territory, party, target, HP/MP). **Strictly read-only** — no
  `WriteProcessMemory`, no `Process.Start`, no registry writes (one AV-name read only).

### 2.3 The two outputs (the integration boundary)

Each decoded packet becomes a typed event (`FFXIV_ACT_Plugin.Common.Models`) that the plugin
emits **two ways**:

**(A) Log-line path — `Logfile.dll` / `LogOutput`.** The event is formatted as a
pipe-delimited line prefixed with a numeric `LogMessageType`, queued, and appended to:

```
%APPDATA%\Advanced Combat Tracker\FFXIVLogs\Network_<version>_<yyyyMMdd>.log
```

`LogOutput.ConfigureLogFile()` then drives ACT's normal log-tailing via the `IActWrapper`
shim around `ActGlobals.oFormActMain`:

```csharp
_actWrapper.LogFileFilter = "Network_*.log";
_actWrapper.LogFilePath   = _logFileName;
_actWrapper.OpenLog(GetCurrentZone: true, GetCharNameFromFile: false);
```

To ACT this is indistinguishable from importing any other game's combat log.

**The full `LogMessageType` set** (`FFXIV_ACT_Plugin.Logfile.LogMessageType`) — the line-type
prefixes our facade and the parser must round-trip:

```
0  ChatLog            20 StartsCasting     30 StatusRemove     40 ChangeMap
1  Territory          21 ActionEffect      31 Gauge            41 SystemLogMessage
2  ChangePrimaryPlayer 22 AOEActionEffect  32 World            42 StatusList3
3  AddCombatant       23 CancelAction      33 Director         43 StatusListForay3
4  RemoveCombatant    24 DoTHoT            34 NameToggle       249 Settings
11 PartyList          25 Death             35 Tether           250 Process
12 PlayerStats        26 StatusAdd         36 LimitBreak       251 Debug
                      27 TargetIcon        37 EffectResult     252 PacketDump
                      28 WaymarkMarker     38 StatusList       253 Version
                      29 SignMarker        39 UpdateHp         254 Error
```

OverlayPlugin layers **custom** line IDs on top via `RegisterCustomLogLine` (256+): 257
MapEffect, 258 FateControl/FateDirector, 259 CEDirector, 260 InCombat, plus Combatant, RSV,
ActorCastExtra, AbilityExtra, ContentFinderSettings, NpcYell, BattleTalk2, Countdown(+Cancel),
ActorMove, ActorSetPos, SpawnNpcExtra, ActorControl(Self)Extra.

**(B) Typed SDK path — `Common.dll`.** The only surface intended for other plugins.
Exposed off the plugin instance as **properties named exactly `DataSubscription` and
`DataRepository`** (this exact naming is load-bearing — see §4).

`IDataSubscription` (full event set):

```csharp
public interface IDataSubscription {
    event NetworkReceivedDelegate   NetworkReceived;    // (string conn, long epoch, byte[] msg)
    event NetworkSentDelegate       NetworkSent;
    event CombatantAddedDelegate    CombatantAdded;
    event CombatantRemovedDelegate  CombatantRemoved;
    event PrimaryPlayerDelegate     PrimaryPlayerChanged;
    event ZoneChangedDelegate       ZoneChanged;        // (uint zoneID, string zoneName)
    event PlayerStatsChangedDelegate PlayerStatsChanged;
    event PartyListChangedDelegate  PartyListChanged;   // (ReadOnlyCollection<uint>, int size)
    event LogLineDelegate           LogLine;            // (uint EventType, uint Seconds, string line)
    event ParsedLogLineDelegate     ParsedLogLine;
    event ProcessChangedDelegate    ProcessChanged;     // (Process)
}
```

`IDataRepository` (full lookup set):

```csharp
public interface IDataRepository {
    Language GetSelectedLanguageID();
    Process  GetCurrentFFXIVProcess();
    IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType);
    uint     GetCurrentTerritoryID();
    uint     GetCurrentPlayerID();
    ReadOnlyCollection<Combatant> GetCombatantList();
    Player   GetPlayer();
    DateTime GetServerTimestamp();
    string   GetGameVersion();
    bool     IsChatLogAvailable();
    string[] GetAntiVirusNames();
    byte     GetGameRegion();
}
```

> We host the real plugin → both channels exist for free. We never reimplement opcodes,
> memory scanning, or Machina. A game patch ships a new `FFXIV_ACT_Plugin.dll`; nothing
> on our side changes.

---

## 3. Leg 2 — ACT aggregates into encounters (we build: the `Fct.Aggregation` engine — authority in the host's `Fct.Engine`, a parity-gated replica behind each satellite's `Fct.Compat.Act` facade)

ACT loaded the plugin via `IActPluginV1.InitPlugin(TabPage, Label)` and recorded it in
`ActGlobals.oFormActMain.ActPlugins`. When log lines arrive, `FormActMain` runs the
encounter pipeline:

```
log line ──regex──► MasterSwing ──AddCombatAction──► CombatantData ──► EncounterData
                                                          └─ reverse action ─► victim CombatantData
```

### 3.1 The aggregation engine (`Fct.Aggregation`, from-scratch, behavior reproduced from the decompile)

The engine types live in `Fct.Aggregation` (one strong-named identity, multi-targeted `net48;net10`).
The net48 `Fct.Compat.Act` facade references it and **type-forwards** the engine types into the
`Advanced Combat Tracker` identity so precompiled plugins resolve; the net10 `Fct.Compat.Shim.ActFacade`
references the same engine. The engine reads its ACT-core flags (`charName`/`blockIsHit`/`restrictToAll`)
through accessor delegates each facade's `ActGlobals` wires to its real static fields.

- `MasterSwing` — one action/damage event (attacker, victim, attackType, damage `Dnum`,
  swingType, critical, special, time/timeSorter). Strings interned on add.
- `CombatantData` — per-combatant buckets (`DamageTypeData`: outgoing/incoming
  damage/healing/etc.), lazily-cached metrics. Key formula:
  `EncDPS => Damage / Parent.Duration.TotalSeconds`, `Damage => Items[OutgoingDamage].Damage`.
- `EncounterData` — per-encounter aggregate over allies. `DPS => Damage / Duration.TotalSeconds`;
  `Damage => GetAllies().Sum(t => t.Damage)`. `GetAllies()` is an iterative graph-walk:
  seed with your character, propagate ally/enemy values across combatants until the set
  stabilizes, partition by sign.
- `ZoneData` — holds `ActiveEncounter`; `ChangeZone` / `SetEncounter` / `EndCombat` drive the
  lifecycle. `InCombat` gates `AddCombatAction` (throws if false).

> **Bit-for-bit requirement.** The `Fct.Aggregation` engine's `EncounterData`/`CombatantData`/
> `MasterSwing`/`AttackType` aggregation reproduces the real ACT binary on captured combat
> (differential tests in `docs/TESTING.md`); `Fct.Compat.Act` carries the `Advanced Combat Tracker`
> identity and type-forwards these engine types into it. The lazy `xxxCached` flag pattern, the
> `GetAllies` graph-walk, the `Dnum` semantics, and the `ExportVariables`/`ColumnDefs` formatter
> tables all match.

### 3.2 The MasterSwing boundary

For FFXIV, **the `FFXIV_ACT_Plugin` produces the encounter input; ACT does not re-derive
damage from raw packets.** Two facts pin the split:

1. The canonical path the decompile documents is **log-line → ACT regex → MasterSwing**
   (`FormActMain` owns the regex; `OpenLog` drives the tail).
2. IINACT's working `NotACT` confirms the split in practice: its `ParseRawLogLine` only fires
   log-line events + writes the file — it **never** calls `AddCombatAction`; the DPS engine is
   fed by the plugin via `AddCombatAction`/`SetEncounter` directly. Either way, **damage byte
   decode lives in the plugin, not in ACT.**

Consequence for us: we reproduce the *aggregation* and the *event/encounter lifecycle*; we do
not reimplement damage decode at all. Decode is the plugin's job, permanently — reimplementing
the FFXIV parser natively is an explicit non-goal.

### 3.3 The hub surface other plugins bind to — `ActGlobals.oFormActMain`

Reproduce faithfully (signatures verified against `E:\dev\ACT-decompiled`). `FormActMain`
**is a real WinForms `Form`** — not a POCO hub. Consumers poll its form lifecycle and marshal
onto its UI thread; the facade must be a live `Form` with a running message loop.

- **Form lifecycle + threading:** `FormActMain : Form`. OverlayPlugin **gates its entire
  phase-2 init** on `InitActDone && Handle != IntPtr.Zero` (`PluginMain.cs:246`), guards
  shutdown with `IsActClosing` (`:459`), and marshals nearly every ACT call through
  `InvokeRequired` / `Invoke(Action)`. A non-`Form` or handleless facade never initializes.
- **Plugin registry:** `ActPlugins : List<ActPluginData>`. Each `ActPluginData` exposes
  `pluginObj` (the plugin instance), `lblPluginTitle.Text`, `lblPluginStatus.Text`,
  `cbEnabled.Checked`, `pluginFile`, and `tpPluginSpace` (the plugin's `TabPage` — OP reads it
  at `FFXIVRepository.cs:322`). `oFormActMain.PluginGetSelfData(plugin)` resolves a plugin to
  its own `ActPluginData` (Discord-triggers/Triggernometry use it to find their install dir).
  > **Status-string contract:** Triggernometry discovers the FFXIV plugin by matching
  > `lblPluginStatus.Text` against `"FFXIV_ACT_Plugin Started."` / `"FFXIV Plugin Started."` and
  > `lblPluginTitle.Text` starting `"FFXIV_ACT_Plugin"`. The facade must set these exact strings.
- **Ingestion:** `ParseRawLogLine(bool isImport, DateTime, string)`; `AddCombatAction(MasterSwing)`
  (+ multi-arg overload); `SetEncounter`/`EndCombat`/`ChangeZone` (encounter lifecycle, driven
  directly by Triggernometry); `OpenLog(bool, bool)`; `LogFileFilter`, `LogFilePath`.
- **Events:** `BeforeLogLineRead`/`OnLogLineRead` (`LogLineEventArgs`, **mutable** `logLine`,
  readonly `companionLogName`), `OnCombatStart`/`OnCombatEnd` (`CombatToggleEventArgs`),
  `BeforeCombatAction`/`AfterCombatAction` (`CombatActionEventArgs`, `cancelAction`),
  `LogFileChanged`. The real `FormActMain` also declares `ActLifecycleChanged`, `LogFileRenamed`,
  `XmlSnippetAdded`, `BeforeClipboardSet`, `UrlRequest`, `UpdateCheckClicked` — these must exist
  as bindable members (a plugin's `+=` `MissingMethodException`s if absent) even when unused.
- **State:** `ActiveZone.ActiveEncounter`, `ZoneList` (+ `ZoneList[i].Items`), `CurrentZone`,
  `InCombat`, `LastEstimatedTime`/`LastKnownTime`, `AppDataFolder` (`DirectoryInfo` — config/bridge
  path resolution for OP, Triggernometry, Discord). `ActGlobals.oFormImportProgress` (nullable;
  MiniParse reads `?.Visible` to suppress DPS pushes mid-import — the **field must exist** or OP
  `MissingFieldException`s).
- **Models + static tables:** `EncounterData` (incl. `GetAllies()`, `LogLines : List<LogLineEntry>`,
  `Tags`, `EncId`/`EndTime`/`Active`), `CombatantData`, `MasterSwing`, `Dnum`, `LogLineEntry`, and
  the static `EncounterData.ExportVariables` / `CombatantData.ExportVariables` / `ColumnDefs` plus
  `CombatantData`'s static damage-type tables (`OutgoingDamageTypeDataObjects`,
  `DamageTypeDataOutgoingDamage`, …). `GetTextExport(EncounterData, …)` overloads (encounter text
  export — Triggernometry/Discord).
- **Triggers/alerts** *(M2 — Triggernometry; not yet built)*: `CustomTrigger` (regex; `SoundType`
  1=Beep/2=Sound/3=TTS), `CustomTriggers` / `ActiveCustomTriggers`, `CustomTriggerCheck()`,
  `SpellTimer` + `ActGlobals.oFormSpellTimers` (must be non-null for timers to fire), corner
  notifications `CornerControlAdd`/`CornerControlRemove`, `PlaySound(string)`, `TTS(string)`.
  `PlaySoundMethod`/`PlayTtsMethod` are **settable delegate properties** — Discord-triggers
  *replaces* them to reroute audio through its bridge, so they must be publicly assignable, not
  just invokable.
- **Housekeeping/update:** `WriteExceptionLog(...)` (error log; used widely). `RestartACT(...)`,
  `PluginDownload(...)`, `UnZip(...)` are touched by the legacy auto-updaters — **safe to stub**
  (we do not self-update the legacy plugins).
- **Identity:** the facade assembly is named/strong-named `Advanced Combat Tracker`
  (`PublicKeyToken=a946b61e93d97868`); supplied via `AppDomain.AssemblyResolve`. Likewise
  `FFXIV_ACT_Plugin.Common` identity for the SDK types.

---

## 4. Leg 3 — OverlayPlugin consumes (real, hosted unmodified)

OverlayPlugin uses **four distinct taps**. All file refs are under
`E:\dev\OverlayPlugin\OverlayPlugin.Core\`.

### 4.1 Discovery — reflect *through* ACT to find the plugin (`Integration/FFXIVRepository.cs`)

```csharp
// GetPluginData(): scan ACT's plugin list, match by TITLE STRING
ActGlobals.oFormActMain.ActPlugins.FirstOrDefault(plugin =>
    plugin.cbEnabled.Checked &&
    plugin.pluginObj != null &&
    plugin.lblPluginTitle.Text.StartsWith("FFXIV_ACT_Plugin"));

// Then pull the SDK off the instance BY REFLECTED PROPERTY NAME:
repository   = (IDataRepository)  FFXIV.pluginObj.GetType()
                  .GetProperty("DataRepository").GetValue(FFXIV.pluginObj);
subscription = (IDataSubscription)FFXIV.pluginObj.GetType()
                  .GetProperty("DataSubscription").GetValue(FFXIV.pluginObj);
```

**This is the single most fragile seam.** It depends on: (a) `ActPlugins` entries shaped with
`cbEnabled.Checked` / `pluginObj` / `lblPluginTitle.Text`; (b) the title starting with the
literal `"FFXIV_ACT_Plugin"`; (c) the plugin instance exposing properties literally named
`DataSubscription` / `DataRepository`. Because we host the **real** plugin, (c) is free — but
only if the real instance is what sits in `pluginObj`. Our facade owns (a) and (b).

### 4.2 Live event subscription (`Integration/FFXIVRepository.cs`, `EventSources/`)

Direct to the plugin SDK:

```csharp
subscription.LogLine          += handler;   // RegisterLogLineHandler
subscription.NetworkReceived  += handler;   // RegisterNetworkParser  (raw bytes)
subscription.PartyListChanged += handler;
subscription.ZoneChanged      += handler;
subscription.ProcessChanged   += handler;
```

And to **ACT's** event (in `FFXIVOptionalEventSource`):

```csharp
ActGlobals.oFormActMain.BeforeLogLineRead += LogLineHandler;
// LogLineHandler splits args.originalLogLine on '|', switches on (LogMessageType)args.detectedType
```

In our topology, `Before/OnLogLineRead` above is the **sole** `RawLogLine` source, and it carries
every line type. `Fct.LegacyHost/BridgeForwarder` (`BridgeForwarder.cs:82-93`) subscribes the
producer facade's `FormActMain.OnLogLineRead` once and forwards `args.logLine` **verbatim** (both
frame fields), stamped from the line's own parsed time; the frame's `LogMessageType` is extracted
from the leading `NN|` key (`BridgeForwarder.cs:337-346`), never from `args.detectedType`. The SDK
`subscription.LogLine` tap above is not reproduced on the producer side — no v1 consumer binds it —
so type 00 (ChatLog), 01/02/40 (zone/player/map), every combat line, and 249/250/253 all cross
through this one tap, not a second parallel path.

### 4.3 DPS / MiniParse (`EventSources/MiniParseEventSource.cs`) — reads ACT's aggregate

```csharp
var allies = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.GetAllies();

// encounter dict: run every registered formatter
foreach (var kv in EncounterData.ExportVariables)
    encounterDict[kv.Key] = kv.Value.GetExportString(
        ActGlobals.oFormActMain.ActiveZone.ActiveEncounter, allies, "");

// per-combatant dict (parallel over allies)
foreach (var kv in CombatantData.ExportVariables)
    valueDict[kv.Key] = kv.Value.GetExportString(ally, "");
```

Emitted as the `CombatData` event:

```json
{ "type": "CombatData",
  "Encounter":  { "encdps": "...", "damage": "...", "DURATION": "...", ... },
  "Combatant":  { "<PlayerName>": { "encdps": "...", "damage%": "...", ... }, ... },
  "isActive":   "true" }
```

So the DPS overlay consumes **ACT's** `ExportVariables` output — i.e. *our* engine output
(the replica in OverlayPlugin's own satellite, replaying the host-routed swing stream) — not
the plugin directly. This is why the full ExportVariables key set must match.

Before each push MiniParse checks `ActGlobals.oFormImportProgress?.Visible` (`:189`) to skip
updates during a log import — the field must exist on our `ActGlobals` (nullable is fine).

The full `ACT_UIMods`-contributed key set (`Job`, `ParryPct`, `BlockPct`, `IncToHit`,
`OverHealPct`, `DirectHitPct`/`DirectHitCount`, `CritDirectHitPct`/`CritDirectHitCount`,
`Last10/30/60DPS` on both the combatant and encounter dictionaries, `CurrentZoneName`) registers
once in the shared engine binary — `EngineTables.Install()` (`src/Fct.Aggregation/EngineTables.cs:19-26`),
which calls `CombatTables.Setup()` for the ACT-core keys first and then adds these FFXIV-specific
ones — never in `CombatTables.Setup()` itself, so the ACT-core parity fixtures (which stand a
`FormActMain` up through `CombatTables.Setup()` only) never observe them. `EncounterProjector`
(`src/Fct.Engine/EncounterProjector.cs:55-69`) enumerates the same registered
`EncounterData.ExportVariables`/`CombatantData.ExportVariables` dictionaries into
`EncounterSnapshot`/`CombatantMetrics.ExportVariables` for the modern API.

### 4.4 Raw opcodes (`NetworkProcessors/`)

For packets ACT/the plugin don't surface as line types, each processor:

```csharp
ffxiv.RegisterNetworkParser(MessageReceived);   // → IDataSubscription.NetworkReceived
ffxiv.RegisterProcessChangedHandler(ProcessChanged);
// MessageReceived: parse bytes via Machina structs for the current region,
//                  then logWriter(line, serverTime) — writes a synthetic custom log line
//                  (ID 256+) back INTO the plugin log system → round-trips as a log-line event.
```

This is the path our `IRawPacketSource` escape hatch must feed: `(connection, epoch, byte[])`.

The synthetic-line `logWriter` is **not** an ACT call — OverlayPlugin reaches the plugin's own
log system by reflection: it reads the plugin instance's **private `_iocContainer` field**
(`FFXIVRepository.cs:458`), `GetService("FFXIV_ACT_Plugin.Logfile.ILogOutput")` (`:498`), and
writes through `ILogOutput`. Because we host the real plugin, this works for free — but only if
the wrapper keeps `_iocContainer` reflection-reachable and `ILogOutput.WriteLine` live. Custom
lines 256+ (MapEffect 257, CEDirector 259, InCombat 260, …) are silent if this seam breaks.

### 4.5 Transport (`WebSocket/WSServer.cs`, Fleck)

`ws://127.0.0.1:10501` (default), routed by path:

| Path | Handler | Audience |
|---|---|---|
| `/ws` | `SocketHandler` | modern OverlayPlugin API |
| `/MiniParse` | `LegacyHandler` | legacy cactbot |
| `/BeforeLogLineRead` | `LegacyHandler` | raw log-line stream |

`EventSource → DispatchEvent(JObject) → EventDispatcher → receiver.HandleEvent → conn.Send(json)`.

### 4.6 Environment scalars OverlayPlugin reads outside the SDK

Two `IDataRepository` scalars listed in §2.3B are not what OverlayPlugin actually reads for its
own purposes:

- **Region.** `FFXIVRepository` never calls `GetGameRegion()`; it resolves
  `Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.GameRegion` by reflection on its own loaded
  copy of `Machina.FFXIV` (`OverlayPlugin.Core/Integration/FFXIVRepository.cs:384-403`) — Machina's
  own opcode-table selection, independent of the plugin's SDK property. Machina defaults that
  singleton to `Global`, already correct for the primary audience; only a KR/CN client needs it set
  explicitly.
- **Server timestamp.** `GetServerTimestamp()` is populated **only** by the plugin's own live
  process-memory scan — it is `DateTime.MinValue` with no live game process, never derived from the
  log.

Our consumer stand-in reproduces both from the forwarded environment state rather than a local
scan: `MachinaRegionBridge.TrySetRegion` (`src/Fct.Parser.Legacy/MachinaRegionBridge.cs:31`)
reflectively invokes `OpcodeManager.Instance.SetRegion(...)` with the forwarded region whenever
`Machina.FFXIV` is already loaded in-process (best-effort, KR/CN only, never blocking);
`ConsumerDataRepository.GetServerTimestamp()` (`src/Fct.Parser.Legacy/ConsumerDataSurface.cs:187`)
serves an offset-corrected `DateTime.UtcNow + ServerClockOffset` instead of a local scan.

---

## 5. What we build vs. inherit (the scope, exactly)

| Seam | Owner | Notes |
|---|---|---|
| packets/memory → log lines + `IDataSubscription`/`IDataRepository` | **inherit** | real `FFXIV_ACT_Plugin`, hosted unmodified in `Fct.LegacyHost` |
| log line → regex → `MasterSwing` → `CombatantData` → `EncounterData` | **build** | the `Fct.Aggregation` engine (referenced by `Fct.Compat.Act`), bit-for-bit vs ACT |
| `EncounterData.ExportVariables` / `CombatantData.ExportVariables` formatters | **build** | full key set; MiniParse reads these |
| `ActGlobals.oFormActMain` hub (events, state, `ActPlugins`, `ActiveZone`) | **build** | the surface every plugin binds to |
| `ActPluginData` shape (`pluginObj`, `lblPluginTitle.Text`, `cbEnabled.Checked`) | **build** | exact shape FFXIVRepository reflects against |
| `DataRepository` property + `_iocContainer` field on the plugin instance | **inherit** | `pluginObj` is `Fct.Parser.Legacy.WrappedFfxivPlugin`, which forwards both to the real instance unchanged |
| `DataSubscription` property | **interpose** | the wrapper returns `RingBufferDataSubscription` — funnels the real plugin's events through one bounded ring + single dispatch thread, replacing the plugin's per-subscriber `BeginInvoke` fan-out. Same `FFXIV_ACT_Plugin.Common` identity (unified via `AssemblyResolve`) so OverlayPlugin's cast is our type. `PluginGetSelfData` resolves the wrapper back to the real plugin via `IActPluginAlias` so the real plugin still finds its own folder. |
| `RegisterNetworkParser` / `NetworkReceived` raw-byte pass-through | **build (hatch)** | `IRawPacketSource` on the ring dispatcher; OverlayPlugin `NetworkProcessors` depend on it. OverlayPlugin subscribes packet capture only once a live FFXIV process appears. |
| OverlayPlugin WS server + cactbot + Triggernometry + Discord | **inherit** | hosted unmodified; they self-host on the surfaces above |
| assembly identity (`Advanced Combat Tracker` token a946…) | **build** | facade strong-name + `AssemblyResolve` |

The "build" rows are the **facade surface**, instantiated inside each net48 satellite
(`Fct.LegacyHost` / `Fct.Compat.Act`) next to the one plugin it hosts. The **calculation
authority is the net10 host**: the parser satellite forwards the full pre-aggregation
`MasterSwing` stream + encounter lifecycle over the bridge, `Fct.Engine` aggregates it as
the single source of truth, and each consumer satellite's facade replays the same routed
stream through its `Fct.Aggregation` replica (the identical multi-targeted binary,
parity-gated) to serve the plugin's synchronous reads. See `docs/ARCHITECTURE.md` §3b–§4
for the runtime split and `docs/ISOLATION-PLAN.md` for the invariants.

---

## 6. Acceptance — the stack "works" when

1. **Engine (M0):** real `FFXIV_ACT_Plugin` reaches *Started*; `AddCombatAction` count > 0;
   encounters/DPS appear; `Network_*.log` written. Numbers match real ACT on a fight.
2. **OverlayPlugin discovery (M1):** `FFXIVRepository.GetPluginData()` finds our `ActPluginData`
   (title starts `FFXIV_ACT_Plugin`, `pluginObj` = real instance), and
   `GetProperty("DataSubscription")` / `GetProperty("DataRepository")` resolve non-null.
3. **MiniParse (M1):** `ActiveZone.ActiveEncounter` + `ExportVariables` produce a `CombatData`
   JSON whose `encdps`/`damage`/per-combatant values match ACT; cactbot raidboss/timeline runs.
4. **NetworkProcessors (M1):** `RegisterNetworkParser` receives raw bytes; custom lines 257–260
   emit; MapEffect/CEDirector overlays react.
5. **Triggernometry (M2):** `CustomTrigger` regex matches log lines; timers + TTS/sound fire.
6. **Discord (M3):** trigger/encounter events post to Discord on a kill.

> Validation harness: `tools/mass-compare/` + `tools/act-oracle/` (plus the satellite's
> `ParseOracle`/`--mass-*` modes) run captured `Network_*.log`s
> through both our engine and the reference, asserting `MasterSwing`/`ExportVariables` parity.
> See `docs/TESTING.md` (Differential compat).

---

## 7. Precedent — IINACT (`E:\dev\IINACT`)

IINACT solves the *same* problem with the opposite tradeoff: it runs **in-process** as a
Dalamud plugin on net10, and gets there by **modifying** the ecosystem — it binary-patches
`FFXIV_ACT_Plugin.dll` and forked/ported OverlayPlugin to net10. Its `NotACT` project is a
from-scratch ACT that impersonates the `Advanced Combat Tracker` identity — independent
confirmation that our `Fct.Compat.Act` approach is correct, and a working reference for the
aggregation engine. Where IINACT reaches into plugin internals (e.g. reading
`FfxivPlugin._dataCollection._logOutput._LogQueue` by reflection) it can pin/patch the DLL;
**we cannot** — our unmodified-drop-in directive means we bind only against the documented
SDK + the reflected ACT shape above. Full comparison context: this is the same stack, hosted
two different ways.

---

## 8. Seam → host pipe (how each upstream coupling is routed in the isolated topology)

Upstream, every seam above is a shared-heap shortcut. In our topology each one is a
**host-routed pipe** projected back into legacy shape by the facade inside the consuming
satellite (phases: [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)):

| Upstream coupling (this doc) | Producer side | Host pipe | Consumer-satellite projection |
|---|---|---|---|
| log-line events (§2.3A, §4.2) | producer facade's `OnLogLineRead` tap (`BridgeForwarder`) — one tap, every line verbatim → `RawLogLine` frames | typed bus fan-out | `Before/OnLogLineRead` re-raised; stand-in `IDataSubscription.LogLine` |
| one-shot line-state late-join priming (§4.2) | `LastLineCache` (`src/Fct.Host/Hosting/LastLineCache.cs`) caches the last-seen verbatim line per one-shot type (`01/02/12/40/249/250/253`), keyed on the frame's typed `LogMessageType` | `rawlog` prime replay in ACT emission order — `253/249/250 → 01 → 02 → 40 → 12` (`SatelliteHost.BuildPrimeEvents`) | late `Before/OnLogLineRead` subscriber converges on the cached lines without waiting for a live re-send |
| `MasterSwing` → encounters → `ExportVariables` (§3) | parser facade tap → `CombatSwing` + lifecycle frames | `Fct.Engine` (the truth) + fanned swing stream; the plugin-contributed `ACT_UIMods` keys (§4.3) register once in `EngineTables.Install()`, the same shared binary every replica runs | local `Fct.Aggregation` replica → synchronous `ActiveZone.ActiveEncounter` reads (§4.3) |
| SDK discovery by reflection (§4.1) | — | — | synthetic parser stand-in in `ActPlugins` (exact title/status/property shape) |
| `IDataRepository` polls (§2.3B) | parser satellite publishes fixed-rate repository snapshots + dictionaries + PID | snapshot stream (+ `IGameSnapshot` priming) | local mirror answers `Get*` synchronously |
| environment/party-size state (§4.6) | `SessionStateChanged` (version/language/region/clock-offset/chat-log) forwarded on bind + relog; `PartySize` rides the `PARTY` frame atomically with the member list | `repository`/`zoneparty` stream fold — `IGameSnapshot.Client` / `PartySnapshot.Size` (+ priming to late joiners) | local mirror serves the forwarded scalars; region is additionally pushed into Machina's `OpcodeManager` (`MachinaRegionBridge`, KR/CN only) |
| raw packets / `RegisterNetworkParser` (§4.4) | parser facade tap → `RawPacketReceived` frames | raw-packet fan-out (opt-in) | stand-in `NetworkReceived`/`NetworkSent` |
| custom lines 256+ via `_iocContainer`/`ILogOutput` (§4.4) | consumer facade routes `WriteLine` to the host | `IRawLogLineEmitter` write-back → fanned to all log-line subscribers (incl. origin) | stand-in `_iocContainer` seam |
| TTS / `PlaySound`, `PlayTtsMethod` hijack (§3.3) | any facade → audio frames | `IAudioOutput` routing; delegate-slot assignment registers a terminal sink | facade slots invoke the hijacker's delegate in its own satellite |
| named callbacks (Triggernometry) | facade register/invoke | `IPluginRegistry` over the control channel | facade re-exposes ACT's callback surface |

The WebSocket seam (§4.5) needs no pipe: OverlayPlugin's Fleck server is already
cross-process by nature — cactbot and other WS consumers connect to its satellite unchanged.
