# Build and run

The commands of this repository. It replaces the source's `build-and-run.md`, whose solution, PowerShell scripts and compose stack are not part of the north star. `CLAUDE.md` holds the short list; this page adds what each needs and what it showed when run here.

"Ran here" means run in this repository on 2026-11-22, on Linux with Node 22, Python 3, .NET 10 and .NET 8, Go and Docker, while this page was written.

## Needs

| Tool | For |
| --- | --- |
| Node.js 22 | The studio (`studio/package.json` asks for 22 or later) |
| Python 3 with `spec/tools/requirements.txt` | The spec's tools |
| .NET 10 SDK | The runtime (`runtime/global.json`) |
| .NET 8 SDK | The DTFx and Elsa harnesses |
| Java and Maven | The Flowable harness (`engines/flowable/setup.sh` fetches Maven) |
| Docker | The runtime's tests (SQL Server and PostgreSQL in Testcontainers), Temporal on SQL Server, one kit scenario on Temporal |
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
| `validate.py` | 0 errors, 4 warnings |
| `expand.py --check` | Up to date |
| `mutation_tests.py` | 28 of 28 caught |

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
| `npm test` | 223 pass, 1 skipped |
| `npm run typecheck` | No errors |
| `studio -- test` | 19 of 19 |
| `studio -- kit` | 48 of 48 |
| `studio -- check` | 0 errors, 13 warnings |
| `studio -- seed-content` | 163 of 163 seeded files current |
| `npm run test:blackbox` | Not run here |

### Run the studio

For development, with no GitHub:

```
cd studio
npm run studio:dev
```

It starts a fake GitHub in memory, seeded from `content-configurations/`, and the studio against it on http://localhost:4317 (`PORT` changes it). Nothing is written to disk. Ran here: it served the web app (HTTP 200) and reported 167 documents read into memory.

Against a real branch:

```
cd studio
npm run web:build
TH_CONTENT_REPO=<owner>/<repo> TH_CONTENT_BRANCH=main TH_GITHUB_TOKEN=<token> npm start
```

`web:build` ran here. `npm start` against a real branch was not run here: it needs a token. Choose the tenant and the user you act as in the header.

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
| DTFx | 48 of 48, about 4 minutes |
| Elsa | 48 of 48, about 5 minutes |
| Temporal | 48 of 48, about 8 minutes (the adapter runs CK-FOREACH-02 on a real dev server, as its README says) |
| Flowable | 48 of 48, about 26 minutes on a shared machine (7 on its own, as its README says) |

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

Ran here: `dotnet build` builds. `dotnet test` was not run here.

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

There is no secret scan or dependency audit in CI ([I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment)). To run them by hand, see [security/secure-coding.md](security/secure-coding.md).
