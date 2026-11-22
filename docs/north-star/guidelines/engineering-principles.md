# Engineering principles

The principles every change follows, in the north star. They replace the source's engineering principles. Many of the source's principles hold; where the north star changes one, the table says so.

## The principles

| # | Principle | What it means here | Checked by |
| --- | --- | --- | --- |
| P1 | A payment is data | Flows, rules, values and bindings are authored in the studio and released. Shared code holds no scheme table and no bank. | `BankConfigurationTests`, `spec/tools/validate.py` |
| P2 | Insert-only | A change of state is a new fact. A release, an activation, a rule version and a published file are never edited. | `InsertOnlyGuard` in the store; the release API's 405; `content.test.ts` |
| P3 | Facts apart from status | Current status is a projection over facts, never a column updated in place (plan W2). | Store design; review |
| P4 | One model | The context is the canonical ISO 20022 model. Only map blocks touch another format (`spec/CLAUDE.md`, Data). | Schemas; check catalogue |
| P5 | Database neutral | One EF Core model on SQL Server and PostgreSQL. No raw SQL. | `NeutralityTests`; every store test on both databases |
| P6 | Engine neutral | One interpreter, one seam, four engines. A workflow means the same on each. | The conformance kit on each engine |
| P7 | Pinned logic, dated values | A payment runs the logic of its release. Values, lists and registries are looked up by date and reach payments in flight. | Pins with hashes; lookup tests |
| P8 | Configuration carries values, not logic | A value a bank may change is a parameter with a default and bounds (`spec/CLAUDE.md`, DSL). | Bounds checks in the studio |
| P9 | No personal data in state | Workflow state, history, logs, tests and samples hold references and synthetic values only. | Review; the history scan of plan W1 is target |
| P10 | Fail closed | When money or the record is in doubt, stop. A missing binding or setting is refused, not defaulted. | `BankConfigurationTests`; review |
| P11 | Separation of duty | The checker is never the maker. | R-11-19; studio governance tests |
| P12 | Checks over assertions | A rule names the check that enforces it. Never weaken a check to make content pass. | `spec/CLAUDE.md`, Checks |

What changed from the source:

- **No database foreign keys** was a source rule. The north star has no rule on it: the store's model is in `StoreDbContext`, and integrity is kept by insert-only facts with a per-subject sequence and a unique index.
- **Schema per tenant** and **workers per bank** are gone. A tenant is a column. A worker serves a task queue derived from a scope (R-09-05), never a bank.
- **`BIGINT IDENTITY` keys and `DECIMAL(18,4)`** are replaced by string ids from the application and `decimal(19,4)` for money (plan W2).
- **Patch markers** on one workflow are replaced by releases (P7).
- **Hot reload of code** is gone. Only values are hot; code is released.

## When principles conflict

The source's order still holds:

1. safety of money and of the record;
2. data integrity;
3. security;
4. correctness;
5. performance;
6. convenience.

A real conflict is an Issue and a Recommendation in `docs/ISSUES.md`, not a choice made in code.

## Design intent and what is built

Some of the north star is target, not code: see the "Built" column of [../architecture-essentials.md](../architecture-essentials.md). Build toward the target, never against it. Do not add code that would have to be unwound when the target lands.

## Changes and reviews

- One story, one branch, one pull request (`spec/CLAUDE.md`).
- Tests first. A change is done when the common Definition of Done in `spec/CLAUDE.md` holds.
- A red check blocks the merge. Find the cause; do not bypass it.
- The source's waiver process (two leads, 24 hours) is not carried. Here a rule that is wrong is raised as an Issue and fixed in the rule and its check.
