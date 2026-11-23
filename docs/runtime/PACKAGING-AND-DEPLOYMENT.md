# Packages, distributions and how a payment moves from an old version to a new one

Status: decided by the owner's direction of 22 November 2026. This document decides the package granularity and the deployment model. It supersedes the package paragraph of W4 in `NSTAR-CONVERGENCE-PLAN.md` where they differ.

## 1. The problem

- A scheme such as `uae.wps` is a leg. It fills a slot (`scheme-leg`, `scheme-refuse`) in a platform workflow (`p.wf.corporate`, `p.wf.inward` and the others).
- If the scheme were deployed on its own, deploying it would add a leg to a workflow that is already running. The runtime would then have to combine a platform version with a scheme version at run time, and every pair would need testing.
- If everything were one platform package, a fix to one scheme would redeploy every scheme.
- The owner's rules: a new package is inserted, never updated; it runs independently as a complete copy; the old one drains and is then removed from the service.

## 2. The decision: three layers, two kinds of package

| Layer | What it is | Who changes it | How it reaches a runtime | Changes the code that runs? |
| --- | --- | --- | --- | --- |
| **Runtime software** | The engine hosts, workers, store, API, consoles. Container images built by NEST. | NEST | A software upgrade, by the bank's service engineers | Yes, the hosts. It declares the DSL and contract versions it supports. |
| **Distribution** | A complete, self-contained, signed copy of everything one bank runs: the platform release, the scheme releases the bank licenses, the bank's own pack, the engine binding. | Built by the studio when a version is approved | Installed from GitHub through the service console | Yes. This is the only way new workflow code or activity code arrives. |
| **Configuration release** | The bank's values: dials (parameters within bounds), rule values, port bindings (which core banking, which hosts), calendars. Effective-dated. | The bank configurator, approved by a bank approver | Installed from GitHub through the service console | No. It changes values only, never which code runs. |

So the **authoring and versioning units** in the studio stay as they are: the platform pack (`p`, `uae.p`), each scheme pack (`uae.wps`, and later others), each bank pack (`b`). Each has its own semantic version.

The **deployment unit** is the distribution. A scheme pack is never deployed on its own into a running platform. The studio resolves the platform, the schemes and the bank pack into one distribution at build time, so the runtime never combines versions at run time.

## 3. What is in a distribution

A distribution is a signed directory published to the GitHub content repository under `releases/distributions/<bank>/<distribution id>/` with an annotated tag `dist/<bank>/<distribution id>`. Its id is a date and a counter, for example `2026.11.22-1`.

| Part | Content |
| --- | --- |
| `manifest.yaml` | The distribution id, the bank, the target engine, the DSL version, the runtime software range it needs, the bill of materials (every pack release and every artefact release with its SHA-256), the parameter contract (every dial with its type and bounds), and the change sets it came from. |
| `manifest.sig` | The signature over the manifest, with NEST's key id. |
| `workflows/` | Every platform and scheme workflow, with its slots already filled, in the expanded form (R-09-17), plus the engine's own form (BPMN for Flowable, definitions for Elsa). |
| `activities/` | Every activity contract, and its implementation: scripts, and the native assemblies with their checksums. |
| `rules/` | Every rule set and table, the logic only. Values come from the configuration release. |
| `ports/` | Port contracts and the adapter code for each adapter the bank may bind (for example `simulator`, `gateway-rest`). |
| `errors/` | The error dictionary entries and runbooks for every error code the distribution can raise. |
| `ops/` | The operations console's leg views for each scheme in the distribution. |
| `tests/` | The test pack and the conformance results the release passed. |

Not in a distribution: simulators (except in a test environment), secrets, values, and anything from another bank.

## 4. What is in a configuration release

A signed directory under `releases/configuration/<bank>/<release>/`, with `manifest.yaml` and `manifest.sig` as above, holding:

- the configuration set (dial values, rule values), each with its `validFrom`;
- port bindings (adapter and settings, with credentials as `secretRef` only), each with its `validFrom`;
- calendars;
- the reference tables the bank sees, each with every edition (`reference/<name>.yaml`; R-01-25): the participant directory, emirates, free zones, purpose codes and business-day calendars the platform vendor keeps, and the bank's own branches. A value that reads a list from a table (`fromReference`, R-11-51) goes out as the values it reads. The runtime serves the tables by date under `GET /lookups/{domain}` (R-09-75). Bank customer data (the corporate registration) is never in a release: it lives in the runtime's registers (R-05-62);
- `requiresContract`: the parameter contract versions it was checked against.

The runtime stores each value as an insert-only, effective-dated version (built in P6, `BankValues`).

## 5. How a payment is pinned

1. At acceptance, the runtime picks the distribution that is **active** for the bank and environment at that moment, and records its id on the payment. That never changes.
2. Every leg, child workflow, activity and rule of that payment runs from that distribution. A payment never mixes distributions.
3. Values are read from the configuration versions in force on the date each rule declares: as of acceptance, or as of the step (W4). A value change therefore reaches a running payment only where its rule says "as of the step".
4. A message for a running payment (an ACK, a NAK, a supplementary file) is correlated to that payment's instance, so it is handled by the distribution the payment is pinned to.
5. A related new payment (an inward return for an earlier outward file) is a new payment. It takes the distribution active when it arrives and refers to the earlier payment by id.
6. Where this happens: every file reaches the runtime through the File API (section 11), and the front door that pins, starts and correlates runs in the File API's process, behind its intake.

## 6. Running side by side, draining and removal

A distribution moves through these states. Each change is an insert-only activation row with who did it and when.

| State | Takes new payments | Runs its open payments | Workers and definitions |
| --- | --- | --- | --- |
| Installed | No | None | Loaded and verified, health-checked |
| Active | Yes | Yes | Running |
| Draining | No | Yes, to the end | Running |
| Drained | No | None open | Running until the longest wait it declares has passed, for late messages |
| Removed | No | None | Stopped and unregistered. The package stays in GitHub and in the store as a record. |

- **Independent copies.** Each distribution has its own workflow definitions (`name@distribution`), its own task queues or worker build ids, and its own activity assemblies, loaded side by side (W4). Engine by engine:
  - Temporal: worker versioning by build id, one per distribution.
  - DTFx: orchestration and activity names carry the distribution.
  - Elsa: a workflow definition version per distribution.
  - Flowable: one deployment per distribution.
- **The long-running host** (`runtime/tools/TransactHub.EngineHost`, R-09-66 to R-09-68). A supervisor per engine, bank and environment reads the distributions' states from the store and runs a host per distribution that is Installed, Active, Draining or Drained, side by side. The Active one's host is current on the engine (on Temporal, the worker deployment's current version); each payment is pinned at acceptance and run by the host of its distribution; a Draining distribution is recorded Drained once none of its payments is open; a Removed one's host unregisters it from the engine (on Temporal, its workers stop and the version is deleted) and stops. A host refuses to stop while a payment it runs is open. An operator wait (a hold, a parked step) waits in the host for as long as its own expiry says, on the bank's business calendar (R-09-88); the per-payment harnesses and their patience are for tests. On DTFx and Elsa the host runs each payment's engine in a harness run of its own, in real time (R-09-93); such a run does not survive a restart of its host yet (Issue I-165). When no caller writes a host's plan, the supervisor builds it from the stored, verified package (R-09-94).
- **Shared:** the runtime software, the engine servers and the store. Store schema changes follow expand-then-contract, so every installed distribution keeps working through a runtime upgrade.
- **Switch-over:** activating a new distribution moves the old one to Draining in the same step. From that instant, new payments take the new one.
- **Removal** is refused while any payment pinned to the distribution is open, or before its longest declared wait (for example the 30-day returns wait) has passed. The service console shows the open count per distribution.
- **Rollback:** activate the previous distribution again. If it was removed, reinstall it from GitHub first. Payments that started on the newer one finish on it.
- **One bank per runtime** (owner's decision of 23 November 2026, R-09-76): a runtime is configured with its bank at install (`TH_BANK`) and refuses a distribution or configuration release whose signed manifest names another bank, at verify, install and activate. The studio builds distributions for every bank; each bank's runtime takes only its own.
- **Consent** (owner's decision of 23 November 2026, R-09-77): installing, activating, draining, removing and rolling back are asked for by a service engineer in the service console and carried out once a bank approver, another person, consents. The release API's pipeline may do them itself only in a test environment (R-09-78).
- **Insert-only, at the database too** (R-09-81): a deployment runs the hosts as a login that may insert and read the store, never update or delete it. `deploy/laptop/test/insert-only.mts` shows a switch-over end to end on the laptop stack and that no row of a distribution, its activations or the fact log changed.

## 7. The dependency tree

```
Runtime software (NEST images, store schema)
  supports DSL and contract versions
  |
  +-- Distribution, per bank and environment (signed, immutable, complete)
  |     bill of materials:
  |       platform pack release   (p, uae.p)
  |       scheme pack releases    (uae.wps, later others)  each needs a platform slot contract
  |       bank pack release       (b: hooks, bank rules, bank activities)
  |       engine binding
  |     parameter contract (every dial, type, bounds)
  |
  +-- Configuration release, per bank (signed, effective-dated values)
        checked against the parameter contracts of every distribution not yet removed
```

- A scheme pack declares the platform slot contract it fills (for example `scheme-leg@1`). The studio refuses to build a distribution whose scheme does not fit its platform's slot contract.
- A configuration release is refused at install if any value is unknown to, or outside the bounds of, a distribution that will read it (one that is Installed, Active, Draining or Drained). A value for a new dial is ignored by distributions that do not declare that dial.

## 8. Scenarios

| # | Change | Studio produces | Runtime does | In-flight payments |
| --- | --- | --- | --- | --- |
| 1 | A bank changes a dial or rule value (a limit, the ack wait) | Configuration release | Stores dated values; no deployment | Rules "as of acceptance" keep the old value; "as of the step" see the new one from its date |
| 2 | A bank switches core banking from the simulator to its gateway | Configuration release (binding with a later `validFrom`) | Uses the new binding for attempts from that time | Attempts after the switch use the new adapter (its code is already in the distribution) |
| 3 | A bank switches a hook on or off, or adds a bank rule or a bank activity | Bank pack release, then a new distribution | Install, activate; old drains | Finish on the old distribution, without the hook |
| 4 | A fix to a WPS activity or subflow | Scheme pack patch release, then a new distribution per bank that runs WPS | Install, activate; old drains | Finish on the old code |
| 5 | A new version of a platform workflow | Platform pack release, then a new distribution per bank | Install, activate; old drains | Finish on the old platform workflow |
| 6 | A new scheme (for example IPI) | New scheme pack (it fills the platform's slot contract; the dispatch table gains rows), then a new distribution for each bank that licenses it | Install, activate | Unaffected; the new scheme is reachable only from the new distribution |
| 7 | A new dial that a bank should then set | Platform or scheme release that adds the parameter, a new distribution, then a configuration release with the value | Install the distribution, then the configuration | The old distribution ignores the new value |
| 8 | Emergency rollback | Nothing new | Activate the previous distribution | New payments go to the previous one; the newer one's open payments finish on it |
| 9 | Move a bank from one engine to another | A new distribution with the other engine binding | Install on the other engine, activate; old drains on its engine | Finish on the old engine |
| 10 | A runtime software upgrade | Nothing in the studio | Upgrade the images (expand-then-contract schema) | Unaffected; every installed distribution keeps running |
| 11 | A bank onboards | Bank pack, its first distribution and its first configuration release | Install both, activate | None yet |
| 12 | A rule's logic changes (not its value) | Platform or scheme release, then a new distribution | As 4 | As 4 |

## 9. Why not the alternatives

- **One platform package with every scheme, for every bank:** the same distribution for all banks would carry schemes a bank does not license, and every bank's hook changes would need a release for all banks.
- **Deploying scheme packs on their own and combining them at run time:** every platform and scheme pair would need testing, a payment's behaviour would depend on what else is installed, and draining would have to track several versions per payment.
- **Values inside the distribution:** a limit change would need a full deployment. Keeping values effective-dated and separate is what makes scenario 1 a no-deployment change (proved in `e2e-p6-hot-update.test.ts`).

## 10. What changes in the code (work items)

1. Studio: build a distribution per bank from approved pack releases (bill of materials, slot-contract check, parameter contract, error dictionary and leg views included), sign it, publish it with its tag. Build configuration releases separately, signed.
2. Runtime: store distributions and configuration releases insert-only; activation per bank and environment; pin `distributionId` on every payment; load each distribution side by side on every engine; the five states; the removal guard; rollback.
3. Service console: list both kinds from GitHub, verify, show the difference with the active one, install, activate, drain, remove, roll back; the open-payment count per distribution.
4. Spec: requirements for distributions, configuration releases, pinning and draining in Part 09 and Part 11, with checks.
5. Tests: scenarios 1 to 12 as end-to-end tests, at least on Temporal, with 3, 4, 5, 8 and 9 on every engine.

## 11. The File API: how files reach and leave a runtime

Owner's decision of 23 November 2026 (R-09-82 to R-09-86, I-158).

| Part | Runtime software | Its settings at an installation |
| --- | --- | --- |
| File API (`runtime/src/TransactHub.FileApi`) | Its own process and container image, beside the release API, the engine host, the native host and the consoles | `Store:*`, `FileApi:Environment`, `TH_BANKS_DIR` (the port bindings), `FileApi:EngineHosts:<bank>`, `FileApi:Workers:<scope>`, `FileApi:Messages:*`, `FileApi:Inboxes:*`, `TH_FILE_SIGNING_KEY_REF`, `Auth:*` |
| Front door | In the File API's process | The engine host's supervisor of each bank, the workers that run the intake maps |
| SFTP gateway | The `sftp` adapter in the File API (`TransactHub.Ports`), polling each partner's inbox and pushing the outbox | The bank's binding of `p.port.file-transfer.v1` (a configuration release): partners, host keys and login by reference, `sessions.*` |

- A file is routed by the distribution active for the bank: its dispatch table says whether the file starts a payment or answers one. A file for a payment pinned to a draining distribution is handed to that distribution's host, never the new one.
- The bank's file rules (name, extension, size) and its inboxes are settings of the installation until the scheme declares them (I-159, I-160).
- On a laptop the binding is the laptop's own (`deploy/laptop/banks`, laid over the bank's with a later `validFrom`): `sftp` to the stack's one SFTP server, each inbox naming its message, the host key pinned by reference and the login made at the stack's start. At a bank it is `sftp` to the bank's partners. The File API polls the inboxes of the runtime's bank only (R-09-104), and on the laptop runs each intake map on the workers of the payment's distribution (R-09-105).
- The engine host takes a correlated message at any time: on Temporal as a signal to the payment's front door workflow, on DTFx, Elsa and Flowable through the payment's outbox to its run (R-09-93). The supervisor answers the File API beyond loopback only with its token; on the laptop it listens on the compose network (R-09-95, I-158).
- A scheme's report file is reconciled on arrival (R-09-92): each break is a fact, listed and resolved in the operations console's break queue, a resolution that changes the system with a bank approver's consent. Which messages are reports is the installation's setting until the scheme declares them (I-164).
