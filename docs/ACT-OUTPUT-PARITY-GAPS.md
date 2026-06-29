# ACT Output-Parity — scope and status

> **What we replicate.** We replicate **Advanced Combat Tracker** (`E:\dev\ACT-decompiled`): how ACT
> reads a `Network_*.log`, collects `MasterSwing`s, runs its combat window, and aggregates
> encounters/DPS. The input is a log that FFXIV_ACT_Plugin already produced; our job is to feed our
> parser the same log and emit the **same output ACT emits** from it.
>
> **Two ground truths, and only two:** `ACT-decompiled` (ACT's own host / log-reading / aggregation
> behavior) and the **empirical oracle** — ACT's actual captured output (`--mass-oracle`), where the
> real plugin is used as an *opaque producer* to generate reference output. **We never read, mirror,
> or port logic from the FFXIV_ACT_Plugin decompile.** If any clean-room code was derived from it,
> that is a bug to fix, not a feature to refine.

## In scope — and its status

Everything ACT actually does. For nearly every swing the value is already in the log line, so it is
parse-the-line + aggregate. Over the local corpus (67 logs, ~5.9M swings) the parser reproduces ACT's
output bit-for-bit on the strict tuple `(swingType, crit, amount, special, attackType, attacker,
damageType, victim)`:

| Swing class | Source in the log | Status |
|---|---|---|
| Damage / auto-attack (0/2) | `21`/`22` effect bytes | auto **99.98%**, ability **99.85%** |
| Heals (4) | `21`/`22` heal effects | **95.1%** (residual is combat-window boundary) |
| Power/MP (6/7) | `21`/`22` MP effects | **99.9%** |
| Status (8) | `26` status-add | **99.4%** |
| Action (1) | `21`/`22` with no effect | **92.4%** (combat-window boundary residue) |
| Cancelled cast (2/4/1) | `23` | log-derived |
| DoT/HoT ticks (3/5) | `24` — the log carries a per-tick amount **and** a source for every tick (incl. status id `0`) | emitted from the log; sums to ACT's output (see below) |

The residual on heals/action(1)/status(8) is **combat-window boundary** precision (exactly when ACT's
idle-end opens/closes the encounter), which is `ACT-decompiled` behavior — that is where the remaining
in-scope work is, if any.

## The right yardstick — ACT's output, not per-swing bags

ACT's *output* is the DPS table: DoT/HoT swings are summed into each combatant's damage and healed
totals. Per-swing bag-diff is the wrong test for DoT/HoT, because the plugin splits one log tick line
into multiple per-status `MasterSwing`s and stamps each with a **potency estimate** (the `(*)` value:
flat per status, crit-independent) rather than the log's amount. The log tick line carries the real
per-tick amount (which varies tick to tick) and a source for *every* tick — so the parser emits a
DoT/HoT swing per sourced tick from the log's own values. The plugin's estimate and the log's amounts
differ swing by swing but **net out at the output level**.

Validated end to end (`Network_30108_20260502`, both swing streams aggregated through the **real ACT
`EncounterData`/`CombatantData` engine** via `tools/act-oracle`):

- encounter damage **180,946,519 vs ACT 180,863,774 (100.05%)**; DPS **3189.43 vs 3187.98 (100.05%)**;
- pure-damage combatants match **exactly**;
- healed is **~94%** in aggregate — see the open residual below.

`tools/mass-compare --timed` writes our swings in the oracle's 9-column timestamped format so they
feed the same real-ACT aggregation; the comparison is per-combatant damage/healed, not a per-swing bag.

## Open residual — damage shields and HoT attribution

The healing axis is not yet exact. Two known causes:

1. **Damage shields (log type `11`)** route into ACT's "Healed (Out)" bucket, and the parser emits
   **nothing** for them — the plugin still synthesizes shield absorption upstream, and we have not yet
   established what the log carries for it. This is the dominant healing gap.
2. **HoT estimate / source attribution** — the per-tick HoT amount in the log differs from the
   plugin's potency estimate (same as DoT), and incidental-heal source attribution is noisier, so
   per-combatant healed does not net out as cleanly as damage does.

The plugin's per-status potency *estimate* itself — the `(*)` value — is **plugin logic and not in the
log**, so it is never reproduced; we use the log's real per-tick amounts and judge parity at the
output. The old `PotencySimulator.cs` (a port of the plugin's DoT/shield simulation) stays removed,
along with the value-axis tables it consumed (`status-defs.tsv` / `action-potency.tsv` are not dumped
by `--dump-tables`). The action/status name tables (`actions.full.tsv`, `statuses.full.tsv`) stay —
those are FFXIV game data, not plugin logic.
