# The operations console

Where operators see and act on running payments (R-05-48 to R-05-50, R-06-22). It is leg-generic: every payment shows its tenant, scheme, direction, ISO status, last outcome, hold, amounts, ids and file names, and each leg shows what its pack's operations view declares (`spec/factory/*/views/*.view.operations.v1.yaml`). There is no code for any scheme here; a new scheme gets its console by declaring its view. The design is `docs/north-star/design/operations-console.md`.

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
| `Operations:Roles` | The role mapping: `[{ "ClaimType": "groups", "Value": "<bank group>", "Tenant": "<tenant>", "Role": "Operator" }]`; by default the claim `th_roles` read as `<tenant>:<role>` |
| `TH_OIDC_AUTHORITY`, `TH_OIDC_AUDIENCE`, `TH_OIDC_CLIENT_ID`, `TH_OIDC_SCOPES` | The bank's identity provider: the API validates its bearer tokens; the app signs in with the authorisation code flow and PKCE as a public client |
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

## Not finished

- The store holds no end of a payment and no files of a deployment yet, so the duration and the documents of a deployed file are empty (I-97).
- A hold answered by the runtime waits in one attempt, at most `TH_OPS_HOLD_WAIT_SECONDS` (I-98).
- Signals are put on the outbox for the engine host, which does not read them yet; retry and resend are recorded and refused (I-99).
- Fields of the source's WPS screens with no source in the new model, and an unmask (I-100); reports, dashboards and exports (I-68).
- One projecting process per installation is assumed; a lease for several is not built.
