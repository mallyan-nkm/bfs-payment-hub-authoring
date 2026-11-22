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

## 6.5 Roles

| Role | Tenant | May |
| --- | --- | --- |
| Shared designer | Owner | Author shared content |
| Shared checker | Owner | Approve shared content and releases |
| Bank designer | Bank | Author hooks and configuration |
| Bank checker | Bank | Approve the bank's hooks, configuration and release acceptance |
| Auditor | Either | Read everything in its tenant; change nothing |

## 6.6 Examples

- Halt, transform and notify hooks: `examples/bank/hooks/`.
- Configuration set with a dated value and three bindings: `examples/bank/configuration-set.yaml`.
- Bank pack manifest with the tenant id: `examples/bank/pack.yaml`.
- Hook points declared by a segment: `factory/p/workflows/p.seg.check-and-screen.v1.yaml`.
