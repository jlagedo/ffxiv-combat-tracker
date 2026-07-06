# GO-LIVE — v1 Remediation Plan

Working punch-list to take FFXIV Combat Tracker to a functional v1 that runs the shipped four
target legacy packages (FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry,
ACT-Discord-Triggers) drop-in and loads native plugins through a full lifecycle; the fifth package,
ACT.Hojoring, follows in P10 on the same completed fabric ([`ISOLATION-PLAN.md`](ISOLATION-PLAN.md)
§P10). v1 ships on the **isolated topology** — one satellite per plugin package, every inter-plugin
path host-routed; the build-out phases and e2e gates live in [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md).
This is a **tracking document**, not a facts doc — it records outstanding work, owners, and acceptance
bars, and is retired once v1 ships.

## v1 scope decisions (locked)

- **Plugin discovery (native + legacy):** in-app install catalog only — no boot-time scan of the
  ACT Plugins folder and no scan of any folder next to the exe. The host bundles no plugins; the
  catalog is the single source of truth for what is installed/enabled.
- **Satellite crash resilience:** auto-restart on unexpected exit **and** safe manual restart.
- **Verification bar:** the four shipped packages (FFXIV_ACT_Plugin, OverlayPlugin/cactbot,
  Triggernometry, ACT-Discord-Triggers) proven working live before ship; ACT.Hojoring's live
  acceptance is P10a (ISOLATION-PLAN §P10a), on the same fabric after v1.

## Current state (baseline)

- Native net10 plugin lifecycle: complete and robust (discover → gate → collectible-ALC load →
  time-boxed fault-guarded init → capability-gated run → quarantine/unload → hot-reload).
- Recompile-shim lifecycle: complete except WinForms `TabPage` embedding (slice D8) — deferred.
- Legacy satellite lifecycle: load/init/deinit/hot-reload all implemented; the satellite boot-loads
  **nothing** — every legacy plugin (parser included) arrives on demand via the host `LOADPLUGIN`
  command and loads **in place**. Live end-to-end verification of the four shipped packages is
  pending (A1); Hojoring is P10a.
- Logging: release-ready, coherent host↔satellite, one EventId taxonomy, no stray console writes.
- Host-crash → orphaned satellite: solved via Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`).
- Codebase is clean of incompleteness markers (no TODO/FIXME/NotImplementedException in `src/`).

---

## Workstream A — Legacy plugin catalog: complete + verify the shipped four

Catalog-only, so the catalog path must be trustworthy and cover the four shipped packages
(Hojoring's live verification is P10a).

- **A1 — Verify the pipeline against the real DLLs.** Run Triggernometry and ACT-Discord-Triggers
  (OverlayPlugin has its own P8 gate) through `PluginInstaller.InstallAsync` →
  `PluginClassifier.Classify` → `SatelliteRouter.RequestLoadPluginAsync` (per-package satellite) →
  satellite `FacadeHost.LoadPlugin` end-to-end on a machine with the real ACT
  install. Fix bugs in `Fct.Host/Plugins/PluginClassifier.cs`, `Fct.LegacyHost/FacadeHost.cs`,
  `Fct.LegacyHost/Program.cs` as they surface.
  - **Accept:** each shipped package loads, `InitPlugin` runs, its WinForms config tab embeds via
    `EmbeddedSatelliteView`, and it produces expected behavior (OverlayPlugin overlays render,
    Triggernometry fires a test trigger, Discord-Triggers hooks TTS). Hojoring's equivalent
    load/attach/route + spell-timer acceptance is P10a (ISOLATION-PLAN §P10a).
- **A2 — Persist per-plugin enabled/disabled state.** Add `Enabled` to `PluginRegistryStore.cs`
  (+ migration), honor it in `PluginLifetime.cs`, expose a toggle in `PluginsView` + VM.
  - **Accept:** disabling survives restart and the plugin does not load; re-enabling loads it
    without reinstall.
- **A3 — [DONE] Route all plugins through the catalog.** The satellite boot-loads nothing; the
  `LOADPLUGIN` command path selects the wrapped loader for `FFXIV_ACT_Plugin.dll` and the generic
  loader otherwise (`Fct.LegacyHost/Program.cs HandleCommand`). Legacy plugins install **by reference**
  (loaded in place, never copied; uninstall leaves files) via `Fct.Host/Plugins/PluginInstaller.cs`.
  One consistent installed model; the roster reflects it.

## Workstream B — Satellite fault safety net

- **B1 — [DONE] Global exception handlers in the satellite** (`Fct.LegacyHost/Program.cs`, top of
  `Main`): WinForms `Application.ThreadException` + `SetUnhandledExceptionMode(CatchException)` log a
  hosted plugin's UI-thread throw and keep pumping — a modal crash dialog would block the message
  pump, and with it every hosted plugin and the bridge;
  `AppDomain.CurrentDomain.UnhandledException` logs critical + flushes the sinks (file + host-forwarding
  `BridgeLogSink`) before the CLR terminates.
  - **Accept:** a forced background-thread throw is logged host-side under `Fct.Satellite`; a
    UI-thread throw does not pop a modal dialog inside the embedded window.
- **B2 — Launch timeout** on `SatelliteHost.WaitForConnectionAsync` — link to a CTS
  (timeout + `Process.Exited`) so a boots-but-never-connects satellite surfaces as an error.
  - **Accept:** kill the satellite mid-handshake → host flips to a clear "engine failed to start"
    state within the timeout instead of hanging "Starting…".

## Workstream C — Satellite crash recovery

- **C1 — Tear-down-first restart** (`SatelliteHost` `StartAsync`): dispose prior pipe
  servers/readers/writer, kill any live process, null state, then start. Fixes double-spawn /
  duplicate-bus-event / handle-leak.
  - **Accept:** Restart on a healthy engine yields exactly one live satellite, no duplicated events.
- **C2 — Auto-restart on unexpected exit** (`SatelliteHost` `Process.Exited`): on non-graceful
  exit, relaunch via C1's safe path with bounded backoff (≈3 attempts + status each time), then a
  persistent "engine down — Retry" state.
  - **Accept:** kill mid-session → engine returns automatically, DPS/overlays resume, embedded
    HWNDs re-populate.
- **C3 — Roster/status truthfulness on crash:** flip legacy plugins to "engine down/reconnecting",
  clear stale embedded HWND regions.
  - **Accept:** during the crash window the Plugins page shows real state.

## Workstream D — App-shell hardening

- **D1 — Single-instance guard** (`Program.cs`): named `Mutex`; second launch signals + focuses
  the existing window and exits.
  - **Accept:** second launch exits, first is focused; only one satellite.
- **D2 — Version in About:** wire `InformationalVersion` into the Settings About card.

## Workstream E — Settings & storage crash-safety

- **E1 — Atomic writes** for `UiSettingsStore.cs`, `PluginRegistryStore.cs`, `PluginStorage.cs`
  (write-temp → `File.Replace`).
  - **Accept:** an interrupted write leaves the previous good file intact.
- **E2 — Eager legacy-settings flush** (periodic timer or config-tab focus-loss) so a crash doesn't
  lose config that today only flushes on graceful ≤8s shutdown.
  - **Accept:** ungraceful kill after changing an OverlayPlugin setting → setting survives.
- **E3 — Document** the `%APPDATA%\Advanced Combat Tracker` shared-folder decision in
  `docs/ARCHITECTURE.md` (collision with a real ACT install; don't run both concurrently).

## Workstream F — Test coverage for confidence

- **F1 — Headless VM tests:** `EncountersViewModel` (Refresh/SyncCombatants) and `MainViewModel`
  state transitions (engine up/down/reconnecting).
- **F2 — One "live data → meter" E2E:** synthetic `EncounterSnapshot`/`GameSnapshot` through
  `IEncounterService` to `EncountersViewModel`.
- **F3 — Restart/crash-recovery test** exercising Workstream C (stub satellite process).
  - **Accept:** CI without ACT installed proves shell, meter binding, and crash-recovery logic.

---

## Suggested sequencing

**B → C → D1** (small, self-contained, make the app safe to hammer on) → **A** (the big
verification pass, easier once the satellite no longer wedges/dies silently) → **E** and **F** in
parallel as hardening.

## Out of scope for v1 (deferred)

ACT.Hojoring isolation + live acceptance (P10 — its facade/stand-in/router contract gaps are already
closed and gated in P9a; it stands up on the completed fabric after v1), shim UI embedding (slice D8),
system tray / minimize-to-tray, auto-update / update-check, lossy typed bridge events (BNpc id /
statuses / degraded `ActionEffect` — the log-line firehose covers the target plugins), settings schema
migration/versioning, in-app language switcher.

## Open questions

- Distribution model: are system tray and auto-update must-haves for how v1 ships? If so, promote
  from "deferred" into Workstream D.
- A1 verification needs a live Windows environment with the real ACT install
  (`%APPDATA%\Advanced Combat Tracker\Plugins\`). B–F and A2/A3 can be implemented headless.
