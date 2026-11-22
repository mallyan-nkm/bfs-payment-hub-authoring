# Deployment

What an installation of the north star is made of. It replaces the source's deployment architecture, infrastructure agent and compose skills. The source sized three tiers of its own services in containers; those figures are not carried. Sizes and targets for the north star are [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side).

## Two things are deployed, apart

| Part | Where it runs | Talks to |
| --- | --- | --- |
| The studio | One Node.js 22 server with its web app (`npm start`, port 4317 by default) | GitHub (the content branch), and the Claude API for the assistant |
| A runtime installation | The runtime's hosts, one engine, its servers and databases | The branch through its pipeline; outside parties through ports |

The studio is never deployed beside a runtime in a way that lets it reach a payment (R-10-01). Its only output is commits on the branch.

## The studio

- Set `TH_CONTENT_REPO`, `TH_CONTENT_BRANCH` and `TH_GITHUB_TOKEN`. Without `TH_CONTENT_REPO` it does not start. The token needs contents write on the repository, and pull requests write in pull-request mode.
- Optional: `TH_GITHUB_API` (a GitHub Enterprise base), `TH_CONTENT_ROOT`, `TH_BANKS`, `PORT`, `TH_SPEC_ROOT`, and `ANTHROPIC_API_KEY` for the assistant. Behind a proxy: `HTTPS_PROXY` (or `HTTP_PROXY`) and `NO_PROXY` for the GitHub client, and `NODE_EXTRA_CA_CERTS` for a proxy's or the bank's own certificate authority.
- It keeps no state of its own: stop it, start it elsewhere, and it reads the branch again.
- Before banks share one installation, the studio needs a sign-in ([I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it)) and scripts need an isolated worker (D14).
- Set `TH_SIGNING_KEY` to a reference to the studio's package-signing key (`npm run studio -- signing-key <dir>` makes one). Without it the studio publishes no workflow release.

Full list: `studio/README.md`, "Run it".

## A runtime installation

Each installation runs one engine (D1). How each engine is hosted is decision D3:

| Engine | Servers | Its own database | Workers |
| --- | --- | --- | --- |
| Temporal | Temporal service (frontend, history, matching) | PostgreSQL, or SQL Server through `runtime/temporal-mssql` (D2) | Poll task queues over gRPC |
| DTFx | None: a library | SQL Server task hub (D7: no PostgreSQL) | Orchestration and activity workers share the hub |
| Elsa 3 | Elsa Server | SQL Server or PostgreSQL | The interpreter runs inside Elsa; activities go to the workers' queues |
| Flowable 7 | Flowable REST server (Java) | SQL Server or PostgreSQL | .NET external workers over REST |

Beside the engine:

- **The store**: SQL Server 2022 or PostgreSQL 16, as tested. Migrations come from the provider's migrations assembly.
- **The release API** (`TransactHub.Api`): stores releases and activations, each verified against the studio's signature. It takes bearer JWTs from the bank's identity provider only, with roles from the bank's role map (`guidelines/access-control.md`). It needs `Auth:Oidc:Issuer`, `Auth:Oidc:Audience`, `Auth:RoleMap` and `TH_TRUSTED_SIGNERS`, or it does not start.
- **The native host** (`TransactHub.Native`): code-behind for native activities, on `TH_NATIVE_URLS`.
- **One worker per task queue**, the queue derived from the scope (R-09-05). A bank never has a worker of its own.

The front door host and the worker hosts of the plan (`TransactHub.FrontDoor`, `TransactHub.Host`) are target.

## Temporal on SQL Server

`runtime/temporal-mssql/README.md` is the guide. What the bank's SQL Server must allow is [I-47](../../ISSUES.md#i-47-what-a-banks-sql-server-must-allow-for-temporal): SQL Server 2019 or later (2022 tested), a UTF-8 binary collation and snapshot reads on Temporal's two databases, a schema user for setup and a service user with read and write only. Each Temporal upgrade re-runs the plugin's whole proof first ([I-42](../../ISSUES.md#i-42-who-keeps-the-sql-server-plugin-for-temporal-and-how-an-upgrade-is-proved)).

## Releasing without downtime

The source rolled services to change behaviour. Here behaviour changes by release and activation:

- A new workflow release is stored, then activated from a time. New payments take it; running ones finish on theirs. Engines run several releases side by side.
- A rule or configuration value changes by its effective date, with no deployment.
- Rollback is a new activation pointing at an earlier release.
- Code of the runtime itself (hosts, adapters, the interpreter) is still deployed. How workers keep old activity versions loaded while payments pinned to them are open is plan W4, and is target.

## Configuration and secrets

- An installation's settings come from its environment. A bank's values come from its folder on the branch.
- Secrets are set at deployment, never committed, and named by reference: `env:NAME`, `file:/path` (a Kubernetes or Docker secret), `vault:mount/path#field` (`TH_VAULT_ADDR`, `TH_VAULT_TOKEN`). See `guidelines/encryption-and-secrets.md`.
- The studio signs every released package with `TH_SIGNING_KEY` (a reference). Each runtime installation and each deploy runner trusts the studio's public keys the bank lists in `TH_TRUSTED_SIGNERS`, with retired and revoked keys; nothing unsigned is stored, activated or run.
- GitHub is the artefact repository: each published release is a signed folder on the content branch and an annotated tag `release/<workflow>/<release>/<target>`. An installation may take versions from it through its package source (`PackageSource:GitHub:*`, a read-only token by reference), verified like any upload.
- A worker that reads the runtime's values sends a client-credentials token from the bank's provider, by reference in `TH_RUNTIME_TOKEN`.

## Not decided

Sizes, availability and recovery targets, container images and orchestration, gateway and TLS termination, and telemetry ([I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio), [I-68](../../ISSUES.md#i-68-non-functional-targets-deployment-sizes-and-the-reporting-read-side)).
