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
| R-11-22 | Every change set in progress MUST live on its own branch, `studio/<change set id>`, started from the content branch. Each draft save MUST be one commit there. A new draft joins the open change set of the first draft, by change set id, that it refers to or that refers to it, else a new one. Approval publishes the closure (R-11-19), across branches if it must, under the id of the approved draft's branch, into the content branch as one commit (R-09-20), or as a pull request in pull-request mode (R-09-22); when it publishes all of the branch, the commit takes the branch in as its second parent, and the audit of every other branch it empties goes with it. A branch that still holds drafts after a publish keeps them; they publish later under a new id. Drafts survive a restart. | Content store tests ("each draft save is a commit on studio/<id>", "approval publishes the change set", "a new draft joins the first change set, by id", "a branch that still holds drafts after a publish keeps them"); no-disk test (`studio/test/no-disk-write.test.ts`) |
| R-11-23 | The studio's state MUST be files on the content branch, and MUST be append-only: a file once written is not rewritten. The audit is one file per change set (`audit/change-sets/<id>.yaml`, on the change set's branch until it publishes) and one per event that is part of no change set (`audit/events/<date>/<time>-<action>-<hash>.yaml`), including a later event about a published change set; each record hashes the one before it in its file. A waiver is a change set of its own: one commit with a change set record that holds the waiver (`waivers:`) and its audit. In-flight counts (`governance/in-flight/`), handed-over supersessions (`supersessions/handed-over/`) and samples (`samples/`) are one file each. Two files are replaced, each by one commit that MUST find the file as the studio read it: the users and roles (`governance/users.yaml`), only through maker-checker, and the studio settings (`governance/settings.yaml`), by a platform approver or service operations, with its event; Git keeps every earlier version (Issue I-44). | Content store tests ("keeps the audit", "changes the users only through maker-checker", "a waiver goes with its change set") |
| R-11-24 | The studio MUST hold its content in memory only: read through the GitHub REST API (refs, trees, blobs, compare) and cached by commit SHA, and written only through the Git Data API (a tree with its files' text, a commit, a ref created or moved by fast-forward, and a change set branch deleted, R-11-26) and, in pull-request mode, a pull request. Any token value is sent as given and its format is not checked, so a proxy that authenticates the requests may supply the credential. It MUST NOT write a file to disk: no workspace file, no data folder, no clone. | No-disk test (`studio/test/no-disk-write.test.ts`); content store tests ("calls only the endpoints of its client") |
| R-11-25 | The factory packs (`spec/factory`: `p`, `uae.p`, `uae.wps`), the engine bindings and the supersession example (`spec/examples`), the example bank (`spec/examples/bank`), and the factory's script-behinds (`studio/scripts/`, to `scripts/`) and test samples (`studio/fixtures/samples/`, to `samples/`) MUST be published into the content branch by the seed command (`npm run studio -- seed-content`), as one change set, beside what is already there. The studio MUST read them from the branch, not from `studio/`. The seed MUST be idempotent: a second run changes nothing. Each seeded file's first line says `Generated by` and names its source in `spec/` or `studio/`. A factory change after the seed needs a new release: on a connected branch the seed never rewrites a file and stops naming it (R-09-21). This repository's `content-configurations/` MUST hold the seed as the sources are now; `seed-content --write-local` regenerates it. | Content store tests ("seeds once as one change set", "reads them from the branch, where the seed published them", "a factory change after the seed needs a new release"); `npm run studio -- seed-content` reports every seeded file current |
| R-11-26 | The studio MUST delete a change set branch, `studio/<change set id>`, once the content branch holds everything on it: in commit mode right after the publish that empties it, in pull-request mode when it next reads the branches and finds the pull request merged (the content branch has taken the branch in, or nothing on it differs). It deletes a branch only if it still points where the studio saw it. It MUST NOT delete any other branch, and a command that only reads deletes none. | Content store tests ("deletes only a change set branch", "approval publishes the change set", "in pull-request mode, approval opens a pull request"); `studio/test/content.real-github.test.ts` with `TH_REAL_GITHUB=1` |
| R-11-27 | The studio MUST NOT read every audit file at start. It reads the events its state depends on (a retirement, who set a configuration set), the newest event, whose hash the next one chains to, and the audit of each change set in progress; the rest it reads when the audit is opened, once, cached by blob SHA. The hash chain MUST verify over the whole audit. | Content store tests ("the audit is read when it is opened") |
| R-11-28 | A bank pack MAY live in `content-configurations/banks/<bank>/`, with a `pack.yaml` of `packType: bank` and scope `b` that names its tenant (Issue I-43). Every studio artefact in the folder MUST be that tenant's, in scope `b` or a test, and MUST refer only to the factory or the folder. Documents of kind `port-bindings` and `configuration-values` are the runtime's and are not pack artefacts. The lock file MUST list every artefact with its kind, release, hash and path, and depend on the scopes its artefacts, its tests among them, reach. The studio MUST load such a pack only when asked (`--bank <name>`, `TH_BANKS`, `openEnv({ banks })`), never by default; the example bank, which the seed publishes there (R-11-25), is the one it always reads. | Lint L35 (`tools/validate.py` reads `content-configurations/banks/`); `studio/test/bank-packs.test.ts` |

### Sign-in, roles and the permission table

Decided by the owner on 22 November 2026 (Issue I-65, the studio's part). The studio and the runtime have separate sign-in and separate trust.

| Id | Requirement | Check |
| --- | --- | --- |
| R-11-29 | The studio's roles MUST be: Bank Configurator and Bank Approver, each in exactly one bank tenant; Workflow Designer, Workflow Developer, Platform Approver, Administrator and Service operations, in the owner tenant; Auditor, in either. A user is an entry of `governance/users.yaml` with an id, one tenant, roles and optionally the identity provider's subject it signs in as. A bank role in the owner tenant, or a platform role in a bank tenant, grants nothing and is refused in a proposed list. A role name of an earlier release (shared designer, shared checker, bank designer, bank checker) is read as the roles that hold the same rights (Issue I-69). An identity that is no user, and a role the studio does not know, grant nothing. | `studio/test/permissions.test.ts` ("reads the roles of earlier releases", "does not let a bank role work in the owner tenant", "the accounts") |
| R-11-30 | One permission table MUST decide every write, release and approval: from role, scope class (scope `b` of the user's bank, the shared scopes, or the studio), kind (each artefact kind, and bench, import, export, deploy, waiver, settings, release, in-flight, users, assistant) and action (author, approve, accept, run, retire), to allowed. Every API path that changes or runs something MUST ask it, with the signed-in user, among them drafts, scripts, samples, approval, configuration, import, waivers, settings, export, packs, retirement, supersession, users and the assistant's actions, which go through the same API as the user. A user acts only for its own tenant: a call that names another is refused. A refusal is answered 403 and audited. The web app reads the table only to choose what to show. | `studio/test/permissions.test.ts` (every role, kind and action); `studio/test/blackbox/auth.test.ts`; `studio/test/blackbox/api.test.ts` ("answers the permission question per role") |
| R-11-31 | A maker's save MUST need no approval: a Workflow Designer, a Workflow Developer and a Bank Configurator save, change and delete drafts, and request the release of a change set. The one checker step is the approval that makes a change set's drafts a released version, the version that is exported to the runtime. Its approver MUST hold the approver right for the change set's area (a Bank Approver of the bank for a bank's change set, a Platform Approver for a designer's or a developer's) and MUST NOT be a maker of any member. Change sets are per tenant, so a change that touches a bank and shared content is two change sets, each approved by its own approver. A bank's configuration set is a draft until a Bank Approver approves it. A change to the users and roles is proposed by one Administrator and approved by another (R-11-23). | `studio/test/blackbox/auth.test.ts` ("a bank approver who is not the maker makes each a version"); `studio/test/permissions.test.ts` ("lets no maker approve", "refuses a release approved by a maker"); `studio/test/content.test.ts` ("changes the users only through maker-checker") |
| R-11-32 | The studio MUST require sign-in, by OpenID Connect with the authorization code flow and PKCE (S256), against the identity provider its environment names (`TH_OIDC_ISSUER`, `TH_OIDC_CLIENT_ID`, and a client secret only by reference, `TH_OIDC_CLIENT_SECRET_REF=env:<NAME>`). It MUST verify the ID token's signature against the provider's keys, its issuer, its audience, its expiry and its nonce. The session MUST be an id in a signed, http-only, same-site cookie, held in memory only (R-11-24); sign-out ends it. Every API call MUST take its user from the session, never from the request's body or query; a body that names another acting user is refused. No session is 401; a signed-in identity that is no user of the studio is 403. A development provider (`TH_AUTH_DEV=1`) MAY serve local work and tests; the studio MUST refuse to start with it when `NODE_ENV=production`, and MUST refuse to start with no sign-in configured. | `studio/test/auth.test.ts`; `studio/test/blackbox/auth.test.ts` ("sign-in"); `studio/test/blackbox/ui-login.test.ts`; `studio/test/no-disk-write.test.ts` |
| R-11-33 | Every state-changing call MUST carry the session's CSRF token (`x-csrf-token`) and, when the browser sends an `Origin`, come from the studio's own origin; otherwise it is refused with 403. | `studio/test/blackbox/auth.test.ts` ("refuses a change without the CSRF token") |
| R-11-34 | The studio's sign-in MUST be its own: its identity provider is the one the platform vendor that operates the studio configures for the studio alone, and the studio trusts that one issuer. Bank Configurators and Bank Approvers sign in with accounts in that provider; federation with a bank's own provider, if it comes, is brokered by that provider, never trusted by the studio directly. The studio MUST NOT accept a bearer token on its API, a token issued for another client (the runtime's among them), or a session of the runtime, and MUST NOT share a session with the runtime. Each account MUST be one identity in one tenant: no id or subject twice in `governance/users.yaml`. The provider's groups MAY narrow a user's roles (`TH_OIDC_GROUPS_CLAIM`, a group `<prefix><role>`), never add one. | `studio/test/auth.test.ts` ("accepts no bearer token", "refuses a token for another client", "narrows a user's roles"); `studio/test/permissions.test.ts` ("the accounts") |

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
scripts/<activity>.ts | .py                 a script-behind: the factory's (seeded) or a designer's, published with its activity
samples/<name>                              a synthetic sample: the factory's test samples (seeded) or one a designer added
banks/<tenant>/                             a bank pack in its own layout (pack.yaml, activities/, ...)
banks/<tenant>/artefacts/ ... configuration-sets/ ... scripts/   what the studio publishes for the bank
change-sets/<id>.yaml                       a published change set: members, makers, checker, waivers
audit/change-sets/<id>.yaml                 the audit of a change set
audit/events/<date>/<time>-<action>-<hash>.yaml   an event that is part of no change set
governance/users.yaml   governance/settings.yaml   governance/in-flight/
supersessions/<id>.yaml   supersessions/handed-over/<id>.yaml
releases/<scope>/<name>/<release>/<target>/ an engine package, for the runtime
environments/<env>/activations/<id>.yaml   an activation, for the runtime
```

A change set in progress is a branch, `studio/<change set id>`, that holds its drafts at the paths they publish to, their scripts and the change set's audit (R-11-22). It is deleted once the content branch holds everything on it (R-11-26).

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
