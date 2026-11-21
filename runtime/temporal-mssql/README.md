# Temporal on SQL Server

This module is a persistence plugin that lets the Temporal server keep its state in Microsoft SQL Server 2022. It implements decision D2 and plan item W5 (P4) of `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`. Temporal itself supports MySQL, PostgreSQL, SQLite and Cassandra. It does not support SQL Server, and Temporal does not support this plugin. We maintain it.

Both stores are on SQL Server: the persistence store and the visibility store.

## Pinning

- `go.temporal.io/server` is pinned to exactly **v1.32.2** in `go.mod`. The plugin, the schema and the proof below are for that release only.
- **Every Temporal upgrade MUST re-run the whole proof** before it ships: the persistence suites, the durability check, the conformance kit and the WPS salary file. The CI job `temporal-sqlserver` in `.github/workflows/engines.yml` runs all four, the kit as its fast subset (see "CI" below).
- An upgrade that adds a schema version (Temporal's `schema/postgresql/v12/temporal/versioned/v1.20`, for example) needs the same change written for SQL Server in `schema/sqlserver/temporal/versioned/v1.20/`, and `Version` in `sqlplugin/sqlserver/plugin.go` raised to match.
- An upgrade that changes a store method in `common/persistence/sql/sqlplugin` needs the matching change here. Compare the new release's PostgreSQL plugin with the one this port started from.

## What is in it

| Path | What it is |
| --- | --- |
| `sqlplugin/sqlserver/` | The plugin. A port of Temporal's PostgreSQL plugin (`common/persistence/sql/sqlplugin/postgresql`) to T-SQL, on `github.com/microsoft/go-mssqldb`. It registers itself as `sqlserver`. |
| `schema/sqlserver/temporal/` | The persistence schema: `schema.sql`, `version` and `versioned/v1.19/`. |
| `schema/sqlserver/visibility/` | The visibility schema: `schema.sql`, `version` and `versioned/v1.14/`, written by `generate.py`. |
| `cmd/temporal-server/` | The Temporal server with the plugin. It is Temporal's own `cmd/server/main.go` plus one import, so flags and config loading are upstream's. |
| `cmd/temporal-sql-tool/` | Temporal's schema tool with the plugin, so `create-database`, `setup-schema` and `update-schema` work on SQL Server. |
| `config/development-sqlserver.yaml` | A single-node development configuration. It reads the address and password from the environment. |
| `scripts/dev.sh` | Builds, starts SQL Server in Docker, sets up the schemas and runs the server. |
| `scripts/durability-check.sh` | Kills the server in the middle of a workflow and shows the workflow completes. |
| `scripts/kit-on-sqlserver.sh` | Runs the conformance kit on several servers on SQL Server side by side, and prints one result. |
| `scripts/suite-summary.sh` | Counts the persistence suites' results per suite, from `go test -v` output. |
| `tests/` | Temporal's persistence suites wired to SQL Server. |
| `tools/durability/` | The worker of the durability check. It is a separate Go module, so the plugin's pins stay as they are. |

## How to run it

Go 1.26 (the toolchain `go.mod` names) and Docker are needed.

```
cd runtime/temporal-mssql
scripts/dev.sh up          # build, SQL Server on 127.0.0.1:1433, both schemas, the server in the background
docker run --rm --network host temporalio/temporal:1.5.1 operator namespace create --namespace default --address 127.0.0.1:7233
scripts/dev.sh down        # stop the server and remove the SQL Server container
```

To run several servers on one host, give each its own databases (`TEMPORAL_DB`, `TEMPORAL_VISIBILITY_DB`) and a `TEMPORAL_PORT_OFFSET` in thousands: every port moves by the offset, and the frontend is on 7233 plus the offset.

Against a SQL Server of your own, set `TEMPORAL_SQLSERVER_ADDR`, `TEMPORAL_SQLSERVER_USER` and `TEMPORAL_SQLSERVER_PASSWORD`, then run `scripts/dev.sh build schema server`. The schema steps by hand are:

```
bin/temporal-sql-tool --plugin sqlserver --ep <host> -p 1433 -u <user> --pw <password> --db temporal create-database
bin/temporal-sql-tool --plugin sqlserver --ep <host> -p 1433 -u <user> --pw <password> --db temporal setup-schema -v 0.0
bin/temporal-sql-tool --plugin sqlserver --ep <host> -p 1433 -u <user> --pw <password> --db temporal update-schema -d schema/sqlserver/temporal/versioned
# the same three for temporal_visibility, with schema/sqlserver/visibility/versioned
```

In a server configuration, a store is:

```yaml
sql:
  pluginName: "sqlserver"
  databaseName: "temporal"
  connectAddr: "sqlhost:1433"
  user: "temporal"
  password: "..."
  connectAttributes:        # optional go-mssqldb URL parameters
    encrypt: "true"
```

### What the database needs

- **Collation `Latin1_General_100_BIN2_UTF8`.** `create-database` creates the database with it. The server refuses to start on a database with another collation. VARCHAR then holds UTF-8 and compares byte by byte, as Temporal expects. NVARCHAR would double every key and push four primary keys over SQL Server's 900-byte limit.
- **`READ_COMMITTED_SNAPSHOT ON`.** `create-database` sets it. Plain reads then see the last committed row, as on PostgreSQL, and the plugin's locking reads still lock.
- **Ids of at most 255 bytes.** Workflow, run and task queue ids are VARCHAR(255) keys. In UTF-8 that is 255 ASCII characters, or fewer when an id has other characters. The development dynamic config sets `limit.maxIDLength` to 255.
- **Rights.** The user needs `CREATE DATABASE` for `create-database`, and `db_owner` on both databases for `setup-schema` and `update-schema`. The running server should need only `db_datareader` and `db_datawriter`. That was not tested: the tests here use `sa`.

## How the port works

The store methods, the row types and the tests are Temporal's. What differs is T-SQL:

| PostgreSQL | SQL Server |
| --- | --- |
| `INSERT ... ON CONFLICT DO UPDATE` (8 upserts) | `MERGE ... WITH (HOLDLOCK)`, one row per statement. A batch runs row by row in the caller's transaction. |
| `SELECT ... FOR UPDATE` | `WITH (UPDLOCK, ROWLOCK)` on each table read. No HOLDLOCK: a key-range lock deadlocked the queues (see below). |
| `SELECT ... FOR SHARE` | `WITH (REPEATABLEREAD, ROWLOCK)` |
| `LIMIT n` | `OFFSET 0 ROWS FETCH NEXT n ROWS ONLY`, or `TOP 1` |
| `(a, b) > (x, y)` | `a > x OR (a = x AND b > y)` |
| `$1` placeholders | `@p1` (sqlx binds named queries for the `sqlserver` driver as `@pN`) |
| `sqlx.In` | unchanged, then rebound to `@pN` |
| duplicate key (`23505`) | errors 2627 and 2601 |
| `BYTEA` | `VARBINARY`, `BINARY(16)` for ids |
| `TIMESTAMP` | `DATETIME2(6)`. A time is sent as `DATETIME2` in UTC: go-mssqldb would send `DATETIMEOFFSET`, and SQL Server converts that to a `DATETIME2` column by keeping the local clock time. |

Three more things are specific to SQL Server:

- **`chasm_node_maps`.** Its key would hold `chasm_path VARBINARY(1536)`, which is over both index key limits. The key holds `chasm_path_hash`, a persisted SHA-256 of the path, instead. The upsert matches on the hash and the path. The path is sent as bytes, because a Go string goes to SQL Server as NVARCHAR, which does not convert to VARBINARY.
- **Batches.** SQL Server takes at most 2,100 parameters in one request. A batch insert is cut into requests of at most 2,000.
- **String parameters.** go-mssqldb sends a Go string as NVARCHAR, and the id columns are VARCHAR. On this Windows collation SQL Server still seeks the key through the conversion: the plans of the execution lock, the transfer task read and the timer task read are clustered index seeks. The upserts cast ids to VARCHAR in their source, so their match is exact.

### Visibility

Visibility is on SQL Server too. The design:

- **One computed column per search attribute**, over `JSON_VALUE(search_attributes, '$.Name')`, as PostgreSQL and MySQL have one generated column each. A column that is indexed is `PERSISTED`. Keyword, Int, Double, Bool and Datetime attributes are indexed with the list order, as on PostgreSQL.
- **The list order** `COALESCE(close_time, '9999-12-31 23:59:59')` is a persisted, indexed column, `close_time_or_max`.
- **Keyword List attributes** (a JSON array, such as `BuildIds`) have no indexable column form. A trigger keeps one row per value in `executions_visibility_keyword_lists`, keyed on a SHA-256 of the value. `BuildIds = 'x'` becomes an indexed `EXISTS` on that table.
- **Text attributes** match when any word of the query is a word of the text, as PostgreSQL's `tsvector` match does. SQL Server's Linux image has no full-text search, so this is a `STRING_SPLIT` over the rows the other filters leave. It is not indexed (see `docs/ISSUES.md` I-46).
- **The query converter** (`sqlplugin/sqlserver/query_converter.go`) plugs into Temporal's unified converter. It writes `1` and `0` for Bool, plain T-SQL string literals, the keyword list `EXISTS`, the text match, and `OFFSET ... FETCH`.

Visibility passes Temporal's visibility suites (below), and the server's list and count queries work on it: `WorkflowType = 'x' AND ExecutionStatus = 'Completed'`, `WorkflowId STARTS_WITH 'x'`, and `BuildIds IN (...)`.

## What is proved

All of it was run on 2026-11-21 against `mcr.microsoft.com/mssql/server:2022-latest` (SQL Server 2022, 16.0.4325.4) in Docker.

### 1. Temporal's persistence suites

`tests/sqlserver_test.go` is Temporal's `common/persistence/tests/postgresql_test.go` with the PostgreSQL set-up replaced by SQL Server's. It runs every suite the PostgreSQL wiring runs, 45 of them. Two helpers are unexported in Temporal and are copied (`tests/copied_*_test.go`, changed only in the package name and one qualified call).

```
SQLSERVER_ADDR=127.0.0.1:1433 SQLSERVER_PASSWORD=... go test ./tests/ -run 'TestSQLServer$' -count=1 -v | tee run.log
scripts/suite-summary.sh run.log
```

The result: **45 of 45 suites pass. 479 test cases pass, none fail, 2 are skipped.**

| Suite (as in Temporal's PostgreSQL wiring) | Pass | Fail | Skip |
| --- | ---: | ---: | ---: |
| ClosedConnectionError | 1 | 0 | 0 |
| ClusterMetadataPersistence | 7 | 0 | 0 |
| ExecutionMutableStateStoreSuite | 45 | 0 | 1 |
| ExecutionMutableStateTaskStoreSuite | 15 | 0 | 0 |
| FairTaskQueueSuite | 10 | 0 | 0 |
| HistoryCurrentChasmExecutionSuite | 9 | 0 | 0 |
| HistoryCurrentExecutionSuite | 9 | 0 | 0 |
| HistoryExecutionActivitySuite | 10 | 0 | 0 |
| HistoryExecutionBufferSuite | 5 | 0 | 0 |
| HistoryExecutionChasmSuite | 5 | 0 | 0 |
| HistoryExecutionChildWorkflowSuite | 10 | 0 | 0 |
| HistoryExecutionRequestCancelSuite | 10 | 0 | 0 |
| HistoryExecutionSignalRequestSuite | 10 | 0 | 0 |
| HistoryExecutionSignalSuite | 10 | 0 | 0 |
| HistoryExecutionSuite | 10 | 0 | 0 |
| HistoryExecutionTimerSuite | 10 | 0 | 0 |
| HistoryNodeSuite | 7 | 0 | 0 |
| HistoryReplicationDLQTaskSuite | 10 | 0 | 0 |
| HistoryReplicationTaskSuite | 10 | 0 | 0 |
| HistoryShardSuite | 8 | 0 | 0 |
| HistoryStoreSuite | 12 | 0 | 0 |
| HistoryTimerTaskSuite | 10 | 0 | 0 |
| HistoryTransferTaskSuite | 10 | 0 | 0 |
| HistoryTreeSuite | 5 | 0 | 0 |
| HistoryV2PersistenceSuite | 5 | 0 | 0 |
| HistoryVisibilityTaskSuite | 10 | 0 | 0 |
| MatchingFairTaskQueueSuite | 10 | 0 | 0 |
| MatchingTaskQueueSuite | 10 | 0 | 0 |
| MatchingTaskSuite | 9 | 0 | 0 |
| MatchingTaskV2Suite | 2 | 0 | 0 |
| MetadataPersistenceSuiteV2 | 22 | 0 | 1 |
| NamespaceSuite | 12 | 0 | 0 |
| NexusEndpointPersistence | 9 | 0 | 0 |
| QueueMessageSuite | 10 | 0 | 0 |
| QueueMetadataSuite | 7 | 0 | 0 |
| QueuePersistence | 4 | 0 | 0 |
| QueueV2 | 60 | 0 | 0 |
| ShardStoreSuite | 4 | 0 | 0 |
| TaskQueueFairTaskSuite | 3 | 0 | 0 |
| TaskQueueSuite | 10 | 0 | 0 |
| TaskQueueTaskSuite | 4 | 0 | 0 |
| TaskQueueUserDataSuite | 2 | 0 | 0 |
| VisibilityPersistenceSuite | 15 | 0 | 0 |
| VisibilitySuite | 22 | 0 | 0 |
| TestClusterDropsDatabaseWithOpenConnection (no sub-tests) | 1 | 0 | 0 |
| **Total test cases** | **479** | **0** | **2** |

The suites were run five times. One run found a deadlock in QueueV2 (`DeleteSameRangeTwice`, parallel enqueues): the "last message id" query used `WITH (UPDLOCK, HOLDLOCK)`, and HOLDLOCK locks the key range, which on an empty queue is shared with other queues. It now takes an update lock on the last row only, as `FOR UPDATE` does on PostgreSQL. The queue suites then passed eight times in a row, and the whole set twice more. The table is from the last run.

The two skips are Temporal's own, for every SQL store: `TestListConcreteExecutions` ("only implemented by cassandra persistence") and `TestRenameNamespaceCassandra`. No test was skipped or changed for SQL Server.

The plugin's own tests pass as well: `TestSQLServerPluginDatabaseSettings` (`tests/sqlserver_plugin_test.go`: `create-database` gives the UTF-8 binary collation and snapshot reads, and the start-up check refuses a database in another collation), and `go test ./sqlplugin/...`, which needs no database (the schema files and versions agree, and the converter writes T-SQL).

### 2. The server runs on SQL Server, and a workflow survives a kill

`scripts/durability-check.sh` starts the server on SQL Server and a worker, starts workflow `durability.v1` with the Temporal CLI (an activity, a 40-second timer, a second activity), kills the server with SIGKILL while the timer runs, starts it again and waits for the result:

```
[16:23:13] starting the server on SQL Server (127.0.0.1:14330)
[16:23:13] server pid 13677 is SERVING on 127.0.0.1:7233
[16:23:13] starting workflow durability-1795278193 with the Temporal CLI
Running execution:
  WorkflowId  durability-1795278193
  RunId       01a1feeb-45c5-7b83-af21-8df8527bfc02
  Type        durability.v1
  Namespace   default
  TaskQueue   durability
[16:23:14] the timer has started (event 11, TimerStarted)
[16:23:14] killing the server (SIGKILL, pid 13677)
[16:23:18] the server is down: no process, no frontend
[16:23:23] starting the server again
[16:23:24] server pid 14243 is SERVING; waiting for the workflow result
Results:
  Status          COMPLETED
  Result          "before the restart ran at 2026-11-21T16:23:14Z (attempt 1); after the restart ran at 2026-11-21T16:23:54Z (attempt 1)"
  ResultEncoding  json/plain
[16:23:54] final history:
    1  2026-11-21T16:23:14Z  WorkflowExecutionStarted
    2  2026-11-21T16:23:14Z  WorkflowTaskScheduled
    3  2026-11-21T16:23:14Z  WorkflowTaskStarted
    4  2026-11-21T16:23:14Z  WorkflowTaskCompleted
    5  2026-11-21T16:23:14Z  ActivityTaskScheduled
    6  2026-11-21T16:23:14Z  ActivityTaskStarted
    7  2026-11-21T16:23:14Z  ActivityTaskCompleted
    8  2026-11-21T16:23:14Z  WorkflowTaskScheduled
    9  2026-11-21T16:23:14Z  WorkflowTaskStarted
   10  2026-11-21T16:23:14Z  WorkflowTaskCompleted
   11  2026-11-21T16:23:14Z  TimerStarted
   12  2026-11-21T16:23:54Z  TimerFired
   13  2026-11-21T16:23:54Z  WorkflowTaskScheduled
   14  2026-11-21T16:23:54Z  WorkflowTaskStarted
   15  2026-11-21T16:23:54Z  WorkflowTaskCompleted
   16  2026-11-21T16:23:54Z  ActivityTaskScheduled
   17  2026-11-21T16:23:54Z  ActivityTaskStarted
   18  2026-11-21T16:23:54Z  ActivityTaskCompleted
   19  2026-11-21T16:23:54Z  WorkflowTaskScheduled
   20  2026-11-21T16:23:54Z  WorkflowTaskStarted
   21  2026-11-21T16:23:54Z  WorkflowTaskCompleted
   22  2026-11-21T16:23:54Z  WorkflowExecutionCompleted
  StartTime             41 seconds ago
  CloseTime             now
  HistoryLength         22
  OriginalStartTime  41 seconds ago
  Status          COMPLETED
[16:23:55] PASS: durability-1795278193 completed after a SIGKILL restart of the server
```

The timer was started before the kill and fired after the restart, from what SQL Server held. In the database afterwards: collation `Latin1_General_100_BIN2_UTF8`, `is_read_committed_snapshot_on = 1`, and the closed execution in `temporal_visibility.dbo.executions_visibility` with status 2 (completed).

### 3. The conformance kit

`studio/engines/temporal/run-kit.ts` with `TH_TEMPORAL_ADDRESS` runs the harness on a real server, in real time: a business wait is a real wait. Run one after another, the 46 scenarios take more than two hours, and each harness polls the same task queue, so two cannot share a server. The kit was therefore run on four servers on SQL Server side by side, each with its own databases. `scripts/kit-on-sqlserver.sh` does the same.

The result: **43 of 46 scenarios pass on Temporal with its state in SQL Server. None fails on its trace or its checks. The other 3 wait four business hours to three days, and did not finish in real time within the harness limit.**

| Run | Server (own databases) | Scenarios | Result | Wall time |
| --- | --- | --- | --- | --- |
| Shard 1 | 127.0.0.1:7233 | 14, CK-AWAIT-02 among them | 13 pass, CK-AWAIT-02 stopped at the 15-minute limit | 2,733 s |
| Shard 2 | 127.0.0.1:7333 | 16, CK-AWAIT-03 and CK-LOOKUP-01 among them | 14 pass, those two stopped at the limit | 3,121 s |
| Shard 3 | 127.0.0.1:7433 | 16, CK-WAIT-01, CK-BLOCK-02 and CK-BLOCK-03 among them | 13 pass, those three stopped at the limit | 4,052 s |
| Again, limit 75 minutes | 127.0.0.1:7233 | CK-WAIT-01 (waits one hour) | pass | 3,602 s |
| Again, limit 75 minutes | 127.0.0.1:7433 | CK-BLOCK-02 (waits one hour) | pass | 3,601 s |
| Again, limit 140 minutes | 127.0.0.1:7333, rebuilt with the queue fix | CK-AWAIT-03 (the late reply comes after two hours) | pass | 7,202 s |

The shards ran side by side, so the whole kit took about 70 minutes of wall time, plus the reruns of the scenarios that wait one and two hours. The 43 that pass are every scenario but CK-AWAIT-02, CK-BLOCK-03 and CK-LOOKUP-01.

CK-FOREACH-02, the 10,000-item batch in chunks that continue as new (some 20,000 activity runs), passes in about three minutes on SQL Server. The Java test server cannot run it at all.

The scenarios that wait hours or days take that long in real time on any real server, whatever its database. When the harness stopped them, their workflows were waiting on these timers on the server on SQL Server: CK-AWAIT-02 259,198 s (three days, on the business calendar), CK-LOOKUP-01 172,798 s (two days), CK-BLOCK-03 14,398 s (four hours). CK-AWAIT-03 (two hours) passed when it was given the time. Their logic passes on the time-skipping server (`studio/engines/temporal/README.md`), and the store under them is what the persistence suites prove. `docs/ISSUES.md` I-48 asks how such scenarios should be proved on a real server.

In CI the kit runs as a fast subset: the 40 scenarios that wait less than a business hour, on three servers, in about 30 minutes. The six long-wait scenarios are left out there, and the job says so.

### 4. The WPS salary file, with the payments store on SQL Server too

`studio/test/blackbox/e2e-wps-temporal.test.ts` now takes `TH_TEMPORAL_ADDRESS`: the deployment's harness then runs on that server in real time, and the test server is not set up. Its store case starts a second SQL Server for the runtime's payments store:

```
cd studio
TH_TEMPORAL_ADDRESS=127.0.0.1:7233 npx vitest run --config vitest.blackbox.config.ts \
  test/blackbox/e2e-wps-temporal.test.ts -t "database on SqlServer"
```

The result, on a server on SQL Server:

```
Temporal: the server at 127.0.0.1:8233, in real time
 ✓ ... keeps the payment in the database on SqlServer: accepted as ISO 20022, every payload change a fact 612429ms
      Tests  1 passed | 5 skipped (6)
```

The test checks that the payment settles with the full trace (validate to `end:settled`), and that the store line reads `store: SqlServer holds 1 bulk(s), 1 payment(s), ...; ... payload(s) rebuilt from their facts, chains unbroken, all match the run`. The run takes about ten minutes because the scheme's acknowledgement comes ten business minutes after the file. Temporal's databases held the run afterwards: the front door and five interpreter workflows (the corporate payment, the WPS leg and the hooks), all completed, in 197 history nodes.

### 5. CI

The job `temporal-sqlserver` in `.github/workflows/engines.yml` builds the plugin, runs the unit tests and the persistence suites against a SQL Server 2022 service container, sets up the schemas with `temporal-sql-tool`, runs the durability check, then runs the kit's fast subset of 40 scenarios on three servers on SQL Server (`scripts/kit-on-sqlserver.sh`, about 30 minutes) and the WPS salary file. It leaves out the six scenarios that wait a business hour or more (`docs/ISSUES.md` I-48). The step name says so.

## What is not proved

- **Load.** Nothing here measures throughput or tail latency on SQL Server against PostgreSQL. The studio offers functional tests only, so this belongs to a runtime performance test.
- **Multi-node and multi-cluster.** The proof uses one node. Replication between clusters (the DLQ and replication tables) passes the suites but was not run between two servers.
- **Azure SQL Database and Managed Instance.** The plugin uses nothing outside SQL Server 2022, but only the container was tested.
- **Elasticsearch visibility** is not affected: a bank that has it can keep visibility there and the persistence store on SQL Server.
