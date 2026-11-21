# Part 1. Concepts, scopes, names and versions

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119. Every MUST names the check that enforces it.

## 1.1 Purpose

This part fixes the vocabulary every other part uses: what an artefact is, where it lives, what it is called, how it is versioned, and what a running payment is allowed to see change.

## 1.2 Terms

| Term | Meaning |
| --- | --- |
| Artefact | One versioned definition the studio stores: a rule, an activity, a simulator, a workflow, a scheme manifest, a hook, a port or a context extension. |
| Kind | The artefact's type. Seven kinds face the designer: data, rules, activities, simulators, workflows, schemes, hooks. |
| Scope | Where an artefact lives and who owns it: `p`, `<cluster>.p`, `<cluster>.<scheme>` or `b`. |
| Major line | The artefact's name, which ends in `.v<major>`. A step refers to a major line. |
| Release | One `major.minor.patch` of a major line. Immutable once published. |
| Pin | An exact release and content hash, recorded in the exported document. |
| Pack | The unit of release: all artefacts of one scope, with a manifest. |
| Tenant | A bank in the studio. The owner tenant holds shared content. |
| Lookup | Reading an effective-dated value at a step, by date. |
| Supersession | An approved replacement of one pinned release by a later one of the same major line. |
| Instance | One run of a platform workflow, a leg or a handler. |

## 1.3 Scopes

| Scope | Pattern | Owner | May depend on |
| --- | --- | --- | --- |
| Platform | `p` | Product factory | `p` only |
| Cluster platform | `<cluster>.p`, for example `uae.p` | Cluster team | `p`, own cluster |
| Cluster scheme | `<cluster>.<scheme>`, for example `uae.wps` | Scheme owner | `p`, own cluster platform, own scheme |
| Bank | `b` | The bank, as a tenant | Anything, through hook points and parameters only |

## 1.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-01-01 | An artefact name MUST match `<scope>.<type>.<name>.v<major>`. | Schema `common#artefactName` (L00) |
| R-01-02 | `<type>` MUST be one of the eleven activity types, or `rule`, `ruleset`, `table`, `sim`, `wf`, `seg`, `sub`, `handler`, `hook`, `port`, `ext`. | Schema (L00) |
| R-01-03 | A cluster code MUST be 2 to 8 lower-case letters and a scheme code 2 to 12 lower-case letters or digits. `p` and `b` are reserved. | Schema (L00) |
| R-01-04 | An artefact MUST NOT depend on a scope its own scope may not depend on (table 1.3). | Lint L02 |
| R-01-05 | Every reference MUST resolve to a published artefact. | Lint L03 |
| R-01-06 | Shared content MUST NOT contain a bank, tenant or vendor name in a name, title, description or party. | Lint L09 |
| R-01-07 | A tenant id MUST NOT appear in an artefact name. It lives in the pack manifest and the configuration set. | Schema `hook`, `pack` (L00); lint L09 |
| R-01-08 | A release MUST be `major.minor.patch`. Major changes only when the interface changes; minor when function improves; patch for a bug fix. | Lint L26 (semver-diff) |
| R-01-09 | The interface of an artefact is its reads, writes, outcomes, errors, ports and parameters. For a workflow it is also its slots, hook points, entry and exit. | Lint L26 |
| R-01-10 | The major in the name MUST equal the major of the release. | Lint L04 |
| R-01-11 | A published release MUST be immutable. The store rejects a second write to the same name and release. | Store test (C01) |
| R-01-12 | The content hash MUST be SHA-256 over the canonical JSON form of the document (RFC 8785). | Store test (C01) |
| R-01-13 | A workflow step MUST refer to an artefact by major line, never by release. | Schema `workflow` (L00) |
| R-01-14 | An exported workflow or hook MUST carry an `artefacts` block that pins the exact release and hash of every artefact it uses, directly or through a segment or rule set. | Lint L23 |
| R-01-15 | An instance MUST execute exactly the releases pinned by the workflow release it started on. | Conformance CK-PIN-01 |
| R-01-16 | An instance is pinned when it starts. A leg is pinned when the leg starts, not when the payment starts. | Conformance CK-PIN-02 |
| R-01-17 | Parameter values, lists, registries and reference data MUST NOT be pinned. They MUST be resolved by effective date at each step that reads them. | Conformance CK-LOOKUP-01 |
| R-01-18 | A new release of an artefact MUST NOT change any published workflow release. The studio reruns the tests of each workflow on that major line and, on green, issues a new workflow release with the new pin: a patch for a dependency patch, a minor for a dependency minor. | Studio test (C05) |
| R-01-19 | A supersession MUST name one artefact, the release it replaces and a later release of the same major line. | Schema `supersession` (L00); lint L27 |
| R-01-20 | A supersession MUST carry a green rerun of the test pack of every pinned workflow release in its scope, with the replacement swapped in. | Schema `supersession` (L00); studio test (C19) |
| R-01-21 | A supersession MUST be applied outside the studio, with a second approver. It applies only to steps not yet started. | Conformance CK-SUP-01 |
| R-01-22 | The payment record MUST show, per step, the pinned release, the release that ran and any supersession reference. | Conformance CK-SUP-01 |
| R-01-23 | A release MUST NOT be removed while any published workflow release pins it or any instance is pinned to it. | Store test (C19) |
| R-01-24 | A block used by two schemes SHOULD move to the cluster platform, and a block used by two clusters SHOULD move to `p`. | Studio usage report (advisory) |

## 1.5 Hot update, in one table

| Mechanism | Carries | Reaches new instances | Reaches running instances |
| --- | --- | --- | --- |
| Lookup | Values, lists, registries, rail order, cut-offs | From the effective date | From the effective date, at the next step that reads them |
| Release | Rule logic, activities, maps, hooks, hook points, workflows | When the intake rule selects the new workflow release | Never |
| Supersession | One replacement within a major line | Not needed | Steps not yet started |

A value change does not rerun past steps. Re-evaluation is the `re-screen` signal (Part 5).

## 1.6 Examples

- Major line: `uae.p.validate.iban.v1`. Release: `1.0.0`. File: `factory/uae.p/activities/uae.p.validate.iban.v1.yaml`.
- Supersession: `examples/supersession/SUP-2026-001.yaml`.
- Pack manifest with pins: `factory/uae.wps/pack.yaml`.

## 1.7 Open decisions touching this part

D15 (supersession policy: explicit only, even for a patch) and D12 (bank scope name, closed as `b`).
