# Part 11. Packs and governance

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 11.1 Purpose

A pack is the unit of release. This part defines pack types, the manifest, how content is approved, and how it leaves the studio for Configuration and Test and release.

## 11.2 Pack types

| Type | Scope | Holds | Promotable to production |
| --- | --- | --- | --- |
| Shared | `p`, `<cluster>.p`, `<cluster>.<scheme>` | Activities, rules, maps, ports, extensions, workflows, the scheme manifest | Yes, once it holds no simulator and no planned message |
| Bank | `b` | A tenant's hooks and their blocks and rules | Yes |
| Test | Any | Simulators, test cases, conformance fixtures, samples | Never |

A factory pack is a shared pack with `origin: factory` (Part 12).

## 11.3 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-11-01 | A pack MUST have a manifest that lists every artefact with its exact release, hash and path. | Schema `pack` (L00) |
| R-11-02 | A pack MUST name the packs it depends on, with their releases. | Schema (L00) |
| R-11-03 | A pack carries its own `major.minor.patch`. A patch in any member is at least a patch of the pack. | Lint L26 |
| R-11-04 | A bank pack MUST have scope `b` and name its tenant in the manifest. | Schema (L00) |
| R-11-05 | A pack that contains a simulator MUST be marked not promotable. | Schema (L00) |
| R-11-06 | A scheme pack with a `planned` message MUST NOT be published. | Lint L10 |
| R-11-07 | Simulators, tests and samples SHOULD be exported as a test pack beside the shared pack, so the shared pack is promotable. | Export test (C19) |
| R-11-08 | Artefacts MUST be stored as files in a repository, one file per artefact, at `<scope>/<folder>/<name>.yaml`. | Store test (C01) |
| R-11-09 | The stored form MUST be JSON-compatible YAML or JSON. Canonical JSON is the form that is hashed. | Store test (C01) |
| R-11-10 | Each tenant MUST have its own repository or its own isolated branch space. Shared content is read from the owner's releases. | Isolation tests (C17) |
| R-11-11 | Publishing MUST require a maker and a different checker. Review is the checker step. | Governance tests (C17) |
| R-11-12 | Every publish, acceptance, binding and supersession MUST be written to an append-only change log. | Audit test (C17) |
| R-11-13 | An artefact MUST record provenance: origin, status (`confirmed` or `provisional`), clauses, and AI details where AI authored it. | Schema `common#provenance` (L00) |
| R-11-14 | An artefact with status `provisional` MUST NOT be promoted to production. | Release check (C19) |
| R-11-15 | A release leaves the studio as an export handed to Configuration. Nothing reaches production except through Test and release. | Process; north-star change path |
| R-11-16 | A manifest MUST NOT name a native code-behind release that has not passed the conformance pack. | Release check (C19) |
| R-11-17 | The release report MUST list, per artefact, what changed in the interface and which workflows gained a new release. | Release report test (C19) |
| R-11-18 | Retirement MUST be a tracked step. The studio lists who still pins a release; operations supply which instances are still running. | Retirement test (C19) |

## 11.4 Repository layout

```
<scope>/
  pack.yaml                 the manifest; generated
  scheme.yaml               scheme packs only
  activities/  rules/  ports/  simulators/  extensions/  workflows/  hooks/  tests/
  scripts/                  script-behind files, one per artefact
  samples/                  sample and golden messages
```

## 11.5 Release flow

1. A designer changes an artefact as a draft.
2. Static checks and tests run.
3. A checker approves. The artefact publishes at the semver its interface diff implies.
4. The studio reruns every workflow on that major line and proposes new workflow releases.
5. A pack release is cut. The conformance kit runs on every target.
6. Each tenant sees the compatibility run for its hooks and accepts the release.
7. The export goes to Configuration, then through Test and release to production.

## 11.6 Examples

- Shared pack manifest: `factory/uae.wps/pack.yaml`.
- Bank pack manifest: `examples/bank/pack.yaml`.

## 11.7 Open decisions touching this part

D8 (artefacts as files in a repository per tenant).
