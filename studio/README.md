# TransactHub Designer Studio

The design-time tool in which a payments workflow designer defines, tests and exports payment workflows, built from the specification in `../spec`. It reads the spec repository directly: the factory pack, the example bank, the schemas and the conformance kit manifest are its content.

The studio never reads or changes a running payment (R-10-01). It has no connection to one.

## Run it

```bash
npm install
```

```bash
npm run web:build
```

```bash
npm start
```

Then open http://localhost:4317. Set `ANTHROPIC_API_KEY` (or run `ant auth login`) for the AI assistant. `PORT` changes the port, `TH_SPEC_ROOT` the spec repository (default `../spec`), `TH_DATA_ROOT` the workspace for drafts, releases and the audit log (default `studio/data`).

## Check it

```bash
npm test
```

```bash
npm run studio -- test
```

```bash
npm run studio -- kit
```

```bash
npm run studio -- check
```

```bash
npm run studio -- export p.wf.commercial.v1 flowable
```

| Command | What it shows |
| --- | --- |
| `npm test` | The automated suite: store, names, hashes, schemas, rule service, sandbox, simulators, interpreter, factory tests, conformance kit, checks with mutation cases, export |
| `studio -- test [ID...]` | The factory and bank test cases on the studio interpreter, with failing checks |
| `studio -- kit [CK-ID...]` | The 43 conformance scenarios against the expected traces in `spec/conformance/kit.yaml` |
| `studio -- check` | Schemas and static checks over every artefact |
| `studio -- export <workflow> <target>` | The manifest of an export package |
| `npx tsx engines/dtfx/run-kit.ts` | The kit on DTFx (needs `dotnet-sdk-8.0`) |
| `engines/flowable/setup.sh && npx tsx engines/flowable/run-kit.ts` | The kit on Flowable (needs Java; fetches Maven) |

## Where things are

| Path | Component | Build spec |
| --- | --- | --- |
| `src/core/store.ts` | Artefact store: load, releases, drafts, immutable publish, maker-checker, tenancy, audit | C01, C17 |
| `src/core/names.ts`, `canonical.ts`, `yaml.ts` | Names and scopes; RFC 8785 canonical JSON and the content hash; YAML as the spec tools read it | C01 |
| `src/core/schema.ts` | Validation by kind against `spec/schemas`, with errors a designer can act on | C02 |
| `src/checks/checks.ts` | Static checks L02 to L29, per document as it is edited and over the store; interface diff and semver | C04, C05 |
| `src/rules/` | The rule service: expression language, sessions with NRules semantics, stages, parameters by date and tenant, decision tables with gap and overlap analysis, examples, trace | C06 |
| `src/interp/sandbox.ts` | The TypeScript script sandbox and the script lint | C07 |
| `src/sim/runtime.ts` | Simulators: scenarios, conversation state, virtual-clock delays, the standard scenario set | C10 |
| `src/interp/` | The studio interpreter: virtual clock, policies, every DSL construct, front door and dispatch, hooks, signals, pins, lookup, supersession, deltas, claim-check | C09 |
| `src/interp/reference.ts` | Reference behaviour of a block that has no script-behind yet | Issue I-13 |
| `scripts/` | Scripts-behind for the intake maps and the blocks that need real logic | C24 to C26, in part |
| `src/testing/runner.ts` | Test runner for every level: ruleset, activity, hook, handler, leg, end-to-end | C11 |
| `conformance/` | Kit content in the reserved cluster `ck`: a library and one workflow per scenario | C23 |
| `src/conformance/kit.ts` | Kit runner and trace comparison; the adapter an engine replaces | C23 |
| `src/export/` | Export packages for Temporal and DTFx (interpreter) and Flowable (generated BPMN), pins, portability report, reproducible zip | C05, C20, C21, C22 |
| `src/server/` | HTTP API and static server | C12 to C19 (back end) |
| `web/` | The studio's web app | C12 to C19 (front end) |
| `fixtures/samples/` | Synthetic sample messages for the factory tests | C25, C26, in part |
| `src/core/expand.ts` | Expansion of composite steps into blocks; the form every run and engine uses | C04-S6 |
| `src/core/model.ts`, `types.ts`, `registry.ts`, `registries/` | The canonical model as data, types per activity in both languages with the type check gate, versioned code-set registries | C03 |
| `src/governance/` | Packs and manifests; roles, review and approval, retirement, propagation, supersession, configuration, compatibility run, tenant export, release gate | C05, C17, C18, C19, C23-S3 |
| `src/sim/coverage.ts`, `stub.ts` | Reason-code coverage; the deployable simulator stub | C10-S4, C10-S5 |
| `src/samples/` | Valid and invalid sample messages per scheme, each proved | C12-S4 |
| `src/ai/` | The authoring gates and the assistant on the Claude API | C16 |
| `src/conformance/party.ts`, `party-server.ts` | The party service an engine harness calls for every activity attempt | C20 to C23 |
| `engines/dtfx/`, `engines/flowable/` | The conformance kit on DTFx and on Flowable, each with its own README | C21, C22 |

## How a run works

A test names its target, level and bench. The runner builds a `Run`: a virtual clock starting at the evaluation date, one simulator instance per bench port, and the tenant's configuration set. Inward simulators send first; their messages reach the front door, which runs the intake map, picks the platform workflow from `p.table.workflow-select.v1`, and starts it with the leg the dispatch table names. Each step runs with its effective policy (type default, then activity, then step). An activity runs its script-behind if one exists, otherwise its reference behaviour, and reaches outside only through its declared ports, which the bench binds to simulators. Time moves only when every coroutine is blocked, so a wait of days ends at once and a run is deterministic.

The run record keeps, per instance: the business trace, the release trace, every step with its attempts, effective policy and source of each value, the delta it wrote and the release that ran, the rule traces, the pins and bindings taken at start, any supersession applied, and the engine state, which holds only `header`, `facts` and message references.

See `../docs/ISSUES.md` for where the spec was silent or contradictory and what the studio does, and `../docs/adr/0001-studio-stack.md` for the stack.
