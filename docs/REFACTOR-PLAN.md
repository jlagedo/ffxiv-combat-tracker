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
| 2 | Extract `Fct.Host` (god-project split) | #1 (CRITICAL) | med | ☑ |
| 3 | Extract `Fct.Aggregation` engine | #2 | med (parity) | ☑ |
| 4 | Thin ACT facade + parser direction | #4, #5 | low–med | ☑ |
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
| `Plugins/*` **incl.** `PluginLifetime.cs` (headless `IHostedService` — depends only on `PluginManager`/`PluginInstaller`) | — |
| `Plugins/Ui/PluginUiCoordinator.cs`, `Plugins/Ui/PluginUiHost.cs` (compile-time Avalonia only) | `Plugins/Ui/AvaloniaUiDispatcher.cs` (runtime `Dispatcher.UIThread`) |
| `SatelliteHost.cs`, `SatelliteLifetime.cs`, `ProcessJob.cs` | `EmbeddedSatelliteView.cs` (WinForms/Avalonia view), `Views/`, `ViewModels/`, `Styles/`, `Converters/`, `Lang/` |

- [x] Create `Fct.Host` (net10.0 class library, `InternalsVisibleTo` `Fct.App` + `Fct.App.Tests`);
      moved `Hosting/*`, `Plugins/*` (incl. `PluginLifetime` — headless, not Avalonia-coupled),
      `Plugins/Ui/{PluginUiCoordinator,PluginUiHost}`, and `SatelliteHost`/`SatelliteLifetime`/
      `ProcessJob`. Namespaces renamed `Fct.App.*` → `Fct.Host.*`. `AvaloniaUiDispatcher` stays in
      `Fct.App` (runtime `Dispatcher.UIThread`).
- [x] `Fct.App` references `Fct.Host`. Added an `AddFctHostServices()` registration extension in
      `Fct.Host`; `Program.BuildHost` now calls it + registers the shell/Avalonia pieces only
      (`MainWindow`/`MainViewModel`, `IUiDispatcher`→`AvaloniaUiDispatcher`, `LegacyPluginHostFactory`,
      `ISatelliteNotificationText`). `SatelliteHost`'s `Lang.Resources` coupling is broken by the
      `ISatelliteNotificationText` seam (impl `SatelliteNotificationText` in `Fct.App`); `Lang` stays put.
- [x] Kept the staging targets (`StageSatellite`, `StageSamplePlugin`) in `Fct.App`.
- [x] Rewrote `Fct.App.Tests` to **reference `Fct.Host`**; deleted **all** host-source
      `<Compile Include>` links. Internal-type construction works via `InternalsVisibleTo`.

**Exit gate:** ☑ build + tests green (114 App, 61 Compat.Act, 21 Flow, 55 Shim, 6 Parser, 7 Integration
+ 1 data-dependent skip); `Fct.App.Tests` links **zero** host source (references only); the Integration
suite launched the real staged satellite and completed the READY/HWND handshake through the moved
`SatelliteHost` — bridge-client verified from `Fct.Host`.
**Risk / rollback:** med (largest move; namespace + DI churn). Pure move — revert restores the WinExe.

---

## Phase 3 — Extract `Fct.Aggregation` engine · parity-critical

**Goal:** give the parity-critical aggregation engine one identity instead of compiling the same
source into two `Advanced Combat Tracker` assemblies; guarantee both facades consume identical engine
binaries.

**New project:** `Fct.Aggregation` (net48;net10) ← `shared/Aggregation/*.cs`. Pin `Nullable=disable`,
`ImplicitUsings=disable`, `LangVersion=latest` to match **both** current compile contexts exactly
(prevents behavior drift from a different compile setting).

- [x] Create `Fct.Aggregation` (net48;net10); move `shared/Aggregation/*.cs`; pin the compile settings
      above (`Nullable=disable`, `ImplicitUsings=disable`, `LangVersion=latest`).
- [x] Reference it from `Fct.Compat.Act` (net48) and `Fct.Compat.Shim.ActFacade` (net10); deleted the
      `shared/Aggregation` `<Compile Include>` links from both.
- [x] Update `Fct.Compat.Act.Tests` to **reference** `Fct.Aggregation` instead of recompiling
      `shared/Aggregation`; still recompiles only the thin facade sources.
- [x] Ran the differential `AggregateCompatTests` against the ACT oracle — **bit-for-bit identical**
      (61 pass). Full suite green; replay through the real plugin restores the `YOU` combatant
      (2,692,084 damage, 2 idle-split encounters) — proving the plugin↔facade↔engine path is intact.
- [x] Deleted the emptied `shared/Aggregation` (+ empty `shared/`); added the project to `.slnx`.

**Three constraints the plan didn't foresee (all resolved, all parity-load-bearing):**

1. **Engine↔facade coupling.** The engine reads `ActGlobals.{charName,blockIsHit,restrictToAll}`, which
   live on each facade — a cycle if referenced directly. Resolved by an accessor seam: the engine's
   `AggregationGlobals` exposes `Func<>` accessors (ACT-default fallbacks) that each facade's
   `ActGlobals` static ctor wires to its own fields. The engine reads one live source without
   referencing the facade.
2. **`ActGlobals` fields must stay fields.** Precompiled plugins bind those statics as `ldsfld` (e.g.
   `FFXIV_ACT_Plugin.Common`'s `ACTWrapper.CharName => ActGlobals.charName`). An earlier attempt to make
   them forwarding *properties* broke the real plugin's primary-player→`YOU` substitution (its 2.7M
   damage vanished, only 1 encounter). Kept as real fields; the accessor seam above bridges to the engine.
3. **Assembly-qualified type identity.** Real plugins reference the ACT types by
   `"<type>, Advanced Combat Tracker"`. Moving them out triggered
   `TypeLoadException: Could not load type 'DamageTypeDef' from 'Advanced Combat Tracker'`. Resolved with
   `[assembly: TypeForwardedTo]` for every top-level engine type in the net48 facade (`TypeForwards.cs`);
   nested types ride their declaring type's forward. The net10 side recompiles, so its transitive
   project reference suffices — no forwarders there. Signing: `Fct.Compat.Act` is public-signed, so it may
   only reference strong-named assemblies (CS8002) → `Fct.Aggregation` is **fully** strong-named
   (`Fct.Aggregation.snk`, a real key pair, valid signature that also loads in the VSTest testhost).

**Exit gate:** ☑ aggregation oracle diff **bit-identical** (61 pass); both facades build; engine has one
identity; full suite green (61 Compat.Act, 55 Shim, 21 Flow, 6 Parser, App, 7 Integration + 1
data-dependent skip); real-plugin replay reaches `Started` and reproduces the `YOU` aggregate.
**Risk / rollback:** med — parity. Revert restores linked source.

---

## Phase 4 — Thin the ACT facade + resolve parser dependency direction

**Goal:** with the engine extracted, reduce `Fct.Compat.Act` to impersonation-identity + WinForms host
shims, and settle the odd `Fct.Parser.Legacy → Fct.Compat.Act` direction.

- [x] Confirmed `Fct.Compat.Act` now holds **only** host-surface: `FormActMain` (+ combat pipeline
      delegation into the engine), `ActGlobals` (statics + the `AggregationGlobals` accessor wiring),
      the `IActPluginV1`/`IActPluginAlias`/`ActPluginData` contract + ACT event args/delegates
      (`Primitives.cs`), WinForms shims (`SettingsSerializer`, `FormSpellTimers`, `FormImportProgress`,
      `TraySlider`), the fake-identity/signing config, and `TypeForwards.cs`. **No engine code compiled
      in** — the aggregation types are `TypeForwardedTo(Fct.Aggregation)`, which is referenced (P3).
- [x] Audited `Fct.Parser.Legacy`'s consumption of `Fct.Compat.Act`: a single file
      (`WrappedFfxivPlugin.cs`) touches the `Advanced_Combat_Tracker` namespace, and it consumes
      **only two host-surface contract types** — `IActPluginV1` and `IActPluginAlias`. No engine,
      aggregation, `ActGlobals`, or `FormActMain` types are consumed by the parser.
- [x] **Decision: accept the direction, document the rationale** (narrowing is impossible, not merely
      unattractive). `WrappedFfxivPlugin` is placed in `ActPluginData.pluginObj`, so it must **be** an
      `IActPluginV1`, and it composes over a real plugin's `IActPluginV1` — which resolves to the
      facade's `Advanced_Combat_Tracker.IActPluginV1, Advanced Combat Tracker`. That contract must carry
      the impersonation identity by requirement, so it cannot be relocated to a thinner shared surface
      without breaking the identity the real precompiled plugins bind to. Recorded in
      `docs/ARCHITECTURE.md` (§4, after the project table). No clean "ACT host-surface" separates —
      no split performed.
- [x] Doc truth-up alongside: refreshed the `Fct.Compat.Act` / `Fct.Parser.Legacy` project-table
      entries in `docs/ARCHITECTURE.md`, and fixed a stale §8 reference that still called
      `Fct.Compat.Act` "the ACT engine" (the engine is `Fct.Aggregation` since P3).

**Exit gate:** ☑ facade confirmed minimal (host surface only; engine referenced, not compiled);
parser→facade dependency justified + documented as a necessary host-surface reference; no code moves
were needed (investigation confirmed the split is already clean). Build + full suite green (61
Compat.Act, 114 App, 6 Parser, 21 Flow, 55 Shim, 7 Integration + 1 data-dependent skip); the
Integration suite launched the real staged satellite through the READY/HWND handshake.
**Risk / rollback:** low–med; investigation + docs only — no source moved, nothing to roll back.

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

- ~~Phase 4: final call on the `Fct.Parser.Legacy → Fct.Compat.Act` direction (narrow vs. document).~~
  **Resolved: documented.** The direction is a necessary host-surface reference — `WrappedFfxivPlugin`
  implements the facade's `IActPluginV1`, which must carry the `Advanced Combat Tracker` impersonation
  identity, so it cannot be narrowed to a thinner surface. Rationale in `docs/ARCHITECTURE.md` §4.
- Phase 5: confirm shared-ALC identity unification for the impersonation facades holds without the
  `deps.json` bake-in; fall back to hard reference if not.
