# Training: the TransactHub Designer Studio, step by step

The complete guide, with every feature, setting up and deploying, this training path, and a full workflow built from scratch in the web app, is `docs/guide/studio-guide.html` (built from `docs/guide/template.html` by `docs/guide/build.ts`). This file is the training path as plain text. To watch before you start: the training videos in `docs/demo/training/` (the ideas, as a kitchen; then hands-on, step by step), and the two shorter demos in `docs/demo/`.

**The videos are out of date.** They were recorded before the Aani scheme (`uae.aani`) was removed from the studio in plan phase P6 (Issue I-37 in `docs/ISSUES.md`), and they still show it. `uae.wps` is now the only scheme. Where a video and this file differ, follow this file. The videos need re-recording.

This guide teaches the studio by doing. Work through the exercises in order: each builds on the one before. Every step says what to click or type and what you should see. Allow about four hours for the whole guide; Part A and the first five exercises take about an hour.

All data here is synthetic. Never type real names, account numbers or other personal data into the studio, its tests or its samples.

## Contents

- Part A. Install, start and check
- Part B. The ideas you need
- Part C. Exercises in the web app (1 to 12)
- Part D. Exercises on the command line: export, deploy, run (13 to 15)
- Part E. Worked examples to read: a new scheme, and WPS by configuration
- Part F. Reset, troubleshooting and reference

---

## Part A. Install, start and check

### A1. What you need

| Tool | Needed for | Check |
| --- | --- | --- |
| Node.js 22 and npm | The studio, its tests, the command line | `node --version` shows v22 |
| Python 3.10 or later | The spec's own tools (optional) | `python3 --version` |
| .NET SDK 8 | Deploying and running on DTFx (Part D) | `dotnet --version` shows 8 |
| Java 17 or later | The kit on Flowable (optional) | `java -version` |
| A browser | The web app | Any current browser |

### A2. Install

From the repository root:

```bash
cd studio
```

```bash
npm install
```

```bash
npm run web:build
```

### A3. Start the studio on a training branch

The studio keeps nothing on disk: its drafts, releases, scripts, samples and audit are files on a GitHub branch (R-11-21). For training, start it on an in-memory fake GitHub seeded from `content-configurations/`. Everything you do is gone when you stop it, so you can start again.

```bash
npm run studio:dev
```

Open http://localhost:4317. You should see the catalogue, with about 200 documents listed in the footer of the left-hand menu. To use another port, set `PORT=4400` before `npm start`.

### A4. Check the installation

In a second terminal, from `studio/`:

```bash
npm test
```

You should see every test pass (about 160).

```bash
npm run studio -- kit
```

The last line should read `48 of 48 conformance scenarios pass on the studio interpreter`.

```bash
npm run studio -- test
```

The last line should read `16 of 16 tests pass`. These are the factory's and the example bank's own tests.

```bash
npm run studio -- check
```

The last line should read `0 error(s)` and some warnings.

If all four pass, the studio is ready.

---

## Part B. The ideas you need

Read this once; come back to it when an exercise mentions a word you do not know.

### B1. What the studio is

A design-time tool. A payments designer defines, tests and exports payment workflows here. The studio never reads or changes a running payment. What it produces is an export package that a workflow engine (Temporal, DTFx or Flowable) runs.

### B2. Tenants and users

One studio serves several banks. Each bank is a **tenant**. The **owner** tenant holds shared content (the platform and the schemes). A bank reads shared content but never changes it; it sets its own configuration values and writes hooks.

The header of the web app has two selectors: **Tenant** and **Acting as**. Everything you do is done as that user in that tenant, and roles are enforced.

| User | Tenant | Roles | May |
| --- | --- | --- | --- |
| `shared.designer` | owner | shared designer | Author shared content |
| `shared.checker` | owner | shared checker | Approve and publish shared content |
| `shared.auditor` | owner | auditor | Read only |
| `service.operations` | owner | service operations | Retire releases, import in-flight counts |
| `example.maker` | example-bank | bank designer | Author the bank's content and configuration |
| `example.checker` | example-bank | bank checker | Approve the bank's content |
| `example.auditor` | example-bank | auditor | Read only |
| `second.maker`, `second.checker` | second-bank | bank designer, bank checker | Only their own bank |

**Maker-checker.** The person who makes a change never approves it. Every publish needs a different checker with the checker role.

### B3. Names and scopes

Every artefact name is `<scope>.<type>.<name>.v<major>`, for example `uae.p.validate.iban.v1`.

| Scope | What it is | Example |
| --- | --- | --- |
| `p` | The platform, the same everywhere | `p.wf.corporate.v1` |
| `uae.p` | The cluster platform for the UAE | `uae.p.validate.iban.v1` |
| `uae.wps` | A scheme, the only one in the factory | `uae.wps.wf.salary-file-out.v1` |
| `b` | A bank's own content | `b.hook.pre-release-approval.v1` |

A release is `major.minor.patch`. A published release never changes. A change is a new release.

### B4. The kinds of artefact

| Kind | What it is |
| --- | --- |
| Workflow | Flow only: steps, branches, waits through composites. Kinds: platform, segment, leg, handler, subflow |
| Activity | A block a workflow calls: validate, enrich, map, exchange, decide, hold, post, notify. It declares what it reads, writes and its outcomes |
| Rule, rule set, decision table | Business rules the rule service evaluates. Values a bank may change are parameters with defaults and bounds |
| Port | A connection to a party: the scheme, core banking, sanctions |
| Simulator | Stands in for a port's party in tests, with scenarios (accept, reject, no reply and so on) |
| Hook | A bank's own logic at a hook point a platform workflow offers |
| Scheme | A scheme manifest: its messages and its **dispatch table**, which routes each message |
| Configuration set | A bank's values, accepted pack releases and hook bindings, with effective dates |
| Test | A target, a level, a bench of simulators, what is given and what is expected |

### B5. How a payment runs

Learn this path; most exercises follow it.

1. A file or message arrives at the **front door**.
2. The scheme's **dispatch table** says what the message is. For a start message it names the payment it starts (kind, direction and, for an outward payment, segment), the **intake map**, and the **leg** to start. The dispatch table routes; the intake map only reads the file.
3. The intake map turns the file into the canonical ISO 20022 context: header, batch, items.
4. The **workflow-select** table (`p.table.workflow-select.v1`) picks the **platform workflow** from the kind, direction and segment the dispatch table named. An outward corporate payment runs `p.wf.corporate.v1`. Check L33 makes sure every start message routes to a platform workflow whose slot its leg fills.
5. The platform workflow validates, screens, checks limits, reserves funds, and routes. The **route** step passes only a rail that the bank has enabled in its configuration.
6. The platform workflow has a **slot** called `scheme-leg`. The leg the dispatch table named fills it and does the scheme's part: checks, render, submit, wait for the acknowledgement.
7. Back in the platform workflow: post, verify, notify, and the bank's hooks where it bound them.

### B6. Composites and the expanded form

A step that talks to a party and waits for an answer is a **composite** activity. Underneath, a **subflow** sends, awaits and reads the reply. The designer sees one step; the engines run the **expanded form**, where each composite step becomes a block of inner steps. Runs show two traces: the **business trace** (one line per authored step) and the **detail** (the inner steps).

### B7. Runs, benches and the virtual clock

A run happens on a virtual clock that starts on the **evaluation date**. Waits of days end at once. A **bench** binds each port to a simulator and a scenario. A port left out of the bench uses its default simulator, and the run report says so.

---

## Part C. Exercises in the web app

Start the studio as in A3 before each session. Unless an exercise says otherwise, act as tenant **owner** and user **shared.designer**.

### Exercise 1. Find your way around (10 minutes)

1. Open **Catalogue**. Type `iban` in the search box. The list narrows to artefacts that name IBAN or read an IBAN path.
2. Click `uae.p.validate.iban.v1`. Read its sections: **Contract** (reads, writes, outcomes), **Steps and effective policy**, **Used by**, **Tests that target it**, **Releases**, **Provenance**.
3. Click **Back to the catalogue**. Filter **Kind** to `workflow` and open `p.wf.corporate.v1`. Read its outline: the steps listed in B5, the `scheme-leg` slot and the hook points `before-release` and `on-close`.
4. Open **ISO data**. In **Canonical model**, open `payment` then `instructedAmount`. Elements marked as personal data must never appear in workflow state, logs, tests or samples.
5. In **Code registry**, look up code `AM04`. You should see `Insufficient funds`.
6. In **ISO data**, find the scheme message catalogues. Open `uae.wps` and read its dispatch table: `uae.wps.sif` starts an outward corporate payment, with the leg `uae.wps.wf.salary-file-out.v1` and the intake map `uae.wps.map.sif-in.v1`.

Check yourself: which leg does a WPS payment file (`uae.wps.pif`) start? (Answer: `uae.wps.wf.agent-payment-in.v1`.)

### Exercise 2. Run a factory test and read the report (15 minutes)

1. Open **Test bench**, tab **Tests**. Filter for `WPS-OUT-001` and click **Run**. The row shows a pass.
2. Open the run report. Read:
   - **Business trace**: `business-date:set`, `file-checks:pass`, `record-checks:pass`, `record-iban:pass`, `render:mapped`, `submit:accepted`, `end:settled`.
   - **Detail**: inside the `submit` composite, `submit.send:accepted`, `submit.await-ack:received`, `submit.read-ack:ack`. Use **Open every composite** to expand it.
   - **Timeline on the virtual clock**: the simulator answers after ten minutes of virtual time.
   - **Messages in and out**: the submission to WPS and the acknowledgement back.
3. Run `WPS-OUT-004`. No acknowledgement comes; the composite waits one business day of virtual time and the leg ends `pending`. The run took a moment of real time.
4. Run `WPS-OUT-002`. WPS refuses the file; the leg ends `rejected` with reason code `AM10`.

Check yourself: why does `WPS-OUT-004` end after a business day and not a calendar day? (The composite `uae.wps.exchange.submit-salary-file.v1` declares `replyTimeout: P1D` with `replyCalendar: business`.)

### Exercise 3. Your own run on the ad hoc bench (15 minutes)

1. In **Test bench**, choose **Ad hoc bench**.
2. **Target**: `uae.wps.wf.salary-file-out.v1`.
3. Click **Add a port** twice and bind:
   - `uae.wps.port.wps.v1` to `uae.wps.sim.wps-outward.v1`, scenario `reject`;
   - `uae.p.port.calendar.v1` to `uae.p.sim.calendar.v1`, scenario `accept`.
4. Choose **Inbound sample** and pick `samples/wps-sif-valid.sif`. Leave **Start message** empty: the leg starts alone.
5. Click **Run the bench**. The outcome is `rejected`.
6. Change the WPS scenario to `fail-twice-then-accept` and run again. The send fails twice and succeeds on the third attempt; the steps section shows three attempts on `submit.send`.
7. Change the sample to `samples/wps-sif-bad-control-total.sif` and the scenario back to `accept`. The file checks reject the file before anything is sent.
8. Under the report, use **Save the run as a test**. Give it an id such as `TRAIN-OUT-001` and a title. This saves a test draft with the assertions you tick.

### Exercise 4. A whole payment, end to end (15 minutes)

This runs the path in B5: front door, intake, workflow-select, the corporate workflow and the WPS leg in its slot.

1. In **Ad hoc bench**, **Target**: `p.wf.corporate.v1`. **Tenant for the run**: `example-bank`.
2. Bind five ports, each to its simulator with scenario `accept`:
   - `p.port.core-banking.v1` to `p.sim.core-banking.v1`;
   - `p.port.sanctions.v1` to `p.sim.sanctions.v1`;
   - `p.port.fraud.v1` to `p.sim.fraud.v1`;
   - `uae.wps.port.wps.v1` to `uae.wps.sim.wps-outward.v1`;
   - `uae.p.port.calendar.v1` to `uae.p.sim.calendar.v1`.
3. **Inbound sample**: `samples/wps-sif-valid.sif`. **Start message**: `uae.wps.sif`.
4. Click **Run the bench**. The outcome is `settled`, and the business trace has 17 lines, among them `reserve:reserved`, `route:decided`, `hook:continue` (the bank's pre-release hook), `leg:settled`, `post:posted`, `verify:verified`.
5. Read the **Warnings** box: ports you did not bind used their default simulators. That is expected.
6. Open **Instances**: the intake, the platform workflow, the leg, and the bank's hooks each ran as an instance.

Check yourself: change **Tenant for the run** to `owner` and run again. What happens and why? (The route finds no enabled rail, because the owner has no configuration set enabling `uae.wps`: `route:no-match`, `undo:reserve`, `end:rejected`.)

### Exercise 4a. The same payment in the debugger (15 minutes)

1. With the same set-up as exercise 4 (tenant `example-bank` again), click **Debug step by step**. The debugger opens below the bench.
2. Click **Step into ▶** a few times. The front door reads the file; then the corporate workflow starts. On the right, **Data changed by this event** shows each value before and after, and **All the data now** marks what changed.
3. On the left, click the dot beside `route` to set a breakpoint, then **Continue ⏭**. The run stops after the route step: **Decisions and rules** shows the decision `rail: "uae.wps"` and the table `p.table.rail-choice.v1` that made it.
4. Click **Step into ▶** until **Where the run is** shows a bank hook or the WPS leg, then **Step out ⤴** to go back up. Try **Step over ⤼** at the slot: it passes the whole leg at once.
5. Click **Step back** a few times, then **Continue ⏭** to the end.

Check yourself: what is the last event, and why is it after the payment ended? (The bank's notify hook at `on-close`: a notify hook runs alongside and never delays the payment, R-06-09.)

### Exercise 5. The conformance kit (5 minutes)

1. Open **Conformance kit** and click **Run all 48**. All 48 pass.
2. Open one scenario, for example `CK-RETRY-01`, and read what it proves: the expected trace and the checks on attempts.

The same 48 scenarios run on DTFx, Elsa and Flowable; see Part D.

### Exercise 6. Author and publish a rule, with maker and checker (25 minutes)

1. Act as `owner` / `shared.designer`. Open **Artefact editors**, choose **Rule**, click **Start new**.
2. Click **Edit the YAML** and replace the document with:

   ```yaml
   kind: rule
   name: p.rule.training-amount-positive.v1
   release: 1.0.0
   title: Training rule, the amount is positive
   ruleKind: field
   level: payment
   repeatability: non-repeatable
   when:
     - match:
         fact: Payment
         as: p
         where: p.amount <= 0
   then:
     - yield:
         finding:
           severity: reject
           path: payment.amount
           reasonCode: AM12
   examples:
     - name: passes
       evaluationDate: '2026-11-17'
       given:
         Payment: { amount: 1 }
       expect:
         findings: []
     - name: fails
       evaluationDate: '2026-11-17'
       given:
         Payment: { amount: 0 }
       expect:
         findings: [{ reasonCode: AM12 }]
   provenance:
     origin: authored
     status: provisional
     clauses: [Training exercise]
   ```

3. Read the **Gates** panel. Both examples run; the panel is green.
4. Break it on purpose: change the second example's `amount` to `5`. The example fails and the gate goes red. Change it back.
5. Under **Save and publish**, click **Save draft as shared.designer**, then **Submit for review**.
6. Choose **Checker** `shared.auditor` and click **Publish as shared.auditor**. It is refused: an auditor is not a checker.
7. Switch **Acting as** to `shared.checker`. Open **Governance**, tab **Review and approve**. Your draft is listed; **Publishes with** says `alone`, because no other draft refers to it. Choose **Checker** `shared.checker` and click **Approve as shared.checker**. Approval reruns the tests that target the draft, then publishes it as change set `CS-<date>-<n>`.
8. Open the rule in the **Catalogue**: release `1.0.0`, maker `shared.designer`, checker `shared.checker`.
9. Try to change it: edit the title, keep release `1.0.0`, save and publish. It is refused: a published release never changes. Bump the release to `1.0.1` and it publishes.
10. Open **Governance**, tab **Audit log**. Every save, submission, publish and refusal is there. Click **Verify the chain**: the hash chain verifies.

### Exercise 7. Tenancy and roles (10 minutes)

1. Switch **Tenant** to `example-bank` and **Acting as** to `example.maker`.
2. Open `uae.p.validate.iban.v1` in the catalogue. It is marked shared content, read only for a bank.
3. In **Artefact editors**, try to save a rule named `uae.wps.rule.my-copy.v1`. It is refused: a bank may author only in scope `b`.
4. Switch **Acting as** to `example.auditor`. The header shows **read and review only**.
5. Open the **Acting as** list: in `example-bank` it offers only that bank's users. A user of `second-bank` cannot act in it; over the API such a request is refused (R-06-01).

### Exercise 8. Change a bank's configuration (20 minutes)

1. Act as `example-bank` / `example.maker`. Open **Tenant**.
2. Read **Configuration set**, **Parameters** and **Hook points**. The bank enables the rail `uae.wps`, and binds three hooks.
3. In **Edit the configuration set**, click **Bump the release** (it becomes `1.1.0`). In the YAML, change the value of `p.table.rail-choice.v1#enabledRails` to `[]`, so that the bank runs no rail.
4. Click **Save the configuration set**. It is saved as release `1.1.0`.
5. Repeat Exercise 4 with **Tenant for the run** `example-bank`. The payment is now rejected at `route:no-match`, and the reservation is undone (`undo:reserve`).
6. Bump the release again to `1.2.0`. Set `enabledRails` back to `[uae.wps]`, and delete the binding whose `hookPoint` is `p.wf.corporate.v1#before-release`. Save.
7. Run Exercise 4 again. The payment settles, and the trace no longer has `hook:continue` before the leg: the bank's pre-release hook is unbound.
8. Try to set `b.rule.internal-limit.v1#internalLimit` to `2000000`. The check refuses it: the rule's bound is 1000000, and the configuration may only lower it.
9. Use **Compatibility run**, then **Run**. Before a bank accepts a new shared release, it checks that every bound hook point still exists with a compatible contract and that the bank's hook tests pass. Leave the box empty to run against the current releases.

10. The bank's internal limit (`b.rule.internal-limit.v1#internalLimit`) holds a payment, each line of a file and the file total. Bump the release again, put back the `before-release` binding from the shipped configuration, and set the limit in force in 2026 to `1200`. Save.
11. Run Exercise 4 with the sample `samples/wps-sif-two-lines.sif` (lines of 1000 and 500, total 1500), and bind `p.port.operations-queue.v1` to `p.sim.operations.v1` with scenario `decline`. Every line is within the limit but the total is not. The trace shows `hook:hold`, then `hold:declined`, `undo:reserve`, and the payment is rejected. With scenario `approve` it settles after `hold:approved`.
12. Set the limit to `900`: now the line of 1000 is above it, and the payment is held for that line. Set it to `2000`: nothing is held.

### Exercise 9. The workflow designer (25 minutes)

1. Act as `owner` / `shared.designer`. Open **Workflow designer**.
2. Under **Open**, pick `p.wf.corporate.v1` and click **Open**. Switch between **Canvas**, **Outline** and **Expanded form**. In the expanded form, composite steps inside legs become blocks.
3. Under **New**: **Start from** `From a template`, **Kind** `workflow`, **Workflow kind** `leg`, **Name** `uae.wps.wf.training-leg.v1`, **Title** `Training leg`. Click **Create**.
4. On the canvas, use **+ Add a step** to add a call to `uae.p.enrich.business-date.v1`. Watch the **Checks** panel: an unhandled non-primary outcome or a missing reference shows at once.
5. Add a step calling an activity that does not exist, for example `uae.wps.validate.nothing.v1`. Check `L03` appears: the reference does not resolve. Remove it.
6. Read **Interface and release**: the studio proposes the next release from the interface change.
7. Click **Save draft as shared.designer** to keep it. You need not publish it. To delete it, use **Delete** in **Governance**, **Review and approve**.

Why a new leg in WPS would not run on its own: the WPS dispatch table names one leg for the salary file. A new leg starts from real files only when a new release of the scheme's manifest names it. See Part E for a new scheme built that way.

### Exercise 10. Export a package (10 minutes)

1. Act as `owner` / `shared.designer`. Open **Export**.
2. Tick `p.wf.corporate.v1`. **Target**: `dtfx`. **Tenant**: `example-bank` (bank pack).
3. Click **Preview**. Read the manifest:
   - **workers**: one per task queue. Here, `th-bank`, `th-p`, `th-uae-p` and `th-uae-wps`: the platform, the bank's hooks, the cluster platform and the WPS leg that fills the slot.
   - **tasks**: each named `name@release`, each on its queue.
   - **files**: workflows in expanded form with their pins, activities, rules, the binding, and `configuration/example-bank.yaml`.
   - **changeSets**: the change sets the package's artefacts were published in. Shipped content is listed by pack release, for example `pack:uae.wps@1.0.0`; what you approved in Exercise 6 appears as `CS-<date>-<n>` when the package uses it.
4. Tick **With simulators** and click **Download the zip**. A package with simulators is marked not promotable: it is for testing only.
5. Download the same package twice and compare: the bytes are identical (a reproducible zip).

### Exercise 11. Governance (20 minutes)

1. Open **Governance**, tab **Pins, retirement, propagation**. Enter `uae.p.validate.iban.v1` and click **Show pins**: `uae.wps.wf.salary-file-out.v1@1.0.0` pins it.
2. As `shared.checker`, try to retire `uae.p.validate.iban.v1@1.0.0`. It is refused because a workflow pins it.
3. Tab **Packs**: each factory pack's manifest regenerates to the same hashes.
4. Tab **Conformance**: the release-gate report. Add a waiver for a scenario with a reason (as `shared.checker`). A waiver needs a reason.
5. Tab **Coverage**: reason codes covered by simulator scenarios, and outcomes with no test. `uae.wps.wf.salary-file-out.v1` shows `returned` untested (Issue I-25).
6. Tab **Bindings and stubs**: the engine bindings regenerate from the packs. Download a simulator's deployable stub.
7. Tab **Audit log**: filter by your user, and **Verify the chain**.

### Exercise 11a. The assistant beside the editor (15 minutes)

1. Open **Artefact editors**, **Rule**, **Start new**. The panel on the right shows **What is happening**. (If it is closed, click the **Assistant** tab on the right edge.)
2. Click into **Rule kind**, then **Level**. The panel says what each field means, the values it takes, an example and the usual mistake.
3. Change the name and the title. Watch the plain-words explanation, the picture and the checklist follow your edits.
4. With a key set for the studio server, open **Ask** and type: *Build a rule that the days in the pay period are 1 to 31, with a passing, a failing and a boundary example.*
5. The assistant looks around first: you see each thing it read. When it wants to change the rule, a card shows what it would change and its checks. Choose **Allow once**. The form fills in.
6. Ask it to shorten the title, and choose **Allow always**. Then click **Undo** beside that change in the list: the title goes back.
7. Save the draft yourself, and publish it with a checker as in exercise 6. The assistant never publishes.

The same panel is beside the workflow designer, a bank's configuration and the test bench.

### Exercise 12. The AI assistant (optional, needs a key)

The assistant drafts a rule, activity, simulator or script from a plain-language instruction and public scheme text. It needs `ANTHROPIC_API_KEY` set for the studio server; without it, the view says how to set it.

1. Stop the studio, then start it with a key:

   ```bash
   ANTHROPIC_API_KEY=<your key> npm run studio:dev
   ```

2. Open **AI assistant**. **Draft kind** `rule`. **Instruction**: `The salary month must be a valid month, else reject with DT01`.
3. Click **Ask for a draft**. Read the draft, the **Questions** it asks, the **Clauses** it relied on, and the **Provenance**.
4. Read the gates: schema, checks, tests per outcome, determinism for scripts, provenance. A draft never publishes itself; **Save as a draft** and follow maker-checker as in Exercise 6.

Never paste personal data into the instruction or the source text.

---

## Part D. Exercises on the command line: export, deploy, run

These need the .NET 8 SDK. Run them from `studio/` with the studio stopped or running; they read the spec directly.

### Exercise 13. Export with separate workers

```bash
npm run studio -- export-zip uae.wps.wf.salary-file-out.v1 dtfx /tmp/wps-leg.zip --with-simulators
```

You should see `workers th-uae-p, th-uae-wps`. Unzip it and look at `workers/th-uae-wps/manifest.json`: the tasks that worker serves, and nothing else.

### Exercise 14. Deploy and run a leg

```bash
npm run studio -- deploy /tmp/wps-leg.zip --workflow uae.wps.wf.salary-file-out.v1 --inbound fixtures/samples/wps-sif-valid.sif --bench uae.wps.port.wps.v1=uae.wps.sim.wps-outward.v1:accept --bench uae.p.port.calendar.v1=uae.p.sim.calendar.v1:accept
```

Read the output:

- `worker th-uae-p up ... pid` and `worker th-uae-wps up ... pid`: two separate processes.
- `intake uae.wps.map.sif-in.v1 on th-uae-wps: mapped`.
- One line per worker with what it served and `refused 0`.
- `outcome settled`, the business trace and the detail, the same as on the studio.

Run it again with `:reject` instead of `:accept` for the WPS port: `outcome rejected`.

### Exercise 15. Deploy and run a whole payment for a bank

Export the corporate workflow for the bank, with simulators:

```bash
npm run studio -- export-zip p.wf.corporate.v1 dtfx /tmp/corporate.zip --with-simulators --tenant example-bank
```

Deploy it and send a salary file through the front door:

```bash
npm run studio -- deploy /tmp/corporate.zip --workflow p.wf.corporate.v1 --message uae.wps.sif --inbound fixtures/samples/wps-sif-valid.sif --tenant example-bank --bench p.port.core-banking.v1=p.sim.core-banking.v1:accept --bench p.port.sanctions.v1=p.sim.sanctions.v1:accept --bench p.port.fraud.v1=p.sim.fraud.v1:accept --bench uae.wps.port.wps.v1=uae.wps.sim.wps-outward.v1:accept --bench uae.p.port.calendar.v1=uae.p.sim.calendar.v1:accept
```

Read the output:

- `configuration example-bank@1.0.0 from the package`: the bank's configuration travels in the package.
- `front door: workflow-select chose p.wf.corporate.v1 for payment outward corporate`.
- `front door: uae.wps.wf.salary-file-out.v1 fills scheme-leg`.
- Separate workers, among them `th-bank` (the bank's hooks), `th-p`, `th-uae-p` and `th-uae-wps`, each serving only its own tasks.
- `outcome settled` and the same 17-line trace as Exercise 4.

To deploy a configuration you changed in the web app (Exercise 8), download the package from **Export** with tenant `example-bank` and **With simulators**, and pass that zip to `deploy`. The package carries the configuration as it was when you exported it. `--config <file>` overrides it.

`TH_WORKER_LOG=/tmp/workers.log` before the command makes each worker log every attempt it served.

### Optional: the kit on real engines

```bash
npx tsx engines/dtfx/run-kit.ts
```

```bash
npx tsx engines/elsa/run-kit.ts
```

```bash
engines/flowable/setup.sh && npx tsx engines/flowable/run-kit.ts
```

Each ends with `48 of 48 conformance scenarios pass`.

---

## Part E. Worked examples to read

Two end-to-end tests show complete pieces of work, done only through the HTTP API and the command line. Read them as worked examples, then run them.

### E1. A new scheme from scratch

`studio/test/blackbox/e2e-new-scheme.test.ts`, with the scheme's documents in `studio/test/blackbox/demo-scheme.ts`. `uae.demo` is a synthetic scheme made up for training.

1. Sample files are saved through `POST /api/samples`.
2. The port and its simulator refer to each other. The studio generates their change set, and approving either publishes both (R-11-19).
3. The rule, rule set and file checks are published, then the intake map and its script-behind (`POST /api/scripts`).
4. The render map, the acknowledgement map and the send are published.
5. One generated change set holds the scheme manifest, the composite and its subflow, and the leg. The dispatch table names the leg and routes the file as an outward corporate payment; the leg fills the `scheme-leg` slot of `p.wf.corporate.v1`.
6. The end-to-end test fails at the route until the bank's configuration accepts the scheme and enables its rail.
7. The export gives the new scheme its own worker, `th-uae-demo`, and the deploy pays the credit file end to end.

Run it:

```bash
npx vitest run --config vitest.blackbox.config.ts test/blackbox/e2e-new-scheme.test.ts
```

### E2. WPS changed only by bank configuration

`studio/test/blackbox/e2e-wps.test.ts`. WPS is a factory scheme, so a bank does not author in it; it changes its configuration. Releases `1.1.0` (rail off) and `1.2.0` (rail on, pre-release hook unbound) are each run on the studio and deployed on DTFx with four separate workers.

```bash
npx vitest run --config vitest.blackbox.config.ts test/blackbox/e2e-wps.test.ts
```

### E3. All the black-box suites

```bash
npm run test:blackbox
```

API, command line, the web app in a browser, and both end-to-end tests. Add `TH_ENGINES=1` to run the kit on DTFx, Elsa and Flowable too.

---

## Part F. Reset, troubleshooting and reference

### F1. Start again

Stop the studio started with `npm run studio:dev` and start it again: its fake GitHub is in memory, so you start from the seeded content. There is nothing on disk to delete.

The factory content and the example bank are seeded from `spec/` into `content-configurations/`, the factory's script-behinds and test samples from `studio/scripts/` and `studio/fixtures/samples/`, and the studio never changes a published file. A change set's branch, `studio/<id>`, is deleted once its change set is on the content branch.

### F2. Troubleshooting

| You see | Do this |
| --- | --- |
| The page is blank or says the web app is not built | `npm run web:build`, then restart |
| `EADDRINUSE` on start | Another studio is running; stop it, or set `PORT` |
| A publish is refused | Read the message. Common causes: you are the maker, the user has no checker role, the release is already published, a test is red, or the draft refers to drafts not yet published (publish them together) |
| `No start message in the package's dispatch tables` on deploy | Give `--message` for a whole payment, or check the leg is named in its scheme's dispatch table |
| `route:no-match` | The tenant's configuration does not enable that rail (Exercise 8) |
| Deploy says `dotnet build failed` | Install the .NET 8 SDK; the first deploy builds the DTFx harness |
| A worker did not start | Run again with `TH_WORKER_LOG=/tmp/workers.log` and read the log |
| The assistant says it has no credentials | Set `ANTHROPIC_API_KEY` for the studio server |

### F3. Commands

| Command, from `studio/` | What it does |
| --- | --- |
| `npm start` | The studio on http://localhost:4317, on the content branch `TH_CONTENT_REPO` (`TH_CONTENT_BRANCH`, `TH_GITHUB_TOKEN`, `TH_GITHUB_API`, `PORT`, `TH_SPEC_ROOT`) |
| `npm run studio:dev` | The studio on an in-memory fake GitHub seeded from `content-configurations/`: for training, gone when it stops |
| `npm test` | The white-box suite |
| `npm run test:coverage` | The same with coverage |
| `npm run test:blackbox` | The black-box suites |
| `npm run studio -- test [ID...]` | Factory and bank tests on the studio |
| `npm run studio -- kit [CK-ID...]` | The conformance kit on the studio |
| `npm run studio -- check` | Schemas and static checks over every artefact |
| `npm run studio -- export <workflow> <target>` | Print an export manifest |
| `npm run studio -- export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t]` | Write an export package |
| `npm run studio -- deploy <package> --workflow <name> [--inbound file [--message m]] [--tenant t] [--config file] [--bench port=simulator:scenario ...]` | Deploy on DTFx with separate workers and run |

From `spec/`, the spec's own tools: `python3 tools/validate.py`, `python3 tools/expand.py --check`, `python3 tools/mutation_tests.py`, `python3 tools/build_docs.py`.

### F4. Where to read more

| Document | What it covers |
| --- | --- |
| `README.md` | What runs today, with evidence |
| `studio/README.md` | The studio's components, export, workers, deploy |
| `spec/design/` | The specification, part by part. Start with Part 1 (concepts), Part 5 (the DSL) and Part 10 (studio functions) |
| `spec/design/annex-a-wps.md` | The factory scheme, with its dispatch table |
| `docs/ISSUES.md` | Where the spec was silent or contradictory, and what the studio does |

### F5. Glossary

| Word | Meaning |
| --- | --- |
| Bench | The simulators a run binds to ports, with a scenario each |
| Business trace | One line per authored step: `step:outcome` |
| Change set | What one approval publishes: the draft and the drafts it refers to or that refer to it. The studio generates it; an export lists the change sets of its artefacts |
| Composite | An activity whose behaviour is a subflow, typically send, await, read |
| Detail | The inner steps of composites: `step.inner:outcome` |
| Dispatch table | A scheme's routing of each message: start, deliver or sent by |
| Evaluation date | The date a run starts on its virtual clock |
| Expanded form | A workflow with composite steps replaced by blocks; what engines run |
| Front door | Where messages arrive: intake for start messages, delivery for replies |
| Hook | A bank's own logic at a declared hook point |
| Leg | A scheme's part of a payment, filling a platform workflow's slot |
| Pin | An exact release a workflow uses, fixed when it is published |
| Slot | A place in a platform workflow that a leg fills |
| Worker | A process serving one task queue's tasks in a deployment |
