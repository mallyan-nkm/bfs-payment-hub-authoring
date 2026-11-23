# The studio and one bank's runtime on a laptop

The TransactHub Designer Studio and one bank's runtime, as separate services in Docker containers (spec R-09-80). One runtime serves one bank (R-09-76), ADIB by default; the studio is multi-tenant. Temporal on the SQL Server plugin is the default engine (R-09-79). Every change of the system in the consoles waits for a bank approver's consent (R-09-77).

## Services

| Service | Image | What | On the host |
| --- | --- | --- | --- |
| `studio` | th-laptop-studio | The studio's API and web app; its in-memory fake GitHub seeded from `content-configurations/` unless `TH_CONTENT_REPO` names a real branch; its own development sign-in (the platform vendor's provider on a laptop) | http://localhost:4317 |
| `runtime-api` | th-laptop-runtime | The release API and the front door (pins, ends, kept files) | http://localhost:8481 |
| `service-api` | th-laptop-runtime | The service console's API and the runtime's web app | http://localhost:8482 |
| `ops-api` | th-laptop-runtime | The operations console's API and the web app | http://localhost:8483 |
| `testbench` | th-laptop-runtime | The test bench's service and web app; Temporal by default, DTFx and Elsa in its harnesses, Flowable with the `flowable` profile | http://localhost:8484/#/testbench |
| `native` | th-laptop-runtime | The native host, behind the bank's provider | (inside) |
| `file-api` | th-laptop-runtime | The File API (R-09-82 to R-09-86): every file the runtime receives or sends, and the front door behind its intake, which starts or correlates a payment on the bank's engine host, its intake map on the `workers` of the payment's distribution. It polls the inboxes of the runtime's bank only (R-09-104), on the laptop's binding of the bank's file transfer (`banks/<bank>/file-transfer.yaml`), which reaches it inside the bank's signed configuration release, put in force with consent (R-09-110 to R-09-112): every partner is the `sftp` service (R-09-105) | http://localhost:8485 |
| `sftp` | atmoz/sftp (by digest) | The bank's SFTP partners on a laptop: the channel's pick-up folder (`upload/wps/inbound`, message `uae.wps.sif`), the Central Bank's answers and files, the results. Its login is made at the stack's start (`TH_LAPTOP_SFTP_PASSWORD`); its host keys are made on first start, read out of band and pinned by reference (`TH_LAPTOP_SFTP_HOST_KEY`) | 127.0.0.1:12222 |
| `workers` | th-laptop-runtime | Every distribution's activity workers, side by side: on a distribution's first call it fetches and verifies the package and starts that distribution's own workers, one per queue. The engine hosts' plans (`TH_HOST_WORKERS_URL`) and the File API's front door (`FileApi__Workers__*`) name it by distribution (R-09-105, I-193) | (inside) |
| `machine-auth` | th-laptop-runtime | Fresh client-credentials tokens in the `auth-files` volume, for the hosts that call the File API with a token by reference: the native host and the engine hosts (`TH_FILE_API_URL`), the consoles' resend (`Operations__FileApi__*`, `Service__FileApi__*`) | (inside) |
| `enginehost` | th-laptop-runtime | The long-running engine host's supervisor for the bank, on Temporal. It builds each distribution's plan from the stored package (R-09-94) and answers the File API's front door on the compose network with its token (`TH_LAPTOP_ENGINE_HOST_TOKEN`, R-09-95) | (inside) |
| `enginehost-dtfx`, `-elsa`, `-flowable` | th-laptop-runtime | A supervisor per other engine, by profile, set up as `enginehost` | (inside) |
| `bank-idp` | th-laptop-runtime | The bank's development identity provider: synthetic users, client credentials, refused in Production | http://localhost:8480 |
| `temporal` | th-laptop-temporal | Temporal 1.32.2 with the SQL Server plugin (`runtime/temporal-mssql`), or on PostgreSQL with `compose.postgres.yaml` (`TEMPORAL_DB_PLUGIN=postgres12`) | 127.0.0.1:17233 |
| `sqlserver` | mssql/server:2022 | The store (`th_store`), the read model (`th_ops`) and Temporal's two databases | 127.0.0.1:14330 |
| `keys-init`, `db-init`, `db-grants`, `temporal-namespace` | | One-off: the keys; the migrate step (`runtime/tools/TransactHub.Migrate`), which brings the store, the read model and Temporal's two databases to their schemas as the owner, once, at the start (R-09-141); the logins and their grants; the `default` namespace. No host migrates from the runtime's login: every one runs with `Store__Migrate=false` | |
| `flowable` | flowable-rest 7.1.0 | Profile `flowable` | |
| `otel-collector`, `prometheus` | | Profile `observability`, with `runtime/observability`'s configuration | 127.0.0.1:4318, 9090 |

Every image runs as the unprivileged user `app`, has a health check and is built on a base pinned by digest. No image holds a key, a password or a token: passwords and client secrets come from the environment, keys are made by `keys-init` into a volume on first start. Ports are published on 127.0.0.1 only.

## Commands

From the repository root:

```
deploy/laptop/build.sh                 # the three images, from source inside Docker (multi-stage)
deploy/laptop/build.sh --local         # or with this machine's .NET 10 SDK, Node 22 and Go (Docker builds with no network)

# Passwords and client secrets of your own, in your shell only (never in a file in the repository):
export TH_LAPTOP_SA_PASSWORD=... TH_LAPTOP_RUNTIME_DB_PASSWORD=... TH_LAPTOP_TESTBENCH_DB_PASSWORD=... TH_LAPTOP_TEMPORAL_DB_PASSWORD=...
export TH_LAPTOP_USER_PASSWORD=... TH_LAPTOP_PIPELINE_SECRET=... TH_LAPTOP_FRONTDOOR_SECRET=... TH_LAPTOP_ENGINE_HOST_TOKEN=...
export TH_LAPTOP_CHANNEL_SECRET=... TH_LAPTOP_OPERATIONS_SECRET=... TH_LAPTOP_SFTP_PASSWORD=...

docker compose -f deploy/laptop/compose.yaml config -q                     # valid
docker compose -f deploy/laptop/compose.yaml up -d --wait sftp             # the SFTP server first; then pin its host keys:
export TH_LAPTOP_SFTP_HOST_KEY=$(for k in ed25519 rsa; do docker compose -f deploy/laptop/compose.yaml exec -T sftp ssh-keygen -lf /etc/ssh/ssh_host_${k}_key.pub | cut -d' ' -f2; done | paste -sd,)
docker compose -f deploy/laptop/compose.yaml up -d --wait                  # healthy
docker compose -f deploy/laptop/compose.yaml --profile dtfx --profile elsa up -d
TH_LAPTOP_FLOWABLE_PASSWORD=... docker compose -f deploy/laptop/compose.yaml --profile flowable up -d
docker compose -f deploy/laptop/compose.yaml --profile observability up -d
docker compose -f deploy/laptop/compose.yaml down -v                       # and its volumes

(cd studio && node_modules/.bin/tsx ../deploy/laptop/test/insert-only.mts)  # the insert-only proof, on a stack of its own
(cd studio && node_modules/.bin/tsx ../deploy/laptop/test/insert-only.mts --postgres)  # the same on PostgreSQL (or TH_LAPTOP_IT_DB=postgres)
sh deploy/laptop/prune-cache.sh                                             # the build cache of the intermediate stages
```

- **Another bank**: `TH_LAPTOP_BANK=dib` (the runtime then refuses ADIB's content).
- **On PostgreSQL**: `docker compose -f deploy/laptop/compose.yaml -f deploy/laptop/compose.postgres.yaml up -d --wait`. The store (`th_store`), the read model (`th_ops`) and Temporal's two databases (Temporal's own `postgres12` plugin, its schema files of the same server version in `temporal/schema/postgresql`) are on one PostgreSQL 16 server, `postgres` (pinned by digest, on `TH_LAPTOP_SQL_PORT`), and SQL Server is not started (R-09-140). The logins are SQL Server's: `sql/grants-postgres.sql` gives `th_runtime` SELECT and INSERT in the store and nothing more (UPDATE, DELETE and TRUNCATE revoked, no table owned, so no ALTER), read and write in the read model, and `th_temporal` data rights in Temporal's databases only. The same passwords from the environment (`TH_LAPTOP_SA_PASSWORD` is the server owner's).
- **Files**: every host reaches the bank's files through the File API (`TH_FILE_API_URL`, R-09-138): puts go to its outbox, and reads, listings and removals to its `POST /transfer`. A host that names no File API is refused a folder or an SFTP partner unless it says it is a development machine (`TH_FILES_DIRECT=dev`); so is a console's resend folder (`Operations:FileTransfer:Folder`, `Service:FileTransfer:Folder`).
- **A salary file the real way**: drop it on the SFTP server's pick-up folder (`sftp -P 12222 th@127.0.0.1`, folder `upload/wps/inbound`), or post it to the File API (`POST http://localhost:8485/files` with the channel's client credentials, `th-channel`). The File API takes it, keeps it, and its front door starts the payment on the engine host; a taken file moves to `done/` (or `error/`). Until I-195 is done a salary file carries the samples' first line, `# file: <its name>`.
- **Other host ports**: `TH_LAPTOP_STUDIO_PORT`, `TH_LAPTOP_IDP_PORT` (with `TH_LAPTOP_IDP_URL`), `TH_LAPTOP_API_PORT`, `TH_LAPTOP_SERVICE_PORT`, `TH_LAPTOP_OPS_PORT`, `TH_LAPTOP_TESTBENCH_PORT`, `TH_LAPTOP_FILE_API_PORT`, `TH_LAPTOP_SQL_PORT`, `TH_LAPTOP_TEMPORAL_PORT`, `TH_LAPTOP_SFTP_PORT`, so that two stacks run side by side.
- **The real content branch**: `TH_CONTENT_REPO=owner/repo TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=...` for the studio, and `TH_LAPTOP_GITHUB_API=https://api.github.com/ TH_LAPTOP_GITHUB_READ_TOKEN=...` (a read-only token) for the runtime's package source. The tokens come from the environment only.
- **Sign-in**: the studio's development provider lists the studio's users; the runtime's web app signs in at the bank's development provider (`bank-idp`) as one of its synthetic users: a service engineer, a bank approver, an operator, a supervisor, a tester (`identity/directory.json`). Machine callers (the pipeline, the front door) use client credentials. The role maps are `identity/role-map-*.json`; the File API's (`role-map-file-api.json`) lets the operators upload, the channel send and the front door use the outbox.

## Insert, never update

`test/insert-only.mts` brings a stack of its own up healthy, with passwords and the SFTP server's login made for the run and its host keys pinned, and shows: distribution A published by the studio, installed and activated through the service console with a bank approver's consent; a salary file dropped on the SFTP server, taken by the File API's inbox poller and started by its front door as a payment pinned to A; B published, installed and activated, A draining; a second file by SFTP and a third posted to `POST /files`, both on B; the payment on A finishing on A, the others on B, on the engine host on Temporal on SQL Server (or, with `--postgres`, on PostgreSQL); a file sent through the File API's outbox onto the SFTP server and resent by the consoles' client (R-09-105); every row of `release`, `release_activation` and `payment_fact` unchanged after all of it, and the runtime's login refused an UPDATE, a DELETE and a TRUNCATE on each by the database (`sql/grants.sql`, R-09-81); a second install of A's id with other content refused at the release API and at the database's unique key; and DIB's distribution refused in the ADIB runtime at verify, install and the release API. It removes its stack and volumes at the end (`--keep` leaves them).

On a real Temporal server a WPS payment waits in real time for the simulator's acknowledgement, so each payment takes about ten minutes (Issue I-156 (5)).

## What is not here

- A bank's installation (TLS, the bank's identity provider, its secret stores, high availability, the `sftp` binding to the bank's partners): this is a laptop. What the laptop chose that an installation decides is Issue I-156.
- A test distribution's outward salary file goes to the WPS simulator, not out as a file: no runtime adapter of the WPS port writes it to the Central Bank's folder yet (Issue I-192).
- A DTFx or Elsa payment on its supervisor does not survive a restart of its container, and Flowable deploys per payment (Issue I-165).
