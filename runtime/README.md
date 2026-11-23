# TransactHub runtime

The new runtime, built beside the TransactHub source (`TransactHub-src.zip`), which is never changed in place. The plan is `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`; this is phase P1.

| Project | What |
| --- | --- |
| `src/TransactHub.Model` | The internal payment model on ISO 20022 (pain.001 / pacs.008 shapes), and `PaymentContext`, which is what a workflow carries: ids, release, as-of date and branch facts, never personal data. |
| `src/TransactHub.Store` | The generalized store, insert-only, through EF Core only: payments, bulks, parties, hash-chained facts, messages, the files a deployment received or sent with their signed hashes and their bytes once per hash (`stored_file`, `file_content`, R-09-64), exception items, releases and activations, effective-dated rules, lists and configuration values, the outbox. |
| `src/TransactHub.Ports` | The port adapters a bank binds (`PortBindings`, effective-dated): core banking (`simulator`, `gateway-rest`, and `file-bulk`, posting by bulk file through the file-transfer port, R-12-15), file transfer (`simulator`, `directory`), file encryption and decryption (`openpgp`, and `gnupg` on the source's key folder, R-12-17), and the labour ministry's establishment register (`simulator`, `gateway-rest`, R-12-16). |
| `src/TransactHub.Native` | The native host (R-08-14): the runtime's code-behind of the activities bound `native`, on the banks' port bindings; with a store, it keeps every file it reads, renders, encrypts or decrypts there. |
| `src/TransactHub.Store.Migrations.SqlServer`, `…PostgreSql` | The migrations of each provider, generated from the one model. |
| `src/TransactHub.Interpreter` | The studio's interpreter (`studio/engines/shared`), compiled into the runtime: one interpreter for every engine. |
| `src/TransactHub.Engine.Temporal` | The Temporal binding: the generic interpreter workflow, the front-door workflow, and the host activities (attempts, claim check, handlers). See `studio/engines/temporal/README.md`. |
| `src/TransactHub.Engine.Flowable` | The Flowable binding (plan D3): `FlowableRest`, the client of the Flowable REST server (deploy, start, acquire and lock, complete, fail, BPMN errors, jobs, timers, messages), and `FlowableBinding`, the external worker that runs the exported BPMN there, with the payloads in the claim check. See `studio/engines/flowable/README.md`. |
| `src/TransactHub.Api` | The release API: `POST /releases` (a studio package, checked against its manifest and its signature, stored once), `POST /activations` (the stored package verified again), `GET /activations/active`, `POST /import-branch` (the branch the studio exports to), `GET /releases` (with each verification), `GET /values`. PUT, PATCH and DELETE are refused. Every call needs a bearer JWT from the bank's identity provider. |
| `src/TransactHub.Security` | The one secret interface (`ISecretResolver`: `env:`, `file:`, `vault:`, `akv:` Azure Key Vault, `aws-sm:` AWS Secrets Manager, `aws-ssm:` Parameter Store, on their official SDKs, with a short cache), and the package signature: `ReleaseSignature` (ECDSA P-256 over `release.json`), `TrustedSigners` (key ids, retired and revoked keys). |
| `src/TransactHub.Auth` | Who may call a runtime host: bearer JWTs from the bank's one OIDC issuer, the bank's role map from its claims and groups to the host's roles. No identity store. Any host calls `AddRuntimeAuth` with its own roles. |
| `src/TransactHub.Api` | The release API: `POST /releases` (a studio package, checked against its manifest, stored once), `POST /activations`, `GET /activations/active`, `POST /import-branch` (the branch the studio exports to). PUT, PATCH and DELETE are refused. |
| `src/TransactHub.Operations` | The operations read model (R-05-49): a projection of the store's facts through the outbox, on both databases (`…Operations.Migrations.SqlServer`, `…PostgreSql`); the packs' operations views (R-05-48); operator actions with maker-checker and the holds the runtime answers (R-05-50): a hold answers held and the engine awaits the decision (R-05-56); decisions, signals and controls go on the outbox (`engine-signal`) for the engine's host (R-05-57); `retry` reaches a parked step (R-05-55) and `resend` sends a kept file again on the file-transfer port (`TH_FILE_TRANSFER_DIR`, R-05-58); holds whose expiry passed are expired by the operations host. The engine hosts' reader is `StoreSignals` in `TransactHub.Engines.Durable`; a harness on virtual time waits for a live operator at most `TH_OPERATOR_PATIENCE_SECONDS` (I-127). |
| `src/TransactHub.Operations.Api`, `ops-console/` | The operations console's API (behind `TransactHub.Auth`: roles Operator and Supervisor from the bank's role map, R-06-24) and its web app. See `ops-console/README.md`. |
| `src/TransactHub.Service`, `src/TransactHub.Service.Api` | The service console for service engineers (R-06-25, R-09-39 to R-09-44): health with a short history, content install, activation, draining, rollback and removal from GitHub through `IPackageSource`, signed configuration backups and restore, the error and event log, the error dictionary and its runbooks (R-05-51, R-05-52), troubleshooting actions with a ServiceApprover for the risky ones, and an assistant on the Anthropic API, masked and read-only. Routes under `/service/api`; its web app is the Service section of `ops-console/`. Roles ServiceEngineer and ServiceApprover from the bank's role map. |
| `tests/TransactHub.Service.Tests` | The service console's API per route and role (401, 403), install, verify and refuse (unsigned, tampered, untrusted), activation and removal, backup and restore, the actions, the assistant on a fake model (no party value leaves), the dictionary's coverage of the runtime's codes (PostgreSQL in a container). |
| `src/TransactHub.Telemetry` | OpenTelemetry for every runtime host: traces, metrics and logs over OTLP by the standard `OTEL_*` variables, masking of personal data before export, `/health/live` and `/health/ready` (R-09-31 to R-09-36). See `docs/north-star/guidelines/observability.md`. |
| `observability/` | The local monitoring stack for development: collector, Jaeger, Loki, Prometheus, Grafana and the dashboards Payments, Engines, Ports, Store (R-09-37). |
| `load/` | The load harness (R-09-38): synthetic WPS salary files on Temporal, its persistence on SQL Server or PostgreSQL, the store on either. See `docs/runtime/LOAD-TEST.md`. |
| `tools/TransactHub.TemporalHarness` | Runs a plan (a conformance scenario or a deployment) on Temporal and prints the observation. |
| `tools/TransactHub.EngineHost` | The long-running engine host (R-09-66 to R-09-68, Issues I-114, I-127): `supervise` serves a bank's distributions on one engine, a host per distribution from installation to removal, pins each payment at acceptance and runs it on its distribution's host; `serve` is one distribution's host. On Temporal (a real server, `TH_TEMPORAL_ADDRESS`) its workers run for as long as the host does, as a version of the bank's worker deployment whose build id is the distribution's id; on DTFx, Elsa and Flowable it runs each payment on the engine's runtime harness. |
| `tests/TransactHub.EngineHost.Tests` | The switch-over on Temporal (worker versioning on the CLI image's development server), DTFx, Elsa and Flowable: a payment pinned to A runs on A after B is active, new payments go to B, A is removed only once drained and its host stops (PostgreSQL, Temporal and Flowable in containers). |
| `tools/TransactHub.DevIdentity` | A development identity provider for one bank's runtime on a laptop (`deploy/laptop`, R-09-80): discovery, a JWKS, the code flow with PKCE over synthetic users, client credentials; refused in Production. `keys --out <dir>` makes the laptop's signing keys once, into a volume. |
| `tools/TransactHub.Migrate` | Applies the store's and the read model's migrations once, as the databases' owner, and exits, so that the hosts run as a login that may not update or delete the store (R-09-81). |
| `tools/TransactHub.FlowableHarness` | Runs a deployment's plan on the Flowable REST server at `TH_FLOWABLE_URL` and prints the observation. |
| `tools/TransactHub.TestBench` | The test bench: send data, it runs on the engines a profile names with the store on its database and compares each with the studio interpreter. CLI, service and web UI; profiles, benches, the sample library, the data builder and the shipped suite in `testbench/`. See [testbench/README.md](testbench/README.md). |
| `tools/temporal-test-server` | Temporal's time-skipping test server from Maven Central: `setup.sh` once, then the harness starts it. |
| `tests/TransactHub.Store.Tests` | Every store test on SQL Server 2022 and PostgreSQL 16 (Testcontainers), and the database-independence check. |
| `tests/TransactHub.Security.Tests` | Secrets by reference (Vault 1.18 and LocalStack 4.14.0 in containers; Azure Key Vault against `FakeKeyVault`, an in-process fake of its REST protocol), and the signature: tampered, extra and missing files, no signature, a wrong key, a revoked key, rotation, an `openssl` check. |
| `tests/TransactHub.TestBench.Tests` | The test bench's profiles, data builder, judge and service sign-in. |
| `tests/TransactHub.Security.Tests` | Secrets by reference (and Vault 1.18 in a container), and the signature: tampered, extra and missing files, no signature, a wrong key, a revoked key, rotation, an `openssl` check. |
| `tests/TransactHub.Engine.Flowable.Tests` | The BPMN reader, and the worker client and the binding against the Flowable REST server 7.1.0 (Testcontainers); the binding killed in the middle of a payment and started again from its facts in the store, in production and virtual mode (`FlowableRestartTests`, R-09-65). |

## Commands

```
cd runtime
dotnet test                       # needs Docker: starts SQL Server, PostgreSQL and Flowable REST containers
dotnet tool restore
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.SqlServer -s src/TransactHub.Store.Migrations.SqlServer -o Migrations
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.PostgreSql -s src/TransactHub.Store.Migrations.PostgreSql -o Migrations
```

A model change needs a migration for both providers; a test fails if either is missing.

## Running the release API

```
TH_TRUSTED_SIGNERS=file:/etc/transacthub/trusted-signers.json \
Auth__Oidc__Issuer=https://idp.bank.example/realms/runtime Auth__Oidc__Audience=transacthub-runtime \
Auth__RoleMap=file:/etc/transacthub/role-map.json \
Store__Provider=PostgreSql Store__Connection=... dotnet run --project src/TransactHub.Api
```

- `TH_TRUSTED_SIGNERS`: the studio's package-signing public keys the bank trusts, with retired and revoked keys (`docs/north-star/guidelines/encryption-and-secrets.md`). Read again at each verification.
- `Auth:*`: the bank's identity provider and role map (`docs/north-star/guidelines/access-control.md`). The runtime keeps no users, passwords or tokens, and trusts no issuer but the bank's.
- `Releases:ImportRoots`: the folders `POST /import-branch` may read, comma-separated (a checkout of the content branch is under one of them). Without it no branch is imported (R-09-58).
- The native host (`src/TransactHub.Native`) takes the same `Auth:*` settings, with a role map that grants `native-caller` to the workers' client credentials per tenant; without them it answers only on loopback addresses (`TH_NATIVE_URLS`) and does not start on any other (R-08-17). The consoles' hosts limit changes per client address, `RateLimits:WritesPerMinute` (default 600) (R-09-60).
- `PackageSource:GitHub:Repository` (owner/repo), `:Api`, `:Root`, `:TokenRef` (a read-only token, by reference): the studio's content repository as the source of signed releases. `GET /sources/versions` lists the release tags; `POST /sources/install { "tag": "release/<workflow>/<release>/<target>" }` downloads, verifies and stores one (spec R-09-30).
- Secrets anywhere in its configuration or a bank's bindings are references: `env:NAME`, `file:/path`, `vault:mount/path#field` (`TH_VAULT_ADDR`, `TH_VAULT_TOKEN`), `akv:<vault-name>/<secret-name>[#version]` (managed identity, workload identity, or `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `TH_AKV_CLIENT_SECRET` by reference; `TH_AKV_DNS_SUFFIX`), `aws-sm:<region>/<secret-id>[#json-field]` and `aws-ssm:<region>/<parameter-name>` (the AWS default credential chain). Values read over the network are kept for `TH_SECRET_CACHE_SECONDS` (default 300). See `docs/north-star/guidelines/encryption-and-secrets.md`.

## Database independence

The runtime reaches its data only through EF Core. The provider is chosen by `StoreOptions` (`SqlServer` or `PostgreSql`), in one place (`StoreDbContext.Configure`). `NeutralityTests` fails the build on a database driver, a raw connection, raw SQL, a T-SQL idiom or a provider call anywhere else in `src/`, and on a provider package referenced by any project but the store.
