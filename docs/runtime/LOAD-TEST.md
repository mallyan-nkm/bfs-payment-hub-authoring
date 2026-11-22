# The runtime's load test

Status: first run, 22 November 2026. The owner decided that a load test runs before any bank goes live ([I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side)). This page says how the harness works, what it measured here and under what conditions, what it found, the targets proposed for the owner, and what to run at bank scale. Requirement R-09-30.

The studio offers functional tests only (`spec/CLAUDE.md`). This harness is the runtime's, in `runtime/load/`, and is not part of the studio.

## What the harness does

`runtime/load/run.ts` drives synthetic WPS salary files through the runtime on Temporal, as a deployment runs them:

1. It exports the factory's corporate payment workflow (`p.wf.corporate.v1`, with the WPS leg `uae.wps.wf.salary-file-out.v1` in its slot) for the example bank with the Temporal binding and the factory simulators, as the studio's deploy runner does.
2. It starts the package's four workers (one Node process per task queue), and sends each file through the front door's intake on them.
3. One Temporal harness process (`TransactHub.TemporalHarness --load`, `LoadRunner`) runs every file side by side, up to a set concurrency, with one set of Temporal workers and one claim check, its payloads in the store (`TH_STORE_PROVIDER`, `TH_STORE_CONNECTION`).
4. It reports per file the wall time from acceptance into the store to the end, and the outcome; and for the run the CPU seconds of the harness, the workers, the Temporal server and each database container (from its cgroup), and the IO each database reports of itself (SQL Server `sys.dm_io_virtual_file_stats`; PostgreSQL `pg_stat_io` and its WAL bytes).

Every file is synthetic: one employer, valid UAE IBANs computed by the harness, employee numbers from a counter, AED 1,000.00 per line. `runtime/load/matrix.sh` runs the cases on every pairing of Temporal's persistence (our SQL Server plugin, `runtime/temporal-mssql`, or Temporal's own PostgreSQL plugin) and the runtime's store (SQL Server or PostgreSQL). `runtime/load/temporal.sh` starts the Temporal server for each, from the same binary (Temporal v1.32.2), with the same shards, pools, ports and dynamic configuration (`runtime/load/config/temporal-postgres.yaml` mirrors the SQL Server one).

```
cd runtime/temporal-mssql && go build -o bin/ ./cmd/...
docker run -d --name th-load-mssql -m 3g -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=<test-only> -p 127.0.0.1:14433:1433 mcr.microsoft.com/mssql/server:2022-latest
docker run -d --name th-load-pg -m 3g -e POSTGRES_PASSWORD=<test-only> -p 127.0.0.1:15432:5432 postgres:16-alpine -c max_connections=300
TEMPORAL_PORT_OFFSET=2000 TEMPORAL_SQLSERVER_ADDR=127.0.0.1:14433 TEMPORAL_SQLSERVER_PASSWORD=<test-only> runtime/load/temporal.sh up sqlserver
TH_LOAD_DIR=runtime/load/.run-pg TEMPORAL_PORT_OFFSET=3000 TEMPORAL_PG_ADDR=127.0.0.1:15432 TEMPORAL_PG_PASSWORD=<test-only> runtime/load/temporal.sh up postgres
TEMPORAL_SQLSERVER=127.0.0.1:9233 TEMPORAL_SQLSERVER_PID=<pid> TEMPORAL_POSTGRES=127.0.0.1:10233 TEMPORAL_POSTGRES_PID=<pid> \
  STORE_SQLSERVER="Server=127.0.0.1,14433;User Id=sa;Password=<test-only>;TrustServerCertificate=true" \
  STORE_POSTGRES="Host=127.0.0.1;Port=15432;Username=postgres;Password=<test-only>" \
  TH_LOAD_CONTAINERS="mssql=th-load-mssql,postgres=th-load-pg" runtime/load/matrix.sh small large parallel ceiling
```

The results are JSON files under `runtime/load/.run/results/` (not committed). With the local monitoring stack up (`runtime/observability/`) and `OTEL_EXPORTER_OTLP_ENDPOINT` set, the same run shows on the dashboards.

### Two settings of the harness, and why

- **The scheme's acknowledgement after 5 seconds** (`--ack PT5S`). The factory simulator answers after `PT10M`, on a real server ten real minutes. A load run shortens it in the deployed simulator only, or it would measure the wait.
- **`TH_TEMPORAL_TIMERS=logical`.** Without it a file of more than a few lines fails on a real Temporal server ([I-77](../ISSUES.md#i-77-on-a-real-temporal-server-a-steps-attempt-timeout-is-spent-by-processing-time)). The ceiling runs below are without it.

## The machine and the conditions

| | |
| --- | --- |
| Machine | A shared cloud container: 4 vCPUs (Intel Xeon @ 2.10 GHz, one thread per core), 15 GB of memory, of which 2 to 3 GB were free, no swap, an overlay disk with 4 to 9 GB free |
| Neighbours | Five other agents ran builds, test suites and containers on the same 4 vCPUs throughout. The load average was 15 to 37 during the runs (4 would be a full machine). There was a host-wide stall at 06:36 to 06:40 UTC (both Temporal servers lost their database connections) and the disk filled at 07:01 UTC, which stopped the PostgreSQL container. Runs caught in either are listed as such and not used for the comparison. |
| Temporal | v1.32.2, one node per store, 4 history shards, persistence pool 20, visibility pool 10, log level warn |
| SQL Server | 2022 Developer Edition (16.0.4325.4) in Docker, memory limit 3 GB, defaults otherwise; Temporal's databases and the store's on the same instance |
| PostgreSQL | 16.16 (alpine) in Docker, memory limit 3 GB, `max_connections=300`, defaults otherwise (`shared_buffers` 128 MB, `synchronous_commit` on) |
| Runtime | .NET 10, the Temporal harness in Release; Temporal workers with 500 concurrent activities and 200 concurrent workflow tasks per queue; Node 22 workers, one per queue (four) |
| Workflow | `p.wf.corporate.v1` with the WPS leg, tenant `example-bank`, bench: core banking, sanctions, fraud, WPS outward and calendar simulators, scenario `accept` |

These numbers are what this machine gave under these conditions. They are not a sizing. They show the shape (what grows with what, where the time goes) and they found two defects that stop a go-live.

## Results

"Engine" is the wall time from the first file's acceptance into the store to the last file's end. Latency is per file, from acceptance to its end. A line is one credit (one payment of the bulk). CPU is in seconds over the run.

### Many small files: 32 files of 10 lines, 8 at a time

| Temporal on | Store on | Settled | Engine | Files/min | Lines/s | p50 | p95 | p99 | Load avg |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SQL Server | SQL Server | 31 of 32 | 136.3 s | 13.6 | 2.27 | 30.1 s | 58.2 s | 61.1 s | 18.5 |
| SQL Server | PostgreSQL | 32 of 32 | 98.7 s | 19.5 | 3.24 | 23.1 s | 30.2 s | 31.9 s | 20.2 |
| PostgreSQL | SQL Server | 32 of 32 | 96.6 s | 19.9 | 3.31 | 23.3 s | 31.3 s | 31.4 s | 21.7 |
| PostgreSQL | PostgreSQL | 32 of 32 | 84.9 s | 22.6 | 3.77 | 19.4 s | 27.4 s | 27.7 s | 25.3 |

The one file that did not settle (SQL Server and SQL Server) failed at the rail decision: an attempt took more than its 5-second timeout of real time on the starved machine (`p.decide.rail.v1 ... timed out`). Each run made about 2,150 activity runs (68 per file).

| Temporal on | Store on | Temporal server CPU | Harness CPU | Workers CPU | SQL Server container: CPU, read, written | PostgreSQL container: CPU, read, written |
| --- | --- | --- | --- | --- | --- | --- |
| SQL Server | SQL Server | 47 s | 31 s | (see note) | 62.0 s, 11.4 MB, 128.1 MB | idle |
| SQL Server | PostgreSQL | 47 s | 31 s | (see note) | 55.1 s, 0 MB, 104.1 MB (Temporal) | 2.3 s, 1.0 MB, 13.3 MB (store) |
| PostgreSQL | SQL Server | 44 s | 32 s | 3 s | 6.5 s, 11.4 MB, 14.5 MB (store) | 30.7 s, 0 MB, 52.2 MB (Temporal) |
| PostgreSQL | PostgreSQL | 43 s | 30 s | 3 s | idle | 32.4 s, 0.1 MB, 57.9 MB |

Note: in the first two runs the harness counted other agents' worker processes too (fixed before the third); their workers' figure is not given.

### One large file: 1,000 lines

| Temporal on | Store on | Settled | Engine | Lines/s | Temporal server CPU | Harness CPU | Database CPU (written) | Load avg |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SQL Server | SQL Server | yes | 240.3 s | 4.16 | 55 s | 41 s | SQL Server 59.6 s (104 MB) | 25.2 |
| SQL Server | PostgreSQL | no: an item check timed out at line 272 (see below) | 111.9 s | | | | | 27.3 |
| PostgreSQL | SQL Server | yes | 319.4 s | 3.13 | 56 s | 44 s | PostgreSQL 27.9 s (46.0 MB), SQL Server 17.7 s (24.8 MB) | 20.8 |
| PostgreSQL | PostgreSQL | yes | 220.0 s | 4.55 | 54 s | 42 s | PostgreSQL 32.2 s (48.2 MB) | 16.8 |

Each file made about 4,030 activity runs (two attempts per line, each with its claim check). The SQL Server and PostgreSQL row failed with `TH_TEMPORAL_TIMERS=logical` set: at a load average of 27 one record check took more than 5 seconds of real time, and the attempt timeout is real time for a running attempt. An earlier run of each pairing, before the fix below, processed every line and then failed at the hand-over: SQL Server and SQL Server 225 s, SQL Server and PostgreSQL 217 s, PostgreSQL and SQL Server 232 s (the lines take about the same time on every pairing; the database is not where the time goes).

### Four large files at once: 4 × 1,000 lines, 4 at a time

| Temporal on | Store on | Settled | Engine | Lines/s | p50 | p95 | Temporal server CPU | Harness CPU | Workers CPU | SQL Server container |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SQL Server | SQL Server | 4 of 4 | 406.4 s | 9.84 | 404.5 s | 406.4 s | 196 s | 108 s | 13 s | 226.7 s CPU, 19.4 MB read, 485.4 MB written |
| PostgreSQL | PostgreSQL | not measured: the host's disk filled during the run and stopped PostgreSQL | | | | | | | | |

Four files at once more than doubled the lines per second of one (9.8 against 4.2): the time per line is latency (round trips), not capacity.

### Without the harness's timer setting: the largest file that settles

| Lines | Load avg | Outcome |
| --- | --- | --- |
| 3 (2 files, an early run) | about 8 | settled |
| 10 | 25.9 | failed: the rail decision's attempt timed out |
| 20 | 20.8 | failed in the record checks after line 6 |
| 30 | 16.9 | failed in the record checks after line 6 |
| 30 (an early run) | about 15 | failed; settled with `TH_TEMPORAL_TIMERS=logical` in 9.9 s |
| 100 (an early run) | about 15 | failed in the record checks after line 11 |

With the binding as it stands, a file settles only if its whole item loop fits in five seconds of real time: a few lines on this machine (I-77).

### Temporal's persistence, compared

From each server's own metrics (`persistence_latency`), over every run on it, the stalls included:

| | Temporal on SQL Server (our plugin) | Temporal on PostgreSQL |
| --- | --- | --- |
| Persistence calls | 192,278 | 152,963 |
| Mean latency | 18.4 ms | 18.3 ms |
| `UpdateWorkflowExecution` | 92,380 calls, 21.7 ms mean | 66,605 calls, 20.3 ms mean |
| `GetTaskQueue` | 5,796 calls, 120.7 ms mean | 6,157 calls, 125.5 ms mean |
| Errors | 560 (the 06:36 stall) | 21,861 (the stall, and the stop at 07:01) |

The plugin's latency per call is level with PostgreSQL's. For the same work it used about 1.8 times the database CPU (small files: 55 s against 31 s) and wrote about twice the bytes (104 MB against 52 MB): SQL Server's transaction log and its `MERGE` upserts row by row (`runtime/temporal-mssql/README.md`). The store's own writes are a small part of the databases' load: 2 to 7 CPU seconds for 32 files.

## What it found

1. **A file of more than a few lines fails on a real Temporal server** (I-77). The binding sets an attempt's deadline on Temporal's clock, from a logical time that does not move with processing. This stops any bank going live on Temporal.
2. **A large file's lines run one after another, at about a quarter of a second each** (I-78): 4 lines a second for one file, so about 40 minutes for a 10,000-line file on this machine, the same on either database. The time is round trips per attempt (an activity task, an HTTP call to the worker, a claim-check local activity, a fact in the store), not database capacity.
3. **A leg whose output is kept by the host lost its outcome** (fixed in this change). Over 256 KB, a run's output is kept by the host and the run returns a reference; the reference carried no outcome, so a parent read the leg of a 1,000-line file, which had settled, as failed. The reference now carries what a parent reads (`TemporalEngine.cs`, `InterpreterWorkflow.RunAsync`).
4. **A deployed worker could hand one payment's reply to another** (fixed in this change, I-79 for what is left). Deliveries and the hold flag are kept per attempt.
5. **Under CPU starvation an attempt's real time passes its timeout.** At load averages over 20, a 5-second attempt timeout was missed by a decision and by a record check. A bank's installation must leave headroom, and the attempt timeouts must be sized for real ports (I-77).

## Proposed targets, for the owner to set (I-68)

Proposed, not decided. Each is to be met on every engine and each database, by this harness, on a reference installation.

| Target | Proposed value | Today, this machine |
| --- | --- | --- |
| A salary file of 10,000 lines, from acceptance to submission to the scheme | 15 minutes or less (p95) | About 40 minutes estimated (not run: I-78) |
| A salary file of 1,000 lines | 2 minutes or less (p95) | 220 to 320 s |
| Small files (10 lines), 8 at a time | p95 30 s or less, 30 files a minute | p95 27 to 58 s, 14 to 23 files a minute |
| Sustained lines per installation | 50 lines a second (180,000 an hour), the month-end peak of a mid-size bank to be confirmed by the owner | 4 a second for one file, 10 for four at once |
| Errors | No file fails for a reason other than its own content, at 70 % CPU | Fails above a few lines (I-77) and at load averages over 20 |
| Store write | p95 50 ms or less per save | Not separated in this run (the dashboards' Store panel measures it) |
| Availability | 99.9 % of the business day, a file in flight survives a restart of any host | The durability check passes on SQL Server (`runtime/temporal-mssql`); not measured under load |
| Recovery | Recovery point zero for an accepted file (it is in the store and in Temporal), recovery time 30 minutes | Not measured |

## What to run at bank scale

- **A reference installation**, not a shared container: Temporal on its own hosts (at least 3 nodes, 512 history shards for a production cluster, sized by Temporal's guidance), the persistence database and the store on their own servers with dedicated disks, workers scaled per queue, the monitoring stack of the bank, and nothing else on the machines.
- **The matrix of this page on each pairing**, after I-77 and I-78 are fixed: one file of 10,000 lines; four and then sixteen files of 10,000 lines at once; a month-end peak (say 500 files of mixed sizes, 1 to 10,000 lines, arriving over an hour); and a soak (a business day of a normal flow).
- **With the real acknowledgement wait** (`--ack PT10M` or the bank's value): files then wait in flight, and the test shows how many a cluster holds (timers, history size, visibility).
- **Failures under load**: a Temporal node, a worker and the store's database restarted in the middle of the peak; every file must still settle once.
- **Measure** with the dashboards (Payments, Engines, Ports, Store) and the databases' own tools (Query Store, `pg_stat_statements`), and keep the result files with the installation's records, not in this repository.

## What was not run here, and why

- **The 10,000-line file.** One run takes about 40 minutes at the measured rate and writes about 1 GB into SQL Server; the host's disk was full once already (07:01 UTC), so it was stopped. The rate of 1,000-line files gives the estimate above.
- **Four files at once on PostgreSQL**, stopped by the full disk.
- **The scheme's real acknowledgement wait** (ten minutes): it would measure the wait.
- **DTFx, Elsa and Flowable.** The harness drives Temporal only. The other engines run the same deployment through the deploy runner (`studio/test/blackbox/e2e-wps-engines-store.test.ts`), one file at a time.
