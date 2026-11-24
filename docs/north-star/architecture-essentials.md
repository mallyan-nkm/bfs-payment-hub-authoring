# Architecture essentials

How the north-star system fits together. This replaces the source's `.github/architecture-essentials.md`. The plan is `docs/runtime/NSTAR-CONVERGENCE-PLAN.md`; the studio's spec is `spec/`. Where this page and either of them differ, they win: fix this page.

Each section says what is **built** in this repository today and what is **target**. A target is in the plan; it is not yet code.

## The one idea

A payment is data, not code. The source already said so, and then kept scheme tables, bank forks and one hand-written workflow. The north star goes the whole way:

- one ISO 20022 model, in the database and in the workflow context;
- one insert-only store, with scheme and bank as columns;
- workflows authored in the studio, released insert-only, pinned per payment;
- four engines that run the same released package, proved equal by one conformance kit;
- a bank is configuration: rule values, hooks and port bindings. Shared code never names a bank.

If a change makes shared code know a scheme's table or a bank's name, it is wrong. Revisit the design.

## Layout

| Folder | What it is | Built |
| --- | --- | --- |
| `spec/` | The studio's specification: design parts, schemas, the factory pack, the example bank, the conformance kit, the backlog | Yes |
| `studio/` | The studio: server, interpreter, rule service, sandboxes, simulators, checks, export, governance, web app | Yes |
| `studio/engines/` | The kit on each engine: Temporal, DTFx, Flowable, Elsa; `shared/` holds the one C# interpreter | Yes |
| `runtime/` | The .NET 10 runtime: model, store, release API, File API and front door, interpreter, engine bindings and hosts, native activities, ports, operations and service consoles, test bench | Yes, for one bank per runtime; what of the source's platform is here: `docs/runtime/PLATFORM-PARITY.md` |
| `deploy/laptop/` | The studio and one bank's runtime in Docker containers (R-09-80) | Yes |
| `runtime/temporal-mssql/` | Temporal's persistence and visibility on SQL Server, as a plugin we keep (D2) | Yes (P4) |
| `content-configurations/` | What the studio publishes and what a bank configures; the studio's only store | Yes |

## The studio is the only authoring tool

- A designer authors workflows, rules, tables, maps, ports, simulators, hooks and configuration sets in the studio, and tests them on its interpreter with a virtual clock.
- The studio reads and writes only the GitHub branch `TH_CONTENT_REPO`/`TH_CONTENT_BRANCH`, folder `content-configurations/` (R-11-21 to R-11-25). It keeps no local copy. A change set in progress is its own branch, `studio/<change set id>`.
- Approval is maker-checker: the checker is never the maker (R-11-19). It publishes the change set as one commit, or opens a pull request in pull-request mode (D8).
- A published file is never rewritten (R-09-21). The two exceptions are `governance/users.yaml` and `governance/settings.yaml` (I-44).
- The studio never reads or changes a running payment (R-10-01).
- From `spec/` it reads only its schemas and the conformance kit.

The source's workflow JSON files, its DSL schema and its workflow-builder agents have no place here. The DSL is the spec's (Part 5), checked by the catalogue in `spec/design/05-workflow-dsl.md`.

## Names, scopes and banks

- A name is `<scope>.<type>.<name>.v<major>`. Scopes are `p` (platform), `<cluster>.p` (for example `uae.p`), `<cluster>.<scheme>` (for example `uae.wps`) and `b` (bank). See `spec/CLAUDE.md`.
- A scheme is a leg on the common lifecycle. WPS is the only factory scheme (I-37), and it is provisional.
- A refund is an order of its own on the platform's refund lifecycle (`p.wf.refund.v1`): the platform finds the earlier payment by id, converts and prices on the bank's rates and charges, blocks, approves and posts; the scheme only answers and returns funds in its slot (R-12-19 to R-12-22, I-133). Built.
- A bank is a tenant. Its content lives in `content-configurations/banks/<bank>/`: its bank pack, configuration sets, hook bindings and port bindings, effective-dated.
- `BankConfigurationTests.No_shared_code_names_a_bank` fails the runtime build if shared code names a bank. `spec/tools/deny-names.txt` does the same for the spec.

## The ISO 20022 model and the workflow context

- `runtime/src/TransactHub.Model` holds the internal model on pain.001 and pacs.008 shapes (plan W1). Built.
- A WPS salary file is one bulk; each employee line is one transaction. Scheme fields with no ISO element go into typed supplementary data, never into columns.
- A workflow carries only `PaymentContext`: tenant, subject id and kind, scheme, direction, release, as-of date and the facts the flow branches on. Parties, accounts and amounts are read from the store by id, so engine history holds no personal data. Built.

## One store

`runtime/src/TransactHub.Store`, through EF Core only (plan W2). Built.

- Payments, bulks, parties, hash-chained facts, messages, exception items, releases and activations, effective-dated rules, lists and configuration values, and the outbox.
- Insert-only. Current status is a projection over the facts. Each fact records the hash of the fact before it on its subject's chain, and its own SHA-256 hash over both; a check walks the chain and names the first fact that was changed.
- SQL Server and PostgreSQL on the same model. The provider is chosen in one place, `StoreDbContext.Configure`.
- `NeutralityTests` fails the build on a database driver, raw SQL, a T-SQL idiom or a provider call anywhere else.
- Every store test runs on both databases, in Testcontainers.

There is no schema per scheme and no schema per bank. The source's per-scheme tables are mapped to the new tables for a cut-over (plan W2).

Rules: [guidelines/store-and-migrations.md](guidelines/store-and-migrations.md).

## Four engines on one store

All four run one interpreter of the expanded workflow, behind one seam (`IEngine` in `studio/engines/shared/Interpreter.cs`), proved by the conformance kit (57 scenarios). Temporal is the default engine, on the SQL Server plugin (R-09-79).

| Engine | How it is hosted (D3) | Its own state | Kit | On the store |
| --- | --- | --- | --- | --- |
| Temporal | Temporal service; workers poll task queues | PostgreSQL, or SQL Server through `runtime/temporal-mssql` | Not run on 23 November 2026 (the test server did not start here) | Yes, both databases |
| DTFx | A library; workers share the SQL Server task hub | SQL Server only (D7) | 59 of 59 (24 November 2026) | Yes, payments data on both databases |
| Elsa 3 | Elsa Server; the interpreter runs inside one Elsa activity | Both, through EF Core | 59 of 59 (24 November 2026) | Yes, both databases |
| Flowable 7 | Flowable REST server; .NET external workers over REST | Both | 56 of 56 on 22 November 2026, before CK-PARK-03 (embedded harness on H2) | Yes, both databases (the REST server, in virtual and in production mode) |

Each installation runs one engine (D1). The source's `IsPatched` ladder is not carried: versions come from releases, not patches.

The long-running engine host (`runtime/tools/TransactHub.EngineHost`) runs a host per distribution, side by side, builds each host's plan from the stored package (R-09-94), and takes the messages the front door correlates to a running payment (R-09-93). On DTFx, Elsa and Flowable each distribution runs on one engine service that serves all its payments in real time and survives a restart of its host: DTFx and Elsa rebuild it from its history in the store, Flowable's one binding per distribution from its journal, with one deployment in the distribution's tenant (R-09-196 to R-09-199).

## Releases, activations and hot update

- `studio export` writes a package per engine target: expanded workflows, pins as `name@release` with hashes, one worker per task queue. Released packages go to `content-configurations/releases/`, written once.
- The runtime's release API (`runtime/src/TransactHub.Api`): `POST /releases` stores a package once and refuses the same id with another hash; `POST /activations` records from when a release is the one new payments take; `POST /import-branch` stores the releases and activations of a checked-out copy of the branch. PUT, PATCH and DELETE answer 405. Built.
- A payment is pinned to the release active at its acceptance and finishes on it. Rollback is a new activation row.
- Values, lists and registries are looked up by date and reach payments in flight. Logic is pinned and does not. A rule value changes through maker-checker in the studio, with no deployment.

Rules: [guidelines/versioning-and-releases.md](guidelines/versioning-and-releases.md).

## Files: one way in and out (the File API)

Owner's decision of 23 November 2026 (I-158). Built.

- Every file the runtime receives or sends passes through one runtime service, the File API (`runtime/src/TransactHub.FileApi`, its own process and image). Nothing else reads a partner's folder or writes to one.
- **In.** A channel's or a corporate's HTTPS upload (`POST /files`, system clients of the bank's identity provider), a person's upload (`POST /files/ui`), a file the SFTP gateway hands in, and the inboxes it polls on the bank's binding of `p.port.file-transfer.v1` (a folder on a laptop, a partner's SFTP folder at a bank).
- **On arrival.** The message must be one the active distribution receives; size, type and name checks; a duplicate by hash or by name is refused; an encrypted file is decrypted by `p.protect.decrypt-file.v1` with the bank's key; the file is kept once in the store with its signed SHA-256, linked to its payment, each step a fact (R-09-82, R-09-83).
- **The front door** runs in the same process, behind the intake (R-09-85): a start message pins the payment to the bank's active distribution, runs the scheme's intake map on its worker, routes it by the dispatch table and `p.table.workflow-select.v1`, and starts it on the bank's engine host; a message for a running payment (an ACK or NAK, a PSI, an answer) is correlated to its payment and handed to the host of the distribution the payment is pinned to. The studio's deploy runner routes a start message the same way (`studio/src/deploy/route.ts`, one fixture for both).
- **Out.** A workflow's file goes to the File API's outbox (`POST /outbox`, or any put on `p.port.file-transfer.v1` on a host that names `TH_FILE_API_URL`), is kept with its payment and leg, and is delivered by the bank's binding: `sftp` (the Central Bank, the labour ministry, core banking; host keys pinned by reference, R-09-86), `directory` or `simulator`. Each delivery is a fact; a resend from the operations or service console sends the kept bytes again (R-09-84).

```
channel / corporate --HTTPS--+
person (upload screen) ------+--> File API: check, decrypt, keep --> front door --> engine host (start, or the running payment)
partners' SFTP inboxes ------+        |
laptop inbox folder ---------+        +-- outbox <-- workflows' file sends, consoles' resend
                                      +--> sftp / directory adapter --> Central Bank, ministry, core banking
```

## Ports and adapters

- An outside system is a port. Core banking is `TransactHub.Ports.ICoreBanking`, with adapters `simulator`, `gateway-rest` and `file-bulk` (R-12-15). Built.
- Production adapters sit beside the simulators (R-09-90): screening over REST, the channel's notices by e-mail and SMS, the business calendar and the participant directory from reference tables or REST, the outward facts as a signed webhook. Each bank's binding is a shape until the bank names its counterparts (I-163).
- A bank chooses an adapter by an effective-dated port binding in its folder (kind `port-bindings`). The example bank moves from the simulator to the gateway on 2026-12-01 by a binding alone, and a test shows it.
- A port with no binding, or a setting that is missing, is refused, not defaulted.
- Native code-behind for activities bound `native` runs in `runtime/src/TransactHub.Native` (R-08-14).

## One bank per runtime, and consent

- A runtime serves one bank, set at install (`TH_BANK`, R-09-76); it refuses another bank's distribution, configuration release, package, kept file and backup, whatever the caller's roles (I-154). The studio stays multi-tenant.
- Every change of the system is asked for by one person and carried out when another, with the bank approver's role, consents (R-09-77, I-155): installs, activations, drains, removals, rollbacks, restores, the troubleshooting actions, a register change, an unmask, an erasure, a rebuild of the read model.
- The hosts run as a login that may insert and read the store, never update or delete it (R-09-81).

## What is not decided

| Subject | Issue |
| --- | --- |
| What the File API and the front door leave open: file rules, inbox messages, host keys | [I-158](../ISSUES.md#i-158-the-file-api-and-the-runtimes-front-door) to [I-161](../ISSUES.md#i-161-the-banks-sftp-partners-have-no-pinned-host-key-and-the-logins-form) |
| The throughput of one engine service per distribution, and how long it takes to start | [I-300](../ISSUES.md#i-300-a-distributions-engine-service-runs-one-work-item-at-a-time), [I-301](../ISSUES.md#i-301-a-distributions-history-chain-is-read-whole-at-every-start-of-its-service) |
| Performance targets, deployment sizes | [I-68](../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side), [I-143](../ISSUES.md#i-143-the-service-level-targets-the-alert-rules-have-no-number-for) |
| Who keeps the SQL Server plugin on each Temporal release | [I-42](../ISSUES.md#i-42-who-keeps-the-sql-server-plugin-for-temporal-and-how-an-upgrade-is-proved) |
| The original platform's capabilities the owner deferred, and those with no home yet | [I-188](../ISSUES.md#i-188-the-original-platforms-capabilities-the-owner-deferred), [I-189](../ISSUES.md#i-189-capabilities-of-the-original-platform-with-no-home-yet) |
| Where scripts run (D14), whether NRules is the evaluator (D13) | `docs/adr/0001-studio-stack.md` |
