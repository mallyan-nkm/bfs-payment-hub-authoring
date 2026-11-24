# Test strategy

Which suites prove the north star, and what each showed on 2026-11-23. It replaces the source's test-case catalogue, which listed unit and integration cases per source service. The rules for writing a test are [../guidelines/testing.md](../guidelines/testing.md).

## The levels

| Level | Proves | Suite |
| --- | --- | --- |
| Spec | Every document is valid; every check fires when the repository is broken on purpose | `spec/tools/validate.py`, `mutation_tests.py`, `expand.py --check` |
| Studio, white-box | Each component in process, every API route through `dispatch` | `studio/test/*.test.ts` (`npm test`) |
| Studio, black-box | The studio as separate processes: HTTP API, command line, web app in Chromium, end to end | `studio/test/blackbox/` (`npm run test:blackbox`) |
| Content | The factory's and the example bank's own test cases | `npm run studio -- test` |
| Conformance | The same expected traces (59 scenarios) on the interpreter and on each engine | `npm run studio -- kit`; `studio/engines/*/run-kit.ts` |
| Runtime | The store on both databases, neutrality, bank configuration, release API, native activities, the File API and the sftp adapter, the engine hosts, the operations and service consoles, secrets and signatures, the test bench | `runtime/tests/*` (`cd runtime && dotnet test`) |
| Deployment | The laptop stack: a switch-over on Temporal on SQL Server, insert-only at the database, another bank refused | `deploy/laptop/test/insert-only.mts` |
| Parity | The runtime's WPS rules answer the source's own code exactly | `runtime/tests/TransactHub.Store.Tests/Golden` (1,977 cases), `docs/runtime/PARITY.md` |
| Engine persistence | Temporal's own persistence and visibility suites on SQL Server; a crash mid-workflow | `runtime/temporal-mssql/tests`, `scripts/durability-check.sh` |

There are no load, soak or back-pressure tests in the studio (`spec/CLAUDE.md`). The runtime has a load harness outside the studio (`runtime/load/`, R-09-38): synthetic WPS salary files on Temporal, on each Temporal persistence store and each store database. It runs before any bank goes live; its first results and the proposed targets are in [`docs/runtime/LOAD-TEST.md`](../../runtime/LOAD-TEST.md), and the targets themselves wait on the owner ([I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side)). Telemetry has its own tests: spans, metrics and masking on a WPS run with in-memory exporters (R-09-31 to R-09-37).

## What ran here on 2026-11-23

Run in this repository while writing these docs, on Linux with Node 22, .NET 10 and .NET 8, and Docker.

| Command | Result |
| --- | --- |
| `cd spec && python3 tools/validate.py` | 267 documents, 0 errors, 3 warnings (L11) |
| `cd spec && python3 tools/expand.py --check` | Up to date: 6 workflows |
| `cd spec && python3 tools/mutation_tests.py` | 80 of 80 mutations caught |
| `cd studio && npm test` | 501 pass, 1 skipped (the real-GitHub test), in 54 files |
| `cd studio && npx tsc --noEmit -p .` | No errors |
| `cd studio && npm run studio -- test` | 63 of 63 tests pass |
| `cd studio && npm run studio -- kit` | 59 of 59 on the studio interpreter |
| `cd studio && npm run studio -- check` | 0 errors, 18 warnings |
| `cd studio && npm run studio -- seed-content` | 295 of 295 seeded files current |
| `cd studio && npx tsx engines/dtfx/run-kit.ts` | 57 of 57 on DTFx |
| `cd studio && npx tsx engines/elsa/run-kit.ts` | 57 of 57 on Elsa |
| `cd studio && npx tsx engines/temporal/run-kit.ts` | Not run: the test server did not start here |

The runtime's `dotnet test`, the black-box suites, the Flowable kit, the laptop stack and Temporal on SQL Server were not run for this page; CI runs them (below), and `runtime/temporal-mssql/README.md` has the plugin's proof.

## In CI

| Workflow | Jobs |
| --- | --- |
| `.github/workflows/studio.yml` | Typecheck, `npm test`, `check`, `seed-content` |
| `.github/workflows/runtime.yml` | Build and `dotnet test` of the runtime solution |
| `.github/workflows/engines.yml` | `blackbox`, `kit` (four engines), `store` (three engines on two databases), `flowable-production`, `wps-parity`, `temporal-sqlserver` |
| `.github/workflows/ops-console.yml` | The operations and service consoles' tests, the web app's tests, Playwright end to end |
| `.github/workflows/testbench.yml` | The test bench's shipped suite on each database |
| `.github/workflows/security.yml` | Secret scan, literal secrets, `npm audit`, `dotnet` and Go vulnerabilities, pinned actions |

## Where the source's test cases went

| Source cases for | North star |
| --- | --- |
| Gateway, IAM | Release API, runtime authentication and role map tests (`ApiAuthTests`, `OperatorApiTests`, `ServiceApiTests`) |
| Routing | Dispatch and workflow-select tests in the studio; the end-to-end tests |
| Workflow engine | The conformance kit on each engine |
| Business rules | Rule examples and rule-set tests in the studio; WPS rules golden in the runtime |
| Message conversion | Map tests; proved samples per scheme |
| Settlement | Lifecycle step tests, with the core-banking simulator |
| Audit | Store chain tests; the studio's audit tests |
| Configuration and multi-tenancy | Tenant isolation, configuration bounds and overlap tests; `BankConfigurationTests` |
| Integration tests per service | The end-to-end tests: a new scheme from scratch, and WPS changed only by configuration |
