#!/usr/bin/env bash
# The Temporal server of a load run (R-09-38, docs/runtime/LOAD-TEST.md), on SQL Server (our plugin) or on PostgreSQL
# (Temporal's own postgres12 plugin), from the same binary: runtime/temporal-mssql's server, Temporal v1.32.2.
#
#   runtime/load/temporal.sh up sqlserver     create the databases and schemas, start the server in the background
#   runtime/load/temporal.sh up postgres
#   runtime/load/temporal.sh down             stop the server this script started
#
# Environment (defaults in brackets):
#   TH_LOAD_BIN        the folder with temporal-server and temporal-sql-tool [runtime/temporal-mssql/bin];
#                      build them with `cd runtime/temporal-mssql && go build -o bin/ ./cmd/...`
#   TH_LOAD_DIR        where the server's log and pid go [runtime/load/.run]
#   TEMPORAL_PORT_OFFSET  [0]: the frontend is 7233 + offset, the metrics 8000 + offset
#   SQL Server: TEMPORAL_SQLSERVER_ADDR [127.0.0.1:1433], TEMPORAL_SQLSERVER_USER [sa], TEMPORAL_SQLSERVER_PASSWORD
#   PostgreSQL: TEMPORAL_PG_ADDR [127.0.0.1:5432], TEMPORAL_PG_USER [postgres], TEMPORAL_PG_PASSWORD
#   TEMPORAL_DB / TEMPORAL_VISIBILITY_DB [temporal / temporal_visibility]
# Passwords come from the environment only; the containers of a local run use a test-only one.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MSSQL="$HERE/../temporal-mssql"
BIN="${TH_LOAD_BIN:-$MSSQL/bin}"
RUN="${TH_LOAD_DIR:-$HERE/.run}"
: "${TEMPORAL_PORT_OFFSET:=0}"
: "${TEMPORAL_DB:=temporal}"
: "${TEMPORAL_VISIBILITY_DB:=temporal_visibility}"
export TEMPORAL_PORT_OFFSET TEMPORAL_DB TEMPORAL_VISIBILITY_DB
export TEMPORAL_DYNAMIC_CONFIG="$MSSQL/config/dynamicconfig/development-sqlserver.yaml"
export TEMPORAL_LOG_LEVEL="${TEMPORAL_LOG_LEVEL:-warn}"
mkdir -p "$RUN"

schema_sqlserver() {
  : "${TEMPORAL_SQLSERVER_ADDR:=127.0.0.1:1433}" "${TEMPORAL_SQLSERVER_USER:=sa}"
  export TEMPORAL_SQLSERVER_ADDR TEMPORAL_SQLSERVER_USER
  local tool=("$BIN/temporal-sql-tool" --plugin sqlserver --ep "${TEMPORAL_SQLSERVER_ADDR%:*}" -p "${TEMPORAL_SQLSERVER_ADDR##*:}" -u "$TEMPORAL_SQLSERVER_USER" --pw "$TEMPORAL_SQLSERVER_PASSWORD")
  for pair in "$TEMPORAL_DB:temporal" "$TEMPORAL_VISIBILITY_DB:visibility"; do
    "${tool[@]}" --db "${pair%%:*}" create-database
    "${tool[@]}" --db "${pair%%:*}" setup-schema -v 0.0
    "${tool[@]}" --db "${pair%%:*}" update-schema -d "$MSSQL/schema/sqlserver/${pair##*:}/versioned"
  done
}

schema_postgres() {
  : "${TEMPORAL_PG_ADDR:=127.0.0.1:5432}" "${TEMPORAL_PG_USER:=postgres}"
  export TEMPORAL_PG_ADDR TEMPORAL_PG_USER
  # Temporal's own schema for PostgreSQL, from the module the server is built from.
  local schema
  schema="$(cd "$MSSQL" && go list -m -f '{{.Dir}}' go.temporal.io/server)/schema/postgresql/v12"
  local tool=("$BIN/temporal-sql-tool" --plugin postgres12 --ep "${TEMPORAL_PG_ADDR%:*}" -p "${TEMPORAL_PG_ADDR##*:}" -u "$TEMPORAL_PG_USER" --pw "$TEMPORAL_PG_PASSWORD")
  for pair in "$TEMPORAL_DB:temporal" "$TEMPORAL_VISIBILITY_DB:visibility"; do
    "${tool[@]}" --db "${pair%%:*}" create-database
    "${tool[@]}" --db "${pair%%:*}" setup-schema -v 0.0
    "${tool[@]}" --db "${pair%%:*}" update-schema -d "$schema/${pair##*:}/versioned"
  done
}

up() {
  local store="$1" config
  case "$store" in
    sqlserver) schema_sqlserver; config="$MSSQL/config/development-sqlserver.yaml" ;;
    postgres) schema_postgres; config="$HERE/config/temporal-postgres.yaml" ;;
    *) echo "up sqlserver | up postgres" >&2; exit 2 ;;
  esac
  nohup "$BIN/temporal-server" --config-file "$config" --allow-no-auth start >"$RUN/temporal-$store.log" 2>&1 &
  echo $! >"$RUN/temporal.pid"
  local port=$((7233 + TEMPORAL_PORT_OFFSET))
  for _ in $(seq 1 60); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
      echo "Temporal on $store: pid $(cat "$RUN/temporal.pid"), frontend 127.0.0.1:$port, metrics 127.0.0.1:$((8000 + TEMPORAL_PORT_OFFSET)), log $RUN/temporal-$store.log"
      return 0
    fi
    sleep 1
  done
  echo "Temporal did not start; see $RUN/temporal-$store.log" >&2
  tail -20 "$RUN/temporal-$store.log" >&2
  return 1
}

down() {
  if [ -f "$RUN/temporal.pid" ]; then
    kill "$(cat "$RUN/temporal.pid")" 2>/dev/null || true
    rm -f "$RUN/temporal.pid"
  fi
}

case "${1:-}" in
  up) up "${2:-}" ;;
  down) down ;;
  *) echo "usage: temporal.sh up sqlserver|postgres | down" >&2; exit 2 ;;
esac
