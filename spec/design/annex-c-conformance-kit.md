# Annex C. Conformance kit

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## C.1 Purpose

The kit proves that the DSL means the same thing everywhere. It is a set of small workflows with expected business traces. The studio interpreter and every target engine must produce the same trace for every scenario. Semantics are proven, not asserted.

## C.2 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-C-01 | Every construct of the portable core MUST have at least one scenario. | Coverage table C.4 |
| R-C-02 | A scenario MUST be self-contained: its own workflow, its own activities, its own simulators, in the reserved cluster `ck`. | Kit validates under `tools/validate.py` |
| R-C-03 | A business trace is the ordered list of step id and outcome, plus markers for joins, awaits, hooks, signals, compensation and end. | Runner (C23) |
| R-C-04 | Two traces are equal when they match exactly, except where a scenario marks an order as free. | Runner (C23) |
| R-C-05 | The kit MUST run on the studio interpreter, Temporal, DTFx, Flowable and Elsa. A red scenario on any target blocks the release. | Release gate (C19) |
| R-C-06 | A scenario that needs time MUST use the virtual clock in the studio and the target's own time control on an engine. | Runner (C23) |
| R-C-07 | The kit carries its own `major.minor.patch`. A new construct adds scenarios and is a minor. | Kit manifest |
| R-C-08 | Extension constructs are outside the kit. | Lint L24 |

## C.3 Scenarios

The machine-readable list is `conformance/kit.yaml`. It holds 46 scenarios in draft 0.2.

| Group | Scenarios | Proves |
| --- | --- | --- |
| Flow | CK-SEQ-01, CK-HANDLE-01 to 03, CK-SWITCH-01 and 02 | Order, outcome handling, branching |
| Concurrency | CK-PAR-01, CK-FOREACH-01 and 02 | Parallel join, per-item outcomes, chunking |
| Business retry | CK-LOOP-01 and 02, CK-UNKNOWN-01 | Bounded loops; inquiry after an unknown send |
| Technical retry and timeouts | CK-RETRY-01 and 02, CK-TIMEOUT-01 and 02, CK-POLICY-01 | Retry with idempotency; attempt and total timeouts; policy resolution |
| Waiting | CK-AWAIT-01 to 05, CK-WAIT-01, CK-DEADLINE-01 | Messages, timers, business calendar, deadlines; early, late and duplicate messages |
| Undo and close | CK-COMP-01, CK-FINALLY-01 | Compensation in reverse; finally on every end |
| Composition | CK-SLOT-01 and 02, CK-HANDLER-01 | Slots and legs; handlers without durable state |
| Composite steps | CK-BLOCK-01 to 03 | Blocks and returns; the reply time of the calling step; the business trace and the detail |
| Signals | CK-SIGNAL-01 to 03 | Pause, resume, cancel, re-screen |
| Hooks | CK-HOOK-01 to 04 | Halt, transform, notify, unbound |
| Versions | CK-PIN-01 and 02, CK-LOOKUP-01, CK-SUP-01 | Exact pins, lookup by date, supersession |
| Record | CK-CLAIM-01, CK-DELTA-01, CK-TRACE-01 | Claim-check, deltas, rule trace |

## C.4 Coverage of the portable core

| Construct | Scenarios |
| --- | --- |
| Call, handle, end | CK-SEQ-01, CK-HANDLE-01 to 03 |
| Switch | CK-SWITCH-01, CK-SWITCH-02 |
| Parallel | CK-PAR-01 |
| For each | CK-FOREACH-01, CK-FOREACH-02 |
| Loop | CK-LOOP-01, CK-LOOP-02 |
| Await, wait | CK-AWAIT-01 to 05, CK-WAIT-01 |
| Slot | CK-SLOT-01, CK-SLOT-02 |
| Block, return | CK-BLOCK-01 to 03 |
| Use | Covered inside CK-SEQ-01, which inlines a segment |
| Try, compensate | CK-COMP-01 |
| Hook | CK-HOOK-01 to 04 |
| Finally, deadline | CK-FINALLY-01, CK-DEADLINE-01 |
| Signals | CK-SIGNAL-01 to 03 |
| Policy | CK-RETRY-01 and 02, CK-TIMEOUT-01 and 02, CK-POLICY-01 |
| Handler | CK-HANDLER-01 |

## C.5 What the kit does not prove

Throughput, latency, back-pressure, failover and recovery. Those belong to pre-prod performance and resilience runs.
