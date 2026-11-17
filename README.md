# TransactHub Designer Studio

The design-time tool in which a payments workflow designer defines, scripts, tests and exports payment workflows built from composable blocks over one ISO 20022 canonical context. One multi-tenant application; each bank is a tenant that shares platform and scheme content, sets configuration values and authors hooks.

| Folder | What it is |
| --- | --- |
| `spec/` | The specification set as delivered on 17 November 2026: design parts and annexes, schemas, the factory pack, the example bank, the conformance kit, the build backlog and the spec's own tools. The re-ideation plan (`.docx`) and its summary sit beside them. |
| `studio/` | The studio, built from the spec. See `studio/README.md`. |
| `docs/` | The stack decision record and the issues found in the spec while building. |

## Start

```bash
cd studio && npm install && npm run web:build && npm start
```

Then open http://localhost:4317. Switch tenant in the header between `owner` (shared content) and `example-bank`.

## What runs today

| Evidence | Result |
| --- | --- |
| Spec documents loaded and valid against their schemas | 184 of 184 |
| Content hashes (RFC 8785) equal to the pack manifests | 170 of 170 |
| Rule and table examples | 60 of 60 |
| Conformance kit on the studio interpreter | 43 of 43 scenarios |
| Factory and bank test cases on the studio interpreter | 25 of 26; WPS-IN-001 cannot pass as written (Issue I-01) |
| Static checks over the spec | 0 errors, the 2 expected L10 warnings, and warnings from the checks built here (L19, L21, L22, L25) |
| Automated test suite (`cd studio && npm test`) | 74 tests, green |
| Web app | Driven end to end with Playwright through every view |
| Spec tools (`spec/tools`) | validator 0 errors, 21 of 21 mutations caught, unchanged by this work |

## Coverage of the build backlog

The spec's process is one story per branch and pull request. This build was done on one branch, as one walking skeleton across the components, so read it as a first pass to review at G1, not as accepted stories.

| Component | State |
| --- | --- |
| C01 Artefact store and naming | Built: loader, names and scopes, canonical JSON and hash, drafts, immutable publish, releases of a line |
| C02 Schema validation | Built: by kind, errors a designer can act on, command line check |
| C03 Canonical model and type generation | Partly: context paths, extension fields and readers and writers in the ISO data browser. Not built: generated TypeScript and Python types, code-set registries |
| C04 Static checks | Built: the reference checks, plus L19, L20, L21, L22 (bench half), L23, L25, L28, L29, per document on every edit. L24 has nothing to flag: DSL 1.0.0 has no extension constructs |
| C05 Pins, manifests and semver | Built: closure and `artefacts` block, interface diff and proposed release (L26). Not built: dependency release propagation (C05-S4) |
| C06 Rule service | Built: expression language, sessions with NRules semantics, stages, parameters by date and tenant, decision tables, examples, trace. The evaluator is TypeScript, not NRules (D13 open) |
| C07, C08 Script sandboxes | Built for TypeScript and Python, with contract enforcement, determinism and a parity check. Not a security boundary (D14 open) |
| C09 Studio interpreter | Built: every construct of the portable core, virtual clock, policies, hooks, signals, pins, lookup, supersession, deltas, claim-check |
| C10 Simulator runtime | Built: outward and inward scenarios, conversation state, standard scenario check. Not built: self-check of emitted messages, deployable stub |
| C11 Test runner | Built for every level, with the run record the bench shows |
| C12 to C15 Catalogue, editors, designer, test bench | Built as a web app: catalogue and artefact pages, ISO data and scheme catalogues, workflow designer (YAML with checks on every edit, a structured outline, interface diff, drafts, maker-checker publish), test bench with timeline, deltas, rule traces and an ad hoc bench, the kit, tenant configuration, export, governance. Not built: form editors for rules, tables, maps and simulators (they show read-only), sample generation (F-05), a drag-and-drop canvas |
| C16 AI authoring assistant | Partly: the gates every draft must pass (`POST /api/gates`). No model is connected |
| C17 Tenants, roles, maker-checker | Built: tenant isolation, shared content read-only to a bank, maker and checker must differ, audit log. Roles are named, not enforced by login |
| C18 Hooks and configuration sets | Built: hooks at hook points with timeout and failure mode, configuration values in bounds, compatibility run, tenant export |
| C19 Packs, release, retirement, supersession | Partly: publish through maker-checker, supersession test. Not built: pack build, retirement |
| C20 to C22 Export | Built: packages for Temporal and DTFx (interpreter) and Flowable (generated BPMN), reproducible, with pins, binding, tests and a portability report. Not run on any engine: no engine is part of this build |
| C23 Conformance kit and runner | Built: kit content in cluster `ck`, runner and trace comparison, 43 of 43 on the studio |
| C24 to C26 Factory content | Partly: synthetic samples for every factory test, six scripts-behind; the other blocks run a reference behaviour by type (Issue I-13) |

## Not verified

- `uae.wps` and `uae.aani` stay provisional. Nothing in this build confirms a scheme fact; the samples and scripts follow the factory pack's own provisional reading.
- No engine was run. The Temporal, DTFx and Flowable packages are generated and checked by the studio only.
- The sandboxes keep honest scripts deterministic; they were not tested against hostile code.
