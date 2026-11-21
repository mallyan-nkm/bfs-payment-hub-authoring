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
| R-11-08 | Artefacts MUST be stored as files in the content branch's `content-configurations/` folder, one file per document at its release: `artefacts/<scope>/<name>/<release>.yaml` (a test at `artefacts/<scope>/tests/<id>.yaml`), a bank's own in `banks/<tenant>/` (section 11.4). The factory's authoring source stays `spec/factory/<scope>/<folder>/<name>.yaml`. | Store test (C01); content store tests (`studio/test/content.test.ts`) |
| R-11-09 | The stored form MUST be JSON-compatible YAML or JSON. Canonical JSON is the form that is hashed. | Store test (C01) |
| R-11-10 | Each tenant MUST have its own repository or its own isolated branch space. Shared content is read from the owner's releases. | Isolation tests (C17) |
| R-11-11 | Publishing MUST require a maker and a different checker. Review is the checker step. | Governance tests (C17) |
| R-11-12 | Every publish, acceptance, binding and supersession MUST be written to an append-only change log: the audit files on the content branch (R-11-23). | Audit test (C17); content store tests ("keeps the audit as one file per change set") |
| R-11-13 | An artefact MUST record provenance: origin, status (`confirmed` or `provisional`), clauses, and AI details where AI authored it. | Schema `common#provenance` (L00) |
| R-11-14 | An artefact with status `provisional` MUST NOT be promoted to production. | Release check (C19) |
| R-11-15 | A release leaves the studio as an export handed to Configuration. Nothing reaches production except through Test and release. | Process; north-star change path |
| R-11-16 | A manifest MUST NOT name a native code-behind release that has not passed the conformance pack. | Release check (C19) |
| R-11-17 | The release report MUST list, per artefact, what changed in the interface and which workflows gained a new release. | Release report test (C19) |
| R-11-18 | Retirement MUST be a tracked step. The studio lists who still pins a release; operations supply which instances are still running. | Retirement test (C19) |
| R-11-19 | Approval publishes a change set the studio generates: the draft, and every draft of the tenant that refers to it or that it refers to, directly or through others. Every member is checked with the others in place, the tests that target any member pass with the whole set in place, the checker is not the maker of any member, and all publish or none does. The change set is recorded with an id. | Publish test (C17) |
| R-11-20 | An export MUST list the change sets its artefacts were published in: those recorded at approval, and for content shipped with the spec (published by the seed, R-11-25), its pack release. Every artefact of the package is in exactly one. | Export test (C20) |
| R-11-21 | The content branch MUST be the studio's only store. The studio reads every artefact and every piece of its state from that branch's `content-configurations/` folder and from the branches of the change sets in progress. It MUST NOT read the factory or the example bank from `spec/` at run time. The conformance kit (`spec/conformance`) is the engines' test fixture, not content, and MAY be read from `spec/`. | Content store tests (`studio/test/content.test.ts`: "reads the factory and the example bank from the branch"); `npm run studio -- check` |
| R-11-22 | Every change set in progress MUST live on its own branch, `studio/<change set id>`, started from the content branch. Each draft save MUST be one commit there. A new draft joins the open change set of a draft it refers to or that refers to it. Approval publishes the change set into the content branch as one commit (R-09-20), or as a pull request in pull-request mode (R-09-22); when it publishes all of the branch, the commit takes the branch in as its second parent. Drafts survive a restart. | Content store tests ("each draft save is a commit on studio/<id>", "approval publishes the change set"); no-disk test (`studio/test/no-disk-write.test.ts`) |
| R-11-23 | The studio's state MUST be files on the content branch, and MUST be append-only: a file once written is not rewritten. The audit is one file per change set (`audit/change-sets/<id>.yaml`, on the change set's branch until it publishes) and one per event that is part of no change set (`audit/events/<date>/<time>-<hash>.yaml`); each record hashes the one before it in its file. A waiver is in its change set's record. In-flight counts (`governance/in-flight/`), handed-over supersessions (`supersessions/handed-over/`) and samples (`samples/`) are one file each. Two files are replaced, each by one commit that MUST find the file as the studio read it: the users and roles (`governance/users.yaml`), only through maker-checker, and the studio settings (`governance/settings.yaml`) (Issue I-44). | Content store tests ("keeps the audit", "changes the users only through maker-checker", "a waiver goes with its change set") |
| R-11-24 | The studio MUST hold its content in memory only: read through the GitHub REST API (refs, trees, blobs, compare) and cached by commit SHA, and written only through the Git Data API (blobs, a tree, a commit, a ref created or moved by fast-forward) and, in pull-request mode, a pull request. It MUST NOT write a file to disk: no workspace file, no data folder, no clone. | No-disk test (`studio/test/no-disk-write.test.ts`); content store tests ("calls only the endpoints of its client") |
| R-11-25 | The factory packs (`spec/factory`: `p`, `uae.p`, `uae.wps`), the engine bindings and the supersession example (`spec/examples`) and the example bank (`spec/examples/bank`) MUST be published into the content branch by the seed command (`npm run studio -- seed-content`), as one change set, beside what is already there. The seed MUST be idempotent: a second run changes nothing. Each seeded file's first line says `Generated by` and names its source in `spec/`. This repository's `content-configurations/` MUST hold the seed as the sources are now. | Content store tests ("seeds once as one change set"); `npm run studio -- seed-content` reports every seeded file current |

## 11.4 Repository layout

The authoring source of the factory, in `spec/`:

```
factory/<scope>/
  pack.yaml                 the manifest; generated
  scheme.yaml               scheme packs only
  activities/  rules/  ports/  simulators/  extensions/  workflows/  hooks/  tests/
```

The content branch, which is the studio's only store (R-11-21), in `content-configurations/`:

```
artefacts/<scope>/<name>/<release>.yaml     each published document, the seeded factory's among them
artefacts/<scope>/tests/<id>.yaml           a test case
scripts/<activity>.ts | .py                 a designer's script-behind, published with its activity
samples/<name>                              a synthetic sample a designer added
banks/<tenant>/                             a bank pack in its own layout (pack.yaml, activities/, ...)
banks/<tenant>/artefacts/ ... configuration-sets/ ... scripts/   what the studio publishes for the bank
change-sets/<id>.yaml                       a published change set: members, makers, checker, waivers
audit/change-sets/<id>.yaml                 the audit of a change set
audit/events/<date>/<time>-<hash>.yaml      an event that is part of no change set
governance/users.yaml   governance/settings.yaml   governance/in-flight/
supersessions/<id>.yaml   supersessions/handed-over/<id>.yaml
releases/<scope>/<name>/<release>/<target>/ an engine package, for the runtime
environments/<env>/activations/<id>.yaml   an activation, for the runtime
```

A change set in progress is a branch, `studio/<change set id>`, that holds its drafts at the paths they publish to, their scripts and the change set's audit (R-11-22).

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
