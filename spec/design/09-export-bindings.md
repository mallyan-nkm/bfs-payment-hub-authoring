# Part 9. Export bindings

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 9.1 Purpose

The DSL is the single source of truth and stays engine-neutral. Temporal, DTFx, Flowable and Elsa are all delivery targets, and together they are the proof of independence. This part defines what is exported and how each engine runs it.

## 9.2 Strategies

| Target | Strategy | What is exported | State |
| --- | --- | --- | --- |
| Temporal | Interpreter | The workflow documents as data, the manifest, task registrations by exact release | Exists today; extend |
| DTFx | Interpreter | The same documents; one generic orchestration interprets them | To build |
| Flowable | Generation | BPMN 2.0 XML per workflow, plus worker topics by exact release | To build; least certain |
| Elsa | Interpreter | The same documents; one generic Elsa activity interprets them, its journal kept in the workflow state | Built on Elsa 3.10.2 (I-30) |

## 9.3 Construct mapping

| Construct | Temporal | DTFx | Flowable | Elsa |
| --- | --- | --- | --- | --- |
| Call | Activity on a task queue | Task activity | External worker task | Operation run on the task's worker queue; the bookmark is resumed with the result |
| Slot, use of a leg | Child workflow | Sub-orchestration | Call activity | Child workflow instance of the same activity |
| Handler | Plain request handler; no workflow | Plain request handler; no orchestration | Plain request handler; no process | Plain request handler; no workflow instance |
| Hook | Child workflow or activity from the tenant binding | Sub-orchestration or task from the tenant binding | Call activity or service task from the tenant binding | Child workflow instance or operation from the tenant binding |
| Rule evaluation | Activity calling the rule service | Task calling the rule service | Service task calling the rule service | Operation calling the rule service |
| Switch | Interpreter branch | Interpreter branch | Exclusive gateway | Interpreter branch |
| Parallel | Concurrent activities, await all | `Task.WhenAll` | Parallel gateway | `Task.WhenAll` over the branches, in one episode |
| For each | Loop or child workflows; continue-as-new for large batches | Loop or sub-orchestrations; continue-as-new | Multi-instance, sequential or parallel | Loop or child instances; continue-as-new for large batches |
| Loop | Interpreter loop with timer | Interpreter loop with durable timer | Modelled loop with timer event | Interpreter loop with a journalled timer |
| Await | Signal and timer; signals received early are kept in workflow state | External event and durable timer; events received early are kept in orchestration state | Message catch event with timer boundary; early messages are held by the binding | A bookmark resumed by the message, raced with a journalled timer; early messages are kept in the journal |
| Wait | Timer | Durable timer | Timer event | Journalled timer |
| Block | A frame in the interpreter; no engine construct | A frame in the interpreter; no engine construct | Embedded sub-process whose end events set the outcome, then an exclusive gateway | A frame in the interpreter; no engine construct |
| Return | Leaves the frame with an outcome | Leaves the frame with an outcome | An end event of the sub-process | Leaves the frame with an outcome |
| Try, compensate | Interpreter runs compensations in reverse | Interpreter runs compensations in reverse | Compensation boundary events and handlers | Interpreter runs compensations in reverse |
| Signals | Signals | External events | Signal or message events | Events delivered at the bookmark |
| Hold | Activity creates the item; await a signal | Task creates the item; await an external event | User task or receive task | Operation creates the item; await an event |
| Deadline | Timer and cancellation scope | Timer race | Timer boundary on the sub-process | Timer race |
| Finally | Interpreter runs it on every end | Interpreter runs it on every end | End-path sub-process on every end event | Interpreter runs it on every end |

## 9.4 Policy mapping

| Policy | Temporal | DTFx | Flowable | Elsa |
| --- | --- | --- | --- | --- |
| `attemptTimeout` | Start-To-Close | Durable timer raced against the task | Timer boundary on the external worker task | Journalled timer raced against the operation; a late result is abandoned |
| `totalTimeout` | Schedule-To-Close | `RetryOptions.RetryTimeout` | Timer boundary on the enclosing sub-process | The retry plan's total time |
| `retry` | Retry Policy | `RetryOptions` with `ScheduleWithRetry` | A modelled loop in a sub-process per call, for fixed and growing backoff alike (proved in C22-S1) | The retry plan, each backoff a journalled timer |
| Queue timeout, heartbeat | Schedule-To-Start, Heartbeat; from the binding | Not available | Not available | Not available |

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
| R-09-11 | On DTFx and Elsa, an attempt timeout abandons the late result; it does not cancel the running task. The activity MUST therefore be idempotent. | Conformance CK-TIMEOUT-01 |
| R-09-12 | On Flowable, an activity MUST be exported as a wait state (external worker task) so that timer boundaries apply. | Generator test (C22) |
| R-09-13 | Large batches MUST be chunked on history-based engines. The chunk size comes from the binding. | Conformance CK-FOREACH-02 |
| R-09-14 | Handlers MUST be exported as plain request handlers at the interop boundary, outside the durable engine. | Conformance CK-HANDLER-01 |
| R-09-15 | A generated BPMN document MUST be reproducible: the same input gives the same bytes. | Generator test (C22) |
| R-09-16 | A binding MUST keep a message that reaches an instance before its `await` starts, and MUST hand a message that finds no waiting instance to operations as unmatched. Where an engine delivers a message only to an execution that is already waiting, the binding holds the message until it is. | Conformance CK-AWAIT-03, CK-AWAIT-04 |
| R-09-17 | An engine binding MUST receive the expanded form only. It MUST NOT resolve a composite activity at run time. | Conformance CK-BLOCK-01 |
| R-09-18 | On Elsa, the interpreter MUST keep its journal (the instance input and every result, in the order it arrived) in the workflow state Elsa persists, and MUST resume only from a bookmark. A resumed run MUST reach the same operations in the same order; a journal entry the replay does not reach MUST fail the run. | Conformance kit on Elsa (`studio/engines/elsa`); replay check in the harness |
| R-09-19 | The studio MUST be connected to exactly one GitHub repository and content branch, by its environment: `TH_CONTENT_REPO`, `TH_CONTENT_BRANCH`, `TH_GITHUB_TOKEN` and, for another API base, `TH_GITHUB_API`. The branch MUST already exist, and the studio MUST NOT start without a connection. Only a shared checker or service operations changes how the studio publishes (mode, engine target, environment), in `governance/settings.yaml`. The token MUST come from the environment: it is never stored, shown, logged or written to the audit. | Content store tests (`studio/test/content.test.ts`: "does not start without TH_CONTENT_REPO", "never stores, shows, logs or writes the token", the settings refusal in "in pull-request mode") |
| R-09-20 | Each published change set MUST be one commit on the content branch, by the studio's own identity, under `content-configurations/`: each member at `artefacts/<scope>/<name>/<release>.yaml` (a bank's own at `banks/<tenant>/artefacts/…`, a configuration set at `banks/<tenant>/configuration-sets/<release>.yaml`), each member's script, the engine package of each workflow at `releases/<scope>/<name>/<release>/<target>/`, the change set record at `change-sets/<id>.yaml`, its audit at `audit/change-sets/<id>.yaml` and, if the settings name an environment, an activation at `environments/<env>/activations/<id>.yaml`. The commit message names the members, the makers and the checker. A publish that does not reach the branch publishes nothing: the studio reads the branch again and says why. | Content store tests ("approval publishes the change set into the content branch as one commit") |
| R-09-21 | The branch is insert-only. The studio MUST NOT rewrite a file on the branch (the two state files of R-11-23 aside), MUST stop a write that would and name the file, and MUST NOT force a ref update: a ref update MUST fast-forward. When the branch moved, the studio reads it again, checks the write again on top of it, and retries once; if it moved again, the studio stops with a message. Publishing what is already there changes nothing. | Content store tests ("never rewrites a published file", "re-reads, rechecks and retries once") |
| R-09-22 | In pull-request mode, approval MUST commit the change set to its own branch `studio/<change set id>` and open a pull request into the content branch; the content branch is not written. The members stay in review, on their branch, until the pull request merges; then the studio reads them as published. | Content store tests ("in pull-request mode, approval opens a pull request"); `studio/test/blackbox/ui-git.test.ts` |

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

### Proved on Elsa 3.10.2 (I-30)

The kit runs on Elsa Workflows 3.10.2 (`studio/engines/elsa/`), with the same interpreter as DTFx (`studio/engines/shared/`). 46 of 46 scenarios pass, and two runs give the same traces.

| Who does what | |
| --- | --- |
| Elsa | Each engine instance is an Elsa workflow instance of one generic activity. Elsa keeps its state, suspends it at a bookmark while it waits, and resumes it with input. Its own serialiser writes the state the host keeps between runs |
| The interpreter | Runs again from the start on each resume, against the journal in the workflow state: an operation whose result is in the journal returns it; one that is not becomes pending |
| The host | Carries out pending operations (an activity attempt on the worker of its queue, a timer, a child instance, an event to another instance) on the virtual clock, and resumes the bookmark with each result |

Limits that remain:

- Elsa does not replay code, so the replay is the interpreter's own. Each resume reruns the journal; large batches stay small by the binding's chunk size and continue-as-new (CK-FOREACH-02: 10,000 items, 21 executions).
- Timers and retries are journalled operations run by the host's clock, not Elsa's scheduler or its activities, so that the kit can run on virtual time (R-C-06). A production host would hand timers to Elsa's scheduler.
- The state is kept by the host in memory between runs. A production host would use an Elsa persistence provider.
- Workflows are not shown in Elsa's designer: the documents stay the source, as for every target.

## 9.8 Examples

- Four bindings: `examples/bindings/bindings.yaml`.
- Pins as they appear in a manifest: `factory/p/pack.yaml`.
