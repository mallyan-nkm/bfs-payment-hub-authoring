# TransactHub Designer Studio

The design-time tool in which a payments workflow designer defines, scripts, tests and exports payment workflows built from composable blocks over one ISO 20022 canonical context. One multi-tenant application; each bank is a tenant that shares platform and scheme content, sets configuration values and authors hooks. Built from the specification set at draft 0.2.

| Folder | What it is |
| --- | --- |
| `spec/` | The specification set at draft 0.2: design parts and annexes, schemas, the factory pack, the example bank, the conformance kit, the expanded forms, the build backlog and the spec's own tools. The re-ideation plan (`.docx`), its summary, and `delta/DELTA-0.2.md` sit beside them. |
| `studio/` | The studio: server, interpreter, rule service, sandboxes, simulators, checks, export, governance and the web app. See `studio/README.md`. |
| `studio/engines/` | The conformance kit on real engines: DTFx and Elsa (C#), Temporal, and Flowable (Java). |
| `content-configurations/` | The studio's store as seeded: the published factory, the example bank, and the four banks' folders (configuration, hooks, port bindings). |
| `runtime/` | The new .NET 10 runtime: one bank per runtime, the insert-only store, the release API, the File API and the front door, the engine hosts, the native host, the operations and service consoles, the test bench. See `runtime/README.md`. |
| `deploy/laptop/` | The studio and one bank's runtime in Docker containers on a laptop. See `deploy/laptop/README.md`. |
| `docs/` | The stack decision records, the issues found while building (`docs/ISSUES.md`), the runtime's plan and parity documents (`docs/runtime/`), the user guide and the training. |
| `docs/north-star/` | The source's engineering and AI docs rewritten for the north star: architecture essentials, build and run, how an AI agent works here, guidelines, design, requirements, quality, security and deployment, with an inventory of every source file. Start at `docs/north-star/README.md`. |

## Start

```bash
cd studio && npm install && npm run studio:dev
```

Then open http://localhost:4317 and sign in as one of the listed users (the development sign-in). The studio runs on an in-memory fake GitHub seeded from `content-configurations/`; against a real content branch it needs `TH_CONTENT_REPO` and an identity provider (`studio/README.md`). Roles are enforced.

The studio and one bank's runtime together: `deploy/laptop/README.md`.

## Learn it

`docs/guide/studio-guide.html` is the user guide: setting up and deploying, every feature, a training path, and a full workflow built from scratch in the web app. Its walk-through is also a browser test (`studio/test/blackbox/ui-build.test.ts`). `docs/TRAINING.md` has the training path as plain text.

## What runs today

Run in this repository on 23 November 2026, unless the row says otherwise.

| Evidence | Result |
| --- | --- |
| Spec tools at draft 0.2 (`spec/tools`) | `validate.py`: 267 documents, 0 errors, 3 warnings (L11: three artefacts no workflow reaches yet); `expand.py --check` up to date (6 workflows); 80 of 80 mutations caught; 125 stories render |
| Factory release | 1.9.0 (`spec/factory/p/pack.yaml`), seeded to `content-configurations/` |
| Factory and bank test cases on the studio interpreter | 63 of 63 (`npm run studio -- test`) |
| Static checks over the content (`npm run studio -- check`) | 0 errors, 18 warnings |
| Conformance kit, studio interpreter | 58 of 58 |
| Conformance kit, DTFx (DurableTask.Core 3.11.3) | 57 of 57 |
| Conformance kit, Elsa 3.10.2 | 57 of 57 |
| Conformance kit, Temporal (time-skipping test server) and Flowable 7.1.0 with generated BPMN | Not run for this page (Temporal's test server did not start here); CI runs both (`engines.yml`, job `kit`) |
| White-box suite (`cd studio && npm test`) | 501 pass, 1 skipped (the real-GitHub test), in 54 files |
| Black-box suites (`npm run test:blackbox`) | Not run for this page; CI runs them (`.github/workflows/engines.yml`). They cover the HTTP API, the command line, the web app in Chromium, and end to end a new scheme, WPS by configuration, WPS on Temporal, DTFx, Elsa and Flowable with the store, the WPS parity suite, the source slice, refund orders, distributions, the party register, the hot update and Flowable in production mode |
| Runtime (`cd runtime && dotnet test`) | Not run for this page; CI runs it (`runtime.yml`, `ops-console.yml`, `testbench.yml`) |
| Laptop stack (`deploy/laptop/test/insert-only.mts`) | Not run for this page. It shows a distribution switched over on Temporal on SQL Server, every row of the store unchanged, the runtime's login refused UPDATE, DELETE and TRUNCATE, another bank's distribution refused |
| End to end, a new scheme (`e2e-new-scheme.test.ts`, black box, not run for this page) | A synthetic scheme built from scratch over the API: manifest and dispatch table, port, simulator, maps with a script, rule, exchange with its subflow, and a leg in the corporate workflow's slot. The bank enables the rail in its configuration, and the payment settles end to end on the interpreter and deployed on DTFx, the new scheme on its own worker |
| End to end, WPS (`e2e-wps.test.ts`, black box, not run for this page) | The factory WPS scheme changed only by bank configuration: the rail turned off (rejected at the route) and a hook unbound (settled without it), each deployed on DTFx with four separate workers |

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
| C16 AI authoring assistant | Done except a live model run: the assistant calls the Claude API; no credentials are set here, so it was tested with an injected drafter. Gates, provenance and questions work |
| C17 Tenants, roles, maker-checker | Done: roles enforced on every write, tenant isolation suite, maker and checker differ, a hash-chained audit with diffs and refusals |
| C18 Hooks and configuration sets | Done: hooks at hook points, configuration editing with bounds and overlap checks, compatibility run, tenant export |
| C19 Packs, release, retirement, supersession | Done: pack build, review and approval with green tests, release gate, retirement against pins and an in-flight import, supersession draft, evidence and hand-over |
| C20 Exporter and Temporal binding | Done: packages and binding (expanded forms, pins, `name@release`, a separate worker per task queue, a deploy runner on DTFx and Temporal), and the kit on Temporal (`studio/engines/temporal`) |
| C21 DTFx | Done: the kit on DTFx |
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

## Not verified

- `uae.wps` stays provisional, and it is the only factory scheme since plan phase P6 (I-37). Nothing here confirms a scheme fact; samples and scripts follow the factory pack's own provisional reading.
- The ISO code-set registry was written from memory, like the factory's reason codes.
- The AI assistant has not run against a live model in this environment.
- The sandboxes keep honest scripts deterministic; they were not tested against hostile code.
