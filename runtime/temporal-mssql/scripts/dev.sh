#!/usr/bin/env bash
# Run Temporal on SQL Server for development. From runtime/temporal-mssql:
#
#   scripts/dev.sh build        build bin/temporal-server and bin/temporal-sql-tool
#   scripts/dev.sh sqlserver    start SQL Server 2022 in Docker (container th-temporal-mssql)
#   scripts/dev.sh schema       create both databases and set up or update their schemas
#   scripts/dev.sh server       run the server in the foreground (config/development-sqlserver.yaml)
#   scripts/dev.sh up           all four, the server in the background (log in .dev/server.log)
#   scripts/dev.sh down         stop the background server and remove the container
#
# Environment: TEMPORAL_SQLSERVER_ADDR [127.0.0.1:1433], TEMPORAL_SQLSERVER_USER [sa],
# TEMPORAL_SQLSERVER_PASSWORD [Th-Test-Only-1234, a test-only password for the local container],
# TEMPORAL_PORT_OFFSET [0], added to every port (the frontend is 7233 + offset).
set -euo pipefail
cd "$(dirname "$0")/.."

: "${TEMPORAL_SQLSERVER_ADDR:=127.0.0.1:1433}"
: "${TEMPORAL_SQLSERVER_USER:=sa}"
: "${TEMPORAL_SQLSERVER_PASSWORD:=Th-Test-Only-1234}"
: "${TEMPORAL_PORT_OFFSET:=0}"
TEMPORAL_FRONTEND_PORT=$((7233 + TEMPORAL_PORT_OFFSET))
: "${TEMPORAL_DB:=temporal}"
: "${TEMPORAL_VISIBILITY_DB:=temporal_visibility}"
export TEMPORAL_SQLSERVER_ADDR TEMPORAL_SQLSERVER_USER TEMPORAL_SQLSERVER_PASSWORD TEMPORAL_PORT_OFFSET TEMPORAL_DB TEMPORAL_VISIBILITY_DB
CONTAINER=${TEMPORAL_SQLSERVER_CONTAINER:-th-temporal-mssql}
IMAGE=${TEMPORAL_SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}

build() {
  mkdir -p bin
  go build -o bin/ ./cmd/...
}

sqlserver() {
  local port=${TEMPORAL_SQLSERVER_ADDR##*:}
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER" -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$TEMPORAL_SQLSERVER_PASSWORD" \
    -p "127.0.0.1:${port}:1433" "$IMAGE" >/dev/null
  for _ in $(seq 1 90); do
    if docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$TEMPORAL_SQLSERVER_PASSWORD" -Q "SELECT 1" >/dev/null 2>&1; then
      echo "SQL Server is up on $TEMPORAL_SQLSERVER_ADDR"
      return 0
    fi
    sleep 1
  done
  echo "SQL Server did not start" >&2
  docker logs "$CONTAINER" | tail -20 >&2
  return 1
}

schema() {
  local tool=(bin/temporal-sql-tool --plugin sqlserver --ep "${TEMPORAL_SQLSERVER_ADDR%:*}" -p "${TEMPORAL_SQLSERVER_ADDR##*:}"
    -u "$TEMPORAL_SQLSERVER_USER" --pw "$TEMPORAL_SQLSERVER_PASSWORD")
  for pair in "$TEMPORAL_DB:temporal" "$TEMPORAL_VISIBILITY_DB:visibility"; do
    local db=${pair%%:*} part=${pair##*:}
    "${tool[@]}" --db "$db" create-database
    "${tool[@]}" --db "$db" setup-schema -v 0.0
    "${tool[@]}" --db "$db" update-schema -d "schema/sqlserver/$part/versioned"
  done
}

server() {
  exec bin/temporal-server --config-file config/development-sqlserver.yaml --allow-no-auth start
}

up() {
  build
  sqlserver
  schema
  mkdir -p .dev
  nohup bin/temporal-server --config-file config/development-sqlserver.yaml --allow-no-auth start >.dev/server.log 2>&1 &
  echo $! >.dev/server.pid
  echo "Temporal server pid $(cat .dev/server.pid), frontend 127.0.0.1:${TEMPORAL_FRONTEND_PORT}, log .dev/server.log"
}

down() {
  if [ -f .dev/server.pid ]; then kill "$(cat .dev/server.pid)" 2>/dev/null || true; rm -f .dev/server.pid; fi
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
}

[ $# -gt 0 ] || set -- up
for cmd in "$@"; do "$cmd"; done
