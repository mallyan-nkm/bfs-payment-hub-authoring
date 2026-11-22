#!/usr/bin/env bash
# Run the conformance kit on Temporal servers whose state is in SQL Server. From runtime/temporal-mssql,
# with SQL Server up (scripts/dev.sh sqlserver) and the binaries built (scripts/dev.sh build):
#
#   scripts/kit-on-sqlserver.sh                 all 50 scenarios
#   KIT_EXCLUDE="CK-AWAIT-02 ..." scripts/kit-on-sqlserver.sh
#
# On a real server the kit runs in real time, so its business waits are real waits (I-48), and run one
# after another the scenarios take more than two hours. Each harness polls the same task queue, so two
# harnesses cannot share a server. The script starts KIT_SERVERS servers (default 3), each with its own
# databases (temporal_kit<N>, temporal_visibility_kit<N>) and ports (offset 1000 x N), deals the scenarios
# to them by their measured real time, runs the shards side by side and prints one result.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)
: "${TEMPORAL_SQLSERVER_ADDR:=127.0.0.1:1433}"
: "${TEMPORAL_SQLSERVER_USER:=sa}"
: "${TEMPORAL_SQLSERVER_PASSWORD:=Th-Test-Only-1234}"
export TEMPORAL_SQLSERVER_ADDR TEMPORAL_SQLSERVER_USER TEMPORAL_SQLSERVER_PASSWORD
N=${KIT_SERVERS:-3}
EXCLUDE=" ${KIT_EXCLUDE:-} "
OUT=${KIT_OUT:-$ROOT/.dev/kit}
mkdir -p "$OUT"

# Every scenario with the real time it took on a server on SQL Server (seconds, measured 2026-11-21),
# longest first. 900 is the harness limit (TH_KIT_TIMEOUT_MIN=15): those scenarios wait longer.
SCENARIOS="CK-AWAIT-02:900 CK-AWAIT-03:900 CK-WAIT-01:900 CK-LOOKUP-01:900 CK-BLOCK-02:900 CK-BLOCK-03:900 CK-DEADLINE-01:600 CK-SIGNAL-01:600
CK-SIGNAL-02:600 CK-SIGNAL-03:600 CK-PIN-01:420 CK-PIN-02:300 CK-AWAIT-05:300
CK-FOREACH-02:180 CK-LOOP-01:120 CK-LOOP-02:120 CK-AWAIT-01:60 CK-SUP-01:60 CK-CLAIM-01:60 CK-DELTA-01:60
CK-TRACE-01:60 CK-BLOCK-01:20 CK-TIMEOUT-02:12 CK-UNKNOWN-01:8 CK-TIMEOUT-01:8
CK-SWITCH-01:5 CK-SWITCH-02:5 CK-PAR-01:5 CK-FOREACH-01:5 CK-SEQ-01:5 CK-HANDLE-01:5 CK-HANDLE-02:5
CK-HANDLE-03:5 CK-RETRY-01:4 CK-RETRY-02:4 CK-POLICY-01:4 CK-AWAIT-04:2 CK-COMP-01:2 CK-FINALLY-01:2
CK-SLOT-01:2 CK-SLOT-02:4 CK-HOOK-01:4 CK-HOOK-02:2 CK-HOOK-03:2 CK-HOOK-04:4 CK-HANDLER-01:2"

# Deal each scenario to the shard with the least time so far.
declare -a shard load
for ((i = 0; i < N; i++)); do shard[i]=""; load[i]=0; done
for entry in $SCENARIOS; do
  id=${entry%%:*} secs=${entry##*:}
  [[ "$EXCLUDE" == *" $id "* ]] && continue
  best=0
  for ((i = 1; i < N; i++)); do ((load[i] < load[best])) && best=$i; done
  shard[best]="${shard[best]} $id"
  load[best]=$((load[best] + secs))
done

tool=(bin/temporal-sql-tool --plugin sqlserver --ep "${TEMPORAL_SQLSERVER_ADDR%:*}" -p "${TEMPORAL_SQLSERVER_ADDR##*:}"
  -u "$TEMPORAL_SQLSERVER_USER" --pw "$TEMPORAL_SQLSERVER_PASSWORD")
if command -v temporal >/dev/null 2>&1; then cli() { temporal "$@"; }; else cli() { docker run --rm --network host temporalio/temporal:1.5.1 "$@"; }; fi

pids=()
cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT
for ((i = 0; i < N; i++)); do
  db=temporal_kit$i vdb=temporal_visibility_kit$i off=$(((i + 1) * 1000)) addr=127.0.0.1:$((7233 + (i + 1) * 1000))
  for pair in "$db:temporal" "$vdb:visibility"; do
    "${tool[@]}" --db "${pair%%:*}" drop-database -f >/dev/null 2>&1 || true
    "${tool[@]}" --db "${pair%%:*}" create-database >/dev/null 2>&1
    "${tool[@]}" --db "${pair%%:*}" setup-schema -v 0.0 >/dev/null 2>&1
    "${tool[@]}" --db "${pair%%:*}" update-schema -d "schema/sqlserver/${pair##*:}/versioned" >/dev/null 2>&1
  done
  TEMPORAL_PORT_OFFSET=$off TEMPORAL_DB=$db TEMPORAL_VISIBILITY_DB=$vdb \
    bin/temporal-server --config-file config/development-sqlserver.yaml --allow-no-auth start >"$OUT/server-$i.log" 2>&1 &
  pids+=($!)
  for _ in $(seq 1 60); do cli operator cluster health --address "$addr" 2>/dev/null | grep -q SERVING && break; sleep 1; done
  cli operator namespace create --namespace default --retention 24h --address "$addr" >/dev/null
  echo "server $i on $addr ($db, $vdb): ${load[i]} s of scenarios:${shard[i]}"
done

(cd ../tools/TransactHub.TemporalHarness && dotnet build -c Release --nologo -v q >/dev/null)
t0=$(date +%s)
runs=()
for ((i = 0; i < N; i++)); do
  [ -n "${shard[i]}" ] || continue
  (cd ../../studio && TH_TEMPORAL_ADDRESS=127.0.0.1:$((7233 + (i + 1) * 1000)) npx tsx engines/temporal/run-kit.ts ${shard[i]} >"$OUT/kit-$i.log" 2>&1) &
  runs+=($!)
  sleep 20 # the harness build at the start of each run
done
status=0
for r in "${runs[@]}"; do wait "$r" || status=1; done
echo "wall time $(($(date +%s) - t0)) s on $N servers"
grep -hE "^(PASS|FAIL)  " "$OUT"/kit-*.log | sort -k2
pass=$(cat "$OUT"/kit-*.log | grep -cE "^PASS  " || true)
total=$(cat "$OUT"/kit-*.log | grep -cE "^(PASS|FAIL)  " || true)
echo "$pass of $total conformance scenarios pass on Temporal with its state in SQL Server"
exit $status
