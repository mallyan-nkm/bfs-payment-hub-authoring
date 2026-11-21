# Part 8. Script-behind and conformance

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 8.1 Purpose

The script-behind is the executable reference of a contract, written with AI and run in the studio. The code-behind is what runs in production, in any language. Both must pass the same conformance pack.

## 8.2 Languages

Python and TypeScript. A script is written in one of them. Blocks in different languages mix freely in a workflow because they meet only through the context.

## 8.3 The sandbox interface

Both languages get the same interface, injected. Nothing else is reachable.

| Member | Purpose | Notes |
| --- | --- | --- |
| `ctx.read(path)` | Read a declared context path | Fails on an undeclared path |
| `ctx.write(path, value)` | Write a declared context path | Fails on an undeclared path; recorded as a delta |
| `ctx.outcome(name)` | Return a declared outcome | Ends the script |
| `ctx.fail(error, detail)` | Raise a declared error | Retryable or not, as declared |
| `ports.<port>.<operation>(message)` | Call a declared port | Bound to a simulator in the studio |
| `rules.evaluate(ruleSet)` | Evaluate a declared rule set | Returns findings and decisions |
| `params.get(name)` | Read a declared parameter | Already resolved by date and tenant |
| `clock.now()` | The evaluation instant | Virtual in the studio; never the system clock |
| `ids.idempotencyKey()` | The key for this step | The same on every attempt |
| `log.info(message, fields)` | Structured log | Personal data is redacted |

## 8.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-08-01 | A script MUST be Python or TypeScript and MUST use only the sandbox interface. | Sandbox tests (C07, C08) |
| R-08-02 | A script MUST NOT reach the network, the file system, the system clock, randomness or the environment. | Lint L28; sandbox tests |
| R-08-03 | The same input and the same simulator scenario MUST give the same output. | Determinism test in the conformance pack |
| R-08-04 | Canonical types MUST be generated for both languages from the one model. | Type generation test (C03) |
| R-08-05 | The two sandboxes MUST behave identically. One test suite runs against both. | Sandbox parity suite (C07, C08) |
| R-08-06 | A script MUST stop at its attempt timeout. A script that does not stop is killed and the attempt fails as transient. | Sandbox test |
| R-08-07 | An activity MUST declare its binding: `script`, `native`, `builtin` or `subflow`. A `subflow` binding has no script; its steps have their own bindings. | Schema `common#implementation` (L00) |
| R-08-08 | A conformance pack is the set of an artefact's tests marked `conformance: true`. It is language-neutral. | Schema `test-case` (L00) |
| R-08-09 | A code-behind MUST pass the conformance pack of its major line before a manifest may name it. | Release check (C19) |
| R-08-10 | Script and code-behind carry the same release number. A change to the conformance pack is a release of the artefact. | Release check (C19) |
| R-08-11 | AI-authored content MUST record provenance: model, prompt reference, inputs and scheme clauses. | Schema `common#provenance` (L00) |
| R-08-12 | AI output MUST pass every gate before a person reviews it: type check, sandbox lints, contract enforcement, its own tests, simulator self-check. | Authoring pipeline test (C16) |
| R-08-13 | A person MUST approve every AI-authored rule and script. Approval is by behaviour: test results and context diffs. | Maker-checker (C17) |
| R-08-14 | A `native` code-behind runs in the runtime's native host, never in the studio. The studio's interpreter, and every deployed worker, call it with the attempt's view (its reads only), its parameter values as configured and the engine's time, over the native protocol (`POST /native`, `TH_NATIVE_HOST`). Its outcome MUST be one of the activity's outcomes and its writes MUST be within its declared writes. Without a native host the attempt fails with `NativeHostUnavailable`, never with a guessed result. | Native tests (`studio/test/native.test.ts`, `runtime/tests/.../NativeHostTests.cs`) |

## 8.5 AI authoring loop

1. Inputs: the contract, canonical types, scheme clauses, rules and sample messages.
2. AI drafts the script and its test cases.
3. Gates run. A red gate returns to step 2 with the failure as input.
4. The designer reviews behaviour.
5. Maker-checker publishes, with provenance stored.

AI proposes; the gates decide.

## 8.6 Test levels

| Level | Target | Bench |
| --- | --- | --- |
| `ruleset` | A rule set or table | None |
| `activity` | One activity | Simulators for its ports |
| `hook` | One hook | Simulators for its ports |
| `handler` | One handler | Simulators for its ports |
| `leg` | One leg | Simulators for every port the leg reaches |
| `end-to-end` | A platform workflow with a leg | A full test bench |

## 8.7 Examples

- Conformance fixtures: `factory/uae.aani/tests/AANI-RULE-002.yaml`, `factory/p/tests/P-RES-001.yaml`.
- A lookup test with a tenant and two dates: `factory/uae.aani/tests/AANI-RULE-003.yaml` and `AANI-RULE-004.yaml`.

## 8.8 Open decisions touching this part

D7 (script as a production runtime), D14 (where scripts run).
