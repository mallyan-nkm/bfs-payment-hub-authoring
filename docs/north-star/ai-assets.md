# The source's agents, skills, prompts and instructions

What each AI asset in the source's `.github/` became. Most were personas or checklists for building one service, one scheme table or one bank's code by hand. Under the north star that work is authoring in the studio, a check, or a decision still to take.

This repository adds no agent, skill or prompt files. An agent here works from `CLAUDE.md`, `spec/CLAUDE.md` and [ai-instructions.md](ai-instructions.md). The class letters are those of [INVENTORY.md](INVENTORY.md).

## Agents (`agents/*.agent.md`)

| Source agent | Class | North-star equivalent |
| --- | --- | --- |
| api-development-common | b | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md), section API |
| architect | a | Plan mode and the story's Definition of Done (`spec/CLAUDE.md`); the checks in [ai-instructions.md](ai-instructions.md) |
| audit-trail | a | The store's fact chain and the studio's audit: [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| db-perf-architect | c | None. Per-scheme schemas are gone (plan W2) |
| design-doc-generator | c | None. Docs are written against the plan and change with the code |
| dotnet-code | b | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| ef-migration | a | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md), section Migrations |
| encryption | b | [guidelines/encryption-and-secrets.md](guidelines/encryption-and-secrets.md); [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) |
| front-end (Angular portal) | c | None. The studio's web app is the only authoring UI |
| gap-closure | c | None |
| infra | a | [deployment/deployment.md](deployment/deployment.md) |
| integration-platform | c | Ports and adapters bound per bank: [design/components.md](design/components.md) |
| observability | a | [guidelines/observability.md](guidelines/observability.md); [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio) |
| ops-console | c | [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |
| rbac | a | [guidelines/access-control.md](guidelines/access-control.md) |
| react-ui-architect | a | [guidelines/studio-web.md](guidelines/studio-web.md) |
| report-builder | c | [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) |
| security | b | [security/threat-model.md](security/threat-model.md), [security/secure-coding.md](security/secure-coding.md) |
| spec-driven-design-pipeline | a | `spec/CLAUDE.md`, "How to work a story"; stories in `spec/build/` |
| standard-workflow-analyser | c | For reading the source: `docs/import/EXTRACTION-PROMPT.md`. For a studio workflow: its business trace and the debugger |
| standard-workflow-generator | c | None. A studio workflow and its trace are the documentation |
| standard-workflow-reviewer | c | None |
| swagger-agent | c | None. The runtime's API is listed in `runtime/README.md` |
| workflow-builder | c | Authoring in the studio, with the assistant (C16) behind the designer's gates |
| workflow-engine-migration | c | Done: one seam, four engines, one kit ([architecture-essentials.md](architecture-essentials.md)) |
| workflow-implementation | c | None |

## Skills (`skills/*/`)

| Source skill | Class | North-star equivalent |
| --- | --- | --- |
| 3p-scan | a | [security/secure-coding.md](security/secure-coding.md); CI scan is [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) |
| ai-scan | b | [security/secure-coding.md](security/secure-coding.md) |
| audit-trail | a | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| db-perf-architect | c | None |
| dotnet-code | b | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| engine-migration | c | Done; the kit is the conformance suite |
| gap-closure | c | None |
| infra-skills | c | None |
| integration-skills | c | None |
| ipi-outward-impl | c | None. IPI comes later on the same store and lifecycle (D6) |
| log-documenter | c | None |
| observability-patterns | a | [guidelines/observability.md](guidelines/observability.md) |
| ops-console-skills | c | [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |
| otel-code | a | [guidelines/observability.md](guidelines/observability.md) |
| otel-troubleshooter | c | None |
| privacy-scan | b | [security/privacy.md](security/privacy.md) |
| react-edit | a | [guidelines/studio-web.md](guidelines/studio-web.md) |
| report-seed | c | None |
| reverse-adrs, reverse-deployment, reverse-design, reverse-fmea, reverse-requirements | c | None. Decisions are `docs/adr/` and plan section 8; requirements are spec rows |
| secure-code-improvements | b | [security/secure-coding.md](security/secure-coding.md) |
| security-monitor | a | [security/secure-coding.md](security/secure-coding.md) |
| threat-model | b | [security/threat-model.md](security/threat-model.md) |
| traceability | a | [requirements/requirements.md](requirements/requirements.md); `docs/runtime/PARITY.md` |
| workflow-builder | c | Authoring in the studio |

## Prompts (`prompts/*.md`)

| Source prompt | Class | North-star equivalent |
| --- | --- | --- |
| adr-update-prompt | c | None |
| cdc-report-replica-analysis | c | None. Reports are fed from the outbox (plan W2) |
| engine-trial-run | c | The kit on each engine, and `npm run studio -- deploy` ([build-and-run.md](build-and-run.md)) |
| report-performance-test | c | [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) |
| service-design | c | None |
| temporal-server-startup | a | [build-and-run.md](build-and-run.md), section Temporal |

## Instructions (`instructions/*.instructions.md`)

| Source instruction | Class | North-star equivalent |
| --- | --- | --- |
| architecture-guardrails | a | The routing table in [ai-instructions.md](ai-instructions.md) |
| design-sync | a | [ai-instructions.md](ai-instructions.md): a doc changes with its code |
| docker | c | None |
| dotnet | b | [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) |
| dsl | c | `spec/design/05-workflow-dsl.md` |
| ef-migration | a | [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) |
| logging | a | [guidelines/observability.md](guidelines/observability.md) |
| reporting | c | [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) |
| sdd | a | `spec/CLAUDE.md` |
| security | b | [security/secure-coding.md](security/secure-coding.md) |
| temporal-versioning | a | [guidelines/versioning-and-releases.md](guidelines/versioning-and-releases.md) |
| testing | b | [guidelines/testing.md](guidelines/testing.md) |

## The frontend's assets (`frontend/.github/`)

| Source asset | Class | North-star equivalent |
| --- | --- | --- |
| copilot-instructions | c | None. The operations apps are not the authoring path |
| instructions: accessibility, react-components | b | [guidelines/studio-web.md](guidelines/studio-web.md) |
| instructions: api-data, banking-ops, forms, theming, ui-library | c | None. The studio's stack is ADR 0001 |
| skills: a11y-audit | b | [guidelines/studio-web.md](guidelines/studio-web.md) |
| skills: banking-ops-edit, itps-edit, react-new-component, service-ops-edit, shadcn-add-component, token-sync | c | None |

## The generated adapters

The source generated Claude Code adapters (`.claude/rules`, `.claude/agents`, `.claude/skills`, `.claude/commands`) from its `.github/` files with PowerShell scripts, and failed a pull request when they drifted. This repository has no such adapters: Claude Code reads `CLAUDE.md`, and Copilot reads `.github/copilot-instructions.md`, which points to the same two files. Nothing is copied, so nothing can drift.
