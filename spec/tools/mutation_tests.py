#!/usr/bin/env python3
"""Mutation tests for tools/validate.py.

Each case copies the repository to a temporary folder, breaks one thing, runs the
validator and expects the named check to fire. A check that no mutation catches is
an assertion, not a mechanism.

Run: python3 tools/mutation_tests.py
"""
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
COPY = ("schemas", "factory", "examples", "conformance", "tools", "design")
# The bank packs on the branch (check L35) sit beside spec/, so a case copies them too.
BANKS = ROOT.parent / "content-configurations" / "banks"
CONTENT = ROOT.parent / "content-configurations"
EB = "../content-configurations/banks/example-bank"

WF = "factory/p/workflows/p.wf.corporate.v1.yaml"
SAL = "factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml"
OPV = "factory/uae.wps/views/uae.wps.view.operations.v1.yaml"
ERRS = "factory/p/errors/p.errors.platform.v1.yaml"

# (id, expected line prefix, file, old text, new text, what it breaks)
CASES = [
    ("M01", "ERROR L00", "factory/p/activities/p.reserve.funds.v1.yaml",
     "idempotencyKey:", "idemKeyRemoved:", "a write activity loses its idempotency key"),
    ("M02", "ERROR L02", WF,
     "call: p.validate.limits.v1", "call: uae.p.validate.iban.v1", "platform scope calls a cluster block"),
    ("M03", "ERROR L03", WF,
     "call: p.validate.limits.v1", "call: p.validate.nothing.v1", "a call names an activity that does not exist"),
    ("M04", "ERROR L04", "factory/p/activities/p.reserve.funds.v1.yaml",
     "release: 1.0.0", "release: 2.0.0", "the release major disagrees with the name"),
    ("M05", "ERROR L05", SAL,
     "      pending: { end: pending }\n", "", "an outcome is left unhandled"),
    ("M06", "ERROR L06", WF,
     "- hook: before-release", "- hook: not-declared", "a hook step names an undeclared hook point"),
    ("M07", "ERROR L07", "factory/uae.wps/workflows/uae.wps.wf.inward-refuse.v1.yaml",
     "wfKind: leg", "wfKind: handler", "a handler calls a block that holds"),
    ("M08", "ERROR L08", SAL,
     "  slot: scheme-leg", "  slot: no-such-slot", "a leg fills a slot that is not declared"),
    ("M09", "ERROR L09", "factory/p/ports/p.port.channel.v1.yaml",
     "title: Originating channel", "title: Originating channel of example-bank", "shared content names a tenant"),
    ("M10", "ERROR L10", "factory/uae.wps/workflows/uae.wps.sub.request-refund.v1.yaml",
     "message: uae.wps.rsu", "message: uae.wps.psi", "a deliver message is awaited by nobody"),
    ("M11", "WARN  L11", "factory/p/activities/p.reconcile.scheme-report.v1.yaml",
     "invokedBy: schedule\n", "", "a block that nothing calls stops saying what runs it"),
    ("M12", "ERROR L12", "factory/uae.p/rules/uae.p.rule.iban-length.v1.yaml",
     "      finding:", "      fact:", "a validation set holds a rule that yields something other than a finding"),
    ("M13", "ERROR L13", "examples/bank/activities/b.enrich.cost-centre.v1.yaml",
     "  - ext.b.costCentre", "  - payment.creditor", "a hook block writes outside the hook point contract"),
    ("M14", "ERROR L14", "examples/bank/configuration-set.yaml",
     "    value: 100000\n", "    value: 1500000\n", "a configuration value is outside its bounds"),
    ("M15", "ERROR L15", "factory/uae.wps/tests/WPS-OUT-001.yaml",
     "    scenario: accept\n  - port: uae.p.port.calendar.v1", "    scenario: no-such-scenario\n  - port: uae.p.port.calendar.v1",
     "a test bench names a scenario the simulator lacks"),
    ("M16", "ERROR L16", "factory/uae.wps/workflows/uae.wps.sub.submit-salary-file.v1.yaml",
     "message: uae.wps.ack", "message: uae.wps.pif", "a subflow awaits a message that is not a deliver message"),
    ("M17", "ERROR L17", "factory/p/rules/p.rule.amount-within-platform-limit.v1.yaml",
     "      as: maxAmount\n", "      as: otherParam\n", "a rule uses a parameter it does not declare"),
    ("M18", "ERROR L18", "examples/bank/activities/b.enrich.cost-centre.v1.yaml",
     "  - ext.b.costCentre", "  - ext.zz.costCentre", "an activity writes an unregistered extension path"),
    ("M19", "ERROR L27", "examples/supersession/SUP-2026-001.yaml",
     '"uae.wps.wf.salary-file-out.v1@1.0.0"', '"p.wf.request.v1@1.0.0"', "a supersession names a workflow that does not use the artefact"),
    ("M20", "ERROR L27", "examples/supersession/SUP-2026-001.yaml",
     "with: 1.0.1", "with: 2.0.0", "a supersession crosses a major line"),
    ("M21", "ERROR L22", "factory/uae.p/simulators/uae.p.sim.calendar.v1.yaml",
     "port: uae.p.port.calendar.v1", "port: uae.p.port.participant-directory.v1", "a port is left with no simulator"),
    ("M22", "ERROR L30", "factory/uae.wps/workflows/uae.wps.sub.submit-salary-file.v1.yaml",
     "  - return: accepted\n", "  - return: settled\n", "a subflow returns an outcome its activity does not declare"),
    ("M23", "ERROR L31", "factory/uae.wps/workflows/uae.wps.sub.submit-salary-file.v1.yaml",
     "wfKind: subflow", "wfKind: segment", "an await is written outside a subflow"),
    ("M24", "ERROR L32", "factory/uae.wps/activities/uae.wps.exchange.submit-salary-file.v1.yaml",
     "  replyTimeout: P1D\n", "", "an await with no timeout has no reply time to take"),
    ("M25", "ERROR L33", "factory/uae.wps/scheme.yaml",
     "        direction: outward\n        segment: corporate\n", "        direction: inward\n",
     "a start message routes to a platform workflow whose slot its leg does not fill"),
    ("M26", "ERROR L33", "factory/uae.wps/scheme.yaml",
     "        segment: corporate\n", "",
     "an outward payment names no segment, so workflow-select picks no platform workflow"),
    ("M27", "ERROR L34", "factory/inventory.yaml",
     "  - scope: uae.wps\n", "  - scope: uae.retired\n", "a folder under factory/ is not a pack the inventory lists"),
    ("M28", "ERROR L34", "factory/uae.p/ports/uae.p.port.calendar.v1.yaml",
     "name: uae.p.port.calendar.v1", "name: uae.retired.port.calendar.v1", "content of another scope is left in a pack's folder"),
    ("M29", "ERROR L35", EB + "/configuration-set.yaml",
     "tenant: example-bank\n", "tenant: other-bank\n", "a document in a bank's folder names another tenant"),
    ("M30", "ERROR L35", EB + "/activities/b.notify.treasury.v1.yaml",
     "title: Tell treasury of a settled payment", "title: Tell treasury", "a bank artefact changes and its lock file is not regenerated"),
    ("M31", "ERROR L35", EB + "/pack.yaml",
     "packType: bank", "packType: shared", "a bank folder's pack.yaml is not a bank pack"),
    ("M32", "ERROR L36", "factory/uae.wps/workflows/uae.wps.sub.return-credits.v1.yaml",
     "family: rfa", "family: rfx", "an await names a family its message's dispatch entry does not declare"),
    ("M33", "ERROR L17", "factory/uae.wps/workflows/uae.wps.sub.return-credits.v1.yaml",
     "wait: { parameter: uploadHold }", "wait: { parameter: uploadHolds }", "a wait names a parameter its composite does not declare"),
    ("M34", "ERROR L14", "examples/bank/configuration-set.yaml",
     "    value: P10D\n", "    value: P100D\n", "a duration value is longer than its bounds allow (P100D sorts before P30D as text)"),
    # R-06-21: a code, a string or a list item outside its pattern, and a duration outside its bounds (L14)
    ("M35", "ERROR L14", "examples/bank/configuration-set.yaml",
     "values:\n", "values:\n  - parameter: p.post.accounting.v1#transactionCode\n    value: not a code\n    validFrom: '2026-01-01'\n",
     "a transaction code a bank sets does not match its pattern"),
    ("M36", "ERROR L14", "examples/bank/configuration-set.yaml",
     "values:\n", "values:\n  - parameter: uae.wps.exchange.submit-salary-file.v1#ackWait\n    value: P30D\n    validFrom: '2026-01-01'\n",
     "a bank waits longer for the acknowledgement than the parameter's bound"),
    # R-05-47: a reply wait a bank may set names a parameter the composite declares (L30)
    ("M37", "ERROR L30", "factory/uae.wps/activities/uae.wps.exchange.submit-salary-file.v1.yaml",
     "replyTimeoutParameter: ackWait", "replyTimeoutParameter: noSuchWait", "a composite's reply wait names a parameter it does not declare"),
    # R-06-23: no literal secret in a binding, a configuration or a package (L37)
    ("M38", "ERROR L37", "../content-configurations/banks/dib/core-banking.yaml",
     "secretRef: env:TH_DIB_CORE_BANKING_TOKEN", "secretRef: synthetic-literal-value", "a binding's secretRef holds a value, not a reference"),
    ("M39", "ERROR L37", "../content-configurations/banks/dib/file-transfer.yaml",
     "secretRef: env:TH_DIB_SFTP_CREDENTIAL", "secretRef: env:TH_DIB_SFTP_CREDENTIAL\n      password: synthetic-literal-value", "a binding carries a password as a value beside its reference"),
    # R-06-22: a secretRef naming a store that is not one of the six is not a reference (L37)
    ("M52", "ERROR L37", "../content-configurations/banks/dib/core-banking.yaml",
     "secretRef: env:TH_DIB_CORE_BANKING_TOKEN", "secretRef: gcp-sm:projects/x/secrets/th-token", "a binding's secretRef names a store the secret interface does not have"),
    # R-05-48, R-05-49: an operations view says only what the store can answer (L38)
    ("M40", "ERROR L38", OPV,
     '"step:submit.read-ack.outcome"', '"step:submit.read-acks.outcome"', "a view's field reads a step the leg does not have"),
    ("M41", "ERROR L38", OPV,
     "source: line.ext.uae.wps.personId, mask: id,", "source: line.ext.uae.wps.personId,", "a view shows a personal value with no mask"),
    ("M42", "ERROR L38", OPV,
     "action: retry, label: Retry the failed step", "action: approve, label: Retry the failed step", "a view offers a hold decision where no flow holds for one"),
    ("M43", "ERROR L38", OPV,
     "columns: [cbResponse, ackReceivedAt]", "columns: [cbResponse, ackAt]", "a view's list column is not a declared field"),
    ("M44", "ERROR L38", OPV,
     "leg: uae.wps.wf.return-in.v1", "leg: uae.wps.wf.return-out.v1", "a view declares a leg the scheme does not have"),
    ("M45", "ERROR L38", OPV,
     "mask: none, oldScreen: \"SIF Summary report, Corporate Name (an organisation's name, shown whole)\" }", "mask: none }",
     "a view shows a value unmasked without naming the old screen that did"),
    ("M46", "ERROR L38", OPV,
     "source: ext.uae.wps.salaryMonth,", "source: ext.uae.wps.salaryMonths,", "a view reads an extension field nobody registers"),
    # R-05-51, R-05-52: every error an activity, rule, port or engine can raise has a dictionary entry (L39)
    ("M47", "ERROR L39", ERRS,
     "  - code: TransientError\n", "  - code: TransientFailure\n", "the activities' TransientError has no entry"),
    ("M48", "ERROR L39", "factory/p/rules/p.rule.amount-positive.v1.yaml",
     "reasonCode: AM01", "reasonCode: AM99", "a rule gives a reason code no dictionary has"),
    ("M49", "ERROR L39", ERRS,
     "  - code: flowable.DeadLetterJob\n", "  - code: flowable.DeadLetter\n", "an engine binding's error has no entry"),
    ("M50", "ERROR L39", "factory/uae.wps/errors/uae.wps.errors.wps.v1.yaml",
     "  - code: FF01\n", "  - code: AM10\n", "a dictionary has two entries for one code, and the party's FF01 has none"),
    ("M51", "ERROR L39", ERRS,
     "  - code: AC04\n    title: Closed account number\n    category: business\n    retryable: false\n    isoReasonCode: AC04\n    raisedBy:\n      - port\n",
     "  - code: AC04\n    title: Closed account number\n    category: business\n    retryable: false\n    isoReasonCode: AC04\n    raisedBy:\n      - rule\n",
     "a party's reason code is in the dictionary only as a rule's"),
    # R-08-16: only an activity with a script-behind or a builtin may say the native host implements it too (L40)
    ("M53", "ERROR L40", "factory/uae.wps/activities/uae.wps.exchange.return-credits.v1.yaml",
     "  subflow: uae.wps.sub.return-credits.v1\n", "  subflow: uae.wps.sub.return-credits.v1\n  native: true\n",
     "a composite says the native host implements it"),
    # R-11-36: a file a seed published keeps the text its ledger records (L41)
    ("M54", "ERROR L41", "../content-configurations/artefacts/uae.wps/uae.wps.map.ack-in.v1/1.0.0.yaml",
     "title: ACK or NAK to status", "title: ACK or NAK to a status", "a published release is edited on the branch"),
    # R-11-35: a factory artefact changed at a release the branch already holds (L41)
    ("M55", "ERROR L41", "factory/uae.wps/rules/uae.wps.rule.currency-aed.v1.yaml",
     "title: ", "title: Changed ", "a factory artefact is changed without a new release"),
    # R-11-36: a seeded file no ledger records (L41)
    ("M56", "ERROR L41", "../content-configurations/change-sets/ledger/CS-2026-11-21-001.yaml",
     "  - { path: artefacts/uae.wps/uae.wps.map.ack-in.v1/1.0.0.yaml,", "  - { path: artefacts/uae.wps/uae.wps.map.ack-in.v1/0.9.0.yaml,",
     "a seeded file is in no ledger"),
    ("M57", "ERROR L42", "factory/uae.wps/rules/uae.wps.rule.control-total-matches.v1.yaml",
     "  - having: total != b.controlSum", "  - having: total - b.controlSum > 0.001",
     "a rule compares money with a floating-point tolerance"),
    ("M58", "ERROR L42", "factory/uae.wps/rules/uae.wps.rule.control-total-matches.v1.yaml",
     "        controlSum: 300\n", "        controlSum: 1234567890123456.78\n",
     "an example gives an amount a binary double cannot hold as a number"),
    ("M59", "ERROR L43", SAL,
     '  reads: [header, batch, "items[]", ext.uae.wps, facts.reservation]', '  reads: [header, batch, "items[]", ext.uae.wps, facts.reservation, facts.posting]',
     "a leg reads what its slot contract does not give it"),
    ("M69", "ERROR L35", "../content-configurations/banks/dib/pack.yaml",
     "licensedSchemes:\n  - uae.wps\n", "", "a bank pack names no scheme it licenses"),
    ("M68", "ERROR L43", "factory/uae.wps/workflows/uae.wps.wf.return-in.v1.yaml",
     "  writes: [status, facts.posting]", "  writes: [status, facts.posting, facts.reservation]",
     "a leg writes what its slot contract does not let it write"),
    ("M60", "ERROR L44", "factory/uae.wps/activities/uae.wps.exchange.submit-salary-file.v1.yaml",
     "    default: P1D\n", "    default: P9D\n", "a dial's default is outside its own bounds"),
    ("M61", "ERROR L41", "../content-configurations/artefacts/bindings/p.binding.temporal.v1/1.1.0.yaml",
     "forEachConcurrency: 64", "forEachConcurrency: 32", "a published engine binding release is edited in place (Issue I-83)"),
    # R-03-24: a protect activity reaches its key service through a port and changes nothing in the bank (L00)
    ("M62", "ERROR L00", "factory/p/activities/p.protect.encrypt-file.v1.yaml",
     "effect: read", "effect: pure", "the encryption block claims to need no key service"),
    # L27: a supersession's scope names a workflow release the repository has, the latest or a published one
    ("M63", "ERROR L27", "examples/supersession/SUP-2026-001.yaml",
     '["uae.wps.wf.salary-file-out.v1@1.0.0"]', '["uae.wps.wf.salary-file-out.v1@0.9.0"]', "a supersession names a release never published"),
    # L45 (R-05-55): a step parks for at least a minute and at most thirty days
    ("M64", "ERROR L45", "factory/p/workflows/p.wf.commercial.v1.yaml",
     "policy: { attemptTimeout: PT2S, totalTimeout: PT4S, retry: { maxAttempts: 2, firstInterval: PT1S } }",
     "policy: { attemptTimeout: PT2S, totalTimeout: PT4S, retry: { maxAttempts: 2, firstInterval: PT1S }, park: P90D }",
     "a step parks for longer than thirty days"),
    # L46 (R-12-17, Issue I-117): on a file scheme every file is encrypted before it is sent
    ("M65", "ERROR L46", "factory/uae.wps/workflows/uae.wps.sub.confirm-receipt.v1.yaml",
     "  - id: encrypt\n    call: p.protect.encrypt-file.v1\n    handle:\n      refused: { return: rejected }\n", "",
     "the receipt confirmation is sent unencrypted"),
    # L47 (R-12-19, Issue I-133): the refund order's posting is the platform's; the WPS leg that fills its slot posts nothing
    ("M66", "ERROR L47", "factory/uae.wps/workflows/uae.wps.wf.refund-request-in.v1.yaml",
     "  - id: render-answer\n", "  - id: debit-employee\n    call: p.post.accounting.v1\n    handle:\n      rejected: { end: pending }\n      unknown: { end: pending }\n  - id: render-answer\n",
     "the WPS leg posts the refund the platform workflow owns"),
    ("M67", "ERROR L48", "../content-configurations/banks/dib/core-banking.yaml",
     "  - port: p.port.core-banking.v1", "  - port: p.port.core-ledger.v1", "a bank binds a port that no pack holds"),
    # L38 (R-05-61, Issue I-135): an action that names its platform workflows is held to those, and they are the view's own
    ("M70", "ERROR L38", "factory/p/views/p.view.operations.v1.yaml",
     "when: { workflow: [p.wf.corporate.v1, p.wf.inward.v2] }", "when: { workflow: [p.wf.corporate.v1, p.wf.refund.v1] }",
     "re-screen is offered on the refund order, which takes no re-screen"),
    ("M71", "ERROR L38", "factory/p/views/p.view.operations.v1.yaml",
     "when: { workflow: [p.wf.corporate.v1, p.wf.inward.v2] }", "when: { workflow: [p.wf.corporate.v1, p.wf.request.v1] }",
     "an action names a platform workflow the view does not show"),
    # L47 (R-12-24): the outward refund order's posting is the platform's too; the employer's WPS leg posts nothing
    ("M72", "ERROR L47", "factory/uae.wps/workflows/uae.wps.wf.refund-out.v2.yaml",
     "  - end: settled\n", "  - id: credit-employer\n    call: p.post.accounting.v2\n    handle:\n      rejected: { end: pending }\n      unknown: { end: pending }\n  - end: settled\n",
     "the employer's WPS leg posts the credit the outward refund order owns"),
    # L49 (R-01-25): a reference table's rows fit its columns, and a column that references another table holds its keys
    ("M73", "ERROR L49", "factory/uae.p/reference/uae.p.ref.participant-directory.v1.yaml",
     "routingCode: '802420101'", "routingCode: '80242010'", "a participant's routing code is not nine digits"),
    ("M74", "ERROR L49", "factory/uae.p/reference/uae.p.ref.participant-directory.v1.yaml",
     "        headOffice: AE-AJ\n", "        headOffice: AE-XX\n", "a head office names no emirate of the emirates table"),
    ("M75", "ERROR L49", "factory/uae.p/reference/uae.p.ref.emirates.v1.yaml",
     "      - code: AE-DU\n", "      - code: AE-AZ\n", "an edition repeats a key"),
    # L38 (R-05-94, Issue I-279): an action held to the step that led to its hold names an activity a flow of the view reaches
    ("M84", "ERROR L38", "factory/p/views/p.view.operations.v1.yaml",
     "when: { hold: approval, activity: p.screen.fraud.v1 }", "when: { hold: approval, activity: p.screen.fraud.v9 }",
     "a supervisor's release is held to a fraud step no flow has"),
    # L52 (R-11-101, Issue I-276): a bank that binds the rates port takes its rates from the feed and sets none in its configuration
    ("M82", "ERROR L52", "../content-configurations/banks/adib/configuration-set.yaml",
     "\nvalues:\n", "\nvalues:\n  - parameter: p.enrich.fx-rate.v1#rates\n    validFrom: \"2026-11-01\"\n    value:\n      - { from: USD, to: AED, rate: \"3.6725\" }\n",
     "a bank on the rates feed types its rates into its configuration"),
    # L60 (R-09-247, Issue I-278): a bank on the rates feed requires each rate's asOf, so a hand-typed rate is refused like a stale one
    ("M104", "ERROR L60", "../content-configurations/banks/dib/configuration-set.yaml",
     "    value: true\n    validFrom: \"2026-11-24\"\n", "    value: false\n    validFrom: \"2026-11-24\"\n",
     "a bank on the rates feed lets a rate typed by hand stand without its asOf"),
    # L61 (R-05-107, Issue I-185): escalation after a time is off by default and names the role it escalates to
    ("M105", "ERROR L61", "factory/p/views/p.view.operations.v1.yaml",
     "escalateAfter: { type: duration, default: P0D,", "escalateAfter: { type: duration, default: PT4H,", "escalation after a time is on before the bank sets it"),
    ("M106", "ERROR L61", "factory/p/views/p.view.operations.v1.yaml",
     "bounds: { allowed: [Supervisor, BankApprover] }", "bounds: { allowed: [Supervisor, Auditor] }", "an item escalates to a role no one works items with"),
    # L53 (R-11-102, Issue I-276): the studio never makes a patch release of a bank's configuration
    ("M83", "ERROR L53", "examples/bank/configuration-set.yaml",
     "\nrelease: 1.10.0\n", "\nrelease: 1.10.1\n", "the studio makes a patch release of a bank's configuration"),
    # L50 (R-11-51): a value read from a reference table names a published table and its columns
    ("M76", "ERROR L50", "examples/bank/configuration-set.yaml",
     "    value:\n      - { routingCode: \"203320101\", name: Example Bank One }\n      - { routingCode: \"302620122\", name: Example Bank Two }\n",
     "    fromReference: { table: uae.p.ref.participant-directory.v1, columns: { routingCode: routingCode, name: bankName } }\n",
     "a participant list reads a column the participant directory does not have"),
    # L09 (R-01-25): a shared reference table's own words name no bank, whatever its public rows hold
    ("M77", "ERROR L09", "factory/uae.p/reference/uae.p.ref.participant-directory.v1.yaml",
     "title: The licensed banks of the UAE", "title: The licensed banks of the UAE for dib", "a shared reference table's title names a tenant"),
    ("M78", "ERROR L38", "factory/uae.wps/views/uae.wps.view.operations.v1.yaml",
     "line.totalSalary, line.lineStatus]", "line.totalSalary, line.netSalary]", "a fixed report's column is not a declared line field"),
    ("M79", "ERROR L38", "factory/uae.wps/views/uae.wps.view.operations.v1.yaml",
     "      document: uae.wps.sif\n", "      document: uae.wps.payroll\n", "a report of documents lists a kind the view does not declare"),
    ("M80", "ERROR L38", "factory/p/views/p.view.operations.v1.yaml",
     "  unmaskWindow: { type: duration,", "  unmaskWindows: { type: duration,", "a view declares a dial the operations console does not read"),
    # L51 (R-09-118, Issue I-190): the kit is the one place that counts its scenarios; a design text that counts them agrees
    ("M81", "ERROR L51", "design/annex-c-conformance-kit.md",
     "and it is the one place that counts the scenarios", "it holds 55 scenarios, and it is the one place that counts the scenarios",
     "Annex C counts the kit's scenarios again, and wrongly"),
    # L54 (R-03-26, Issue I-303): every attempt is at least once, so an activity with an outside effect carries a stable key
    ("M90", "ERROR L54", "factory/p/activities/p.notify.channel.v1.yaml",
     "effect: write\nidempotencyKey: concat(header.paymentId, ':channel')", "effect: read",
     "a notify activity that says it only reads and carries no idempotency key"),
    ("M91", "ERROR L54", "factory/p/activities/p.notify.channel.v1.yaml",
     "idempotencyKey: concat(header.paymentId, ':channel')", "idempotencyKey: concat(header.paymentId, now())",
     "an idempotency key that reads the clock, so it changes on every attempt"),
    # L57 (R-03-27, Issue I-342): a pure activity names no port; a validate or decide without rule sets is pure only as a script
    ("M97", "ERROR L57", "factory/p/activities/p.validate.duplicate.v1.yaml",
     "effect: read", "effect: pure", "a duplicate check that reads the payment record through a port says it is pure"),
    ("M98", "ERROR L57", "factory/p/activities/p.decide.await-posting-window.v1.yaml",
     "effect: read", "effect: pure", "a decide without rule sets bound to a subflow says it is pure"),
    # L55 (R-09-209 to R-09-212; Issues I-159, I-164, I-235, I-267): what a scheme message declares beside its disposition holds
    ("M92", "ERROR L55", "factory/uae.wps/scheme.yaml",
     "repliesTo: [uae.wps.sif-submission]", "repliesTo: [uae.wps.pif]", "the acknowledgement answers a message the bank receives"),
    ("M93", "ERROR L55", "factory/uae.wps/scheme.yaml",
     "correlationMap: uae.wps.map.ack-correlation.v1", "correlationMap: uae.wps.map.sif-out.v1", "a correlation map that is an outward map"),
    ("M94", "ERROR L55", "factory/uae.wps/scheme.yaml",
     "  - id: uae.wps.pif\n    title: Payment information file, one per agent\n",
     "  - id: uae.wps.pif\n    title: Payment information file, one per agent\n    file: { namePattern: '([0-9' }\n", "a file name pattern that does not compile"),
    ("M95", "ERROR L55", "factory/uae.wps/scheme.yaml",
     "legs:\n  - uae.wps.wf.salary-file-out.v1\n",
     "  - id: uae.wps.test-report\n    title: A report\n    direction: in\n    disposition:\n      reconcile: { activity: p.post.verify.v1, map: uae.wps.map.psi-in.v1, period: P1D }\nlegs:\n  - uae.wps.wf.salary-file-out.v1\n",
     "a report whose reconcile activity is not a reconcile activity"),
    # L58 (R-05-101, Issue I-293): a send after an encryption step reads the encryption step's fact
    ("M99", "ERROR L58", "factory/uae.wps/activities/uae.wps.exchange.send-refund-response.v2.yaml",
     "  - messages\n  - facts.encryption\n", "  - messages\n", "the refund response's send would send the plain file after the subflow encrypts it"),
    # L59 (R-05-102, Issue I-310): every message the bank sends names its one exchange, and no exchange sends two
    ("M100", "ERROR L59", "factory/uae.wps/scheme.yaml",
     "      sentBy: uae.wps.exchange.send-disbursement-cancellation.v2\n", "      sentBy: uae.wps.exchange.send-refund-request.v2\n",
     "the refund request's send sends two messages, so its file would have two kinds"),
    ("M101", "ERROR L59", "factory/uae.wps/scheme.yaml",
     "      sentBy: uae.wps.exchange.send-returns-file.v2\n", "      sentBy: uae.wps.map.rfa-out.v1\n",
     "the returns file is said to be sent by its render, which sends nothing"),
    # L38 (R-05-103, Issue I-337): a view reads the delivery only of a message the bank sends
    ("M102", "ERROR L38", "factory/uae.wps/views/uae.wps.view.operations.v1.yaml",
     'source: "delivery:uae.wps.sif-submission.outcome"', 'source: "delivery:uae.wps.ack.outcome"',
     "a view reads the delivery of the acknowledgement, which the bank receives"),
    # L62 (R-09-250, Issue I-168): a break's order starts by a message of the scheme that starts an order of that kind
    ("M107", "ERROR L62", "factory/uae.wps/scheme.yaml",
     "legs:\n  - uae.wps.wf.salary-file-out.v1\n",
     "  - id: uae.wps.test-report\n    title: A report\n    direction: in\n    disposition:\n      reconcile: { activity: p.reconcile.scheme-report.v1, map: uae.wps.map.psi-in.v1, period: P1D, resolutions: { return: uae.wps.sif } }\nlegs:\n  - uae.wps.wf.salary-file-out.v1\n",
     "a break's return starts by a message that starts an outward payment, not a return"),
    # L56 (R-02-24): a default currency is a country cluster's, an ISO 4217 code; the platform names none
    ("M108", "ERROR L56", "factory/uae.p/activities/uae.p.enrich.default-currency.v1.yaml",
     "default: AED", "default: Dirham", "a cluster's default currency that is not an ISO 4217 code"),
    # L56 (R-05-100, Issue I-282): a parameter marked a ledger account is an activity's string or code
    ("M96", "ERROR L56", "factory/p/activities/p.post.accounting.v2.yaml",
     "  vatAccount:\n    type: string\n", "  vatAccount:\n    type: integer\n", "a ledger account that is a number"),
]


def run_case(case):
    cid, expect, rel, old, new, what = case
    with tempfile.TemporaryDirectory() as top:
        tmp = pathlib.Path(top) / "spec"
        for d in COPY:
            shutil.copytree(ROOT / d, tmp / d, ignore=shutil.ignore_patterns("__pycache__"))
        # the content branch's copy beside spec/: the bank packs (L35) and what the seeds published (L41)
        if CONTENT.is_dir():
            shutil.copytree(CONTENT, pathlib.Path(top) / "content-configurations")
        rel_path = pathlib.Path(rel)
        f = (pathlib.Path(top) / rel_path.relative_to("..")) if rel.startswith("../") else tmp / rel
        text = f.read_text()
        if text.count(old) < 1:
            return cid, "SETUP", "text to mutate not found in %s" % rel
        f.write_text(text.replace(old, new, 1))
        out = subprocess.run([sys.executable, str(tmp / "tools" / "validate.py")], capture_output=True, text=True).stdout
        hit = [l for l in out.splitlines() if l.startswith(expect)]
        return cid, ("CAUGHT" if hit else "MISSED"), (hit[0][:150] if hit else what)


def main():
    base = subprocess.run([sys.executable, str(ROOT / "tools" / "validate.py")], capture_output=True, text=True)
    if base.returncode != 0:
        print("the unmutated repository does not validate; fix that first")
        sys.exit(1)
    bad = 0
    for case in CASES:
        cid, verdict, detail = run_case(case)
        print("%s %-6s %s" % (cid, verdict, detail))
        bad += verdict != "CAUGHT"
    print("%d of %d mutations caught" % (len(CASES) - bad, len(CASES)))
    # every check the validator implements has at least one case here
    implemented = set(re.findall(r'(?:err|warn)\("(L\d\d)"', (ROOT / "tools" / "validate.py").read_text()))
    proven = {c[1].split()[-1] for c in CASES}
    for code in sorted(implemented - proven):
        print("NO CASE for check %s" % code)
        bad += 1
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
