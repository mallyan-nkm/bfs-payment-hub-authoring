# Part 5. Workflow DSL

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 5.1 Purpose

The DSL is the single definition of a workflow. It is block-structured and engine-neutral. It evolves today's JSON definition and borrows construct names from the CNCF Serverless Workflow DSL where they fit.

## 5.2 Kinds

| Kind | Holds | Durable |
| --- | --- | --- |
| `platform` | The lifecycle for a family of payments, with one or more slots. Any number may exist. | Yes |
| `leg` | The part unique to one scheme and one direction. Fills a slot. Runs as a child workflow. | Yes |
| `segment` | A reusable run of steps, inlined at publish. | Not on its own |
| `handler` | A stateless request and response. No await, timer, hold or write. | No |

## 5.3 Constructs (the portable core)

| Step | Form | Meaning |
| --- | --- | --- |
| Call | `call: <activity>` with optional `id`, `level`, `policy`, `handle` | Run one activity |
| Handle | `handle: { <outcome>: <action> }` | What to do on each non-primary outcome |
| Action | steps, or `{ end: <outcome> }`, or `{ continue: true }` | Run steps, end the workflow, or record and carry on |
| Switch | `switch: { by: { outcomeOf \| decision }, cases, default }` | Branch on a named outcome or decision |
| Parallel | `parallel: { branches, join: all }` | Run branches together; wait for all |
| For each | `forEach: { in, do, concurrency, onItemFailure }` | Run steps per batch item |
| Loop | `loop: { max, every, until, do }` | A bounded loop, for business retry |
| Await | `await: { message, correlateBy, timeout, calendar, onTimeout }` | Wait for a message, with a timeout |
| Wait | `wait: <duration>` | A timer |
| Slot | `slot: <name>` with `handle` | Run the leg that fills the slot |
| Use | `use: <segment>` | Inline a segment |
| Try | `try: [...]` with `catch` and `compensate: all` | Scope for errors and compensation |
| Hook | `hook: <hook point>` | Run the tenant's hook, if one is bound |
| End | `end: <outcome>` with optional `compensate: all` | End with an exit outcome |

Top-level blocks: `entry`, `exit`, `slots`, `hookPoints`, `signals`, `parameters`, `deadline`, `finally`, `synchronous`, `artefacts`, `do`.

### Sending and waiting for a reply

An activity never sits waiting for a reply that comes later. It sends and returns; the workflow waits. Four forms cover every case.

| The reply comes | Form | Example |
| --- | --- | --- |
| On the same connection, inside the attempt timeout | One `exchange` activity. Its outcome is the reply; no reply is the outcome `unknown` | `uae.aani.exchange.send-credit.v1` |
| Later, as its own message | A send step, then an `await` step on that message, with a timeout and a timeout branch | `submit` then `await-ack` in `uae.wps.wf.salary-file-out.v1` |
| Only when asked | A `loop` with `every` around an inquiry activity | The status inquiry in `uae.aani.wf.credit-out.v1` |
| From a person | A `hold` activity, which creates the item and waits for the decision | `p.hold.repair.v1` |

The reply reaches the workflow through the front door, not through the activity that sent the request. The scheme's dispatch table marks the message `deliver` and names its correlation key; the front door finds the instance and hands it the message. On an engine that hand-over is a signal or an external event (Part 9). In this DSL the word signal is kept for the four operator controls.

## 5.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-05-01 | A workflow MUST validate against `workflow.schema.json`. | L00 |
| R-05-02 | The DSL MUST NOT use a key that YAML 1.1 reads as a boolean (`on`, `off`, `yes`, `no`). Outcome handling is `handle`; a switch selects `by`. | Schema (L00) |
| R-05-03 | Flow MUST be block-structured. There is no jump. | Schema (L00) |
| R-05-04 | Every non-primary outcome of a called activity MUST have an entry in `handle`, and `handle` MUST name only outcomes the activity returns. | Lint L05 |
| R-05-05 | A branch MUST read a named outcome or a decision. | Schema (L00) |
| R-05-06 | A platform workflow MUST declare its slots. A slot is a contract: reads, writes, outcomes and the primary outcome. | Schema (L00) |
| R-05-07 | A leg MUST name the slot it fills and the platform workflows it may run in. Its exit outcomes MUST be a subset of the slot's. | Lint L08 |
| R-05-08 | A leg is chosen by the dispatch table for the message that started the payment, or by the route decision for a payment order. | Conformance CK-SLOT-01 |
| R-05-09 | A hook step MUST name a hook point declared in the workflow or in a segment it uses. | Lint L06 |
| R-05-10 | A hook point is part of the interface. Adding one is a minor; changing or removing one is a major. | Lint L26 |
| R-05-11 | A handler MUST contain only call, switch and end steps, and MUST call only pure or read activities and exchange activities in `respond` mode. | Lint L07 |
| R-05-12 | A workflow MAY handle only the fixed signals: `pause`, `resume`, `cancel`, `re-screen`. | Schema (L00) |
| R-05-13 | `cancel` MUST run compensation for completed steps in reverse order, then `finally`. | Conformance CK-SIGNAL-02 |
| R-05-14 | `finally` MUST run on every end, whatever the outcome, after any compensation. | Conformance CK-FINALLY-01 |
| R-05-15 | `compensate: all` MUST run the compensation of every completed write step in the enclosing try, in reverse order. | Conformance CK-COMP-01 |
| R-05-16 | A step's effective policy is resolved type default, then activity, then step. The studio shows where each value came from. | Conformance CK-POLICY-01 |
| R-05-17 | Retry MUST apply only to activities that are pure, read, or carry an idempotency key. | Lint L20 |
| R-05-18 | The sum of attempt budgets on the longest path of a leg MUST fit inside its deadline, where one is declared. | Lint L21 |
| R-05-19 | A long wait MUST be an `await` or a `wait`, never an activity timeout. `calendar: business` counts business days. | Review; conformance CK-AWAIT-02 |
| R-05-20 | A deadline MUST be realised as a timer with a handler, never as an engine execution timeout. | Conformance CK-DEADLINE-01 |
| R-05-21 | An awaited message MUST be in the scheme's catalogue with a `deliver` disposition. | Lint L16 |
| R-05-22 | Every message in a scheme's catalogue MUST have exactly one disposition. A pack with a `planned` message MUST NOT publish. | Schema `scheme` (L00); lint L10 |
| R-05-23 | Every path a step reads MUST be written by an earlier step, by intake, or be in `entry.reads`. | Lint L19 |
| R-05-24 | Step ids MUST be unique within a workflow after segments are inlined. | Lint L29 |
| R-05-25 | Rule evaluation and lookups MUST run in activities, never inline, so that flow is deterministic. | Schema (L00); conformance kit |
| R-05-26 | A construct outside the portable core MUST be flagged per target engine and MUST NOT appear in `p` scope. | Lint L24 |
| R-05-27 | An exported workflow MUST carry its own release, the DSL version and the `artefacts` pins. | Lint L23 |
| R-05-28 | A call with `level: item` inside a `forEach` over `items[]` binds `payment` to the payment of the current item, so a payment-level block is reused per item without change. | Conformance CK-FOREACH-01 |
| R-05-29 | A message with a `deliver` disposition MUST be awaited by at least one workflow of its scheme. | Lint L10 |
| R-05-30 | A message delivered to an instance before the matching `await` starts MUST be kept and consumed by that `await`. | Conformance CK-AWAIT-04 |
| R-05-31 | A `deliver` message that finds no instance waiting for it, because none exists, the instance has ended or the `await` has timed out, MUST be recorded against the payment and handed to operations as unmatched. It MUST NOT start a workflow, resume one, or be dropped. | Conformance CK-AWAIT-03 |
| R-05-32 | A second copy of a message that an `await` has already consumed MUST be recorded and ignored. | Conformance CK-AWAIT-05 |

## 5.5 Messages that are not payments

| Message | Disposition | What runs |
| --- | --- | --- |
| Starts a payment | `start` with kind `payment` | A platform workflow with the named leg |
| Starts an exception or request | `start` with kind `request` or `return` | The request or inward platform workflow with the named leg |
| Belongs to a running payment | `deliver` | The leg that awaits it, found by correlation key |
| A question about a payment we hold, or a stateless service | `handler` | A handler |
| Sent by us | `sentBy` | The exchange block that sends it |
| Not yet defined | `planned` | Nothing; the pack cannot publish |

An inward platform workflow MAY declare a second slot, `scheme-refuse`, so that a leg always answers the scheme when the platform refuses a payment.

## 5.6 The check catalogue

Every check that `tools/validate.py` implements has at least one case in `tools/mutation_tests.py` that breaks the repository and expects the check to fire. A check with no mutation case MUST NOT be marked Yes.

| Check | What it proves | In `tools/validate.py` |
| --- | --- | --- |
| L00 | Schema validity | Yes |
| L02 | Scope direction | Yes |
| L03 | References resolve | Yes |
| L04 | Major in name equals major of release | Yes |
| L05 | Outcomes handled | Yes |
| L06 | Hook points declared | Yes |
| L07 | Handler restrictions | Yes |
| L08 | Slots and legs agree | Yes |
| L09 | Bank-neutral shared content | Yes |
| L10 | Dispatch coverage: a `planned` message is a warning; a `deliver` message that no workflow awaits is an error | Yes |
| L11 | Unused blocks: a block that nothing reaches says what runs it | Yes, as a warning |
| L12 | Validation sets yield findings only | Yes |
| L13 | Hook stays inside its hook point contract | Yes |
| L14 | Configuration values inside bounds; bindings resolve | Yes |
| L15 | Test bench matches ports and scenarios | Yes |
| L16 | Awaited messages are deliver messages | Yes |
| L17 | Parameters declared | Yes |
| L18 | Extension paths registered | Yes |
| L19 | Data flow: reads are written earlier | To build (C04) |
| L20 | Effects: retry needs idempotency; writes have compensation | To build (C04) |
| L21 | Budgets fit the deadline | To build (C04) |
| L22 | Every port has a simulator, and every port a test can reach is bound in its bench | The first half, yes. The second is to build (C04) |
| L23 | Pins complete | To build (C05) |
| L24 | Portability per target | To build (C04) |
| L25 | Decision tables and yield graphs: catch-all, gaps, overlaps, no cycle | To build (C06) |
| L26 | Semver matches the interface diff | To build (C05) |
| L27 | Supersession: forward within one major line; every workflow release in scope uses the artefact; evidence stays in scope; maker and checker differ | Yes |
| L28 | Script determinism | To build (C07, C08) |
| L29 | Step ids unique after inlining | To build (C04) |

## 5.7 Examples

- Platform workflow with a slot, hook points and `finally`: `factory/p/workflows/p.wf.corporate.v1.yaml`.
- Inward platform workflow with a refuse slot: `factory/p/workflows/p.wf.inward.v1.yaml`.
- Leg with a short budget, a bounded inquiry loop and a deadline: `factory/uae.aani/workflows/uae.aani.wf.credit-out.v1.yaml`.
- Leg with long awaits on a business calendar: `factory/uae.wps/workflows/uae.wps.wf.refund-out.v1.yaml`.
- Handler: `factory/uae.aani/workflows/uae.aani.handler.status-inquiry-in.v1.yaml`.
- Dispatch table: `factory/uae.wps/scheme.yaml`.

## 5.8 Open decisions touching this part

D9 (synchronous segment for instant rails; the `synchronous` marker is carried now), D17 (where handlers run).

Open point: an `await` names one catalogue message. Where a scheme answers with different files or message types, the dispatch table maps them to one catalogue message that carries a status, as `uae.wps.ack` does for ACK and NAK. Waiting for the first of several different messages is not in the portable core. Add it only when a scheme needs it, with a mapping for each engine and a conformance scenario.
