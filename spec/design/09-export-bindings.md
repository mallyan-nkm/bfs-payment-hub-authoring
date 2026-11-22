# Part 9. Export bindings

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 9.1 Purpose

The DSL is the single source of truth and stays engine-neutral. Temporal, DTFx, Flowable and Elsa are all delivery targets, and together they are the proof of independence. This part defines what is exported and how each engine runs it.

## 9.2 Strategies

| Target | Strategy | What is exported | State |
| --- | --- | --- | --- |
| Temporal | Interpreter | The workflow documents as data, the manifest, task registrations by exact release | Exists today; extend |
| DTFx | Interpreter | The same documents; one generic orchestration interprets them | To build |
| Flowable | Generation | BPMN 2.0 XML per workflow, plus worker topics by exact release | To build; least certain |
| Elsa | Interpreter | The same documents; one generic Elsa activity interprets them, its journal kept in the workflow state | Built on Elsa 3.10.2 (I-30) |

## 9.3 Construct mapping

| Construct | Temporal | DTFx | Flowable | Elsa |
| --- | --- | --- | --- | --- |
| Call | Activity on a task queue | Task activity | External worker task | Operation run on the task's worker queue; the bookmark is resumed with the result |
| Slot, use of a leg | Child workflow | Sub-orchestration | Call activity | Child workflow instance of the same activity |
| Handler | Plain request handler; no workflow | Plain request handler; no orchestration | Plain request handler; no process | Plain request handler; no workflow instance |
| Hook | Child workflow or activity from the tenant binding | Sub-orchestration or task from the tenant binding | Call activity or service task from the tenant binding | Child workflow instance or operation from the tenant binding |
| Rule evaluation | Activity calling the rule service | Task calling the rule service | Service task calling the rule service | Operation calling the rule service |
| Switch | Interpreter branch | Interpreter branch | Exclusive gateway | Interpreter branch |
| Parallel | Concurrent activities, await all | `Task.WhenAll` | Parallel gateway | `Task.WhenAll` over the branches, in one episode |
| For each | Loop or child workflows; continue-as-new for large batches | Loop or sub-orchestrations; continue-as-new | Multi-instance, sequential or parallel | Loop or child instances; continue-as-new for large batches |
| Loop | Interpreter loop with timer | Interpreter loop with durable timer | Modelled loop with timer event | Interpreter loop with a journalled timer |
| Await | Signal and timer; signals received early are kept in workflow state | External event and durable timer; events received early are kept in orchestration state | Message catch event with timer boundary; early messages are held by the binding | A bookmark resumed by the message, raced with a journalled timer; early messages are kept in the journal |
| Wait | Timer | Durable timer | Timer event | Journalled timer |
| Block | A frame in the interpreter; no engine construct | A frame in the interpreter; no engine construct | Embedded sub-process whose end events set the outcome, then an exclusive gateway | A frame in the interpreter; no engine construct |
| Return | Leaves the frame with an outcome | Leaves the frame with an outcome | An end event of the sub-process | Leaves the frame with an outcome |
| Try, compensate | Interpreter runs compensations in reverse | Interpreter runs compensations in reverse | Compensation boundary events and handlers | Interpreter runs compensations in reverse |
| Signals | Signals | External events | Signal or message events | Events delivered at the bookmark |
| Hold | Activity creates the item; await a signal | Task creates the item; await an external event | User task or receive task | Operation creates the item; await an event |
| Deadline | Timer and cancellation scope | Timer race | Timer boundary on the sub-process | Timer race |
| Finally | Interpreter runs it on every end | Interpreter runs it on every end | End-path sub-process on every end event | Interpreter runs it on every end |

## 9.4 Policy mapping

| Policy | Temporal | DTFx | Flowable | Elsa |
| --- | --- | --- | --- | --- |
| `attemptTimeout` | Start-To-Close of each attempt, counted from when a worker starts it; the binding keeps the deadline on business time and abandons a later answer (R-05-54) | Durable timer raced against the task | Timer boundary on the external worker task; in production the binding places it when it starts the attempt (R-05-54) | Journalled timer raced against the operation; a late result is abandoned |
| `totalTimeout` | The retry plan's total time, on business time; no Schedule-To-Close | `RetryOptions.RetryTimeout` | Timer boundary on the enclosing sub-process | The retry plan's total time |
| `retry` | Retry Policy | `RetryOptions` with `ScheduleWithRetry` | A modelled loop in a sub-process per call, for fixed and growing backoff alike (proved in C22-S1) | The retry plan, each backoff a journalled timer |
| Queue timeout, heartbeat | Schedule-To-Start, Heartbeat; from the binding | Not available | Not available | Not available |

## 9.5 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-09-01 | An export MUST hold: workflow, leg, segment and handler documents with pins; activity contracts; rules; maps; the pack manifest; the engine binding; the test pack; the conformance pack. | Export test (C20) |
| R-09-02 | A tenant's export adds its bank pack and its configuration set. | Export test (C18) |
| R-09-03 | The engine binding MUST be a separate document. The designer never edits it. | Schema `engine-binding` (L00) |
| R-09-04 | A task MUST be registered as `<name>@<release>`, so two releases of one line run side by side. | Conformance CK-PIN-01 |
| R-09-05 | The worker queue or topic MUST be derived from the scope by the binding. | Schema (L00) |
| R-09-06 | An interpreter MUST be deterministic. It MUST NOT evaluate rules, read values or call ports inline. | Conformance kit; replay test |
| R-09-07 | Every target MUST produce the same business trace as the studio interpreter for every scenario of the conformance kit. | Conformance kit runner (C23) |
| R-09-08 | The conformance kit MUST gate every release on every target. | Release check (C19) |
| R-09-09 | A workflow that uses an extension construct MUST name the targets it supports. | Lint L24 |
| R-09-10 | Engine state MUST carry references, never the payment payload. | Conformance CK-CLAIM-01 |
| R-09-11 | On DTFx, Elsa and Temporal, an attempt timeout abandons the late result; it does not cancel the running task. The activity MUST therefore be idempotent. | Conformance CK-TIMEOUT-01 |
| R-09-12 | On Flowable, an activity MUST be exported as a wait state (external worker task) so that timer boundaries apply. | Generator test (C22) |
| R-09-13 | Large batches MUST be chunked on history-based engines. The chunk size comes from the binding. | Conformance CK-FOREACH-02 |
| R-09-14 | Handlers MUST be exported as plain request handlers at the interop boundary, outside the durable engine. | Conformance CK-HANDLER-01 |
| R-09-15 | A generated BPMN document MUST be reproducible: the same input gives the same bytes. | Generator test (C22) |
| R-09-16 | A binding MUST keep a message that reaches an instance before its `await` starts, and MUST hand a message that finds no waiting instance to operations as unmatched. Where an engine delivers a message only to an execution that is already waiting, the binding holds the message until it is. A held message goes only to an await of its family (R-05-44), and not to an await whose step began after it was received once an earlier await waited for it (R-05-43). | Conformance CK-AWAIT-03, CK-AWAIT-04, CK-AWAIT-06, CK-AWAIT-07 |
| R-09-17 | An engine binding MUST receive the expanded form only. It MUST NOT resolve a composite activity at run time. | Conformance CK-BLOCK-01 |
| R-09-18 | On Elsa, the interpreter MUST keep its journal (the instance input and every result, in the order it arrived) in the workflow state Elsa persists, and MUST resume only from a bookmark. A resumed run MUST reach the same operations in the same order; a journal entry the replay does not reach MUST fail the run. | Conformance kit on Elsa (`studio/engines/elsa`); replay check in the harness |
| R-09-19 | The studio MUST be connected to exactly one GitHub repository and content branch, by its environment: `TH_CONTENT_REPO`, `TH_CONTENT_BRANCH`, `TH_GITHUB_TOKEN` and, for another API base, `TH_GITHUB_API`. The branch MUST already exist, and the studio MUST NOT start without a connection. Only a shared checker or service operations changes how the studio publishes (mode, engine target, environment), in `governance/settings.yaml`. The token MUST come from the environment: it is never stored, shown, logged or written to the audit. | Content store tests (`studio/test/content.test.ts`: "does not start without TH_CONTENT_REPO", "never stores, shows, logs or writes the token", the settings refusal in "in pull-request mode") |
| R-09-20 | Each published change set MUST be one commit on the content branch, by the studio's own identity, under `content-configurations/`: each member at `artefacts/<scope>/<name>/<release>.yaml` (a bank's own at `banks/<tenant>/artefacts/…`, a configuration set at `banks/<tenant>/configuration-sets/<release>.yaml`), each member's script, the engine package of each workflow at `releases/<scope>/<name>/<release>/<target>/`, the change set record at `change-sets/<id>.yaml`, its audit at `audit/change-sets/<id>.yaml` and, if the settings name an environment, an activation at `environments/<env>/activations/<id>.yaml`. The commit message names the members, the makers and the checker. A publish that does not reach the branch publishes nothing: the studio reads the branch again and says why. | Content store tests ("approval publishes the change set into the content branch as one commit") |
| R-09-21 | The branch is insert-only. The studio MUST NOT rewrite a file on the branch (the two state files of R-11-23 aside), MUST stop a write that would and name the file, and MUST NOT force a ref update: a ref update MUST fast-forward. When the branch moved, the studio reads it again, checks the write again on top of it, and retries once; if it moved again, the studio stops with a message. Publishing what is already there changes nothing. | Content store tests ("never rewrites a published file", "re-reads, rechecks and retries once") |
| R-09-22 | In pull-request mode, approval MUST commit the change set to its own branch `studio/<change set id>` and open a pull request into the content branch; the content branch is not written. The members stay in review, on their branch, until the pull request merges; then the studio reads them as published. | Content store tests ("in pull-request mode, approval opens a pull request"); `studio/test/blackbox/ui-git.test.ts` |
| R-09-23 | Every workflow package the studio releases (published to the branch, downloaded, or written by `export-zip`) MUST be signed at release. Beside its files it carries `release.json`, a canonical JSON (RFC 8785) statement of the release id (`<root workflow>/<target>@<release>`), workflow, release, target, tenant, change set, signing key id, signing time and the SHA-256 of every other file, and `release.sig`, an ECDSA P-256 SHA-256 signature over the statement's bytes, DER encoded, in Base64 (section 9.9). The signing key MUST come only from the secret interface, by the reference in `TH_SIGNING_KEY` (any of its stores, R-06-22); it MAY then be held in the studio's process, and no hardware or remote signer is required (I-74, decided 2026-11-22). Without it nothing is released. | `studio/test/signing.test.ts` ("every released package is signed"); `SigningTests` (`runtime/tests/TransactHub.Security.Tests`), with an `openssl` check of a signature |
| R-09-24 | The runtime MUST verify a package before it stores it (`POST /releases`, `POST /import-branch`) and again before it activates it (`POST /activations`), against the signers it trusts then. It MUST refuse an unsigned package, a signature by a key it does not trust or has revoked, a signature that does not verify, a statement for another release or tenant, and any file that is missing, extra or does not match the statement. Each verification of a stored release MUST be a fact (`release.verified`) on the release's chain, naming the key, the statement's hash and the change set. A package signed again with the same files is the same release. | `ReleaseApiTests` (on SQL Server and PostgreSQL); `studio/test/blackbox/e2e-p6-hot-update.test.ts`, steps 2 and 2b |
| R-09-25 | The signers a runtime or a deployment trusts MUST be configuration (`TH_TRUSTED_SIGNERS`: the trusted-signers document, or a reference to it), read again at each verification. It lists several keys by key id (the first 32 hex digits of the SHA-256 of the key's SubjectPublicKeyInfo), each checked against its key; a key with `retiredAt` verifies only what it signed before then; a revoked key verifies nothing. | `SigningTests.Keys_rotate_…`; `ReleaseApiTests.A_key_revoked_after_a_release_was_stored_stops_its_activation_and_a_rotated_key_takes_over`; `studio/test/signing.test.ts` ("rotates keys") |
| R-09-26 | The deploy runner MUST verify a package against `TH_TRUSTED_SIGNERS` before it starts a worker or hands a document to an engine, and MUST NOT run one that does not verify. | `studio/test/signing.test.ts` ("the deploy runner refuses an unsigned or tampered package"); every black-box deploy |
| R-09-27 | Every endpoint of the runtime's release API MUST authenticate its caller with a bearer JWT from the bank's identity provider (any OIDC issuer; issuer, audience, signing keys or discovery, and clock skew are configuration), a person's token or a machine caller's client-credentials token alike. It MUST map the bank's claims and groups to its roles through the bank's role map (`Auth:RoleMap`), taking no role from a token's own claim, and MUST authorise each action by role: `release-importer` stores and imports, `release-activator` activates, `release-reader` reads releases, packages and activations, `values-reader` reads the values of the tenants the caller is given. No caller, or a token the bank's provider did not issue, is 401; a caller without the role or tenant is 403; the actor recorded is the caller's verified name. | `ApiAuthTests` (401, 403, app roles, groups, tenants, clock skew, start-up refusals) |
| R-09-28 | CI MUST scan the whole history and the tree for secrets with a pinned scanner, and MUST fail on a known vulnerability in the studio's runtime dependencies (`npm audit --omit=dev`) or the runtime's NuGet packages (`dotnet list package --vulnerable`). | `.github/workflows/security.yml` |
| R-09-29 | The runtime belongs to the bank and MUST keep no identity store: no users, passwords or tokens of its own. It MUST trust exactly one issuer, the bank's, and never the studio's. The only link from the studio to the runtime is the package signature: the runtime trusts the studio's package-signing public keys that the bank configures (`TH_TRUSTED_SIGNERS`). Authentication MUST be one library (`runtime/src/TransactHub.Auth`) that every runtime host calls with its own roles. | `ApiAuthTests.No_token_or_one_the_bank_s_provider_did_not_issue_is_401` (the studio's issuer refused), `ApiAuthTests.The_role_map_is_a_library_another_host_uses_with_its_own_roles`; review |
| R-09-30 | GitHub is the artefact repository. When a change set publishes a workflow release in commit mode, the studio MUST put the signed package (its files, `release.json` and `release.sig`) at its release folder on the content branch, never rewritten, and an annotated tag `release/<workflow>/<release>/<target>` on that commit, never moved. A runtime MAY take released versions from that repository read-only, through a package source (`IPackageSource`; the GitHub one lists the release tags and reads each version's folder through the Git Data API, its token by secret reference), and MUST store one only through the verification of R-09-24, refusing a package that is unsigned, changed, signed by a key it does not trust, or not the release its tag names. | `studio/test/signing.test.ts` ("publishing a workflow release": the tag and its package); `PackageSourceTests` (on SQL Server and PostgreSQL); `e2e-wps-source-slice.test.ts` step 4 (the runtime lists and installs the studio's tagged release from the fake GitHub) |
| R-09-31 | Every runtime host (the release API and the host of each engine binding) MUST export traces, metrics and logs with OpenTelemetry over OTLP, configured only by the standard `OTEL_*` variables. With none set, nothing is exported. Shared code MUST NOT name a backend. A host MUST keep running when the collector cannot be reached. | `runtime/tests/TransactHub.Store.Tests/TelemetryTests.cs` ("A WPS salary file through the Temporal host emits one trace ..."); `runtime/src/TransactHub.Telemetry` names no backend |
| R-09-32 | A payment MUST be one trace, from the front door's intake through every engine attempt. The process that starts a run passes the trace on (`TRACEPARENT` to a process, `traceparent` to a worker), and on Temporal the interceptor carries it through workflows, activities and signals. Each engine binding MUST emit one span per activity attempt, with the engine, activity, release, step, attempt, instance, queue and payment id. | `TelemetryTests` (one trace; the worker is handed it with every attempt); `studio/test/telemetry.test.ts` |
| R-09-33 | Every port call and every write to the store MUST be a span and a latency: `th.port.call.duration` by port and result, `th.store.write.duration` by row kinds and database. | `TelemetryTests`; `studio/test/telemetry.test.ts` |
| R-09-34 | The hosts MUST emit: payments ended by outcome, scheme, tenant and engine (`th.payments`), with their lines and duration; activity attempt latency and errors; attempts in flight per queue; holds raised and open; store write latency and errors; releases stored and activations; Temporal's SDK metrics; and the Temporal server's own, its persistence metrics among them. Attribute names are lower case and dotted and the same in every host. They carry ids, document names and outcomes only. | `TelemetryTests` (names and dimensions); `TelemetryTests` ("the local stack ... dashboards read only metrics the hosts emit") |
| R-09-35 | No personal data MAY reach an exporter. A value whose key names personal data (a name, an account, an IBAN, an email, a phone, an identity number) MUST be masked, and IBANs, emails and runs of eight or more digits MUST be masked in every attribute, status, event, log message and exception, before any exporter sees them. | `TelemetryTests` ("Masking replaces ..."; a WPS run's exported spans, metrics and logs hold no value of its party records) |
| R-09-36 | Every long-running runtime host MUST answer `GET /health/live` and `GET /health/ready`, without authentication whatever else the host requires. Ready means what it depends on answers (the release API: its store). The answer names no server and no error text. A deployed worker answers the same. | `TelemetryTests` ("The release API answers live and ready without a token while every other endpoint is 401 ...") |
| R-09-37 | The local monitoring stack (`runtime/observability/`) MUST pin every image by tag and digest and MUST ship the dashboards Payments, Engines, Ports and Store, reading only metrics the hosts emit. It is for development: an installation brings its own collector and backends. | `TelemetryTests` ("The local stack pins every image ...") |
| R-09-38 | The runtime MUST have a repeatable load harness outside the studio (`runtime/load/`). It drives synthetic WPS salary files through a deployment on Temporal, on each Temporal persistence store and each store database, and reports throughput, latency percentiles, errors and the databases' CPU and IO. Its results are recorded in `docs/runtime/LOAD-TEST.md`, not committed as files. The targets it is held to are I-68's. | `runtime/load/matrix.sh`; `docs/runtime/LOAD-TEST.md` |
| R-09-39 | Each runtime MUST have a service console for service engineers (section 9.12), behind the runtime's authentication (R-06-25). It MUST show the health of the engine hosts and servers, the store and the read model (with its lag), each port's cheap probe, the workers and their versions, the signing keys and certificates with their expiry, and disk: the state now and a short history, with links to the dashboards. | `ServiceApiTests`; `service-console` Playwright test |
| R-09-40 | The service console MUST list the signed versions the package source offers, of every kind (workflow packages, distributions, configuration releases), read-only; MUST verify a version against the trusted signers before it is stored and refuse one that is unsigned, changed or signed by a key not trusted; MUST show the difference against what is active (artefacts, versions, parameters); and MUST install, activate from a date (the previous release then draining), roll back, and remove. Removal MUST be refused while the release is active or any payment pinned to it is open; the open count per release MUST be shown. A distribution's open payments are those pinned to it with no end fact (R-09-54). Each action MUST be a fact with who, when and why. | `ContentTests`, `DistributionContentTests`; `service-console` Playwright test |
| R-09-41 | The service console MUST export a signed configuration backup (releases' ids, activations, configuration values, bindings with `secretRef` only, the role map, the trusted signers) and verify it; a restore MUST verify it, MUST offer a dry run, and MUST be refused into a runtime that is neither empty nor a test environment. A backup MUST NOT hold a secret value. | `BackupTests` |
| R-09-42 | The service console MUST show the runtime's structured logs and the store's facts, masked (R-09-35), filtered by error code, leg, scheme, tenant, payment and time, each linked to the payment in the operations console and each error to its dictionary entry. | `ServiceApiTests` |
| R-09-43 | A troubleshooting action (section 5.11) MUST need a person's confirmation with a reason, MUST be a fact with who, when and why, and a risky one MUST be carried out only when a ServiceApprover approves it. An action whose engine path is not built MUST be recorded and refused, naming the issue (Issue I-101). | `ActionTests` |
| R-09-44 | The service console's assistant MUST be switchable off (`TH_ASSISTANT_ENABLED`) and MUST mask personal data before anything leaves the runtime (names, accounts, IBANs, employee ids). It MUST use read-only tools only, MUST cite its sources, and MAY propose a troubleshooting action with the runbook it relies on, which only a person can confirm in the console. | `AssistantTests` (a fake model: no party value leaves) |
| R-09-45 | An engine binding MAY set `defaults.forEachConcurrency`. A `forEach` that declares no `concurrency`, and whose steps are all calls of activities that hold no state (effect `pure` or `read`, not a hold) that handle an outcome only by ending, then runs that many items side by side, chunk by chunk where the batch is chunked (R-09-13). Its trace is the one item by item gives (R-05-53). | Conformance kit on DTFx, Elsa and Temporal with the binding's concurrency (CK-FOREACH-01, CK-FOREACH-03); `e2e-wps-temporal` |
| R-09-46 | The claim check and the record of each step's outcome MUST keep a payload's facts in order without holding up another payload's: a lock per payload, never one per host. They MAY write the facts that queue for one payload in one save, each chained to the one before, so that every chain stays numbered without gaps and linked (`FactLog.Verify`). | `ClaimCheckTests` ("many payloads and many items side by side", on SQL Server and PostgreSQL); the store report of every deploy |
| R-09-47 | An engine binding MUST carry every amount in the context as the text it received and MUST NOT compute on it: it parses JSON numbers without binary rounding (.NET `JsonNode`, Java `BigDecimal` with exact decimals) and passes decimal strings through. Rules run in the one rule service (R-04-21). | Conformance CK-MONEY-01 on Temporal, DTFx, Elsa and Flowable (the `amounts` check) |
| R-09-48 | The studio MUST build a bank's distribution for one engine from published releases only: the platform packs (`p` and the cluster platform of each licensed scheme), the scheme packs the bank licenses (R-11-37), the bank's own pack and the engine binding. Its `manifest.yaml` MUST carry its id (`YYYY.MM.DD-n`), bank, engine, DSL version, the runtime range it needs, the bill of materials (every pack release, and every artefact release with its path and SHA-256), the parameter contract (R-11-39), the slot contracts its schemes fill (R-11-38), the longest wait it declares and the change sets it came from. It MUST carry the error dictionaries, the operations views, every platform workflow in its expanded form with the legs of its slots named (`workflows/slots.yaml`), and each platform workflow's engine package (`engine/<workflow>/`, itself signed). It MUST NOT carry a value, a secret, or another bank's content; it carries simulators only when built for a test environment, and is then not promotable. | `studio/test/distribution.test.ts` ("holds the platform, the schemes the bank licenses ...") |
| R-09-49 | A distribution MUST be signed with the statement of R-09-23, kind `distribution`, release id `dist/<bank>@<id>`, tenant the bank, carrying its bill of materials and parameter contract as signed fields, and published to the content branch under `releases/distributions/<bank>/<id>/` with an annotated tag `dist/<bank>/<id>`, never moved (R-09-21). The runtime MUST verify it, its kind and that its signed bill of materials and parameter contract are its manifest's before it stores it, and again before it activates it. | `studio/test/distribution.test.ts` ("is signed as a distribution"); `DistributionApiTests` (on SQL Server and PostgreSQL); `e2e-distributions.test.ts` scenario 11 |
| R-09-50 | The runtime MUST store distributions and configuration releases insert-only: stored again with the same content, nothing changes; the same id with other content is refused (409). No endpoint updates or deletes one. | `DistributionApiTests.A_distribution_is_verified_and_stored_once_...` |
| R-09-51 | A configuration release MUST carry the bank's configuration set (dial and rule values, each with its `validFrom`), its port bindings (adapter and settings, each with its `validFrom`, credentials as `secretRef` only), its calendars and `requiresContract` (each distribution's parameter contract it was checked against, by SHA-256), signed with kind `configuration` and release id `config/<bank>@<release>`, under `releases/configuration/<bank>/<release>/` with the tag `config/<bank>/<release>`. On install the runtime MUST check every value against the parameter contract of every distribution of the bank not removed: a value no distribution declares, or out of the bounds of one that declares it, is refused; a distribution that does not declare a dial ignores its value. Each value, binding and calendar is then an effective-dated version. | `DistributionApiTests.A_configuration_release_is_checked_against_every_distribution_not_removed`; `studio/test/distribution.test.ts` ("a configuration release"); `e2e-distributions.test.ts` scenarios 1, 2 and 7 |
| R-09-52 | A distribution MUST be activated per bank and environment. Activation is the switch-over: the distribution becomes active and the one active until then draining in the same transaction. A distribution that carries simulators MAY be activated only in a test environment. | `DistributionApiTests.Switch_over_drains_...`; `e2e-distributions.test.ts` scenarios 3 to 5 |
| R-09-53 | At acceptance the runtime MUST pin a payment to the distribution active for its bank and environment then, as a `distribution.pinned` fact on the payment's chain, and refuse the payment when none is active. The pin never changes; the engines record the distribution as the payment's release in the store. | `DistributionApiTests`; `e2e-distributions.test.ts` (every scenario's payments) |
| R-09-54 | Every engine host MUST record a payment's end (`payment.end`, its outcome) when its root instance ends (Issue I-97). A payment is open while it is pinned and has no end; the open count per distribution is computed from these facts. | `DistributionApiTests`; `e2e-distributions.test.ts` (each run ends the payment) |
| R-09-55 | Every leg, child, activity and rule of a payment MUST run from the distribution it is pinned to, loaded side by side with the others: on Temporal a build id and task queues per distribution, on DTFx an orchestration name carrying it, on Elsa a workflow definition per distribution, on Flowable one deployment per distribution. A message for a running payment goes to its pinned distribution. | `e2e-distributions.test.ts` (the engine's `distribution` in every run, on Temporal, DTFx, Elsa and Flowable) |
| R-09-56 | A distribution's states MUST be insert-only facts (`distribution.state`: installed, active, draining, drained, removed) with who, when and why. Removal MUST be refused while it is active, while any payment pinned to it is open, and before its longest declared wait has passed since it stopped taking payments and since its last payment ended. The package stays in the store and on GitHub. | `DistributionApiTests.Switch_over_drains_...`; `e2e-distributions.test.ts` ("removal") |
| R-09-57 | Rollback MUST activate the distribution active before the current one; a removed one is installed again first. Payments that started on the newer one finish on it. | `DistributionApiTests`; `e2e-distributions.test.ts` scenario 8 |
| R-09-58 | The runtime MUST keep an intake switch per bank and scheme, as insert-only facts (`intake paused` or `resumed`), set only by the service console's `pause-intake` and `resume-intake`. While it is paused, the runtime MUST refuse to accept a new payment of that scheme for that bank (HTTP 423, code `ops.IntakePaused`) and MUST NOT pin it; a payment already accepted runs on. The front door MUST name the payment's scheme when it asks, and MAY keep a refused payment and offer it again until intake is resumed. | `ActionTests` ("intake per scheme"); `service-console` Playwright test |
| R-09-59 | A deployed worker MUST answer `POST /drain` by taking no new attempt (each is answered 503 and the engine's router offers it again) while those in flight run to their end, and `POST /restart` by draining and then exiting with code 75 when nothing is in flight; the deploy runner MUST then start it again from the same package, so at the same version, on the same port. `GET /health` MUST give the worker's version, process, whether it drains and its attempts in flight. | `ActionTests` ("drain and restart a worker"); `service-console` Playwright test |

## 9.6 Core and extension profiles

The core is the constructs of Part 5, section 5.3. An extension is anything a single target offers beyond them. Extensions are flagged by lint L24, are never used in `p` scope, and are excluded from the conformance kit.

## 9.7 Known limits

- The Flowable column is the least certain. Build spec C22 starts with a spike that proves timer boundaries, retry and compensation on generated BPMN.
- Whether an instant rail runs every step as a durable task is open (D9). The `synchronous` marker lets the binding choose later.


### Proved on Flowable 7.1.0 (C22-S1)

The kit runs on an embedded Flowable 7.1.0 engine with the generated BPMN (`studio/engines/flowable/`). These patterns pass it.

| Construct | Pattern | Why |
| --- | --- | --- |
| Retry and attempt timeout | A modelled loop in a sub-process per call; the attempt timeout is a timer boundary on the external worker task; the total timeout a timer boundary on the sub-process | An attempt timeout ends the task before any failed-job retry, and a spent `failedJobRetryTimeCycle` ends in a dead-letter job, not the `unknown` outcome |
| Await | An event-based gateway between the message catch and a timer; the binding holds a message until a catch is waiting | Flowable delivers a message only to a catch that is already waiting (R-09-16) |
| For each | A multi-instance sub-process with outcomes local to the item; `onItemFailure: continue` is an error event sub-process inside it; `concurrency: n` runs in chunks of n | A parallel multi-instance of 10,000 items in one transaction is quadratic inside the engine |
| Compensation | Compensation boundary events and handler sub-processes; the flow waits on a message the binding sends when no handler is left | Flowable 7.1.0 does not wait for compensation handlers to finish |

Two engine gaps in Flowable 7.1.0, worked around in the binding:

| Gap | Work-around |
| --- | --- |
| Compensation handlers are not awaited (`waitForCompletion` is not implemented) | The binding signals completion when no handler is left |
| An interrupting event sub-process loses the boundary events of the main sub-process | Cancel ends main with a mark that a gateway after main reads, instead of throwing an end error |

Limits that remain: compensation runs in reverse document order, which in parallel branches is not always reverse completion order; chunked concurrency puts a barrier between chunks; external worker tasks are non-exclusive, as exclusive jobs were not acquired while parallel branches waited.

### Proved on Elsa 3.10.2 (I-30)

The kit runs on Elsa Workflows 3.10.2 (`studio/engines/elsa/`), with the same interpreter as DTFx (`studio/engines/shared/`). 55 of 55 scenarios pass, and two runs give the same traces.

| Who does what | |
| --- | --- |
| Elsa | Each engine instance is an Elsa workflow instance of one generic activity. Elsa keeps its state, suspends it at a bookmark while it waits, and resumes it with input. Its own serialiser writes the state the host keeps between runs |
| The interpreter | Runs again from the start on each resume, against the journal in the workflow state: an operation whose result is in the journal returns it; one that is not becomes pending |
| The host | Carries out pending operations (an activity attempt on the worker of its queue, a timer, a child instance, an event to another instance) on the virtual clock, and resumes the bookmark with each result |

Limits that remain:

- Elsa does not replay code, so the replay is the interpreter's own. Each resume reruns the journal; large batches stay small by the binding's chunk size and continue-as-new (CK-FOREACH-02: 10,000 items, 21 executions).
- Timers and retries are journalled operations run by the host's clock, not Elsa's scheduler or its activities, so that the kit can run on virtual time (R-C-06). A production host would hand timers to Elsa's scheduler.
- The state is kept by the host in memory between runs. A production host would use an Elsa persistence provider.
- Workflows are not shown in Elsa's designer: the documents stay the source, as for every target.

## 9.8 Examples

- Four bindings, each at its releases: `examples/bindings/p.binding.<target>.v1@<release>.yaml` (Issue I-83).
- Pins as they appear in a manifest: `factory/p/pack.yaml`.

## 9.9 The signature of a released package

Status: built 22 November 2026 (Issue I-67, owner's decision of 2026-11-22).

| Part | What |
| --- | --- |
| `release.json` | The statement, canonical JSON: `format` (`th-release-statement/1`), `kind` (`workflow-package` today; `distribution` and `configuration` for the per-bank packages of `docs/runtime/PACKAGING-AND-DEPLOYMENT.md`, which add their bill of materials and parameter contract as further signed fields), `releaseId`, `workflow`, `release`, `target`, `tenant` (null for shared content), `changeSet`, `keyId`, `signedAt`, and `files`: every other file of the package with its SHA-256, in path order |
| `release.sig` | ECDSA P-256 with SHA-256 over the bytes of `release.json`, DER encoded, in Base64 |
| Key id | The first 32 hex digits of the SHA-256 of the public key's SubjectPublicKeyInfo (DER) |
| Trusted signers | `{ "kind": "trusted-signers", "keys": [{ "keyId", "publicKey", "retiredAt"? }], "revoked": [{ "keyId", "revokedAt", "reason"? }] }` |

Why this format: ECDSA P-256 is built into .NET and Node.js, so neither side needs a cryptography package; it is a FIPS 186 curve a bank's key store (an HSM, Azure Key Vault, HashiCorp Vault transit) can hold and sign with; and a detached blob signature over one statement file is what standard tools check: `openssl dgst -sha256 -verify signer.pem -signature <(base64 -d release.sig) release.json`, or `cosign verify-blob --key signer.pem --signature release.sig release.json`. Ed25519 would need a package in .NET 10. A full Sigstore bundle (a transparency log, keyless certificates) needs services an installation may not reach; it can be added beside this later. An ECDSA signature differs each time it is made, so the runtime hashes a release without its signature: the same files signed again are the same release.

## 9.10 Who may call the runtime

The studio and the runtime have separate owners and separate identity providers. The studio is the vendor's, with its own sign-in. A runtime installation is the bank's (R-09-29):

| | The studio | The runtime |
| --- | --- | --- |
| Identity provider | The studio's own | The bank's (any OIDC issuer), configured by the bank |
| Identities kept | Its users and roles (`governance/users.yaml`) | None: it validates the bank's bearer JWTs and keeps no users, passwords or tokens |
| Roles | Authoring roles, maker-checker | The release API's roles (R-09-27), and an operations console's (operator, supervisor) through the same library, by the bank's role map |
| Machine callers | | Client-credentials tokens from the bank's provider (the pipeline, the workers reading values) |
| The link between them | Signs every released package | Trusts the studio's package-signing public keys the bank configures (`TH_TRUSTED_SIGNERS`), and nothing else of the studio |

## 9.11 GitHub as the artefact repository

The content branch is the record of what was released (R-09-20). Each published workflow release is also a version a bank's runtime can take from it (R-09-30):

| On GitHub | What |
| --- | --- |
| `content-configurations/releases/<scope>/<workflow>/<release>/<target>/` | The package's files, `release.json`, `release.sig` and `package.sha256`. Written once (R-09-21) |
| Tag `release/<workflow>/<release>/<target>` | Annotated, on the commit that published it; its message names the package folder, the change set, the signing key and the statement's hash. Created once, never moved |

A runtime installation reads the repository with a read-only credential (a fine-grained token with contents read, or a GitHub App installation token), by secret reference (`PackageSource:GitHub:TokenRef`). `GET /sources/versions` lists the tags and which are stored; `POST /sources/install { tag }` downloads one version's folder at its tagged commit and stores it through the same verification as an upload, recording `release.verified` with purpose `package-source:<tag>`. Activation stays a separate, verified step. A service console in the bank's runtime calls these two endpoints. The source also lists a bank's distribution tags (`dist/<bank>/<id>`, folder `releases/distributions/<bank>/<id>/`) and configuration release tags (`config/<bank>/<release>`, folder `releases/configuration/<bank>/<release>/`) by kind, and installs each through the verification of its kind (section 9.13).

## 9.13 Distributions and configuration releases

Status: built 22 November 2026 (`docs/runtime/PACKAGING-AND-DEPLOYMENT.md`, section 10). The deployment unit is a bank's distribution; values travel separately in configuration releases. R-09-48 to R-09-57 and R-11-37 to R-11-40.

| Endpoint (release API) | Role | What |
| --- | --- | --- |
| `POST /distributions`, `POST /configuration-releases` | release-importer | A zip, verified by its kind and stored insert-only |
| `POST /sources/install { tag }` | release-importer | A `dist/` or `config/` tag from the package source, verified and stored the same way |
| `GET /distributions?bank&environment`, `GET /distributions/active`, `GET /distributions/package` | release-reader | States, open payments, the package as stored |
| `POST /distributions/activate`, `/drain`, `/remove`, `/rollback` | release-activator | The state changes, each a fact |
| `POST /payments/pin`, `POST /payments/end` | payment-intake | The front door: the pin at acceptance, the end |
| `GET /bindings?tenant&port&at` | values-reader | The port binding in force at a moment |

## 9.12 The service console

The service engineers' console is a section of the runtime's web app (`runtime/ops-console`, the same shell as the operations console) with its own API host (`runtime/src/TransactHub.Service.Api`, routes under `/service/api`), behind `TransactHub.Auth` with the roles ServiceEngineer and ServiceApprover (R-06-25). It has seven pages: Health (R-09-39), Content (R-09-40), Backup (R-09-41), Logs (R-09-42), Errors, the dictionary and its runbooks (R-05-52), Actions (R-09-43) and the Assistant (R-09-44).

| Content state | Derived from |
| --- | --- |
| Offered | A tag of the package source that is not stored |
| Installed | Stored and verified, never active |
| Active | The activation in force for its scope and environment |
| Draining | It was active, another activation superseded it, payments pinned to it are open |
| Drained | Superseded and none of its payments open |
| Removed | A `release.removed` fact; the package stays in the store and on GitHub as a record |

The risky actions, which need a ServiceApprover: activate, roll back and remove a release, and the troubleshooting actions `release-hold`, `expire-hold`, `redrive-workflow`, `reset-activity`, `resend-file`, `pause-intake`, `drain-worker`, `restart-worker`, `rebuild-read-model` and `rotate-trusted-key`. Installing, verifying, a backup and a restore (confined to an empty or a test runtime) need a person's confirmation and a reason only.

