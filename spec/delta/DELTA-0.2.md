# Delta 0.2: composite steps, and what an engine runs

Status: 17 November 2026. From draft 0.1 to draft 0.2. Normative words follow RFC 2119.

This file is for a build that has already started from draft 0.1. It says what changed, why, which files to replace, and what to do about work already done. Nothing outside this delta changed.

## 1. Why

- **Issue.** In draft 0.1 a designer who placed "submit the salary file" also had to place the wait for the acknowledgement and the step that reads it. That is plumbing. It differs by scheme and by how a scheme answers, and a designer who forgets it, or forgets the inquiry after an unknown send, builds a wrong payment.
- **Recommendation.** The designer places one block and handles its business outcomes. The block is a composite activity; what it does underneath is a subflow owned by the scheme's owner. The workflow is expanded before it runs, so the interpreter and the engines see every send, wait and read. Adopted in this delta.

## 2. The change in one page

| | Draft 0.1 | Draft 0.2 |
| --- | --- | --- |
| What the designer writes | `submit`, `await-ack`, `read-ack` as three steps | `submit` as one step that calls a composite activity |
| Where the wait is defined | In every leg that needs it | Once, in the subflow of the composite |
| What an engine runs | The workflow as written | The expanded form: each composite step is a `block` holding its subflow's steps |
| How long the wait is | `timeout` on the `await` | `policy.replyTimeout` on the designer's step, else on the activity |
| Traces | One | Two: the business trace (one entry per authored step) and the detail (the steps inside each block) |

The designer writes:

```yaml
- id: submit
  call: uae.wps.exchange.submit-salary-file.v1
  handle:
    rejected: { end: rejected }
    pending: { end: pending }
```

An engine runs the expansion, in `expanded/uae.wps.wf.salary-file-out.v1.yaml`: a `block` with `submit.send`, `submit.await-ack` and `submit.read-ack` inside, each `return` giving the block its outcome. Part 5, section 5.6 has the full example and the four rules of expansion. `tools/expand.py` is the reference implementation.

Three terms are new.

| Term | Meaning |
| --- | --- |
| Composite activity | An activity whose implementation binding is `subflow`. One block to the designer |
| Subflow | A workflow of kind `subflow`, name type `sub`, that implements one composite. It ends with `return`, never `end` |
| Expanded form | The workflow after expansion. Derived, reproducible, never edited, never stored as authored |

## 3. Which draft you have, and how to apply the delta

```
grep -c "CK-AWAIT" conformance/kit.yaml
```

| Result | You have | Apply |
| --- | --- | --- |
| 3 | Draft 0.1 as first delivered (41 scenarios) | Sections 4 to 8, including every row marked "0.1a" |
| 5 | Draft 0.1 with the reply rules (43 scenarios) | Sections 4 to 8; rows marked "0.1a" are already in your copy |

The overlay `designer-studio-spec-delta-0.2.zip` holds every file that was added or changed, at its path. It is the same for both cases, because each file is replaced whole.

1. Check that you have not edited any file in section 7. If you have, merge by hand; do not overwrite your change.
2. Copy the overlay over your copy of the spec. No file is removed.
3. Run the tools in section 9. All five MUST pass before any code changes.
4. Work the two delta stories in section 8, then carry on with the backlog.

## 4. Requirements

### New

| Id | Requirement | Check | Rows marked 0.1a |
| --- | --- | --- | --- |
| R-03-19 | An activity whose work includes waiting for a message MUST be a composite. | L30, L31 | |
| R-03-20 | The outcomes of a composite MUST be exactly the outcomes its subflow returns. | L30 | |
| R-03-21 | A composite MUST declare `replyTimeout` when an await in its subflow has none. Its policy holds only `replyTimeout` and `replyCalendar`. | L30, L32 | |
| R-03-22 | A composite that sends MUST deal with the unknown outcome of its send inside its subflow, by inquiry with the original ids. | Review; CK-UNKNOWN-01 | |
| R-03-23 | A composite MUST be tested alone, one test per outcome, with the inner steps in `detail`. | Publish check | |
| R-05-30 | A message delivered before the matching `await` starts MUST be kept and consumed by it. | CK-AWAIT-04 | 0.1a |
| R-05-31 | A `deliver` message that finds no instance waiting MUST be recorded and handed to operations as unmatched. | CK-AWAIT-03 | 0.1a |
| R-05-32 | A second copy of a consumed message MUST be recorded and ignored. | CK-AWAIT-05 | 0.1a |
| R-05-33 | A platform workflow, leg, segment or handler MUST NOT contain an `await`. | L31 | |
| R-05-34 | A subflow MUST implement exactly one composite, return only its outcomes, and hold no `end`, `slot`, `hook` or `use`. | L30 | |
| R-05-35 | Expansion MUST be a pure function: the same input gives the same bytes. The expanded form MUST NOT be edited or stored as authored. | `tools/expand.py --check`; L31 | |
| R-05-36 | The studio interpreter and every engine binding MUST run the expanded form and record the authored release and hash it came from. | CK-BLOCK-01 | |
| R-05-37 | An `await` with no timeout MUST take the reply time of the calling step: the step's policy, else the activity's. | L32; CK-BLOCK-03 | |
| R-05-38 | A run MUST yield the business trace and the detail. Tests and conformance compare both. | CK-BLOCK-01, CK-BLOCK-02 | |
| R-05-39 | A check that needs the whole flow MUST run on the expanded form and report at the authored step, naming the inner step. | Studio test (C04) | |
| R-05-40 | A step that calls a composite MUST have an id. | L30 | |
| R-05-41 | The pins of a workflow MUST include each composite it calls, its subflow, and everything the subflow reaches. | L23 | |
| R-09-16 | A binding MUST keep a message that arrives before its `await`, and hand an unmatched message to operations. | CK-AWAIT-03, CK-AWAIT-04 | 0.1a |
| R-09-17 | An engine binding MUST receive the expanded form only. It MUST NOT resolve a composite at run time. | CK-BLOCK-01 | |

### Changed

| Id | Change |
| --- | --- |
| R-01-02 | The name types gain `sub` |
| R-03-09 | The exchange modes gain `receive`: a block that waits for a message it did not ask for |
| R-08-07 | The bindings gain `subflow`. A subflow binding has no script |

### Checks

| Check | What it proves | In `tools/validate.py` | Mutation cases |
| --- | --- | --- | --- |
| L30 | A composite and its subflow agree; a subflow holds no `end`, `slot`, `hook` or `use`; a step that calls a composite has an id | Yes | M22 |
| L31 | `await` and `return` only in a subflow; `block` only in the expanded form | Yes | M23 |
| L32 | An `await` with no timeout has a reply time to take | Yes | M24 |

No check was withdrawn and no code changed its meaning. L05, L07, L10 and L16 now also read subflows: a handler MUST NOT call a composite that waits, and a `deliver` message is awaited by a subflow.

### Conformance kit: 41 to 46 scenarios

| Scenario | Proves | Row |
| --- | --- | --- |
| CK-AWAIT-03 (changed) | A late message resumes nothing and is recorded as unmatched | 0.1a |
| CK-AWAIT-04 | A message that arrives before its await is kept | 0.1a |
| CK-AWAIT-05 | A second copy is recorded and ignored | 0.1a |
| CK-BLOCK-01 | A block returns its primary outcome and the workflow carries on | |
| CK-BLOCK-02 | An outcome returned from inside a block is handled by the step that holds it | |
| CK-BLOCK-03 | An await inside a block waits for the reply time resolved from the calling step | |

## 5. Schemas

| Schema | Change | Breaks a draft 0.1 document |
| --- | --- | --- |
| `common` | `artefactName`: type `sub`. `policy`: `replyTimeout`, `replyCalendar`. `implementation`: binding `subflow` with `subflow` | No |
| `activity` | `mode`: `receive` | No |
| `workflow` | `wfKind`: `subflow`. `implements`: a leg's slot or a subflow's activity. New steps `return` and `block`. A `return` action in `handle` and `onTimeout`. `form` and `expandedFrom`. Step ids and `outcomeOf` MAY be dotted. `await.timeout` is optional | No. The checks do: see section 6 |
| `test-case` | `expect.detail`: the steps inside composite steps | No |

Every draft 0.1 document still validates against the draft 0.2 schemas. What changes is that an authored leg with an `await` now fails L31.

## 6. Factory pack

### Added

| Artefact | What it is |
| --- | --- |
| `uae.wps.exchange.submit-salary-file.v1` with `uae.wps.sub.submit-salary-file.v1` | Send the salary file, await the acknowledgement, read it |
| `uae.wps.exchange.confirm-receipt.v1` with `uae.wps.sub.confirm-receipt.v1` | Send the receipt confirmation, await the acknowledgement, read it |
| `uae.wps.exchange.request-refund.v1` with `uae.wps.sub.request-refund.v1` | Send the refund request, read the acknowledgement, await and read the settlement |
| `uae.wps.exchange.receive-supplement.v1` with `uae.wps.sub.receive-supplement.v1` | Await the supplementary file. Mode `receive` |
| `uae.aani.exchange.submit-credit.v1` with `uae.aani.sub.submit-credit.v1` | Send; on a pending reply await the status report; on an unknown outcome inquire up to three times |
| `WPS-EXC-001`, `WPS-EXC-002`, `AANI-EXC-001`, `AANI-EXC-002` | Each composite tested alone |
| `expanded/` | The expanded form of the four legs that call a composite. Generated |

### Changed

| Artefact | Change |
| --- | --- |
| `uae.wps.wf.salary-file-out.v1` | `submit`, `await-ack`, `read-ack` become one step `submit`. The unused parameter `ackWait` is gone; the wait is `replyTimeout` |
| `uae.wps.wf.agent-payment-in.v1` | `await-supplement` becomes `supplement`, with a step override of `replyTimeout`. `confirm-receipt`, `await-receipt-ack`, `read-receipt-ack` become one step `confirm-receipt` |
| `uae.wps.wf.refund-out.v1` | `submit` to `read-settlement` become one step `request` |
| `uae.aani.wf.credit-out.v1` | The `send` step calls the composite. The pending branch and the inquiry loop moved into the subflow |
| `WPS-OUT-001`, `WPS-OUT-003`, `AANI-OUT-003`, `AANI-OUT-004`, `AANI-OUT-006`, `AANI-OUT-007` | The trace is now the business trace; the inner steps are in `detail`. `AANI-OUT-003` counts attempts on `send.send` |

### Not changed

The primitive exchanges (`send-salary-file`, `send-credit`, `inquire-status` and the rest), every map, rule, port and simulator, the platform workflows, the inward Aani legs, the handlers, the scheme manifests and the example bank.

## 7. Files

Added: `delta/DELTA-0.2.md`, `tools/expand.py`, `expanded/` (4 files), 5 composite activities, 5 subflows, 4 tests (section 6).

Changed by hand:

| Area | Files |
| --- | --- |
| Design | `design/01`, `03`, `05`, `07`, `08`, `09`, `10`, `annex-a`, `annex-b`, `annex-c` |
| Schemas | `common`, `activity`, `workflow`, `test-case` |
| Guard-rails and maps | `CLAUDE.md`, `README.md`, `factory/README.md` |
| Factory | The four legs in section 6 |
| Kit | `conformance/kit.yaml` |
| Backlog | `build/backlog/1-core.yaml`, `2-execution.yaml`, `3-authoring-ui.yaml`, `5-export.yaml`, `6-factory-content.yaml` |
| Tools | `build_factory.py`, `validate.py`, `mutation_tests.py`, `build_docs.py` |

Regenerated: `build/00-backlog.md`, every `build/Cnn-*.md`, the six tests in section 6, `pack.yaml` in `uae.wps` and `uae.aani`, `factory/inventory.yaml`, the inventory tables in `design/12`, `annex-a` and `annex-b`.

Removed: none.

## 8. Backlog

No story id changed. Four stories were added at the end of their components, so 126 stories become 130 and 505 points become 523.

### New stories

| Story | Title | Points | Phase | Done first |
| --- | --- | --- | --- | --- |
| C04-S6 | Expand composites | 5 | 2 | C04-S1 |
| C09-S12 | Blocks, returns and the two traces | 5 | 2 | C09-S2, C04-S6 |
| C13-S6 | Composite and subflow editor | 5 | 3 | C13-S3, C14-S2 |
| C14-S6 | Composite steps on the canvas | 3 | 3 | C14-S2, C04-S6 |

### Stories whose text changed

| Story | Change | If it is already done |
| --- | --- | --- |
| C04-S1 | Every check marked Yes in the catalogue, which now includes L30 to L32 | Do delta story D02-S2 |
| C09-S2 | Early, late and duplicate messages; CK-AWAIT-01 to 05 (0.1a) | Add the three behaviours and the two scenarios |
| C11-S2 | `expect` compares the business trace and the detail | Add `detail`; rerun the six changed tests |
| C15-S3 | Inner steps nest under a composite in the timeline | Add the nesting |
| C20-S1, C20-S2 | The package holds expanded forms; a block is an interpreter frame | Feed the interpreter the expanded form; add the frame |
| C21-S1, C22-S2 | Block scenarios on DTFx and Flowable; on Flowable a block is an embedded sub-process | Not started in any plan; no rework |
| C23-S1, C23-S2 | 46 scenarios; the comparison includes the detail | Add the scenarios and the second trace |
| C25-S3, C26-S2 | The composites are tested alone | Not started in any plan; no rework |

Component C13 now depends on C14, because the subflow editor reuses the canvas.

### Delta stories

Work these two before the next backlog story. They are prompts, like any story.

#### D02-S1 Bring the spec to draft 0.2

Size: 1 SP. Branch: `story/d02-s1-spec-draft-0-2`.

**Story.** As a delivery lead, I want the repository's copy of the spec replaced by draft 0.2, so that every later story is built against one version.

**Definition of Ready**

- Read: this file; `design/05-workflow-dsl.md`, section 5.6.
- Done first: nothing.
- No file named in section 7 has local edits, or the edits are listed for a merge by hand.

**Definition of Done**

- The overlay is copied in. No other file changed.
- `README.md` says draft 0.2 and `conformance/kit.yaml` holds 46 scenarios.
- The common Definition of Done in `CLAUDE.md` holds.

**Definition of Success**

- The five commands of section 9 give the results that section states.

#### D02-S2 Bring the code already built to draft 0.2

Size: 3 SP. Branch: `story/d02-s2-code-draft-0-2`. Skip it if no story of C01, C02 or C04 is done.

**Story.** As a developer, I want the loader, the schema validation and the checks already built to accept draft 0.2, so that composites and subflows load, validate and are checked like everything else.

**Definition of Ready**

- Read: this file; `design/03-activity-contract-and-types.md`, section 3.7; `design/05-workflow-dsl.md`, sections 5.6 and 5.7; the four changed schemas.
- Done first: `D02-S1`.
- Fixtures: `factory/`, `examples/`, `expanded/`.

**Definition of Done**

- The name parser accepts the type `sub` and still rejects every name it rejected before (C01-S2).
- The loader reads a workflow of kind `subflow`, and an activity with the binding `subflow` resolves its subflow (C01-S1).
- Schema validation uses the draft 0.2 schemas (C02-S1).
- L30, L31 and L32 are added to the ported checks. L05, L07, L10 and L16 read subflows (C04-S1).
- A pin closure walks through a composite into its subflow (C05-S1), where that story is done.
- Tests were written first. The common Definition of Done in `CLAUDE.md` holds.

**Definition of Success**

- The code reports no error on `factory/` and `examples/`, and catches every case of `tools/mutation_tests.py` with the same code, M22 to M24 included.

## 9. Results to expect

```
python3 tools/build_factory.py     # uae.wps 61 artefacts, uae.aani 43
python3 tools/validate.py          # 0 error(s), 2 warning(s)
python3 tools/expand.py --check    # expanded/ is up to date: 4 workflows
python3 tools/mutation_tests.py    # 24 of 24 mutations caught
python3 tools/build_docs.py        # components 26  stories 130  points 523
```

## 10. Open points

| Id | Point |
| --- | --- |
| OP-1 | Composites cover scheme exchanges. A bank-side port is request and reply at the interop boundary; an adapter bridges a queue within the attempt timeout. A bank-side reply that can outlast an attempt timeout needs port-level messages and dispatch, which draft 0.2 does not define. |
| OP-2 | An `await` names one catalogue message. Waiting for the first of several different messages is not in the portable core. |
| OP-3 | `tools/expand.py` expands composites. Segments are still inlined by the interpreter story C09-S1; the product SHOULD do both in the one pass that C04-S6 builds. |
| OP-4 | The reply times in the factory pack are placeholders, like every other budget in it. |
| OP-5 | Who may author a subflow is the shared designer of the scheme. Whether that needs its own role is for Part 6 at the next draft. |
