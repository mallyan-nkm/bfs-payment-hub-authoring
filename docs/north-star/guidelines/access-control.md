# Access control

Who may do what. It replaces the source's RBAC guidelines and agent, its IAM and RBAC designs, and its user-management screens.

There are two kinds of user, and they never mix:

- **Authors** work in the studio. They change content, never a running payment (R-10-01).
- **Operators** act on running payments in the runtime. Their identity is the bank's (below); their surface and permissions are [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it).

## Two owners, two identity providers (owner's decision, 2026-11-22)

| | The studio | The runtime |
| --- | --- | --- |
| Owned by | The vendor (NEST) | The bank |
| Identity provider | The studio's own | The bank's: any OIDC issuer (Entra ID, AD FS, Keycloak, Ping…) |
| Identities kept | Its users and roles in `governance/users.yaml` | None. No users, passwords or tokens of its own (R-09-29) |
| Machine callers | | Client-credentials tokens from the bank's provider: the pipeline, the workers reading values |
| The link | Signs every released package with its key | Trusts the studio's package-signing public keys that the bank configures (`TH_TRUSTED_SIGNERS`), and nothing else of the studio. It never trusts the studio's issuer |

The studio's sign-in is being built separately; the runtime does not depend on it.

The source had one identity service, a permission catalogue in five stores, rail gates and field masks for both. None of it is carried. Its own security suite fails on that catalogue (N15 in the plan).

## The studio: decided and built

| Rule | Source | Checked by |
| --- | --- | --- |
| Roles are enforced on every write. | C17 | Studio governance and API tests |
| The roles are: shared designer, shared checker, bank designer, bank checker, auditor, service operations. A user belongs to one tenant; `owner` is the tenant of shared content. | `studio/src/governance/governance.ts` | Tenant isolation suite |
| A tenant sees and changes only its own content and the shared content it may read. | C17 | Tenant isolation suite |
| The checker of a change set is never the maker of any member. | R-11-19 | Publish tests |
| Users and roles are `governance/users.yaml` on the content branch, changed only through maker-checker. | I-44 | `content.test.ts` |
| Every event is audited, hash-chained, on the branch. A refusal is audited too. | C17; `content-configurations/README.md` | Audit tests |
| The GitHub token is read from the environment, never stored, shown, logged or audited. | `studio/README.md` | Review; `no-disk-write.test.ts` |

The studio has no sign-in yet: the acting user is chosen in the web app's header and sent with each request. A shared installation needs a verified identity first ([I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it)).

What the source's user-management screens did, and where it went:

| Source screen | North star |
| --- | --- |
| Approval queue for admin changes | Maker-checker on change sets in the studio |
| Admin activity log | The studio's audit on the branch |
| GL accounts | Bank rule values in the bank's configuration (plan W6) |
| Rails | A rail is enabled in the bank's configuration set; the route decision passes only an enabled rail |
| System configuration | Configuration sets and effective-dated values |
| Users, roles, screens, departments, profile switching, unlocking | Studio users in `governance/users.yaml`; for operators, I-65 |

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

Do not build role screens or a permission store in the runtime: roles come from the bank's provider through the role map. The native host (`TransactHub.Native`) has no authentication yet ([I-75](../../ISSUES.md#i-75-the-native-host-has-no-authentication)).
