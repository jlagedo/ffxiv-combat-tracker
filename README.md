# FFXIV Combat Tracker

> ## ⚠️ DRAFT — TEST / EXPERIMENT ONLY
>
> **This is not a released product, not usable software, and not supported.** It is a
> personal, exploratory prototype published for reference and discussion only. There are
> **no builds, no installers, and no guarantees**. APIs, structure, and decisions change
> without notice, and large parts are unfinished or stubbed. **Do not use this for anything
> real.** Use the actual upstream tools ([Advanced Combat Tracker](https://advancedcombattracker.com/),
> [FFXIV_ACT_Plugin](https://github.com/ravahn/FFXIV_ACT_Plugin),
> [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin)) for live play.

---

**FFXIV Combat Tracker modernizes the stack under the FFXIV ACT plugin ecosystem.** It runs
today's ACT plugins unmodified on current .NET, and opens an incremental, opt-in path to
migrate them onto a typed modern API. **It is not a new ACT and not a replacement** — the
point is to carry the community's existing plugins forward, not to compete with the tools they
rely on.

Concretely, this is an **FFXIV-only** experiment: a .NET 10 host that runs the real
FFXIV_ACT_Plugin + OverlayPlugin (and the wider ecosystem) unmodified — each legacy plugin
package in its own isolated satellite process — with the network/opcode parser kept as a
swappable, independently-released component.

The authoritative design lives in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). The map of
how data flows through the real upstream stack — and the compat seams this project must
reproduce — is in [`docs/DATA-FLOW.md`](docs/DATA-FLOW.md).

## The idea in one breath

Today's stack is three independently-evolved layers glued together by a stringly-typed,
pipe-delimited log line. This project explores splitting that across a modern host and a set
of isolated satellite processes:

- **A .NET 10 host** (`Fct.App`, Avalonia) that owns all calculations and routing: it runs the
  authoritative ACT aggregation engine (`Fct.Engine`/`Fct.Aggregation`), runs new typed plugins
  in isolated load contexts, and routes combat data over the IPC bridge. The engine — not any
  legacy binary — is the single source of truth for encounters/DPS/`ExportVariables`.
- **Real .NET Framework 4.8 satellites** (`Fct.LegacyHost`) that host the legacy plugins
  **unmodified**, **one satellite process per plugin package**, each behind its own private ACT
  facade. The two CLRs cannot share a process, so the OS process boundary *is* both the runtime
  boundary and the isolation boundary; only typed data crosses. The target set is five:
  FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry, ACT-Discord-Triggers, ACT.Hojoring.

Two hard directives gate every decision:

1. The first version must run the five legacy plugins by **drop-in, unmodified**.
2. Built for the future with a **clear legacy → native migration path** — opt-in,
   incremental, never a flag day.

A key invariant: **opcodes never cross the plugin boundary.** The host↔plugin contract is
typed domain events, so a game patch ships a new parser plugin only — everything else is
untouched. One opt-in escape hatch (`IRawPacketSource`) exposes raw packets for legacy
`RegisterNetworkParser` consumers.

## What's in here so far

This is a prototype; the table below reflects what exists in the tree, not a finished system.

This lists the load-bearing projects only; the full project map lives in
[`CLAUDE.md`](CLAUDE.md) and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

| Project | TFM | Role |
|---|---|---|
| `Fct.App` | net10 | Avalonia control panel + shell (MVVM) + composition root; binds the host runtime to the UI. |
| `Fct.Host` | net10 | the .NET 10 host runtime: `IPluginHost` services, the ALC plugin loader, and the IPC bridge client that spawns/routes one satellite per plugin package. |
| `Fct.Aggregation` | net48;net10 | **the ACT aggregation engine** (`EncounterData`/`CombatantData`/`MasterSwing` + `ExportVariables`) — one strong-named binary shared by the host authority and every facade replica. |
| `Fct.Engine` | net10 | the host-side ACT engine — the single source of truth for encounter calculations, folding the bridged swing + lifecycle stream through `Fct.Aggregation`. |
| `Fct.LegacyHost` | net48 | the satellite: ACT facade host + `IActPluginV1` loader, one real plugin package per process; the net48 end of the bridge. |
| `Fct.Compat.Act` | net48 | the net48 ACT facade surface (host shims + fake `Advanced Combat Tracker` identity); fronts a parity-gated `Fct.Aggregation` replica for legacy synchronous reads. |
| `Fct.Parser.Legacy` | net48 | wraps the real FFXIV_ACT_Plugin (the sole parser); ring-buffered single-dispatch `IDataSubscription`/`IRawPacketSource`. |
| `Fct.Abstractions` | net48;net10 | the forward, typed plugin SDK — contracts + domain records shared across the bridge. No opcodes. |

Plus `tools/mass-compare/` — a corpus-scale differential harness holding our ACT engine to the real
ACT binary, both fed the same plugin-produced swings, on recorded logs.

## Status

An exploratory prototype: the host loads the real plugins in per-package satellites and the
forward typed contract is in place, but large parts are unfinished or stubbed and everything is
subject to change. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §11 for the milestone map and
[`docs/PLUGIN-API.md`](docs/PLUGIN-API.md) for the forward surface + remaining work.

## Validation — ACT-engine output parity

The one hard, measurable claim this prototype makes: **our from-scratch ACT aggregation engine
(`Fct.Aggregation`, authoritative in the net10 `Fct.Engine`) reproduces the real ACT binary's
output bit-for-bit** (no ACT code is copied), given the same plugin-produced swings. "Output" means `ExportVariables` — the exact per-combatant and
per-encounter dictionaries OverlayPlugin builds and cactbot/overlays read.

### How the test works (`tools/mass-compare`)

The real **FFXIV_ACT_Plugin is the sole parser** — this project never decodes a log line itself.
The pipeline feeds **one** plugin-produced swing stream into **two** aggregators and diffs the
result:

1. Load the **real FFXIV_ACT_Plugin** once and capture its `MasterSwing` parse of every
   `Network_*.log` → `<name>.oracle.tsv` (the producer's output, captured once).
2. Aggregate that stream through the **real ACT binary** → `<name>.oracle.exports.tsv` (the gold
   baseline).
3. Aggregate the **same** stream through **our engine** → `<name>.engine.exports.tsv`.
4. Diff the two `ExportVariables` payloads, per file × per row × per key.

Identical input into both engines, so any difference is purely *our aggregation vs ACT's*.

### The corpus

| | |
|---|---|
| Real `Network_*.log` files | **208** |
| Players | **3** |
| Patches spanned | **27109 → 30203** |
| Content | solo/city downtime, dungeons, Extremes, two Savage tiers, Ultimate |
| Key/value pairs compared | **460,432** |

### Result

**100.000% exact** — all 460,432 string values identical, **0 ours-only, 0 act-only**, every
per-key numeric Σ bit-identical on both sides. Every export key OverlayPlugin reads matches,
for both the `Combatant` object (per-player DPS/HPS/crit%/max-hit/deaths/…) and the raid-wide
`Encounter` object.

This 208-log corpus is **private play-data — it is not shipped in this repo**, so the
460,432-pair number can't be reproduced from a checkout. What ships and runs anywhere is the
fixture-level version of the same check (`AggregateCompatTests` / `ExportVarsCompatTests`),
held to ACT bit-for-bit over **two committed slices** (1,452 pairs) on every test pass. Full
method: [`docs/TESTING.md`](docs/TESTING.md).

### What this does and doesn't prove

- **It does:** an overlay (cactbot, MiniParse, …) reading combat data off this host would see the
  same numbers it sees off real ACT, to the character.
- **It doesn't:** validate *parsing* — that is the plugin's job, hosted unmodified. DoT/HoT/shield
  values are the plugin's own estimates, baked into the swings both engines receive; we sum
  whatever we are handed, exactly as ACT does. And this is still a prototype (see the banner above).

## Building (for the curious only)

Requires the .NET 10 SDK, the net48 targeting pack, and the WindowsDesktop runtime on
Windows. The net10 host build-depends on the net48 satellite and stages it alongside.

```powershell
dotnet build src\Fct.App\Fct.App.csproj   # net10 host; chains + stages the net48 satellite
./test.ps1                                 # build, stage, and run all test suites
```

Data-dependent tests skip automatically when their prerequisites (a real `Network_*.log`,
or an installed `FFXIV_ACT_Plugin.dll`) are absent.

## Relationship to upstream projects

**This is not a replacement for ACT, FFXIV_ACT_Plugin, or OverlayPlugin — use the upstream
tools for live play.** This project exists to carry their ecosystem forward onto modern .NET,
not to compete with it. It studies and reproduces compat seams against the **decompiled**
behavior of Advanced Combat Tracker, FFXIV_ACT_Plugin, and OverlayPlugin so the real plugins
keep working; those are third-party works owned by their respective authors, and nothing here
is affiliated with or endorsed by them. This repository contains **no game client code and no
reverse-engineered game opcodes** — the v1 design deliberately hosts the real parser plugin
rather than reimplementing it.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This is an unaffiliated fan project.

## License

No license is granted. This is published for reference only; all rights reserved by the
author pending a license decision. Treat it as **look, don't reuse**.
