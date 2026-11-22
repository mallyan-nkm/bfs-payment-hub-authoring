# Requirements

Where the north star's requirements are, and what became of the source's. It replaces the source's user, system and microservice requirement specifications and its traceability matrix.

This page states no requirement of its own. A requirement here is one row in the spec: id, requirement, check (`spec/CLAUDE.md`, Writing). The runtime's requirements are the plan's workstreams and phase exits until they are written as rows.

## Where requirements live

| Kind | Where | How many rows | Each names its check |
| --- | --- | --- | --- |
| Studio: names, scopes, versions | `spec/design/01-concepts-scopes-names-versions.md` | 24 | Yes |
| Canonical context (ISO 20022) | `spec/design/02-canonical-context.md` | 16 | Yes |
| Activity contracts and types | `spec/design/03-activity-contract-and-types.md` | 23 | Yes |
| Rules | `spec/design/04-rule-model.md` | 22 | Yes |
| The DSL and its checks | `spec/design/05-workflow-dsl.md` | 42 | Yes |
| Tenants, hooks, configuration | `spec/design/06-tenants-hooks-configuration.md` | 20 | Yes |
| Simulators | `spec/design/07-simulator-model.md` | 14 | Yes |
| Scripts-behind and conformance | `spec/design/08-script-behind-and-conformance.md` | 14 | Yes |
| Export and engine bindings | `spec/design/09-export-bindings.md` | 22 | Yes |
| Studio functions | `spec/design/10-studio-functions.md` | 6 | Yes |
| Packs and governance | `spec/design/11-packs-and-governance.md` | 25 | Yes |
| Factory pack | `spec/design/12-factory-pack.md` | 11 | Yes |
| Conformance kit | `spec/design/annex-c-conformance-kit.md` | 8 | Yes |
| Runtime | `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`, W1 to W8 and section 6 | Phase exits | Partly: each built item names its test |
| No function lost from the source | `docs/runtime/PARITY.md` | One line per source item | Yes: golden, test or "dropped, because" |

The counts are of rows that start `| R-` in each part, as of this page.

## The source's user requirements

The source wrote 16 user requirements over six schemes and the platform. Under the north star:

| Source area | North star | Status |
| --- | --- | --- |
| Outward and inward payments for IPI, FTS, cross-border and direct debit | Later, on the same store and lifecycle (D6) | Not in the first drop |
| WPS salary processing | The `uae.wps` legs on the common lifecycle; outward first (P1), inward complete in P2 | Provisional scheme content; outward runs end to end |
| Multi-tenancy | Tenants in the studio (R-06-01 to R-06-04); tenant as a column in the store | Built |
| Bank extension and integration | Hooks, configuration sets and port bindings (Part 6; plan W6) | Built for core banking |
| Observability and monitoring | [I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio) | Not decided |
| Security and access control | Studio roles and maker-checker (C17); operators [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) | Studio built; operators not decided |
| Immutable audit | The store's fact chain; the studio's audit on the branch | Built |
| Reporting | [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) | Not decided |
| Settlement and reconciliation | `p.*` lifecycle steps (reconcile) | Target (plan W6) |
| Exceptions and disputes | Exception items in the store; operator actions (plan W4) | Store built; actions not decided |
| Notification | A `p.*` lifecycle step and port | Target |
| Admin UI | The studio's web app for authoring; operators [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) | Studio built |

## The source's system requirements

| Source area | North star |
| --- | --- |
| Payment submission and routing | Front door, dispatch table, workflow-select, route decision ([../design/components.md](../design/components.md)) |
| Workflow orchestration | Four engines, one interpreter, one kit |
| Inbound messages and bulk files | Intake maps; a bulk is a pain.001 payment information block |
| Message conversion | Map blocks; only they touch another format |
| Business rules and validation | The rule service (Part 4) |
| Settlement and positions | Lifecycle steps behind the core-banking port |
| Security requirements | [../security/threat-model.md](../security/threat-model.md) |
| Performance, availability, recovery | [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). The source's figures are not carried: they were for its own services and sizes |

## The source's microservice requirements

The runtime is not split into the source's eighteen services, so their requirements do not carry. [../design/components.md](../design/components.md) maps each service to its part here.

## Tracing a requirement

- From a spec row to its check: the row names it. A new check goes into the catalogue, the validator and a mutation case in one change (`spec/CLAUDE.md`, Checks).
- From a source item to its proof: `docs/runtime/PARITY.md`.
- From a story to its requirements: the story's Definition of Ready and Done in `spec/build/`.
- A requirement with no check is an Issue, not a requirement.
