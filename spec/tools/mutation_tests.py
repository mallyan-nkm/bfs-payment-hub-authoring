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
COPY = ("schemas", "factory", "examples", "conformance", "tools")
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
    ("M60", "ERROR L44", "factory/uae.wps/activities/uae.wps.exchange.submit-salary-file.v1.yaml",
     "    default: P1D\n", "    default: P9D\n", "a dial's default is outside its own bounds"),
    ("M61", "ERROR L41", "../content-configurations/artefacts/bindings/p.binding.temporal.v1/1.1.0.yaml",
     "forEachConcurrency: 64", "forEachConcurrency: 32", "a published engine binding release is edited in place (Issue I-83)"),
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
