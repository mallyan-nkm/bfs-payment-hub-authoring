# The source's WPS operations screens, field by field, in the operations console

The source (`TransactHub-src.zip`, its `frontend/` and `PaymentHub-UI/`) showed WPS files on scheme-specific screens. The runtime's operations console is leg-generic: it shows what the packs' operations views declare (`spec/factory/p/views/p.view.operations.v1.yaml`, `spec/factory/uae.wps/views/uae.wps.view.operations.v1.yaml`, R-05-48). This table lists every field those screens showed and where it is now. `OperationsViewTests` checks that every field marked **mapped** is declared in the view named, so this table and the views cannot drift apart.

The views are artefacts of their packs, released like any artefact: this table follows `uae.wps.view.operations.v1` 1.2.0 and `p.view.operations.v1` 1.1.0.

Status:

- **mapped**: the view declares it and the read model fills it from the store's facts.
- **mapped, empty**: the view declares it, but nothing writes its source on the runtime yet (Issues I-97 and I-115); it shows `—` until something does.
- **not mapped**: no flow or fact of the new model gives it yet (no factory flow, plan P2, Issue I-97), or it is deliberately not carried.

The owner's decision of 2026-11-22 (Issue I-100) gave every field the earlier release listed as having no source an ISO 20022 home: the initiator (GrpHdr/InitgPty), the employer's name and debit account (Dbtr/Nm, DbtrAcct, from the channel's upload at intake), the allowances (the WPS salary extension, EVP), the account type (CdtrAcct/Tp), the bank name (CdtrAgt/FinInstnId/Nm), the NAK's per-line errors (pain.002 TxInfAndSts), the posting's legs, charge and VAT (camt.054 entries and ChrgsInf), the channel and the labour ministry status (declared extensions), and the dashboard's counts (derived). Before it: 101 mapped (15 empty), 27 not mapped.

Screens inventoried: Banking Ops `features/reports/wps` (SIF Summary report, SIF Detail report, SIF Summary 360 drawer with File Summary, Integration 360 and Record List, the audit trail drawer), Banking Ops `features/wps` (Upload History, File Detail), the admin portal's `views/reports/wps/sif-summary` (the same report in Angular), and PaymentHub-UI's WPS dashboard.

## SIF Summary report (Banking Ops and admin portal)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| SL No | mapped | (list row) | the list's position | Every list has it; not a field |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | The inbound SIF is not kept in `message` on a deployment yet (I-97) |
| Transaction ID | mapped | uae.wps:subject:transactionId | `subject.id` | The bulk's id in the store; searchable with `q` |
| Corporate Name | mapped | uae.wps:subject:corporateName | `bulk.debtorName` | Dbtr/Nm, carried from the channel's upload at intake (R-02-18); empty when the channel declares none |
| Account Number | mapped | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | DbtrAcct, carried from the channel's upload at intake (R-02-18) |
| MOL Establishment ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | Masked, as Upload History masked it |
| Upload Date | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | Also the list's date filter |
| Total Records | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | Range filter |
| Total Amount | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | Range filter, and the list's amount filter |
| Status | mapped | p:subject:status | `subject.status` | The ISO status the workflow state carries; the leg's outcome beside it |
| Uploaded By | mapped | uae.wps:subject:initiatedBy | `bulk.initiatingParty.id`, masked | GrpHdr/InitgPty, the channel user's id only, never a name (R-02-18) |
| Actions | mapped | (offers) | the views' `actions` | Approve, decline, release, reject, cancel, re-screen, retry, resend |

## SIF Detail report (one row per salary line)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | I-97 |
| SIF File Name | mapped | uae.wps:subject:sifFileId | `bulk.messageId` | The file id the SCR names |
| Employer Unique ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | |
| Corporate Name | mapped | uae.wps:subject:corporateName | `bulk.debtorName` | Dbtr/Nm, carried from the channel's upload at intake (R-02-18); empty when the channel declares none |
| Debit Account Number | mapped | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | DbtrAcct, carried from the channel's upload at intake (R-02-18) |
| Routing Code | mapped | uae.wps:subject:employerRoutingCode | `bulk.debtorAgent.memberId` | |
| Salary Month | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | MMYYYY, as the SCR has it |
| Salary Year | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | The same field: month and year together |
| File Upload DateTime | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | |
| Channel | mapped | uae.wps:subject:uploadChannel | `ext.uae.wps.uploadChannel` | A declared extension (SplmtryData), from the channel's upload at intake |
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
| MOHRE Update Status | mapped | uae.wps:subject:employerStatus | `ext.uae.wps.employerStatus` | A declared extension (SplmtryData): the labour ministry's answer to `uae.wps.enrich.employer-status.v1` (I-88) |
| Account Type | mapped, empty | uae.wps:lines:accountType | `line.creditorAccount.type` | CdtrAcct/Tp; the SIF carries no account type, and no WPS map writes one yet (I-115) |
| Bank Name | mapped, empty | uae.wps:lines:bankName | `line.creditorAgent.name` | CdtrAgt/FinInstnId/Nm, by routing code; the participant directory's adapter does not answer names yet (I-115) |
| EDR Status | mapped | uae.wps:lines:lineStatus | `line.facts.validation.worst` | The line's own checks |
| Housing Allowance | mapped | uae.wps:lines:housingAllowance | `line.ext.uae.wps.housingAllowance` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Conveyance Allowance | mapped | uae.wps:lines:conveyanceAllowance | `line.ext.uae.wps.conveyanceAllowance` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Medical Allowance | mapped | uae.wps:lines:medicalAllowance | `line.ext.uae.wps.medicalAllowance` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Annual Passage Allowance | mapped | uae.wps:lines:annualPassageAllowance | `line.ext.uae.wps.annualPassageAllowance` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Overtime Allowance | mapped | uae.wps:lines:overtimeAllowance | `line.ext.uae.wps.overtimeAllowance` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Other Allowances | mapped | uae.wps:lines:otherAllowances | `line.ext.uae.wps.otherAllowances` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |
| Leave Encashment | mapped | uae.wps:lines:leaveEncashment | `line.ext.uae.wps.leaveEncashment` | From the SIF's EVP record (RmtInf/Strd semantics; provisional until the rulebook confirms the layout); shown whole, as the old screens did |

## SIF Summary 360: File Summary

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Name | mapped, empty | uae.wps:subject:fileName | `message:uae.wps.sif.fileName` | I-97 |
| Corporate | mapped | uae.wps:subject:corporateName | `bulk.debtorName` | Dbtr/Nm, carried from the channel's upload at intake (R-02-18); empty when the channel declares none |
| Channel | mapped | uae.wps:subject:uploadChannel | `ext.uae.wps.uploadChannel` | A declared extension (SplmtryData), from the channel's upload at intake |
| Current Status | mapped | p:subject:status | `subject.status` | |
| Initiated By | mapped | uae.wps:subject:initiatedBy | `bulk.initiatingParty.id`, masked | GrpHdr/InitgPty, the channel user's id only, never a name (R-02-18) |
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
| Related Messages: NAK errors per line (line, code, description) | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:nakLines | `step:submit.read-ack.writes.status.transactions` | pain.002 TxInfAndSts per DER record (R-02-19); the runtime's ACK reader writes them when the NAK is a file |
| Raw File: Original SIF | mapped, empty | (documents) | `documents: uae.wps.sif` | Downloadable when kept (I-97) |
| Raw File: ACK/NAK | mapped, empty | (documents) | `documents: uae.wps.ack` | I-97 |

## SIF Summary 360: Integration 360

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| Total Transaction Amount | mapped | p:subject:postingTotal | `step:post.writes.facts.posting.totalAmount` | The amount debited with charge and VAT, camt.054 Ntry/Amt, written by the runtime's posting |
| Total Salary Amount | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | |
| Charge Amount | mapped | p:subject:chargeAmount | `step:post.writes.facts.posting.charges.amount` | ChrgsInf/Amt |
| VAT | mapped | p:subject:vatAmount | `step:post.writes.facts.posting.charges.tax.amount` | ChrgsInf/Tax, with its rate |
| CORE posting: type, status, start, end | mapped | p:subject:posting, p:subject:postedAt | `step:post.outcome`, `step:post.at` | Each attempt is also on the timeline |
| CORE posting: legs (D/C, account, amount, currency, branch, type code) | mapped | p:subject:postingLegs | `step:post.writes.facts.posting.entries` | camt.054 entries; each account shows its last four characters only |
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
| Housing, Conveyance, Medical, Passage, Overtime, Other, Leave Encash | mapped | uae.wps:lines:housingAllowance, conveyanceAllowance, medicalAllowance, annualPassageAllowance, overtimeAllowance, otherAllowances, leaveEncashment | `line.ext.uae.wps.housingAllowance`, `line.ext.uae.wps.conveyanceAllowance`, `line.ext.uae.wps.medicalAllowance`, `line.ext.uae.wps.annualPassageAllowance`, `line.ext.uae.wps.overtimeAllowance`, `line.ext.uae.wps.otherAllowances`, `line.ext.uae.wps.leaveEncashment` | From the EVP record; shown whole |
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
| Channel | mapped | uae.wps:subject:uploadChannel | `ext.uae.wps.uploadChannel` | A declared extension (SplmtryData), from the channel's upload at intake |
| Status | mapped | (timeline) | a fact's code | |
| Remarks | mapped | (timeline, actions) | an operator fact's remarks | |
| More Details, request and response bodies | not mapped | | | Deliberately: they hold personal data |

## Upload History (Banking Ops, WPS)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| File Reference | mapped | uae.wps:subject:sifFileId | `bulk.messageId` | |
| Corporate | mapped | uae.wps:subject:corporateName | `bulk.debtorName` | Dbtr/Nm, carried from the channel's upload at intake (R-02-18); empty when the channel declares none |
| Salary Period | mapped | uae.wps:subject:salaryMonth | `ext.uae.wps.salaryMonth` | |
| Records | mapped | uae.wps:subject:totalRecords | `bulk.numberOfTransactions` | |
| Total Amount (AED) | mapped | uae.wps:subject:totalAmount | `bulk.controlSum` | |
| Status | mapped | p:subject:status | `subject.status` | |
| Submitted On | mapped | uae.wps:subject:uploadedAt | `subject.acceptedAt` | |
| Submitted By | mapped | uae.wps:subject:initiatedBy | `bulk.initiatingParty.id`, masked | GrpHdr/InitgPty, the channel user's id only, never a name (R-02-18) |
| Debit A/c No | mapped | uae.wps:subject:debitAccount | `bulk.debtorAccount.iban`, masked | DbtrAcct, carried from the channel's upload at intake (R-02-18) |
| MOL ID | mapped | uae.wps:subject:employerId | `ext.uae.wps.employerId`, masked | |
| View & Download | mapped | (documents) | `documents` | I-97 |
| Error Details | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:fileFindings | `step:file-checks.writes.facts.validation.findings` | And per line, uae.wps:lines:lineFindings |

## WPS dashboard (PaymentHub-UI)

| Old field | Status | View field | Source | Note |
| --- | --- | --- | --- | --- |
| Per file type: ACK received, NAK received (SIF) | mapped | uae.wps:uae.wps.wf.salary-file-out.v1:cbResponse | `step:submit.read-ack.outcome` | As a filter: the list's count by CB response |
| Per file type: PRC | mapped | uae.wps:uae.wps.wf.agent-payment-in.v2:prcStatus | `step:confirm-receipt.read-ack.outcome` | |
| Per file type: DIF | mapped | uae.wps:uae.wps.wf.agent-payment-in.v2:difStatus | `step:report-disbursement.read-ack.outcome` | |
| Per file type: RFA | mapped | uae.wps:uae.wps.wf.agent-payment-in.v2:rfaStatus | `step:return-credits.read-ack.outcome` | |
| Per file type: RTC | mapped | uae.wps:uae.wps.wf.return-in.v1:employerCredit | `step:credit-employer.outcome` | |
| Refund flows: RFR, RSU | mapped | uae.wps:uae.wps.wf.refund-out.v2:rfrStatus, settlement | `step:request.read-ack.outcome`, `step:request.read-settlement.outcome` | The employer's refund is an outward refund order on `p.wf.refund-out.v1`, WPS its leg (R-12-24); the credit to the employer is the platform view's posting (`posting`, `postingTotal`, `postingLegs`) |
| Refund flows: RRI, DCR, RRR | mapped | uae.wps:uae.wps.wf.refund-request-in.v1:dcrStatus, rrrStatus | `step:return-funds.read-ack.outcome`, `step:answer.read-ack.outcome` | A refund order on `p.wf.refund.v1`, WPS as its leg (R-12-19 to R-12-22, I-133); a refused order's RRR is the refuse leg's `rrrStatus`. The source's maker and checker screens, its manual block and its balance enquiry runs have no field of their own: the approval hold stands for them (I-134) |
| Refund cycle: the earlier payment, the exchange rate, the charge (RRI posting) | mapped | p:subject:originalPaymentId, fxRate, refundCharge | `header.originalPaymentId`, `step:fx.writes.facts.fx.rate`, `step:charges.writes.facts.charges.amount` | The platform's order, not the scheme's: FX and charges are platform activities on the bank's values (R-12-20) |
| Refund cycle: maker and checker verification (the decision, the checker, when) | mapped | p:subject:approval, approvalBy, approvalAt | `step:approval.outcome`, `step:approval.writes.facts.approval.by`, `step:approval.writes.facts.approval.at` | The approval hold's decision is the hold step's fact on every engine (R-05-59, I-136) |
| Returned | mapped | uae.wps:uae.wps.wf.agent-payment-in.v2:returnsCount | `step:returns.writes.facts.returns.count` | |
| Returned amount | mapped, empty | uae.wps:uae.wps.wf.agent-payment-in.v2:returnsAmount | `step:returns.writes.facts.returns.amount` | The returns table yields no amount yet (plan P2, RFA) |
| Sanction completed, failed, confirmed | mapped | p:subject:sanctions, p:subject:lastOutcome | `step:sanctions.outcome`, `subject.lastOutcome` | Per payment; the counts are the list filtered |
| KPIs, MOHRE compliance status, employer establishments | mapped | (list) | the list's counts under a filter | Derived: a count is the list filtered (by CB response, by `employerStatus`, by `employerId`); a report of its own is I-68 |
| Recent transactions: id, type, status, direction, tenant, start | mapped | (list) | the list's own columns | |
| Recent transactions: duration | mapped | p:subject:duration | `subject.duration` | Derived: the `payment.end` fact's time less the acceptance (I-97); empty while the payment is open |

## Coverage

Of the 130 rows above: 128 mapped (10 of them empty until the store keeps the files or a lookup answers them, I-97 and I-115), 2 not mapped. Of the 2, 2 are kept out on purpose (request and reply bodies and the audit's payloads hold personal data; error texts can quote values), and 0 wait for a flow or a fact not built yet (plan P2, I-97). The refund flows (RRI, DCR, RRR) were the one that waited; they are built as a refund order (I-133). Before the owner's decision of 2026-11-22 (Issue I-100): 101 mapped (15 empty), 27 not mapped, 25 of them with no source in the new model.
