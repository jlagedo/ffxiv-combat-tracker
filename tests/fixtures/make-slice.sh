#!/usr/bin/env bash
# Build an anonymized combat-log slice for the differential parser test.
#
#   ./make-slice.sh <source Network_*.log> <out slice.log> [maxAbilityLines] [skipLines]
#
# Keeps only combat-relevant line types (01/02/03/04 setup + 21/22 abilities), in order,
# and BLANKS the human-name fields. The damage decode reads only the effect bytes (fields
# 8..45 of an ability line), so blanking names leaves every value/crit/flag intact while
# removing player/mob identities. The trailing checksum field is left as-is (the plugin's
# parser does not validate it). skipLines skips that many leading source lines so the slice
# can start inside continuous combat (where ACT's InCombat gate is on for heals too).
set -euo pipefail
src=${1:?source log}; out=${2:?out slice}; maxAbil=${3:-600}; skip=${4:-0}

awk -F'|' -v OFS='|' -v MAX="$maxAbil" -v SKIP="$skip" '
  { if (NR<=SKIP) next; t=$1 }
  t=="01" || t=="02" || t=="03" || t=="04" {
    if (NF>=4) $4="";                      # blank name/zone
    print; next
  }
  t=="21" || t=="22" {
    if (NF>=4) $4="";                      # source name
    if (NF>=8) $8="";                      # target name
    print
    if (++abil>=MAX) exit
    next
  }
' "$src" > "$out"

echo "wrote $(wc -l < "$out") lines to $out"
