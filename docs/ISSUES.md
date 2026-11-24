# Issues found while building the studio

Status: 23 November 2026. Each entry is an Issue and a Recommendation, as `spec/CLAUDE.md` asks. Where the build needed an answer to go on, the entry says what the studio or the runtime does now; where the spec changed with the code, the entry names the requirement. Each is for review at G1. The issues that wait on the owner or on others are listed in `docs/runtime/PLATFORM-PARITY.md`.

## I-01. WPS-IN-001 cannot pass as written

**Issue.** The test runs `p.wf.inward.v1` with the WPS port bound to `uae.wps.sim.wps-inward.v1`, scenario `payment-file`. The agent leg `uae.wps.wf.agent-payment-in.v1` sends a receipt confirmation and then awaits `uae.wps.ack`. A bench binds one simulator per port, and the inward simulator's scenario has no step that sends the ACK, so the await times out and the leg ends `pending`. Separately, the test names no tenant, so `uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes` is its default, the empty list, and the payment-file checks reject the file before the ACK is ever awaited. With `tenant: example-bank` the run reaches `await-receipt-ack:timeout` and stops there.

**Recommendation.** Add `tenant: example-bank` to WPS-IN-001, and add to the `payment-file` scenario of `uae.wps.sim.wps-inward.v1` an `expect: uae.wps.prc` followed by `send: uae.wps.ack`, and the same for the DIF. Or let a bench bind two simulators to one port, one per direction, and say so in Part 7.

**Resolved in this build (story C25-S4).** The first recommendation is applied through `spec/tools/build_factory.py`: WPS-IN-001 names `tenant: example-bank`, and the `payment-file` scenario now expects the receipt confirmation, answers with an ACK after ten minutes, and expects the disbursement file. WPS-IN-001 passes. The spec's five tools still give the stated results.

## I-02. The trace of an item-level step

**Issue.** CK-FOREACH-01 expects `item[0]:ok`, `item[1]:other`, so an item-level step carries its index. WPS-OUT-001 expects `record-checks:pass` for an item-level step inside a `forEach`, with no index. R-C-03 does not say which.

**Recommendation.** State in Annex C that an item-level step is traced as `<id>[<index>]:<outcome>`, and write the WPS traces that way.

**What the studio does.** Traces the index. The test runner treats an expected entry without an index as equal to `[0]` when the batch has one item, and says so in `traceEqual`.

## I-03. Whether a send is answered on the connection

**Issue.** Part 5 says a synchronous reply stays inside one `exchange` activity and a later reply is an `await`. The activity contract does not say which a given exchange expects. `uae.aani.exchange.send-credit.v1` must read the switch's reply on the connection (its `pending` outcome and AANI-OUT-001 say so), while `uae.wps.exchange.send-salary-file.v1` must not wait for the ACK (WPS-OUT-003 expects `submit:accepted` when WPS never replies).

**Recommendation.** Add `reply: same-connection | later | none` to the exchange contract (R-03-09), checked by the schema.

**What the studio does.** `inquire` reads a reply on the connection, `respond` expects none, and `send` or `report` reads one only when the activity declares a `pending` outcome (`expectsSyncReply` in `src/interp/reference.ts`).

## I-04. Ports a test reaches but does not bind

**Issue.** R-07-10 says every port a workflow under test can reach must have a simulator bound. Most factory tests do not bind the ports reached by `finally` and by the platform blocks (`p.port.channel.v1`, `p.port.facts-out.v1`, `p.port.payment-record.v1`, `p.port.operations-queue.v1`). The reference validator does not check this half of L22.

**Recommendation.** Either add the ports to the benches, or state in Part 7 that an unbound port uses its first simulator in scenario `accept`, and make L22 a warning for it.

**What the studio does.** The second option. The run records the default binding and says so in its warnings; L22 reports it as a warning.

## I-05. Digit strings depend on the YAML version

**Issue.** `uae.wps.rule.person-id-format.v1` has the example value `personId: 00001234567890`, unquoted. PyYAML (YAML 1.1) reads it as a string; a YAML 1.2 reader reads the number 1234567890, and the example then fails. The content hashes in the pack manifests are taken over the PyYAML reading.

**Recommendation.** Quote every identifier that is a digit string in `tools/build_factory.py`, and state in Part 1 that documents are read as YAML 1.2 with identifiers quoted.

**What the studio does.** Reads YAML with the integer tag narrowed to PyYAML's, so a leading zero keeps a string (`src/core/yaml.ts`). Every hash then matches the manifests.

## I-06. Budgets do not fit the deadlines (L21)

**Issue.** L21, built here, finds that the attempt budgets on the longest path exceed the deadline in `uae.aani.wf.credit-out.v1` (87 s against PT8S), `uae.aani.wf.credit-in.v1` (28 s against PT5S) and `p.wf.commercial.v1` (114 s against PT10S), using the type defaults of Part 3 where a block sets no policy.

**Recommendation.** Give the Aani blocks and the platform blocks used on instant rails explicit, short policies, or define in R-05-18 which attempts count towards the budget.

**What the studio does.** Reports L21 as a warning, so the factory pack still validates.

## I-07. Reads that nothing writes (L19)

**Issue.** L19, built here, finds legs that read `status` before any step writes it (`render-refusal`, `render-acceptance` in `uae.aani.wf.credit-in.v1`, `render` in `uae.aani.wf.credit-refuse.v1`, `render-receipt` in `uae.wps.wf.agent-payment-in.v1`), and inward legs that call `p.post.accounting.v1`, which reads `facts.route` and `facts.reservation` that no inward flow writes.

**Recommendation.** Narrow the reads of those blocks, or have intake write an initial `status`, and give inward posting its own block or optional reads.

**What the studio does.** Reports L19 as a warning, since the reference validator marks it "to build".

## I-08. CK-LOOKUP-01 needs one step id twice

**Issue.** The expected trace `limit:pass, await:received, limit:reject` names step `limit` twice. Two steps with one id break R-05-24 (L29).

**Recommendation.** Say in the kit that the scenario is a loop, or rename the second step.

**What the studio does.** The kit content runs `limit` and the `await` inside a bounded loop, so one step runs twice.

## I-09. When a signal enters the trace

**Issue.** CK-SIGNAL-01 expects `a:ok, signal:pause, signal:resume, b:ok`. A pause that arrives while `a` runs could be traced before or after `a:ok`.

**Recommendation.** State in Annex C that a signal is traced when the instance takes it: at the next step boundary for pause, at once for the others.

**What the studio does.** That.

## I-10. The `await:waiting` marker

**Issue.** CK-SIGNAL-03 expects `await:waiting`; no other scenario shows a waiting marker, and R-C-03 does not define it.

**Recommendation.** Define it in R-C-03: when a signal reaches an instance that is in an await, the trace records `<await id>:waiting` before the signal.

**What the studio does.** That.

## I-11. The scheme manifest's name in a pack

**Issue.** Pack manifests list the scheme manifest as `uae.wps.scheme` and `uae.aani.scheme`. `scheme` is not a type in R-01-02, so the name breaks the grammar it sits beside.

**Recommendation.** Name it by scope in the manifest (`scope: uae.wps, kind: scheme`) rather than by a pseudo-name.

**What the studio does.** Resolves `<scope>.scheme` to the scheme manifest of that scope.

## I-12. The source file's name in a sample

**Issue.** The SIF edge rule `uae.wps.rule.file-name-matches-control.v1` reads the name the file had on the channel. A sample's own file name (`wps-sif-valid.sif`) is not that name, and the test schema has no field for it.

**Recommendation.** Add `given.inboundName` to `test-case.schema.json`.

**What the studio does.** The synthetic samples in `studio/fixtures/samples/` carry a `# file:` line that the intake maps read and strip.

## I-13. Most blocks have no script-behind

**Issue.** Every factory activity names a script or a built-in service; none exists (README, "Not verified").

**Recommendation.** C24 to C26 as planned.

**What the studio does.** Runs a reference behaviour by activity type: validate runs its rule sets, decide its tables, maps take the reply status, and blocks with ports take their outcome from the simulator's reply (`src/interp/reference.ts`). Six scripts-behind exist where a block needs real logic: the SIF and PIF intake maps, the Aani credit and status-request intake maps, the business date, and the duplicate check (`studio/scripts/`).

## I-14. What a message count counts

**Issue.** AANI-OUT-003 expects three `uae.aani.credit-transfer` messages for one logical message sent with two retries; AANI-OUT-001 expects one. A count could be of transmissions or of logical messages.

**Recommendation.** Say in the test schema that `count` is transmissions on the wire, and add `logical` for distinct message ids.

**What the studio does.** Counts transmissions and notes the logical count when they differ.

## I-15. Elapsed time on a business calendar

**Issue.** CK-AWAIT-02 expects `virtualElapsed: P1D` for an await on the business calendar. Started on a Friday, that is three calendar days.

**Recommendation.** Say that `virtualElapsed` follows the calendar of the wait it measures.

**What the studio does.** The kit compares business days for that scenario. WPS-OUT-003 starts on a Tuesday, so P1D is one calendar day there.

## I-16. Which reason code a test checks

**Issue.** A test's `expect.reasonCode` does not say where the code is read: `status`, a finding, or the leg's outcome.

**Recommendation.** Define it as the ISO status reason code the payment record shows at the end (`status.reasonCode`).

**What the studio does.** Takes the last reason code recorded with a non-primary outcome on the instance, which today is the same.

## I-17. Hold decisions

**Issue.** `p.sim.operations.v1` decides with `released`, `rejected`, `approved` or `declined`; `p.hold.approval.v1` returns `approved`, `declined` or `expired`. The mapping from a decision to an outcome is not written down, and the default scenario `release` does not name an approval outcome.

**Recommendation.** Give each hold block the decision values it accepts.

**What the studio does.** A decision equal to an outcome name is that outcome; `released` and `approved` are the primary; `rejected` and `declined` the first negative.

## Found while building draft 0.2

## I-18. What `attempts` counts for a step that runs more than once

**Issue.** AANI-OUT-003 expects `attempts: { send.send: 3 }`: three attempts of one call. AANI-EXC-002 expects `attempts: { inquire: 3 }` with no reply at all, where the inquiry runs three times in a loop and each call makes two attempts before its total timeout (PT3S attempts, PT8S total). Counting attempts gives six; counting calls gives three.

**Recommendation.** Split the expectation: `attempts` for the attempts of one call, `calls` for how many times a step ran.

**What the studio does.** For a step that ran once, `attempts` is its attempts; for a step that ran more than once, its number of calls. The test report shows both.

## I-19. The kit's own workflows await at the top level

**Issue.** L31 says an `await` appears only in a subflow, and engines run the expanded form, where an await sits inside a block. The kit's await scenarios (CK-AWAIT-01 to 05, CK-SIGNAL-03, CK-LOOKUP-01) expect `await:received` in the business trace, so their workflow awaits at the top level, which an authored workflow may not do.

**Recommendation.** State in Annex C that kit workflows are written in the expanded form and are exempt from L31, or move the await scenarios into blocks and their entries into `detail`.

**What the studio does.** The kit content is engine-level and is not checked with L31.

## I-20. The bank pack's dependencies

**Issue.** `tools/build_factory.py` writes the example bank pack with `dependsOn: p, uae.p, uae.aani` from a fixed table. Derived from what the bank's artefacts reach, the pack depends on `p` only; its configuration set accepts all four shared packs.

**Recommendation.** Define a bank pack's `dependsOn` as the shared packs its configuration set accepts, or as what its artefacts reach, and generate it.

**What the studio does.** Generates the four shared manifests identically; reports the bank manifest's difference.

## I-21. A refused payment file and the refuse leg (C25-S4)

**Issue.** C25-S4 says a payment file addressed to another agent takes the refuse leg and is handed to operations. The agent leg checks the file itself and ends `pending` on a reject; the refuse slot of `p.wf.inward.v1` runs only on the platform's disposition, which knows nothing of the agent check.

**Recommendation.** Decide where the agent check runs: in the leg, ending `rejected` and letting the platform call the refuse slot on that outcome, or before the leg as a scheme check the disposition reads.

**What the studio does.** Nothing beyond the spec: the wrong-agent case ends `pending` in the leg.

## I-22. `returned` cannot be reached in the Aani outward leg (C26-S2)

**Issue.** `uae.aani.wf.credit-out.v1` declares the exit outcome `returned`, but no path ends with it, so no test can expect it. The C11-S5 check reports it as untested.

**Recommendation.** Remove `returned` from the leg's exit, as C26-S2 allows, in a new release.

**What the studio does.** Reports the gap; the leg is unchanged.

## I-23. Provenance of AI-drafted content

**Issue.** None in the spec: `common#provenance` already holds `ai: {model, promptRef, inputs}`. Recorded here because the first implementation got it wrong and the gates caught it.

**What the studio does.** The assistant writes `origin: authored`, `status: provisional`, the clauses, and the model, a prompt hash and the inputs under `ai`. No prompt text is stored.

## I-24. A new leg has no start message until its scheme is released

**Issue.** A leg reaches a running payment only through a scheme's dispatch table (`messages[].disposition.start.leg`). A designer who builds a new leg in a scheme cannot test it from an inbound message, or deploy it, until a new scheme release names it. The `uae.wps` scheme cannot be released while it lists planned messages (C25-S6), so a new WPS leg has no way in.

**Recommendation.** Say in Part 4 how a leg that fills the same slot as a dispatched leg is started before its scheme names it: either a test-only start that a test or a deploy names explicitly, or a rule that it takes the intake of the dispatched leg that fills the same slot of the same platform workflow.

**What the studio does.** It takes the second reading. The test runner and the deploy runner start such a leg from the intake of the leg filling the same slot (`startEntryFor`), and the export package records the start it took under `fallbackStarts` in its manifest, with this issue's id.

The end-to-end tests no longer depend on it. WPS is a factory scheme, so its test changes only bank configuration. A new leg belongs in a scheme whose dispatch table names it, as the `uae.demo` test shows; a dispatch table names one leg per start message, so a second leg for the same message replaces the first in a new scheme release.

## I-25. `returned` has no test in the WPS salary file leg

**Issue.** `uae.wps.wf.salary-file-out.v1` declares the exit outcome `returned`, but no path ends with it and no factory test expects it. The C11-S5 check reports it as untested. It is the same gap as I-22.

**Recommendation.** Remove `returned` from the leg's exit in a new release, or add the return path (a WPS rejection after acknowledgement) once the rulebook confirms it (D10).

**What the studio does.** Reports the gap in the test coverage view and in `GET /api/test-coverage`; the leg is unchanged. A white-box test pins the report.

## I-26. Who sets a payment's direction and segment at intake

**Issue.** `p.table.workflow-select.v1` picks the platform workflow from `header.kind`, `header.direction` and `header.segment`. A start entry in a dispatch table names only the `kind`. Part 2 says the scheme is set at intake but says nothing of direction and segment. An employer's salary file comes in (`direction: in`) but starts an outward corporate payment, so a front door that takes the payment's direction from the message's direction sends it to the inward workflow.

**Recommendation.** Add `direction` and `segment` to `disposition.start` in the scheme schema, so that the dispatch table says what payment a message starts, with a check that the pair selects a platform workflow whose slot the leg fills. Until then, require the intake map to write them.

**Decision.** The dispatch table routes, not the intake script.

**What changed.** `disposition.start` now requires `kind` and `direction` and may name a `segment` (schema `scheme`). R-05-42 says what they mean, and check L33 enforces it in the spec's validator and in the studio, with mutation cases M25 and M26. L33 requires that the entry select, through workflow-select, one platform workflow whose slot the named leg fills. The WPS and Aani manifests name them. The front door, in the studio interpreter, the test runner and the deploy runner, sets them on the header from the dispatch table, and they stand after the intake map runs. The intake scripts no longer set them.

## I-27. Artefacts that refer to one another cannot be published one at a time

**Issue.** Maker-checker is per draft (R-06-04), and a draft publishes only when its references resolve. Some artefacts refer to one another: a port needs a simulator (L22) and the simulator names the port; a composite names its subflow and the subflow implements the composite; a subflow may await only a message in its scheme's catalogue (L16) while the scheme's dispatch table names the leg that calls the composite. None of them can be published first, so a new scheme cannot be published at all.

**Recommendation.** Define a change set in Part 11: drafts reviewed and approved together, checked as a whole, with green tests with the whole set in place, published all or none. The pack is a candidate unit, but a change set is smaller: it is what one review covers.

**Decision.** The studio generates the change set, during approval and export.

**What changed.** R-11-19 and R-11-20 in Part 11.
- A draft is checked with the tenant's other drafts in place while it is edited.
- `GET /api/change-set?key=` shows the change set the studio generates for a draft: the draft and every draft that refers to it or that it refers to, directly or through others.
- Approving any member, `POST /api/publish`, publishes the whole set. Each member is checked with the others in place, the tests that target any member must be green, and the checker must not be the maker of any. All publish or none does.
- The set is recorded with an id such as `CS-2026-11-17-001`, listed by `GET /api/change-sets` and written to the audit log as `publish-change-set`.
- Governance, Review and approve, shows what each draft publishes with.
- An export manifest lists `changeSets`: those recorded at approval, and `pack:<scope>@<release>` for content shipped with the spec. Every artefact of the package is in exactly one.

## I-28. The example bank's large-payment hold does not see salary files

**Issue.** `b.hook.pre-release-approval.v1` holds a corporate payment above the bank's internal limit, through `b.rule.internal-limit.v1`, which reads `payment.instructedAmount`. A salary file is a batch: its total is `batch.controlSum` and each item carries its own amount, so the rule never fires for one. Lowering the limit to 1000 still lets a salary file of 1500 through.

**Recommendation.** Decide whether the internal limit applies to a batch total, to each item, or to both, and say so in the rule; for a batch, match on `Batch.controlSum`. This is the bank's content, so it is the bank's decision.

**Decision.** The limit checks each line and the file total, as well as a single payment.

**What changed.**
- `b.rule.internal-limit.v1` has three cases against the one `internalLimit` the bank sets: a payment's amount, each line's amount, and the file's `controlSum`. Its examples now include the boundary (R-04-05), a line above the limit, and a total above it with every line within.
- `b.validate.internal-limit.v1` reads `batch` and `items[]` as well.
- So that a bank's hook may read the lines (R-06-07), the `before-release` hook point of `p.wf.corporate.v1` now offers `items[]` in its contract.
- These are edits to draft 0.2 content at release 1.0.0, made in the generator and the hand-written workflow, with the pack manifests regenerated. Once the factory and bank packs are released, the same change would be new releases.
- A finding's path is fixed per rule. The finding names `payment.instructedAmount.value`, and its message says the amount may be a payment, a line or a file total.

**Found on the way.**
- A halt hook that found something always counted as failed, both in the studio and on DTFx and Flowable. Its rule writes `facts.validation`, and the runtimes allowed a hook's blocks to write only `facts.hooks`, while check L13 allows any `facts.*`. The runtimes now follow L13, so the trace shows `hook:hold`.
- On a deployed worker the approval hold could not run: the party service fired its expiry timer at once, and the operations decision went to the engine instead of the waiting hold. Timers on the party service are now the engine's, and a hold's decision stays with the hold.

## I-29. An assistant beside every surface

**Issue.** C16 defines the AI assistant as a page of its own: an instruction and scheme text in, a gated draft out. The forms have many fields, and a designer who is not a payments engineer needs help where they are working: what a field means, what the document does, what is left before it can be published, and an assistant that can build with them. The spec is silent on an assistant inside the editors, and on whether it may change a document.

**Recommendation.** A panel beside every authoring surface, with the guard-rails of C16 kept: every change is gated, the designer decides, publishing stays with maker-checker, provenance records the model.

**Decision.** The designer asked for it on every surface: the artefact editors (each kind), the workflow designer and hooks, a bank's configuration, and the test bench. The assistant may change the document, and save drafts and tests, but asks first each time, as a coding agent does: allow once, allow always, or deny. It can undo its own changes, and so can the designer. It never publishes.

**What changed.**
- "What is happening" (`studio/src/explain/`): help for every field of every form, the document in plain words with a small picture, a checklist and the next step, and a bench run step by step. No model, so it works without credentials. `test/inline-assistant.test.ts` fails when a form has a field without help.
- "Ask" (`studio/src/ai/agent.ts`, `POST /api/ai/agent`): Claude with tools. Tools that only look run at once: the document, the checks, the explanation, field help, the catalogue, a published artefact, the samples, a bench run. Tools that change something wait for the designer: `edit_document` (the whole document, with its checks shown before it is allowed), `undo_change`, `save_draft`, `save_test`, `save_sample`. The server holds no conversation; each change carries its before and after.
- A document the assistant changed carries `provenance.ai` with the model and a prompt reference (C16-S5).
- `TH_ANTHROPIC_API_KEY` is read as well as `ANTHROPIC_API_KEY`, for hosts that keep the usual name for themselves. `TH_AI_REPLAY` replays a recorded conversation for tests and offline demonstrations, and the panel says so.
- The browser walk-through and the demo scripts waited too little for the designer's blank starter before pasting; the panel's requests showed it. They now wait for it.

**Open.** The live model was not exercised in this change: the session had no key. The replayed conversations test the loop, the permissions and the undo; a run against the live model should follow once a key is set.

## I-30. Elsa Workflows as an engine target

**Issue.** The spec names three engine targets: Temporal, DTFx and Flowable. A bank may run Elsa Workflows, a .NET engine, and asked for it. Elsa's model differs from DTFx's: it keeps a workflow instance's state, suspends it at bookmarks and resumes it with input, but it does not replay code.

**Recommendation.** Add Elsa as a fourth target with the interpreter strategy, sharing the DTFx harness's interpreter, so a workflow means the same thing on both. Keep the interpreter's journal in Elsa's workflow state and resume only at a bookmark, which gives the replay that DTFx and Temporal provide. Prove it with the whole conformance kit and a deployment with separate workers.

**Decision.** The designer asked to plan and build it, on the latest Elsa release: 3.10.2 at the time.

**What changed.**
- Spec: `elsa` in the engine binding schema and the example bindings; Part 9 has the Elsa strategy, an Elsa column in the construct and policy mappings, R-09-11 extended to Elsa, R-09-18 (the journal in the workflow state, resumed only at a bookmark, replayed to the same operations), and what was proved on Elsa 3.10.2 with its limits; R-C-05 names Elsa.
- `studio/engines/shared/`: the interpreter, the deployment, the claim-check store and the virtual clock, moved out of the DTFx harness. The interpreter no longer uses a DTFx type: retries are a `RetryPlan` that DTFx runs with its `RetryInterceptor` and Elsa as journalled timers. DTFx still passes 46 of 46.
- `studio/engines/elsa/`: the Elsa harness (one generic Elsa activity with its journal and bookmark, the replay engine, the host), the kit adapter and runner. **46 of 46 conformance scenarios pass on Elsa**, and two runs give the same traces. CK-FOREACH-02 runs 10,000 items in 21 Elsa workflow executions.
- Export and deploy: the target `elsa` in the export page, the API and the binding generator; the deploy runner reads the engine from the package's binding. In the black-box suite, the scheme built from scratch is exported for Elsa and deployed with a separate worker per queue: paid end to end, and rolled back when the clearing house refuses it.
- The black-box test client resends a GET once when its pooled connection was closed while a synchronous deploy test blocked it.

**Limits.** The replay is the interpreter's own, so each resume reruns the journal; the chunk size keeps it small. Timers and retries run on the host's virtual clock rather than Elsa's scheduler, so the kit can use virtual time; a production host would hand timers to Elsa's scheduler and keep state with an Elsa persistence provider. Elsa's designer is not used: the documents stay the source.

## I-31. The assistant's replies show markdown as marks

**Issue.** Against the live model, the agent's replies in the panel beside every surface (I-29) use markdown: `**bold**` labels, `- ` lists and `` `code` `` for artefact names. The panel showed them as plain text, so the designer saw the asterisks, dashes and backticks. It looked broken in training video 2. The spec is silent on how the assistant's replies are shown.

**Recommendation.** Render a small, safe subset of markdown in the assistant's replies, and nothing more. Asking the model for plain text alone would not be enough: a model does not always follow it, and recorded conversations already hold markdown.

**Decision.** The panel renders paragraphs, `- ` and `1. ` lists, `#` headings as a bold line, `**bold**` and `` `code` ``. Anything else stays text.

**What changed.**
- `studio/web/src/markdown.tsx`: a hand-written renderer, no new dependency (ADR 0001). It builds React elements and never sets HTML, so a tag in a reply is shown as text. It makes no links: a URL or `[text](url)` stays text, so nothing in a reply navigates away.
- The assistant's bubbles use it. The designer's own messages are shown as typed.
- `test/markdown.test.ts` checks bold, code, lists and paragraphs, and that `<script>` is escaped. `test/blackbox/ui-assistant.test.ts` replays a reply with `**bold**`, `` `code` `` and a list, and checks no `**` or backtick reaches the page.

**Found, and fixed with I-30.** `test/units.test.ts` "knows when it has credentials" failed where `TH_ANTHROPIC_API_KEY` is set in the environment: it cleared `ANTHROPIC_API_KEY` but not `TH_ANTHROPIC_API_KEY`. It now clears both.

## I-32. The debugger: stepping through a run

**Issue.** F-17 asks that a designer can step through a run, see the timeline of attempts, waits and timers, and see the context diff per step. The bench showed a report after the run: the trace, the steps with their deltas, and the timeline. It could not show where the run was in the workflow at a given moment, the data as it stood then, or what a step decided, and it could not step into a leg or hook.

**Recommendation.** A debugger on the bench: the run goes end to end on the studio interpreter and virtual clock, keeps the data after every event, and the designer steps through it afterwards, forwards and back. A run is deterministic, so stepping back is exact and nothing needs pausing; and it never touches a running payment.

**Decision.** The designer asked for a debugger that runs a workflow end to end and lets users step in it, seeing the data change, what ran and the decisions.

**What changed.**
- A debug run (`POST /api/debug/run`, or a saved test with `config.debug`) keeps, in each timeline event, the instance's data after it (only when it changed, and up to a limit for very large batches). Each step's outcome event also carries its activity and release, its context delta, its decisions, its worst finding, its findings and the rules that ran. A switch records which branch it took. The record carries the document each instance ran, and each segment it uses. A run that is not a debug run is unchanged.
- The debugger (`studio/web/src/views/Debugger.tsx`), on the bench and beside each saved test: where the run is (the workflow and its steps, segments expanded, ticked as reached); what happened, in plain words; decisions and rules; data changed; all the data now, with changes marked. Step into, step over, step out, step back, restart, breakpoints and continue, every event, and the whole run as a list.
- `test/debugger.test.ts` and `test/blackbox/ui-debugger.test.ts`. Training video 2 has a seventh part on the debugger; the guide has a section, and exercise 4a.

## I-33. Importing a scheme from its source code

**Issue.** A scheme that already runs as workflows (WPS on the existing engine, NPSS) has its behaviour in code, configuration and tests. The studio could be given that content only by hand, document by document. The spec has an AI assistant that drafts single artefacts from scheme text (C16), but no way to bring in a whole scheme, and no rule for how complete such an extraction must be.

**Recommendation.** An extraction prompt for an AI agent, and an import that checks its output hard and saves it as drafts. Comprehensiveness is checked, not trusted: every file of the source is inventoried or excluded with a reason, every document is explained by an item of the source, and every unmapped item is listed for the expert. The import never publishes.

**Decision.** The designer asked for a prompt to point at the WPS source written as workflows, extracting configuration, code and content, and an import function, with comprehensive extraction.

**What changed.**
- `docs/import/EXTRACTION-PROMPT.md`: the prompt (inputs, guard-rails, eight phases, what to look for in every file, where each thing goes in the studio, scripts-behind, tests and samples, findings, a self-check with the importer), and the bundle format.
- `studio/src/import/bundle.ts`: read a bundle (zip or folder); analyse it (the checks I10 to I80 listed in the prompt, the studio's static checks with the rest of the bundle in place, a published release never changed, and the bundle's tests run in a shadow of the catalogue, in memory, with the bundle in it as if published); apply it as drafts, with every permission checked first so that an import is saved whole or not at all.
- `studio/src/import/sample.ts`: a training bundle, the factory WPS scheme as `uae.wpsx`, with its inventory (72 source files, 75 items, the four planned messages retired with their reason), the bank's values for it, and findings.
- `POST /api/import/preview`, `POST /api/import/apply`, `GET /api/import/sample`, `GET /api/import/prompt`; `npm run studio -- import <bundle> [--apply]` and `import-sample`; the Import page.
- `test/import.test.ts`: the training bundle previews clean, imports as drafts only, publishes through maker-checker and its tests pass on the published content, and a second import is unchanged; and every refusal. `test/blackbox/ui-import.test.ts` in the browser.

**Found on the way.** Copying WPS under a new scope showed that a bank's values belong to an artefact by name: the imported rule needs the bank's routing code again, or the inward test fails at the simulator's self-check. The bundle carries the bank's configuration for that reason, and the prompt asks for it.

## I-34. Exporting releases to a Git branch

**Issue.** The studio exported a package as a zip to download. Nothing carried a published change set to the place a runtime deploys from, and nothing kept that place insert-only, as releases are.

**Recommendation.** Connect the studio to one repository and branch; write each published change set to it as one commit; never rewrite a file it wrote and never force a push; offer a pull-request mode for banks that review in their repository host. The runtime's pipeline reads the branch (plan W8).

**Decision.** The designer asked that the studio connect to a GitHub branch and export to it after changes are made.

**What changed.**
- `spec/design/09-export-bindings.md`: R-09-19 to R-09-22.
- `studio/src/export/git.ts`: connect (a shared checker or service operations; the branch must exist; the token is read from `TH_GITHUB_TOKEN` at push time and never stored, shown or audited), the files of a change set (`artefacts/`, `banks/`, `releases/<scope>/<name>/<release>/<target>/`, `change-sets/`, `environments/<env>/activations/`), the insert-only check against the branch, commit or pull request, and a fresh start when the branch moved. It uses the `git` command, so a local or on-premises mirror works the same as GitHub.
- `POST /api/publish` exports at once when the connection says so; a failed export is reported with the publish, never undoes it. `GET`, `PUT`, `DELETE /api/git` and `POST /api/git/export`. The Git branch panel on the Export page.
- `test/git-export.test.ts` (7 tests, on a local bare repository) and `test/blackbox/ui-git.test.ts` in the browser.

**Not tested here.** Opening a pull request on GitHub itself needs a real repository and token; on any other remote the branch is pushed and the result says the review is to be opened in the repository host.

**Superseded by I-44.** The branch is now the studio's only store, written through the GitHub API, not a target it exports to with the `git` command; `test/git-export.test.ts` gave way to `test/content.test.ts`.

## I-35. Temporal as a runtime engine

**Issue.** The plan (W3) needs four engines behind one seam. The studio proved the conformance kit on DTFx, Flowable and Elsa; the Temporal binding existed only in the TransactHub source, behind its own DSL runner. The machine that builds here cannot download Temporal's native test server.

**Recommendation.** Bind Temporal in the new runtime with the studio's own interpreter, to the same plan and observation as the other harnesses, so that the kit runner and the deploy runner compare it unchanged. Run Temporal's time-skipping test server from its Maven Central artifact.

**Decision.** Accepted with the plan (D1, D3).

**What changed.**
- `runtime/src/TransactHub.Interpreter` compiles `studio/engines/shared`, so there is one interpreter.
- `runtime/src/TransactHub.Engine.Temporal` holds the Temporal binding:
  - the generic interpreter workflow;
  - the front door as a long-running workflow, which handles root starts, signals, deliveries at their times and correlation;
  - handlers as activities;
  - one task queue per scope;
  - a logical business clock.
- `runtime/tools/TransactHub.TemporalHarness` and `runtime/tools/temporal-test-server` run a plan on Temporal.
- `studio/engines/temporal` holds the kit adapter. `studio/src/deploy/deploy.ts` now drives Temporal packages too.
- `studio/test/blackbox/e2e-wps-temporal.test.ts` covers the WPS salary file on Temporal: four task queues and four worker processes. For rejection, sanctions and holds, the trace must match the studio interpreter.

**Found on the way.** Three rules apply to the binding:
- **Business time cannot be Temporal's clock.** Processing time leaks into it, by 0.2 s over an hour's wait. Business time therefore moves only on timers, declared latencies and arrivals. A child's end is an arrival.
- **Results due at the same business time are handed back in call order,** as the interpreter and DTFx do. Otherwise parallel branches swap places in the trace.
- **A party's messages sent during an attempt are routed before the attempt ends.** Otherwise an early reply misses its await.

## I-36. Native code-behind, and the source's rules as studio rules

**Issue.** The plan's first vertical slice runs the TransactHub source's own WPS code inside a studio flow. The spec names a `native` binding ("code-behind in the platform language, for hot-path and regulated blocks") but says nothing of where it runs or how the studio tests it. It also requires validation to be declarative rule sets (R-03-08), while the source's CBX checks are C# classes.

**Recommendation.**
- **Where native code runs:** in the runtime's native host. The studio's interpreter and every deployed worker call it with the attempt's view, parameters and time. There is one implementation, the runtime's port of the source, which the golden corpus proves.
- **Where validation goes:** the source's checks become studio rules, proven against the port with the same corpus, rather than a native validate activity.

**Decision.** Agreed with the designer as step 2 of the plan.

**What changed.**
- **Spec:** R-08-14.
- **Studio:** the interpreter runs a `native` activity on `TH_NATIVE_HOST`. The virtual clock waits for work outside the process, so a native call is not taken for an idle run. Deployed workers pass `TH_NATIVE_HOST` on.
- **Runtime:**
  - `runtime/src/TransactHub.Native` is the native host.
  - `TransactHub.Activities.Wps/Native` holds the source's Central Bank SIF writer and its CBX checks, on the canonical context.
  - The release API picks a package's root workflow by kind, not by position. The first flow listed was a bank hook.
- **Extraction:** `docs/import/extract_wps_outward_cbx.py` turns the source's outward flow (`dib-wps-outward.v1`, 25 steps) and its rule and writer classes into an import bundle, kept in `content-configurations/extractions/wps-outward-cbx`. Every step and class is mapped or named as not done with the reason.
- **Leg:** `uae.wps.wf.salary-file-out.v1` release 1.1.0 renders the file with the source's writer, behind the same interface.
- **End to end:** `studio/test/blackbox/e2e-wps-source-slice.test.ts` covers extraction → import → maker-checker publish → branch → runtime release API (PostgreSQL) → Temporal deployment. The Central Bank SIF it produces is checked field by field.

**Not done.**
- The CBX, SSM, DE, DIF, RFA, PRC and RRI checks as studio rules.
- Rule 79 and the EVP rules need the raw file lines, which the canonical context does not keep.
- Rules 82 and 83 need a registered-accounts lookup.

## I-37. The Aani scheme is removed from the studio

**Issue.** The plan's phase P6 (W7 in `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`) removes the Aani scheme (`uae.aani`) from the studio. Its content was provisional: the message set, time limits and reason codes were never public. The factory, the tests, the guide and the training all used it, and some of them used it as "a second scheme".

**Recommendation.** Remove it in one change, keep what it proved by re-pointing it at WPS, the platform or the example bank, and add a check so that a removed scheme cannot be left behind.

**Decision.** Accepted by the owner with the plan (P6).

**What changed.**
- **Spec:**
  - `factory/uae.aani/`, `expanded/uae.aani.wf.credit-out.v1.yaml`, Annex B and build component C26 are gone. Annex B's letter is not reused.
  - `tools/build_factory.py` lists the shared packs in `SHARED_PACKS`: `p`, `uae.p`, `uae.wps`.
  - R-12-11 and check L34: the factory holds only the packs its inventory lists. Every folder under `factory/` is a listed pack, every listed pack has a folder, and an artefact sits in its own scope's folder. L34 is in the catalogue of Part 5, in `tools/validate.py` and in the studio (`factoryPackIssues` in `studio/src/checks/checks.ts`). Mutation cases M27 and M28 prove it.
  - Mutation cases M07, M10, M14, M15 and M17 broke Aani files. They now break WPS, platform or example-bank files. M07 turns a WPS leg into a handler, because no handler is left (I-38).
  - The example bank's dated lookup now uses its own limit. `b.rule.internal-limit.v1#internalLimit` has a bound (at most 1000000). The configuration set holds 250000 for 2026 and 100000 from 2027. New tests BANK-LIM-001 and BANK-LIM-002 take the place of AANI-RULE-003 and 004. BANK-HOOK-001 and 002 now run a synthetic WPS salary file; 001 is held on the file total. The bank pack depends on `uae.wps` in place of `uae.aani`. Its `dependsOn` is still a fixed table (I-20).
  - `p.table.rail-choice.v1`'s examples, the engine bindings, the backlog stories that cited Aani, the design examples and the counts name WPS, the platform or the bank.
- **Studio:** the Aani samples, generators and scripts-behind are gone. The templates, the editors, the pack and binding orders and the coverage picker name `uae.wps`. Tests that needed a handler or a switch, which only Aani had, use a minimal synthetic one inside the test.
- **Docs:** the guide, `docs/TRAINING.md`, the extraction prompt and the WPS bundle's configuration. The narrated videos still show Aani; `docs/TRAINING.md` says they need re-recording.

**Kept as history.** `spec/delta/DELTA-0.2.md` and the entries above this one still name Aani. They record what was true when they were written.

## I-38. What the spec has no factory example of once Aani is gone

**Issue.** Aani was the only factory content for several things the spec requires or describes. With it removed, the spec still describes them but the factory shows none:
- **No leg fills `p.wf.commercial.v1`.** The instant workflow, its deadline, its synchronous marker and decision D9 have no scheme behind them. L21 still warns on the workflow itself (114 s of attempts against PT10S).
- **No handler.** R-05-11, the `handler` workflow kind, a scheme's `handlers` list and the `handler` disposition have no factory content. L07 is proven only by mutation case M07, and CK-HANDLER-01 covers the interpreter. R-12-07 asks for a test per exit outcome, so there is nothing to test.
- **No inquiry after an unknown send, and no `switch` in any factory workflow.** Part 5's "only when asked" row now cites CK-UNKNOWN-01.
- **No inward credit answered on the connection, and no prefund check.**
- **Three blocks are reached by nothing:** `p.enrich.payment-record.v1`, `uae.p.enrich.participant.v1` and `uae.p.rule.currency-aed.v1`. L11 warns on each. Annex D still lists the participant lookup as a check block.
- **Conformance fixtures:** AANI-RULE-001, 002 and AANI-EXC-001, 002 were marked `conformance`. WPS-EXC-001, 002, WPS-MAP-001, 002 and P-RES-001, 002 remain.

**Recommendation.** Decide with the owner whether the factory needs a second, synthetic scheme as a worked example of an instant rail and a handler. It would be marked as an example, as the example bank is, and carry no scheme facts. Otherwise:
- state in Part 12 that `p.wf.commercial.v1` waits for its first rail;
- move the handler and inquiry examples of Part 5 to the conformance kit;
- decide whether the three unreached blocks stay in the factory, or declare what runs them.

**What the studio does.** Nothing new. The tests that need a handler, a switch or a second scope build a minimal synthetic one inside the test. The kit stays at 46 of 46.

**Decision (owner, 2026-11-21).** No second synthetic scheme. So:
- Part 12 states that `p.wf.commercial.v1` waits for its first rail.
- The handler, `switch` and inquiry examples are covered by the conformance kit (CK-HANDLER-01, CK-UNKNOWN-01) and by the synthetic fixtures inside studio tests, not by factory content.
- The three unreached blocks stay in the factory as platform blocks. Their L11 warnings stand until a WPS flow uses them.

## I-39. Which failed credits a returns file carries

**Issue.** Plan phase P6 authors the agent's returns file (`uae.wps.rfa`). Annex A says only that returns run as a returns file from the agent. It does not say which employee credits go in it. A credit can fail in two ways: the core system refuses it, or its outcome is unknown after the retries, and it may have posted. The spec does not say whether a file with some refused credits returns those alone (a partial return) or the whole file, whether a credit with an unknown outcome waits for verification first, or whether credits refused after the returns file was sent go in a second one. The source the plan learns from returns only the refused credits whose failure has been verified, holds the unverified ones back and counts them, and sends one returns file per payment file. Its line layout is itself marked provisional there.

**Recommendation.** Confirm against the CBUAE WPS rulebook (D10): partial or whole-file return, what happens to a credit whose outcome is unknown, and whether one payment file may have more than one returns file. Record the answer in Annex A and in `uae.wps.table.returns.v1`.

**What the factory does.** It returns the refused credits only. `uae.wps.decide.returns.v1` counts the items whose posting outcome is `rejected` (`uae.wps.table.returns.v1`); a credit whose outcome is unknown is not returned, and the leg does not wait for it. One returns file per payment file. The amount and the line layout sit behind `uae.wps.map.rfa-out.v1`, whose source model is provisional. All of it is `provenance.status: provisional`.

**What the studio does.** WPS-IN-002 to 004 refuse the first of the two credits of `wps-pif-valid.pif` (core banking scenario `reject-first-then-accept`, new) and return it. The test runner's end-to-end level now finds a leg the platform ran in its slot when the test names the leg as its target, so the tests compare the leg's own trace and detail.

**What the source does, in detail** (`Inward/Rfa/InwardRfaGenerationService.cs`, `RfaAmountRule.cs`, `InwardRfaParameters.cs`, workflow `dib-wps-inward-paf.v1` 1.15.0 to 1.19.0).
- **States.** Each employee record (EDR) of a payment file carries a status:
  - 30, initiated: the refusal is not yet verified;
  - 31, verified refused;
  - 32, in a returns file;
  - 33, return acknowledged;
  - 34, return refused.
- **What goes in.** Only status 31. Status 30 is counted and logged as a warning ("await verification and are not included"), never returned.
- **When it runs.** At any of the four points where the file's posting work ends:
  - after restricted-bank posting;
  - after core-banking posting with no DIF;
  - after a DIF ACK;
  - after a DIF NAK.
- **When it is skipped.** With no EDR at 31 the step ends `NOT_REQUIRED` and nothing is sent.
- **How many.** One returns file per payment file ("the only generation of v1.0").
- **Amounts.** Each line is round2(FixedPay + VariablePay), half away from zero. The file total is the sum of the rounded lines, not the rounded sum.
- **The gap.** An EDR still at 30 when the returns file is made is never returned by the flow: its money stays in suspense until an operator acts.

**Recommendation (refined).**
1. **Partial return as the rule.** Return the verified refused credits only; a whole-file return is the case where every credit was refused, not a separate mode.
2. **Verify before deciding.** A credit whose outcome is `unknown` goes through the platform's inquiry first. The leg decides returns only when every credit is known. Do not do as the source does and leave an unverified credit behind.
3. **One returns file per payment file.** A credit found refused after the returns file was sent goes to operations (`p.hold.repair.v1`), until the rulebook says a second returns file is allowed.
4. **The source's amount rule**, as a rule in the factory: line round2, half away from zero, and the total the sum of the lines.
5. **Every entry point.** Run the returns decision after the DIF's ACK and after its NAK too, not only after an accepted DIF.

Items 1 and 3 still need the rulebook (D10). Items 2, 4 and 5 are ours to decide; I recommend we adopt them now.

**Decision (2026-11-22).** The owner adopted the refined recommendation, all five items. Items 1 and 3 are still to confirm against the rulebook (D10).

**What was built.**
- **Partial return as the rule.** `uae.wps.table.returns.v1` returns the verified refused credits only, and names the scope: `partial`, or `whole-file` when every credit was refused. It is not a separate mode.
- **Verify before deciding.** A credit whose posting outcome is `unknown` is asked about at once (`verify-credit`, `p.post.verify.v1`, inside the `forEach`). A credit the inquiry finds not posted (`mismatch`) is refused. While a credit is still unknown, `uae.wps.decide.returns.v1` ends `unverified`, the file goes to operations (`unverified-operations`, `p.hold.repair.v1`) and the leg ends `pending`. No credit is left behind. WPS-IN-006 (core banking scenario `unknown-first-then-not-posted`, new) shows it.
- **One returns file per payment file.** Every credit is known before the returns file is sent, so none is found refused after it. A refusal that reaches the front door later is unmatched and goes to operations (R-05-31). Annex A, A.4.1 says so.
- **The amount rule.** The table's `amount` is `sumWhere(items, refused, round2(fixedPay + variablePay))`: each line rounded to two decimals half away from zero, the total the sum of the rounded lines. `round2` and `sumWhere` are new functions of the expression library (Part 4, section 4.4). The table's examples prove both, one of them with lines whose rounded sum differs from the sum of the rounded lines.
- **Every entry point.** The disbursement report is now a composite, `uae.wps.exchange.report-disbursement.v1` (subflow `uae.wps.sub.report-disbursement.v1`), that awaits the DIF's ACK or NAK. The returns are decided after either. After a NAK the refused credits still go back and the leg ends `pending` (WPS-IN-007, inward simulator scenario `payment-file-disbursement-refused-returns-accepted`, new). A DIF with no acknowledgement ends `pending` without returns, as the source has no entry point there.

## I-40. The agent leg's outcome after a return

**Issue.** The slot `scheme-leg` of `p.wf.inward.v1` offers `settled`, `rejected`, `returned` and `pending`. When some credits of a payment file post and the refused ones are returned and recorded, the spec does not say whether the leg ends `settled` or `returned`, nor whether it ends `returned` when every credit was returned. On a NAK of the returns file the source raises an operator record and does nothing more: the returned amount stays where it is.

**Recommendation.** Decide, in Part 5 or Annex A, which outcome a leg gives for a partial return and for a whole-file return, and say what `returned` means for an inward file.

**What the factory does.** The leg ends `settled` once the return is recorded, whatever the number of returned credits. A NAK hands the returns file to operations (`p.hold.repair.v1`) and the leg ends `pending`, which the platform takes to `review`. A missing acknowledgement, or a return that cannot be recorded, also ends `pending`. `returned` is still not reached by the agent leg.

**What the source does.**
- **ACK.** The returns are recorded (EDRs 32 to 33), then the returned total is posted from the return-suspense account to the nostro account. A failed posting loops through an operator decision (retry, mark as success or abandon).
- **NAK.** The NAK reason is recorded on each EDR (32 to 34), an operator record is raised, and nothing else happens: the funds stay in suspense ("nothing follows the record").
- **Outcome.** The run ends with a state word (RFA-Reversal-Posted, RFA-Nak-Attached), not a business outcome.

**Recommendation (refined).**
- **Partial return.** If some credits were paid and the refused ones were returned and the reversal posted, the leg ends `settled`, with the count and amount returned in `facts.returns`. The file was disbursed; the returns are its exceptions.
- **Whole-file return.** If no credit reached an employee and the whole amount went back, the leg ends `returned`. That is what `returned` means for an inward file.
- **NAK.** The leg ends `pending` (operations, funds in suspense), as the factory does now. That matches the source and keeps the money visible.
- **Reversal posting fails after an ACK.** The repair hold, then `pending`.

Write this into Annex A's leg table and Part 5's outcome list, and add a factory test for the whole-file case.

**Decision (2026-11-22).** The owner adopted the refined recommendation.

**What was built.** R-05-46 (Part 5) and Annex A, A.4.1 state the outcomes.
- **Partial return**: `settled`, with `facts.returns` holding the count and amount returned (WPS-IN-002, WPS-IN-006).
- **Whole-file return**: `returned`, after the switch on the decision `returnScope` (WPS-IN-005, core banking scenario `reject-two-then-accept`, new). This is what `returned` means for an inward file. The agent leg now reaches `returned`.
- **NAK**: the repair hold, then `pending` (WPS-IN-003), as before.
- **The reversal posting fails after an ACK**: `record-return` `rejected` or `unknown` goes to the repair hold (`return-posting-operations`, `return-posting-unknown-operations`), then `pending`.
- The leg's exit writes now name `facts.returns`.

## I-41. Telling the returns file's acknowledgement from the receipt confirmation's

**Issue.** The agent leg awaits `uae.wps.ack` twice: once for the receipt confirmation (PRC) and once for the returns file (RFA). Both awaits correlate by `header.fileId`, the payment file's id, as the dispatch table says (`deliver: { correlateBy: file id }`). The spec does not say which file id an acknowledgement of a returns file carries, its own or the payment file's, or how the front door tells it from a late acknowledgement of the PRC. The source tells them apart by the response's file family (RFA ACK or NAK) and by its time, later than the upload.

**Recommendation.** Confirm what the RFA acknowledgement carries (D10). If it names the returns file, give the returns file its own id in the context and correlate the await by it. If it names the payment file, say in Part 5 that an `await` takes only a message sent after the step that waits began, and add a conformance scenario for it.

**What the factory does.** The `return-credits` subflow awaits `uae.wps.ack` by `header.fileId`, as `confirm-receipt` does. The studio interpreter keeps them apart because the two acknowledgements answer different messages. The wait is one business day, a placeholder like the others (OP-A2); the source waits much longer.

**What the source does.**
- **What it accepts.** The wait (`await_rfa_response`, on the core-bank response signal) accepts only an `RFAACK` or `RFANAK` payload newer than the returns file's upload time (`rfaUploadedAtUtc`). The same rule applies to DIF responses. A PRC- or DIF-cycle response still in state therefore never ends it.
- **Family.** The response's file name carries the family, and the NAK name (31 characters) is re-checked.
- **How long.** The wait parks for up to 2,592,000 s (30 days). A timeout ends the run with `SIGNAL_TIMEOUT`.
- **Before the upload.** An upload-delay gate may hold the upload for 0 to 10 minutes (`RfaUploadMaxHoldMinutes`).

**Recommendation (refined).**
1. **Tell them apart by family, not by time.** The intake map reads the acknowledged file's family from its name. It gives the acknowledgement its own message type: `uae.wps.ack` with `header.acknowledges` set to `prc`, `dif` or `rfa`. Each await then correlates on the file id and on that family. This is deterministic and does not depend on clocks.
2. **Also state the time rule as a guarantee** in Part 5: an `await` takes only a message received after the await began. Add a conformance scenario for it, with a late PRC ACK arriving during the RFA wait. That covers schemes whose acknowledgements cannot be told apart.
3. **Wait length.** Make it a parameter of the returns exchange, with the source's 30 days as the default, bounded 1 to 30 days, on the business calendar. Do not hard-code the factory's one-day placeholder.
4. **Upload hold.** Model the upload hold as a parameter of the send (0 to 10 minutes, default 0), not as code.

**Decision (2026-11-22).** The owner adopted the refined recommendation, all four items. What an RFA acknowledgement carries is still to confirm (D10).

**What was built.**
- **Family, not time.** The dispatch entry of `uae.wps.ack` declares the families the front door reads from the acknowledged file's name: `sif`, `rfr`, `prc`, `dif`, `rfa` (`deliver.families`, schema `scheme`). Each subflow that awaits `uae.wps.ack` names its family (`await.family`, schema `workflow`): `submit-salary-file` `sif`, `request-refund` `rfr`, `confirm-receipt` `prc`, `report-disbursement` `dif`, `return-credits` `rfa`. The WPS simulators send each ACK and NAK with its family (`family` on a simulator step). An await takes only a message of its family, or one whose family could not be read (R-05-44). The family is carried on the message as `family`, not as `header.acknowledges`: the context's header is the payment's, and the message is the front door's to classify. Lint L36 holds an await's family to its dispatch entry (mutation M32). Conformance CK-AWAIT-07: a late reply of family `a` arrives while the second exchange waits for family `b`, and is not taken.
- **The time rule, as a guarantee.** R-05-43 (Part 5). Conformance CK-AWAIT-06: a stale second reply to the first exchange arrives between two exchanges on the same message, and the second exchange does not take it; it is recorded as unmatched. The requirement is narrower than "only a message received after the await began", which would break R-05-30 (CK-AWAIT-04, and the PSI, which can arrive before the leg starts): Issue I-50.
- **Wait length.** `uae.wps.exchange.return-credits.v1` declares `replyWait`, a `duration` parameter, default `P30D`, bounds `P1D` to `P30D`, on the business calendar (`policy.replyCalendar: business`); the subflow's await names it (`timeout: { parameter: replyWait }`). The one-day placeholder is gone. The example bank sets `P10D`, so WPS-IN-004 waits two weeks; `studio/test/wps-returns.test.ts` shows the default, six weeks of elapsed time. On the business calendar the default is longer than the source's 30 calendar days: Issue I-51.
- **Upload hold.** `uploadHold`, a `duration` parameter of the same composite, default `PT0S`, bounds `PT0S` to `PT10M`; the subflow holds the upload with `wait: { parameter: uploadHold }` (detail `return-credits.upload-hold:PT0S`).
- **Parameters in a subflow's timing** (R-05-45). A wait, or an await's timeout, may name a `duration` parameter of the subflow's composite. The expansion names it in full and carries its default (`tools/expand.py`, `studio/src/core/expand.ts`); the studio interpreter and the engines' shared interpreter (`studio/engines/shared/Interpreter.cs`) take the tenant's value in force on the business date the step starts, else the default. Lint L17 holds the name to the composite's parameters (mutation M33). Lint L14 now compares a duration with its bounds by length: `P100D` sorts before `P30D` as text (mutation M34).
- **The kit** has 50 scenarios. It passes 50 of 50 on the studio interpreter, DTFx, Elsa and Temporal, and on Flowable since I-49 was done.

## I-42. Who keeps the SQL Server plugin for Temporal, and how an upgrade is proved

**Issue.** D2 is confirmed and the plugin is built (`runtime/temporal-mssql`, W5). It is pinned to Temporal v1.32.2. Temporal does not support it, and Temporal releases often: the persistence schema is at its 20th version, and a release can change a store method or add a table. The plan says to re-prove the plugin on each upgrade, but it does not name an owner, say how soon after a Temporal release the runtime must follow, or say what blocks an upgrade.

**Recommendation.** Name an owner for `runtime/temporal-mssql`. Make a Temporal upgrade one change that moves the pin, ports any new schema version and store method from the PostgreSQL plugin, and passes the CI job `temporal-sqlserver` (persistence suites, durability check, kit, WPS). Say in the plan that the runtime stays on the pinned release until that job is green, and that a security release of Temporal is the only reason to hurry.

**What is built.** The pin is exact in `go.mod`, and the README says each upgrade must re-run the proof. The CI job runs the whole proof on every push to `runtime/**`.

**Decision (2026-11-22).** The owner of `runtime/temporal-mssql` is the team that owns this GitHub repository. An upgrade of Temporal is one change by that team that moves the pin, ports any new schema version and store method from the PostgreSQL plugin, and passes CI job `temporal-sqlserver`; until it is green the runtime stays on the pinned release. `runtime/temporal-mssql/README.md` records it.

## I-43. Where a bank pack may live

**Issue.** The owner decided on 2026-11-21 that a real bank's configuration lives in this repository, in `content-configurations/banks/<bank>/` (D5 as amended in `docs/runtime/NSTAR-CONVERGENCE-PLAN.md` section 8). The spec knows one bank pack only, the example bank in `spec/examples/bank/`. It says nothing of a bank pack anywhere else: which folders hold one, how the studio finds its tenant, or which documents in a bank's folder are studio artefacts and which are the runtime's own (port bindings, configuration values). `tools/validate.py` reads `spec/` only, so it never sees a bank's folder.

**Recommendation.** Add a requirement to Part 6 (or Part 11): a bank pack MAY live in `content-configurations/banks/<bank>/`, with a `pack.yaml` of `packType: bank` that names its tenant; every studio artefact in the folder is that tenant's; the documents of kind `port-bindings` and `configuration-values` are the runtime's and are not pack artefacts. Its check: the studio loads such a folder and refuses a document that names another tenant, and the pack's lock file equals the one the studio generates. Give it an id, a line in the check catalogue of `design/05-workflow-dsl.md` and a mutation case, as `spec/CLAUDE.md` requires.

**What the studio does.** It loads a bank pack from its own folder on request, never by default: `openEnv({ banks })`, `TH_BANKS`, or `--bank <name|dir>` on any command. (Since I-44 the folder is read from the content branch and named by its folder name; the example bank's pack is seeded there and always loaded.) `Store.loadBankPack` takes the tenant from `pack.yaml`, refuses a document that names another tenant, and leaves the runtime's formats out. The pack is then checked as the example bank is (schemas, bounds, hook contracts, bindings, pins), its tests run on the interpreter, and `npm run studio -- bank-pack <bank> --write` writes its lock file. `studio/test/bank-packs.test.ts` shows it on the first such bank.

**Found, not fixed.** The example bank's lock file (`spec/examples/bank/pack.yaml`, written by `tools/build_factory.py`) depends on `p`, `uae.p` and `uae.wps`; the studio's `packManifest` derives `p` only, because it skips the references of tests. `compareManifest(env, 'b')` has reported that difference since before this change.

**Decision (2026-11-22).** The owner adopted the recommendation: a bank pack MAY live in `content-configurations/banks/<bank>/` with a `pack.yaml`, loads only on request, and the spec's own validator checks such folders.

**What was built.**
- **R-11-28** (Part 11) states it: the folder, the tenant, the runtime's documents left out, the lock file, and loading only on request (the example bank's seeded pack is the one always read).
- **Lint L35** in `tools/validate.py` reads every folder of `content-configurations/banks/` beside `spec/`: its `pack.yaml` is a valid bank pack that names its tenant; every studio artefact in the folder (outside the studio's own `artefacts/`, `configuration-sets/` and `scripts/`) is schema-valid, names no other tenant, is in scope `b` or is a test, and refers only to the factory or the folder; and the lock file lists every artefact by kind, release, hash and path, and nothing else. Mutations M29 (a document names another tenant), M30 (an artefact changes and its lock file is stale) and M31 (`pack.yaml` is not a bank pack) prove it; `tools/mutation_tests.py` now copies the bank folders beside `spec/`. `banks/dib/` and `banks/example-bank/` pass.
- **The note above is fixed.** The studio's `packManifest` now counts the references of a bank pack's tests, which are in its lock file, and `tools/build_factory.py` derives the example bank's dependencies the same way instead of naming them. Both give `p` for the example bank, and `p`, `uae.p` and `uae.wps` for the bank in `banks/dib/` (its tests run WPS). `compareManifest(env, 'b')` is equal, and `studio/test/bank-packs.test.ts` now asserts it whole.
- `Store.reload` no longer asks for the example bank's folder by name, so `seed-content --write-local` can regenerate the example bank's `pack.yaml`.
- **The bank's hooks.** `banks/dib/` carries the employer AML check and the labour ministry notification complete (hook, activity, script-behind in `banks/dib/scripts/`, port, simulator, tests) and switched off: the shipped `bindings` is empty. `studio/test/dib-hooks.test.ts` binds them in a test-only configuration and runs a salary file end to end (cleared, refused, ministry refusing), and fails if either is bound in the shipped configuration. `banks/dib/README.md` says how to switch them on.

## I-44. The content branch as the studio's only store

**Decision.** The owner decided on 2026-11-21 that the studio reads only from the GitHub branch's `content-configurations/`, with no local copy and no cloud copy: a branch per change set, the factory published into the content, state as append-only files on the branch, and memory only through the GitHub API. The spec now says so in R-09-19 to R-09-22 (amended) and R-11-08, R-11-12, R-11-20 (amended) and R-11-21 to R-11-25 (new), each checked by `studio/test/content.test.ts` or `studio/test/no-disk-write.test.ts`.

**Issue 1. Two state files are replaced.** Rule 3 says state is append-only and no published file is rewritten. It also says the users and roles are one file, changed through maker-checker. One file that changes is a file that is rewritten. The studio settings (publish mode, engine target, environment) are the same.

**Recommendation.** Keep the two as the only files the studio replaces: `governance/users.yaml`, through maker-checker (a change set whose branch holds the new file; a different shared checker approves; one commit replaces it, with the change set record and its audit), and `governance/settings.yaml`, by a shared checker or service operations, with its event. Each replacement must find the file as the studio read it, or it stops. Git keeps every earlier version. If the owner wants these append-only too, version them (`governance/users/<n>.yaml`, the latest applies) as the in-flight counts are.

**Issue 2. Which change set a draft joins.** The spec says a change set is the closure of drafts that refer to one another (R-11-19), but a branch must exist from the first save, before the closure is known. A draft can later come to refer to drafts on two branches.

**Recommendation.** A new draft joins the open change set of the first draft (by change set id) that it refers to or that refers to it, else a new one; this is what the studio does. Approval publishes the closure, across branches if it must, under the id of the requested draft's branch. A branch that still holds drafts after a publish keeps them; they publish later under a new id. Say this in Part 11, or let a designer name the change set when she saves.

**Issue 3. Waivers "with their change set".** A conformance waiver is approved by one checker and belongs to no draft.

**Recommendation.** A waiver is its own change set: one commit with a change set record that holds the waiver (`waivers:`) and its audit. This is what the studio does. If a waiver should instead ride with the release it unblocks, say so in Part 11 and the release gate.

**Issue 4. What still comes from disk.** The factory's script-behinds (`studio/scripts/`) and the studio's own sample fixtures (`studio/fixtures/samples/`) ship with the studio's code; the schemas and the conformance kit come from `spec/`. The spec does not say whether a factory script-behind is content.

**Recommendation.** Treat them as the studio's code, as now, or publish the factory's scripts with the seed into `content-configurations/scripts/` (where a designer's scripts already go). A designer's scripts and samples are files on the branch.

**Issue 5. A factory source that changes at the same release.** The seed never rewrites a file on the branch, so a change to a factory artefact without a new release stops the seed (R-09-21). `tools/build_factory.py` today rewrites factory files at release 1.0.0.

**Recommendation.** A factory change after the seed needs a new release, as any published change does. For this repository's own copy, `npm run studio -- seed-content --write-local` regenerates `content-configurations/` from `spec/` (it records a new seed change set); a connected branch is reseeded only with new releases.

**Issue 6. Reading cost and old branches.** The studio reads every audit file and every `studio/` branch at start. Both grow: one audit file per event, and a published change set's branch stays (the studio uses no endpoint that deletes a ref). `compare` skips a branch the content branch has taken in, so an old branch costs one request.

**Recommendation.** Let the repository delete a branch when its pull request merges, and allow the studio to delete a change set branch it published (one more endpoint). Read the audit on demand rather than at start once it is large.

**Accepted.** The owner accepted the six Recommendations on 2026-11-22. The spec says so in R-11-22, R-11-23, R-11-24 and R-11-25 (amended) and R-11-26 and R-11-27 (new).

**What is built.**

1. `governance/users.yaml` (through maker-checker) and `governance/settings.yaml` are the only files the studio replaces; each replacement must find the file as the studio read it.
2. A new draft joins the open change set of the first draft, by id, that it refers to or that refers to it. Approval publishes the closure under the id of the approved draft's branch, with the audit of every other branch it empties. A branch that keeps drafts after a publish keeps them, and they publish later under a new id. Each change set record names each member's maker.
3. A waiver is its own change set: one commit, a record with `waivers:`, and its audit.
4. The seed publishes the factory's script-behinds (`studio/scripts/` to `scripts/`) and its test samples (`studio/fixtures/samples/` to `samples/`). The studio reads them from the branch; a sample the seed published is factory content and a designer may not take its name. The import's training bundle is built from the branch too.
5. A factory change after the seed needs a new release: the seed stops and names the file. `seed-content --write-local` regenerates this repository's copy.
6. The studio deletes a change set branch once the content branch holds everything on it: in commit mode right after the publish, in pull-request mode when it next reads the branches. The client deletes only a `studio/CS-<date>-<n>` branch and refuses any other name before a request is made; the commands that only read delete nothing. The audit files are read when the audit is opened, except the events the state depends on (a retirement, a configuration set: an event file's name now carries its action) and the newest event, whose hash the next one chains to.

Also: a commit sends each file's text inline in its tree request, so a seed of several hundred files is a few requests, not one per file; and the client lists `studio/` branches without a trailing slash in the path, which some proxies refuse. `studio/test/content.real-github.test.ts` runs the whole flow on a real repository with `TH_REAL_GITHUB=1`.

## I-45. Id length on SQL Server is in bytes, not characters

**Issue.** Temporal's key columns for ids (workflow, run, task queue, signal, timer) are VARCHAR(255). On MySQL and PostgreSQL that is 255 characters. On SQL Server the plugin needs a UTF-8 collation to keep the keys under the 900-byte limit, so VARCHAR(255) is 255 bytes: an id with non-ASCII characters is shorter. The spec does not say which characters a workflow id may hold.

**Recommendation.** State in Part 5 that workflow ids, business ids and task queue names are ASCII, at most 255 characters. The runtime already builds them from references, so this costs nothing and keeps every engine and database the same.

**What is built.** The development dynamic config sets `limit.maxIDLength` to 255. An id over 255 bytes is refused by SQL Server on write.

## I-46. Text search attributes on SQL Server are not indexed

**Issue.** Temporal's Text search attributes use full-text search: a GIN index on PostgreSQL, FULLTEXT on MySQL. SQL Server's full-text search is a separate component that the Linux container image does not include, and that a bank's DBA may not enable. The plugin matches Text with `STRING_SPLIT` over the rows the other filters leave, which is correct but not indexed. The spec does not say whether operators search payments by free text in Temporal's visibility.

**Recommendation.** Do not rely on Text search attributes for operator search. Keep search on Keyword attributes (indexed on every database), and use the runtime's own payment store for free-text search. A bank that needs Text search in Temporal at scale keeps visibility on Elasticsearch.

**What is built.** Keyword, Int, Double, Bool, Datetime and Keyword List attributes are indexed on SQL Server. Text is matched without an index and passes Temporal's visibility suites.

## I-47. What a bank's SQL Server must allow for Temporal

**Issue.** The plugin needs each Temporal database created with the collation `Latin1_General_100_BIN2_UTF8` (SQL Server 2019 or later) and with `READ_COMMITTED_SNAPSHOT ON`. Some banks fix the collation of every database by policy, and some DBAs do not allow row versioning in tempdb. The deployment guide does not list these as prerequisites.

**Recommendation.** List them in the installation prerequisites: SQL Server 2019 or later (2022 is tested), two databases for Temporal with that collation and snapshot reads, a schema user with `db_owner` for `temporal-sql-tool`, and a service user with read and write only. Ask each bank's DBA before the installation is planned.

**What is built.** `temporal-sql-tool create-database` sets both. The server refuses to start on a database with another collation, with a message naming the one it needs.

**The rights, as tested (2026-11-22).** `runtime/temporal-mssql/scripts/least-privilege.sh` runs Temporal as a bank would, with two logins:

| Who | Server level | In each of the two Temporal databases (`temporal`, `temporal_visibility`) | Used for |
| --- | --- | --- | --- |
| The admin (the bank's DBA) | Enough to create a database (`dbcreator`) or an existing pair of empty databases it owns | `db_owner` | `temporal-sql-tool create-database`, `setup-schema`, `update-schema`, and each Temporal upgrade's schema change. Not used by the running server. |
| The service login (the server's) | None: no server role, no server permission beyond `CONNECT SQL` | A user for the login, member of `db_datareader` and `db_datawriter`, and `GRANT EXECUTE` on the database | Everything the server does at run time |

The grants, per database: `CREATE USER [svc] FOR LOGIN [svc]; ALTER ROLE db_datareader ADD MEMBER [svc]; ALTER ROLE db_datawriter ADD MEMBER [svc]; GRANT EXECUTE TO [svc];`. The trigger that keeps the visibility keyword lists runs under the table owner's rights, so the service login needs none of its own. The script proves the login holds no server role, is neither `db_owner` nor `db_ddladmin`, and is refused a `CREATE DATABASE`, a `CREATE TABLE`, an `ALTER TABLE` and a read of another database; with it, the durability check passes (a workflow whose timer was running completes after a SIGKILL restart of the server), and so do Temporal's persistence suites with every store under test connecting as the login and the admin creating, setting up and dropping each test database (`SQLSERVER_ADMIN_USER`, `tests/sqlserver_test_util_test.go`). CI job `temporal-sqlserver` runs both, and the WPS salary file runs on the server as that login.

## I-48. The conformance kit on a real Temporal server waits in real time

**Issue.** The Temporal harness runs on Temporal's time-skipping test server, which keeps no state in a database. With `TH_TEMPORAL_ADDRESS` it runs on a real server, whatever its database, in real time: business time is the server's time plus an offset. Six scenarios wait a long business time. On the server on SQL Server their open timers were: CK-AWAIT-02 three days, CK-LOOKUP-01 two days, CK-BLOCK-03 four hours, CK-WAIT-01, CK-AWAIT-03 and CK-BLOCK-02 one hour. With the harness's time limit (`TH_KIT_TIMEOUT_MIN`, 15 minutes) they cannot finish, so the whole kit cannot be shown in one run on a server that keeps its state in SQL Server, or in PostgreSQL. The whole kit one after another takes more than two hours even without them. Temporal v1.32.2 has a per-execution time-skipping option (`frontend.WorkflowTimeSkippingEnabled`), but it keeps one clock per execution, and the harness's front door and root workflows share one business clock.

**Recommendation.** Decide what proves an engine's persistence. Either accept the kit on a real server without the scenarios whose business waits are longer than a few minutes, since the time-skipping server covers their logic and the persistence suites cover the store; or give the harness a business-clock scale for real servers (one business hour to one real second, say), applied to every timer and to the front door, and add a kit check that the scale changes no trace.

**What is built.** `runtime/temporal-mssql/scripts/kit-on-sqlserver.sh` runs the kit on several servers on SQL Server side by side. The CI job `temporal-sqlserver` runs it without the six long-wait scenarios, and says so. `runtime/temporal-mssql/README.md` gives the result of the full run, scenario by scenario.

## I-49. The Flowable harness does not apply R-05-43 to R-05-45

**Issue.** The studio interpreter and the engines' shared interpreter (DTFx, Elsa, Temporal) apply the time rule (R-05-43), the families (R-05-44) and durations from parameters (R-05-45). The Flowable harness (`studio/engines/flowable/`) translates the expanded form to BPMN and has its own front door in Java. It was not changed here: Flowable belongs to parallel work, and it was not run. Its front door has no family and no block start, and its BPMN timer reads a literal duration, so CK-AWAIT-06 and CK-AWAIT-07 are expected to fail there, and the WPS returns subflow would not translate (`{ parameter }` is not a duration).

**Recommendation.** In the Flowable binding: carry `family` on the catch's annotation and compare it in `frontDoor` and `deliverHeld`; record the start time of each block's sub-process and drop a held message older than it once an earlier catch of the same message has fired; and resolve a `{ parameter, default }` duration from the deployment's values before the timer is written. Then run the kit and expect 48 of 48. Until then the release gate (R-C-05) is red on Flowable for these two scenarios, and `studio/test/blackbox/engines.test.ts` (opt in, `TH_ENGINES=1`) expects 48 of 48 there, so it fails until then.

**Done (22 November 2026).** The recommendation is applied in the generator, the kit harness and the REST binding. The kit passes 48 of 48 on Flowable.
- **Families (R-05-44).** `src/export/bpmn.ts` writes the await's `family` on `th:await`. The kit harness (`engines/flowable/harness`, `Harness.waiting`) and the REST binding (`FlowableBinding.Waiting`) deliver a message only to a catch whose family it fits, or a message with no family, in the front door and when a held message is delivered. CK-AWAIT-07 passes.
- **The time rule (R-05-43).** The binding records when each composite step's sub-process began: the kit harness as the block starts, the REST binding at a new point, `th:point kind="blockStart"`, which the generator writes at the start of each block for a REST binding only. Each instance keeps the messages its awaits have waited for. When a held message is about to go to a catch and an earlier await of the instance has waited for that message, a message received before the innermost block began (or before now, outside a block) is not delivered: it is counted as unmatched, with a note. CK-AWAIT-06 passes.
- **Durations from parameters (R-05-45).** A `{ parameter, default }` wait or await timeout was written as `[object Object]` and only failed when the timer ran. The generator now writes the timer with the default and marks it `th:timer parameter default` (with `calendar` when the await says so); it refuses a duration that is neither an ISO 8601 duration nor a parameter named in full with a default. The binding places the timer by the tenant's value in force on the business date the timer was created, else the default, and the wait's detail entry names the value the timer took. The kit harness reads the values from the plan (`values`, from the configuration set and the scenario); the REST binding from the deployment's configuration. `portability.ts` warns at each such step, and still reports every factory workflow portable. No kit scenario has a duration parameter: a hand-made variant of CK-AWAIT-07 (a hold from a parameter) and of CK-BLOCK-02 (a timeout from a parameter on the business calendar) gives the studio interpreter's trace, detail and elapsed time on both bindings. `test/export.test.ts` checks the marks in the BPMN of `uae.wps.wf.agent-payment-in.v1`.

## I-50. "Only a message received after the await began" contradicts R-05-30

**Issue.** I-41 asks Part 5 to say that an await takes only a message received after the await began. R-05-30 says a message delivered before its await starts is kept and consumed by that await (CK-AWAIT-04). Both cannot hold. The reply to a send can arrive between the send and the await, and the PSI can arrive before the agent leg starts. The source's rule is narrower: a response newer than the upload, that is after the exchange began.

**Recommendation.** Keep R-05-30, and state the time rule as R-05-43 does: once an earlier await of the same instance has waited for the same message, an await takes only a message received after the composite step that holds it began; the first await of a message takes one kept since the instance began. This is what the interpreter and the engines do, and what CK-AWAIT-06 proves. If the owner wants the rule as worded, R-05-30 must change and CK-AWAIT-04 go.

**Decision (2026-11-22).** The owner accepted the narrower time rule as built: R-05-30 stays, R-05-43 states the rule, CK-AWAIT-04 and CK-AWAIT-06 prove both. Nothing more is to be built.

## I-51. Thirty days, or thirty business days

**Issue.** The source waits 2,592,000 s, 30 calendar days, for the RFA acknowledgement. The decision asks for a wait of 30 days by default, bounded 1 to 30 days, on the business calendar. On the UAE business calendar 30 business days are about six weeks.

**Recommendation.** Keep the parameter on the business calendar, as decided, and confirm the default with the rulebook (D10). If the scheme counts calendar days, 30 of them are about 22 business days: set the default to `P22D`, or let the parameter carry its own calendar.

**Decision (2026-11-22).** The owner decided that the reply wait defaults to **30 calendar days**, as the source waits (2,592,000 s). The bounds stay 1 to 30 days.

**What is built.** `uae.wps.exchange.return-credits.v1` takes its wait the way the salary file's acknowledgement does (R-05-47): `policy.replyTimeout: P30D`, `policy.replyCalendar: elapsed`, and the bank's parameters `replyWait` (default `P30D`, bounds `P1D` to `P30D`) and, new, `replyWaitCalendar` (default `elapsed`, allowed `elapsed` and `business`). The subflow's await no longer fixes its timeout or calendar; the expanded await names both parameters. Annex A, A.4 says so. The example bank keeps its ten business days by setting both values, so WPS-IN-004 is unchanged. `studio/test/wps-returns.test.ts` shows the default, thirty days of elapsed time, and the business calendar on request; `studio/test/export.test.ts` the Flowable timer's marks.

## I-52. On Flowable an await counts as having waited only once it ends

**Issue.** R-05-43 turns on whether an earlier await of the instance has waited for the same message. The interpreter marks the message when the await starts. The Flowable bindings (I-49) mark it when the await ends, at its received or timeout point: the REST binding has no listener, and it sees a catch only when a held message is about to go to it. The two differ only for an earlier await that was cut off before it ended, by cancel, a deadline or a failing scope, and then only if the instance goes on to await the same message again. No kit scenario, and no factory workflow, does that.

**Recommendation.** Accept the difference while no flow can reach it. If one can, generate a `th:point kind="awaitStart"` before each event-based gateway for a REST binding (and mark the message at the catch's start in the kit harness), and add a conformance scenario in which a cancelled exchange is followed by another on the same message.

## I-53. The Flowable REST server runs on the binding's clock, with its async executor off

**Issue.** Plan D3 runs Flowable as its own server, worked over REST. The REST server has no clock a client can move. With its async executor on, it fires timers and runs async continuations in real time, so an attempt timeout, a backoff or an await timeout of days takes that long, and the trace depends on how fast the workers answer. The kit and the WPS run need the engine's own time control (R-C-06). The runtime's binding (`runtime/src/TransactHub.Engine.Flowable`) therefore needs the server started with `FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false`: it runs every executable job itself, and moves a timer to an executable job when its virtual clock reaches the timer's due time. That server cannot be shared with workers or executors that expect real time, and a payment waits only while the binding runs. A production installation needs the async executor on and real time.

**Recommendation.** Keep the executor off for the kit and the deploy runner, which prove the flow. For production, decide whether the binding (with its clock) or the server's async executor drives jobs and timers, and add a run of the WPS e2e with the executor on and short real waits, to show the same trace. Until then, document that a Flowable installation's REST server is the binding's alone.

**What is built.** The binding keeps the virtual clock and fires timers by the model's duration (I-55 for the business calendar). The e2e test and the runtime tests start the server with the executor off. Each run deploys in a tenant of its own and deletes its deployment afterwards (`TH_FLOWABLE_KEEP=1` keeps it).

**Production mode (22 November 2026).** Decided: in production the server's async executor drives jobs and timers, in real time, and the binding is a pure external worker. `TH_FLOWABLE_MODE=production` (`FlowableOptions`) runs it so: the binding acquires, locks, completes and fails jobs and throws BPMN errors; Flowable runs its own jobs, fires its timers, retries a failed job and hands out a job whose lock expired (I-54); the binding places the timers it must compute (I-55) and reports every dead letter. Business time is the wall clock shifted to the plan's evaluation date. Two completions of one instance are let take effect one after the other, so parallel branches give the virtual run's trace. The virtual mode stays the default for the kit, the deploy runner's tests and the store matrix. Shown by `FlowableProductionTests` (runtime, a server with its executor on) and `studio/test/blackbox/e2e-wps-flowable-production.test.ts`: the WPS salary file settles on the store on PostgreSQL and SQL Server, with the bank's acknowledgement wait set to two minutes; a reply that never comes times out on a fifteen-second real timer and gives the trace of the virtual run; CI job `flowable-production`. The server need not be the binding's alone in production mode; it still needs a tenant per run in the tests. What is still open is I-89.

## I-54. A failed external job comes back only when the async executor resets its lock

**Issue.** Flowable's external worker API fails a job with `POST /external-job-api/acquire/jobs/{id}/fail` and a number of retries left. The job's lock owner is cleared, but its lock expiry is set, and only the async executor's expired-lock reset hands the job out again. With the executor off (I-53), a failed job with retries left is never acquired again, and `unacquire` is refused once the worker no longer holds the lock. Shown by `FlowableRestTests` on Flowable 7.1.0.

**Recommendation.** Treat a worker that does not answer as a technical failure inside the binding: retry the call while the job's retries last, and fail the job with no retries left when they are spent, so it becomes a dead letter an operator sees. This is what the binding does. If the async executor is turned on for production (I-53), use the server's retries instead and remove the binding's loop.

**What is built.** `FlowableBinding` retries a worker call up to the job's retries, with a short back-off, and then fails the job with none left. A BPMN error (an activity's retryable or fatal error) is unchanged: it goes to the model's error boundary, as on the embedded engine.

**Production mode (22 November 2026).** With the executor on, the binding uses the server's retries: a worker that does not answer fails the job with one retry fewer and a retry timeout (`TH_FLOWABLE_RETRY_WAIT`), the server's expired-lock reset hands it out again, and the binding takes it as the same attempt (a technical failure is not an attempt). With no retries left the job is a dead letter; the binding reports it with its step (`engine.deadLetters`, a note) and marks the payment failed there. A job held by a worker that crashed comes back once its lock (`TH_FLOWABLE_LOCK`) expires. Shown on Flowable 7.1.0 with the server's reset interval at one second: in the runtime tests, and in the WPS salary file with a crash at `post` (it settles) and a `post` worker that never answers (a dead letter on SQL Server). The binding's own loop stays for the virtual mode only.

## I-55. The REST server has no business calendar

**Issue.** An `await` with `calendar: business` is generated with `flowable:businessCalendarName="business"`. The kit's embedded engine registers that calendar. The Flowable REST server has only its standard calendars, so the timer would fail when it is created. Adding a calendar to the server means adding Java to it, which D3 does not foresee.

**Recommendation.** Keep the calendar in the binding. Confirm that a bank's business calendar (weekends and holidays) belongs to the binding for every engine, as it already does for Temporal, DTFx and Elsa.

**What is built.** For a REST binding the generator leaves the calendar name out and marks the timer `th:timer calendar="business"`. The binding fires that timer at the model's duration counted in business days (`Duration.Add`, the interpreter's calendar).

**Production mode (22 November 2026).** Chosen: a due date the binding computes, not a business calendar bean. The binding sees each timer marked `th:timer` (the business calendar, or a duration or calendar from a parameter, R-05-45 and R-05-47) when the server creates it, computes its due date as the virtual mode does, and reschedules it (`POST /management/timer-jobs/{id}`, action `reschedule`). The calendar and the dated values stay in the binding, as on every other engine, and the stock image needs no Java. The window it leaves: a timer the binding has not yet seen runs on its written default, so a timer fires at the default only if the binding is down for the whole of it. A business calendar bean would close that window, at the cost of a server image of the installation's own and a second copy of the calendar. Shown by the runtime tests (one business day from a Friday placed on Monday; a parameter wait of three seconds in place of a day) and by the WPS run, whose acknowledgement wait of one business day is placed at the bank's two minutes.

## I-56. A parallel join waited for one token per exit of a branch

**Issue.** The generator joined a parallel step by flowing every exit of every branch into the join gateway. A branch with a handled outcome that continues has more than one exit, and a BPMN parallel join waits for a token on each incoming flow. The corporate workflow's screening (sanctions and fraud, each with a handled outcome) had five incoming flows for two branches, so it never joined on Flowable: the payment stopped after `fraud:clear`. No kit scenario has such a branch, so the kit on Flowable did not see it.

**Recommendation.** Add a conformance scenario with a parallel branch whose handled outcome continues, so every engine is checked on it. The kit is in `spec/conformance`; that change belongs with the spec work.

**What is built.** The generator merges a branch's exits with an exclusive gateway before the join (`src/export/bpmn.ts`, `parallel`). The WPS salary file now settles on Flowable, and the kit still passes 46 of 46 on the embedded harness.

## I-57. A wait a bank sets is read differently on each engine

**Issue.** The acknowledgement wait of the salary file is now the bank's parameter (`uae.wps.exchange.submit-salary-file.v1#ackWait` and `#ackWaitCalendar`, R-05-47). The expanded await names the parameter and keeps the policy's value as its default. The studio's interpreter looks the value up on the day the await starts, as it does any parameter. DTFx, Elsa and Temporal read it from the configuration values the deployment hands the engine (`config.values`), on the engine's day: a value published after a payment started reaches its await only if the deployment carries it. Flowable's BPMN timer is written with the default and marked with both parameters (`th:timer parameter calendarParameter`); its bindings place it by the deployment's values, on the virtual clock, as for a parameter timeout (R-05-45). The spec is silent on how an engine's orchestration reads a dated value, since until now only activities did.

**Recommendation.** Decide that an engine reads a dated value for a timer through an activity of its own (a lookup, retried and recorded like any read), so the value comes from the runtime's store as of the step, on every engine. On Flowable, set the timer from a process variable that lookup sets, in place of the binding's own placement.

**What is built.** The interpreter, the shared C# interpreter (DTFx, Elsa, Temporal), both expanders, and on Flowable the BPMN export, the kit harness and the REST binding (`FlowableBinding.VirtualDue`). Lint L30 checks that the parameter is declared, typed and defaults to the policy. `studio/test/ack-wait.test.ts` shows the late acknowledgement waited for with the bank's 72 elapsed hours and not with the default.

## I-58. A bank's accounts are not in the source

**Issue.** The accounting template now has parameters for the settlement, suspense, charge and VAT accounts (R-12-12). For DIB the source holds only code fallbacks (`999000000000001` to `...005`, `DibWpsActivities.Independent.cs`), not the bank's accounts, and it seeds no charge slab. Carrying the fallbacks would present invented accounts as the bank's.

**Recommendation.** Ask the bank for its WPS settlement (nostro), suspense, charge income and VAT accounts and its charge slabs, and set them in `content-configurations/banks/dib/configuration-set.yaml` with a validFrom. Until then a DIB posting uses the factory's placeholder accounts, which no core banking knows, so a deployment for the bank must set them.

**What is built.** DIB's transaction codes, charge narration, account kinds, VAT and acknowledgement wait are set; its accounts and slabs are not, and its README says so.

## I-61. A branch is deleted without a check on GitHub's side

**Issue.** GitHub's delete-ref endpoint takes no expected SHA. The studio reads the change set branch's head and deletes the branch only if it is where the studio saw it, but a push between that read and the delete is lost with the branch. The spec says the studio never loses a commit it did not make.

**Recommendation.** Accept the window: a change set branch is the studio's own, and nobody else is meant to push to it. A bank that wants a guarantee protects `studio/*` from pushes by people (a ruleset that allows only the studio's identity), so that only the studio writes there.

**What is built (22 November 2026).** The studio reads the branch's ref again right before each delete, when a publish empties the branch and when a read finds it taken in, and refuses the delete if the branch has moved since the studio saw it. The branch is kept, with what was pushed to it; the publish's result (`keptBranches`) and the git page name it, with the commit the studio expected and the one it found. `studio/test/delete-ref.test.ts` pushes to the branch while the studio publishes it and shows the branch kept, with the pushed file, before and after a restart. **What remains:** the moment between that read and the DELETE request. A push in it is lost with the branch, and the studio cannot see it, because GitHub's delete-ref (REST and GraphQL alike) takes no expected SHA. The ruleset above closes it.

## I-62. How a pull request is merged

**Issue.** In pull-request mode the studio finds a merged change set when the content branch has taken its branch in (a merge commit), or when nothing on the branch differs from the content branch (a squash or a rebase merge). A squash or rebase merge also rewrites the commit that carries the change set, so the content branch's history no longer shows the studio's own commit (R-09-20), and a reviewer who edits the files on merge publishes what no checker approved.

**Recommendation.** Ask each bank to allow only merge commits on its content branch, and to require that the pull request's head is not changed after approval. The studio then reads every published change set as the commit its checker approved.

## I-63. What the studio still reads from its own folder

**Issue.** With the factory's scripts and samples on the branch, the studio still reads from `studio/` at run time: the code-set registries (`studio/registries/`), the conformance kit's library and scenarios (`studio/conformance/`), and the Python harness. R-11-21 lets the kit be read from `spec/`; it does not say whether the registries are content, and a registry release is looked up by date like any other value.

**Recommendation.** Treat the conformance kit and the harness as the engines' test fixture and the studio's code, as now. Decide whether the code-set registries are content: if they are, publish them with the seed (`registries/` on the branch) and read them from there; if not, say in Part 11 that they ship with the studio's code.

## I-64. Large commits and GitHub's limits

**Issue.** GitHub limits how many objects a client creates a minute and an hour (its secondary rate limits) and how large one request may be. A commit with one blob request per file, as the seed was, trips the first limit on a real repository. With the text inline in the tree request, the seed is a few requests, but GitHub does not publish the largest request it takes, and the studio does not page a tree it reads (`truncated`).

**Recommendation.** Keep the text inline and the tree in parts of at most 200 files and about 1 MB, as built. Test the seed and a large import on a real repository before a bank's first use, and raise an Issue with the numbers if a part is refused. A repository whose content folder outgrows one tree read (100,000 files) splits its content by bank.

## I-65. The runtime's operator surface and who may use it

**Issue.** The plan lets a running payment change only through declared operator actions (retry, repair, cancel, decide), each with a permission and a second approver where the package says so (W4). It does not say where an operator takes those actions, how an operator is identified, or where the permissions live. The studio's users and roles (`governance/users.yaml`, maker-checker) are for authoring, and the studio never touches a running payment (R-10-01), so they cannot be the operators' roles. The runtime's release API (`runtime/src/TransactHub.Api/Program.cs`) has no authentication today: anyone who reaches it can store a release or record an activation. The source had an operations console, an identity service and a permission catalogue; none of them is carried, and the source's own security suite fails on that catalogue (N15).

**Recommendation.** Decide, in one change to the plan: (1) the operator surface (an API only, or an API and a console), (2) the identity provider an installation uses and the claims it needs (tenant, roles), (3) that every operator action and every call to `POST /releases` and `POST /activations` is authorised server-side and audited on the payment's fact chain, and (4) which actions need a second approver. Until then, run the release API only on a network the pipeline alone can reach, and do not build a console.

**The same gap in the studio.** The studio enforces roles on every write, but it takes the acting user from the request (`actor`), chosen in the web app's header; it has no sign-in. That is enough for the walking skeleton, not for a shared installation. Decide the studio's sign-in with the same identity provider, so that `actor` comes from a verified identity and never from the request body.

**The studio's part: decided and done (2026-11-22).** The owner decided the studio's sign-in and roles, and it is built: OpenID Connect with PKCE against the identity provider the platform vendor operates for the studio alone, signed in-memory sessions, CSRF protection, no "Act as" selector, the user and tenant always from the session, and one permission table for Bank Configurator, Bank Approver, Workflow Designer, Workflow Developer, Platform Approver, Administrator, Service operations and Auditor (R-11-29 to R-11-34, R-10-10 to R-10-12). The studio's sign-in is separate from the runtime's and accepts no runtime token. What remains of it is in I-69 to I-72.

**What is built (the studio).** The studio's sign-in, roles, maker-checker and hash-chained audit are built and tested (C17; `studio/test/permissions.test.ts`, `studio/test/auth.test.ts`, `studio/test/blackbox/auth.test.ts`, `studio/test/blackbox/ui-login.test.ts`). `docs/north-star/guidelines/access-control.md` keeps the two apart.

**The runtime part, decided and built (owner, 2026-11-22).** The runtime belongs to the bank and has no identity store. `runtime/src/TransactHub.Auth` validates bearer JWTs from the bank's one OIDC issuer (issuer, audience, discovery or a JWKS, clock skew: configuration), people's tokens and machine callers' client-credentials tokens alike, and maps the bank's claims and groups to the host's roles through the bank's role map (`Auth:RoleMap`); a role claim in the token itself is ignored. It never trusts the studio's issuer: the studio's only link to the runtime is the package signature (R-09-23 to R-09-26). The release API requires a caller on every endpoint and authorises by role: `release-importer`, `release-activator`, `release-reader`, `values-reader` (per tenant); the actor recorded is the verified caller (R-09-27, R-09-29; `ApiAuthTests`: 401, 403, app roles, groups, tenants, the studio's issuer refused, start-up refusals). The library takes any host's roles, so an operations console's API uses it for operator and supervisor. Still open: the operator surface itself, which actions need a second approver, audit of operator actions on the payment's chain, and the native host's callers (I-75).

**What is built for operators (2026-11-22, the owner's decision of that day).** The owner decided (1) an API and a console, (2) the bank's identity provider over OIDC, the runtime keeping no identity store, roles Operator and Supervisor per tenant from the bank's claims or groups through a configured mapping, never the studio's issuer, and (3) that every operator action is authorised server-side and recorded on the payment's fact chain. Built: `runtime/src/TransactHub.Operations.Api` and `runtime/ops-console/` (R-05-48 to R-05-50, R-06-24). The API is behind the runtime's authentication library (`TransactHub.Auth`, `AddRuntimeAuth` with the roles Operator and Supervisor and the bank's role map) and refuses the studio's issuer (`TH_STUDIO_ISSUER`); the console signs in with the authorisation code flow and PKCE; a dev sign-in (`TH_AUTH_DEV=1`) is refused when the host runs in Production. Second approvers (4) are declared per action in the packs' operations views (`secondApprover`): for the factory, releasing a repair item and cancelling a payment, as the source's business exceptions were dual control. Each request, approval and result is a fact with the operator's identity. Left open: the role map grants roles and tenants together, so a person who is an operator in one tenant and a supervisor in another is a supervisor in both; a grant per tenant pair in `RoleMap` would close it. Signals do not yet reach an engine (I-99).

**Decided (22 November 2026).** The owner accepted per-tenant role grants in the runtime's role map, so one person may be an operator in one bank and a supervisor in another. Built the same day (security review F-04, R-06-28): each grant gives its roles in its own tenants only (`th:tenant-role` claims, `RuntimeAuth.HasRoleFor`), the operations console resolves roles per tenant from them, and the release API asks for the role in the bank for values, bindings, payments and every distribution action (`ApiAuthTests`, `OperatorApiTests`).

**Decided (23 November 2026).** The owner decided that one runtime serves one bank (I-154, R-09-76), so the per-tenant grants of R-06-28 collapse to that bank; their checks stay. Every change of the system in the service console and the operations console needs the consent of another person with the bank approver's role (`BankApprover`), both recorded as facts (I-155, R-09-77); an operator's decisions on a payment keep the second approver their view declares.

## I-66. Observability of the runtime and the studio

**Issue.** The plan and the spec say nothing of traces, metrics, structured logs or health checks for the runtime, the engine hosts or the studio. The runtime has no telemetry today: no `ActivitySource`, no `Meter`, no OpenTelemetry package. The spec's rule that personal data stays out of logs and traces (`spec/CLAUDE.md`, Data) is stated, but nothing checks it in logs, because there are none. Each engine has its own telemetry, so a payment's trace crosses four kinds of host.

**Recommendation.** Add a short section to the plan: OpenTelemetry for traces, metrics and logs in every runtime host, with no named backend in shared code; one set of attribute names (payment id, release, scheme, leg, tenant, engine, step), never a party value; health and readiness endpoints on each host; and a test that runs the WPS salary file and scans every exported span and log for any value from the party records, as the plan already does for engine history (W1).

**What is built.** Nothing. `docs/north-star/guidelines/observability.md` states only the rules that already hold.

**Decided and built (22 November 2026).** The owner decided to add monitoring. Built as the recommendation says (R-09-31 to R-09-37, R-10-07 to R-10-09): OpenTelemetry, pinned, in the release API, the four engine bindings' hosts, the studio server, the deploy runner and the deployed workers, exporting over OTLP by the standard `OTEL_*` variables only; one trace per payment from intake through every engine attempt, activity, port call and store write; the metrics of R-09-34; structured logs carrying the trace id; masking of personal data before any exporter, proved by tests that run the WPS salary file on in-memory exporters (`runtime/tests/TransactHub.Store.Tests/TelemetryTests.cs`, `studio/test/telemetry.test.ts`); `/health/live` and `/health/ready`; and a local stack with four dashboards (`runtime/observability/`). The guideline is rewritten. What it does not yet do is I-80.

## I-67. Secrets, keys and file encryption at deployment

**Issue.** `content-configurations/README.md` says a secret is named by reference and set at deployment. No format for that reference exists: the port bindings in `banks/<bank>/core-banking.yaml` carry settings only, and the runtime reads no secret. The plan puts a decrypt activity in `p.*` (section 2 and W6), the factory has none yet, and WPS files can arrive encrypted (`.SIF.GPG`), but the runtime has no decryption and no key store, and the source's encryption library is not carried. This repository is public and its CI has no secret scan and no dependency audit.

**Recommendation.** Decide: (1) a reference form for a secret in a binding (for example `secretRef: <name>`), resolved by the host from the installation's secret store and never written to the branch, the store or a log; (2) that decryption is an activity behind a port, with one key ring per scheme and per bank, chosen by configuration and never by a file name; (3) a secret scan and a dependency audit as CI jobs on every push; (4) how personal data is erased from an insert-only store when the law asks for it (for example a key per party, destroyed on erasure), and how long it is kept. Each with a requirement id and a check, as `spec/CLAUDE.md` asks.

**What is built.** The file-name rules that tell an encrypted SIF from a plain one (`CbxFileNameRules`, golden against the source). `docs/north-star/guidelines/encryption-and-secrets.md` keeps the rules from the source that still hold.

**Decided and built (owner, 2026-11-22), points 1 and 3, and package signing.**
- (1) A secret is a reference: `secretRef: env:NAME | file:/path | vault:mount/path#field`, resolved by the host through one interface (`ISecretResolver` in `runtime/src/TransactHub.Security`, `resolveSecret` in `studio/src/secrets`) and never written to the branch, the store, a package, a log or an exception (R-06-22). The Vault provider (KV version 2) is tested against a Vault dev server in a container. Check L37 refuses a literal secret in the spec, the content branch and every package (R-06-23; mutation cases M38, M39).
- (3) CI job `security` on every push: gitleaks 8.21.2 (checksum pinned) over the history and the tree, L37, `npm audit --omit=dev --audit-level=high`, `dotnet list package --vulnerable --include-transitive` (R-09-28).
- Package signing: ECDSA P-256 over a canonical statement of every file's SHA-256, the release id, tenant and change set; the key by reference (`TH_SIGNING_KEY`); verified by the runtime at storage and activation, recorded as a fact, and by the deploy runner; several trusted keys with retirement and revocation (`TH_TRUSTED_SIGNERS`) (R-09-23 to R-09-26; `spec/design/09-export-bindings.md`, 9.9).

Still open: (2) decryption as an activity behind a port, (4) erasure and retention. The Azure Key Vault and AWS providers are built (I-73, done) and the signing key stays in the studio's process (I-74, decided).

## I-68. Non-functional targets, deployment sizes and the reporting read side

**Issue 1. Targets.** The source stated latency, throughput, availability and recovery targets per deployment size. The plan states none for the runtime, and the spec forbids load tests in the studio. Without targets there is nothing to size an installation against, and no test to fail. Beyond the engine hosting of D3, the plan also does not say how an installation is packaged and fronted: container images, orchestration, the gateway and where TLS ends.

**Recommendation.** Write the targets an installation must meet (latency per step, salary-file size and time, availability, recovery point and time) in the plan, measured by a runtime performance suite outside the studio, on each engine and each database. Add a deployment section with one reference installation per engine, and grow it into sizes once the suite has measured them.

**Issue 2. Reports.** The plan decides only that the report database is fed from the outbox, not from change data capture (W2). Which reports exist, their shape, and how an operator searches payments are not decided. I-46 recommends free-text search on the runtime's store, not in Temporal.

**Recommendation.** Decide the reports of the first drop (WPS) and the read model behind them, as projections from `outbox`, in the same change as the code that builds them.

**What is built.** The `outbox` table in the store. Nothing reads it yet.

**What is built for the operator surface (2026-11-22).** The first read model: the operations read model (`runtime/src/TransactHub.Operations`, R-05-49). The store now writes an outbox row with every fact and every message, in the same save; the projection follows the outbox from a checkpoint, applies each fact once by its number on its chain, is rebuilt from the facts on request with the same result, and shows its lag (`GET /ops/api/health`, the console's top bar). It runs on SQL Server and PostgreSQL through EF Core, with its own migrations, in the store's database or one of its own (`Operations:Connection`). The operator's search is the console's list: by tenant, scheme, leg, direction, ISO status, outcome, hold and its reason, dates, amounts, ids, references and file names, and by any filter a pack's view declares. Not yet: the reports of the source's report database (dashboards, counts per file type, exports to CSV, Excel and PDF), and one projecting process per installation is assumed (a second would only redo work, since applying is by number; a lease is not built).
**A first load test (22 November 2026).** The owner decided a load test runs before any bank goes live. The harness is built (`runtime/load/`, R-09-38) and ran here on Temporal with its persistence on our SQL Server plugin and on PostgreSQL, and the store on SQL Server and on PostgreSQL: `docs/runtime/LOAD-TEST.md` has the conditions and the numbers, and proposes targets for the owner to set. It found two things that stop a bank going live as the runtime stands: a file of more than a few dozen lines fails on a real Temporal server (I-77), and a large file takes far too long, because its items run one after another at a fixed cost each (I-78). Issue 1 stays open until the owner sets the targets; Issue 2 is unchanged.

**Built for Issue 2, reports (23 November 2026).** The operations console has a Reports page (`runtime/ops-console/src/ops/Reports.tsx`) over `GET /ops/api/reports`, `/reports/summary`, `/reports/dashboard` and `/reports/export.csv` (`Reports` in `runtime/src/TransactHub.Operations`, R-05-59), built only from the read model and the operations views: the totals by scheme, status, day and hold; for each scheme view the totals of its list columns of counts and amounts and the counts by its exact filters; a summary by any dimension of the read model or by a view's exact filter; and the list as CSV with the views' list columns. Everything answers for the tenants the caller holds the operator's role for, and a personal field is shown and exported only as its view masks it. Tests: `ReportTests` on both databases, and the WPS end-to-end case in `wps-hold.test.ts`. Not built: Excel and PDF (CSV opens in both), a report a view declares of its own (the reports take the view's columns and filters as they are), scheduled reports, and an export role apart from the operator's: I-144.

## I-69. The roles of earlier releases, and who authors a bank's own ports and extensions

**Issue 1.** A content branch written by an earlier release names the roles shared designer, shared checker, bank designer and bank checker in `governance/users.yaml`. Read strictly, every user there would lose every right after an upgrade, the Administrator role among them, and nobody could change the users again. The studio therefore reads each old name as the roles that held the same rights (shared designer as Workflow Designer and Workflow Developer, shared checker as Platform Approver and Administrator, bank designer as Bank Configurator, bank checker as Bank Approver), and refuses an old name in a newly proposed list.

**Recommendation 1.** On each installation, have an administrator propose the users with the new role names, and have another approve it. Once every branch in use is migrated, remove `LEGACY_ROLES` from `studio/src/governance/permissions.ts`, in a change with its requirement (R-11-29) amended.

**Issue 2.** The owner's decision forbids a Bank Configurator to create or change a port, and does not list extensions among its kinds. A bank's own hooks can need a bank's own port and extension in scope `b` (the example bank has `b.port.treasury.v1` and `b.ext.bank.v1`). No role may now author those: the developer works only in shared scopes, and the configurator may not.

**Recommendation 2.** Decide who authors a port and an extension in scope `b`: either the Bank Configurator (one row added to the table), or a Workflow Developer working for the bank, which needs a role that is both a platform role and of one bank. Until then, a bank's port and extension change only through the seed or the bank's pack folder.

**Issue 3.** The operations view (R-05-48), which declares a workflow's operator actions and their second approvers, came after the owner's decision and is not in it. The table gives it to the Workflow Designer, with the workflow it belongs to, and its version to the Platform Approver.

**Recommendation 3.** Confirm that, or name another author (the second-approver rules are a control the bank may want to approve too).

**Issue 4.** The error dictionary (R-05-51) also came after the decision. The table gives it to the Workflow Developer, who builds the activities, rules and ports whose errors it documents, and its version to the Platform Approver. A bank has no dictionary of its own in scope `b`.

**Recommendation 4.** Confirm that, and decide whether a bank documents the errors of its own hooks in a dictionary of scope `b`, authored by its Bank Configurator.

**Decided (22 November 2026).** The owner decided: the Workflow Designer and Developer are the studio's roles. Operations and Service Engineer are not studio roles: they are the runtime roles of the operations console and the service console, signed in through the bank's identity provider. In the studio the operations views stay with the Workflow Designer and the error dictionaries with the Workflow Developer, approved by the Platform Approver, as built. A new kind of connection (a bank-only port or extension) is created by the Workflow Developer; a bank then binds it.

## I-70. Port bindings have no editor in the studio

**Issue.** The owner's decision lets a Bank Configurator change the port bindings for its bank, and the permission table grants it (`port-bindings`, author, scope `b`). But a port binding is a runtime document (`banks/<bank>/core-banking.yaml`, R-11-28): the studio reads past it and offers no editor and no API to change it. The table row is therefore not reachable today.

**Recommendation.** Add port bindings to the studio as a bank's artefact: a draft on the bank's change set branch, checked against its port, approved as a version by a Bank Approver, in one change with a requirement for its shape and a check. Secrets in a binding stay references (I-67).

**Built (22 November 2026), as recommended.** Port bindings are a bank's artefact in the studio (R-11-47): kind `port-bindings`, the shape of `spec/schemas/port-bindings.schema.json`, in scope `b`, one release per file at `banks/<bank>/port-bindings/<release>.yaml`. The Bank workspace has an editor (`studio/web/src/views/PortBindings.tsx`): a row per binding with its port (from the published port contracts the bank sees), adapter, `validFrom`, settings and secret reference; it starts from the bank folder's runtime documents until the studio has released bindings, and from the last release after. `GET /api/tenants/<bank>/port-bindings` and `POST .../port-bindings/check` serve it; a save is a draft through `POST /api/drafts`, decided by the permission table (`port-bindings`, author, scope `b`). Check L48 holds a draft and every bank folder's runtime documents to the port contracts (a port the bank may bind, an adapter, a valid date, no port twice from one `validFrom`, no literal secret), in the studio and in `tools/validate.py` (mutation M67). A Bank Approver's approval builds the configuration release with the released bindings (`bindings/port-bindings.yaml`), released as `<configuration set release>+bindings.<port bindings release>` so that new bindings with the same values are a release of their own; the runtime already installs them as effective-dated bindings. Tests: `studio/test/port-bindings.test.ts`, `studio/test/blackbox/ui-login.test.ts`. A port contract still declares no adapters or settings, so the check cannot say which adapter a port offers or which settings it needs; that is I-140.

## I-71. The command line and the deploy runner are not signed in

**Issue.** The server's API asks for a signed-in user on every call. The command line (`npm run studio -- import --apply`, `seed-content`, `deploy`) runs in the operator's own process with the operator's GitHub token, and takes its actor from `--actor` and `--bank-actor`. The permission table still decides what that actor may do, but nothing proves who typed the command.

**Recommendation.** Treat the command line as a tool of the platform's own pipeline and administrators, run only where the GitHub token is held, and say so in the deployment guide. If it must serve other users, give it the same sign-in (a device authorization flow against the studio's identity provider) and drop `--actor`.

**Decided and built (22 November 2026).** The command line signs in with the same OpenID Connect provider as the web app, by the device authorization flow (RFC 8628), through the studio (R-11-46): `npm run studio -- login --server <url>` prints the provider's page and a code, the operator signs in there, and the studio verifies the ID token (its audience the command line's client, `TH_OIDC_CLI_CLIENT_ID`, by default the studio's own) and maps it to a user of `governance/users.yaml` as it does a browser's. It gives back a sealed token of its own (`thcli.`, `TH_CLI_SESSION_HOURS`), kept in `TH_STUDIO_TOKEN` or a file readable by its owner only. With `--server`, `import` (preview and apply), `whoami` and `deploy`'s permission check call the API as that user, and the permission table decides as it does for the web app; `--actor` and `--bank-actor` are refused there; applying an import to a connected branch without a studio is refused. Built in `studio/src/cli-auth.ts`, `studio/src/server/auth.ts` and `http.ts`; the development provider has the device endpoints. Tests: `studio/test/sessions.test.ts`, `studio/test/blackbox/cli.test.ts`. Still the pipeline's: `seed-content` on a connected branch publishes the factory with the GitHub token, as before, and `deploy` runs the package on the operator's machine (it asks the table first only when a studio is named).

## I-72. Sessions live in one process

**Issue.** Sessions are held in memory (R-11-24 forbids a disk). A second studio instance does not know the first one's sessions, so a load balancer must keep each browser on one instance, and a restart signs everyone out. A change to the users takes effect on the next call, but a session is not ended early when its identity is disabled in the identity provider: it lasts until it expires (8 hours by default).

**Recommendation.** For one instance, keep it. For several, either sticky sessions, or a session cookie that carries its own signed, short-lived claims (no server state) with the same signing key on every instance (`TH_SESSION_KEY_REF`). Decide the session length with the identity provider's owner, and whether the studio should check the provider's session on a short interval.

**Decided and built (22 November 2026).** Sealed, stateless sessions (R-11-45, `docs/adr/0002-stateless-sessions.md`): the session and the sign-in in progress are AES-256-GCM sealed cookies that carry their own claims and expiry, under keys derived from `TH_SESSION_KEY_REF`, the same on every instance and required in production. A second instance reads the first one's sessions, a sign-in started on one finishes on another, and a restart signs nobody out; nothing is stored on the branch or on disk. The idle time slides by sealing the cookie again after a minute of use. What it leaves is I-139.

## I-73. The Azure Key Vault secret provider is not built

**Issue.** The secret interface names `akv:` references, but only `env:`, `file:` and `vault:` (HashiCorp Vault KV version 2) have providers. An Azure installation can mount Key Vault secrets as files (the Secrets Store CSI driver) and use `file:`, but cannot read Key Vault directly. A reference to `akv:` fails with a message naming this issue. The Key Vault SDK and a managed identity cannot be tested in this repository's CI without an Azure subscription.

**Recommendation.** Mount Key Vault secrets as files (`file:`) until a bank needs a direct read. Then add an `AzureKeyVaultSecretProvider` behind `ISecretProvider` (Azure.Security.KeyVault.Secrets with a managed identity), tested against a recorded fake in CI and once against a real vault before the bank's first use.

**Done (owner's decision of 2026-11-22).** The owner decided to support Azure Key Vault and AWS beside HashiCorp Vault, in the runtime and the studio. Built (R-06-22 amended, R-06-26, R-06-27):
- `akv:<vault-name>/<secret-name>[#version]`: `AzureKeyVaultSecretProvider` (Azure.Security.KeyVault.Secrets 4.12.0, Azure.Identity 1.21.0) and `studio/src/secrets/cloud.ts` (@azure/keyvault-secrets 4.12.0, @azure/identity 4.14.1). It signs in with the first configured of client credentials (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `TH_AKV_CLIENT_SECRET`, a reference to env: or file:), workload identity (`AZURE_FEDERATED_TOKEN_FILE`) and managed identity, in DefaultAzureCredential's order. A literal `AZURE_CLIENT_SECRET` is refused. `TH_AKV_DNS_SUFFIX` names a sovereign cloud's vault domain.
- `aws-sm:<region>/<secret-id>[#json-field]` and `aws-ssm:<region>/<parameter-name>`: `AwsSecretsManagerSecretProvider` and `AwsParameterStoreSecretProvider` (AWSSDK.SecretsManager 4.0.100.23, AWSSDK.SimpleSystemsManagement 4.0.104.9) and the studio's (@aws-sdk/client-secrets-manager and @aws-sdk/client-ssm 3.1162.0), on the AWS default credential chain. A field is read from a JSON object; a parameter must be a SecureString.
- A value read over the network is cached for `TH_SECRET_CACHE_SECONDS` (default 300, 0 for none) and never past it or when its store fails; `ISecretResolver.Invalidate` and `WithSecret` read a refused credential again, once (`SecretStores.withSecret` in the studio).
- Tests: Azure against `FakeKeyVault` (`runtime/tests/TransactHub.Security.Tests`) and `studio/test/support/fake-key-vault.ts`, an in-process fake of the Key Vault REST protocol and the token endpoint, given to the official SDK as its transport, since there is no official emulator; AWS against LocalStack 4.14.0 in a container named for the run. What these do not prove is I-105.

## I-74. The studio's signing key is held in its process

**Issue.** The studio signs packages with a private key it reads into its own memory, by reference, from the environment, a mounted file or Vault. A compromise of the studio's host can sign packages that every runtime trusting the key accepts, until the key is revoked in each installation's trusted signers. There is no transparency log of what was signed, so a bank cannot list every package a key signed.

**Recommendation.** For production, sign with a key that never leaves a key store: Vault transit, a cloud KMS or an HSM, all of which sign ECDSA P-256 over SHA-256, so the format and the verification do not change. Record each signature (key id, statement hash, change set) in the change set's audit on the branch, or in a transparency log (Sigstore's Rekor) where the bank may use one. Rotate the key at least yearly, as the guidelines describe.

**Decided (owner, 2026-11-22).** The studio's signing key may stay in the studio's process: no HSM, KMS or remote signer is required. The key is still read only through the secret interface (`TH_SIGNING_KEY`), so it may come from any of its stores: env:, file:, Vault, Azure Key Vault, AWS Secrets Manager or Parameter Store. A key in a store over the network is read when the studio starts (`primeSigningKey`) and held until it restarts (I-106). R-09-23 says so. The transparency log and the yearly rotation stay recommendations.

## I-75. The native host has no authentication

**Issue.** The release API now takes only the bank's bearer JWTs (R-09-27). The native host (`TransactHub.Native`), which answers native activities for the workers over HTTP (`TH_NATIVE_URLS`), and the engine harness hosts take any caller. A caller that reaches the native host can post to core banking through the bank's binding.

**Recommendation.** Put the native host behind the same library (`TransactHub.Auth`) with a `native-caller` role the workers' client-credentials tokens carry, or bind it to the workers' private network only and say so in the deployment guide. Decide with the worker hosts of the plan (`TransactHub.Host`), which are target.

**Done (22 November 2026, security review F-03).** The native host is behind `TransactHub.Auth` (R-08-17): with `Auth:Oidc` configured it takes only a bearer JWT of the bank's issuer whose role map gives `native-caller`, in the tenant the attempt names, and refuses another bank's attempt with 403 before any activity runs; without it, it answers only on loopback addresses and does not start when it listens beyond the machine. The studio's interpreter and the deploy runner's workers send `TH_NATIVE_TOKEN`, a reference (`NativeHostAuthTests`, `native.test.ts`). What remains, the worker host's own token and mutual TLS, is I-121.

## I-76. Two gaps in publishing: memory left promoted on a refusal, and no release tag in pull-request mode

**Issue 1.** `approveChangeSet` promotes the members in memory, then builds the files to commit (`filesFor`) inside the content store's transaction. If building them throws (for example a package that carries a literal secret, which is not signed, R-06-23), the commit is not sent but the in-memory entries stay promoted until the studio reads the branch again. The missing signing key is checked before the transaction, so that case is refused cleanly; the literal-secret case and any other error in `filesFor` are not.

**Recommendation 1.** Build the change set's files before promoting anything, or restore the entries when the transaction throws, with a test that a refused publish leaves every member a draft.

**Issue 2.** In commit mode a published workflow release gets its annotated tag `release/<workflow>/<release>/<target>` on the publishing commit (R-09-30). In pull-request mode the studio commits to the change set's branch and opens a pull request; the release reaches the content branch only when someone merges it, on a commit the studio did not make (and a squash or rebase merge rewrites it, I-62). No tag is made, so a runtime's package source does not list that release.

**Recommendation 2.** When the studio sees the merge (it already reads it to mark the change set published), tag the merge commit, after checking that the release folder there is byte for byte what it committed and signed. Until then, a bank in pull-request mode installs with `POST /import-branch` from its pipeline.

## I-77. On a real Temporal server, a step's attempt timeout is spent by processing time

**Issue.** The Temporal binding keeps business time logical: it moves on a timer, a party's declared latency or an arrival, never on processing (`TemporalEngine`). But the timer the interpreter races against each attempt (its `attemptTimeout`, `PT5S` for a validate activity by the type default) is set on Temporal's own clock: it fires when the server's time passes the deadline. On a real server that time moves while the workers process, and the logical time does not. Each item of a salary file costs about 0.2 to 0.4 seconds of real time on the server (two attempts, a claim check), so once the item loop has run for five seconds every new attempt's deadline is already past and the attempt times out. On 22 November 2026, on Temporal with its persistence on SQL Server and a heavily shared machine, a file of 3 lines settled and files of 10, 20, 30 and 100 lines failed (a decision or the record checks timed out, `leg:failed`), with the default setting (`docs/runtime/LOAD-TEST.md`, the ceiling runs). The time-skipping test server and the studio's interpreter do not show it: there, processing takes no time on the clock. This stops any bank going live on Temporal.

**Recommendation.** Decide in Part 9 what an attempt timeout measures on a real engine: business time (from the logical now, as `TH_TEMPORAL_TIMERS=logical` does, `TemporalEngine.Timer`) or real time (then the logical clock must move with the server's, and the type defaults must be sized for real ports). Prove the choice with a kit scenario run on a real server that processes a file whose item loop outlasts the step's timeout, on every engine. Until then the load harness sets `TH_TEMPORAL_TIMERS=logical` and says so; the default is unchanged.

**Decided and built (owner, 2026-11-22).** An attempt timeout measures only that attempt's own execution, never the engine's processing or queueing; deadlines, await timeouts, waits, loop intervals and backoffs are timers on business time, on the business calendar where the step says so (R-05-54, Part 5). This is the default on every engine. Temporal: the binding keeps the attempt's deadline itself (`IAttemptDeadlines`, `TemporalEngine.Attempt`): a party's declared time past the deadline, an answer that never comes, or, on a real server, the activity's own Start-To-Close (the attempt's budget, counted by Temporal from when a worker starts it; no Schedule-To-Close) end the attempt as a Timeout at its deadline, and the interpreter races no timer the server's clock could spend. Every other timer is logical, from the business time it is set at; `TH_TEMPORAL_TIMERS=logical` is gone, and `TH_TEMPORAL_TIMERS=server` keeps the old timers for a comparison run only. DTFx and Elsa already had these semantics (their clock moves only when nothing is in flight); Flowable's embedded kit harness likewise; the Flowable REST binding in production mode moves an attempt's timer boundary out of the way when the server creates it and sets it from when the binding begins the attempt (`FlowableBinding.ArmAttemptTimer`). Proved by the new kit scenario CK-TIMEOUT-03 (300 items one after another, each a decision with the type default `PT5S`, then a step of four seconds: on a real server the item loop takes longer than five seconds, and business time moves by four seconds only), which the Temporal adapter runs on a real server. The kit is 50 scenarios (CK-TIMEOUT-03 and CK-FOREACH-03 added). On the load harness, a file of 100 lines with the binding's defaults failed in 7.2 s before and settles after (`docs/runtime/LOAD-TEST.md`).

## I-78. A large file runs its items one after another, and the claim check writes one at a time

**Issue.** The WPS leg checks each line in a `forEach` with no parallelism, and each line is two activity attempts, each a Temporal activity on the scheme's task queue plus a claim-check local activity, a fact in the store and an HTTP call to a Node worker. Measured on 22 November 2026 (`docs/runtime/LOAD-TEST.md`), a 1,000-line file takes several minutes and its lines go at a few per second; a 10,000-line file would take the better part of an hour. Two host-level limits add to it: `DurableClaimCheck` writes every fact of every payment in a host behind one lock, and each deployed worker is one Node process.

**Recommendation.** Decide, with the owner's targets (I-68): (1) a bounded parallel `forEach` for item-level checks that hold no state (Part 5), or one activity per chunk of items, so a file's cost grows with its chunks, not its lines; (2) a lock per payload in the claim check, not per host; (3) more than one worker process per queue. Re-run the load matrix after each.

**Decided and built (owner, 2026-11-22), points 1 and 2.** (1) A `forEach` that declares no concurrency, whose steps only call activities that hold no state and handle an outcome only by ending, runs `defaults.forEachConcurrency` items side by side (the bindings say 64 for Temporal, DTFx and Elsa), chunk by chunk (R-09-45). Its trace is the one item by item gives, whatever order the items end in: each item records into its own sink, and the sinks go into the trace in item order; an item that stops the forEach is the last whose entries are kept (R-05-53; the studio interpreter, the shared interpreter of DTFx, Elsa and Temporal, the Flowable kit harness and the REST binding; CK-FOREACH-03). (2) The claim check, its journal and the step facts lock per payload, never per host, and write the facts that queue for one payload in one save, each chained to the one before (`FactWriter`, `FactLog.AppendMany`, R-09-46; `ClaimCheckTests` "many payloads and many items side by side": every chain verifies, the rebuilt payload is the one in memory, and fewer saves than facts). A deployed worker gives each item's attempt an instance of its own, so items of one payment side by side do not share a context. Measured on 22 November 2026 (`docs/runtime/LOAD-TEST.md`, Temporal and the store on PostgreSQL, a shared 4-vCPU machine): a 1,000-line file went from 212.6 s to 57.8 s (4.7 to 17.3 lines a second); a 10,000-line file took 13.6 minutes (about 40 estimated before); four 500-line files at once from 208.8 s to 68.6 s (9.6 to 29.2 lines a second); eight at once 25.5 lines a second, which is what this machine gives: the CPU per line halved (0.12 to 0.06 CPU seconds), and the run is held by the CPU the shared host leaves it. (3), more than one worker process per queue, is not done: the Node workers used under 10 % of the run's CPU. What is left is I-108.

## I-79. A deployed worker shares one clock and one run across the attempts in flight

**Issue.** A deployed worker (`studio/src/worker/main.ts`) runs every attempt on one `PartyService`, which has one simulator run and one clock: `attempt` sets `run.clock.now` to the request's time, and the hold flag and the deliveries were also kept once for the service. A worker serves many attempts of many payments at once, so one attempt could take another's deliveries (a scheme's acknowledgement handed to the wrong payment) or read another's time. The deliveries and the hold flag are now kept per attempt (`PartyService.attemptOwn`, an `AsyncLocalStorage`), which the load runs needed; the clock is still shared.

**Recommendation.** Give each attempt its own clock view, or serialise the attempts of one simulator conversation. This matters only where simulators run (a test deployment); in production a port is the bank's connection. Add a test that runs two payments' attempts on one worker at once and checks each gets its own replies at its own time.

**Done (22 November 2026).** Each attempt in flight on a deployed worker has its own view of the clock: the simulator run's `now` is the attempt's on its own async path (`PartyService`, `AsyncLocalStorage`), and the service's own outside any attempt; an item's attempt runs on an instance of its own. `studio/test/units.test.ts` ("gives two attempts in flight on one worker each its own clock and its own replies") runs two payments' attempts at once at different times on one service and checks each gets its own replies, due at its own time; it fails on the code before.

## I-80. What the telemetry does not yet do

**Issue.** (1) The facts do not carry the trace id, so an operator cannot go from a fact to its trace. (2) Span events cannot be changed in .NET once added; the masking processor takes them off and adds them back masked through `Activity`'s private `_events` field. A test fails if that field moves (`TelemetryTests`), but it is a dependency on the runtime's internals. (3) A name in free text has no pattern: the masking finds IBANs, emails and long numbers, and replaces values under personal keys, but a name written into a message by a library would pass. (4) No alert rules, service-level objectives or retention are written, and the dashboards are for the local stack only. (5) The deployed worker bundles OpenTelemetry, which makes `worker.cjs` about 1.2 MB.

**Recommendation.** (1) Add the trace id to the payload's `create` fact as a correlation reference, outside the hashed business data, in the store's next migration. (2) Ask OpenTelemetry .NET for a way to filter events in a processor, and drop the private field when there is one. (3) Keep the rule that hosts write ids only, and add a scan of the collector's output for the party values in the load harness. (4) Write the alerts with the targets of I-68. (5) Accept it, or load OpenTelemetry in the worker only when an endpoint is set.

**Done for (1) (23 November 2026).** Every fact names the OpenTelemetry trace and span it was written in (`TraceId`, `SpanId` on `payment_fact`, from the writer's current activity), outside its hashed data, and the operations read model's timeline carries the trace id (R-09-69). The columns are added by expand-then-contract, nullable, by the migrations `TraceIds` of the store and of the read model on SQL Server and PostgreSQL alike: a host of an earlier release writes and reads facts without them, and there is nothing to contract. The operations console's timeline and the service console's log link a fact to its trace (`TH_TRACE_URL`, a template with `{traceId}`), and the service console finds a trace's facts and logs by `?trace=`. Tests: `TraceIdTests` on both databases, `LogSourceTests`. (4) is answered by R-09-72 (the alert rules and SLOs, generated from the agreed targets). (2), (3) and (5) stay open.

## I-81. A test case carries no release

**Issue.** A published release never changes (spec/CLAUDE.md, "Versions"). A test case is keyed by its id and has no release (`test-case.schema.json`), so a factory test that changes after it was seeded has no new release to go to: the seed rewrote `artefacts/<scope>/tests/<id>.yaml` in place. WPS-IN-002 to 004 were rewritten that way on 2026-11-22, and plan P2 changes the agent leg's trace again (the posting window).

**Recommendation.** Give a test case a release, or state in Part 1 that a test is released with its pack. Until then, publish a changed test beside the earlier one, under its pack's release.

**What is built.** R-11-35. The seed publishes every factory test of a pack at a release above 1.0.0 to `artefacts/<scope>/tests/<id>@<pack release>.yaml`; the file of the earlier pack release stays as it was published. The studio reads both and takes the later. Only the latest pack's tests are held to its lock file (`studio/test/core.test.ts`). WPS-IN-002 to 004 are back as first published; their 1.1.0 texts are at `tests/WPS-IN-00n@1.1.0.yaml`.

## I-82. The source's RRI and DE flows have no factory home yet

**Issue.** Plan P2 lists RRI and DE among the WPS flows. The source has both (DATA, `TransactHub-src`, `dsl/workflows/`):
- **RRI**, the refund request instruction (`dib-wps-inward-rri.v1` 1.2.0): one run per RRI file; the file is verified, parsed and saved (`wps.ParseAndSaveInwardRri`, `InwardRriParser`), a rejected file is raised to the exception queue, and a saved file goes into an automated account block (`wps.BlockInwardRriLines`): one `cbs.amount.block` call per line, parked on the `CbPostingWindow` signal between passes, ending `RRI-Block-Completed` or `RRI-Block-Expired`. An unblock flow (`RriUnblock`) and reject reasons follow.
- **DE**, inside the agent's payment file flow (`dib-wps-inward-paf.v1` 1.20.0, Activities 12 to 15 and the repost and lost-file activities): the bank's credits posted as one bulk file to its core system (generate the DE bulk file, transfer it by SFTP, submit the upload, determine the outcome from `getDeuploadStatus`, repost the failed lines).

Neither is a scheme step the factory can author with what it has. The RRI's block needs an amount-block operation on an employee's account; `p.port.core-banking.v1` has posting, balance, reversal and verification only. DE is the bank's route to its own core banking, one bank's bulk upload: in the north-star layout it is a mode of a core-banking adapter, not a WPS step.

**Recommendation.**
1. **RRI.** Add `block` and `release` operations to `p.port.core-banking.v1` (an additive change: a minor) and an activity `p.reserve.block.v1`, then author `uae.wps.wf.refund-request-in.v1`: map the RRI, block each line in the posting window (`p.decide.await-posting-window.v1`), hand an expired block to operations. It needs the owner to decide what the RRI's reject reasons (000, 002) mean to the leg and to give `uae.wps.rri` a dispatch entry (D10 for the scheme facts).
2. **DE.** Make it a `bulk` adapter of the core-banking binding: the leg's credits posted as one upload, the outcome read back per line, chosen per bank by its binding. No factory flow.

**What is built.** The RRI parser as a native runtime activity, `uae.wps.map.rri-in.v1` (the source's `InwardRriParser`, proved by its own 671-line unit test, `runtime/tests/TransactHub.Activities.Wps.Tests`), with its factory contract. No flow reads it yet (L11 warns). Nothing of DE.

**Decided (22 November 2026).** DE is a bank's bulk posting to its own core banking by file (generate the bulk file, transfer it, submit the upload, read the upload status, repost the failed lines). It is not a WPS step: it is another connection, built as a file-based bulk mode of the core-banking port (a `file-bulk` adapter beside `gateway-rest`), which a bank chooses in its binding.

**Done for DE (owner's decision of 2026-11-22).** The `file-bulk` adapter of `p.port.core-banking.v1` (R-12-15), in `runtime/src/TransactHub.Ports/FileBulkCoreBanking.cs`, beside `gateway-rest`. A batch of postings becomes one bulk file in the source's DE layout (`DeBulkFileBuilder`, FSD-PROVISIONAL-2: a header of branch, source code, description and batch reference; one `~`-separated line per leg, D or C, with an extra `~` on a credit line; CRLF; free text cleaned as `DeFieldSanitiser` does); the file goes to core banking's landing folder (`folder`) through the bank's `p.port.file-transfer.v1` (removed, put, read back and compared, as Activity 13); the upload is submitted (`uploadFunctionId`, `uploadUser`, `uploadBranch`, the source's FLEXCALL body); its status is read until core banking answers (`statusPolls`, `statusPollSeconds`; records by `EXTERNAL_REF_NO`, fault 12 "no record" is every line posted, 901 "file not found" is read again, the source's `DeOutcomeReplyReader`); and the lines it failed are posted again in a file of their own, `<name>-R<n>.txt` (`repostAttempts`, `repostCodes`). Idempotent by batch: the file's name is the batch reference, a batch answered once is answered again, and after a restart "File Already Uploaded" is read as taken and its status read instead of a second upload. A status never answered is `PostingFateUnknown`, which the posting activities read as `unknown`, never posted blind. The upload gateway is `gateway-rest` or `simulator` (setting `upload`; the simulator reads the file from the landing folder and fails `simulator.failAccounts`). The posting fact names each line's outcome (`facts.posting.lines`). A bank chooses it by a new binding of `p.port.core-banking.v1` with a later `validFrom`; no workflow changes (DIB's README, "Posting by file"). Tests: `FileBulkCoreBankingTests` (the layout, a repost, a line failed after its reposts, the repost codes, the same batch never uploaded twice across a restart, the status polls and an unknown fate, a foreign currency refused, the gateway's wire, a bank binding it through the posting activity, the directory transfer).

**Done for RRI (owner's direction of 2026-11-22).** The refund flows are orders on the platform's `p.wf.refund.v1`, with WPS as the leg (I-133); `uae.wps.map.rri-in.v1` 1.1.0 is reached by the scheme's `uae.wps.rri` and has a script-behind. The account block is the platform's reservation, not an amount block (I-134 (7)).

**What remains.** For DE: one bulk file per posting batch rather than per salary file (I-132). The `sftp` adapter of the file-transfer port is built (I-129).

## I-83. Factory releases that were edited in place

**Issue.** The factory was first published at 1.0.0 by change set CS-2026-11-21-001. Later work changed 20 of those artefacts, the three pack lock files, the scheme, the inventory, three tests and the example bank's configuration set and lock file, all still at 1.0.0, and `seed-content --write-local` rewrote the published files with them. A change set record lists its members but not their hashes, so nothing on the branch shows a release changed. Two parallel branches also both used the id CS-2026-11-22-001; the merge kept one record.

**Recommendation.** Release every change (R-11-35) and record what each seed publishes with its hash (R-11-36). Version the bank folders' own configuration sets as the example bank's are (`configuration-sets/<release>.yaml`) once the banks agree. Allocate change set ids per branch so parallel seeds cannot clash.

**Done for the engine bindings (owner's decision of 2026-11-22).** Engine bindings are released like every other artefact: each is `p.binding.<target>.v1` at its release, its source `spec/examples/bindings/p.binding.<target>.v1@<release>.yaml`, published at `artefacts/bindings/<name>/<release>.yaml` (seed CS-2026-11-22-007). The forEachConcurrency change is release 1.1.0 of the Temporal, DTFx and Elsa bindings; their 1.0.0 is the text first published, and Flowable's binding is at 1.0.0. A distribution pins its binding's release and hash in its bill of materials. The exception that let a seed replace the bindings file is gone from `tools/validate.py`, the studio's L41 and the seed: a binding release is never replaced (mutation M61; `studio/test/releases.test.ts`, "engine bindings are released like every other artefact"). The old unreleased `artefacts/bindings/bindings.yaml` stays on the branch as a record, and its one replacement, recorded by CS-2026-11-22-005 before the decision, stays accepted as recorded; the studio no longer reads that file once released bindings are on the branch.

**What is built.**
- **The earlier releases, as first published.** Every changed file is back as its first publish had it, from the commit that added it. The changes are new releases (`tools/build_factory.py`, `RELEASES`):
  - minor, 1.1.0: `p.post.accounting.v1`, `p.sim.core-banking.v1`, `uae.wps.exchange.return-credits.v1`, `uae.wps.exchange.submit-salary-file.v1`, `uae.wps.post.return.v1`, `uae.wps.sim.wps-inward.v1`, `uae.wps.sim.wps-outward.v1`, `uae.wps.table.returns.v1`, `uae.wps.sub.confirm-receipt.v1`, `uae.wps.sub.request-refund.v1`, `uae.wps.sub.return-credits.v1`, `uae.wps.sub.submit-salary-file.v1`, the `uae.wps` scheme; with plan P2 `uae.wps.map.pif-in.v1`, `uae.wps.map.ack-in.v1`, `uae.wps.map.rfa-out.v1`, `uae.wps.wf.return-in.v1`;
  - a new major line, at 2.0.0, where the interface check (L26) calls the change a major: `uae.wps.decide.returns.v2` (outcome `unverified` added), `uae.wps.wf.agent-payment-in.v2` (exit write `facts.returns` added), and with plan P2 `uae.wps.map.prc-out.v2` and `uae.wps.map.dif-out.v2` (reads added) and `uae.wps.map.rtc-in.v2` (writes added). Each v1 line stays at 1.0.0; the studio's packs leave a line a later major replaces out of the next pack release;
  - the packs `p`, `uae.p` (its dependency moved), `uae.wps` and the example bank at 1.1.0, the inventory at 1.1.0;
  - the banks: the example bank's configuration set 1.1.0 (`configuration-sets/1.1.0.yaml`), DIB's 1.2.0 and ADIB's, ENBD's and FAB's 1.1.0 accept the 1.1.0 packs; their lock files depend on them (each bank folder's pack 1.1.0). A bank folder's configuration set is the bank's own working file, not seeded, and was changed where it is, as DIB's was from 1.0.0 to 1.1.0.
  - the operations view `uae.wps.view.operations.v1` at 1.1.0: its payment-file leg is `uae.wps.wf.agent-payment-in.v2`, the leg of the scheme at 1.1.0 (its 1.0.0 names the v1 leg). The packs `p` and `uae.wps` at 1.1.0 carry the views; the packs `p`, `uae.p` and `uae.wps` at 1.1.0 carry the error dictionaries too; their 1.0.0 lock files and the inventory, which the views' and the dictionaries' seeds (CS-2026-11-22-003 and 004) had rewritten, are back as first published.
- **The seed adds, never rewrites** (R-11-35). `seed-content --write-local` no longer regenerates a changed file: it stops and names it, as on a connected branch. The bank pack's lock file is the one file a seed replaces, after keeping the earlier one at `packs/<release>.yaml`.
- **Ledgers** (R-11-36). Each seed writes `change-sets/ledger/<id>.yaml`. The ledgers of the six seeds before this (CS-2026-11-21-001, CS-2026-11-22-001 to 005) were recorded from the commits that published them; this work's own seed is CS-2026-11-22-006. The engine bindings (`artefacts/bindings/bindings.yaml`) carry no release, so a seed that changes them replaces the file, as a bank pack's lock file is replaced, and its ledger names the hash it replaces (CS-2026-11-22-005, `forEachConcurrency`); the earlier text is in the branch's history. Recommendation: give an engine binding a release, then publish it beside the earlier one like any artefact; the change set whose record the merge lost is `CS-2026-11-22-001-7d74785.yaml`. Lint L41 (`tools/validate.py`, mutations M54 to M56) and `studio check` hold every recorded file to its hash and every seeded file to a ledger, and a factory artefact at a published release to what was seeded.
- **Not recoverable.** Nothing: every first publish is in the repository's history. The change set records of the earlier seeds are not edited (they are published); their hashes are in the new ledgers instead.

## I-84. What a native code-behind and its script-behind are held to

**Issue.** R-08-16 lets an activity carry a script-behind for the studio and a native code-behind on the runtime. The native ones are the source's parsers and builders, and read the files the source reads; the factory's scripts and simulators were written from public sources and use their own shapes. Three gaps follow:
- the factory's first PIF layout (`PDR` lines, an eleven-field `PCR`, `samples/wps-pif-valid.pif`) is not the source's (`EDR`, `EVP`, `PCR`); the native parser reads the source's only;
- the WPS simulators send acknowledgements and the PSI as a status with no file, so the native ACK reader reads a status there, and the PSI map has nothing to read (OP-A7);
- the native builders write more than the reference (file names, hashes, counts), so the two agree on outcomes and the business trace, not on every write.

**Recommendation.** Move the factory's samples and tests to the source's PIF layout and drop the first layout in the next major of the map. Give the inward simulator files to send (an ACK or NAK file, a PSI), so the native readers are exercised on every engine. Decide whether the agent leg reads the PSI (a step after the await) before the map is wired.

**What is built.** `uae.wps.map.pif-in.v1` 1.1.0's script reads both layouts; WPS-IN-008 runs the source's layout on the studio and, in `e2e-wps-inward-native`, on the native host, with the same trace. The native ACK reader takes a file when the message is one (it starts `AHR,`), else the status. `uae.wps.map.psi-in.v1` and `uae.wps.map.rri-in.v1` are native only and called by no leg.

**On the engines.** The PSI the inward simulator sends with the payment file reached the platform workflow before its leg began, and the engines' leg (a child instance) never saw it. Now a leg takes over what its platform workflow kept for the payment, a message arriving while the leg runs is sent on to it (`studio/engines/shared/Interpreter.cs`), and an inward simulator's opening messages are due a second after the run starts (`studio/src/conformance/party.ts`). The inward payment file then runs on DTFx and Elsa with the studio's trace (`e2e-wps-parity`). On Temporal the platform workflow now hands a kept message to its leg once the leg has started (R-09-16, the engine fix of 2026-11-22), and the cases run green there and on the Flowable REST binding with the store on PostgreSQL (`e2e-wps-temporal`, `e2e-wps-engines-store`), no longer skipped. And a simulator's scenario state is its worker's, one per queue, so a scenario ordered across queues (the core-banking simulator's `reject-first-then-accept` when the return's posting runs on `th-uae-wps`) is not what the interpreter plays: the engine suites compare the inward cases whose calls stay on one queue, and the return through core banking is shown on the runtime.

**Recommendation (engines).** Give the deployed simulators one state per port, not per worker.

## I-85. ADIB's, ENBD's and FAB's core-banking settings are not in the source

**Issue.** By the owner's decision of 2026-11-22, ADIB, ENBD and FAB each have a bank folder in `content-configurations/banks/`, set up as DIB's is. DIB's core-banking binding carries thirteen settings from the source (`CoreBanking:*` in its compose overlay). The three other banks' forks read no `CoreBanking:*` setting: they call core banking through a named HTTP client, and every route they call is the bank simulator's (`/api/simulator/...`). So the source holds no service code, host id, branch, flags, paths or function ids for them. Carrying DIB's values or the simulator's routes would present them as the bank's.

**Recommendation.** Ask each bank for its core-banking gateway settings and add them as a new binding of `p.port.core-banking.v1`, with a later `validFrom`, in its `core-banking.yaml`. If a bank's core banking is not the gateway's shape, write a new adapter for it in `TransactHub.Ports` and bind that.

**What is built.** Each folder binds `p.port.core-banking.v1` to `gateway-rest` with a `.invalid` host and `secretRef: env:TH_<BANK>_CORE_BANKING_TOKEN`, and nothing else, so the adapter refuses every call ("has no '...'") and no payment of the bank reaches a simulator or a guessed host (`PlatformOnlyBankFolderTests`). Each README lists the settings a deployment must add.

## I-86. Whether ADIB, ENBD and FAB run WPS

**Issue.** The factory has one scheme, WPS. The source contradicts itself on ADIB: its customer overlay licenses IPI and FTS only (`LICENSE_RAILS=ipi,fts`), and its bank worker is built without its WPS plugin; but the base database seed subscribes the `adib` tenant to WPS, two generic WPS workflows call ADIB's WPS steps (one pins them to a queue `wps-adib` that no worker serves), and Smart Integration's WPS templates carry `tenantId: ADIB`. ENBD and FAB have no deployment at all in the source: no overlay, no licence, no WPS parameter (no routing code, CBX value, acknowledgement wait, accounting code, SFTP layout or key). The participant directory lists all three as WPS participants, which is what the scheme knows of them, not what they run here.

**Recommendation.** Ask the owner, and then each bank, whether it runs WPS on this platform. For one that does, collect its WPS values as DIB's were (routing code, CBX values, acknowledgement wait, accounting codes, Central Bank SFTP layout, decryption profile), set the rail (`p.table.rail-choice.v1#enabledRails`), and add them to its folder with a `validFrom`. Until then each folder configures the platform only.

**What is built.** Each folder sets `enabledRails` to the empty list, from 2026-11-22, and no WPS value. BANK-RAIL-001 shows a payment that asks for WPS finds no rail, and `studio/test/platform-bank-packs.test.ts` shows a salary file is refused at routing, before anything is sent.

**Done (owner's decision of 2026-11-22): ADIB runs WPS.** ENBD and FAB stay platform only.

- ADIB's folder (`content-configurations/banks/adib/`) carries the WPS values the source holds for it: the rail (`enabledRails` `[uae.wps]`, the base seed's subscription in `infra/docker/mssql/init.sql`); VAT 5 per cent and the four general-ledger names of `dsl/workflows/wps-outward.v2.workflow.json` (the WPS workflow only `AdibWpsActivities` serves); the file transfer the bank's WPS plugin uses (`file-transfer.yaml`: `/wps/inbound`, `/wps/outbound`, `/cb/wps/ack`, `/cb/wps/rtc`, `.invalid` hosts, the credential by `secretRef`); the encryption of its salary files (`encryption.yaml`, I-87). Configuration set 1.2.0 on the 1.2.0 packs; the lock file regenerated.
- Hooks by DIB's rule: the labour ministry notification is bound (`p.wf.corporate.v1#on-close`), since `wps-outward.v2` calls `MohreNotification` with `mohreNotificationEnabled` true; the employer AML check stays built and unbound, since no workflow calls `AdibWpsEmployerAmlCheck`.
- Not carried, with the reason in the README: no routing code (the source has none for the bank), no CBX values, no decryption (the workflow reads files unencrypted and `AdibDecryptSifFile` names no key), the acknowledgement wait (I-116), core banking (I-85).
- Tests: BANK-RAIL-001 (routed to WPS), BANK-RTE-001 (no routing code), BANK-WPS-001 (a salary file end to end: routed, the employer's status asked, encrypted, sent, acknowledged, posted, the ministry told), BANK-WPS-002 (a suspended employer refused, nothing sent); `studio/test/adib-wps.test.ts`; `AdibWpsBankFolderTests` and `EncryptionTests` in `runtime/tests/TransactHub.Store.Tests`.

## I-87. No port encrypts a file

**Issue.** The factory has `p.port.decryption.v1` for files a bank receives, and no port for a file a bank encrypts before it sends it. The source's encryption library has encrypt-only profiles for WPS files (its sample settings give FAB `WPS_SalaryUpload` and `WPS_SIFUpload`, GnuPG, keys by name), so a bank that encrypts its SIF to the Central Bank has nowhere to bind that.

**Recommendation.** Decide whether a file a bank sends is encrypted by the file-transfer adapter (a setting of `p.port.file-transfer.v1`'s binding: profile and key by reference) or by its own port and activity in `p.*`, in one change with a requirement id and a check, together with I-67.

**What is built.** Nothing. FAB's README lists the sample's profiles as not carried.

**Done (owner's decision of 2026-11-22): a file the bank sends is encrypted by a port of its own** (R-03-24, R-12-13).

- A twelfth activity type, `protect`, of effect `read` with at least one port (schema `activity`, mutation M62). `p.port.encryption.v1` (operation `encrypt`), the pair of `p.port.decryption.v1`, with `p.sim.encryption.v1` carrying the standard outward scenarios. `p.protect.encrypt-file.v1` encrypts the outgoing file; its parameter `recipientKey` names the recipient's key by id, and the bank's binding holds the key by reference (`keys.<id>.secretRef`).
- The salary file's send path encrypts first: `uae.wps.sub.submit-salary-file.v1` 1.2.0 (step `encrypt`; a refusal returns `rejected` and nothing is sent). Tests WPS-OUT-001, 003 and 008, WPS-EXC-001 to 003, P-ENC-001 and 002.
- DIB and ADIB bind it (`encryption.yaml`, adapter `openpgp`, the Central Bank's public key by `env:TH_<BANK>_CB_WPS_PUBLIC_KEY`, binary output as the source's profiles wrote it) and set `recipientKey` to `cbuae-wps`. The source has no encrypt profile for either bank, so the id is the folder's own name for the key.
- The runtime's adapters (`runtime/src/TransactHub.Ports/OpenPgp.cs`): OpenPGP through BouncyCastle.Cryptography 2.6.2, pinned; AES-256 with an integrity packet; encryption and its pair, decryption with a secret key ring and passphrase by reference. `EncryptionTests` makes a key pair when it runs and proves the round trip, armour, a tampered file, another key's holder, an unknown key id and a key that is not a reference.
- Not done: I-117.

## I-88. The employer's labour ministry status has no rule

**Issue.** Each bank fork in the source has the same WPS rule step (`{Bank}ValidateWpsRules`): a file needs at least one record, its total must be within the balance, and an employer the labour ministry has suspended is refused. The plan makes the copies one `uae.wps` rule (W6). The factory has the first two (`uae.wps.rule.record-count-matches.v1`, the funds check of `p.reserve.funds.v1`) and nothing for the third: no rule, and no registered field or port that gives the employer's status at the ministry.

**Recommendation.** Add a `uae.wps` rule that refuses a salary file whose employer the ministry has suspended, with the status as a registered extension read by an activity (a lookup behind a port), and its reason code from the scheme's code set once confirmed (`uae.wps` is provisional). Do not add it as a bank rule: it is the same for every bank.

**What is built.** Nothing in the factory. ADIB's, ENBD's and FAB's READMEs list the step as not carried; their `rules/` are empty.

**Done (owner's decision of 2026-11-22): the rule is the scheme's** (R-12-14).

- The source's behaviour (`AdibValidateWpsRules` and its DIB, ENBD and FAB copies): `mohreStatus` `SUSPENDED` fails the file's WPS rules (`WPS_RULES_FAILED`), and the file stops, a reject, not a hold; an empty status passes.
- `uae.wps.rule.employer-not-suspended.v1` rejects with RR04 (regulatory reason; the scheme's own code is not confirmed, so the pack stays provisional), in `uae.wps.ruleset.salary-file.v1` 1.1.0; RR04 is in `uae.wps.errors.wps.v1` 1.1.0 as raised by a rule.
- The status is a declared extension field, `ext.uae.wps.employerStatus` (`uae.wps.ext.salary.v1` 1.1.0), written by `uae.wps.enrich.employer-status.v1` behind `uae.wps.port.labour-ministry.v1`, with `uae.wps.sim.labour-ministry.v1` (`accept`, `suspended`, `not-registered`). The leg `uae.wps.wf.salary-file-out.v1` 1.1.0 asks before the file's checks; an employer the register does not know is not refused for it. Tests WPS-OUT-006 and 007, WPS-ENR-001 and 002, the rule's four examples, BANK-WPS-002.
- The employer AML check stays a bank hook (`b.hook.employer-aml-check.v1`). Not done: I-118.

## I-89. The Flowable binding keeps a run's state in memory

**Issue.** In production mode (I-53) Flowable keeps the flow, the jobs, their locks and retries, and the store keeps the payloads. The binding still keeps in memory what it learnt during the run: the business trace and detail, the attempts, held early messages, the messages each instance has waited for (R-05-43), the hook bindings taken at start and the timers it has placed. A job whose worker crashed comes back from Flowable when its lock expires, and the tests show that, but if the binding's process itself stops, a new one cannot pick up the payments the old one was running: it does not know them. The harness runs one plan and ends, as the other engines' harnesses do.

**Recommendation.** Before a payment depends on the binding in production, keep that state on the payment's fact chain in the store (the trace and detail as facts, held messages in the inbox, the hook bindings with the instance), and let a binding that starts read the server's running instances of its tenant and rebuild its view from the store. Add a test that kills the binding mid-payment and starts another.

**Done (22 November 2026).** The REST binding keeps its state in the store, insert-only, as facts on a subject of its own (`flowable-run:<run key>`, kind `engine-run`; `BindingJournal` in `runtime/src/TransactHub.Engine.Flowable`, R-09-65): before every command that moves the engine it records each instance's trace and detail, its attempts, steps and deltas, held messages, the hook bindings taken at start, the timers it placed, what it has scheduled (now data, not closures) and the command itself; after each worker's answer it records the answer. A binding started with the same run key (`TH_FLOWABLE_RUN`) reads the last fact, rebuilds the claim check from the payloads' facts, follows the live feed again without a second `engine started`, sends the recorded command again (a completion that had reached the server is refused there and taken as done), hands back every job its predecessor had locked (`POST /external-job-api/unacquire/jobs`) and answers a job whose answer it had recorded without calling the worker again. After a restart the claim check records no create, fork, copy or stage twice (a stage is named after its job), and the step recorder no step twice. The run's journal is closed at its end, and its key is refused again. The deploy runner starts a Flowable binding that stopped again with the same key, up to three times. Shown by `FlowableRestartTests` (the binding as its own process, its worker and the store outliving it): killed while the payment waits on the server's timer, and stopped right after a step's answer, in production mode; stopped in virtual mode; each time the payment ends with the same trace and the same step and payload facts, in the same order, as an uninterrupted run, and no worker is called twice. `e2e-wps-flowable-production`: the WPS salary file settles on PostgreSQL after the binding stops at the posting (`TH_FLOWABLE_FAULT=exit:post`). The kit's embedded harness is not a host and keeps its state in memory; what is left is in I-137.

## I-93. Engine runs on a busy machine: Temporal's test server, and workers on shared ports

**Issue.** On Temporal's time-skipping test server, with the store on (`TH_STORE_PROVIDER`), the WPS salary file can fail at its first step: `p.validate.canonical.v1 failed: attempt 1 of validate timed out; a late result is ignored`, at about five seconds of virtual time. It shows on a loaded machine: here `studio/test/blackbox/e2e-wps-temporal.test.ts -t "database on PostgreSql"` fails the same way (load average 16 to 20), without the test bench. With claim checks as local activities (the default, `TH_LOCAL_CLAIMCHECK`, `runtime/src/TransactHub.Engine.Temporal/TemporalEngine.cs`) it failed on every run here: a local activity does not hold the test server's clock while it writes facts to the database. With `TH_LOCAL_CLAIMCHECK=0` it settles on most runs, and still fails on some (2 of the first 9 outward cases of one suite run; in another, a step of the WPS leg failed the same way, `leg:failed`): an attempt that waits on a busy worker races its attempt timer.

**Recommendation.** (1) Decide that a deployment with a store runs its claim checks as regular activities on the test server (or always), in the Temporal binding. (2) Make the attempt timer of a deployment's steps not race the test server's clock: lock time skipping while an activity is outstanding, or warm each worker before the first workflow starts. Each with a test that runs the salary file with the store under an artificial delay on the worker and on each fact write.

**Issue 2.** The deploy runner gives a package's workers ports from 7400 to 7800 at random when it is given none, and takes a worker as up when anything on its port answers `/health`. With several deployments on one machine (CI runners, parallel test sessions), an engine can reach another run's worker: here Elsa on SQL Server failed with `p.validate.canonical.v1@1.0.0 is not registered on queue th-bank`, from a worker of another run.

**Recommendation 2.** Let the deploy runner choose ports the system gives it (listen on port 0, then pass each worker its port), and check that `/health` answers with the deployment's own id. **Done since:** the deploy runner now starts its workers on port 0 and reads each worker's port from its first line; the test bench's own port check is gone.

**What is built.** The test bench sets `TH_LOCAL_CLAIMCHECK=0` for Temporal when the store is on and Temporal is the test server, and runs a Temporal run on the test server whose outcome is not the interpreter's once more, on a fresh database, saying so in the result and the report (`runtime/tools/TransactHub.TestBench/Runner.cs`). A real difference shows on both runs and still fails the run. It retries nothing else. On a real Temporal server (`environment.temporal: host:port`) it does neither. The harness and the e2e test are unchanged.

## I-94. The WPS inward flow cannot run on the engines through the deploy runner

**Issue.** A payment file (PIF) waits for its supplement (PSI), which the WPS inward simulator sends after it. The studio interpreter plays those later deliveries, and settles the factory's inward scenarios (WPS-IN-002 to 007). The deploy runner (`studio/src/deploy/deploy.ts`) never asks a worker for an inward simulator's deliveries (the worker's `POST /start`), so on every engine the leg ends `supplement:missing`, `end:pending`. The test bench's inward profiles show this on Temporal, DTFx, Elsa and Flowable alike; no engine test ran the inward flow before.

**Recommendation.** In the deploy runner, after the front door, ask each worker with inward simulators for their deliveries and hand them to the engine's front door as the kit runner does, with an end-to-end test of WPS-IN-002 on each engine. Then remove `knownIssue: I-94` from `runtime/testbench/suite/TB-WPS-IN-*.yaml`; the suite fails while the mark stays on a case that now agrees.

**What is built.** The inward profiles, samples, benches and cases. The cases pass on the interpreter and are marked as known to differ on the engines.

**Done (23 November 2026).** Two causes, both fixed. (1) The inward profiles ran the leg `uae.wps.wf.agent-payment-in.v1` on its own, while scheme 1.3.0 starts the PIF in `p.wf.inward.v1` with `uae.wps.wf.agent-payment-in.v2` in its slot, as the interpreter runs it: the profiles now name `p.wf.inward.v1`, so the engines run the payment end to end through the front door, as the outward profiles do, and the PSI reaches the leg through the platform workflow, which hands it over (R-05-30). (2) On Temporal's front door workflow, a message due before its root had registered was unmatched and dropped: on a server's clock the PSI, due a second after the run starts, can come first. It is now kept and routed when a run it correlates to registers, and unmatched once every root has begun (`FrontDoorWorkflow`). For files that reach a runtime, the runtime's front door correlates the PSI to its running payment and hands it to the engine host (R-09-85, I-158). TB-WPS-IN-001 and TB-WPS-IN-004 pass on Temporal, DTFx, Elsa and Flowable and carry no mark; TB-WPS-IN-002 and TB-WPS-IN-003 differ on every engine for another reason, a simulator scenario that runs once per worker (I-162), and carry that mark.

## I-95. ISO 20022 messages and canonical payments have no front door

**Issue 1.** No scheme in the factory takes pain.001 or pacs.008 as a start message: the only intake maps are the WPS files'. The owner asked for a test bench that takes ISO 20022 messages and canonical JSON payments. The test bench maps such a message to the canonical context (ids, amounts, accounts and agents; never a party's name or address) and runs the platform workflow from that context, on the interpreter and every engine. That is a parity test, not a test of an intake: no map of the factory checked the mapping.

**Issue 2.** Run from a context, the inward workflow (`p.wf.inward.v1`) with no leg in its slot ends `failed` on the interpreter and `leg:pending`, `end:review` on DTFx (and the other engines). The two treat an empty slot differently.

**Recommendation.** (1) Decide whether the platform takes pain.001 (a corporate channel) and pacs.008 (a scheme) through the front door; if so, add their intake maps and dispatch entries in `p.*`, with tests, and the test bench sends them through the front door like the WPS files. (2) Decide what a platform workflow does with an empty slot, write it as a requirement with a check, and make the interpreter and the engines agree.

**What is built.** The mapping in `runtime/tools/TransactHub.TestBench/Inputs.cs`, the `platform-iso-*` profiles, and three cases; TB-ISO-002 is marked as known to differ (I-95).

**Owner (22 November 2026).** To be designed later.

## I-96. What synthetic test data can and cannot drive

**Issue 1.** The factory's rules add money as binary floating point: `uae.wps.rule.control-total-matches.v1` sums `fixedPay + variablePay` and compares the sum with the control total exactly, so a correct file whose amounts sum to 7894.87 is refused with AM10 (the sum is 7894.870000000001). The factory's samples use round amounts and never show it. A bank's real salary file would.

**Issue 2.** The simulators decide by their scenario, not by the data: a payee named as a sanctions hit is screened clear unless the bench gives the sanctions port the `reject` scenario. Personal data never enters workflow state (`spec/CLAUDE.md`), so no screening activity sees a name. The pack has no rule against a person id repeated within a salary file.

**Recommendation.** (1) Compare money in rules as decimals, or round each sum to the currency's minor unit before comparing, as a requirement with a check and a mutation case. (2) Keep scenarios as the way a bench drives the parties, and say in the simulator spec that a sanctioned-name sample needs its scenario. (3) Ask the business whether a repeated person id in one file is refused, once `uae.wps` is confirmed.

**What is built.** The builder makes amounts in quarters of a dirham, whose sums are exact in binary, so its files are valid unless an error is injected on purpose; the `sanctioned-name` injection names the `sanctions-hit` bench to go with it. The README lists both.

**Done (2026-11-22): money is exact everywhere.** Issue 1 and Recommendation (1) are built; Recommendation (2) is written down; (3) stays open.

- Part 2 states the rule (R-02-17): an amount is an exact decimal, carried in canonical JSON as a decimal string or as a JSON number read by its text (its shortest round-trip form), never by its binary value. One of more than 15 significant digits is a decimal string. Documents written before keep their meaning, since a number of up to 15 digits reads the same either way.
- The rule language computes on exact decimals (R-04-23): `studio/src/core/decimal.ts`, a small fixed-point module on BigInt (no dependency to pin; the same semantics as .NET `decimal` and Java `BigDecimal` for these operations). Literals keep their text; `+ - * %`, comparisons, `sum`, `sumWhere`, `min`, `max` and table bounds are exact; `round2` is half away from zero on the exact value; `/` keeps 20 places. A result of up to 15 digits is a number, a longer one a decimal string. The factory's `uae.wps.rule.control-total-matches.v1` now accepts the 7894.87 file unchanged: no factory artefact changed.
- Scripts get the same arithmetic on `rules.money` (TypeScript) and `api.rules.money` (Python, on `decimal`).
- Engines: rules run in the one rule service (R-04-21), so DTFx, Elsa, Temporal and Flowable compute nothing on amounts; they carry them as received (R-09-47). The Flowable kit harness now reads JSON with exact `BigDecimal`s. The runtime's .NET code was already `decimal` (CBX rules, the SIF writer, the posting activities, the views' `lines.sum`); `OperationsViewTests` proves the sum of the same lines.
- Lint L42 refuses a floating-point tolerance in a rule (`a - b < 0.01`) and a number of more than 15 significant digits in examples, parameters, configuration values and tests (mutation cases M57, M58; the studio's `check` runs the same).
- Conformance scenario CK-MONEY-01: seven lines that sum to 7894.870000000001 in doubles match their control total; `round2` gives 0.13, -0.13 and 2.68 where half-even or binary rounding would not; an amount of 90071992547409.93, whose minor units are 2^53 + 1, is computed and carried exactly; the `amounts` check reads the context each engine ends with. The kit is 51 scenarios.
- Recommendation (2) is in Part 7, 7.3: a simulator decides by its scenario, and a test that means a sanctions hit names that scenario in its bench.
- Not done: the factory's SIF map (`studio/scripts/uae.wps.map.sif-in.v1.ts`) writes amounts as numbers; see I-109.

## I-97. The store holds no end of a payment and no files of a deployment

**Issue.** The operations read model shows what the store's facts say (R-05-49). Two things a WPS operator saw are not facts yet. (1) A payment's end: the outcome the engine reaches (`end:settled`, `end:rejected`) is the engine's, not the store's; the read model shows the ISO status the workflow state last carried and each leg's last outcome, but no end and no duration. (2) Files: the inbound SIF, the SIF sent to WPS and the ACK or NAK are not kept in the store's `message` table on a deployment (the native host keeps the rendered SIF in its own memory, `GET /files`), so the views' file names and the documents tab stay empty, and the old Raw File downloads have nothing to serve.

**Recommendation.** Have each engine binding record an `end` fact on the payment's chain when its root instance ends (outcome, release, time): Temporal's front door has the `Completed` signal, Flowable the process end, DTFx and Elsa their harness's end. Have the front door keep each inbound message, and each exchange activity each file it sends or receives, with `PaymentStore.KeepMessage` and the payment's id. Both are then on the outbox and reach the console with no change to it.

**What is built.** The views name the sources (`message:uae.wps.sif.fileName`, `documents`), the read model projects every `message` row and the API serves its content to the payment's operators; `OperatorApiTests` shows a kept file listed and downloaded.

**Done for (1) (22 November 2026).** A payment's end is a fact: when a payment's root instance ends, the host that runs it records `payment.end` with the outcome on the payment's chain, through the runtime's `POST /payments/end` (R-09-54). It is recorded on every engine (Temporal, DTFx, Elsa and Flowable) by the front door that runs the payment (`studio/src/deploy/front-door.ts`), which is the runtime's front door in this repository; `e2e-distributions.test.ts` ends every payment it runs and `DistributionApiTests` shows a second end changes nothing. A payment is open while it is pinned and has no end, and the open count per distribution is computed from these facts. (2), the files of a deployment, is still open.

**Done for (2), the files of a deployment (owner's decision of 2026-11-22, R-09-64).** Every file a host receives or sends is kept in the store, insert-only, on SQL Server and PostgreSQL (migration `StoredFiles` of both): `stored_file` holds its name, size, direction (`in`, `out`), kind, tenant, payment and leg (the instance), the activity, and the SHA-256 of its bytes signed by the host (ECDSA P-256 over a fixed statement, `FileSignature`; key by `TH_FILE_SIGNING_KEY_REF`); `file_content` holds the bytes once per hash (`runtime/src/TransactHub.Store/Files.cs`). Each file is on the outbox (topic `file`) and the projector makes it a document of its payment, with its leg and the key it is signed by (ops migration `DocumentSource`). The front door keeps the file a payment arrives with (`POST /payments/files`, `studio/src/deploy/front-door.ts`); the native host keeps each file it reads (a map's inbound message), renders (SIF, PRC, DIF, RFA), encrypts or decrypts, when it has a store (`TH_STORE_PROVIDER`, `TH_STORE_CONNECTION`). The operations console's documents tab lists them with direction, leg, size and hash, and downloads them for the payment's operators only (`/ops/api/payments/{id}/documents/{doc}`, bytes checked against the signed hash). Tests: `StoredFileTests` (both databases), `OperatorApiTests` ("A stored file is a document of its payment ...").

**What remains.** The host's signing key at deployment (I-131).

**Done for (2) (23 November 2026).** Every file of a deployment is kept by the File API (I-158, R-09-82 to R-09-84): each file received, as received and as decrypted, and each file a workflow sends through its outbox, with its payment and leg, so the documents tab lists them.

## I-98. A hold answered by the runtime waits in one attempt

**Issue.** A hold reaches the operator through its port, `p.port.operations-queue.v1`, given to the runtime (R-08-15): the native host opens the hold on the payment's chain and the attempt waits for the decision the console records (R-05-50). An attempt holds a connection while it waits, and is bounded (`TH_OPS_HOLD_WAIT_SECONDS`, default 240, under the worker's five minutes): a hold not decided by then ends `expired`, not after the hold's own `expiry` (P1D). The hold also finds its payment by the header's file id or end-to-end id, the newest of that tenant and scheme, because the header carries no id of the store's.

**Recommendation.** Make a hold durable on every engine: the expanded hold block sends the item and awaits `p.ops.decision` (as the studio's reference already does), and the console's decision is delivered to the engine as that message, through the engine host's message path (Temporal's front door `Deliver`, Flowable's message event, DTFx's raised event, Elsa's bookmark). Put the store's payment id on the header when the payment is accepted, and have the hold name it.

**What is built.** The bench path, on all four engines alike since it is an activity's reply: `StoreHold` in `TransactHub.Operations`, the native host serving it when the store is configured, and the end-to-end test (`runtime/ops-console/test/e2e/wps-hold.test.ts`).

**Done (22 November 2026).** A hold is an engine await on the operator's decision on all four engines (R-05-56). The attempt that opens the hold answers `held` with the hold's id and its expiry in force (the native host's `StoreHold` opens it on the payment's chain and answers at once; a simulator's hold in a kit run or a bench answers the same), and the instance awaits `p.ops.decision` until the expiry on its business clock: the shared interpreter (`AwaitDecision` in `studio/engines/shared/Interpreter.cs`) for DTFx, Elsa and Temporal, and on Flowable an event-based gateway after the hold's task that the generator writes (`heldDecision` in `studio/src/export/bpmn.ts`), with the binding setting `th_decision` from the message (the Java harness and `FlowableBinding`). The console's approve, decline, release and reject, the service console's `release-hold` and `expire-hold`, and the runtime's own sweep of holds whose expiry passed (`HoldExpiry`, in the operations host every 30 s) record the decision and put it on the outbox as `decide`; the engine's host delivers it as that message (Issue I-99). `TH_OPS_HOLD_WAIT_SECONDS` is gone: no attempt waits. Evidence: CK-HOLD-01 (a decision after two days) and CK-HOLD-02 (expiry) pass on the interpreter, DTFx, Elsa, Temporal and Flowable; `OperatorActionTests` on both databases; the `wps-hold` Playwright test. The store's payment id on the header is still not used: the hold finds its payment by the file id or end-to-end id as before, and the host follows the payment its root was accepted as. What remains is in I-127.

## I-99. Signals, retry and resend have no path from the runtime to an engine

**Issue.** Operator actions that are signals (`cancel`, `re-screen`, `pause`, `resume`, R-05-12) are recorded and approved as facts, and then put on the outbox (topic `engine-signal`) for the engine's host to deliver; no engine host reads that topic yet, so a running payment is not cancelled. The engines' signal paths exist (Temporal's workflow signal, Flowable's `th.signal.*` messages), but a deployment's instance ids are the engine host's, and the store does not record them. `retry` (an activity that ended in an error the policy did not retry) and `resend` (a file sent again) are not in the DSL at all: no construct takes them.

**Recommendation.** Record each instance's engine id as a fact when it starts (the binding knows it), and give each engine host a reader of the `engine-signal` topic that delivers the signal on the engine's own path and records the result as a fact. Decide in Part 5 whether `retry` and `resend` are operator actions: if they are, a composite's subflow gains a declared retry point and an exchange a declared resend, each with a conformance scenario; until then the views may declare them and the runtime records and refuses them.

**What is built.** `IEngineSignals` with `OutboxSignals`, maker-checker before a signal is sent (`OperatorActionTests`), and the refusal of `retry` and `resend` with this issue's number.

**Done (22 November 2026).** (1) Each instance's engine ids are a fact when the host follows it (`engine started`: engine, instance, run). (2) Every engine host reads the outbox topic `engine-signal` for its payment (`StoreSignals` in `TransactHub.Engines.Durable`, on SQL Server and PostgreSQL): in outbox order, each row once (a `signal delivered` fact per row; the instance ignores a row id it has seen), from where it stopped after a restart (R-05-57). It delivers a signal or an operator's control as the engine's signal (DTFx and Elsa: a raised event; Temporal: a workflow signal; Flowable: the `th.signal.*` or `th.operator.*` message) and a hold's decision as `p.ops.decision`. (3) `retry` and `resend` are operator actions (R-05-55, R-05-58): a step whose policy names `park` waits, parked, for an operator's `retry` (one more attempt) or `redrive` (its full retry policy), or fails at the end of its park; `resend` puts the file the store kept on the file-transfer port again. CK-PARK-01 and CK-PARK-02 pass on every engine; lint L45 and mutation M64 hold a park to a call that may run again, outside a handler, from PT1M to P30D. What remains is in I-125 and I-127.

## I-100. WPS fields of the source's screens with no source in the new model

**Issue.** `docs/runtime/ops-console-wps-mapping.md` maps every field of the source's WPS operations screens to the operations views. These have no source in the new model: the uploading user, the initiating user and the channel (the channel's upload is not in the payment model, and the user is personal data); the employer's name and debit account, which the channel supplied and the SIF does not carry; the six allowances and the leave encashment of a salary line (`uae.wps.ext.salary.v1` registers fixed and variable pay only); the labour ministry's update status (I-88); the employee account's type and bank name (the participant lookup `uae.p.enrich.participant.v1` is reached by no workflow); the NAK's per-line DER records (the ACK map writes the status only); the posting's charge, VAT and total, and its legs (the core-banking adapter keeps them, the posting facts carry its outcome); the RRI, RRR and DCR refund flows (no factory flow); and the dashboard's aggregates. The source also let a privileged user unmask a masked field on screen; the read model keeps only the masked value.

**Recommendation.** Register the allowances and the leave encashment as item fields of `uae.wps.ext.salary.v1` (provisional until the rulebook confirms them) and map them in `uae.wps.map.sif-in.v1`; carry the channel, the employer's name and the debit account from the channel's upload into the bulk at intake; write the NAK's DER records and the posting's charge, VAT and legs as step writes (state, with masked accounts); and add the reports as projections of their own (I-68). Decide whether an unmask is needed at all: if it is, it reads the store, needs a permission of its own, and is a fact on the payment's chain.

**What is built.** Each of these rows is in the mapping as not mapped, with this issue's number; every mapped row is checked against the views (`OperationsViewTests`).

**Done (owner's decision of 2026-11-22: the recommendation, with ISO 20022 homes).** Coverage went from 101 mapped (15 empty) and 27 not mapped to 125 mapped (10 empty) and 3 not mapped of 128 rows (`docs/runtime/ops-console-wps-mapping.md`, checked by `OperationsViewTests`).

- Canonical model (studio `studio/src/core/model.ts`, runtime `TransactHub.Model/Iso20022.cs`): `batch.initiatingParty.id` (GrpHdr/InitgPty, an id only, personal data, R-02-18), `batch.debtor` and `batch.debtorAccount` (Dbtr/Nm, DbtrAcct), an account's `type` (CdtrAcct/Tp), an agent's `name` (FinInstnId/Nm), `status.transactions[]` (pain.002 TxInfAndSts, R-02-19); the store keeps the initiator as a party and the account type (migration `WpsConsoleFields` on SQL Server and PostgreSQL).
- `uae.wps.ext.salary.v1` 1.1.0: the seven EVP allowance fields (item level, personal data, RmtInf/Strd semantics, provisional), `uploadChannel` and `employerStatus` (declared extensions, SplmtryData semantics).
- `uae.wps.map.sif-in.v1` 1.1.0 reads the EVP record and what the channel declared beside the file at upload; amounts as decimal strings (I-109). `uae.wps.map.ack-in.v1` 1.2.0: the runtime's ACK reader writes each DER record as a TxInfAndSts (`WpsAckNakTests`). The runtime's posting writes camt.054 entries (accounts to their last four characters), the total debited and ChrgsInf with its tax (`CoreBankingPostingTests`).
- Views: `uae.wps.view.operations.v1` 1.2.0 and `p.view.operations.v1` 1.1.0; the read model projects the initiator, the account type and the agent's name. The dashboard's counts are the list filtered.
- The payment's end and its duration (the `payment.end` fact of I-97, less the acceptance) are projected into the platform view (`endedAt`, `endOutcome`, `duration`).
- Left: the account type and bank name have no writer (I-115). The RRI, RRR and DCR flows are built as a refund order (I-133). The unmask stays not built.

## I-101. Troubleshooting actions with no engine path from the runtime

**Issue.** The service console (R-09-43, design/05 section 5.11) offers seventeen troubleshooting actions and three content actions. Each needs a person's confirmation and a reason, is a fact on the console's chain (and on the payment's, when it acts on one), and the risky ones wait for a ServiceApprover. Nine reach nothing today, because no path from the runtime to a running engine instance exists:
- `retry-attempt` and `resend-file`: not in the DSL; no construct takes them (I-99).
- `redrive-workflow` and `reset-activity` (Temporal): the store does not record an instance's engine ids (the Temporal workflow and run ids, the Flowable process instance, the DTFx instance, the Elsa instance), and no engine host has a re-drive or reset call the runtime can reach.
- `expire-hold`: the attempt that waits for a hold ends it only on a decision or its own timeout (I-98); it reads no expiry fact.
- `reconcile-posting`: the core-banking port (`p.port.core-banking.v1`) has no inquiry operation, and the gateway adapter (`GatewayRestCoreBanking`) no inquiry call, so an unknown posting cannot be asked about.
- `pause-intake` and `resume-intake`: the front door reads no intake switch per scheme.
- `drain-worker` and `restart-worker`: a deployed worker exposes no drain control, and the runtime has no path to the installation's orchestrator.
`re-screen` reaches the outbox (topic `engine-signal`) and stops there (I-99).

**Recommendation.** (1) Record each instance's engine ids as a fact when it starts (I-99), and give each engine host a reader of `engine-signal` that carries `re-screen`, a re-drive (Temporal: reset to the failed activity's task; Flowable: move the failed job; DTFx: rewind; Elsa: resume the faulted activity) and records the result as a fact. (2) Decide in Part 5 whether `retry` and `resend` are operator actions; if so, a composite's subflow gains a declared retry point and an exchange a declared resend, each with a conformance scenario. (3) Have the hold block await an expiry message as it awaits a decision (I-98). (4) Add an `inquire` operation to the core-banking port, a simulator scenario and the gateway adapter's call. (5) Give the front door an intake switch per tenant and scheme, read from the store's versioned values (`kind: intake`). (6) Give each worker a `POST /drain` and a `GET /health` with its version and queue; leave restarts to the orchestrator and link to it.

**What is built.** Every action's permission, confirmation, approval and audit, end to end (`ActionTests`); the refusal names this issue. Built end to end: `release-hold` (the hold's decision on the payment's chain, which the operations-queue port reads), `requeue-dead-letter` (Flowable's management API), `rebuild-read-model`, `replay-read-model` (the projection from its checkpoint), `reverify-package`, `rotate-trusted-key` (written where `TH_TRUSTED_SIGNERS` names a writable file and `Service:TrustWritable` is true), and the content actions `activate`, `rollback` and `remove`.

**Done (22 November 2026).** All seventeen troubleshooting actions are built (`ActionBook.Catalogue`, every path `built`); none is refused for want of a path. `re-screen`, `retry-attempt`, `redrive-workflow` and `reset-activity` go to the payment's engine host on the outbox (I-99): the last three need a parked step (R-05-55) and are refused otherwise. `expire-hold` records the hold expired and sends the decision (I-98). `resend-file` sends the kept bytes again on the file-transfer port, idempotent by the file's id (`FileResend`, `FolderFileTransfer`, R-05-58). `reconcile-posting` asks core banking by the posting's reference through the port's new `Inquire` operation (simulator and gateway adapter, R-08-19): posted, the parked posting step is re-driven; not posted, it is retried; the answer is a fact. `pause-intake` and `resume-intake` set the intake switch per bank and scheme (`IntakeSwitch`, insert-only facts); the runtime refuses a new payment of a paused scheme (423, `ops.IntakePaused`) and the front door names the scheme and may keep the payment and offer it again (R-09-62). `drain-worker` and `restart-worker` call the worker's `POST /drain` and `POST /restart`; a worker that drains answers new attempts 503 and the engines' router offers them again, and one that restarts exits once idle and the deploy runner starts it again from the same package on the same port (R-09-63). Evidence: `ActionTests`, `ServiceApiTests`, the `service-console` Playwright test (the catalogue all built; a pause of intake and a worker's drain approved by a ServiceApprover). What remains is in I-125 and I-128.

## I-102. Distributions, configuration releases and the open payments of a release

**Issue.** (1) The service console lists every kind the package source offers (R-09-40), but installs only workflow packages: a distribution (`dist/<bank>/<id>`) or a configuration release has no runtime model yet (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, section 10, items 1 and 2), so installing one answers 501 with this issue. (2) A release's state (installed, active, draining, drained, removed) is derived from the activations and the console's facts: the store has no state column and no `removed` table, and the five states of the packaging document are not enforced by the engines (a removed release's workers keep running). (3) The open payments of a release are those whose ISO status in the read model is not final (ACSC, ACCC, RJCT, CANC), because the store holds no end of a payment (I-97). A payment records the release id the engine gave it; a deployment gives the workflow's name, so such a payment is counted for that workflow's release active when it was accepted, by the activation rows, which is an inference.

**Recommendation.** Build items 1 and 2 of section 10 of the packaging document: store distributions and configuration releases insert-only, pin `distributionId` on every payment at acceptance, record each state change as an activation row, and have each engine host stop a removed distribution's workers. Record an `end` fact per payment (I-97) and count open payments from it.

**What is built.** Listing of every kind, verification, the difference against what is active (flows, files and parameters), install, activation from a date with the previous release recorded as draining, rollback, removal refused while active or while a pinned payment is open, and the open count per release (`ContentTests`, the `service-console` Playwright test).

**Done (22 November 2026).** Items 1 to 3 of section 10 of the packaging document are built (R-09-48 to R-09-57, R-11-37 to R-11-40). The studio builds and signs a bank's distribution and configuration release and publishes each with its tag; the runtime stores both insert-only after verifying their signature and kind, activates a distribution per bank and environment with the old one draining in the same transaction, records the five states as insert-only facts, pins `distributionId` on each payment at acceptance, records each payment's end (I-97), refuses removal while a pinned payment is open or before the longest declared wait has passed, and rolls back. The service console lists, verifies, diffs, installs, activates, drains, removes and rolls back both kinds, and counts open payments per distribution exactly; the 501 refusal is gone. A workflow package's open payments are still counted from the read model's status, as before. What is not built is in I-114.

## I-103. What the runtime's errors and logs do not carry yet

**Issue.** (1) The runtime's libraries raise exceptions with a message only: `SignatureRejected`, `PackageRejected`, `ActionRefused`, `ReleaseConflict` carry no error code, so the service console classifies them by their words (`ErrorCodes.Of`), which breaks if a message is reworded. (2) A step fact records a reason code and an error name when the worker gives them, but an engine's own failure (a Temporal workflow task failure, a Flowable dead-letter job, a DTFx or Elsa fault) is not a fact: the console sees it only through the engine's own console. (3) The service console's log viewer reads its own host's structured logs (masked, in memory) and the store's facts; the other hosts' logs go to the installation's log back end over OTLP, which the console does not query, and the facts carry no trace id (I-80).

**Recommendation.** (1) Give each refusal type a `Code` property set where it is thrown, from the dictionary's codes, and have `ErrorCodes.Of` read it; keep the words as the message only. (2) Have each engine binding record an `engine.failed` fact with the dictionary's code when an instance fails or a job is dead-lettered. (3) Add a read-only log source to the service console for the installation's back end (a Loki or OpenSearch query by trace id and time), configured like a probe, and add the trace id to the facts (I-80).

**What is built.** `ErrorCodes`, the classification and its test, the masked ring buffer of the service host's logs, and the viewer over the store's facts with filters by error code, leg, scheme, tenant, payment and time, each error linked to its runbook (`ServiceApiTests`).

**Done for (1) (22 November 2026).** Every refusal type carries the dictionary's code, set where it is thrown: `SignatureRejected` (each `ReleaseSignature.Verify` refusal names its own: not signed, untrusted, revoked, retired, wrong release, trust list invalid, else tampered), `PackageRejected`, `ReleaseConflict`, `InsertOnlyViolation`, `DistributionRefused`, `ActionRefused` (every throw names its code) and `IntakePaused`. `ErrorCodes.Of` reads the code and nothing else; the words are the message only (`ErrorDictionaryTests`: a refusal's words no longer change its code). The service console's audit facts and event log record the code. (2) and (3) are not built: an engine's own failure is not an `engine.failed` fact, and the log viewer reads no other host's logs. What remains is in I-126.

**Done for (2) and (3) (23 November 2026).** (2) When an engine fails a payment, the long-running engine host (`runtime/tools/TransactHub.EngineHost`, `DistributionHost`) records a fact `engine failed` on the payment's chain with the dictionary's code of that engine (`temporal.WorkflowTaskFailed`, `dtfx.OrchestrationFailed`, `elsa.WorkflowFaulted`, `flowable.DeadLetterJob`), the instance, the distribution and the engine's reason cut short (R-09-70, `EngineFailures` in `TransactHub.Store`); the service console shows it with its runbook. (3) The service console reads the other hosts' logs, read only, from the installation's back end (`Service:LogSource:*`, Loki), by trace id or a time window, masked, for an engineer who sees every tenant; a back end that does not answer is an item with `service.LogSourceUnreachable` (R-09-71, `LogSourceTests`). Facts carry their trace id (I-80), so `?trace=` finds a trace's facts, this host's logs and the other hosts' lines together. Not built: the per-payment test harnesses (`TransactHub.*Harness`) record no `engine failed` (they report the failure to their caller), and an OpenSearch source; I-142.

## I-104. The assistant sends masked data to the model provider

**Issue.** The service console's assistant (R-09-44) sends the question, the selected payment's masked trace and facts, masked logs, dictionary entries and document passages to the Anthropic API with the studio's key (`TH_ANTHROPIC_API_KEY`). Masking replaces every value the store holds for the parties the engineer may see (names, accounts, IBANs, the extension fields that name a person or employer) and the telemetry's patterns; a test proves no party value leaves (`AssistantTests`). It cannot mask what the store does not know: a name a person types into a free-text remark, or one inside a scheme's file kept as a message (which the assistant is never given). Whether a bank may send even masked operational data to a model provider is the bank's decision, not the runtime's. The assistant is off unless `TH_ASSISTANT_ENABLED=true`.

**Recommendation.** Ask each bank's data protection officer to approve the assistant before it is switched on, with the list of what is sent (Part 9, R-09-44) and the provider's retention terms; record the decision in the bank's configuration release. Keep it off by default. The free-text fields of operator and service facts (`remarks`, `why`, `reason`) are not sent at all; keep it so.

**What is built.** The switch, the masking and its test, read-only tools, citations, proposals that a person confirms on the Actions page, and a fake model for the tests: no test calls the API.

## I-105. The cloud secret providers are proved against a fake and LocalStack, not the real services

**Issue.** The Azure Key Vault provider is tested against an in-process fake of the Key Vault REST protocol and the token endpoint (`FakeKeyVault`, `fake-key-vault.ts`), and the AWS providers against LocalStack. Neither proves a managed identity, a workload identity or an IAM role against the real service, nor Key Vault's and AWS's real refusals, throttling and private endpoints. The fake is pinned to the protocol as the SDK spoke it on 2026-11-22. LocalStack is pinned to 4.14.0, the last image that runs without an account token; later images (2026.x) need one, so it gets no fixes. This repository's CI has no Azure subscription or AWS account, and must hold no real credential.

**Recommendation.** Before a bank's first use of `akv:`, `aws-sm:` or `aws-ssm:`, run the providers' tests once against the bank's own vault or account, in the bank's environment, with its identity, and record the result in the installation's acceptance. Replace LocalStack with Moto (moto_server, Apache 2.0) or an account-token LocalStack if 4.14.0 stops matching the SDK.

## I-106. A rotated secret reaches a running host only after the cache time or a restart

**Issue.** The interface caches a value read over the network for up to `TH_SECRET_CACHE_SECONDS` and can read a refused credential again (`WithSecret`, R-06-27), but no adapter calls `WithSecret` yet: the core-banking gateway and the package source resolve their credential and, when it is refused, fail the call. A credential rotated in its store is therefore used only once the cache time has passed. The studio's signing key, when held in a store over the network, is read at start and kept until the studio restarts (I-74), so a rotated signing key needs a restart.

**Recommendation.** Have each adapter that sends a credential (the core-banking gateway, the GitHub package source, the worker's runtime token) call through `WithSecret`, with a 401 from the far side as the refusal, in the change that next touches each adapter, with a test that rotates the credential in the store. Re-prime the studio's signing key when the cache time passes, or document a restart as part of rotating it.
## I-107. Items side by side on Flowable: a stop, and the binding's concurrency

**Issue.** On Flowable a `forEach` with `concurrency: n` is a sequential multi-instance of parallel chunks. The kit harness and the REST binding now hold each item's trace entries and put them into the trace in item order before anything else the instance records (R-05-53, CK-FOREACH-03 passes). Two things differ from the shared interpreter. (1) Where an item stops the `forEach` (`onItemFailure: stop`), the other items of its chunk have already run, and their entries are kept; the interpreter keeps none after the stopping item. No kit scenario has a stopping item with concurrency. (2) The Flowable binding has no `forEachConcurrency`: a `forEach` that declares none runs item by item, as before. In production mode the binding moves an attempt's timer boundary out of the way only when it next lists the server's timers (every poll, 50 ms by default); an attempt whose job waits longer than its timeout before that is still timed out by the queue.

**Recommendation.** (1) Drop the entries of items after the first stopping item at the chunk's end, as the interpreter does, with a kit scenario of a stopping item at concurrency 3 on every engine. (2) Generate the chunks of a `forEach` that declares no concurrency from the binding's `forEachConcurrency` (studio/src/export/bpmn.ts, from the binding the export writes), once (1) holds. For the timer, generate the attempt's timer boundary with a duration from a variable the binding sets when it takes the job, so the server never starts it early.

**Done (22 November 2026).** (1) An end or a failure inside an item of a `forEach` whose items run side by side and stop it marks the stopping item first (`itemStop`, `studio/src/export/bpmn.ts`); the embedded harness and the REST binding then keep no entry of a later item, as the interpreter does (R-05-53). New kit scenario CK-FOREACH-04 (three items at concurrency 3, the second stops the `forEach`) passes on the interpreter, DTFx, Elsa, Temporal and Flowable; the kit is 56 scenarios. (2) Flowable's binding 1.1.0 (`p.binding.flowable.v1`) sets `defaults.forEachConcurrency: 64`; the generator chunks a `forEach` that declares none from it where R-09-45 allows (every step a call of an activity that holds no state, handling an outcome only by ending), for the export and the kit alike, so CK-FOREACH-01 runs its items side by side on Flowable too. The WPS salary file's record checks run in one parallel chunk: `e2e-wps-flowable-production` settles a file of forty lines on the REST server in production mode with the trace of every other engine. The attempt timer is still moved out of the way when the binding next lists the server's timers (I-54); a step record of an item after the stopping one is still in the REST binding's `steps` (the trace is not), see I-137.

## I-108. What a line costs on Temporal, and the 50 lines a second

**Issue.** After I-77 and I-78 a line of a WPS salary file costs about 0.06 CPU seconds across the Temporal server, the harness, the workers and the database (`docs/runtime/LOAD-TEST.md`): two activity attempts, each with a claim-check commit as a local activity (another workflow task), a staged-write fact, a commit fact and a step fact. The proposed 50 lines a second sustained therefore needs about three dedicated cores for these processes alone; the shared 4-vCPU machine of the runs gave 25 to 29 lines a second with four to eight files at once. A single file is held by its one workflow: its workflow tasks run one after another, about 17 lines a second here.

**Recommendation.** Measure on a reference installation before deciding (I-68). If the target is not met there: commit an item's staged writes with its attempt's result (one claim-check call per item, not per attempt), or check a chunk of lines in one activity attempt (one attempt per chunk, R-09-13), so a file's cost grows with its chunks; give Temporal more history shards than the four of the harness's server; and run more than one worker per queue.

**Decided (22 November 2026).** The owner accepted 50 lines a second as the go-live target, with each runtime node sized at four or more cores, to be confirmed by a load test on the bank's hardware.

## I-109. The factory's SIF map writes a large total as a number

**Issue.** R-02-17 says an amount of more than 15 significant digits is a decimal string in canonical JSON. The factory's SIF intake map (`studio/scripts/uae.wps.map.sif-in.v1.ts`, behind `uae.wps.map.sif-in.v1`) parses each amount with `Number` and writes `batch.controlSum`, `fixedPay`, `variablePay` and `instructedAmount.value` as JSON numbers. Every amount below 10^13 dirhams has at most 15 digits and is exact, so no realistic salary file is affected; a control total of 10^13 or more would lose its last digits before any rule reads it.

**Recommendation.** In a new release of the map (the factory's RELEASES in `spec/tools/build_factory.py`, after the WPS 1.1.0 work), write each amount with `rules.money.str` (or keep the SIF's own text) and compute `instructedAmount.value` with `rules.money.add`. Add a sample with a 16-digit total to the map's round-trip tests.
## I-111. The bank packs' lock files do not list the schemes a bank licenses

**Issue.** A distribution carries the schemes its bank licenses (R-11-37), but no lock file on the content branch has `licensedSchemes` yet: the bank packs under `content-configurations/banks/` were written before the list, and the example bank's (`spec/examples/bank/pack.yaml`, from `tools/build_factory.py`) depends on `p` only although its configuration set sets WPS dials. A published lock file is replaced only by a later release with its ledger (R-11-35, R-11-36), so the list was not added in place.

**Recommendation.** Add `licensedSchemes` to each bank's lock file in its next release (and to the example bank pack in `tools/build_factory.py`), and then make it required. Until then the studio reads a lock file without the list as the scheme scopes it depends on and those its configuration set sets dials of (`licensedSchemes` in `studio/src/export/distribution.ts`); every bank in the repository then licenses `uae.wps`.

**Done (23 November 2026), as recommended.** Every bank's lock file names the schemes it licenses, in a new release: the example bank's from `tools/build_factory.py` (`LICENSED_SCHEMES`, pack `b` 1.5.0, the earlier kept at `packs/1.4.0.yaml`), and DIB's, ADIB's, ENBD's and FAB's from `npm run studio -- bank-pack <bank> --licensed uae.wps --release 1.5.0 --write`, which keeps the earlier lock file at `banks/<bank>/packs/1.4.0.yaml` and refuses to change a lock file at its own release. The list is now required (R-11-37): L35 refuses a bank pack without it, or with a scheme the factory does not hold (mutation M69), and the studio refuses to build that bank's distribution; the fallback reading is gone. Their configuration sets moved their pins to the 1.5.0 packs (only the pins and their release numbers changed). A bank's list changes only by a release of its lock file (`tools/build_factory.py` for the example bank, `bank-pack --licensed` for the others): the studio has no editor for it, and a scheme imported for a bank (the import's training bundle) is licensed only once that release is made. A bank configuration that sets the dials of a scheme the bank does not license is now refused at its configuration release.

## I-112. A leg writes what its slot contract does not list

**Issue.** The slot-contract check (R-11-38, L43) compares a leg's outcomes and reads with its slot's contract. It does not compare writes: `uae.wps.wf.agent-payment-in.v2` writes `facts.returns`, which the `scheme-leg` slot of `p.wf.inward.v1` does not list among its writes. Checking writes would refuse every distribution today.

**Recommendation.** Decide whether `facts.returns` belongs to the inward slot's contract. If it does, release `p.wf.inward` with it (a contract change is an interface change: a new major, and the slot contract becomes `scheme-leg@2`); if not, release the leg without it. Then extend L43 and `checkSlotContracts` to writes.

**Built (22 November 2026): the check.** L43 and `checkSlotContracts` compare a leg's exit writes with its slot's writes too: a write beyond the slot's writes and the scheme's own extensions refuses the leg in `tools/validate.py` (mutation M68), in the studio's checks and in a distribution's build (`studio/test/distribution.test.ts`). The content decision this recommendation asks for is not made here, so the one write it names, `facts.returns` of `uae.wps.wf.agent-payment-in.v2` in `scheme-leg@1` of `p.wf.inward.v1`, is reported as a warning and let into a distribution until the owner decides: I-141.

## I-113. Who switches distributions on, and which engine a bank runs

**Issue.** Building distributions and configuration releases on approval is a setting (`distributions: true` in `governance/settings.yaml`, off by default), so that a publish that does not want them is unchanged; a test environment's distributions carry simulators (`testDistributions: true`). The distributions built on approval are for the settings' engine target, one engine for every bank: no document records which engine a bank runs, so scenario 9 (a bank moves engine) builds the other engine's distribution on its own (`POST /api/distributions` with `engine`).

**Recommendation.** Owner to decide whether distributions are on by default once every bank's lock file lists its schemes (I-111), and to record each bank's engine (and environments) in its bank pack, so that an approval builds each bank's distribution for its own engine.

**Done (owner's decision of 2026-11-22).** Building distributions and configuration releases on approval is on by default (`DEFAULT_SETTINGS.distributions` true, `studio/src/core/store.ts`); `distributions: false` in `governance/settings.yaml` turns it off, and the settings always record the choice (`studio/src/export/git.ts`). R-11-40 says so. Which engine each bank runs stays one engine target for all banks (the second half of the recommendation is not decided).

**Decided (23 November 2026).** Temporal is the default engine, on the Temporal SQL Server plugin (`runtime/temporal-mssql`): the engine host's supervisor runs Temporal unless another engine is named, a test bench run whose profile and request name no engines runs on Temporal (`wps-default-engine`), and the laptop stack runs it on SQL Server (R-09-79, `deploy/laptop`). From the test bench a tester still starts DTFx, Elsa or Flowable for a run, in the request or by ticking it in the web app (the Playwright test does). Which engine each bank runs in production is still the settings' one target.

## I-114. Side by side, as the harnesses run it

**Issue.** Each distribution runs as its own copy: the front door keeps one verified copy per distribution, and the engine names what it registers after it (on Temporal the task queues and the worker build id, on DTFx the orchestration's name, on Elsa the workflow definition, on Flowable the deployment). But each payment's run is its own harness process: no long-running engine host serves several distributions at once, Temporal's worker-versioning rules (a build id set per task queue on a server) are not used, the time-skipping test server does not support them, and a removed distribution's workers stop because nothing starts them, not because a host unregisters them. The longest declared wait is the largest bound or default of a duration dial named a wait, a timeout or a hold; an `await` with a literal timeout is not counted. Scenario 6 (a new scheme) is shown only as far as one scheme allows: every distribution lists only its bank's schemes, and a leg naming a slot contract the platform does not offer refuses the approval.

**Recommendation.** When the runtime's engine hosts become long-running (I-89, I-101), give each a registry of the distributions it serves, started at activation and stopped at removal, with Temporal worker versioning on the real server; count an await's literal timeout in the longest wait (or make every wait a dial, as the DSL asks); and add a second scheme pack to the factory's tests so that scenario 6 builds a distribution for a bank that licenses it and not for one that does not.

**Done (2026-11-22).** `uae.wps.map.sif-in.v1` 1.1.0 (`studio/scripts/uae.wps.map.sif-in.v1@1.1.0.ts`) reads every amount from the file's own text into exact minor units and writes it as a decimal string with two places; a line's amount is the exact sum of its fixed and variable pay. WPS-MAP-004 maps a sixteen-digit total (`samples/wps-sif-large-total.sif`) with every digit. The runtime's `CanonicalMapper` reads an amount given as a string.

**Done for the switch-over (22 November 2026).** The engine host is long-running (`runtime/tools/TransactHub.EngineHost`, R-09-66 to R-09-68): `supervise` reads a bank's distributions and their states from the store, starts a host per distribution that is installed, active, draining or drained, makes the active one's host current on the engine, records a draining one drained once none of its payments is open, and stops a removed one's host after it unregisters the distribution from its engine; it pins each payment it is given at acceptance and hands it to the host of its distribution. On Temporal each host runs its distribution's workers for as long as it runs, as a version of the bank's worker deployment (`transacthub-<bank>-<environment>`, build id the distribution's id) on the same task queues, workflows pinned (`DistributionWorkers`); activation sets the version current on the server, removal stops the workers and deletes the version. `EngineHostTests` on a real Temporal server (the CLI image's development server) shows a payment pinned to A running every workflow on A's version after B is current, A's version draining on the server, new payments on B, removal refused while the payment is open and A's version deleted once it ended and A was drained and removed. The same test on DTFx, Elsa and Flowable shows the switch-over through the same supervisor: there each payment runs on the engine's runtime harness inside the distribution's host, the engine naming its orchestration, definition or deployment after the distribution. What remains is in I-138.

## I-115. A line's account type and bank name have no writer

**Issue.** The model has homes for both (CdtrAcct/Tp, CdtrAgt/FinInstnId/Nm; Issue I-100) and the WPS view shows them, but nothing fills them. The SIF carries no account type (the source's screen showed a constant, "Own bank account"). A bank's name is reference data by routing code: shared content may not name a bank, the participant directory port's simulator answers a status and no data, and the runtime has no adapter for the directory.

**Recommendation.** Build a participant directory adapter on the runtime that answers a member's name by routing code, and call it in a new release of `uae.p.enrich.participant.v1` that writes `creditorAgent.name` (a major: writes added). Decide with the scheme whether the account type is the PIF's EDR account type or a classification (own bank, other bank); if the latter, it is derived in the view, not stored.

**Done (owner's decision of 2026-11-22, R-12-18).** `uae.wps.map.sif-in.v1` 1.2.0 (`studio/scripts/uae.wps.map.sif-in.v1@1.2.0.ts`) writes each line's bank name (`items[].payment.creditorAgent.name`) from the scheme's participant directory by the line's routing code, and its account type (`items[].payment.creditorAccount.type`) where the channel declared one at upload (`# account-type:`); the SIF itself carries none. The directory is the table `uae.wps.table.participant-names.v1` in the scheme pack, whose rows are its parameter `participants` (routing code and name), a value looked up by date. Its default is empty, since shared content names no bank (L09); the example bank sets two synthetic participants. Test WPS-MAP-005; the table's two examples.

**What remains.** Where the real directory comes from (I-130).

## I-116. ADIB's WPS values the source does not hold, or holds outside the bounds

**Issue.** ADIB runs WPS (I-86), but the source holds no WPS routing code, CBX value, decryption profile or Central Bank bank code for it. Its WPS workflow waits for the acknowledgement 20 minutes (60 s between polls, 20 polls; the step's 1200 s timeout), below the bound of `uae.wps.exchange.submit-salary-file.v1#ackWait` (PT1H to P7D). The four general-ledger "accounts" of the workflow (`CB-NOSTRO-WPS` and the others) read as names, not account numbers.

**Recommendation.** Ask the bank for its routing code, its CBX values and its acknowledgement wait, and whether the four names are its general-ledger accounts. Until then the folder leaves the routing code unset (a payment file to the bank is refused, BANK-RTE-001) and the wait at the factory default (one business day). Do not widen the bound for one bank's poll loop without the scheme's word.

**Done for the acknowledgement wait (owner's decision of 2026-11-22).** The factory's minimum for `uae.wps.exchange.submit-salary-file.v1#ackWait` is fifteen minutes (`PT15M`, release 1.2.0; the maximum stays seven days), and ADIB's configuration set 1.3.0 sets the source's twenty minutes, elapsed time (`ackWait` `PT20M`, `ackWaitCalendar` `elapsed`). Test BANK-WPS-003 (no acknowledgement: pending after twenty minutes). The routing code, the CBX values and the meaning of the four general-ledger names are still to come from the bank.

## I-117. Only the salary file is encrypted

**Issue.** The owner's decision (I-87) is that outgoing files are encrypted. The factory encrypts the SIF only. The refund request (RFR), the receipt confirmation (PRC), the disbursement file (DIF) and the returns file (RFA) are sent unencrypted; no check refuses a send of a file that was not encrypted. The encryption block has no native code-behind, so on the runtime the encrypted bytes are not yet what the file-transfer adapter sends (the native host keeps the rendered SIF in memory, I-97), and DIB's decryption binding names a `gnupg` adapter this runtime does not build (I-67).

**Recommendation.** Encrypt each outgoing file in a new release of its subflow, then add a lint that an exchange of mode `send` or `report` on a `wire: file` scheme is preceded by a `protect` step, with a mutation case. Give `p.protect.encrypt-file.v1` a native code-behind on `OpenPgpEncryption` once the native host keeps files in the store, and bind DIB's decryption to `openpgp` with its key ring by reference.

**Done (owner's decision of 2026-11-22, R-12-17).** Every outgoing WPS file is encrypted before it is sent: new releases of the subflows of the receipt confirmation (`uae.wps.sub.confirm-receipt.v1` 1.2.0), the disbursement file (`uae.wps.sub.report-disbursement.v1` 1.1.0), the returns file (`uae.wps.sub.return-credits.v1` 1.2.0) and the refund request (`uae.wps.sub.request-refund.v1` 1.2.0), each with a step `encrypt` whose refusal returns `rejected`, so nothing is sent; the salary file was already. Lint L46 (`tools/validate.py` and the studio's checks; mutation M65): on a scheme whose wire is a file, a send or report exchange comes after a protect step. The `gnupg` decryption adapter (`GnuPgDecryption`, `runtime/src/TransactHub.Ports/OpenPgp.cs`): the source's key folder (`keyFolderRef`, by reference; `.asc`, `.gpg`, `.pgp`, `.key` read before each decryption), the passphrase by `secretRef`, BouncyCastle as for `openpgp`; DIB's binding now builds. Native code-behinds (R-08-16): `p.protect.encrypt-file.v1` 1.1.0 encrypts the file the host rendered and keeps the encrypted one as the file sent (`facts.encryption` names it, `<name>.gpg`); a new `p.protect.decrypt-file.v1` 1.0.0 decrypts a received file through the bank's decryption binding and keeps both (`runtime/src/TransactHub.Activities.Platform/Native/FileProtection.cs`). Tests WPS-IN-001 to WPS-IN-009 (WPS-IN-009: a refused receipt confirmation is never sent), P-DEC-001, P-DEC-002; `FileProtectionAndRegisterTests`.

**What remains.** No leg or front door calls `p.protect.decrypt-file.v1` yet: it is declared `invokedBy: front-door`, and the front door still hands the file over as received (the refund flows and the inward intake that would decrypt are later work). The encrypted bytes are what the host keeps as sent; the transfer to the Central Bank's SFTP is I-129.

**Done: decryption is called (23 November 2026).** The File API decrypts every encrypted file on arrival through `p.protect.decrypt-file.v1`'s native code-behind and the bank's decryption binding, before the front door reads it, and keeps both files (R-09-83, I-158). The refund flows' inward files reach it the same way once they arrive through the File API.

## I-118. The labour ministry status comes only from a simulator

**Issue.** `uae.wps.port.labour-ministry.v1` has a simulator and no runtime adapter, and the scheme's reason code for a suspended employer is not confirmed (RR04 is the ISO code the factory uses). The source read `mohreStatus` from its input without saying where it came from.

**Recommendation.** Agree with each bank where the establishment register is reached (a labour ministry service, or the bank's own copy of it) and build its adapter; confirm the scheme's code with the rulebook and carry it beside RR04 (R-02-14).

**Done for the adapter (owner's decision of 2026-11-22, R-12-16).** The runtime reaches the establishment register through the bank's binding of `uae.wps.port.labour-ministry.v1` (`runtime/src/TransactHub.Ports/EmployerRegister.cs`): `gateway-rest` (`POST {baseUrl}{statusPath}` with the employer id; the reply's `status`; 404 is an employer the register does not know; the credential by `secretRef`, as a bearer token or in `credentialHeader`) and `simulator` (`suspended`, `notRegistered`). `uae.wps.enrich.employer-status.v1` 1.1.0 has a native code-behind on it (`runtime/src/TransactHub.Activities.Wps/Native/EmployerStatus.cs`). DIB and ADIB bind it to `gateway-rest` with a `.invalid` host and the credential by reference and nothing else, so every call is refused until the bank says where its register is (`labour-ministry.yaml`). Tests: `FileProtectionAndRegisterTests`.

**Still open.** The scheme's reason code for a suspended employer: RR04 stays provisional until the rulebook confirms the scheme's own (R-02-14). And each bank's register endpoint.

## I-119. A script-behind still runs inside the studio's process

**Issue.** The security review of November 2026 (`docs/north-star/security/review-2026-11.md`, F-01 and F-02) found that a Bank Configurator, who may author a script-behind (R-10-10), could reach the studio's `process` from a TypeScript script through any host object's constructor (`Object.constructor('return process')()`), and from a Python script through `().__class__.__base__.__subclasses__()` to `os.system` and to the studio's own environment in `/proc`. Either gives the GitHub token, the model key and the signing key of a shared installation, and every other bank's content. Both are fixed (R-08-18): the TypeScript context holds no object of the host's realm and every slice of a script runs under its timeout; the Python harness runs under an audit hook that refuses files, processes, sockets, native libraries, new modules and compiling code. D14 still holds: neither is a boundary against a determined attacker, a V8 or CPython flaw, or a hook not yet thought of. The Python process still runs as the studio's user, and the TypeScript script still shares the studio's heap.

**Recommendation.** Before more than one bank authors scripts in one installation, run each script-behind in a worker process of its own with nothing of the studio in it: no environment, no credentials, its own user, a read-only root file system and no network (a container or a Node process under `--permission` with no grants; a Python process under the same user namespace), the bench talking to it over the JSON protocol the Python harness already uses. Until then, keep the two hardened sandboxes and their regression tests, and say in the deployment guide that bank script authoring in a shared installation is accepted at that risk.

**Done (23 November 2026, R-08-20).** The recommendation is built. Every script-behind runs in a worker process with nothing of the studio in it (`studio/src/interp/isolation.ts`, `script-worker.cjs`, `pysandbox.ts`): an empty environment, JSON over stdin and stdout only, a memory limit (`TH_SCRIPT_MEMORY_MB`, 256) and a wall limit (`TH_SCRIPT_WALL_MS`, 120000) beside each slice's timeout. A TypeScript script runs in a Node process under the permission model with no grant (no file read or write, no child process, no worker thread, no addon, no WASI), with only the worker file loaded; it is transpiled in the studio and the injected interface stays there, reached by message. A Python script keeps its audit hook and, on Linux, a Landlock ruleset that refuses every file and TCP connection, an address-space limit and no file size. On Linux both run in a user and network namespace of their own (`unshare`), so no network exists for them. A worker serves one tenant only and takes no other run after a timeout. The bench, the debugger, the interpreter, the kit's party service and an exported worker (which carries `script-worker.cjs`) use it; business time does not move while a script computes, and moves while it waits on its ports. `script-isolation.test.ts` puts a synthetic token in the studio's environment and shows that neither a script nor a probe started exactly as the worker is reaches it, a studio file, a process, a worker thread or a socket. What remains is I-147: a platform without namespaces or Landlock.

## I-120. Reads that are not yet scoped to a tenant

**Issue.** The review closed the cross-tenant reads it found in the studio (F-05, F-13) and the bank checks of the release API's distribution and payment calls (F-06). Some reads remain whole-installation by design or by omission: in the studio `GET /api/test-coverage`, `/api/coverage/:scope`, `/api/bindings`, `/api/supersessions`, `/api/propagate`, `/api/releases/:name/pins` and `/api/packs` summarise across tenants and can name another bank's artefacts or tests; in the runtime, `release-reader` is installation-wide, so `GET /payments/pin`, `/distributions` and `/distributions/active` answer for every bank. `POST /payments/pin` checks the payment first and appends after, so two concurrent pins of one new payment id could both be recorded (a time-of-check race; the fact chain shows it).

**Recommendation.** Decide whether a bank's own engineers read the release API (then give `release-reader` per tenant, R-06-28, and filter the three reads by `HasRoleFor`), and filter the studio's summaries by the caller's visible entries as `/api/checks` now does. Make the pin an insert guarded by a unique key on (payment, `distribution.pinned`), as the release store's own insert is.

**Owner's decision (23 November 2026).** The studio is multi-tenant and the runtime single-bank. A bank's users can never read, test, export or deploy another bank's content. The runtime's side (`release-reader` per tenant, the pin as a guarded insert) is another agent's.

**Done, the studio's side (23 November 2026, R-11-48).** Every API call of a bank's user is answered from the bank's view of the store (`tenantView`, `scopedEnv` in `studio/src/core/scope.ts`): the entries, the releases, the latest, the kinds, the drafts, the scripts and the samples show shared content and the bank's own only, and writes go through. So every listing and summary named above (`/api/test-coverage`, `/api/coverage/:scope`, `/api/bindings`, `/api/supersessions`, `/api/propagate`, `/api/releases/:name/pins`, `/api/packs`), the model, the registry report, a bank's configuration page, the branch's writes on `/api/git`, a check's interface diff, and the assistant's tools, which call the same routes, name nothing of another bank's. A test run reads its test's tenant's view only; an export, a pack, a distribution and a configuration release read their bank's. A bank's scripts are keyed by its tenant, so another bank's activity of the same name never runs them, and a bank's samples are its own (`banks/<tenant>/samples/`). `tenant-isolation.test.ts` asks for another bank's content by every route and is given none of it, not even its name; with the view turned off, twelve of its cases fail.

## I-121. Callers of the native host, the harnesses and mutual TLS

**Issue.** The native host now takes only the bank's machine callers (R-08-17, I-75 done): a client-credentials JWT whose role map gives `native-caller` in the attempt's tenant, or nobody beyond loopback. Every worker that calls it must therefore send `TH_NATIVE_TOKEN`, a reference to a token the bank's identity provider issues for the worker's client; no worker host (`TransactHub.Host`, target) fetches or refreshes such a token yet, and the engine harness hosts (DTFx, Elsa, Temporal, Flowable harnesses in `runtime/tools`) still take any caller, on loopback only. Mutual TLS, the other model the owner named, is not built.

**Recommendation.** Have the worker host obtain the client-credentials token itself and refresh it before expiry (with `WithSecret` for the client secret, I-106). Keep the harnesses loopback-only and say so in the deployment guide. Build mutual TLS only if a bank's network model asks for it; then accept a client certificate from the bank's CA in place of the bearer token, with the same role per tenant.

**Done (23 November 2026, R-08-21, R-08-22).** A worker gets its own client-credentials token from the bank's provider (`TH_NATIVE_TOKEN_URL`, `TH_NATIVE_CLIENT_ID`, `TH_NATIVE_CLIENT_SECRET` by reference, optional scope and audience), keeps it while it has more than a minute left, and fetches it again on a 401 once (`studio/src/interp/native-auth.ts`); `TH_NATIVE_TOKEN` stays for a token kept fresh elsewhere. Mutual TLS is an option on the native host (`RuntimeTls` in `TransactHub.Auth`): `Tls:CertificateRef` and `Tls:KeyRef` serve https, and `Tls:ClientCaRef` takes only a client certificate that CA signed for client authentication, whose name (`x509:cn`, `x509:dns`, `x509:thumbprint`) the bank's role map turns into `native-caller` per tenant exactly as it does a token's claims; with it the host needs no OIDC issuer. The worker presents its certificate by reference (`TH_NATIVE_CLIENT_CERT`, `TH_NATIVE_CLIENT_KEY`) and checks the host's against `TH_NATIVE_CA`. The engine hosts and their supervisor take a caller only with their token (`TH_ENGINE_HOST_TOKEN` by reference; a supervisor without one makes one per run for its hosts; a host without one does not start), on top of loopback only. Tests: `native-auth.test.ts` and `NativeHostMutualTlsTests` make a dev CA at test time; `HostTokenTests`. The worker host of the plan (`TransactHub.Host`, target) is still to be built; it takes the same settings.

## I-122. A restore is one engineer's decision

**Issue.** The service console's restore (R-09-41) verifies the signed backup, offers a dry run and refuses a runtime that is neither empty nor a test environment, but a real restore needs only a ServiceEngineer, not a ServiceApprover, while the risky troubleshooting actions wait for one (R-09-43). A backup download is also one engineer's: it carries the role map and the trusted signers (no secret value).

**Recommendation.** Make a real restore an action of the Actions page with a ServiceApprover's approval, as the risky actions are, and record the backup download as a fact on the console's chain with its reason (it is recorded in the audit today). Add the case to `BackupTests`.

**Done (23 November 2026, the owner's decision of that day).** A real restore is an action of the service console's book: planned and verified when asked for (a backup that does not verify, or one that may not go into this runtime, is refused at once), its bytes kept in the store by their hash, and carried out only when a bank approver, another person, consents (R-09-77, I-155). A dry run needs no consent. `BackupTests` restores through the consent; `ConsentTests` shows the maker may not consent.

## I-123. The repository's own settings and the images the tests pull

**Issue.** CI now pins every action to a commit SHA and gives each workflow a read-only token (R-09-61), but what protects the default branch is the repository's settings, which are not in the repository: required reviews, required status checks (`security` among them), a CODEOWNERS entry for `.github/workflows/` and `spec/`, and who may approve a workflow run from a fork. The test fixtures pull images by tag, not digest (`mcr.microsoft.com/mssql/server:2022-latest`, `postgres:16-alpine`, `hashicorp/vault:1.18.3`, `localstack/localstack:4.14.0`, `temporalio/temporal:1.5.1`); the observability stack already pins by digest. The full govulncheck of `runtime/temporal-mssql` (the Temporal server under `cmd/`) could not finish in the review's sandbox for memory; the plugin's own packages are clean after the upgrades of F-15.

**Recommendation.** The owner sets branch protection with the `security` workflow required, a CODEOWNERS file for the workflows, the spec and `runtime/src/TransactHub.Auth`, and "require approval for all outside collaborators" on fork runs. Pin the test images by digest in one change across the fixtures, and let a dependency bot propose SHA and digest updates. Watch the CI job `go-vulnerabilities` on its first run.

## I-124. Instructions written into content, and the assistant's "allow always"

**Issue.** The studio's assistant reads documents, samples, tool results and another designer's text, any of which can carry instructions written to steer a model. Its system prompt now says that such text is data (F-29), its writes ask the designer first, and nothing it writes is published without a checker, but a designer who chose "allow always" for a tool lets it save drafts, tests and samples without asking, and a sample is a file on the content branch at once (R-11-24), outside maker-checker.

**Recommendation.** Never offer "allow always" for `save_sample`, or save an assistant's sample as part of a change set so a checker sees it. Show in the assistant's panel when a tool result held text that looks like an instruction. Add a recorded conversation with an injected instruction to the assistant's replay tests.

**Done (23 November 2026, R-11-49).** Both recommendations are built. `save_sample` is asked about every time: the panel offers no "Allow always" for it, an "always" the browser sends for it counts once, and it is never kept in the allowed list. A sample the assistant saves goes into the tenant's open change set, on its branch (`saveSampleDraft`); it is published only with that change set's approval, the change set's record names it (`samples:`), and `GET /api/change-set` shows it to the checker. Without an open change set it is refused, so the sample goes with the draft that uses it. A designer's own sample is published at once, as before, in the bank's folder for a bank. A tool result that holds text that reads like an instruction (`instructionIn` in `studio/src/ai/agent.ts`) is shown in the panel, and the result tells the model it is data. `assistant-injection.test.ts` replays a conversation whose draft carries an injected instruction, and checks the factory's own content holds none.

## I-125. Re-drive, reset and requeue are the parked step's controls, not the engines' own

**Issue.** `redrive-workflow`, `reset-activity` and `retry-attempt` reach a step that parked (R-05-55); a step without `policy.park` fails at once as before, and an instance that has ended cannot be re-driven. Temporal's own `ResetWorkflowExecution` and activity reset, DTFx's rewind and Elsa's resume of a faulted activity are not called: the interpreter keeps the failed step parked inside the running instance, which gives the same effect on all four engines with one path. On Flowable `retry` re-enters the call's sub-process, so the parked step runs again with its full retry policy, as `redrive` does; on the other engines `retry` is one attempt. `requeue-dead-letter` stays Flowable's management API, for a job the binding left dead (production mode). No factory workflow declares `park` yet, so a WPS payment that fails at a step still ends failed.

**Recommendation.** Decide per step of the factory's flows which may park (the WPS submit and the posting, with a park of a business day), and release them with `park` (a minor).

**What is built.** The refund order's posting parks for a business day (`p.wf.refund.v1` step `post`, `policy.park: P1D`, I-133), and the platform view offers `retry` for it. The WPS steps of the payment flows do not park yet: each is a published release, so a park there is a new release of the leg, still to decide. The WPS payment flows park too (R-12-26): `uae.wps.wf.salary-file-out.v1` 1.2.0 (the business date, the employer's status, the file checks, the render), `uae.wps.wf.agent-payment-in.v2` 2.1.0 (the file checks, the receipt and disbursement files, the returns decision and file, the return's posting) and `uae.wps.wf.return-in.v1` 1.2.0 (the employer's credit), each for a business day (`studio/test/wps-park.test.ts`). Not parked: a composite step (the submit, the confirmation, the reports), whose failure is inside its subflow, and the item-level credit of a payment file, whose failure the leg already reports and returns; parking either is a decision for the owner (I-146). Give the Flowable generator a variable maximum of attempts for the parked loop, so that `retry` is one attempt there too. If a bank asks to re-drive an instance that has ended, add Temporal's reset to the failed activity's workflow task as a host operation behind the same action, with a conformance scenario on the time-skipping server.

## I-126. Codes the dictionary does not have yet, and the rest of I-103

**Issue.** The platform's error dictionary `p.errors.platform.v1` is published at 1.0.0 and a published release is not edited. The refusals new in this change therefore reuse its codes: no parked step, no kept file, no posting reference and an unknown payment are `ops.ActionNotOffered`; a worker that does not answer its control is `service.ProbeFailed`. The runtime's refusal of a paused scheme answers `ops.IntakePaused`, which has no entry yet. An engine's own failure (a Temporal workflow task failure, a Flowable dead letter, a DTFx or Elsa fault) is not a fact, and the service console's log viewer reads only its own host's logs and the store's facts (I-103 (2) and (3)).

**Recommendation.** Release `p.errors.platform.v1` 1.1.0 with the next factory release, with entries for `ops.IntakePaused`, `ops.NoParkedStep`, `ops.NoFileKept` and `ops.WorkerUnreachable`, and give the refusals those codes. Record `engine.failed` from each engine host with the dictionary's code, beside `engine started`; add the installation's log back end as a read-only source (I-103 (3), I-80).

**Done (23 November 2026), as recommended.** `p.errors.platform.v1` 1.1.0, in factory release 1.5.0 (the 1.0.0 file stays on the branch as published), adds entries with runbooks for `ops.IntakePaused`, `ops.NoParkedStep`, `ops.NoFileKept`, `ops.NoPostingReference`, `ops.UnknownPayment`, `ops.WorkerUnreachable` and `service.LogSourceUnreachable`, and the runtime's refusals use them (`ErrorCodes` in `TransactHub.Service`, `OpsCodes` in `TransactHub.Operations`; an unknown action id stays `ops.ActionNotOffered`). The long-running engine host records `engine failed` with the engine's dictionary code (R-09-70), and the service console reads the other hosts' logs from the installation's back end (R-09-71): see I-103.

## I-127. A live operator on a harness that runs one payment on virtual time

**Issue.** The deploy runner runs each payment in a harness process of its own, on virtual time (I-114). With a store, the harness follows the payment's outbox rows, and while an operator wait is open (a hold awaiting its decision, a parked step) it holds its clock and waits in wall time, for at most `TH_OPERATOR_PATIENCE_SECONDS` (default 600), before it lets the wait's own timer fire; on Temporal it stops the test server's time skipping meanwhile. A hold of a business day therefore expires after ten minutes of wall time in such a run, by the engine's timer, while the runtime's sweep expires it only after the day. A row recorded after the harness has ended is acknowledged `ended` by no one: it stays undelivered. A hold's expiry is counted in elapsed time, not on the business calendar, on every engine and in the runtime's sweep.

**Recommendation.** Run the engine hosts long-running, on real time, one per engine and distribution (I-89, I-114), so that an operator wait is as long as its expiry and every row finds its instance; keep the patience only for the harnesses of tests. Decide with the scheme whether a hold's expiry is counted on the business calendar; if so, add `expiryCalendar` to the hold activities (a minor) and count it as an await's timeout is counted.

**Done in part (22 November 2026).** A hold waits in the long-running host for as long as its expiry says (R-09-68): on Temporal on the server's clock with no patience at all (the decision comes from the outbox, as before), on Flowable in production mode with the binding's journal (I-89), and on DTFx and Elsa, whose engines still keep a virtual clock in a harness run, for at most the host's patience (`TH_HOST_OPERATOR_PATIENCE`, default 30 days) instead of the test harness's ten minutes. `TH_OPERATOR_PATIENCE_SECONDS` stays for the harnesses of tests. Shown by `EngineHostTests` (each payment waits on its hold in the host until the operator decides it). Whether a hold's expiry is counted on the business calendar is still the scheme's to decide; DTFx and Elsa on real time are in I-138.

**Done (23 November 2026), as decided by the owner: business days (R-09-88).** `p.hold.approval.v1` and `p.hold.repair.v1` 1.1.0 (factory release 1.9.0) declare `expiryCalendar` (default `business`, or `elapsed`). The expiry counts business days of the bank's reference table of domain `business-calendar` in force on the day the hold opens: the studio interpreter waits that long; the party answers an engine with the expiry, its calendar and the days, and the shared interpreter (DTFx, Elsa, Temporal) and the Flowable binding set their timers on it; the runtime's hold reads the table its configuration installed, records `expiresAt` on the `opened` fact and the sweep expires it then. Evidence: `studio/test/hold-expiry.test.ts` (a Friday's approval expires on Monday, one opened before the December holidays on the Friday after them), `HoldExpiryCalendarTests` (both databases). The engine host runs DTFx and Elsa in real time now (R-09-93), so an operator wait there is as long as its expiry, not the host's patience. What is left: I-166.

## I-128. The adapters behind resend, reconcile, intake and drain

**Issue.** `resend-file` writes to a folder the scheme's gateway is assumed to collect from (`TH_FILE_TRANSFER_DIR`); there is no SFTP or Central Bank file-transfer adapter in the runtime, and only files the store keeps (`PaymentStore.KeepMessage`: the SIF today) can be resent. `reconcile-posting` needs the gateway's inquiry settings (`inquiryPath`, `inquiryFunctionId`), which no bank's binding has; the simulator answers from what it posted. An intake refused while paused is kept and offered again only by a front door that was asked to (`queueWhilePaused`), in its own memory. `drain-worker` and `restart-worker` reach the deploy runner's workers by their address; a worker under the installation's orchestrator (Kubernetes, a service manager) is restarted by it, not by the runtime.

**Recommendation.** Build the file-transfer adapter the WPS gateway needs behind `IFileTransferPort` (with the port work of the WPS adapters), and keep every file an exchange sends (I-97 (2)) so that any can be resent. Ask each bank for its gateway's inquiry function and add it to the bank's core-banking binding. Keep refused payments in the store as `intake.queued` facts if a bank wants them taken in order after a pause. Link the worker actions to the orchestrator's own drain (a readiness probe that fails while draining) in the installation's guide.

**Done for the file transfer (23 November 2026).** The `sftp` adapter is built (I-129), and the consoles' resend goes through the File API's outbox onto the bank's binding (R-09-84), so any file the File API kept can be sent again. The rest stays open.

## I-129. The runtime has no SFTP adapter of the file-transfer port

**Issue.** DIB's and ADIB's `p.port.file-transfer.v1` bindings name an `sftp` adapter (the source's SFTP gateway profiles and folders). The runtime builds two file-transfer adapters, `simulator` and `directory` (a folder on the host, its root by reference), and refuses `sftp` ("which this runtime does not build"). So the `file-bulk` core-banking adapter (I-82) and the encrypted WPS files (I-117) reach a real counterpart only through a mounted folder.

**Recommendation.** Build an `sftp` adapter in `TransactHub.Ports` on a pinned SSH library, with the host key pinned by reference, the credential by `secretRef`, and the session settings DIB's binding carries (`sessions.*`); prove it against an SFTP container with a key made at test time. Until then a bank that posts by file binds `directory` on a share its SFTP gateway mirrors. The operations console's resend (`IFileTransferPort` in `runtime/src/TransactHub.Operations/Controls.cs`, I-128) writes to a folder of its own: put it on the same port adapters (`IFileTransfer`, `FileTransferAdapters`), so one binding of `p.port.file-transfer.v1` serves both.

**Done (23 November 2026, R-09-86).** `SftpFileTransfer` in `runtime/src/TransactHub.Ports/Sftp.cs`, on SSH.NET 2026.0.0 (pinned): each partner of the binding by its prefix (`centralBank`, `ministry`, `coreBanking`, `pickup`), its host key pinned by reference and checked before any login, the login by `secretRef`, the `sessions.*` settings as a session pool, a file written under `.part` and renamed. `SftpTests` proves it against an SFTP server in a container (atmoz/sftp, pinned by digest) with a login key made at test time, and loads DIB's and ADIB's bindings. The File API polls the partners' inboxes and delivers the outbox through it; the consoles' resend is on the same binding (I-158). What remains: the banks' host keys (I-161) and inbox messages (I-160).

## I-130. The participant directory has no source the factory may carry

**Issue.** A salary line's bank name comes from the scheme's participant directory (`uae.wps.table.participant-names.v1`, I-115). Shared content may not name a bank (L09), so the table's rows are a value, empty by default; the example bank sets two synthetic participants. No bank folder sets the real directory, and the source holds none to mine. The runtime has no participant directory adapter (`uae.p.port.participant-directory.v1` answers a status only).

**Recommendation.** Take the directory from the Central Bank's published participant list as reference data, effective-dated, and decide with the owner where it lives: a value in each bank's configuration set, or a registry the deployment loads. Then set it for DIB and ADIB with its `validFrom`.

**Decided and built (owner's decision of 23 November 2026).** The directory is a reference table in shared scope, `uae.p.ref.participant-directory.v1`, loaded from the Central Bank's register of licensed banks, October 2026 (63 banks, `validFrom` 2026-10-01; R-01-25, I-150), and DIB's and ADIB's participant lists read names by routing code from it (`fromReference`, R-11-51). The register has no routing codes; only DIB's, which the source holds, is filled (I-151).

## I-131. A host's file signing key at deployment

**Issue.** Each kept file's hash is signed by the host that kept it (R-09-64). The key is named by `TH_FILE_SIGNING_KEY_REF` (a P-256 private key in PEM, by reference); without it the native host and the runtime's API make a key for their own lifetime, so a file kept before a restart cannot be checked against a key anyone still holds, and the public keys are not published anywhere.

**Recommendation.** Require the reference outside development, as the release signing key is; publish each host's public key id with the trusted signers (`TH_TRUSTED_SIGNERS`) so an auditor can verify a file's signature; record a rotation as a fact. Decide with I-67 who holds the key.

**Note (23 November 2026).** The File API signs the files it keeps with the same setting, `TH_FILE_SIGNING_KEY_REF`; the recommendation applies to it as well.

## I-132. A bulk posting file per posting batch, and the DE columns the store cannot fill

**Issue.** The `file-bulk` adapter (I-82) makes one bulk file of each posting batch. The platform's posting activity posts per item on a payment file (one batch for each credit, its settlement debit and the employee's credit), so on the agent leg a payment file becomes one bulk file per line, where the source's DE posted the whole file's credits in one file under one debit. Three columns of the source's layout (the relationship customer, the cooperate name, the labour ministry id) are written blank, since a posting batch does not carry them, and the repost file of the failed lines carries those lines only, with no debit line: the source left that to its exception queue and DIB's schema is open (OD-OC1).

**Recommendation.** For a bank that binds `file-bulk`, post a payment file's credits as one batch: a batch-level posting step in a new release of the agent leg, chosen by the slot contract rather than the bank's binding. Confirm the layout's last three columns and the repost file's shape with DIB before it is used.

## I-133. Refunds as orders on the platform, with the scheme as a leg

**Issue.** The source's refund flows (RRI, the refund request instruction; DCR, the disbursement cancellation request; RRR, the refund request response; DATA, `input/WS_DIB_WPS_Inward_RRI_v1.1.md`, `dsl/workflows/dib-wps-inward-rri.v1.workflow.json`) were one bank's WPS flow with its own tables: the file is parsed and saved, each line's amount blocked on the employee's account, a maker and a checker verify it with a balance enquiry, the accepted lines are posted to core banking by a bulk debit file with an exchange rate, then the DCR goes to WPS, and after its ACK the RRR. The spec had no refund order: `p.wf.request.v1` moves no money, its WPS leg `uae.wps.wf.refund-out.v1` posts the employer's credit itself, `uae.wps.rrr` and `uae.wps.dcr` were planned messages, and the RRI map was reached by no flow (I-82). The owner's direction of 22 November 2026: build the refunds as orders on the platform, with WPS as a leg, and FX and charges as platform activities.

**Recommendation.** A refund is a new order of a platform workflow that owns its money; a scheme fills its slots; FX and charges are platform activities on the bank's values.

**What is built (R-12-19 to R-12-22, R-02-20).**
- `p.wf.refund.v1`, a platform workflow that `p.table.workflow-select.v1` 1.1.0 picks for `header.kind` refund. Its steps: the earlier payment by `header.originalPaymentId` (`p.enrich.payment-record.v1`), FX (`p.enrich.fx-rate.v1`), charges (`p.enrich.charges.v1`), the block (`p.reserve.funds.v1`, released by compensation on a decline), the approval hold (`p.hold.approval.v1`, the source's maker and checker), a hook point `before-posting` for the bank, the posting (`p.post.accounting.v1`, `policy.park: P1D`, the first factory step that parks, I-125) and its verification. Slots `scheme-leg` and `scheme-refuse`. No step names a scheme.
- FX and charges: `p.enrich.fx-rate.v1` converts each line into its account's currency at the bank's rate (`#rates`); `p.enrich.charges.v1` prices each line on the bank's slabs (`#chargeSlabs`, `#minCharge`, `#maxCharge`, `#vatPercent`, the source's charge configuration: fixed plus a percentage, floor, ceiling, VAT). Script-behinds on `rules.money`, exact, rounded per line half away from zero. The factory sets no rate and no charge; the example bank sets synthetic ones; DIB and ADIB are left unset until the banks give theirs (I-58, their READMEs).
- WPS as the leg: `uae.wps.wf.refund-request-in.v1` returns the funds (DCR, `uae.wps.exchange.return-refund.v1`) and answers (RRR, `uae.wps.exchange.answer-refund.v1`); `uae.wps.wf.refund-request-refuse.v1` answers a refusal with an RRR alone. Each file is encrypted before it is sent (L46). Neither leg moves money: lint L47 refuses a leg that reserves or posts when its platform workflow does (mutation M66). The scheme 1.2.0 starts a refund order from `uae.wps.rri` (the planned `uae.wps.rfr-in` is replaced) and sends `uae.wps.dcr` and `uae.wps.rrr`; acknowledgements gain the families `dcr` and `rrr`. `uae.wps.map.rri-in.v1` 1.1.0 has a script-behind for the source's layout; `uae.wps.ext.salary.v1` 1.2.0 registers the RRI's fields with no ISO element.
- ISO 20022: the RRI is read as a cancellation request (camt.056), the DCR as a payment return (pacs.004), the RRR as a resolution of investigation (camt.029) (Annex A). No table of any scheme; the facts are the store's.
- Distribution: a refund order is a new order, pinned to the distribution active when it is accepted, that names the earlier payment by id (PACKAGING-AND-DEPLOYMENT section 5, point 5).
- Operations: `p.view.operations.v1` 1.2.0 shows the order (the earlier payment, FX, charges, approval, and the posting fields it already had) and offers `retry` for the parked posting; `uae.wps.view.operations.v1` 1.3.0 adds the two refund legs (`selectBy` kind `refund`) and the RRI, DCR and RRR documents. No refund screen.
- A second scheme fills the same slots with no change to the platform workflow (`studio/test/refund-order.test.ts`).
- Tests: P-FX-001 to P-FX-003, P-CHG-001, P-CHG-002, WPS-RRI-001 to WPS-RRI-007; `studio/test/refund-order.test.ts`; `studio/test/blackbox/e2e-refund-order.test.ts` (Temporal and DTFx, on the store, read back from the operations read model).

**What remains.** I-134 (what the source and the scheme leave open). I-135 and I-136 are done. The employer's side is moved onto the platform: an outward refund order on `p.wf.refund-out.v1`, WPS its leg `uae.wps.wf.refund-out.v2` (R-12-24, I-145); its intake still has no layout of its own (C25-S5, I-146).

## I-134. What the source and the scheme leave open in the refund cycle

**Issue.** Building the RRI, DCR and RRR as an order left these open:
1. **Per line or per file.** The source's maker accepts or rejects each line (with a balance check and a manual block) and its RRR answers each line. `p.wf.refund.v1` decides the order as a whole: every line is converted, priced, blocked, approved and posted, or the order is refused.
2. **Reason codes.** The RRI's reason codes (for example 003, 005) and the RRR's reject reasons (the source's `RriRejectReasons`, 000 and 002 among them) have no confirmed ISO mapping (camt.056 CxlRsnInf, camt.029 CxlStsRsnInf). They are carried as the scheme's codes (`ext.uae.wps.refundReason`), and the refuse leg's RRR gives no reason per refusal yet.
3. **A refused DCR and a refused RRR.** The source regenerates a DCR that WPS refuses (Activity 9, the regeneration case) and resends a refused RRR (Activity 13). Here a refused DCR ends the order in review, after it was posted; a refused RRR leaves the order pending.
4. **The RRR's acknowledgement.** The source never polls for it (Activity 14). The factory waits one business day for it, a placeholder.
5. **Several earlier payments.** An RRI's lines may name different payroll files. The map names the earlier payment only when every line names the same one.
6. **The account's currency.** The RRI carries none, so a line converts at one, as the source's posting did (exchange rate 1.00). The currency would come from core banking (a balance enquiry), which the order does not ask.
7. **The block.** The source blocks each line's amount on its own account (`cbs.amount.block`, released by `RriUnblock`); the platform's block is `p.reserve.funds.v1` on the order, and `p.port.core-banking.v1` has no amount-block operation (I-82).

**Recommendation.** Ask the scheme and the owner: whether an order is accepted per line; the reason code sets and their ISO codes; whether a refused DCR is regenerated and a refused RRR resent, and after how long. Then release `p.wf.refund.v1` 1.1.0 with a per-line decision (a decision activity over the lines, the refused lines answered REJECTED), add `block` and `release` to the core-banking port (a minor), and read the account's currency from a balance enquiry before FX.

## I-135. One platform view for every platform workflow

**Issue.** The runtime's projection applies one platform view (`p.view.operations.v1`) to every subject, and lint L38 holds each of its actions to every platform workflow it lists. The view's `re-screen` action therefore needs `p.wf.refund.v1` to take the `re-screen` signal, though a refund order has no screening step: the signal is declared and changes nothing.

**Recommendation.** Let an action name the platform workflows it applies to (`when: { workflow: [...] }`, schema `operations-view`), checked by L38, and the console offer it only on those; then drop `re-screen` from `p.wf.refund.v1` in its next release.

**Done (22 November 2026).** An action may name the platform workflows it applies to, `when.workflow` (schema `operations-view`, R-05-61): lint L38 holds each named workflow to the view's own and a signal action to the workflows named (mutations M67, M68); the runtime offers it only on a payment whose root steps name one of them (`OperatorActions.Offers`; each step fact now carries its `workflow`, from every engine). `p.view.operations.v1` 1.3.0 offers `re-screen` on `p.wf.corporate.v1` and `p.wf.inward.v1` only, and `p.wf.refund.v1` 1.1.0 no longer declares it. Evidence: `OperatorActionTests` (both databases).

## I-136. What core banking posts for a refund order, and the code-behinds the runtime lacks

**Issue.** The charges and the converted amounts are facts of the order (`facts.charges`, `facts.fx`) that the operations console shows, but `p.post.accounting.v1` does not read them: its contract reads the payment, the route and the reservation, and on the runtime its native code-behind prices charges from its own `chargeSlabs`. A bank that sets both would price twice. FX and charges have script-behinds only, so on the runtime's native host they run in the studio's script sandbox through the deploy runner's workers, not as native code-behinds (R-08-16).

On the engines, a hold's decision comes back to the instance as a message, not as a worker's answer, so the approval step leaves no step fact with its final outcome: the operations console shows a refund order's approval only through the hold facts the runtime's own operations queue writes (`StoreHold`), and none when a simulator answers the hold, as in the end-to-end test. The step facts also kept only some of the header's ids; `originalPaymentId` is now one of them (`StepFacts.HeaderKeys`), so the console names the earlier payment.

**Recommendation.** Release `p.post.accounting.v2`, which reads `facts.fx` and `facts.charges` and posts the lines with their charge and VAT legs (camt.054), with no slabs of its own; move `p.wf.refund.v1` onto it. Record a hold's decided outcome as a step fact of the hold's step when the instance takes the decision. Add native code-behinds for `p.enrich.fx-rate.v1` and `p.enrich.charges.v1` on `decimal`, proved equal to the scripts by `native-dual.test.ts`.

## I-137. What the Flowable binding's journal costs, and the windows it leaves

**Issue.** (1) The binding records its whole state before every command it sends, so a payment of n jobs writes n facts that each grow with the run: a large file costs about n squared in the journal's bytes. (2) A completion it sent again after a restart and that the server refuses is taken as done; if a timer boundary had ended that attempt at the same moment, its late result is applied as though it had completed. (3) A worker answered after its predecessor recorded the job as begun but before it recorded the answer is called again; the step is not recorded twice, but a simulator's reply message is delivered twice (the front door counts it a duplicate). (4) The kit's embedded harness (Java, H2 in memory) is not a host and has no journal. (5) A step record of an item after the one that stopped a `forEach` stays in the REST binding's `steps`.

**Recommendation.** Record what changed since the last fact (the instances touched, the events scheduled), with a full state every so many facts, before a file of thousands of lines runs on Flowable in production. Ask the server for the attempt's historic activity before taking a refused completion as done. Keep the embedded harness for the kit only, and say so in Part 9. Buffer an item's step records with its trace entries, as the interpreter does.

## I-138. Long-running hosts on DTFx, Elsa and Flowable, and where a host's plan comes from

**Issue.** (1) On DTFx and Elsa the long-running host runs each payment on the engine's harness, whose orchestration service and clock live for that run: the host is long-running, the engine is not, and an operator wait is bounded by the host's patience. (2) On Flowable each payment deploys the distribution's BPMN again as a deployment of its own run (in a tenant of its own); the distribution has no one deployment shared by its payments. (3) The supervisor reads a host's plan (the distribution's documents, its binding's queues and its workers) from a folder the caller fills, not from the package the store keeps. (4) Temporal lets a version be deleted only a few minutes after its last poll, so removal on Temporal takes that long; its own drainage status is refreshed every few minutes, so the host relies on the store and on its own count of the version's running workflows instead. (5) The supervisor runs one engine for one bank and environment; one engine per bank is still the settings' choice (I-113).

**Recommendation.** Run DTFx on a durable orchestration service (its SQL Server provider) and Elsa on its persistence, in real time, as Temporal runs, so that their hosts keep one engine for every payment of a distribution. Deploy a Flowable distribution once, in a tenant of the distribution, and have the binding take only its own instances' jobs. Build a host's plan from the stored, verified package in the host itself. Decide with the owner whether one supervisor per engine serves every bank of an environment.

**Done (22 November 2026).**
1. `p.post.accounting.v2` 2.0.0 (R-12-23), a new major line beside v1, which the payment flows keep: each line's converted amount (`facts.fx`), then its charge and its VAT (`facts.charges`) as entries of their own, debited from the line's account; a line with only a creditor account is credited. No slabs of its own. A script-behind on `rules.money` and a native code-behind on `decimal` (`PostAccountingV2`). `p.wf.refund.v1` 1.1.0 and `p.wf.refund-out.v1` post with it. Tests P-POST-001 to P-POST-004 give the exact amounts (610.89 debited for 599.75 with 10.60 charged and 0.54 VAT; 594.12 credited net for 600.00).
2. Native code-behinds of `p.enrich.fx-rate.v1` and `p.enrich.charges.v1` 1.1.0 (`TransactHub.Activities.Platform/Native/OrderPricing.cs`, R-12-25). `PlatformNativeTests` runs every P-FX, P-CHG and P-POST test, the studio's own, on the native code with the same parameters and compares the writes as exact decimals.
3. A hold's decision is the hold step's fact on all four engines (R-05-60): DTFx, Elsa and Temporal record it through `Interp.Decided` when the instance takes the decision, Flowable when it delivers the decision to the hold's catch; `StoreSignals.Install` records it once per instance, step and call (a replay records nothing again), with who decided (`by`) and when (`at`). `p.view.operations.v1` 1.3.0 shows the order's approval again (`approval`, `approvalBy`, `approvalAt`). Evidence: `OperatorActionTests`; `e2e-refund-order` on Temporal and DTFx.
4. The studio interpreter writes a list inside a fact whole (a posting's entries and legs), so it stays a list in the context.

**Done in part (23 November 2026, R-09-93, R-09-94).** (3) is built: when no caller wrote a plan, the supervisor builds the host's plan from the stored, verified package (`runtime/tools/TransactHub.EngineHost/Plans.cs`: a plan per platform workflow from `engine/<workflow>/`, its documents, its binding's queues and its BPMN; the workers from `TH_HOST_WORKERS`), and a host runs every platform workflow of its distribution. (1) in part: DTFx and Elsa runs in the host follow the wall clock (`TH_HARNESS_REAL_TIME`), so an await or a hold waits as long as it says, and a message reaches a run after it started (I-158 (1)); each payment still runs in a harness run of its own, in memory, and does not survive a restart of its host. (2) is not built: Flowable still deploys per payment. Both are I-165. Evidence: `EngineHostTests` ("takes a message after the run started with a plan from the store", DTFx and Elsa). Both are done since (I-165, 24 November 2026, R-09-196 to R-09-198).

## I-139. A sign-out reaches only the instance that took it

**Issue.** With sealed sessions (R-11-45, I-72) the server holds no session. Sign-out clears the browser's cookie and the instance that signed it out refuses that session at once, but a copy of the cookie (or a command line token) is still accepted by another instance until it has been unused for `TH_SESSION_IDLE_MINUTES` or reaches its expiry. A user disabled at the identity provider likewise keeps a session in use until its expiry (8 hours by default). The rate limits of R-11-41 count per instance.

**Recommendation.** Decide with the identity provider's owner whether that window is acceptable. If it is not: shorten `TH_SESSION_HOURS` and `TH_CLI_SESSION_HOURS`, and have the studio ask the provider's session (a refresh token or the provider's introspection) every few minutes; or add a shared revocation list, which is a store of its own (a cache every instance reads) and therefore an owner's decision against R-11-21.

**Owner's decision (23 November 2026).** Keep stateless sealed sessions, make them shorter, and keep a heartbeat: the web app and the command line have the session checked with the identity provider every few minutes, and a session whose provider session ended stops at the next heartbeat on every instance.

**Done (23 November 2026, R-11-50).** At sign-in the studio keeps the provider's refresh token sealed in the session cookie (or the command line's token). Every `TH_SESSION_HEARTBEAT_MINUTES` (5) a session in use is renewed with it on whichever instance it reaches; the page asks for the heartbeat itself (`x-th-heartbeat`) without counting it as use, so the idle time still runs. A refusal (the provider session ended, the user disabled or revoked) ends the session there, and every other instance ends it at its own heartbeat, so the window is at most one heartbeat; a provider that cannot be reached gives at most one more interval, then the session ends. One heartbeat per session at a time on an instance. The defaults are now: 8 hours absolute (`TH_SESSION_HOURS`), 30 minutes idle (`TH_SESSION_IDLE_MINUTES`, was 60), a heartbeat every 5 minutes, and 1 hour for the command line (`TH_CLI_SESSION_HOURS`, was 8), which keeps the token a heartbeat renews. Production refuses a heartbeat of 0. A provider that issues no refresh token gives sessions that end at their first heartbeat: configure it to issue one (Entra ID needs the scope `offline_access`, in `TH_OIDC_SCOPES`; Keycloak's are bound to the provider's session already). The development provider issues and rotates refresh tokens and can end a user's sessions (`/auth/dev/end-session`). Tests: `sessions.test.ts` ("the session heartbeat with the identity provider"). Rotation across instances: I-148.

## I-140. A port contract does not say which adapters answer it, or what settings they need

**Issue.** L48 checks a bank's port bindings against the port contracts, but a port contract (`spec/schemas/port.schema.json`) lists only its party and operations. Which adapters the runtime offers for a port (`simulator`, `gateway-rest`, `sftp`, `file-bulk`), and which settings each needs (`baseUrl`, `postingPath`, a host and folders), are known only to the runtime's adapters (`runtime/src/TransactHub.Ports`). A binding to an adapter the distribution does not carry, or without a setting its adapter needs, is found only when the runtime first calls the port.

**Recommendation.** Have each distribution's `ports/` part declare, per port, the adapters it carries and each adapter's settings (name, required or not, and whether it is a `secretRef`), generated from the runtime's adapters, and extend L48 and the configuration release's check to hold a binding to them. Until then the studio's editor shows the adapter as free text.

## I-141. Whether an inward leg's returns belong to the platform's slot

**Issue.** The slot-contract check now covers writes (I-112, L43). One leg writes beyond its slot: `uae.wps.wf.agent-payment-in.v2` hands back `facts.returns`, which `scheme-leg@1` of `p.wf.inward.v1` does not list. Its v2 line was made to add that exit write (`tools/build_factory.py`, the note above RELEASES), and the slot has a `returned` outcome, which suggests the platform should see what was returned. But nothing in `p.wf.inward.v1` reads `facts.returns` today: not its steps after the leg, not `p.notify.channel.v1` or `p.notify.publish-fact.v1`, not its `on-close` hook. The operations view reads the returns from the step's facts, not from the context. Either answer is a new major line: of the platform workflow (`p.wf.inward.v2`, `scheme-leg@2`), or of the leg (`uae.wps.wf.agent-payment-in.v3`), with the WPS tests, the scheme manifest and the operations view released again to name it. Until then the check reports the write as a warning (`SLOT_WRITE_OPEN` in `tools/validate.py` and `studio/src/checks/checks.ts`) and lets it into a distribution.

**Recommendation.** Since the platform reads nothing of it, release the leg without `facts.returns` in its exit (`uae.wps.wf.agent-payment-in.v3`, the returns kept in its step facts for the operations view), together with the next WPS release so the tests and the view move once; then remove the entry from `SLOT_WRITE_OPEN`. If the owner wants the platform to act on returns (a returns notice to the channel, a report), release `p.wf.inward.v2` with `facts.returns` in its `scheme-leg@2` instead.

**Decided (owner, 23 November 2026) and done (R-09-87).** The returns stay. `p.wf.inward.v2` 2.0.0 (hand-written, a new major line) gives its leg `scheme-leg@2`, which lets the leg write `facts.returns`, and hands the returns on in its exit. `p.table.workflow-select.v1` 1.3.0 routes inward payments and returns to it; `uae.wps.wf.agent-payment-in.v2` 2.2.0, `uae.wps.wf.return-in.v1` 1.3.0 and `uae.wps.wf.inward-refuse.v1` 1.1.0 fill its slots; `p.view.operations.v1` 1.5.0 shows it in place of v1. `p.wf.inward.v1` left `spec/factory` and stays on the content branch as published, for the payments started on it. `SLOT_WRITE_OPEN` is empty in `tools/validate.py` and `studio/src/checks/checks.ts`, and L43 reports nothing. The factory is release 1.9.0, seeded with `seed-content --write-local`; the four banks' lock files moved to 1.9.0, the earlier ones kept in `packs/`; their configuration sets still accept the 1.7.0 packs (I-170).

## I-142. Engine failures seen only by the long-running host, and one kind of log back end

**Issue.** The fact `engine failed` (R-09-70) is recorded by the long-running engine host, which runs every payment of an installation. The per-payment harnesses (`runtime/tools/TransactHub.TemporalHarness`, `DtfxHarness`, `ElsaHarness`, `FlowableHarness`) and the deploy runner report a failure to their caller and record nothing, and the Flowable binding's own dead-letter handling and Temporal's workflow task failures that the engine retries by itself are seen only when the run ends failed. The service console reads one kind of log back end, Grafana Loki; an installation on OpenSearch or a cloud log service has no source yet.

**Recommendation.** Keep the harnesses as they are: they are for tests, and their caller asserts the failure. Record a failure the engine retries by itself (a workflow task failure that later succeeds) as a step fact only if the operators ask for it, since it is noise for a payment that then settles. Add an OpenSearch source behind `ILogSource` when the first installation needs it, configured the same way (`Service:LogSource:Kind=opensearch`).

## I-143. The service-level targets the alert rules have no number for

**Issue.** The alert rules are generated from the agreed targets (R-09-72, `runtime/observability/slo/targets.yaml`). Three are agreed: 50 lines a second on nodes of four or more cores (I-108), the acknowledgement wait (the bank's `ackWait`, by default P1D) and the hold expiry (`p.hold.approval.v1#expiry`, by default P1D). Two are not. (1) How many payments may be open on one distribution at once: the rules record the open payments per distribution (`th:payments_open:by_distribution`), and the alert is not generated while the number is null. (2) The waits are the factory's defaults; a bank that sets a shorter `ackWait` (ADIB's twenty minutes, I-116) or hold expiry is alerted only at the default. The warning points (75% of the wait, 80% of the expiry) and the `for` durations are the generator's, not the owner's. The throughput alert fires only while attempts wait on the queues, so a node that is simply idle never fires it.

**Recommendation.** Have the owner set (1) per installation size once the load test on the bank's hardware has run (I-68), and confirm the warning points. For (2), generate one rule per bank from its configuration release's values (the service console holds them), or have the gauges carry each wait's own limit so one rule compares the age with the limit.

## I-144. Which reports a bank needs beyond the list, and who may export

**Issue.** The console's reports are generic (R-05-59): they group and sum what the read model and the operations views hold. The source's report database also had fixed reports per file type (for example the SIF summary and detail reports, the upload history) and exports to Excel and PDF. Which of those a bank still needs, whether a view should declare a report of its own (its own groupings, measures and order), whether reports are sent on a schedule, and whether exporting payments needs a right of its own (a Supervisor, or a role the bank's role map names) beyond the operator's right to read the list, are not decided. Every export is logged with the caller and the row count, but it is not a fact on the store.

**Recommendation.** Ask each bank's operations which of the source's reports they use, and express each as a view's declared report (an operations-view schema change with its lint, in the same change as the console that renders it), not as a screen. Keep CSV as the one export format unless a regulator asks for another. Decide with the bank whether an export needs its own role, and if it does, record each export as an audit fact like the service console's actions.

**Decided (23 November 2026).** CSV is the one export format, an export needs no role of its own beyond the operator's right to read the list, and every export is an audit fact on the operations console's own chain: who, when, the filter as asked and the number of rows, with the fact's number in the answer's `x-th-export-fact` header (R-05-65, `ConsoleRecordTests`). A supervisor or a bank approver reads them at `GET /ops/api/audit`. Which fixed reports a bank needs is still open.

**Built (owner's direction of 23 November 2026).** The SIF summary, the SIF detail and the upload history, as fixed reports the WPS view declares (R-05-67): CSV only, masked, each download an export fact. What they leave open is Issue I-183.

## I-145. The employer's refund pair is retired by draining, not by a supersession

**Issue.** The employer's refund was a request on `p.wf.request.v1` whose WPS leg, `uae.wps.wf.refund-out.v1` 1.0.0, posted the employer's credit itself. It is now an outward refund order on `p.wf.refund-out.v1` with `uae.wps.wf.refund-out.v2` as its leg (R-12-24), and `uae.wps` 1.3.0 starts the employer's refund order there. A supersession cannot retire the old pair: it replaces one release by a later release of the same major line (R-01-19, lint L27), and the leg that posts nothing is a new major (its exit writes and its slot changed). Nor should it: a request started on the old pair has no platform posting, so superseding its leg would leave the employer uncredited.

**Recommendation.** Retire the old pair as the packaging model drains any release (PACKAGING-AND-DEPLOYMENT section 6): requests started on a distribution that holds `uae.wps` 1.2.0 finish on it; distributions built from 1.3.0 start none; once none is open, retire `uae.wps.wf.refund-out.v1` 1.0.0 through the studio's tracked retirement (R-11-18), which lists who still pins it and asks operations for the open count. The leg stays published on the content branch as a record; the factory no longer holds it.

## I-146. What the order's posting and the outward refund order leave open

**Issue.** Building `p.post.accounting.v2` and `p.wf.refund-out.v1` left these open:
1. **One batch per order.** The posting sends the whole order to core banking as one batch (the reference is the order's), as the source's RRI bulk debit file did; a bank that posts line by line (I-132) gets one file for the order.
2. **Who bears the charge on a credit.** On an outward refund order the charge and its VAT are debited from the account credited (the employer's). The source charged the refund cycle on the agent's side only; whether an employer's refund bears a charge is the bank's to say (its slabs are unset for DIB and ADIB, I-58).
3. **The employer's account currency.** As for an inward order (I-134 (6)), the refund converts at one unless the account names its currency; no balance enquiry asks it.
4. **The employer's refund order layout.** `uae.wps.map.refund-order-in.v1` has no script: the employer channel's layout is not in the source (C25-S5). The end-to-end test gives the order already canonical (`deploy --context`).
5. **What may park.** A composite step (the WPS submit, the receipt confirmation, the reports) does not park: its failure is a failure inside its subflow, and a park there would resend a file. A payment file's item-level credit does not park either: a credit that fails is already reported and returned (I-125).
6. **The posting's legs in the workflow state.** As v1 did, the legs keep the accounts so a reversal can post them back (`p.post.reverse.v1`); the entries show the last four characters only.

**Recommendation.** Ask DIB and ADIB whether an employer's refund bears a charge and whether their core banking takes an order's lines in one batch; read the account's currency from a balance enquiry before FX (with I-134); take the employer channel's refund order layout from the bank (C25-S5) and give the map a script; decide with the owner whether a composite's send step parks inside its subflow (a new release of each subflow, `policy.park` on its send, idempotent by the file's id) and whether an item-level credit parks; move the reversal to a reference of the posting in core banking, so the state holds no account.

## I-147. Script isolation where the platform has no namespaces or Landlock

**Issue.** R-08-20 gives a script-behind's worker no network through a Linux user and network namespace (`unshare`), and gives a Python script no file and no TCP through Landlock. On macOS and Windows, and on a Linux host that forbids unprivileged user namespaces, the worker keeps an empty environment, Node's permission model (no file, process, worker or addon), Python's audit hook and the memory and time limits, but a TypeScript worker that broke out of V8 could still open a socket, and a Python one that broke out of its audit hook could still read a file the studio's user can read. `isolation()` reports which layers hold. Node 22's permission model has no network permission.

**Recommendation.** Run a shared installation's studio on Linux with user namespaces allowed (the default on current distributions), or in a container whose seccomp profile allows them, and check `npm run studio -- check`'s report of the isolation. Where that is not possible, run the studio in a container with no network for its workers, or wait for Node's network permission (`--allow-net`, from Node 25) and add it to the worker's flags.

## I-148. A refresh token rotated on one instance while another still holds the older one

**Issue.** The heartbeat (R-11-50) renews a session with its refresh token, and a provider that rotates refresh tokens gives a new one each time. One instance coalesces concurrent heartbeats of one session and remembers what it learnt, but two instances that heartbeat the same session within the same moment each send the token they hold; a provider that refuses the older token at once, with reuse detection, then ends the session, and the user signs in again.

**Recommendation.** Configure the provider's rotation with a grace period for the previous token (Okta's grace period, Keycloak's default that keeps it valid, Entra ID's tokens that stay valid until they expire), as the development provider does for a minute; or keep a load balancer's affinity for the minute after a heartbeat. A shared store of heartbeats would close it, at the cost R-11-21 names.

## I-149. The corporate registration had no home, and how the bank's registers are kept

**Issue.** The source's WPS pre-check matched a salary file against a registered corporate (`WpsPlatformActivities.Precheck`, `SifUploadRules.MatchCorporate`, ported in `runtime/src/TransactHub.Activities.Wps/Rules/Shared.cs`), from its `CorporateRegistration` table, which an operator maintained on a screen. Nothing in the new model held that registration: the port was a function no activity called, the factory could not ask whether a file's employer was the bank's customer, and the operations console had no screen for it. Bank customer data cannot go into the studio or onto GitHub (owner's decision of 23 November 2026).

**Decided and built (owner's decisions of 23 November 2026).** The runtime keeps the bank's customer data in **registers** of declared kinds, maintained in the operations console (R-05-62 to R-05-64): a `register-kind` declaration (key, fields, types, ISO 20022 homes, personal fields and their masks, lookup fields), not a WPS screen. The first kind is the corporate registration (`runtime/src/TransactHub.Operations/Registers/corporate.register.json`), with the source's fields on the ISO 20022 party: its organisation ids (corporate id, CIF, unified number, trade licence, labour ministry establishment id, each an `OrgId/Othr` with its issuing scheme), name, postal address and emirate, contact details, accounts, and the bank's own flags (active, WPS registration form held, registered with the labour ministry, free zone, branch, managers, corporate type). Records are insert-only, effective-dated versions in the store on SQL Server and PostgreSQL (`register_change`, `register_version`, `register_index`, `register_decision`; migration `Registers`). A change is a maker's draft that takes effect only on a different bank approver's consent, from the later of its `validFrom` and the consent (R-05-63); the full history is shown, masked as the operations views mask. Core banking reads and syncs through the register API (R-09-73). The platform looks a debtor up with `p.enrich.party-register.v1`, and WPS feeds `MatchCorporate` with what it found (R-12-27): `uae.wps.wf.salary-file-out.v1` 1.3.0 refuses a file whose employer is not registered (the source's code 003) or registered with another debit account (010).

**Left open.**
1. **Masking for core banking.** The register API answers core banking unmasked: it is the system the data came from. If the bank wants its systems to see masked values too, add a scope to the role map's grant.
2. **Unmasking on screen.** As for payments (I-100), the console shows personal fields masked only; a maker changing a record sends only the fields it changed, so a masked value is never resubmitted.
3. **A register's size.** Search reads the records in force into memory; a register of more than some tens of thousands of records needs a read model of its own (a projection like the operations read model).
4. **References between registers and reference tables.** The corporate's emirate, free zone and branch name codes of the reference tables (R-01-25), but the runtime does not yet check them against the installed configuration's tables.

**Recommendation.** Keep the register kinds declared in the runtime software (a new kind is a runtime release, since it is bank data, not configuration); add the reference check of (4) on `/lookups` when a bank installs its first configuration release with branches; decide (1) with each bank's security officer.

**Addressed (owner's direction of 23 November 2026).** (1) is a setting, not a guess: the register API answers core banking unmasked by default and masked where the installation sets `Operations:Registers:SystemReadsMasked` (R-09-101); the choice stays the bank's security officer's. (2) A payment's masked field is unmasked on a bank approver's consent (R-05-69); a register record is not yet. Issue I-181.

## I-150. Reference tables: a kind of their own, not rule tables

**Issue.** Market, scheme and bank reference data (the participant directory, emirates, free zones, purpose codes, business-day calendars, a bank's branches) had no home: the participant directory was a list parameter of `uae.wps.table.participant-names.v1`, empty by default (I-130), and each bank would have had to copy the whole directory into its configuration set. Rule tables are logic, pinned with the workflow, with hit policies and examples; reference data is values, looked up by date.

**Decided and built.** A new artefact kind, `reference-table` (`spec/schemas/reference-table.schema.json`, R-01-25): columns and key are its interface, each edition of its rows has a `validFrom`, and a new edition is a new minor release. The platform vendor keeps the shared ones (`p.ref.purpose-codes.v1`, `uae.p.ref.participant-directory.v1`, `uae.p.ref.emirates.v1`, `uae.p.ref.free-zones.v1`, `uae.p.ref.business-calendar.v1`), the Bank Configurator a bank's own (`b.ref.branches.v1`, synthetic in the example bank). Tables travel to the runtime in the bank's configuration release, never in a distribution, and the runtime serves them under `GET /lookups/{domain}` by date (R-09-75). A list parameter may read its value from a table (`fromReference`, R-11-51): DIB's and ADIB's participant lists now read names by routing code from the participant directory. Lints L49 and L50 check them. The tables are written by hand in `spec/factory/*/reference/` and `spec/examples/bank/reference/`.

**Recommendation.** Give the Bank Configurator an editor for a bank's own tables in the studio (today a table is authored as YAML, like a rule), and add each new kind of reference data as a table, not as a parameter.

**Recommendation done (24 November 2026, R-11-106).** The studio's Reference tables view (Fluent UI 2, `studio/web/src/views/ReferenceTables.tsx`), which already added an edition to a bank's table (R-11-52), now starts a new table of the bank's own: a New table form takes its name (`b.ref.<name>.v1`), title, domain, columns (id, type, title, required, allowed values) and key, checks it as a table (schema and L49) and hands it to the edition editor, which saves the first edition with it as a draft at 1.0.0 for a Bank Approver. A published edition is never edited, as before. `studio/test/bank-calendar.test.ts` starts a synthetic table through the editor's API calls: a key that is no column refused, a row outside its allowed values refused, the draft listed.

## I-151. What the reference tables still lack

**Issue.** Loading the Central Bank's register of licensed banks, October 2026, as the participant directory left gaps:
1. **Routing codes.** The register gives no UAEFTS routing code. The TransactHub source's participant directory (`services/ecosystem/ParticipantDirectory`, seed data in `services/platform/Database/.../SeedData/Platform/PlatformModelBuilderExtensions.MasterData.cs`) holds BIC-like bank codes only, some not real, and no nine-digit routing code. The one routing code the source holds is DIB's WPS routing code, 802420101 (`Migrations/Wps/20261006094046_ReseedWpsParametersWithIdentityIds.cs`, confirmed by the business on 2026-10-06), filled in register row 24. Every other bank's routing code is empty, so a salary line to another bank still gets no bank name.
2. **The other sections of the register.** Only the Banks section (63 rows) is loaded; finance companies, exchange houses and the rest are not participants of the payment schemes in the factory.
3. **The business calendar.** Only the weekend and the holidays fixed by date are in `uae.p.ref.business-calendar.v1`; the holidays that follow the moon are announced each year. The calendar port's `reference` adapter reads the table (R-09-90), and a hold's expiry counts it (R-09-88); an await's business days still do not (I-166).
4. **Free zone codes.** `uae.p.ref.free-zones.v1` takes the source's codes (`Corporate_FreezoneHeaders`), which the bank's own systems may name differently.

**Recommendation.** Ask the Central Bank (or the scheme operator) for the participant list with routing codes and release `uae.p.ref.participant-directory.v1` 1.1.0 with an edition that fills them; add each year's announced holidays as a new edition and have the calendar adapter read the table; confirm the free zone codes with DIB's and ADIB's operations.

## I-152. The roles for the registers and the consent of system changes

**Issue.** The runtime had the roles Operator and Supervisor (payments) and ServiceEngineer and ServiceApprover (the service console). The owner decided that any action that updates the system needs the bank approver's consent (a maker plus a different approver), and that the operations console maintains the bank's customer data. Neither role pair fits a register: an operator acts on payments, and a supervisor's approval is a second approver of a payment action.

**Decided and built.** Two new people's roles in the operations console, given per tenant by the bank's role map (R-06-29): **RegisterMaker** drafts a register change, **BankApprover** consents to it or refuses it; both may read the registers, as may an Operator and a Supervisor. A person holding both may never consent to their own change. Two machine roles for system clients of the bank's identity provider (client credentials): `register-client` (core banking's register API and bulk sync, its changes waiting for a BankApprover like a person's) and `channel-enquiry` (the channel enquiry APIs, R-09-74), and `lookup-reader` on the release API (R-09-75).

**Recommendation.** When the runtime's consent for other system changes is built (configuration, distributions), use the same BankApprover role, so the bank grants one approver role for every change to the system; the dev sign-in's synthetic users show both roles.

## I-153. What the channel enquiries and the party register lookup leave open

**Issue.**
1. **The CIF and e-mail the channel supplied.** The source's `MatchCorporate` compared a CIF, an account and an e-mail the channel supplied with the upload. The canonical model carries the debit account (`batch.debtorAccount`) but neither a CIF nor an e-mail, so the runtime's match compares the account only.
2. **Which status a channel sees.** The channel status (PENDING, ACCEPTED, REJECTED) is worked out from the ISO status and how the payment ended, and the scheme's own status is the status fields of the scheme's operations view. The source mapped the Central Bank's status through a per-tenant rail status catalogue; no view declares which field is the channel's status.
3. **A line's status.** A line is RJCT in the enquiry when its validation found it wanting; a NAK's per-line errors (`status.transactions`) are not yet merged into the lines' statuses.
4. **The read model's lag.** The enquiries read the operations read model, so a payment accepted a moment ago may not be found yet.

**Recommendation.** Register the CIF as a debtor's organisation id with scheme CIF (R-02-21) and have the intake map write it from the channel's upload; let an operations view declare its channel status field (an operations-view schema change with its lint, in the same change as the enquiry that reads it); merge `status.transactions` into the lines in the projection; answer 404 with a retry hint while the read model's lag is above a second.

**(1) Done (24 November 2026, R-02-22).** The debtor's organisation id holds its establishment id (scheme MOL, R-12-27), so its CIF is a further organisation id: the canonical model gives a party `otherOrganisationIds[]` (Pty/Id/OrgId/Othr, repeated), and the salary file's intake map 1.5.0 (factory 1.20.0, `studio/scripts/uae.wps.map.sif-in.v1@1.5.0.ts`) writes the CIF the channel declared at upload (`# cif:`) there with scheme CIF. The store keeps every organisation id of a party as its OrgId/Othr (`CanonicalMapper`, which kept none before, so the MOL id is now in the store too). The runtime's match of the corporate still compares the account only: comparing the CIF and the e-mail is the source's `MatchCorporate`, whose e-mail the model does not carry (Issue I-314). (2) to (4) stay open.

## I-154. One runtime serves one bank: what the runtime still allows for several

**Issue.** The owner decided on 23 November 2026 that the runtime is not multi-tenant: one runtime serves one bank, and it must be impossible to deploy DIB's package in ADIB. Until then the runtime stored a distribution, a configuration release, a workflow package exported for a tenant, a payment's pin, a kept file, a branch's configuration sets and a backup for any bank a caller had a role in. Per-tenant role grants (R-06-28) let one person hold roles in several banks of one runtime.

**Recommendation.** Configure the runtime with its bank at install and refuse everything of another bank by what its signed manifest names, at verify, install and activate, whatever the caller's roles; keep the per-tenant grants and their checks, which then collapse to the one bank; keep the studio multi-tenant.

**Done (23 November 2026, the owner's decision of that day).** `TH_BANK` (or `Runtime:Bank`) is required by the release API and the service console, which do not start without it (`RuntimeBank`, R-09-76). `DistributionService` refuses another bank's distribution and configuration release at install, and a distribution of another bank at activation, drain and rollback, even one already in the store; it refuses to pin another bank's payment. The service console refuses another bank's version at verify and install (it lists it, not installable), a workflow package exported for another tenant at install and activation, and a backup that holds another bank's releases, values or bindings. The release API refuses another bank's workflow package and kept file, and imports only the bank's own configuration set from a branch. The engine host's supervisor takes the runtime's bank and refuses another. Tests: `DistributionApiTests.Another_banks_distribution_configuration_and_payment_are_refused_whatever_the_callers_roles` and `A_runtime_without_its_bank_does_not_start` (both databases), `ConsentTests.Another_banks_distribution_is_refused_at_verify_install_and_activate`, and the laptop stack's `deploy/laptop/test/insert-only.mts` (a DIB distribution published by the same studio, signed by the same key, refused in the ADIB runtime at verify, install and the release API).

**Done for (1) (23 November 2026, R-09-117).** A refusal of another bank's content carries its own code, `package.WrongBank`, with its runbook in `p.errors.platform.v1` 1.2.0 (the factory's 1.10.0); the service console answers it at verify, install and activate (`ConsentTests`), and `ErrorDictionaryTests` holds every runtime code to an entry.

**Still open.** (2) The operations console, the test bench and the native host still accept a role map that names several tenants; they hold no content of their own, so nothing of another bank gets in through them, but their tenant filters are now one bank's. Recommendation: leave them, as the owner asked, and read `TH_BANK` there too when a bank asks for it.

## I-155. Consent of the bank approver for every change of the system: where the line is

**Issue.** The owner decided on 23 November 2026 that any action that updates the system needs the consent of the bank approver: a maker asks, a different user with the bank approver's role consents, both recorded as facts; and that operators' business decisions on a payment stay as they are unless they change the system. Until then the service console carried out an install, a restore and seven troubleshooting actions on one engineer's word (I-122), the operations console rebuilt its read model on a supervisor's, and the release API's pipeline role activated, drained, removed and rolled back distributions in any environment.

**Recommendation.** Draw the line by what an action changes. It changes the system when it changes what the runtime runs or holds, or how it runs: content, configuration, intake, workers, keys, the read model, a payment's engine state from outside its declared operator actions. It does not when it only reads or verifies, or does what the runtime does by itself. A business decision on a payment that its operations view declares (approve, decline, release, reject a hold; retry, repair, cancel) stays with the operators and the view's second approver.

**Done (23 November 2026).** The line drawn (R-09-77):
- **Service console, with consent**: install (a workflow package, a distribution or a configuration release), restore, activate, roll back, remove, drain, release a hold, expire a hold, re-screen, retry an attempt, re-drive, reset an activity, resend a file, reconcile a posting, requeue a dead letter, pause and resume intake, drain and restart a worker, rotate a trusted key, rebuild the read model. A version that does not verify, or a backup that may not go into this runtime, is refused when asked for, before any consent; a dry run of a restore changes nothing and needs none.
- **Service console, at once**: re-verify a package, replay the read model from its checkpoint (the projection does both by itself), verify a version, the difference, a backup (a download, recorded with its reason).
- **Operations console, with consent**: rebuild the read model (`POST /ops/api/rebuild` asks; `POST /ops/api/system-actions/{id}/decision` consents; both on the console's own chain, `operations-console`).
- **Operations console, as before**: an operator's actions on a payment and their second approver (R-05-50).
- **Release API**: its pipeline may change which release or distribution runs only in the environments `Releases:MachineActivationEnvironments` names, by default the test environments (R-09-78); elsewhere the change goes through the service console.
The consent is the role `BankApprover`, in the service console's and the operations console's role maps; `ServiceApprover`, the service console's earlier name for its approver, gives the same consent so an existing role map still starts. The person who asked may not consent, even holding the role. Each request, consent (with who asked) and result is a fact. Tests: `ConsentTests`, `ActionTests`, `BackupTests`, `ContentTests`, `DistributionContentTests`, `ConsoleRecordTests`, `DistributionApiTests.Outside_a_test_environment_the_pipeline_may_not_change_which_distribution_runs`, the service console's Playwright test, and the laptop stack's insert-only test.

**Done for (1) (23 November 2026, the owner's approval of the north-star review, R-09-115).** `ServiceApprover` is dropped: it gives no role and no consent in the service console, its web app and the dictionary's runbook (`service.ApproverRequired` names the bank approver); every role map in this repository names `BankApprover` (`ConfigurationConsentTests.The_earlier_role_name_ServiceApprover_gives_no_consent`). An installation whose role map still says `ServiceApprover` must rename it before it takes this runtime release.

**Still open.** (2) The web app's Service section shows a pending install among the actions waiting for consent; a page of its own for pending installs and restores would read better. (3) Whether the bank approver of the runtime is the same person as the studio's Bank Approver role (R-11-31) is the bank's to say: they are separate roles in separate identity providers.

## I-156. The laptop deployment: what it leaves to an installation

**Issue.** The studio and one bank's runtime now run on a laptop as separate services in Docker containers (`deploy/laptop`, R-09-80). Some choices were the builder's, not the owner's.

**Recommendation and what was chosen.** (1) Three images on one base (ASP.NET Core 10 and Node 22's binary): the studio, the runtime (every runtime host, one image, a service per host) and Temporal with the SQL Server plugin; the runtime's image carries the studio's bundle as one shared layer, because the test bench and the front door run the studio's deploy runner. Recommendation: keep it for a laptop; for a bank's installation split the test bench out, so the hosts that run payments carry no studio code. (2) The supervisor builds each host's plan from the stored package (I-138 (3), R-09-94), and the laptop's File API front door starts and correlates payments on it (R-09-95); the insert-only test still runs its two payments with the front door's one-off runner (`deploy/laptop/bin/run-pinned.mts`) on Temporal. Recommendation: run the insert-only test's payments through the File API, so the laptop proves the path a bank's files take. (3) The bank's identity provider is a development provider of the runtime's own (`runtime/tools/TransactHub.DevIdentity`), refused in Production; the studio signs in with its own built-in development provider, standing in for the platform vendor's. (4) The images are built from source by the Dockerfile's build stages, or, where Docker builds have no network (as here), from the host's builds (`deploy/laptop/build.sh --local`); both end in the same final stages. (5) On a real Temporal server a WPS payment waits in real time for the scheme's acknowledgement, up to the bank's `ackWait` (ADIB's is twenty minutes, at least fifteen by the factory's bound, I-116): the simulator's acknowledgement comes about ten minutes after the file is sent, so the insert-only test runs its two payments side by side. Recommendation: a laptop profile of the simulators that answers at once, or a test calendar, so that a laptop run of a WPS file takes seconds. (6) A whole distribution posted to the release API's `POST /distributions` is refused by Kestrel's request body limit (30 MB by default) before the API sees it: the studio publishes to GitHub and the runtime installs from there (`/sources/install`, the service console), so only an upload is affected; the insert-only test shows the refusal of a second install of the same id with a small bundle. Recommendation: raise the limit for that route (`RequestSizeLimit`) to the largest distribution the studio builds, with a test.

**Done for (2) (23 November 2026, R-09-105).** The insert-only test sends its payments as a bank's files arrive: two salary files dropped on the stack's SFTP server, which the File API's inbox poller reads, and one posted to `POST /files` by the channel's client; the File API's front door pins, routes and starts each on the engine host over the compose network, on the workers of its distribution (I-193). `bin/run-pinned.mts` stays for a one-off run. The host ports may be moved (`TH_LAPTOP_*_PORT`), so the test can run beside another laptop stack. (5) still holds: each payment waits for the simulator's acknowledgement in real time.

## I-157. Insert-only at the database, and what the database cannot say

**Issue.** The store refused updates and deletes in its own code (`InsertOnlyGuard`, plan W2), but the database let the runtime's login change anything, so a host with a bug, or anyone with the hosts' connection, could update a distribution, an activation or a fact.

**Recommendation.** Run the migrations once as the databases' owner; run every host as a login granted SELECT and INSERT on the store and denied UPDATE, DELETE and ALTER, so the database itself refuses; prove it by trying.

**Done (23 November 2026).** On the laptop (`deploy/laptop/sql/grants.sql`, `runtime/tools/TransactHub.Migrate`, R-09-81): `db-init` migrates as the owner and exits; every host runs with `Store:Migrate=false` as `th_runtime`, denied UPDATE, DELETE and ALTER on the store's schema; the read model, which the projector rebuilds, is a database of its own the hosts may write. The insert-only test checks that every row of `release`, `release_activation` and `payment_fact` seen after the first activation is unchanged after a switch-over and two payments, that the runtime's login is refused an UPDATE, a DELETE and a TRUNCATE on each by the database, that it holds no UPDATE or DELETE, and that the same distribution id is not stored twice (409 at the API, the unique key at the database).

**Still open.** (1) The grants are the laptop's; a bank's installation has its own database administrators: give them `grants.sql` as the statement of what the runtime needs, for SQL Server and, written the same way, for PostgreSQL (`REVOKE UPDATE, DELETE, TRUNCATE`). (2) The owner's login can still change anything; that is the database's administration, outside the runtime. Recommendation: ledger tables (SQL Server 2022's append-only ledger) for `payment_fact`, `release` and `release_activation`, decided with the bank, as a second proof a database administrator cannot silence.

## I-158. The File API and the runtime's front door

**Issue.** Files reached the runtime by four roads that did not meet: the studio deploy runner's front door kept each inbound file (`POST /payments/files`), the native host kept what its activities rendered in memory, the consoles' resend wrote to a folder of their own, and nothing read a partner's SFTP folder. Nothing called the decryption binding (I-117), and the front door's logic (the intake map, the route, the pin, the start) lived in the studio's deploy runner, not in the runtime.

**Decided (owner, 23 November 2026) and built.** One runtime service, the File API (`runtime/src/TransactHub.FileApi`, its own process), is the way every file comes in and goes out (R-09-82 to R-09-84). The runtime's front door is part of the File API's process, behind its intake, so a file is checked, decrypted and kept before anything is started from it (R-09-85). It is a class of its own (`RuntimeFrontDoor`, `Dispatch`), with the intake map run on the worker of its queue and the payment started on the bank's engine host's supervisor, so another host can use it. The deploy runner keeps its own front door for its tests and shares the route with the runtime (`studio/src/deploy/route.ts`), both held to one fixture (`front-door-routes.json`).
- Inbound: `POST /files` (channels and corporates, `file-sender`; the SFTP gateway, `file-gateway`), `POST /files/ui` (a person, `file-uploader`), and the inboxes it polls on the bank's binding of `p.port.file-transfer.v1` (a folder on a laptop, a partner's SFTP folder at a bank). The source's `/api/wps/UploadSIFFile` and `/api/wps/ui/upload-sif-file` are these with message `uae.wps.sif`.
- On arrival: the message must be one the active distribution's dispatch table receives; size, extension and name checks; a duplicate by hash, or by name where the message's names are unique, is refused; an encrypted file is decrypted by `p.protect.decrypt-file.v1`'s native code-behind; both files are kept once with the host's signature, linked to the payment; each step is a fact on the file's chain.
- Outbound: `POST /outbox` keeps a workflow's file with its payment and leg and delivers it by the bank's binding; a host that sets `TH_FILE_API_URL` puts every file of `p.port.file-transfer.v1` there (`OutboxFileTransfer`); each delivery is a fact; the operations and service consoles resend through it (`FileApiTransferPort`, settings `Operations:FileApi:*`, `Service:FileApi:*`).
- The engine host takes the front door's start with the workflow and the legs it routed to, and a correlated message for a running payment (`POST /payments/{id}/messages`): on Temporal it is signalled to the payment's front door workflow, which routes it to the run that awaits it.

**What remains.** (1) On DTFx, Elsa and Flowable the engine host runs each payment in one harness run and refuses a message that arrives after the run started (409); the PSI that arrives with its PIF reaches the leg only on Temporal through the runtime's front door. Recommendation: give the harness runs an inbox the host feeds (the party's `start` and `sent` calls already reach the workers), or run those engines in their hosts as Temporal's are. (2) The File API reads the banks' port bindings from `TH_BANKS_DIR`, as the native host does, not from the installed configuration release (`GET /bindings`). Recommendation: read the binding in force from the store, so a binding's `validFrom` switches the File API with no restart. (3) The laptop set-up runs the File API as its own service (`deploy/laptop/compose.yaml`, `file-api`, the runtime image's `file-api` host, its role map `identity/role-map-file-api.json`, a `files` volume), with its front door off: the engine host answers on a loopback address only (R-08-22), so another container cannot start a payment on it. Recommendation: let the engine host's supervisor answer the File API on the compose network with its token, then set `FileApi__EngineHost` and `FileApi__Workers__*` there. (4) The old `POST /payments/files` of the release API stays for the deploy runner; once every file goes through the File API it should be removed.

**Done for (1) and (3) (23 November 2026, R-09-93, R-09-95).** (1) On DTFx, Elsa and Flowable the engine host takes a correlated message for a payment whose run has started: it is put on the payment's outbox (signal `deliver`, `StoreSignals.DeliverToRun`) and the run, which follows the payment's live path, hands it to the instance that awaits it; DTFx and Elsa runs follow the wall clock in the host. Shown by `EngineHostTests` on DTFx and Elsa; on Flowable the binding's live path is the one its hold decisions already take, but no test runs a Flowable payment that awaits a message in the host yet (I-165). Since 24 November 2026 one does, before and after a restart of its host (I-165, R-09-198). (3) The supervisor answers beyond loopback when its token is configured (`TH_HOST_URLS`, `TH_ENGINE_HOST_TOKEN`); on the laptop stack every engine host listens on the compose network with no published port, and the File API calls it with the token by reference (`FileApi__EngineHost`, `FileApi__EngineHostTokenRef`; `LaptopDeploymentTests`). The laptop still sets no `FileApi__Workers__*`: the intake maps need the workers, which the laptop stack does not run as services yet. (4) stays open.

**Done for (2) (23 November 2026, R-09-112, R-09-113, Issue I-213).** The File API reads the bank's bindings from the configuration release in force, live, at each poll, delivery and decryption: a binding a new configuration release brings is used with no restart (`FileApiTests`, the resend test, which switches the outbox's folder by a later release). A File API given `TH_BANKS_DIR` does not start.

**Done for the laptop's workers (23 November 2026, R-09-105).** The laptop sets `FileApi__Workers__uae.wps` and `FileApi__Workers__p` to its `workers` service, by distribution, and its engine hosts name the same service (`TH_HOST_WORKERS_URL`); every host that puts a file names the File API (`TH_FILE_API_URL`), and the operations and service consoles resend through it (`Operations__FileApi__*`, `Service__FileApi__*`). The inbox poller reads only the runtime's bank's inboxes (R-09-104). (2) and (4) stay open.

**Done for every host's files (23 November 2026, R-09-138, R-09-141).** A host that put a file through the File API still read, listed and removed the bank's files itself, on the folder or SFTP partner of the binding (`OutboxFileTransfer` kept the direct adapter for them), and a host that named no File API wrote to the folder directly. Now a read, a listing and a removal go through the File API too (`POST /transfer`, role `file-outbox`), and a host that names no File API is refused a `directory` or `sftp` binding unless it says it is a development machine (`TH_FILES_DIRECT=dev`); a console's resend folder (`Operations:FileTransfer:Folder`, `Service:FileTransfer:Folder`, `TH_FILE_TRANSFER_DIR`) likewise. The remaining direct readers of a bank's files are the File API itself and the in-memory `simulator`. (4) stays open.

## I-159. The scheme declares no file rules

**Issue.** The File API checks a file's size, extension and name and refuses a name received before, but the scheme manifest says nothing of a message's file: not its name pattern, its extensions, its largest size, whether a name may come twice, nor whether it is encrypted. The source's WPS upload checked a file name structure and date and refused a repeated file name (codes 002, 005).

**Recommendation.** Add a `file` block to a scheme manifest's message (`namePattern`, `extensions`, `maxBytes`, `uniqueName`, `encrypted`) in a schema change with its lint and mutation case, and have the File API read it from the active distribution. Until then the rules are the installation's settings (`FileApi:Messages:<message>:MaxBytes|Extensions|NamePattern|UniqueName`, default: any extension, 50 MB, names unique), and `uae.wps` sets none, as it is provisional.

**Done (24 November 2026, R-09-209).** A scheme manifest's message may declare a `file` block (`namePattern`, `extensions`, `maxBytes`, `uniqueName`, `encrypted`; `spec/schemas/scheme.schema.json`, lint L55 with mutation case M94). The File API reads it from the scheme of the bank's active distribution (`MessageEntry.File`, `FileRules.For`): each rule the scheme declares stands, a rule it does not declare is the installation's setting as before, and `encrypted` refuses a file in the clear (`file.NotEncrypted`) or an encrypted one (`file.Encrypted`). `uae.wps` declares none, as it is provisional. `FileApiTests.A_messages_file_rules_are_the_schemes_in_the_active_distribution` declares rules on a test release of the scheme. Found on the way: `OpenPgp.IsEncrypted` threw on a text file that BouncyCastle reads as a packet of an unknown version; it now answers false.

## I-160. How a received file names its message and its payment

**Issue.** (1) An inbox must say which message its files are, and the banks' bindings do not: DIB's `pickup.folder` holds the channel's salary files and ADIB's `centralBank.ackFolder` and `rtcFolder` the Central Bank's answers, but neither names a message, and neither bank names a labour ministry or core banking partner at all. (2) A message for a running payment is correlated by its keys, but the scheme does not say where a file carries them: the factory's simulators put a `correlation` beside the message, a real PSI or ACK file has only its content and its name.

**Recommendation.** (1) Have each bank name, in a new binding with a later `validFrom`, each inbox's message (`<partner>.message`, or `<partner>.inbox.<name>.*`) and the partners it lacks (`ministry.*`, `coreBanking.*`); until then the File API polls only the inboxes that name a message, and an installation may name more in `FileApi:Inboxes`. (2) Give each `deliver` disposition a correlation map (an activity of type `map` that reads the key from the file), so the front door reads the key as the scheme says; until then the front door matches the keys the sender gives (`correlation` on `POST /files`) and, failing those, the stem of the file name, which pairs a PIF with its PSI only when both share it.

**On the laptop (23 November 2026, R-09-105).** The laptop's binding of each bank's file transfer (`deploy/laptop/banks/<bank>/file-transfer.yaml`, carried in the bank's signed configuration release that the insert-only test has the studio build and puts in force with consent, R-09-110 to R-09-112) names each inbox's message: the pick-up folder's `uae.wps.sif`, the Central Bank's `uae.wps.ack` and `uae.wps.rtc`. Synthetic folders on the stack's SFTP server; the banks' own bindings still name none, and (2) stays open.

**(2) Done (24 November 2026, R-09-212):** see Issue I-267. A deliver disposition names its correlation map; `uae.wps.ack`'s is `uae.wps.map.ack-correlation.v1`. The PSI and the RSU have none yet (Issue I-311).

## I-161. The banks' SFTP partners have no pinned host key, and the login's form

**Issue.** The `sftp` adapter refuses a partner whose host key the binding does not pin (`<partner>.hostKeyRef`), and DIB's and ADIB's bindings pin none: the source's SFTP gateway trusted what its profile held, and the profiles are not in the source. The login is one reference per binding (`secretRef`), and a reference holds one value, so its form (`<user>:<password>`, or `<user>` and a new line and a private key) is the runtime's choice; the source kept the user in the gateway's profile.

**Recommendation.** Ask each bank for its partners' host key fingerprints and add them by reference in a new binding with a later `validFrom` (`centralBank.hostKeyRef: vault:...`), one reference per partner, with the login per partner (`<partner>.secretRef`, `<partner>.user`). Until then the bindings load and name their partners, and no session opens.

**On the laptop (23 November 2026, R-09-105).** The stack's SFTP server makes its host keys on first start; the insert-only test reads them out of band (`ssh-keygen -lf`) and pins them by reference (`TH_LAPTOP_SFTP_HOST_KEY`), and the login is made at the stack's start (`TH_LAPTOP_SFTP_PASSWORD`), both from the environment only. The banks' partners still have no pinned host key.

## I-162. A simulator's scenario runs once per worker on the engines

**Issue.** A simulator scenario is a sequence: `p.sim.core-banking.v1:reject-first-then-accept` refuses the first posting and accepts the rest, `reject-two-then-accept` the first two. The studio interpreter runs one simulator per port for the whole payment. The deploy runner starts one worker process per task queue, and each worker holds its own copy of every simulator (`studio/src/worker/main.ts`, one `PartyService` per process), so the sequence starts again on each queue. On the inward leg the credits post on queue `th-p` (`p.post.accounting.v1`) and the return posts on `th-uae-wps` (`uae.wps.post.return.v1`): the return is that worker's first posting, so it is refused, the leg opens a repair hold, the operations simulator refuses it and the leg ends `pending`, the payment `review`. The interpreter ends `settled` (TB-WPS-IN-002) and `returned` (TB-WPS-IN-003). It shows on Temporal, DTFx, Elsa and Flowable alike, and it is the simulators', not the engines'.

**Recommendation.** Run each port's simulator in one place for a deployment: the deploy runner starts the simulators once (a party service of their own, as `studio/src/conformance/party-server.ts` already is) and every worker sends its port calls there, keyed by the payment's conversation; then remove `knownIssue: I-162` from TB-WPS-IN-002 and TB-WPS-IN-003. Until then the two cases carry the mark on the four engines.

**Done (23 November 2026, R-09-89).** The deploy runner names one folder for its workers (`TH_SIM_SHARED_DIR`, in the run's work folder) and every move of a scenario's place is made there under a lock, keyed by simulator, scenario and conversation (`FolderSequences`, `studio/src/sim/runtime.ts`); an inward simulator's leading sends are played once, whichever workers start it. `studio/test/sim-shared.test.ts` shows the sequence shared, a retry keeping its answer, another conversation starting again, and three processes counting under the lock. The `knownIssue: I-162` marks of TB-WPS-IN-002 and TB-WPS-IN-003 are removed: the test bench runs TB-WPS-IN-001 to 004 through the front door on Temporal, DTFx, Elsa and Flowable on PostgreSQL, each with the interpreter's outcome (settled, settled, returned, review).

## I-163. The production adapters' counterparts are the bank's, and the source names none

**Issue.** The runtime now has production adapters behind the platform's ports (R-09-90): sanctions and fraud screening over REST, the channel's notices by e-mail and SMS, the business calendar and the participant directory from the bank's reference tables or a REST service, and the outward facts as a signed webhook. The request and reply forms are the runtime's own (`/screen` with `result`, `/messages`, `/business-date`, `/participants/{code}`, `/facts` with `Idempotency-Key` and `X-TH-Signature`), because no bank has told which screening engine, gateway or event endpoint it runs, and the source holds none. The four banks' bindings (`content-configurations/banks/<bank>/platform-adapters.yaml`) are shapes: hosts under `.invalid`, credentials by `env:` reference, so every call fails until the bank gives its own. The notice's recipient is the channel's mailbox or number in the binding, not a party of the payment, so no personal data reaches a notice. A screening request carries the parties the activity reads to the bank's own engine, which must see them. The fraud engine's `pending` is taken as `suspect`, since `p.screen.fraud.v1` has no pending outcome.

**Recommendation.** Ask each bank for its engines' and gateways' interfaces and put a mapping adapter in front of the runtime's form where they differ (a `map` of the port, as the scheme's files have), then give each bank a binding with a later `validFrom`, its hosts and its references. Give `p.screen.fraud.v1` a `pending` outcome in its next major if a fraud engine can answer later.

## I-164. The scheme declares no report message and no report layout

**Issue.** Reconciliation (R-09-92) runs when a scheme's report file arrives through the File API, but the scheme manifest names no report message, and the WPS sources describe no reconciliation report of the Central Bank. Which messages are reports is therefore the installation's setting (`FileApi:Reports:<message>:Scheme`), and the report is read in a layout of the runtime's own: CSV with a header naming `reference` (the end-to-end id) and any of `amount`, `currency` and `status`, or JSON lines. A payment the store has that the report does not name is not a break, since a report names no period. `p.reconcile.scheme-report.v1` reads `header, status, messages` and its contract says nothing of lines; the runtime's code-behind is the File API's intake, not a native activity an engine calls.

**Recommendation.** Add a `reconcile` disposition to a scheme manifest's message (`{ activity, map, period }`) in a schema change with its lint and mutation case, with a `map` that reads the scheme's layout into report lines and a period that lets the match find what the report misses; then release `p.reconcile.scheme-report.v2` with a contract over the lines, and let the File API read both from the active distribution.

**Done, the mechanism only (24 November 2026, R-09-210).** A scheme manifest's inward message may have a `reconcile` disposition (`{ activity, map, period }`; lint L55, mutation case M95). The File API reconciles such a file on arrival and never routes it: its map, run on the distribution's worker, reads the scheme's layout into report lines (`messages.report.lines`, `RuntimeFrontDoor.ReportLines`), and `SchemeReconciliation` records each payment of the bank and scheme accepted in the period before the report's arrival that no line names as a break (`not-in-report`), naming the disposition's activity and period in the run's fact. The installation's `FileApi:Reports:<message>:Scheme` still names a report the scheme does not declare, read in the runtime's own layout. The upload page offers a report message. WPS declares no report and `p.reconcile.scheme-report.v2` is not released: the Central Bank gives no report layout, so writing one would invent a scheme fact (Issue I-312). Tests: `FileApiTests.A_report_the_scheme_declares_is_read_by_its_map_and_reconciled_over_its_period`, `ReconciliationTests.A_payment_of_the_reports_period_the_report_does_not_name_is_a_break` (both databases).

## I-165. DTFx and Elsa runs do not survive a restart of their host, and Flowable deploys per payment

**Issue.** The engine host runs DTFx and Elsa payments in real time and takes messages after a run started (R-09-93), but each payment still runs in a harness run of its own with an in-memory orchestration service: if the host stops, its open payments stop with it, and nothing resumes them (Flowable's binding resumes from its journal, I-89). DTFx's SQL Server provider would make the orchestration durable on SQL Server only; there is none for PostgreSQL. Elsa's own persistence is a store of its own beside the runtime's. On Flowable each payment still deploys the distribution's BPMN in a tenant of its own run, because external worker jobs are acquired per tenant and one binding per payment would take another payment's jobs from a shared tenant. No test runs a Flowable payment that awaits a message through the host's live path.

**Recommendation.** Persist the harness's orchestration history to the store, insert-only, as facts per episode (the claim check already is), and rebuild the in-memory service from them when a host starts, so that one service per distribution serves all its payments and survives restarts on both databases; then run Elsa the same way. On Flowable, deploy once per distribution in its tenant and run one binding per distribution that serves every instance of the tenant. Add a host test of a Flowable payment that takes a message after it started.

**Done (24 November 2026, R-09-196 to R-09-199).** Each DTFx, Elsa and Flowable distribution now runs on one engine service for as long as its host runs (`runtime/tools/TransactHub.EngineHost/HarnessService.cs`: the harness started with `serve <plan>`), and every payment of the distribution runs on it. DTFx and Elsa keep the service's history in the store, insert-only and chained, on one chain per engine and distribution (`StoreHistory`, subject `<engine>-history:<hash>`, kind `engine-history`): on DTFx each message an instance takes, each episode (its new history events, the activities and timers it scheduled, its state) and each payment's start; on Elsa each execution's start, what reached it and the operations it handed out, so that Elsa's own state is never kept beside the store. Each fact is written before the change it records, so the chain is always a prefix of what the service did. A service that starts again folds its chain back into the service it was, restores each open payment's payload and staged writes from the claim check's facts, follows each payment's live path again and reports the payments it took back, which its host runs to their end; a timer due meanwhile fires at once, an activity that was running runs again (at least once; its step fact is recorded once, `StepFacts.SkipRecorded`), and a payment's end is told to the host until the host records it (`reported`). On Flowable the binding serves the distribution: one deployment in the distribution's own tenant, one binding as the external worker of every instance of that tenant, its journal keyed by the distribution (`dist-<hash>`); an ended payment the host recorded leaves the journal's state. Kept: messages after start (R-09-93) on all three, values as of each step (R-09-135), the distribution named on every attempt with a worker router per platform workflow (`ServiceParty`), the plan from the store (R-09-94). Evidence: `EngineHostTests` ("survives a restart of its host" on DTFx, Elsa and Flowable, each on PostgreSQL and SQL Server: the host is killed while the payment awaits its message, a new supervisor starts, the message is delivered and the payment ends `done` with its chains verified; "serves many payments of a distribution on one service" / "from one deployment"), and every earlier `EngineHostTests` case. What remains: I-300 to I-304.

## I-166. An await's business calendar is still Saturday and Sunday only

**Issue.** A hold's expiry counts the bank's business calendar table with its holidays (R-09-88), but an await whose `calendar` is `business` (`replyCalendar`, the WPS acknowledgement waits) is still counted on the default calendar, Saturday and Sunday, in the studio interpreter, the engines and the Flowable binding. The two waits of one payment can therefore count a public holiday differently.

**Recommendation.** Count every business-calendar wait on the same table: hand the calendar to the engines with the deployment's values (it is a reference table the configuration release carries), and replace `DEFAULT_CALENDAR` and `BusinessCalendar.Default` by it where a run has one; prove it with a WPS acknowledgement wait across a holiday on every engine.

**Still open (23 November 2026).** The business calendar the studio interpreter and the party hand a hold now counts the bank's own closed days too (R-09-102), but an await's business calendar did not fall out of the cut-off work: the engines count an await on `BusinessCalendar.Default` and would need the calendar with the deployment's values, so the studio interpreter was left as the engines are, to keep their waits equal.

**Done (24 November 2026, R-09-183).** Every business-calendar wait counts the bank's business calendar, holidays and the bank's own closing days included, on every engine, as a hold's expiry and the turnaround already did. The studio interpreter counts an await's business timeout on `Run.businessCalendar` (the bank's reference tables of domains `business-calendar` and `bank-calendar`). The engines' shared interpreter (Temporal, DTFx, Elsa) and the Flowable REST binding count it on `Deployment.BusinessCalendarAt`: on a runtime's engine host the calendar of the configuration release in force, read from the store (`Deployment.LiveCalendar`, installed by `StoreValues.Install` beside the live values), else the plan's reference tables, read as the studio reads them; a hold whose answer names no calendar takes the same one. The Flowable kit harness is handed the run's calendar in place of `DEFAULT_CALENDAR`. `Duration.Add(t, s, bool business)` is gone, so no wait can fall back to Saturday and Sunday by mistake. The kit's scenarios carry the store's calendar tables, so every engine counts the calendar the interpreter counts. Conformance CK-AWAIT-08: an await of one business day from Friday 27 November 2026, over the bank's holiday on Monday 30 November, ends on Tuesday, four days on (Saturday and Sunday alone would give three).

## I-167. A requeue cannot be told from a retry by the kit's stubs

**Issue.** Requeue (R-09-91) runs a dead-lettered step with its full retry policy, a retry gives it one attempt. The kit's stub fails a step only on its first call (`failTimes` counts the first call's attempts), so CK-PARK-03 shows the requeue's path on every engine but not that the second run had the full policy. On Flowable a dead-lettered payment's run waits for a requeue only in production mode with the runtime's live path; elsewhere the binding still ends the run at the dead letter.

**Recommendation.** Let a stub fail a number of attempts across calls (`failAttempts`), and add a scenario whose requeued step fails once more and then succeeds, which a retry would not survive.

**Done (24 November 2026, R-09-208).** A kit stub may fail a number of a step's attempts counted across its calls, `failAttempts` (`studio/src/interp/interpreter.ts`, counted per instance and step on the run, so on every engine through the party service). CK-PARK-04 fails the first three attempts of its step: both attempts of its policy fail and it parks; the requeue runs its full policy, whose first attempt fails once more and whose second succeeds (four attempts in all), which one more attempt (a retry) would not survive. The kit holds 59 scenarios (with CK-AWAIT-08 of wave 6); the counts derived from it are updated (`studio/test/execution.test.ts`, the READMEs, `docs/north-star`, `docs/TRAINING.md`, Annex C's table). CK-PARK-03 and 04 pass on the studio interpreter, DTFx and Elsa.

**Engines (24 November 2026).** The whole kit, 59 scenarios, passes on DTFx (351 s) and Elsa (354 s); CK-PARK-01, 03 and 04 pass on Temporal. On the embedded Flowable harness CK-PARK-01, 03 and 04 do not pass (`no handler waits for signal requeue`): as this issue says, a dead-lettered run there waits for an operator's control only in production mode on the runtime's live path.

## I-168. A reconciliation break is resolved by status only

**Issue.** The break queue resolves a break by an explanation or by adopting the scheme's status (R-09-92). An amount or currency that differs, or a payment the bank has no record of, has no resolution that changes the system: posting a correction, returning funds or starting an investigation are business processes the platform has no workflow for.

**Recommendation.** Decide with the owner which breaks start a workflow (an exception request on `p.wf.request.v1`, a return) and add those resolutions, each with the bank approver's consent where it changes the system.

**Owner's decision (24 November 2026):** accepted. Build the resolutions as recommended: a break may start an exception request on `p.wf.request.v1` or a return, each with the bank approver's consent where it changes the system. To build in wave 7.

## I-169. The Flowable production black-box test expected a minute the run does not keep

**Issue.** `studio/test/blackbox/e2e-wps-flowable-production.test.ts` ("on SqlServer: settles in real time ...") looked for the binding's timer line with fixed minutes: `placed at 2026-11-17T06:02:..`, written `2026-11-18T06:00:..`. Business time is the wall clock shifted to 06:00 on the plan's evaluation date, so those minutes hold only while the acknowledgement wait starts within the first wall minute of the run; on a slower run (SQL Server's journal writes, I-137, or a loaded machine) the wait starts later, both minutes move, and the line no longer matches although the binding placed it. The binding's code (`PlaceTimers`) places the timer as specified: two minutes after the wait began, from the bank's value, not the written day. Run again here (23 November 2026) both cases passed with the old pattern too (the waits began at 06:00:10 and 06:00:07), so the failure was not reproduced; the only paths that leave no line at all are a reschedule the server refuses and a timer already at its due, neither seen.

**Recommendation.** Done: the test now reads the line's two times and checks what the requirement says, that the timer was placed one business day minus two minutes before the day the BPMN wrote, whatever minute the wait began. If the line is ever missing again, keep the binding's log of the run (the assertion prints its tail) and look at the reschedule's answer.

**Done (23 November 2026).** The test is corrected as the recommendation says.

## I-170. The banks' configuration sets accept earlier packs

**Issue.** Factory release 1.9.0 adds `expiryCalendar` to the holds (R-09-88) and routes inward payments to `p.wf.inward.v2` (R-09-87). The four banks' lock files moved to 1.9.0, but their configuration sets name earlier packs in `accepts`, so a bank cannot yet set `expiryCalendar` (the default, business days, applies) and its configuration is checked against the 1.7.0 parameter contract.

**Recommendation.** Release each bank's configuration set again, accepting the 1.9.0 packs, when the bank's configurator next changes a value, with its approver's consent.
## I-180. Erasure in an insert-only store: the design and what it does not do

**Issue.** The owner asked for retention and erasure with periods the bank sets. The store is insert-only, at the database too (R-09-81): a host may insert and read, never update or delete, and every fact is chained to the one before it. A payment's personal data is in several places: the read model (masked), the store's payment and line rows (`bulk`, `payment`, `party`, their supplementary data), the kept messages and files (`message`, `stored_file`, `file_content`), and the payload facts of the claim check. Deleting or overwriting any of them would break the rule, the chain and every distribution's record.

**Decided and built (R-05-70).** An erasure is a tombstone: a fact `erasure`/`erased` on the payment's own chain, with the consent's id, who asked, who consented and why (the retention period or a request), and no value. It is asked for by a supervisor and carried out only on a different bank approver's consent (R-09-77, the console's system actions); it is refused while the payment is open, erased already, or under a legal hold, when asked and again at the consent (a refusal at the consent is recorded as `failed`). From the tombstone the read model shows every field a view masks as `[erased]`, which a rebuild gives again; the console refuses the payment's kept documents (410) and its unmasking. A payment is due when it ended longer ago than the bank's `retentionPeriod` (R-05-66, P0D by default: nothing is due until the bank sets one). Legal holds are facts on the console's chain: placed at once by a supervisor or a bank approver, released only on a bank approver's consent.

**What it does not do.**
1. **The values stay in the store.** The payment rows, the kept files and the payload facts still hold them, readable by whoever reads the database or a backup. The tombstone governs what the runtime gives out, not what the database holds.
2. **Only the operations console honours it.** The channel enquiries show no personal field today; the release API, the File API's outbox, the engines' own histories (Temporal's event history, DTFx's and Elsa's instance stores, Flowable's variables) and the observability back ends are not read for it.
3. **Registers.** A corporate's record in the bank's registers (R-05-62) is not erased this way; a register change can blank fields, but every earlier version is kept by design.
4. **A field shown whole** (`mask: none`, a corporate's name) is erased too, since it declares a mask; a field that declares none is not personal by the view's own lint (L38) and stays.

**Recommendation.** To make an erasure reach the bytes, crypto-shred: encrypt each payment's personal values and kept files at acceptance with a key of its own (a data key per payment, wrapped by the bank's key in its key vault, as the secret providers of I-73 already reach), keep only the wrapped key in the store, and erase by destroying the data key in the vault, which the insert-only store does not hold. That needs the store's write path (accept, keep a file, the claim check) and every reader to decrypt, and a decision with each bank's data protection officer on the periods per kind of data (payments, kept files, registers, logs) and on the engines' histories. Until then, say to each bank that an erasure in TransactHub masks and refuses, and that the database keeps the values for as long as the bank keeps the database and its backups.

## I-181. Unmasking, and the register API's answer to core banking (I-149 (1) and (2))

**Issue.** The console shows personal fields masked only (I-100, I-149 (2)); the owner asked for an unmask on a bank approver's consent. I-149 (1) left open whether core banking should read the registers masked.

**Decided and built.** Unmasking (R-05-69): an operator asks for one field of a payment or a line with a reason; a different bank approver consents through the console's system actions; the value is shown to the asker only, for the bank's `unmaskWindow` (a dial of the platform's view, P0D by default, so unmasking is off until the bank sets it, at most four hours). The request, the consent and each view are facts with ids only; the answer carries `Cache-Control: no-store`. The register API (R-09-101) keeps answering core banking unmasked by default, and answers masked where the installation sets `Operations:Registers:SystemReadsMasked=true`.

**Still open.** (1) Unmasking reads a field of a payment or its lines; a leg's fields and a register record's are not unmasked yet. (2) The value is shown in the browser: a screenshot or a copy is outside the runtime's reach. (3) Whether a bank approver may consent to unmasks of every tenant (`MayConsent` is any tenant's bank approver) only matters if a runtime ever served several banks (R-09-76).

**Recommendation.** Ask each bank's security officer to choose `SystemReadsMasked` and the unmask window before go-live, and record both in the bank's README with the date.

## I-182. The operations console's notification port, and the turnaround alerts

**Issue.** The owner asked for turnaround breaches to be alerted "through the notification port". The factory has no port for operator alerts: `p.port.channel.v1#notify` tells the originating channel of a payment's outcome, and the alert rules of R-09-72 go to the observability stack, not to a person.

**Decided and built (R-05-68).** The console has a notification port of its own, bound by its settings (`Operations:Notify:Adapter`): `log` by default (a warning with ids only), `simulator` for tests, `webhook` (a POST of the alert, ids only, to `Operations:Notify:Url`). The console sweeps for breaches every `Operations:TatSweepSeconds` (60 by default) and alerts each once: a fact `tat`/`breached` on its chain, then the port; a port that fails is recorded as `alert-failed` and not retried. The limit is the bank's `tatLimit` (P0D: none) in force on the day the payment was accepted, the scheme view's where the bank set one. The breach list reads the read model, up to 100 000 payments a pass.

**Recommendation.** Add `p.port.notification.v1` to the platform pack (operations: `alert`, style send) with a simulator, so a bank binds where operator alerts go in its configuration release like any port, and move the console's adapters behind it; decide with each bank whether a turnaround is counted on the business calendar (today elapsed time) and whether a breach should also be a metric for the alert rules (I-143).

**Done (23 November 2026, owner's decisions, R-05-80, R-05-81, R-09-150 to R-09-154, R-11-85, R-11-86).** The platform pack has `p.port.notification.v1` (operation `alert`, send) and `p.port.event-stream.v1` (operation `publish`, send), each with its simulator (factory 1.15.0, change set CS-2026-11-23-010). The runtime's notification adapters are `simulator`, `log`, `webhook` and `mqtt` (MQTTnet); its event-stream adapters `simulator` and `kafka` (Confluent.Kafka, an idempotent producer with acks all). The console's own adapter is gone: its turnaround alerts go through the bank's binding of the port in force, else its log, and `Operations:Notify:*` is no longer read. The payment events go out from the store's outbox, references only, at least once and in order per payment, the position insert-only. The turnaround limit counts business days of the bank's calendar, its closing days included (`p.view.operations.v1` 1.6.0). Each real bank binds both ports by placeholder (I-163). Whether a breach is also a metric for the alert rules stays open (I-143). What is left: I-255 to I-259.

## I-183. The fixed WPS reports: what the source's reports had that the read model does not

**Issue.** I-144 left open which of the source's fixed reports a bank needs. The owner asked for the SIF summary, the SIF detail and the upload history as fixed reports, CSV only, every export audited.

**Decided and built (R-05-67).** A view declares its fixed reports as data; the WPS view 1.5.0 declares the three, each column a field the view already declares (with the old screen it came from), plus the file's status. The console renders any declared report the same way, so no WPS screen is written; each download is an export fact (R-05-65) naming the report.

**Still open.** (1) The source's upload history showed the uploading user's name and the channel's error text per record; the new model holds the channel user's id (masked) and the validation findings, not a name. (2) The source's SIF detail had the MOHRE update date; the new model has the labour ministry's status only. (3) Excel and PDF are not offered (I-144, CSV only). (4) A report is not sent on a schedule (deferred by the owner).

**Recommendation.** Confirm with DIB's and ADIB's operations that the three reports, with these columns, replace the source's; add a column only as a field of the view (an operations-view release), never as a report's own source.

## I-184. The channel's enquiries of core banking: what the port contract does not say

**Issue.** The owner asked for balance, associated accounts and charge preview as channel enquiries through the core-banking port. `p.port.core-banking.v1` declares reserve, release, post, verify and reverse only; the runtime's port already had a balance call, used by no factory activity. The source's "associated accounts" (`WpsAssociatedAccountsEndpoints`) were the bank's own GL accounts a WPS upload posts against (nostro, suspense, P&L, VAT), from its GL mapping, and its charge preview came from its charge resolution service, the one the workflow priced with.

**Decided and built (R-09-100).** Three enquiries for `channel-enquiry` callers: a balance of an account the corporate register holds for the corporate, the accounts core banking holds for the corporate's CIF (each marked whether the register holds it), and a charge preview for a file of a scheme, records and amount. Each asks the bank's core-banking binding in force: the `simulator` answers from its settings, `gateway-rest` (and `file-bulk`, whose enquiries go to its gateway) from `accountsPath`/`accountsFunctionId` and `chargesPath`/`chargesFunctionId`. A corporate or account the register does not hold is 404 and nothing is sent; a binding with no path is 503.

**Still open.** (1) The bank's GL accounts of the source's "associated accounts" are the accounting template's parameters in the new model (`p.post.accounting.v2`), not a core-banking answer; they are not offered as an enquiry. (2) The charge preview is core banking's; the workflow prices with `p.enrich.charges.v1` and the bank's slabs, so the two can differ.

**Recommendation.** Add `balance`, `accounts` and `charges` to `p.port.core-banking.v1` in its next release (and their simulator scenarios), so a binding's settings for them are checked like the others (L48); decide with each bank whether the charge preview should be the platform's own pricing (`p.enrich.charges.v1` on the bank's dials, what the payment will post) instead of core banking's, and whether the channel needs the GL accounts at all.

**Done for (1) (23 November 2026, owner's decision, docs/OWNER-ACTIONS.md A3, R-05-91).** The bank's own ledger accounts (settlement, suspense, charge income and VAT of each accounting activity, as its configuration in force names them, an account it does not set shown as not set) are a read-only page of the operations console for supervisors (`GET /ops/api/ledger-accounts`). They are bank settings, changed only in the studio, and not offered to channels: the channel keeps the corporate's own accounts from core banking. (2) stays open; what the view cannot know is I-282.

## I-185. Work management: what the queue does not yet do

**Issue.** The owner asked for assignment, escalation and reassignment of a payment or a queue item, bulk actions, saved filters per user and a live queue.

**Decided and built (R-05-71).** Each item (a payment with an open hold, or an operator action waiting for its second approver) has its own chain of `work` facts; the rules are the tenant's roles; a bulk change and a bulk operator action apply each item's own rules and its second approver, item by item; saved filters are facts on each person's own chain; the queue long-polls the store's outbox and reloads when anything was written. Assigning changes no payment and needs no bank approver.

**Still open.** (1) An escalation goes to the supervisors as a role; there are no named teams or levels beyond the count, and nothing escalates by itself on time (a breach is alerted, R-05-68, but not escalated). (2) The queue reads the facts of every item it shows; past some thousands of open items it needs a projection of its own, like the read model. (3) An assignee is named by the identity provider's subject; the runtime has no directory to offer names. (4) The live queue wakes on any write to the store, not only on its own items.

**Recommendation.** Decide with each bank's operations whether an item escalates by itself after a time (a dial of the platform's view) and to whom, and project the queue into the read model when a bank's open items pass a thousand.

**Owner's decision (24 November 2026):** accepted. Escalation after a time is a dial of the platform's view, off by default, with the role it escalates to; each bank's operations sets it. To build in wave 7.

## I-186. A bank's own reference table built a distribution, not a configuration release

**Issue.** Reference tables travel to the runtime in the bank's configuration release, never in a distribution (R-01-25). But the studio's approval classified a bank's own reference table as bank pack content: approving `b.ref.branches.v1` built a new distribution of the bank (without the table) and no configuration release, so the table reached the runtime only with the next change of the bank's values.

**Decided and built (R-11-52).** A bank's own reference table is values: its approval builds the bank's configuration release, which carries the table with all its editions, and no distribution. The example bank has a calendar of its own closing days (`b.ref.bank-calendar.v1`) and its cut-off times (`b.ref.cut-offs.v1`), synthetic, edited in the studio's Bank page by the Bank Configurator as a new edition and approved by the Bank Approver.

**Still open.** (1) The runtime serves the tables under `/lookups` (R-09-75) but no activity reads the bank's own calendar or cut-offs yet: the calendar port's `reference` adapter reads the shared `business-calendar` domain, not the bank's `bank-calendar`, and no step refuses or defers a file after the cut-off. (2) A shared reference table approved by the platform approver still builds distributions for the banks that take it, not configuration releases.

**Recommendation.** Have the calendar adapter read `uae.p.ref.business-calendar.v1` and the bank's `b.ref.bank-calendar.v1` by date, and add a platform step (or a front-door rule) that reads `b.ref.cut-offs.v1` for the file's scheme and direction, each with its tests; make a shared reference table's approval build each taking bank's configuration release.

**Done for (1) (23 November 2026, R-12-28, R-09-102).** `p.decide.cut-off.v1` reads the bank's cut-offs, its own closing days and the market's calendar, each from its reference table by the bank's configuration (R-11-51), and moves a late file to the next business day or refuses it (I-191); the WPS salary file calls it after its business date, on the studio interpreter and every engine, and the operations console shows it (R-05-72). The calendar port's `reference` adapter and a hold's expiry count the bank's closed days (domain `bank-calendar`) as well as the market's. (2) stays open.

## I-187. The source slice's bundle carried a release the factory had since published

**Issue.** `studio/test/blackbox/e2e-wps-source-slice.test.ts` failed at its first step: the extraction bundle (`content-configurations/extractions/wps-outward-cbx`) held `uae.wps.wf.salary-file-out.v1` at 1.3.0, written when the factory's leg was at 1.2.0; the factory then published its own 1.3.0 (the employer looked up in the party register, R-12-27). The import refused it rightly (R-01-10: a published release is never edited). The test and its bundle were stale, not the code. The bundle's configuration set (1.7.0) was also no later than the example bank's own.

**Decided and fixed.** The bundle's leg is now the factory's current leg at its next minor (1.4.0), with the source's writer in the render step; its test WPS-CBX-006 runs the party register step; its configuration set is the example bank's next release. The generator (`docs/import/extract_wps_outward_cbx.py`) now derives the leg's release from the factory's, so it cannot fall behind again, and the test reads the leg's release from the bundle. `e2e-wps.test.ts` likewise reads the shipped configuration release and makes the next ones after it. Once step 1 passed, step 4 failed the same way: it expected the runtime's branch import to report nothing, but since one runtime serves one bank (R-09-76) the import names the other banks' configuration sets it does not take; the test now allows exactly those.

**Recommendation.** Regenerate the bundle with the generator whenever the factory's leg changes.

## I-188. The original platform's capabilities the owner deferred

**Issue.** The original platform had treasury screens (the nostro grid, FX quotes, liquidity, funding and exposure), compliance case work (compliance cases, sanctions case review and suspicious activity reports), incident management (incident command, collaboration, shift handover, workflow replay and documents), and regulatory and scheduled reports. None of them is in the new runtime, and no requirement asks for them. The factory has a liquidity port (`p.port.liquidity.v1`) with no runtime adapter, and the screening ports (`p.port.sanctions.v1`, `p.port.fraud.v1`) answer a payment's screening only, with no case behind a hit.

**Decided.** The owner deferred all four. A payment's screening still runs through the bank's screening engine (R-09-90, I-163); a hit holds or refuses the payment as its workflow says; the case work stays in the bank's own compliance system.

**Recommendation.** When the owner takes one of them up, write it as requirements first: treasury as views over the store and the liquidity port, compliance and incident cases as their own register kinds or workflows with the bank approver's consent where they change the system, and regulatory reports as fixed reports a view declares (R-05-67), sent on a schedule only once a scheduler is decided (I-183 (4)). Until then `docs/runtime/PLATFORM-PARITY.md` lists them as deferred.

## I-189. Capabilities of the original platform with no home yet

**Issue.** Listing the original platform's capabilities against the new runtime (`docs/runtime/PLATFORM-PARITY.md`) found some that no requirement, issue or decision covers:
1. **Customer and counterparty views.** The original's customer 360 (accounts, transaction history, KYC and a risk score) and counterparty 360 (exposure, sanctions history). The new runtime has the corporate register (R-05-62) and the payment list by party, but no view of one customer across payments and accounts.
2. **Fraud case handling.** The original listed fraud alerts and cases. The new runtime screens a payment through `p.port.fraud.v1` and holds or refuses it; there is no alert list or case.
3. **A person's workspace and notification centre.** The original let an operator arrange widgets (KPIs, a small queue, a notification feed) and read in-app notifications. The new console has fixed pages, the work queue and alerts through its notification adapter (I-182).
4. **The exchange-rate feed.** The original refreshed exchange rates on a schedule and flagged stale ones (`RefreshExchangeRates`, `EvaluateExchangeRateStaleness`). In the new model a bank's rates are a dial of `p.enrich.fx-rate.v1` (`rates`), set in a configuration release; nothing refreshes them or says they are stale.
5. **Integration controls.** The original's integration console had circuit breakers per connection and a Kubernetes portal. The new runtime has health probes per bound host, the requeue of a dead letter (R-09-91) and the drain and restart of a worker (I-128), but no circuit breaker and no orchestrator view.

**Recommendation.** Ask the owner, item by item, whether each is needed, deferred or dropped. For the exchange-rate feed, if it is needed: an `fx-rates` reference table or a rates port with a staleness limit (a dial with a default and bounds), so a payment refuses a rate older than the bank allows.

**Done for the circuit breakers of (5) (23 November 2026, owner's decision, R-09-155, R-09-156).** Every production adapter that calls an outside system calls it through a breaker per target: core banking and the bank's other gateways, SFTP, MQTT, Kafka and the webhooks. After `breakerFailures` failures in a row (default 5, 1 to 100) it stops calling for `breakerOpenSeconds` (default 30, 1 to 3600), then tries one call; each move is a row of `breaker_change`, and the service console's health shows an open breaker (`circuit-breakers`). The Kubernetes portal is dropped and the customer 360 and the workspace deferred (docs/OWNER-ACTIONS.md A2); the exchange-rate feed and the fraud alert list are still to build. What is left of the breakers: I-255 and I-259.

**Done for the exchange-rate feed of (4) and the fraud alerts of (2) (23 November 2026, owner's decision, R-09-177 to R-09-179, R-05-90, R-05-92, R-11-95, R-11-96).** (4) The rates port `p.port.fx-rates.v1` (factory 1.17.0, with its simulator) and its runtime adapters `simulator` and `gateway-rest`; a scheduled job of the service console's host, the rates feed, asks the bank's binding every `refreshMinutes` and publishes the rates as a signed configuration release of the bank (the table `b.ref.fx-rates.v1` and the value `p.enrich.fx-rate.v1#rates`, each rate with its `asOf`), put in force on the bank approver's standing consent to rate releases or else waiting for its own (I-275). `p.enrich.fx-rate.v1` 1.2.0 refuses a rate older than the bank's `maxRateAge` (default PT24H, from PT1H to P7D) as `no-rate` with `fx.RateStale`, on the studio interpreter and the native code alike, and the operations console says "rates stale" in its top bar. Each real bank binds the rates port by placeholder until it names its rates service (I-163). (2) A suspect payment is an alert in the work queue, with its reason and a link to the payment; staff take, escalate, note and close it, and release or refuse its payment by the payment's own action with the view's rules. Left: I-276 to I-280.

## I-190. Annex C and Part 9 still count 55 conformance scenarios

**Issue.** `spec/design/annex-c-conformance-kit.md` says `conformance/kit.yaml` "holds 55 scenarios in draft 0.2", and `spec/design/09-export-bindings.md` (section on Elsa) says 55 of 55 pass on Elsa. The kit holds 57 since CK-PARK-03 (R-09-91): `npm run studio -- kit` reports 57 of 57 on the studio interpreter, and `studio/test/execution.test.ts` expects 57. The engines' black-box test (`studio/test/blackbox/engines.test.ts`) still expects "56 of 56" on DTFx, Elsa and Flowable, so it fails once the kit runs all 57.

**Recommendation.** State the count in one place only, the kit file, and have Annex C and Part 9 say "every scenario of `conformance/kit.yaml`" instead of a number, in the next change to the spec; have `engines.test.ts` read the count from the kit, as the white-box test should.

**Done (23 November 2026, R-09-118).** Annex C and Part 9 name no number: the kit is the one place that counts its scenarios, and lint L51 (mutation case M81) refuses a design text that counts them otherwise. Annex C's groups now name CK-FOREACH-03 and 04, CK-TIMEOUT-03 and CK-PARK-03, which it had left out. `engines.test.ts` reads the count from the kit (commit 23c773d).

## I-191. The bank's cut-off: where it is applied, what moves, and a file of a day that is no business day

**Issue.** A bank's cut-off times are a reference table of its own (`b.ref.cut-offs.v1`, R-11-52): per scheme and direction a time, a time zone, and what follows it (`next-business-day` or `refuse`). Nothing read it (I-186). The spec says what a cut-off is and what follows it, but not (1) where in a payment it is applied, (2) what "the next business day" moves, (3) what a file accepted on a day that is no business day is, (4) whether a half day of the bank's calendar moves the cut-off, (5) which instant counts as the file's acceptance, or (6) the reason a refusal gives. The source holds no cut-off of its own (its WPS forks have none), so it cannot answer either.

**Recommendation, built and marked as the recommendation (R-12-28).** (1) A platform step, `p.decide.cut-off.v1`, called where the business date is set: in `uae.wps.wf.salary-file-out.v1` 1.4.0 right after `uae.p.enrich.business-date.v1`, so the same rule runs on the studio interpreter and every engine (each runs it on its workers, as any script-behind); the inward legs and the other platform workflows call it once a bank names an inward cut-off or a scheme other than WPS. (2) What moves is the business date (`header.businessDate`), which the posting reads as its value date; the file is not held until that day. (3) A file accepted on a day that is no business day counts for the next business day and is never refused for it: the cut-off is a time of a business day. (4) On the bank's half day the cut-off is the earlier of the two times. (5) The front door's acceptance, `header.receivedAt`, in the row's time zone; the edition of each table is the one in force on the day of the step, which on every engine is the day of acceptance, since the step runs at once. (6) TM01 (ISO 20022: received after the cut-off). The values reach the step as the bank's lists read from its reference tables (`fromReference`, R-11-51); a bank's configuration names them (the example bank's does). Every one of these is the builder's recommendation, not a scheme rule: the activity's provenance says so. Decide with the banks; a change is a new release of the activity.

**Owner's decision (23 November 2026):** accepted as built.

## I-192. A test distribution's outward WPS file goes to the simulator, not out as a file

**Issue.** The owner asked that the laptop's outbound files go out through the File API's outbox. On the laptop's test distribution the WPS port (`uae.wps.port.wps.v1`) is bound to its simulator, so the salary file the outward leg submits is a message to the simulator, not a file on `p.port.file-transfer.v1`; and no runtime adapter of the WPS port writes the file to the Central Bank's folder. The only files a runtime host puts on the file-transfer port today are the native host's inward renders (PRC, DIF, RFA) and core banking's `file-bulk` postings, and ADIB's outward path on the laptop has neither.

**Recommendation.** Give the WPS port a `file-transfer` adapter in the runtime (the SIF to `centralBank.sifFolder`, the ACK and NAK read from `centralBank.ackFolder`, correlated as I-160 (2) says), so that a bank's outward file leaves through the File API's outbox. Until then the laptop test shows the outbox the way every host uses it: a file posted to `POST /outbox` with the front door's client is delivered on the laptop's SFTP binding, and the consoles' client resends the same bytes through `POST /files/{id}/resend`, once per action (R-09-105). Every host that could put a file names the File API (`TH_FILE_API_URL`).

**Done (23 November 2026, owner's decision A7, R-09-160 to R-09-162).** The File API is a port that sends data out: a port may be bound to adapter `file-transfer` (`runtime/src/TransactHub.Ports/PortFileTransfer.cs`), whose send puts the file on the bank's binding of `p.port.file-transfer.v1`, the File API's outbox on a runtime host, at the folder the port's binding names (`<operation>.folder`, here `centralBank.sifFolder`), kept as the payment's file (`<operation>.kind`), delivered, audited and resent there. The image answers it for a native module (`NativePorts`). The salary file is rendered by the source's writer (`uae.wps.map.sif-out.v1` 1.1.0, native) and sent (`uae.wps.exchange.send-salary-file.v1` 1.1.0, native) on the bank's binding of the WPS port. The ACK or NAK the Central Bank leaves in its inbox comes back through the File API: the sent file's stem is recorded as the payment's key, the inbox's `stemDrop` strips the Central Bank's file id (I-160 (2), Issue I-267), and the worker reads the file the File API kept (Issue I-265). ADIB's laptop configuration release binds the WPS port so (`deploy/laptop/banks/adib/file-transfer.yaml`); the laptop's bench gives the port to the runtime and its workers give the render, the send and the ACK's reading to the native host. A test distribution keeps the port's simulator, and any other bench may name it. Factory 1.18.0, change set CS-2026-11-23-013 (1.16.0 and CS-2026-11-23-011 were held for it; merged after 1.17.0 and CS-2026-11-23-012, it is released as 1.18.0 under the next change set). Tests: `PortFileTransferTests`, `FileApiTests.An_answer_named_after_the_file_a_payment_sent_reaches_that_payment`, and `deploy/laptop/test/insert-only.mts` step 7 (each payment's SIF on the SFTP server through the outbox, the test answering as the Central Bank with an ACK and one NAK, P1's file resent). What remains: Issues I-266 (the encrypted file) and I-268 (the scheme's other sends).

## I-193. The laptop's workers, and the tokens its hosts give the File API

**Issue.** The File API's front door runs the intake map on a worker of the payment's distribution, and the engine host's plans name the workers of each queue; the laptop ran no worker services (I-158 (3)), so its front door was off for intake and its engine host had no workers. A host that puts a file through the File API, and the consoles' resend, take a bearer token by reference, which a laptop's development identity provider issues for an hour only.

**Recommendation and what was chosen (R-09-105).** (1) One `workers` service on the laptop: on a distribution's first call it fetches the package from the release API with the front door's client, verifies it against the trusted signers, and starts that distribution's own workers, one process per queue, so a payment pinned to A is never served by B's; an attempt goes to the worker whose tasks name its activity and release. The engine host names it by a template (`TH_HOST_WORKERS_URL`, `{distribution}`, `{workflow}`, `{queue}`), the File API by `FileApi:Workers:<scope>` with `{distribution}`. At a bank the workers are the installation's (one deployment per distribution and queue, R-09-04). (2) A `machine-auth` service writes fresh client-credentials tokens to a volume every ten minutes, and the hosts read them by `file:` reference. At a bank the hosts take tokens from the bank's own identity provider; give `FileApiTransferPort` and `OutboxFileTransfer` a client-credentials source of their own so that no file of tokens is needed.

**Found on the way, and what remains.** (3) A plan built from the stored package (R-09-94) read its YAML documents with every scalar a string, so the first step with a retry policy failed on the engine host ("Nullable object must have a value"); `Plans.Yaml` now types the scalars as YAML's core schema does. (4) A distribution carries no values, so a payment on the engine host reads the bank's values from its installed configuration release: the laptop's workers call the release API's `GET /values` at each attempt's time (`TH_RUNTIME_URL`), and the insert-only test installs ADIB's configuration release with consent before it activates A. (5) The engine host's plan names no hook bindings (they are the bank's configuration set's, not the distribution's), so a hook the bank binds (ADIB's labour ministry notice on close) does not run on the engine host. Recommendation: have the supervisor read the bindings in force from the installed configuration release, as the workers read the values, with a test that a bound hook runs on each engine.

**Done for the engine host's own values (23 November 2026, R-09-135; the north-star review of 23 November 2026).** (4) covered the workers only: the engine host's plan held no values, so a wait or an await's timeout that the bank sets (the WPS acknowledgement wait) ran on the factory's default. A plan built from the stored package now says `liveValues`, and the engines read each value the step needs from the configuration releases in force in the store, as of the step's time or, for a parameter its owner reads as of the payment's date, as of the payment's acceptance (its pin). Each read is a call of the host's claim check (op `values`), recorded by Temporal, DTFx and Elsa as an activity is, so a run replays what it read; a later configuration release reaches the later steps of a running payment with no restart. On Flowable the binding reads the value once per timer and journals it. Each payment's Temporal run now starts at its own time, not at the evaluation date of the host's start. `EngineHostTests` (`Temporal_`, `Dtfx_` and `Elsa_reads_the_banks_values_as_of_each_step`). (5) stays open; so does Issue I-166.

## I-194. The studio read a test's releases in text order: 1.9.0 after 1.10.0

**Issue.** A factory test carries no release of its own; each pack release publishes it again as `tests/<id>@<pack release>.yaml`, and the store keeps the one it reads last. The store read the branch in text order, so at the factory's 1.10.0 it read `@1.9.0` after `@1.10.0` and kept the older test; the same held for a bank's `packs/` and `configuration-sets/` files.

**Done.** The store reads the branch in release order (`naturalPath` in `studio/src/core/store.ts`: every run of digits compared as a number), so the later release is read last. Found while releasing the factory at 1.10.0; shown by `core.test.ts` ("matches the hashes the factory pack manifests carry").

## I-195. The salary file's intake map reads the file's name from the file, not from the File API

**Issue.** `uae.wps.map.sif-in.v1` takes the file's name from a first line `# file: <name>`, the samples' metadata, and its edge rule `uae.wps.rule.file-name-matches-control.v1` refuses a file whose name does not start with the employer's id. A file a bank's channel drops on the SFTP server or posts to `POST /files` has no such line: the File API's front door hands the map the file's name beside its content (the message's `fileName`), which the map does not read, so the map answers `unmappable` and nothing starts. Found by the laptop test (R-09-105), which keeps the samples' first line in its files until this is fixed.

**Recommendation.** A minor release of the map (its script at `scripts/uae.wps.map.sif-in.v1@<release>.ts`) that takes the name from the inbound message's `fileName` when the file carries no `# file:` line, with a test that maps a file without it; then the laptop test drops the line.

**Done (23 November 2026, R-05-85).** `uae.wps.map.sif-in.v1` 1.4.0 (`studio/scripts/uae.wps.map.sif-in.v1@1.4.0.ts`) takes the file's name from the inbound message's `fileName` when the file carries no `# file:` line; the interpreter hands a map the message's `fileName`, and a test may give it (`given.fileName`, `test-case.schema.json`). Tests: WPS-MAP-006 (a file with no line maps by the File API's name), WPS-MAP-007 (with neither it is unmappable, as before), and `studio/test/sif-file-name.test.ts` (the worker's attempt as the front door asks it; release 1.3.0, pinned in flight, still reads the line only). The laptop test's files carry no `# file:` line (sample `wps-sif-no-file-line.sif`). Factory 1.18.0.

## I-200. On the laptop stack the File API keeps a file but cannot start its payment

**Issue.** Recording the runtime videos (`studio/demo/full-run.ts`) on the laptop stack, a salary file and a demo credit file uploaded to the File API (`POST /files/ui`, as the operator) were checked and kept, and then refused with `frontdoor.NoWorker`: "No worker of dist/adib@… serves uae.wps for uae.wps.map.sif-in.v1". The front door runs a scheme's intake map on a worker of the distribution (`FileApi:Workers:<scope>`, R-09-85), and `deploy/laptop/compose.yaml` names none, so no file that reaches the laptop's File API starts a payment. The laptop's own test runs its payments with the front door's one-off runner (`deploy/laptop/bin/run-pinned.mts`) for this reason, and its README lists it under "What is not here", but nothing says that an upload is refused.

**What the video does.** It shows the upload and the refusal, then pins the payment at the release API and runs it with `run-pinned`, as an operator would, and says so.

**Recommendation.** Give the laptop's File API the workers of the bank's distributions (`FileApi__Workers__*`), served by the engine host's per-distribution host or by a worker service of the runtime image, and extend `deploy/laptop/test/insert-only.mts` to upload a file and see its payment start and end on the distribution it is pinned to.

**Done (23 November 2026, R-09-163).** The laptop's File API names the `workers` service for each scope's intake maps (`FileApi__Workers__*`, by the distribution the payment is pinned to), and the engine host's plans name the same workers. `deploy/laptop/test/insert-only.mts` starts P1 and P2 by the SFTP pick-up folder, P3 by `POST /files` and P4 by `POST /files/ui` as an operator (`ops.operator`, multipart), each run to its end on the engine host.

## I-201. No console has a screen to upload a file to the File API

**Issue.** The File API takes a person's upload (`POST /files/ui`, role `file-uploader`, which the laptop's role map gives the operators), but neither the operations console nor the service console has a page that sends one. An operator who must put a file in by hand needs a command.

**Recommendation.** An upload page in the operations console: tenant (the runtime's bank), message (the messages the active distribution receives), the file; it shows the File API's answer (kept, refused, started) with its code linked to the runbook and a link to the payment it started.

**Done (23 November 2026, R-05-73, R-05-74).** The operations console has an Upload page. It offers the runtime's bank (`TH_BANK`, else each tenant the person operates) and the messages the distribution active in the console's environment (`Operations:Environment`) receives: each scheme manifest's latest release in the stored package, `direction: in` with a `start` or a `deliver` disposition (`UploadCatalogue`, `GET /ops/api/upload`). `POST /ops/api/upload` refuses a message the distribution does not receive and a tenant the person does not operate, then posts the file to the File API's `POST /files/ui` (`Operations:FileApi:Url`) with the person's own token, so the File API decides by its role `file-uploader`; the console keeps nothing of the file. The page shows kept, refused or started, a refusal's code linked to its runbook in the service console (`Operations:ServiceConsole`) and the payment a start began. The laptop's `ops-api` sets both. Tests: `UploadAndAuditTests` (the File API a stand-in), `runtime/ops-console/test/unit/upload-audit.test.ts`, `runtime/ops-console/test/e2e/upload-audit.test.ts`. Not built: in dev sign-in (`TH_AUTH_DEV`) the console has no bearer token to pass on, and the File API has no dev sign-in, so an upload from a dev-mode console is refused by the File API (I-246).

## I-202. The consoles' own audit trails have no screen

**Issue.** The operations console keeps its own hash-chained record of what people did (`GET /ops/api/audit`), and the service console its audit (`GET /service/api/audit`), but no page of either shows them. A payment's timeline shows that payment's facts; the console's own trail (filters saved, reports downloaded, unmasks asked, consents given) can be read only from the API. `docs/runtime/PLATFORM-PARITY.md` lists the audit log as built.

**Recommendation.** An Audit page in each console, for the Supervisor and the BankApprover in the operations console and for the service roles in the service console: newest first, filtered by person, action and date, each row with its hash and whether the chain verifies.

**Done (23 November 2026, R-05-75, R-05-76).** Each console has an Audit page (`runtime/ops-console/src/shell/AuditTrail.tsx`, shared). The operations console's, for a Supervisor or a BankApprover, reads `GET /ops/api/audit`, which now also gives `rows`: every fact of the console's chain, newest first, filtered by `person` (the actor or the name the fact records), `action` (its kind, its code or the system action it names) and `from`/`to` (days, UTC), each with its hash and the hash it links to, and `chain` (whether it verifies, and where it breaks). The service console's, for its service roles, reads `GET /service/api/audit` with the same filters, each row with its hash, and `GET /service/api/audit/chain`: whether every chain its facts are on (the console's and each release's) verifies, naming any that does not. The filter is one record in the store (`AuditFilter`). Tests: `UploadAndAuditTests` (both databases), `ServiceAuditTests`, the unit tests, and the e2e tests `upload-audit.test.ts` and `service-console.test.ts`.

## I-203. The laptop's identity provider has no register maker

**Issue.** `deploy/laptop/identity/directory.json` has five synthetic users, and no role map gives `RegisterMaker`, so on the laptop the operations console's Registers section can be read but nobody can propose a register change, and the maker-checker of a register cannot be shown.

**Recommendation.** Add a synthetic register maker (`reg.maker`, group `th-register-makers`) to the directory and `RegisterMaker` for that group to `role-map-ops.json`, with the bank approver consenting as today.

**Done (23 November 2026).** `deploy/laptop/identity/directory.json` has the synthetic `reg.maker` (Example register maker, group `th-register-makers`), and `role-map-ops.json` gives that group `RegisterMaker` and nothing else; the bank approver consents as before. `runtime/ops-console/test/unit/upload-audit.test.ts` holds both files to it; the laptop's README names the user.

## I-204. The new-scheme tests name configuration releases of an earlier factory

**Issue.** `studio/test/blackbox/e2e-new-scheme.test.ts` and `ui-build.test.ts` build the synthetic scheme `uae.demo` and then save the example bank's configuration set as release `1.3.0` (and expect `config:example-bank@1.2.0` in the web app). The example bank's shipped set is at `1.8.0`, so the save is refused (a release is later than the last, R-11-35) and every step after it fails: the payment is rejected at the route, the export names `1.8.0`. Before that, both stopped earlier still: the demo scheme sent its file with no protect step, which L46 refuses; that is fixed (the subflow encrypts first). `demo/build.ts` already derives the next release from the shipped one and its dry run passes.

**Recommendation.** Derive the next release from the set the studio returns, as `demo/build.ts` and `e2e-wps.test.ts` do (I-187), in both tests, and match the saved release by pattern in the web app's step.

**Done (23 November 2026).** Both tests take the next minor of the set the studio returns (`nextMinor` in `studio/test/blackbox/demo-scheme.ts`), the web app's step matches the saved release by pattern, and the export and the deploy expect the release the test made. Since R-11-77, a configuration that accepts `uae.demo` is refused until the bank licenses it, so both tests now license it first (I-205): `e2e-new-scheme.test.ts` through the API (16 of 16 pass), `ui-build.test.ts` in the bank workspace (12 of 12 pass); `demo/build.ts` and `demo/full-run.ts` do the same (`licenseScheme` in `demo/record.ts`).

## I-205. A scheme built in the studio cannot be licensed to a bank from the studio

**Issue.** A distribution carries the schemes the bank pack's lock file licenses (`licensedSchemes`, R-11-37). The lock file is written by `npm run studio -- bank-pack <bank> --licensed <scheme> --write`, which writes this repository's folder, not the studio's content branch; no screen or API of the running studio changes it. So a scheme built in the studio (the synthetic `uae.demo` in `studio/demo/full-run.ts`) can be accepted in a bank's configuration set, enabled as a rail and tested on the studio's bench for the bank, yet the bank's next distribution does not carry it, and the runtime finds no route for its file ("No start message in the package's dispatch tables leads to p.wf.corporate.v1").

**What the video does.** It builds and tests the new leg in the studio, then sends a WPS file through the runtime, and says why.

**Recommendation.** Let the Bank Configurator propose a new release of the bank pack's lock file that licenses a further scheme, as a change set the Bank Approver approves (R-11-35), which builds the bank's next distribution with it; and check at approval that a scheme a configuration set accepts is one the bank pack licenses.

**Owner's decision (23 November 2026):** build it as recommended.

**Done (23 November 2026, R-11-75 to R-11-78).** The bank workspace has "Schemes licensed" (`studio/web/src/views/Licensing.tsx`): every scheme of the factory, whether the bank licenses it and whether its configuration binds it. A Bank Configurator proposes a licence (`POST /api/tenants/<bank>/licensing`): the studio writes the bank's next lock file as `bank-pack` does (`packManifest`), at the next minor, with the scheme added, and saves it as a draft of kind `pack` (now a kind of the permission table, scope `b`: the configurator authors, the approver approves). A Bank Approver who is not its maker approves it (the page's button, or `POST /api/publish`): the approval refuses a lock file that is not the one the studio writes for the bank at that moment, publishes it insert-only at `banks/<bank>/artefacts/b/pack/<release>.yaml` (the seeded `pack.yaml` stays as it was), and builds the bank's next distribution, which carries the scheme. The lock file in force is the bank's latest release (`bankPackOf`, `lockFileOf`). At every approval of a bank's change set, each scheme its configuration binds (accepted, enabled as a rail, dialled, or hooked) must be licensed, with the change set's lock file and set in place. Tests: `studio/test/licensing.test.ts` (10), `e2e-new-scheme.test.ts`, `ui-build.test.ts` step 8. What a later seed does with a studio-published lock file is I-245.

## I-206. A payment pinned but never started keeps its distribution open

**Issue.** The front door pins a payment to the active distribution (`POST /payments/pin`) before its intake runs. When the run then fails before the payment is stored (no route, no worker, I-200), the pin stays without an end, the service console counts it as an open payment of the distribution, and the distribution can never be removed after it drains. Nothing in the consoles lists such pins or closes one.

**Recommendation.** Pin and store the payment in one step after the intake has mapped it, or record an end fact (`abandoned`, with its reason) when the front door's start fails; list pins with no payment in the service console with a troubleshooting action, with the bank approver's consent, to close one.

**Owner's decision (23 November 2026):** build it as recommended.

**Done (23 November 2026, R-09-164, R-09-165).** Of the two, recording the end was the cleaner: the front door pins before the intake because the map runs on the pinned distribution's workers. A start that fails after the pin (the map refuses it, no worker or route, the engine host refuses it) ends the payment `abandoned`, with the failure's code and words (`DistributionRegistry.Abandon`, `RuntimeFrontDoor.Start`). The service console lists the pins with no payment (no end, no payload an engine's run created, pinned at least `Service:StuckPinAfter` ago, default PT15M) at `GET /service/api/content/stuck-pins`, and its troubleshooting action `end-pin` ends one `abandoned` with its reason on a bank approver's consent; a pin whose payment runs is refused. Tests: `StuckPinTests` (SQL Server and PostgreSQL), `FileApiTests` (a map that refuses, an engine host that refuses: the distribution then drains and is removed), `DistributionContentTests.A_pin_with_no_payment_is_listed_ended_with_consent_and_its_distribution_drains_and_is_removed`.

## I-210. A bank's values had a way into the runtime with no signature, no bounds and no consent

**Issue.** The north-star review of 23 November 2026 found that `POST /import-branch` loaded every configuration set under a branch's `banks/` folder into the store as values in force: no signature, no check against any distribution's parameter contract, no consent. The spec asked for values to arrive in signed configuration releases checked against the parameter contracts (R-09-51, PACKAGING section 7), but did not say whether a test environment might keep a shorter path.

**Recommendation and what was chosen.** No folder brings values, in any environment. `POST /import-branch` takes workflow packages and activations only; `BankValues.Import` (the folder reader) is gone. A test environment uses the same signed configuration release as any other, checked at install against the parameter contract of every distribution not yet removed; what differs there is only who may put it in force (I-211). So the black-box test of a hot update (`e2e-p6-hot-update.test.ts`) now installs the bank's distribution and its configuration releases from GitHub, as a bank's runtime does (R-09-110).

**Done (23 November 2026).** Built as the recommendation says (R-09-110).

## I-211. Configuration releases from the release API went into force without consent

**Issue.** `POST /configuration-releases` and `POST /sources/install` put a configuration release's values, bindings, calendars and tables in force at once, while the release API's machine caller was held to `Releases:MachineActivationEnvironments` only for activate, drain, remove and rollback (R-09-78). The spec does not say in which environment a configuration release is: its values are the bank's, not an environment's.

**Recommendation and what was chosen.** Installing and putting in force are two steps (R-09-111). Install verifies, checks the bounds and stores; putting in force publishes the values once, with a `configuration.in-force` fact naming the consent. The release API's pipeline puts a release in force by itself only when the runtime's own environment (`Releases:Environment`, default production) is one of `Releases:MachineActivationEnvironments`; elsewhere it answers 202 and asks a bank approver's consent in the service console's book (`put-in-force`, the same `action.requested` fact a service engineer's request is, `ConsentRequests`). The service console's install of a configuration release is itself consented, so it puts it in force at once. At the consent the signature and every distribution's bounds are checked again, so a distribution installed meanwhile with a narrower bound refuses it.

**Still open.** A distribution installed through the release API (`POST /distributions`, `/sources/install`) is stored without consent, as before: it runs nothing until it is activated, and activation waits for consent outside the test environments, but R-09-77 counts installing as a change of the system. Recommendation: hold it to the same rule (stored, its host not started, until consented) in the next change to the engine host's supervisor, which starts a host for every installed distribution.

**Done (23 November 2026).** Built as the recommendation says (R-09-111). Distributions installed through the release API still go without consent; activation waits for it.

## I-212. Per-scope workflow packages beside distributions: refused, not yet retired

**Issue.** The release API (`POST /releases`, `/activations`, `/import-branch`, `/sources/install`) and the service console still installed and activated the earlier per-scope workflow packages beside a bank's distributions, so code could run that is not part of the distribution a payment is pinned to.

**Recommendation and what was chosen.** Refuse them where the runtime's bank has a distribution installed (R-09-114): stored and activated by neither the release API nor the service console, listed as not installable, `package.KindNotInstallable`. They are not retired yet, because the studio's deploy runner, the test bench and several black-box tests still run them in runtimes with no distribution. Recommendation: retire them once those run from distributions; the packages stored stay as the record.

**Done (23 November 2026).** Built as the recommendation says (R-09-114); retiring the packages waits for the deploy runner and the test bench.

## I-213. Port bindings read once from a folder

**Issue.** The File API, the native host, the operations console's enquiries and the service console read the banks' port bindings from `TH_BANKS_DIR` (or `Operations:Banks`, `Service:Banks`) once at start, so a binding a configuration release brought (PACKAGING scenario 2) was not used, and a folder of every bank's bindings, signed by nobody, was in force.

**Recommendation and what was chosen.** Every host of the runtime reads its bank's bindings from the configuration releases in force in the store (`ConfigurationBindings`, `PortBindings.Live`, refreshed at most every two seconds), as of each attempt's time; the latest configuration release that binds a port is the bank's whole set for it (R-09-112). A host of the runtime given a folder does not start (R-09-113); only a native host that serves no runtime's bank (the studio's test runs, with no `TH_BANK`) still reads `TH_BANKS_DIR`. The laptop stack's `compose.yaml` names no folder.

**Still open.** (1) The laptop stack has no configuration release in force until one is installed: until then its native host and File API have no binding. The insert-only test does not install one yet; the laptop's own bindings (the `directory` file transfer of its `files` volume) belong in the bank's configuration release built for the laptop. Recommendation: the laptop work installs ADIB's configuration release through the service console, with consent, after its first distribution. (2) The service console's health probes of bound hosts are built when it starts; a new binding's host is probed from the next start. (3) When the store cannot be read, a native activity is refused as `PortNotBound`, not retried. Recommendation: make that refusal transient.

**Done (23 November 2026).** Built as the recommendation says (R-09-112, R-09-113).

## I-214. A backup took every bank folder's bindings

**Issue.** The service console's backup took the bindings of every folder under `Service:Banks`, while a restore refuses a backup that holds another bank's content (R-09-76), so a laptop's backup (its folder holds five banks) could not be restored into its own runtime.

**Recommendation and what was chosen.** A backup holds the runtime's own bank only (R-09-116): its releases and shared workflow packages, their activations, its values, and the bindings of its configuration releases in force, from the store. A restore puts the bindings back as values of the store and writes no bindings folder. `BackupTests.A_laptop_backup_holds_only_its_own_bank_and_restores_into_its_own_runtime` restores a laptop's backup into its own (test) runtime.

**Done (23 November 2026).** Built as the recommendation says (R-09-116).

## I-220. A native module still reaches into the image beyond the contract

**Issue.** R-09-123 puts one versioned assembly, `TransactHub.Activities.Contract`, between the runtime image and a distribution's native assemblies. The WPS module needs more than that assembly holds: it derives from the platform's `CoreBankingActivity`, builds `BoundAdapters` over `PortBindings`, gives the platform's party register a `PartyMatcher`, and reads the source's rules over `TransactHub.Model` and `TransactHub.Store` types. Those assemblies stay in the image and are shared with the module by name. A change to one of their public types that the module uses breaks a distribution that the contract version says is supported.

**Recommendation.** Until the module needs the contract only, treat a change to a public type of `TransactHub.Activities.Platform`, `TransactHub.Ports`, `TransactHub.Model`, `TransactHub.Store` or `TransactHub.Security` that a module uses as a new major of the native contract. Then narrow the surface: move what a scheme module needs (the posting base, the bound adapters, the party match) behind interfaces of the contract assembly, so that a module references the contract and nothing else.

**Done (23 November 2026, R-09-125).** The native contract is 1.1.0, a minor: `NativeContract.Supports` still takes the runtime's major at a minor no later than its own, so a module built against 1.0.0 runs on it. `INativeServices` now gives what a module truly needs of the image: `CoreBanking` (`INativeCoreBanking`, the binding in force with its posting window, the account and branch rules, the posting with its reply classified), `Ports` (`INativePorts`: the party register, encryption, decryption and the labour ministry's register, each by the image's adapter), `Files` (`INativeFiles`, moved into the contract) and `Contribution` (another module's contribution; `PartyMatch`, `PartyMatcher` and `NativeParty` moved into the contract). The WPS module references the contract and nothing else of TransactHub: `PostReturn` posts through `INativeCoreBanking`, the employer status asks through `INativePorts`, the corporate match is a contract `PartyMatcher`, the NAK's lines are its own record, and what reads and writes the store for WPS (`WpsRuleValues`, `CentralBankSif`) moved to `TransactHub.Activities.Wps.Store`, which no distribution carries. The image's side is `NativeHostServices`, `NativeCoreBanking`, `NativePorts` and `FileProtectionPort` in `TransactHub.Activities.Platform`. The studio reads each module assembly's references from its metadata (`studio/src/export/assembly-refs.ts`) and refuses one that references another assembly of TransactHub; the runtime refuses such a distribution at install and at load (`NativeAssemblies.References`), both with the code `package.NativeImageReference`, in the platform's error dictionary 1.3.0 (factory 1.12.0, change set CS-2026-11-23-007). Tests: `NativeInstallTests` and `SideBySideTests.A_distribution_whose_module_references_the_image_beyond_the_contract_is_not_loaded` (an emitted assembly that references `TransactHub.Ports` is refused; the WPS and platform builds pass), `studio/test/native-modules.test.ts`.

## I-221. The platform's native activities stay in the runtime image

**Issue.** By the owner's direction of 23 November 2026, a distribution carries the scheme and bank native activities, and the platform's own (`p.post.accounting.v2`, `p.enrich.fx-rate.v1`, `p.enrich.charges.v1`, `p.enrich.party-register.v1`, the file protection and the platform's production adapters) stay in the runtime image as runtime software (R-09-120). A fix to one of them is therefore a runtime software upgrade, not a platform release and a new distribution, and two distributions side by side share one build of them. Section 8 of `docs/runtime/PACKAGING-AND-DEPLOYMENT.md` says scenario 5 (a platform release) changes the code that runs; for these activities it does not.

**Recommendation.** Decide whether the platform pack's native activities become a module of their own, carried like a scheme's, with only the ports, the store and the adapters left in the image. Until then the packaging document says that a platform native activity changes with the runtime image.

**Done (23 November 2026, R-09-126).** They are a module, scope `p`, `runtime/src/TransactHub.Activities.Platform.Native`, carried in each distribution under `activities/native/p/` as WPS's is: every platform activity with `implementation.native: true` (`p.enrich.fx-rate.v1`, `p.enrich.charges.v1`, `p.decide.posting-window.v1`, `p.post.accounting.v2`, `p.enrich.party-register.v1`, `p.protect.encrypt-file.v1`, `p.protect.decrypt-file.v1`). `p.decide.posting-window.v1`, which the WPS module had served, is the platform module's. The native host's image holds none of them: only the builtin services the runtime gives a port (`p.post.accounting.v1`, the reservation, the reversal and the verification, the holds) and the production adapters (R-09-90) stay, and the File API decrypts through the image's `FileProtectionPort`. `NativeAssemblies.Shared` (what the image keeps) is documented as such; `deploy/laptop/Dockerfile` and `build.sh --local` build the platform module beside WPS's for the studio image; the packaging document (sections 2, 3, 3.1, 6, 8 scenario 5) says a platform native activity changes with a platform release. Shown by `SideBySideTests.Each_distribution_runs_its_own_build_of_a_platform_native_activity_side_by_side`: two distributions with two builds of `p.decide.posting-window.v1` answer each with its own build in one native host, and `e2e-native-distributions.test.ts` carries the platform module in each distribution. A black-box test that runs native activities outside a distribution gives the host both modules as development modules (`devModules()`).

## I-222. Only the worker says which distribution an attempt is for

**Issue.** The native host serves an attempt from the distribution the attempt names (R-09-122). The worker names it from `TH_DISTRIBUTION`, which the deploy runner sets for the workers it starts for a pinned payment. An engine's attempt request (Temporal's `Host.Attempt`, the DTFx and Elsa harnesses, the Flowable binding) does not carry the distribution, so a worker started without `TH_DISTRIBUTION` sends none, and the host then serves the attempt from its development modules, or refuses it. The long-running engine host takes its workers from the installation (`TH_WORKER_URL`), and does not say which distribution each serves.

**Recommendation.** Put the distribution in every attempt request the engines send, from the payment's pin, and have the worker forward it to the native host before its own `TH_DISTRIBUTION`. Until then an installation runs one worker set per distribution, each with its `TH_DISTRIBUTION`, as the deploy runner does.

**Done (23 November 2026, R-09-130 to R-09-133).** Every attempt an engine sends to a worker names its payment's distribution: the workers' router of the four engines (`WorkerRouter`, `studio/engines/shared/Deployment.cs`) adds the engine's `distribution` to each attempt, on Temporal (the deploy runner's runs and the long-running host's version workers), DTFx, Elsa and Flowable (and the studio's Flowable harness), and the File API's front door names the payment's pin for its intake map. The worker hands it to the native host before its own `TH_DISTRIBUTION`, which it sends beside it as `workerDistribution`. The native host serves the attempt from the distribution it names (`AttemptRouting`, `runtime/src/TransactHub.Native`), and refuses it when the worker runs another distribution (`WrongDistribution`) or when the payment is pinned to another (`NotPinned`). An attempt that names none is served as before. One worker set that names no distribution now serves every distribution whose tasks it registers: the engine host's installation workers (`TH_HOST_WORKERS`) needed nothing more. The laptop's `workers` service keeps a set per distribution, started from the distribution's own copy with its `TH_DISTRIBUTION` (R-09-105), because its tasks and scripts are that distribution's (Issue I-232); its attempts now also name the distribution, which the native host checks against the worker's. The deploy runner takes an installation's workers (`DeployRequest.workers`, `startWorkers`), and `e2e-native-distributions` runs two builds of the WPS ACK mapping through one worker set on Temporal and DTFx. A set still serves only the tasks it registers (Issue I-232); the refusals' codes have no dictionary entry yet (Issue I-230).

## I-223. A module built ahead of time is trusted as the studio is

**Issue.** The studio builds a distribution's native assemblies from the code-behind project at publish (`TH_NATIVE_MODULES=build`, R-09-120). A studio with no .NET SDK, such as the laptop image, takes the same build made ahead of time (`TH_NATIVE_MODULES=<dir>`, written by `runtime/tools/native-module/build.sh` in the image's build stage). That folder is checked against its own SHA-256, but nothing signs it: it is trusted as the studio's own files are. The distribution's signature then covers whatever the folder held.

**Recommendation.** Have NEST's build sign each module build with its own key, and have the studio verify that signature against its trusted signers before it carries the module, so that a module reaches a distribution only from the code-behind's own build.

**Done (23 November 2026, R-09-127).** `runtime/tools/native-module/build.sh` signs the folder it writes as the studio signs a distribution: `release.json`, a `th-release-statement/1` of kind `native-module` for `native-module/<scope>` naming every file (the assembly, `native-module.json`) with its SHA-256 and the module's assemblies, contract and source hash; `release.sig`, ECDSA P-256 with SHA-256, DER, Base64. The key comes from the environment only (`TH_NATIVE_SIGNING_KEY=env:<NAME>`) and is piped to `openssl`, never written; `build.sh --signers` prints its trusted-signers entry. The source hash is now one `sha256sum` can give (the SHA-256 of what it lists for every .cs and .csproj file, in byte order), the same in the studio. The studio verifies a prebuilt folder (`TH_NATIVE_MODULES=<dir>`) against `TH_NATIVE_MODULE_SIGNERS` and refuses one unsigned, changed, signed for another scope or by a key it does not trust; `TH_NATIVE_MODULES_UNVERIFIED=test` or `dev` takes it outside production and records `signature: { unverified }` in the manifest. The studio's unit tests sign their synthetic modules with a key made for the run. Tests: `studio/test/native-modules.test.ts` (a good, a tampered, an unsigned, another scope's and an untrusted folder; the mode, and its refusal in production) and `studio/test/blackbox/native-module-signing.test.ts` (build.sh's own signature verified, tampered and unsigned builds refused).


## I-224. Distributions built before native contract 1.1.0 no longer load

**Issue.** A WPS module built before R-09-125 references `TransactHub.Activities.Platform`, `TransactHub.Ports`, `TransactHub.Model` and `TransactHub.Store`. The runtime now refuses such a distribution at install and at load (`package.NativeImageReference`), so a payment pinned to one cannot run its native activities on this runtime, though its contract version (1.0.0) is supported.

**Recommendation.** Rebuild and reinstall every distribution of a bank once its runtime takes this change, before it takes payments (no bank is in production yet). If a runtime must keep such a distribution, keep the earlier image for it until it drains; do not relax the check.


**Superseded (24 November 2026, R-09-262).** The owner decided to break the native contract once, while no bank is live: contract 2.0.0 refuses every module built against 1.x (`native.ContractUnsupported`), so no earlier distribution is kept either way. Every module is rebuilt against 2.0.0 and every distribution built again; the steps are `docs/runtime/PACKAGING-AND-DEPLOYMENT.md` section 3.2.

## I-225. The laptop's module builds are signed by a key their own image build makes

**Issue.** The laptop image (`deploy/laptop/Dockerfile`, `build.sh --local`) has no module builder's key, so its build step makes a P-256 key, holds it in that step's environment, signs the platform and WPS modules with it and writes its public key to `/opt/th/native-module-signers.json`, which the laptop's studio trusts. That proves the folder was not changed after the image was built, not who built it.

**Recommendation.** For any image beyond a laptop, pass NEST's module signing key into the build as a BuildKit secret mounted as an environment variable (`RUN --mount=type=secret,id=...,env=TH_NATIVE_MODULE_KEY`), and give the studio NEST's trusted signers in `TH_NATIVE_MODULE_SIGNERS` at run time.

**Done (24 November 2026, R-09-181).** The Dockerfile's build stage takes NEST's module signing key as a BuildKit secret (`th_native_module_key`), mounted for the signing step only and read into that step's environment, with `--build-arg TH_MODULE_SIGNING=secret`; with that mode and no secret the build stops and makes no key. `TH_MODULE_SIGNING=laptop`, the default, keeps the self-made key for the laptop only. `TH_NATIVE_MODULE_KEY_FILE=<PEM file> deploy/laptop/build.sh` passes the key as the secret (with `--local`, into the signing step's environment only). Documented in `deploy/laptop/README.md` and `docs/runtime/PACKAGING-AND-DEPLOYMENT.md` (section 3.1); `LaptopDeploymentTests.A_production_image_build_takes_the_module_signing_key_as_a_BuildKit_secret_and_never_makes_one`. No image was built here (disk); the test reads the files.

## I-226. `INativeServices.Service<T>` still hands out the image's types

**Issue.** Contract 1.0.0's `Service<T>()` gave a module the image's `PortBindings` and `CoreBankingResolver`. It stays, so that a module built against 1.0.0 keeps running, but a module that references the contract only has nothing of the image it can name with it.

**Recommendation.** Remove `Service<T>` at the contract's next major (2.0.0), with every other change that breaks a module, and let the runtime refuse 1.x modules then.


**Done (24 November 2026, R-09-262).** Native contract 2.0.0 removes `Service<T>` from `INativeServices`, from the image's `NativeHostServices` and from the native host's scoped services. A module that used it reaches core banking through `CoreBanking` and a port through `Ports` or `BoundPorts`. The runtime refuses 1.x modules at install and at load (`native.ContractUnsupported`), and the studio refuses a 1.x module build. `SideBySideTests.The_contract_is_versioned_and_the_runtime_takes_its_major_up_to_its_own_minor` (no `Service` method), `ContractTwoTests.A_module_built_against_contract_1_is_refused_with_its_own_code_and_a_rebuilt_one_runs`, `NativeInstallTests` (both databases), `studio/test/native-modules.test.ts`.

## I-227. Two small pieces of logic are written twice for the contract's sake

**Issue.** The posting window gate (`PostingWindow`) is in the image, where it checks a core-banking binding's posting hours, and in the platform module, for `p.decide.posting-window.v1`. The accounting template's reading is in the image (`AccountingTemplate`, for `p.post.accounting.v1`), in the platform module (`PostingTemplate`) and in WPS's `PostReturn`. A module may not reference the image, so each carries its own copy.

**Recommendation.** Move `p.post.accounting.v1` and the window gate's use by a binding into the platform module in a later platform release (the image then keeps no copy), or accept the copies and keep a test that each gives the same answers on the same cases.

**Done, the second option for now (24 November 2026, R-09-215).** The copies stay, held to each other: `DuplicatedLogicTests` runs the image's and the platform module's posting window gate on every pair of sixteen starts and ends (valid, blank, malformed, overnight) at 39 times of the day, and the image's and the module's reading of the accounting template on full, minimal and incomplete parameters, refusals included, and expects the same answers. The move (the recommendation's first option, which this batch was asked to prefer) needs the same thing I-237 needs, a module that can read a binding in force and call it through the image's guarded HTTP: the image's posting gate checks the core-banking binding's posting hours, which a module cannot read today. That is Issue I-315. WPS's `PostReturn` reads only five of the template's values by their names and is not a copy of the template's reading; it stays.


**Done, the first option (24 November 2026, R-09-264).** `p.post.accounting.v1` is the platform module's (`PostAccountingV1`), reading the accounting template with the one `PostingTemplate` (charge slabs and VAT per cent added), and the posting window gate is the platform module's only (`PostingWindow`, `PostingGate`). The module contributes its gate to its distribution as a `NativeBindingGate` under `binding-gate:p.port.core-banking.v1`; the image asks it before every core-banking call for that distribution (the reservation, the reversal, and every module's posting through `INativeCoreBanking`, WPS's `PostReturn` among them), and refuses a binding that names a posting window when no gate is contributed (`PortNotBound`), so nothing is called ungated. The image keeps no `PostingWindow`, `AccountingTemplate`, `ChargeSlab` or `PostAccounting`. `DuplicatedLogicTests` is removed and `NoCopyInTheImageTests` holds the image to having none. A promotable distribution that carries `p.post.accounting.v1` needs the platform module; the studio refuses one without it (`MODULE_BUILTINS`, Issue I-381).

## I-230. The native host's refusals have no entry in the error dictionary

**Issue.** The native host refuses an attempt with a name of its own: `NotNative`, `NotInDistribution`, `DistributionUnknown`, `DistributionRemoved`, `NativeModuleRefused` and, since R-09-131 and R-09-132, `WrongDistribution` and `NotPinned`. None has an entry in the platform's error dictionary (`p.errors.platform.v1`), so no runbook covers them, and `ErrorDictionaryTests` does not look for them: it reads only `NativeFailure`, `StepFailure` and `AttemptFailed` codes. The change that made the last two kept the factory unchanged, so it could not add them.

**Recommendation.** In the next release of `p.errors.platform.v1` (1.3.0), add an entry with a runbook for each refusal of the native host, under a `native.` prefix as the runtime's other codes have one (`native.WrongDistribution`, `native.NotPinned`, and the others), raise them with those codes, and have `ErrorDictionaryTests` read `NativeRefusal` codes as well.

**Done (23 November 2026, R-09-128).** The native host refuses with codes under a `native.` prefix: `native.NotNative`, `native.NotInDistribution`, `native.DistributionUnknown`, `native.DistributionRemoved`, `native.ModuleRefused` (was `NativeModuleRefused`), `native.WrongDistribution` and `native.NotPinned`; a module that references the image beyond the contract keeps its own code, `package.NativeImageReference` (R-09-125). Each has an entry with a runbook in `p.errors.platform.v1` 1.3.0 (factory 1.12.0, change set CS-2026-11-23-007), and `ErrorDictionaryTests.Every_code_a_native_activity_or_the_interpreter_raises_has_an_entry` reads every `NativeRefusal` code too, so a refusal added without an entry fails it. `SideBySideTests`, `AttemptRoutingTests` and `e2e-native-distributions.test.ts` expect the new codes.

## I-231. An attempt for a payment with no pin

**Issue.** The native host refuses an attempt for a distribution its payment is not pinned to (R-09-132). A payment with no `distribution.pinned` fact in the host's store is served from the distribution the attempt names: the studio's test runs and the native host's own tests attempt for payments that the store never accepted. On a runtime every payment is pinned at acceptance (R-09-53), before its intake map, so an attempt for an unpinned payment there is a fault, but the spec does not say so.

**Recommendation.** Have a runtime's native host (one with `TH_BANK`) refuse an attempt that names a distribution for a payment with no pin, as `NotPinned`, and keep the present rule for a host that serves no runtime's bank.

**Done (23 November 2026, R-09-143).** A runtime's native host (`TH_BANK`) refuses an attempt that names a distribution for a payment the store has no pin of, or that names no payment, as `native.NotPinned`, not retryable (`AttemptRouting.FromEnvironment`). Only an explicit mode, `TH_NATIVE_MODE` `dev` or `test`, serves it from the distribution it names; any other value of the setting stops the host at its start. A host that serves no runtime's bank (the studio's test runs) keeps the earlier rule. `AttemptRoutingTests.A_runtimes_native_host_refuses_an_attempt_for_a_payment_with_no_pin_unless_in_dev_or_test_mode`. The refusal is the dictionary's `native.NotPinned` (R-09-128).

## I-232. A worker set serves only the tasks it registers

**Issue.** One set of workers now serves every distribution of a bank (R-09-133), but a worker holds one package's tasks, by name and release, with their script-behinds, maps and rules. A distribution whose other tasks are at releases the set does not register is refused at the worker (`NotOnThisQueue`). Only a native activity's build is chosen per attempt.

**Recommendation.** Until a worker can load a distribution's tasks as the native host loads its assemblies, an installation starts a worker set per package that differs in those tasks, and names each set's queues in the plan of the distributions it serves. Then have the worker load each distribution's tasks from the store by the distribution the attempt names, as the native host does.

**Owner's decision (23 November 2026):** deferred; one worker set per distribution stays.

## I-235. A scheme declares no reply that a message it sends waits for

**Issue.** The service console's gauge of the oldest wait for a reply (`th.await.oldest`, R-05-47) named the WPS salary file and its acknowledgement in platform code (`RuntimeGauges.Default`). R-09-142 takes it out: the gauge watches what the installation names (`Service:Gauges:AwaitedReplies`; the laptop names WPS's). The scheme manifest says which message an exchange sends (`disposition.sentBy`) and which one is delivered to an awaiting run (`disposition.deliver`), but not that one answers the other, so the runtime cannot derive the pairs.

**Recommendation.** Add to the scheme manifest's message an optional `repliesTo` (the message ids it answers), with a lint that each names a message of the same scheme sent `out`, in the next release of `uae.wps` (`uae.wps.ack` answers `uae.wps.sif-submission`), and have the gauge read the pairs from the schemes of the active distribution, the setting only adding to them.

**Done (24 November 2026, R-09-211).** A scheme manifest's message may say which of its scheme's outward messages it answers (`repliesTo`; lint L55: an inward message, naming outward messages of its own scheme, mutation case M92). `uae.wps` 1.5.0 (1.4.0 is wave 6's) says `uae.wps.ack` answers `uae.wps.sif-submission`. The gauge `th.await.oldest` reads the pairs from the schemes of every active distribution (`RuntimeGauges.InForce`, kept per distribution id) and the installation's `Service:Gauges:AwaitedReplies` only adds to them. `GaugesTests.The_awaited_replies_are_the_active_distributions_schemes_and_the_setting_adds_to_them` (both databases). What a sent document is called in the read model is the bank binding's `<operation>.kind` (on the laptop `uae.wps.sif-out`), not the message id: Issue I-310.

## I-236. The studio interpreter reads a rule's values as of the business date on a word the schema does not have

**Issue.** R-04-10 and `common#parameter.lookupDate` name the two dates `step-time` and `payment-date`. The studio's rule service (`studio/src/rules/service.ts`, `paramFacts` and `runRules`) reads a parameter as of the business date only when its `lookupDate` is `business`, which the schema refuses, so a parameter that declares `payment-date` is read as of the step on the studio interpreter, while the runtime's engine host reads it as of the payment's acceptance (R-09-135). No factory rule declares `payment-date` yet (only the operations views' `tatLimit` does), so no test shows the difference.

**Recommendation.** Make the rule service read `payment-date` as of the payment's business date (`opts.businessDate`), with a studio test of a rule whose parameter declares it; found by this change and not fixed, as it is the studio's.

**Done (24 November 2026, R-05-93).** The rule service reads a parameter declared `payment-date` as of the payment's business date (`opts.businessDate`, the header's `businessDate`), and every other as of the step; with no business date, as of the step (`lookupDateOf` in `studio/src/rules/service.ts`). The word `business` the schema does not have is no longer read. `studio/test/rules.test.ts` shows both dates and that `business` reads as of the step.

## I-237. The labour ministry's port is a scheme's, but its adapters are in the platform's ports library

**Issue.** R-09-142 says platform code names no scheme. `TransactHub.Ports/EmployerRegister.cs` holds the adapters of `uae.wps.port.labour-ministry.v1`, and names that port, because the WPS native module reaches them through `TransactHub.Ports` (Issue I-220). Its default channel header (`WPS`) is gone: the channel is the bank's binding's setting (`channel`), set in each bank's binding. `NeutralityTests.No_platform_or_engine_host_code_names_a_scheme` lists this file as the one exception.

**Recommendation.** Move the labour ministry's adapters into the WPS module (`TransactHub.Activities.Wps`) when the native contract gives a module what it needs to build a bound adapter (I-220), and drop the exception.

**Not moved (24 November 2026): blocked on the native contract, Issue I-315.** The adapters (`simulator` and `gateway-rest`) need, from inside a module, the binding in force for the attempt (its adapter and settings), the image's guarded HTTP (circuit breaker, timeouts, R-09-156) and the resolution of the binding's credential reference. The native contract 1.1.0 gives a module none of these: its ports are answered by the image (`INativePorts`). Moving them needs a contract minor (1.2.0), which changes the version every distribution's manifest, the studio's module builds and their tests name, and was not safe to make in this batch beside the release of factory 1.20.0. The exception in `NeutralityTests` stays, with this issue.


**Done (24 November 2026, R-09-263).** The adapters (`simulator`, `gateway-rest`) are the WPS module's, in `TransactHub.Activities.Wps/Native/EmployerStatus.cs`, built on the binding in force the native contract's bound ports give (R-09-261): the REST adapter builds its request (`POST {statusPath}`, `X-Ref`, the bank's `X-Channel`) and the image sends it to the binding's `baseUrl` with its credential, breaker and timeout. `TransactHub.Ports/EmployerRegister.cs` and the image's `INativePorts` case are gone, and `NeutralityTests.No_platform_or_engine_host_code_names_a_scheme` has no exception left. `LabourMinistryTests` (the module on the contract alone), `FileProtectionAndRegisterTests` (through the image's bound ports, a recorded gateway), `ContractTwoTests.A_module_builds_its_own_bound_adapter_and_the_image_sends_its_request_with_the_credential_it_keeps` (through the native host, a register on this machine).

## I-238. Other values of a UAE cluster in platform code

**Issue.** R-09-142 is checked for a scheme's name. A cluster's values are still defaults in platform code: the currency `AED` where a payment, a binding or a reply names none (`PostingActivities`, `CanonicalMapper`, the core banking adapters' `currency` and `localCurrency`, `CoreBankingEnquiries`). The test bench's one-off run (`deploy/laptop/bin/run-pinned.mts`) also takes its values from the bank folder's configuration set, not from the configuration release in force, and the WPS salary file as its default message.

**Recommendation.** Make a currency a binding's required setting or the payment's own, with no default, in a change of its own with the banks' bindings; have `run-pinned` read the values from the release API (`GET /values`) and name its message.

**Done (24 November 2026, R-09-185, R-09-187, R-11-104).** No platform or engine host code gives `AED` as a default: a currency is the payment's own or its batch's (`CanonicalMapper.ToPayment` takes the batch's), the caller's (the channel's charges preview now needs `currency`), or a binding's required setting (`currency` of the core banking simulator and of a gateway whose reply names none, `localCurrency` of the file bulk adapter, each by `PortBinding.Required`); a posting with no currency is refused (`BusinessReject`). `Money.Aed` is gone. Each bank folder's core-banking binding names its currency (`currency: AED`, the bank's setting). `NeutralityTests.No_platform_or_engine_host_code_defaults_a_currency` holds the platform's code to it and every bank folder's binding. `deploy/laptop/bin/run-pinned.mts` reads the bank's values from the release API (`GET /values`) as of the payment's date and gives them to the run, the distribution's own configuration set giving its bindings, and it needs its caller to name the message and the file; `LaptopDeploymentTests.The_one_off_run_reads_the_values_in_force_from_the_release_API_and_names_its_message`. The WPS module and the test bench's WPS data builder keep the scheme's currency, which is theirs.

## I-239. An activation that loses a race is refused, not tried again

**Issue.** Two activations of one bank and environment at once cannot both win (R-09-137): the store's unique key on the activation's number refuses the second, as `distribution.ActivationRaced`, and nothing of it is recorded. The service console shows the refusal to the bank approver who consented, whose action then fails; the person must ask again. A workflow package's dated activations (`ReleaseRegistry.Activate`) are numbered again and tried up to five times instead, as they are not a switch-over.

**Recommendation.** Keep the refusal: a switch-over the approver consented to was a switch-over from the distribution active then, and another may now be active. Have the service console's action say so and offer to ask again from the distribution active now. The refusal's code, `distribution.ActivationRaced`, has no entry in the platform's error dictionary yet: add one with its runbook in the next release of `p.errors.platform.v1`, and have `ErrorDictionaryTests` read the store's refusal codes too.

**Done in part (23 November 2026).** `distribution.ActivationRaced` has its entry and runbook in `p.errors.platform.v1` 1.8.0 (factory 1.18.0; 1.6.0 was held for it and never published): ask again from the distribution active now, with a new consent. `ErrorDictionaryTests.Every_code_a_refusal_of_the_store_carries_has_an_entry` reads every code the store's refusals carry. The service console's own "ask again" button is not built.

## I-240. On Flowable a value read as of the payment's date is read as of the timer

**Issue.** R-09-135 reads a parameter declared `payment-date` as of the payment's acceptance. The Flowable binding computes a timer's due date outside the run's state and does not have the payment's id where it reads the value, so it reads every value as of the timer's start, as of the step.

**Recommendation.** Give the binding's timer the payment's id (the root's `header.paymentId`, which the binding holds for the instance) and pass it with the read, with a test on the engine host's Flowable supervisor.

**Done (24 November 2026, R-09-186).** The Flowable binding hands a timer's read of a bank's value the payment's id, its root instance's `header.paymentId` (`FlowableBinding.PaymentOf`), so a parameter declared `payment-date` is read as of the payment's acceptance. `EngineHostTests.Flowable_reads_the_banks_values_as_of_each_step_and_as_of_the_payments_date` runs the values test of the other engines on the engine host's Flowable supervisor, with a stored package that carries the waiting workflow's BPMN as the generator writes it.

## I-245. A seed after a lock file the studio published

**Issue.** Since R-11-76 a bank's lock file can have a release the studio published on the content branch (`banks/<bank>/artefacts/b/pack/<release>.yaml`) beside the seeded `pack.yaml`. The seed replaces `pack.yaml` only by a later release (R-11-35), and does not read the studio's releases: a seed whose `pack.yaml` is at or below the studio's release leaves the studio's in force (the latest release wins), and its new artefacts are not in any lock file in force until the bank licenses again; a seed above it drops a licence the studio added unless the repository's `pack.yaml` names it too. The spec does not say which is the bank's source of truth once the studio has licensed a scheme.

**Recommendation.** Have `seed-content` read the bank's latest lock file on the branch (the studio's included) and keep its `licensedSchemes` in the lock file it writes, refusing a seed whose release is not later than it; or decide that a bank licensed in the studio is no longer seeded from this repository. Until then, after licensing in the studio, write the same list into the repository with `npm run studio -- bank-pack <bank> --licensed <scheme> --release <later> --write`.

**Done (24 November 2026, R-11-105).** `seed-content` reads the bank's latest lock file on the branch. When it is one the studio published (`banks/<bank>/artefacts/b/pack/<release>.yaml`), the seed keeps its `licensedSchemes`, with the seed's own after them, in the `pack.yaml` it writes (and in its ledger), and refuses a seed whose release is not later than it, writing nothing (`studio/src/content/seed.ts`, `keepStudioLicences`; the plan reports what it kept, `seedPlan(env).kept`). While the latest lock file is the seeded one, nothing changes. `studio/test/releases.test.ts` shows both.

## I-246. An upload from a console in dev sign-in has no token for the File API

**Issue.** The operations console's upload page passes the person's own bearer token to the File API's `POST /files/ui` (R-05-74). A console in dev sign-in (`TH_AUTH_DEV=1`) identifies its synthetic users by a header, not a token, and the File API has no dev sign-in, so the File API answers 401 and the page shows the refusal. On the laptop stack, where the console signs in at the bank's development provider, the upload works.

**Recommendation.** Leave the File API without a dev sign-in (it is the runtime's one way in for files); say on the upload page, in dev mode, that uploads need the identity provider's sign-in. If a dev-mode upload is wanted, give the File API the same dev header, refused in Production, as the other hosts.

## I-255. The File API and the engine host do not record their breakers' moves

**Issue.** Each production adapter's circuit breaker works in every host (R-09-155), but a breaker's moves reach the service console's health only from a host that sets the breakers' journal (R-09-156): the native host and the operations console do. The File API (its SFTP partners) and the engine host do not, because the change that built the breakers left their `Program.cs` to another change in flight. An open SFTP breaker is therefore not seen in the service console's health; the File API still refuses, and retries, as before.

**Recommendation.** In `runtime/src/TransactHub.FileApi/Program.cs` and `runtime/tools/TransactHub.EngineHost`, once the other change has landed, set `CircuitBreakers.Shared.Journal = new StoreBreakerJournal(store, "<machine>/file-api")` (and `/engine-host`) where the store is opened, and extend `BreakerHealthTests` with an SFTP partner that does not answer.

**Done (23 November 2026).** The File API's and the engine host's `Program.cs` set `CircuitBreakers.Shared.Journal = new StoreBreakerJournal(store, "<machine>/file-api")` (and `/engine-host`); both projects reference `TransactHub.Operations` for the journal. `BreakerHealthTests` with an SFTP partner that does not answer is not added.

## I-256. Where a payment-event stream starts, and one position per bank

**Issue.** The relay keeps one position per bank for `p.port.event-stream.v1` (R-09-153), whatever the binding: when the port is first bound it starts at the first row of the outbox, so every fact the store holds goes out; a binding with a new topic or cluster goes on from the position, so the new topic has no history. The spec does not say where a new stream starts, nor whether a change of topic is a new stream.

**Recommendation.** Add a binding setting `stream` (default the port's name) that names the position, so a bank that wants a replay to a new topic names a new stream, and `from` (`earliest`, the default, or `latest`), read only when the stream has no position yet. Both values with defaults and bounds, set in the configuration release.

**Done as recommended (23 November 2026, owner's acceptance, R-09-176).** The relay reads `stream` (default `p.port.event-stream.v1`; small letters, digits, dots and dashes) and `from` (`earliest` or `latest`) from the binding in force. A topic changed under the same stream goes on from its position; a new stream starts at the first fact or, with `latest`, records the newest outbox number as its start and sends no history. A value outside its bounds is refused, never clamped (`EventStreamTests`).

## I-257. A save slower than the gap timeout is passed over by the event stream

**Issue.** The relay publishes only an unbroken run of outbox numbers and waits at a gap (a save in flight) until the row after it is ten seconds old, as the read model's projection does (R-05-49). A save that takes longer than that between taking its outbox number and committing is taken for one that was rolled back, and its event is never published, though at-least-once delivery is asked for.

**Recommendation.** Make the gap timeout a dial of the operations console with a default and bounds, record each gap passed over as a fact on the console's chain so it can be looked for, and, when the store can give one, publish in commit order instead (a commit-ordered sequence or the database's change feed).

**Done (23 November 2026, owner's acceptance, R-09-175), by re-scanning behind the position.** When the relay passes a gap after the timeout it keeps the range of numbers it passed over in its position row (`stream_position.GapsJson`, insert-only, a column added on both databases), and every later pass looks for them again: a save that commits after the timeout is published on the next pass, before anything newer, even after a restart. Order per payment holds, because a payment's next fact can only be saved once the one before it committed (its chain's hash). The gap timeout is the binding's dial `gapSeconds` (default 10, from 1 to 600), not the console's, since it is the stream's; a range is dropped only after `gapHorizonMinutes` (default 1440, from 10 to 10080), and each range dropped is written in the position row so it can be looked for. `EventStreamTests` commits a save after the timeout, with the relay restarted in between, and sees every fact once. What is left is I-281.

## I-258. The laptop's configuration release does not carry its alert and event bindings

**Issue.** The laptop's optional profile `events` starts an MQTT broker and a Kafka broker (`compose.events.yaml`), and `deploy/laptop/banks/<bank>/events.yaml` binds both ports to them. A binding reaches the runtime only inside the bank's signed configuration release (R-09-110), and the laptop's test that builds that release (`deploy/laptop/test/insert-only.mts`) was held by another change, so it does not add `events.yaml`. Until it does, the laptop's bank has the placeholder bindings of its folder: an alert is recorded as `alert-failed` and the events wait.

**Recommendation.** When the profile is on, have the laptop build the bank's configuration release with `events.yaml` as it does with `file-transfer.yaml`, and add a check that a turnaround breach reaches the broker and a payment's events reach the topic.

**Done (23 November 2026).** `deploy/laptop/test/insert-only.mts` builds ADIB's configuration release with the bindings of `deploy/laptop/banks/adib/events.yaml` beside `file-transfer.yaml`. The check that a turnaround breach reaches the broker and a payment's events the topic is not added: the test runs without the profile `events`.

## I-259. A refusal by an open circuit breaker has no error code of its own

**Issue.** A call an open breaker refuses fails as a transport failure (`CircuitOpenException`, an `HttpRequestException`), so a step retries it as it retries a system that does not answer, and the service console's probe `circuit-breakers` uses `service.ProbeFailed`. There is no entry in the platform's error dictionary that says a breaker is open and what to do (`p.errors.platform.v1` 1.4.0 is another change's, so this change added none).

**Recommendation.** In `p.errors.platform.v1` 1.5.0 add `port.CircuitOpen` with a runbook (find the failing system in the probe's detail, mend it or its binding, and wait for the trial call), raise it from the probe and from a step's refusal, and have `ErrorDictionaryTests` read it.

**Done (23 November 2026).** `port.CircuitOpen` is in `p.errors.platform.v1` 1.8.0, with 1.7.0's entries (1.4.0 to 1.6.0 were never published) with its runbook. The service console's probe `circuit-breakers` fails with it, and a step a breaker refused fails with it, retryable (`PlatformPorts.Call`, `NativePorts` for a port's file); the code is `CircuitBreakers.OpenCode`. `ErrorDictionaryTests` reads it; `BreakerHealthTests` expects it.

## I-265. A message a payment received is carried by reference only, so a map that reads it on the runtime saw no content

**Issue.** The engines keep a message the front door delivered to a running payment by its id and type (`messages.lastReceived`, `messages.received`), never its content, and a deployed worker holds no payloads. On the runtime the Central Bank's ACK or NAK the File API received therefore reached `uae.wps.map.ack-in.v1` as a reference with no file: the reference behaviour and the native code-behind both read it as an ACK, so a NAK could not refuse the file. Found building the outward file through the File API (Issue I-192).

**Done (23 November 2026, R-09-162).** A message the runtime's front door delivered from a kept file is named `in-<file id>`; a worker that names the File API (`TH_FILE_API_URL`, its token by reference in `TH_FILE_API_TOKEN`) reads such a message's content and name from the File API (`GET /files/{id}`, `GET /files/{id}/content`) before the attempt, so the map's view holds the file (`studio/src/conformance/party.ts`). The laptop's `workers` service names the File API.

**Recommendation.** Have each engine hand the attempt the message's claim-check reference, as it does the context's payload, so that a worker with no File API reads the message too; until then a worker that reads a received file names the File API.

## I-266. The salary file's send reads no encryption fact, so an encrypted file is not what goes out

**Issue.** `uae.wps.exchange.send-salary-file.v1` reads `header` and `messages`. Its native code-behind (1.1.0, R-09-162) sends the file the render kept (`messages.rendered`). The encryption step before it (`p.protect.encrypt-file.v1`) writes the encrypted file's name and hash to `facts.encryption`, which the send cannot read; reading it is a read added, a new major line (check L26). On the laptop the encryption port is the test distribution's simulator, so nothing is lost there; at a bank that encrypts for WPS the plain file would go out.

**Recommendation.** Either release `p.protect.encrypt-file.v1` in a minor that also writes the encrypted file as `messages.rendered` (a write added to `messages`, which it does not write yet: check whether L26 calls that a major), or add `uae.wps.exchange.send-salary-file.v2` that reads `facts.encryption` and sends the encrypted file when there is one. Until one is decided, a bank that binds the WPS port to the File API must not also bind `p.port.encryption.v1` to a real key.

**Done (24 November 2026, R-09-182).** The second option: `uae.wps.exchange.send-salary-file.v2` (2.0.0, a read added, a new major line) reads `facts.encryption` and, where the bank's binding encrypted the file, sends the encrypted file the host kept; an encryption that names a file the host does not hold, or one the service refused, sends nothing; a simulated encryption that names no file sends the rendered file, as before. The submit block's subflow `uae.wps.sub.submit-salary-file.v1` 1.3.0 calls it and the scheme's catalogue 1.4.0 names it as the sender (factory 1.19.0). Why not a minor of `p.protect.encrypt-file.v1` writing `messages.rendered`: a write added is a major too (L26), and a send that knows it sends an encrypted file can say so (`messages.sent.encrypted`) and refuse a mismatch. `PortFileTransferTests.The_send_v2_puts_the_encrypted_salary_file_on_the_central_banks_folder_where_the_bank_encrypts`: on the openpgp adapter with a key the test makes, the bytes put on the Central Bank's folder are an OpenPGP message that the recipient's key opens to the rendered SIF; v1 would have sent the plain file. The scheme's other sends are Issue I-293.

## I-267. An acknowledgement is correlated by a stem the bank's binding trims, not by a correlation map

**Issue.** The Central Bank names an ACK or NAK after the SIF it answers with its twelve-character WPS file id before the extension (the source's `WpsAckNakFile`). The front door correlates a received file by the keys the sender gives, else the stem of its name (I-160 (2)), which matches no payment for such a name. The scheme does not say this naming; it is the source's reading of the SSM.

**What is built (R-09-162).** A file a payment sends records its stem as the payment's key (`frontdoor.correlation`, `fileStem`); an inbox of the bank's file-transfer binding may say how many characters the partner adds to that stem (`<partner>.inbox.<name>.stemDrop`), and a file taken from it is correlated by its stem without them. ADIB's laptop binding sets `centralBank.inbox.ack.stemDrop: "12"`. No platform code names the scheme.

**Recommendation.** Have the scheme declare a correlation map for each `deliver` disposition, as I-160 (2) recommends, and replace `stemDrop` by it once the Central Bank confirms its naming; until then each bank's binding sets `stemDrop` for its Central Bank inbox.

**Done (24 November 2026, R-09-212, with I-160 (2)).** A `deliver` disposition may name a correlation map (`correlationMap`, an inward map that reads the message; lint L55, mutation case M93). The front door runs it on a received file before it is kept (`RuntimeFrontDoor.CorrelationKeys`) and matches the payment by the keys it answers (`messages.correlation`), before the stem of the file's name; a map that reads no key refuses the file (`frontdoor.Uncorrelated`). `uae.wps` 1.5.0 names `uae.wps.map.ack-correlation.v1` (factory 1.20.0) for the ACK: the AHR record's processed file name, else the ACK's own name split from the back before the Central Bank's twelve-character file id, as the source's `WpsAckNakFile` reads it (a native code-behind on that reader, `MapAckCorrelation`, and a script-behind). It is provisional, as the scheme is. `stemDrop` is retired: the File API no longer reads it and ADIB's laptop binding no longer sets it; ADIB's laptop behaviour (ACK name = SIF stem and the twelve-character id) now comes from the map. Tests: `FileApiTests.An_answer_named_after_the_file_a_payment_sent_reaches_that_payment` (by the AHR record and by the name; a short name refused; an answer to no payment unmatched), `NativeInwardTests.The_acknowledgements_correlation_map_reads_the_salary_file_it_answers`, `PortFileTransferTests` and `LaptopDeploymentTests` (no `stemDrop`). The PSI and the RSU keep matching by the sender's keys and the stem: their layouts name no key the factory knows (Issue I-311).

## I-268. On the laptop the WPS port is the runtime's, so the scheme's other exchanges have no simulator there

**Issue.** The laptop's bench gives `uae.wps.port.wps.v1` to the runtime (`TH_WORKERS_BENCH`), so the salary file leaves through the File API (R-09-162). The scheme's other sends on that port (the inward receipt confirmation, the returns and refund files) have no native code-behind that puts their file on the port, and on the laptop they find no simulator in the bench: the interpreter falls back to the port's first simulator with a warning. The laptop test runs outward salary files only.

**Recommendation.** Give each WPS send its native code-behind on the port, as for the salary file, in minor releases, and a check that every activity of a port the bench gives to the runtime has one; until then a laptop test of an inward or refund flow names the port's simulator in its bench.

**Done (24 November 2026, R-09-213).** The scheme's other sends on the WPS port have native code-behinds, as the salary file's has: `uae.wps.exchange.send-receipt-confirmation.v1`, `send-disbursement-file.v1`, `send-returns-file.v1`, `send-disbursement-cancellation.v1`, `send-refund-request.v1` and `send-refund-response.v1`, each at 1.1.0 (factory 1.20.0), one code-behind in the WPS module (`SendKeptFile`) that sends the file its render kept on the bank's binding of the port and refuses with nothing rendered. The check: `runtimePortGaps` (`studio/src/checks/runtime-ports.ts`) lists the activities of a port the bench gives to the runtime that have no native code-behind (a composite counts by its subflow); the studio's check of a test reports one under L15, and a worker whose package holds one does not start. `studio/test/runtime-port.test.ts` holds the laptop's bench to it; `PortFileTransferTests` sends a receipt confirmation through the binding. What the native sends cannot send yet is Issue I-313.

## I-269. No test hands a Flowable payment a message after its run started on the engine host

**Issue.** On the engine host a message for a running payment reached the run on DTFx and Elsa (tests since R-09-93) but not on Temporal: the host signalled the payment's front door workflow with the message as the sender gave it, and a message with no key of the run's was counted unmatched (found by the last change, fixed here: R-09-161, `DistributionWorkers.ForPayment` gives it the payment's conversation and id, and `LiveSignals.EventOf` adds the payment's id beside a sender's keys). The same test on Flowable cannot run on the stored package the test builds: Flowable runs the BPMN the studio exports, and a package without it is refused (`The plan has no BPMN`). Flowable takes the message on the same live path as DTFx and Elsa (`FlowableBinding`, `LiveSignals.EventOf`), so the fix applies, but no test shows it.

**Recommendation.** Give `EngineHostTests.MessageAfterStart` a Flowable variant whose stored package carries the exported BPMN of `p.wf.ask.v1` (an event-based await, as the generator writes it for a REST binding), so that Flowable is held to R-09-161 as the other engines are.

**Done (24 November 2026, R-09-207).** `EngineHostTests.MessageAfterStart` has a Flowable variant (`Flowable_takes_a_message_after_the_run_started_with_a_plan_from_the_store`): its stored package carries the exported BPMN of `p.wf.ask.v1` (an event-based await, as the generator writes it for a REST binding) at `engine/p.wf.ask.v1/flowable/`, the supervisor builds the plan from the store, and the message posted after the first step reaches the run (`w:received`, outcome `done`). The I-165 restart test (`Flowable_survives_a_restart_of_its_host_on_*`) awaits a message on a plan written by the test; this one reads the plan from the store, so the two do not overlap. Both pass on PostgreSQL; the restart test on SQL Server too.

## I-275. Who consents to the exchange-rate feed's releases

**Issue.** The owner asked that the rates feed publish its rates as a signed configuration release, which goes in force like any configuration (R-09-110, R-09-111): on a bank approver's consent. Rates refresh many times a day. The spec does not say whether each rate release needs its own consent, or whether the bank approver may consent once to a kind of release.

**Recommendation, built (R-09-179).** A standing consent the bank approver grants once, for rate releases only. It is asked for in the service console's book (`grant-rate-consent`, by a service engineer) and given by a bank approver, another person, like every change of the system (R-09-77); it is revoked the same way (`revoke-rate-consent`); each is a fact on its own chain (`standing-consent:fx-rates:<bank>`). It covers only a release whose configuration set sets `p.enrich.fx-rate.v1#rates` and nothing else, whose only other file is the rates table `b.ref.fx-rates.v1`, with no binding, and whose manifest names the feed (`feed: fx-rates`). Such a release is still verified against the trusted signers and checked against every distribution's parameter contract, and its in-force fact names the standing consent and who gave it. Every other release, and every rate release while no standing consent is in force, waits for a consent of its own. Why not a consent each time: an hourly consent would either be given without reading or leave payments refusing stale rates; the rates are market data the bank's own rates service gives, and the staleness limit (R-09-178) and the bounds still hold. Decide with each bank's treasury and its bank approver whether to grant it.

**Owner's decision (24 November 2026):** confirmed as built: a standing consent for rate releases only, granted and revoked by a bank approver.

## I-276. The rates feed's releases and the studio's releases share one line of numbers

**Issue.** A configuration release is numbered on the bank's one line of releases, and the value in force is the one the latest release that sets the parameter gives (R-09-111). The feed numbers its release the patch after the bank's newest (`1.10.1`, `1.10.2`, ...). The studio's next release is a minor (`1.11.0`), so the two do not collide while the studio makes no patch releases; but a studio release that sets `p.enrich.fx-rate.v1#rates` (the example bank's does) is newer than the feed's and its rates win until the feed runs again, and a studio patch release could take a number the feed took (the install refuses it, `ReleaseConflict`).

**Recommendation.** For a bank on the feed, the studio should not set `p.enrich.fx-rate.v1#rates` (lint: a bank whose configuration binds `p.port.fx-rates.v1` sets no `rates`), and should never make a patch release of a bank's configuration; or give the feed a line of its own (a configuration release per kind, ordered per parameter by release and kind). Until one is decided, a bank that takes the feed removes its typed rates.

**Done (24 November 2026, R-11-101, R-11-102).** The recommendation's first option. Lint L52 (tools/validate.py and the studio's check, mutation M82): a bank whose port bindings bind `p.port.fx-rates.v1` sets no `p.enrich.fx-rate.v1#rates` in its `configuration-set.yaml` or its newest released configuration set. Lint L53 (mutation M83): a bank's configuration is released as a major or a minor (`x.y.0`), never a patch, so the feed's patch numbers and the studio's never meet. The real banks bind the feed and type no rates; the example bank types its rates and binds no feed. `studio/test/port-bindings.test.ts`.

## I-277. The feed's signing key is trusted like the studio's

**Issue.** The feed signs its releases with a key of its own (`Service:FxRates:SigningKeyRef`, by reference), which the bank adds to its trusted signers. The trust list does not say what a key may sign, so a leak of the feed's key could sign any configuration release. That release would still wait for a bank approver's consent (the standing consent covers rate releases only), but it would verify.

**Recommendation.** Let a trusted signer name the kinds it may sign (`kinds: [configuration]`) and, for a configuration release, a scope (`feed: fx-rates`), checked at verification; give the feed's key that scope only.

**Done (24 November 2026, R-09-184, R-11-103).** A trusted signer may name `kinds` (among `workflow-package`, `distribution`, `configuration`) and, for a configuration release, `scope: { feed }`; verification refuses another kind, and for a scoped key a statement that claims no feed or another, with `signing.OutOfScope` (error dictionary 1.9.0, with its runbook). The feed signs `feed: fx-rates` into its statement; a release that claims a feed must name the same feed in its manifest and hold only what the feed makes (`Bundle.NotFeedRelease`, which the standing consent's check now uses too). A scope on a key that may sign more than configuration releases is an invalid trust list. Give the feed's key `"kinds": ["configuration"], "scope": { "feed": "fx-rates" }` in the bank's trusted signers. `SigningTests.A_trusted_signer_signs_only_the_kinds_and_the_feed_the_trust_list_gives_it`.

## I-278. A rate set by hand is never aged

**Issue.** `p.enrich.fx-rate.v1` 1.2.0 ages a rate only by its `asOf`, which the feed gives (R-09-178). A rate the bank types into its configuration has no `asOf`, so it is never refused as stale, and the operations console lists it as standing. That keeps every refund order of today running, but a bank that forgets to remove its typed rates once it takes the feed keeps old rates in force without a warning.

**Recommendation.** Add a dial `requireAsOf` (default false) to `p.enrich.fx-rate.v1`; a bank on the feed sets it, so that a rate with no `asOf` is refused like a stale one. Decide with each bank's treasury.

**Owner's decision (24 November 2026):** accepted. A bank on the feed sets `requireAsOf`, so a hand-typed rate with no `asOf` is refused like a stale one. To build in wave 7.

## I-279. Releasing a suspect payment needs no second approver

**Issue.** An alert's payment is released by the payment's own `approve` action (R-05-90). `p.view.operations.v1` offers `approve` on any approval hold to an Operator with no second approver, so one operator can release a payment the fraud engine found suspect. The view cannot tell a fraud hold from another approval hold; the original platform's rule for this is not on this machine.

**Recommendation.** Let a view's action say `when: { hold: approval, activity: p.screen.fraud.v1 }` (the step that led to the hold), and give the platform view an `approve` for fraud holds with `secondApprover: Supervisor`, in its next release. Decide with each bank's fraud team.

**Done (24 November 2026, R-05-94).** A view's action may say `when: { hold, activity }`: it is offered only on a hold that a step of that activity led to, as the hold's opening fact names it (`after`, which the runtime's hold now records) or, for a hold opened before, as the chain shows it (`HoldCause`: the latest step before the hold with a reason code or an outcome that holds, the step the hold's reason names). `p.view.operations.v1` 1.7.0 (factory 1.19.0) offers `approve` on a hold after `p.screen.fraud.v1` with a second approver of role Supervisor and remarks required, declared before the plain approve, which every other approval hold keeps. The fraud alert list's release goes through it: it waits for a supervisor, another person, and the console says so. Lint L38 holds `when.activity` to a hold and an activity a flow of the view reaches (mutation M84). `FraudAlertTests` and `runtime/ops-console/test/unit/oversight.test.ts`. A hold after parallel branches is Issue I-290. Decide with each bank's fraud team whether the second approver should also refuse.

## I-280. What makes a fraud alert, beyond a suspect payment

**Issue.** An alert is a payment `p.screen.fraud.v1` found `suspect` (R-05-90). The spec does not say whether a sanctions hit (`p.screen.sanctions.v1`, `hit`), a fraud engine that did not answer, or a payment the engine scored high but cleared is an alert too, nor whether an alert should carry the engine's score and case reference (the fraud port answers only a status, a reason code and a reference).

**Recommendation.** Keep sanctions hits out of the fraud list (they are compliance work, deferred with the cases, I-188); ask each bank's fraud team whether a high but clear score should be an alert, and if so add a `score` to the fraud port's answer and a dial for the threshold.

**Owner's decision (24 November 2026):** accepted. Sanctions hits stay out of the fraud alert list; each bank's fraud team is asked whether a high but clear score should raise an alert, and the score rule is added only on that answer.

## I-281. The event stream still drops a gap after its horizon

**Issue.** The relay looks for a passed-over outbox number until the gap horizon (R-09-175, default a day). A save that commits later than that is still never published. No save of the store lasts that long (each is one transaction with the database's command timeout), but the spec asks that no committed fact is ever skipped.

**Recommendation.** When the store can give one, publish in commit order instead: on SQL Server a `rowversion` read below `MIN_ACTIVE_ROWVERSION()`, on PostgreSQL the transaction id read below `pg_snapshot_xmin(pg_current_snapshot())`; then no gap is passed over at all. Until then, keep the horizon well above the store's longest transaction.

**Done (24 November 2026, R-09-214).** The relay publishes in commit order by default (`EventStreamRelay.CommitOrder`): each outbox row carries the database's commit version (`outbox.CommitVersion`: on SQL Server a computed column over a new `rowversion`, on PostgreSQL the writing transaction's id, `pg_current_xact_id()`; migration `EventStreamCommitOrder` on both), and a pass publishes the rows below the oldest version in flight (`MIN_ACTIVE_ROWVERSION()`, `pg_snapshot_xmin(pg_current_snapshot())`) after the keyset its last position names (`stream_position.ThroughVersion`, `ThroughId`, both added, insert-only). A save in flight holds back only what was written after it, for as long as it takes: no gap is passed over, kept or dropped. A stream that published by number keeps that number as its floor and its open gaps until found or past the horizon, so a running stream changes over without sending anything twice. The model is cached per provider, as it now differs by provider. `EventStreamTests.In_commit_order_a_save_that_commits_after_the_horizon_is_published_and_no_gap_is_kept` holds a save open past the horizon on both databases; the earlier gap tests run by number (`CommitOrder = false`).

## I-282. The ledger view knows four parameter names

**Issue.** The operations console's ledger view lists the accounts of every accounting activity whose parameters are named `settlementAccount`, `suspenseAccount`, `chargeIncomeAccount` or `vatAccount` (R-05-91). An activity that names a ledger account otherwise (a scheme's charge account, a future nostro per currency) is not shown, and the console cannot know it is an account.

**Recommendation.** Let an activity's parameter say what it is (`role: ledger-account`, with its kind), in its next release, and have the view list every parameter so marked; until then the four names are the platform's convention, held by the accounting templates.

**Done (24 November 2026, R-05-100).** A parameter may say what it is: `role: ledger-account`, with its `ledgerKind` (`spec/schemas/common.schema.json`; lint L56, an activity's string or code, mutation case M96). The accounting activities mark their accounts in their next releases (`p.post.accounting.v1` 1.2.0, `p.post.accounting.v2` 2.1.0, `uae.wps.post.return.v1` 1.2.0; factory 1.20.0). The ledger view (`LedgerAccounts`) lists every parameter so marked by the activities of the bank's active distributions, set or not, by its kind, and keeps the four names for an activity released before. `LedgerAccountTests.The_view_lists_every_parameter_the_active_distribution_marks_as_a_ledger_account` lists a scheme's charge account and a nostro per currency the four names do not know, on both databases.

## I-285. A native map that reads no header was refused on a runtime as not pinned

**Issue.** A runtime's native host finds the payment an attempt is for in the view's `header.paymentId` (R-09-132, R-09-143). The view is what the activity reads, and `uae.wps.map.sif-out.v1` reads the batch, its items and the scheme's extension only. Once the laptop gave the render to the native host (`TH_NATIVE_ACTIVITIES`), every salary file's render was refused as `native.NotPinned` and the payment was stuck before its file was written. The laptop's insert-only test, run for the first time after that change, showed it: no salary file reached the Central Bank's folder.

**Done (23 November 2026).** The worker sends the payment's id beside the view, as the attempt's `paymentId`; the native host takes the view's `header.paymentId` first and the attempt's own otherwise, for its routing and for the files it keeps (`AttemptRouting.PaymentOf`, `AttemptPaymentTests`, `studio/test/native.test.ts`). The view stays what the activity reads.

**Recommendation.** Write the attempt's envelope (activity, release, distribution, payment, instance, tenant, level) as a schema of its own, apart from the view, so that a field the host needs is never looked for in the view again.

## I-286. A file's payment has two ids, and the operations console knows only one

**Issue.** Recording runtime video 3 on the laptop: the File API's front door starts a salary file's payment under its own id (`uae-wps-<n>`), pins it and keeps the file, the outgoing SIF and the ACK under that id. The store accepts the file's canonical batch as a bulk with an id of its own (`StoreJournal`, `AcceptBulk`), and every fact of the run, the read model and the operations console's payment page use that one. So the upload page's link to the payment it started (`Payment uae-wps-…`, R-05-74) answers "No payment or bulk … for your tenants", and the payment's Documents tab lists none of its files, which the read model holds under the front door's id. Nothing records which bulk a front-door payment became, other than its pin and its end on the release API.

**Recommendation.** Make one id: let the store accept a payment the front door started under the front door's id (`header.paymentId`), or record the bulk's id as a correlation on the front door's chain when it is accepted. Then the console's payment route resolves either id, the read model files each kept file under the subject the console shows, and the upload page's link opens the payment. Until then the video opens the payment from the payments list and shows its kept files through the File API (`GET /files?paymentId=`).

**Done (24 November 2026, R-09-180, R-05-95).** The first option: the store accepts a payment the front door started under the front door's id. Why: one id needs no lookup anywhere, where a correlation would need one in every reader (the console's route, the read model's documents, the File API's `GET /files?paymentId=`, the service console's actions, the release API's end); and the payment's pin, its run's facts and its end are one chain that verifies as one. The claim check's acceptance carries `header.paymentId` (`AcceptInfo.PaymentId`, `ClaimCheckStore.AcceptFor`, on every engine), and `PaymentStore` takes it where the store holds the pin under that id and no bulk or payment has it yet; otherwise a new id, as before. The read model files a message or file kept under a pinned id before acceptance under that id, takes the front door's facts named `payment` for the bulk the store holds, and puts the chain's earlier facts (the pin, the start) on the timeline once the payment is accepted. So the console's payment route resolves the upload page's link, and the Documents tab lists the file in, the SIF out and the ACK in. `ClaimCheckTests.A_payment_the_front_door_started_is_accepted_under_the_front_doors_id`, `OperatorApiTests.A_payment_the_front_door_started_opens_under_its_id_with_its_files`, on SQL Server and PostgreSQL. The video's workaround is no longer needed; it was not recorded again.

## I-290. A hold after parallel branches is told by the latest step with a reason

**Issue.** R-05-94 offers the supervisor's release on a hold that a fraud step led to. The runtime's hold records the step that led to it (`after`) as the latest step before it with a reason code or an outcome that holds (`HoldCause`), the step the hold's reason has always named. In `p.seg.check-and-screen.v1` sanctions and fraud screening run side by side: if both hold at once (sanctions `pending`, fraud `suspect`), each hold names the same latest step, so one of the two holds names the other's cause. A hold after a parallel step that cleared is not affected: a clear answer gives no reason code.

**Recommendation.** Let the engine name the step that holds and the step whose handler it is in, in the attempt's envelope (with Issue I-285's envelope schema), and have the hold record that step, not the latest one.

**Done (24 November 2026, R-09-241).** The engine names the step whose handler an attempt is in (`handlerOf`: step, activity, outcome) on DTFx, Elsa and Temporal (the shared interpreter) and in the studio's interpreter and worker; the native host gives it to the image's holds (`AttemptEnvelope`, not the native contract), and a hold records that step (`after`, `afterStep`) and its reason; an attempt that names none keeps R-05-94's latest step. Evidence: `OperatorActionTests.Holds_after_parallel_branches_each_name_the_step_whose_handler_they_are_in` (SQL Server and PostgreSQL). Flowable is Issue I-361; the attempt's envelope schema stays I-285's.

## I-291. The kit's business elapsed check counts Saturday and Sunday only

**Issue.** A kit scenario may check its elapsed time on the business calendar (`virtualElapsedBusiness`, CK-AWAIT-02). The runner counts it on `DEFAULT_CALENDAR`, not on the calendar the scenario's documents give, so a scenario over a holiday must state its elapsed time in plain days, as CK-AWAIT-08 does.

**Recommendation.** Count `virtualElapsedBusiness` on the scenario's calendar (`businessCalendarIn` over the scenario's documents and the store's tables, as the run counts it).

## I-292. The trusted signers of a bank do not show what each key may sign

**Issue.** R-09-184 lets a trusted signer name its kinds and scope. The service console shows the trusted keys and their rotation, not their kinds or scope, and no laptop or bank folder holds the feed's entry: each installation adds it by hand to the document `TH_TRUSTED_SIGNERS` names.

**Recommendation.** Show each key's kinds and scope on the service console's trust page, and have the rotation action ask for them, with the feed's key offered as `kinds: [configuration]`, `scope: { feed: fx-rates }`.

## I-293. The scheme's other sends still read no encryption fact

**Issue.** R-09-182 makes the salary file's send send the encrypted file. The returns file, the refund request and response, the disbursement cancellation and the receipt confirmation are encrypted by their subflows too, but have no native code-behind yet (Issue I-268) and read no encryption fact: once each is given one, it would send the plain file as the salary file's v1 did.

**Recommendation.** Give each send its v2 that reads `facts.encryption`, with its native code-behind, in the same change as I-268's; and retire `uae.wps.exchange.send-salary-file.v1` (now reached by no workflow, L11) once no distribution in force pins it.

**Done (24 November 2026, R-09-232, R-09-233, R-05-101).** WPS's six other sends have v2 lines at 2.0.0 that read `facts.encryption` and send the encrypted file where the bank's binding encrypted it, one code-behind with the salary file's (`SchemeSend`, `runtime/src/TransactHub.Activities.Wps/Native/SchemeSends.cs`); the six subflows call them from their next minor and the scheme's catalogue 1.6.0 names them; the v1 lines leave the factory, and their native code-behinds stay for the distributions pinned to 1.20.0 (Issue I-364). Lint L58 holds a send after an encryption step to reading the fact (mutation M99). `uae.wps.exchange.send-salary-file.v1` is retired: out of `RELEASES`, the WPS module and the laptop's worker list (`deploy/laptop/compose.yaml`, `LaptopDeploymentTests`); no workflow of the factory has called it since 1.19.0 and the laptop's distribution pins 1.20.0. Evidence: `PortFileTransferTests` (`Every_send_v2_puts_the_encrypted_file_on_its_messages_folder_where_the_bank_encrypts`, `The_WPS_module_carries_no_send_the_factory_retired_and_each_send_is_its_schemes_message`).

## I-294. A payment with no currency is accepted with none

**Issue.** R-09-185 removed the platform's default currency. A payload whose payment and batch name no currency is accepted into the store with an empty currency, and only its posting refuses it (`BusinessReject`). The canonical checks do not refuse it first.

**Recommendation.** Have `p.validate.canonical.v1` refuse a payment that names no currency (a rule of the platform's canonical rule set, in its next release), so the payment is refused at its checks, before anything else runs.

**Done (24 November 2026, R-09-231).** The check-and-screen segment's canonical checks read the batch and its lines: `p.validate.canonical.v2` (2.0.0, a read added, a new major line) with `p.ruleset.canonical.v1` 1.1.0, which holds `p.rule.batch-currency-present.v1`: a file whose batch names no currency and one of whose lines names none either is refused (`AM03`) at its checks, before anything else runs; a line that names its own is not refused. `p.seg.check-and-screen.v1` 1.1.0 calls it (factory 1.21.0). The inward legs keep v1: their payment names its own currency, which `p.rule.currency-present.v1` already checks. Evidence: the rule's examples (`tools/validate.py`, `npm run studio -- check`) and `studio/test/rules.test.ts` (`refuses a file that names no currency`).

## I-300. A distribution's engine service runs one work item at a time

**Issue.** Since I-165 one DTFx or Elsa service runs every payment of a distribution (R-09-196). Its orchestration service is the harness's: on DTFx one work item holds the baton at a time (an episode or an activity attempt), on Elsa every operation runs on the clock's thread, and a timer fires only while nothing is in flight. One slow worker call therefore delays every other payment of the distribution, and the service's throughput is that of one payment at a time. The spec sets no throughput for an engine host (I-68).

**Recommendation.** Decide the target with the owner (I-68). Then let the service run work items of different instances side by side (a baton per instance, activities off the baton), and keep the history's order per instance rather than per service, or run several services per distribution, each with a share of the payments by root id.

**Done (24 November 2026, R-09-238).** A distribution's DTFx service runs several work items at once (`VirtualTimeOrchestrationService.Concurrency`): episodes of different instances and activities side by side, an instance's episodes still one at a time in its own message order, each activity finding its own slot by its input. The host's setting `TH_HOST_SERVICE_CONCURRENCY` (1 to 64, default 4; out of bounds stops the host) reaches the service as `TH_SERVICE_CONCURRENCY`, and the observation says it. A kit run keeps one at a time, so it stays deterministic. Evidence: `RestartTests.Dtfx_runs_a_distributions_payments_side_by_side_while_one_workers_call_is_slow` (a payment's 25-second worker call holds up no other), and the DTFx restart and one-service tests. Elsa is Issue I-360; the throughput target stays I-68's, and the default is Issue I-362.

## I-301. A distribution's history chain is read whole at every start of its service

**Issue.** The service's history (R-09-196) is insert-only, so it is never trimmed: a service that starts again reads and folds every fact of every payment the distribution ever ran, though only the open payments matter, and an ended payment's instances are rebuilt too. A distribution that runs for months will take long to start. The spec says nothing of how long a host may take to start.

**Recommendation.** Add a checkpoint fact to the chain (the open instances' state, the activities and timers outstanding) every so many episodes, and fold only from the last checkpoint; on Elsa, skip the facts of payments whose end is `reported`, as the rebuild already skips their instances. Set a start-time target with the owner.

**Done (24 November 2026, R-09-239).** The DTFx and Elsa services checkpoint their history every `TH_HOST_HISTORY_CHECKPOINT_EVERY` facts (50 to 100000, default 500): one fact holding, in their order, the facts of the payments whose end the host has not recorded, without the service's openings (`StoreHistory.Compact`). A service that starts again reads the chain from its last checkpoint only and folds it as before. Chosen over a snapshot of each engine's state: the fold stays the one way a service is rebuilt, on both engines. The chain stays insert-only and verifies whole. Evidence: `StoreHistoryTests` (SQL Server and PostgreSQL) and the DTFx and Elsa restart tests. The start-time target is Issue I-362.

## I-302. The Flowable binding's journal holds a payment's whole context at its start

**Issue.** Before the binding starts a root it records the command it is about to send (I-89), and the `start` command carries the payment's context: its payload (amounts, parties) as well as its header. The journal's facts are therefore not free of personal data (R-09-10), unlike DTFx's and Elsa's history (R-09-196), which carries the engine's state only. This was so before I-165; a distribution's binding now holds it for every payment it starts.

**Recommendation.** Record the claim check's payload first (its `create` fact) and put only the engine's state and the payload's reference in the journal's `start` command, as the DTFx and Elsa services do; the replay of a start then reads the payload from the claim check.

**Done (24 November 2026, R-09-206).** The binding records a root start's payload in the claim check first, under a reference known before the server names the process (`<label>-<tenant>#start`, a `create` fact that accepts nothing), and journals the `start` command with the engine's state and that reference only; a start scheduled in the binding's queue is held the same way. A start sent again after a restart reads its payload from the claim check, rebuilt with the instances' payloads (`FlowableBinding.StartOf`, `ContextOf`). The payload is then created under the process's own reference and accepted, as before. A journal written before the change, whose `start` carries `ctx`, is still read. `FlowableRestartTests` (all three) check that no fact of the run's journal holds the payload and that the start's payload is a claim-check fact; they fail on the code before.

## I-303. An activity that was running when its service stopped runs again

**Issue.** A DTFx or Elsa service that starts again runs again an activity attempt that was in flight when it stopped (R-09-197), as Temporal and Flowable hand out again a task whose worker's lock expired. The step's fact is recorded once (StepFacts), but the worker is called twice. The DSL allows retry only on an activity that is pure, read, or carries an idempotency key, and says nothing of a run again after a crash, which no workflow can declare away.

**Recommendation.** State in the spec that every activity attempt is at least once on every engine (a crash, an expired lock), so that an activity with an effect MUST carry an idempotency key whether or not it retries, and add the check to the validator with its mutation case.

**Done (24 November 2026, R-03-25, R-03-26).** The spec says that every activity attempt is at least once on every engine and in the interpreter (R-03-25, in `spec/design/03-activity-contract-and-types.md` and the guard-rails of `spec/CLAUDE.md`), and that an activity with an outside effect carries an idempotency key whether or not it retries (R-03-26): its effect is `write` or `external`, it is a `reserve`, `hold` or `notify` (a `screen` counts by its effect, Issue I-316), an `exchange` that sends, responds or reports, or it names a port whose operations only send. Lint L54 checks it in `spec/tools/validate.py` and in the studio (`checks.ts`), with mutation cases M90 (a notify that says it reads, with no key) and M91 (a key that reads the clock), and `studio/test/checks.test.ts`. The key must read a business id of the header and not the clock, a new id or the attempt. Every activity of the factory and of the bank packs already passes: the schema has required a key on `write` and `external` since R-03-05, and no activity of another type with an outside effect says it only reads, so no factory activity needed a new release.

## I-304. A rule trace kept in memory is lost when a service starts again

**Issue.** The DTFx and Elsa services keep the rule traces a worker reports (R-03-*) in the claim check's memory (`RuleTraces`), not in the store. A payment taken back after a restart (R-09-197) loses the traces of the steps it ran before the restart, so its observation's `ruleTraceSteps` is short. The step facts are kept.

**Recommendation.** Record a step's rule trace as a fact on the payment's chain with its step fact, and read it from there for the observation and the operations console.

**Done (24 November 2026, R-09-240).** A step's rule trace is kept with its step fact on the payment's chain (`StepFacts.RuleTrace`): the rules with their release, stage, fired and activations, the findings' codes and paths, the decisions' names and the worst; never the facts the rules were given, their bindings or the parameters' values. The engine host's observation reads them from the chain (`ruleTraces`), so a payment taken back after a restart has them all, and the console's timeline shows the rules that fired. Evidence: `OperatorActionTests.A_steps_rule_trace_is_kept_on_the_payments_chain_without_the_facts_it_was_given` (SQL Server and PostgreSQL).

## I-330. The pages' content security policy and a UI library that writes its own styles

**Issue.** The studio and the runtime's consoles moved to Fluent UI 2 (ADR 0003). Fluent writes its component styles and its theme's tokens into `<style>` elements at run time. The pages' policies said `style-src 'self'` (R-11-43 for the studio, R-09-60 for a runtime host), which a browser applies to those elements too: with the policy as it was, every page drew unstyled. R-09-60 says the console's page "may load only its own scripts and styles"; it does not say whether styles the page's own script writes are its own. R-11-43 forbids inline script only.

**Done (24 November 2026).** Each answer with the page names a fresh nonce: the studio server (`appCsp`, `src/server/http.ts`) and the runtime hosts (`SecurityHeaders.WriteConsolePageAsync`, `UseConsolePage`) put it in the page's `<meta name="th-style-nonce">` and in `style-src 'self' 'nonce-…'`; the page gives it to Fluent's renderer, which sets it on every style element. `unsafe-inline` stays out, and scripts are still the page's own files only (`script-src 'self'`). Checked by `studio/test/security.test.ts` and `SecurityHeadersTests`.

**Recommendation.** Say in R-09-60 and R-11-43 that a page may also apply style elements its own script writes when they carry the nonce of that answer, and never inline script; or, if the owner prefers no style elements at all, build Fluent's styles ahead of time into a stylesheet (Griffel's CSS extraction), which needs a build plugin the studio's Vite build does not have today.
## I-337. A payment's page does not show that its file was never delivered

**Issue.** A WPS leg's send step ends `accepted` when the File API's outbox takes the salary file (R-09-162), and the leg's "Sent to the Central Bank at" is that time. When the SFTP partner is down, the outbox's delivery attempts fail and its circuit breaker opens, but nothing reaches the payment's chain: the operations console shows the payment as sent and waiting for the Central Bank's answer, and only the service console's health (`circuit-breakers`) shows that something is wrong, with no link to the payments it holds up. An operator who opens the payment cannot tell a late answer from a file that never left. Found on the laptop while recording the end-to-end video's technical failure (`docs/demo/4-end-to-end.mp4`).

**Recommendation.** Have the File API record on the payment's chain each failed delivery of its file and the end of its attempts (with the port, adapter and error code), and the delivery itself; show them on the payment's timeline and set the leg's sent time from the delivery, not from the outbox's acceptance. Let the operations console list the payments whose file is kept and undelivered, with the resend the service console offers (Issue I-333).

**Done (24 November 2026, R-09-236, R-09-237, R-05-103).** The File API's outbox tells the payment's own chain of each delivery of its file (`file.delivered`), each failed attempt (`file.delivery-failed`, code `port.CircuitOpen` where the partner's breaker is open, `file.NotBound`, else `file.DeliveryFailed`) and the end of its attempts (`file.undelivered`, `file.Undelivered`); an open breaker now fails the attempt instead of the outbox's round. The read model reads them as a view source (`delivery:<message>.<outcome|at|attempts|adapter|code|fileName|folder>`, lint L38, mutation M102); `uae.wps.view.operations.v1` 1.7.0 takes "Sent to the Central Bank at" from the delivery and shows the delivery, its attempts and its error, with the outbox's acceptance as "Taken by the File API at". The console lists the payments whose kept file is not delivered (`GET /ops/api/undelivered`, section Undelivered files) with the payment's resend, and its timeline says each delivery in words. Error dictionary 1.11.0 holds the three codes. Evidence: `FileApiTests` (`The_payment_is_told_each_failed_delivery_of_its_file_the_end_of_the_attempts_and_the_delivery`, PostgreSQL), `ProjectionTests` (`A_file_the_File_API_never_delivered_shows_on_the_payment_its_leg_and_the_undelivered_list`, SQL Server and PostgreSQL), `runtime/ops-console/test/unit/oversight.test.ts`.

## I-336. The service console's log failed once any action was done

**Issue.** The service console's log (`GET /service/api/logs`, R-09-69) read a few fields of each fact's data as text: `result`, `error`, `code`, `reasonCode`, `step`, `activity`, `instance`. A service action's `action.done` fact, and a release's `activate` fact, carry their `result` as an object, and casting an object to text throws. So once an engineer's action had been approved and done (on the laptop, the first install of a distribution), the log answered 500 and its page showed only "Unexpected token 'S' ... is not valid JSON". Found on the laptop while recording the end-to-end video.

**Recommendation.** Read such a field as text only when it is text, and an object by its `code`; test the log on a chain that holds a done action.

**Done (24 November 2026).** `EventLog.Facts` reads them so (`Word`, `runtime/src/TransactHub.Service/Logs.cs`); `ActionTests.A_risky_action_waits_for_another_person_and_every_troubleshooting_action_reaches_its_target` now reads the log of a payment with done actions, and answered 500 before.

## I-335. No bank hook ran on a runtime

**Issue.** The long-running engine host builds each payment's plan from the stored distribution with the bank's values read live (R-09-135), but with no hook bindings: a distribution carries no configuration set, and the configuration release put in force kept its values, port bindings, calendars and tables, not its hook bindings. Every bank hook point therefore ran as unbound on a runtime, whatever the bank's configuration said: ADIB's bound labour ministry notification never ran, and a hook a bank binds in a new configuration release never runs, while the studio's bench and the test bench (which read the configuration set from the package) ran them. Found on the laptop while preparing the end-to-end video's new hook.

**Recommendation.** Keep a configuration release's hook bindings when it is put in force, and have the engine host give each instance the bindings in force when it starts, read through its claim check like a value so that a replay takes the same.

**Done (24 November 2026).** Putting a configuration release in force keeps its configuration set's hook bindings (`HookBindings`, kind `hook-bindings`); the engine host reads those of the latest release in force when an instance starts (`Deployment.LiveBindings`, the claim check's op `bindings`, `StoreValues.Bindings`), on Temporal, DTFx and Elsa through the shared interpreter, and on Flowable when the binding starts an instance (R-09-217; `EngineHostTests.*_runs_the_hook_the_configuration_in_force_binds`, which time out before). A configuration release put in force before this change kept no hook bindings: put it in force again as a new release. Flowable's read has no test of its own yet.

## I-334. An engine host that restarts refuses the answers for the payments it was running

**Issue.** The engine host keeps the payments it started in memory only (`DistributionHost.Payments`). When its container is recreated (on the laptop, `docker compose up -d` after a new image), the Temporal runs of the payments in flight go on, but the host answers 404 (`... is not running on <distribution>`) to the File API's front door for every message of theirs: the Central Bank's ACK is moved to the inbox's `error/` folder (`file.not-handed`, `frontdoor.EngineRefused`) and the payment waits until its acknowledgement wait ends. Found on the laptop while preparing the end-to-end video.

**Recommendation.** On start, have the host rebuild its list of running payments from the store (each payment's `engine started` fact and no end) or from the engine (Temporal's running front-door workflows of the distribution's build id), and add a laptop or engine-host test that restarts the host between a payment's send and its ACK. Until then, do not restart an engine host while payments wait for an answer.

## I-333. The service console could not resend a salary file the File API kept

**Issue.** Since R-09-162 a WPS salary file leaves through the File API's outbox, which keeps it; the store's `message` table holds no copy. The service console's `resend-file` (and the operations console's `resend`) read the store's copy only, so for a SIF the SFTP server never took (the partner down, the outbox's delivery attempts used up while its circuit breaker was open) both answered `ops.NoFileKept`, and nothing in a console could send it again. Found on the laptop while preparing the end-to-end video's technical failure.

**Recommendation.** Let a resend send the File API's kept bytes when the store keeps none: list the payment's files at the File API (`GET /files`) and resend its last sent file there (`POST /files/{id}/resend`), idempotent by the action's id, with the `message resent` fact on the payment's chain. Have the operations console offer `resend` for such a payment too.

**Done in part (24 November 2026).** The service console's `resend-file` does so (`FileResend`, `FileApiTransferPort.ResendKept`; R-05-58; `FileApiTests.The_consoles_resend_goes_through_the_File_API_outbox_and_is_delivered_on_the_banks_binding`, which failed with `ops.NoFileKept` before). The operations console still offers `resend` only where the store keeps the file: that part is open.

## I-332. The laptop's Flowable server never reports healthy

**Issue.** `deploy/laptop/compose.yaml` checks the `flowable` service's health at `/flowable-rest/actuator/health` with no credentials. Flowable REST 7.1.0 answers that endpoint to its admin only (401), and the image has BusyBox's `wget` and no `curl`, so the service stays `unhealthy` and `docker compose --profile flowable up -d --wait` fails, although the server works. Found while running the bench's Flowable run on the laptop for I-331.

**Recommendation.** Check the endpoint as the admin the service is given.

**Done (24 November 2026).** The health check calls the endpoint with the admin's login from the container's own environment (`th-laptop`, `FLOWABLE_REST_APP_ADMIN_PASSWORD`); the service reports healthy.

## I-331. The laptop's test bench cannot pay a bank's salary file on Temporal

**Issue.** On the laptop stack (`deploy/laptop`) the runtime's test bench ran no passing payment for the runtime's bank, found while recording the installation video (`docs/demo/2-installation.mp4`). Three causes were small and are fixed: (1) the bench signs what it exports from the content branch with a key of its own, but trusted that key only when `TH_TRUSTED_SIGNERS` was unset, and the laptop sets it to verify released packages, so every content run was refused (`runtime/testbench/driver/bench.ts`); (2) the bench's host runs with `Store__Migrate=false` for the bank's store, and an engine run inherited it, so the run's own new database was never made (`TransactHub.TestBench/Runner.cs` now sets `Store__Migrate=true` for that database); (3) the profile `wps-default-engine` evaluated on 17 November 2026, before ADIB's values start (22 November), so the bank's rails were not enabled and the reference answered `route:no-match` (its date is now 24 November). A tester of the laptop may act for its bank only, so a profile's fixed date before the bank's values cannot be worked around from the web app, which has no date field. The cause that is left: on the laptop's Temporal server (not the time-skipping test server) the WPS leg never receives the outward simulator's acknowledgement (the run's `messages` is empty), waits about fifty minutes of the harness's time and ends `rejected`, while the interpreter settles. It happens for the example tenant too, with either date.

**Recommendation.** Find why the Temporal harness does not deliver a simulator's answer to the leg on a real Temporal server (the test-bench run with `environment.temporal` an address), and add the laptop's `wps-default-engine` run to `deploy/laptop/test/insert-only.mts` or a laptop test of its own. Give the bench's web app a date (the profile's by default), or let a profile take the bank's first `validFrom`. Until then the video shows the bench's run on the reference interpreter only, with this issue named, and one real payment through the File API.

**Done (24 November 2026).** The cause was not the answer's way to the run but the task queues. Every Temporal run the bench starts polled the plan's bare task queue names (`transacthub`, `th-p`, `th-uae-p`, `th-uae-wps`, `th-bank`), and so did every other bench run on the same server and the engine host's long-running workers (which tell distributions apart by build id, on the same names). On the laptop's shared Temporal server one run's workflow and activity tasks were taken by another process: two runs at once (the tester's bank and the example bank), the bank's file checks ran in the other run's harness, which held no payload for that run (`ext.uae.wps.employerId` absent, `uae.wps.rule.employer-id-format.v1` rejects, FF01), so the leg ended `rejected` before its salary file was sent (the run's `messages` empty) while the interpreter settled; a front door's task taken by another process routes no simulator answer. The time-skipping test server is one per run and never showed it. Now a run that is not a long-running host's polls queues of its own: its distribution's, or names that carry its run id (`ScenarioRunner.RunQueue`, R-09-216; test `TemporalRunQueueTests`). On the laptop, after the change, the bank's run and the example bank's run at the same time both settle on Temporal on SQL Server, the same as the interpreter (each about ten minutes: the simulator answers after ten minutes of business time, which a real server plays in real time); DTFx and Elsa settle too, and Flowable (below). The bench's web app has a business date (the profile's by default; the request's `clock.date`), so a tester can move a run on or after the bank's values. The laptop's own Temporal run is not added to `insert-only.mts`: it takes ten minutes of real time (Issue I-48).

## I-310. A sent file's kind is the bank binding's word, not the scheme's message

**Issue.** The gauge of the oldest wait for a reply now reads its pairs from the schemes (R-09-211): `uae.wps.ack` answers `uae.wps.sif-submission`. The read model's documents carry the kind the File API kept a sent file under, which is the bank binding's `<operation>.kind` (on the laptop `submit-file.kind: uae.wps.sif-out`), so the pair the scheme declares matches no document of a sent salary file, and the laptop's `Service:Gauges:AwaitedReplies` names `uae.wps.sif`, the channel's inward file's kind. The spec does not say what a sent file's kind is.

**Recommendation.** Have the File API keep a file a port sends under the id of the scheme message its exchange sends (the message whose `disposition.sentBy` names the exchange), and retire `<operation>.kind`; until then a bank's binding names that message id as the kind, and the installation's setting names the pair its kinds need.

**Done (24 November 2026, R-09-234, R-05-102).** A sent file is kept as the id of the scheme message its exchange sends: each native send names its message (`uae.wps.sif-submission` for the salary file) and `PortFileTransfer.KindOf` takes it; `<operation>.kind` is read only for a send that names none (a module of an earlier distribution), and ADIB's laptop binding names none. Lint L59 holds every message a file scheme sends to one send exchange of its own, and no exchange to two messages (mutations M100, M101). The laptop's `Service:Gauges:AwaitedReplies` stays as it is: it names the channel's inward file, which the scheme's pairs do not. Evidence: `PortFileTransferTests`. The store's own copy of the Central Bank SIF is Issue I-363.

## I-311. The PSI and the RSU have no correlation map

**Issue.** A deliver disposition may now name a correlation map (R-09-212), and the ACK's does. The PSI (paired with its PIF by the payment file id) and the RSU (by the refund request file id) have none: their layouts in the factory say no field that carries the key, so the front door still matches them by the sender's keys and, failing those, the stem of the file name.

**Recommendation.** Once the rulebook (D10) gives the PSI's and the RSU's layouts, give each deliver disposition its correlation map, in a release of `uae.wps`; until then the stem stands.

## I-312. No report layout for WPS, and the report activity reads no lines

**Issue.** The scheme manifest can now declare a report (`reconcile`, R-09-210), but no source gives the Central Bank's reconciliation report, so `uae.wps` declares none, no report map exists, and `p.reconcile.scheme-report.v1` still reads `header, status, messages` with a contract that says nothing of lines; the runtime's reconciliation is the File API's intake (`SchemeReconciliation`), named by the disposition's activity.

**Recommendation.** When the Central Bank gives its layout, write its map in a release of `uae.wps` and declare the report; release `p.reconcile.scheme-report.v2` then, with a contract over `messages.report.lines`, so that the studio can test a report as the runtime reads it.

## I-313. Three of WPS's sends have no native render, and every send of the port goes to one folder

**Issue.** Since R-09-213 every send of the WPS port has a native code-behind, which sends the file its render kept. The receipt confirmation, the disbursement file and the returns file are rendered natively by the source's writers (PRC, DIF, RFA); the disbursement cancellation (DCR), the refund request (RFR) and the refund request response (RRR) are not: the source holds no writer for them and the factory's layouts are unconfirmed, so on a runtime their native send finds no kept file and refuses (`FF01`). And the bank's binding of the port names one folder and one kind for its `submit-file` operation (on the laptop `centralBank.sifFolder`, `uae.wps.sif-out`), so every file of the port goes there under the salary file's kind.

**Recommendation.** Write the DCR, RFR and RRR renders natively once the rulebook (D10) gives their layouts, in minor releases; and let a port binding name a folder and a kind per message the port sends (`submit-file.<message>.folder`), or the File API derive the kind from the message (Issue I-310). Until then a laptop or test of the refund cycle names the port's simulator in its bench.

**Done (24 November 2026, R-09-235; the folders part).** A port binding may name a folder per message the port sends (`submit-file.<message>.folder`, as `submit-file.folder` is named); a file of that message goes there, else to the operation's folder. Evidence: `PortFileTransferTests.Every_send_v2_puts_the_encrypted_file_on_its_messages_folder_where_the_bank_encrypts`. Still open: the DCR, RFR and RRR renders, until the rulebook (D10) gives their layouts.

## I-314. The corporate match does not compare the CIF yet, and the e-mail has no place

**Issue.** Since R-02-22 the debtor carries the CIF the channel declared (scheme CIF), but WPS's match of the corporate the party register found (`WpsCorporateMatch`, the source's `MatchCorporate`) compares the account only. The source compared a CIF, an account and an e-mail; the canonical model has no place for the uploader's e-mail, which is personal data, and the spec does not say whether the match needs it.

**Recommendation.** Have the match compare the debtor's CIF organisation id with the register's CIF where both are known (a minor of the WPS module); and decide with the owner whether the e-mail is compared at all, and if so where it is read (the channel's identity, not the payment).

## I-315. A native module cannot build a bound adapter of its own

**Issue.** The labour ministry's adapters (I-237) and the posting window's use by a binding (I-227) stay in the image because a module, which references the native contract only (R-09-125), cannot read the binding in force for an attempt, call its endpoint through the image's guarded HTTP (circuit breaker, timeouts), or have the image resolve the binding's credential reference. Each image answer a module needs is a case of `INativePorts.Ask`, written in the image for one scheme's port.

**Recommendation.** Release native contract 1.2.0 with bound ports: `INativeServices.BoundPorts.For(attempt, port)` giving the binding's adapter and settings, and `Send(attempt, binding, request)` that sends a request to the binding's base URL with its credential applied by the image and its breaker and timeouts, the failures as the contract's `TransientError`, `PortNotBound` and `SecretUnavailable`. Then move the labour ministry's adapters into the WPS module and `p.post.accounting.v1` with the posting window's check into the platform module, drop the image's copies, `NeutralityTests`' exception and `DuplicatedLogicTests`, and update the contract version wherever a manifest, a build or a test names it.


**Done (24 November 2026, R-09-261 to R-09-264), as native contract 2.0.0, not 1.2.0.** The owner chose to make the breaking change once, while no bank is live, with I-226. `INativeServices.BoundPorts` (`INativeBoundPorts`) gives `For(attempt, port)`, the binding in force (`NativeBinding`), and `Send(attempt, binding, request)` (`NativeHttpRequest`, `NativeHttpResponse`): the image sends on its own copy of the binding in force, refuses another (`PortNotBound`), a path that names a host or a header that carries the credential (`NativePortRefused`), applies `secretRef` as a bearer token or in `credentialHeader`, and goes through the endpoint's circuit breaker within `timeoutSeconds`; failures are `TransientError`, `port.CircuitOpen`, `SecretUnavailable`, `NoTenant` and `PortNotBound`. A module's gate on a binding is the new `NativeBindingGate`, keyed by `NativeContributions.BindingGate`. The labour ministry's adapters moved (I-237), and `p.post.accounting.v1` with the posting window (I-227). Every manifest, the studio's module builds (`NATIVE_CONTRACT`), the black-box tests, the laptop's module build and signing (which read the contract project's `Version`) and the docs name 2.0.0; the migration is `docs/runtime/PACKAGING-AND-DEPLOYMENT.md` section 3.2.

## I-316. Whether a bank's AML screening leaves a case at the service

**Issue.** Lint L54 (R-03-26) first counted every `screen` as an outside effect. DIB's `b.screen.employer-aml.v1` (mined from the source's bank fork) is a screen declared `read`, with no idempotency key, and the source does not say whether its AML service records a case for each request. The factory's screens (sanctions, fraud) are `external` and carry keys. So a screen now counts by its effect, as an enrich does: one that only reads may be `read`.

**Recommendation.** Ask DIB whether its AML service records a case per request. If it does, release `b.screen.employer-aml.v1` 1.1.0 as `external` with an idempotency key on the payment's id; if not, nothing changes.

## I-340. A bank's second table approval cannot build its configuration release

**Issue.** Approving a bank's own reference table builds the bank's configuration release (R-11-52), named by the configuration set's release (and `+bindings.<release>` when port bindings are released, R-11-47). When a release of that name is already on the content branch, a second approval that changes only a table builds a release of the same name with other content, and is refused: `R-09-21 The branch main already holds other content for ... releases/configuration/example-bank/1.11.0/...`. So, after the configuration set's release has been built once, a bank cannot approve a new edition of its calendar, its cut-offs or a new table of its own without also releasing a new configuration set. Found while recording video 5a: the configuration set (with the cut-off edition in its change set) was approved, then a new table `b.ref.payroll-desks.v1` was refused. The video approves the new table first, which builds the release that was not there yet.

**Recommendation.** Name a configuration release by everything it carries, as the port bindings already are: for example `<configuration set release>+tables.<n>`, `n` counting the bank's table approvals since that set release, or a hash of the reference tables' releases. Then the runtime's release ids, the tag pattern in `studio/src/content/github.ts`, R-11-47 and R-11-52 change with it, with a test in `studio/test/bank-calendar.test.ts` that approves two table editions in turn under one configuration set release.

**Done (24 November 2026, R-11-115).** Each approval that builds a bank's configuration release gives it a release of its own, numbered rather than named by its contents: the configuration set's release while no configuration release of the bank on the branch has reached it, else the minor after the newest (`nextConfigurationNumber`, `nextConfigurationRelease` in `studio/src/export/distribution.ts` and `studio/src/governance/distributions.ts`, reading the head commit's tree and what the studio built since). The port bindings' release stays as build metadata after it (R-11-47), so the tag pattern in `studio/src/content/github.ts` and the runtime's release ids are unchanged. The number is never a patch (R-11-102, lint L53), so the rates feed's patches after the newest release and the studio's next minor never meet, and a later release orders after every earlier one on the runtime as well (`ReleaseOrder`); the runtime's checks (signature, parameter contracts, insert-only store) are as before. The configuration set inside keeps its own release, so the values' `configurationSet` is what it was. A built-on-its-own configuration release without a release given takes the same number. `studio/test/bank-calendar.test.ts` approves a table, a second table and a value change in turn under one configuration set release and gets three releases; it failed with the 409 before.

## I-341. The bench and the tests run only published releases

**Issue.** The test bench (`POST /api/bench/run`) and a stored test run their target from the published releases: a draft activity is `404 No <name>`. So a designer who writes a new activity and its script cannot run it before it is published: the activity is approved and published first, its tests are saved from the bench afterwards, in a change set of their own, and until then its tests gate stays red ("No test expects: pass, reject"). Part 8 says AI drafts a script with its tests, and R-11-19 says approval runs the tests that target a member with the whole set in place, which suggests tests are written before the approval; the spec does not say whether the bench runs drafts. Video 5b shows the order the studio allows: publish, then bench, then tests.

**Recommendation.** Decide whether the bench runs the signed-in maker's drafts as a working set, as the checks already do (`gov.draftSet`). If it should, resolve the target and every call from the tenant's drafts first on the bench and in a draft test's run, mark such a run as made with drafts, and add a requirement and a black-box test: an activity draft with its script runs on the bench, its tests are saved as drafts in the same change set, and approval publishes the three together.

**Done (24 November 2026, R-09-230).** The bench runs the signed-in designer's drafts when asked: a draft run. `POST /api/bench/run` with `drafts: true`, a draft test, or `drafts: true` on `POST /api/tests/{id}/run`, runs on a copy of the store in which the tenant's drafts take the place of their published releases (`withDrafts`, `studio/src/testing/draft-run.ts`), the same working set the checks use; the copy writes nothing. The report carries `draftRun` (the drafts it ran and a note), the bench shows it as "Draft run" on the page, and a draft test is listed with a `draft` badge. A draft run sees no other tenant's drafts, is not kept as a test's last result, and publishes nothing; a distribution, an export and a configuration release are built from the published store only. A draft target asked for without drafts is still a 404, now saying it is a draft. A test saved from a draft run goes into the activity's change set, so approval publishes the activity, its script and its tests together, with the tests run with the whole set in place (R-11-19). `studio/test/draft-run.test.ts`, `studio/test/blackbox/ui-draft-run.test.ts`. The bench change is a checkbox and the draft-run note only, apart from the business-date field of I-331.

## I-342. A scripted check must say it reads outside state

**Issue.** The activity schema lets a `validate` or `decide` activity without rule sets have only `effect: read` (`spec/schemas/activity.schema.json`, allOf 3), so that a check against an outside register is not called pure. A check written as a script that reads only the context, such as `uae.wps.validate.pay-period.v1` in video 5b, is pure, but the schema refuses `effect: pure` for it, so it is declared `read`, and its retry and idempotency reasoning (R-03-25) treats it as a read.

**Recommendation.** Allow `effect: pure` for a `validate` or `decide` activity bound to a script with no ports, keep `read` required when it names ports, and add the mutation case and the check to the catalogue in `design/05-workflow-dsl.md` as `spec/CLAUDE.md` asks.

**Done (24 November 2026, R-03-27).** The activity schema takes `effect: pure` beside `read` for a `validate` or `decide` without rule sets (`spec/schemas/activity.schema.json`). Lint L57 holds the rest, in `tools/validate.py` and the studio's `checks.ts`: a pure activity names no port, and a check without rule sets is pure only when it is bound to a script (a builtin or a subflow without rule sets reads, as `p.validate.duplicate.v1` and `p.decide.await-posting-window.v1` do; mutations M97 and M98). Pure needs no idempotency key, as read does not (L54). The interpreter refuses a port call from a pure activity (`ContractViolation` R-03-27) before the declared-port check. R-03-08 says a check of its own may name no rule sets. No factory activity changes: `p.decide.cut-off.v1` and `p.decide.posting-window.v1` read the bank's calendar and the clock and stay `read`. `studio/test/checks.test.ts` ("a scripted check without rule sets may be pure").

## I-338. A step that parks on Temporal deadlocked its run, so no retry reached it

**Issue.** A step with a park policy that fails parks for an operator's retry or re-drive (R-05-55), and the engine host records the payment's step fact `stuck` through `Interp.Stuck` (`runtime/src/TransactHub.Engines.Durable/EngineSignals.cs`). That callback wrote the fact to the store and waited for it, in workflow code. On Temporal the workflow runs on its deterministic scheduler, so the write's continuation could never run: the workflow task failed with `TMPRL1101 deadlocked after 00:00:02`, every later task of the run timed out, and the operator's retry, delivered and recorded on the payment's chain, never reached the parked step. The hold's decision had been moved off the workflow's thread for the same reason (5ba6106); the stuck fact had not. Found while recording video 5c: with the laptop's native host stopped, a salary file's render step parked, and `retry-attempt` with the approver's consent changed nothing.

**Recommendation.** Write the stuck fact as the hold's decision is written: queued, by a thread of its own, in order.

**Done (24 November 2026, R-05-55).** `StoreSignals.Install` queues both the stuck fact and the hold's decision for one writer thread (`th-step-facts`); a replay that parks again still records one fact. `OperatorActionTests.A_parked_step_is_recorded_stuck_without_waiting_on_the_workflows_scheduler` calls the callback on a scheduler of one thread, as a workflow's is, on SQL Server and PostgreSQL; it timed out before. On the laptop, the parked render step now takes the retry, renders and sends the SIF (video 5c).

## I-339. The service console listed running salary files as pins never started

**Issue.** The content page's pins with no payment (R-09-165) counted a pin as started only when a payload `create` fact named the payment. A salary file's run on Temporal writes its payload as `accepted` for the bulk, and its steps on the bulk's own chain, so every salary file still running fifteen minutes after its pin was listed as never started, with `end-pin` offered: a bank approver's consent would have ended a running payment as abandoned. Found on the laptop while preparing video 5c (`GET /service/api/content/stuck-pins` listed two files waiting for their ACK).

**Recommendation.** Count a pin as started when an engine's run took the payment in any of the ways it records it.

**Done (24 November 2026, R-09-165).** `DistributionRegistry.Unstarted` (`runtime/src/TransactHub.Store/Distributions.cs`) treats a pin as started when a payload `create` or `accepted` fact names the payment, or when its own chain has a `step` or `engine` fact. `DistributionContentTests.A_pin_with_no_payment_is_listed_ended_with_consent_and_its_distribution_drains_and_is_removed` adds such a bulk; it was listed before.

## I-350. A step's error had no runbook link in the service console's log

**Issue.** An engine's step fact names its error as an object, `error: { name, retryable }`. The service console's log (`runtime/src/TransactHub.Service/Logs.cs`) read only `error.code` from an object, so a step's error was neither linked to its runbook nor found by the code filter, and the page showed it only inside the masked data, where the masking replaced its `name` with `[masked]`. And the error a worker raises when it cannot reach the native host, `NativeHostUnreachable` (`studio/src/interp/interpreter.ts`), has no entry in the platform error dictionary (`p.errors.platform.v1` 1.10.0), so even read by its name it has no runbook. Found in video 5c, where the native host was stopped and the render step failed with it.

**Recommendation.** Read an error object's `name` as its code. Release `p.errors.platform.v1` 1.11.0 with an entry for `NativeHostUnreachable` (technical, retryable; checks: the native host's health and its log; safe action: bring the host back, then retry the parked step; action `retry-attempt`), with the factory's next release and a check that every error name the interpreter and the engine bindings throw has an entry.

**Partly done (24 November 2026, R-05-52).** The log reads an error object's name as its code, so the code filter finds it and a known code links to its runbook (`ServiceApiTests.Logs_show_the_facts_masked_filtered_and_linked_to_the_payment_and_each_error_to_its_runbook`); each item carries its step's error unmasked, by name and whether it may be retried (an error's name is a code; the fact's data is masked, and an error object's `name` was masked with it), and an error the dictionary does not know is named on the row (`stepError`, `runtime/ops-console/src/service/format.ts`). The dictionary entry is not added: it is a new release of factory content.

**Done (24 November 2026, R-09-243).** The platform's error dictionary 1.11.0 holds `NativeHostUnreachable` (technical, retryable, raised by `interpreter`; checks the native host's health and log; safe action: bring the host back, then retry the parked step; action `retry-attempt`). `ErrorDictionaryTests` passes. The check that every error name the interpreter and the engine bindings throw has an entry is `ErrorDictionaryTests.Every_code_a_native_activity_or_the_interpreter_raises_has_an_entry`, as before.

## I-351. The service console's content page misread configuration releases and lacked what it is for

**Issue.** Four things, found while recording video 5c. (1) The Difference of a configuration release grouped its files by `kind:name`, which a configuration release's bill of materials does not have, so every file appeared as one line keyed `:` with all the hashes joined. (2) Every configuration release ever put in force was shown `in-force`, although only the latest counts (a configuration set is the bank's whole set, `ConfigurationValues`). (3) The page showed neither the values in force nor the pins with no payment, which have API routes or store reads of their own, and offered no Put in force for a release installed but not in force. (4) Nothing showed the trusted signers, nor a distribution's native modules and their SHA-256 (R-09-120 to R-09-123). And a long probe name, the disk's path, widened the health page beyond the screen.

**Recommendation.** Key a configuration release's files by path; mark earlier releases superseded; show the values in force, the pins and what the runtime trusts.

**Done (24 November 2026, R-09-40, R-09-110, R-09-111, R-09-165, R-09-120).** The Difference keys a file without a kind by its path. Of a bank's configuration releases put in force, the latest is `in-force` and the earlier ones `superseded` (the rates feed's releases among themselves). `GET /service/api/content/values` answers the values in force at a moment, each with its configuration set and day, and the hook bindings in force; `GET /service/api/trust` the trusted signers, each in use, retired or revoked, and each installed distribution's native modules with their files' SHA-256 (`ContentService.ValuesInForce`, `Trust`, `NativeOf`). The content page shows the values in force, the pins with no payment with End pin, and Put in force for a configuration release awaiting consent; a new page, Signers and modules, shows the signers and the modules, with Re-verify. Long names and values wrap in their cells. `DistributionContentTests.A_configuration_releases_difference_values_in_force_and_supersession_and_the_signers_and_native_modules_are_shown`, `A_distributions_native_section_is_listed_module_by_module`, `ServiceApiTests.Every_route_needs_a_signed_in_caller_with_a_service_role`, `runtime/ops-console/test/unit/service.test.ts`.

## I-352. The service console's health has no probe for the native host, the workers or the engine host

**Issue.** The health page probes the store, the read model, the package source, the trusted signers, Temporal, the disk and the circuit breakers. With the native host stopped on the laptop, health stayed green apart from the disk while salary files parked at their render step; the engineer found the cause only in the log (video 5c).

**Recommendation.** Add probes for the native host (`/health` of `TH_NATIVE_HOST`), the activity workers and the engine host, each with an error code and a runbook entry, as `TH_HEALTH_URL` already names for each container.

**Done (24 November 2026, R-09-244).** The service console's health probes the native host (`Service:Hosts:NativeHost`, else `TH_NATIVE_HOST`'s `/health`), the activity workers (`Service:Hosts:Workers`) and the engine host (`Service:Hosts:EngineHost`, a connection only, as its routes need a token), in the group `hosts`, each failing with its own code and runbook (`service.NativeHostDown`, `service.WorkersDown`, `service.EngineHostDown`, dictionary 1.11.0); an HTTP probe whose host does not answer now fails instead of throwing. The laptop names the three (`deploy/laptop/compose.yaml`). `RuntimeHostProbeTests`.

## I-353. The File API answers 500 with an exception's text when the engine host is down

**Issue.** With the laptop's engine host stopped, `POST /files` answered `500` with the .NET exception and its stack (`HttpRequestException: Name or service not known (enginehost:7600)`), and no code. The channel cannot tell a refusal from a fault, and the text names the runtime's hosts. Found while preparing video 5c.

**Recommendation.** Have the front door catch a failure to reach the engine host, answer `503` with a code of the error dictionary (for example `frontdoor.EngineUnreachable`) and no exception text, and say whether the file was kept for a retry.

**Done (24 November 2026, R-09-244).** The front door's engine host client (`HttpEngineHost`) turns a host that does not answer into a refusal, `frontdoor.EngineUnreachable`, answered 503 with words that name no host and no exception; the file is kept and the pin ends abandoned with the code (R-09-164). Dictionary 1.11.0 has the entry. `FileApiTests.An_engine_host_that_does_not_answer_is_a_clean_refusal_with_its_code_and_the_file_kept`.

## I-354. The first native attempt after the native host starts timed out

**Issue.** The native host loads a distribution's modules, and checks every file's SHA-256, when the first attempt names that distribution (`NativeModules.For`). On the laptop that load takes about nine seconds, and a native activity's start-to-close timeout on Temporal is five, so the first attempt after the host starts timed out, although the host then answered: in video 5c, the retried render step of a salary file parked again, with the SIF rendered too late to count.

**Recommendation.** Load the distributions that can take attempts when the host starts, before an attempt asks.

**Done (24 November 2026, R-09-122).** The native host's refresh, which already unloads removed distributions, also loads each distribution active or draining in some environment, once, and its first tick comes as the host starts (`NativeModules.Preload`, `runtime/src/TransactHub.Native/NativeModules.cs`); a refusal is left to the attempt that asks. `SideBySideTests.A_host_that_starts_loads_the_active_distribution_before_any_attempt` failed before. The host's health still answers before the load ends; a recommendation for a readiness that waits for it stays open.

## I-380. `native.ContractUnsupported` has no entry in the error dictionary yet

**Issue.** Contract 2.0.0 refuses a 1.x module with its own code, `native.ContractUnsupported` (R-09-262), at install (the release API's package refusal) and at load (the native host's refusal). The code is in `ErrorCodes` (`NativeContractUnsupported`), so `ErrorDictionaryTests.Every_runtime_code_has_an_entry_with_a_runbook` fails until the platform's error dictionary has an entry; this change was asked not to release the factory while two other changes release 1.21.0 and 1.22.0.

**Recommendation.** Add the entry to the next release of `p.errors.platform.v1` (in `spec/factory/p/errors/`, then `seed-content --write-local`):

```yaml
  - code: native.ContractUnsupported
    title: 'A native module was built against a contract this runtime does not take'
    category: technical
    retryable: false
    raisedBy:
      - native
      - package
    meaning: 'The distribution''s native modules were built against a native contract of another major than the runtime''s, or a later minor (R-09-262). Since native contract 2.0.0 every module built against 1.x is refused, at install by the release API and again when the native host loads the distribution.'
    causes:
      - 'The distribution was built before the runtime took native contract 2.0.0.'
      - 'A module build made ahead of time was made against another contract than the runtime image''s.'
    runbook:
      checks:
        - 'Read the refusal: it names the module, the contract it was built against and the contract the runtime supports.'
        - 'Read the native host''s contract (GET /health, contract) and the distribution''s manifest (native.contract).'
      safeActions:
        - 'Do not retry. Rebuild every module against the runtime''s contract (the studio with TH_NATIVE_MODULES=build, or runtime/tools/native-module/build.sh), build a new distribution of the bank, install and activate it (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.2).'
        - 'Remove the refused distribution once nothing is pinned to it.'
      escalation: 'Escalate to the runtime''s service team with the distribution id and the contract versions.'
```

**Done (24 November 2026, R-09-243).** The platform's error dictionary 1.11.0 holds `native.ContractUnsupported` with its runbook, raised by `native` and `package-source` (the schema has no raiser `package`; the release API's package refusals are `package-source`'s, as `package.Rejected` is). `ErrorDictionaryTests.Every_runtime_code_has_an_entry_with_a_runbook` passes once the factory is seeded.

## I-381. A promotable distribution now needs the platform module

**Issue.** `p.post.accounting.v1` is a builtin (`implementation.binding: builtin`, `service: accounting`) with no script-behind, and since R-09-264 only the platform module answers it on the runtime. So a promotable distribution built with no module builds (`TH_NATIVE_MODULES` unset), which was taken before with `native.notCarried`, is now refused by the studio, as one with an activity bound `native` is. The factory's activity does not say that a module answers it: the studio lists it in `MODULE_BUILTINS` (`studio/src/export/native-modules.ts`).

**Recommendation.** In the next platform release, say in the activity that the runtime's platform module implements it (`implementation.native: true` beside `binding: builtin`, with lint L40 extended to take it), and drop `MODULE_BUILTINS`. Until then a studio that builds promotable distributions sets `TH_NATIVE_MODULES`, as the laptop and every bank's studio already do.

## I-382. The reservation and the reversal stay in the image, gated by the platform module

**Issue.** The image still answers `p.reserve.funds.v1`, `p.reserve.release.v1`, `p.post.reverse.v1` and `p.post.verify.v1` (the builtin reservation service and the posting's reversal and check). They call core banking, so they ask the gate the platform module contributes (R-09-264); in a table with no platform module (a development host whose `TH_NATIVE_DEV_MODULES` names none) a core-banking binding with a posting window is refused for them. The image names the two settings of a window (`CoreBankingCalls.GateSettings`) only to refuse that case.

**Recommendation.** Move these four into the platform module at its next release too, with a balance enquiry on `INativeCoreBankingBinding` (a contract minor, 2.1.0), so that the image keeps the core-banking adapters and no core-banking activity; then the image need not name the window's settings at all.

## I-383. Bound ports carry JSON over HTTP only

**Issue.** `INativeBoundPorts.Send` sends a JSON body and reads any body back; it is what the labour ministry's register needs. A scheme port on another wire (a file over SFTP, a message queue, a SOAP service with a signed envelope) still needs an adapter in the image, reached through `INativePorts` (a kept file on a port bound to `file-transfer` already is).

**Recommendation.** When a scheme's port needs another wire, add it to the bound ports as a contract minor (a body of bytes with its content type, then a file put through the binding's transfer), rather than an image case for the port.

## I-360. A distribution's Elsa service runs one operation at a time

**Issue.** R-09-238 lets a distribution's DTFx service run several work items at once. The Elsa service runs every operation on its clock's thread (`studio/engines/elsa/harness/Serve.cs`, commands and deliveries scheduled on `Clock`), so one slow worker call still delays every other payment of the distribution.

**Recommendation.** Run Elsa's dispatched operations (activity attempts) off the clock's thread, bounded by the same `TH_SERVICE_CONCURRENCY`, and hand each answer back to the clock's thread, so Elsa's own journal stays single-threaded; test it with the DTFx side-by-side test on Elsa.

**Done (24 November 2026, R-09-286).** A distribution's Elsa service runs its activity attempts side by side: the request is read on the clock's thread, the worker's call runs off it, bounded by `TH_SERVICE_CONCURRENCY` (a semaphore), and the answer is handed back to the clock's thread, which stages its writes and delivers it to the instance, on the history first. Elsa's journals, the history and the claim-check store stay on the clock's thread. The observation and the service's `READY` line say the concurrency. A timer of an instance that comes due while one of its attempts is in its call fires after the answer (Issue I-390). A kit run keeps one at a time on the clock's thread. Evidence: `RestartTests.Elsa_runs_a_distributions_payments_side_by_side_while_one_workers_call_is_slow` on PostgreSQL and SQL Server (it fails with `TH_HOST_SERVICE_CONCURRENCY=1`), every Elsa and DTFx engine host test, and 59 of 59 conformance scenarios on Elsa.

## I-361. Flowable names no handler for a hold

**Issue.** R-09-241 has the interpreter name the step whose handler an attempt is in, so a hold records that step. Flowable runs the exported BPMN, not the interpreter: its attempts name no handler, and a hold after parallel branches there still names the latest step with a reason (R-05-94).

**Recommendation.** Have the BPMN export put the handled step, its activity and outcome on each task of a handler (an extension element) and the Flowable binding send it as the attempt's `handlerOf`.

## I-362. The service's default concurrency and its start-time target are not the owner's yet

**Issue.** R-09-238 and R-09-239 set defaults of their own: four work items at once and a checkpoint every 500 facts. The spec sets no throughput for an engine host (I-68) and no time a host may take to start.

**Recommendation.** Decide with the owner a throughput per distribution and a start-time target, then set the defaults from a load run (`runtime/load`) on each engine.

## I-363. The store's copy of the Central Bank SIF keeps the binding's old word as its kind

**Issue.** R-09-234 keeps a file a port sends as its scheme message's id (`uae.wps.sif-submission`). `CentralBankSif` (`runtime/src/TransactHub.Activities.Wps.Store`), which keeps a stored bulk's SIF as a message, still names it `uae.wps.sif-out` (`MessageKind`), so the two copies of one file have two kinds.

**Recommendation.** Keep it as `uae.wps.sif-submission` in the next WPS store change, and read both kinds where a reader looks for the old one.

**Done (24 November 2026, R-09-288).** `CentralBankSif` keeps the store's copy under `SendSalaryFile.Message` (`uae.wps.sif-submission`), the id the File API keeps the sent file under. The store is insert-only, so a message or file kept before under `uae.wps.sif-out` stays so; the operations read model takes it as the scheme message (`DocumentKinds`), so the views' `message:` sources, the reports and the gauges find one kind. The laptop's insert-only test checks that no copy of P1's file carries the old word. Evidence: `CentralBankSifTests`, `ProjectionTests.A_SIF_kept_under_the_former_kind_is_read_as_the_scheme_message` and `OperatorApiTests.A_payment_the_front_door_started_opens_under_its_id_with_its_files` (SQL Server and PostgreSQL), `FileApiTests`.

## I-364. The six v1 sends' native code-behinds stay in the WPS module

**Issue.** Factory 1.21.0 replaces WPS's six other sends with their v2 lines (R-09-232). Their v1 lines' native code-behinds stay in the WPS module, because every real bank's lock file at 1.20.0 pins the subflows that call them.

**Recommendation.** Retire them as the salary file's v1 was (R-09-233), once no distribution in force pins a 1.20.0 lock file.

## I-390. On the engine host, an attempt's timeout does not end a worker's call that outlives it

**Issue.** R-05-54 has an attempt's timeout measure the attempt's own execution, in business time, and the Elsa binding races a journalled timer against the operation (Part 9, 9.4). On the engine host, business time is the wall clock. DTFx's service fires no timer while any work item is in flight (its clock is busy), and Elsa's service, before R-09-286, ran each worker's call on its clock's thread, so a call that outlived its attempt timeout was answered and taken, not abandoned. R-09-286 keeps that for Elsa: a timer of an instance that comes due while one of its attempts is in its call waits for the answer. So on the host a slow worker's call is never abandoned at its attempt timeout, and a retry never starts beside it; the worker's own timeout and the HTTP client's (five minutes) bound it instead.

**Recommendation.** Decide with the owner whether the engine host's attempt timeout is the worker's (Start-To-Close, as on Temporal and Flowable in production) or the engine's. If the engine's, have DTFx's and Elsa's services end an attempt at its deadline on the host's clock and abandon the late answer, with an engine host test of a call that outlives its timeout on both.

## Not done, and why

| Story | Why |
| --- | --- |
| C25-S1 Confirm the pack against the rulebook | Needs the current CBUAE WPS rulebook (D10). Nothing here confirms a scheme fact. |
| C25-S6 Planned message groups | Each needs a business decision to build, defer or drop. |
| C25-S5 Refund | Needs the refund order layout, which the pack marks unconfirmed; writing it would invent scheme facts. |
| C24-S1 to S5 Scripts and tests for every platform block | The reference behaviour runs every block; six blocks have scripts. Writing a test per outcome for every block is factory content for the generator, not yet done. |
