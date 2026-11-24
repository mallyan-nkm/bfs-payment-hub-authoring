#!/usr/bin/env bash
# The north-star video's store: a PostgreSQL 16 container of its own (no compose project, no image of ours), the runtime's store
# migrated into it, one synthetic salary file seeded through the store's own classes (store/Program.cs), and the runtime's login
# th_runtime granted SELECT and INSERT only, as deploy/laptop/sql/grants-postgres.sql grants it. The video reads it with psql in the
# container. The password is made at random here and lives only in this shell; nothing is written to a file.
#
#   demo/north-star/store.sh up     # start, migrate, seed, grant
#   demo/north-star/store.sh down   # remove the container and the build output
#
# TH_DEMO_STORE names the container (default th-nstar-store), TH_DEMO_STORE_PORT the loopback port (default 55433).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
name="${TH_DEMO_STORE:-th-nstar-store}"
port="${TH_DEMO_STORE_PORT:-55433}"

case "${1:-}" in
  up)
    password="$(openssl rand -hex 24)"
    docker run -d --name "$name" -e POSTGRES_PASSWORD="$password" -p "127.0.0.1:$port:5432" postgres:16-alpine >/dev/null
    for _ in $(seq 1 60); do docker exec "$name" pg_isready -U postgres -q && break; sleep 1; done
    sleep 2
    docker exec "$name" psql -U postgres -qc "CREATE DATABASE th_store"
    TH_DEMO_STORE_CONNECTION="Host=127.0.0.1;Port=$port;Database=th_store;Username=postgres;Password=$password" \
      dotnet run --project "$here/store/NorthStarStore.csproj" -c Release
    docker exec -i "$name" psql -U postgres -d th_store -q -v ON_ERROR_STOP=1 <<'SQL'
CREATE ROLE th_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE th_store TO th_runtime;
GRANT USAGE ON SCHEMA public TO th_runtime;
REVOKE CREATE ON SCHEMA public FROM th_runtime;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA public TO th_runtime;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA public FROM th_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO th_runtime;
SQL
    echo "store $name ready: docker exec $name psql -U th_runtime -d th_store"
    ;;
  down)
    docker rm -f "$name" >/dev/null 2>&1 || true
    rm -rf "$here/store/bin" "$here/store/obj"
    ;;
  *)
    echo "Usage: $0 up|down" >&2
    exit 2
    ;;
esac
