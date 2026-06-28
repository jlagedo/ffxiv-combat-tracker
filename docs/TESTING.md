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

## Known coverage gap

`Fct.Parser.Native` parses log-line **structure** only (types, timestamps, actor/zone/ability
names). It does **not** decode FFXIV damage/heal amounts — that encoding belongs to the full
native parser and is not yet implemented, so no test certifies native damage numbers. Live
damage aggregation is covered today through the real FFXIV plugin feeding the ACT engine
(verified by the satellite integration self-test).
