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
| `Auth:Oidc:Issuer`, `Auth:Oidc:Audience`, `Auth:Oidc:Authority`, `Auth:Oidc:SigningKeys` | The bank's identity provider, through the runtime's authentication library (`runtime/src/TransactHub.Auth`, `AddRuntimeAuth`): the API validates its bearer tokens |
| `Auth:RoleMap` | The bank's role map (kind `runtime-role-map`, or a reference to it): its claims and groups to the roles `Operator` and `Supervisor` and the tenants |
| `Auth:Oidc:ClientId`, `Auth:Oidc:Scopes` | The console's public client: the app signs in with the authorisation code flow and PKCE |
| `TH_STUDIO_ISSUER` | The studio's issuer, which is refused as an operator's |
| `TH_AUTH_DEV=1` | Dev sign-in with synthetic users (`X-TH-Dev-Identity`); refused when the host runs in Production (the default environment) |

For a WPS file's holds to wait for an operator, give the operations-queue port to the runtime (`--bench p.port.operations-queue.v1=runtime`) and run the native host with the store (`TH_STORE_PROVIDER`, `TH_STORE_CONNECTION`; `TH_OPS_HOLD_WAIT_SECONDS`, default 240).

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
| `GET /ops/api/health` | The read model's lag and the views loaded |
| `POST /ops/api/rebuild` | A supervisor rebuilds the read model from the facts |

PUT, PATCH and DELETE are refused: a payment changes only through declared actions.

## The test bench

The same web app has a Test bench section (`src/testbench`, `#/testbench`) for testers, served by the test bench service (`runtime/tools/TransactHub.TestBench`, routes under `/testbench/api`; `testbench serve`). It needs the `Tester` role in the bank's role map. All three sections sign in once, as one public client, `transacthub-runtime-ui`: see `runtime/testbench/README.md`.

## The service console

The same web app has a Service section for service engineers (R-06-25, R-09-39 to R-09-44; design: `spec/design/09-export-bindings.md`, section 9.12), served by its own API host, `runtime/src/TransactHub.Service.Api` (routes under `/service/api`, the built app at `/`). The app finds which APIs answer (`/ops/api`, `/service/api`, or both behind one gateway) and shows their sections. Its pages: Health, Content, Logs, Errors and runbooks, Actions, Backup and the Assistant. Every action asks for a reason and is a fact; a risky one waits for a ServiceApprover; every error shown links to its runbook (`#/service/errors/<code>`).

```
cd runtime && dotnet run --project src/TransactHub.Service.Api -- \
  --Store:Provider=PostgreSql --Store:Connection="..." --TH_TRUSTED_SIGNERS=file:/etc/transacthub/trusted-signers.json \
  --PackageSource:GitHub:Repository=owner/repo --PackageSource:GitHub:TokenRef=env:TH_GITHUB_READ_TOKEN --urls http://127.0.0.1:7410
```

| Setting | What |
| --- | --- |
| `Service:Environment` | The environment it activates in (default `production`); `Releases:TestEnvironments` as the release API's |
| `Service:Errors`, `Service:Docs` | The published artefacts holding the error dictionaries (default `content-configurations/artefacts`), and the documents the assistant searches (default `docs/north-star`) |
| `Service:Probes:<n>:Name`, `:Group`, `:Kind`, `:Target`, `:Dashboard` | Cheap probes (`http`, `tcp`, `worker`, which reads the worker's version); `Service:Banks` adds a TCP probe per bound port's host; `Service:Certificates:<n>:Name` and `:Path`, `Service:Disks:<n>`, `Service:Dashboards:<name>`; `TH_FLOWABLE_URL`, `TH_TEMPORAL_ADDRESS` |
| `TH_BACKUP_SIGNING_KEY`, `Service:Backup:TrustedSigners`, `Service:RestoreDir` | The backup's signing key (a reference), whose backups it restores, and where a restore writes the bindings, the role map and the trusted signers |
| `Service:TrustWritable` | `true` lets `rotate-trusted-key` write the file `TH_TRUSTED_SIGNERS` names |
| `Service:Project` | `true` when this host is the installation's projecting process |
| `TH_ASSISTANT_ENABLED`, `TH_ANTHROPIC_API_KEY` | The assistant (off unless `true`), on `claude-opus-5-5` with the studio's key |
| `Auth:*`, `TH_AUTH_DEV=1` | As the operations console's: the bank's identity provider and role map (roles ServiceEngineer, ServiceApprover); dev sign-in outside production only |

`npm run test:e2e` also runs `test/e2e/service-console.test.ts`: a service engineer signs in, checks health, installs a signed version from a fake GitHub, sees a refused version's error with its runbook, and confirms an action.

## Not finished

- The store holds no end of a payment and no files of a deployment yet, so the duration and the documents of a deployed file are empty (I-97).
- A hold answered by the runtime waits in one attempt, at most `TH_OPS_HOLD_WAIT_SECONDS` (I-98).
- Signals are put on the outbox for the engine host, which does not read them yet; retry and resend are recorded and refused (I-99).
- Fields of the source's WPS screens with no source in the new model, and an unmask (I-100); reports, dashboards and exports (I-68).
- One projecting process per installation is assumed; a lease for several is not built.
- Service console: actions with no engine path (I-101); distributions and configuration releases are listed, not installed (I-102); errors classified by their words, logs of other hosts (I-103); the assistant's data handling is the bank's decision (I-104).
