#!/usr/bin/env bash
# The durability check: a workflow survives a server kill, because its state is in SQL Server.
# From runtime/temporal-mssql, with SQL Server up and the schemas set up (scripts/dev.sh sqlserver schema):
#
#   scripts/durability-check.sh
#
# 1. Start the server (bin/temporal-server, config/development-sqlserver.yaml) and the worker
#    (tools/durability). Register the default namespace if it is missing.
# 2. Start workflow durability.v1 with the Temporal CLI: step 1, a 40-second timer, step 2.
# 3. When the timer has started, kill the server with SIGKILL, show it is down, start it again.
# 4. Wait for the result. The workflow completes: the timer fired and step 2 ran after the restart.
#
# The Temporal CLI is `temporal` on the PATH, or else the temporalio/temporal:1.5.1 image.
set -euo pipefail
cd "$(dirname "$0")/.."

: "${TEMPORAL_PORT_OFFSET:=0}"
TEMPORAL_FRONTEND_PORT=$((7233 + TEMPORAL_PORT_OFFSET))
export TEMPORAL_PORT_OFFSET
ADDR="127.0.0.1:${TEMPORAL_FRONTEND_PORT}"
WAIT_NS=40000000000 # the timer, 40 s as a Go time.Duration
WF_ID="durability-$(date +%s)"
mkdir -p .dev

if command -v temporal >/dev/null 2>&1; then
  cli() { temporal "$@" --address "$ADDR"; }
else
  cli() { docker run --rm --network host temporalio/temporal:1.5.1 "$@" --address "$ADDR"; }
fi

start_server() {
  nohup bin/temporal-server --config-file config/development-sqlserver.yaml --allow-no-auth start >>.dev/server.log 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 60); do
    if cli operator cluster health 2>/dev/null | grep -q SERVING; then return 0; fi
    sleep 1
  done
  echo "server did not become healthy" >&2
  return 1
}

say() { echo "[$(date -u +%H:%M:%S)] $*"; }

[ -x bin/temporal-server ] && [ -x bin/durability-worker ] || {
  go build -o bin/ ./cmd/...
  (cd tools/durability && go build -o ../../bin/durability-worker .)
}

say "starting the server on SQL Server ($TEMPORAL_SQLSERVER_ADDR)"
start_server
say "server pid $SERVER_PID is SERVING on $ADDR"
cli operator namespace describe --namespace default >/dev/null 2>&1 || cli operator namespace create --namespace default --retention 24h

bin/durability-worker --address "$ADDR" >.dev/durability-worker.log 2>&1 &
WORKER_PID=$!
trap 'kill $WORKER_PID 2>/dev/null || true' EXIT

say "starting workflow $WF_ID with the Temporal CLI"
cli workflow start --namespace default --type durability.v1 --task-queue durability --workflow-id "$WF_ID" --input "$WAIT_NS"

for _ in $(seq 1 60); do
  if cli workflow show --namespace default --workflow-id "$WF_ID" 2>/dev/null | grep -q TimerStarted; then break; fi
  sleep 1
done
say "the timer has started; history so far:"
cli workflow show --namespace default --workflow-id "$WF_ID" | grep -E "^ +[0-9]+ " || true

say "killing the server (SIGKILL, pid $SERVER_PID)"
kill -9 "$SERVER_PID"
wait "$SERVER_PID" 2>/dev/null || true
sleep 3
if cli operator cluster health >/dev/null 2>&1; then echo "the server is still up" >&2; exit 1; fi
say "the server is down: no process, no frontend"
sleep 5

say "starting the server again"
start_server
say "server pid $SERVER_PID is SERVING; waiting for the workflow result"
RESULT=$(cli workflow result --namespace default --workflow-id "$WF_ID")
echo "$RESULT"
say "final history:"
cli workflow show --namespace default --workflow-id "$WF_ID" | grep -E "^ +[0-9]+ " || true
cli workflow describe --namespace default --workflow-id "$WF_ID" | grep -E "Status|StartTime|CloseTime|HistoryLength"
echo "$RESULT" | grep -q "after the restart" && say "PASS: $WF_ID completed after a SIGKILL restart of the server"
echo "server pid $SERVER_PID" >.dev/server.pid
