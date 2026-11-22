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

## I-42. Who keeps the SQL Server plugin for Temporal, and how an upgrade is proved

**Issue.** D2 is confirmed and the plugin is built (`runtime/temporal-mssql`, W5). It is pinned to Temporal v1.32.2. Temporal does not support it, and Temporal releases often: the persistence schema is at its 20th version, and a release can change a store method or add a table. The plan says to re-prove the plugin on each upgrade, but it does not name an owner, say how soon after a Temporal release the runtime must follow, or say what blocks an upgrade.

**Recommendation.** Name an owner for `runtime/temporal-mssql`. Make a Temporal upgrade one change that moves the pin, ports any new schema version and store method from the PostgreSQL plugin, and passes the CI job `temporal-sqlserver` (persistence suites, durability check, kit, WPS). Say in the plan that the runtime stays on the pinned release until that job is green, and that a security release of Temporal is the only reason to hurry.

**What is built.** The pin is exact in `go.mod`, and the README says each upgrade must re-run the proof. The CI job runs the whole proof on every push to `runtime/**`.

## I-43. Where a bank pack may live

**Issue.** The owner decided on 2026-11-21 that a real bank's configuration lives in this repository, in `content-configurations/banks/<bank>/` (D5 as amended in `docs/runtime/NSTAR-CONVERGENCE-PLAN.md` section 8). The spec knows one bank pack only, the example bank in `spec/examples/bank/`. It says nothing of a bank pack anywhere else: which folders hold one, how the studio finds its tenant, or which documents in a bank's folder are studio artefacts and which are the runtime's own (port bindings, configuration values). `tools/validate.py` reads `spec/` only, so it never sees a bank's folder.

**Recommendation.** Add a requirement to Part 6 (or Part 11): a bank pack MAY live in `content-configurations/banks/<bank>/`, with a `pack.yaml` of `packType: bank` that names its tenant; every studio artefact in the folder is that tenant's; the documents of kind `port-bindings` and `configuration-values` are the runtime's and are not pack artefacts. Its check: the studio loads such a folder and refuses a document that names another tenant, and the pack's lock file equals the one the studio generates. Give it an id, a line in the check catalogue of `design/05-workflow-dsl.md` and a mutation case, as `spec/CLAUDE.md` requires.

**What the studio does.** It loads a bank pack from its own folder on request, never by default: `openEnv({ banks })`, `TH_BANKS`, or `--bank <name|dir>` on any command. (Since I-44 the folder is read from the content branch and named by its folder name; the example bank's pack is seeded there and always loaded.) `Store.loadBankPack` takes the tenant from `pack.yaml`, refuses a document that names another tenant, and leaves the runtime's formats out. The pack is then checked as the example bank is (schemas, bounds, hook contracts, bindings, pins), its tests run on the interpreter, and `npm run studio -- bank-pack <bank> --write` writes its lock file. `studio/test/bank-packs.test.ts` shows it on the first such bank.

**Found, not fixed.** The example bank's lock file (`spec/examples/bank/pack.yaml`, written by `tools/build_factory.py`) depends on `p`, `uae.p` and `uae.wps`; the studio's `packManifest` derives `p` only, because it skips the references of tests. `compareManifest(env, 'b')` has reported that difference since before this change.

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

## I-57. A wait a bank sets is read differently on each engine

**Issue.** The acknowledgement wait of the salary file is now the bank's parameter (`uae.wps.exchange.submit-salary-file.v1#ackWait` and `#ackWaitCalendar`, R-05-43). The expanded await names the parameter and keeps the policy's value as its default. The studio's interpreter looks the value up on the day the await starts, as it does any parameter. DTFx, Elsa and Temporal read it from the configuration values the deployment hands the engine (`config.values`), on the engine's day: a value published after a payment started reaches its await only if the deployment carries it. Flowable's BPMN timer holds the default; the portability report says so. The spec is silent on how an engine's orchestration reads a dated value, since until now only activities did.

**Recommendation.** Decide that an engine reads a dated value for a timer through an activity of its own (a lookup, retried and recorded like any read), so the value comes from the runtime's store as of the step, on every engine. Generate Flowable's timer from a process variable that lookup sets.

**What is built.** The interpreter, the shared C# interpreter (DTFx, Elsa, Temporal) and both expanders. Lint L30 checks that the parameter is declared, typed and defaults to the policy. `studio/test/ack-wait.test.ts` shows the late acknowledgement waited for with the bank's 72 elapsed hours and not with the default.

## I-58. A bank's accounts are not in the source

**Issue.** The accounting template now has parameters for the settlement, suspense, charge and VAT accounts (R-12-12). For DIB the source holds only code fallbacks (`999000000000001` to `...005`, `DibWpsActivities.Independent.cs`), not the bank's accounts, and it seeds no charge slab. Carrying the fallbacks would present invented accounts as the bank's.

**Recommendation.** Ask the bank for its WPS settlement (nostro), suspense, charge income and VAT accounts and its charge slabs, and set them in `content-configurations/banks/dib/configuration-set.yaml` with a validFrom. Until then a DIB posting uses the factory's placeholder accounts, which no core banking knows, so a deployment for the bank must set them.

**What is built.** DIB's transaction codes, charge narration, account kinds, VAT and acknowledgement wait are set; its accounts and slabs are not, and its README says so.

## Not done, and why

| Story | Why |
| --- | --- |
| C25-S1 Confirm the pack against the rulebook | Needs the current CBUAE WPS rulebook (D10). Nothing here confirms a scheme fact. |
| C25-S6 Planned message groups | Each needs a business decision to build, defer or drop. |
| C25-S5 Refund | Needs the refund order layout, which the pack marks unconfirmed; writing it would invent scheme facts. |
| C24-S1 to S5 Scripts and tests for every platform block | The reference behaviour runs every block; six blocks have scripts. Writing a test per outcome for every block is factory content for the generator, not yet done. |
