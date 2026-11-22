# Testing

How to test a change here. It replaces the source's testing instructions. The source's central rule holds unchanged in substance: every behaviour has a success test and a failure test.

## The rule

Every unit of behaviour you add or change ships at least two tests: one where it succeeds and one where it fails the designed way. A failure test asserts the designed handling: the outcome, the reason code, the compensation that ran, the HTTP status and message. "Does not throw" is not an assertion.

| You change | The success test | The failure test |
| --- | --- | --- |
| An activity or block | Its primary outcome | Each non-primary outcome it declares, with its reason code |
| A workflow or leg | It settles | It is rejected, and what was reserved is undone |
| A rule or table | An example that passes | An example that fails, at each bound |
| An API route | The success response | A bad body (400), a refused method (405), a clash (409) |
| A store operation | It reads back as written, on both databases | It refuses an update, a delete, or a second content for one id |
| A port adapter | The answer it maps | A missing binding or setting is refused |

Tests are written first and seen to fail (`spec/CLAUDE.md`).

## Where tests live

| Suite | What | Command |
| --- | --- | --- |
| Spec tools | Schemas, the check catalogue, expansion, one mutation per check | `cd spec && python3 tools/validate.py && python3 tools/mutation_tests.py` |
| Studio white-box | Store, names, hashes, rules, sandboxes, simulators, interpreter, checks, export, every API route, governance | `cd studio && npm test` |
| Studio black-box | The HTTP API, the command line, the web app in Chromium, end to end | `cd studio && npm run test:blackbox` |
| Factory and bank tests | The pack's own test cases on the interpreter | `npm run studio -- test` |
| Conformance kit | 48 scenarios, the same expected traces on every engine | `npm run studio -- kit`, then each engine's `run-kit.ts` |
| Runtime | Store on both databases, neutrality, bank checks, release API, WPS golden corpus | `cd runtime && dotnet test` |
| Temporal on SQL Server | Temporal's own persistence suites, the durability check, the kit, the WPS file | `runtime/temporal-mssql/README.md` |

## What counts as proof

- **A check fires.** A new static check has a mutation case that breaks the repository and expects the check to fire (`spec/CLAUDE.md`).
- **Both databases.** A store test runs on SQL Server and PostgreSQL. Passing on one is a failure.
- **Every engine.** An engine change passes the 48 kit scenarios on that engine. A trace that differs from the interpreter's is a failure.
- **The source as reference.** The WPS rules in the runtime answer the golden corpus generated from the source's code exactly (`runtime/tests/TransactHub.Store.Tests/Golden`). The parity matrix (`docs/runtime/PARITY.md`) names the proof of each source item.

## What not to write

- No load, back-pressure or soak tests in the studio (`spec/CLAUDE.md`). Runtime performance targets are [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side).
- No real personal data, account numbers or bank values in a test or sample. Use synthetic values.
- No test that needs a network service the repository does not start for itself. A test that needs Docker says so in its suite.

## Naming

Name a test for the behaviour it shows, as a sentence: `A_future_dated_rule_does_not_hide_the_rule_in_force`, `it('never rewrites a published file: ...')`. The source named tests `{Scheme}_{Scenario}_{Outcome}`; scheme names in test names are fine, bank names are not.
