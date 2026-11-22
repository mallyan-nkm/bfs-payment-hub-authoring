# TransactHub runtime: the north-star convergence plan

Status: **proposed, for acceptance before any runtime code is built.**
Input: `TransactHub-src.zip` (commit `666f1ee`), the N* architecture, the AI architecture review (21 issues), the competition view.
Rule we keep: the source is never changed in place. The new runtime is built beside it, and the source stays as the reference and as the baseline we compare against.

## 1. What the source is today

| Area | Today | N* target |
| --- | --- | --- |
| Payment model | No ISO 20022 types. A flat `transaction_headers` table is copied into five scheme schemas. WPS has 64 tables of its own, plus 21 in the report database. | One ISO 20022 internal model (pacs.008 / pain.001 / pacs.002 / pacs.004 / camt.054 shapes) in the database and in the workflow context. |
| Storage | Schemas per scheme (`wps`, `fts`, `npss`, `ipi`, `cbft`, `edda`, `xbdr`) and one per bank (`adib`). `RailSchemaResolver` picks a schema by scheme name and falls back to `ipi` without telling anyone. | One generalized store: scheme and bank are columns, never schemas. |
| Database | SQL Server only. There are 102 raw `SqlConnection` uses, 24 stored procedures, about 30 table-valued parameter types, MERGE with HOLDLOCK, 36 files with lock hints, memory-optimised tables, and change data capture for the report database. | Database-neutral: SQL Server and PostgreSQL on the same code. |
| Changes to records | MERGE/UPDATE on cycle states, hot caches, FTS inward tables and status columns. Some pieces are already insert-only: `TransactionStateLog`, `workflow_events`, the CB posting records and the hash-chained audit. | Insert-only facts with a hash chain. Current status is a projection. |
| Workflow context | A `Dictionary<string,string>`, and the whole of it goes to every activity. The SIF upload puts email, account, CIF and IBAN into it, so they stay in engine history for good. | A typed context: the payment's id and release, plus the few facts the flow branches on. Personal data stays in the store. |
| Engines | Temporal (on PostgreSQL) and DTFx (on SQL Server), behind `IWorkflowRuntime`. There is no Elsa and no Flowable. On DTFx `IsPatched` is always true. | Temporal, DTFx, Elsa and Flowable behind one seam. Each installation runs one engine, and all four pass one conformance kit. |
| Workflow language | The step types are sequential, conditional, parallel, fan_out, hook and wait_for_signal. It has no timer, no leg or sub-workflow call and no rule step. `state_effect` is ignored, and the runner hard-codes step ids and one workflow name. | The studio's package format 0.2: the platform lifecycle, scheme legs as child workflows, segments, hooks, rule steps, timers and awaits on a business calendar. |
| Versions | A running payment stays on its definition (it is loaded once and recorded in history). Activity code is **not** pinned: hot reload over MQTT replaces the code that runs in flight. Definitions can still be edited before Production and when moved to Deprecated. Rules are effective-dated, but the version is chosen before the date is filtered, and the date used is "now" (bug N1). | Releases are insert-only. Each payment is pinned to a release (workflow, activities with checksums, rules). Rules and lists are hot-updated by effective date, as of a date fixed for the payment. |
| WPS | 112 activity names (about 95 behaviours, 58,700 lines). They contain the shared lifecycle as `wps.*` code: approval, charges, balance, posting, reversal, reconciliation, notification. | A short WPS leg (`uae.wps.*`) on the common lifecycle (`p.*`, `uae.p.*`). |
| Banks | The DIB, ADIB, ENBD and FAB activity projects are forks. DIB has 17 activities and copies of the repository, transaction codes and posting legs. The same validation rule is copied four times. The default tenant is "DIB" in four option classes and three services. Workflow names carry the bank. | No bank name in shared content. A bank is a set of rule values, hooks and port bindings. |

The surveys behind this table are in `docs/runtime/SURVEY.md`.

## 2. Target structure

```
studio/                      the no-code studio (as now; spec/ stays at the root until decision D4)
runtime/                     the new .NET 10 runtime (nothing in it is edited source; it is new code)
  src/
    TransactHub.Model/            ISO 20022 internal model, PaymentContext, money and party types
    TransactHub.Store/            the generalized store: EF Core, SQL Server and PostgreSQL providers
    TransactHub.Releases/         loads studio packages, checks their hashes, keeps the insert-only release registry
    TransactHub.Interpreter/      one interpreter of package format 0.2 (from studio/engines/shared)
    TransactHub.Engine.Temporal/  the adaptors, one per engine, behind IEngine
    TransactHub.Engine.Dtfx/
    TransactHub.Engine.Elsa/
    TransactHub.Engine.Flowable/  a Flowable 7 sidecar (Java) worked by .NET external workers
    TransactHub.Activities.Platform/   p.*: intake, decrypt, duplicate, approval, charges, funds, posting, reverse, reconcile, notify
    TransactHub.Activities.Uae/        uae.p.*: IBAN and routing code, AED, UAE calendar, CB SFTP
    TransactHub.Activities.Wps/        uae.wps.*: SIF/CBX, PIF/PSI, PRC, DIF, RFA, RRI, DE, RTC
    TransactHub.Ports/                 core banking, screening, SFTP, notification: interfaces only
    TransactHub.Ports.<Adapter>/       adapters, chosen per bank by a port binding (never by a bank fork)
    TransactHub.FrontDoor/             one intake API; the file watchers call it
    TransactHub.Host/                  worker and API hosts, one engine per installation
  temporal-mssql/             the Temporal SQL Server persistence plugin (Go; decision D2)
  tests/                      unit, contract, conformance (4 engines x 2 databases), parity, replay
content-configurations/      what the studio publishes and what a bank configures
  releases/<scope>/<release>/      released packages, written once, never changed
  banks/<bank>/                    configuration sets: rule values, hook bindings, port bindings
  environments/<env>/              which releases are active, from when (activation rows)
```

## 3. Workstreams

### W1. ISO 20022 internal model and workflow context (N* items 1 and 2)
- `TransactHub.Model` covers the parts of pacs.008, pain.001, pacs.002, pacs.004 and camt.054 that payments use:
  - parties with structured postal address;
  - agents (BIC, LEI, clearing system member id);
  - instructed and interbank settlement amounts;
  - charge bearer;
  - payment identification (instruction id, end-to-end id, UETR, transaction id);
  - purpose, remittance and supplementary data.
- A bulk is a pain.001 payment information block with its transactions: a WPS salary file is one bulk, and each EDR line is one transaction.
- Scheme fields with no ISO element go into `SupplementaryData`, typed per scheme and declared in the scheme's message schema. Examples: the MOL establishment id, the EVP fixed and variable pay, the salary month. They never become columns.
- The workflow context holds only `PaymentContext`:
  - the payment or bulk id;
  - the release id and the as-of date;
  - the scheme and leg;
  - the facts the flow branches on, as declared by the package (e.g. `validationErrorCount`, `ackType`).
- Activities read and write the payment through the store by id. Engine history then holds no personal data. A test scans each engine's history for any value from the party records.

### W2. One generalized store (N* items 3 and 4)
- **Tables:** `payment`, `payment_bulk`, `party`, `account`, `agent`, `payment_fact` (append-only, hash-chained per payment), `message` (raw files and messages in and out, with a content hash), `message_link`, `exception_item`, `release`, `release_activation`, `rule_version`, `list_version`, `config_value_version`, and `outbox`.
- Scheme and bank are columns. There are no per-scheme or per-bank schemas.
- **Current status** is a view or projection over `payment_fact`. Nothing is updated in place, so the `usp_UpsertCycleStates` MERGEs, the hot-cache upserts and the status columns go.
- **Database independent, through an ORM.** EF Core 10 is the only way the runtime reaches its data.
  - The same model, the same migrations source and the same queries run on SQL Server and PostgreSQL. The provider is chosen by configuration (`Store:Provider = SqlServer | PostgreSql`).
  - Each provider has its own generated migrations assembly (`TransactHub.Store.Migrations.SqlServer`, `…PostgreSql`), both made from one model.
  - The runtime has no raw SQL, `SqlConnection`, stored procedures, table-valued parameters, MERGE, lock hints, `OUTPUT INSERTED`, memory-optimised tables or change data capture.
  - Types are portable: `decimal(19,4)` for money, `DateTimeOffset` in UTC, string ids, and JSON columns through EF's JSON mapping for `SupplementaryData`.
  - Concurrency: the facts are insert-only, so there is no row locking to port. Ordering comes from a per-payment sequence number with a unique index, and a duplicate insert is a clean conflict on both databases.
  - Bulk insert goes through EF Core batching (or EFCore.BulkExtensions, which supports both) behind one interface.
  - Identity and time come from the application, not from `SYSUTCDATETIME`.
  - **A build check fails the runtime** if it finds `Microsoft.Data.SqlClient`, `Npgsql` or `FromSqlRaw` outside the two provider registration files.
- **Every store test runs twice**, once on SQL Server 2022 and once on PostgreSQL 16, in Testcontainers. CI runs the whole runtime suite (unit, store, conformance, WPS parity) against both. A test that passes on one database and not the other is a failed build.
- **The report database** is fed from the `outbox`, not from change data capture.
- **Mapping the old tables:** every one of the 64 `wps.*` tables, plus `transaction_headers`, `cycle_states` and `xbdr.Transactions`, is mapped to the new tables in a mapping table kept in `runtime/docs/data-mapping.md`. A migration tool copies the old data across for a cut-over.

### W3. Four engines behind one seam (N* item 5; the review's item 16)
- We start from what the studio has already proved. The conformance kit (48 scenarios since 2026-11-22; 46 when this was written) passes on Temporal, DTFx, Flowable and Elsa through `studio/engines/*`, with one interpreter (`studio/engines/shared/Interpreter.cs`) and one `IEngine` seam.
- The runtime turns that into a product:
  - **Temporal:** the .NET SDK, with worker versioning by Build ID. Each release's activity versions run on their own versioned workers.
  - **DTFx:** `Microsoft.DurableTask.SqlServer`. Task-queue routing is done by the host (one hub per release line). The source's "`IsPatched` is always true" is not carried over: versions come from releases, not patches.
  - **Elsa 3.x:** journal replay inside one Elsa activity, with bookmarks, as the studio's harness does, now with Elsa's SQL Server and PostgreSQL persistence.
  - **Flowable 7:** BPMN generated from the expanded workflow (`src/export/bpmn.ts`). It runs in a Java sidecar with its own database. .NET external workers take the jobs over REST.
- **The conformance kit runs in CI** against 4 engines × 2 databases, using Testcontainers for SQL Server, PostgreSQL, Temporal and Flowable. Each runtime release must pass all 48 scenarios plus the WPS parity suite on each engine.
- The runtime's own data is always through EF Core (W2). Each engine also keeps its own state:

| Engine | Its own state on SQL Server | Its own state on PostgreSQL |
| --- | --- | --- |
| Temporal | the new plugin (W5) | native |
| DTFx | native (`DurableTask.SqlServer`) | **none today**: there is no supported PostgreSQL provider for DTFx. A DTFx installation needs SQL Server for the engine, while its payments data can still be on PostgreSQL. Building a provider is optional work, D7. |
| Elsa 3 | native (EF Core) | native (EF Core) |
| Flowable 7 | native | native |

  - So "every engine on both databases" holds for the payments data in every cell. For the engine's own state, it holds in every cell but DTFx on PostgreSQL.
- **N\* says "one runtime only".** The review recommends amending it, and so do we (D1): the engine is chosen per installation, each installation runs one, and all four are proved equal by the kit.

### W4. How the studio deploys to the runtime (N* items 6 and 7)
- **Package:** `studio export` writes a signed package.
  - Its manifest lists every workflow@release, activity@release (with the checksum of its code or script), rule@release (with its effective dates), list, configuration set and binding target.
  - It goes to `content-configurations/releases/<scope>/<release>/`.
- **Insert, never update:**
  - `POST /releases` stores a package once.
  - The same id with a different hash is refused.
  - There is no PUT and no DELETE. An old release is retired by an activation row, never by editing it.
- **Activation** is a `release_activation` row (environment, scope, release, effective from), also insert-only.
  - At acceptance, a new payment takes the release active at that moment and records the release id in its context.
  - A payment already running finishes on its own release.
  - Rollback is a new activation row pointing to an earlier release.
- **Hot update, by kind:**
  - **Workflows:** a new release, activated. New payments take it and running ones keep theirs. Engines run several definitions side by side.
  - **Activities:** code is versioned as name@version with a checksum.
    - Workers load each version in its own `AssemblyLoadContext` and keep old ones while any payment pinned to them is open.
    - On Temporal, a release's activities run on a versioned task queue or Build ID.
    - A payment never runs code its release did not name. This closes the review's item 11.
  - **Rules, lists and configuration values:** effective-dated version rows, also insert-only.
    - Each rule says whether it is evaluated as of the payment's acceptance or as of the step.
    - The runtime fixes the source's bug N1 by filtering on date first, then taking the version.
    - They change with no deployment, through maker-checker in the studio. The runtime only reads published versions.
- **Signed packages and who may call (built 2026-11-22, owner's decision).**
  - Every package the studio releases is signed: `release.json` (every file's SHA-256, the release id, tenant and change set) and `release.sig` (ECDSA P-256), with the studio's key read by reference (`TH_SIGNING_KEY`). The runtime verifies it at `POST /releases`, `POST /import-branch` and again at activation, against `TH_TRUSTED_SIGNERS` (several keys, retired and revoked), and records each verification as a `release.verified` fact on the release. The deploy runner runs only a verified package. Spec R-09-23 to R-09-26.
  - The runtime is the bank's: it takes bearer JWTs from the bank's identity provider only, people's and machine callers' client-credentials tokens alike, maps the bank's claims and groups to its roles by the bank's role map, and keeps no identities (`TransactHub.Auth`, R-09-27, R-09-29). The studio's only link to it is the signature.
  - GitHub is the artefact repository (owner, 2026-11-22). Each published release is its signed folder on the content branch and an annotated tag `release/<workflow>/<release>/<target>`. A runtime takes a version through a package source (`IPackageSource`, `GitHubPackageSource`: list the tags, download the folder at its commit, read-only, its token by reference), only through the same verification (`GET /sources/versions`, `POST /sources/install`). Spec R-09-30.
  - Secrets are named by reference (`env:`, `file:`, `vault:`) and read through one interface (`TransactHub.Security`, R-06-22); check L37 refuses a literal one (R-06-23); CI scans for secrets and audits dependencies (R-09-28).
- **Running payments** can change only through declared operator actions (retry, repair, cancel, decide), each with a permission and a second approver where the package says so. `InjectState` and arbitrary key/value edits are not carried over. This closes the review's item 13.

### W5. Temporal on SQL Server (N* item 8)
- Temporal ships persistence for PostgreSQL, MySQL, SQLite and Cassandra, but not SQL Server.
- Recommendation (D2):
  - Write a SQL Server `sqlplugin` for the Temporal server in Go (`runtime/temporal-mssql/`), with the schema and its migrations.
  - Prove it with Temporal's own persistence test suite, then the conformance kit.
  - Pin it to one Temporal server version and re-prove it on each upgrade.
- This is a fork-like maintenance burden that Temporal will not support. The alternative is to keep Temporal on PostgreSQL and SQL Server for everything else.
- The runtime's own data is on SQL Server or PostgreSQL either way (W2).
- **Spike done (P4 sizing).** Results are in `runtime/tools/temporal-sqlserver/README.md`, measured against Temporal v1.32.2 and SQL Server 2022:
  - The plugin registers from our own module, so no fork is needed.
  - It is about 4,500 lines and 157 store methods.
  - The persistence schema fits, using a UTF-8 binary collation. One table, `chasm_node_maps`, needs a hashed key, and that fix was proved.
  - Visibility does not translate and should stay on PostgreSQL or Elasticsearch for now.
- **Built (P4).** D2 is accepted. The plugin is `runtime/temporal-mssql/` (README there), pinned to Temporal v1.32.2. Measured on 21 November 2026 against SQL Server 2022:
  - Temporal's own persistence suites, wired as for PostgreSQL: 45 of 45 suites pass (479 test cases, none fail; the 2 skips are Temporal's Cassandra-only tests).
  - Visibility is on SQL Server too: a computed column per search attribute, and a trigger-kept side table for keyword lists. It passes Temporal's visibility suites.
  - The server runs on SQL Server. A workflow whose timer was running when the server was killed (SIGKILL) completes after the restart.
  - The conformance kit on servers on SQL Server, in real time: 43 of 46 pass, none fails on its trace. The other 3 wait four business hours to three days, which a real server waits in real time (I-48). CK-FOREACH-02, the 10,000-item batch, passes in about three minutes.
  - The WPS salary file settles on Temporal on SQL Server with the runtime's payments store on SQL Server too (`e2e-wps-temporal.test.ts`, "database on SqlServer", with `TH_TEMPORAL_ADDRESS`).
  - CI job `temporal-sqlserver` runs all of it.
- **P4 status: met, with one gap.** The plugin passes Temporal's persistence suites and runs the kit and the WPS salary file. The kit's scenarios that wait hours or days are proved on the time-skipping server, not on SQL Server, until I-48 is decided. CI runs the fast subset of 40.
- **Left open.** Who keeps the plugin on each Temporal release (I-42); ids are bytes, not characters, on SQL Server (I-45); Text search attributes are not indexed (I-46); what a bank's SQL Server must allow (I-47: the server now runs, and passes the durability check and the persistence suites, as a login with only read, write and execute on its two databases); the kit's long business waits on a real server (I-48).

### W6. WPS as a leg; the bank mined into rules, hooks and configuration (N* items 10 and 11)
- **Splitting the 112 activities** (the full list is in `SURVEY.md` section 3):
  - into `p.*`: intake, decrypt, duplicate, approval, payment queue, charges, balance and funds, posting, reverse, status, reconcile, notify, posting windows, exchange rate;
  - into `uae.p.*`: IBAN and routing codes, AED, the UAE calendar, the CB SFTP transport;
  - into `uae.wps.*`: SIF/CBX parse and generation, the MOL corporate match, ACK/NAK, PIF/PSI, PRC, DIF, RFA, RRI, DE bulk, RTC.
- **The WPS leg:** only the scheme's own steps remain in the leg, roughly 5 of today's 36 outward steps. The rest is the common lifecycle.
- **C# rules become declarative.** About 40 rule classes become studio rules and tables:
  - SIF upload, CBX filename, record and SCR rules, and the SSM structure checks;
  - DE, DIF, RFA, PRC and RRI rules;
  - the approval and posting classifiers.
  - Their error codes stay (00803, 00829 and the rest), so channel messages do not change.
- **The DIB fork becomes configuration of the example bank:**
  - **Core banking:** mode, variant, ServiceCode, ExternalHostId, TxnFlag, UploadBranch, AuthStatus and paths become a port binding for the core-banking adapter.
  - **Accounting:** GL accounts, narration, transaction codes, VAT and charge slabs become bank rule values, with UAE defaults.
  - **Routing code:** one neutral parameter, `BankRoutingCode`, replaces `{tenant}RoutingCode`.
  - **CBX parameters:** bank values with UAE defaults.
  - **AML check, MOHRE notify and SFTP:** hooks or port bindings.
  - **Exchange-rate reply shape:** an adapter.
  - **Queues:** queue pins and default tenants go. A tenant is always given, never defaulted.
- **The four copies of one validation rule** (record count, total within balance, MOHRE status) become one `uae.wps` rule.
- **Progress (P5).**
  - **The check.** `BankConfigurationTests.No_shared_code_names_a_bank` fails the build if a bank is named in shared code: the runtime, the studio and the engine harnesses, in the shapes the source used (`Dib`, `DIB`, `ADIB`, `Enbd`, `FAB` and the full names). A second test proves the check finds them.
  - **The port.** Core banking is a port, `TransactHub.Ports.ICoreBanking`, with adapters `simulator` and `gateway-rest`. It is bound per tenant by effective-dated port bindings, in `content-configurations/banks/<bank>/*.yaml` (kind `port-bindings`). The 13 `CoreBanking:*` settings the bank fork read are settings of the binding; `core-banking.yaml` maps each one.
  - **The switch.** The example bank moves from the simulator to the gateway on 2026-12-01 by a binding alone, and the tests show it.
  - **The four bank sets.** Under D5 as first decided they were not in this repository. Each bank keeps its set in its own copy, and `example-bank` holds every knob with synthetic values.
  - **DIB's set (D5 as amended, 2026-11-21).** DIB's real configuration is in `content-configurations/banks/dib/`: its pack (hooks for the employer AML check and the labour ministry notification, carried but not bound), its configuration set (rails and routing code), its core-banking binding and its CBX values. Since 2026-11-22 it also carries its accounting codes, charge narration, account kinds, VAT and the 72-hour acknowledgement wait as factory parameters (R-12-12, R-05-47), and its Central Bank SFTP and salary-file decryption as bindings of `p.port.file-transfer.v1` and `p.port.decryption.v1`, secrets by reference. Its accounts and charge slabs are not in the source (I-58). Its `README.md` gives the source of each value and lists what is not yet carried. The studio loads it with `--bank dib` (Issue I-43); the runtime reads its bindings (`BankFolderConfigurationTests`).
  - **ADIB's, ENBD's and FAB's sets (D5 as amended, 2026-11-22).** Each is a bank folder set up as DIB's: `content-configurations/banks/adib/`, `enbd/` and `fab/`. None runs WPS in the source (ADIB is licensed for IPI and FTS only; ENBD and FAB have no deployment), so each configures the platform only: no rail of the factory's, a core-banking binding to the gateway with a placeholder host and the credential by reference that refuses every call until the bank's settings are added (the source has none, I-85), and the employer AML check and labour ministry notification of its own fork as hooks, built and tested and not bound. Whether they run WPS is open (I-86). Each `README.md` traces every value and lists the IPI, FTS and other flows with no factory scheme (D6). The studio loads each with `--bank <name>` (`studio/test/platform-bank-packs.test.ts`); the runtime reads their bindings (`PlatformOnlyBankFolderTests`).
  - **Posting through the port (P2).** WPS posting and balance on the runtime call `ICoreBanking` through the tenant's binding in force at the engine's time (`CoreBankingResolver`, `PortBindings.InForce`). See "Progress on P2" in section 6.
- **ADIB RTC** becomes a `uae.wps` RTC flow, with the ADIB posting as a hook.
- **ENBD and FAB** are thin stubs with no tests today. They become configuration sets only.
- **Real bank values** (routing code, GL accounts, host ids) are not committed, except DIB's: the owner decided on 2026-11-21 that DIB's live in `content-configurations/banks/dib/` (D5 as amended). Another bank's set still lives in its private copy of `content-configurations/banks/<bank>/`. Secrets are never committed, DIB's included: the folder names them by reference.

### W7. The studio: WPS only, structure
- **Remove Aani** from the studio's factory content, tests, guide, training and videos.
- **Keep WPS as a leg** of the platform lifecycle.
- **Add the leg's missing parts.** The studio adds the WPS flows the source has that the factory has not yet modelled: inward PIF/PSI, PRC, DIF, RFA, RRI, DE and RTC, plus posting windows as a schedule-gated await (review: repair policy on the block contract). They come in through the import function (I-33), from an extraction of the source.
- **Spec changes** go with the code that needs them, each with a requirement id and a check, as `spec/CLAUDE.md` says.

### W8. The studio connects to a GitHub branch and exports to it
- **Connect.** In the studio's settings, an owner connects a repository and a branch, for example the `content-configurations` repository on `main`, or a branch per environment.
  - The token comes from an environment variable (`TH_GITHUB_TOKEN`) or a GitHub App installation, never from the studio's documents or the browser.
  - The studio checks that it can read the branch and write to it, and shows the last commit it wrote.
- **Export after changes.** When a checker approves a change set and the release is published, the studio writes one commit to the branch. The commit contains:
  - the new release folder `releases/<scope>/<release>/` (the package, its manifest and hashes);
  - changed bank configuration sets under `banks/<bank>/`;
  - an activation row under `environments/<env>/`, if the change set asks for one.
  - The commit message names the change set, the maker, the checker and the requirement ids. The author is the studio's bot identity; the maker and checker are trailers.
- **Insert-only on the branch too.** The studio never rewrites a folder it has written. Exporting a release that already exists with a different hash is refused, as it is in the runtime. A rule or list change is a new effective-dated version file, not an edit.
- **Optional pull-request mode.** For banks that want their own review in GitHub, the studio pushes to a branch named after the change set and opens a pull request into the connected branch, instead of committing to it directly.
- **Conflicts.** The studio fetches before it writes. If the branch moved and the new commit touches a file the studio would write, the export stops and says which file. It never force-pushes.
- **The runtime side.** A pipeline on the connected branch runs the package checks and posts each new release to the runtime's `POST /releases` (W4). The branch is then the record of what was released and when, beside the runtime's own release table.
- **Tests:**
  - a local bare repository for the unit and black-box tests (connect, export, refuse a rewrite, conflict, pull-request mode);
  - one test against a real GitHub branch, run only when `TH_GITHUB_TOKEN` is set.

## 4. "No functionality is lost": how we prove it

1. **The baseline.** The source's own test suite as it runs today, per project (section 5). Every source test is kept and classified as passing, failing because of missing inputs, or failing for real.
2. **A parity matrix** in `runtime/docs/parity.md`. Each line maps a source item to where it went, and to a test that proves it:
   - each of the 112 activities;
   - about 40 rule classes;
   - 15 signals;
   - 29 definitions (once the missing files arrive);
   - the 64 WPS tables;
   - each endpoint.
   No line may be left empty. "Dropped, because…" needs an accepted reason, for example `InjectState`.
3. **Golden runs.** The same synthetic SIF, PIF, PRC, DIF, RFA and RRI files go through the source (on Temporal, from the docker compose) and the new runtime. The CB SIF generated, the core-banking requests, the posting legs, the channel responses and the error codes must match field by field, apart from ids and times.
4. **Scenario ports.** The WPS scenarios of the source's integration tests (about 1,900 WPS and inward tests) are rewritten as runtime tests and studio tests. They run on all four engines.
5. **Replay.** The source's recorded Temporal histories are kept as a regression input for the migration tool: what each payment did must be readable in the new store.

## 5. Baseline (the source, run as is, before any change)

The results, pass by pass and project by project, with each failure classified (missing input, this machine, finding), are in `docs/runtime/BASELINE.md`. In short:
- **Build:** 153 projects, no errors.
- **Restored inputs:** with `dsl.zip` restored and Temporal available, the source's WPS unit, workflow and engine-conformance tests pass. Engine conformance passes 223 of 226 on its own Temporal and DTFx lanes.
- **Still missing:** two recorded histories and the corporate-registration SQL scripts. The `.github/` docs arrived in `TransactHub-Package.zip` on 2026-11-22; their north-star versions are in `docs/north-star/` (inventory: `docs/north-star/INVENTORY.md`).
- **Findings:** the largest is in the security suite (N15). `PaymentHub.UnitTests.Wps` never builds (N16).

## 6. Phases and exit criteria

| Phase | What | Done when |
| --- | --- | --- |
| P0 | Inputs and baseline | The missing `dsl/` and `.github/` files are added. The baseline is recorded with every failure classified. The parity matrix is listed, with targets still open. |
| P1 | Model, store, releases, interpreter, Temporal | WPS outward (SIF upload to CB ACK and the NAK reversal) runs on the new runtime on Temporal, on SQL Server and PostgreSQL. Its golden runs match the source. Releases are insert-only and pinned. |
| P2 | WPS complete | Inward PIF/PSI, PRC, DIF, RFA, RRI, DE, RTC and posting windows. Every WPS line of the parity matrix is green. |
| P3 | DTFx, Elsa, Flowable | The kit (48) and the WPS parity suite pass on all four engines × two databases in CI. |
| P4 | Temporal on SQL Server | The plugin passes Temporal's persistence suite and the kit (if D2 is accepted). Met on 21 November 2026, see W5. |
| P5 | Banks | DIB, ADIB, ENBD and FAB are each a configuration set. No bank name is in shared code, and a check enforces it. Switching the example bank's core banking needs only a binding. |
| P1s | Studio export to GitHub (W8), alongside P1 | A published change set lands as one commit on a connected branch. A rewrite is refused. The pull-request mode and the conflict stop are tested on a local bare repository. |
| P6 | Studio | Aani is removed. The WPS leg and the new inward flows are authored and published. The studio deploys a release to the runtime and hot-updates a rule value with no deployment, shown end to end. |

### Progress on P2 (WPS complete)

- **Posting and balance through the core-banking port.** `runtime/src/TransactHub.Activities.Platform` answers every factory activity of `p.port.core-banking.v1` in the native host (`TransactHub.Native`, `TH_BANKS_DIR` names the banks' folders):
  - `p.reserve.funds.v1`: a balance enquiry on the debtor account; enough available in a postable status (`postableStatuses`, a setting of the binding) reserves it in the hub;
  - `p.post.accounting.v1`: an item or an inward payment credits the creditor from the settlement account; an outward payment debits the debtor to it, with the template's charge slab and VAT. The legs' codes, narration and accounts are the activity's parameters;
  - `p.post.reverse.v1`, `p.post.verify.v1`, `p.reserve.release.v1`;
  - `uae.wps.post.return.v1` (`TransactHub.Activities.Wps`): an accepted returns file moves the returned amount from suspense to settlement.
  - The adapter is the binding's: the simulator, or `gateway-rest` with its wire settings. A call that may have reached core banking is `unknown`, never retried blind (the source's `AccTxnReplyClassifier`). A tenant with no binding in force is refused (`PortNotBound`).
  - A test bench gives the port to the runtime with `simulator: runtime` (R-08-15; `studio deploy --bench p.port.core-banking.v1=runtime`).
  - Tests: `CoreBankingPostingTests` (the simulator before the example bank's switch, the gateway after it and for the bank folder's binding, through a recording handler), `studio/test/runtime-port.test.ts`, and end to end `e2e-wps-core-banking.test.ts`: the payment file's credits posted by the real native host on the simulator adapter before the switch, and sent to the gateway after it.
- **Posting windows.** The source's call-hours gate (`CbCallWindowGate`) is `PostingWindow`. A binding with `postingWindowStart` and `postingWindowEnd` (HH:mm, business-local) takes no posting outside them: the attempt fails with a retryable error and the engine tries again later. An invalid window fails closed. Not yet: a schedule-gated await in the factory's legs, so a payment waits for the window instead of retrying into it.
- **The inward flows, flow by flow.**

| Flow | On the runtime | Not yet |
| --- | --- | --- |
| PIF (payment file, bank as agent) | Each credit posts through the binding (`p.post.accounting.v1` at item level). | The PIF parser and its checks run as the studio's scripts (`uae.wps.map.pif-in.v1`), not native code. |
| PSI (supplementary file) | Nothing to post. | The pairing with the PIF is the leg's exchange in the studio. The source's `InwardPsiParser` is not ported. |
| PRC (receipt confirmation) | Nothing to post. | Generation, upload and the ACK/NAK extraction (`InwardPrc*`) are not ported; the factory renders the PRC as a script. |
| DIF (disbursement file) | Nothing to post. | Generation, upload and ACK/NAK (`InwardDif*`) are not ported. |
| RFA (returns file) | The accepted-returns posting (`uae.wps.post.return.v1`). | The returns decision (`uae.wps.table.returns.v1`) yields `returns` and `count` but no `amount`, so on the runtime the posting is refused until it does. The RFA reposting and the file itself are not ported. |
| RRI (refund request instruction) | Nothing. | No factory flow. The parser (`InwardRriParser`), the reject reasons (000, 002) and the unblock flow are not ported. |
| DE (bulk file, transfer, submit, outcome, repost, lost file) | Nothing. | No factory flow; none of `Inward/De*` is ported. |
| RTC (return to the employer's bank) | The leg's posting is `p.post.accounting.v1`, which the runtime answers (inward: settlement to the creditor). | The RTC map (`uae.wps.map.rtc-in.v1`) writes the items but no payment-level creditor account, so the runtime refuses the posting until the leg credits per item or the map names the account. The map is a script, not native code. |
| Posting windows | The gate, on every posting activity. | The schedule-gated await, above. |

  P2's exit ("every WPS line of the parity matrix is green") is not met.

### Progress on P6 (the studio deploys and hot-updates)

- **Deploy, then change a rule value with no deployment, end to end** (`studio/test/blackbox/e2e-p6-hot-update.test.ts`, on Temporal with the runtime's store on PostgreSQL):
  - the bank publishes its internal limit in the studio, and the commit lands on the connected branch;
  - the runtime's release API stores the corporate workflow's Temporal package once and activates it for test; the pipeline imports the branch, so the runtime holds the bank's values as effective-dated versions (`POST /import-branch`, `BankValues`);
  - a salary file runs from the package the runtime holds (`GET /releases/package`): its workers read the bank's values from the runtime at each attempt (`GET /values?tenant&at`, `TH_RUNTIME_URL`), and the file is held;
  - the bank raises the limit from a later validFrom; the pipeline stores a new value version, and no release or activation;
  - a second file on a later date runs from the same package, byte for byte, takes the new limit and is not held; a file on the first payment's date still takes the old limit and gives the first payment's trace.
- The values in force are those of the latest configuration set that sets the parameter, by date (`BankValuesTests`, on both databases). Lookups of dated values by an engine's own timers are I-57.
- **Not yet:** Aani's removal and the inward flows' authoring are tracked under P2 and W7; this item shows only the deploy and the hot update.

### Progress on P3 (DTFx, Elsa, Flowable)

- **Kit.** The kit runs in CI on all four engines (`.github/workflows/engines.yml`, job `kit`).
- **Store, on all four engines.** Temporal, DTFx, Elsa and Flowable run on the store, each on PostgreSQL and SQL Server. CI job `store` is that matrix of 4 engines × 2 databases. With `TH_STORE_PROVIDER` set, the deploy runner runs the runtime's build of the DTFx and Elsa harnesses (`runtime/tools/TransactHub.{Dtfx,Elsa}Harness`). That build is the same engine code, with `StoreJournal` installed on the claim check:
  - every create, staged write, commit, fork and copy is a fact on the payload's chain, written before memory changes;
  - a root's payments are accepted into the ISO 20022 tables;
  - the observation's `engine.store` shows each payload rebuilt from its facts and compared with what the engine held (`StoreReport`, the same for Temporal and Flowable).
- **Flowable on the store (D3).** Built on 22 November 2026. The Flowable REST server (`flowable/flowable-rest:7.1.0`, the Flowable of the kit harness) runs the BPMN the studio exports; `runtime/src/TransactHub.Engine.Flowable` is its external worker over REST, and keeps the payloads in `DurableClaimCheck`, as the Temporal binding does:
  - `FlowableRest` is the client: deploy through `/repository/deployments` (one zip), start through `/runtime/process-instances`, acquire and lock, complete, fail and BPMN errors through `/external-job-api`, run jobs and move timers through `/management`, deliver messages to executions;
  - `FlowableBinding` is the binding: every activity attempt goes to the worker of its task queue, and the binding's own tasks (topic `th.binding`) are where it writes the trace, names a slot's leg, counts a batch, checks a hook's contract and learns of each instance;
  - the export for `flowable` generates BPMN for this REST binding (`binding: 'rest'` in `src/export/bpmn.ts`), and the bound hooks with it; the kit's embedded harness keeps the embedded form and still passes 46 of 46;
  - the server's async executor is off and the binding keeps the clock, firing each timer when the virtual clock reaches it (Issue I-53);
  - the deploy runner runs a Flowable package with `runtime/tools/TransactHub.FlowableHarness` against the server at `TH_FLOWABLE_URL`, its payloads in the store when `TH_STORE_PROVIDER` is set; `e2e-wps-engines-store.test.ts` starts the server as a container and asserts the trace and `engine.store` on both databases.

  Found on the way: the generator's parallel join counted one token per exit of a branch, so the corporate workflow never joined on Flowable. Fixed (Issue I-56). P3's store matrix is met.
- **The WPS parity suite on DTFx and Elsa.** The scenarios of `e2e-wps-temporal.test.ts` run on DTFx and Elsa in `e2e-wps-parity.test.ts`: the salary file settles through four workers on four queues, and the scheme's rejection, a sanctions hit and a two-line file held and declined or approved each give the studio interpreter's trace and outcome. It runs on the studio's engine harnesses and, with `TH_STORE_PROVIDER`, on the runtime's builds with the store. The source's Central Bank SIF writer, imported and published through the studio, settles the salary file on DTFx and Elsa with the same SIF as on Temporal (`e2e-wps-source-slice.test.ts`, steps 6 and 7). CI job `wps-parity` runs all of it per engine.

### Monitoring and the load test (I-66, I-68; decided by the owner on 22 November 2026)

- **Monitoring (I-66).** OpenTelemetry in every runtime host and in the studio: traces, metrics and logs over OTLP, configured only by the standard `OTEL_*` variables, no backend named in shared code (R-09-31, R-10-07). One trace per payment from the front door's intake through every engine attempt, activity, port call and store write, on all four engines (R-09-32, R-09-33). The metrics of R-09-34, Temporal's SDK and server metrics among them. Personal data masked before any exporter, proved on the WPS salary file (R-09-35, R-10-08). `/health/live` and `/health/ready` (R-09-36, R-10-09). A local stack with dashboards (`runtime/observability/`, R-09-37). See `docs/north-star/guidelines/observability.md`.
- **Load test (I-68).** `runtime/load/` (R-09-38): synthetic WPS salary files through a deployment on Temporal, Temporal's persistence on our SQL Server plugin or on PostgreSQL, the store on SQL Server or on PostgreSQL. The first run, its conditions and the targets proposed for the owner are in `docs/runtime/LOAD-TEST.md`. It found I-77 (a file of more than a few dozen lines fails on a real Temporal server) and I-78 (a large file's lines run one after another). Both stop a bank going live; P2 on a real server is not met until they are fixed and the load matrix meets the targets the owner sets.

## 7. Issues found while planning (beyond the review's 21)

- **N1.** `RuleRepository.GetActiveEffectiveRulesAsync` takes the newest version, then filters by date. A future-dated rule therefore hides the rule in force. The date used is also "now", not a date fixed for the payment. This is a money risk (charges).
- **N2.** On DTFx, `IsPatched` always returns true, so a payment started before a patch runs the new branch after a restart. The two engines behave differently.
- **N3.** Personal data (email, account, CIF, IBAN) goes into workflow context and history from the SIF upload endpoints, and whole batch records go in as variables.
- **N4.** The generic runner hard-codes the step ids `aml_hit_hold` and `validation_non_stp`, and the workflow name `WF_DIB_WPS_Outward_Channel_IB`.
- **N5.** The default tenant is "DIB" in four option classes and three services, and the simulator reads DIB parameters directly. A misconfigured installation silently acts as DIB.
- **N6.** `platform.Transactions.OriginalPayload` is never written.
- **N7.** `RailSchemaResolver` falls back to `ipi` for an unknown scheme.
- **N8.** The DSL's `state_effect` is ignored, so compensation intent is not checked.
- **N9.** Definitions can be changed before Production and when deprecated, and Draft and Deprecated rows can be deleted.
- **N10.** The same validation is copied in four bank projects. ENBD and FAB are identical stubs with no tests, and ADIB's RTC has no tests.
- **N11.** The restore script needs SQL Server 2025, while the compose file runs 2022.
- **N12.** The DTFx conformance lane needs an external SQL Server. There are no Testcontainers, so it does not run in CI by default.
- **N13.** `WorkflowActivityUpdater` writes `_assembly_version` into step JSON, but nothing reads it, which gives a false sense that code is pinned.
- **N14.** The zip is incomplete (section 5).
- **N15.** Security: 25 tests in the source's own security suite fail, mostly in `IamAuditActivityLogAccessTests`. A user who holds only `bankingops:page.admin-activity-log:view` is answered 200 OK, not 403 Forbidden, when proposing role, screen and mapping changes. One permission key is also registered outside `usp_RegisterPermissionKey`.
- **N16.** `tests/PaymentHub.UnitTests.Wps` sets package versions that central package management forbids (NU1008), so it never builds. Its tests run only through `PaymentHub.UnitTests`, which links them.

## 8. Decisions

All recommendations were accepted.

**How the engines are deployed (D3).** Three of the four engines run as separate servers, with the workers connecting to them:

| Engine | Server | How workers connect |
| --- | --- | --- |
| Temporal | The Temporal service: frontend, history and matching, with its own database. | Workers poll task queues over gRPC. |
| Flowable | The Flowable REST application, a Java server, with its own database. | Workers fetch and complete external jobs over REST. |
| Elsa | Elsa Server, an ASP.NET host, with its own database. | The interpreter runs inside Elsa; activities are dispatched to the workers' task queues. |
| DTFx | None. DTFx is a library, and its "server" is its database. | Orchestration and activity workers are processes that share the SQL Server task hub. |

The decision table:


| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Amend N* "one runtime only" | Accept: the engine is chosen per installation, all four are proved equal by the kit. |
| D2 | Temporal on SQL Server | Accepted. The plugin is built, pinned to Temporal v1.32.2 and proved as P4 (W5). Each Temporal upgrade re-runs the proof. |
| D3 | Flowable hosting | Accepted. Flowable 7 runs as its own server with its own database. .NET external workers poll it for jobs over REST. |
| D4 | Where `spec/` lives | It stays at the root for now. Moving it under `studio/` is a separate change. |
| D5 | Real bank values | Not in this repository: synthetic placeholders here, the bank's set in its private configuration repository. **Amended 2026-11-21 by the owner, for DIB:** DIB's real configuration (rules, hooks, values and bindings, never secrets) lives here, in `content-configurations/banks/dib/`. Shared content (`spec/`, `runtime/src`, `studio/src`) still names no bank. Other banks are unchanged. |
| D7 | DTFx on PostgreSQL | Do not build a provider now. A DTFx installation keeps SQL Server for the engine's own state, and its payments data can be on either database. |
| D8 | Studio export to GitHub: direct commit or pull request | Direct commit to the connected branch by default (maker-checker already happened in the studio). Pull-request mode per connection for banks that want it. |
| D6 | Scope of the first drop | WPS only (P1 to P2) on Temporal, then the other engines. FTS, IPI, NPSS, CBFT, EDDA and XBDR come later, on the same store and lifecycle. |

## 9. Survey notes (where each finding is in the source)
See `docs/runtime/SURVEY.md`: the data model and databases, the engines and deployment, and the WPS and bank inventory, with file paths.

The source's engineering and AI docs (its `.github/` folder), rewritten for this plan, are in `docs/north-star/`: start at `docs/north-star/README.md`.
