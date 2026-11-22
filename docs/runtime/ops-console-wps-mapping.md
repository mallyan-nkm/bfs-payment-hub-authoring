# The source's WPS operations screens, field by field, in the operations console

The source (`TransactHub-src.zip`, its `frontend/` and `PaymentHub-UI/`) showed WPS files on scheme-specific screens. The runtime's operations console is leg-generic: it shows what the packs' operations views declare (`spec/factory/p/views/p.view.operations.v1.yaml`, `spec/factory/uae.wps/views/uae.wps.view.operations.v1.yaml`, R-05-48). This table lists every field those screens showed and where it is now. `OperationsViewTests` checks that every field marked **mapped** is declared in the view named, so this table and the views cannot drift apart.

Status:

- **mapped**: the view declares it and the read model fills it from the store's facts.
- **mapped, empty**: the view declares it, but nothing writes its source on the runtime yet (Issue I-97); it shows `—` until something does.
- **not mapped**: the new model has no source for it (Issue I-100), or it is deliberately not carried.

Screens inventoried: Banking Ops `features/reports/wps` (SIF Summary report, SIF Detail report, SIF Summary 360 drawer with File Summary, Integration 360 and Record List, the audit trail drawer), Banking Ops `features/wps` (Upload History, File Detail), the admin portal's `views/reports/wps/sif-summary` (the same report in Angular), and PaymentHub-UI's WPS dashboard.

## SIF Summary report (Banking Ops and admin portal)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| SL No | mapped | (list row) | the list's position | Every list has it; not a field |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | The inbound SIF is not kept in `message` on a deployment yet (I-97) |
| Transaction ID | mapped | uae.wps:subject:transactionId | `subject.id` | The bulk's id in the store; searchable with `q` |
| Corporate Name | mapped, empty | uae.wps:subject:corporateName | `bulk.debtorName` | The SIF carries no employer name; the channel's upload did (I-100) |
| Account Number | mapped, empty | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | The SIF carries no debit account; the channel's upload did (I-100) |
| MOL Establishment ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | Masked, as Upload History masked it |
| Upload Date | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | Also the list's date filter |
| Total Records | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | Range filter |
| Total Amount | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | Range filter, and the list's amount filter |
| Status | mapped | p:subject:status | `subject.status` | The ISO status the workflow state carries; the leg's outcome beside it |
| Uploaded By | not mapped | | | The channel user is not in the payment model and is personal data (I-100) |
| Actions | mapped | (offers) | the views' `actions` | Approve, decline, release, reject, cancel, re-screen, retry, resend |

## SIF Detail report (one row per salary line)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | I-97 |
| SIF File Name | mapped | uae.wps:subject:sifFileId | `bulk.messageId` | The file id the SCR names |
| Employer Unique ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | |
| Corporate Name | mapped, empty | uae.wps:subject:corporateName | `bulk.debtorName` | I-100 |
| Debit Account Number | mapped, empty | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | I-100 |
| Routing Code | mapped | uae.wps:subject:employerRoutingCode | `bulk.debtorAgent.memberId` | |
| Salary Month | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | MMYYYY, as the SCR has it |
| Salary Year | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | The same field: month and year together |
| File Upload DateTime | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | |
| Channel | not mapped | | | The header carries no channel (I-100) |
| ACK/NAK Status | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:cbResponse | `step:submit.read-ack.outcome` | Per family: the SIF's here; PRC, DIF and RFA on the agent leg |
| ACK/NAK Received DateTime | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:ackReceivedAt | `step:submit.read-ack.at` | |
| Total Record Count | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | |
| Total Salary Amount | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | |
| Current Status | mapped | p:subject:status | `subject.status` | |
| Employee Unique ID | mapped | uae.wps:lines:employeeId | `line.ext.uae.wps.personId`, masked | |
| Credit Account Number | mapped | uae.wps:lines:creditAccount | `line.creditorAccount.iban`, masked | |
| Pay Start Date | mapped | uae.wps:lines:payStartDate | `line.ext.uae.wps.payStartDate` | |
| Pay End Date | mapped | uae.wps:lines:payEndDate | `line.ext.uae.wps.payEndDate` | |
| No Of Days | mapped | uae.wps:lines:daysInPeriod | `line.ext.uae.wps.daysInPeriod` | |
| Fixed Amount | mapped | uae.wps:lines:fixedPay | `line.ext.uae.wps.fixedPay` | Shown whole, as the old screens did (`mask: none`) |
| Variable Amount | mapped | uae.wps:lines:variablePay | `line.ext.uae.wps.variablePay` | Shown whole, as the old screens did |
| Leave Days | mapped | uae.wps:lines:leaveDays | `line.ext.uae.wps.leaveDays` | |
| MOHRE Update Status | not mapped | | | No rule or port gives the labour ministry's status (I-88, I-100) |
| Account Type | not mapped | | | Not in the SIF or the model (I-100) |
| Bank Name | not mapped | | | The routing code is there; the participant directory lookup (`uae.p.enrich.participant.v1`) is reached by no workflow (I-100) |
| EDR Status | mapped | uae.wps:lines:lineStatus | `line.facts.validation.worst` | The line's own checks |
| Housing Allowance | not mapped | | | `uae.wps.ext.salary.v1` registers fixed and variable pay only (I-100) |
| Conveyance Allowance | not mapped | | | I-100 |
| Medical Allowance | not mapped | | | I-100 |
| Annual Passage Allowance | not mapped | | | I-100 |
| Overtime Allowance | not mapped | | | I-100 |
| Other Allowances | not mapped | | | I-100 |
| Leave Encashment | not mapped | | | I-100 |

## SIF Summary 360: File Summary

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | I-97 |
| Corporate | mapped, empty | uae.wps:subject:corporateName | `bulk.debtorName` | I-100 |
| Channel | not mapped | | | I-100 |
| Current Status | mapped | p:subject:status | `subject.status` | |
| Initiated By | not mapped | | | I-100 |
| Approved By | mapped | p:subject:approvedBy | `hold.decidedBy` | The old screen always showed a dash; the new model records the operator |
| Initiated DateTime | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | |
| Approved DateTime | mapped | p:subject:approvedAt | `hold.decidedAt` | |
| Total Records | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | |
| System Accepted | mapped | uae.wps:subject:systemAccepted | `lines.count(line.facts.validation.worst = pass)` | |
| System Rejected | mapped | uae.wps:subject:systemRejected | `lines.count(line.facts.validation.worst != pass)` | |
| Own A/Cs | mapped | uae.wps:subject:ownBankAccounts | `lines.count(line.creditorAgent.memberId = bulk.debtorAgent.memberId)` | The bank's own routing code is the employer's agent's |
| Other Bank A/C | mapped | uae.wps:subject:otherBankAccounts | `lines.count(line.creditorAgent.memberId != bulk.debtorAgent.memberId)` | |
| Sent to CB DateTime | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:sentToCbAt | `step:submit.send.at` | |
| CB File Name | mapped, empty | uae.wps:uae.wps.wf.salary-file-out.v1:cbFileName | `message:uae.wps.sif-submission.fileName` | I-97 |
| CB Response | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:cbResponse | `step:submit.read-ack.outcome` | |
| ACK/NAK File Name | mapped, empty | uae.wps:uae.wps.wf.salary-file-out.v1:ackFileName | `message:uae.wps.ack.fileName` | I-97 |
| Related Messages: SIF ACK/NAK status | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:ackStatus | `step:submit.read-ack.writes.status.code` | |
| Related Messages: NAK error code (file) | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:nakReason | `step:submit.read-ack.reasonCode` | |
| Related Messages: NAK errors per line (line, code, description) | not mapped | | | The ACK map writes the status only; the DER records are not in the model (I-100) |
| Raw File: Original SIF | mapped, empty | (documents) | `documents: uae.wps.sif` | Downloadable when kept (I-97) |
| Raw File: ACK/NAK | mapped, empty | (documents) | `documents: uae.wps.ack` | I-97 |

## SIF Summary 360: Integration 360

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| Total Transaction Amount | not mapped | | | The posting's charge and VAT are not recorded in its facts (I-100) |
| Total Salary Amount | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | |
| Charge Amount | not mapped | | | I-100 |
| VAT | not mapped | | | I-100 |
| CORE posting: type, status, start, end | mapped | p:subject:posting, p:subject:postedAt | `step:post.outcome`, `step:post.at` | Each attempt is also on the timeline |
| CORE posting: legs (D/C, account, amount, currency, branch, type code) | not mapped | | | The core-banking adapter keeps the legs; they are not facts (I-100) |
| CORE posting: Req Log, Res Log | not mapped | | | Deliberately: request and reply bodies hold personal data and are not kept |
| Channel Interactions: channel, action, start, status, direction | mapped | (timeline) | every `step` fact | Each attempt of each step, with its outcome and time |
| Channel Interactions: Retry | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:sendAttempts | `step:submit.send.attempt` | And each attempt on the timeline |
| Channel Interactions: Failure Reason | mapped | (timeline) | a `step` fact's error name | The error's text is not kept: it can quote a value |

## SIF Summary 360: Record List, and File Detail

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| # | mapped | uae.wps:lines:lineNo | `line.lineNo` | |
| Employee ID | mapped | uae.wps:lines:employeeId | `line.ext.uae.wps.personId`, masked | The SIF has one employee id, the MOL person id |
| MOHRE ID | mapped | uae.wps:lines:employeeId | `line.ext.uae.wps.personId`, masked | The same id |
| Credit Account / Account No | mapped | uae.wps:lines:creditAccount | `line.creditorAccount.iban`, masked | |
| Routing Code / Routing | mapped | uae.wps:lines:routingCode | `line.creditorAgent.memberId` | |
| Pay Start | mapped | uae.wps:lines:payStartDate | `line.ext.uae.wps.payStartDate` | |
| Pay End | mapped | uae.wps:lines:payEndDate | `line.ext.uae.wps.payEndDate` | |
| Days | mapped | uae.wps:lines:daysInPeriod | `line.ext.uae.wps.daysInPeriod` | |
| Fixed | mapped | uae.wps:lines:fixedPay | `line.ext.uae.wps.fixedPay` | |
| Variable | mapped | uae.wps:lines:variablePay | `line.ext.uae.wps.variablePay` | |
| Leave Days | mapped | uae.wps:lines:leaveDays | `line.ext.uae.wps.leaveDays` | |
| Total Salary | mapped | uae.wps:lines:totalSalary | `line.amount` | |
| Status | mapped | uae.wps:lines:lineStatus | `line.facts.validation.worst` | |
| Housing, Conveyance, Medical, Passage, Overtime, Other, Leave Encash | not mapped | | | I-100 |
| SCR: establishment id | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | |
| SCR: record count | mapped | uae.wps:subject:scrRecordCount | `bulk.numberOfTransactions` | |
| SCR: total salary | mapped | uae.wps:subject:scrTotalSalary | `bulk.controlSum` | Beside the sum of the lines (`linesTotal`) |
| SCR: salary month | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | |

## Audit trail drawer

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| Activity | mapped | (timeline) | every fact of the chain: `step`, `hold`, `operator`, `status`, `message` | |
| Action By | mapped | (timeline) | a fact's actor (`engine`, `runtime`, `operator:<subject>`) | An operator's identity from the bank's identity provider |
| Actioned DateTime | mapped | (timeline) | a fact's time | |
| Channel | not mapped | | | I-100 |
| Status | mapped | (timeline) | a fact's code | |
| Remarks | mapped | (timeline, actions) | an operator fact's remarks | |
| More Details, request and response bodies | not mapped | | | Deliberately: they hold personal data |

## Upload History (Banking Ops, WPS)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Reference | mapped | uae.wps:subject:sifFileId | `bulk.messageId` | |
| Corporate | mapped, empty | uae.wps:subject:corporateName | `bulk.debtorName` | I-100 |
| Salary Period | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | |
| Records | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | |
| Total Amount (AED) | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | |
| Status | mapped | p:subject:status | `subject.status` | |
| Submitted On | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | |
| Submitted By | not mapped | | | I-100 |
| Debit A/c No | mapped, empty | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | I-100 |
| MOL ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | |
| View & Download | mapped | (documents) | `documents` | I-97 |
| Error Details | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:fileFindings | `step:file-checks.writes.facts.validation.findings` | And per line, uae.wps:lines:lineFindings |

## WPS dashboard (PaymentHub-UI)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| Per file type: ACK received, NAK received (SIF) | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:cbResponse | `step:submit.read-ack.outcome` | As a filter: the list's count by CB response |
| Per file type: PRC | mapped | uae.wps:uae.wps.wf.agent-payment-in.v1:prcStatus | `step:confirm-receipt.read-ack.outcome` | |
| Per file type: DIF | mapped | uae.wps:uae.wps.wf.agent-payment-in.v1:difStatus | `step:report-disbursement.read-ack.outcome` | |
| Per file type: RFA | mapped | uae.wps:uae.wps.wf.agent-payment-in.v1:rfaStatus | `step:return-credits.read-ack.outcome` | |
| Per file type: RTC | mapped | uae.wps:uae.wps.wf.return-in.v1:employerCredit | `step:credit-employer.outcome` | |
| Refund flows: RFR, RSU | mapped | uae.wps:uae.wps.wf.refund-out.v1:rfrStatus, settlement | `step:request.read-ack.outcome`, `step:request.read-settlement.outcome` | |
| Refund flows: RRI, RRR, DCR | not mapped | | | No factory flow (`uae.wps.rrr` and `uae.wps.dcr` are planned messages; RRI is in no flow, plan P2) |
| Returned | mapped | uae.wps:uae.wps.wf.agent-payment-in.v1:returnsCount | `step:returns.writes.facts.returns.count` | |
| Returned amount | mapped, empty | uae.wps:uae.wps.wf.agent-payment-in.v1:returnsAmount | `step:returns.writes.facts.returns.amount` | The returns table yields no amount yet (plan P2, RFA) |
| Sanction completed, failed, confirmed | mapped | p:subject:sanctions, p:subject:lastOutcome | `step:sanctions.outcome`, `subject.lastOutcome` | Per payment; the counts are the list filtered |
| KPIs, MOHRE compliance status, employer establishments | not mapped | | | Dashboards and counts are reports, not a leg's view (I-68) |
| Recent transactions: id, type, status, direction, tenant, start | mapped | (list) | the list's own columns | |
| Recent transactions: duration | not mapped | | | The end of a payment is not a fact yet (I-97) |

## Coverage

Of the 128 rows above: 101 mapped (15 of them empty until the store keeps the files or the channel's fields, I-97 and I-100), 27 not mapped. Of the 27, 2 are kept out on purpose (request and reply bodies and the audit's payloads hold personal data; error texts can quote values), and 25 have no source in the new model, each in Issue I-100.
