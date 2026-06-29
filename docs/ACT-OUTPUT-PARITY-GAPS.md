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
| Real ground-AoE DoT/HoT (3/5) | `24` with **status id ≠ 0** — the log carries the true amount | exact |

The residual on heals/action(1)/status(8) is **combat-window boundary** precision (exactly when ACT's
idle-end opens/closes the encounter), which is `ACT-decompiled` behavior — that is where the remaining
in-scope work is, if any.

## Out of scope — personal DoT/HoT ticks and damage shields

The game sends one **combined, unattributed** personal DoT tick per target (log type `24`, **status
id `0`** — no per-source / per-status breakdown). The per-source split that appears in ACT's output is
**not ACT's work**: `ACT-decompiled` contains no DoT/HoT/shield simulation. The plugin synthesizes
those values upstream (from its own bundled potency tables and an internal attack-power estimate) and
hands the finished `MasterSwing`s to ACT.

Because that synthesis is **plugin logic** and the inputs are **not in the log**, it is out of scope:
reproducing it would mean porting FFXIV_ACT_Plugin, which we do not do. Our parser emits only the
DoT/HoT ticks the log actually carries (the real, attributed ground-AoE ticks above). Personal
per-source DoT/HoT/shield amounts are not reproduced.

The old `PotencySimulator.cs` (a port of the plugin's DoT/shield simulation) **has been removed**,
along with the value-axis tables it consumed (`status-defs.tsv` / `action-potency.tsv` are no longer
dumped by `--dump-tables`) and the simulated-value parity metric in `tools/mass-compare`. The
action/status name tables (`actions.full.tsv`, `statuses.full.tsv`) stay — those are FFXIV game data,
not plugin logic.
