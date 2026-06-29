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

A clean-slate, **FFXIV-only** experimental rebuild of the ACT + FFXIV_ACT_Plugin +
OverlayPlugin stack on modern .NET. The aim being explored here: a host that can run the
**existing plugin ecosystem unmodified** while exposing a typed, future-facing plugin API,
with the network/opcode parser as a swappable, independently-released component.

The authoritative design lives in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). The map of
how data flows through the real upstream stack — and the compat seams this project must
reproduce — is in [`docs/DATA-FLOW.md`](docs/DATA-FLOW.md).

## The idea in one breath

Today's stack is three independently-evolved layers glued together by a stringly-typed,
pipe-delimited log line. This project explores collapsing that into two cooperating
processes:

- **A real .NET Framework 4.8 satellite** (`Fct.LegacyHost`) that hosts the five legacy
  plugins **unmodified** (FFXIV_ACT_Plugin, OverlayPlugin/cactbot, Triggernometry,
  ACT-Discord-Triggers, ACT.Hojoring) behind a clean-room ACT engine facade.
- **A .NET 10 host** (`Fct.App`, Avalonia) that runs new typed plugins and receives data
  over IPC. The two CLRs cannot share a process, so the OS process boundary *is* the
  runtime boundary — only data crosses.

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

| Project | TFM | Role |
|---|---|---|
| `Fct.App` | net10 | Avalonia control panel + shell (MVVM); launches and embeds the satellite. |
| `Fct.LegacyHost` | net48 | clean-room ACT engine; hosts the real plugins; bridge client. |
| `Fct.Compat.Act` | net48 | the ACT facade surface — `EncounterData`/`CombatantData` aggregation reproducing real ACT's binary output bit-for-bit on captured combat. |
| `Fct.Parser.Legacy` | net48 | wraps the real FFXIV_ACT_Plugin; ring-buffered single-dispatch `IDataSubscription`/`IRawPacketSource`. |
| `Fct.Parser.Native` | net10 | clean-room parser experiment: log-line structure, effect-byte decode, stateful combat parser, and ACT's simulated DoT/HoT/shield amounts. |

Plus `tools/mass-compare/` — a differential harness comparing the native parser against the
real-plugin oracle on recorded logs.

## Status

Slice 1 (loading the real FFXIV_ACT_Plugin + OverlayPlugin in the two-process host) is built
and exercised through run logs and tests, pending a live-game capture. Everything remains
subject to change. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §11 for the milestone
map.

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

This project studies and reproduces compat seams against the **decompiled** behavior of
Advanced Combat Tracker, FFXIV_ACT_Plugin, and OverlayPlugin. Those are third-party works
owned by their respective authors; nothing here is affiliated with or endorsed by them. This
repository contains **no game client code and no reverse-engineered game opcodes** — the v1
design deliberately hosts the real parser plugin rather than reimplementing it.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This is an unaffiliated fan project.

## License

No license is granted. This is published for reference only; all rights reserved by the
author pending a license decision. Treat it as **look, don't reuse**.
