#!/bin/sh
# Temporal on the SQL Server plugin (runtime/temporal-mssql), the laptop's default engine (R-09-76).
#   start.sh schema   create both databases if absent and bring their schemas to the plugin's version (as the databases' owner)
#   start.sh server   run the server, its frontend on 0.0.0.0:7233 for the other containers (as the least-privileged login)
# TEMPORAL_SQLSERVER_ADDR, TEMPORAL_SQLSERVER_USER and TEMPORAL_SQLSERVER_PASSWORD come from the environment only.
set -eu
cd /opt/th/temporal
case "${1:-server}" in
  schema)
    host=${TEMPORAL_SQLSERVER_ADDR%:*}; port=${TEMPORAL_SQLSERVER_ADDR##*:}
    for pair in "${TEMPORAL_DB:-temporal}:temporal" "${TEMPORAL_VISIBILITY_DB:-temporal_visibility}:visibility"; do
      db=${pair%%:*}; part=${pair##*:}
      tool() { bin/temporal-sql-tool --plugin sqlserver --ep "$host" -p "$port" -u "$TEMPORAL_SQLSERVER_USER" --pw "$TEMPORAL_SQLSERVER_PASSWORD" --db "$db" "$@"; }
      tool create-database 2>/dev/null || echo "database $db is there"
      tool setup-schema -v 0.0 2>/dev/null || echo "schema of $db is set up"
      tool update-schema -d "schema/sqlserver/$part/versioned"
    done
    ;;
  server)
    export TEMPORAL_BIND_IP=${TEMPORAL_BIND_IP:-0.0.0.0}
    exec bin/temporal-server --config-file config/development-sqlserver.yaml --allow-no-auth start
    ;;
  *) echo "usage: start.sh schema|server" >&2; exit 2 ;;
esac
