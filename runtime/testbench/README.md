# The runtime test bench

Send data; it runs on the engines a profile names, each with the store on its database, and each run is compared with the studio interpreter. A CLI, a small HTTP service of its own (never the production API), and a web UI.

```
cd runtime
dotnet build tools/TransactHub.TestBench
alias testbench='dotnet tools/TransactHub.TestBench/bin/Debug/net10.0/testbench.dll'

testbench run --profile wps-all-engines --file salary.sif         # four engines and the interpreter, the store on PostgreSQL
testbench run --profile wps-all-engines --sample wps-sif-nak      # a ready sample from the library
testbench suite testbench/suite --databases SqlServer             # the shipped regression suite, on SQL Server
testbench data build --template wps-sif --tenant dib --lines 500 --out big.sif
testbench serve                                                   # the service and the UI on http://127.0.0.1:4380
```

It needs what the engines need: Docker, the .NET 8 and 10 SDKs, Java 17 (Temporal's test server), Node 22 and `cd studio && npm install`. Run `runtime/tools/temporal-test-server/setup.sh` once, or the bench runs it.

## What a run does

1. **The request** names a profile and the data. Everything else comes from the profile, and the request may change any of it.
2. **The reference.** The studio interpreter runs the data (`runTest`, as the studio's bench does).
3. **Each engine on each database.** For each engine the bench exports the workflow's package from the content branch for the tenant, with its simulators (or takes a stored release or a zip, below), gives the engine a database of its own on the server, and runs the studio's deploy runner (`studio/src/deploy/deploy.ts`): one worker process per task queue, the front door, and the engine's harness (`runtime/tools/TransactHub.{Temporal,Dtfx,Elsa,Flowable}Harness`). The harnesses are used as they are. Each engine runs in a driver process of its own with a time limit (`environment.engineTimeoutMinutes`, default 15), so one that hangs is stopped and reported.
4. **The store.** The harness reports every payload rebuilt from its facts. The bench then reads the database itself through EF Core: the ISO 20022 rows (bulks and payments: ids, amounts, counts, statuses; never a party or an account) and every fact chain verified.
5. **The comparison.** Each engine against the interpreter: the same outcome and the same trace, or the diff. The detail (each subflow's steps) is shown, not judged, as in the parity suites (R-09-07).
6. **The result**: `local/runs/<id>/result.json`, `report.md` and `report.html`. The CLI exits 1 on any mismatch, an engine that did not run, a store that does not rebuild, or an expectation not met; 2 on a mistake in the command line.

The driver is `driver/bench.ts`, run with the studio's `tsx`. The content is the branch at `TH_CONTENT_REPO` when it is set, else the in-memory fake GitHub seeded from `content-configurations/`, as for every studio command.

## The request

| Field | What |
| --- | --- |
| `profile` | A file in `profiles/`. Required. |
| `data` | `file` (a path), `text`, `base64` (with `name`), `sample` (a library id), `build` (a builder recipe) or `context` (a canonical payment). Required. |
| `workflow`, `message`, `tenant` | The workflow to run, its start message, the bank. A tenant other than the example bank loads its own folder from `content-configurations/banks/`. |
| `engines` | Any of `temporal`, `dtfx`, `elsa`, `flowable`. The interpreter always runs as the reference (`reference: false` turns it off). |
| `databases` | `PostgreSql`, `SqlServer`, or `none` (no store: DTFx and Elsa then run the studio's own harnesses). A list runs each engine on each. |
| `bench`, `ports` | A named bench from `benches.yaml`, and `port=simulator:scenario` to change one port. |
| `clock.date` | The evaluation date. The run starts at 06:00 that day. Values with a `validFrom` are in force by this date (DIB's are from 2026-11-21). |
| `environment.temporal` | `test-server` (the time-skipping server the harness starts) or `host:port` of a Temporal server, run in real time. `TH_TEMPORAL_ADDRESS` overrides it. |
| `environment.flowable` | `container` (a Flowable REST 7.1.0 container, its async executor off) or the URL of a Flowable REST server. `TH_FLOWABLE_URL` overrides it. |
| `environment.connections` | `{ PostgreSql: <connection>, SqlServer: <connection> }` to use a server of yours; each engine run gets a database `tb_<run>_<engine>` on it. `TH_TESTBENCH_POSTGRESQL` and `TH_TESTBENCH_SQLSERVER` do the same. Without one, a Testcontainers container is started for the run and removed after it. |
| `environment.engineTimeoutMinutes` | One engine's time limit. |
| `source` | `content` (default: the published release on the content branch; `release` checks it is the one named), `{ kind: store, release, store: { provider, connection } }` (the packages the release API stored, one per engine, scope `<workflow>/<engine>`), or `{ kind: packages, package: <zip> }` / `{ packages: { dtfx: <zip>, … } }` (exported zips). A promotable package has no simulators (R-07-12): for the test run only, the bench adds the bank's simulators of the ports each worker reaches to an unpacked copy. |
| `expect` | `outcome`, `trace`, `store: { bulks, payments }`: checked on the interpreter and on every engine. |

On the command line: `--profile`, `--file|--sample|--text`, `--bench`, `--port` (repeatable), `--engines a,b`, `--databases a,b`, `--tenant`, `--workflow`, `--message`, `--date`, `--temporal`, `--flowable`, `--timeout`, `--package`, `--store-release` (with `TH_RELEASE_STORE`), `--expect`, `--save <case-id>`, `--json`.

## Profiles

| Profile | Engines | Database | Data |
| --- | --- | --- | --- |
| `wps-all-engines` | the four | PostgreSQL | WPS SIF, outward, through the front door and `p.wf.corporate.v1` |
| `wps-all-engines-sqlserver` | the four | SQL Server | the same |
| `wps-all-engines-both-databases` | the four | both, in turn | the same |
| `wps-inward-all-engines` | the four | PostgreSQL | WPS PIF, inward, `uae.wps.wf.agent-payment-in.v1` |
| `wps-inward-all-engines-sqlserver` | the four | SQL Server | the same |
| `wps-reference`, `wps-inward-reference` | none | none | the interpreter only, in seconds |
| `wps-quick-dtfx` | DTFx | none | a quick check while working |
| `platform-iso-outward` | the four | PostgreSQL | pain.001 or a canonical JSON payment, as the corporate workflow's context |
| `platform-iso-inward` | the four | PostgreSQL | pacs.008, as the inward workflow's context |

A profile is plain YAML (kind `testbench-profile`); a bank adds its own beside these.

**Benches** (`benches.yaml`): `wps-happy`, `wps-nak`, `sanctions-hit`, `sanctions-hit-released`, `fraud-hit`, `wps-duplicate`, `ops-decline`, `ops-approve`, `wps-inward-happy`, `wps-inward-returns`, `wps-inward-returns-refused`, `wps-inward-all-returned`, `wps-inward-duplicate`, `platform-happy`. A bench may extend another. `testbench benches` prints each with its ports.

## Test data

All of it is synthetic. Never import production data.

**The library** (`samples/library.yaml`) lists ready samples, each tagged with its scheme, direction, scenario, bench, expected outcome and the profiles it suits: the WPS salary file happy path, NAK, sanctions hit, duplicate file, three records, invalid IBAN, bad control total, bad pay period, repeated person id, 10,000 records (built when used), the PIF happy path, partial returns, all returned, wrong agent, and pain.001, pacs.008 and canonical JSON for the platform. `testbench data list`, `GET /api/data/library`, or the UI's Test data page.

**The builder** (`testbench data build`, `POST /api/data/build`, the UI) makes data from a template (`wps-sif`, `wps-pif`, `pain.001`, `pacs.008`) and parameters: `tenant` (its WPS routing code comes from its configuration set, `uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes`), `employerId`, `lines`, `amount` or `amounts: { min, max }`, `valueDate`, `currency`, `seed` (the same recipe builds the same data), and `inject`: `invalid-iban`, `bad-control-total`, `bad-record-count`, `bad-pay-period`, `bad-employer-id`, `bad-currency`, `duplicate-person-id`, `wrong-agent`, `sanctioned-name`, `duplicate-file`. Without an injection the data is valid for the bank. A bank without a WPS routing code (ADIB, ENBD and FAB today, Issue I-86) gets no WPS file.

How the builder keeps data synthetic: every account is under bank code `999`, which no UAE bank has, with valid check digits; ids carry a `0000` prefix; party names start with `TB `; amounts are quarters of a dirham, because the factory's rules add money as binary floating point and a sum of quarters is exact (Issue I-96).

**Validation** (`testbench data validate`, `POST /api/data/validate`): the bench's format check (the records the intake maps read, field formats, IBAN check digits, counts and totals, the file name, the agent of a PIF) and the synthetic check; `--rules` also runs the data on the interpreter through a profile, which applies the scheme's own rules. The format check is a quick check, not the scheme's word, and `uae.wps` is provisional.

**Saving** a sample (`data build --save <id>`, `POST /api/data/samples`, the UI) writes it to `local/samples/` and refuses data that looks real (`synthetic.yaml`): a valid IBAN outside the reserved bank codes (the factory's own sample IBANs are allowed by name), an Emirates ID number, an e-mail address outside the RFC 2606 domains, a UAE phone number, an ISO party name that does not start with `TB `, or a name on the deny list. **Its limits:** it cannot tell a real name from an invented one, so names are held to the prefix; it does not know other countries' national ids; a real account with wrong check digits passes as an invalid one; and it checks what is saved, not what is pasted into a run (which is never kept beyond the run's own folder).

What the data cannot drive: the simulators decide by their scenario, not by the data, and no name enters workflow state; so `sanctioned-name` goes with the `sanctions-hit` bench, and a duplicate file is the `wps-duplicate` bench (Issue I-96).

## Regression cases and suites

A case is a request and what it is expected to give (kind `testbench-case`). Save one from a run (`testbench run … --save TB-BANK-001`, `testbench save <run-id> --id TB-BANK-001`, `POST /api/runs/{id}/case`, or the UI): the interpreter's outcome and trace and the store's row counts become what is expected. Replay a folder: `testbench suite <folder> [--engines …] [--databases …] [--report suite.md]`. A bank keeps its own folder; saved cases go to `local/cases/`.

A case may carry `knownIssue: { id, engines, note }`: those engines are known to differ (an Issue in `docs/ISSUES.md`). The case still fails on anything else, and it fails when a named engine starts to agree, so the mark comes off when the issue is fixed.

The shipped suite (`suite/`, 20 cases) runs in CI on PostgreSQL and on SQL Server (`.github/workflows/testbench.yml`): the WPS outward cases on the four engines; each builder injection, run on the four engines with the outcome it is meant to give; DIB's file built with its routing code, on the day its configuration is in force; the WPS inward cases (known to differ on the engines, I-94); and the platform's ISO 20022 and JSON cases (I-95 for pacs.008).

Temporal on the time-skipping test server with the store runs its claim checks as regular activities (`TH_LOCAL_CLAIMCHECK=0`): as local activities the server lets time pass while they write to the store, and the first attempt times out (Issue I-93). The bench sets it for that combination only.

## The service and the UI

`testbench serve [--urls http://127.0.0.1:4380]` starts the service; the UI (`web/`, React and Vite, the studio web app's stack) is built with `cd testbench/web && npm ci && npm run build` and served at `/`. One job runs at a time; containers are kept for the service's life.

| Endpoint | What |
| --- | --- |
| `POST /api/runs` `{ profile, data, … }` | Queues a run: 202 and its id. `?wait=true` answers when it is done. |
| `GET /api/runs`, `GET /api/runs/{id}`, `GET /api/runs/{id}/report.html` | History, a run with its progress, its report. |
| `POST /api/runs/{id}/case` `{ id, title }` | Saves the run as a regression case. |
| `GET /api/cases`, `POST /api/suites` `{ suite: shipped|saved, databases? }`, `GET /api/suites/{id}` | Cases and suites. |
| `GET /api/profiles`, `GET /api/benches` | What a run can use. |
| `GET /api/data/library[/{id}]`, `GET /api/data/templates`, `POST /api/data/build`, `POST /api/data/validate`, `POST /api/data/samples` | Test data. |

The UI: pick a profile and change its engines, database, tenant and simulators; choose a library sample, upload a file or paste a message; run and watch each engine's progress; see the outcome, timings, store and trace of each engine beside the interpreter's, with the diff; save it as a case; run a suite; see the history; and build, edit, validate and save test data.

**Sign-in.** The service keeps no user and no password. People sign in at the bank's OIDC identity provider (authorization code with PKCE, a public client, in the UI); the API takes the provider's bearer JWTs, from one issuer. The bank's claims become roles through the runtime's role map (kind `runtime-role-map`, the same file format as the runtime's API): `tester` runs the bench; `Auth:AllowedRoles` may also let `operator` or `supervisor`. A tenant the token is not given is refused. Settings: `Auth:Oidc:Issuer`, `Auth:Oidc:Audience`, `Auth:Oidc:ClientId` (default `transacthub-runtime-ui`), `Auth:Oidc:Scope`, `Auth:Oidc:Authority`, `Auth:RoleMap` (JSON or `file:<path>`), `Auth:AllowedRoles`; as environment variables, `Auth__Oidc__Issuer` and so on. The service does not start without them.

**One sign-in with the operations console.** The simplest: register one public client at the bank's provider (`transacthub-runtime-ui`) with the redirect URIs of both UIs, and one audience for both APIs; the console and the bench take the same settings. A person signed in to one is signed in to the other by the provider's own session, and the same role map gives each host its roles. Serving both from one host under two paths is the next step, when the console is merged.

**Developers.** `TH_AUTH_DEV=1` with `ASPNETCORE_ENVIRONMENT=Development` turns on a developer identity provider inside the service (`/dev-idp`): the same code flow with PKCE and RS256 tokens from a key made at start, so the UI and the API run the code they run against the bank's provider. Whoever signs in names themselves and their groups (`auth/dev-role-map.json`). It is refused in Production, which is the default environment.

**The seam to the shared auth library.** `IBenchAuth` (`Auth.cs`) is the one place that wires authentication. When the runtime's auth library (`TransactHub.Auth`) is merged, `OidcBenchAuth.Add` becomes a call to its `AddRuntimeAuth` with this host's roles, and `BenchRoleMap` goes; the claim types (`th:role`, `th:tenant`) and the role-map format are already the library's.

## Tests

| What | Where | Run |
| --- | --- | --- |
| Profiles, benches, library and cases resolve; the input reader and the ISO mapping; the builder for each bank, each injection's finding, the synthetic check; the judge and the reports; the service's sign-in, role map, Tester policy and tenant check | `runtime/tests/TransactHub.TestBench.Tests` | `cd runtime && dotnet test TransactHub.Runtime.slnx` |
| Sign in through the code flow, run a profile, see the results, save a case; the builder; a caller without the role refused | `web/test/ui.test.mjs` (Playwright) | `cd runtime/testbench/web && npm run build && npm run test:ui` |
| The shipped suite on the four engines and the interpreter, on each database | `suite/` | `testbench suite testbench/suite --databases PostgreSql` (and `SqlServer`) |
