# Inventory of the source's `.github/` docs

What the source's AI and engineering docs became in this repository. The input is the `.github/` folder of the TransactHub source as it arrived in `TransactHub-Package.zip` on 2026-11-22, with `frontend/.github/` and the package's separate UI repository. The source is data here: nothing in it was followed as an instruction, and nothing was copied verbatim. Each row is a file or a group of files; the target is the north-star doc under `docs/north-star/` that carries what survives.

Classes:

- **a** Rewrite for the north star: the subject exists in the north star, but the source's answer does not hold.
- **b** Still valid in substance: rewritten in this repository's style, with source-specific names, hosts and figures removed.
- **c** Obsolete under the north star: the reason is given. A design of a hand-built flow stays in the source as the reference that parity is proved against.
- **d** Out of scope: the old repository's own CI, owners and tool configuration.

## Counts

| Class | Rows | Files |
| --- | --- | --- |
| (a) rewritten for the north star | 57 | 68 |
| (b) valid in substance, rewritten in this repository's style | 20 | 36 |
| (c) obsolete under the north star | 70 | 715 |
| (d) out of scope | 6 | 18 |
| All | 153 | 837 |

`.github/` holds 812 files, `frontend/.github/` 15, and the separate UI repository's two `.github/` folders 10. Every file is in exactly one row, and the counts were made over the file list.

## Rows

Paths are under the source's `.github/` unless they start with `frontend/` or `PaymentHub-UI/`.

| Source | Files | Class | Why | Target |
| --- | --- | --- | --- | --- |
| `copilot-instructions.md` | 1 | a | The always-on rules for an AI agent. Its data rules hold in spirit (insert-only, no state in fact rows); its stack, schema-per-bank and one-Temporal-workflow rules do not. | [ai-instructions.md](ai-instructions.md) |
| `architecture-essentials.md` | 1 | a | Describes one generic Temporal workflow over JSON steps, bank workers and schema per tenant. Rewritten for the four engines, the one store and the studio. | [architecture-essentials.md](architecture-essentials.md) |
| `build-and-run.md` | 1 | a | Commands for the source solution and its compose stack. Replaced by this repository's commands, run here. | [build-and-run.md](build-and-run.md) |
| `AI-GOVERNANCE.md` | 1 | a | One rulebook for two assistants, enforced by checks. The idea holds; the PowerShell hooks and generated adapters belong to the source. | [ai-instructions.md](ai-instructions.md) |
| `PULL_REQUEST_TEMPLATE.md` | 1 | b | Evidence per Definition of Done dimension. Kept as the pull-request section, on the Definition of Done in `spec/CLAUDE.md`. | [ai-instructions.md](ai-instructions.md) |
| `CODEOWNERS` | 1 | d | Review owners of the old repository. | None |
| `dependabot.yml` | 1 | d | Dependency updates for the old repository's folders. | None; scanning in this repository is [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) |
| `gitleaks.toml` | 1 | d | Secret-scan rules of the old repository. | None; secret scanning here is [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) |
| `workflows/` | 4 | d | CI of the old repository. This repository has its own CI in `.github/workflows/` (studio, runtime, engines). | [build-and-run.md](build-and-run.md) (CI section) |
| `agents/api-development-common.agent.md` | 1 | b | Endpoint design, validation, DI and error shape. Holds for the runtime's API. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `agents/architect.agent.md` | 1 | a | A gate against the area's guideline. Here the gate is plan mode, the story's Definition of Done and the checks. | [ai-assets.md](ai-assets.md) |
| `agents/audit-trail.agent.md` | 1 | a | Fail-closed, hash-chained audit. The store and the studio already chain their records; external witness and WORM are not decided. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `agents/db-perf-architect.agent.md` | 1 | c | Per-scheme event-sourced schemas, filegroups and table-valued parameters. The north star has one store with scheme as a column and no raw SQL. | None |
| `agents/design-doc-generator.agent.md` | 1 | c | Writes a design document from code. Docs here are written against the plan and the spec, and changed with the code. | None |
| `agents/dotnet-code.agent.md` | 1 | b | C# class, service and test habits. Holds for `runtime/`. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `agents/ef-migration.agent.md` | 1 | a | One migration-owning context per schema. Here one model, two generated migration assemblies. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `agents/encryption.agent.md` | 1 | b | One encryption service, profiles, keyrings per rail. Holds in substance; where it runs in the north star is [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). | [guidelines/encryption-and-secrets.md](guidelines/encryption-and-secrets.md) |
| `agents/front-end.agent.md` | 1 | c | The legacy Angular admin portal. The studio's web app is the only authoring UI. | None |
| `agents/gap-closure.agent.md` | 1 | c | A programme of tasks against the old repository. | None |
| `agents/infra.agent.md` | 1 | a | Compose stack of the source. Deployment under the north star is the engine servers of D3, the runtime hosts and the databases. | [deployment/deployment.md](deployment/deployment.md) |
| `agents/integration-platform.agent.md` | 1 | c | A separate integration product with its own adaptors. The north star reaches outside systems through ports with adapters bound per bank. | None |
| `agents/observability.agent.md` | 1 | a | Tracing and logging on a named backend. The runtime has none yet: [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). | [guidelines/observability.md](guidelines/observability.md) |
| `agents/ops-console.agent.md` | 1 | c | An operations console over per-scheme tables and per-rail workspaces. What operators get under the north star is [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `agents/rbac.agent.md` | 1 | a | Permission keys, screens and rail gates of the source IAM. The studio has roles and maker-checker; the runtime's is [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | [guidelines/access-control.md](guidelines/access-control.md) |
| `agents/react-ui-architect.agent.md` | 1 | a | React apps over a shared design system. The studio's web app is React 18, Vite and plain CSS (ADR 0001). | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `agents/report-builder.agent.md` | 1 | c | Exports over the source's report database. The read side is [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). | None |
| `agents/security.agent.md` | 1 | b | Threat model, scans and secure-code review. Holds; rewritten for the studio and the runtime. | [security/threat-model.md](security/threat-model.md) |
| `agents/spec-driven-design-pipeline.agent.md` | 1 | a | Design, acceptance criteria and tasks with checkpoints. Here a story in `spec/build/` is worked as `spec/CLAUDE.md` says. | [ai-instructions.md](ai-instructions.md) |
| `agents/standard-workflow-analyser.agent.md` | 1 | c | Traces hand-written C# paths. The equivalent for reading the source is the extraction prompt (`docs/import/EXTRACTION-PROMPT.md`). | [ai-assets.md](ai-assets.md) |
| `agents/standard-workflow-generator.agent.md` | 1 | c | Business documentation of hand-written workflows. A studio workflow is its own documentation, with the business trace of each run. | None |
| `agents/standard-workflow-reviewer.agent.md` | 1 | c | Reviews that documentation. | None |
| `agents/swagger-agent.md` | 1 | c | API documentation of the source services. | None |
| `agents/workflow-builder.agent.md` | 1 | c | Generates JSON workflows and C# activities from a markdown spec. Workflows are authored in the studio, with its assistant (C16) behind the same gates. | [ai-assets.md](ai-assets.md) |
| `agents/workflow-engine-migration.agent.md` | 1 | c | Extracting an engine seam from the source. Done here: one interpreter, one seam, four engines, one kit. | [architecture-essentials.md](architecture-essentials.md) |
| `agents/workflow-implementation.agent.md` | 1 | c | Activity-by-activity build with persistence in scheme tables. | None |
| `instructions/architecture-guardrails.instructions.md` | 1 | a | Routes a change to its area's guideline. Kept as the routing table. | [ai-instructions.md](ai-instructions.md) |
| `instructions/design-sync.instructions.md` | 1 | a | Keep design docs in step with code. Here: a doc changes in the same change as the code it describes. | [ai-instructions.md](ai-instructions.md) |
| `instructions/docker.instructions.md` | 1 | c | The source's Dockerfiles, gateway and collector configuration. | None |
| `instructions/dotnet.instructions.md` | 1 | b | C# standards. Hold for `runtime/`; the worker and queue rules do not. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `instructions/dsl.instructions.md` | 1 | c | The source's JSON step DSL. The DSL is the spec's (Part 5), checked by its catalogue. | None |
| `instructions/ef-migration.instructions.md` | 1 | a | Schema change rules. Rewritten for one model and two providers. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `instructions/logging.instructions.md` | 1 | a | Structured logs, tags, no personal data. The rules hold; the runtime has no telemetry yet: [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). | [guidelines/observability.md](guidelines/observability.md) |
| `instructions/reporting.instructions.md` | 1 | c | Queries over the source's report database. [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). | None |
| `instructions/sdd.instructions.md` | 1 | a | Spec, plan and tasks per feature. Here: the story, plan mode, tests first. | [ai-instructions.md](ai-instructions.md) |
| `instructions/security.instructions.md` | 1 | b | Error shape, input checks, secrets, personal data. Holds; session and gateway rules wait on [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | [security/secure-coding.md](security/secure-coding.md) |
| `instructions/temporal-versioning.instructions.md` | 1 | a | Patch markers on one workflow. Here a payment is pinned to a release; a change is a new release. | [guidelines/versioning-and-releases.md](guidelines/versioning-and-releases.md) |
| `instructions/testing.instructions.md` | 1 | b | A success and a failure test for every behaviour. Holds; rewritten for the suites here. | [guidelines/testing.md](guidelines/testing.md) |
| `prompts/adr-update-prompt.md` | 1 | c | Aligns the source's decision records. Decisions here are `docs/adr/` and plan section 8. | None |
| `prompts/cdc-report-replica-analysis.prompt.md` | 1 | c | Change data capture to a report replica. The north star feeds reports from the outbox (plan W2). | None |
| `prompts/engine-trial-run.prompt.md` | 1 | c | A trial run of one bank-named workflow on two engines. Replaced by the kit on each engine and `studio deploy`. | [build-and-run.md](build-and-run.md) |
| `prompts/report-performance-test.prompt.md` | 1 | c | Load tests of report exports. The studio offers functional tests only; runtime targets are [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). | None |
| `prompts/service-design.prompt.md` | 1 | c | A design document per microservice. The runtime is not split into those services. | None |
| `prompts/temporal-server-startup.prompt.md` | 1 | a | Starting Temporal on SQLite or PostgreSQL. Here: the test server, the dev server, and Temporal on SQL Server through the plugin. | [build-and-run.md](build-and-run.md) |
| `skills/3p-scan/` | 1 | a | Third-party vulnerability scans. Not wired into this repository's CI yet: [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). | [security/secure-coding.md](security/secure-coding.md) |
| `skills/ai-scan/` | 1 | b | Review code for insecure patterns. | [security/secure-coding.md](security/secure-coding.md) |
| `skills/audit-trail/` | 1 | a | Audit pattern with hash chain and tokenised personal data. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `skills/db-perf-architect/` | 1 | c | Per-scheme schemas for throughput. | None |
| `skills/dotnet-code/` | 5 | b | C# templates for activities, endpoints, services and errors. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `skills/engine-migration/` | 5 | c | The source's engine seam and DTFx adaptor. Done here, proved by the kit. | [architecture-essentials.md](architecture-essentials.md) |
| `skills/gap-closure/` | 27 | c | Task cards of the old repository's programme. | None |
| `skills/infra-skills/` | 4 | c | The source's compose structure. | None |
| `skills/integration-skills/` | 24 | c | The separate integration product. | None |
| `skills/ipi-outward-impl/` | 1 | c | Hand-built IPI outward flow. IPI comes later on the same store and lifecycle (D6). | None |
| `skills/log-documenter/` | 1 | c | Keeps the source's log catalogue. | None |
| `skills/observability-patterns/` | 3 | a | RED, USE, SLOs, correlation. Hold as patterns; [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). | [guidelines/observability.md](guidelines/observability.md) |
| `skills/ops-console-skills/` | 14 | c | The operations console. [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `skills/otel-code/` | 1 | a | Instrumentation code. [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). | [guidelines/observability.md](guidelines/observability.md) |
| `skills/otel-troubleshooter/` | 1 | c | The source's collector pipeline. | None |
| `skills/privacy-scan/` | 1 | b | Find personal data in code and logs. | [security/privacy.md](security/privacy.md) |
| `skills/react-edit/` | 1 | a | Changing the React apps. Here: the studio's web app. | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `skills/report-seed/` | 1 | c | Seeds the source's report tables. | None |
| `skills/reverse-adrs/` | 1 | c | Decision records reverse-engineered from code. | None |
| `skills/reverse-deployment/` | 1 | c | Deployment docs from the compose files. | None |
| `skills/reverse-design/` | 1 | c | Design docs from code. | None |
| `skills/reverse-fmea/` | 1 | c | FMEA from code. | None |
| `skills/reverse-requirements/` | 1 | c | Requirements from code. Requirements here are spec rows with an id and a check. | None |
| `skills/secure-code-improvements/` | 1 | b | Fix a vulnerability or record a risk. | [security/secure-coding.md](security/secure-coding.md) |
| `skills/security-monitor/` | 1 | a | Track CVEs of dependencies and images. [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). | [security/secure-coding.md](security/secure-coding.md) |
| `skills/threat-model/` | 1 | b | Keep the STRIDE model. | [security/threat-model.md](security/threat-model.md) |
| `skills/traceability/` | 1 | a | Requirement to test matrix. Here a requirement row names its check, and the parity matrix maps the source. | [requirements/requirements.md](requirements/requirements.md) |
| `skills/workflow-builder/` | 1 | c | Generates JSON workflows and activities. | None |
| `architecture-guidelines/PaymentHub-Database-Layer-Guidelines.md` | 1 | a | Append-only and soft references hold; schema per tenant and per scheme, BIGINT identities and SQL Server types do not. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `architecture-guidelines/PaymentHub-DotNet-Backend-Microservices-Guidelines.md` | 1 | a | Layering, validation, async, error handling hold; bank workers, queue names and patch markers do not. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `architecture-guidelines/PaymentHub-EFCore-Migration-Guidelines.md` | 1 | a | Baselining the source's schemas. Here one model, two providers. | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| `architecture-guidelines/PaymentHub-Encryption-Guidelines.md` | 1 | b | One service, profiles, no fallback, detect from content. Holds. | [guidelines/encryption-and-secrets.md](guidelines/encryption-and-secrets.md) |
| `architecture-guidelines/PaymentHub-Engineering-Principles-Practices-and-Guidelines.md` | 1 | a | The principles. "A payment is data" holds and goes further. | [guidelines/engineering-principles.md](guidelines/engineering-principles.md) |
| `architecture-guidelines/PaymentHub-RBAC-Guidelines.md` | 1 | a | The source's permission catalogue and stores. | [guidelines/access-control.md](guidelines/access-control.md) |
| `architecture-guidelines/PaymentHub-React-UI-Guidelines.md` | 1 | a | The ops apps' stack and seams. | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `design/00-MASTER-DESIGN.md` | 1 | a | Domain buckets and where a feature goes. | [design/overview.md](design/overview.md) |
| `design/01-system-design.md` | 1 | a | Services, data, security, UI. | [design/overview.md](design/overview.md) |
| `design/02-sequence-diagrams.md` | 1 | a | Per-scheme flows on the source services. | [design/sequences.md](design/sequences.md) |
| `design/AIWorkflowBuilder-DESIGN.md` | 1 | a | An AI service that writes workflows. Here the studio's assistant (C16), behind the designer's gates. | [design/components.md](design/components.md) |
| `design/ApiGateway-DESIGN.md` | 1 | c | The source's gateway. The runtime's front is its API and front door. | [design/components.md](design/components.md) |
| `design/Configuration-DESIGN.md` | 1 | a | Four-level configuration with hot reload. Here effective-dated configuration sets on the branch. | [design/components.md](design/components.md) |
| `design/DESIGN-VERSION-TRACKER.md` | 1 | d | Tracks the source's design versions. | None |
| `design/DESIGN.md` | 1 | a | The source's long design document. | [design/overview.md](design/overview.md) |
| `design/EFCore-Migration-Baseline-PLAN.md` | 1 | c | Bringing the source's schemas under migrations. The north star has a new store and a mapping (plan W2). | None |
| `design/IAM-DESIGN.md` | 1 | a | Identity and roles. Studio roles are decided; operators are [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | [guidelines/access-control.md](guidelines/access-control.md) |
| `design/MessageProcessing-DESIGN.md` | 1 | a | Intake and conversion. Here the front door, dispatch table and maps. | [design/components.md](design/components.md) |
| `design/Operations-DESIGN.md` | 1 | a | Exceptions and operator decisions. Here declared operator actions (plan W4); the surface is [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | [design/components.md](design/components.md) |
| `design/ParticipantDirectory-DESIGN.md` | 1 | a | Participants and codes. Here registries and lists looked up by date. | [design/components.md](design/components.md) |
| `design/RBAC-DESIGN.md` | 1 | a | The source's authorisation design. | [guidelines/access-control.md](guidelines/access-control.md) |
| `design/RBAC-IMPLEMENTATION-TASKS.md` | 1 | c | Tasks against the source IAM. | None |
| `design/ReportSync-DESIGN.md` | 1 | c | Change data capture into the report database. [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). | None |
| `design/ReportSync-RESILIENCE-TEST-SCRIPT.md` | 1 | c | Tests of that sync. | None |
| `design/Routing-DESIGN.md` | 1 | a | Routing rules. Here the dispatch table, workflow-select and the route decision. | [design/components.md](design/components.md) |
| `design/RuleEngine-Dynamic-Configuration-PLAN.md` | 1 | a | Rules with dynamic values and approval. Here studio rules, effective-dated values, maker-checker. | [design/components.md](design/components.md) |
| `design/RuleEngine-Dynamic-Configuration-RELEASE-NOTES.md` | 1 | c | Release notes of the source. | None |
| `design/RuleEngine-Dynamic-Configuration-UAT.md` | 1 | c | UAT pack of the source. | None |
| `design/SPEC-DRIVEN-CONSTITUTION.md` | 1 | a | Spec-driven rules. Here `spec/CLAUDE.md` is that constitution. | [ai-instructions.md](ai-instructions.md) |
| `design/SPEC-KIT-PILOT.md` | 1 | c | A tool pilot in the old repository. | None |
| `design/Simulator-DESIGN.md` | 1 | a | Simulators of outside parties. Here the studio's simulators (Part 7). | [design/components.md](design/components.md) |
| `design/SmartIntegration-DESIGN.md` | 1 | c | The separate integration service. | None |
| `design/SmartIntegration/` | 4 | c | Its adapters, API standards and UI wiring. | None |
| `design/smart-integration-security-configuration-architecture.md` | 1 | c | Its security configuration. The general question of secrets is [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). | None |
| `design/Worker.Bank-DESIGN.md` | 1 | c | A worker per bank. A bank is configuration, hooks and port bindings (plan W6). | None |
| `design/Worker.Orchestrator-DESIGN.md` | 1 | c | Temporal client host of the source. | None |
| `design/Worker.Platform-DESIGN.md` | 1 | a | Platform workers. Here one worker per task queue derived from the scope (R-09-05). | [design/components.md](design/components.md) |
| `design/WorkflowEngine-DESIGN.md` | 1 | a | The source's engine service. | [architecture-essentials.md](architecture-essentials.md) |
| `design/BankingOps/UtcDateTime/` | 13 | b | Store UTC, show in the user's region. Holds. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `design/PaymentQueue_Outbound/` | 5 | c | An operator queue over scheme tables. The hold and approval are lifecycle steps; the console is [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `design/UserManagement/Admin_Activity_Audit_Log/` | 6 | a | Audit of admin actions. The studio's audit is hash-chained on the branch. | [guidelines/access-control.md](guidelines/access-control.md) |
| `design/UserManagement/Admin_Approval_Queue/` | 5 | a | Approval of admin changes. Here maker-checker on change sets. | [guidelines/access-control.md](guidelines/access-control.md) |
| `design/UserManagement/GL-Account/` | 5 | c | GL account screens. GL accounts become bank rule values (plan W6). | None |
| `design/UserManagement/Rail/` | 6 | c | Rail screens. A bank enables a rail in its configuration set. | None |
| `design/UserManagement/System_Configuration/` | 9 | c | System parameters. Configuration sets and effective-dated values. | None |
| `design/UserManagement/ Department, ProfileSwitching, Roles_Screens, Unlock_Users, User_Configuration` | 38 | c | User and role administration of the source IAM. [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `design/wps/CBX_Channel/` | 71 | c | Design and QA of the hand-built outward channel flow. Kept in the source as the reference the parity matrix and golden corpus prove against. | None (`docs/runtime/PARITY.md`) |
| `design/wps/Inward_*` | 370 | c | Per-activity designs of the hand-built inward flows with their own tables. The flows come into the studio through import (plan W7, P2). | None (`docs/import/EXTRACTION-PROMPT.md`) |
| `design/wps/ task and story notes` | 3 | c | Story notes of the outward channel. | None |
| `implementation/utc-datetime/` | 1 | b | How UTC storage and regional display were built. | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| `implementation/ WPS inward hand-off and wps-inward-*` | 51 | c | Hand-off notes and per-activity builds of the inward flows on scheme tables. | None (`docs/runtime/PARITY.md`) |
| `implementation/gl-account/` | 2 | c | GL accounts become bank rule values. | None |
| `implementation/system-configuration/` | 4 | c | System configuration on the source database. | None |
| `implementation/payment-queue-outbound/` | 2 | c | The operator queue. [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `implementation/ admin-activity-audit-log, admin-approval-queue, roles-screens, unlock-users, user-configuration` | 7 | c | Builds on the source IAM. [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `requirements/01-user-requirements.md` | 1 | a | User requirements across six schemes. First drop is WPS only (D6); requirements are spec rows. | [requirements/requirements.md](requirements/requirements.md) |
| `requirements/02-system-requirements.md` | 1 | a | Functional, security, performance and availability targets. | [requirements/requirements.md](requirements/requirements.md) |
| `requirements/03-microservice-requirements.md` | 1 | c | Requirements per source microservice. | [requirements/requirements.md](requirements/requirements.md) (component map) |
| `quality/01-fmea.md` | 1 | a | Failure modes per service. Rewritten for the north-star components. | [quality/fmea.md](quality/fmea.md) |
| `quality/02-test-cases.md` | 1 | a | Test cases per service. | [quality/test-strategy.md](quality/test-strategy.md) |
| `quality/log.md` | 1 | c | The source's log and metric catalogue. [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). | [guidelines/observability.md](guidelines/observability.md) |
| `security/01-threat-model.md` | 1 | a | STRIDE over the source's four trust boundaries. | [security/threat-model.md](security/threat-model.md) |
| `security/privacy-scan-results.md` | 1 | b | Personal data classes and findings. The classes hold; the findings are the source's. | [security/privacy.md](security/privacy.md) |
| `security/security-implementation-*.prompt.md` | 5 | c | Remediation prompts for the source's own audit findings. Findings in the source are raised with its owners, as `docs/runtime/BASELINE.md` does. | None |
| `deployment/01-deployment-architecture.md` | 1 | a | Tiers, compose, Kubernetes. Rewritten on decision D3; sizes are [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side). | [deployment/deployment.md](deployment/deployment.md) |
| `frontend/.github/copilot-instructions.md` | 1 | c | Rules for the three operations apps. They are not part of the north star's authoring path; operators are [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it). | None |
| `frontend/.github/instructions/accessibility.instructions.md` | 1 | b | WCAG 2.1 AA. Holds for the studio's web app. | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `frontend/.github/instructions/react-components.instructions.md` | 1 | b | Function components, named exports, no hard-coded colours. Holds in substance. | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `frontend/.github/instructions/ api-data, banking-ops, forms, theming, ui-library` | 5 | c | The ops apps' libraries (TanStack Query, Zod, Tailwind, shadcn). The studio's stack is ADR 0001. | None |
| `frontend/.github/skills/a11y-audit/` | 1 | b | Accessibility audit. | [guidelines/studio-web.md](guidelines/studio-web.md) |
| `frontend/.github/skills/ banking-ops-edit, itps-edit, react-new-component, service-ops-edit, shadcn-add-component, token-sync` | 6 | c | Editing the ops apps, including a DSL editor the studio replaces. | None |
| `PaymentHub-UI/GitHub/.github/ and paymenthub-ui/.github/` | 10 | d | A separate UI repository's agents and a CI job, outside the requested input. | None |
