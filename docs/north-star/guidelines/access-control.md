# Access control

Who may do what. It replaces the source's RBAC guidelines and agent, its IAM and RBAC designs, and its user-management screens.

There are two kinds of user, and they never mix:

- **Authors** work in the studio. They change content, never a running payment (R-10-01). They sign in to the studio's own identity provider.
- **Operators** act on running payments in the runtime, in the operations console. Their identity is the bank's (below); their roles, Operator and Supervisor, are per tenant (R-06-24, [the console's design](../design/operations-console.md)).

## Two owners, two identity providers (owner's decision, 2026-11-22)

| | The studio | The runtime |
| --- | --- | --- |
| Owned by | The vendor (NEST) | The bank |
| Identity provider | The studio's own, operated by NEST (R-11-32) | The bank's: any OIDC issuer (Entra ID, AD FS, Keycloak, Ping…) |
| Identities kept | Its users and roles in `governance/users.yaml` | None. No users, passwords or tokens of its own (R-09-29) |
| Machine callers | | Client-credentials tokens from the bank's provider: the pipeline, the workers reading values |
| The link | Signs every released package with its key | Trusts the studio's package-signing public keys that the bank configures (`TH_TRUSTED_SIGNERS`), and nothing else of the studio. It never trusts the studio's issuer |

The studio's sign-in is built (below); the runtime does not depend on it, and the studio accepts no runtime or bank token (R-11-34).

The source had one identity service, a permission catalogue in five stores, rail gates and field masks for both. None of it is carried. Its own security suite fails on that catalogue (N15 in the plan).

## The studio: decided and built

Decided by the owner on 22 November 2026 (I-65, the studio's part; R-11-29 to R-11-34, R-10-10 to R-10-12).

**Two logins, two trusts.** The studio is owned and operated by NEST, the platform vendor. Its sign-in is OpenID Connect against NEST's identity provider, configured for the studio alone (`TH_OIDC_ISSUER`, `TH_OIDC_CLIENT_ID`). Bank Configurators and Bank Approvers sign in with NEST-managed accounts in that provider; each account is tied to exactly one bank tenant in `governance/users.yaml`. Federation with a bank's own provider may come later: it is brokered by NEST's provider, so the studio still trusts one issuer. The studio never accepts a runtime or bank token and never shares a session with the runtime. The runtime's authentication is decided apart (I-65, the runtime's part).

| Rule | Source | Checked by |
| --- | --- | --- |
| Sign-in is OpenID Connect, authorization code with PKCE; the ID token's signature, issuer, audience, expiry and nonce are verified. | R-11-32 | `studio/test/auth.test.ts` |
| The session is an id in a signed, http-only, same-site cookie, in memory only; sign-out ends it. A restart signs everyone out. | R-11-32, R-11-24 | `studio/test/blackbox/auth.test.ts`; `no-disk-write.test.ts` |
| Every API call takes its user from the session, never from the request. No session is 401; an identity that is no user is 403. | R-11-32 | `studio/test/blackbox/auth.test.ts` |
| A state-changing call carries the session's CSRF token and comes from the studio's origin. | R-11-33 | `studio/test/blackbox/auth.test.ts` |
| No bearer token is accepted; a token for another client, the runtime's among them, is refused. | R-11-34 | `studio/test/auth.test.ts` |
| The development provider (`TH_AUTH_DEV=1`) is for local work and tests and is refused when `NODE_ENV=production`. | R-11-32 | `studio/test/auth.test.ts` |
| The roles are Bank Configurator and Bank Approver (one bank each), Workflow Designer, Workflow Developer, Platform Approver, Administrator and Service operations (owner), and Auditor. | R-11-29 | `studio/test/permissions.test.ts` |
| One permission table, from role, scope class, kind and action, decides every write, release and approval, the assistant's, the import's, waivers, settings and exports among them. A refusal is 403 and audited. | R-11-30 | `studio/test/permissions.test.ts`; `studio/test/blackbox/auth.test.ts` |
| A Bank Configurator works only in scope `b` of its own bank; it never changes a workflow, a scheme, a port or shared content, or another bank. | R-10-10 | `studio/test/blackbox/auth.test.ts` |
| A maker's saves need no approval. A change set becomes a version only when an approver for its area, who is not one of its makers, approves it. | R-11-31, R-06-04 | `studio/test/blackbox/auth.test.ts`; Publish tests |
| The web app shows each role only its surfaces and lands it on its own. It is never the control. | R-10-11 | `studio/test/blackbox/ui-login.test.ts` |
| A tenant sees and changes only its own content and the shared content it may read; a call that names another tenant is refused. | C17, R-06-01 | Tenant isolation suite; `studio/test/blackbox/auth.test.ts` |
| Users and roles are `governance/users.yaml` on the content branch, changed only by one administrator's proposal and another's approval. Each account is one identity in one tenant. | I-44, R-11-34 | `content.test.ts`; `permissions.test.ts` |
| The identity provider's groups may narrow a user's roles, never add one. | R-11-34 | `studio/test/auth.test.ts` |
| Every event is audited, hash-chained, on the branch. A refusal is audited too. | C17; `content-configurations/README.md` | Audit tests |
| The GitHub token and the OIDC client secret are read from the environment by reference, never stored, shown, logged or audited. | `studio/README.md` | Review; `no-disk-write.test.ts` |

The permission table:

| Role | Where | May |
| --- | --- | --- |
| Bank Configurator | scope `b` of its bank | author hooks, activities, scripts, rules, rule sets, tables, the configuration set (dials and hook bindings), port bindings, simulators, tests, samples; run the bench, import the bank part, export, deploy, the assistant |
| Bank Approver | scope `b` of its bank | approve the same kinds as versions; accept a shared release; run the bench, export, deploy |
| Workflow Designer | shared scopes | author workflows (subflows among them), schemes, tests, samples, supersessions; run the bench, import, export, deploy, the assistant |
| Workflow Developer | shared scopes | author activities, scripts, engine bindings, ports, rules, rule sets, tables, simulators, extensions, tests, samples, supersessions; run as the designer |
| Platform Approver | shared scopes, the studio | approve every shared kind and supersessions; waive; retire; the publishing settings; run the bench, export, deploy |
| Administrator | the studio | propose and approve changes to the users and roles |
| Service operations | the studio | retire, in-flight counts, the publishing settings |
| Operations | shared scopes | author the operations console's views of each scheme's legs (operations views); run the assistant |
| Service Engineer | shared scopes | author the error dictionaries and their runbooks; run the assistant. The same people use the runtime's service console, signed in there through the bank's identity provider |
| Auditor | its tenant | read |

What the source's user-management screens did, and where it went:

| Source screen | North star |
| --- | --- |
| Approval queue for admin changes | Maker-checker on change sets in the studio |
| Admin activity log | The studio's audit on the branch |
| GL accounts | Bank rule values in the bank's configuration (plan W6) |
| Rails | A rail is enabled in the bank's configuration set; the route decision passes only an enabled rail |
| System configuration | Configuration sets and effective-dated values |
| Users, roles, screens, departments, profile switching, unlocking | Studio users in `governance/users.yaml`, signed in with the studio's identity provider; for operators, I-65 |

## The runtime: decided and built

Authentication is one library, `runtime/src/TransactHub.Auth` (`RuntimeAuth.AddRuntimeAuth`), that every runtime host calls with the roles it knows. The release API uses it today; an operations console's API calls it with its own roles (operator, supervisor).

| Setting | What |
| --- | --- |
| `Auth:Oidc:Issuer`, `Auth:Oidc:Audience` | The bank's issuer and the runtime's audience. Exactly one issuer |
| `Auth:Oidc:Authority` or `Auth:Oidc:SigningKeys` | Discovery (default: the issuer), or a JWKS (or a reference to one) for an installation that does not fetch it |
| `Auth:Oidc:ClockSkewSeconds` | Default 60 |
| `Auth:Oidc:NameClaim` | The claim the store records as the actor; default `sub` |
| `Auth:RoleMap` | The bank's role map, JSON or a reference (`file:/etc/transacthub/role-map.json`) |

The role map grants roles (and tenants) to a claim value: an app role in `roles`, a group id in `groups`, a scope in `scp`. A role in the token under the runtime's own claim types is ignored: only the map gives roles.

```json
{ "kind": "runtime-role-map", "tenantsClaim": "tenants",
  "grants": [
    { "claim": "roles", "value": "TransactHub.Pipeline", "roles": ["release-importer", "release-activator", "release-reader", "values-reader"], "tenants": ["*"] },
    { "claim": "groups", "value": "<the bank's group id>", "roles": ["release-reader"] } ] }
```

| Role | May |
| --- | --- |
| `release-importer` | `POST /releases`, `POST /import-branch` |
| `release-activator` | `POST /activations`, and the activations a branch import carries |
| `release-reader` | `GET /releases`, `GET /releases/package`, `GET /activations/active` |
| `values-reader` | `GET /values` for the tenants it is given |

No token, or one the bank's provider did not issue (the studio's included), is 401; a caller without the role or the tenant is 403 (`ApiAuthTests`). The actor recorded on a release, an activation and each verification fact is the caller's verified name. The API does not start without the issuer, the audience, a role map naming only roles it knows, and trusted signers.

What still holds for operators:
- A running payment changes only through declared operator actions: retry, repair, cancel, decide (plan W4). There is no free edit of state.
- Authorisation is server-side. A user interface may hide what a user cannot do, but it is never the control. This was the source's rule and it holds.
- A tenant comes from a verified identity, never from a request header or body a client sets.
- Personal data is masked in any response to an operator unless the operator holds a permission to see it, and seeing it is audited. The source's masking catalogue is a good starting list; which permission unmasks what is part of I-65.

Decided on 2026-11-22 (I-65): the console and its API are built (`runtime/ops-console`, `runtime/src/TransactHub.Operations.Api`). Each operator action is offered only as a pack's operations view declares it, authorised server-side by tenant and role, and recorded on the payment's chain with the operator's identity; maker-checker where the view names a second approver. Masking is in the read model, as each view declares (R-05-49); an unmask is not built (I-100). Do not build a permission catalogue or role screens beyond the two roles: the bank's identity provider holds the users and groups.

Do not build role screens or a permission store in the runtime: roles come from the bank's provider through the role map. The native host (`TransactHub.Native`) has no authentication yet ([I-75](../../ISSUES.md#i-75-the-native-host-has-no-authentication)).
