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

Current compat on the slice:
- **Damage values: exact (100%).** Every damage `MasterSwing` ACT produces — amount (including
  the `>65535` `Flags2&0x40 ? Flags1<<16` transform), crit flag, and miss/block/parry — is
  reproduced exactly (443/443 on the fixture). This layer is pure byte decode: fully determined
  by the line, no game-data tables.
- **Swing-type: conservative and correct.** Player auto-attacks (action id `0x07`) are
  classified as auto vs ability and verified against ACT with no false positives. NPC
  auto-attacks use other action ids that ACT only knows from its bundled **action table**, so
  those (3/443 on the fixture) are the sole swing-type gap — the test pins that the only
  mismatches are ACT-auto/ours-ability with identical values.
- **Heals: every ACT-reported heal reproduced (0 missing).** Our decode is a superset because
  ACT only reports heals while `InCombat` (`ReportCombatData.AddHealEntry`); the extras are
  out-of-combat heals ACT suppresses.

Remaining toward full parity (tracked, not yet done) — each needs a layer beyond pure
line decode: the **action table** (NPC auto-attack ids, ability names, damage-type/element
enum strings); **combat-state** (`InCombat`) tracking for exact heal/shield/resource
reporting; **combatant tracking** for attacker/victim name resolution; and DoT/HoT simulation.

Regenerate the oracle fixture (needs the ACT install):

```powershell
bash tests/fixtures/make-slice.sh "<a Network_*.log>" tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 600
# build the satellite, then:
src/Fct.LegacyHost/bin/Debug/net48/Fct.LegacyHost.exe --parse-oracle `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.log 100000 `
  tests/Fct.Parser.Native.Tests/fixtures/combat-slice.oracle.tsv
```
