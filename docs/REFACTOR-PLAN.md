# Architecture Refactoring Plan

Phase-by-phase tracker for the structural refactoring identified in the project-layout review.
This is a **working/tracking document** (unlike the facts docs in `docs/`): it carries sequence,
status, and rationale. Update the status table and tick the checkboxes as each phase lands.

## How to use this doc

- Work **top-to-bottom**. The order is dependency-driven, not priority-driven — an earlier phase
  removes rework from a later one.
- Every phase ends **green**: `dotnet run --project build` (or `dotnet build src\Fct.App`) succeeds,
  `.\test.ps1` passes (data-dependent tests may skip without `FFXIV_ACT_Plugin.dll`), and the
  host↔satellite smoke path still works. A phase is not "done" until its **exit gate** is met.
- Each phase is a **move/re-wire**, not a behavior change. If a diff changes behavior, it's out of
  scope for that phase.
- After each phase, refresh the `CLAUDE.md` project map + `docs/ARCHITECTURE.md` so the docs never
  lag the code.

Status legend: ☐ not started · ◐ in progress · ☑ done · ⊘ blocked

## Decisions locked (from review Q&A)

- **The net10 in-process legacy path is strategic** (`Fct.Compat.Shim` + `ActFacade`/`SdkFacade` are
  here to stay). So Phase 5 hardens that path rather than removing it, and the double-compiled engine
  (Phase 3) is a permanent liability worth fixing now.
- **No constraint on assembly count.** Promoting linked-source piles to real multi-targeted
  (`net48;net10`) libraries is approved on the merits (identity, testability, anti-drift).

## Status overview

| # | Phase | Addresses (review finding) | Risk | Status |
|---|-------|----------------------------|------|--------|
| 0 | Baseline & docs truth-up | Stale map, #7, #8 | minimal | ☑ |
| 1 | Shared contracts → libraries | #6 | low | ☑ |
| 2 | Extract `Fct.Host` (god-project split) | #1 (CRITICAL) | med | ☐ |
| 3 | Extract `Fct.Aggregation` engine | #2 | med (parity) | ☐ |
| 4 | Thin ACT facade + parser direction | #4, #5 | low–med | ☐ |
| 5 | Shim-as-plugin (drop hard ref) | #3 | high | ☐ |

## Sequence rationale

```
0 baseline ─► 1 contracts ─► 2 Fct.Host ─────────────► 5 shim-as-plugin
                     │                                        ▲
                     └──► 3 Fct.Aggregation ─► 4 thin facade ─┘
```

- **Contracts (1) before everything** — the logging/bridge wire types are linked into the two
  processes and into the files Phase 2 moves. Making them libraries first means Phase 2 references a
  library instead of dragging linked source, and it warms up the `net48;net10` library pattern that
  Phase 3 reuses.
- **`Fct.Host` (2) is the critical fix, done as early as its one prerequisite allows.** It unblocks
  headless testing of the runtime and deletes the ~15 `<Compile Include>` links in `Fct.App.Tests`.
- **Aggregation (3) → facade thinning (4)** — the facade can't be reduced to an impersonation shell
  until the engine has moved out; the parser-direction question resolves once the engine/surface split
  exists.
- **Shim-as-plugin (5) is last.** It changes the legacy runtime load path (ALC identity of the
  impersonation facades) — the highest behavioral risk — so it lands on an otherwise-stable base with
  a clean loader (from Phase 2) underneath it.
- **2 and 3 are independent** — they can run in parallel across two people if desired; the linear
  order just keeps each landing green.

---

## Phase 0 — Baseline & documentation truth-up

**Goal:** lock a green baseline and clear zero-risk doc/config defects so later diffs read clean.

- [x] Record baseline: full build + `.\test.ps1` green; note which data-dependent tests skip.
      (All suites pass; one skip: `Fct.Integration.Tests.SatelliteIntegrationTests
      .Self_test_aggregation_matches_known_vector`, data-dependent on `FFXIV_ACT_Plugin.dll`.)
- [x] Refresh the `CLAUDE.md` project map to include `Fct.Compat.Shim`,
      `Fct.Compat.Shim.ActFacade`, `Fct.Compat.Shim.SdkFacade`, and `Fct.Abstractions.Testing`.
- [x] Document the two **intentional** assembly-name collisions (`Advanced Combat Tracker` ×2,
      `FFXIV_ACT_Plugin.Common` ×2) in the map so nobody "fixes" them (finding #8).
- [x] Drop the speculative `net48` TFM from `Fct.Abstractions.Testing` → `net10` only (all three
      consumers are net10; re-add when a net48 shim consumer is real) (finding #7).
- [x] Commit this plan.

**Exit gate:** build + tests green; `CLAUDE.md` map matches the `.slnx`.
**Risk / rollback:** minimal; each item is independently revertible.

---

## Phase 1 — Shared contracts → multi-target libraries

**Goal:** give the logging + IPC wire contracts a single identity, de-link both host processes, and
establish the `net48;net10` library pattern. Kills the drift risk between the two bridge ends.

**New projects**

| Project | TFM | Absorbs (currently linked source) |
|---|---|---|
| `Fct.Logging.Contracts` | net48;net10 | `shared/Logging/{LogEvents,LogPaths,BridgeLogRecord}.cs` |
| `Fct.Bridge.Contracts` | net48;net10 | `shared/Bridge/GameEventFrame.cs` + `Fct.App/SatelliteProtocol.cs` |

- [x] Create `Fct.Logging.Contracts` (net48;net10). Moved `shared/Logging/*.cs` in; types made
      `public`. No `Microsoft.Bcl.AsyncInterfaces` needed (no records/async in these types).
- [x] Create `Fct.Bridge.Contracts` (net48;net10, references `Fct.Abstractions`). Moved
      `shared/Bridge/GameEventFrame.cs` and `Fct.App/SatelliteProtocol.cs` in (namespace `Fct.App` →
      `Fct.Bridge`, types made `public`); the protocol is now shared instead of net10-only.
- [x] Reference both libs from `Fct.App` and `Fct.LegacyHost`; deleted the `<Compile Include>`
      `shared/Logging` + `shared/Bridge` links from both.
- [x] Pointed the **satellite** handshake at the shared `SatelliteProtocol` — added `FormatReady`/
      `FormatHwnd`/`FormatPlugin` and switched `Program.cs` (READY/HWND/PLUGIN/PLUGINS-END emit,
      LOADPLUGIN/UNLOADPLUGIN parse, UNLOADED emit) onto it; retired the hand-rolled format strings.
- [x] Updated `Fct.App.Tests` + `Fct.Integration.Tests` to reference the libs; deleted their
      `SatelliteProtocol.cs` / `LogEvents.cs` / `LogPaths.cs` / `GameEventFrame.cs` links and fixed the
      `using Fct.App;` → `using Fct.Bridge;` in the moved-type consumers.
- [x] Deleted the emptied `shared/Logging` + `shared/Bridge` folders; added both projects to `.slnx`.

**Exit gate:** ☑ build + tests green (114 App, 61 Compat.Act, 21 Flow, 55 Shim, 6 Parser, 7 Integration
+ 1 data-dependent skip); **host↔satellite handshake verified** — the integration suite launched the
real staged satellite and completed the READY/HWND handshake through the shared `SatelliteProtocol`
codec; no linked logging/bridge source remains anywhere.
**Risk / rollback:** low–med (records on net48; confirm `Fct.LegacyHost` binding redirects unaffected).
Pure move + re-wire — revert restores links.

---

## Phase 2 — Extract `Fct.Host` (god-project split) · CRITICAL

**Goal:** move the headless runtime (host services + plugin loader + bridge-client orchestration) out
of the Avalonia WinExe into a class library, so it's testable/reusable in isolation and the
`Fct.App.Tests` source-links disappear.

**New project:** `Fct.Host` (net10 class library — bump to `net10.0-windows` only if a moved file
needs WinForms; `Fct.App.Tests` compiles these under plain net10 today, so net10 should suffice).
References: `Fct.Abstractions`, `Fct.Abstractions.UI`, `Fct.Bridge.Contracts`, `Fct.Logging.Contracts`,
`Microsoft.Extensions.Hosting.Abstractions`, `System.Reflection.MetadataLoadContext`,
`Microsoft.Extensions.Logging.Abstractions` (Serilog backend stays in the `Fct.App` composition root).

**Move boundary**

| Moves to `Fct.Host` | Stays in `Fct.App` (UI / composition root) |
|---|---|
| `Hosting/*` (bus, registry, audio, encounter, snapshot, storage, session, raw sources) | `App.axaml`, `MainWindow`, `Program` (composition root) |
| `Plugins/*` **except** `PluginLifetime.cs` | `Plugins/PluginLifetime.cs` (Avalonia-coupled `IHostedService`) |
| `Plugins/Ui/PluginUiCoordinator.cs`, `Plugins/Ui/PluginUiHost.cs` (compile-time Avalonia only) | `Plugins/Ui/AvaloniaUiDispatcher.cs` (runtime `Dispatcher.UIThread`) |
| `SatelliteHost.cs`, `SatelliteLifetime.cs`, `ProcessJob.cs` | `EmbeddedSatelliteView.cs` (WinForms/Avalonia view), `Views/`, `ViewModels/`, `Styles/`, `Converters/`, `Lang/` |

- [ ] Create `Fct.Host`; move the left-column files. Keep namespaces or update references in one pass.
- [ ] `Fct.App` references `Fct.Host`; fix the composition root (`Program`/`App`) DI registrations to
      the moved types.
- [ ] Keep the staging targets (`StageSatellite`, `StageSamplePlugin`) in `Fct.App` — they're
      deployment concerns of the shell.
- [ ] Rewrite `Fct.App.Tests` to **reference `Fct.Host`**; delete **all** host-source
      `<Compile Include>` links (`Hosting/*`, `Plugins/*`, `SatelliteProtocol`, `ProcessJob`).

**Exit gate:** build + tests green; `Fct.App.Tests` links **zero** host source (references only);
manual smoke — host boots, satellite handshake completes, `Fct.SamplePlugin` + `Fct.SampleLegacyPlugin`
load.
**Risk / rollback:** med (largest move; namespace + DI churn). Pure move — revert restores the WinExe.

---

## Phase 3 — Extract `Fct.Aggregation` engine · parity-critical

**Goal:** give the parity-critical aggregation engine one identity instead of compiling the same
source into two `Advanced Combat Tracker` assemblies; guarantee both facades consume identical engine
binaries.

**New project:** `Fct.Aggregation` (net48;net10) ← `shared/Aggregation/*.cs`. Pin `Nullable=disable`,
`ImplicitUsings=disable`, `LangVersion=latest` to match **both** current compile contexts exactly
(prevents behavior drift from a different compile setting).

- [ ] Create `Fct.Aggregation` (net48;net10); move `shared/Aggregation/*.cs`; pin the compile settings
      above.
- [ ] Reference it from `Fct.Compat.Act` (net48) and `Fct.Compat.Shim.ActFacade` (net10); delete the
      `shared/Aggregation` `<Compile Include>` links from both.
- [ ] Update `Fct.Compat.Act.Tests` to **reference** `Fct.Aggregation` (now a normal unsigned DLL that
      loads in testhost) instead of recompiling `shared/Aggregation`; keep recompiling only the thin
      facade sources (the signed-identity load barrier applies to the facade, not the engine).
- [ ] Run the differential `AggregateCompatTests` against the ACT oracle — **must stay bit-for-bit
      identical**. This is the gate.
- [ ] Delete the emptied `shared/Aggregation`; add the project to `.slnx`.

**Exit gate:** aggregation oracle diff **bit-identical**; both facades build; engine has one identity.
**Risk / rollback:** med — parity. The oracle diff is the hard gate; the only real risk is compile-context
drift, controlled by pinning. Revert restores linked source.

---

## Phase 4 — Thin the ACT facade + resolve parser dependency direction

**Goal:** with the engine extracted, reduce `Fct.Compat.Act` to impersonation-identity + WinForms host
shims, and settle the odd `Fct.Parser.Legacy → Fct.Compat.Act` direction.

- [ ] Confirm `Fct.Compat.Act` now holds only: `FormActMain`/`EncounterData`/`CombatantData` WinForms
      shims, `ActGlobals`, and the fake-identity/signing config (engine is referenced, not compiled in).
- [ ] Audit what `Fct.Parser.Legacy` actually consumes from `Fct.Compat.Act`. If it's only
      host-surface types (`ActGlobals`/`Advanced_Combat_Tracker`), decide: narrow the reference to a
      thinner surface, or accept the direction and record the rationale in `docs/ARCHITECTURE.md`.
- [ ] (Optional) if a clean "ACT host-surface" separates naturally, split it; otherwise document the
      accepted dependency with its reason.

**Exit gate:** facade responsibilities minimized + documented; parser dependency justified or narrowed;
build + tests green.
**Risk / rollback:** low–med; mostly investigation + small moves.

---

## Phase 5 — Shim-as-plugin: drop `Fct.App`'s hard `Fct.Compat.Shim` reference

**Goal:** honor directive #2 ("opt-in, never a flag day"). Load the net10 legacy shim + its two
impersonation facades as a **staged plugin package** into the shared/default ALC, so the modern host's
static graph no longer bakes in ACT-impersonation identities. Highest runtime risk → last.

- [ ] Verify the loader (now in `Fct.Host`) can place an assembly set into the shared/default ALC via
      `PluginLoadContext.IsShared` — the same mechanism that shares Avalonia/`Fct.Abstractions` today.
- [ ] Change `Fct.App`'s `<ProjectReference Include="Fct.Compat.Shim">` to
      `ReferenceOutputAssembly="false"` + a staging target (mirror `StageSamplePlugin`), staging
      `Fct.Compat.Shim` + `ActFacade` (`Advanced Combat Tracker`) + `SdkFacade`
      (`FFXIV_ACT_Plugin.Common`) into the legacy-runtime folder.
- [ ] Ensure the two impersonation facades resolve into the **shared** ALC so a shimmed plugin and the
      shim agree on type identity (the original reason for the hard ref) — via the loader's
      shared-assembly path, **not** a `deps.json` bake-in.
- [ ] End-to-end: load `Fct.SampleLegacyPlugin` through the shim; confirm `InitPlugin(TabPage, Label)`
      + `ActGlobals` wiring still work.

**Exit gate:** `Fct.App`'s static graph contains **no** ACT-impersonation identities;
`Fct.SampleLegacyPlugin` loads + runs via the shim through the loader; build + tests green.
**Risk / rollback:** HIGH (runtime ALC identity + legacy load path). Keep the ability to revert to the
hard reference if shared-ALC identity unification proves fragile.

---

## Target end-state reference graph

```
Fct.Abstractions (net48;net10) ─ leaf
  ├─ Fct.Abstractions.UI (net10, +Avalonia)
  └─ Fct.Abstractions.Testing (net10)              ← net48 target dropped (P0)

Fct.Logging.Contracts (net48;net10) ─ leaf         ← new (P1)
Fct.Bridge.Contracts  (net48;net10) ─ leaf         ← new (P1), owns SatelliteProtocol
Fct.Aggregation       (net48;net10) ─ leaf         ← new (P3)

Fct.Host (net10) ── host services + plugin loader + bridge-client   ← new (P2)
  └─> Fct.Abstractions, Fct.Abstractions.UI, Fct.Bridge.Contracts, Fct.Logging.Contracts

Fct.App (net10, WinExe) ── shell only: Views/ViewModels + composition root
  ├─> Fct.Host, Fct.Abstractions(.UI)
  ├─> Fct.LegacyHost, Fct.SamplePlugin, Fct.Compat.Shim   (all staged, ReferenceOutputAssembly=false)  ← P5
  └─> Fct.Bridge.Contracts, Fct.Logging.Contracts

Fct.LegacyHost (net48, WinExe)
  └─> Fct.Compat.Act, Fct.Parser.Legacy, Fct.Abstractions, Fct.Bridge/Logging.Contracts

Fct.Compat.Act (net48, "Advanced Combat Tracker") ──> Fct.Aggregation   ← thinned (P4)
Fct.Compat.Shim.ActFacade (net10, "Advanced Combat Tracker") ──> Fct.Aggregation
```

## Open items to revisit

- Phase 4: final call on the `Fct.Parser.Legacy → Fct.Compat.Act` direction (narrow vs. document).
- Phase 5: confirm shared-ALC identity unification for the impersonation facades holds without the
  `deps.json` bake-in; fall back to hard reference if not.
