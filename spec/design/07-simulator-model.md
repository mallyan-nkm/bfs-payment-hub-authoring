# Part 7. Simulator model

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 7.1 Purpose

A simulator is a scripted stand-in for one external party on one port. It is built with AI from the scheme's message set and bound to an activity test or a workflow test. Simulators are functional: they do not model throughput, back-pressure or soak behaviour.

## 7.2 Kinds

| Kind | Stands in for | Triggered by |
| --- | --- | --- |
| Outward | The party the platform sends to | A message arriving at its port |
| Inward | The party that sends to the platform | A test step, or a schedule on the virtual clock |

## 7.3 The standard scenario set

Every outward simulator carries these eight scenarios, so timeouts and retries can be tested on any port.

| Scenario | Behaviour | What it proves |
| --- | --- | --- |
| `accept` | Replies accepted | The happy path |
| `reject` | Replies rejected with a reason code | Business rejection is not retried |
| `pending-then-final` | Replies pending, then final after a delay | Waiting for a final status |
| `no-reply` | Never replies | Attempt timeout, total timeout, the unknown-outcome path |
| `late-reply` | Replies after the timeout | A late reply resumes nothing, is recorded as unmatched, and is never double-counted |
| `duplicate-reply` | Replies twice | Idempotent handling of replies |
| `fail-twice-then-accept` | Fails transiently twice, then accepts | Retry with one logical message accepted |
| `malformed-reply` | Replies with an invalid message | Map and validation failure handling |

A simulator decides by its scenario, not by the data of the payment (Issue I-96). Personal data never enters workflow state, so no screening simulator sees a name: a sample whose payee stands for a sanctions hit is screened clear unless its bench gives the sanctions port the scenario that answers a hit (`reject`; the test-data builder's `sanctioned-name` injection names its `sanctions-hit` bench). A test that means a hit names that scenario in its bench, as R-07-09 asks of every test.

## 7.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-07-01 | A simulator MUST implement exactly one port. An activity cannot tell it from the real connection. | Schema `simulator` (L00); sandbox test |
| R-07-02 | An outward simulator MUST carry the standard scenario set. | Publish check (C10) |
| R-07-03 | A scenario is a list of steps: expect a message, send a message, wait, do not reply, fail a number of times, duplicate. | Schema (L00) |
| R-07-04 | Delays MUST run on the virtual clock. A wait of days tests in seconds. | Conformance CK-AWAIT-02 |
| R-07-05 | A simulator keeps conversation state keyed by message id, so a later status inquiry gets a consistent answer. | Simulator runtime test (C10) |
| R-07-06 | Every message a simulator emits MUST validate against its self-check rule sets, unless the scenario is marked malformed. | Simulator runtime test (C10) |
| R-07-07 | A scheme simulator SHOULD load sample messages or certification cases as golden files, to break the symmetry of AI writing both sides. | Publish report (advisory) |
| R-07-08 | Scenario coverage MUST be reported against the scheme's reason-code list. Each code has a scenario or a recorded waiver. | Coverage report (C10) |
| R-07-09 | A test names its bench: for each port, the simulator and the scenario. The simulator MUST implement that port and have that scenario. | Lint L15 |
| R-07-10 | Every port MUST have at least one simulator, and every port a workflow under test can reach MUST have one bound. | Lint L22 |
| R-07-11 | The same simulator definition MUST run embedded in the studio and as a deployable stub in pre-prod. | Export test (C10) |
| R-07-12 | A pack that contains a simulator MUST NOT be promotable to production. | Schema `pack` (L00) |
| R-07-13 | A bank MAY author simulators for its own ports in `b`. Shared simulators are read-only to it. | Tenancy tests (C17) |
| R-07-14 | A file that starts a payment from the bank's own channel, not from a scheme, has no inward simulator. An end-to-end test names it with `given.inbound` and `given.message`; the front door receives it as that message. The message MUST be a start message in a dispatch table. | Schema `test-case` (L00); runner test (C11) |

## 7.5 Anatomy

| Part | Content |
| --- | --- |
| Port | The interop profile it implements |
| Scenarios | Named scripts |
| State | Conversation memory |
| Clock | The studio's virtual clock |
| Script-behind | Written by AI from the message catalogue, scheme rules and samples |
| Self-check | The rule sets its messages must pass |

## 7.6 Examples

- Outward scheme simulator with later replies and refund scenarios: `factory/uae.wps/simulators/uae.wps.sim.wps-outward.v1.yaml`.
- Inward file simulator: `factory/uae.wps/simulators/uae.wps.sim.wps-inward.v1.yaml`.
- An operator as an inward simulator: `factory/p/simulators/p.sim.operations.v1.yaml`.
- A test with a bench: `factory/uae.wps/tests/WPS-OUT-003.yaml`.
