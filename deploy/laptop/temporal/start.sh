#!/bin/sh
# Temporal for the laptop stack: on the SQL Server plugin (runtime/temporal-mssql), the default engine (R-09-79), or on PostgreSQL
# (compose.postgres.yaml, TEMPORAL_DB_PLUGIN=postgres12).
#   start.sh server   run the server, its frontend on 0.0.0.0:7233 for the other containers, as the least-privileged login
# Its two databases' schemas are not set up here: the stack's migrate step (db-init, runtime/tools/TransactHub.Migrate) brings them
# up to the server's version once, at the stack's start, as the databases' owner (R-09-141). The server's login holds data rights
# only. TEMPORAL_SQLSERVER_* or TEMPORAL_POSTGRES_* (address, user, password) come from the environment only.
set -eu
cd /opt/th/temporal
case "${1:-server}" in
  server)
    export TEMPORAL_BIND_IP=${TEMPORAL_BIND_IP:-0.0.0.0}
    case "${TEMPORAL_DB_PLUGIN:-sqlserver}" in
      sqlserver) config=config/development-sqlserver.yaml ;;
      postgres12) config=laptop/development-postgres.yaml ;;
      *) echo "TEMPORAL_DB_PLUGIN is sqlserver or postgres12" >&2; exit 2 ;;
    esac
    exec bin/temporal-server --config-file "$config" --allow-no-auth start
    ;;
  *) echo "usage: start.sh server (the schemas are the migrate step's, R-09-141)" >&2; exit 2 ;;
esac
