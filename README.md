# TransactHub Designer Studio

The design-time tool in which a payments workflow designer defines, scripts, tests and exports payment workflows built from composable blocks over one ISO 20022 canonical context. One multi-tenant application; each bank is a tenant that shares platform and scheme content, sets configuration values and authors hooks. Built from the specification set at draft 0.2.

| Folder | What it is |
| --- | --- |
| `spec/` | The specification set at draft 0.2: design parts and annexes, schemas, the factory pack, the example bank, the conformance kit, the expanded forms, the build backlog and the spec's own tools. The re-ideation plan (`.docx`), its summary, and `delta/DELTA-0.2.md` sit beside them. |
| `studio/` | The studio: server, interpreter, rule service, sandboxes, simulators, checks, export, governance and the web app. See `studio/README.md`. |
| `studio/engines/` | The conformance kit on real engines: DTFx (C#) and Flowable (Java). |
| `docs/` | The stack decision record and the issues found in the spec while building. |

## Start

```bash
cd studio && npm install && npm run web:build && npm start
```

Then open http://localhost:4317. Choose the tenant (`owner` for shared content, `example-bank`) and the user you act as in the header; roles are enforced.

## Learn it

`docs/guide/studio-guide.html` is the user guide: setting up and deploying, every feature, a training path, and a full workflow built from scratch in the web app. Its walk-through is also a browser test (`studio/test/blackbox/ui-build.test.ts`). `docs/TRAINING.md` has the training path as plain text.

## What runs today

| Evidence | Result |
| --- | --- |
| Spec tools at draft 0.2 (`spec/tools`) | validate 0 errors and the 4 expected warnings (I-38); `expand.py --check` up to date; 28 of 28 mutations caught; 125 stories render |
| Spec documents valid against their schemas | 157 of 157 |
| Content hashes (RFC 8785) equal to the pack manifests | All; four of the five pack manifests also regenerate identically (Issue I-20 for the bank pack) |
| Expansion of composite steps | Identical to `tools/expand.py` on the three reference workflows |
| Rule and table examples | 60 of 60 |
| Factory and bank test cases on the studio interpreter | 16 of 16 |
| Conformance kit, studio interpreter | 46 of 46 |
| Conformance kit, DTFx (DurableTask.Core 3.11.3) | 46 of 46 |
| Conformance kit, Flowable 7.1.0 with generated BPMN | 46 of 46 |
| Conformance kit, Temporal | Not run: see below |
| Engine bindings regenerated from the packs | 3 of 3 identical to `spec/examples/bindings` |
| Generated samples, each proved valid or failing with its labelled code | 7 of 7 |
| Static checks over the spec | 0 errors; warnings from the checks built here (L19, L21, L22, L25) |
| White-box suite (`cd studio && npm test`) | 161 tests, green; 90.7% of lines, 78.1% of branches, 91.7% of functions (`npm run test:coverage`) |
| Black-box suites (`npm run test:blackbox`) | 123 tests, green: HTTP API 71, command line 8, web app in Chromium 24, end to end 20 (new scheme 12, WPS by configuration 8); the engine suite (2) runs with `TH_ENGINES=1` |
| End to end, a new scheme | A synthetic scheme built from scratch over the API: manifest and dispatch table, port, simulator, maps with a script, rule, exchange with its subflow, and a leg in the corporate workflow's slot. The bank enables the rail in its configuration, and the payment settles end to end on the interpreter and deployed on DTFx, the new scheme on its own worker |
| End to end, WPS | The factory WPS scheme changed only by bank configuration: the rail turned off (rejected at the route) and a hook unbound (settled without it), each deployed on DTFx with four separate workers |

## Coverage of the build backlog

The spec's process is one story per branch and pull request, each reviewed. This build was done on one branch as a first implementation of the whole backlog, to review at G1, not as accepted stories.

| Component | State |
| --- | --- |
| D02-S1, D02-S2 Draft 0.2 | Done: the overlay applied; `sub` names, subflows, L30 to L32, checks reading subflows, pins through composites |
| C01 Artefact store and naming | Done: loader, names and scopes, canonical JSON and hash, drafts, immutable publish, releases, tenant-keyed bank content |
| C02 Schema validation | Done |
| C03 Canonical model and types | Done: the model as data with ISO types and personal-data flags, the extension registry, TypeScript and Python types per activity with a type check gate, versioned code-set registries. The registry content is from memory and marked unverified |
| C04 Static checks | Done: every check of the catalogue, L00 to L32, per document on every edit; expansion (C04-S6); whole-flow checks on the expanded form reported at the authored step |
| C05 Pins, manifests and semver | Done: closure and pins, pack manifests, interface diff and proposed release, dependency release propagation |
| C06 Rule service | Done. The evaluator is TypeScript with NRules semantics; D13 stays open |
| C07, C08 Script sandboxes | Done for TypeScript and Python with a parity check. Not a security boundary (D14) |
| C09 Studio interpreter | Done: the portable core, blocks and returns, the business trace and the detail |
| C10 Simulator runtime | Done: scenarios, self-check, reason-code coverage, a deployable stub |
| C11 Test runner | Done: every level, the detail, publish check of untested outcomes |
| C12 to C15 Catalogue, editors, designer, test bench | Done in the web app: catalogue, ISO data and model browser, scheme catalogues with proved samples, editors for rules, tables, activity contracts, maps, simulators and composites, the workflow canvas with composite expansion, the test bench with nested detail and saved assertions |
| C16 AI authoring assistant | Done except a live model run: the assistant calls the Claude API (`claude-opus-5-5`); no credentials are set here, so it was tested with an injected drafter. Gates, provenance and questions work |
| C17 Tenants, roles, maker-checker | Done: roles enforced on every write, tenant isolation suite, maker and checker differ, a hash-chained audit with diffs and refusals |
| C18 Hooks and configuration sets | Done: hooks at hook points, configuration editing with bounds and overlap checks, compatibility run, tenant export |
| C19 Packs, release, retirement, supersession | Done: pack build, review and approval with green tests, release gate, retirement against pins and an in-flight import, supersession draft, evidence and hand-over |
| C20 Exporter and Temporal binding | Packages and binding done (expanded forms, pins, `name@release`, a separate worker per task queue, a deploy runner on DTFx). The Temporal run of the kit (C20-S2 to S4) is not built |
| C21 DTFx | Done: 46 of 46 on DTFx |
| C22 Flowable | Done: generated BPMN runs on Flowable; patterns and engine gaps recorded in Part 9, section 9.7; L24 uses the portability report |
| C23 Conformance kit and runner | Done: kit content in `ck`, the adapter interface, a reference adapter, the release-gate report with waivers |
| C24 to C26 Factory content | Partly: samples for every test, six scripts-behind, WPS-IN-001 fixed (C25-S4). Confirming against the rulebooks and the planned message groups wait on D10 and business decisions; see `docs/ISSUES.md` |

## Found by the test suites

The black-box and white-box suites found and fixed these:

- Ten API routes answered 500 to an empty or malformed body; they now answer 400 with a message.
- `permit` allowed any action it did not know.
- The command line exited 0 for an unknown command and crashed on a malformed `--bench`; usage mistakes now exit 2.
- The ISO data and editor views scrolled sideways at phone width.
- A deployed worker lost a simulator's later reply, so a refused WPS file settled. The party service now keeps the reply.
- The deploy runner returned before its workers had exited.
- The factory WPS leg has an untested outcome, `returned` (Issue I-25), and a new leg has no start message until its scheme is released (Issue I-24).
- A simulator keyed by message id answered only its first request, so a payment's posting after its reservation got no reply (R-07-05).
- On DTFx a bank's transform hook always failed, on a JSON serialiser without a type resolver; the kit has no scenario that catches it.
- A new scheme's tasks fell back to the platform queue; the queue is now derived from the scope (R-09-05).
- Artefacts that refer to one another could not be published (Issue I-27); a salary file starts an outward payment that the front door took for inward (Issue I-26); the example bank's hold did not see batches (Issue I-28). All three are decided and built: change sets are generated, the dispatch table routes (R-05-42, L33), and the limit checks each line and the file total.
- A halt hook that found something always counted as failed, on every runtime, because a hook's blocks could write only `facts.hooks` while check L13 allows `facts.*`.
- On a deployed worker the approval hold could not run: the party service fired its timer at once and sent the operations decision to the engine instead of the hold.

## Temporal

Running the kit on Temporal was started as a separate piece of work and was refused by the session's permission classifier, with no reason given. It was not attempted another way. The studio's Temporal export package and binding exist and are tested; the Temporal SDK installs and a Temporal test server is downloadable from GitHub releases here, so the work is feasible once permitted.

## Not verified

- `uae.wps` stays provisional, and it is the only factory scheme since plan phase P6 (I-37). Nothing here confirms a scheme fact; samples and scripts follow the factory pack's own provisional reading.
- The ISO code-set registry was written from memory, like the factory's reason codes.
- The AI assistant has not run against a live model in this environment.
- The sandboxes keep honest scripts deterministic; they were not tested against hostile code.
