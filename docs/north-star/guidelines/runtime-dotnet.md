# .NET in the runtime

How C# is written in `runtime/` and in the C# engine harnesses (`studio/engines/shared`, `studio/engines/dtfx/harness`, `studio/engines/elsa/harness`). It replaces the source's backend and microservices guidelines, its .NET instructions, its API agent and its code templates. What held in the source and still holds is kept, in short; what belonged to its services, bank workers and queues is gone.

## The baseline

- .NET 10, pinned by `runtime/global.json` (SDK 10.0.100, roll forward to the latest feature band). The DTFx and Elsa harnesses build on .NET 8.
- One solution: `runtime/TransactHub.Runtime.slnx`. Projects set `Nullable` and `ImplicitUsings` on.
- Tests are xUnit, in `runtime/tests/`, and run with `dotnet test` (Docker is needed for the database containers).
- Package versions are set in each project file. The source's central package versions, warnings-as-errors and `.editorconfig` are not adopted in `runtime/` today. Adopting them is a change of its own, with the check that keeps it.

## Structure

The source split each microservice into API, Application, Domain and Infrastructure projects. The runtime is not a set of microservices. It is split by what a project knows:

| Project | Knows | Must not know |
| --- | --- | --- |
| `TransactHub.Model` | The ISO 20022 model and `PaymentContext` | Any database, engine or scheme file format |
| `TransactHub.Store` | EF Core and the two providers | An engine or a scheme |
| `TransactHub.Ports` | Port interfaces and port bindings | An adapter's wire format beyond its own adapter |
| `TransactHub.Interpreter` | The package format (compiled from `studio/engines/shared`) | A database provider |
| `TransactHub.Engine.*`, `TransactHub.Engines.Durable` | One engine each | Another engine |
| `TransactHub.Activities.Wps` | The WPS scheme's own steps and rules | A bank |
| `TransactHub.Api`, `TransactHub.Native` | HTTP hosting | Business rules beyond the call they serve |

The dependency rule still holds: the model depends on nothing; the store depends on the model; hosts depend on the rest, never the other way.

## Rules

| Rule | Checked by |
| --- | --- |
| No bank is named in shared code, in any of the shapes the source used. A bank is configuration, hooks and port bindings. | `BankConfigurationTests.No_shared_code_names_a_bank` |
| A tenant is always given, never defaulted. The source defaulted to one bank in seven places (N5). | `BankConfigurationTests`; review |
| An outside system is reached through a port, with an adapter chosen by the bank's port binding. No `HttpClient` call to a bank system from an activity. | `BankConfigurationTests.The_example_bank_switches_core_banking_by_a_binding_alone` |
| A port with no binding, or a binding missing a setting, is refused. | `BankConfigurationTests.A_port_with_no_binding_and_a_setting_that_is_missing_are_refused_not_defaulted` |
| Data only through the store (EF Core). | `NeutralityTests` |
| Time from an injected `TimeProvider`, never `DateTime.Now`. Store and compare in UTC; show in the user's region only at the edge. | `StoreTests.Times_and_money_read_back_exactly`; review |
| An activity is pure, a read, or carries an idempotency key, before it may be retried (`spec/CLAUDE.md`, DSL). | Check catalogue in the studio |
| A workflow holds flow only. Rules and lookups run in activities. | Check catalogue |
| Nothing written in a workflow, a log or an exception message holds personal data. | Review; the history scan of plan W1 is target |
| An API refuses what it does not allow with a problem response and a status, never a stack trace. The release API answers 405 to PUT, PATCH and DELETE. | `ReleaseApiTests` |

The source's rules on task-queue names per bank, `ActivityInput`/`ActivityOutput` dictionaries, `Workflow.Patched` ladders and the `InjectState` signal are not carried. Queues come from scopes (R-09-05). A step's input and output are typed by its activity contract (Part 3). A payment changes only through declared operator actions (plan W4).

## Habits that still hold

- Async all the way, with a `CancellationToken`. Never `.Result` or `.Wait()`.
- Sealed classes and records by default; `required` and `init` for data.
- Nullable honestly modelled; no `!` to silence a warning.
- Validate at every boundary: a package against its manifest and hashes, a binding against its port, a request body before use.
- Stream large files; never hold a whole bulk in memory where a stream will do.
- Name things for what they are in the payment, not for the engine or the bank.

## Tests

See [testing.md](testing.md). In short: a success and a failure test for every behaviour; a store test runs on both databases; an engine change runs the kit.
