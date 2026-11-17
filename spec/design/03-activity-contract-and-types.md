# Part 3. Activity contract and types

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 3.1 Purpose

A block is its contract. The script-behind and the code-behind are two implementations of the same contract. This part defines the contract and the eleven activity types.

## 3.2 Types

| Type | What it does | Lifecycle stage | Usual effect | Standard outcomes, primary first |
| --- | --- | --- | --- | --- |
| `map` | Parses a source model into canonical, or renders canonical out | Intake, scheme leg | pure | `mapped`, `unmappable` |
| `validate` | Runs a rule set and returns findings | Check | pure | `pass`, `repair`, `reject` |
| `enrich` | Looks up and adds reference data | Check | read | `found`, `not-found` |
| `screen` | Sanctions, AML and fraud disposition | Screen | external | `clear`, `hit`, `pending` |
| `decide` | Evaluates a decision table and records the reason | Route | pure | `decided`, `no-match` |
| `reserve` | Reserves or releases funds; checks liquidity or prefund | Reserve | write | `reserved`, `insufficient` |
| `exchange` | Sends to a scheme, responds to it, asks it for status, or reports to it | Scheme leg | external | `accepted`, `rejected`, `unknown` |
| `post` | Posts with accounting templates; verifies; reverses | Post, close | write | `posted`, `rejected`, `unknown` |
| `notify` | Tells a channel; publishes a fact outward | Close | write | `notified` |
| `hold` | Parks the payment for a person | Any | write | `released`, `rejected`, `expired` |
| `reconcile` | Matches scheme reports to the record | Close | read | `matched`, `unmatched` |

A block MAY name its outcomes differently where the business meaning differs; the first outcome is always the primary one.

## 3.3 Effect classes

| Effect | Meaning | Retry | Idempotency key |
| --- | --- | --- | --- |
| `pure` | No input but the context, parameters and rule sets; no side effect | Not needed | Not needed |
| `read` | Reads through a port; no side effect | Allowed | Not needed |
| `write` | Changes state inside the bank through a port | Allowed with a key | Required |
| `external` | Sends to or asks a party outside the bank | Allowed with a key | Required |

## 3.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-03-01 | An activity MUST declare type, reads, writes, outcomes, effect and implementation. | Schema `activity` (L00) |
| R-03-02 | Exactly one outcome MUST be primary. A workflow continues past the primary outcome and MUST handle every other one. | Schema (L00); lint L05 |
| R-03-03 | An outcome is a business result. A technical failure is an error, never an outcome. | Review; conformance CK-RETRY-01 |
| R-03-04 | Every error MUST be marked retryable or not. A business rejection MUST be non-retryable and SHOULD carry an ISO reason code. | Schema (L00) |
| R-03-05 | A `write` or `external` activity MUST declare an idempotency key. The key MUST derive from business ids and be the same on every attempt. | Schema (L00); conformance CK-RETRY-01 |
| R-03-06 | An activity MUST reach outside the sandbox only through its declared ports. | Sandbox test (C07, C08) |
| R-03-07 | An activity MUST read and write only its declared paths. A read or write outside them fails the run. | Sandbox test (C07, C08) |
| R-03-08 | A `validate` or `decide` activity MUST name its rule sets and MUST NOT carry rule logic in its script. | Schema (L00) |
| R-03-09 | An `exchange` activity MUST declare a mode (`send`, `respond`, `inquire`, `report`) and at least one port. | Schema (L00) |
| R-03-10 | A `map` activity MUST declare direction and source model. An inward map SHOULD round-trip. | Schema (L00); map tests |
| R-03-11 | After a send whose outcome is `unknown`, a workflow MUST NOT send again with a new id. It inquires with the original ids, then decides. | Conformance CK-UNKNOWN-01 |
| R-03-12 | A `write` activity that can be undone SHOULD name its compensation. | Lint L20 |
| R-03-13 | An activity MUST NOT branch on the tenant. | Lint L09; review |
| R-03-14 | An activity MUST declare its level when it works on a batch or an item. | Schema (L00) |
| R-03-15 | Policy on the activity is a default. A step MAY override it; the type default applies where neither sets a value. | Conformance CK-POLICY-01 |
| R-03-16 | An activity MUST ship at least one test per outcome. | Publish check (C11) |
| R-03-17 | A port names a party by role. It MUST NOT name an endpoint, a bank or a vendor. | Schema `port` (L00); lint L09 |
| R-03-18 | An activity that no workflow calls MUST say what runs it: `invokedBy: front-door` or `invokedBy: schedule`. A block that nothing reaches and that says neither is reported. | Lint L11 |

## 3.5 Type default policies

Values are placeholders. They are parameters of the engine binding, set per environment.

| Type | Attempt timeout | Attempts | Non-retryable |
| --- | --- | --- | --- |
| `map`, `validate`, `decide` | PT5S | 1 | All errors |
| `enrich`, `reconcile` | PT5S | 3 | None |
| `screen`, `exchange`, `post`, `reserve` | PT10S | 3 | `BusinessReject` |
| `notify` | PT5S | 5 | None |
| `hold` | None; it waits for a person | 1 | All errors |

## 3.6 Implementation bindings

| Binding | What runs in production | Fits |
| --- | --- | --- |
| `script` | The script-behind, in a sandboxed worker | Maps, low-volume blocks, bank hooks |
| `native` | Code-behind in the platform language | Hot-path and regulated blocks |
| `builtin` | An existing platform service behind the contract | Screening, reservation, accounting, rules |

## 3.7 Examples

- External block with a short budget: `factory/uae.aani/activities/uae.aani.exchange.send-credit.v1.yaml`.
- Write block with compensation: `factory/p/activities/p.reserve.funds.v1.yaml`.
- Item-level validate block: `factory/uae.wps/activities/uae.wps.validate.salary-record.v1.yaml`.
