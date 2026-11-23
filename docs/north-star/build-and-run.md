# Build and run

The commands of this repository. It replaces the source's `build-and-run.md`, whose solution, PowerShell scripts and compose stack are not part of the north star. `CLAUDE.md` holds the short list; this page adds what each needs and what it showed when run here.

"Ran here" means run in this repository on 2026-11-23, on Linux with Node 22, Python 3, .NET 10 and .NET 8, Java, Go and Docker, while this page was written.

## Needs

| Tool | For |
| --- | --- |
| Node.js 22 | The studio (`studio/package.json` asks for 22 or later) |
| Python 3 with `spec/tools/requirements.txt` | The spec's tools |
| .NET 10 SDK | The runtime (`runtime/global.json`) |
| .NET 8 SDK | The DTFx and Elsa harnesses |
| Java and Maven | The Flowable harness (`engines/flowable/setup.sh` fetches Maven) |
| Docker | The runtime's tests (SQL Server, PostgreSQL, Temporal, Flowable, Vault, LocalStack and SFTP in Testcontainers), Temporal on SQL Server, one kit scenario on Temporal, the laptop stack |
| Go (the version `runtime/temporal-mssql/go.mod` names) | Temporal on SQL Server |

## The spec

```
cd spec
pip install -r tools/requirements.txt
python3 tools/build_factory.py     # regenerate the factory's generated files
python3 tools/validate.py          # schemas and static checks; exit 1 on any error
python3 tools/expand.py            # write the expanded form of each workflow that calls a composite
python3 tools/mutation_tests.py    # break the pack once per check; every case must be caught
python3 tools/build_docs.py        # check the backlog; render build specs and generated blocks
```

Run all five in that order before a pull request that touches `spec/` (`spec/CLAUDE.md`). `python3 tools/expand.py --check` fails when `expanded/` is out of date. After `build_factory.py` changes the factory, run `npm run studio -- seed-content --write-local` in `studio/`.

| Ran here | Result |
| --- | --- |
| `validate.py` | 267 documents, 0 errors, 3 warnings (L11) |
| `expand.py --check` | Up to date (6 workflows) |
| `mutation_tests.py` | 80 of 80 caught |
| `build_docs.py` | 125 stories, 6 generated blocks |

## The studio

```
cd studio
npm install
npm test                                  # the white-box suite
npm run typecheck
npm run studio -- test                    # factory and bank tests on the interpreter
npm run studio -- kit                     # the conformance kit on the interpreter
npm run studio -- check                   # schemas and static checks
npm run studio -- seed-content            # is content-configurations/ the seed as spec/ is now?
npm run test:blackbox                     # API, command line, web app in Chromium, end to end
```

`test`, `kit` and `check` read the branch named by `TH_CONTENT_REPO` when it is set, and otherwise an in-memory fake GitHub seeded from this repository's `content-configurations/`. They say which on stderr.

| Ran here | Result |
| --- | --- |
| `npm test` | 501 pass, 1 skipped (the real-GitHub test), in 54 files |
| `npx tsc --noEmit -p .` | No errors |
| `studio -- test` | 63 of 63 |
| `studio -- kit` | 57 of 57 |
| `studio -- check` | 0 errors, 18 warnings |
| `studio -- seed-content` | 295 of 295 seeded files current |
| `npm run test:blackbox` | Not run here |

### Run the studio

For development, with no GitHub:

```
cd studio
npm run studio:dev
```

It starts a fake GitHub in memory, seeded from `content-configurations/`, and the studio against it on http://localhost:4317 (`PORT` changes it). Nothing is written to disk.

Against a real branch:

```
cd studio
npm run web:build
TH_CONTENT_REPO=<owner>/<repo> TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=<token> npm start
```

`web:build` ran here. `npm start` against a real branch was not run here: it needs a token. Sign in: `npm run studio:dev` uses the built-in development sign-in (`TH_AUTH_DEV=1`); a shared installation names its identity provider with `TH_OIDC_ISSUER` and `TH_OIDC_CLIENT_ID` (`studio/README.md`, "Sign-in and roles").

### Export, deploy and import

```
npm run studio -- export p.wf.commercial.v1 flowable                       # the manifest of a package
npm run studio -- export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t]
npm run studio -- deploy <package> --workflow <name> [--message <start message>] [--tenant t]
npm run studio -- import-sample extraction-sample.zip                      # the training bundle
npm run studio -- import <bundle.zip|dir> [--bank-tenant b] [--apply]      # check, then save as drafts
```

Ran here: `import-sample`, then `import` of that bundle. Without `--bank-tenant` the import refuses the bundle (I14, it carries bank content); with `--bank-tenant example-bank` it reports no errors and asks for `--apply`. How to make a bundle from a source system: `docs/import/EXTRACTION-PROMPT.md`.

`deploy` runs a package on DTFx with each worker as its own process; see `studio/README.md`, "Export, workers and deploy".

## The kit on each engine

From `studio/`:

```
npx tsx engines/dtfx/run-kit.ts                         # needs .NET 8
npx tsx engines/elsa/run-kit.ts                         # needs .NET 8
../runtime/tools/temporal-test-server/setup.sh          # once, for Temporal
npx tsx engines/temporal/run-kit.ts                     # TH_TEMPORAL_ADDRESS=host:7233 for a real server
engines/flowable/setup.sh                               # once, for Flowable
npx tsx engines/flowable/run-kit.ts
```

| Ran here | Result |
| --- | --- |
| DTFx | 57 of 57, 419 s |
| Elsa | 57 of 57, 400 s |
| Temporal | Not run: the time-skipping test server did not start here (`setup.sh` not run in this checkout) |
| Flowable | Not run here; 56 of 56 on 22 November 2026, before CK-PARK-03 (its README) |

## The runtime

```
cd runtime
dotnet build TransactHub.Runtime.slnx
dotnet test                       # needs Docker: SQL Server and PostgreSQL containers
dotnet tool restore
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.SqlServer -s src/TransactHub.Store.Migrations.SqlServer -o Migrations
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.PostgreSql -s src/TransactHub.Store.Migrations.PostgreSql -o Migrations
```

A model change needs a migration for both providers; a test fails if either is missing.

`dotnet test tests/TransactHub.FileApi.Tests` runs one project. Ran here: the DTFx, Elsa and Temporal harnesses built for the kit; `dotnet test` was not run here.

## The runtime test bench

Send data and run it on the engines a profile names, each compared with the studio interpreter, with the store on PostgreSQL or SQL Server:

```
cd runtime
dotnet run --project tools/TransactHub.TestBench -- run --profile wps-all-engines --file salary.sif
dotnet run --project tools/TransactHub.TestBench -- suite testbench/suite --databases SqlServer
dotnet run --project tools/TransactHub.TestBench -- serve      # the service and the UI on http://127.0.0.1:4380
```

Profiles, the sample library, the data builder, regression cases, the service, the UI and its sign-in: [runtime/testbench/README.md](../../runtime/testbench/README.md).

## The laptop stack

The studio and one bank's runtime as separate services in Docker containers (R-09-80): `deploy/laptop/README.md` has the commands, the services, the secrets to set in the shell and the insert-only test. Not run here.

## Temporal

| Need | Use |
| --- | --- |
| Tests and the kit, fast | The time-skipping test server: `runtime/tools/temporal-test-server/setup.sh`, then the harness starts it |
| A real server, quickly | The dev server in Docker (`temporalio/temporal`), which the Temporal kit adapter starts for its one long scenario |
| Temporal on SQL Server | `cd runtime/temporal-mssql && scripts/dev.sh up`, then create the namespace; `scripts/dev.sh down` to stop. See its README |

The source's startup prompt for Temporal on SQLite and PostgreSQL is not carried. Temporal on SQL Server was not started here.

## CI

| Workflow | Runs |
| --- | --- |
| `.github/workflows/studio.yml` | Typecheck, `npm test`, `check`, `seed-content` |
| `.github/workflows/runtime.yml` | Build and test the runtime solution |
| `.github/workflows/engines.yml` | Black-box suites, the kit on four engines, the store matrix, Temporal on SQL Server |
| `.github/workflows/ops-console.yml` | The operations and service consoles' tests, the app's own tests and its Playwright tests |
| `.github/workflows/testbench.yml` | The test bench's shipped suite on four engines and the interpreter, on PostgreSQL and on SQL Server; its UI in a browser |
| `.github/workflows/security.yml` | Secret scan (gitleaks, pinned), check L37, `npm audit --omit=dev`, `dotnet list package --vulnerable` |

The secret scan and the dependency audits gate every push (`security.yml`). To run them by hand, see [security/secure-coding.md](security/secure-coding.md). A test run makes its own signing key (`studio/test/support/signing-env.ts`); to run the studio or a deploy by hand, `npm run studio -- signing-key <dir>` and set the two variables it prints (`npm run studio:dev` makes one for the run).
