# The conformance kit and deployments on Temporal

The Temporal binding lives in the runtime: `runtime/src/TransactHub.Engine.Temporal`, with its harness `runtime/tools/TransactHub.TemporalHarness`. This folder holds the kit adapter only. The harness takes the same plan as the DTFx and Elsa harnesses and prints the same observation, so the kit runner and the deploy runner compare it unchanged.

## How to run

.NET 10, Java 17 or later, and Maven are needed. Temporal is its own time-skipping test server, fetched from Maven Central (the native build cannot be downloaded here), so a run that waits days takes seconds.

```
runtime/tools/temporal-test-server/setup.sh     # once: resolve the test server
cd studio
npx tsx engines/temporal/run-kit.ts             # all 55 scenarios, PASS or FAIL each
npx tsx engines/temporal/run-kit.ts CK-AWAIT-03 --verbose
npm run studio -- deploy <export-for-temporal.zip> --workflow p.wf.corporate.v1 ...   # a deployment, a worker process per task queue
```

With `TH_TEMPORAL_ADDRESS=host:7233` the harness runs on that Temporal server instead, in real time.

`CK-FOREACH-02` (10,000 items, in chunks that continue as new) is the one scenario the Java test server cannot run: it refuses a workflow task with "invalid history builder state for action". The adapter runs that scenario on Temporal's real dev server (`temporalio/temporal:1.5.1`, started in Docker by the adapter, or the server at `TH_TEMPORAL_ADDRESS`), in real time. It takes about two minutes and some 20,000 activity runs. Without Docker it fails. The full kit is 55 of 55 in about 520 s on a shared machine (22 November 2026, after I-77).

`CK-TIMEOUT-03` (R-05-54) runs there too: only on a real server does processing take time on the server's clock, so only there does it show that an attempt's timeout measures the attempt's own execution and not the item loop's. Since I-77 the binding keeps an attempt's deadline itself (`IAttemptDeadlines`): on a real server an activity's Start-To-Close is the attempt's own budget, and every other timer runs for its business length from the logical now. `TH_TEMPORAL_TIMERS=server` restores the old timers for a comparison run only.

How large batches stay fast:

- Ordering checks on results that arrive at the same time take constant time (sorted sets).
- A timer that is already due is skipped.
- Claim-check calls are local activities (`TH_LOCAL_CLAIMCHECK=0` turns that off).
- Signals are flushed before continue-as-new and before a run ends.
- An output larger than 256 KB is kept by the host in 512 KB pieces. The run returns a reference, because Temporal refuses payloads over about 2 MB.

## How it maps

| DSL | Temporal |
| --- | --- |
| A workflow, leg, segment or subflow | One generic workflow, `transacthub.interpreter`, that runs the expanded document its input names at its exact release, with the studio's own interpreter (`studio/engines/shared`). |
| An activity attempt | A Temporal activity of type `name@release`, on the task queue of its scope (`th-p`, `th-uae-p`, `th-uae-wps`, `th-bank`), with no Temporal retry: the interpreter applies the policy. |
| Retry, backoff | The interpreter's retry plan, on Temporal timers. |
| A timer, an await's deadline, an attempt timeout | `Workflow.DelayAsync`, raced by the interpreter. |
| A party's latency, an answer that never comes | Played in the workflow on Temporal timers, after the activity returns, so the attempt timeout sees it. |
| A child, a for-each chunk | A child workflow; the chunk's report is a signal to the parent. |
| Continue-as-new | `Workflow.CreateContinueAsNewException`. |
| A message, a signal | A Temporal signal. The front door is a long-running workflow, `transacthub.front-door`: it starts the roots at their times, raises signals at theirs, delivers each later message when the party said and correlates it to its run; a handler runs as an activity, outside the durable engine (R-09-14). |
| Business time | Temporal's time plus a fixed offset in each workflow's input, so a run can start on a business date that is not today and stays deterministic on replay. |
| The payload | Not in Temporal: history holds references (R-09-10); the harness decodes every payload in every history and reports the names it finds. |
