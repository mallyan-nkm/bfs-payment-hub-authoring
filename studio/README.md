# TransactHub Designer Studio

The design-time tool in which a payments workflow designer defines, tests and exports payment workflows, built from the specification in `../spec`.

Its only store is a GitHub branch (R-11-21 to R-11-25): the branch's `content-configurations/` folder, read through the GitHub REST API into memory and cached by commit SHA, and written through the Git Data API. It keeps no local copy: no workspace file, no data folder, no clone. The factory packs and the example bank are published there by the seed command; `spec/factory` stays their authoring source. From `spec/` the studio reads only its schemas and the conformance kit, which is the engines' test fixture, not content.

The studio never reads or changes a running payment (R-10-01). It has no connection to one.

## Run it

```bash
npm install
```

```bash
npm run web:build
```

```bash
TH_CONTENT_REPO=<owner>/<repo> TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=<token> npm start
```

Then open http://localhost:4317. Without `TH_CONTENT_REPO` the studio does not start. The token needs contents: write on the repository (and pull requests: write for pull-request mode); where a proxy authenticates the requests to the API host, any placeholder value will do, since the studio never checks its format; it is read from the environment at each request and never stored, shown, logged or audited. `TH_GITHUB_API` changes the API base (default `https://api.github.com`), `TH_CONTENT_ROOT` the folder on the branch (default `content-configurations`), `TH_BANKS` the bank packs to load besides the example bank (folder names under `banks/`). Set `ANTHROPIC_API_KEY` (or run `ant auth login`) for the AI assistant. `PORT` changes the port, `TH_SPEC_ROOT` the spec repository (default `../spec`).

For local development, without GitHub:

```bash
npm run studio:dev
```

It starts the fake GitHub API (`test/support/fake-github.ts`) seeded from this repository's `content-configurations/`, and the studio against it. Everything stays in that process's memory and is gone when it stops: still no local copy.

### What lives where on the branch

| Branch and path | What |
| --- | --- |
| content branch, `content-configurations/artefacts/<scope>/<name>/<release>.yaml` | Every published document, the seeded factory's among them |
| content branch, `banks/<tenant>/` | A bank pack in its own layout (the example bank is seeded here), and what the studio publishes for the bank |
| content branch, `change-sets/<id>.yaml`, `audit/change-sets/<id>.yaml` | A published change set and its audit |
| content branch, `audit/events/<date>/<time>-<action>-<hash>.yaml`, `governance/`, `supersessions/handed-over/` | Events outside a change set, users and settings, in-flight counts, handed-over supersessions |
| content branch, `scripts/`, `samples/` | Script-behinds and synthetic samples: the factory's (seeded from `studio/scripts/` and `studio/fixtures/samples/`) and a designer's |
| `studio/<change set id>` | A change set in progress: one commit per draft save |

Approval publishes the change set as one commit on the content branch, which takes its branch in as a second parent; in pull-request mode it is one commit on the change set's branch and a pull request. A published file is never rewritten. When someone else moved the branch, the studio reads it again, checks again and retries once, then stops with a message; it never force-pushes.

A change set branch is deleted once the content branch holds everything on it: in commit mode right after the publish, in pull-request mode when the studio next reads the branch and finds the pull request merged (R-11-26). The commands that only read (`check`, `test`, `kit`, the engine adapters) delete nothing, and no other branch is ever deleted. The audit files are read when the audit is opened, not at start, except the events the studio's state depends on (a retirement, a configuration set) and the newest event (R-11-27).

A new draft joins the open change set of the first draft, by change set id, that it refers to or that refers to it, else a new one. Approval publishes the closure, across branches if it must, under the id of the approved draft's branch. A branch that still holds drafts after a publish keeps them; they publish later under a new id. A waiver is a change set of its own. `governance/users.yaml` (through maker-checker) and `governance/settings.yaml` are the only files the studio replaces (Issue I-44).

### Seeding

```bash
npm run studio -- seed-content
```

Against the connected branch it publishes the factory, the example bank, and the factory's script-behinds and test samples as one change set; a second run changes nothing. A factory change after the seed needs a new release: the seed never rewrites a file on a connected branch, and stops naming the file. Without `TH_CONTENT_REPO` it reports whether this repository's `content-configurations/` holds the seed as `spec/` is now. `npm run studio -- seed-content --write-local` regenerates that folder (each seeded file's first line says `Generated by` and names its source; edit the source, not the file).

## Check it

```bash
npm test
```

The smoke test against real GitHub is skipped unless `TH_REAL_GITHUB=1`. It runs on a scratch content branch of its own, never `main`, `master` or the default branch, and refuses a branch with a commit it did not create. It seeds the branch, approves a draft in commit mode, opens a pull request in pull-request mode, and deletes only the branches it created. An empty repository gets its first commit through the Contents API, on the scratch branch. The token comes from `TH_GITHUB_TOKEN` and is never printed:

```bash
TH_REAL_GITHUB=1 TH_CONTENT_REPO=<owner>/<scratch repo> TH_CONTENT_BRANCH=th-smoke-<date> TH_GITHUB_TOKEN=<token> npx vitest run test/content.real-github.test.ts
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

```bash
npm run test:blackbox
```

| Command | What it shows |
| --- | --- |
| `npm test` | The white-box suite, in process: store, names, hashes, schemas, rule service, sandboxes, simulators, interpreter, factory tests, conformance kit, checks with mutation cases, export, every API route through `dispatch`, the party service, the deploy runner, the assistant with a fake client |
| `npm run test:coverage` | The same with line and branch coverage (V8), written to `coverage/` |
| `npm run test:blackbox` | The black-box suites, against the studio as separate processes: the HTTP API on a fresh fork of the seeded content repository in the fake GitHub, the command line by output and exit code, the web app in Chromium through Playwright (every view, at desktop and phone width), and the end-to-end test below. `TH_ENGINES=1` adds the kit on DTFx and Flowable |
| `studio -- test [ID...]` | The factory and bank test cases on the studio interpreter, with failing checks. Like `kit`, `check` and the engine adapters, it reads the connected branch when `TH_CONTENT_REPO` is set, else an in-memory fake seeded from `content-configurations/`; it says which on stderr |
| `studio -- seed-content [--write-local]` | Publish the factory and the example bank to the content branch as one change set, or regenerate `content-configurations/` |
| `studio -- kit [CK-ID...]` | The 48 conformance scenarios against the expected traces in `spec/conformance/kit.yaml` |
| `studio -- check` | Schemas and static checks over every artefact |
| `studio -- export <workflow> <target>` | The manifest of an export package |
| `studio -- export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t]` | The package as a reproducible zip, with one worker per task queue |
| `studio -- deploy <package> --workflow <name> [--inbound file [--message <start message>]] [--tenant t] [--config file] [--bench port=simulator:scenario ...]` | Deploys the package on DTFx: starts each worker as its own process, runs the workflow, prints the outcome, the traces and what each worker served |
| `npx tsx engines/dtfx/run-kit.ts` | The kit on DTFx (needs `dotnet-sdk-8.0`) |
| `engines/flowable/setup.sh && npx tsx engines/flowable/run-kit.ts` | The kit on Flowable (needs Java; fetches Maven) |

## Where things are

| Path | Component | Build spec |
| --- | --- | --- |
| `src/core/store.ts` | Artefact store: load, releases, drafts, immutable publish, maker-checker, tenancy, audit, over the content store | C01, C17 |
| `src/content/` | The content store: the GitHub client (`github.ts`), the layout of the branch (`layout.ts`), the store with its SHA cache and write queue (`content-store.ts`), the seed (`seed.ts`), the in-memory fake for development (`local.ts`, `dev.ts`) | R-11-21 to R-11-25 |
| `test/support/fake-github.ts` | The fake GitHub API every test runs on: real SHA semantics, fast-forward checks, pull requests | R-11-24 |
| `src/core/names.ts`, `canonical.ts`, `yaml.ts` | Names and scopes; RFC 8785 canonical JSON and the content hash; YAML as the spec tools read it | C01 |
| `src/core/schema.ts` | Validation by kind against `spec/schemas`, with errors a designer can act on | C02 |
| `src/checks/checks.ts` | Static checks L02 to L29, per document as it is edited and over the store; interface diff and semver | C04, C05 |
| `src/rules/` | The rule service: expression language, sessions with NRules semantics, stages, parameters by date and tenant, decision tables with gap and overlap analysis, examples, trace | C06 |
| `src/interp/sandbox.ts` | The TypeScript script sandbox and the script lint | C07 |
| `src/sim/runtime.ts` | Simulators: scenarios, conversation state, virtual-clock delays, the standard scenario set | C10 |
| `src/interp/` | The studio interpreter: virtual clock, policies, every DSL construct, front door and dispatch, hooks, signals, pins, lookup, supersession, deltas, claim-check | C09 |
| `src/interp/reference.ts` | Reference behaviour of a block that has no script-behind yet | Issue I-13 |
| `scripts/` | Authoring source of the factory's scripts-behind for the intake maps and the blocks that need real logic; the seed publishes them to the branch's `scripts/`, where the studio reads them | C24 to C26, in part |
| `src/testing/runner.ts` | Test runner for every level: ruleset, activity, hook, handler, leg, end-to-end | C11 |
| `conformance/` | Kit content in the reserved cluster `ck`: a library and one workflow per scenario | C23 |
| `src/conformance/kit.ts` | Kit runner and trace comparison; the adapter an engine replaces | C23 |
| `src/export/` | Export packages for Temporal and DTFx (interpreter) and Flowable (generated BPMN), pins, portability report, reproducible zip | C05, C20, C21, C22 |
| `src/server/` | HTTP API and static server | C12 to C19 (back end) |
| `web/` | The studio's web app | C12 to C19 (front end) |
| `fixtures/samples/` | Authoring source of the synthetic sample messages for the factory tests; seeded to the branch's `samples/` | C25, C26, in part |
| `src/core/expand.ts` | Expansion of composite steps into blocks; the form every run and engine uses | C04-S6 |
| `src/core/model.ts`, `types.ts`, `registry.ts`, `registries/` | The canonical model as data, types per activity in both languages with the type check gate, versioned code-set registries | C03 |
| `src/governance/` | Packs and manifests; roles, review and approval, retirement, propagation, supersession, configuration, compatibility run, tenant export, release gate | C05, C17, C18, C19, C23-S3 |
| `src/sim/coverage.ts`, `stub.ts` | Reason-code coverage; the deployable simulator stub | C10-S4, C10-S5 |
| `src/samples/` | Valid and invalid sample messages per scheme, each proved | C12-S4 |
| `src/ai/` | The authoring gates and the assistant on the Claude API | C16 |
| `src/conformance/party.ts`, `party-server.ts` | The party service an engine harness calls for every activity attempt | C20 to C23 |
| `engines/dtfx/`, `engines/flowable/` | The conformance kit on DTFx and on Flowable, each with its own README | C21, C22 |

## Export, workers and deploy

An export package holds, under `workers/<queue>/`, one worker per task queue of the engine binding (for example `th-uae-p` and `th-uae-wps`): a bundled `worker.cjs`, a `manifest.json` naming the tasks it serves as `name@release`, the artefacts those tasks need, their scripts and, with `--with-simulators`, the simulators of their ports. A package with simulators is marked not promotable. A worker refuses a task that is not on its queue.

`studio deploy` unpacks the package and starts every worker as a separate process. It runs the intake map on the worker that owns it, then runs the workflow on DTFx with a router that sends each activity attempt to the worker of its queue. With `--message`, the front door runs a payment end to end. Workflow-select picks the platform workflow, and the leg the dispatch table names fills its slot. The tenant's configuration set comes from the package; `--config` overrides it. Exit code 0 when the workflow ends, 1 when it fails, 2 for a mistake in the command line. `TH_WORKER_LOG=<file>` makes each worker log its attempts.

## End to end

A payment reaches a scheme leg through the front door. A file comes in. The scheme's dispatch table names its intake map and the leg it starts. `p.table.workflow-select.v1` picks the platform workflow from the payment's kind, direction and segment. The leg fills that workflow's `scheme-leg` slot. The route decision (`p.decide.rail.v1`) passes only a rail the bank has enabled in its configuration set. Two black-box tests run this whole path through the HTTP API and the command line only.

**A new scheme from scratch** (`test/blackbox/e2e-new-scheme.test.ts`). `uae.demo` is a synthetic scheme made up for the test; its documents are in `test/blackbox/demo-scheme.ts`.

1. A shared designer saves synthetic sample files, then authors:
   - the port and a simulator with the standard scenario set;
   - a rule, its rule set and the file checks;
   - the intake map with its script-behind;
   - render and acknowledgement maps and the send;
   - a composite with its subflow;
   - the leg that fills the scheme-leg slot of `p.wf.corporate.v1`;
   - the scheme manifest, whose dispatch table routes the credit file to that leg.
2. Artefacts that refer to one another are published together as one reviewed change set (Issue I-27); each set and each artefact has a different checker from its maker.
3. The composite and leg tests pass, started from the dispatch table.
4. The end-to-end test first fails at the route: the bank has not enabled the rail. Then the bank releases configuration 1.1.0, which accepts the scheme and enables the rail, and the payment settles.
5. The corporate workflow is exported for the bank with simulators. The new scheme gets its own worker, `th-uae-demo`, with the queue derived from its scope.
6. Deployed on DTFx with `studio deploy --message uae.demo.credit-file`, three cases run:
   - an acknowledged file is paid;
   - a refused file is rejected and its reservation undone;
   - a file in dollars stops at the file checks.

   Every worker runs as its own process and serves only its queue.

**The factory WPS scheme, changed only by configuration** (`test/blackbox/e2e-wps.test.ts`). WPS is factory content, so the bank authors nothing in it.

1. As shipped, a salary file settles. It passes through the corporate workflow, the WPS leg in its slot and the bank's hooks.
2. The bank is refused when it sets a scheme limit above its bound, reuses a release, or acts as a checker where an author is needed.
3. Configuration 1.1.0 turns the WPS rail off: the payment is rejected at the route and its reservation undone.
4. Configuration 1.2.0 turns WPS back on and unbinds the pre-release hook: the payment settles without it.
5. Each configuration runs on the interpreter, and is exported for the bank and deployed on DTFx. The configuration set travels in the package. There are four separate workers: `th-bank`, `th-p`, `th-uae-p` and `th-uae-wps`.
6. The audit shows no WPS artefact was touched.

## How a run works

A test names its target, level and bench. The runner builds a `Run`: a virtual clock starting at the evaluation date, one simulator instance per bench port, and the tenant's configuration set. Inward simulators send first; their messages reach the front door, which runs the intake map, picks the platform workflow from `p.table.workflow-select.v1`, and starts it with the leg the dispatch table names. Each step runs with its effective policy (type default, then activity, then step). An activity runs its script-behind if one exists, otherwise its reference behaviour, and reaches outside only through its declared ports, which the bench binds to simulators. Time moves only when every coroutine is blocked, so a wait of days ends at once and a run is deterministic.

The run record keeps, per instance: the business trace, the release trace, every step with its attempts, effective policy and source of each value, the delta it wrote and the release that ran, the rule traces, the pins and bindings taken at start, any supersession applied, and the engine state, which holds only `header`, `facts` and message references.

See `../docs/ISSUES.md` for where the spec was silent or contradictory and what the studio does, and `../docs/adr/0001-studio-stack.md` for the stack.
