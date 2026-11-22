#!/usr/bin/env bash
# Temporal on SQL Server as a bank would run it: the server connects with a least-privilege login (docs/ISSUES.md I-47).
# An admin sets the schemas up; the service login gets read, write and execute on the two Temporal databases and nothing
# else: no server role, no DDL. From runtime/temporal-mssql, with SQL Server up (scripts/dev.sh sqlserver) and the admin's
# credentials in TEMPORAL_SQLSERVER_USER [sa] and TEMPORAL_SQLSERVER_PASSWORD:
#
#   scripts/least-privilege.sh login       create the service login, with no server role
#   scripts/least-privilege.sh schema      the admin creates both databases and their schemas (scripts/dev.sh schema)
#   scripts/least-privilege.sh grant       the admin gives the login a user in each database: reader, writer, execute
#   scripts/least-privilege.sh prove       the login is no sysadmin, cannot create a database or a table, can read the schema version
#   scripts/least-privilege.sh durability  scripts/durability-check.sh with the server connecting as the login
#   scripts/least-privilege.sh suites      Temporal's persistence suites, the stores connecting as the login, the admin doing set-up
#   scripts/least-privilege.sh             all six, in that order
#
# Environment: TEMPORAL_SERVICE_USER [temporal_svc], TEMPORAL_SERVICE_PASSWORD [Th-Svc-Test-Only-1234, a test-only password],
# TEMPORAL_DB [temporal], TEMPORAL_VISIBILITY_DB [temporal_visibility], TEMPORAL_SQLSERVER_IMAGE (for sqlcmd).
set -euo pipefail
cd "$(dirname "$0")/.."

: "${TEMPORAL_SQLSERVER_ADDR:=127.0.0.1:1433}"
: "${TEMPORAL_SQLSERVER_USER:=sa}"
: "${TEMPORAL_SQLSERVER_PASSWORD:=Th-Test-Only-1234}"
: "${TEMPORAL_SERVICE_USER:=temporal_svc}"
: "${TEMPORAL_SERVICE_PASSWORD:=Th-Svc-Test-Only-1234}"
: "${TEMPORAL_DB:=temporal}"
: "${TEMPORAL_VISIBILITY_DB:=temporal_visibility}"
IMAGE=${TEMPORAL_SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}
ADMIN_USER=$TEMPORAL_SQLSERVER_USER
ADMIN_PASSWORD=$TEMPORAL_SQLSERVER_PASSWORD
HOST=${TEMPORAL_SQLSERVER_ADDR%:*}
PORT=${TEMPORAL_SQLSERVER_ADDR##*:}

say() { echo "[$(date -u +%H:%M:%S)] $*"; }

# sqlcmd from the server image, on the host's network: as <user> <password> [database] -Q <query>
sqlcmd_as() {
  local user=$1 pw=$2 db=$3; shift 3
  docker run --rm --network host --entrypoint /opt/mssql-tools18/bin/sqlcmd "$IMAGE" \
    -C -S "$HOST,$PORT" -U "$user" -P "$pw" -d "$db" -b -h -1 -W "$@"
}
admin() { sqlcmd_as "$ADMIN_USER" "$ADMIN_PASSWORD" "${2:-master}" -Q "SET NOCOUNT ON; $1"; }
service() { sqlcmd_as "$TEMPORAL_SERVICE_USER" "$TEMPORAL_SERVICE_PASSWORD" "${2:-master}" -Q "SET NOCOUNT ON; $1"; }

login() {
  say "creating login $TEMPORAL_SERVICE_USER, with no server role"
  admin "IF SUSER_ID(N'$TEMPORAL_SERVICE_USER') IS NULL CREATE LOGIN [$TEMPORAL_SERVICE_USER] WITH PASSWORD = N'$TEMPORAL_SERVICE_PASSWORD', CHECK_POLICY = OFF;"
}

schema() {
  say "the admin ($ADMIN_USER) sets up $TEMPORAL_DB and $TEMPORAL_VISIBILITY_DB"
  [ -x bin/temporal-sql-tool ] || go build -o bin/ ./cmd/...
  TEMPORAL_SQLSERVER_USER=$ADMIN_USER TEMPORAL_SQLSERVER_PASSWORD=$ADMIN_PASSWORD scripts/dev.sh schema
}

# The rights, one database at a time: what a bank's DBA grants (I-47). The same list as ServiceGrants in tests/.
grant() {
  for db in "$TEMPORAL_DB" "$TEMPORAL_VISIBILITY_DB"; do
    say "granting $TEMPORAL_SERVICE_USER db_datareader, db_datawriter and EXECUTE in $db"
    admin "IF USER_ID(N'$TEMPORAL_SERVICE_USER') IS NULL CREATE USER [$TEMPORAL_SERVICE_USER] FOR LOGIN [$TEMPORAL_SERVICE_USER];
           ALTER ROLE db_datareader ADD MEMBER [$TEMPORAL_SERVICE_USER];
           ALTER ROLE db_datawriter ADD MEMBER [$TEMPORAL_SERVICE_USER];
           GRANT EXECUTE TO [$TEMPORAL_SERVICE_USER];" "$db"
  done
}

expect_refused() {
  local what=$1; shift
  if "$@" >/dev/null 2>&1; then echo "the service login could $what; it must not" >&2; exit 1; fi
  say "refused, as it must be: $what"
}

prove() {
  local roles
  roles=$(service "SELECT IS_SRVROLEMEMBER('sysadmin') + IS_SRVROLEMEMBER('dbcreator') + IS_SRVROLEMEMBER('securityadmin') + IS_SRVROLEMEMBER('serveradmin');" | tr -d '[:space:]')
  [ "$roles" = "0" ] || { echo "the service login holds a server role ($roles)" >&2; exit 1; }
  say "$TEMPORAL_SERVICE_USER holds no server role"
  local owner
  owner=$(service "SELECT IS_ROLEMEMBER('db_owner') + IS_ROLEMEMBER('db_ddladmin');" "$TEMPORAL_DB" | tr -d '[:space:]')
  [ "$owner" = "0" ] || { echo "the service login is db_owner or db_ddladmin in $TEMPORAL_DB" >&2; exit 1; }
  say "$TEMPORAL_SERVICE_USER is neither db_owner nor db_ddladmin"
  expect_refused "create a database" service "CREATE DATABASE th_least_privilege_probe;"
  expect_refused "create a table in $TEMPORAL_DB" service "CREATE TABLE th_least_privilege_probe (i int);" "$TEMPORAL_DB"
  expect_refused "alter the schema of $TEMPORAL_DB" service "ALTER TABLE schema_version ADD th_probe int;" "$TEMPORAL_DB"
  expect_refused "read another database" service "SELECT TOP 1 name FROM msdb.dbo.sysjobs;"
  local v
  v=$(service "SELECT curr_version FROM schema_version;" "$TEMPORAL_DB" | tr -d '[:space:]')
  say "$TEMPORAL_SERVICE_USER reads the schema version of $TEMPORAL_DB: $v"
}

durability() {
  say "the durability check, the server connecting as $TEMPORAL_SERVICE_USER"
  TEMPORAL_SQLSERVER_USER=$TEMPORAL_SERVICE_USER TEMPORAL_SQLSERVER_PASSWORD=$TEMPORAL_SERVICE_PASSWORD scripts/durability-check.sh
}

suites() {
  say "Temporal's persistence suites: the stores as $TEMPORAL_SERVICE_USER, the set-up as $ADMIN_USER"
  mkdir -p .dev
  SQLSERVER_ADDR=$TEMPORAL_SQLSERVER_ADDR SQLSERVER_USER=$TEMPORAL_SERVICE_USER SQLSERVER_PASSWORD=$TEMPORAL_SERVICE_PASSWORD \
    SQLSERVER_ADMIN_USER=$ADMIN_USER SQLSERVER_ADMIN_PASSWORD=$ADMIN_PASSWORD \
    go test ./tests/ -run 'TestSQLServer' -count=1 -v -timeout 40m | tee .dev/least-privilege-suites.log
  scripts/suite-summary.sh .dev/least-privilege-suites.log
}

[ $# -gt 0 ] || set -- login schema grant prove durability suites
for cmd in "$@"; do "$cmd"; done
