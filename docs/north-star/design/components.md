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

| Component | What it does | Where | Built |
| --- | --- | --- | --- |
| Model | ISO 20022 internal model and `PaymentContext` | `runtime/src/TransactHub.Model` | Yes |
| Store | Insert-only data on two databases | `runtime/src/TransactHub.Store` | Yes |
| Release API | Store a release once, activate, import the branch | `runtime/src/TransactHub.Api` | Yes, without authentication ([I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it)) |
| Interpreter | The one C# interpreter for every engine | `studio/engines/shared`, compiled into `runtime/src/TransactHub.Interpreter` | Yes |
| Temporal binding | Interpreter workflow, front-door workflow, host activities | `runtime/src/TransactHub.Engine.Temporal` | Yes |
| DTFx and Elsa on the store | The claim check journalled to the store | `runtime/src/TransactHub.Engines.Durable`, `runtime/tools/TransactHub.{Dtfx,Elsa}Harness` | Yes |
| Flowable binding | External-worker client and BPMN deployment | `TransactHub.Engine.Flowable` | No (plan P3) |
| Temporal on SQL Server | Persistence and visibility plugin | `runtime/temporal-mssql` | Yes, pinned to v1.32.2 |
| WPS activities | The scheme's own rules and file formats, golden against the source | `runtime/src/TransactHub.Activities.Wps` | In part (outward) |
| Native host | Code-behind for activities bound `native` (R-08-14), and the holds of the operations-queue port when given the store | `runtime/src/TransactHub.Native` | Yes |
| Operations read model | The projection of the store's facts through the outbox; the operations views; operator actions | `runtime/src/TransactHub.Operations` | Yes ([operations-console.md](operations-console.md)) |
| Operations console | The console's API (OIDC, roles per tenant) and its web app | `runtime/src/TransactHub.Operations.Api`, `runtime/ops-console` | Yes; signals reach no engine yet ([I-99](../../ISSUES.md#i-99-signals-retry-and-resend-have-no-path-from-the-runtime-to-an-engine)) |
| Ports | Interfaces and effective-dated port bindings | `runtime/src/TransactHub.Ports` | Core banking only |
| Platform and UAE activities | `p.*` and `uae.p.*` code-behind | `TransactHub.Activities.Platform`, `TransactHub.Activities.Uae` | No |
| Front door host | One intake API; file watchers call it | `TransactHub.FrontDoor` | No; the front door runs as a workflow today |
| Hosts | Worker and API hosts, one engine per installation | `TransactHub.Host` | No |

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

Code sets and participant lists are versioned registries and lists, looked up by date (C03). This replaces the source's participant directory service. The registry content in the studio was written from memory and is marked unverified.

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
| Participant directory | Registries and lists |
| Simulator | The studio's simulators; a deployable stub for tests, never in a promotable pack |
| AI workflow builder | The studio's assistant |
| Operations | The operations console: declared operator actions on the packs' operations views ([operations-console.md](operations-console.md), I-65) |
| IAM, RBAC | [../guidelines/access-control.md](../guidelines/access-control.md) |
| Smart integration | Ports and adapters |
| Report sync | The outbox ([I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side)) |
