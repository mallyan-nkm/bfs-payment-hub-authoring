# Baseline: the TransactHub source, run as is

The source (`TransactHub-src.zip`, commit `666f1ee`, with `dsl.zip`) was built and its 20 test projects run before any runtime code was written, and again as each missing input was supplied. Nothing in the source was changed. Each failure is classified: **missing input** (a file the zip did not carry), **this machine** (a tool or service the test needs that this machine lacks), or **finding** (a test that fails on its own terms and needs a look).

- .NET SDK 10.0.115 (`global.json` 10.0.100, roll forward). Docker 29.9.
- `services/PaymentHub.slnx`, 153 projects: builds, 0 errors, 1 warning (xUnit1026 in `RfaGenerationWorkflowRoutingTests.cs`). Five test projects are not in the solution and build on their own.

## Passes

| Pass | What changed | Why |
| --- | --- | --- |
| A | Nothing: the zip as given | The baseline |
| B | `dsl/` from `dsl.zip` restored | 29 workflow definitions, the registration scripts and two schemas were missing from the zip |
| C | Temporal stand-ins (`runtime/tools/temporal-test-server/install-sdk-standins.sh`) | `temporal.download` is blocked here; the SDK reuses a server it finds in its cache, so the same test server (from Maven Central) and dev server (its Docker image) stand in |
| D | Each project as its own docs say | `DOTNET_USE_POLLING_FILE_WATCHER=1` for the security suite; the engine conformance lanes switched on, DTFx on a SQL Server container |

## Results

| Project | A | Best pass | Remaining failures |
| --- | --- | --- | --- |
| Encryption.Tests | 176 / 176 | A | none |
| RuleEngine.UnitTests | 300 / 300 | A | none |
| ReportSync.UnitTests | 156 / 156 | A | none |
| UnitTests.WpsReports | 330 / 330 | A | none |
| ContractTests | 8 pass, 34 skipped | A | skipped: need the running stack (`RUN_SIT=true`) |
| PerformanceTests (+ Benchmarks) | 13 skipped | A | skipped: need the running stack or `PAYMENTHUB_PQ_PERF=run` |
| UnitTests | 10,729 / 10,875 (135 fail) | B: 10,859 (5 fail) | this machine: 9 skipped need SQL Server LocalDB (Windows), 2 need SQL Server for `EF.Functions.DateDiffSecond`, 1 Windows path separator; finding: 1 compose start version 1.20.0 behind the DSL, 1 graph snapshot not committed |
| WorkflowTests | 9 / 173 (164 fail: Temporal downloads blocked) | C: 171 / 173 | missing input: 2 replay `Workflows/Histories/v11-ui-upload-3.5.*.json`, which the zip does not carry |
| EngineConformance.Tests | did not build without `dsl/` | D: 223 / 226, Temporal and DTFx lanes | this machine: 3 call `pwsh` (PowerShell), not installed |
| IntegrationTests | timed out at 40 min (Temporal downloads failing) | C: 1,235 / 3,750 (2,457 fail, 58 skipped), 1 h 2 min | this machine: about 2,180, the SQL Server test containers under load (2,031 connection refused, 99 pre-login, 15 timeouts, 37 memory-optimised tables and disk, 7 out of memory); 38 skipped need the running stack (`RUN_SIT=true`); 11 pending in the source itself (GL migrations not in the repository). Missing input: 9 need `db-script/corporate-registration-schema-*.sql`. To check: 81 invalid column names and 36 objects that already exist (schema drift between migrations and tests), 50 role assignments refused until a screen is mapped. Run on a machine with more memory, a SQL Server per test class at a time, to separate these properly. |
| SecurityTests | 1,280 / 1,600 (320 fail) | D: 1,574 / 1,600 | this machine: 294 were the inotify limit (fixed in D). **Finding: 23 in `IamAuditActivityLogAccessTests` and 2 more expect 403 Forbidden and get 200 OK**: a user holding only `bankingops:page.admin-activity-log:view` can propose role, screen and mapping changes. 1 permission key registered outside `usp_RegisterPermissionKey`. |
| IAM.IntegrationTests | 194 / 196 | A | finding: 2 (an audit snapshot status, a preview skip count) |
| IAM.UnitTests | 415 / 419 | A | finding: 4 (role payload round-trip adds `rails: []`; a locale zone) |
| AdminActivity.IntegrationTests | 40 / 42 | A | finding: 2 (an extra row in a modify list and a page count) |
| UnitTests.AdminActivity | 297 / 299 | A | finding: 2 (mapping rule list) |
| SmartIntegration.UnitTests | 1,817 / 1,822 | A | this machine: 4 need an address family the container lacks, 1 a Windows certificate store |
| SmartIntegration.IntegrationTests | 7 / 50 | A | this machine: 43 need the service's `StorageTimeZone` parameter from a running database |
| ChaosTests | 3 / 23 | A | this machine: 18 need the docker-compose stack running (ports 5300, 5010) |
| UnitTests.Wps | did not restore | A | finding: `NU1008`, the project sets package versions that central package management forbids, so it never builds |

## What this means for the plan

- **Missing inputs are mostly resolved** with `dsl.zip`: the WPS workflow tests, unit tests and conformance tests run and pass. Still missing: two recorded histories (`tests/PaymentHub.WorkflowTests/Workflows/Histories/v11-ui-upload-3.5.0-awaiting-cb.json` and `…-3.5.1-stp-cancelled.json`) and the `.github/` docs the package's CLAUDE.md imports. Checked again on 2026-11-21: the zip holds the folder tree of `.github/` (288 folders) but one file only, `.github/design/wps/CBX_Channel/v1.0/qa/README.md`; `dsl/` holds folders and no files (`dsl.zip` supplied those). The files the source's `CLAUDE.md` imports (`.github/copilot-instructions.md`, `architecture-essentials.md`, `build-and-run.md`) and the agents, skills, instructions and design documents are not in it. The zip was most likely made by a tool that kept directories but dropped their files, so a fresh export from the source repository (`git archive`) would carry them.
- **The source's engines pass their own conformance kit** on Temporal and on DTFx (223 of 226; the 3 left need PowerShell). The runtime keeps this as its reference: the new runtime's WPS rules match the source's code exactly (`runtime/tests/TransactHub.Store.Tests/Golden`, 1,977 cases).
- **Findings to raise with the source's owners**: the access-log permission letting a viewer propose changes (security, 25 tests), `UnitTests.Wps` not building, the uncommitted graph snapshot and the stale compose version. None is carried into the runtime: its release API refuses changes in place, and its access rules are the studio's maker-checker.
- **Not reproduced here**: anything that needs the whole stack running (compose, SQL Server 2025 restore, the services). The runtime's proof is instead the golden corpus, the conformance kit on each engine, and the end-to-end runs on both databases.
