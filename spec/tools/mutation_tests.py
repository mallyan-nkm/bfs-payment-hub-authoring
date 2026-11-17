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

WF = "factory/p/workflows/p.wf.corporate.v1.yaml"
SAL = "factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml"

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
    ("M07", "ERROR L07", "factory/uae.aani/workflows/uae.aani.handler.proxy-lookup.v1.yaml",
     "call: uae.aani.enrich.proxy-resolve.v1", "call: p.reserve.funds.v1", "a handler calls a block that writes"),
    ("M08", "ERROR L08", SAL,
     "  slot: scheme-leg", "  slot: no-such-slot", "a leg fills a slot that is not declared"),
    ("M09", "ERROR L09", "factory/p/ports/p.port.channel.v1.yaml",
     "title: Originating channel", "title: Originating channel of example-bank", "shared content names a tenant"),
    ("M10", "ERROR L10", "factory/uae.aani/workflows/uae.aani.sub.submit-credit.v1.yaml",
     "message: uae.aani.status-report", "message: uae.aani.status-request-in", "a deliver message is awaited by nobody"),
    ("M11", "WARN  L11", "factory/p/activities/p.reconcile.scheme-report.v1.yaml",
     "invokedBy: schedule\n", "", "a block that nothing calls stops saying what runs it"),
    ("M12", "ERROR L12", "factory/uae.p/rules/uae.p.rule.iban-length.v1.yaml",
     "      finding:", "      fact:", "a validation set holds a rule that yields something other than a finding"),
    ("M13", "ERROR L13", "examples/bank/activities/b.enrich.cost-centre.v1.yaml",
     "  - ext.b.costCentre", "  - payment.creditor", "a hook block writes outside the hook point contract"),
    ("M14", "ERROR L14", "examples/bank/configuration-set.yaml",
     "    value: 25000", "    value: 75000", "a configuration value is outside its bounds"),
    ("M15", "ERROR L15", "factory/uae.aani/tests/AANI-OUT-001.yaml",
     "    scenario: accept\n  - port: p.port.liquidity.v1", "    scenario: no-such-scenario\n  - port: p.port.liquidity.v1",
     "a test bench names a scenario the simulator lacks"),
    ("M16", "ERROR L16", "factory/uae.wps/workflows/uae.wps.sub.submit-salary-file.v1.yaml",
     "message: uae.wps.ack", "message: uae.wps.pif", "a subflow awaits a message that is not a deliver message"),
    ("M17", "ERROR L17", "factory/uae.aani/rules/uae.aani.rule.amount-within-limit.v1.yaml",
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
]


def run_case(case):
    cid, expect, rel, old, new, what = case
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        for d in COPY:
            shutil.copytree(ROOT / d, tmp / d, ignore=shutil.ignore_patterns("__pycache__"))
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
