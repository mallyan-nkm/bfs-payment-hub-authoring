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
