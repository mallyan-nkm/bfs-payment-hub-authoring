# Failure modes

What can go wrong in the north star, what it does, and what stops or finds it. It replaces the source's FMEA, which scored failure modes of its own services. The source's scores are not carried. Scoring severity, occurrence and detection needs the targets of [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side); until then each mode names its control and whether a test proves it.

| # | Failure mode | Effect | Prevented or found by | Proved |
| --- | --- | --- | --- | --- |
| F1 | A published release is edited | Payments in flight run logic no one approved | Insert-only branch (R-09-21); `POST /releases` refuses the same id with another hash; 405 on PUT, PATCH, DELETE | `content.test.ts`, `ReleaseApiTests` |
| F2 | A row in the store is updated or deleted | The record no longer shows what happened | `InsertOnlyGuard`; the fact chain names the first changed fact | `StoreTests` on both databases |
| F3 | Two writers take the same sequence number on a payment | Facts out of order | Unique index on (subject, sequence); a clean conflict | `StoreTests.Two_writers_on_one_subject_never_share_a_number` |
| F4 | A future-dated rule hides the rule in force | Wrong charges or limits (the source's N1) | Date first, then version | `StoreTests.A_future_dated_rule_does_not_hide_the_rule_in_force` |
| F5 | A hot update changes a payment that asked for its acceptance date | A payment's answer changes in flight | Lookup as of the date the rule declares | `StoreTests.A_payment_asking_as_of_its_acceptance_gets_the_same_answer_after_a_hot_update` |
| F6 | Engines disagree on a workflow | A payment's outcome depends on the installation | One interpreter; the kit's 48 scenarios on each engine | `run-kit.ts` per engine; CI job `kit` |
| F7 | A test package reaches production | A simulator answers for a real party | A package with simulators is not promotable | `ReleaseApiTests` |
| F8 | A bank is defaulted when none is given | One bank's values applied to another (the source's N5) | A tenant is always given; a port with no binding is refused | `BankConfigurationTests` |
| F9 | Shared code names a bank | A fork grows back | Build check on bank names | `BankConfigurationTests.No_shared_code_names_a_bank` |
| F10 | Code reaches the database around EF Core | One database works and the other does not | Build check on drivers, raw SQL and T-SQL idioms | `NeutralityTests` |
| F11 | A migration exists for one provider only | Deployment fails on the other database | Build check on both migration assemblies | `NeutralityTests.Each_provider_has_migrations_for_the_whole_model` |
| F12 | Personal data in engine history | It stays in history for good (the source's N3) | `PaymentContext` carries ids only | Plan W1 asks for a history scan; target |
| F13 | The branch moves while the studio publishes | A lost or doubled change | Fast-forward only; read, recheck and retry once, then stop | `content.test.ts` |
| F14 | A maker approves their own change | No second pair of eyes | Checker differs from every maker (R-11-19) | Governance tests |
| F15 | The Temporal server loses state on a crash | Payments stall or repeat | SQL Server plugin passes Temporal's persistence suites; a killed server resumes its timers | `runtime/temporal-mssql/scripts/durability-check.sh` |
| F16 | An id longer than 255 bytes reaches Temporal on SQL Server | A write is refused | Ids built from references; length limit set | I-45 |
| F17 | A script-behind reaches outside its interface | Non-deterministic or unsafe runs | Sandboxes with only the injected interface | Sandbox tests; not a security boundary (D14) |
| F18 | An outside party does not answer | A payment waits for ever | Every await has a timeout (check L32) | Mutation case for L32 |
| F19 | A non-primary outcome is not handled | A payment stops with no path | Every non-primary outcome is handled (check catalogue) | `validate.py`, `mutation_tests.py` |
| F20 | A secret is committed | Credentials exposed in a public repository | Review only today | No scan in CI: [I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment) |
| F21 | An operator changes a running payment without a second approver | Money moved on one person's word | Declared operator actions (plan W4) | Not built: [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |
| F22 | Someone stores or activates a release without leave | Unapproved logic goes live | None in the API today | [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |

The source's top risks were in parts the north star reaches through ports (screening, core banking, exchange rates) or has not decided (notification, reconciliation). They return as failure modes of those ports and lifecycle steps when each is built, each with its simulator scenario.
