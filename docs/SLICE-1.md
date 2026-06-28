# Slice 1 — Load real plugins in the two-process host

Goal: run the **real, unmodified** FFXIV_ACT_Plugin + OverlayPlugin in the two-process
(net10 Avalonia host + net48 satellite) topology, with the legacy plugin UI embedded in
the Avalonia window.

Decisions taken (this slice): **two-process** topology; real DLLs from the local ACT
install; **full live capture** as the acceptance bar.

---

## Confirmed facts (verified on this machine)

### Binary locations
- **FFXIV_ACT_Plugin.dll** — `%APPDATA%\Advanced Combat Tracker\Plugins\FFXIV_ACT_Plugin.dll`
  (2.9 MB, current; assembly `FFXIV_ACT_Plugin, 3.0.2.3`, strong-named token `9f740ac505d6bc50`).
- **ACT install** — `E:\dev\Advanced Combat Tracker\` (`Advanced Combat Tracker.exe`
  `3.8.5.288`, strong-named token **`a946b61e93d97868`**).
- **OverlayPlugin** — `E:\dev\Advanced Combat Tracker\OverlayPlugin-0.16.5\`
  (`OverlayPlugin.dll` thin loader, version 0.18.2; real code in `libs\OverlayPlugin.Core.dll`,
  0.60 MB). **This build is from 2022 — old.** It references `Advanced Combat Tracker,
  3.4.7.268`. Its bundled opcodes are stale, so OverlayPlugin's *own* network parsing
  (custom log lines / cactbot raidboss network triggers) may not match the current game —
  but core combat data flows through ACT's aggregation, not OverlayPlugin's opcodes, so
  MiniParse DPS is unaffected. Building a current OverlayPlugin from `reference/overlayplugin`
  is the alternative if cactbot network triggers are needed.

### Assembly identity — the binding constraint
Both plugins reference ACT by **strong name** with `PublicKeyToken=a946b61e93d97868`:
- FFXIV_ACT_Plugin → `Advanced Combat Tracker, 3.8.5.288, PublicKeyToken=a946b61e93d97868`
- OverlayPlugin.Core → `Advanced Combat Tracker, 3.4.7.268, PublicKeyToken=a946b61e93d97868`

We do **not** have ACT's private signing key, so a facade cannot satisfy this reference
through normal binding. **Solution (IINACT-proven):** hook
`AppDomain.CurrentDomain.AssemblyResolve` in the satellite — in .NET Framework the loader
uses whatever assembly the handler returns **without re-checking strong name or version**.
Requirements:
- The facade assembly is named `Advanced Combat Tracker` (output `Advanced Combat Tracker.dll`).
- The **real** `Advanced Combat Tracker.exe` must be kept OUT of the satellite's probe path
  so normal binding fails and the resolve hook fires.
- Strong-name the facade with **our own** key (satisfies any signed→signed rule; the token
  is not matched on the resolve path).

### Combat data flow (gates facade scope)
- FFXIV_ACT_Plugin does its **own** packet→MasterSwing parsing and calls
  `ActGlobals.oFormActMain.AddCombatAction(MasterSwing)` directly (via its `ACTWrapper`),
  plus `SetEncounter(...)` and `ChangeZone`/`ChangeTerritory`.
  Evidence: `…\ffxiv_act_plugin.parse\FFXIV_ACT_Plugin.Parse\ReportCombatData.cs`
  (AddCombatAction at lines 97, 112, 196, 247, 265, 292, 307, 360, 374; SetEncounter ~513).
- ACT performs the **aggregation** (`MasterSwing → CombatantData → EncounterData → DPS`)
  in `FormActMain.ThreadAfterCombatAction` (~18831–18910); models in
  `Models\{EncounterData,CombatantData,MasterSwing,ZoneData,Dnum}.cs`.
- OverlayPlugin MiniParse reads **ACT's aggregates** only:
  `ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.GetAllies()` +
  `CombatantData.ExportVariables` formatters.
  Evidence: `OverlayPlugin.Core\EventSources\MiniParseEventSource.cs` (241, 321–351, 410–413).

**Consequence:** live-DPS in an overlay requires reproducing ACT's full combat-aggregation
engine, not just plugin loading.

### Plugin init requirements (facade surface)
- `ActGlobals.oFormActMain`: `GetVersion()` (≥ 3.8.0.281), `AppDataFolder`, `ActPlugins`,
  `InitActDone`, `Handle` (valid HWND), `IsActClosing`, `Invoke`, `LogFilePath`/`LogFileFilter`/
  `OpenLog(bool,bool)`, `NotificationAdd`, `WriteExceptionLog`, `RestartACT`, `PluginGetSelfData`.
- `ActPluginData` with **real WinForms** `cbEnabled` (CheckBox, `.Checked=true`),
  `lblPluginTitle` (Label `.Text="FFXIV_ACT_Plugin"`), `tpPluginSpace` (TabPage),
  `pPluginInfo` (Panel), `lblPluginStatus` (Label), `pluginFile` (FileInfo → real path),
  `pluginObj`.
- FFXIV_ACT_Plugin requires `Environment.Is64BitProcess`; unpacks bundled DLLs via Costura
  (`AppDomain.AssemblyResolve`); sets Deucalion path from `pluginFile.DirectoryName`.
- OverlayPlugin: checks ACT version, polls `InitActDone && Handle != 0`, discovers the FFXIV
  plugin by scanning `ActPlugins` for `cbEnabled.Checked && lblPluginTitle.Text.StartsWith
  ("FFXIV_ACT_Plugin")`, then reflects its public `DataRepository`/`DataSubscription`
  properties. Load order independent (polls). Downloads ~100 MB CEF to
  `AppData\OverlayPluginCef\x64` on first run (needs MSVC runtime + internet).

---

## Two-process topology (this slice)

```
Fct.Host (.NET 10, Avalonia 12)                Fct.LegacyHost (.NET Fx 4.8, WinForms)
  • launches + supervises the satellite          • Advanced Combat Tracker facade
  • embeds the satellite's tab window   ◄──IPC──►  (AssemblyResolve-supplied)
    via cross-process SetParent                  • loads real FFXIV_ACT_Plugin.dll
  • barebones Avalonia shell                     • loads real OverlayPlugin.dll
                                                 • WinForms TabControl w/ plugin tabs
                                                 • (live capture + overlays render here)
```

The satellite does all the real work (plugins, capture, overlays render as their own
windows). The net10 host launches it, embeds its tab window, and exchanges a minimal
handshake (ready + HWND) over IPC. The full typed-event bridge is **not** needed this slice.

---

## Scope decision (OPEN — needs confirmation)

"Overlay shows live DPS" requires ACT's aggregation engine (above). Two ways to scope slice 1:

- **Option A — Defer the engine (recommended).** Slice 1 proves the *new, risky* unknowns:
  two-process launch, strong-name facade via `AssemblyResolve`, real plugin loading + init,
  FFXIVRepository discovery, OverlayPlugin alive (WS server up), and the plugin UI embedded
  in Avalonia. Acceptance excludes live DPS aggregation. The aggregation engine becomes
  **slice 2** (→ MiniParse DPS).
- **Option B — Include live DPS now.** Also build ACT's combat-aggregation engine
  (`AddCombatAction`/`SetEncounter`/`ChangeZone` + `MasterSwing→CombatantData→EncounterData`
  + the `ExportVariables` DPS formatters). Much larger; also raises copy-vs-clean-room
  (ACT is copyrighted — the architecture mandates clean-room reimplementation, not copying
  decompiled code).

---

## Build sequence (de-risking order)

1. **S0** — Avalonia 12 net10 shell launches a net48 child process; minimal IPC handshake.
2. **S1** — cross-process `SetParent`: embed a trivial net48 WinForms window into the
   Avalonia window. *(de-risk the embedding)*
3. **S2** — `Advanced Combat Tracker` facade + `AssemblyResolve`; load **real
   FFXIV_ACT_Plugin.dll**; reach status "Started"; `Network_*.log` written; live capture
   with FFXIV running. *(de-risk facade + binding + capture)*
4. **S3** — load **real OverlayPlugin.dll**; FFXIVRepository discovery succeeds; CEF
   provisions; WS server up; an overlay window renders. *(de-risk coupling + CEF)*
5. **S4** — both plugin tabs embedded in the Avalonia shell.
6. **(Option B only)** **S5** — aggregation engine → MiniParse shows live DPS.

---

## Results (as built)

All phases build, were tested via run logs, and are committed.

| Phase | Result |
|---|---|
| S0 | net10 host launches net48 satellite; `READY x64=True` over named pipe. |
| S1 | `EmbeddedSatelliteView : NativeControlHost` reparents the satellite window under the host window (`parentPid==ourPid`). |
| S2 | Facade `Advanced Combat Tracker` **public-signed with ACT's public key** → exact token `a946b61e93d97868`; `AssemblyResolve` supplies it. Real FFXIV_ACT_Plugin v3.0.2.3 → **"FFXIV_ACT_Plugin Started."** |
| S3 | Real OverlayPlugin v0.18.2 hosted; reaches init Phase 2 (⇒ FFXIVRepository discovery works); CEF runs (2 `CefSharp.BrowserSubprocess`). |
| S4 | Embedded TabControl shows 3 tabs: *FFXIV ACT Plugin*, *OverlayPlugin*, *OverlayPlugin WSServer*. |
| S5 | Self-test through the facade: 10×1000 dmg over 9s ⇒ `Damage=10000 Hits=10 Crit%=40 EncDPS=1111.1`; `ExportVariables: encdps=1111 damage=10000 name=Player One crithit%=40%`. |

### Key technical resolutions
- **Strong-name impersonation:** the plugins reference ACT by strong name; `AssemblyResolve`
  requires the returned assembly be strong-named *and* token-matching. Solved by **public
  signing** the facade with ACT's extracted public key (`act.publickey`) — no private key
  needed; .NET Framework's strong-name bypass skips signature verification for full-trust
  local assemblies.
- **Nested ACT types:** `NullDelegate`, `AttackTypeGraphGenerator`, `DateTimeLogParser` are
  nested in `FormActMain`; `GetDateTimeFromLog` is a delegate **field**, not a method.
- **Init pulls the aggregation surface:** the plugin populates `CombatantData`/`EncounterData`
  tables at startup, so S2 completion required the S5 model (the two merged).

### Remaining
- **Live capture:** with FFXIV running, confirm `AddCombatAction` count > 0 and an overlay
  shows live DPS. Data source (FFXIV plugin) is Started; the rest of the path is verified.
- **Debt:** the aggregation is a simplified clean-room slice impl (no threat / personal-
  duration splits / full column set); OverlayPlugin 0.18.2 is old; satellite paths are
  hard-coded; IPC is the S0/S1 handshake only (no typed event bridge yet).

## Risks
- Cross-process HWND `SetParent` — focus/DPI/input/lifetime quirks across processes.
- `AssemblyResolve` facade fidelity — type/member shape must match what the plugins' IL
  references exactly.
- Avalonia 12 transparent/click-through + hosting a foreign HWND.
- OverlayPlugin 0.16.5 is old; CEF first-run download; MSVC runtime dependency.
- Option B: clean-room reimplementation of `ExportVariables` keys overlays depend on.
