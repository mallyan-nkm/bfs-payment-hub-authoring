# Threat model

STRIDE over the north star's trust boundaries. It replaces the source's threat model, which covered its gateway, eleven services and their shared database. Each threat names its control and whether a test proves it. Where the control is not decided, the Issue is linked. Updated after the security review of November 2026 ([review-2026-11.md](review-2026-11.md)): its finding ids (F-nn) are given where a row changed.

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
| TB8 | A worker to the runtime's native host | `POST /native` with the bank's client-credentials token (R-08-17) |
| TB9 | An operator's or engineer's browser to the runtime's consoles | The operations, service and test bench APIs with the bank's bearer token; the console's page and its policy |
| TB10 | A contributor or a fork to CI | Workflows on push and pull request; third-party actions; the repository token |

The studio and the runtime share no connection: the studio never reaches a running payment (R-10-01). The branch is the only path between them.

## STRIDE

### Spoofing

| Threat | Control | Status |
| --- | --- | --- |
| Someone acts in the studio as another user | OpenID Connect sign-in with PKCE against the studio's own identity provider; a signed, http-only, same-site session cookie; the user is taken from the session, never the request; one permission table on every write (R-11-30, R-11-32) | Proved: `studio/test/auth.test.ts`, `studio/test/blackbox/auth.test.ts`, `studio/test/permissions.test.ts` |
| A runtime or bank token is replayed against the studio | The studio accepts no bearer token, only ID tokens for its own client from its one issuer, and shares no session with the runtime (R-11-34) | Proved: `studio/test/auth.test.ts` |
| A page elsewhere makes a signed-in designer's browser change something | CSRF token on every state-changing call, origin check (the public origin only, F-19), same-site cookie (R-11-33); no frame may hold a page (R-11-43, R-09-60) | Proved: `studio/test/blackbox/auth.test.ts`, `security.test.ts`, `SecurityHeadersTests` |
| Someone calls the release API | A bearer JWT from the bank's identity provider on every call; roles from the bank's role map; the actor is the verified caller (R-09-27, R-09-29) | Proved: `ApiAuthTests` |
| Someone calls the native host and posts to a bank's core banking | The bank's machine callers only, role `native-caller` in the attempt's tenant; loopback only without an identity provider (R-08-17, F-03) | Proved: `NativeHostAuthTests`; the worker's own client-credentials token and mutual TLS with the bank's CA as an option (R-08-21): `native-auth.test.ts`, `NativeHostMutualTlsTests`; the engine hosts take their callers' token (R-08-22): `HostTokenTests` |
| A role a person holds in one bank is used in another | Each grant of the role map gives its roles in its own tenants; every host asks for the role in the tenant (R-06-28, F-04, F-06) | Proved: `ApiAuthTests`, `OperatorApiTests` |
| Sign-in is flooded, or a session left open is used | Rate limits on sign-in, a bounded number of sign-ins in progress, an idle timeout, security events (R-11-41, R-11-42, F-11, F-20, F-21) | Proved: `studio/test/security.test.ts` |
| A package is forged, or signed by a stolen key | Every package is signed by the studio's key; the runtime and the deploy runner trust only the keys the bank lists, and refuse a revoked one even for a release stored before (R-09-23 to R-09-26) | Proved: `ReleaseApiTests`, `SigningTests`, `signing.test.ts`; the key in the studio's process is accepted ([I-74](../../ISSUES.md#i-74-the-studios-signing-key-is-held-in-its-process), decided 2026-11-22) |
| A forged file arrives as a scheme's | The dispatch table and intake map accept only a declared message; duplicate files are known by content | Partly: `StoreTests.A_file_seen_before_is_known_by_its_content`; transport security is per port binding |
| A tenant claims another tenant's content | A bank tenant writes only scope `b` (R-06-03) and sees only its own content (R-06-01); a test run takes no tenant, store or scripts from the request (R-11-44, F-05) | Proved: every read of a bank's user is answered from the bank's view (R-11-48): `tenant-isolation.test.ts`, `security.test.ts`. The runtime's `release-reader` per tenant: [I-120](../../ISSUES.md#i-120-reads-that-are-not-yet-scoped-to-a-tenant) |

### Tampering

| Threat | Control | Status |
| --- | --- | --- |
| A published file is changed | Insert-only branch, fast-forward only, no force push (R-09-21) | Proved: `content.test.ts` |
| A stored release is replaced | Same id with another hash refused; no PUT, PATCH, DELETE | Proved: `ReleaseApiTests` |
| A package is altered between branch and runtime | The API checks the package against its manifest and against the signed statement of every file's hash, at storage and at activation | Proved: `ReleaseApiTests.A_package_that_does_not_match_its_manifest_or_its_signature_is_refused`; `e2e-p6-hot-update.test.ts` step 2b |
| A payment's record is edited in the database | Insert-only guard; hash chain per subject | Proved for the application path; a database administrator can still write rows, and the chain then shows it |
| A running worker runs code its release did not name | Tasks registered as `name@release`; a worker refuses a task not on its queue | Proved in the studio's export; runtime versioned workers are target |
| A caller's name for a file or a document writes or reads outside its folder | Import roots for `/import-branch` (R-09-58, F-07); path segments never `.` or `..` (R-11-44, F-16); a sample is a file name | Proved: `ApiAuthTests`, `security.test.ts` |
| A workflow or an action is changed to run other code in CI | Every action pinned to a commit SHA; read-only token on every workflow; no credentials left in the checkout (R-09-61, F-14) | Proved: CI job `pinned-actions`. Branch protection and CODEOWNERS: [I-123](../../ISSUES.md#i-123-the-repositorys-own-settings-and-the-images-the-tests-pull) |

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
| An error answer shows internals | Problem responses with a message, never a stack trace or an exception's text (R-11-43, F-17) | Proved for the studio: `security.test.ts`; review for the runtime |
| Another bank's names in the studio's listings | Checks, data browser, simulators, stubs, counts and usages filtered by tenant (F-13) | Proved: `security.test.ts` |
| A console's token is read by script injected into its page | A content security policy with no inline script, connecting only to the host and the bank's identity provider (R-09-60, R-11-43, F-12) | Proved: `SecurityHeadersTests`, `security.test.ts` |
| Content sent to the Claude API steers the assistant | The system prompt says content is data; writes ask the designer; nothing is published without a checker (F-29) | Proved: a sample is asked about every time and goes into a change set for its checker; an instruction in a tool result is shown in the panel (R-11-49): `assistant-injection.test.ts` |

### Denial of service

| Threat | Control | Status |
| --- | --- | --- |
| A huge file at the front door | Streaming parse; limits per port | Not decided: [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side) |
| A large body, a zip bomb or a pattern that backtracks holds a host | Body limit (R-11-41, F-10); every zip read within fixed limits (R-09-59, F-08); e-mail patterns bounded (F-09) | Proved: `security.test.ts`, `TelemetryTests` |
| One caller holds a host with heavy calls | Per-user limits on the studio's runs and imports (R-11-41); per-address limits on the console hosts (R-09-60, F-30) | Proved: `security.test.ts`, `SecurityHeadersTests` |
| An outside party never answers | Every await has a timeout (check L32) | Proved: mutation case |
| The studio's branch grows without bound | Reads cached by commit; old branches cost one request | Open in part: I-44, issue 6 |

### Elevation of privilege

| Threat | Control | Status |
| --- | --- | --- |
| A bank designer changes shared content | Scope rules (R-06-03) | Proved |
| A designer publishes without a checker | Maker-checker (R-06-04, R-11-19) | Proved |
| A script-behind escapes its sandbox | Only the injected interface; no host realm object in the TypeScript context and every slice timed; an audit hook in Python (R-08-18, F-01, F-02) | Proved: each script runs in a worker process with no environment, file or network and memory and time limits (R-08-20): `script-isolation.test.ts`, `sandbox.test.ts`, `python.test.ts`. A platform without namespaces or Landlock: [I-147](../../ISSUES.md#i-147-script-isolation-where-the-platform-has-no-namespaces-or-landlock) |
| A test run or an override acts with another bank's configuration | The run's tenant, store and scripts are the server's (R-11-44, F-05) | Proved: `security.test.ts` |
| SQL injection | No raw SQL anywhere in the runtime; the Temporal plugin writes every visibility value as one T-SQL literal | Proved: `NeutralityTests`, `TestQueryConverterKeepsEveryValueInsideOneLiteral` |
| A vulnerable dependency | `npm audit`, `dotnet list package --vulnerable`, govulncheck on every push (R-09-28, R-09-61, F-15) | Proved: CI job `security` |

## The source's findings

The source's own audit findings and its failing security tests (N15 in the plan) are about its services. They are raised with the source's owners (`docs/runtime/BASELINE.md`) and not carried here.
| A session goes on after the provider ended it, or on another instance after sign-out | A heartbeat with the provider every 5 minutes; 8 hours, 30 minutes idle, 1 hour for the command line (R-11-50) | Proved: `sessions.test.ts`. Rotation across instances: [I-148](../../ISSUES.md#i-148-a-refresh-token-rotated-on-one-instance-while-another-still-holds-the-older-one) |
