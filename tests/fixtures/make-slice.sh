#!/usr/bin/env bash
# Build an anonymized combat-log slice for the differential parser test.
#
#   ./make-slice.sh <source Network_*.log> <out slice.log> [maxAbilityLines] [skipLines]
#
# Keeps only combat-relevant line types (01/02/03/04 setup + 21/22 abilities), in order,
# and REPLACES each human name with a stable per-name pseudonym (real -> "Combatant-NNN").
# A consistent 1:1 mapping keeps distinct players distinct (so the parse does not condense
# them) while removing real identities. The damage decode reads only the effect bytes
# (fields 8..45 of an ability line), so renaming never changes any value/crit/flag. The
# trailing checksum field is left as-is (the plugin's parser does not validate it). skipLines
# skips that many leading source lines.
set -euo pipefail
src=${1:?source log}; out=${2:?out slice}; maxAbil=${3:-600}; skip=${4:-0}

awk -F'|' -v OFS='|' -v MAX="$maxAbil" -v SKIP="$skip" '
  function fake(n) {
    if (n == "") return n
    if (!(n in map)) map[n] = sprintf("Combatant-%03d", ++cnt)
    return map[n]
  }
  { if (NR<=SKIP) next; t=$1 }
  t=="01" { print; next }                  # zone name is game content, not a person
  t=="02" || t=="03" || t=="04" {
    if (NF>=4) $4=fake($4);                 # combatant name
    print; next
  }
  t=="21" || t=="22" {
    if (NF>=4) $4=fake($4);                 # source name
    if (NF>=8) $8=fake($8);                 # target name
    print
    if (++abil>=MAX) exit
    next
  }
' "$src" > "$out"

echo "wrote $(wc -l < "$out") lines to $out"
