# The operations console

Where operators see and act on running payments (R-05-48 to R-05-50, R-06-24). It is leg-generic: every payment shows its tenant, scheme, direction, ISO status, last outcome, hold, amounts, ids and file names, and each leg shows what its pack's operations view declares (`spec/factory/*/views/*.view.operations.v1.yaml`). There is no code for any scheme here; a new scheme gets its console by declaring its view. The design is `docs/north-star/design/operations-console.md`.

| Part | Where |
| --- | --- |
| The web app (React 18, Vite, plain CSS, as the studio's) | `runtime/ops-console/` |
| The shared shell: sign-in and sections, for the test bench too | `src/shell/` |
| The console's API, which serves the built app at `/` | `runtime/src/TransactHub.Operations.Api` |
| The read model, its projection, the views, the actions, the holds | `runtime/src/TransactHub.Operations` |

## Run it

```
cd runtime/ops-console && npm install && npm run build
cd runtime && dotnet run --project src/TransactHub.Operations.Api -- \
  --Store:Provider=PostgreSql --Store:Connection="Host=127.0.0.1;Database=th;Username=postgres;Password=..." \
  --urls http://127.0.0.1:7400
```

Then open http://127.0.0.1:7400. Configuration:

| Setting | What |
| --- | --- |
| `Store:Provider`, `Store:Connection` | The store (`SqlServer` or `PostgreSql`) |
| `Operations:Connection` | The read model's database; by default the store's |
| `Operations:Views` | The published artefacts that hold the operations views; by default `content-configurations/artefacts` |
| `Operations:Console` | The built app; by default `runtime/ops-console/dist` |
| `Operations:PollMilliseconds` | How often the projection reads the outbox (500) |
| `Operations:HoldSweepSeconds` | How often holds whose expiry passed are expired (30); a hold's expiry counts the bank's business calendar (R-09-88) |
| `Operations:FileApi:Url`, `:Folder`, `:TokenRef` | A resend goes through the File API's outbox onto the bank's binding (R-09-84); else `Operations:FileTransfer:Folder` (or `TH_FILE_TRANSFER_DIR`), a folder |
| (no setting) | The console's alerts go through the bank's binding of `p.port.notification.v1` in its configuration release in force (`simulator`, `log`, `webhook` or `mqtt`), else to its log, ids only (R-05-80, R-09-150) |
| `Operations:TatSweepSeconds` | How often turnaround breaches are looked for and alerted, each once (60, R-05-68); a limit counts business days of the bank's calendar (R-05-81) |
| `Operations:EventStream:PollMilliseconds` | How often the store's outbox goes out as payment events on the bank's binding of `p.port.event-stream.v1` (1000; with `TH_BANK` only, R-09-152, R-09-153). The stream's name, where a new one starts and its gap dials are the binding's settings `stream`, `from`, `gapSeconds` and `gapHorizonMinutes` (R-09-175, R-09-176) |

The pages beside the payments include the fraud alerts (`#/alerts`, R-05-90; a suspect payment's release waits for a supervisor's second approval, R-05-94), the bank's ledger accounts for supervisors, read-only (`#/ledger`, R-05-91), and, in the top bar, "rates stale" while a rate in force is older than the bank's `maxRateAge` (R-05-92). The service console's host runs the exchange-rate feed when `Service:FxRates:SigningKeyRef` names the feed's signing key by reference and the bank binds `p.port.fx-rates.v1` (R-09-177).
| (no bank folder) | The channel's core-banking enquiries use the core-banking binding of the bank's configuration release in force, read at each enquiry (R-09-100, R-09-112); the host does not start with `Operations:Banks` or `TH_BANKS_DIR` (R-09-113) |
| `Operations:Registers`, `Operations:Registers:SystemReadsMasked` | Further register kinds; `true` makes the register API answer core banking masked (R-09-101, I-181) |
| `Auth:Oidc:Issuer`, `Auth:Oidc:Audience`, `Auth:Oidc:Authority`, `Auth:Oidc:SigningKeys` | The bank's identity provider, through the runtime's authentication library (`runtime/src/TransactHub.Auth`, `AddRuntimeAuth`): the API validates its bearer tokens |
| `Auth:RoleMap` | The bank's role map (kind `runtime-role-map`, or a reference to it): its claims and groups to the roles `Operator` and `Supervisor` and the tenants |
| `Auth:Oidc:ClientId`, `Auth:Oidc:Scopes` | The console's public client: the app signs in with the authorisation code flow and PKCE |
| `TH_STUDIO_ISSUER` | The studio's issuer, which is refused as an operator's |
| `TH_AUTH_DEV=1` | Dev sign-in with synthetic users (`X-TH-Dev-Identity`); refused when the host runs in Production (the default environment) |

For a WPS file's holds to wait for an operator, give the operations-queue port to the runtime (`--bench p.port.operations-queue.v1=runtime`) and run the native host with the store (`TH_STORE_PROVIDER`, `TH_STORE_CONNECTION`): the hold answers held and the engine awaits the decision (R-05-56).

## Registers (R-05-62 to R-05-64)

The bank's customer data, starting with the corporate registration, lives in the runtime's registers, never in the studio or on GitHub. The **Registers** section renders each declared register kind (`runtime/src/TransactHub.Operations/Registers/*.register.json`; further kinds in `Operations:Registers`): the records in force, searched, each with its full history, personal and declared fields masked as the operations views mask. A **RegisterMaker** proposes a new record or a change (only the fields changed are sent); it takes effect only when a **BankApprover** who is not its maker consents, from the later of its `validFrom` and the consent. Both roles come from the bank's role map per tenant, like Operator and Supervisor (R-06-29); the dev sign-in has a synthetic maker and approver.

The same host serves the bank's systems, as system clients of its identity provider (client credentials), by roles the role map gives per tenant:

| Route | Role | What |
| --- | --- | --- |
| `GET /api/registers/{kind}/{key}`, `GET /api/registers/{kind}?cif=…` (any lookup field), `?q=` | `register-client` | Look up, list and search, unmasked (R-09-73) |
| `POST /api/registers/{kind}/changes`, `POST /api/registers/{kind}/sync` | `register-client` | Draft changes; load or sync in bulk, once per `reference`; every change waits for a BankApprover |
| `GET /api/channel/payments/{transactionId}/status`, `GET /api/channel/batches/{batchId}/status`, `GET /api/channel/payments?reference=&from=&to=&status=&scheme=` | `channel-enquiry` | A payment's or a file's status, and a search, in pain.002 words with the scheme's own status (R-09-74): the source's `/api/payment/{id}/status`, `/api/wps/inquiry/{id}`, `/api/batch/{id}/status` and SIF upload search |
| `POST /api/channel/accounts/balance`, `GET /api/channel/accounts`, `GET /api/channel/charges/preview` | `channel-enquiry` | Core banking asked through the bank's binding in force: a balance of an account the register holds for the corporate, the accounts core banking holds for its CIF, a charge preview (R-09-100). A corporate or account the register does not hold is 404 and nothing is sent; a binding with no path is 503 (I-184) |

## Development

```
npm run dev          # the app on http://localhost:5174, /ops/api proxied to TH_OPS_API (default http://localhost:7400)
npm run typecheck
npm test             # the app's own tests: PKCE, rendering from a declaration, no scheme named in the code
npm run test:e2e     # Playwright: a WPS file on the runtime's DTFx build, its hold approved in the browser (needs Docker and .NET 10)
```

## The API

| Route | What |
| --- | --- |
| `GET /ops/api/auth/config` | How to sign in (`dev` or `oidc`, with the identity provider's authority and client id) |
| `GET /ops/api/me` | The caller and their roles per tenant |
| `GET /ops/api/views` | The operations views, as the app renders them |
| `GET /ops/api/facets` | The values the list can be filtered by, for the caller's tenants |
| `GET /ops/api/payments` | The list: `tenant`, `scheme`, `kind`, `direction`, `status`, `outcome`, `hold=open`, `holdReason`, `leg`, `legOutcome`, `from`, `to`, `minAmount`, `maxAmount`, `q` (ids, references, file names), and declared filters `f:<view>:<subject or leg>:<field>` (with `:min`, `:max`, `:contains`) |
| `GET /ops/api/payments/{id}` | A payment or bulk: its fields, legs, lines, timeline, documents, holds, actions, and the actions offered now |
| `GET /ops/api/payments/{id}/documents/{doc}` | A kept file, to the payment's operators |
| `POST /ops/api/payments/{id}/actions` | `{ action, remarks }`: ask for an action; carried out at once unless the view names a second approver |
| `POST /ops/api/payments/{id}/actions/{action}/decision` | `{ approve, remarks }`: a second approver decides |
| `GET /ops/api/actions/pending` | Actions waiting for the caller as second approver |
| `POST /ops/api/payments/bulk-actions`, `POST /ops/api/actions/bulk-decision` | The same for a selection, item by item, each with its own rules and second approver (R-05-71) |
| `GET /ops/api/work/queue`, `/work/changes`, `/work/{kind}/{id}`; `POST /ops/api/work`, `/work/bulk` | The work queue: the payments waiting for a person and the actions waiting for a second approver; take, assign, escalate, release; a long poll for changes (R-05-71) |
| `GET`, `POST /ops/api/filters`, `POST /ops/api/filters/{id}/remove` | A person's saved filters |
| `POST /ops/api/unmask`, `GET /ops/api/unmask/{id}` | Ask, with a reason, to see one masked field of a payment or a line; shown to the asker only, once a bank approver consents, for the bank's `unmaskWindow` (R-05-69) |
| `GET /ops/api/tat/breaches` | The payments past their turnaround limit (R-05-68) |
| `GET /ops/api/retention/due`, `POST /ops/api/erasures`, `GET`, `POST /ops/api/legal-holds`, `POST /ops/api/legal-holds/{id}/release` | Retention and erasure: what is due, a supervisor's erasure on a bank approver's consent, legal holds (R-05-70, I-180) |
| `GET /ops/api/reports`, `/reports/summary`, `/reports/dashboard`, `/reports/export.csv` | Reports, the dashboard and CSV export from the read model and the views; each export an audit fact (R-05-59, R-05-65) |
| `GET /ops/api/reports/fixed`, `/reports/fixed/{view}/{id}.csv` | The fixed reports a view declares (WPS: SIF summary, SIF detail, upload history), CSV only (R-05-67, I-183) |
| `GET /ops/api/breaks`, `POST /ops/api/breaks/{id}/resolution`, `/breaks/{id}/consent` | The reconciliation break queue: an explanation at once, adopting the scheme's status on a bank approver's consent (R-09-92, I-168) |
| `GET /ops/api/system-actions/pending`, `POST /ops/api/system-actions/{id}/decision` | The changes of the system waiting for a bank approver: unmasks, erasures, a legal hold's release, a rebuild (R-09-77) |
| `GET /ops/api/audit` | The console's own chain: exports and system actions, for a supervisor or a bank approver; `rows` for the Audit page, newest first, filtered by `person`, `action`, `from`, `to`, each with its hash, and `chain`, whether it verifies (R-05-75) |
| `GET /ops/api/upload`, `POST /ops/api/upload` | The Upload page (R-05-73, R-05-74): the runtime's bank and the messages the distribution active in `Operations:Environment` receives; a person's file (a form: tenant, message, file) posted to the File API's `POST /files/ui` (`Operations:FileApi:Url`) with the person's own token, answered kept, refused or started, a refusal's code linked to its runbook (`Operations:ServiceConsole`) |
| `GET /ops/api/health` | The read model's lag and the views loaded |
| `POST /ops/api/rebuild` | A supervisor asks for the read model to be rebuilt from the facts; a bank approver consents (R-09-77) |

PUT, PATCH and DELETE are refused: a payment changes only through declared actions.

## The test bench

The same web app has a Test bench section (`src/testbench`, `#/testbench`) for testers, served by the test bench service (`runtime/tools/TransactHub.TestBench`, routes under `/testbench/api`; `testbench serve`). It needs the `Tester` role in the bank's role map. All three sections sign in once, as one public client, `transacthub-runtime-ui`: see `runtime/testbench/README.md`.

## The service console

The same web app has a Service section for service engineers (R-06-25, R-09-39 to R-09-44; design: `spec/design/09-export-bindings.md`, section 9.12), served by its own API host, `runtime/src/TransactHub.Service.Api` (routes under `/service/api`, the built app at `/`). The app finds which APIs answer (`/ops/api`, `/service/api`, or both behind one gateway) and shows their sections. Its pages: Health, Content, Logs, Errors and runbooks, Actions, Backup and the Assistant. Every action asks for a reason and is a fact; one that changes the system (an install, activation, drain, removal, rollback, restore, and the troubleshooting actions that change what runs) waits for a BankApprover, another person (R-09-77; the earlier name `ServiceApprover` gives nothing, R-09-115); a configuration release the release API installed outside its test environments waits there as `put-in-force` (R-09-111); every error shown links to its runbook (`#/service/errors/<code>`).

```
cd runtime && dotnet run --project src/TransactHub.Service.Api -- \
  --Store:Provider=PostgreSql --Store:Connection="..." --TH_TRUSTED_SIGNERS=file:/etc/transacthub/trusted-signers.json \
  --PackageSource:GitHub:Repository=owner/repo --PackageSource:GitHub:TokenRef=env:TH_GITHUB_READ_TOKEN --urls http://127.0.0.1:7410
```

| Setting | What |
| --- | --- |
| `Service:Environment` | The environment it activates in (default `production`); `Releases:TestEnvironments` as the release API's |
| `Service:Errors`, `Service:Docs` | The published artefacts holding the error dictionaries (default `content-configurations/artefacts`), and the documents the assistant searches (default `docs/north-star`) |
| `Service:Probes:<n>:Name`, `:Group`, `:Kind`, `:Target`, `:Dashboard` | Cheap probes (`http`, `tcp`, `worker`, which reads the worker's version); the bank's configuration release in force adds a TCP probe per bound port's host (R-09-112); `Service:Certificates:<n>:Name` and `:Path`, `Service:Disks:<n>`, `Service:Dashboards:<name>`; `TH_FLOWABLE_URL`, `TH_TEMPORAL_ADDRESS` |
| `TH_BACKUP_SIGNING_KEY`, `Service:Backup:TrustedSigners`, `Service:RestoreDir` | The backup's signing key (a reference), whose backups it restores, and where a restore writes the bindings, the role map and the trusted signers |
| `Service:TrustWritable` | `true` lets `rotate-trusted-key` write the file `TH_TRUSTED_SIGNERS` names |
| `Service:Project` | `true` when this host is the installation's projecting process |
| `TH_ASSISTANT_ENABLED`, `TH_ANTHROPIC_API_KEY` | The assistant (off unless `true`), on the Anthropic API with the studio's key; switch it on only once the bank's data protection officer approves (I-104) |
| `TH_BANK` | The one bank this runtime serves; the host does not start without it (R-09-76) |
| `Auth:*`, `TH_AUTH_DEV=1` | As the operations console's: the bank's identity provider and role map (roles ServiceEngineer, BankApprover); dev sign-in outside production only |

`npm run test:e2e` also runs `test/e2e/service-console.test.ts`: a service engineer signs in, checks health, installs a signed version from a fake GitHub, sees a refused version's error with its runbook, and confirms an action.

## Not finished

- One projecting process per installation is assumed; a lease for several is not built.
- The work queue: no named teams, nothing escalates by itself, and it reads every item's facts (I-185).
- Unmasking reads a payment's or a line's fields only, not a leg's or a register record's (I-181).
- An erasure masks and refuses; the store keeps the values (I-180).
- A break is resolved by an explanation or the scheme's status only (I-168); which messages are reports is the installation's setting (I-164).
- The console's alerts have no platform port yet (I-182); the fixed WPS reports lack the source's user names and ministry date (I-183).
- Service console: re-drive and reset reach a parked step only (I-125); the drain and restart of a worker under an orchestrator (I-128); the assistant's data handling is the bank's decision (I-104).
