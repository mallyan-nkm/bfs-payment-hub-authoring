# DTFx interpreter and kit adapter (C21)

This folder runs the conformance kit on the Durable Task Framework (DTFx, `Microsoft.Azure.DurableTask.Core` 3.11.3, .NET 8). One generic orchestration interprets the expanded DSL document. Every activity attempt goes through the studio's party service. The kit runner compares the business trace exactly as it does for the studio.

Result: **45 of 46 conformance scenarios pass on DTFx.** CK-TRACE-01 fails because the party service does not return the rule trace (see Limits). Two full runs gave identical output.

## How to run

From `studio/`:

```
apt-get install -y dotnet-sdk-8.0      # once; NuGet restores DurableTask.Core on the first build
npx tsx engines/dtfx/run-kit.ts        # all 46 scenarios, about 3 minutes
npx tsx engines/dtfx/run-kit.ts CK-AWAIT-04 CK-SIGNAL-02 --verbose
```

`run-kit.ts` builds the harness (`dotnet build -c Release`) and calls the kit runner's `runKit` with the DTFx adapter. It prints PASS or FAIL per scenario. For a failure it prints the first differing trace entry, and with `--verbose` the engine's own figures: instances, executions, replayed episodes, activity runs, history events and late results abandoned.

## Files

| File | What it is |
| --- | --- |
| `adapter.ts` | The `KitAdapter`. Expands every workflow and hook (`expandWorkflow`), reads the DTFx binding, runs the harness per scenario, parses its `KitObservation` |
| `run-kit.ts` | The runner. It prints `N of 46 conformance scenarios pass on DTFx` |
| `../shared/Interpreter.cs` | The interpreter, ported from `src/interp/interpreter.ts` onto engine operations. Shared with the Elsa harness (`../elsa`) |
| `harness/Engines.cs` | `InterpreterOrchestration` (the one `TaskOrchestration`), `PartyActivity`, the DTFx engine and the plain engine used for handlers |
| `harness/VirtualTime.cs` | The in-process orchestration service with virtual time (the clock itself is `../shared/Clock.cs`) |
| `harness/Harness.cs` | Worker set-up, the front door, signals, activity attempts and the observation |
| `../shared/Deployment.cs` | Deployed documents, the claim-check store, the party client and the worker router. Shared with the Elsa harness |

## Mapping implemented (Part 9, DTFx column)

| Construct or policy | On DTFx here |
| --- | --- |
| Call | `ScheduleTask` of a `TaskActivity` registered as `<name>@<release>` (R-09-04). It calls the party service with the orchestration's time as `now` |
| Slot, leg | `CreateSubOrchestrationInstance` of the same generic orchestration. The slot contract's writes are copied back |
| Hook | Sub-orchestration from the binding taken at instance start. A notify hook runs alongside and does not delay the flow |
| Handler | Plain request handler with no orchestration (`durableInstances` 0) |
| Switch, loop, block, return, try, compensate, finally | Interpreter frames. Completed write steps are kept in orchestration state and compensated in reverse |
| Parallel | `Task.WhenAll` over the branches |
| For each | Inline up to the binding's `forEachChunk` (500). Above it, a batch sub-orchestration runs one chunk per execution and continues as new. CK-FOREACH-02 runs 20 executions of 500 items |
| Await | External event raced with a durable timer (business calendar too). An event that arrives early is kept in orchestration state (`OnEvent`). A copy is recorded and ignored. One after the timeout is unmatched |
| Wait, loop interval, deadline | Durable timers. The deadline is a timer race that cancels the body scope |
| Signals | External events `signal`. Pause and resume take effect at the next step boundary; cancel compensates, then runs finally; re-screen runs the screen step again |
| `retry`, `totalTimeout` | `RetryOptions` run by DTFx's `RetryInterceptor` (what `ScheduleWithRetry` uses), with `RetryTimeout` = totalTimeout. `Handle` stops a retry that is non-retryable or whose wait would reach the total timeout |
| `attemptTimeout` | Durable timer raced against the task, cancelled when the task wins. When the timer wins, the task is not cancelled and its late result is abandoned (R-09-11). The history shows the late results: one in CK-TIMEOUT-01, two in CK-TIMEOUT-02 |
| Engine state | Header, facts, status and message references only. The payload sits in a claim-check store outside the engine. An attempt stages its writes and the orchestration commits them only for an accepted result, so a late result never reaches the payment (R-09-10). The harness also scans every stored history for `payment`, `items` and `ext` keys |
| Pins, supersession, release trace | Pins from `artefacts` at start; legs pinned when they start; supersession for steps not yet started |

## Time

The emulator (`DurableTask.Emulator` 2.6.0) fires timers against `DateTime.UtcNow`, polling once a second. Its queues poll every 500 ms. A kit wait of two days would take two days, and 10,000 items would take hours. So the harness uses its own `IOrchestrationService`, modelled on the emulator, with a virtual clock:

- Time moves only when nothing is in flight: no orchestration message is waiting and no activity is running. Then the clock jumps to the next due item and fires it. The due item is a durable timer, an activity's latency, a party delivery or a signal. This is the studio's model and that of Temporal's time-skipping test server. No durations are scaled: the virtual times are exact, so `virtualElapsed` checks compare milliseconds.
- An orchestration middleware sets each episode's `OrchestratorStartedEvent` timestamp to the virtual time. So `CurrentUtcDateTime`, `CreateTimer` and `RetryInterceptor` all run on virtual time. The timestamps are stored in history, so a replay sees the same times.
- One work item runs at a time, in message order, so a run is deterministic. History is stored serialised, as in the emulator. Every episode replays it from the start (no extended sessions), so determinism is checked on every step.

## Limits and what is not built

- **The party service has two gaps.** (1) It drops the rule trace, so CK-TRACE-01 fails. With `ruleTrace: r.ruleTrace` added to the result in `src/conformance/party.ts`, CK-TRACE-01 passes. This was checked with a patched copy outside the repository, via `DTFX_PARTY_SERVER`. (2) It answers at once and does not report a stub's `latency`. The harness reads the stub's latency from the scenario configuration and makes the activity take that long on the virtual clock. CK-PAR-01, CK-AWAIT-04 and CK-SIGNAL-01 depend on this.
- **An attempt that never answers** (`hang`) ends at its own deadline: the attempt timeout plus one virtual second. It then returns a late `Timeout` result, which the orchestration has already abandoned. Without that, DTFx would never complete the orchestration, because it waits for open tasks. An activity with no attempt timeout that hangs keeps its orchestration open.
- **The orchestration service is the harness's own**, not the emulator, for the reasons under Time. The framework parts are DTFx's own: the dispatchers, replay, timers, sub-orchestrations, continue-as-new, `RetryInterceptor` and the data converter.
- **Cancellation of children.** DTFx Core cannot cancel a sub-orchestration or a running task. A deadline, a cancel signal or a hook timeout abandons the child, and its result is ignored. A cancel signal during a retry backoff takes effect at the next attempt, because `RetryInterceptor`'s timer cannot be cancelled.
- **One task hub.** The binding's per-scope queues are not mapped to separate hubs or workers.
- Intake (a `start` disposition with an intake map and workflow selection) is not built. The kit does not use it. Documents resolve from the scenario's documents only, not from the studio store.
- Retryability of an error comes from the party's `retryable` flag together with the effective policy. An activity failure that the party marks retryable but the policy does not retry is not retried. The studio would retry it.
- The binding is read, not regenerated. C21-S4 (regenerate `examples/bindings` with no difference) needs the studio exporter and is not part of this folder.
