# The store and its migrations

Rules for the runtime's data. They replace the source's database-layer, EF Core migration and audit-trail guidelines. The plan is W2; the code is `runtime/src/TransactHub.Store` and its two migration assemblies.

## What the store is

One model, `StoreDbContext`, with these row types: parties, bulks, payments, facts, messages, exception items, releases, activations, versioned values (rules, lists, configuration values) and the outbox. Every row type is marked `IInsertOnly`.

The source spread the same data over a schema per scheme and a schema per bank, with a fallback to one scheme for an unknown name (N7). None of that is carried. Scheme and tenant are columns.

## Rules

| Rule | Why | Checked by |
| --- | --- | --- |
| Reach data only through EF Core. No driver, connection, raw SQL, stored procedure, table-valued parameter, MERGE, lock hint, `OUTPUT INSERTED`, memory-optimised table or change data capture. | One code path on two databases | `NeutralityTests.No_source_file_reaches_a_database_except_through_EF_Core` |
| Only the store and its two migration assemblies reference a provider package. | The provider is chosen in one place | `NeutralityTests.Only_the_store_and_its_migrations_reference_a_provider_package` |
| The provider is a `StoreOptions` value (`SqlServer` or `PostgreSql`), applied in `StoreDbContext.Configure` and nowhere else. | The choice is configuration | `NeutralityTests` allows the provider call there only |
| Never update or delete a row. Record a new fact. | Insert-only (P2) | `InsertOnlyGuard`, a save interceptor that throws `InsertOnlyViolation`; `StoreTests.The_store_refuses_any_update_or_delete` |
| Current status is a projection over facts. | Facts apart from status (P3) | `StoreTests.Facts_chain_by_hash_and_the_status_is_their_projection` |
| A fact links to the one before it on its subject by hash. | Tamper evidence | The chain check names the first changed fact |
| Order on a subject is a sequence number with a unique index on (subject, sequence). A second writer gets a clean conflict. | No row locks to port | `StoreTests.Two_writers_on_one_subject_never_share_a_number` |
| Ids and times come from the application. Times are `DateTimeOffset` in UTC. | No database clock | `StoreTests.Times_and_money_read_back_exactly` |
| Money is `decimal(19,4)`. | Same on both databases | Same test |
| Scheme fields with no ISO element are typed supplementary data in a JSON column, never new columns. | One model (P4) | Review; schema of the scheme's messages |
| No personal data outside the party and account rows. Workflow context carries ids. | P9 | Review; the history scan of plan W1 is target |
| A rule version is chosen by date first, then by version. | Fixes the source's bug N1 | `StoreTests.A_future_dated_rule_does_not_hide_the_rule_in_force` |

## Migrations

The source kept migration-owning contexts per schema, baselined existing schemas by hand, and forbade `Migrate()` at runtime. Here:

- There is one model. Each provider has its own generated migrations assembly: `TransactHub.Store.Migrations.SqlServer` and `TransactHub.Store.Migrations.PostgreSql`.
- A model change needs a migration for both, made with the commands in `runtime/README.md`. `NeutralityTests.Each_provider_has_migrations_for_the_whole_model` fails if either is missing or out of step.
- Migrations are generated. Do not edit one by hand.
- Every store test runs twice, on SQL Server 2022 and on PostgreSQL 16, in Testcontainers (`Databases.cs`). A test that passes on one and fails on the other is a failed build.

How a migration is applied to a bank's database, and by whom, is part of the deployment ([../deployment/deployment.md](../deployment/deployment.md)).

## Audit and tamper evidence

| Record | Where | How it is chained |
| --- | --- | --- |
| A payment's facts | The store, `FactRow` | SHA-256 over the previous hash and the fact; one chain per subject |
| A change set's events, from first draft to publish | The branch, `audit/change-sets/<id>.yaml` | Each record hashes the one before |
| An event outside a change set | The branch, `audit/events/<date>/` | One file per event |
| The branch itself | Git | Commits; the studio never force-pushes (R-09-21) |

The source's audit design also asked for an external witness, WORM storage and tokenised personal data in audit rows. None of these is decided for the north star. Raise it with [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it), which covers who acts and how it is audited, before building one.

## Bulk writes and the read side

- Bulk insert goes through EF Core batching behind one interface (plan W2).
- The report database is fed from the `outbox`, not from change data capture. What it holds is [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side).
- Free-text search of payments belongs on the store, not in Temporal visibility ([I-46](../../ISSUES.md#i-46-text-search-attributes-on-sql-server-are-not-indexed)).

## Engine state is separate

Each engine keeps its own state in its own database, beside the store: see the table in [../architecture-essentials.md](../architecture-essentials.md). The rules above are for the runtime's data. Temporal's state on SQL Server follows `runtime/temporal-mssql/README.md`, which needs its own collation and snapshot setting ([I-47](../../ISSUES.md#i-47-what-a-banks-sql-server-must-allow-for-temporal)).

## Mapping the source's tables

Every source WPS table, and the shared header and cycle-state tables, is mapped to the new tables in a mapping kept in `runtime/docs/data-mapping.md` (plan W2), with a migration tool for a cut-over. That file is target; it is not in the repository yet.
