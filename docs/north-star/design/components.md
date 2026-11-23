# Components

Each part of the north star: what it does, where it is, and whether it is built. The table at the end maps each of the source's service designs to its part here.

## Authoring

| Component | What it does | Where | Built |
| --- | --- | --- | --- |
| Content store | Reads `content-configurations/` from the branch into memory, cached by commit; writes through the Git Data API; a branch per change set | `studio/src/content/` | Yes |
| Artefact store and governance | Drafts, change sets, maker-checker, releases, retirement, supersession, audit | `studio/src/core/store.ts`, `studio/src/governance/` | Yes |
| Checks | Schemas and the static check catalogue, per document as it is edited | `studio/src/checks/`, `studio/src/core/schema.ts` | Yes |
| Rule service | Expression language, rule sets with NRules semantics, decision tables with gap and overlap analysis, parameters by date and tenant | `studio/src/rules/` | Yes (D13 open) |
| Interpreter | Runs a workflow on a virtual clock: every DSL construct, front door, hooks, pins, lookups | `studio/src/interp/` | Yes |
| Script sandboxes | TypeScript and Python scripts-behind with only the injected interface | `studio/src/interp/sandbox.ts` | Yes; not a security boundary (D14) |
| Simulators | Outside parties with scenarios and conversation state (Part 7) | `studio/src/sim/` | Yes |
| Test runner | Rule set, activity, hook, handler, leg and end-to-end tests | `studio/src/testing/` | Yes |
| Export | A package per engine target, with pins and one worker per task queue; BPMN for Flowable | `studio/src/export/` | Yes |
| Import | An extraction bundle from a source system, checked and saved as drafts (I-33) | `studio/src/import/`, `docs/import/` | Yes |
| Assistant | Drafts content on the Claude API; drafts pass the designer's gates and maker-checker (C16) | `studio/src/ai/` | Yes; not run against a live model here |
| Web app | Catalogue, editors, canvas, test bench, debugger, import, assistant | `studio/web/` | Yes |

## Running

One runtime serves one bank (`TH_BANK`, R-09-76). What of the source's platform is here, and what is not: `docs/runtime/PLATFORM-PARITY.md`.

| Component | What it does | Where | Built |
| --- | --- | --- | --- |
| Model | ISO 20022 internal model and `PaymentContext` | `runtime/src/TransactHub.Model` | Yes |
| Store | Insert-only data on two databases, insert-only at the database too (R-09-81) | `runtime/src/TransactHub.Store` | Yes |
| Release API | Store a release or distribution once, activate in a test environment, import the branch, pin a payment, record its end | `runtime/src/TransactHub.Api` | Yes: the bank's bearer JWTs and role map (`TransactHub.Auth`), every package's signature verified (`TransactHub.Security`) |
| File API and front door | Every file in and out: uploads, inboxes, checks, decryption, kept files, the outbox; the front door starts or correlates a payment on the bank's engine host | `runtime/src/TransactHub.FileApi` | Yes ([I-158](../../ISSUES.md#i-158-the-file-api-and-the-runtimes-front-door) to [I-161](../../ISSUES.md#i-161-the-banks-sftp-partners-have-no-pinned-host-key-and-the-logins-form)) |
| Interpreter | The one C# interpreter for every engine | `studio/engines/shared`, compiled into `runtime/src/TransactHub.Interpreter` | Yes |
| Temporal binding | Interpreter workflow, front-door workflow, host activities | `runtime/src/TransactHub.Engine.Temporal` | Yes |
| DTFx and Elsa on the store | The claim check journalled to the store | `runtime/src/TransactHub.Engines.Durable`, `runtime/tools/TransactHub.{Dtfx,Elsa}Harness` | Yes |
| Flowable binding | External-worker client of the Flowable REST server; the exported BPMN, payloads in the claim check | `runtime/src/TransactHub.Engine.Flowable` | Yes, in virtual and production mode |
| Engine host | A supervisor per engine, a host per distribution, side by side; plans from the stored package | `runtime/tools/TransactHub.EngineHost` | Yes; DTFx and Elsa runs do not survive a restart ([I-165](../../ISSUES.md#i-165-dtfx-and-elsa-runs-do-not-survive-a-restart-of-their-host-and-flowable-deploys-per-payment)) |
| Temporal on SQL Server | Persistence and visibility plugin, the default engine (R-09-79) | `runtime/temporal-mssql` | Yes, pinned to v1.32.2 |
| Native activities | The platform's posting, pricing, reservation and party register lookup; WPS's rules and file formats, golden against the source | `runtime/src/TransactHub.Activities.Platform`, `runtime/src/TransactHub.Activities.Wps` | Yes |
| Native host | Code-behind for activities bound `native` (R-08-14), and the holds of the operations-queue port when given the store | `runtime/src/TransactHub.Native` | Yes |
| Operations read model | The projection of the store's facts through the outbox; the operations views; operator actions; work, reports, unmasking, turnaround, retention, breaks; the registers | `runtime/src/TransactHub.Operations` | Yes ([operations-console.md](operations-console.md)) |
| Operations console | The console's API (OIDC, roles per tenant), the register API and the channel enquiry APIs, and its web app | `runtime/src/TransactHub.Operations.Api`, `runtime/ops-console` | Yes |
| Service console | Health, content install and activation with consent, logs, errors and runbooks, troubleshooting actions, backup, the assistant | `runtime/src/TransactHub.Service`, `runtime/src/TransactHub.Service.Api` | Yes |
| Ports | Interfaces, effective-dated port bindings, and the adapters: core banking, file transfer (`directory`, `sftp`), encryption, the registers, screening, notices, calendar, participant directory, facts out | `runtime/src/TransactHub.Ports` | Yes; the banks' counterparts are to come ([I-163](../../ISSUES.md#i-163-the-production-adapters-counterparts-are-the-banks-and-the-source-names-none)) |
| Test bench | Send data, run it on the engines a profile names, compare with the studio interpreter | `runtime/tools/TransactHub.TestBench`, `runtime/testbench` | Yes |
| Laptop stack | The studio and one bank's runtime in Docker containers | `deploy/laptop` | Yes |

## How a payment is routed

1. A message or file reaches the front door.
2. The scheme's dispatch table names the intake map and the leg it starts.
3. `p.table.workflow-select.v1` picks the platform workflow from the payment's kind, direction and segment.
4. The leg fills that workflow's `scheme-leg` slot.
5. The route decision `p.decide.rail.v1` passes only a rail the bank has enabled in its configuration set.

This replaces the source's routing service, message processor and per-scheme intake.

## Rules and values

- A rule, rule set or table is authored in the studio, tested with its examples, and published.
- A value a bank may change is a parameter with a default and bounds. The bank sets it in a configuration set, effective-dated.
- The runtime reads published versions only, as of the date the rule asks for: the payment's acceptance or the step (plan W4).

This replaces the source's rule engine screens and its four-level configuration with hot reload.

## Participants and code sets

Code sets are versioned registries, looked up by date (C03); their content in the studio was written from memory and is marked unverified. Reference data (the participant directory from the Central Bank's register, emirates, free zones, purpose codes, the business calendar, a bank's branches, calendar and cut-offs) are reference tables with editions by date, edited in the studio and served by the runtime at `GET /lookups` (R-01-25, R-09-75). This replaces the source's participant directory service; the routing codes are still missing ([I-151](../../ISSUES.md#i-151-what-the-reference-tables-still-lack)).

## Map from the source's service designs

| Source design | North-star part |
| --- | --- |
| API gateway | The runtime's API and front door. The gateway product is a deployment choice ([../deployment/deployment.md](../deployment/deployment.md)) |
| Routing | Dispatch table, workflow-select, route decision (above) |
| Message processing | Front door and maps |
| Workflow engine | Four engines behind one seam ([../architecture-essentials.md](../architecture-essentials.md)) |
| Platform worker | Workers per task queue derived from the scope (R-09-05) |
| Bank worker, orchestrator | None: a bank is configuration; the engine binding hosts the client |
| Configuration | Configuration sets on the branch |
| Rule engine dynamic configuration | Rules and values (above) |
| Participant directory | Reference tables (above) |
| Simulator | The studio's simulators; a deployable stub for tests, never in a promotable pack |
| AI workflow builder | The studio's assistant |
| Operations | The operations console: declared operator actions on the packs' operations views ([operations-console.md](operations-console.md), I-65) |
| IAM, RBAC | [../guidelines/access-control.md](../guidelines/access-control.md) |
| Smart integration | Ports and adapters |
| Report sync | The outbox and the operations read model; reports and fixed reports in the console |
| SFTP gateway | The File API and its `sftp` adapter |
