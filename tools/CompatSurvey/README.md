# CompatSurvey

Deterministic ACT-ecosystem **demand-surface** analyzer. Reads the compiled IL of a
folder of plugin/ACT binaries and emits every member each plugin binds **into an
ACT-ecosystem provider** (the ACT host, FFXIV_ACT_Plugin, or another plugin). No
plugin is executed; third-party targets are filtered out.

Not in `ffxiv-combat-tracker.slnx` (a standalone dev tool, like `build/`).

## Run

```powershell
dotnet run --project tools/CompatSurvey -- <inputDir> <outputDir>
# defaults: inputDir = E:\tmp\plugins , outputDir = <bin>\out
```

Same binaries in → byte-identical output out (nodes are pinned by SHA-256).

## Output

| File | Contents |
|---|---|
| `00-nodes.json` | every analyzed binary: SHA-256, assembly name, version, PKT, ecosystem category |
| `10-edges-static.csv` | the demand checklist — one row per statically-bound member (`consumer,kind,provider,type,member,count,intraSuite`) |
| `20-dynamic-reflection.csv` | every reflection call site (`Assembly.Load`/`Type.GetType`/`GetMethod`/…) |
| `21-dynamic-strings.csv` | string literals matching an ecosystem member/type name |
| `30-ledger.csv` | reflection + string rows as an `UNREVIEWED` triage worklist (the runtime-confirm step) |
| `COMPAT-SURFACE.md` | human summary |

A committed snapshot of the output lives in `docs/compat-survey/`.

## Phase 0 — embedded SDK extraction & surface harvest

`FFXIV_ACT_Plugin.dll` ships its SDK (`FFXIV_ACT_Plugin.Common`/`.Network`/`.Memory`/…) as
**deflate-compressed embedded resources** — one file on disk, many assemblies in memory. Phase 0
pulls those out of the parser DLL (auto-detecting raw-`MZ` / gzip / deflate), registers each as a
node with `origin: embedded` and a `carrier.dll!inner.dll` provenance path, and closes the
ref-closure (every Parser-family edge target now resolves to a node).

It also **harvests the ACT + parser-SDK member surface** into the dynamic-string vocabulary, so a
reflection-by-name into a host/SDK member is flagged even when no plugin references it statically.
Harvest is scoped to `ACT` + `Parser` on purpose — harvesting every ecosystem assembly floods the
string ledger with generic-name noise. The string pass favors recall over precision; its rows are
candidates to be triaged in `30-ledger.csv`, not confirmed edges.

## Completeness model

- **Static edges** (`10-…`) are complete by construction: the JIT resolves statically
  bound calls only through the `TypeRef`/`MemberRef`/`MethodSpec` metadata tables this
  tool reads. Nothing statically linked escapes them.
- **Dynamic edges** (`20-…`, `21-…`) are the only escape hatch, and the reflection API
  surface is finite — so every call site is enumerable. The `30-ledger.csv` rows must be
  dispositioned (real edge / false positive); computed-string targets need a runtime
  binder-log/profiler pass to resolve. That closure is what turns the enumeration into
  100% confidence.
