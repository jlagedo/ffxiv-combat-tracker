# Testing

Automated tests live under `tests/`. Run everything with:

```powershell
./test.ps1                 # Debug; builds + stages the satellite, then runs all suites
./test.ps1 -Configuration Release
./test.ps1 -Unit           # unit/parser only — skip the satellite integration tests
./test.ps1 -Filter Dnum    # pass a --filter through to dotnet test
```

Or run a single project directly with `dotnet test tests/<project>`.

## Projects

| Project | TFM | Scope |
|---|---|---|
| `Fct.Compat.Act.Tests` | net48 | The clean-room ACT aggregation engine: `Dnum`, `MasterSwing`, `AttackType`/`CombatantData`/`EncounterData` math, the `ExportVariables` contract OverlayPlugin/cactbot read, and `SettingsSerializer` XML round-trip. |
| `Fct.App.Tests` | net10 | The bridge handshake parser (`SatelliteProtocol`): READY detection, x64 gating, HWND hex parsing. |
| `Fct.Parser.Native.Tests` | net10 | The structural `NetworkLogLine` parser (unit tests) plus a real-log smoke test over an installed `Network_*.log`. |
| `Fct.Integration.Tests` | net10 | Black-box end-to-end: launches the staged net48 satellite, checks the handshake/HWND, and verifies the in-process self-test aggregation from the satellite log. |

## What runs vs. skips

The unit/parser tests are self-contained and always run. The data-dependent tests skip
cleanly when their prerequisites are absent, so a clean checkout (and CI without an ACT
install) stays green:

- **Real-log smoke** (`Fct.Parser.Native.Tests`) needs a `Network_*.log`. It reads the newest
  one from `%APPDATA%\Advanced Combat Tracker\FFXIVLogs`, or from the path in the
  `FCT_FFXIV_LOGS` environment variable. Skips if none is found.
- **Satellite integration** (`Fct.Integration.Tests`) needs the satellite staged
  (`dotnet build src/Fct.App/Fct.App.csproj`); `test.ps1` does this first. The handshake and
  HWND tests run whenever the satellite is staged. The "plugin Started" and self-test
  assertions also need `FFXIV_ACT_Plugin.dll` installed under
  `%APPDATA%\Advanced Combat Tracker\Plugins`; they skip otherwise.

## Determinism note

The engine's damage-type routing tables are populated at runtime by the real FFXIV plugin.
`Fct.Compat.Act.Tests` reproduces that exact setup (mirrored from the plugin's `ACT_UIMods`
registration) in a fixture, so the aggregation is exercised with no live game or plugin. The
canonical regression vector is **10 hits of 1000 over 9 s, every 3rd a crit ⇒ damage 10000,
crit% 40, encdps 1111** — asserted both directly (unit) and through the live satellite
(integration).

## Differential parser compat (ours vs ACT)

`Fct.Parser.Native.ActionEffectDecoder` decodes FFXIV ActionEffect (21/22) lines into
damage/heal values. It is validated **directly against ACT's own parse** of the same lines:

- **Oracle capture** — the satellite's `--parse-oracle` mode replays a real log through the
  real FFXIV_ACT_Plugin (which subscribes to `FormActMain.BeforeLogLineRead`) and records
  every `MasterSwing` the plugin produces. That is ACT's authoritative parse.
- **Fixtures** — `tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log` is an anonymized
  real combat slice (name fields blanked; the decode reads only the effect bytes), and
  `combat-slice.oracle.tsv` is ACT's captured parse of it. Regenerate with
  `tests/fixtures/make-slice.sh` + the satellite (see below).
- **Diff test** (`ParseCompatTests`) decodes the slice and compares value multisets to the
  oracle.

Two layers are tested:

`ActionEffectDecoder` is the pure byte decode (one line → effect values). `CombatLogParser`
is stateful: it tracks the primary player (`02`), combatant names (`03`/`04`) and combat
state, resolving whole `MasterSwing`s (attacker/victim names, `InCombat`).

Current compat on the slice:
- **Damage `MasterSwing`s: full-field exact (100%).** Every damage swing ACT produces matches on
  **amount** (incl. the `>65535` `Flags2&0x40 ? Flags1<<16` transform), **crit**, **miss/block/
  parry**, **attacker/victim names**, **ability name**, and **damage-type string** — 443/443 on
  the fixture. The only excluded field is swing-type for NPC auto-attacks (below).
- **Swing-type: conservative and correct.** Player auto-attacks (action id `0x07`) classify as
  auto vs ability with no false positives. NPC auto-attacks use ids whose *category* ACT knows
  only from its bundled action-category data (3/443) — pinned as the sole swing-type gap.
- **Heals: every ACT-reported heal reproduced (0 missing)** on value + crit. Exact heal *count*
  needs ACT's combat-end detection (`StopCombat` → `InCombat` false mid-fight); a few heals are
  proc-attributed (proc-source decoding pending), so heal names are not yet asserted.

Ability names and the damage-type/element enums are FFXIV game data, not derived: the skill
table is dumped from the plugin's resource via `Fct.LegacyHost.exe --dump-skills`
(`IDataRepository.GetResourceDictionary`), committed as `fixtures/skills.tsv`; the
`DamageType`/`ElementType` enums are read from `ffxiv_act_plugin.resource.dll`.

Remaining toward full parity (tracked): ACT's **action-category** data (NPC auto-attack
swing-type), **combat-end detection** for exact heal/shield/resource counts, **proc-source**
decoding, and DoT/HoT simulation. The differential harness measures each as it lands.

Regenerate the oracle fixture (needs the ACT install):

```powershell
bash tests/fixtures/make-slice.sh "<a Network_*.log>" tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 600
# build the satellite, then:
src/Fct.LegacyHost/bin/Debug/net48/Fct.LegacyHost.exe --parse-oracle `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 100000 `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.oracle.tsv
```
