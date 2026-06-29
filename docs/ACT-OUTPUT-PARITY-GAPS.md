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
parse-the-line + aggregate. Over the local corpus (68 logs, ~5.9M swings) the parser reproduces ACT's
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
| DoT/HoT ticks (3/5) | `24` — one combined tick per target per server tick (real amount; rotating source) | emitted from the log; **value is a producer difference** vs the plugin's estimate (see below) |

The residual on heals/action(1)/status(8) is **combat-window boundary** precision (exactly when ACT's
idle-end opens/closes the encounter), which is `ACT-decompiled` behavior — that is where the remaining
in-scope work is, if any.

## ACT does not parse or estimate DoTs — the producer does

`ACT-decompiled` contains **no** DoT/HoT/shield logic: the string `"Simulated"` appears nowhere in it,
there is no potency table, no tick model, and `FormActMain.AddCombatAction` applies **no** ally/enemy
or value filter — it routes every `MasterSwing` it is handed straight to `ActiveZone.AddCombatAction`
and sums it. Even the bucket name "Simulated DoTs (Out)" is registered by the *plugin*, not ACT. So
every DoT/HoT/shield **value** in the live ACT+plugin output is produced by the **producer** (the
plugin), never by ACT. A divergence in those values is therefore **not** a gap in `ACT-decompiled` —
there is nothing there to be missing; it is a difference between two producers (our native parser,
which reads the log, vs the real plugin, which synthesizes estimates).

## The right yardstick — ACT's output, not per-swing bags

ACT's *output* is the DPS table: swings are summed (by `SwingTypeToDamageTypeDataLinksOutgoing`) into
each combatant's totals — `Damage = Σ amounts of types {0,2,3}`, `Healed = Σ amounts of types {4,5,11}`
(amount > 0). Per-swing bag-diff is the wrong test for DoT/HoT/shield: the plugin splits one log tick
into multiple per-status `MasterSwing`s carrying a **potency estimate** (the `(*)` value: flat per
status, crit-independent), while the log carries one real combined tick. They differ swing by swing.

### Corpus output parity (68 logs, both streams summed per ACT's routing)

| Output component | Source | Parity vs plugin | Verdict |
|---|---|---|---|
| ACT aggregation engine | — | bit-exact | ✓ replicated (Slice 1 S5) |
| auto-attack damage (0) | log `21/22` | **100.000%** | ✓ in the log |
| ability damage (2) | log `21/22` | **99.993%** | ✓ in the log |
| direct heals (4) | log `21/22` | **100.258%** | ✓ in the log |
| HoT total (5) | log `24` | **99.681%** | ✓ nets out |
| **Healed excl. shields** | — | **99.975%** | ✓ |
| DoT value (3) | log `24` vs estimate | 146% corpus (see below) | producer, not ACT |
| shields (11) | not in log | 0 vs 20.28% of Healed | producer, not ACT |

### DoT — producer difference, content-correlated (not a parse bug)

The DoT ratio (ours/plugin) tracks **game patch**, not a fixed offset: 30101–30107 ≈ **100–102%**, then
30108 **201%**, 30109 **205%**, 30201 **320%**, 30202 **391%**, 30203 **347%**. Our per-tick value is
stable (~31k, the real combined-tick total); the plugin's per-swing estimate falls (9.2k → 6.0k) and
it emits relatively fewer of them. Proven structure: FFXIV emits **one combined DoT tick per target
per server tick**, stamped with a single *rotating* source over the combined amount (e.g. Enuo: 179
lines / 179 timestamps / 8 sources / 1 line per instant). Newer Ultimate content (e.g. Futures
Rewritten) adds large volumes of status-0 **PC→PC** ticks that our parser emits verbatim but the
plugin does not emit at all — ACT's consumer does not filter them (it has no such filter), so the
difference is entirely the producer choosing what to emit. Reproducing the plugin's per-source split
or its selective emission requires its potency model + status tracking — plugin logic, not in the log.

### Shields — `maxHP × potency`, synthesized, not logged

Every type-11 shield carries the `(*)` marker and a **constant-per-status** amount (Radiant Aegis
33889 every time). Proven: `33889 = floor(169449 × 0.20)` — the caster maxHP (169449) **is** in the
log, but the 20% potency and the shield synthesis are the plugin's. The shield value is **not** in the
log; grep confirms 33889/`0x8461` never appears on any Radiant Aegis line. Shields are 20.28% of ACT's
`Healed` and are the entire healing residual (heal+HoT alone is 99.975%).

### Environment ticks (fixed)

The parser drops type-24 ticks whose source is FFXIV's null actor `0xE0000000` (the source/target the
log leaves unattributed), matching the plugin, which attributes none of them to a combatant.

The plugin's per-status potency *estimate* (the `(*)` value) is plugin logic and not in the log, so it
is never reproduced; we emit the log's real combined-tick amounts and judge parity at the output. The
old `PotencySimulator.cs` (a port of the plugin's DoT/shield synthesis) stays removed, along with the
value-axis tables it consumed (`status-defs.tsv` / `action-potency.tsv` are not dumped by
`--dump-tables`). The action/status name tables (`actions.full.tsv`, `statuses.full.tsv`) stay — those
are FFXIV game data, not plugin logic.
