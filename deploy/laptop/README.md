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
TH_NATIVE_MODULE_KEY_FILE=/path/outside/the/repository/nest-module-key.pem deploy/laptop/build.sh   # an image beyond a laptop (below)

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

- **The native modules' signing key** (R-09-181, Issue I-225): the laptop's build makes a P-256 key of its own, holds it in one build step's environment, signs the platform's and WPS's modules with it and writes its public key to `/opt/th/native-module-signers.json`, which the laptop's studio trusts. That key is for the laptop only: it proves the folder was not changed after the build, not who built it. An image beyond a laptop is signed by NEST's module signing key: name a PEM file that holds it, outside the repository, in `TH_NATIVE_MODULE_KEY_FILE`, and `build.sh` hands it to `docker build` as the BuildKit secret `th_native_module_key` with `--build-arg TH_MODULE_SIGNING=secret` (with `--local`, into the signing step's environment only). The secret is mounted for the signing step only and is in no layer, argument or environment of an image; with `TH_MODULE_SIGNING=secret` and no secret the build stops rather than make a key. Run that studio with NEST's trusted signers in `TH_NATIVE_MODULE_SIGNERS` (the JSON, or a reference to it), not the file the build wrote.
- **Another bank**: `TH_LAPTOP_BANK=dib` (the runtime then refuses ADIB's content).
- **On PostgreSQL**: `docker compose -f deploy/laptop/compose.yaml -f deploy/laptop/compose.postgres.yaml up -d --wait`. The store (`th_store`), the read model (`th_ops`) and Temporal's two databases (Temporal's own `postgres12` plugin, its schema files of the same server version in `temporal/schema/postgresql`) are on one PostgreSQL 16 server, `postgres` (pinned by digest, on `TH_LAPTOP_SQL_PORT`), and SQL Server is not started (R-09-140). The logins are SQL Server's: `sql/grants-postgres.sql` gives `th_runtime` SELECT and INSERT in the store and nothing more (UPDATE, DELETE and TRUNCATE revoked, no table owned, so no ALTER), read and write in the read model, and `th_temporal` data rights in Temporal's databases only. The same passwords from the environment (`TH_LAPTOP_SA_PASSWORD` is the server owner's).
- **Files**: every host reaches the bank's files through the File API (`TH_FILE_API_URL`, R-09-138): puts go to its outbox, and reads, listings and removals to its `POST /transfer`. A host that names no File API is refused a folder or an SFTP partner unless it says it is a development machine (`TH_FILES_DIRECT=dev`); so is a console's resend folder (`Operations:FileTransfer:Folder`, `Service:FileTransfer:Folder`).
- **A salary file the real way**: drop it on the SFTP server's pick-up folder (`sftp -P 12222 th@127.0.0.1`, folder `upload/wps/inbound`), or post it to the File API (`POST http://localhost:8485/files` with the channel's client credentials, `th-channel`). The File API takes it, keeps it, and its front door starts the payment on the engine host; a taken file moves to `done/` (or `error/`). A person uploads one on `POST http://localhost:8485/files/ui` (multipart, as an operator). A salary file needs no `# file:` line: the intake map takes its name from the File API (I-195).
- **The outward file and its answer**: ADIB's configuration release binds the WPS port to the File API (`banks/adib/file-transfer.yaml`, adapter `file-transfer`, R-09-162): each payment's salary file is written by the native host and leaves through the File API's outbox to the SFTP server's `upload/cb/wps/sif`. Play the Central Bank by leaving an ACK or NAK named `<the SIF's stem><a twelve-character file id>.ACK` (or `.NAK`) in `upload/cb/wps/ack`: the File API reads it, correlates it to the payment by the SIF's stem and the payment goes on. The workers give the render, the send and the ACK's reading to the native host (`TH_NATIVE_ACTIVITIES`); the other ports are the test distribution's simulators (`TH_LAPTOP_WORKERS_BENCH`), and a bench that names the WPS port's simulator sends nothing out.
- **Other host ports**: `TH_LAPTOP_STUDIO_PORT`, `TH_LAPTOP_IDP_PORT` (with `TH_LAPTOP_IDP_URL`), `TH_LAPTOP_API_PORT`, `TH_LAPTOP_SERVICE_PORT`, `TH_LAPTOP_OPS_PORT`, `TH_LAPTOP_TESTBENCH_PORT`, `TH_LAPTOP_FILE_API_PORT`, `TH_LAPTOP_SQL_PORT`, `TH_LAPTOP_TEMPORAL_PORT`, `TH_LAPTOP_SFTP_PORT`, so that two stacks run side by side.
- **The real content branch**: `TH_CONTENT_REPO=owner/repo TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=...` for the studio, and `TH_LAPTOP_GITHUB_API=https://api.github.com/ TH_LAPTOP_GITHUB_READ_TOKEN=...` (a read-only token) for the runtime's package source. The tokens come from the environment only.
- **Sign-in**: the studio's development provider lists the studio's users; the runtime's web app signs in at the bank's development provider (`bank-idp`) as one of its synthetic users: a service engineer, a bank approver, an operator, a supervisor, a tester and a register maker (`identity/directory.json`; the register maker proposes a register change, the bank approver consents, I-203). Machine callers (the pipeline, the front door) use client credentials. The role maps are `identity/role-map-*.json`; the File API's (`role-map-file-api.json`) lets the operators upload, the channel send and the front door use the outbox.

## Insert, never update

`test/insert-only.mts` brings a stack of its own up healthy, with passwords and the SFTP server's login made for the run and its host keys pinned, and shows: distribution A published by the studio, installed and activated through the service console with a bank approver's consent; a salary file dropped on the SFTP server, taken by the File API's inbox poller and started by its front door as a payment pinned to A; B published, installed and activated, A draining; a second file by SFTP, a third posted to `POST /files` and a fourth uploaded on `POST /files/ui` as an operator, all on B, none with a `# file:` line; the payment on A finishing on A, the others on B, on the engine host on Temporal on SQL Server (or, with `--postgres`, on PostgreSQL); each payment's salary file sent through the File API's outbox to the Central Bank's folder on the SFTP server, answered there by the test with an ACK (one with a NAK) that the File API reads back to the payment, and P1's file resent by the consoles' client (R-09-105, R-09-162); every row of `release`, `release_activation` and `payment_fact` unchanged after all of it, and the runtime's login refused an UPDATE, a DELETE and a TRUNCATE on each by the database (`sql/grants.sql`, R-09-81); a second install of A's id with other content refused at the release API and at the database's unique key; and DIB's distribution refused in the ADIB runtime at verify, install and the release API. It removes its stack and volumes at the end (`--keep` leaves them).

On a real Temporal server a WPS payment waits in real time for the Central Bank's acknowledgement, which the test leaves as soon as the salary file arrives.

## Alerts and payment events (optional)

The profile `events` adds an MQTT broker (`mosquitto`, eclipse-mosquitto pinned by digest, anonymous logins refused) for the operators' alerts (`p.port.notification.v1`, the turnaround breaches among them, R-05-80, R-09-150) and a one-node Kafka broker (`kafka`, apache/kafka-native in KRaft mode, pinned by digest) for the payment events out of the store's outbox (`p.port.event-stream.v1`, R-09-152 to R-09-154). Neither starts by default; both are in `compose.events.yaml`, beside the main file, which stays as it is.

```
export TH_LAPTOP_BROKER_PASSWORD=...                                        # the broker's login th-alerts, in your shell only
docker compose -f deploy/laptop/compose.yaml -f deploy/laptop/compose.events.yaml --profile events up -d --wait
docker compose -f deploy/laptop/compose.yaml -f deploy/laptop/compose.events.yaml exec mosquitto \
  mosquitto_sub -u th-alerts -P "$TH_LAPTOP_BROKER_PASSWORD" -t 'transacthub/#' -v            # watch the alerts
kcat -b 127.0.0.1:19094 -t transacthub.payment-events -C -K ' '                                # the events, with any Kafka client
docker compose -f deploy/laptop/compose.yaml -f deploy/laptop/compose.events.yaml --profile events down -v
```

- **Bind them.** The bank's folder binds both ports to placeholder hosts (`content-configurations/banks/<bank>/platform-adapters.yaml`), so until the laptop's bindings are in force an alert is recorded as `alert-failed` and the events wait, keeping their position. `banks/<bank>/events.yaml` binds them to these two services: build the bank's configuration release with it and put it in force with a bank approver's consent, as for `file-transfer.yaml` (R-09-110 to R-09-112).
- **What goes out.** An alert carries ids, codes and periods only. An event is a fact's reference: its subject (the message key), its number on the subject's chain, its kind, code and time, never its data. Delivery is at least once and in order per payment; the stream's position is a row per batch in `stream_position`, insert-only, so a restart resumes and nothing is skipped.
- **Ports**: `TH_LAPTOP_MQTT_PORT` (default 11883) and `TH_LAPTOP_KAFKA_PORT` (default 19094), on 127.0.0.1. The laptop's Kafka is plaintext; a bank's cluster takes SASL over TLS (`securityProtocol: sasl_ssl`).
- **Circuit breakers.** Every production adapter that calls an outside system stops calling it after `breakerFailures` failures in a row (default 5) for `breakerOpenSeconds` (default 30), then tries one call (R-09-155). Stop the broker to see the service console's health show the open breaker (`circuit-breakers`, R-09-156).

## What is not here

- A bank's installation (TLS, the bank's identity provider, its secret stores, high availability, the `sftp` binding to the bank's partners): this is a laptop. What the laptop chose that an installation decides is Issue I-156.
- The scheme's other sends on the WPS port (receipt confirmations, returns, refunds) have no native code-behind on the port yet, so the laptop runs outward salary files only (Issue I-268); an encrypted salary file is not what is sent (Issue I-266).
