# Issues found while building the studio

Status: 17 November 2026. Each entry is an Issue and a Recommendation, as `spec/CLAUDE.md` asks. Where the build needed an answer to go on, the entry says what the studio does now. None of these changes the spec; each is for review at G1.

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
- **The kit** has 48 scenarios. It passes 48 of 48 on the studio interpreter, DTFx, Elsa and Temporal, and on Flowable since I-49 was done.

## I-42. Who keeps the SQL Server plugin for Temporal, and how an upgrade is proved

**Issue.** D2 is confirmed and the plugin is built (`runtime/temporal-mssql`, W5). It is pinned to Temporal v1.32.2. Temporal does not support it, and Temporal releases often: the persistence schema is at its 20th version, and a release can change a store method or add a table. The plan says to re-prove the plugin on each upgrade, but it does not name an owner, say how soon after a Temporal release the runtime must follow, or say what blocks an upgrade.

**Recommendation.** Name an owner for `runtime/temporal-mssql`. Make a Temporal upgrade one change that moves the pin, ports any new schema version and store method from the PostgreSQL plugin, and passes the CI job `temporal-sqlserver` (persistence suites, durability check, kit, WPS). Say in the plan that the runtime stays on the pinned release until that job is green, and that a security release of Temporal is the only reason to hurry.

**What is built.** The pin is exact in `go.mod`, and the README says each upgrade must re-run the proof. The CI job runs the whole proof on every push to `runtime/**`.

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

## I-51. Thirty days, or thirty business days

**Issue.** The source waits 2,592,000 s, 30 calendar days, for the RFA acknowledgement. The decision asks for a wait of 30 days by default, bounded 1 to 30 days, on the business calendar. On the UAE business calendar 30 business days are about six weeks.

**Recommendation.** Keep the parameter on the business calendar, as decided, and confirm the default with the rulebook (D10). If the scheme counts calendar days, 30 of them are about 22 business days: set the default to `P22D`, or let the parameter carry its own calendar.

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

Still open: (2) decryption as an activity behind a port, (4) erasure and retention, the Azure Key Vault provider (I-73) and a remote signer (I-74).

## I-68. Non-functional targets, deployment sizes and the reporting read side

**Issue 1. Targets.** The source stated latency, throughput, availability and recovery targets per deployment size. The plan states none for the runtime, and the spec forbids load tests in the studio. Without targets there is nothing to size an installation against, and no test to fail. Beyond the engine hosting of D3, the plan also does not say how an installation is packaged and fronted: container images, orchestration, the gateway and where TLS ends.

**Recommendation.** Write the targets an installation must meet (latency per step, salary-file size and time, availability, recovery point and time) in the plan, measured by a runtime performance suite outside the studio, on each engine and each database. Add a deployment section with one reference installation per engine, and grow it into sizes once the suite has measured them.

**Issue 2. Reports.** The plan decides only that the report database is fed from the outbox, not from change data capture (W2). Which reports exist, their shape, and how an operator searches payments are not decided. I-46 recommends free-text search on the runtime's store, not in Temporal.

**Recommendation.** Decide the reports of the first drop (WPS) and the read model behind them, as projections from `outbox`, in the same change as the code that builds them.

**What is built.** The `outbox` table in the store. Nothing reads it yet.

**What is built for the operator surface (2026-11-22).** The first read model: the operations read model (`runtime/src/TransactHub.Operations`, R-05-49). The store now writes an outbox row with every fact and every message, in the same save; the projection follows the outbox from a checkpoint, applies each fact once by its number on its chain, is rebuilt from the facts on request with the same result, and shows its lag (`GET /ops/api/health`, the console's top bar). It runs on SQL Server and PostgreSQL through EF Core, with its own migrations, in the store's database or one of its own (`Operations:Connection`). The operator's search is the console's list: by tenant, scheme, leg, direction, ISO status, outcome, hold and its reason, dates, amounts, ids, references and file names, and by any filter a pack's view declares. Not yet: the reports of the source's report database (dashboards, counts per file type, exports to CSV, Excel and PDF), and one projecting process per installation is assumed (a second would only redo work, since applying is by number; a lease is not built).
**A first load test (22 November 2026).** The owner decided a load test runs before any bank goes live. The harness is built (`runtime/load/`, R-09-38) and ran here on Temporal with its persistence on our SQL Server plugin and on PostgreSQL, and the store on SQL Server and on PostgreSQL: `docs/runtime/LOAD-TEST.md` has the conditions and the numbers, and proposes targets for the owner to set. It found two things that stop a bank going live as the runtime stands: a file of more than a few dozen lines fails on a real Temporal server (I-77), and a large file takes far too long, because its items run one after another at a fixed cost each (I-78). Issue 1 stays open until the owner sets the targets; Issue 2 is unchanged.

## I-69. The roles of earlier releases, and who authors a bank's own ports and extensions

**Issue 1.** A content branch written by an earlier release names the roles shared designer, shared checker, bank designer and bank checker in `governance/users.yaml`. Read strictly, every user there would lose every right after an upgrade, the Administrator role among them, and nobody could change the users again. The studio therefore reads each old name as the roles that held the same rights (shared designer as Workflow Designer and Workflow Developer, shared checker as Platform Approver and Administrator, bank designer as Bank Configurator, bank checker as Bank Approver), and refuses an old name in a newly proposed list.

**Recommendation 1.** On each installation, have an administrator propose the users with the new role names, and have another approve it. Once every branch in use is migrated, remove `LEGACY_ROLES` from `studio/src/governance/permissions.ts`, in a change with its requirement (R-11-29) amended.

**Issue 2.** The owner's decision forbids a Bank Configurator to create or change a port, and does not list extensions among its kinds. A bank's own hooks can need a bank's own port and extension in scope `b` (the example bank has `b.port.treasury.v1` and `b.ext.bank.v1`). No role may now author those: the developer works only in shared scopes, and the configurator may not.

**Recommendation 2.** Decide who authors a port and an extension in scope `b`: either the Bank Configurator (one row added to the table), or a Workflow Developer working for the bank, which needs a role that is both a platform role and of one bank. Until then, a bank's port and extension change only through the seed or the bank's pack folder.

**Issue 3.** The operations view (R-05-48), which declares a workflow's operator actions and their second approvers, came after the owner's decision and is not in it. The table gives it to the Workflow Designer, with the workflow it belongs to, and its version to the Platform Approver.

**Recommendation 3.** Confirm that, or name another author (the second-approver rules are a control the bank may want to approve too).

**Issue 4.** The error dictionary (R-05-51) also came after the decision. The table gives it to the Workflow Developer, who builds the activities, rules and ports whose errors it documents, and its version to the Platform Approver. A bank has no dictionary of its own in scope `b`.

**Recommendation 4.** Confirm that, and decide whether a bank documents the errors of its own hooks in a dictionary of scope `b`, authored by its Bank Configurator.

## I-70. Port bindings have no editor in the studio

**Issue.** The owner's decision lets a Bank Configurator change the port bindings for its bank, and the permission table grants it (`port-bindings`, author, scope `b`). But a port binding is a runtime document (`banks/<bank>/core-banking.yaml`, R-11-28): the studio reads past it and offers no editor and no API to change it. The table row is therefore not reachable today.

**Recommendation.** Add port bindings to the studio as a bank's artefact: a draft on the bank's change set branch, checked against its port, approved as a version by a Bank Approver, in one change with a requirement for its shape and a check. Secrets in a binding stay references (I-67).

## I-71. The command line and the deploy runner are not signed in

**Issue.** The server's API asks for a signed-in user on every call. The command line (`npm run studio -- import --apply`, `seed-content`, `deploy`) runs in the operator's own process with the operator's GitHub token, and takes its actor from `--actor` and `--bank-actor`. The permission table still decides what that actor may do, but nothing proves who typed the command.

**Recommendation.** Treat the command line as a tool of the platform's own pipeline and administrators, run only where the GitHub token is held, and say so in the deployment guide. If it must serve other users, give it the same sign-in (a device authorization flow against the studio's identity provider) and drop `--actor`.

## I-72. Sessions live in one process

**Issue.** Sessions are held in memory (R-11-24 forbids a disk). A second studio instance does not know the first one's sessions, so a load balancer must keep each browser on one instance, and a restart signs everyone out. A change to the users takes effect on the next call, but a session is not ended early when its identity is disabled in the identity provider: it lasts until it expires (8 hours by default).

**Recommendation.** For one instance, keep it. For several, either sticky sessions, or a session cookie that carries its own signed, short-lived claims (no server state) with the same signing key on every instance (`TH_SESSION_KEY_REF`). Decide the session length with the identity provider's owner, and whether the studio should check the provider's session on a short interval.

## I-73. The Azure Key Vault secret provider is not built

**Issue.** The secret interface names `akv:` references, but only `env:`, `file:` and `vault:` (HashiCorp Vault KV version 2) have providers. An Azure installation can mount Key Vault secrets as files (the Secrets Store CSI driver) and use `file:`, but cannot read Key Vault directly. A reference to `akv:` fails with a message naming this issue. The Key Vault SDK and a managed identity cannot be tested in this repository's CI without an Azure subscription.

**Recommendation.** Mount Key Vault secrets as files (`file:`) until a bank needs a direct read. Then add an `AzureKeyVaultSecretProvider` behind `ISecretProvider` (Azure.Security.KeyVault.Secrets with a managed identity), tested against a recorded fake in CI and once against a real vault before the bank's first use.

## I-74. The studio's signing key is held in its process

**Issue.** The studio signs packages with a private key it reads into its own memory, by reference, from the environment, a mounted file or Vault. A compromise of the studio's host can sign packages that every runtime trusting the key accepts, until the key is revoked in each installation's trusted signers. There is no transparency log of what was signed, so a bank cannot list every package a key signed.

**Recommendation.** For production, sign with a key that never leaves a key store: Vault transit, a cloud KMS or an HSM, all of which sign ECDSA P-256 over SHA-256, so the format and the verification do not change. Record each signature (key id, statement hash, change set) in the change set's audit on the branch, or in a transparency log (Sigstore's Rekor) where the bank may use one. Rotate the key at least yearly, as the guidelines describe.

## I-75. The native host has no authentication

**Issue.** The release API now takes only the bank's bearer JWTs (R-09-27). The native host (`TransactHub.Native`), which answers native activities for the workers over HTTP (`TH_NATIVE_URLS`), and the engine harness hosts take any caller. A caller that reaches the native host can post to core banking through the bank's binding.

**Recommendation.** Put the native host behind the same library (`TransactHub.Auth`) with a `native-caller` role the workers' client-credentials tokens carry, or bind it to the workers' private network only and say so in the deployment guide. Decide with the worker hosts of the plan (`TransactHub.Host`), which are target.

## I-76. Two gaps in publishing: memory left promoted on a refusal, and no release tag in pull-request mode

**Issue 1.** `approveChangeSet` promotes the members in memory, then builds the files to commit (`filesFor`) inside the content store's transaction. If building them throws (for example a package that carries a literal secret, which is not signed, R-06-23), the commit is not sent but the in-memory entries stay promoted until the studio reads the branch again. The missing signing key is checked before the transaction, so that case is refused cleanly; the literal-secret case and any other error in `filesFor` are not.

**Recommendation 1.** Build the change set's files before promoting anything, or restore the entries when the transaction throws, with a test that a refused publish leaves every member a draft.

**Issue 2.** In commit mode a published workflow release gets its annotated tag `release/<workflow>/<release>/<target>` on the publishing commit (R-09-30). In pull-request mode the studio commits to the change set's branch and opens a pull request; the release reaches the content branch only when someone merges it, on a commit the studio did not make (and a squash or rebase merge rewrites it, I-62). No tag is made, so a runtime's package source does not list that release.

**Recommendation 2.** When the studio sees the merge (it already reads it to mark the change set published), tag the merge commit, after checking that the release folder there is byte for byte what it committed and signed. Until then, a bank in pull-request mode installs with `POST /import-branch` from its pipeline.

## I-77. On a real Temporal server, a step's attempt timeout is spent by processing time

**Issue.** The Temporal binding keeps business time logical: it moves on a timer, a party's declared latency or an arrival, never on processing (`TemporalEngine`). But the timer the interpreter races against each attempt (its `attemptTimeout`, `PT5S` for a validate activity by the type default) is set on Temporal's own clock: it fires when the server's time passes the deadline. On a real server that time moves while the workers process, and the logical time does not. Each item of a salary file costs about 0.2 to 0.4 seconds of real time on the server (two attempts, a claim check), so once the item loop has run for five seconds every new attempt's deadline is already past and the attempt times out. On 22 November 2026, on Temporal with its persistence on SQL Server and a heavily shared machine, a file of 3 lines settled and files of 10, 20, 30 and 100 lines failed (a decision or the record checks timed out, `leg:failed`), with the default setting (`docs/runtime/LOAD-TEST.md`, the ceiling runs). The time-skipping test server and the studio's interpreter do not show it: there, processing takes no time on the clock. This stops any bank going live on Temporal.

**Recommendation.** Decide in Part 9 what an attempt timeout measures on a real engine: business time (from the logical now, as `TH_TEMPORAL_TIMERS=logical` does, `TemporalEngine.Timer`) or real time (then the logical clock must move with the server's, and the type defaults must be sized for real ports). Prove the choice with a kit scenario run on a real server that processes a file whose item loop outlasts the step's timeout, on every engine. Until then the load harness sets `TH_TEMPORAL_TIMERS=logical` and says so; the default is unchanged.

## I-78. A large file runs its items one after another, and the claim check writes one at a time

**Issue.** The WPS leg checks each line in a `forEach` with no parallelism, and each line is two activity attempts, each a Temporal activity on the scheme's task queue plus a claim-check local activity, a fact in the store and an HTTP call to a Node worker. Measured on 22 November 2026 (`docs/runtime/LOAD-TEST.md`), a 1,000-line file takes several minutes and its lines go at a few per second; a 10,000-line file would take the better part of an hour. Two host-level limits add to it: `DurableClaimCheck` writes every fact of every payment in a host behind one lock, and each deployed worker is one Node process.

**Recommendation.** Decide, with the owner's targets (I-68): (1) a bounded parallel `forEach` for item-level checks that hold no state (Part 5), or one activity per chunk of items, so a file's cost grows with its chunks, not its lines; (2) a lock per payload in the claim check, not per host; (3) more than one worker process per queue. Re-run the load matrix after each.

## I-79. A deployed worker shares one clock and one run across the attempts in flight

**Issue.** A deployed worker (`studio/src/worker/main.ts`) runs every attempt on one `PartyService`, which has one simulator run and one clock: `attempt` sets `run.clock.now` to the request's time, and the hold flag and the deliveries were also kept once for the service. A worker serves many attempts of many payments at once, so one attempt could take another's deliveries (a scheme's acknowledgement handed to the wrong payment) or read another's time. The deliveries and the hold flag are now kept per attempt (`PartyService.attemptOwn`, an `AsyncLocalStorage`), which the load runs needed; the clock is still shared.

**Recommendation.** Give each attempt its own clock view, or serialise the attempts of one simulator conversation. This matters only where simulators run (a test deployment); in production a port is the bank's connection. Add a test that runs two payments' attempts on one worker at once and checks each gets its own replies at its own time.

## I-80. What the telemetry does not yet do

**Issue.** (1) The facts do not carry the trace id, so an operator cannot go from a fact to its trace. (2) Span events cannot be changed in .NET once added; the masking processor takes them off and adds them back masked through `Activity`'s private `_events` field. A test fails if that field moves (`TelemetryTests`), but it is a dependency on the runtime's internals. (3) A name in free text has no pattern: the masking finds IBANs, emails and long numbers, and replaces values under personal keys, but a name written into a message by a library would pass. (4) No alert rules, service-level objectives or retention are written, and the dashboards are for the local stack only. (5) The deployed worker bundles OpenTelemetry, which makes `worker.cjs` about 1.2 MB.

**Recommendation.** (1) Add the trace id to the payload's `create` fact as a correlation reference, outside the hashed business data, in the store's next migration. (2) Ask OpenTelemetry .NET for a way to filter events in a processor, and drop the private field when there is one. (3) Keep the rule that hosts write ids only, and add a scan of the collector's output for the party values in the load harness. (4) Write the alerts with the targets of I-68. (5) Accept it, or load OpenTelemetry in the worker only when an endpoint is set.

## I-85. ADIB's, ENBD's and FAB's core-banking settings are not in the source

**Issue.** By the owner's decision of 2026-11-22, ADIB, ENBD and FAB each have a bank folder in `content-configurations/banks/`, set up as DIB's is. DIB's core-banking binding carries thirteen settings from the source (`CoreBanking:*` in its compose overlay). The three other banks' forks read no `CoreBanking:*` setting: they call core banking through a named HTTP client, and every route they call is the bank simulator's (`/api/simulator/...`). So the source holds no service code, host id, branch, flags, paths or function ids for them. Carrying DIB's values or the simulator's routes would present them as the bank's.

**Recommendation.** Ask each bank for its core-banking gateway settings and add them as a new binding of `p.port.core-banking.v1`, with a later `validFrom`, in its `core-banking.yaml`. If a bank's core banking is not the gateway's shape, write a new adapter for it in `TransactHub.Ports` and bind that.

**What is built.** Each folder binds `p.port.core-banking.v1` to `gateway-rest` with a `.invalid` host and `secretRef: env:TH_<BANK>_CORE_BANKING_TOKEN`, and nothing else, so the adapter refuses every call ("has no '...'") and no payment of the bank reaches a simulator or a guessed host (`PlatformOnlyBankFolderTests`). Each README lists the settings a deployment must add.

## I-86. Whether ADIB, ENBD and FAB run WPS

**Issue.** The factory has one scheme, WPS. The source contradicts itself on ADIB: its customer overlay licenses IPI and FTS only (`LICENSE_RAILS=ipi,fts`), and its bank worker is built without its WPS plugin; but the base database seed subscribes the `adib` tenant to WPS, two generic WPS workflows call ADIB's WPS steps (one pins them to a queue `wps-adib` that no worker serves), and Smart Integration's WPS templates carry `tenantId: ADIB`. ENBD and FAB have no deployment at all in the source: no overlay, no licence, no WPS parameter (no routing code, CBX value, acknowledgement wait, accounting code, SFTP layout or key). The participant directory lists all three as WPS participants, which is what the scheme knows of them, not what they run here.

**Recommendation.** Ask the owner, and then each bank, whether it runs WPS on this platform. For one that does, collect its WPS values as DIB's were (routing code, CBX values, acknowledgement wait, accounting codes, Central Bank SFTP layout, decryption profile), set the rail (`p.table.rail-choice.v1#enabledRails`), and add them to its folder with a `validFrom`. Until then each folder configures the platform only.

**What is built.** Each folder sets `enabledRails` to the empty list, from 2026-11-22, and no WPS value. BANK-RAIL-001 shows a payment that asks for WPS finds no rail, and `studio/test/platform-bank-packs.test.ts` shows a salary file is refused at routing, before anything is sent.

## I-87. No port encrypts a file

**Issue.** The factory has `p.port.decryption.v1` for files a bank receives, and no port for a file a bank encrypts before it sends it. The source's encryption library has encrypt-only profiles for WPS files (its sample settings give FAB `WPS_SalaryUpload` and `WPS_SIFUpload`, GnuPG, keys by name), so a bank that encrypts its SIF to the Central Bank has nowhere to bind that.

**Recommendation.** Decide whether a file a bank sends is encrypted by the file-transfer adapter (a setting of `p.port.file-transfer.v1`'s binding: profile and key by reference) or by its own port and activity in `p.*`, in one change with a requirement id and a check, together with I-67.

**What is built.** Nothing. FAB's README lists the sample's profiles as not carried.

## I-88. The employer's labour ministry status has no rule

**Issue.** Each bank fork in the source has the same WPS rule step (`{Bank}ValidateWpsRules`): a file needs at least one record, its total must be within the balance, and an employer the labour ministry has suspended is refused. The plan makes the copies one `uae.wps` rule (W6). The factory has the first two (`uae.wps.rule.record-count-matches.v1`, the funds check of `p.reserve.funds.v1`) and nothing for the third: no rule, and no registered field or port that gives the employer's status at the ministry.

**Recommendation.** Add a `uae.wps` rule that refuses a salary file whose employer the ministry has suspended, with the status as a registered extension read by an activity (a lookup behind a port), and its reason code from the scheme's code set once confirmed (`uae.wps` is provisional). Do not add it as a bank rule: it is the same for every bank.

**What is built.** Nothing in the factory. ADIB's, ENBD's and FAB's READMEs list the step as not carried; their `rules/` are empty.

## I-89. The Flowable binding keeps a run's state in memory

**Issue.** In production mode (I-53) Flowable keeps the flow, the jobs, their locks and retries, and the store keeps the payloads. The binding still keeps in memory what it learnt during the run: the business trace and detail, the attempts, held early messages, the messages each instance has waited for (R-05-43), the hook bindings taken at start and the timers it has placed. A job whose worker crashed comes back from Flowable when its lock expires, and the tests show that, but if the binding's process itself stops, a new one cannot pick up the payments the old one was running: it does not know them. The harness runs one plan and ends, as the other engines' harnesses do.

**Recommendation.** Before a payment depends on the binding in production, keep that state on the payment's fact chain in the store (the trace and detail as facts, held messages in the inbox, the hook bindings with the instance), and let a binding that starts read the server's running instances of its tenant and rebuild its view from the store. Add a test that kills the binding mid-payment and starts another.

## Not done, and why

| Story | Why |
| --- | --- |
| C25-S1 Confirm the pack against the rulebook | Needs the current CBUAE WPS rulebook (D10). Nothing here confirms a scheme fact. |
| C25-S6 Planned message groups | Each needs a business decision to build, defer or drop. |
| C25-S5 Refund | Needs the refund order layout, which the pack marks unconfirmed; writing it would invent scheme facts. |
| C24-S1 to S5 Scripts and tests for every platform block | The reference behaviour runs every block; six blocks have scripts. Writing a test per outcome for every block is factory content for the generator, not yet done. |

## I-97. The store holds no end of a payment and no files of a deployment

**Issue.** The operations read model shows what the store's facts say (R-05-49). Two things a WPS operator saw are not facts yet. (1) A payment's end: the outcome the engine reaches (`end:settled`, `end:rejected`) is the engine's, not the store's; the read model shows the ISO status the workflow state last carried and each leg's last outcome, but no end and no duration. (2) Files: the inbound SIF, the SIF sent to WPS and the ACK or NAK are not kept in the store's `message` table on a deployment (the native host keeps the rendered SIF in its own memory, `GET /files`), so the views' file names and the documents tab stay empty, and the old Raw File downloads have nothing to serve.

**Recommendation.** Have each engine binding record an `end` fact on the payment's chain when its root instance ends (outcome, release, time): Temporal's front door has the `Completed` signal, Flowable the process end, DTFx and Elsa their harness's end. Have the front door keep each inbound message, and each exchange activity each file it sends or receives, with `PaymentStore.KeepMessage` and the payment's id. Both are then on the outbox and reach the console with no change to it.

**What is built.** The views name the sources (`message:uae.wps.sif.fileName`, `documents`), the read model projects every `message` row and the API serves its content to the payment's operators; `OperatorApiTests` shows a kept file listed and downloaded.

## I-98. A hold answered by the runtime waits in one attempt

**Issue.** A hold reaches the operator through its port, `p.port.operations-queue.v1`, given to the runtime (R-08-15): the native host opens the hold on the payment's chain and the attempt waits for the decision the console records (R-05-50). An attempt holds a connection while it waits, and is bounded (`TH_OPS_HOLD_WAIT_SECONDS`, default 240, under the worker's five minutes): a hold not decided by then ends `expired`, not after the hold's own `expiry` (P1D). The hold also finds its payment by the header's file id or end-to-end id, the newest of that tenant and scheme, because the header carries no id of the store's.

**Recommendation.** Make a hold durable on every engine: the expanded hold block sends the item and awaits `p.ops.decision` (as the studio's reference already does), and the console's decision is delivered to the engine as that message, through the engine host's message path (Temporal's front door `Deliver`, Flowable's message event, DTFx's raised event, Elsa's bookmark). Put the store's payment id on the header when the payment is accepted, and have the hold name it.

**What is built.** The bench path, on all four engines alike since it is an activity's reply: `StoreHold` in `TransactHub.Operations`, the native host serving it when the store is configured, and the end-to-end test (`runtime/ops-console/test/e2e/wps-hold.test.ts`).

## I-99. Signals, retry and resend have no path from the runtime to an engine

**Issue.** Operator actions that are signals (`cancel`, `re-screen`, `pause`, `resume`, R-05-12) are recorded and approved as facts, and then put on the outbox (topic `engine-signal`) for the engine's host to deliver; no engine host reads that topic yet, so a running payment is not cancelled. The engines' signal paths exist (Temporal's workflow signal, Flowable's `th.signal.*` messages), but a deployment's instance ids are the engine host's, and the store does not record them. `retry` (an activity that ended in an error the policy did not retry) and `resend` (a file sent again) are not in the DSL at all: no construct takes them.

**Recommendation.** Record each instance's engine id as a fact when it starts (the binding knows it), and give each engine host a reader of the `engine-signal` topic that delivers the signal on the engine's own path and records the result as a fact. Decide in Part 5 whether `retry` and `resend` are operator actions: if they are, a composite's subflow gains a declared retry point and an exchange a declared resend, each with a conformance scenario; until then the views may declare them and the runtime records and refuses them.

**What is built.** `IEngineSignals` with `OutboxSignals`, maker-checker before a signal is sent (`OperatorActionTests`), and the refusal of `retry` and `resend` with this issue's number.

## I-100. WPS fields of the source's screens with no source in the new model

**Issue.** `docs/runtime/ops-console-wps-mapping.md` maps every field of the source's WPS operations screens to the operations views. These have no source in the new model: the uploading user, the initiating user and the channel (the channel's upload is not in the payment model, and the user is personal data); the employer's name and debit account, which the channel supplied and the SIF does not carry; the six allowances and the leave encashment of a salary line (`uae.wps.ext.salary.v1` registers fixed and variable pay only); the labour ministry's update status (I-88); the employee account's type and bank name (the participant lookup `uae.p.enrich.participant.v1` is reached by no workflow); the NAK's per-line DER records (the ACK map writes the status only); the posting's charge, VAT and total, and its legs (the core-banking adapter keeps them, the posting facts carry its outcome); the RRI, RRR and DCR refund flows (no factory flow); and the dashboard's aggregates. The source also let a privileged user unmask a masked field on screen; the read model keeps only the masked value.

**Recommendation.** Register the allowances and the leave encashment as item fields of `uae.wps.ext.salary.v1` (provisional until the rulebook confirms them) and map them in `uae.wps.map.sif-in.v1`; carry the channel, the employer's name and the debit account from the channel's upload into the bulk at intake; write the NAK's DER records and the posting's charge, VAT and legs as step writes (state, with masked accounts); and add the reports as projections of their own (I-68). Decide whether an unmask is needed at all: if it is, it reads the store, needs a permission of its own, and is a fact on the payment's chain.

**What is built.** Each of these rows is in the mapping as not mapped, with this issue's number; every mapped row is checked against the views (`OperationsViewTests`).

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

## I-102. Distributions, configuration releases and the open payments of a release

**Issue.** (1) The service console lists every kind the package source offers (R-09-40), but installs only workflow packages: a distribution (`dist/<bank>/<id>`) or a configuration release has no runtime model yet (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, section 10, items 1 and 2), so installing one answers 501 with this issue. (2) A release's state (installed, active, draining, drained, removed) is derived from the activations and the console's facts: the store has no state column and no `removed` table, and the five states of the packaging document are not enforced by the engines (a removed release's workers keep running). (3) The open payments of a release are those whose ISO status in the read model is not final (ACSC, ACCC, RJCT, CANC), because the store holds no end of a payment (I-97). A payment records the release id the engine gave it; a deployment gives the workflow's name, so such a payment is counted for that workflow's release active when it was accepted, by the activation rows, which is an inference.

**Recommendation.** Build items 1 and 2 of section 10 of the packaging document: store distributions and configuration releases insert-only, pin `distributionId` on every payment at acceptance, record each state change as an activation row, and have each engine host stop a removed distribution's workers. Record an `end` fact per payment (I-97) and count open payments from it.

**What is built.** Listing of every kind, verification, the difference against what is active (flows, files and parameters), install, activation from a date with the previous release recorded as draining, rollback, removal refused while active or while a pinned payment is open, and the open count per release (`ContentTests`, the `service-console` Playwright test).

## I-103. What the runtime's errors and logs do not carry yet

**Issue.** (1) The runtime's libraries raise exceptions with a message only: `SignatureRejected`, `PackageRejected`, `ActionRefused`, `ReleaseConflict` carry no error code, so the service console classifies them by their words (`ErrorCodes.Of`), which breaks if a message is reworded. (2) A step fact records a reason code and an error name when the worker gives them, but an engine's own failure (a Temporal workflow task failure, a Flowable dead-letter job, a DTFx or Elsa fault) is not a fact: the console sees it only through the engine's own console. (3) The service console's log viewer reads its own host's structured logs (masked, in memory) and the store's facts; the other hosts' logs go to the installation's log back end over OTLP, which the console does not query, and the facts carry no trace id (I-80).

**Recommendation.** (1) Give each refusal type a `Code` property set where it is thrown, from the dictionary's codes, and have `ErrorCodes.Of` read it; keep the words as the message only. (2) Have each engine binding record an `engine.failed` fact with the dictionary's code when an instance fails or a job is dead-lettered. (3) Add a read-only log source to the service console for the installation's back end (a Loki or OpenSearch query by trace id and time), configured like a probe, and add the trace id to the facts (I-80).

**What is built.** `ErrorCodes`, the classification and its test, the masked ring buffer of the service host's logs, and the viewer over the store's facts with filters by error code, leg, scheme, tenant, payment and time, each error linked to its runbook (`ServiceApiTests`).

## I-104. The assistant sends masked data to the model provider

**Issue.** The service console's assistant (R-09-44) sends the question, the selected payment's masked trace and facts, masked logs, dictionary entries and document passages to the Anthropic API with the studio's key (`TH_ANTHROPIC_API_KEY`). Masking replaces every value the store holds for the parties the engineer may see (names, accounts, IBANs, the extension fields that name a person or employer) and the telemetry's patterns; a test proves no party value leaves (`AssistantTests`). It cannot mask what the store does not know: a name a person types into a free-text remark, or one inside a scheme's file kept as a message (which the assistant is never given). Whether a bank may send even masked operational data to a model provider is the bank's decision, not the runtime's. The assistant is off unless `TH_ASSISTANT_ENABLED=true`.

**Recommendation.** Ask each bank's data protection officer to approve the assistant before it is switched on, with the list of what is sent (Part 9, R-09-44) and the provider's retention terms; record the decision in the bank's configuration release. Keep it off by default. The free-text fields of operator and service facts (`remarks`, `why`, `reason`) are not sent at all; keep it so.

**What is built.** The switch, the masking and its test, read-only tools, citations, proposals that a person confirms on the Actions page, and a fake model for the tests: no test calls the API.

## Not done, and why

