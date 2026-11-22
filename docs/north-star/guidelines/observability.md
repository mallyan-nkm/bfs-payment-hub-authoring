# Observability

What a runtime host, engine host or the studio tells an operator about itself. It replaces the source's logging instructions, observability agent and skills, and its log catalogue.

**Status.** Decided by the owner on 22 November 2026 ([I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio)): OpenTelemetry for traces, metrics and logs in every runtime host and in the studio, and a load test before any bank goes live ([I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side), [`docs/runtime/LOAD-TEST.md`](../../runtime/LOAD-TEST.md)). The requirements are R-09-23 to R-09-30 and R-10-07 to R-10-09.

## The rules

These hold for every log, span and metric, in every host:

- **No personal data** in a log, a trace, a metric, an exception message, a test, a sample or a commit message. Log a payment's id and release, never a party's name, account, IBAN, identity number, email or phone (R-09-27, R-10-08). The source's privacy scan found this broken in places (PV-001).
- **Masking before export.** Every host passes every span attribute, status, event, log attribute, log message and exception through the masking rules before an exporter sees it: a key that names personal data has its value replaced by `[masked]`, and IBANs, emails and runs of eight or more digits are masked in every string, keeping the last four characters. The rules are the same in .NET (`runtime/src/TransactHub.Telemetry/Masking.cs`) and Node (`studio/src/telemetry/mask.ts`). The local collector's redaction processor is a second guard, not the first. A name in free text has no pattern: the hosts never put one there, and the tests prove that a WPS run exports no value from its party records.
- **The business trace is the record of a run.** The studio's run record keeps the business trace, the release trace, each step with its attempts and policy. Engines produce the same trace; the kit compares it. Telemetry is for operating the system.
- **Engine history holds ids, not data.** `PaymentContext` carries references only (plan W1).
- **The fact chain is the audit.** What happened to a payment is its facts, hash-chained in the store. Telemetry is never the legal record.
- **Telemetry never blocks the work.** With no endpoint configured nothing is installed and the API's no-op runs. A collector that is down loses telemetry, not payments.

## How it is built

| Part | What |
| --- | --- |
| Configuration | The standard variables only: `OTEL_EXPORTER_OTLP_ENDPOINT` (and the per-signal ones), `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_SDK_DISABLED`. Shared code names no backend. |
| .NET hosts | `runtime/src/TransactHub.Telemetry`: `TelemetryHost.Start` for a console host (the Temporal, DTFx, Elsa and Flowable harnesses), `AddTransactHubTelemetry` and `MapTransactHubHealth` for the release API. OpenTelemetry 1.20.1, pinned. Temporal's spans through `Temporalio.Extensions.OpenTelemetry` 1.21.1 (its `TracingInterceptor`), its SDK metrics through `Temporalio.Extensions.DiagnosticSource` 1.21.1. |
| .NET libraries | No OpenTelemetry package: `System.Diagnostics` only. `EngineTelemetry` (`studio/engines/shared/Telemetry.cs`, compiled into every engine binding, the studio's net8 harnesses included) and `StoreTelemetry` (an EF Core save interceptor in `TransactHub.Store`: every write, whoever makes it). |
| Node | `studio/src/telemetry/telemetry.ts`: the studio server, the CLI (and so the deploy runner) and each deployed worker. `@opentelemetry/*` 2.12.0 / 0.223.0 and the API 1.9.0, pinned. The worker bundles them (`worker.cjs`, about 1.2 MB). |
| Health | `GET /health/live` and `GET /health/ready` on the release API (ready: its store answers), the studio server (ready: the content branch is read) and each deployed worker (ready: its tasks are loaded) (R-09-28, R-10-09). |
| Local stack | `runtime/observability/`: the OpenTelemetry Collector, Jaeger, Loki, Prometheus and Grafana, every image pinned by tag and digest, with the dashboards Payments, Engines, Ports and Store (R-09-29). For development only. |

## One trace per payment

The deploy runner opens the span `th.payment` and `th.payment.intake` for the front door. Every process it starts continues the trace: a worker through `traceparent` on each request, the engine harness through `TRACEPARENT`. On Temporal the interceptor carries the trace through the front door, the interpreter workflows, each activity and each signal; on DTFx, Elsa and Flowable each attempt joins it through `EngineTelemetry`'s parent. Inside: `th.engine.run`, one `th.attempt <activity>` per attempt (engine side), one `th.activity <activity>` per attempt (worker side), one `th.port.call <port>` per port call, one `th.store.write <row kinds>` per save (R-09-24, R-09-25).

## Names

Lower case and dotted, the same in every host. Attributes carry ids, document names and outcomes only.

| Attribute | Meaning |
| --- | --- |
| `th.payment.id`, `th.instance`, `th.release`, `th.workflow` | Which payment, which engine instance, which release |
| `th.scheme`, `th.tenant`, `th.engine`, `th.queue` | Dimensions of every metric where they apply |
| `th.activity`, `th.step`, `th.attempt`, `th.level` | The attempt |
| `th.kind`, `th.outcome`, `th.error` | How it ended (an error by its name, never its message) |
| `th.port`, `th.port.result` | A port call |
| `th.store.op`, `th.store.kind`, `th.store.provider` | A save: the row kinds, the database |

| Metric | Dimensions |
| --- | --- |
| `th.payments`, `th.payment.lines`, `th.payment.duration` | outcome, scheme, tenant, engine |
| `th.activity.duration`, `th.activity.errors` | engine, activity, kind or error |
| `th.queue.in_flight` | queue |
| `th.worker.attempt.duration`, `th.worker.attempt.errors` | activity, queue |
| `th.port.call.duration`, `th.port.call.errors` | port, activity, result |
| `th.holds`, `th.holds.open` | activity |
| `th.store.write.duration`, `th.store.write.errors`, `th.store.rows` | row kinds, database |
| `th.release.stored`, `th.release.activations` | scope, environment |
| `http.server.request.duration` | method, route, status (the studio server) |
| Temporal SDK (`temporal_*`), Temporal server (`persistence_*`, `service_*`, `approximate_backlog_count`) | Temporal's own; the server's persistence metrics are where our SQL Server plugin shows |

## Running it locally

```
docker compose -f runtime/observability/docker-compose.yml up -d
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:14318 OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
cd studio && npm run studio -- deploy <package.zip> --workflow p.wf.corporate.v1 ...
# Grafana http://127.0.0.1:13000, Jaeger http://127.0.0.1:16686, Prometheus http://127.0.0.1:19090
docker compose -f runtime/observability/docker-compose.yml down -v
```

## What is not done

See [I-80](../../ISSUES.md#i-80-what-the-telemetry-does-not-yet-do): the facts do not carry the trace id, span events are masked through a private field of `Activity`, and no alert rules or service-level objectives are written.
