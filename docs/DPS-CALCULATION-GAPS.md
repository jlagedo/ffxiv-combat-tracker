# DPS Calculation Gaps — native parser combat-value parity

Actionable backlog for the **clean-room native parser** (`Fct.Parser.Native`) reproducing ACT's
combat-value *calculation* — the simulated DoT/HoT/shield swing **values** that drive DPS —
bit-for-bit against the real FFXIV_ACT_Plugin.

This is the **calculation axis only**. The separate **surface/binding axis** — making the unmodified
legacy plugins load and run on our clean-room ACT facade — lives in
[`ACT-INTERFACE-MAP.md`](ACT-INTERFACE-MAP.md) (Part 2). The two are independent.

- **Code under audit:** `Fct.Parser.Native` (`CombatLogParser` + `PotencySimulator`).
- **Authority:** the decompiled `.parse` assembly,
  `E:\dev\FFXIV_ACT_Plugin\ffxiv_act_plugin\decompiled\…\FFXIV_ACT_Plugin.Parse\` —
  `DoTSimulator.cs`, `DamageShieldSimulator.cs`, `PotencyStatusApplication.cs`, the `ParseStrategy*`
  handlers, `ReportCombatData.cs`. Full prose + measurement harness in [`TESTING.md`](TESTING.md).

| Column | Meaning |
|---|---|
| **Sev** | `BLOCK` hard limit / can't close · `BREAK` materially skews the DPS signal · `MINOR` small/edge |

## Scope

Deterministic, log-derived swing types are already bit-exact or near it (auto 99.98%, ability 99.85%,
power 99.90%, status 97.6%, heal 91%, action 91%, real ground-AoE DoT/HoT exact). The gaps are almost
entirely in ACT's *simulated* `(*)` swings — DoT/HoT ticks and damage shields the plugin synthesizes
from bundled potency data, which depend on internal per-source/per-target state we only partially
reproduce. Current simulated parity: **~95.6% of ACT's damage sum** (the DPS signal); per-tick
bit-exact ~1.5% (DoT) / ~28% (HoT).

| # | Gap | ACT behavior (source) | Ours | Sev | Cost |
|---|---|---|---|---|---|
| **P‑0** | **RNG crit/DH per tick** (uncloseable) | `DoTSimulator.SimulateTicks` draws crit/DH with time-seeded `new Random()`; ~29% of ticks carry a bit the real plugin wouldn't reproduce on re-run | Reproduce the exact individual-crit branch; non-crit ticks deterministic, crit ticks inherently divergent | BLOCK (hard limit) | — |
| **P‑1a** | **Buff bytes not source-correlated** | `ParseStrategyAddStatus.AddOrUpdateStatus` (190–199): `EffectByte0/1/2` pulled from the specific 21/22 `ActionEffect` matched by source+target+statusId | `_applyParams` keyed `(recipient, statusId)` — source dropped, overwritten by last applier → wrong `Param0/1/2` (observed spurious ×0.85 / ×1.51) | BREAK | low |
| **P‑1b** | **One-shot buff consumption** | `PotencyStatusApplication` sets `AppliedTimestamp`; consumed buffs (Kaiten/Boost/Kassatsu/LogosBoost/Harmonized/Tingling/Life Surge/Reassemble) skipped after first use (`ApplyStatusEffectToDamageEntry`:69) | No consume tracking — keeps applying until duration ends → inflated `potMult` | BREAK | low |
| **P‑1c** | **`CalculatedPotency`-side buffs ignored** | `DamageAddPotency` adds to potency, `PotencyMultiplier` ×it (`ApplyStatusEffectToDamageEntry`:76–93); heal adds `GetHealPotency` | Use raw action/DoT potency; fold neither → biased median + amount when active | BREAK | med |
| **P‑1d** | **Zone/category-limited multipliers skipped** | Applies only when `LimitToZoneId==ZoneId`, `LimitToActionCategory==actionCategory` match (line 74) | `BuffApplies` honors action-id/damage-type limits but **drops zone- and category-limited effects** (sim tracks neither) | BREAK | med-high |
| **P‑1e** | **Duration-window asymmetry** | source buffs `Duration+1s`, target debuffs exact `Duration` (`ApplyStatusEffects`:31/43); DoT statuses don't refresh `Param1` on reapply (line 184) | `Expired` uses `Duration+1` for both; `Param1` always refreshed | MINOR | low |
| **P‑1f** | **Override-status remapping** | `GetOverrideStatusIds`: Sacred Soil, Crest of Time, Wheel of Fortune, Improvisation, Undying Flame, Standard Finish Partner take bytes from a *different* status id | Literal statusId only | MINOR | low |
| **P‑2a** | **Crit-buff accumulation + rate exclusion** | `CritDhStatusApplication.CalculateCrit` accumulates `CritBuffAmount` (Chain Stratagem +10%, Inner Chaos/Life Surge = 100%, …); running crit mean updates **only when `CritBuffAmount==0`** (`CriticalHitDamage`:29) | `Sim.CritBuff` always 0; crit mean updates on every hit → skewed tick crit rate | BREAK | med |
| **P‑2b** | **Direct-hit buff accumulation** | `CalculateDirectHit` accumulates `DirectHitBuffs` (Full Metal Field=100%), subtracts before use | None | MINOR | low |
| **P‑2c** | **Medicated/Weakness calibration exclusion** | `IgnoreSomeStatusesExceptAtStart`: with Medicated/Weakness/BrinkOfDeath + >10 swings, `CalculatedPotency=0` → hit excluded from attack-power median | No exclusion; tincture/raise hits pollute median | MINOR | low |
| **P‑3a** | **Proc hits not excluded from calibration** | `SourcePotencyDamage`/`SourcePotencyHeal`:13 skip hits with non-empty `ProcActionName` | We don't identify procs → enter median | MINOR | med |
| **P‑3b** | **Per-target-index potency** | `GetDamagePotency(TargetIndex, combo)` / `GetHealPotency(TargetIndex)` — AoE secondary targets use falloff potency | Only index-0 potency dumped; we calibrate from `TargetIndex==0` hits only (unbiased but AoE-only players contribute nothing) | MINOR | med (needs per-index dump) |
| **P‑4** | **Special simulation paths** | Kardia/Kardion (`ChooseKardionEffect`), Pneuma deferred (`ProcessPneumaTick`), deferred events (`ProcessRemovedDeferredTick`/`ProcessDeferredEvents`), per-status tweaks Kaeshi Higanbana (+15), Blade of Valor (÷2), Wildfire (instant MaxTicks) — `CreateSimulatedDamageState`:114–143 | None of these special paths modelled | MINOR | low-med each |
| **P‑5** | **Shield `Potency` type** | `DamageShieldSimulator` Potency shields = HealMedian × potency × mult | Modelled but inherits heal-calibration + buff gaps; shield-specific cases (Succor/Brutal Shell variants) not handled. `TargetHpPercent`/`HealPercent` shields **bit-exact**. Shields overall ~60% bit-exact | BREAK | med |
| **P‑6** | **Threat swings (type 10)** | `ReportCombatData` emits enmity swings from EntryType 24/25/26 (`oracle=3,655`) | Classified `EffectKind.Threat`, no `CombatAction` emitted (enmity not value-decoded). Not a DPS signal | MINOR | low |

**Close order (cheapest × highest impact first):** P‑1a → P‑1b → P‑2a → P‑2c/P‑3a → P‑1c →
P‑1d → P‑1e/P‑1f/P‑4. Everything in P‑1/P‑2 is fully present in the decompiled source — no new
game-data extraction beyond what `--dump-tables` already produces. P‑0 (RNG), P‑3b (per-index
potency), and P‑6 (threat) are uncloseable or off the DPS-parity path.
