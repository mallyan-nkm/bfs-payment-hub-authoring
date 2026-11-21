# The conformance kit and deployments on Temporal

The Temporal binding lives in the runtime: `runtime/src/TransactHub.Engine.Temporal`, with its harness `runtime/tools/TransactHub.TemporalHarness`. This folder holds the kit adapter only. The harness takes the same plan as the DTFx and Elsa harnesses and prints the same observation, so the kit runner and the deploy runner compare it unchanged.

## How to run

.NET 10, Java 17 or later, and Maven are needed. Temporal is its own time-skipping test server, fetched from Maven Central (the native build cannot be downloaded here), so a run that waits days takes seconds.

```
runtime/tools/temporal-test-server/setup.sh     # once: resolve the test server
cd studio
npx tsx engines/temporal/run-kit.ts             # all 46 scenarios, PASS or FAIL each
npx tsx engines/temporal/run-kit.ts CK-AWAIT-03 --verbose
npm run studio -- deploy <export-for-temporal.zip> --workflow p.wf.corporate.v1 ...   # a deployment, a worker process per task queue
```

With `TH_TEMPORAL_ADDRESS=host:7233` the harness runs on that Temporal server instead, in real time.

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
