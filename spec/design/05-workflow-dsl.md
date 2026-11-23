# Part 5. Workflow DSL

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 5.1 Purpose

The DSL is the single definition of a workflow. It is block-structured and engine-neutral. It evolves today's JSON definition and borrows construct names from the CNCF Serverless Workflow DSL where they fit.

## 5.2 Kinds

| Kind | Holds | Durable |
| --- | --- | --- |
| `platform` | The lifecycle for a family of payments, with one or more slots. Any number may exist. | Yes |
| `leg` | The part unique to one scheme and one direction. Fills a slot. Runs as a child workflow. | Yes |
| `segment` | A reusable run of steps, inlined at publish. | Not on its own |
| `handler` | A stateless request and response. No await, timer, hold or write. | No |
| `subflow` | The steps behind one composite activity. Returns an outcome to the step that called it. Expanded in place. | Not on its own |

## 5.3 Constructs (the portable core)

| Step | Form | Meaning |
| --- | --- | --- |
| Call | `call: <activity>` with optional `id`, `level`, `policy`, `handle` | Run one activity |
| Handle | `handle: { <outcome>: <action> }` | What to do on each non-primary outcome |
| Action | steps, or `{ end: <outcome> }`, or `{ continue: true }` | Run steps, end the workflow, or record and carry on |
| Switch | `switch: { by: { outcomeOf \| decision }, cases, default }` | Branch on a named outcome or decision |
| Parallel | `parallel: { branches, join: all }` | Run branches together; wait for all |
| For each | `forEach: { in, do, concurrency, onItemFailure }` | Run steps per batch item |
| Loop | `loop: { max, every, until, do }` | A bounded loop, for business retry |
| Await | `await: { message, correlateBy, family, timeout, calendar, onTimeout }` | Subflows only: wait for a message, with a timeout. `family` takes only a message of that family (R-05-44) |
| Wait | `wait: <duration>`, with an optional `id` | A timer. In a subflow its duration MAY be a parameter of the composite, `{ parameter: <name> }` (R-05-45); so MAY an await's timeout |
| Slot | `slot: <name>` with `handle` | Run the leg that fills the slot |
| Use | `use: <segment>` | Inline a segment |
| Try | `try: [...]` with `catch` and `compensate: all` | Scope for errors and compensation |
| Hook | `hook: <hook point>` | Run the tenant's hook, if one is bound |
| End | `end: <outcome>` with optional `compensate: all` | End with an exit outcome |
| Return | `return: <outcome>` | Subflows only: give the composite activity its outcome |
| Block | `block: { of, by, do }` with `id` and `handle` | Expanded form only: one composite step with the steps of its subflow inside |

Top-level blocks: `entry`, `exit`, `slots`, `hookPoints`, `signals`, `parameters`, `deadline`, `finally`, `synchronous`, `artefacts`, `do`.

### Sending and waiting for a reply

An activity never sits waiting for a reply that comes later, and the designer never models the wait. A composite activity does both jobs (section 5.6): it is one block on her canvas, and underneath it takes one of four forms.

| The reply comes | Underneath the block | Example |
| --- | --- | --- |
| On the same connection, inside the attempt timeout | One `exchange` activity. Its outcome is the reply | `uae.wps.exchange.send-salary-file.v1`, inside the WPS submit composite |
| Later, as its own message | A send step, then an `await` on that message, then a map that reads it | `uae.wps.sub.submit-salary-file.v1` |
| Only when asked | A `loop` with `every` around an inquiry activity | Kit scenario CK-UNKNOWN-01. No factory scheme has one (Issue I-38 in `docs/ISSUES.md`) |
| From a person | A `hold` block, which was always one block | `p.hold.repair.v1` |

The reply reaches the workflow through the front door, not through the activity that sent the request. The scheme's dispatch table marks the message `deliver` and names its correlation key; the front door finds the instance and hands it the message. On an engine that hand-over is a signal or an external event (Part 9). In this DSL the word signal is kept for the four operator controls.

What stays in the designer's hands: the business outcomes of the block, and how long it waits (`policy.replyTimeout` on her step). `wait`, `loop` and `hold` remain her constructs. Only `await` moves out of her view.

## 5.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-05-01 | A workflow MUST validate against `workflow.schema.json`. | L00 |
| R-05-02 | The DSL MUST NOT use a key that YAML 1.1 reads as a boolean (`on`, `off`, `yes`, `no`). Outcome handling is `handle`; a switch selects `by`. | Schema (L00) |
| R-05-03 | Flow MUST be block-structured. There is no jump. | Schema (L00) |
| R-05-04 | Every non-primary outcome of a called activity MUST have an entry in `handle`, and `handle` MUST name only outcomes the activity returns. | Lint L05 |
| R-05-05 | A branch MUST read a named outcome or a decision. | Schema (L00) |
| R-05-06 | A platform workflow MUST declare its slots. A slot is a contract: reads, writes, outcomes and the primary outcome. | Schema (L00) |
| R-05-07 | A leg MUST name the slot it fills and the platform workflows it may run in. Its exit outcomes MUST be a subset of the slot's. | Lint L08 |
| R-05-08 | A leg is chosen by the dispatch table for the message that started the payment, or by the route decision for a payment order. | Conformance CK-SLOT-01 |
| R-05-09 | A hook step MUST name a hook point declared in the workflow or in a segment it uses. | Lint L06 |
| R-05-10 | A hook point is part of the interface. Adding one is a minor; changing or removing one is a major. | Lint L26 |
| R-05-11 | A handler MUST contain only call, switch and end steps, and MUST call only pure or read activities and exchange activities in `respond` mode. | Lint L07 |
| R-05-12 | A workflow MAY handle only the fixed signals: `pause`, `resume`, `cancel`, `re-screen`. | Schema (L00) |
| R-05-13 | `cancel` MUST run compensation for completed steps in reverse order, then `finally`. | Conformance CK-SIGNAL-02 |
| R-05-14 | `finally` MUST run on every end, whatever the outcome, after any compensation. | Conformance CK-FINALLY-01 |
| R-05-15 | `compensate: all` MUST run the compensation of every completed write step in the enclosing try, in reverse order. | Conformance CK-COMP-01 |
| R-05-16 | A step's effective policy is resolved type default, then activity, then step. The studio shows where each value came from. | Conformance CK-POLICY-01 |
| R-05-17 | Retry MUST apply only to activities that are pure, read, or carry an idempotency key. | Lint L20 |
| R-05-18 | The sum of attempt budgets on the longest path of a leg MUST fit inside its deadline, where one is declared. | Lint L21 |
| R-05-19 | A long wait MUST be an `await` or a `wait`, never an activity timeout. `calendar: business` counts business days. | Review; conformance CK-AWAIT-02 |
| R-05-20 | A deadline MUST be realised as a timer with a handler, never as an engine execution timeout. | Conformance CK-DEADLINE-01 |
| R-05-21 | An awaited message MUST be in the scheme's catalogue with a `deliver` disposition. | Lint L16 |
| R-05-22 | Every message in a scheme's catalogue MUST have exactly one disposition. A pack with a `planned` message MUST NOT publish. | Schema `scheme` (L00); lint L10 |
| R-05-23 | Every path a step reads MUST be written by an earlier step, by intake, or be in `entry.reads`. | Lint L19 |
| R-05-24 | Step ids MUST be unique within a workflow after segments are inlined. | Lint L29 |
| R-05-25 | Rule evaluation and lookups MUST run in activities, never inline, so that flow is deterministic. | Schema (L00); conformance kit |
| R-05-26 | A construct outside the portable core MUST be flagged per target engine and MUST NOT appear in `p` scope. | Lint L24 |
| R-05-27 | An exported workflow MUST carry its own release, the DSL version and the `artefacts` pins. | Lint L23 |
| R-05-28 | A call with `level: item` inside a `forEach` over `items[]` binds `payment` to the payment of the current item, so a payment-level block is reused per item without change. | Conformance CK-FOREACH-01 |
| R-05-29 | A message with a `deliver` disposition MUST be awaited by at least one workflow of its scheme. | Lint L10 |
| R-05-30 | A message delivered to an instance before the matching `await` starts MUST be kept and consumed by that `await`. | Conformance CK-AWAIT-04 |
| R-05-31 | A `deliver` message that finds no instance waiting for it, because none exists, the instance has ended or the `await` has timed out, MUST be recorded against the payment and handed to operations as unmatched. It MUST NOT start a workflow, resume one, or be dropped. | Conformance CK-AWAIT-03 |
| R-05-32 | A second copy of a message that an `await` has already consumed MUST be recorded and ignored. | Conformance CK-AWAIT-05 |
| R-05-33 | A platform workflow, leg, segment or handler MUST NOT contain an `await`. A wait for a message lives in the subflow of a composite activity. | Lint L31 |
| R-05-34 | A subflow MUST implement exactly one composite activity, MUST return only outcomes that activity declares, and MUST NOT contain `end`, `slot`, `hook` or `use`. | Lint L30 |
| R-05-35 | Expansion MUST be a pure function of the authored workflow and its pinned artefacts: the same input gives the same bytes. The expanded form MUST NOT be edited, and MUST NOT be stored as authored. | `tools/expand.py --check`; lint L31 |
| R-05-36 | The studio interpreter and every engine binding MUST run the expanded form, and MUST record the authored release and hash it came from. | Conformance CK-BLOCK-01 |
| R-05-37 | An `await` with no timeout MUST take the reply time of the calling step: the step's policy, else the activity's. | Lint L32; conformance CK-BLOCK-03 |
| R-05-38 | A run MUST yield two traces: the business trace of authored step ids, in which a composite step is one entry, and the detail of the steps inside each block. Tests and conformance compare both. | Conformance CK-BLOCK-01, CK-BLOCK-02 |
| R-05-39 | A static check that needs the whole flow (data flow, effects, budgets, portability, step ids) MUST run on the expanded form and MUST report its finding at the authored step, naming the inner step. | Studio test (C04) |
| R-05-40 | A step that calls a composite MUST have an id. | Lint L30 |
| R-05-41 | The pins of a workflow MUST include each composite it calls, that composite's subflow, and everything the subflow reaches. | Lint L23 |
| R-05-42 | A start message's dispatch entry MUST say what payment it starts: kind, direction and, where the platform workflow depends on it, segment. They MUST select, through workflow-select, one platform workflow whose slot the named leg fills. The front door sets them on the header from the dispatch table; an intake map does not route. | Lint L33; front door (C09) |
| R-05-43 | An `await` takes only a message received after the await began. Precisely: once an earlier await of the same instance has waited for the same message, an `await` MUST take only a message received after the composite step that holds it began (in the expanded form, its block). A message received before answered the earlier exchange: it MUST be recorded as unmatched and handed to operations (R-05-31). The first await of a message in an instance takes one kept since the instance began (R-05-30). | Conformance CK-AWAIT-06 |
| R-05-44 | A `deliver` disposition MAY declare the families the front door tells apart (`families`), read from the message: for a WPS acknowledgement, the family of the file it answers, from its name. An `await` MAY name one of them (`family`). It MUST then take only a message of that family, or one whose family the front door could not read. | Lint L36; conformance CK-AWAIT-07 |
| R-05-45 | A wait in a subflow, or an await's timeout, whose length a bank may change MUST be a `duration` parameter of the composite activity, with a default and bounds, named as `{ parameter: <name> }`. The expansion names it in full (`<activity>#<name>`) and carries its default. The interpreter and every engine MUST take the tenant's value in force on the business date the step starts, else the default. A duration value MUST lie inside its bounds by length, not by its text. | Lint L17, L14; studio test `wps-returns.test.ts`; factory test WPS-IN-004 |
| R-05-46 | An inward leg that paid some items and returned the others MUST end with its primary outcome and record the count and amount returned in `facts.returns`. It MUST end `returned` only when no item was paid and the whole amount went back. A return the scheme refuses, or one that cannot be recorded, MUST end `pending`, with the file at operations. | Factory tests WPS-IN-002 to WPS-IN-007 |
| R-05-47 | A composite activity MAY let a bank set its reply wait. `policy.replyTimeoutParameter` and `policy.replyCalendarParameter` MUST name a `duration` and a `code` parameter the composite declares, whose defaults are the policy's `replyTimeout` and `replyCalendar`. The expanded await carries them as `timeoutParameter` and `calendarParameter` (`activity#parameter`), and the interpreter and the engines MUST wait for the value in force for the tenant on the day the await starts, the timeout being its default. A step that sets `replyTimeout` itself overrides the parameter. | Lint L30; `studio/test/ack-wait.test.ts` |
| R-05-48 | A pack MAY declare how operators see its payments and legs, as an `operations-view` artefact (section 5.10): the platform's view in scope `p`, a scheme's in the scheme's scope. Every field MUST name a source the store can answer: a step the leg (or, for the platform view, a platform workflow) has, an extension field that is registered at the right level, a message of the scheme's catalogue, or a known column of the payment model. Each leg MUST be a leg of the scheme and MUST be told apart by steps it has. Columns and filters MUST name declared fields. A hold decision MUST be offered only where a flow of the view reaches that hold, and a signal only where every platform workflow the view shows handles it. | Lint L38; `OperationsViewTests` |
| R-05-49 | The operations read model MUST be a projection of the store's facts, fed from the outbox: applied in order from a checkpoint, each fact once (by its number on its chain), resumable, and rebuildable from the facts at any time with the same result. It MUST hold no personal data beyond the fields a view declares, and a field that reads personal data MUST declare its mask (`none` only where the source system's screen showed it whole, named in `oldScreen`). Its lag MUST be measured and shown. | Lint L38; `ProjectionTests` (both databases) |
| R-05-50 | A running payment MUST change only through an operator action a view declares, taken by a verified operator of the payment's tenant with the role the view names. Each request, approval and result MUST be a fact on the payment's chain with the operator's identity. An action with a `secondApprover` MUST be carried out only when another person with that role approves it (maker-checker). A hold decision MUST reach the hold as the operations-queue port's decision; a signal MUST reach the engine through its host (Issue I-99). | `OperatorActionTests`; `ops-console` Playwright test |
| R-05-51 | A pack MAY declare an `error-dictionary` artefact (section 5.11). Every error an activity declares, every reason code a rule, rule set or table gives, every reason code a port's party answers (as the port's simulators answer for it), and every code a port boundary, the interpreter and each engine binding raise MUST have an entry, raised by that kind of thing, in a dictionary of the artefact's own scope or of a scope it may depend on. The runtime's own codes (store, read model, signing and packages, the package source, authentication, the operations console, the service console and its assistant) MUST have entries in the platform's dictionary. An entry for an ISO 20022 reason code a rule or a port gives MUST name it as its `isoReasonCode`. Codes MUST be unique in a dictionary. | Lint L39; `ErrorDictionaryTests` |
| R-05-52 | Every entry MUST say whether the error is business or technical and whether it is retryable, what it means and its likely causes, and carry a runbook: the checks, the safe actions, the service console's troubleshooting actions it relies on (from the fixed list of section 5.11) and the escalation. The dictionaries MUST ship in every export package under `errors/`, and every error the service console shows MUST link to its entry. | Schema `error-dictionary`; `studio/test/error-dictionary.test.ts`; `ServiceApiTests`; `service-console` Playwright test |
| R-05-53 | A `forEach` whose items run side by side (its `concurrency`, or the binding's, R-09-45) MUST give the trace that running its items one after another gives: each item's entries together, in item order, whatever order the items end in. Where an item stops the `forEach`, no entry of a later item MAY be in the trace. | Conformance CK-FOREACH-03; CK-FOREACH-04 (an item stops the forEach at concurrency 3); CK-FOREACH-01 with the binding's concurrency |
| R-05-54 | An attempt timeout MUST measure only that attempt's own execution: from when the attempt starts to run to its answer, in business time. The engine's own processing, the time an attempt waits in a queue and the time earlier steps or items took MUST NOT count against it. A deadline, an await's timeout, a wait, a loop's interval and a retry's backoff are timers on business time, counted on the business calendar where the step says so, from the business time they are set at; never on the time the engine spends processing. | Conformance CK-TIMEOUT-03 (the Temporal adapter runs it on a real server); CK-TIMEOUT-01 |
| R-05-55 | A call step MAY declare `policy.park`, a duration: when the step fails (an error its retry policy does not retry, or retries spent with no `unknown` outcome) it MUST NOT end the workflow at once but park, record `<step>:stuck`, and wait for an operator's control or the end of the park. `retry` MUST give the parked step one more attempt; `redrive` MUST run it again with its full retry policy; each is recorded as `operator:<control>` and the step's outcome follows as usual. At the end of the park the step MUST fail as it would have. A control for an instance with no parked step MUST change nothing. A step that parks MUST call an activity that may run again (pure, read, or with an idempotency key), MUST NOT be in a handler, and parks for at least PT1M and at most P30D. | Conformance CK-PARK-01, CK-PARK-02; Lint L45 |
| R-05-56 | A hold MUST be an engine await on the operator's decision, never a wait inside an attempt: the attempt that opens the hold answers `held` with the hold's id and its expiry in force, and the instance awaits the message `p.ops.decision` until that expiry on its business clock; the decision's status is the hold's outcome, and no decision by the expiry is `expired`. A decision naming another hold MUST NOT be taken. The runtime MUST record an expiry that passed (or that a service engineer forces) as the hold's `expired` fact and send the decision `expired` to the engine. | Conformance CK-HOLD-01, CK-HOLD-02; `OperatorActionTests`; `ops-console` Playwright test (`wps-hold`) |
| R-05-57 | Every signal, hold decision and operator control the runtime records for a running payment MUST be an outbox row (topic `engine-signal`) on the payment's key, in the same save as its fact. The host of the engine that runs the payment MUST read the payment's rows in outbox order, deliver each on the engine's own path (DTFx and Elsa: a raised event; Temporal: a workflow signal; Flowable: a message event), and record it as a `signal` fact (`delivered`, or why not) on the payment's chain. A row recorded as delivered MUST NOT be delivered again, a host that restarts MUST start from the facts, and the instance MUST ignore a row it has seen. When an instance starts, its engine ids MUST be a fact (`engine started`: the engine, the instance, the run). | `OperatorActionTests` (both databases); `ActionTests`; `ops-console` Playwright test (`wps-hold`) |
| R-05-58 | The state of a payment decides which operator actions apply: a hold decision only while its hold is open; a signal (`cancel`, `re-screen`, `pause`, `resume`) only while the payment has no end; `retry` only while its last step is parked (R-05-55); `resend` only when the store keeps an outward file of the payment. `resend` MUST put the same bytes the store kept (never rendered or signed again) on the file-transfer port, idempotent by the file's id, and record a `message resent` fact. | `OperatorActionTests`; `ActionTests` |
| R-05-59 | The operations console's reports, dashboard and export MUST be built from the operations read model and the operations views alone, with no screen of a scheme's own: a report counts payments and their lines and sums their amounts by a dimension of the read model (tenant, scheme, direction, kind, status, outcome, hold, day, distribution) or by a field a view declares as an exact filter; the dashboard shows, for each scheme view, the totals of its list columns of format count or amount and the counts by its exact filters; the export is the list as CSV with the read model's columns and the views' list columns. Each MUST answer only for the tenants the caller holds the operator's role for, refuse a tenant it does not, show a personal field only as its view masks it (in the export too, and grouped by its masked value), refuse to group by a field no view declares as an exact filter, and keep a cell from being read as a spreadsheet formula (Issue I-68). | `runtime/tests/TransactHub.Operations.Tests/ReportTests.cs` (both databases); `runtime/ops-console/test/e2e/wps-hold.test.ts` ("reports the file") |
| R-05-60 | When an engine instance takes a hold's decision, the hold's step MUST leave a step fact like any other step's: its outcome (the decision, or `expired`) and its writes, with who decided (`by`) and when, on the engine's business clock (`at`). The four engines MUST record it through the one signal path (`Interp.Decided`, `StoreSignals`), so the operations views read a hold's outcome from the step whichever engine runs the payment and whoever answered the hold. | `EngineSignalsTests`; `studio/test/blackbox/e2e-refund-order.test.ts` (`approval`, `approvalBy` on Temporal and DTFx); engine kits |
| R-05-61 | An operations view's action MAY name the platform workflows it applies to (`when.workflow`); the console MUST then offer it only on a payment whose platform workflow is one of them, and a signal action MUST be handled by each workflow it names. Each workflow named MUST be one the view shows. The platform workflow of a payment is the one its step facts name (`workflow`). | Lint L38 (mutations M67, M68); `OperatorActionTests` |
| R-05-62 | The runtime MUST keep the bank's customer data in registers of declared kinds (a `register-kind` document: its key, its fields with their types and ISO 20022 homes, which are personal and how each is masked, which a record is looked up by), starting with the corporate registration (`runtime/src/TransactHub.Operations/Registers/corporate.register.json`, the source's `CorporateRegistration` on the ISO 20022 party with its organisation ids and accounts). Each record MUST be stored insert-only, as effective-dated versions in the store on SQL Server and PostgreSQL, with every version kept. Bank customer data MUST NOT be sent to the studio or to GitHub. | `RegisterTests` (insert-only, history, lookups, on both databases); migration `Registers` on both providers |
| R-05-63 | A change to a register MUST be a maker's draft that takes effect only when a different person with the bank approver's role consents (owner's decision of 23 November 2026: any action that updates the system needs the bank approver's consent). Its maker MUST NOT consent to it, a change MUST be decided once, a record with a change waiting MUST NOT take another, and a version is in force from the later of its `validFrom` and its consent. Each draft, consent and refusal MUST be a fact on the change's own chain. | `RegisterTests`; `runtime/ops-console/test/e2e/registers.test.ts` |
| R-05-64 | The operations console MUST have a registers area rendered from each kind's declaration alone: the records in force, searched, each with its full history and every personal or declared field masked as the operations views mask; a RegisterMaker proposes a change with only the fields changed; the changes waiting are listed for the bank approver, who consents or refuses. | `runtime/ops-console/test/unit/registers.test.ts`; `runtime/ops-console/test/e2e/registers.test.ts` |
| R-05-65 | The operations console MUST export payments as CSV only, with no role beyond the operator's right to read the list, and every export MUST be an audit fact on the console's own chain (`operations-console`, kind `export`): who, when, the filter as asked and the number of rows (owner's decision of 23 November 2026, Issue I-144). | `runtime/tests/TransactHub.Operations.Tests/ConsoleRecordTests.cs` (`Every_export_is_an_audit_fact_with_who_when_the_filter_and_the_row_count`) |
| R-05-66 | An operations view MAY declare the operations console's dials as parameters (`parameters`), each a duration with a default and bounds: the platform's view `tatLimit`, `unmaskWindow` and `retentionPeriod`, a scheme's view `tatLimit` and `retentionPeriod`. The console MUST read a dial as the bank's value in force by date (kind `config`, `<view>#<parameter>`, the latest configuration set that sets it), a scheme's value where the bank set one, else the platform's, else its default. No period MAY be the runtime's own: every default is P0D, which switches its mechanism off (owner's direction of 23 November 2026). | Lint L38 (mutation M80); `runtime/tests/TransactHub.Operations.Tests/ConsoleWorkTests.cs` (turnaround, unmasking and retention cases) |
| R-05-67 | An operations view MAY declare fixed reports on its subject (`reports`): one row per payment (`subject`), per line (`lines`) or per kept document of a kind the view declares (`documents`), each column a declared field, line field or document attribute. The console MUST give each as CSV only, from the read model and the views alone, masked as each field declares, for the caller's tenants, and every download MUST be an export fact (R-05-65) naming the report. The WPS view declares the source's SIF summary, SIF detail and upload history (Issue I-144). | Lint L38 (mutations M78, M79); `ConsoleWorkTests` (`The_wps_fixed_reports_are_csv_of_declared_fields_masked_and_every_export_is_an_audit_fact`); `runtime/ops-console/test/e2e/wps-hold.test.ts` ("reports the file") |
| R-05-68 | A payment MUST be in breach of its turnaround limit when it is open, or ended, later than its `tatLimit` (R-05-66) in force on the day it was accepted, after its acceptance. The console MUST list the breaches of the caller's tenants, and MUST alert each breach once: a fact on its own chain (kind `tat`, code `breached`, ids only) and a message through its notification port (`Operations:Notify:Adapter`: `log`, `simulator` or `webhook`; Issue I-182). | `ConsoleWorkTests` (`A_payment_past_its_turnaround_limit_is_listed_and_alerted_once_through_the_notification_port`) |
| R-05-69 | An operator of a payment's tenant MAY ask, with a reason, to see one field of the payment or of one of its lines that its view masks. The value MUST be shown only once a different person with the bank approver's role consents (R-09-77), only to the person who asked, and only until the bank's `unmaskWindow` from the consent has passed; P0D refuses every request. The request, the consent and each view MUST be facts on the console's chain with ids only, never the value; an erased payment MUST NOT be unmasked (Issues I-100, I-149, I-181). | `ConsoleWorkTests` (`A_masked_field_is_shown_to_the_person_who_asked_for_the_window_after_a_different_bank_approver_consents`) |
| R-05-70 | A payment MUST be due for erasure when it ended longer ago than the bank's `retentionPeriod` (R-05-66). An erasure MUST be asked for by a supervisor and carried out only when a different bank approver consents (R-09-77), and MUST be refused while the payment is open, already erased or under a legal hold, both when asked and at the consent. A legal hold is placed by a supervisor or a bank approver; its release waits for a bank approver's consent. The store being insert-only (R-09-81), an erasure MUST be a tombstone fact (`erasure`) on the payment's chain and change no fact: from it every field a view masks MUST read `[erased]` in the read model (a rebuild too), and the payment's kept documents and its unmasking MUST be refused. What this leaves in the store is Issue I-180. | `ConsoleWorkTests` (`An_ended_payment_is_due_after_the_banks_retention_period_and_erased_only_on_consent_and_never_under_a_legal_hold`, both databases) |
| R-05-71 | The operations console MUST manage work: the payments that wait for a person and the actions that wait for a second approver are a queue; an item MUST be assigned, escalated, reassigned or released only by the rules of its tenant's roles (an operator takes an unassigned item for themself; giving an item to someone else, taking it from its holder or out of an escalation is a supervisor's; escalating and reassigning need a reason), each change a fact on the item's own chain. A bulk change or a bulk operator action on a selection MUST apply each item's own rules and second approver, and record each item as its own fact. Each person's saved filters MUST be kept as facts of their own, read and removed by that person only, and the queue MUST follow the store without a reload (a long poll on the outbox, `GET /ops/api/work/changes`). | `ConsoleWorkTests` (`Work_is_assigned_escalated_reassigned_and_released_each_a_fact_and_a_bulk_change_keeps_each_items_rules`, `Saved_filters_are_each_persons_own_and_insert_only`); `runtime/ops-console/test/unit/work.test.ts`; `runtime/ops-console/test/e2e/wps-hold.test.ts` ("takes the held file in the work queue") |
| R-05-72 | The operations console MUST show on a payment the bank's cut-off as its file met it (R-12-28): the step's outcome, whether the cut-off was applied, the cut-off time and its time zone, the local time the file was accepted and the business date it counts for, as fields of the scheme's leg view (`uae.wps.view.operations.v1` 1.6.0). | `ProjectionTests` (`A_WPS_file_shows_its_declared_fields_its_leg_and_its_lines_masked_as_declared`) |

## 5.5 Messages that are not payments

| Message | Disposition | What runs |
| --- | --- | --- |
| Starts a payment | `start` with kind `payment` | A platform workflow with the named leg |
| Starts an exception or request | `start` with kind `request` or `return` | The request or inward platform workflow with the named leg |
| Belongs to a running payment | `deliver` | The leg that awaits it, found by correlation key |
| A question about a payment we hold, or a stateless service | `handler` | A handler |
| Sent by us | `sentBy` | The exchange block that sends it |
| Not yet defined | `planned` | Nothing; the pack cannot publish |

An inward platform workflow MAY declare a second slot, `scheme-refuse`, so that a leg always answers the scheme when the platform refuses a payment.

## 5.6 Composite steps: the authored form and the expanded form

A designer places one block, for example "submit the salary file", and handles its business outcomes. What the block does underneath is the subflow of that composite activity, written once by the scheme's owner. A workflow therefore has two forms.

| Form | What it is | Who works with it |
| --- | --- | --- |
| Authored | What the designer writes and the studio stores, versions and pins. A composite is one `call` step. There is no `await`. | The designer, reviewers, tests |
| Expanded | Derived from the authored form. Each composite step is a `block` holding the steps of its subflow. Only the portable core is left. | The studio interpreter, every engine binding, and the checks that need the whole flow |

Expansion does four things for each step that calls a composite. `tools/expand.py` is the reference.

1. It takes the subflow of the composite at its pinned release, expanded in its turn.
2. It prefixes each inner step id, and each `outcomeOf` that names one, with the calling step's id and a dot: `submit.await-ack`.
3. It gives each `await` that has no timeout the reply time of the calling step: the step's `policy.replyTimeout`, else the activity's. The same holds for `replyCalendar`.
4. It wraps the result in a `block` that carries the calling step's id and its `handle`. A `return` inside gives the block its outcome; `handle` then applies as on any call.

What the designer writes:

```yaml
- id: submit
  call: uae.wps.exchange.submit-salary-file.v1
  handle:
    rejected: { end: rejected }
    pending: { end: pending }
```

What an engine runs:

```yaml
- id: submit
  block:
    of: uae.wps.exchange.submit-salary-file.v1
    by: uae.wps.sub.submit-salary-file.v1
    do:
      - id: submit.send
        call: uae.wps.exchange.send-salary-file.v1
        handle:
          rejected: { return: rejected }
          unknown: { return: pending }
      - id: submit.await-ack
        await:
          message: uae.wps.ack
          correlateBy: header.fileId
          timeout: P1D            # from the reply time of the calling step
          calendar: business
          onTimeout: { return: pending }
      - id: submit.read-ack
        call: uae.wps.map.ack-in.v1
        handle:
          nak: { return: rejected }
      - return: accepted
  handle:
    rejected: { end: rejected }
    pending: { end: pending }
```

Her trace has one line, `submit:accepted`. The detail has three: `submit.send:accepted`, `submit.await-ack:received`, `submit.read-ack:ack`. The same authored step expands differently when the scheme changes how it answers: only the subflow gets a new release, and her workflow gets a new pin.

## 5.7 The check catalogue

Every check that `tools/validate.py` implements has at least one case in `tools/mutation_tests.py` that breaks the repository and expects the check to fire. A check with no mutation case MUST NOT be marked Yes.

| Check | What it proves | In `tools/validate.py` |
| --- | --- | --- |
| L00 | Schema validity | Yes |
| L02 | Scope direction | Yes |
| L03 | References resolve | Yes |
| L04 | Major in name equals major of release | Yes |
| L05 | Outcomes handled | Yes |
| L06 | Hook points declared | Yes |
| L07 | Handler restrictions | Yes |
| L08 | Slots and legs agree | Yes |
| L09 | Bank-neutral shared content | Yes |
| L10 | Dispatch coverage: a `planned` message is a warning; a `deliver` message that no workflow awaits is an error | Yes |
| L11 | Unused blocks: a block that nothing reaches says what runs it | Yes, as a warning |
| L12 | Validation sets yield findings only | Yes |
| L13 | Hook stays inside its hook point contract | Yes |
| L14 | Configuration values inside bounds (numbers and durations by min and max, a duration by its length; codes and strings by allowed and pattern; each item of a list); bindings resolve | Yes |
| L15 | Test bench matches ports and scenarios | Yes |
| L16 | Awaited messages are deliver messages | Yes |
| L17 | Parameters declared: a rule's, and a duration parameter a subflow's wait or await timeout names on its composite | Yes |
| L18 | Extension paths registered | Yes |
| L19 | Data flow: reads are written earlier | To build (C04) |
| L20 | Effects: retry needs idempotency; writes have compensation | To build (C04) |
| L21 | Budgets fit the deadline | To build (C04) |
| L22 | Every port has a simulator, and every port a test can reach is bound in its bench | The first half, yes. The second is to build (C04) |
| L23 | Pins complete | To build (C05) |
| L24 | Portability per target | To build (C04) |
| L25 | Decision tables and yield graphs: catch-all, gaps, overlaps, no cycle | To build (C06) |
| L26 | Semver matches the interface diff | To build (C05) |
| L27 | Supersession: forward within one major line; every workflow release in scope uses the artefact; evidence stays in scope; maker and checker differ | Yes |
| L28 | Script determinism | To build (C07, C08) |
| L29 | Step ids unique after inlining | To build (C04) |
| L30 | A composite and its subflow agree: binding, outcomes, policy; a reply wait a bank may set names a parameter the composite declares; a subflow has no `end`, `slot`, `hook` or `use`; a step that calls a composite has an id | Yes |
| L31 | `await` and `return` appear only in a subflow; `block` appears only in the expanded form | Yes |
| L32 | An `await` with no timeout has a reply time to take | Yes |
| L33 | A start message routes: its kind, direction and segment select one platform workflow, and its leg fills that workflow's slot | Yes |
| L34 | Factory packs: every folder under `factory/` is a pack the inventory lists, every listed pack has a folder, and every artefact sits in its own scope's folder | Yes |
| L35 | Bank packs on the branch: each folder in `content-configurations/banks/` has a `pack.yaml` that is a bank pack naming its tenant; every studio artefact there is valid, is that tenant's, is in scope `b` or a test, and refers only to the factory or the folder; its lock file lists every artefact with its kind, release, hash and path, and nothing else (R-11-28); and it names the schemes the bank licenses, each a scheme of the factory (R-11-37) | Yes |
| L36 | An await's `family` is one its message's dispatch entry declares (R-05-44) | Yes |
| L37 | No literal secret: a key that names a secret (password, passphrase, secret, token, apiKey, accessKey, privateKey, clientSecret, credential, or a name ending in one) holds a reference (`env:`, `file:`, `vault:`, `akv:`, `aws-sm:`, `aws-ssm:`) or nothing, a `secretRef` holds a reference (a store not in that list is not one), and no file is a private key; in the factory, the examples, the kit, `content-configurations/` and every export package (R-06-23) | Yes |
| L38 | An operations view says only what the store can answer: its scheme and legs exist and each leg is told apart by steps it has; each source reads a step of the flow, a registered extension field at its level, a catalogue message or a known column; a personal value declares its mask, and `none` names the old screen; columns, filters and groups are declared; a hold decision is offered only where that hold is reached, a signal only where the platform workflows handle it,, an action that names its platform workflows (`when.workflow`) names ones the view shows and is held to those; a fixed report is the subject's, lists a document kind the view declares, and each column is a declared field, line field or document attribute of its rows; and the view declares only the dials the console reads, each a duration (R-05-48, R-05-49, R-05-61, R-05-66, R-05-67) | Yes |
| L39 | The error dictionary covers every error: each error an activity declares, each reason code a rule, rule set or table gives and each a port's simulator answers, each code of a port boundary, the interpreter and every engine binding the examples bind, has an entry raised by that kind of thing, in the artefact's scope or one it may depend on; a dictionary has one entry per code; an ISO reason code a rule or a port gives names its `isoReasonCode` (R-05-51) | Yes |
| L40 | An activity that the runtime's native host implements as well (`implementation.native: true`) has a script-behind or a builtin binding (R-08-16) | Yes |
| L41 | A published release never changes: every file a seed published is in its change set's ledger with the SHA-256 of its text and still has it; a path is published once, but for a bank pack's lock file, which a later ledger replaces; an engine binding is released as `p.binding.<target>.v<major>` and never replaced (Issue I-83); every seeded file is in a ledger; a factory artefact at a release the content branch holds is what was seeded (R-11-35, R-11-36) | Yes |
| L42 | Money is exact (R-02-17, R-04-23): a rule's or table's expression does not compare a difference with a fraction between 0 and 1 (a floating-point tolerance such as `a - b < 0.01`); a number given in a rule's or table's examples, a parameter's default or bounds, a configuration value or a test case has at most 15 significant digits (a longer amount is a decimal string) | Yes |
| L43 | A leg fills the slot contract `<slot>@<major>` of a platform workflow: each workflow it names is a platform workflow with that slot, and the leg reads nothing beyond the slot's contract and its scheme's own extensions, and writes nothing beyond the slot's writes and its scheme's own extensions (one write open, Issue I-141); a distribution whose scheme does not fit is refused at build (R-11-38) | Yes |
| L44 | The parameter contract: every parameter declares its type and a default, and the default lies within its own bounds; a distribution carries every dial with its type and bounds (R-11-39) | Yes |
| L45 | A step that parks (`policy.park`, R-05-55) is a call outside a handler, to an activity that is pure, read or carries an idempotency key, and parks for at least PT1M and at most P30D | Yes |
| L46 | On a scheme whose wire is a file, a step that sends or reports a file (an exchange of mode `send` or `report` that is not a composite) comes after a protect step that encrypts it (R-12-17) | Yes |
| L47 | A leg that fills a slot of a platform workflow which blocks or posts money (calls a `reserve` or `post` activity) calls no `reserve` or `post` activity itself, directly, through a segment or through a composite's subflow: the order's money is the platform's (R-12-19) | Yes |
| L48 | A bank's port bindings (R-11-47) have the port-bindings shape, name their folder's tenant, bind each port to an adapter from a `validFrom`, bind only a published port of the factory or of the bank's pack, never bind one port twice from one `validFrom` in one document, and hold a credential as a `secretRef` only; a studio release sits at `port-bindings/<release>.yaml` | Yes |
| L49 | A reference table (R-01-25) names its key among its columns; its editions run in strictly increasing `validFrom`; every row has its key and required columns, each value of its column's type, pattern and allowed values, and no column it does not declare; a key is not repeated within an edition; a column that `references` another reference table holds that table's keys | Yes |
| L50 | A configuration value read from a reference table (`fromReference`, R-11-51) names a published reference table and only its columns, for a parameter of type list | Yes |

## 5.8 Examples

- Platform workflow with a slot, hook points and `finally`: `factory/p/workflows/p.wf.corporate.v1.yaml`.
- Inward platform workflow with a refuse slot: `factory/p/workflows/p.wf.inward.v2.yaml`.
- Leg that calls composites, as the designer writes it: `factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml`.
- Subflow with a wait for a later reply: `factory/uae.wps/workflows/uae.wps.sub.submit-salary-file.v1.yaml`.
- Subflow with two waits on a business calendar: `factory/uae.wps/workflows/uae.wps.sub.request-refund.v1.yaml`.
- Expanded forms: `expanded/`.
- Handler: none in the factory; kit scenario CK-HANDLER-01 (Issue I-38 in `docs/ISSUES.md`).
- Dispatch table: `factory/uae.wps/scheme.yaml`.

## 5.9 Open decisions touching this part

D9 (synchronous segment for instant rails; the `synchronous` marker is carried now), D17 (where handlers run).

Open point: an `await` names one catalogue message. Where a scheme answers with different files or message types, the dispatch table maps them to one catalogue message that carries a status, as `uae.wps.ack` does for ACK and NAK, and, where one message answers several exchanges, a family the await names, as `uae.wps.ack` does for the file it acknowledges (R-05-44). Waiting for the first of several different messages is not in the portable core. Add it only when a scheme needs it, with a mapping for each engine and a conformance scenario.

Open point: composites cover scheme exchanges. A bank-side port (core banking, screening) is request and reply at the interop boundary; an adapter bridges a queue within the attempt timeout. A bank-side reply that can take longer than an attempt timeout needs port-level messages and dispatch, which this draft does not define.

## 5.10 Operations views

A scheme is a leg, so the runtime's operations console is leg-generic: it lists payments and bulks by what every payment has (tenant, scheme, direction, status, the last outcome, a hold and its reason, dates, amounts, ids, file names) and shows what each leg declares, as data, in its pack's `operations-view` (R-05-48). The console has no code for any scheme. A new scheme gets its console by declaring its view.

| Part | What it declares |
| --- | --- |
| `subject` | The payment or bulk as a whole: fields in groups, the list's columns, filters, the lines of a bulk (`lines`), the documents by message kind, and actions |
| `legs[]` | Per leg of the scheme: how its engine instance is told apart (`selectBy`: direction, kind, steps only it has), its fields, columns, filters and actions |
| A field | `id`, `label`, `group`, `source`, `format`, `mask`, and `oldScreen`: where the source system's screen showed it |
| An action | One of `approve`, `decline` (an approval hold), `release`, `reject` (a repair hold), `cancel`, `re-screen`, `pause`, `resume` (the fixed signals, R-05-12), `retry` (a parked step, R-05-55), `resend` (a kept file, R-05-58); the `role` it needs, a `secondApprover` for maker-checker, whether `remarks` are required, and `when` it applies: an open hold of a kind, the payment's status, or the platform workflows it applies to (`workflow`, R-05-61). The payment's state narrows it further (R-05-58) |

A source is one of:

| Source | Reads |
| --- | --- |
| `subject.<x>`, `bulk.<x>`, `payment.<x>` | The payment model's columns (ids, amounts, accounts, agents, dates, the release) |
| `ext.<scope>.<field>`, `line.ext.<scope>.<field>` | A registered extension field (SupplementaryData), at batch or item level |
| `step:<step>[.<inner step>].<outcome \| reasonCode \| at \| attempt \| status.x \| writes.x>` | The latest fact the step recorded: the authored step id, and the step inside a composite's subflow |
| `header.<x>`, `hold.<x>` | The workflow header's ids; the payment's latest hold (status, reason, decision, who decided and when) |
| `message:<kind>.<fileName \| at \| size \| count>` | A file or message of the scheme's catalogue, as kept |
| `lines.count`, `lines.count(<line source> = \| != <value or source>)`, `lines.sum(<line source>)`, `line.<x>`, `line.facts.<x>` | A bulk's lines and their own facts |

The read model is a projection of the store's facts (R-05-49). Every step a worker answers is a fact on the payment's chain (kind `step`: the step, the activity, the attempt, the outcome and reason code, the header's ids and the step's state writes, never the payload); a hold is opened and decided on the same chain (kind `hold`), and every operator action too (kind `operator`). The projection reads them through the outbox, which the store writes in the same save as each fact. The views of the factory are `factory/p/views/p.view.operations.v1.yaml` and `factory/uae.wps/views/uae.wps.view.operations.v1.yaml`; the runtime's console is `runtime/ops-console/`.

## 5.11 The error dictionary and its runbooks

An error is shown to a service engineer the same way wherever it comes from: by its code, as it was raised, with its entry in an `error-dictionary` artefact (R-05-51, R-05-52). The platform's dictionary is `factory/p/errors/p.errors.platform.v1.yaml`; the UAE cluster's and the WPS scheme's hold only the codes the platform's does not (`factory/uae.p/errors/`, `factory/uae.wps/errors/`). A code is looked up in the artefact's own scope first, then its cluster platform, then `p`.

| Field | What |
| --- | --- |
| `code` | As raised: an activity's error name (`TransientError`), an ISO 20022 reason code (`AC03`), an engine's or the runtime's code (`flowable.DeadLetterJob`, `signing.Tampered`) |
| `category`, `retryable` | `business` or `technical`; whether the engine or a person may try again |
| `isoReasonCode` | The ISO 20022 external status reason code, where one exists |
| `raisedBy` | `activity`, `rule`, `port`, `interpreter`, `engine:<target>`, `native`, `store`, `read-model`, `signing`, `package-source`, `auth`, `operations-console`, `service-console`, `assistant` |
| `meaning`, `causes` | What it means and why it usually happens |
| `runbook` | `checks`, `safeActions`, `actions` (the service console's troubleshooting actions below) and `escalation` |

The troubleshooting actions are fixed: `retry-attempt`, `redrive-workflow`, `requeue-dead-letter` (Flowable), `reset-activity` (Temporal), `resend-file`, `re-screen`, `release-hold`, `expire-hold`, `reconcile-posting`, `pause-intake`, `resume-intake`, `drain-worker`, `restart-worker`, `replay-read-model`, `rebuild-read-model`, `reverify-package`, `rotate-trusted-key`. Each needs a person's confirmation and is a fact; every one that changes the system waits for a bank approver's consent (R-09-43, R-09-77). A dictionary is logic, not a value: it is pinned with the package it ships in (`errors/<name>@<release>.yaml`).

