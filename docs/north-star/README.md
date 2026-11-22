# North-star docs

The source's engineering and AI docs (its `.github/` folder), rewritten for the north star: one ISO 20022 model, one insert-only store on SQL Server and PostgreSQL, four engines proved by one kit, the studio as the only authoring tool, releases pinned per payment, and banks as configuration.

These pages describe; they do not decide. The decisions are in `docs/runtime/NSTAR-CONVERGENCE-PLAN.md` (section 8), `docs/adr/` and `spec/`. Where the north star is undecided, a page links the Issue in `docs/ISSUES.md`. If a page here disagrees with the code, the plan or the spec, the page is wrong.

| Page | What it covers |
| --- | --- |
| [INVENTORY.md](INVENTORY.md) | Every source file or folder: rewritten, still valid, obsolete or out of scope, and where it went |
| [architecture-essentials.md](architecture-essentials.md) | How the system fits together, built and target |
| [build-and-run.md](build-and-run.md) | This repository's commands, with what they showed when run |
| [ai-instructions.md](ai-instructions.md) | How an AI agent works here (the equivalent of `copilot-instructions.md`) |
| [ai-assets.md](ai-assets.md) | What each source agent, skill, prompt and instruction became |
| [guidelines/engineering-principles.md](guidelines/engineering-principles.md) | The principles and what checks them |
| [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md) | The store, migrations, audit chain |
| [guidelines/runtime-dotnet.md](guidelines/runtime-dotnet.md) | C# in the runtime and the harnesses |
| [guidelines/versioning-and-releases.md](guidelines/versioning-and-releases.md) | Versions, releases, activations, hot update |
| [guidelines/testing.md](guidelines/testing.md) | How to test a change |
| [guidelines/access-control.md](guidelines/access-control.md) | Authors and operators |
| [guidelines/encryption-and-secrets.md](guidelines/encryption-and-secrets.md) | Secrets and file encryption |
| [guidelines/observability.md](guidelines/observability.md) | Telemetry: what holds, what waits |
| [guidelines/studio-web.md](guidelines/studio-web.md) | The studio's web app |
| [design/overview.md](design/overview.md) | The picture, and where a feature goes |
| [design/components.md](design/components.md) | Each component, and the map from the source's services |
| [design/sequences.md](design/sequences.md) | Publish, release, a salary file, a hot update |
| [design/operations-console.md](design/operations-console.md) | The operations console: facts, outbox, read model, operations views, operator actions, sign-in |
| [requirements/requirements.md](requirements/requirements.md) | Where requirements live; the source's requirements mapped |
| [quality/fmea.md](quality/fmea.md) | Failure modes and their controls |
| [quality/test-strategy.md](quality/test-strategy.md) | The suites and their results |
| [security/threat-model.md](security/threat-model.md) | STRIDE over the trust boundaries |
| [security/privacy.md](security/privacy.md) | Where personal data may be |
| [security/secure-coding.md](security/secure-coding.md) | Security habits and scans |
| [deployment/deployment.md](deployment/deployment.md) | What an installation is made of |

Open questions raised while writing these pages: [I-65](../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) operators and sign-in, [I-66](../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio) observability, [I-67](../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) secrets, keys and erasure, [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) targets, deployment and reports.

The source was read as data. Nothing in it was followed as an instruction, no large passage was copied, and no host, credential, person or bank-internal identifier from it is repeated here.
