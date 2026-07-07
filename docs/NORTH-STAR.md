# FFXIV Combat Tracker — North Star

This is the project's fixed reference point. Everything else — architecture, phases, code —
serves the goals stated here. When a decision is unclear, it must be resolvable against this
document. If a proposed change conflicts with the North Star, the change is wrong, not the
North Star. This is a **vision anchor**, not an implementation spec — the mechanism lives in
[`ARCHITECTURE.md`](ARCHITECTURE.md), [`ISOLATION-PLAN.md`](ISOLATION-PLAN.md), and
[`PLUGIN-API.md`](PLUGIN-API.md).

---

## The goal

A modern, ACT-inspired **host application** that lets the FFXIV plugin community — who today
build for Advanced Combat Tracker — move onto a modern platform: modern .NET and an Avalonia UI,
free of legacy ACT's constraints.

We are **not** building a new ACT and **not** replacing it feature-for-feature. We are building
the **host and the ecosystem plumbing** that carries the existing plugins forward and gives their
authors a path to evolve.

## What success looks like

**The ecosystem audience is plugin developers; the product still has to win end users.** Two
things are true at once and neither is optional:

- **For developers** — plugin authors can host their plugins on the modern platform, keep them
  running unmodified, and migrate them forward on their own schedule. This is what makes the
  ecosystem move.
- **For users** — the host's own shell is a **polished, modern Avalonia UI** people *want* to run.
  The bar is explicit: it must be a clear step up from legacy ACT's cluttered WinForms interface —
  clean, functional, and pleasant enough that a user chooses it over old ACT. We do not get to hide
  behind "it's a developer platform." If the app is unpleasant to use, we have failed even with a
  thriving plugin ecosystem.

The reason developers come first *strategically* is that without plugins there is nothing for users
to run — but a great plugin story delivered through a bad UI is not success.

## The future state

Every plugin ported to modern .NET with its UI migrated to Avalonia, running natively in the host.
That is the destination. We do not get there with a flag day; we get there by giving every plugin a
place to run **today** and a road to walk **incrementally**.

## The three plugin patterns

The migration road is three stops. A plugin can sit at any stop and move forward when its author
chooses — the host supports all three at once.

1. **Legacy** — runs unmodified in an isolated .NET Framework 4.8 satellite process, talking to the
   host over IPC. This is the drop-in on-ramp: the plugin author does nothing.
2. **.NET 10 legacy** — recompiled onto modern .NET, still using its WinForms UI, but running as a
   native in-process plugin. The programming model is preserved; the runtime is modern.
3. **Fully ported** — modern .NET with the UI migrated to Avalonia. The destination.

The direction of travel is always 1 → 2 → 3, and it is always **opt-in and incremental**. The host
never forces a plugin off the stop it's on.

## Major rules — what we do and do not build

- **We do not implement plugin logic.** Triggers, overlays, timelines, TTS, memory scanning,
  parsing — that is the plugins' domain, and it stays there.
- **We only implement the logic ACT itself provided to support its plugins.** Our job is the host
  surface plugins bind to, not the things plugins do with it.
- **The one thing we reproduce is ACT's aggregation engine** (encounter/DPS/`ExportVariables`
  calculation). This is **not** a user-facing feature we chose to build — it is part of the host's
  contract to plugins: OverlayPlugin/cactbot and others read `ExportVariables` and encounter state
  off the host. Reproducing it *is* "implementing what ACT did to support plugins." Anything
  user-facing beyond that contract is out of scope.
- **We are an agnostic plugin platform.** We wire the pipes and run the ecosystem — plugin
  distribution, loading, isolation, and the migration path — without taking sides on what any
  plugin is for. The host routes by capability, never by plugin identity.

## Anti-drift check

Before adding scope, ask:

- Is this **plugin logic** we'd be implementing ourselves — triggers, overlays, timelines, TTS,
  parsing? If so, stop; that belongs to a plugin, no matter how much a user would like it built in.
- Is this the **aggregation engine, the host surface, or the host's own shell/UX**? All three are
  in scope. Polishing the host UI so users prefer it over legacy ACT is a goal, not drift.
- Does it serve **plugin developers** migrating onto the platform, **or** make the host itself a
  better product for **users**? Either is legitimate. Serving neither is drift.
- Does it keep the **1 → 2 → 3 path opt-in and incremental**? Anything that forces a plugin to move,
  or that closes a stop on the road, violates the North Star.
