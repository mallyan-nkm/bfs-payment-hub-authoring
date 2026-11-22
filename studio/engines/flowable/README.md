# The conformance kit on Flowable

Build spec C22 (stories S1 to S5), with the kit adapter of C23-S2. The studio generates BPMN 2.0 for every workflow a scenario runs (`src/export/bpmn.ts`), from the expanded form (R-09-17). A Java harness deploys it into an embedded Flowable 7.1.0 engine on H2 in memory, runs it, works the external worker jobs through the party service, and moves the engine clock so that waits of days take no time. The kit runner compares what the harness observed with the expected trace, unchanged.

## How to run

Java 21 or later and Node 22 are needed (the harness compiles for release 21). Maven is fetched into `.bin/`, which is gitignored.

```
cd studio
engines/flowable/setup.sh                       # fetch Maven, build the harness (once)
npx tsx engines/flowable/run-kit.ts             # all 48 scenarios, PASS or FAIL each
npx tsx engines/flowable/run-kit.ts CK-COMP-01 --verbose
npx tsx engines/flowable/portability.ts         # C22-S5: every factory workflow, deployed into Flowable
```

Each run writes `out/<scenario>/`: the generated `*.bpmn20.xml`, the binding's `plan.json` and the harness's `observation.json`; `out/results.json` holds every comparison. Set `TH_DEBUG=1` to see each job the worker takes and each party reply.

| File | What it is |
| --- | --- |
| `adapter.ts` | The `KitAdapter`. It expands and generates every workflow the scenario runs (root, each release, legs, bound hooks), writes the plan and runs the harness. |
| `run-kit.ts` | Runs the kit with the adapter and prints `N of 48 conformance scenarios pass on Flowable`. |
| `portability.ts` | The portability report (C22-S5). |
| `harness/` | The Java harness: `Harness.java` (engine, worker, front door, signals, trace), `Handler.java` (plain request handlers), `Support.java` (clock, business calendar, party client, context paths). |

## What maps how

The generated model carries `th:` annotations (namespace `urn:transacthub:flowable:1`). The worker reads the step, activity, release and policy from `th:task`; the binding writes the business trace at `th:point` elements. Flow is in the BPMN only.

| DSL | BPMN on Flowable |
| --- | --- |
| call | An embedded sub-process with the step id as its id, holding an external worker task (`flowable:type="external-worker"`, topic `<queue>:<name>@<release>`, a wait state, R-09-12). |
| attemptTimeout | A timer boundary on the task. |
| retry | A modelled loop in the sub-process: a retryable error is a BPMN error the worker throws, caught on the task; a gateway counts attempts (`th_attempt`, a data object local to the sub-process); the backoff is a timer whose duration is chosen by attempt. A spent loop gives `unknown` where declared, else the step fails. |
| totalTimeout | A timer boundary on the call's sub-process; it gives `unknown` where declared. |
| handle, switch | Exclusive gateways on `outcome_<step>` and `decision_<name>`. An outcome nobody handles fails the step. |
| parallel | Parallel gateways. |
| forEach | A multi-instance sub-process. Outcomes are data objects local to each item. `onItemFailure: continue` is an error event sub-process inside the item. `concurrency: n` is a sequential multi-instance of chunks, each a parallel multi-instance of up to n items. |
| loop | A sub-process with a counter, an `until` gateway and a timer for `every`. |
| await | An event-based gateway between a message catch and a timer; `calendar: business` names the `business` calendar the binding registers. Early messages are held by the binding (R-09-16). |
| wait | A timer catch event. |
| block | An embedded sub-process (`block_N`, as the export test expects); a `return` is an error end caught on its boundary, then an exclusive gateway on the outcome. The step id is the id of the element that writes the block's trace entry. |
| end | A BPMN error caught on the boundary of `main`, then the endpoint that sets the outcome, then finally, then the end event. |
| slot | A call activity; the binding names the leg from the route decision when the slot starts. |
| hook (halt, transform) | A call activity to the bound hook, skipped when unbound; a timer boundary for the hook point's timeout; the binding checks the hook's writes against the contract; a gateway runs the hold block or ends rejected. |
| hook (notify) | A signal thrown to a non-interrupting event sub-process at process level, so the flow does not wait. |
| try, compensate | A write's compensation is a compensation boundary on a marker task on the primary path, with the undo as a handler sub-process that has its own attempt loop. Compensation is a compensation throw per write step, newest step first. |
| finally | A sub-process every endpoint passes through. |
| deadline | An interrupting timer boundary on `main`, then the deadline steps. |
| signals | Message event sub-processes in `main`: pause and resume (with a gate before every step), cancel (interrupting: compensate, then end cancelled), re-screen (runs the screen steps that have run again). |
| handler | Not in the engine. The binding runs it as a plain request handler, one party call after another, with no process instance (R-09-14). |

The binding (the harness) does what Part 9 gives a binding and nothing more: worker topics, the front door (correlation, early, late and duplicate messages), signals, hook bindings taken at instance start and their contracts, leg dispatch, supersession, and the claim-check of the context. The context lives with the binding; the engine holds only references and flow variables, so CK-CLAIM-01 is checked against the real Flowable variable names.

## C22-S1: the spike, and the pattern for each hard construct

| Construct | Pattern chosen | Evidence |
| --- | --- | --- |
| Retry with backoff | A modelled loop in the call's sub-process, for fixed and growing backoff alike. `failedJobRetryTimeCycle` was not used: an attempt timeout is a timer boundary that ends the task, so it never reaches the failed-job retry, and a spent retry cycle ends in a dead-letter job, not in the `unknown` outcome the DSL asks for. | CK-RETRY-01 and 02, CK-TIMEOUT-01 and 02, CK-UNKNOWN-01 pass. |
| Await with timeout | An event-based gateway between a message catch and a timer. Flowable delivers a message only to a waiting subscription, so the binding holds an early message and delivers it when the catch is waiting. | CK-AWAIT-01 to 05, CK-BLOCK-01 to 03 pass. |
| For each with per-item outcomes | A multi-instance sub-process with item-local outcome variables and an error event sub-process for `continue`. A parallel multi-instance of 10 000 items in one transaction is quadratic inside Flowable (its activity-instance cache), so concurrency is chunked. | CK-FOREACH-01 and 02 pass. |
| Compensation in reverse | Compensation boundary events and handler sub-processes, thrown per write step in reverse order. Flowable 7.1.0 does not implement `waitForCompletion` on a compensation throw (a `TODO` in `IntermediateThrowCompensationEventActivityBehavior`), so the flow then waits on a message the binding sends when no handler is left. Within one instant the engine orders compensation by creation time at millisecond resolution, so the harness gives each engine command its own millisecond and maps engine times back to virtual time. | CK-COMP-01, CK-FINALLY-01, CK-SIGNAL-02 pass. |

What generates cleanly: call, handle, switch, parallel, wait, block, return, end, slot, deadline, finally. What needs a modelled pattern: retry and total timeout, await (with the binding holding early messages), forEach with concurrency, compensation, the four signals, notify hooks. What is refused: nothing in the factory or the kit. The generator throws on a step kind it does not know, and `portability.ts` reports it with the step.

Part 9, section 9.7 should be updated from this table. That file is outside this story's folder, so the text is in the report, not in the spec.

## The limits that are real

Part 9 says this column is the least certain. These are the places where the Flowable target holds only because the binding does the work, or where it differs from the interpreter in ways the kit does not check.

- Flowable 7.1.0 has no wait on compensation, and it loses `main`'s boundary executions when an interrupting event sub-process starts. So cancel ends `main` normally with a mark, and a gateway after `main` reads it.
- Compensation order is reverse document order across write steps, and reverse completion order among the runs of one step. In sequential flows that is reverse completion order. In parallel branches it may not be. The portability report warns about it.
- A total timeout ends at the total time. The interpreter gives up as soon as the next backoff would cross it, which can be earlier. The outcome and the attempts are the same; the end time can differ (CK-TIMEOUT-02, CK-UNKNOWN-01 do not check it).
- `concurrency: n` runs chunks of n with a barrier between chunks, not a sliding window of n. CK-FOREACH-02 takes about three minutes for 10 000 items.
- Outcome variables are process variables, except inside a forEach item. Two concurrent runs of one step id outside a forEach would share one.
- Handlers do not run on Flowable at all. The binding runs them (R-09-14).
- The party service runs a stub's latency at once. The harness takes the latency from the scenario's stubs and completes the job that much later. This is the worker standing in for the party service.
- The party service does not return the rule trace, so the binding cannot store it. CK-TRACE-01 fails for that reason.
- External worker tasks are generated with `flowable:exclusive="false"`. With exclusive jobs, Flowable 7.1.0 acquires nothing when two jobs of one instance wait at once (parallel branches).

## Result

Run on 17 November 2026, Flowable 7.1.0, H2 2.2.224, OpenJDK 21: **45 of 46 conformance scenarios pass on Flowable.** The run takes about five minutes, three of them for CK-FOREACH-02.

| Scenario | Result | Reason |
| --- | --- | --- |
| 45 scenarios, every group of Annex C, section C.3 | PASS | The business trace, the detail and every extra check (attempts, accepted messages, distinct ids, effective policy, virtual elapsed time on the business calendar, unmatched, duplicate and early messages, durable instances, engine state keys, start after resume, unchanged paths) match the studio interpreter. |
| CK-TRACE-01 | FAIL | `ruleTrace:stored` is missing. The party service's attempt reply carries no rule trace, so the binding has nothing to store. Recommendation: add `ruleTrace` to `AttemptResponse` in `src/conformance/party.ts` and have the binding store it. |

Every one of the 54 BPMN documents the kit generates is byte-identical on a second generation (R-09-15). All 12 factory workflows generate and deploy into Flowable (`portability.ts`).
