# Secure coding

The security habits for a change here. It replaces the source's security instructions and its scanning and remediation skills. The rules that hold are kept in short; the source's session, gateway and rate-limit figures belonged to its operations apps and wait on [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) and [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side).

## Rules

| Rule | Checked by |
| --- | --- |
| No secret in code, configuration, samples, tests, docs or commit messages. This repository is public. A secret is named by reference (`env:`, `file:`, `vault:`) and read through the one secret interface. | Check L37; gitleaks in CI (`security.yml`) |
| No internal host, address, default login or customer data from the source in anything written here. | Review |
| An error answer gives a message and a status, never a stack trace or an inner exception. | API tests (400, 404, 405, 409, 422 shapes) |
| Validate every input at the boundary: a request body, a package against its manifest, a binding against its port, a document against its schema. | Studio API tests; `ReleaseApiTests`; schema validation (C02) |
| No raw SQL. | `NeutralityTests` |
| No path built from user input without a check that it stays inside its root. | `security.test.ts` (R-11-44); `ApiAuthTests` (R-09-58) |
| Read a caller's archive within fixed limits, never by the sizes its headers claim. | `SafeZip`, `UNZIP_LIMITS` (R-09-59) |
| A pattern applied to a caller's text has bounded repetition, so it runs in linear time. | `security.test.ts`, `TelemetryTests` |
| Every web answer carries security headers; a page carries a content security policy with no inline script. | `security.test.ts`, `SecurityHeadersTests` (R-11-43, R-09-60) |
| A tenant, a store or an actor is never taken from a request body. | `security.test.ts` (R-11-44), `ApiAuthTests` (R-06-28) |
| Every CI action is pinned to a commit SHA, and every workflow names its token's permissions. | CI job `pinned-actions` (R-09-61) |
| A script-behind gets only the injected interface (`ctx`, `ports`, `rules`, `params`, `clock`, `ids`, `log`). | Sandbox tests; lint |
| Do not treat the sandbox as a security boundary for hostile code. Banks authoring scripts in a shared installation need an isolated worker first (D14). | ADR 0001 |
| Authorisation is server-side. A user interface may hide a control; it never is the control. | API tests |
| Add a dependency only when it is needed, at an exact version in the lock file. | Review; `npm ci` in CI |

## Scanning

The source scanned every pull request with SAST, a dependency audit, a file-system scan and a secret scan. This repository's CI runs a secret scan and dependency audits on every push (`.github/workflows/security.yml`, R-09-28, R-09-61), govulncheck on the Temporal SQL Server plugin among them. SAST is not wired. The last whole review is [review-2026-11.md](review-2026-11.md). The same audits by hand:

```
cd studio && npm audit --audit-level=high
cd runtime && dotnet list TransactHub.Runtime.slnx package --vulnerable
cd runtime/temporal-mssql && go run golang.org/x/vuln/cmd/govulncheck@v1.1.4 ./...
```

A finding at high or critical blocks the change. Fix it, or raise an Issue and a Recommendation that says why it does not apply.

At the review of 22 November 2026 (`review-2026-11.md`) the studio's audit reported no advisory, with or without its development tools, the runtime's NuGet packages none, and govulncheck three in the Temporal plugin's modules, fixed by upgrades (F-15).

## A vulnerability

- Fix it in the change that found it if it is inside the change. Otherwise list it under "Found, not fixed" and raise it.
- A vulnerability in the source is not fixed here. Raise it with the source's owners, as `docs/runtime/BASELINE.md` does.
- Do not describe an unfixed vulnerability in this public repository in more detail than its owner needs to find it.
