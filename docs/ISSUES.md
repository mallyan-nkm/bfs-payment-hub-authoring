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

## Not done, and why

| Story | Why |
| --- | --- |
| C25-S1, C26-S1 Confirm the packs against the rulebooks | Needs the current CBUAE WPS rulebook and the AEP participant specification (D10). Nothing here confirms a scheme fact. |
| C25-S6, C26-S5 Planned message groups | Each needs a business decision to build, defer or drop. |
| C25-S5 Refund, C26-S3 refuse test, C26-S4 proxy lookup test | Need message layouts the packs mark unconfirmed (refund order, proxy reply); writing them would invent scheme facts. |
| C24-S1 to S5 Scripts and tests for every platform block | The reference behaviour runs every block; six blocks have scripts. Writing a test per outcome for every block is factory content for the generator, not yet done. |
