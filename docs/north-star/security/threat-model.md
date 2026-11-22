# Threat model

STRIDE over the north star's trust boundaries. It replaces the source's threat model, which covered its gateway, eleven services and their shared database. Each threat names its control and whether a test proves it. Where the control is not decided, the Issue is linked.

## Trust boundaries

| # | Boundary | Crossed by |
| --- | --- | --- |
| TB1 | A designer's browser to the studio server | HTTP API calls with the session cookie and its CSRF token; the sign-in with the studio's identity provider (NEST's) |
| TB2 | The studio server to GitHub | The GitHub REST and Git Data APIs, with `TH_GITHUB_TOKEN` |
| TB3 | The studio server to the Claude API | The assistant's drafting calls, with `ANTHROPIC_API_KEY` |
| TB4 | The content branch to the runtime | The pipeline calling the release API |
| TB5 | Outside parties to the runtime | Channel files and scheme messages into the front door; answers from core banking |
| TB6 | Runtime workers to the store and the engine's state | EF Core to SQL Server or PostgreSQL; engine clients to their servers |
| TB7 | A script-behind to the studio | The injected interface of the sandbox |

The studio and the runtime share no connection: the studio never reaches a running payment (R-10-01). The branch is the only path between them.

## STRIDE

### Spoofing

| Threat | Control | Status |
| --- | --- | --- |
| Someone acts in the studio as another user | OpenID Connect sign-in with PKCE against the studio's own identity provider; a signed, http-only, same-site session cookie; the user is taken from the session, never the request; one permission table on every write (R-11-30, R-11-32) | Proved: `studio/test/auth.test.ts`, `studio/test/blackbox/auth.test.ts`, `studio/test/permissions.test.ts` |
| A runtime or bank token is replayed against the studio | The studio accepts no bearer token, only ID tokens for its own client from its one issuer, and shares no session with the runtime (R-11-34) | Proved: `studio/test/auth.test.ts` |
| A page elsewhere makes a signed-in designer's browser change something | CSRF token on every state-changing call, origin check, same-site cookie (R-11-33) | Proved: `studio/test/blackbox/auth.test.ts` |
| Someone calls the release API | A bearer JWT from the bank's identity provider on every call; roles from the bank's role map; the actor is the verified caller (R-09-27, R-09-29) | Proved: `ApiAuthTests` |
| A package is forged, or signed by a stolen key | Every package is signed by the studio's key; the runtime and the deploy runner trust only the keys the bank lists, and refuse a revoked one even for a release stored before (R-09-23 to R-09-26) | Proved: `ReleaseApiTests`, `SigningTests`, `signing.test.ts`; the key in the studio's process is accepted ([I-74](../../ISSUES.md#i-74-the-studios-signing-key-is-held-in-its-process), decided 2026-11-22) |
| A forged file arrives as a scheme's | The dispatch table and intake map accept only a declared message; duplicate files are known by content | Partly: `StoreTests.A_file_seen_before_is_known_by_its_content`; transport security is per port binding |
| A tenant claims another tenant's content | A bank tenant writes only scope `b` (R-06-03) and sees only its own content (R-06-01) | Proved: tenant isolation suite |

### Tampering

| Threat | Control | Status |
| --- | --- | --- |
| A published file is changed | Insert-only branch, fast-forward only, no force push (R-09-21) | Proved: `content.test.ts` |
| A stored release is replaced | Same id with another hash refused; no PUT, PATCH, DELETE | Proved: `ReleaseApiTests` |
| A package is altered between branch and runtime | The API checks the package against its manifest and against the signed statement of every file's hash, at storage and at activation | Proved: `ReleaseApiTests.A_package_that_does_not_match_its_manifest_or_its_signature_is_refused`; `e2e-p6-hot-update.test.ts` step 2b |
| A payment's record is edited in the database | Insert-only guard; hash chain per subject | Proved for the application path; a database administrator can still write rows, and the chain then shows it |
| A running worker runs code its release did not name | Tasks registered as `name@release`; a worker refuses a task not on its queue | Proved in the studio's export; runtime versioned workers are target |

### Repudiation

| Threat | Control | Status |
| --- | --- | --- |
| A maker or checker denies a change | Change set records name maker and checker; the audit is hash-chained on the branch | Proved: audit tests |
| An operator denies an action | Operator actions on the payment's fact chain | Open: [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |

### Information disclosure

| Threat | Control | Status |
| --- | --- | --- |
| Personal data in engine history | `PaymentContext` carries ids only | Built; the history scan of plan W1 is target |
| Personal data in logs or traces | No logging of party values (`spec/CLAUDE.md`) | No telemetry yet ([I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio)) |
| A secret in this public repository | Secrets are named by reference (R-06-22); check L37 refuses a literal one; gitleaks scans history and tree in CI | Proved: L37 mutation cases M38, M39; CI job `security` |
| A secret in a log or an error | One secret interface; a value prints as `[secret]`; errors name the reference only | Proved: `SecretsTests`, `VaultTests`, `signing.test.ts` |
| A bank's values in shared content | Bank values only in the bank's folder; bank names checked out of shared code | Proved: `BankConfigurationTests` |
| Content sent to the Claude API | The studio holds design content, configuration and synthetic samples, never payment or personal data | By design (R-10-01); a bank's configuration can reach the assistant when its designer asks for help with it |
| An error answer shows internals | Problem responses with a message, never a stack trace | Review |

### Denial of service

| Threat | Control | Status |
| --- | --- | --- |
| A huge file at the front door | Streaming parse; limits per port | Not decided: [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) |
| An outside party never answers | Every await has a timeout (check L32) | Proved: mutation case |
| The studio's branch grows without bound | Reads cached by commit; old branches cost one request | Open in part: I-44, issue 6 |

### Elevation of privilege

| Threat | Control | Status |
| --- | --- | --- |
| A bank designer changes shared content | Scope rules (R-06-03) | Proved |
| A designer publishes without a checker | Maker-checker (R-06-04, R-11-19) | Proved |
| A script-behind escapes its sandbox | Only the injected interface; no network, file system, clock or environment | Honest scripts only: not a security boundary against hostile code (D14) |
| SQL injection | No raw SQL anywhere in the runtime | Proved: `NeutralityTests` |

## The source's findings

The source's own audit findings and its failing security tests (N15 in the plan) are about its services. They are raised with the source's owners (`docs/runtime/BASELINE.md`) and not carried here.
