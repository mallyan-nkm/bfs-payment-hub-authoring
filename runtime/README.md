# TransactHub runtime

The new runtime, built beside the TransactHub source (`TransactHub-src.zip`), which is never changed in place. The plan is `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`; this is phase P1.

| Project | What |
| --- | --- |
| `src/TransactHub.Model` | The internal payment model on ISO 20022 (pain.001 / pacs.008 shapes), and `PaymentContext`, which is what a workflow carries: ids, release, as-of date and branch facts, never personal data. |
| `src/TransactHub.Store` | The generalized store, insert-only, through EF Core only: payments, bulks, parties, hash-chained facts, messages, exception items, releases and activations, effective-dated rules, lists and configuration values, the outbox. |
| `src/TransactHub.Store.Migrations.SqlServer`, `…PostgreSql` | The migrations of each provider, generated from the one model. |
| `tests/TransactHub.Store.Tests` | Every store test on SQL Server 2022 and PostgreSQL 16 (Testcontainers), and the database-independence check. |

## Commands

```
cd runtime
dotnet test                       # needs Docker: starts SQL Server and PostgreSQL containers
dotnet tool restore
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.SqlServer -s src/TransactHub.Store.Migrations.SqlServer -o Migrations
dotnet ef migrations add <Name> -p src/TransactHub.Store.Migrations.PostgreSql -s src/TransactHub.Store.Migrations.PostgreSql -o Migrations
```

A model change needs a migration for both providers; a test fails if either is missing.

## Database independence

The runtime reaches its data only through EF Core. The provider is chosen by `StoreOptions` (`SqlServer` or `PostgreSql`), in one place (`StoreDbContext.Configure`). `NeutralityTests` fails the build on a database driver, a raw connection, raw SQL, a T-SQL idiom or a provider call anywhere else in `src/`, and on a provider package referenced by any project but the store.
