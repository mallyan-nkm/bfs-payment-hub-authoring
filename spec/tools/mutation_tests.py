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
EB = "../content-configurations/banks/example-bank"

WF = "factory/p/workflows/p.wf.corporate.v1.yaml"
SAL = "factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml"
OPV = "factory/uae.wps/views/uae.wps.view.operations.v1.yaml"

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
    # R-05-48, R-05-49: an operations view says only what the store can answer (L37)
    ("M38", "ERROR L37", OPV,
     '"step:submit.read-ack.outcome"', '"step:submit.read-acks.outcome"', "a view's field reads a step the leg does not have"),
    ("M39", "ERROR L37", OPV,
     "source: line.ext.uae.wps.personId, mask: id,", "source: line.ext.uae.wps.personId,", "a view shows a personal value with no mask"),
    ("M40", "ERROR L37", OPV,
     "action: retry, label: Retry the failed step", "action: approve, label: Retry the failed step", "a view offers a hold decision where no flow holds for one"),
    ("M41", "ERROR L37", OPV,
     "columns: [cbResponse, ackReceivedAt]", "columns: [cbResponse, ackAt]", "a view's list column is not a declared field"),
    ("M42", "ERROR L37", OPV,
     "leg: uae.wps.wf.return-in.v1", "leg: uae.wps.wf.return-out.v1", "a view declares a leg the scheme does not have"),
    ("M43", "ERROR L37", OPV,
     "mask: none, oldScreen: \"SIF Summary report, Corporate Name (an organisation's name, shown whole)\" }", "mask: none }",
     "a view shows a value unmasked without naming the old screen that did"),
    ("M44", "ERROR L37", OPV,
     "source: ext.uae.wps.salaryMonth,", "source: ext.uae.wps.salaryMonths,", "a view reads an extension field nobody registers"),
]


def run_case(case):
    cid, expect, rel, old, new, what = case
    with tempfile.TemporaryDirectory() as top:
        tmp = pathlib.Path(top) / "spec"
        for d in COPY:
            shutil.copytree(ROOT / d, tmp / d, ignore=shutil.ignore_patterns("__pycache__"))
        if BANKS.is_dir():
            shutil.copytree(BANKS, pathlib.Path(top) / "content-configurations" / "banks")
        f = tmp / rel
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
