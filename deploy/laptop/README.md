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
| `file-api` | th-laptop-runtime | The File API (R-09-82 to R-09-86): every file the runtime receives or sends, and the front door behind its intake, which starts or correlates a payment on the bank's engine host. The bank's file-transfer binding is a folder here: the inbox and the outbox are the `files` volume | http://localhost:8485 |
| `enginehost` | th-laptop-runtime | The long-running engine host's supervisor for the bank, on Temporal. It builds each distribution's plan from the stored package (R-09-94) and answers the File API's front door on the compose network with its token (`TH_LAPTOP_ENGINE_HOST_TOKEN`, R-09-95) | (inside) |
| `enginehost-dtfx`, `-elsa`, `-flowable` | th-laptop-runtime | A supervisor per other engine, by profile, set up as `enginehost` | (inside) |
| `bank-idp` | th-laptop-runtime | The bank's development identity provider: synthetic users, client credentials, refused in Production | http://localhost:8480 |
| `temporal` | th-laptop-temporal | Temporal 1.32.2 with the SQL Server plugin (`runtime/temporal-mssql`) | 127.0.0.1:17233 |
| `sqlserver` | mssql/server:2022 | The store (`th_store`), the read model (`th_ops`) and Temporal's two databases | 127.0.0.1:14330 |
| `keys-init`, `db-init`, `temporal-schema`, `db-grants`, `temporal-namespace` | | One-off: the keys, the migrations as the owner, Temporal's schemas, the logins and their grants, the `default` namespace | |
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

docker compose -f deploy/laptop/compose.yaml config -q                     # valid
docker compose -f deploy/laptop/compose.yaml up -d --wait                  # healthy
docker compose -f deploy/laptop/compose.yaml --profile dtfx --profile elsa up -d
TH_LAPTOP_FLOWABLE_PASSWORD=... docker compose -f deploy/laptop/compose.yaml --profile flowable up -d
docker compose -f deploy/laptop/compose.yaml --profile observability up -d
docker compose -f deploy/laptop/compose.yaml down -v                       # and its volumes

(cd studio && node_modules/.bin/tsx ../deploy/laptop/test/insert-only.mts)  # the insert-only proof, on a stack of its own
sh deploy/laptop/prune-cache.sh                                             # the build cache of the intermediate stages
```

- **Another bank**: `TH_LAPTOP_BANK=dib` (the runtime then refuses ADIB's content).
- **The real content branch**: `TH_CONTENT_REPO=owner/repo TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=...` for the studio, and `TH_LAPTOP_GITHUB_API=https://api.github.com/ TH_LAPTOP_GITHUB_READ_TOKEN=...` (a read-only token) for the runtime's package source. The tokens come from the environment only.
- **Sign-in**: the studio's development provider lists the studio's users; the runtime's web app signs in at the bank's development provider (`bank-idp`) as one of its synthetic users: a service engineer, a bank approver, an operator, a supervisor, a tester (`identity/directory.json`). Machine callers (the pipeline, the front door) use client credentials. The role maps are `identity/role-map-*.json`; the File API's (`role-map-file-api.json`) lets the operators upload, the channel send and the front door use the outbox.

## Insert, never update

`test/insert-only.mts` brings a stack of its own up healthy, with passwords made for the run, and shows: distribution A published by the studio, installed and activated through the service console with a bank approver's consent; a payment pinned to A; B published, installed and activated, A draining; a payment on B; the payment on A finishing on A, on Temporal on SQL Server; every row of `release`, `release_activation` and `payment_fact` unchanged after all of it, and the runtime's login refused an UPDATE, a DELETE and a TRUNCATE on each by the database (`sql/grants.sql`, R-09-81); a second install of A's id with other content refused at the release API and at the database's unique key; and DIB's distribution refused in the ADIB runtime at verify, install and the release API. It removes its stack and volumes at the end (`--keep` leaves them).

On a real Temporal server a WPS payment waits in real time for the simulator's acknowledgement, so each payment takes about ten minutes (Issue I-156 (5)).

## What is not here

- A bank's installation (TLS, the bank's identity provider, its secret stores, high availability, the `sftp` binding to the bank's partners): this is a laptop. What the laptop chose that an installation decides is Issue I-156.
- The insert-only test runs its payments with the front door's one-off runner (`bin/run-pinned.mts`, the studio's deploy runner on Temporal), not through the File API and the supervisor.
- A DTFx or Elsa payment on its supervisor does not survive a restart of its container, and Flowable deploys per payment (Issue I-165).
