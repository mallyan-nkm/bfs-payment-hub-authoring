# Platform parity: the original platform's capabilities in the new runtime

Status: 23 November 2026. Each capability of the original TransactHub platform (its services, and the screens of its three web apps: banking operations, the integration and payments tools, and service operations), and where it is in the new runtime and the studio. The original was read only to list what it could do; nothing of its data is here. `docs/runtime/PARITY.md` is the WPS activity matrix, activity by activity; this page is the platform as a whole.

| State | Means |
| --- | --- |
| Built | In the code here, with its tests. A built row may still name an issue for a question it leaves |
| Partly | Some of it is built; the issue says what is not |
| Deferred by the owner | Not built, by the owner's decision |
| Waiting on others | Built as far as it can be; the rest needs a bank, the scheme or another party to answer |
| No decision | Not built, and no decision yet |

## Payments and schemes

| Capability of the original | In the new runtime | State | Issue |
| --- | --- | --- | --- |
| WPS outward: salary file intake, checks, SIF to the Central Bank, ACK and NAK | `uae.wps` leg on `p.wf.corporate.v1`; the source's rules ported and proved by a golden corpus; File API intake | Built; the scheme pack is provisional until the rulebook confirms it | C25-S1 (D10) |
| WPS inward: PIF, PSI, PRC, DIF, RFA and returns | `p.wf.inward.v2` with the WPS legs, which hand on their returns (R-09-87); the source's parsers and builders as native code-behinds | Built; the PSI is read by no leg | I-84 |
| WPS returns to the employer's bank (RTC) | `uae.wps.wf.return-in.v1` | Built; the layout is provisional | I-84 |
| WPS refunds: RRI, DCR, RRR, the maker and checker screens | Refund orders on `p.wf.refund.v1` and `p.wf.refund-out.v1`, WPS as the leg, the approval as a hold | Partly | I-134, I-146 |
| DE: a bank's bulk posting file to its core banking | The `file-bulk` adapter of the core-banking port (R-12-15) | Built; one file per posting batch | I-132 |
| ADIB's WPS values (routing code, CBX values, GL names) | ADIB's bank folder; the acknowledgement wait is set | Waiting on others (the bank) | I-116 |
| Labour ministry establishment status | `uae.wps.port.labour-ministry.v1`, adapters `gateway-rest` and `simulator` (R-12-16) | Waiting on others (each bank's endpoint, the scheme's code) | I-118 |
| Corporate registration and its match to the salary file | Registers (R-05-62 to R-05-64) and `p.enrich.party-register.v1` with WPS's match (R-12-27) | Built; the register API answers core banking unmasked by default | I-181 |
| IPI, FTS, NPSS, CBFT and EDDA | Not started: later, on the same store and lifecycle | Deferred by the owner | Plan D6 |
| Routing of a payment to its workflow | The scheme's dispatch table and `p.table.workflow-select.v1`, in the front door (R-09-85) | Built | |
| Message conversion | Maps over the ISO 20022 canonical context, authored and tested in the studio | Built | |
| Rails and bank policies | The scheme manifest's rails, enabled in the bank's configuration set; dials with defaults and bounds | Built | |
| Rule engine | The studio's rule service, with NRules semantics | Built; whether NRules is the evaluator is open | D13 (`docs/adr/0001-studio-stack.md`) |
| Charges and VAT | `p.enrich.charges.v1` on the bank's slabs, posted by `p.post.accounting.v2`, native code-behinds (R-12-25) | Waiting on others (DIB's slabs) | I-58 |
| GL mapping (settlement, suspense, charge income, VAT accounts) | Parameters of the accounting template (R-12-12) | Waiting on others (DIB's accounts) | I-58, I-184 (1) |
| Exchange rates | A bank's rates as a dial of `p.enrich.fx-rate.v1` | Partly: no feed refreshes them | I-189 (4) |
| Posting windows | The posting gate and the schedule-gated await | Built | |
| Business calendar and holds by business day | Reference table `uae.p.ref.business-calendar.v1`, the calendar port's `reference` adapter, hold expiry by business day (R-09-88) | Partly: an await still counts Saturday and Sunday only; the holidays that follow the moon are added each year | I-166, I-151 (3) |

## Connections to other systems

| Capability of the original | In the new runtime | State | Issue |
| --- | --- | --- | --- |
| Core banking: balance, posting, reversal, verification | `p.port.core-banking.v1`, adapters `simulator`, `gateway-rest`, `file-bulk` | Waiting on others (ADIB's, ENBD's and FAB's settings; ADIB's WPS values) | I-85, I-116 |
| SFTP gateway to the Central Bank, the ministry and core banking | The `sftp` adapter in the File API, host keys pinned by reference (R-09-86) | Waiting on others (the partners' host keys) | I-161 |
| File encryption and decryption | `p.protect.decrypt-file.v1` and the encryption port, adapters `openpgp` and `gnupg` (R-09-83, R-12-17) | Built | |
| File intake and delivery, resend | The File API, its own process (R-09-82 to R-09-85) | Built; the scheme declares no file rules or inbox messages yet | I-159, I-160 |
| Sanctions and fraud screening | `gateway-rest` adapters of `p.port.sanctions.v1` and `p.port.fraud.v1` (R-09-90) | Waiting on others (each bank's screening engine) | I-163 |
| Notifications to the channel by e-mail and SMS | `email` and `sms` adapters of `p.port.channel.v1` (R-09-90) | Waiting on others (each bank's relay and gateway) | I-163 |
| Outward events (webhooks) | A signed `webhook` adapter of `p.port.facts-out.v1` (R-09-90) | Waiting on others (each bank's endpoint) | I-163 |
| Participant directory and beneficiary banks | Reference table `uae.p.ref.participant-directory.v1` (the Central Bank's register, 63 banks) and its adapter | Waiting on others (the routing codes) | I-151 |
| Reference data (emirates, free zones, purpose codes, branches) | Reference tables in the studio, editions by date, served at `GET /lookups` (R-01-25, R-09-75) | Built; the free zone codes are to confirm | I-151 (4) |
| A bank's own calendar and cut-off times | `b.ref.bank-calendar.v1` and `b.ref.cut-offs.v1`, edited in the studio's Bank page (R-11-52); `p.decide.cut-off.v1` after the WPS salary file's business date (R-12-28), the bank's closed days in the business calendar (R-09-102), the applied cut-off in the operations console (R-05-72) | Yes, on the studio interpreter and the four engines; where it applies and what moves is the recommendation of I-191 | I-186, I-191 |
| Channel enquiries: payment and file status, search | `/api/channel/...`, role `channel-enquiry`, pain.002 words (R-09-74) | Built | I-153 |
| Channel enquiries of core banking: balance, accounts, charge preview | `/api/channel/accounts/balance`, `/accounts`, `/charges/preview` (R-09-100) | Waiting on others (the port contract's operations; the bank's GL accounts) | I-184 |
| Register API for core banking | `/api/registers/{kind}`, role `register-client` (R-09-73, R-09-101) | Waiting on others (each bank's choice of masked reads) | I-181 |
| Integration controls: dead letters | Requeue of a dead-lettered step from the console (R-09-91) | Built | I-167 |
| Integration controls: circuit breakers, Kubernetes portal | A circuit breaker per outside system in every production adapter, an open one in the service console's health (R-09-155, R-09-156); health probes per bound host; drain and restart of a worker | Built for the breakers; the portal dropped | I-189 (5), I-255, I-128 |

## Operations

| Capability of the original | In the new runtime | State | Issue |
| --- | --- | --- | --- |
| Payments list, detail, live monitor | The operations console, leg-generic from the operations views (R-05-48 to R-05-50) | Built | |
| Payment queue and approval queue | The work queue: assign, escalate, release, bulk actions, saved filters, live (R-05-71) | Built; no teams, no escalation by time | I-185 |
| Exceptions: holds, repair, bulk resolve | Holds as engine awaits (R-05-56), parked steps and retry (R-05-55), bulk actions | Built | I-125 |
| Masked personal data, and unmasking | Masks declared per field (L38); unmask on a bank approver's consent, for the bank's window (R-05-69) | Partly: a leg's and a register record's fields are not unmasked | I-181 |
| Turnaround and service levels | Turnaround limit per view in business days, breaches listed and alerted through `p.port.notification.v1` (R-05-68, R-05-80, R-05-81) | Built | I-182, I-143 |
| Reconciliation | Scheme reports reconciled on arrival, the break queue (R-09-92) | Partly | I-164, I-168 |
| Dashboard, reports and self-service reporting | Reports, dashboard and CSV export from the read model (R-05-59, R-05-65) | Built | I-144 |
| Fixed WPS reports (SIF summary, SIF detail, upload history) | Fixed reports the WPS view declares (R-05-67) | Partly | I-183 |
| Regulatory and scheduled reports | None | Deferred by the owner | I-188 |
| Retention and erasure | Tombstones on a bank approver's consent, legal holds (R-05-70) | Partly: the store keeps the values | I-180 |
| Audit log and admin activity log | Hash-chained facts on every chain; the console's own chain (`GET /ops/api/audit`) and the service console's, each on an Audit page: newest first, filtered by person, action and date, each row with its hash, and whether the chain verifies (R-05-75, R-05-76); the studio's audit | Built | I-202 |
| Customer 360 and counterparty 360 | None | No decision | I-189 (1) |
| Fraud alerts and cases | Screening only | No decision | I-189 (2) |
| Compliance cases, sanctions case review, suspicious activity reports | None | Deferred by the owner | I-188 |
| Treasury: nostro, FX quotes, liquidity, funding, exposure | None (`p.port.liquidity.v1` has no adapter) | Deferred by the owner | I-188 |
| Incidents, shift handover, workflow replay | None (the studio's debugger replays a run at design time) | Deferred by the owner | I-188 |
| A person's workspace and notification centre | None | No decision | I-189 (3) |

## Service operations and governance

| Capability of the original | In the new runtime | State | Issue |
| --- | --- | --- | --- |
| Installation and upgrades of content | The service console: install from GitHub, verify, activate, drain, remove, roll back distributions and configuration releases (R-09-39 to R-09-44) | Built | |
| Consent for changes of the system | A bank approver, another person, consents to every change of the system (R-09-77) | Built | I-155 |
| One bank per installation | `TH_BANK`; another bank's content refused at verify, install and activate (R-09-76) | Built | I-154 |
| Troubleshooting guides and the error dictionary | The error dictionary with a runbook per code (R-05-51, R-05-52) | Built | I-126 |
| Health and monitoring | Health with a short history, probes, OpenTelemetry, dashboards and alert rules (R-09-31 to R-09-37, R-09-72) | Built; two targets have no number | I-143 |
| Backup and restore | Signed configuration backups and restore with consent | Built | |
| The AI agent for service engineers | The service console's assistant: masked, read-only, off by default | Waiting on others (each bank's data protection officer) | I-104 |
| The AI workflow builder | The studio's assistant behind the same gates as a designer | Built; no live model run here | |
| Users, roles, departments, unlocking a user | The bank's identity provider and role map; the runtime keeps no users (R-06-24, R-06-28) | Built | |
| API gateway | Each host authenticates its callers itself (`TransactHub.Auth`); a gateway and TLS termination are the installation's | No decision | I-68 |
| Workflow engine | Four engines behind one interpreter, proved by the conformance kit; long-running engine hosts (R-09-66 to R-09-68, R-09-93 to R-09-95) | Partly: DTFx and Elsa runs do not survive a restart of their host | I-165 |
| Temporal on SQL Server | The SQL Server plugin (`runtime/temporal-mssql`), the default engine (R-09-79) | Built; we keep the plugin | I-42 |
| Workflow authoring, versions and releases | The studio: authoring, tests, maker-checker, signed distributions on GitHub | Built | |
| Testers' tools: WPS tester, payment tester, bank simulator, sandbox | The studio's test bench and simulators; the runtime's test bench (`runtime/testbench`) | Built | |
| Data seeding | `npm run studio -- seed-content` | Built | |

## The issues that wait on the owner or on others

| Issue | Waits on | What is asked |
| --- | --- | --- |
| [I-58](../ISSUES.md#i-58-a-banks-accounts-are-not-in-the-source) | DIB | Its WPS settlement, suspense, charge income and VAT accounts and its charge slabs |
| [I-85](../ISSUES.md#i-85-adibs-enbds-and-fabs-core-banking-settings-are-not-in-the-source) | ADIB, ENBD, FAB | Their core-banking gateway settings |
| [I-116](../ISSUES.md#i-116-adibs-wps-values-the-source-does-not-hold-or-holds-outside-the-bounds) | ADIB | Its WPS routing code, CBX values, and whether the four general-ledger names are its accounts |
| [I-104](../ISSUES.md#i-104-the-assistant-sends-masked-data-to-the-model-provider) | Each bank's data protection officer | Approval of the service console's assistant before it is switched on |
| [I-134](../ISSUES.md#i-134-what-the-source-and-the-scheme-leave-open-in-the-refund-cycle) | The scheme and the owner | Per line or per file; the reason codes and their ISO codes; regeneration and resend of a refused DCR or RRR |
| [I-151](../ISSUES.md#i-151-what-the-reference-tables-still-lack) | The Central Bank or the scheme operator; DIB's and ADIB's operations | The participants' routing codes; each year's holidays; the free zone codes |
| [I-161](../ISSUES.md#i-161-the-banks-sftp-partners-have-no-pinned-host-key-and-the-logins-form) | Each bank | Its SFTP partners' host key fingerprints and logins |
| [I-163](../ISSUES.md#i-163-the-production-adapters-counterparts-are-the-banks-and-the-source-names-none) | Each bank | Its screening engines', notice gateways' and event endpoint's interfaces and hosts |
| [I-181](../ISSUES.md#i-181-unmasking-and-the-register-apis-answer-to-core-banking-i-149-1-and-2) | Each bank's security officer | Whether core banking reads the registers masked, and the unmask window |
| [I-184](../ISSUES.md#i-184-the-channels-enquiries-of-core-banking-what-the-port-contract-does-not-say) | The owner and each bank | Balance, accounts and charges in the core-banking port; whether the charge preview is the platform's own pricing; whether the channel needs the GL accounts |
| [I-188](../ISSUES.md#i-188-the-original-platforms-capabilities-the-owner-deferred) | The owner | When to take up treasury, compliance cases, incidents and regulatory or scheduled reports |
| [I-189](../ISSUES.md#i-189-capabilities-of-the-original-platform-with-no-home-yet) | The owner | Whether each capability with no home is needed, deferred or dropped |
