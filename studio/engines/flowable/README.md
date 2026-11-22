# The conformance kit on Flowable, and the runtime binding

Build spec C22 (stories S1 to S5), with the kit adapter of C23-S2. A deployment runs on the Flowable REST server instead, with the runtime's binding as its external worker (plan D3): see "The runtime binding" below. The studio generates BPMN 2.0 for every workflow a scenario runs (`src/export/bpmn.ts`), from the expanded form (R-09-17). A Java harness deploys it into an embedded Flowable 7.1.0 engine on H2 in memory, runs it, works the external worker jobs through the party service, and moves the engine clock so that waits of days take no time. The kit runner compares what the harness observed with the expected trace, unchanged.

## How to run

Java 21 or later and Node 22 are needed (the harness compiles for release 21). Maven is fetched into `.bin/`, which is gitignored.

```
cd studio
engines/flowable/setup.sh                       # fetch Maven, build the harness (once)
npx tsx engines/flowable/run-kit.ts             # all 55 scenarios, PASS or FAIL each
npx tsx engines/flowable/run-kit.ts CK-COMP-01 --verbose
npx tsx engines/flowable/portability.ts         # C22-S5: every factory workflow, deployed into Flowable
```

Each run writes `out/<scenario>/`: the generated `*.bpmn20.xml`, the binding's `plan.json` and the harness's `observation.json`; `out/results.json` holds every comparison. Set `TH_DEBUG=1` to see each job the worker takes and each party reply.

| File | What it is |
| --- | --- |
| `adapter.ts` | The `KitAdapter`. It expands and generates every workflow the scenario runs (root, each release, legs, bound hooks), writes the plan and runs the harness. |
| `run-kit.ts` | Runs the kit with the adapter and prints `N of 55 conformance scenarios pass on Flowable`. |
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
| hold (R-05-56) | After the hold's task, a gateway on `held`: an event-based gateway between the decision's message catch (`p.ops.decision`, the binding setting `th_decision` from its status) and the expiry's timer (`th_expiry_<step>`, from the attempt's answer); a gateway on `th_decision` sets the outcome. |
| park (R-05-55) | An error boundary on the call's sub-process catches the step's failure; the binding records `<step>:stuck`; an event-based gateway between the operator's `th.operator.retry` and `th.operator.redrive` (each runs the call again) and the park's timer (the step fails). |
| handler | Not in the engine. The binding runs it as a plain request handler, one party call after another, with no process instance (R-09-14). |

The binding (the harness) does what Part 9 gives a binding and nothing more: worker topics, the front door (correlation, early, late and duplicate messages), signals, hook bindings taken at instance start and their contracts, leg dispatch, supersession, and the claim-check of the context. The context lives with the binding; the engine holds only references and flow variables, so CK-CLAIM-01 is checked against the real Flowable variable names.

## C22-S1: the spike, and the pattern for each hard construct

| Construct | Pattern chosen | Evidence |
| --- | --- | --- |
| Retry with backoff | A modelled loop in the call's sub-process, for fixed and growing backoff alike. `failedJobRetryTimeCycle` was not used: an attempt timeout is a timer boundary that ends the task, so it never reaches the failed-job retry, and a spent retry cycle ends in a dead-letter job, not in the `unknown` outcome the DSL asks for. | CK-RETRY-01 and 02, CK-TIMEOUT-01 and 02, CK-UNKNOWN-01 pass. |
| Await with timeout | An event-based gateway between a message catch and a timer. Flowable delivers a message only to a waiting subscription, so the binding holds an early message and delivers it when the catch is waiting. | CK-AWAIT-01 to 05, CK-BLOCK-01 to 03 pass. |
| Families and the time rule (R-05-43, R-05-44) | `th:await` carries the await's `family`; the binding delivers a message only to a catch whose family it fits (or a message with none). The binding records when each block sub-process began (embedded: as it starts; REST: at a `blockStart` point) and, once an earlier await of the instance has waited for the same message, drops a held message received before the block began, as unmatched. | CK-AWAIT-06 and 07 pass. |
| Durations from parameters (R-05-45) | A `{ parameter, default }` wait or await timeout is written with its default and marked `th:timer parameter default`. The binding places the timer by the tenant's value in force on the business date it was created (the plan's `values`), on the business calendar where the await says so, and the wait's detail entry names the value the timer took. | No kit scenario; checked against the studio interpreter by hand (see Result). |
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

## The runtime binding: the Flowable REST server (D3)

A deployment does not use the Java harness. The Flowable REST server (`flowable/flowable-rest:7.1.0`, pinned by digest in the tests) runs the BPMN of the export package, and `runtime/src/TransactHub.Engine.Flowable` is the binding, a .NET external worker over REST. The payloads are in its claim check: `DurableClaimCheck` on the store when `TH_STORE_PROVIDER` is set, as on Temporal.

| | Embedded (this harness, the kit) | REST (the runtime, a deployment) |
| --- | --- | --- |
| Generated by | `generateBpmn(wf, ctx)` | `generateBpmn(wf, { ...ctx, binding: 'rest' })`, as `export` writes `bpmn/` for `flowable` |
| Where the binding acts | A listener on element start (`th:point` on none events) | External worker tasks on topic `th.binding`: the binding writes the trace and completes them with the variables the flow reads next |
| Places only REST needs | | `processStart` after the start event (an instance learns its hook bindings), `slotStart` before a slot (the leg), `forEachStart` before a batch (the count), `hookStart` before a notify throw, `blockStart` at the start of a composite step (R-05-43); call activities pass `th_parent` and `th_via` and call their own deployment |
| Hooks | Generated by the adapter | Generated into the package with the tenant's bindings |
| Pause gate | Writes held signals at the next step | Not marked; held signals are written at the next point |
| Business calendar | Registered in the engine | Virtual mode: applied by the binding when it fires the timer. Production mode: the binding reschedules the timer to the due date it computes (Issue I-55) |
| Time | The engine clock, moved by the harness | Virtual mode (the default): the server's async executor off; the binding runs jobs and fires timers on its virtual clock. Production mode: the executor on, real time (Issue I-53) |
| A worker that does not answer | | Virtual mode: retried by the binding while the job's retries last, then a dead letter. Production mode: the job is failed with one retry fewer and Flowable hands it out again after the retry wait; with none left it is a dead letter (Issue I-54) |
| A worker that crashed holding a job | | Production mode: Flowable hands the job out again once its lock expires; the binding takes it as the same attempt |
| Dead letters | | Reported in the observation (`engine.deadLetters`) and as a note, with the step; the payment stops there and is marked failed |

Run it with the deploy runner, the server and, optionally, the store:

```
docker run -d --name th-flowable -p 127.0.0.1:8080:8080 -e FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false -e FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE=false flowable/flowable-rest:7.1.0
cd studio
npx tsx src/cli.ts export-zip p.wf.corporate.v1 flowable /tmp/th-corporate.zip --with-simulators --tenant example-bank
TH_FLOWABLE_URL=http://127.0.0.1:8080/flowable-rest npx tsx src/cli.ts deploy /tmp/th-corporate.zip --workflow p.wf.corporate.v1 --message uae.wps.sif --inbound fixtures/samples/wps-sif-valid.sif --tenant example-bank --bench ...
npx vitest run --config vitest.blackbox.config.ts test/blackbox/e2e-wps-engines-store.test.ts -t flowable   # starts its own server and databases
```

`TH_FLOWABLE_USER` and `TH_FLOWABLE_PASSWORD` default to the image's `rest-admin` and `test`; the tests start the server with synthetic test-only credentials instead.

### Production mode

`TH_FLOWABLE_MODE=production` runs the binding against a server whose async executor is on (`FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=true`), in real time. The binding is then a pure external worker: it acquires and locks jobs, completes them, throws BPMN errors and fails them; Flowable runs its own jobs (each completion, each continuation), fires its timers, hands a failed job out again after its retry timeout and a job whose lock has expired. Business time is the wall clock, shifted so that the run starts at the plan's evaluation date and start time (a deployment that runs today passes today). The virtual mode stays the default, for the kit and the tests, which need the engine's own time control (R-C-06).

With a store and a run key (`TH_FLOWABLE_RUN`), the binding keeps its state as facts in the store, before every command it sends to the server (R-09-65, Issue I-89). A binding that stops, killed or crashed, and is started again with the same key goes on from them: it sends its last command again, hands back the jobs it had locked, answers a job it had answered from its journal without calling the worker again, and records no fact twice. The deploy runner and the long-running host start a binding that stopped again with its key. `TH_FLOWABLE_FAULT=exit:<step>` stops it once, after the step's answer is recorded, for the tests.

A `forEach` that declares no `concurrency` runs `forEachConcurrency` items side by side where R-09-45 allows (Flowable's binding 1.1.0 sets 64), in chunks of a parallel multi-instance; an item that stops it is marked (`itemStop`), and no later item's entries reach the trace (R-05-53, CK-FOREACH-04).

A timer on the business calendar, or whose duration or calendar is a bank's parameter (R-05-45, R-05-47), is written with the model's default. The binding sees it when the server creates it, computes the due date as the virtual mode does (the tenant's value in force on the date it was created, counted on the business calendar where it says so) and reschedules it to that date (`POST /management/timer-jobs/{id}`, action `reschedule`). This was chosen over a business calendar bean in the server: the calendar and the dated values stay in the binding, as for every other engine (Issue I-55), and the stock image needs no Java added to it. The window it leaves: a timer the binding has not seen yet runs on the written default, so a binding that is down for the whole of that default lets the timer fire at it.

Two completions of one process instance (parallel branches) run on two of the executor's threads. So that the jobs that follow them, and the trace, come in the order the virtual run gives, the binding lets one completion take effect before it works the next job of the same instance.

| Setting | Default | What it is |
| --- | --- | --- |
| `TH_FLOWABLE_MODE` | `virtual` | `virtual` or `production` |
| `TH_FLOWABLE_LOCK` | `PT10M` | How long a job is locked to the binding; a crashed worker's job comes back after it |
| `TH_FLOWABLE_RETRY_WAIT` | `PT30S` | How long Flowable waits before it hands a failed job out again |
| `TH_FLOWABLE_POLL` | `PT0.25S` | How often the binding asks for work when nothing moved |
| `TH_FLOWABLE_MAX_WAIT` | `PT1H` | How long one run may wait for its payments; then it stops with a note |
| `TH_FLOWABLE_SIM_SCALE` | `1` | A simulator's reply delay is multiplied by it, as the stub's `--time-scale` |
| `TH_FLOWABLE_FAULT` | | Tests only: `crash:<step>` (the binding takes the step's first job and stops holding the lock, as a crashed worker; it goes on under a new worker id) or `down:<step>` (the step's worker never answers) |

The server's executor polls for due timers and expired locks on its own intervals (10 s and 60 s by default). The tests shorten them with `FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_TIMER_JOB_ACQUIRE_WAIT_TIME`, `..._DEFAULT_ASYNC_JOB_ACQUIRE_WAIT_TIME` and `..._RESET_EXPIRED_JOBS_INTERVAL` set to `PT1S`. The deploy runner's `--value parameter=value` sets a value from the run's date on top of the configuration set, so a test can give the acknowledgement wait seconds; the deploy runner does not check it against the parameter's bounds.

```
docker run -d --name th-flowable-live -p 127.0.0.1:8081:8080 -e FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=true flowable/flowable-rest:7.1.0
TH_FLOWABLE_MODE=production TH_FLOWABLE_URL=http://127.0.0.1:8081/flowable-rest npx tsx src/cli.ts deploy /tmp/th-corporate.zip --workflow p.wf.corporate.v1 ... --value 'uae.wps.exchange.submit-salary-file.v1#ackWait=PT2M'
npx vitest run --config vitest.blackbox.config.ts test/blackbox/e2e-wps-flowable-production.test.ts   # starts its own servers and databases
```

`FlowableProductionTests` (runtime) shows each mechanism on a small process: the server's own jobs and backoff timer, a worker down twice then answering (Flowable's retries), a dead letter, a crashed worker's job back after its lock expired, a parameter wait and a business-calendar wait placed by the binding. `e2e-wps-flowable-production.test.ts` runs the WPS salary file in production mode on the store, on PostgreSQL and on SQL Server, with the bank's acknowledgement wait of two minutes; a reply that never comes times out on a real timer of fifteen seconds and gives the virtual run's trace; a crash at `post` with a lock of five seconds still settles; and a `post` worker that never answers ends in a dead letter the binding reports. CI job `flowable-production` runs it.

## Result

Run on 22 November 2026, Flowable 7.1.0, H2 2.2.224, OpenJDK 21: **55 of 55 conformance scenarios pass on Flowable**, with CK-AWAIT-06 (the time rule), CK-AWAIT-07 (families) and CK-MONEY-01 (exact amounts; the harness reads JSON with exact `BigDecimal`s, R-09-47). The run takes about seven minutes, three of them for CK-FOREACH-02. The duration parameters were checked by hand on both bindings (embedded and REST): CK-AWAIT-07 with a hold of `{ parameter: hold }` in the second subflow (default `PT10S`, tenant value `PT20S`) and CK-BLOCK-02 with a timeout of `{ parameter: replyWait }` on the business calendar (default `P1D`, tenant value `P3D`) give the studio interpreter's trace, detail and elapsed time. On the REST server, CK-AWAIT-01, 04, 06, 07, CK-BLOCK-01 to 03 and CK-WAIT-01 pass as well.

Earlier, on 22 November 2026: 46 of 46, before CK-AWAIT-06 and 07 were added.

The table is the run of 17 November 2026, which gave 45 of 46; CK-TRACE-01 passes in the run of 22 November.

| Scenario | Result | Reason |
| --- | --- | --- |
| 45 scenarios, every group of Annex C, section C.3 | PASS | The business trace, the detail and every extra check (attempts, accepted messages, distinct ids, effective policy, virtual elapsed time on the business calendar, unmatched, duplicate and early messages, durable instances, engine state keys, start after resume, unchanged paths) match the studio interpreter. |
| CK-TRACE-01 | FAIL | `ruleTrace:stored` is missing. The party service's attempt reply carries no rule trace, so the binding has nothing to store. Recommendation: add `ruleTrace` to `AttemptResponse` in `src/conformance/party.ts` and have the binding store it. |

Every one of the 54 BPMN documents the kit generates is byte-identical on a second generation (R-09-15). All 12 factory workflows generate and deploy into Flowable (`portability.ts`).
