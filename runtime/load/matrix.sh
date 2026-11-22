#!/usr/bin/env bash
# The load matrix of docs/runtime/LOAD-TEST.md (R-09-30): each case on Temporal's persistence on SQL Server (our plugin)
# and on PostgreSQL, with the runtime's store on SQL Server and on PostgreSQL. Results are JSON files in $OUT, which
# is not committed; LOAD-TEST.md records the numbers.
#
#   runtime/load/matrix.sh <case> [<case> ...]       cases: small, large, parallel, xl, ceiling
#
# Environment: OUT [runtime/load/.run/results]; the two Temporal servers (runtime/load/temporal.sh), as
#   TEMPORAL_SQLSERVER=host:port and TEMPORAL_SQLSERVER_PID, TEMPORAL_POSTGRES=host:port and TEMPORAL_POSTGRES_PID;
#   the store's servers, as STORE_SQLSERVER (an ADO.NET connection string without Database) and STORE_POSTGRES (the same
#   for Npgsql); TH_LOAD_CONTAINERS (the database containers to measure); COMBOS [ss sp ps pp] (Temporal, then store:
#   s = SQL Server, p = PostgreSQL).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${OUT:-$HERE/.run/results}"
mkdir -p "$OUT"
: "${COMBOS:=ss sp ps pp}"

run_case() {
  local name="$1" files="$2" lines="$3" conc="$4" combo="$5" timers="${6-logical}"
  local t="${combo:0:1}" s="${combo:1:1}" db="th_load_${name}_${combo}_$(date +%s)"
  local addr pid tstore provider conn
  if [ "$t" = s ]; then addr="$TEMPORAL_SQLSERVER"; pid="$TEMPORAL_SQLSERVER_PID"; tstore=sqlserver; else addr="$TEMPORAL_POSTGRES"; pid="$TEMPORAL_POSTGRES_PID"; tstore=postgres; fi
  if [ "$s" = s ]; then provider=SqlServer; conn="$STORE_SQLSERVER;Database=$db"; else provider=PostgreSql; conn="$STORE_POSTGRES;Database=$db"; fi
  echo "== $name: $files file(s) x $lines line(s), concurrency $conc, Temporal on $tstore, store on $provider, timers ${timers:-server}"
  (cd "$HERE/../../studio" && TH_TEMPORAL_TIMERS="$timers" TH_TEMPORAL_ADDRESS="$addr" TH_LOAD_TEMPORAL_PID="$pid" TH_LOAD_TEMPORAL_STORE="$tstore" \
    TH_STORE_PROVIDER="$provider" TH_STORE_CONNECTION="$conn" \
    npx tsx ../runtime/load/run.ts --files "$files" --lines "$lines" --concurrency "$conc" --ack PT5S \
      --label "$name-$combo" --out "$OUT/$name-$combo.json" >/dev/null) || echo "   (the run failed; see $OUT/$name-$combo.json if written)"
  python3 - "$OUT/$name-$combo.json" <<'PY' || true
import json, sys
d = json.load(open(sys.argv[1])); r = d["results"]
print(f"   settled {r['settled']}/{r['settled'] + r['notSettled']}  {r['filesPerMinute']} files/min  {r['linesPerSecond']} lines/s  "
      f"p50 {r['latencySeconds']['p50']} s  p95 {r['latencySeconds']['p95']} s  p99 {r['latencySeconds']['p99']} s  engine {r['engineSeconds']} s")
PY
}

for c in "$@"; do
  for combo in $COMBOS; do
    case "$c" in
      small) run_case small 32 10 8 "$combo" ;;
      large) run_case large 1 1000 1 "$combo" ;;
      parallel) run_case parallel 4 1000 4 "$combo" ;;
      xl) run_case xl 1 10000 1 "$combo" ;;
      # Without the load harness's timer setting: the largest file that settles as the binding stands (I-77).
      ceiling) for n in 10 20 30; do run_case "ceiling$n" 1 "$n" 1 "$combo" ""; done ;;
      *) echo "unknown case $c" >&2; exit 2 ;;
    esac
  done
done
