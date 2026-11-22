# Part 6. Tenants, hooks and configuration

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 6.1 Purpose

The studio is one multi-tenant application. Every bank is a tenant that shares platform and scheme content read-only, sets configuration values, and authors hooks. A bank can never change a shared definition. This part defines the three layers and the rules between them.

## 6.2 Layers

| Layer | Authored by | A bank may | A bank may not |
| --- | --- | --- | --- |
| Shared content: `p`, `<cluster>.p`, `<cluster>.<scheme>` | Product and cluster teams, in the owner tenant | Read it, test against it, accept an approved release | Edit a definition, a rule's logic, a map or a leg |
| Configuration values | The bank | Set values that shared content exposes as parameters; bind hooks | Add a parameter; set a value outside its bounds |
| Hooks, in `b` | The bank | Build blocks, rules, simulators and short flows; bind them to declared hook points | Attach anywhere that is not a declared hook point |

## 6.3 Hook types

| Type | May | Allowed outcomes | On failure or timeout |
| --- | --- | --- | --- |
| `halt` | Hold or reject the payment | `continue`, `hold`, `reject` | As the hook point declares: fail-open continues, fail-closed holds |
| `transform` | Write only the paths the hook point allows | `done` | Fail-open leaves the context unchanged; fail-closed holds |
| `notify` | Nothing that affects the flow | `done` | Always ignored by the flow; recorded |

## 6.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-06-01 | A tenant MUST see shared content and its own content, and nothing of another tenant. | Isolation tests (C17) |
| R-06-02 | Shared content MUST be authored in the owner tenant and published as releases to every tenant. | Tenancy tests (C17) |
| R-06-03 | A bank tenant MUST NOT create or change an artefact outside scope `b`. | Store test (C17) |
| R-06-04 | Every change in every tenant MUST pass maker-checker. Maker and checker MUST differ. | Governance tests (C17) |
| R-06-05 | A hook point MUST declare name, type, contract and policy (timeout and failure mode). | Schema `workflow#hookPoints` (L00) |
| R-06-06 | A hook MUST name the hook point it implements and the workflow or segment that declares it, and MUST have the hook point's type. | Schema `hook` (L00); lint L13 |
| R-06-07 | A hook MUST read and write only what the hook point's contract allows. `facts` written by a hook are its own. | Lint L13; sandbox test |
| R-06-08 | A `halt` hook that returns `hold` runs the hold block the hook point names; `reject` ends the workflow as rejected with compensation. | Conformance CK-HOOK-01 |
| R-06-09 | A `notify` hook MUST NOT delay or change the flow. It runs after the step that reaches it has been recorded. | Conformance CK-HOOK-03 |
| R-06-10 | The shared owner sets the hook point's timeout and failure mode. A bank MAY tighten them and MUST NOT loosen them. | Lint L14 |
| R-06-11 | An unbound hook point does nothing and costs nothing. | Conformance CK-HOOK-04 |
| R-06-12 | A binding MUST name a hook point, a hook, an exact release and a valid-from date. | Schema `configuration-set` (L00); lint L14 |
| R-06-13 | An instance MUST take the bindings in force when it starts and keep them. | Conformance CK-PIN-02 |
| R-06-14 | A parameter value MUST lie inside the bounds its owner declared. | Lint L14 |
| R-06-15 | A tenant's values and bindings MUST form one versioned, effective-dated configuration set. Changing a value is not a new release of the rule. | Schema (L00) |
| R-06-16 | A configuration set travels on its own, not as a pack, and needs no release of shared content. | Export test (C18) |
| R-06-17 | When a shared workflow is released, the studio MUST rerun every tenant's hook tests against it and report per tenant before the tenant accepts the release. | Compatibility run test (C18) |
| R-06-18 | A tenant's export MUST hold the shared packs it accepted, unchanged; its bank pack; and its configuration set. | Export test (C18) |
| R-06-19 | Shared packs MUST be identical for every tenant on the same release. | Hash comparison in export test (C18) |
| R-06-20 | A hook that two tenants implement SHOULD be offered to the owner as a shared block. | Studio usage report (advisory) |
| R-06-21 | Bounds apply by type: `min` and `max` to a number or a duration, `allowed` and `pattern` (a regular expression) to a code or a string, and every bound to each item of a list, and to each field of an item that is an object. A value outside them is refused. | Lint L14 (`tools/validate.py`, studio check) |
| R-06-22 | A secret MUST be named by reference, never by value: `secretRef: env:NAME`, `file:/absolute/path` (a mounted secret file) or `vault:mount/path#field` (a HashiCorp Vault KV version 2 field). The host MUST resolve it through its one secret interface (`ISecretResolver` in `runtime/src/TransactHub.Security`, `resolveSecret` in `studio/src/secrets`) at the moment it is used. A value MUST NOT be written to the branch, the store, a package, a log line or an exception message; a reference that cannot be resolved fails the step and names the reference only. | `SecretsTests` and `VaultTests` (`runtime/tests/TransactHub.Security.Tests`, Vault in a container); `CoreBankingPostingTests.The_gateway_s_credential_travels_by_reference_only`; `studio/test/signing.test.ts` ("secrets are read by reference only") |
| R-06-23 | No binding, configuration set, artefact or export package MAY carry a literal secret. A package that carries one is not promotable and is not signed. | Lint L37 (`tools/validate.py`, studio check, the exporter); CI job `security` |
| R-06-24 | The runtime's operators MUST be identified by the bank's identity provider (OIDC: the authorisation code flow with PKCE for the console, a validated bearer token for its API), never by the studio's issuer and never by a name in the request. Their roles, Operator and Supervisor, MUST be per tenant and come from the bank's claims or groups through a configured mapping; the runtime keeps no identity store. An operator sees and acts on the payments of their tenants only. A dev sign-in MAY exist for synthetic users only, and MUST be refused when the host runs in production. | `OperatorAuthTests`; `OperatorApiTests` |
| R-06-25 | The runtime's service engineers MUST be identified as operators are (R-06-24), by the bank's identity provider through the role map, with the roles ServiceEngineer and ServiceApprover. The service console MUST refuse a caller with neither role, and a ServiceApprover MUST be another person than the engineer who asked for the action they approve. | `ServiceApiTests` |

## 6.5 Roles

| Role | Tenant | May |
| --- | --- | --- |
| Shared designer | Owner | Author shared content |
| Shared checker | Owner | Approve shared content and releases |
| Bank designer | Bank | Author hooks and configuration |
| Bank checker | Bank | Approve the bank's hooks, configuration and release acceptance |
| Auditor | Either | Read everything in its tenant; change nothing |

The roles above are the studio's, for authoring. The runtime's operators are not studio users and the studio never touches a running payment (R-10-01). They have two roles of their own, per tenant, from the bank's identity provider (R-06-24):

| Runtime role | May |
| --- | --- |
| Operator | See the tenant's payments in the operations console; take the actions an operations view offers with role Operator; ask for one that needs a second approver |
| Supervisor | All an operator may, and approve or reject an action another person asked for (maker-checker, R-05-50); rebuild the read model |
| ServiceEngineer | Use the service console (R-06-25, R-09-39 to R-09-44): health, content, backups, logs, the error dictionary, the assistant; ask for a troubleshooting action |
| ServiceApprover | All a service engineer may, and approve or reject a risky action another person asked for |

## 6.6 Examples

- Halt, transform and notify hooks: `examples/bank/hooks/`.
- Configuration set with a dated value and three bindings: `examples/bank/configuration-set.yaml`.
- Bank pack manifest with the tenant id: `examples/bank/pack.yaml`.
- Hook points declared by a segment: `factory/p/workflows/p.seg.check-and-screen.v1.yaml`.
