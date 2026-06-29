# mass-compare

Massive differential parse test: runs our clean-room native parser (`Fct.Parser.Native`) against
ACT's authoritative parse over an **entire folder of decoded `Network_*.log` files** (months of
logs), file by file, and reports bit-level parity.

The `Network_*.log` files are ACT's *decoded, pipe-delimited* log text — not raw packets — so both
ACT's plugin and our parser re-parse the same lines. There is no game-version skew: the opcode
decode already happened at capture time.

## How it runs

```powershell
./tools/mass-compare/run.ps1                      # all logs in %APPDATA%\...\FFXIVLogs
./tools/mass-compare/run.ps1 -MaxLines 200000     # quick: cap lines per file
./tools/mass-compare/run.ps1 -SkipOracle          # re-diff using existing oracle TSVs
```

Two stages:

1. **Oracle** — `Fct.LegacyHost.exe --mass-oracle <logFolder> <outFolder> [maxLines]` loads the
   real `FFXIV_ACT_Plugin` **once** and feeds every log through `FormActMain.BeforeLogLineRead`,
   in chronological filename order, as one continuous stream (combatant-name/combat state carries
   across day-boundary rotations, exactly as ACT sees it). Every `MasterSwing` the plugin produces
   is written to `<name>.oracle.tsv`. Also dumps the full skill table (`skills.full.tsv`).
2. **Diff** — `MassCompare <logFolder> <oracleFolder> <outFolder>` parses the same logs with our
   parser (one instance, same continuous state) and bag-diffs each file:
   - **damage** on `(crit, amount, special, attacker, victim, attackType, damageType)` — parity
     requires 0 missing **and** 0 extra;
   - **heals** on `(crit, amount)` — headlined on *missing* (ACT-reported heals we failed to
     reproduce); extras are expected (ACT reports only in-combat heals, and exact heal counts need
     combat-end detection, so our in-combat set is a superset).
   - swing-types ACT emits that the parser doesn't model yet (DoT/HoT ticks, etc.) are counted as
     `otherOracle`.

## Outputs (`tmp/mass-compare/`, gitignored)

| file | contents |
|---|---|
| `<name>.oracle.tsv` | ACT's parse of that log (ground truth) |
| `<name>.ours.tsv` | our parse of that log |
| `report.tsv` | one row per file: damage/heal counts, missing, extra |
| `details.txt` | sample mismatches per imperfect file |
| `summary.txt` | aggregate parity across all logs |

Outputs carry real combatant names; they are local-only and never committed.
