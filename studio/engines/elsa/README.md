# Elsa Workflows harness and kit adapter (I-30)

This folder runs the conformance kit, and deployed packages, on **Elsa Workflows 3.10.2** (.NET 8). It uses the same interpreter as the DTFx harness, in `../shared/`, so a workflow means the same thing on both engines.

Result: **50 of 50 conformance scenarios pass on Elsa** (22 November 2026, with CK-AWAIT-06 and 07).

## How to run

From `studio/`:

```
npx tsx engines/elsa/run-kit.ts                         # all 50 scenarios, about 2 minutes
npx tsx engines/elsa/run-kit.ts CK-AWAIT-04 --verbose   # one scenario, with the engine's figures
```

`run-kit.ts` builds the harness (`dotnet build -c Release`; NuGet restores Elsa on the first build) and calls the kit runner with the Elsa adapter. To deploy a package on Elsa, export it with the target `elsa` and use `npm run studio -- deploy` as for DTFx: the deploy runner reads the target from the package's binding.

## How it works

Elsa keeps a workflow instance's state, suspends it at a bookmark and resumes it with input. It does not replay code, as DTFx and Temporal do. So:

| Part | What it does |
| --- | --- |
| `harness/Workflow.cs` | `InterpreterActivity`, the one generic Elsa activity. It keeps a journal in the workflow's own properties: the instance input and every result that has reached it, in order. While the instance waits, it holds a bookmark. `ElsaHost` starts and resumes instances with Elsa's `IWorkflowRunner`, from state written by Elsa's `IWorkflowStateSerializer` |
| `harness/Replay.cs` | The engine operations on the journal. Each resume runs the interpreter again from the start: an operation whose result is in the journal returns it at once; one that is not becomes pending. A journal entry the replay does not reach fails the run (R-09-18) |
| `harness/Harness.cs` | The host. It carries out pending operations (an activity attempt through the party service or the worker of its queue, a timer, a child instance, an event to another instance) on the virtual clock, plays the front door, raises signals, and prints the observation |
| `../shared/` | The interpreter, the deployment, the claim-check store and the virtual clock, shared with the DTFx harness |

## Mapping implemented (Part 9, Elsa column)

| Construct or policy | On Elsa here |
| --- | --- |
| Call | A pending operation, run on the task's worker queue (`<name>@<release>`, R-09-04); the bookmark is resumed with the result |
| Slot, leg, hook | A child workflow instance of the same activity |
| Handler | A plain request handler, with no workflow instance (R-09-14) |
| Await | The bookmark resumed by the message, raced with a journalled timer. An early message is kept in the journal |
| Wait, loop, deadline | Journalled timers |
| Signals | Events delivered at the bookmark |
| For each | Inline up to the binding's chunk (500); above it, a batch child instance that continues as new per chunk (CK-FOREACH-02: 10,000 items in 21 executions) |
| `retry`, `totalTimeout` | The retry plan, with each backoff a journalled timer |
| `attemptTimeout` | A journalled timer raced against the operation; a late result is abandoned (R-09-11; CK-TIMEOUT-01) |
| Engine state | References only: the payload is in the claim-check store. The harness scans every stored Elsa workflow state for `payment`, `items` and `ext` (CK-CLAIM-01) |

## Limits

- **The replay is the interpreter's own**, not Elsa's. Each resume reruns the journal, so cost grows with the journal; the binding's chunk size keeps it small.
- **Timers and retries run on the host's virtual clock**, not Elsa's scheduler, so that a two-day wait takes no time in the kit (R-C-06). A production host would hand timers to Elsa's scheduler.
- **State is kept in memory** by the host between runs, as Elsa serialises it. A production host would use an Elsa persistence provider.
- **Elsa's designer is not used**: the studio's documents stay the source, as for every target.
- On Windows, an application control policy may block the harness, as it can the DTFx one; the studio says so when it happens.
