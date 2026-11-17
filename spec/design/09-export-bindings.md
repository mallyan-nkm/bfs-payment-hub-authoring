# Part 9. Export bindings

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 9.1 Purpose

The DSL is the single source of truth and stays engine-neutral. Temporal, DTFx and Flowable are all delivery targets, and together they are the proof of independence. This part defines what is exported and how each engine runs it.

## 9.2 Strategies

| Target | Strategy | What is exported | State |
| --- | --- | --- | --- |
| Temporal | Interpreter | The workflow documents as data, the manifest, task registrations by exact release | Exists today; extend |
| DTFx | Interpreter | The same documents; one generic orchestration interprets them | To build |
| Flowable | Generation | BPMN 2.0 XML per workflow, plus worker topics by exact release | To build; least certain |

## 9.3 Construct mapping

| Construct | Temporal | DTFx | Flowable |
| --- | --- | --- | --- |
| Call | Activity on a task queue | Task activity | External worker task |
| Slot, use of a leg | Child workflow | Sub-orchestration | Call activity |
| Handler | Plain request handler; no workflow | Plain request handler; no orchestration | Plain request handler; no process |
| Hook | Child workflow or activity from the tenant binding | Sub-orchestration or task from the tenant binding | Call activity or service task from the tenant binding |
| Rule evaluation | Activity calling the rule service | Task calling the rule service | Service task calling the rule service |
| Switch | Interpreter branch | Interpreter branch | Exclusive gateway |
| Parallel | Concurrent activities, await all | `Task.WhenAll` | Parallel gateway |
| For each | Loop or child workflows; continue-as-new for large batches | Loop or sub-orchestrations; continue-as-new | Multi-instance, sequential or parallel |
| Loop | Interpreter loop with timer | Interpreter loop with durable timer | Modelled loop with timer event |
| Await | Signal and timer; signals received early are kept in workflow state | External event and durable timer; events received early are kept in orchestration state | Message catch event with timer boundary; early messages are held by the binding |
| Wait | Timer | Durable timer | Timer event |
| Block | A frame in the interpreter; no engine construct | A frame in the interpreter; no engine construct | Embedded sub-process whose end events set the outcome, then an exclusive gateway |
| Return | Leaves the frame with an outcome | Leaves the frame with an outcome | An end event of the sub-process |
| Try, compensate | Interpreter runs compensations in reverse | Interpreter runs compensations in reverse | Compensation boundary events and handlers |
| Signals | Signals | External events | Signal or message events |
| Hold | Activity creates the item; await a signal | Task creates the item; await an external event | User task or receive task |
| Deadline | Timer and cancellation scope | Timer race | Timer boundary on the sub-process |
| Finally | Interpreter runs it on every end | Interpreter runs it on every end | End-path sub-process on every end event |

## 9.4 Policy mapping

| Policy | Temporal | DTFx | Flowable |
| --- | --- | --- | --- |
| `attemptTimeout` | Start-To-Close | Durable timer raced against the task | Timer boundary on the external worker task |
| `totalTimeout` | Schedule-To-Close | `RetryOptions.RetryTimeout` | Timer boundary on the enclosing sub-process |
| `retry` | Retry Policy | `RetryOptions` with `ScheduleWithRetry` | A modelled loop in a sub-process per call, for fixed and growing backoff alike (proved in C22-S1) |
| Queue timeout, heartbeat | Schedule-To-Start, Heartbeat; from the binding | Not available | Not available |

## 9.5 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-09-01 | An export MUST hold: workflow, leg, segment and handler documents with pins; activity contracts; rules; maps; the pack manifest; the engine binding; the test pack; the conformance pack. | Export test (C20) |
| R-09-02 | A tenant's export adds its bank pack and its configuration set. | Export test (C18) |
| R-09-03 | The engine binding MUST be a separate document. The designer never edits it. | Schema `engine-binding` (L00) |
| R-09-04 | A task MUST be registered as `<name>@<release>`, so two releases of one line run side by side. | Conformance CK-PIN-01 |
| R-09-05 | The worker queue or topic MUST be derived from the scope by the binding. | Schema (L00) |
| R-09-06 | An interpreter MUST be deterministic. It MUST NOT evaluate rules, read values or call ports inline. | Conformance kit; replay test |
| R-09-07 | Every target MUST produce the same business trace as the studio interpreter for every scenario of the conformance kit. | Conformance kit runner (C23) |
| R-09-08 | The conformance kit MUST gate every release on every target. | Release check (C19) |
| R-09-09 | A workflow that uses an extension construct MUST name the targets it supports. | Lint L24 |
| R-09-10 | Engine state MUST carry references, never the payment payload. | Conformance CK-CLAIM-01 |
| R-09-11 | On DTFx, an attempt timeout abandons the late result; it does not cancel the running task. The activity MUST therefore be idempotent. | Conformance CK-TIMEOUT-01 |
| R-09-12 | On Flowable, an activity MUST be exported as a wait state (external worker task) so that timer boundaries apply. | Generator test (C22) |
| R-09-13 | Large batches MUST be chunked on history-based engines. The chunk size comes from the binding. | Conformance CK-FOREACH-02 |
| R-09-14 | Handlers MUST be exported as plain request handlers at the interop boundary, outside the durable engine. | Conformance CK-HANDLER-01 |
| R-09-15 | A generated BPMN document MUST be reproducible: the same input gives the same bytes. | Generator test (C22) |
| R-09-16 | A binding MUST keep a message that reaches an instance before its `await` starts, and MUST hand a message that finds no waiting instance to operations as unmatched. Where an engine delivers a message only to an execution that is already waiting, the binding holds the message until it is. | Conformance CK-AWAIT-03, CK-AWAIT-04 |
| R-09-17 | An engine binding MUST receive the expanded form only. It MUST NOT resolve a composite activity at run time. | Conformance CK-BLOCK-01 |

## 9.6 Core and extension profiles

The core is the constructs of Part 5, section 5.3. An extension is anything a single target offers beyond them. Extensions are flagged by lint L24, are never used in `p` scope, and are excluded from the conformance kit.

## 9.7 Known limits

- The Flowable column is the least certain. Build spec C22 starts with a spike that proves timer boundaries, retry and compensation on generated BPMN.
- Whether an instant rail runs every step as a durable task is open (D9). The `synchronous` marker lets the binding choose later.


### Proved on Flowable 7.1.0 (C22-S1)

The kit runs on an embedded Flowable 7.1.0 engine with the generated BPMN (`studio/engines/flowable/`). These patterns pass it.

| Construct | Pattern | Why |
| --- | --- | --- |
| Retry and attempt timeout | A modelled loop in a sub-process per call; the attempt timeout is a timer boundary on the external worker task; the total timeout a timer boundary on the sub-process | An attempt timeout ends the task before any failed-job retry, and a spent `failedJobRetryTimeCycle` ends in a dead-letter job, not the `unknown` outcome |
| Await | An event-based gateway between the message catch and a timer; the binding holds a message until a catch is waiting | Flowable delivers a message only to a catch that is already waiting (R-09-16) |
| For each | A multi-instance sub-process with outcomes local to the item; `onItemFailure: continue` is an error event sub-process inside it; `concurrency: n` runs in chunks of n | A parallel multi-instance of 10,000 items in one transaction is quadratic inside the engine |
| Compensation | Compensation boundary events and handler sub-processes; the flow waits on a message the binding sends when no handler is left | Flowable 7.1.0 does not wait for compensation handlers to finish |

Two engine gaps in Flowable 7.1.0, worked around in the binding:

| Gap | Work-around |
| --- | --- |
| Compensation handlers are not awaited (`waitForCompletion` is not implemented) | The binding signals completion when no handler is left |
| An interrupting event sub-process loses the boundary events of the main sub-process | Cancel ends main with a mark that a gateway after main reads, instead of throwing an end error |

Limits that remain: compensation runs in reverse document order, which in parallel branches is not always reverse completion order; chunked concurrency puts a barrier between chunks; external worker tasks are non-exclusive, as exclusive jobs were not acquired while parallel branches waited.

## 9.8 Examples

- Three bindings: `examples/bindings/bindings.yaml`.
- Pins as they appear in a manifest: `factory/p/pack.yaml`.
