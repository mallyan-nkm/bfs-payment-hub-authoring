# Secure coding

The security habits for a change here. It replaces the source's security instructions and its scanning and remediation skills. The rules that hold are kept in short; the source's session, gateway and rate-limit figures belonged to its operations apps and wait on [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) and [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side).

## Rules

| Rule | Checked by |
| --- | --- |
| No secret in code, configuration, samples, tests, docs or commit messages. This repository is public. | Review; no scan in CI yet ([I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment)) |
| No internal host, address, default login or customer data from the source in anything written here. | Review |
| An error answer gives a message and a status, never a stack trace or an inner exception. | API tests (400, 404, 405, 409, 422 shapes) |
| Validate every input at the boundary: a request body, a package against its manifest, a binding against its port, a document against its schema. | Studio API tests; `ReleaseApiTests`; schema validation (C02) |
| No raw SQL. | `NeutralityTests` |
| No path built from user input without a check that it stays inside its root. | Review |
| A script-behind gets only the injected interface (`ctx`, `ports`, `rules`, `params`, `clock`, `ids`, `log`). | Sandbox tests; lint |
| Do not treat the sandbox as a security boundary for hostile code. Banks authoring scripts in a shared installation need an isolated worker first (D14). | ADR 0001 |
| Authorisation is server-side. A user interface may hide a control; it never is the control. | API tests |
| Add a dependency only when it is needed, at an exact version in the lock file. | Review; `npm ci` in CI |

## Scanning

The source scanned every pull request with SAST, a dependency audit, a file-system scan and a secret scan. This repository's CI (`.github/workflows/`) runs tests only. Until [I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) is decided, run these by hand before a change that adds a dependency or touches configuration:

```
cd studio && npm audit --audit-level=high
cd runtime && dotnet list TransactHub.Runtime.slnx package --vulnerable
```

A finding at high or critical blocks the change. Fix it, or raise an Issue and a Recommendation that says why it does not apply.

On 2026-11-22 the studio's audit reported 9 advisories (1 high, 2 critical), all in its development and test tools: Vite, Vitest and their dependencies, which the fixes move to new majors. The runtime's NuGet packages reported none. Moving those tools is a change of its own, with the suites green after it.

## A vulnerability

- Fix it in the change that found it if it is inside the change. Otherwise list it under "Found, not fixed" and raise it.
- A vulnerability in the source is not fixed here. Raise it with the source's owners, as `docs/runtime/BASELINE.md` does.
- Do not describe an unfixed vulnerability in this public repository in more detail than its owner needs to find it.
