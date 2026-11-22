# TransactHub runtime

The new runtime, built beside the TransactHub source (`TransactHub-src.zip`), which is never changed in place. The plan is `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`; this is phase P1.

| Project | What |
| --- | --- |
| `src/TransactHub.Model` | The internal payment model on ISO 20022 (pain.001 / pacs.008 shapes), and `PaymentContext`, which is what a workflow carries: ids, release, as-of date and branch facts, never personal data. |
| `src/TransactHub.Store` | The generalized store, insert-only, through EF Core only: payments, bulks, parties, hash-chained facts, messages, exception items, releases and activations, effective-dated rules, lists and configuration values, the outbox. |
| `src/TransactHub.Store.Migrations.SqlServer`, `…PostgreSql` | The migrations of each provider, generated from the one model. |
| `src/TransactHub.Interpreter` | The studio's interpreter (`studio/engines/shared`), compiled into the runtime: one interpreter for every engine. |
| `src/TransactHub.Engine.Temporal` | The Temporal binding: the generic interpreter workflow, the front-door workflow, and the host activities (attempts, claim check, handlers). See `studio/engines/temporal/README.md`. |
| `src/TransactHub.Engine.Flowable` | The Flowable binding (plan D3): `FlowableRest`, the client of the Flowable REST server (deploy, start, acquire and lock, complete, fail, BPMN errors, jobs, timers, messages), and `FlowableBinding`, the external worker that runs the exported BPMN there, with the payloads in the claim check. See `studio/engines/flowable/README.md`. |
| `src/TransactHub.Api` | The release API: `POST /releases` (a studio package, checked against its manifest, stored once), `POST /activations`, `GET /activations/active`, `POST /import-branch` (the branch the studio exports to). PUT, PATCH and DELETE are refused. |
| `src/TransactHub.Telemetry` | OpenTelemetry for every runtime host: traces, metrics and logs over OTLP by the standard `OTEL_*` variables, masking of personal data before export, `/health/live` and `/health/ready` (R-09-23 to R-09-28). See `docs/north-star/guidelines/observability.md`. |
| `observability/` | The local monitoring stack for development: collector, Jaeger, Loki, Prometheus, Grafana and the dashboards Payments, Engines, Ports, Store (R-09-29). |
| `load/` | The load harness (R-09-30): synthetic WPS salary files on Temporal, its persistence on SQL Server or PostgreSQL, the store on either. See `docs/runtime/LOAD-TEST.md`. |
| `tools/TransactHub.TemporalHarness` | Runs a plan (a conformance scenario or a deployment) on Temporal and prints the observation. |
| `tools/TransactHub.FlowableHarness` | Runs a deployment's plan on the Flowable REST server at `TH_FLOWABLE_URL` and prints the observation. |
| `tools/temporal-test-server` | Temporal's time-skipping test server from Maven Central: `setup.sh` once, then the harness starts it. |
| `tests/TransactHub.Store.Tests` | Every store test on SQL Server 2022 and PostgreSQL 16 (Testcontainers), and the database-independence check. |
| `tests/TransactHub.Engine.Flowable.Tests` | The BPMN reader, and the worker client and the binding against the Flowable REST server 7.1.0 (Testcontainers). |

## Commands

```
cd runtime
dotnet test                       # needs Docker: starts SQL Server, PostgreSQL and Flowable REST containers
dotnet tool restore
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.SqlServer -s src/TransactHub.Store.Migrations.SqlServer -o Migrations
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.PostgreSql -s src/TransactHub.Store.Migrations.PostgreSql -o Migrations
```

A model change needs a migration for both providers; a test fails if either is missing.

## Database independence

The runtime reaches its data only through EF Core. The provider is chosen by `StoreOptions` (`SqlServer` or `PostgreSql`), in one place (`StoreDbContext.Configure`). `NeutralityTests` fails the build on a database driver, a raw connection, raw SQL, a T-SQL idiom or a provider call anywhere else in `src/`, and on a provider package referenced by any project but the store.
