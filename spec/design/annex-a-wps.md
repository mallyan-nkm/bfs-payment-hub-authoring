# Annex A. WPS authored on paper

Status: draft 0.2, 17 November 2026. Provisional: built from the 2018 CBUAE responsibility guide and public bank guides. Confirm every item against the current CBUAE WPS rulebook (decision D10).

## A.1 The scheme in one paragraph

An employer sends a salary information file (SIF) to its bank. The bank checks it, secures the funds and submits it to WPS. WPS answers with an acknowledgement, ACK or NAK. WPS splits an accepted file into one payment file (PIF) per agent, each with a supplementary file (PSI). An agent confirms receipt (PRC), pays the employees and reports disbursement (DIF). Refunds run as a request (RFR), a response (RRR) and a settlement file (RSU). Returns run as a returns file from the agent (RFA) and a return file to the employer's bank (RTC).

## A.2 What WPS tests in the model

| Model feature | Why WPS needs it |
| --- | --- |
| Maps both ways | The wire format is a file, not ISO 20022 |
| Batch and item level | A file holds one control record and many employee records |
| Scheme extension | Pay period, days paid, fixed and variable pay, leave days and the ids have no ISO home |
| Long awaits on a business calendar | Acknowledgements, refunds and returns arrive hours or days later |
| Two roles, two legs | The bank is the employer's bank and an agent |
| Reports as exchanges | PRC and DIF move no money |
| A request workflow | A refund request is not a payment |
| A refuse slot that sends nothing | WPS has no message by which an agent refuses a file |

## A.3 Canonical mapping

| Source | Canonical |
| --- | --- |
| SCR employer id | `ext.uae.wps.employerId` |
| SCR employer bank routing code | `batch.debtorAgent.clearingMemberId` |
| SCR file creation date and time | `ext.uae.wps.fileCreationDate`, `ext.uae.wps.fileCreationTime` |
| SCR salary month | `ext.uae.wps.salaryMonth` |
| SCR record count | `batch.numberOfTransactions` |
| SCR total salary | `batch.controlSum` |
| SCR currency | `batch.currency` |
| EDR person id | `items[].ext.uae.wps.personId` (personal data) |
| EDR agent routing code | `items[].payment.creditorAgent.clearingMemberId` |
| EDR IBAN | `items[].payment.creditorAccount.iban` |
| EDR fixed plus variable pay | `items[].payment.instructedAmount.value` |
| EDR fixed pay, variable pay | `items[].ext.uae.wps.fixedPay`, `variablePay` (personal data) |
| EDR pay start, pay end, days, leave days | `items[].ext.uae.wps.payStartDate`, `payEndDate`, `daysInPeriod`, `leaveDays` |
| Constant | `batch.categoryPurpose` = salary |

Public bank guides disagree on whether the SCR is the first or the last line, and on the time format. The intake map therefore carries channel variants (`scr-first`, `scr-last`), and scheme truth comes from the CBUAE specification.

## A.4 Legs

| Leg | Role | Fills | Steps |
| --- | --- | --- | --- |
| `uae.wps.wf.salary-file-out.v1` | Employer's bank | `scheme-leg` of `p.wf.corporate.v1` | Set the business date; file checks; per item, record checks and the cluster IBAN check; render SIF; submit (composite) |
| `uae.wps.wf.agent-payment-in.v2` | Agent | `scheme-leg` of `p.wf.inward.v2` (`scheme-leg@2`, which lets it hand back its returns; R-09-87) | Receive the PSI that pairs with the PIF (composite); file checks; render PRC; confirm receipt (composite); wait for core banking's posting window (`p.decide.await-posting-window.v1`); credit each employee, and ask about a credit whose outcome is unknown; render DIF; report disbursement (composite); after its ACK or its NAK, decide the returns; if any, render RFA, return them (composite), and on ACK record the return, on NAK hand to operations |
| `uae.wps.wf.refund-out.v2` | Employer's bank | `scheme-leg` of `p.wf.refund-out.v1` | Render RFR; request the refund (composite: encrypt, send, read the ACK, wait for and read the RSU). The platform credits the employer (R-12-24); `uae.wps.wf.refund-out.v1`, which credited the employer in the leg on `p.wf.request.v1`, drains (I-145) |
| `uae.wps.wf.return-in.v1` | Employer's bank | `scheme-leg` of `p.wf.inward.v2` | Wait for core banking's posting window; credit the employer's account, which the RTC map (`uae.wps.map.rtc-in.v2`) names at payment level |
| `uae.wps.wf.inward-refuse.v1` | Agent | `scheme-refuse` of `p.wf.inward.v2` | Hand to operations; nothing is sent |

Each exchange that waits is one block to the designer. The steps underneath are a subflow (Part 5, section 5.6).

| Composite | The designer handles | Underneath |
| --- | --- | --- |
| `uae.wps.exchange.submit-salary-file.v1` | `rejected`, `pending` | Send the SIF; await the acknowledgement for the reply time of the step; read ACK or NAK |
| `uae.wps.exchange.confirm-receipt.v1` | `rejected`, `pending` | Send the PRC; await the acknowledgement; read it |
| `uae.wps.exchange.request-refund.v1` | `rejected`, `pending` | Send the RFR; await and read the acknowledgement; await the RSU for up to ten business days; read it |
| `uae.wps.exchange.receive-supplement.v1` | `missing` | Await the PSI for the reply time of the step |
| `uae.wps.exchange.report-disbursement.v1` | `rejected`, `pending` | Send the DIF; await the acknowledgement of the DIF; read ACK or NAK |
| `uae.wps.exchange.return-credits.v1` | `rejected`, `pending` | Hold the upload for `uploadHold` (0 to 10 minutes, default 0); send the RFA; await the acknowledgement of the RFA for `replyWait` counted on `replyWaitCalendar` (1 to 30 days; by default 30 calendar days, the source's wait, Issue I-51; R-05-47); read ACK or NAK |

Each composite that awaits `uae.wps.ack` names the family of the file it waits on: `sif`, `rfr`, `prc`, `dif` or `rfa` (R-05-44). The front door reads the family from the acknowledgement's file name. The time rule (R-05-43) covers a scheme whose acknowledgements cannot be told apart.

### A.4.1 The agent's returns (Issues I-39, I-40)

| Point | What the leg does |
| --- | --- |
| Which credits | The verified refused credits only: those the core system refused, and those the inquiry found not posted. A whole-file return is the case where every credit was refused, not a separate mode. |
| A credit whose outcome is unknown | Asked about first (`p.post.verify.v1`). The returns are decided only when every credit is known; while one is not, the file goes to operations and the leg ends `pending`. |
| How many returns files | One per payment file. Every credit is known before it is sent, so none is found refused after it. A refusal that arrives later is unmatched and goes to operations (R-05-31). |
| The amount | Each line is round2(fixed pay + variable pay), half away from zero; the total is the sum of the rounded lines (`uae.wps.table.returns.v1`). |
| When | After the DIF's ACK and after its NAK. |

| The leg ends | When |
| --- | --- |
| `settled` | Every credit posted; or some were paid and the refused ones were returned and the return recorded. `facts.returns` holds the count and amount returned. |
| `returned` | No credit reached an employee and the whole amount went back, the return recorded. This is what `returned` means for an inward file. |
| `pending` | The DIF was refused (its returns still go back); a credit is still unknown; WPS refused the RFA (NAK: funds in suspense, at operations); no acknowledgement within `replyWait`; or the return could not be recorded (the repair hold first). |

## A.5 Dispatch table

| Message | Direction | Disposition |
| --- | --- | --- |
| `uae.wps.sif` | In, from the employer | Start: outward corporate payment, `salary-file-out` |
| `uae.wps.sif-submission` | Out | Sent by `send-salary-file` |
| `uae.wps.ack` | In | Deliver by file id and by the family of the file it answers: `sif`, `rfr`, `prc`, `dif`, `rfa`, `dcr`, `rrr` |
| `uae.wps.pif`, `uae.wps.psi` | In | Start: inward payment, `agent-payment-in`; the PSI is delivered to it |
| `uae.wps.prc`, `uae.wps.dif` | Out | Sent by the two report blocks |
| `uae.wps.refund-order` | In, from the employer | Start: an outward refund order on `p.wf.refund-out.v1`, `refund-out.v2` (scheme 1.3.0, R-12-24) |
| `uae.wps.rfr` | Out | Sent by `send-refund-request` |
| `uae.wps.rsu` | In | Deliver by refund request file id |
| `uae.wps.rfa` | Out | Sent by `send-returns-file`, inside `return-credits`; its ACK or NAK is `uae.wps.ack` |
| `uae.wps.rtc` | In | Start: inward return, `return-in` |
| `uae.wps.rri` | In, from WPS | Start: a refund order on `p.wf.refund.v1` (camt.056), `refund-request-in`, refused by `refund-request-refuse` (R-12-21) |
| `uae.wps.dcr` | Out | Sent by `send-disbursement-cancellation`, inside `return-refund`: the refunded funds (pacs.004) |
| `uae.wps.rrr` | Out | Sent by `send-refund-response`, inside `answer-refund`: each line accepted or rejected (camt.029) |

The refund order is the platform's (R-12-19): `p.wf.refund.v1` finds the earlier payment file by the id the RRI names, converts and prices the lines on the bank's rates and charges (R-12-20), blocks the amount, holds the order for approval and posts it. The WPS legs only send and acknowledge the DCR and the RRR. The ISO messages chosen for each file are this annex's reading of the source, not the rulebook's: the RRI asks for funds back (a cancellation request, camt.056), the DCR returns them (a payment return, pacs.004) and the RRR resolves the request per line (camt.029). Issue I-134 lists what the source and the scheme leave open.

## A.6 Artefacts

Generated from `factory/inventory.yaml`.

<!-- INVENTORY:uae.wps -->
| Kind | Count | Artefacts |
| --- | --- | --- |
| Activity | 37 | `uae.wps.decide.returns.v2`, `uae.wps.enrich.employer-status.v1`, `uae.wps.exchange.answer-refund.v1`, `uae.wps.exchange.confirm-receipt.v1`, `uae.wps.exchange.receive-supplement.v1`, `uae.wps.exchange.report-disbursement.v1`, `uae.wps.exchange.request-refund.v1`, `uae.wps.exchange.return-credits.v1`, `uae.wps.exchange.return-refund.v1`, `uae.wps.exchange.send-disbursement-cancellation.v2`, `uae.wps.exchange.send-disbursement-file.v2`, `uae.wps.exchange.send-receipt-confirmation.v2`, `uae.wps.exchange.send-refund-request.v2`, `uae.wps.exchange.send-refund-response.v2`, `uae.wps.exchange.send-returns-file.v2`, `uae.wps.exchange.send-salary-file.v2`, `uae.wps.exchange.submit-salary-file.v1`, `uae.wps.map.ack-correlation.v1`, `uae.wps.map.ack-in.v1`, `uae.wps.map.dcr-out.v1`, `uae.wps.map.dif-out.v2`, `uae.wps.map.pif-in.v1`, `uae.wps.map.prc-out.v2`, `uae.wps.map.psi-in.v1`, `uae.wps.map.refund-order-in.v1`, `uae.wps.map.rfa-out.v1`, `uae.wps.map.rfr-out.v1`, `uae.wps.map.rri-in.v1`, `uae.wps.map.rrr-out.v1`, `uae.wps.map.rsu-in.v1`, `uae.wps.map.rtc-in.v2`, `uae.wps.map.sif-in.v1`, `uae.wps.map.sif-out.v1`, `uae.wps.post.return.v1`, `uae.wps.validate.payment-file.v1`, `uae.wps.validate.salary-file.v1`, `uae.wps.validate.salary-record.v1` |
| Rule | 12 | `uae.wps.rule.agent-is-this-bank.v1`, `uae.wps.rule.amounts-non-negative.v1`, `uae.wps.rule.control-total-matches.v1`, `uae.wps.rule.currency-aed.v1`, `uae.wps.rule.employer-id-format.v1`, `uae.wps.rule.employer-not-suspended.v1`, `uae.wps.rule.employer-routing-code-format.v1`, `uae.wps.rule.file-name-matches-control.v1`, `uae.wps.rule.net-pay-positive.v1`, `uae.wps.rule.pay-period-valid.v1`, `uae.wps.rule.person-id-format.v1`, `uae.wps.rule.record-count-matches.v1` |
| Ruleset | 3 | `uae.wps.ruleset.payment-file.v1`, `uae.wps.ruleset.salary-file.v1`, `uae.wps.ruleset.salary-record.v1` |
| Table | 2 | `uae.wps.table.participant-names.v1`, `uae.wps.table.returns.v1` |
| Port | 3 | `uae.wps.port.employer-channel.v1`, `uae.wps.port.labour-ministry.v1`, `uae.wps.port.wps.v1` |
| Simulator | 5 | `uae.wps.sim.employer-channel.v1`, `uae.wps.sim.labour-ministry.v1`, `uae.wps.sim.wps-inward.v1`, `uae.wps.sim.wps-outward.v1`, `uae.wps.sim.wps-refunds.v1` |
| Workflow | 15 | `uae.wps.sub.answer-refund.v1`, `uae.wps.sub.confirm-receipt.v1`, `uae.wps.sub.receive-supplement.v1`, `uae.wps.sub.report-disbursement.v1`, `uae.wps.sub.request-refund.v1`, `uae.wps.sub.return-credits.v1`, `uae.wps.sub.return-refund.v1`, `uae.wps.sub.submit-salary-file.v1`, `uae.wps.wf.agent-payment-in.v2`, `uae.wps.wf.inward-refuse.v1`, `uae.wps.wf.refund-out.v2`, `uae.wps.wf.refund-request-in.v1`, `uae.wps.wf.refund-request-refuse.v1`, `uae.wps.wf.return-in.v1`, `uae.wps.wf.salary-file-out.v1` |
| Scheme | 1 | `uae.wps.scheme` |
| Extension | 1 | `uae.wps.ext.salary.v1` |
| Test | 42 | `WPS-ENR-001`, `WPS-ENR-002`, `WPS-EXC-001`, `WPS-EXC-002`, `WPS-EXC-003`, `WPS-IN-001`, `WPS-IN-002`, `WPS-IN-003`, `WPS-IN-004`, `WPS-IN-005`, `WPS-IN-006`, `WPS-IN-007`, `WPS-IN-008`, `WPS-IN-009`, `WPS-MAP-001`, `WPS-MAP-002`, `WPS-MAP-003`, `WPS-MAP-004`, `WPS-MAP-005`, `WPS-MAP-006`, `WPS-MAP-007`, `WPS-OUT-001`, `WPS-OUT-002`, `WPS-OUT-003`, `WPS-OUT-004`, `WPS-OUT-005`, `WPS-OUT-006`, `WPS-OUT-007`, `WPS-OUT-008`, `WPS-OUT-009`, `WPS-OUT-010`, `WPS-RFR-001`, `WPS-RFR-002`, `WPS-RFR-003`, `WPS-RRI-001`, `WPS-RRI-002`, `WPS-RRI-003`, `WPS-RRI-004`, `WPS-RRI-005`, `WPS-RRI-006`, `WPS-RRI-007`, `WPS-RTC-001` |
| Total | 123 | |
<!-- /INVENTORY:uae.wps -->

## A.7 Open points

| Id | Point |
| --- | --- |
| OP-A1 | Scheme validation runs first in the leg, after the reservation. A reject releases the reservation. Moving it before Reserve needs a second slot in `p.wf.corporate.v1`. |
| OP-A2 | The wait for an acknowledgement and for a refund settlement are placeholders. |
| OP-A3 | What an agent must do with a payment file it cannot process is assumed: hand to operations. |
| OP-A4 | Whether file-level rejection is all-or-nothing, or WPS can reject single records, is not confirmed. |
| OP-A5 | Reason codes for NAK files are scheme codes; the mapping to ISO reason codes is assumed. |
| OP-A6 | WPS is already on the engine. The existing flow is the oracle: `WPS-OUT-001` to `WPS-OUT-005` must reproduce it. |
| OP-A7 | The PSI is awaited and held by reference. Its map is built (`uae.wps.map.psi-in.v1`, the source's parser, native only) and called by no leg yet (Issue I-84). |
| OP-A8 | The returns follow the owner's decision of 2026-11-22 (Issues I-39 to I-41, A.4.1). Still to confirm against the rulebook (D10): partial or whole-file return, whether one payment file may have more than one returns file, the RFA's line layout, and what an RFA acknowledgement carries. |
| OP-A9 | Each WPS file the agent reads or writes has the source's parser or builder as its native code-behind (R-08-16): PIF, PSI, PRC, DIF, RFA, ACK and NAK, RRI. Their layouts are the source's, from the UAEWPS SSM it implements, and are still to confirm against the rulebook (D10). The RTC layout is the factory's own: the source parses none. |
