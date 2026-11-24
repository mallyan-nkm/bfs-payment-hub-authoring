#!/usr/bin/env python3
"""Generate the repetitive factory artefacts (activities, rules, ports, simulators,
extensions), then the pack manifests and the inventory.

Hand-written artefacts (workflows, scheme manifests, tests, hooks, configuration sets)
live beside the generated ones and are picked up by the manifest step.

Run:  python3 tools/build_factory.py
"""
import copy
import hashlib
import json
import pathlib
import re

import yaml

ROOT = pathlib.Path(__file__).resolve().parent.parent
FACTORY = ROOT / "factory"
EXAMPLES = ROOT / "examples"
REL = "1.0.0"
DATE = "2026-11-17"
FOLDER = {"activity": "activities", "rule": "rules", "ruleset": "rules", "table": "rules",
          "port": "ports", "simulator": "simulators", "extension": "extensions", "test": "tests",
          "reference-table": "reference"}
OUT = []  # (base, doc)


def scope_of(name):
    parts = name.split(".")
    return parts[0] if parts[0] in ("p", "b") else ".".join(parts[:2])


def prov(status="confirmed", clauses=None, note=None):
    d = {"origin": "factory", "status": status}
    if clauses:
        d["clauses"] = clauses
    if note:
        d["note"] = note
    return d


PROVISIONAL_WPS = dict(status="provisional", clauses=["CBUAE WPS responsibility guide 2018"],
                       note="From public sources. Confirm against the current CBUAE WPS rulebook.")


def make_iban(bban, country="AE"):
    """A UAE IBAN with valid check digits, for examples."""
    rearranged = bban + "".join(str(ord(c) - 55) for c in country) + "00"
    return "%s%02d%s" % (country, 98 - int(rearranged) % 97, bban)


IBAN_OK = make_iban("0331234567890123456")


# Releases (spec/CLAUDE.md "Versions", R-11-35, R-11-36). A published release is never edited: an artefact that changed after
# its first publish carries its new release here, with the reason, and everything else stays at REL. An interface change is a
# major (a new name), an improvement a minor, a fix a patch. The earlier release stays on the content branch as it was seeded.
# The minor releases. Each was first published at 1.0.0 (CS-2026-11-21-001) and changed after it, so the change is its own release
# (Issue I-83 lists each one). A change that the interface check (L26, studio/src/checks/checks.ts interfaceDiff) calls a major
# is a new major line instead: uae.wps.decide.returns.v2 (an outcome added), uae.wps.map.prc-out.v2 and uae.wps.map.dif-out.v2
# (reads added), uae.wps.map.rtc-in.v2 (writes added) and uae.wps.wf.agent-payment-in.v2 (an exit write added).
RELEASES = {
    # R-12-12: the accounting template's parameters (2026-11-22)
    "p.post.accounting.v1": ("1.1.0", "the accounting template's parameters"),
    "uae.wps.post.return.v1": ("1.1.0", "the accounting template's parameters"),
    # Issue I-39: the core-banking simulator's scenarios for a refused, a twice refused and an unknown credit
    "p.sim.core-banking.v1": ("1.1.0", "scenarios for refused and unknown credits"),
    # Issues I-41, I-51 and R-05-47: the replies' waits are the bank's values
    "uae.wps.exchange.return-credits.v1": ("1.1.0", "replyWait, replyWaitCalendar and uploadHold"),
    # Issue I-41: each acknowledgement names the family of the file it answers
    "uae.wps.sim.wps-inward.v1": ("1.1.0", "the returns scenarios; acknowledgements carry their family"),
    "uae.wps.sim.wps-outward.v1": ("1.1.0", "acknowledgements carry their family"),
    # Issue I-39: the amount rule and the scope of a return
    "uae.wps.table.returns.v1": ("1.1.0", "the amount, the scope and the unverified row"),
    # plan P2: the source's PIF layout (EDR, EVP, PCR) read beside the factory's first layout, and a native code-behind (R-08-16)
    "uae.wps.map.pif-in.v1": ("1.1.0", "reads the source's PIF layout too; native code-behind"),
    # plan P2: the WPS files' native code-behinds, the source's builder or reader (R-08-16)
    "uae.wps.map.rfa-out.v1": ("1.1.0", "the bank's headRoutingCode; native code-behind"),
    "uae.wps.map.ack-in.v1": ("1.1.0", "native code-behind"),
    # the new major lines, at 2.0.0 (R-01-08: the major is in the name); their v1 lines stay at 1.0.0 as first published
    "uae.wps.decide.returns.v2": ("2.0.0", "the outcome unverified (Issue I-39)"),
    "uae.wps.map.prc-out.v2": ("2.0.0", "reads the items and the file name; the bank's agentId; native code-behind (plan P2)"),
    "uae.wps.map.dif-out.v2": ("2.0.0", "reads the PRC's name; the bank's agentId; native code-behind (plan P2)"),
    "uae.wps.map.rtc-in.v2": ("2.0.0", "writes the payment-level creditor account; a script-behind and a native code-behind (plan P2)"),
    # Owner's decisions of 2026-11-22 (Issues I-88 and I-100): the WPS fields the source's screens showed, and the employer the
    # labour ministry has suspended. Each a minor: parameters, fields or rules added, no read, write, outcome or port changed.
    "uae.wps.ext.salary.v1": ("1.1.0", "the allowances, the leave encashment, the upload channel and the employer's labour ministry status"),
    "uae.wps.map.ack-in.v1": ("1.2.0", "the NAK's per-line errors (DER) as status.transactions, pain.002 TxInfAndSts; native code-behind"),
    "uae.wps.ruleset.salary-file.v1": ("1.1.0", "refuses a file whose employer the labour ministry has suspended"),
    # Owner's decisions of 2026-11-22 (Issues I-115 to I-118). Each a minor or a patch of an artefact the content branch holds.
    # I-116: the acknowledgement wait may be as short as fifteen minutes (the source's ADIB waits twenty); a bound widened, a minor.
    # Its 1.1.0 added ackWait and ackWaitCalendar (Issues I-41, I-51, R-05-47).
    "uae.wps.exchange.submit-salary-file.v1": ("1.2.0", "ackWait from PT15M (Issue I-116)"),
    # I-117: the runtime implements the encryption block as well (R-08-16), on the bank's OpenPGP binding.
    "p.protect.encrypt-file.v1": ("1.1.0", "native code-behind (Issue I-117)"),
    # I-118: the runtime asks the labour ministry's register through the bank's binding (R-12-16).
    "uae.wps.enrich.employer-status.v1": ("1.1.0", "native code-behind on the labour ministry register (Issue I-118)"),
    # I-115: each line's bank name from the participant directory and its account type from the channel's upload (R-12-18). Its
    # 1.1.0 read the EVP allowances and the channel's upload: initiator, debtor name and account, channel; amounts as decimal strings (I-109).
    "uae.wps.map.sif-in.v1": ("1.2.0", "each line's bank name from the participant directory, its account type from the channel's upload (Issue I-115)"),
    # The refund order (R-12-19 to R-12-22, Issue I-133): a refund is an order of the platform's p.wf.refund.v1, WPS its leg. Each a
    # minor of an artefact the content branch holds: a row, a field or a script added, no read, write, outcome or port changed.
    "p.table.workflow-select.v1": ("1.1.0", "a refund order runs p.wf.refund.v1"),
    "uae.wps.ext.salary.v1": ("1.2.0", "the refund request instruction's fields: the establishment, the reason and the payroll file of each line"),
    "uae.wps.map.rri-in.v1": ("1.1.0", "a script-behind for the source's RRI layout; the earlier payment by id (header.originalPaymentId)"),
    # Issue I-136, R-12-23 to R-12-25: the posting reads the order's FX and charges (p.post.accounting.v2, a new major line: its reads
    # and its parameters changed), FX and charges gain native code-behinds, and an outward refund order runs p.wf.refund-out.v1.
    "p.post.accounting.v2": ("2.0.0", "reads facts.fx and facts.charges; the converted amount, the charge and the VAT as entries of their own; no slabs of its own"),
    "p.enrich.fx-rate.v1": ("1.1.0", "native code-behind (Issue I-136)"),
    "p.enrich.charges.v1": ("1.1.0", "native code-behind (Issue I-136)"),
    "p.table.workflow-select.v1": ("1.2.0", "an inward refund order runs p.wf.refund.v1, an outward one p.wf.refund-out.v1"),
    # Owner's decision of 2026-11-23 (R-12-27, Issue I-149): the salary file's employer is looked up in the bank's party register by
    # its establishment id, carried as the debtor's organisation id (Dbtr/Id/OrgId/Othr). A field written under batch, a minor.
    "uae.wps.map.sif-in.v1": ("1.3.0", "the employer's establishment id as the debtor's organisation id (R-12-27)"),
    # Owner's decision of 2026-11-23 (Issue I-141, R-09-87): the WPS returns stay. Inward payments run p.wf.inward.v2 (a new major
    # line, hand-written), whose slot contract scheme-leg@2 lets the leg write facts.returns; the WPS inward legs fill it.
    "p.table.workflow-select.v1": ("1.3.0", "inward payments and returns run p.wf.inward.v2"),
    # Owner's decision of 2026-11-23 (Issue I-127, R-09-88): a hold's expiry counts business days of the bank's business calendar.
    # A parameter added with a default, a minor.
    "p.hold.repair.v1": ("1.1.0", "expiryCalendar: the expiry counted on the bank's business calendar"),
    "p.hold.approval.v1": ("1.1.0", "expiryCalendar: the expiry counted on the bank's business calendar"),
    # Owner's decision of 2026-11-23 (Issue I-195, R-05-85): the salary file's name from the inbound message's fileName, the name the
    # File API received it under, when the file carries no `# file:` line. The script reads more of the same message, a minor.
    "uae.wps.map.sif-in.v1": ("1.4.0", "the file's name from the inbound message's fileName when the file carries no # file: line (Issue I-195)"),
    # Owner's decision of 2026-11-23 (Issue I-192, R-09-162): the File API as a port that sends data out. The salary file is rendered
    # by the source's SIF writer and sent on the bank's binding of the WPS port by native code-behinds; a code-behind added, a minor.
    "uae.wps.map.sif-out.v1": ("1.1.0", "native code-behind: the source's SIF writer, the file kept by the host (Issue I-192)"),
    "uae.wps.exchange.send-salary-file.v1": ("1.1.0", "native code-behind: the kept file sent on the bank's binding of the WPS port (Issue I-192)"),
    # Owner's decision of 2026-11-23 (docs/OWNER-ACTIONS.md A2, Issue I-189, R-09-178): the rates feed gives each rate its asOf, and
    # the bank's staleness limit maxRateAge refuses an older one as no-rate with fx.RateStale. A parameter added with a default, and a
    # write's fields: no read, write path, outcome or port changed, a minor.
    "p.enrich.fx-rate.v1": ("1.2.0", "maxRateAge: a rate older than the bank's staleness limit is refused (fx.RateStale)"),
    # Issue I-266 (R-09-182): the salary file's send reads the encryption step's fact and sends the encrypted file where the bank's
    # binding encrypted it. A read added is a major (check L26): a new line, uae.wps.exchange.send-salary-file.v2, at 2.0.0; the v1
    # line stays at 1.1.0 as published.
    "uae.wps.exchange.send-salary-file.v2": ("2.0.0", "reads facts.encryption and sends the encrypted file where there is one (Issue I-266)"),
}
# Script-behinds re-released with their activity: the script is published as scripts/<name>@<release>.ts beside the earlier one.
# Issue I-268, R-09-213 (factory 1.20.0): the scheme's other sends on the WPS port gain native code-behinds, a minor each.
for _n in ("send-refund-request", "send-receipt-confirmation", "send-disbursement-file", "send-returns-file", "send-disbursement-cancellation", "send-refund-response"):
    RELEASES["uae.wps.exchange.%s.v1" % _n] = ("1.1.0", "native code-behind: the kept file sent on the bank's binding of the WPS port (Issue I-268)")
# Issue I-282, R-05-100 (factory 1.20.0): an accounting activity's ledger accounts say what they are (role ledger-account, with
# their kind), so the ledger view lists them by role, not by name. A parameter's description added to, a minor each.
RELEASES["p.post.accounting.v1"] = ("1.2.0", "its ledger accounts marked role ledger-account (Issue I-282)")
RELEASES["p.post.accounting.v2"] = ("2.1.0", "its ledger accounts marked role ledger-account (Issue I-282)")
RELEASES["uae.wps.post.return.v1"] = ("1.2.0", "its ledger accounts marked role ledger-account (Issue I-282)")
# Issue I-153, R-02-22 (factory 1.20.0): the salary file's intake map writes the employer's CIF the channel declared at upload as a
# further organisation id of the debtor, scheme CIF. A field written under batch, a minor; its script at scripts/...@1.5.0.ts.
RELEASES["uae.wps.map.sif-in.v1"] = ("1.5.0", "the employer's CIF from the channel's upload as the debtor's organisation id, scheme CIF (Issue I-153)")
# Owner's decision of 2026-11-24 (Issue I-278, R-09-247, factory 1.22.0): requireAsOf, a bank on the rates feed refuses a rate with
# no asOf like a stale one (fx.RateUndated). A parameter added with a default, a minor; its script at scripts/...@1.3.0.ts.
RELEASES["p.enrich.fx-rate.v1"] = ("1.3.0", "requireAsOf: a bank on the rates feed refuses a rate with no asOf (fx.RateUndated)")
SCRIPT_RELEASES = {"uae.wps.map.pif-in.v1", "uae.wps.map.sif-in.v1", "uae.wps.map.rri-in.v1", "p.enrich.fx-rate.v1"}
# The release of each pack's lock file, and of the inventory: a pack whose content changed is a new release of the pack.
# 1.5.0: the platform's error dictionary 1.1.0, hand-written in factory/p/errors, with the codes of Issue I-126 (p, and the
# packs that depend on it), and the example bank's lock file names the schemes it licenses (b; R-11-37, Issue I-111).
# 1.6.0: the order's posting that reads FX and charges, the outward refund order, the WPS parks (R-12-23 to R-12-26, I-136).
# 1.7.0: reference tables (R-01-25), the party register lookup and its WPS step (R-12-27; owner's decisions of 2026-11-23).
# 1.8.0: the operations views' dials (turnaround, unmask window, retention) and the WPS fixed reports (R-05-66 to R-05-70):
# p.view.operations.v1 1.4.0 and uae.wps.view.operations.v1 1.5.0, written by hand; the example bank's calendar and cut-offs (R-11-52).
# 1.9.0: p.wf.inward.v2 and the WPS inward legs in its scheme-leg@2 (Issue I-141, R-09-87); the holds' expiry on the business
# calendar (Issue I-127, R-09-88); p.view.operations.v1 1.5.0 (R-09-91).
# 1.10.0: the platform's error dictionary 1.2.0, hand-written in factory/p/errors: another bank's content has its own code,
# package.WrongBank (Issue I-154), and service.ApproverRequired names the bank approver (Issue I-155, R-09-115).
# 1.11.0: the bank's cut-off (p.decide.cut-off.v1, R-12-28, Issues I-186 and I-191) after the WPS salary file's business date
# (uae.wps.wf.salary-file-out.v1 1.4.0), and the WPS operations view 1.6.0 that shows it (R-05-72), both written by hand.
# 1.12.0: the platform's error dictionary 1.3.0: a native module that references the runtime image beyond the native contract has
# its own code, package.NativeImageReference (R-09-125, Issue I-220), and each refusal of the native host its native. code (R-09-128, I-230).
# 1.15.0 (change set CS-2026-11-23-010; 1.13.0 and 1.14.0 are other changes'): the platform's notification port
# p.port.notification.v1 and event-streaming port p.port.event-stream.v1, each with its simulator (R-11-85; owner's decisions of
# 23 November 2026, Issue I-182, docs/OWNER-ACTIONS.md A5), and p.view.operations.v1 1.6.0, written by hand: the turnaround limit
# counted in business days of the bank's calendar (R-05-81).
# 1.17.0 (change set CS-2026-11-23-012; 1.16.0 is another change's): the exchange-rate feed (R-11-95; owner's decision of 23 November
# 2026, docs/OWNER-ACTIONS.md A2, Issue I-189): the rates port p.port.fx-rates.v1 and its simulator, p.enrich.fx-rate.v1 1.2.0 with
# the staleness limit maxRateAge (R-09-178), and the platform's error dictionary 1.7.0, hand-written, with fx.RateStale and
# fx.FeedFailed; the example bank's rates table b.ref.fx-rates.v1 and its configuration set 1.10.0.
# 1.18.0 (change set CS-2026-11-23-013; 1.16.0 and CS-2026-11-23-011, held for this change, was never published: it was merged after 1.17.0): the platform's
# error dictionary 1.8.0, hand-written in factory/p/errors, 1.7.0's entries and distribution.ActivationRaced (Issue I-239) and
# port.CircuitOpen (Issue I-259); the salary file's intake map 1.4.0, which takes the file's name from the File API (Issue I-195,
# R-05-85); native code-behinds of the salary file's render and send, whose send goes out on the bank's binding of the WPS port, the
# File API's outbox where the bank binds it so (Issue I-192, R-09-162).
# 1.19.0 (change set CS-2026-11-24-014): the issues before a real bank goes live (R-11-100): the platform's error dictionary 1.9.0,
# hand-written in factory/p/errors, with signing.OutOfScope (Issue I-277, R-09-184); p.view.operations.v1 1.7.0, written by hand:
# a suspect payment's release needs a supervisor's second approval (Issue I-279, R-05-94); the salary file's send v2, which sends the
# encrypted file (Issue I-266, R-09-182), called by the submit block's subflow 1.3.0.
# 1.20.0 (change set CS-2026-11-24-015; 1.19.0 and CS-2026-11-24-014 are another change's): the scheme manifest's message rules,
# uae.wps 1.5.0 (repliesTo and the ACK's correlation map uae.wps.map.ack-correlation.v1, R-09-211, R-09-212; Issues I-235, I-267),
# native code-behinds of WPS's other sends (Issue I-268), the accounting activities' ledger-account parameters (Issue I-282), the
# salary file's intake map writing the employer's CIF (Issue I-153), and the platform's error dictionary 1.10.0, on 1.9.0.
# 1.22.0 (change set CS-2026-11-24-017; 1.21.0 and CS-2026-11-24-016 are another change's): wave 7B, the owner's decisions of 24
# November 2026: p.enrich.fx-rate.v1 1.3.0 with requireAsOf (Issue I-278, R-09-247); p.view.operations.v1 1.8.0, written by hand:
# pause and resume offered, and escalation after a time (Issue I-185); the salary file's intake map 1.6.0, the employer's e-mail
# (Issues I-153, I-314); and the platform's error dictionary 1.12.0, on 1.11.0.
PACK_RELEASES = {"p": "1.22.0", "uae.p": "1.22.0", "uae.wps": "1.22.0", "b": "1.22.0"}
FACTORY_RELEASE = "1.22.0"
# The schemes each bank pack licenses (R-11-37, Issue I-111): a distribution of the bank carries these and no other.
LICENSED_SCHEMES = {"example-bank": ["uae.wps"]}


def emit(doc, base=FACTORY):
    if doc.get("name") in RELEASES and "release" in doc:
        doc["release"] = RELEASES[doc["name"]][0]
    OUT.append((base, doc))
    return doc


# ---------------------------------------------------------------- activities
BUILTIN = {"validate": "rule-service", "decide": "rule-service"}


def act(name, title, reads, writes, outcomes, effect, *, level=None, mode=None, ports=None,
        rule_sets=None, policy=None, comp=None, params=None, map_=None, service=None,
        lang="typescript", p=None, base=FACTORY, invoked_by=None, sub=None, script=False, native=False):
    typ = name.split(".")[-3]
    doc = {"kind": "activity", "name": name, "release": REL, "title": title, "type": typ}
    if mode:
        doc["mode"] = mode
    if level:
        doc["level"] = level
    if invoked_by:
        doc["invokedBy"] = invoked_by
    doc["reads"] = reads
    doc["writes"] = writes
    doc["outcomes"] = [{"name": o, "primary": True} if i == 0 else {"name": o} for i, o in enumerate(outcomes)]
    if effect in ("read", "write", "external"):
        doc["errors"] = [{"name": "TransientError", "retryable": True}]
        if effect != "read":
            doc["errors"].append({"name": "BusinessReject", "retryable": False})
    doc["effect"] = effect
    if effect in ("write", "external"):
        doc["idempotencyKey"] = "concat(header.paymentId, ':%s')" % name.split(".")[-2]
    if ports:
        doc["ports"] = ports
    if rule_sets:
        doc["ruleSets"] = rule_sets
    if params:
        doc["parameters"] = params
    if policy:
        doc["policy"] = policy
    if comp:
        doc["compensation"] = comp
    if map_:
        doc["map"] = map_
    svc = None if script else (service or BUILTIN.get(typ))
    stem = "%s@%s" % (name, RELEASES[name][0]) if name in SCRIPT_RELEASES else name
    doc["implementation"] = {"binding": "subflow", "subflow": sub} if sub else ({"binding": "builtin", "service": svc} if svc
                             else {"binding": "script", "language": lang, "script": "scripts/%s.%s" % (stem, "ts" if lang == "typescript" else "py")})
    # R-08-16: the runtime's native host implements the same contract; a run that gives it the activity calls the host
    if native:
        doc["implementation"]["native"] = True
    doc["provenance"] = prov(**(p or {}))
    return emit(doc, base)


SHORT = {"attemptTimeout": "PT3S", "totalTimeout": "PT8S",
         "retry": {"maxAttempts": 3, "firstInterval": "PT1S", "backoff": 2, "nonRetryable": ["BusinessReject"]}}
STD = {"attemptTimeout": "PT10S", "totalTimeout": "PT1M",
       "retry": {"maxAttempts": 3, "firstInterval": "PT2S", "backoff": 2, "nonRetryable": ["BusinessReject"]}}

# platform
act("p.validate.canonical.v1", "Validate the canonical payment", ["header", "payment"], ["facts.validation"],
    ["pass", "repair", "reject"], "pure", rule_sets=["p.ruleset.canonical.v1"])
act("p.validate.duplicate.v1", "Duplicate check",
    ["header.endToEndId", "payment.instructedAmount", "payment.debtorAccount", "payment.creditorAccount"],
    ["facts.duplicate"], ["unique", "duplicate"], "read", ports=["p.port.payment-record.v1"], service="payment-record")
act("p.validate.limits.v1", "Platform limits", ["header", "payment.instructedAmount"], ["facts.validation"],
    ["pass", "repair", "reject"], "pure", rule_sets=["p.ruleset.limits.v1"])
act("p.enrich.payment-record.v1", "Read the payment record", ["header"], ["status", "facts.record"],
    ["found", "not-found"], "read", ports=["p.port.payment-record.v1"], service="payment-record")
act("p.screen.sanctions.v1", "Sanctions screening",
    ["payment.debtor", "payment.creditor", "payment.debtorAgent", "payment.creditorAgent", "payment.remittanceInformation"],
    ["facts.screening.sanctions"], ["clear", "hit", "pending"], "external", ports=["p.port.sanctions.v1"],
    policy=STD, service="screening")
act("p.screen.fraud.v1", "Fraud screening", ["header", "payment"], ["facts.screening.fraud"],
    ["clear", "suspect"], "external", ports=["p.port.fraud.v1"], policy=STD, service="screening")
act("p.decide.workflow.v1", "Select the platform workflow", ["header.direction", "header.segment", "header.scheme"],
    ["facts.workflow"], ["decided", "no-match"], "pure", rule_sets=["p.table.workflow-select.v1"], invoked_by="front-door")
act("p.decide.inward-disposition.v1", "Accept, refuse or review an inward payment", ["facts.validation", "facts.duplicate", "facts.screening"],
    ["facts.disposition"], ["accept", "refuse", "review"], "pure", rule_sets=["p.table.inward-disposition.v1"])
act("p.decide.rail.v1", "Choose the rail and record the reason",
    ["header", "payment.instructedAmount", "payment.requestedExecutionDate", "payment.categoryPurpose"],
    ["facts.route"], ["decided", "no-match"], "pure", rule_sets=["p.table.rail-choice.v1"])
act("p.reserve.funds.v1", "Reserve funds in the hub",
    ["payment.debtorAccount", "payment.instructedAmount", "batch.controlSum"], ["facts.reservation"],
    ["reserved", "insufficient"], "write", ports=["p.port.core-banking.v1"], policy=STD,
    comp="p.reserve.release.v1", service="reservation")
act("p.reserve.release.v1", "Release a reservation", ["facts.reservation"], ["facts.reservation"],
    ["released"], "write", ports=["p.port.core-banking.v1"], policy=STD, service="reservation")
# The accounting template's values (R-06-21): transaction codes, narration, accounts, charge slabs and VAT. A bank sets
# its own; each is bounded. The defaults are placeholders that name no bank and no real account.
TXN = {"pattern": "^[A-Z0-9]{2,10}$"}
NARR = {"pattern": "^[A-Za-z0-9 .,:/()-]{1,35}$"}
ACCT = {"pattern": "^[A-Za-z0-9-]{1,34}$"}


def txn(default, what):
    return {"type": "code", "default": default, "bounds": dict(TXN), "description": "Core-banking transaction code of %s." % what}


def account(default, what, kind):
    # Issue I-282, R-05-100: a ledger account says so, with its kind, so the ledger view lists it whatever its name.
    return {"type": "string", "default": default, "bounds": dict(ACCT), "description": "The account %s." % what,
            "role": "ledger-account", "ledgerKind": kind}


ACCOUNTING = {
    "template": {"type": "code", "default": "default", "description": "Accounting template id."},
    "transactionCode": txn("PAY", "a payment's legs"),
    "reversalTransactionCode": txn("REV", "a reversal's legs"),
    "chargeTransactionCode": txn("CHG", "the bank's charge"),
    "schemeChargeTransactionCode": txn("SCH", "the scheme's charge"),
    "vatTransactionCode": txn("VAT", "the VAT on a charge"),
    "narration": {"type": "string", "default": "Payment", "bounds": dict(NARR), "description": "Narration of a payment's legs; the payment's reference follows it."},
    "chargeNarration": {"type": "string", "default": "Bank charge", "bounds": dict(NARR), "description": "Narration of the charge and VAT legs."},
    "settlementAccount": account("GL-SETTLEMENT", "a scheme's payments settle through (the nostro)", "settlement"),
    "suspenseAccount": account("GL-SUSPENSE", "refused credits wait in", "suspense"),
    "chargeIncomeAccount": account("GL-CHARGES", "charges are credited to", "income"),
    "vatAccount": account("GL-VAT", "VAT on charges is credited to", "vat"),
    "glAccountTypes": {"type": "list", "default": ["SETTLEMENT", "SUSPENSE", "INCOME", "EXPENSE"], "bounds": {"pattern": "^[A-Z]{2,12}$"},
                       "description": "The kinds of general-ledger account the template's accounts are."},
    "chargeSlabs": {"type": "list", "default": [], "bounds": {"min": 0, "max": 1000000},
                    "description": "Charge slabs, each {upTo, charge} in the payment's currency: the first slab whose upTo is not below the amount gives the charge. None, no charge."},
    "vatPercent": {"type": "decimal", "default": 0, "bounds": {"min": 0, "max": 100}, "description": "VAT on a charge, in per cent."},
}
act("p.post.accounting.v1", "Post with an accounting template", ["header", "payment", "facts.route", "facts.reservation"],
    ["facts.posting"], ["posted", "rejected", "unknown"], "write", ports=["p.port.core-banking.v1"], policy=STD,
    comp="p.post.reverse.v1", service="accounting", params=ACCOUNTING)
# The order's posting (R-12-23, Issue I-136): a new major line, so payments on v1 keep it. It reads what the platform's own steps
# found (facts.fx, the converted amount of each line and of the order; facts.charges, each line's charge and VAT) and prices nothing
# itself: its template has no slabs. Each line posts its converted amount between the line's account and the settlement account (a
# line that names a debtor account is debited, one that names only a creditor account is credited), then its charge and its VAT,
# each debited from the line's account to the template's income and VAT accounts, as entries of their own (camt.054 Ntry, ChrgsInf).
# Exact decimals (R-02-17): the script adds with rules.money; the native code-behind on decimal gives the same entries.
ACCOUNTING_V2 = {k: v for k, v in ACCOUNTING.items() if k not in ("chargeSlabs", "vatPercent", "schemeChargeTransactionCode")}
act("p.post.accounting.v2", "Post an order's converted amounts, charges and VAT with an accounting template",
    ["header", "payment", "items[]", "facts.fx", "facts.charges"], ["facts.posting"], ["posted", "rejected", "unknown"], "write",
    level="batch", ports=["p.port.core-banking.v1"], policy=STD, comp="p.post.reverse.v1", params=ACCOUNTING_V2, script=True, native=True)
act("p.post.verify.v1", "Verify the posting really happened", ["facts.posting"], ["facts.postingVerified"],
    ["verified", "mismatch"], "read", ports=["p.port.core-banking.v1"], service="accounting")
act("p.post.reverse.v1", "Reverse a posting", ["facts.posting"], ["facts.posting"], ["reversed"], "write",
    ports=["p.port.core-banking.v1"], policy=STD, service="accounting")
# FX and charges on an order (R-12-20, Issue I-133): platform activities, never a scheme's. The rates and the charge slabs are the
# bank's values (dials), effective-dated in its configuration releases; the factory's defaults are empty, so an order in a currency
# other than its account's has no rate and an order bears no charge until the bank sets its own. Exact decimals (R-02-17): the
# scripts compute with rules.money, the rule language's arithmetic, and round half away from zero only where a value says so.
RATE_LIST = {"rates": {"type": "list", "default": [], "bounds": {"min": 0, "max": 1000000},
                       "description": "The bank's exchange rates, each {from, to, rate} and, from the rates feed, asOf (the instant the bank's rates service gave it): one unit of from is rate units of to. None, and only a line in its account's own currency converts."},
             # R-09-178 (owner's decision of 2026-11-23, Issue I-189): a rate the feed gave is refused once older than this.
             "maxRateAge": {"type": "duration", "default": "PT24H", "bounds": {"min": "PT1H", "max": "P7D"},
                            "description": "How old a rate with an asOf may be: an older one is refused (the line is no-rate, facts.fx.code fx.RateStale). A rate with no asOf, set by hand, is not aged unless requireAsOf is set."},
             # R-09-247 (owner's decision of 2026-11-24, Issue I-278): a bank on the rates feed sets it, so a hand-typed rate is refused too.
             "requireAsOf": {"type": "boolean", "default": False,
                             "description": "Whether every rate must carry its asOf: a bank on the rates feed sets it, so that a rate with no asOf, set by hand, is refused like a stale one (the line is no-rate, facts.fx.code fx.RateUndated). False: a rate with no asOf is not aged."}}
act("p.enrich.fx-rate.v1", "Convert each line at the bank's exchange rate", ["payment.instructedAmount", "payment.debtorAccount", "items[]"],
    ["facts.fx", "items[]"], ["converted", "no-rate"], "pure", level="batch", params=RATE_LIST, script=True, native=True)
CHARGE_DIALS = {
    "chargeSlabs": {"type": "list", "default": [], "bounds": {"min": 0, "max": 1000000},
                    "description": "Charge slabs, each {upTo, fixed, percent} in the line's converted currency: the first slab whose upTo is not below the line's amount gives a fixed charge plus a percentage of the amount. None, no charge."},
    "minCharge": {"type": "decimal", "default": 0, "bounds": {"min": 0, "max": 1000000}, "description": "The least charge on a line a slab prices; 0, no floor."},
    "maxCharge": {"type": "decimal", "default": 0, "bounds": {"min": 0, "max": 1000000}, "description": "The most charge on a line a slab prices; 0, no ceiling."},
    "vatPercent": {"type": "decimal", "default": 0, "bounds": {"min": 0, "max": 100}, "description": "VAT on a charge, in per cent, each line's rounded to two decimals."},
}
act("p.enrich.charges.v1", "Price the bank's charge and its VAT on each line", ["payment.instructedAmount", "items[]", "facts.fx"],
    ["facts.charges", "items[]"], ["priced", "none"], "pure", level="batch", params=CHARGE_DIALS, script=True, native=True)
# Posting windows (plan P2): the hours core banking takes postings, the bank's values. The decision reads the engine's time; the
# composite waits and asks again while the window is closed, so a payment waits for the window instead of retrying into it
# (the source's AwaitCbPostingWindow). The defaults are always open.
HHMM = {"pattern": "^([01][0-9]|2[0-3]):[0-5][0-9]$"}
WINDOW = {"windowStart": {"type": "string", "default": "00:00", "bounds": dict(HHMM), "description": "When core banking starts taking postings, HH:mm business-local."},
          "windowEnd": {"type": "string", "default": "23:59", "bounds": dict(HHMM), "description": "When it stops, HH:mm business-local, inclusive. Before the start is an overnight window."}}
act("p.decide.posting-window.v1", "Is core banking taking postings now", ["header"], ["facts.postingWindow"], ["open", "closed"], "read",
    params=WINDOW, script=True, native=True, p=dict(status="provisional", clauses=["TransactHub source 666f1ee"],
    note="The source's call-hours gate (CbCallWindowGate). The bank sets the window."))
# The bank's cut-off (R-12-28, Issues I-186 and I-191): a file accepted after the cut-off of its scheme and direction counts for
# the next business day, or is refused, as the bank's row says. Its lists are the bank's values, each read from a reference table
# of the bank's configuration release (fromReference, R-11-51): the cut-offs (b.ref.cut-offs.v1), the bank's own closing days
# (b.ref.bank-calendar.v1) and the market's non-business days (uae.p.ref.business-calendar.v1). Shared content names no bank (L09),
# so every default is empty: no cut-off, and Saturday and Sunday as the only non-business days.
CUT_OFF = {
    "cutOffs": {"type": "list", "default": [],
                "description": "The cut-off of each scheme and direction the bank sets: each a scheme, a direction (inward or outward), cutOff (HH:mm local), timeZone (IANA) and afterCutOff (next-business-day or refuse). Empty by default: no cut-off."},
    "closingDays": {"type": "list", "default": [],
                    "description": "The days the bank itself closes, beyond the market's: each a day (YYYY-MM-DD) and a kind (closed, half-day). A closed day is no business day."},
    "earlyClosings": {"type": "list", "default": [],
                      "description": "The bank's half days: each a day (YYYY-MM-DD) and closesAt (HH:mm local). The cut-off that day is the earlier of the two."},
    "nonBusinessDays": {"type": "list", "default": [],
                        "description": "The market's non-business days: each a day, a weekday name (saturday) or a date (YYYY-MM-DD). Empty by default: Saturday and Sunday."},
}
act("p.decide.cut-off.v1", "Take a file for today or the next business day, by the bank's cut-off", ["header"],
    ["header.businessDate", "facts.cutOff"], ["taken", "refused"], "read", params=CUT_OFF, script=True,
    p=dict(status="provisional", note="Where the step runs, what moves and what a non-business day does are the recommendation of Issue I-191, not a rule of the spec or the source."))
act("p.decide.await-posting-window.v1", "Wait for core banking's posting window", ["header"], ["facts.postingWindow"], ["open", "closed"], "read",
    sub="p.sub.await-posting-window.v1", p=dict(status="provisional", clauses=["TransactHub source 666f1ee"],
    note="The source's AwaitCbPostingWindow: the payment waits for the window. Asked again every fifteen minutes for a day."))
# File encryption (R-03-24, R-12-13, Issue I-87): a file the bank sends is encrypted for its recipient before it leaves. The key
# service is a port the bank binds (OpenPGP on the runtime); the recipient's key is named by id, a parameter the bank sets, and
# held by its binding by reference only. Encrypting changes nothing in the bank, so it is a read and may be retried.
act("p.protect.encrypt-file.v1", "Encrypt the outgoing file for its recipient", ["header", "messages"], ["facts.encryption"],
    ["encrypted", "refused"], "read", ports=["p.port.encryption.v1"], policy=SHORT, service="encryption", native=True,
    params={"recipientKey": {"type": "code", "default": "recipient", "bounds": {"pattern": "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"},
                             "description": "The id of the recipient's public key, as the bank's encryption binding names it. The key itself is never a value: the binding holds it by reference."}})
# The pair of the encryption block (Issue I-117): a file the bank receives encrypted to its own key is decrypted before it is read,
# by the front door, through the bank's binding of p.port.decryption.v1 (OpenPGP, or the source's GnuPG key folder on the runtime).
act("p.protect.decrypt-file.v1", "Decrypt a received file with the bank's own key", ["header", "messages"], ["messages", "facts.decryption"],
    ["decrypted", "refused"], "read", ports=["p.port.decryption.v1"], policy=SHORT, service="decryption", native=True, invoked_by="front-door")
act("p.notify.channel.v1", "Tell the originating channel", ["header", "status"], ["facts.notified"], ["notified"],
    "write", ports=["p.port.channel.v1"], policy=STD)
act("p.notify.publish-fact.v1", "Publish a fact outward", ["header", "status"], ["facts.published"], ["notified"],
    "write", ports=["p.port.facts-out.v1"], policy=STD)
act("p.hold.repair.v1", "Park for repair", ["header", "facts.validation"], ["facts.repair", "payment", "batch"],
    ["released", "rejected", "expired"], "write", ports=["p.port.operations-queue.v1"], service="operations",
    params={"expiry": {"type": "duration", "default": "P2D", "description": "How long a repair item may wait, counted on expiryCalendar."},
            "expiryCalendar": {"type": "code", "default": "business", "bounds": {"allowed": ["business", "elapsed"]},
                               "description": "How the expiry is counted: business days of the bank's business calendar (the reference table of domain business-calendar), or elapsed time."}})
act("p.hold.approval.v1", "Park for approval", ["header", "facts.screening"], ["facts.approval"],
    ["approved", "declined", "expired"], "write", ports=["p.port.operations-queue.v1"], service="operations",
    params={"expiry": {"type": "duration", "default": "P1D", "description": "How long an approval may wait, counted on expiryCalendar."},
            "expiryCalendar": {"type": "code", "default": "business", "bounds": {"allowed": ["business", "elapsed"]},
                               "description": "How the expiry is counted: business days of the bank's business calendar (the reference table of domain business-calendar), or elapsed time."}})
act("p.reconcile.scheme-report.v1", "Match a scheme report to the record", ["header", "status", "messages"],
    ["facts.reconciliation"], ["matched", "unmatched"], "read", ports=["p.port.payment-record.v1"], invoked_by="schedule")

# cluster platform
act("uae.p.validate.iban.v1", "UAE IBAN checks", ["payment.debtorAccount", "payment.creditorAccount"],
    ["facts.validation"], ["pass", "reject"], "pure", rule_sets=["uae.p.ruleset.iban.v1"])
act("uae.p.enrich.participant.v1", "Look up participants by routing code", ["payment.debtorAgent", "payment.creditorAgent"],
    ["facts.participants"], ["found", "unknown"], "read", ports=["uae.p.port.participant-directory.v1"])
act("uae.p.enrich.business-date.v1", "Set the UAE business date", ["header.receivedAt"], ["header.businessDate"],
    ["set"], "read", ports=["uae.p.port.calendar.v1"])

# WPS (provisional)
W = "ext.uae.wps"
WP = PROVISIONAL_WPS


def wmap(name, title, direction, model, reads, writes, outcomes=("mapped", "unmappable"), params=None, native=False, **kw):
    m = {"direction": direction, "sourceModel": model, "roundTrip": direction == "in"}
    m.update(kw)
    return act(name, title, reads, writes, list(outcomes), "pure", map_=m, p=WP, params=params, native=native)


# The bank's identities in the files the agent writes (plan P2, the source's InwardPrcParameters and InwardRfaParameters).
AGENT_ID = {"agentId": {"type": "code", "default": "000", "bounds": {"pattern": "^[A-Za-z0-9]{3}$"},
                        "description": "The bank's three-character WPS agent id, in the names of its PRC and DIF files. The default names no bank."}}
HEAD_ROUTING = {"headRoutingCode": {"type": "code", "default": "0000", "bounds": {"pattern": "^[A-Za-z0-9]{4,20}$"},
                                    "description": "The bank's head-office routing code; characters 2 to 4 begin the name of its returns file. The default names no bank."}}


wmap("uae.wps.map.sif-in.v1", "Salary file to canonical batch", "in", "WPS SIF: EDR and SCR records",
     ["messages"], ["header", "batch", "items[]", W], variants=["scr-first", "scr-last"],
     edgeRules=["uae.wps.rule.file-name-matches-control.v1"],
     fields=[{"from": "SCR.employerId", "to": "ext.uae.wps.employerId"},
             {"from": "SCR.employerRoutingCode", "to": "batch.debtorAgent.clearingMemberId"},
             {"from": "SCR.salaryMonth", "to": "ext.uae.wps.salaryMonth"},
             {"from": "SCR.recordCount", "to": "batch.numberOfTransactions"},
             {"from": "SCR.totalSalary", "to": "batch.controlSum"},
             {"from": "SCR.currency", "to": "batch.currency"},
             {"from": "EDR.personId", "to": "items[].ext.uae.wps.personId"},
             {"from": "EDR.agentRoutingCode", "to": "items[].payment.creditorAgent.clearingMemberId"},
             {"from": "EDR.iban", "to": "items[].payment.creditorAccount.iban"},
             {"from": "EDR.fixedPay + EDR.variablePay", "to": "items[].payment.instructedAmount.value", "via": "sum"},
             {"from": "EDR.fixedPay", "to": "items[].ext.uae.wps.fixedPay"},
             {"from": "EDR.variablePay", "to": "items[].ext.uae.wps.variablePay"},
             {"from": "EDR.payStartDate", "to": "items[].ext.uae.wps.payStartDate"},
             {"from": "EDR.payEndDate", "to": "items[].ext.uae.wps.payEndDate"},
             {"from": "EDR.daysInPeriod", "to": "items[].ext.uae.wps.daysInPeriod"},
             {"from": "EDR.leaveDays", "to": "items[].ext.uae.wps.leaveDays"},
             # 1.1.0 (Issue I-100): the EVP record's allowances, matched to the EDR by the person id
             {"from": "EVP.housingAllowance", "to": "items[].ext.uae.wps.housingAllowance"},
             {"from": "EVP.conveyanceAllowance", "to": "items[].ext.uae.wps.conveyanceAllowance"},
             {"from": "EVP.medicalAllowance", "to": "items[].ext.uae.wps.medicalAllowance"},
             {"from": "EVP.annualPassageAllowance", "to": "items[].ext.uae.wps.annualPassageAllowance"},
             {"from": "EVP.overtimeAllowance", "to": "items[].ext.uae.wps.overtimeAllowance"},
             {"from": "EVP.otherAllowances", "to": "items[].ext.uae.wps.otherAllowances"},
             {"from": "EVP.leaveEncashment", "to": "items[].ext.uae.wps.leaveEncashment"},
             # 1.1.0 (Issue I-100): what the employer's channel declared beside the file at upload, not in the SIF itself
             {"from": "upload.initiatedBy", "to": "batch.initiatingParty.id"},
             {"from": "upload.debtorName", "to": "batch.debtor.name"},
             {"from": "upload.debtorAccount", "to": "batch.debtorAccount.iban"},
             {"from": "upload.channel", "to": "ext.uae.wps.uploadChannel"},
             # 1.2.0 (Issue I-115): the line's account type where the channel declared one at upload (the SIF has none), and the
             # name of the line's bank from the scheme's participant directory, by the line's routing code
             {"from": "upload.accountType", "to": "items[].payment.creditorAccount.type"},
             {"from": "EDR.agentRoutingCode", "to": "items[].payment.creditorAgent.name", "via": "uae.wps.table.participant-names.v1"},
             {"from": "constant SALA", "to": "batch.categoryPurpose"},
             # 1.3.0 (R-12-27): the employer's establishment id as the debtor's organisation id, which the party register is asked by
             {"from": "SCR.employerId", "to": "batch.debtor.organisationId.id"},
             {"from": "constant MOL", "to": "batch.debtor.organisationId.scheme"},
             # 1.5.0 (Issue I-153, R-02-22): the employer's CIF at the bank, as the channel declared it at upload
             {"from": "upload.cif", "to": "batch.debtor.otherOrganisationIds[].id"},
             {"from": "constant CIF", "to": "batch.debtor.otherOrganisationIds[].scheme"}])["ruleSets"] = ["uae.wps.table.participant-names.v1"]
wmap("uae.wps.map.sif-out.v1", "Canonical batch to salary file", "out", "WPS SIF: EDR and SCR records",
     ["batch", "items[]", W], ["messages"], native=True)
wmap("uae.wps.map.ack-in.v1", "ACK or NAK to status", "in", "WPS ACK and NAK: AHR, DER and ATR records", ["messages"], ["status"],
     outcomes=("ack", "nak"), native=True,
     # 1.2.0 (Issue I-100): a NAK's per-line errors as pain.002 TxInfAndSts, one per DER record
     fields=[{"from": "AHR.status", "to": "status.code"}, {"from": "AHR.errorCode", "to": "status.schemeCode"},
             {"from": "DER.lineNo", "to": "status.transactions[].lineNo"}, {"from": "DER.errorCode", "to": "status.transactions[].schemeCode"},
             {"from": "DER.description", "to": "status.transactions[].additionalInformation"},
             {"from": "constant RJCT", "to": "status.transactions[].code"}])
# Issue I-267, I-160 (2), R-09-212: the correlation map of the ACK's deliver disposition. The front door runs it on a received ACK or
# NAK before it is kept: it reads the stem of the salary file the answer names, as the source's WpsAckNakFile reads it (the file's
# name split from the back before the Central Bank's file id, or the AHR record's processed file name), and gives it as the key the
# sent file was recorded by (fileStem). Provisional, as the scheme is: the naming is the source's reading of the SSM.
wmap("uae.wps.map.ack-correlation.v1", "ACK or NAK file to the salary file it answers", "in", "WPS ACK and NAK: the file name and the AHR record",
     ["messages"], ["messages"], outcomes=("correlated", "uncorrelated"), native=True,
     fields=[{"from": "fileName without its last twelve characters before the extension", "to": "messages.correlation.fileStem"},
             {"from": "AHR.processedFileName", "to": "messages.correlation.fileStem"}])
wmap("uae.wps.map.pif-in.v1", "Payment file and details to inward batch", "in", "WPS PIF: EDR, EVP and PCR records",
     ["messages"], ["header", "batch", "items[]", W], native=True)
wmap("uae.wps.map.prc-out.v2", "Receipt confirmation", "out", "WPS PRC: one RDR and one RCR", ["header", "batch", "items[]", W, "status"], ["messages"],
     params=AGENT_ID, native=True)
wmap("uae.wps.map.dif-out.v2", "Disbursement file", "out", "WPS DIF: one DIR per credit posted and a DCR",
     ["header", "batch", "items[]", W, "status", "messages"], ["messages"], params=AGENT_ID, native=True)
# The supplementary file and the refund request instruction: the source's parsers, native only (plan P2). No factory leg reads
# them yet: the PSI is held by reference (OP-A7), and the RRI's account block needs an operation the core-banking port lacks (I-82).
wmap("uae.wps.map.psi-in.v1", "Supplementary file to its pairing", "in", "WPS PSI: one CSI and its ESI records",
     ["header", "messages"], [W], outcomes=("mapped", "unmappable", "unmatched"))
wmap("uae.wps.map.rri-in.v1", "Refund request instruction to canonical request", "in", "WPS RRI: one DRC and its DRR records",
     ["messages"], ["header", "batch", "items[]", W],
     params={"fileNameAgentCode": {"type": "code", "default": "000", "bounds": {"pattern": "^[A-Za-z0-9]{3}$"},
                                   "description": "Characters 4 to 6 of an RRI file name that is for this bank."}})
wmap("uae.wps.map.rfr-out.v1", "Refund request", "out", "WPS RFR", ["header", "batch", "items[]"], ["messages"])
# The agent's answer to a refund request instruction (R-12-21, Issue I-133), after the source's RRI flow: the funds returned for the
# lines the bank posted (DCR, the disbursement cancellation request, pacs.004 PmtRtr) and the answer per line (RRR, the refund
# request response, camt.029 RsltnOfInvstgtn: ACCEPTED, or REJECTED with its reason). The layouts are the source's; the rulebook
# has not confirmed them.
wmap("uae.wps.map.dcr-out.v1", "Disbursement cancellation request", "out", "WPS DCR: one line per refund line posted (pacs.004)",
     ["header", "batch", "items[]", "facts.fx", "facts.posting"], ["messages"], params=AGENT_ID)
wmap("uae.wps.map.rrr-out.v1", "Refund request response", "out", "WPS RRR: one RSR per refund line and its control record (camt.029)",
     ["header", "batch", "items[]", "facts.record", "facts.reservation", "facts.approval"], ["messages"], params=AGENT_ID)
wmap("uae.wps.map.rsu-in.v1", "Refund settlement to status", "in", "WPS RSU", ["messages"], ["status", "facts.settlement"],
     outcomes=("settled", "declined"))
wmap("uae.wps.map.refund-order-in.v1", "Employer refund order to canonical request", "in", "Employer refund order",
     ["messages"], ["header", "batch", "items[]"])
# The RTC layout is the factory's (the source parses none): RTH, the employer and its account, then RTD per employee.
wmap("uae.wps.map.rtc-in.v2", "Return file to canonical return", "in", "WPS RTC: RTH and RTD records (provisional)", ["messages"],
     ["header", "batch", "payment", "items[]", W, "status"], native=True)
# The returns file from the agent: one line per employee credit that is returned, then a control line (Issue I-39).
wmap("uae.wps.map.rfa-out.v1", "Returns file", "out", "WPS RFA: one ARI per returned credit and an ARC",
     ["header", "batch", "items[]", W, "facts.returns"], ["messages"], params=HEAD_ROUTING, native=True)
act("uae.wps.validate.salary-file.v1", "Salary file checks", ["batch", "items[]", W], ["facts.validation"],
    ["pass", "repair", "reject"], "pure", level="batch", rule_sets=["uae.wps.ruleset.salary-file.v1"], p=WP)
act("uae.wps.validate.salary-record.v1", "Salary record checks", ["items[]"], ["facts.validation"],
    ["pass", "reject"], "pure", level="item", rule_sets=["uae.wps.ruleset.salary-record.v1"], p=WP)
act("uae.wps.validate.payment-file.v1", "Inward payment file checks", ["batch", "items[]", W], ["facts.validation"],
    ["pass", "reject"], "pure", level="batch", rule_sets=["uae.wps.ruleset.payment-file.v1"], p=WP)
# The salary file's send v1 went with the subflow that called it (Issue I-266): its v2 below replaces it, and v1 stays on the
# content branch at 1.1.0 as published, for the distributions that pinned it.
for nm, ttl, mode in [("send-refund-request", "Submit a refund request to WPS", "send"),
                      ("send-receipt-confirmation", "Send the receipt confirmation", "report"),
                      ("send-disbursement-file", "Send the disbursement file", "report"),
                      ("send-returns-file", "Send the returns file to WPS", "send"),
                      ("send-disbursement-cancellation", "Send the disbursement cancellation request to WPS", "send"),
                      ("send-refund-response", "Send the refund request response to WPS", "send")]:
    # 1.1.0 (Issue I-268, R-09-213): a native code-behind that sends the file its render kept on the bank's binding of the WPS port,
    # as the salary file's does, so a bench that gives the port to the runtime runs every send of it natively.
    act("uae.wps.exchange.%s.v1" % nm, ttl, ["header", "messages"], ["status", "messages"],
        ["accepted", "rejected", "unknown"], "external", mode=mode, ports=["uae.wps.port.wps.v1"], policy=STD, p=WP, native=True)

# The salary file's send, v2 (Issue I-266, R-09-182): the send reads the encryption step's fact (facts.encryption), and where the bank's
# binding of p.port.encryption.v1 encrypted the file it sends that file, not the plain one the render kept. A read added, a new major
# line; the submit block's subflow calls it from its 1.3.0.
act("uae.wps.exchange.send-salary-file.v2", "Submit the salary file to WPS, encrypted where the bank encrypts it", ["header", "messages", "facts.encryption"],
    ["status", "messages"], ["accepted", "rejected", "unknown"], "external", mode="send", ports=["uae.wps.port.wps.v1"], policy=STD, p=WP,
    native=True)

# The employer's status at the labour ministry (Issue I-88, R-12-14): a lookup behind a port, written to the scheme's extension,
# so the salary file checks can refuse an employer the ministry has suspended. The source's bank forks each read the status
# (AdibValidateWpsRules and its copies, mohreStatus SUSPENDED) and refused the file; no answer let the file through.
act("uae.wps.enrich.employer-status.v1", "Ask the labour ministry for the employer's status", ["header", "ext.uae.wps.employerId"],
    ["ext.uae.wps.employerStatus"], ["found", "not-found"], "read", level="batch", ports=["uae.wps.port.labour-ministry.v1"], policy=SHORT,
    script=True, native=True, p=WP)

# The debtor in the bank's party register (owner's decisions of 2026-11-23, R-12-27, Issue I-149): bank customer data lives in the
# runtime's registers only, so the studio asks a simulator and the runtime's native code-behind reads the register as of the step.
# The platform looks the debtor up by its organisation id (lookupBy names the register's field); a scheme matches what it needs (on
# the runtime, WPS feeds the source's SifUploadRules.MatchCorporate). Only the answer is written: found, active, and why not.
act("p.enrich.party-register.v1", "Look up the debtor in the bank's party register", ["header", "batch.debtor", "batch.debtorAccount"],
    ["facts.partyRegister"], ["registered", "not-registered", "mismatch"], "read", level="batch", ports=["p.port.party-register.v1"],
    policy=SHORT, script=True, native=True,
    params={"register": {"type": "code", "default": "corporate", "bounds": {"allowed": ["corporate"]},
                         "description": "The register kind the debtor is looked up in."},
            "lookupBy": {"type": "code", "default": "establishmentId", "bounds": {"allowed": ["corporateId", "cif", "establishmentId"]},
                         "description": "The register's field the debtor's organisation id is matched against."}})

# composite exchanges: one block to the designer, a subflow underneath (design/05, section 5.6)
WPS_PORT = ["uae.wps.port.wps.v1"]
# How long the bank waits for the acknowledgement is the bank's (R-05-47): a parameter of the composite, the policy's
# replyTimeout and replyCalendar its defaults.
act("uae.wps.exchange.submit-salary-file.v1", "Submit the salary file and get the acknowledgement", ["header", "messages"], ["status", "messages"],
    ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business", "replyTimeoutParameter": "ackWait", "replyCalendarParameter": "ackWaitCalendar"},
    sub="uae.wps.sub.submit-salary-file.v1", p=WP,
    params={"ackWait": {"type": "duration", "default": "P1D", "bounds": {"min": "PT15M", "max": "P7D"},
                        "description": "How long to wait for the scheme's acknowledgement of the salary file before the payment is pending."},
            "ackWaitCalendar": {"type": "code", "default": "business", "bounds": {"allowed": ["business", "elapsed"]},
                                "description": "Whether the wait counts business time or elapsed time."}})
act("uae.wps.exchange.confirm-receipt.v1", "Confirm receipt of the payment file and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="report", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.confirm-receipt.v1", p=WP)
act("uae.wps.exchange.request-refund.v1", "Request a refund and wait for its settlement", ["header", "messages"],
    ["status", "facts.settlement", "messages"], ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.request-refund.v1", p=WP)
act("uae.wps.exchange.receive-supplement.v1", "Wait for the supplementary file that pairs with the payment file", ["header"], ["messages"],
    ["received", "missing"], "read", mode="receive", ports=WPS_PORT, policy={"replyTimeout": "PT2H"},
    sub="uae.wps.sub.receive-supplement.v1", p=WP)
act("uae.wps.exchange.report-disbursement.v1", "Report disbursement and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="report", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.report-disbursement.v1", p=WP)
# The returns exchange (Issue I-41): how long it waits for the acknowledgement and how long the upload is held are
# the bank's values, not constants. The source waits 30 calendar days (2,592,000 s) and may hold the upload for up to
# 10 minutes. The owner decided on 2026-11-22 that the default is the source's, 30 calendar days (Issue I-51).
act("uae.wps.exchange.return-credits.v1", "Return the credits that failed and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P30D", "replyCalendar": "elapsed", "replyTimeoutParameter": "replyWait",
            "replyCalendarParameter": "replyWaitCalendar"}, sub="uae.wps.sub.return-credits.v1", p=WP,
    params={"replyWait": {"type": "duration", "default": "P30D", "bounds": {"min": "P1D", "max": "P30D"},
                          "description": "How long to wait for WPS to acknowledge the returns file, counted on replyWaitCalendar."},
            "replyWaitCalendar": {"type": "code", "default": "elapsed", "bounds": {"allowed": ["elapsed", "business"]},
                                  "description": "Whether the wait counts calendar days (elapsed, the source's) or business days."},
            "uploadHold": {"type": "duration", "default": "PT0S", "bounds": {"min": "PT0S", "max": "PT10M"},
                           "description": "How long to hold the returns file before it is uploaded."}})

# The two exchanges of the agent's refund leg (R-12-21): each a composite, encrypted, sent and acknowledged underneath (L46).
act("uae.wps.exchange.return-refund.v1", "Return the refunded funds to WPS and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.return-refund.v1", p=WP)
act("uae.wps.exchange.answer-refund.v1", "Answer the refund request and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.answer-refund.v1", p=WP)

# the agent's returns: which credits go back, and the posting once WPS has accepted the returns file
act("uae.wps.decide.returns.v2", "Find the employee credits to return", ["items[]"], ["facts.returns"],
    ["none", "returns", "unverified"], "pure", rule_sets=["uae.wps.table.returns.v1"], p=WP)
act("uae.wps.post.return.v1", "Record the returned amount once WPS accepts the returns file", ["header", "batch", "facts.returns"],
    ["facts.posting"], ["posted", "rejected", "unknown"], "write", ports=["p.port.core-banking.v1"], policy=STD,
    comp="p.post.reverse.v1", service="accounting", p=WP,
    params={"template": {"type": "code", "default": "wps-return",
                         "description": "Accounting template for a return. The bank sets the accounts the returned amount moves between."},
            **{k: copy.deepcopy(ACCOUNTING[k]) for k in ("transactionCode", "reversalTransactionCode", "narration", "settlementAccount", "suspenseAccount")}})




# example bank tenant
act("b.validate.internal-limit.v1", "Bank internal limit, per payment, per line and per file", ["payment.instructedAmount", "batch", "items[]"], ["facts.validation"],
    ["pass", "reject"], "pure", rule_sets=["b.ruleset.internal-limit.v1"], base=EXAMPLES)
act("b.enrich.cost-centre.v1", "Add the bank cost centre", ["payment.debtorAccount"], ["ext.b.costCentre"],
    ["done"], "read", ports=["b.port.reference-data.v1"], lang="python", base=EXAMPLES)
act("b.notify.treasury.v1", "Tell treasury of a settled payment", ["header", "status", "payment.instructedAmount"], ["facts.notified"],
    ["notified"], "write", ports=["b.port.treasury.v1"], lang="python", base=EXAMPLES)


# --------------------------------------------------------------------- rules
def m(fact, var, where=None):
    d = {"fact": fact, "as": var}
    if where:
        d["where"] = where
    return {"match": d}


def par(name):
    return m("Param", name, "%s.name == '%s'" % (name, name))


def rule(name, title, kind, when, path, code, ok, bad, *, sev="reject", level=None, params=None, msg=None, p=None, base=FACTORY, more=None):
    doc = {"kind": "rule", "name": name, "release": REL, "title": title, "ruleKind": kind}
    if level:
        doc["level"] = level
    if params:
        doc["parameters"] = params
    doc["repeatability"] = "non-repeatable"
    doc["when"] = when
    f = {"severity": sev, "path": path, "reasonCode": code}
    if msg:
        f["message"] = msg
    doc["then"] = [{"yield": {"finding": f}}]
    doc["examples"] = [
        {"name": "passes", "evaluationDate": DATE, "given": ok, "expect": {"findings": []}},
        {"name": "fails", "evaluationDate": DATE, "given": bad, "expect": {"findings": [{"reasonCode": code}]}}]
    for name_, given, fires in (more or []):
        doc["examples"].append({"name": name_, "evaluationDate": DATE, "given": given, "expect": {"findings": [{"reasonCode": code}] if fires else []}})
    doc["provenance"] = prov(**(p or {}))
    return emit(doc, base)


def ruleset(name, title, mode, rules, stages=None, p=None, base=FACTORY):
    doc = {"kind": "ruleset", "name": name, "release": REL, "title": title, "mode": mode}
    if stages:
        doc["stages"] = stages
    doc["rules"] = rules
    doc["provenance"] = prov(**(p or {}))
    return emit(doc, base)


def pay(**kw):
    return {"Payment": kw}


rule("p.rule.amount-positive.v1", "Amount is greater than zero", "field",
     [m("Payment", "p", "p.instructedAmount.value <= 0")], "payment.instructedAmount.value", "AM01",
     pay(instructedAmount={"value": 10, "currency": "AED"}), pay(instructedAmount={"value": 0, "currency": "AED"}))
rule("p.rule.currency-present.v1", "Currency is present", "field",
     [m("Payment", "p", "isBlank(p.instructedAmount.currency)")], "payment.instructedAmount.currency", "AM03",
     pay(instructedAmount={"value": 10, "currency": "AED"}), pay(instructedAmount={"value": 10, "currency": ""}))
rule("p.rule.debtor-account-present.v1", "Debtor account is present", "field",
     [m("Payment", "p", "isBlank(p.debtorAccount.iban) and isBlank(p.debtorAccount.other)")], "payment.debtorAccount", "AC02",
     pay(debtorAccount={"iban": IBAN_OK}), pay(debtorAccount={}))
rule("p.rule.creditor-account-present.v1", "Creditor account or proxy is present", "field",
     [m("Payment", "p", "isBlank(p.creditorAccount.iban) and isBlank(p.creditorAccount.other) and isBlank(p.creditorAccount.proxy)")],
     "payment.creditorAccount", "AC03", pay(creditorAccount={"iban": IBAN_OK}), pay(creditorAccount={}))
rule("p.rule.end-to-end-id-present.v1", "End-to-end id is present", "field",
     [m("Header", "h", "isBlank(h.endToEndId)")], "header.endToEndId", "CH21",
     {"Header": {"endToEndId": "E2E-1"}}, {"Header": {"endToEndId": ""}})
rule("p.rule.creditor-town-country.v1", "Creditor address carries town and country", "field",
     [m("Payment", "p", "p.creditor.postalAddress != null and (isBlank(p.creditor.postalAddress.townName) or isBlank(p.creditor.postalAddress.country))")],
     "payment.creditor.postalAddress", "BE04", pay(creditor={"postalAddress": {"townName": "Dubai", "country": "AE"}}),
     pay(creditor={"postalAddress": {"addressLine": ["PO Box 1, Dubai"]}}), sev="warn")
ruleset("p.ruleset.canonical.v1", "Canonical payment checks", "validation",
        ["p.rule.amount-positive.v1", "p.rule.currency-present.v1", "p.rule.debtor-account-present.v1",
         "p.rule.creditor-account-present.v1", "p.rule.end-to-end-id-present.v1", "p.rule.creditor-town-country.v1"],
        stages=["field"])
rule("p.rule.amount-within-platform-limit.v1", "Amount is within the platform limit", "policy",
     [m("Payment", "p"), par("maxAmount"), {"having": "p.instructedAmount.value > maxAmount.value"}],
     "payment.instructedAmount.value", "AM02",
     {"Payment": {"instructedAmount": {"value": 100}}, "Param": {"name": "maxAmount", "value": 1000}},
     {"Payment": {"instructedAmount": {"value": 1001}}, "Param": {"name": "maxAmount", "value": 1000}},
     params={"maxAmount": {"type": "amount", "default": 999999999999, "description": "Upper limit for any payment. A lower scope may only lower it."}})
ruleset("p.ruleset.limits.v1", "Platform limits", "validation", ["p.rule.amount-within-platform-limit.v1"], stages=["policy"])

UAE_IBAN_OK = pay(creditorAccount={"iban": IBAN_OK})
rule("uae.p.rule.iban-length.v1", "UAE IBAN has 23 characters", "field",
     [m("Payment", "p", "not isBlank(p.creditorAccount.iban) and len(p.creditorAccount.iban) != 23")],
     "payment.creditorAccount.iban", "AC03", UAE_IBAN_OK, pay(creditorAccount={"iban": "AE0703312345678901234"}),
     p=dict(clauses=["Public bank WPS guides: IBAN is 23 characters"]))
rule("uae.p.rule.iban-country.v1", "UAE IBAN starts with AE", "field",
     [m("Payment", "p", "not isBlank(p.creditorAccount.iban) and not startsWith(p.creditorAccount.iban, 'AE')")],
     "payment.creditorAccount.iban", "AC03", UAE_IBAN_OK, pay(creditorAccount={"iban": "GB070331234567890123456"}))
rule("uae.p.rule.iban-checksum.v1", "IBAN check digits are valid", "field",
     [m("Payment", "p", "not isBlank(p.creditorAccount.iban) and not ibanValid(p.creditorAccount.iban)")],
     "payment.creditorAccount.iban", "AC03", UAE_IBAN_OK, pay(creditorAccount={"iban": "AE990331234567890123456"}))
ruleset("uae.p.ruleset.iban.v1", "UAE IBAN checks", "validation",
        ["uae.p.rule.iban-length.v1", "uae.p.rule.iban-country.v1", "uae.p.rule.iban-checksum.v1"], stages=["field"])
rule("uae.p.rule.currency-aed.v1", "Currency is AED", "field",
     [m("Payment", "p", "p.instructedAmount.currency != 'AED'")], "payment.instructedAmount.currency", "AM03",
     pay(instructedAmount={"value": 10, "currency": "AED"}), pay(instructedAmount={"value": 10, "currency": "USD"}))
rule("uae.p.rule.routing-code-format.v1", "Routing code has nine digits", "field",
     [m("Payment", "p", "not isBlank(p.creditorAgent.clearingMemberId) and not matches(p.creditorAgent.clearingMemberId, '^[0-9]{9}$')")],
     "payment.creditorAgent.clearingMemberId", "RC01", pay(creditorAgent={"clearingMemberId": "302620122"}),
     pay(creditorAgent={"clearingMemberId": "30262"}))

# WPS rules (provisional)
BATCH_OK = {"Batch": {"id": "B1", "numberOfTransactions": 2, "controlSum": 300, "currency": "AED"},
            "Item": [{"batchId": "B1", "fixedPay": 100, "variablePay": 0}, {"batchId": "B1", "fixedPay": 150, "variablePay": 50}]}


def batch_bad(**over):
    d = json.loads(json.dumps(BATCH_OK))
    d["Batch"].update(over)
    return d


Q_ITEMS = {"query": {"as": "items", "match": {"fact": "Item", "where": "item.batchId == b.id"}, "collect": True}}
rule("uae.wps.rule.record-count-matches.v1", "Record count equals the number of records", "cross-field",
     [m("Batch", "b"), Q_ITEMS, {"having": "count(items) != b.numberOfTransactions"}],
     "batch.numberOfTransactions", "AM18", BATCH_OK, batch_bad(numberOfTransactions=3), level="batch", p=WP)
rule("uae.wps.rule.control-total-matches.v1", "Control total equals the sum of fixed and variable pay", "cross-field",
     [m("Batch", "b"), Q_ITEMS, {"let": {"total": "sum(items, i => i.fixedPay + i.variablePay)"}}, {"having": "total != b.controlSum"}],
     "batch.controlSum", "AM10", BATCH_OK, batch_bad(controlSum=299), level="batch", p=WP,
     msg="Control total {b.controlSum} does not equal the sum of records {total}")
rule("uae.wps.rule.currency-aed.v1", "Salary file currency is AED", "field",
     [m("Batch", "b", "b.currency != 'AED'")], "batch.currency", "AM03", BATCH_OK, batch_bad(currency="USD"), level="batch", p=WP)
rule("uae.wps.rule.employer-id-format.v1", "Employer id has thirteen digits", "field",
     [m("WpsBatch", "w", "not matches(w.employerId, '^[0-9]{13}$')")], "ext.uae.wps.employerId", "FF01",
     {"WpsBatch": {"employerId": "0000001234567"}}, {"WpsBatch": {"employerId": "1234567"}}, level="batch", p=WP)
rule("uae.wps.rule.employer-routing-code-format.v1", "Employer bank routing code has nine digits", "field",
     [m("Batch", "b", "not matches(b.debtorAgent.clearingMemberId, '^[0-9]{9}$')")], "batch.debtorAgent.clearingMemberId", "RC01",
     {"Batch": {"debtorAgent": {"clearingMemberId": "302620122"}}}, {"Batch": {"debtorAgent": {"clearingMemberId": "3026"}}},
     level="batch", p=WP)
rule("uae.wps.rule.file-name-matches-control.v1", "File name agrees with the control record", "structural",
     [m("SourceFile", "f"), m("WpsBatch", "w"), {"having": "not startsWith(f.name, w.employerId)"}], "messages", "FF01",
     {"SourceFile": {"name": "0000001234567261117083000.sif"}, "WpsBatch": {"employerId": "0000001234567"}},
     {"SourceFile": {"name": "9999991234567261117083000.sif"}, "WpsBatch": {"employerId": "0000001234567"}}, level="batch", p=WP)
# Issue I-88 (R-12-14): an employer the labour ministry has suspended is refused, as each bank fork of the source refused it
# (WPS_MOHRE, "Employer is suspended in MOHRE"). RR04, regulatory reason, until the scheme's code set is confirmed. An employer
# with no status (the register did not answer, or does not know it) passes, as in the source.
rule("uae.wps.rule.employer-not-suspended.v1", "The labour ministry has not suspended the employer", "field",
     [m("WpsBatch", "w", "w.employerStatus == 'suspended'")], "ext.uae.wps.employerStatus", "RR04",
     {"WpsBatch": {"employerId": "0000001234567", "employerStatus": "active"}},
     {"WpsBatch": {"employerId": "0000001234567", "employerStatus": "suspended"}}, level="batch", p=WP,
     msg="The labour ministry has suspended the employer",
     more=[("no-status", {"WpsBatch": {"employerId": "0000001234567"}}, False),
           ("unknown", {"WpsBatch": {"employerId": "0000001234567", "employerStatus": "unknown"}}, False)])
ruleset("uae.wps.ruleset.salary-file.v1", "Salary file checks", "validation",
        ["uae.wps.rule.employer-id-format.v1", "uae.wps.rule.employer-routing-code-format.v1", "uae.wps.rule.currency-aed.v1",
         "uae.wps.rule.record-count-matches.v1", "uae.wps.rule.control-total-matches.v1", "uae.wps.rule.employer-not-suspended.v1"],
        stages=["field", "cross-field"], p=WP)
ITEM_OK = {"WpsItem": {"personId": "00001234567890", "payStartDate": "2026-10-01", "payEndDate": "2026-10-31", "fixedPay": 100, "variablePay": 0},
           "Item": {"payment": {"creditorAgent": {"clearingMemberId": "302620122"}}}}


def item_bad(**over):
    d = json.loads(json.dumps(ITEM_OK))
    d["WpsItem"].update(over)
    return d


rule("uae.wps.rule.person-id-format.v1", "Person id has fourteen digits", "field",
     [m("WpsItem", "w", "not matches(w.personId, '^[0-9]{14}$')")], "items[].ext.uae.wps.personId", "FF01",
     ITEM_OK, item_bad(personId="1234"), level="item", p=WP)
rule("uae.wps.rule.pay-period-valid.v1", "Pay period start is not after its end", "cross-field",
     [m("WpsItem", "w", "w.payStartDate > w.payEndDate")], "items[].ext.uae.wps.payStartDate", "DT01",
     ITEM_OK, item_bad(payStartDate="2026-11-01"), level="item", p=WP)
rule("uae.wps.rule.amounts-non-negative.v1", "Fixed and variable pay are not negative", "field",
     [m("WpsItem", "w", "w.fixedPay < 0 or w.variablePay < 0")], "items[].ext.uae.wps.fixedPay", "AM02",
     ITEM_OK, item_bad(fixedPay=-1), level="item", p=WP)
rule("uae.wps.rule.net-pay-positive.v1", "Fixed plus variable pay is greater than zero", "cross-field",
     [m("WpsItem", "w", "w.fixedPay + w.variablePay <= 0")], "items[].payment.instructedAmount.value", "AM01",
     ITEM_OK, item_bad(fixedPay=0), level="item", p=WP)
ruleset("uae.wps.ruleset.salary-record.v1", "Salary record checks", "validation",
        ["uae.wps.rule.person-id-format.v1", "uae.p.rule.routing-code-format.v1",
         "uae.wps.rule.amounts-non-negative.v1", "uae.wps.rule.pay-period-valid.v1", "uae.wps.rule.net-pay-positive.v1"],
        stages=["field", "cross-field"], p=WP)
rule("uae.wps.rule.agent-is-this-bank.v1", "The payment file is addressed to this bank", "reference",
     [m("Batch", "b"), par("ownRoutingCodes"), {"having": "not in(b.creditorAgent.clearingMemberId, ownRoutingCodes.value)"}],
     "batch.creditorAgent.clearingMemberId", "RC01",
     {"Batch": {"creditorAgent": {"clearingMemberId": "302620122"}}, "Param": {"name": "ownRoutingCodes", "value": ["302620122"]}},
     {"Batch": {"creditorAgent": {"clearingMemberId": "111111111"}}, "Param": {"name": "ownRoutingCodes", "value": ["302620122"]}},
     level="batch", p=WP,
     params={"ownRoutingCodes": {"type": "list", "default": [], "description": "This bank's routing codes. A bank value, set in its configuration set."}})
ruleset("uae.wps.ruleset.payment-file.v1", "Inward payment file checks", "validation",
        ["uae.wps.rule.agent-is-this-bank.v1", "uae.wps.rule.currency-aed.v1"], stages=["field", "reference"], p=WP)

# example bank rule
# A payment, each line of a file and the file total are held against the one limit the bank sets
# (Issue I-28, decided). A finding's path is fixed per rule, so the message says which amount.
LIM = {"Param": {"name": "internalLimit", "value": 100}}
rule("b.rule.internal-limit.v1", "A payment, each line of a file and the file total are within the bank's internal limit", "policy",
     [par("internalLimit"), {"or": [
         {"and": [m("Payment", "p"), {"having": "p.instructedAmount.value > internalLimit.value"}]},
         {"and": [m("Item", "i"), {"having": "i.payment.instructedAmount.value > internalLimit.value"}]},
         {"and": [m("Batch", "b"), {"having": "b.controlSum > internalLimit.value"}]}]}],
     "payment.instructedAmount.value", "AM02",
     {"Payment": {"instructedAmount": {"value": 10}}, **LIM},
     {"Payment": {"instructedAmount": {"value": 101}}, **LIM},
     msg="An amount above the internal limit: a payment, a line of a file, or a file total",
     more=[("boundary, equal to the limit", {"Payment": {"instructedAmount": {"value": 100}}, **LIM}, False),
           ("fails, one line of a file", {"Batch": {"id": "B1", "controlSum": 100}, "Item": [{"batchId": "B1", "payment": {"instructedAmount": {"value": 101}}}], **LIM}, True),
           ("fails, the file total with every line within", {"Batch": {"id": "B1", "controlSum": 150}, "Item": [{"batchId": "B1", "payment": {"instructedAmount": {"value": 80}}}, {"batchId": "B1", "payment": {"instructedAmount": {"value": 70}}}], **LIM}, True),
           ("passes, a file within on every line and in total", {"Batch": {"id": "B1", "controlSum": 100}, "Item": [{"batchId": "B1", "payment": {"instructedAmount": {"value": 60}}}, {"batchId": "B1", "payment": {"instructedAmount": {"value": 40}}}], **LIM}, False)],
     params={"internalLimit": {"type": "amount", "currency": "AED", "default": 1000000, "bounds": {"max": 1000000},
                               "description": "The bank's own limit. Its configuration set may lower it, never raise it."}}, base=EXAMPLES)
ruleset("b.ruleset.internal-limit.v1", "Bank internal limit", "validation", ["b.rule.internal-limit.v1"], stages=["policy"], base=EXAMPLES)


# -------------------------------------------------------------------- tables
def table(name, title, hit, inputs, outputs, rows, examples, params=None, p=None):
    doc = {"kind": "table", "name": name, "release": REL, "title": title, "hitPolicy": hit}
    if params:
        doc["parameters"] = params
    doc.update({"inputs": [{"name": n, "expression": e} for n, e in inputs], "outputs": outputs, "rows": rows,
                "examples": examples, "provenance": prov(**(p or {}))})
    return emit(doc)


table("p.table.workflow-select.v1", "Which platform workflow runs a payment", "unique",
      [("kind", "header.kind"), ("direction", "header.direction"), ("segment", "header.segment")], ["workflow"],
      [{"when": {"kind": "request"}, "then": {"workflow": "p.wf.request.v1"}, "reason": "Requests and exceptions move no money through the lifecycle"},
       # 1.3.0 (R-09-87, Issue I-141): inward payments and returns run p.wf.inward.v2, whose leg may hand back its returns
       {"when": {"kind": "payment", "direction": "inward"}, "then": {"workflow": "p.wf.inward.v2"}, "reason": "Every inward payment"},
       {"when": {"kind": "return", "direction": "inward"}, "then": {"workflow": "p.wf.inward.v2"}, "reason": "Every inward return"},
       # 1.1.0 (R-12-19): a refund of an earlier payment is an order of its own, on the refund lifecycle
       {"when": {"kind": "refund", "direction": "inward"}, "then": {"workflow": "p.wf.refund.v1"}, "reason": "A refund of an earlier payment is a new order on the refund lifecycle"},
       # 1.2.0 (R-12-24): the bank asks for a refund for its customer (the employer's side), and credits it when the scheme settles
       {"when": {"kind": "refund", "direction": "outward"}, "then": {"workflow": "p.wf.refund-out.v1"}, "reason": "A refund the bank asks for is an order on the outward refund lifecycle"},
       {"when": {"kind": "payment", "direction": "outward", "segment": "corporate"}, "then": {"workflow": "p.wf.corporate.v1"}, "reason": "File and high-value rails"},
       {"when": {"kind": "payment", "direction": "outward", "segment": "commercial"}, "then": {"workflow": "p.wf.commercial.v1"}, "reason": "Instant rails"}],
      [{"name": "inward payment", "given": {"Header": {"kind": "payment", "direction": "inward", "segment": "commercial"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.inward.v2"}]}},
       {"name": "outward corporate", "given": {"Header": {"kind": "payment", "direction": "outward", "segment": "corporate"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.corporate.v1"}]}},
       {"name": "request", "given": {"Header": {"kind": "request", "direction": "outward", "segment": "corporate"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.request.v1"}]}},
       {"name": "refund", "given": {"Header": {"kind": "refund", "direction": "inward"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.refund.v1"}]}},
       {"name": "outward refund", "given": {"Header": {"kind": "refund", "direction": "outward"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.refund-out.v1"}]}}])
table("p.table.inward-disposition.v1", "What to do with an inward payment after checks and screening", "first",
      [("validation", "facts.validation.worst"), ("duplicate", "facts.duplicate.found"), ("sanctions", "facts.screening.sanctions.result"), ("fraud", "facts.screening.fraud.result")],
      ["disposition"],
      [{"when": {"validation": "reject"}, "then": {"disposition": "refuse"}, "reason": "The payment is invalid"},
       {"when": {"duplicate": True}, "then": {"disposition": "refuse"}, "reason": "The payment is a duplicate"},
       {"when": {"sanctions": "hit"}, "then": {"disposition": "review"}, "reason": "A sanctions hit needs a person"},
       {"when": {"sanctions": "pending"}, "then": {"disposition": "review"}, "reason": "Screening has not finished"},
       {"when": {"fraud": "suspect"}, "then": {"disposition": "review"}, "reason": "A fraud alert needs a person"},
       {"when": {}, "then": {"disposition": "accept"}, "reason": "Nothing stands against the payment"}],
      [{"name": "clean", "given": {"Facts": {"validation": {"worst": "pass"}, "duplicate": {"found": False}, "screening": {"sanctions": {"result": "clear"}, "fraud": {"result": "clear"}}}}, "expect": {"decisions": [{"name": "disposition", "value": "accept"}]}},
       {"name": "duplicate", "given": {"Facts": {"validation": {"worst": "pass"}, "duplicate": {"found": True}}}, "expect": {"decisions": [{"name": "disposition", "value": "refuse"}]}},
       {"name": "sanctions hit", "given": {"Facts": {"validation": {"worst": "pass"}, "duplicate": {"found": False}, "screening": {"sanctions": {"result": "hit"}}}}, "expect": {"decisions": [{"name": "disposition", "value": "review"}]}}])
table("p.table.rail-choice.v1", "Which rail carries a payment", "first",
      [("requested", "header.scheme"), ("enabled", "in(header.scheme, enabledRails.value)")], ["rail"],
      [{"when": {"enabled": True}, "then": {"rail": "requested"}, "reason": "The requested rail is enabled for this bank"},
       {"when": {}, "then": {"rail": "none"}, "reason": "No enabled rail accepts this payment"}],
      [{"name": "requested rail enabled", "given": {"Header": {"scheme": "uae.wps"}, "Param": {"name": "enabledRails", "value": ["uae.wps"]}},
        "expect": {"decisions": [{"name": "rail", "value": "uae.wps"}]}},
       {"name": "requested rail disabled", "given": {"Header": {"scheme": "uae.wps"}, "Param": {"name": "enabledRails", "value": []}},
        "expect": {"decisions": [{"name": "rail", "value": "none"}]}}],
      params={"enabledRails": {"type": "list", "default": [], "description": "Rails this bank runs, in order of preference. A bank value."}})
# The scheme's participant directory (Issue I-115, R-12-18): a bank's name by its routing code, for the lines of a salary file. The
# directory is a value, looked up by date: shared content names no bank (L09), so the factory's default is empty and the directory
# is set where the participants are known (a bank's configuration, or the scheme operator's published list; Issue I-130). The
# table hands the directory in force to the map, which looks each line's routing code up in it.
table("uae.wps.table.participant-names.v1", "The scheme's participant directory: a bank's name by its routing code", "first",
      [("directory", "participants.value"), ("known", "count(participants.value)")], ["directory", "known"],
      [{"when": {}, "then": {"directory": "directory", "known": "known"}, "reason": "The participant directory in force"}],
      [{"name": "the directory in force", "given": {"Param": {"name": "participants", "value": [{"routingCode": "203320101", "name": "Example Bank One"}]}},
        "expect": {"decisions": [{"name": "directory", "value": [{"routingCode": "203320101", "name": "Example Bank One"}]}, {"name": "known", "value": 1}]}},
       {"name": "no directory is set", "given": {"Param": {"name": "participants", "value": []}},
        "expect": {"decisions": [{"name": "directory", "value": []}, {"name": "known", "value": 0}]}}],
      params={"participants": {"type": "list", "default": [],
                                "description": "The scheme's participants: each a routingCode (nine digits) and the name the directory gives it. Empty by default: shared content names no bank."}},
      p=WP)
# WPS agent returns (provisional; Issue I-39). A credit the core system refused, or one the inquiry found not
# posted, is returned. A credit whose outcome is still unknown after the inquiry is not decided: the file goes to
# operations. A line's amount is round2(fixed pay + variable pay), half away from zero, and the returned amount is
# the sum of the rounded lines, not the rounded sum. Every credit refused is the whole file returned (Issue I-40).
REFUSED = "i.facts.posting.outcome == 'rejected' or (i.facts.posting.outcome == 'unknown' and i.facts.postingVerified.outcome == 'mismatch')"
UNVERIFIED = "i.facts.posting.outcome == 'unknown' and not (i.facts.postingVerified.outcome in ['verified', 'mismatch'])"


def RET(*outs, verify=None):
    """Example items: each credit's posting outcome, the inquiry's answer where there is one, and synthetic pay."""
    pay = [(1200.005, 300), (1999.995, 0)]
    items = []
    for n, o in enumerate(outs):
        it = {"facts": {"posting": {"outcome": o}}, "ext": {"uae": {"wps": {"fixedPay": pay[n][0], "variablePay": pay[n][1]}}}}
        if verify and verify[n]:
            it["facts"]["postingVerified"] = {"outcome": verify[n]}
        items.append(it)
    return {"Item": items}


def DEC(returns, scope, count, amount):
    return {"decisions": [{"name": "returns", "value": returns}, {"name": "returnScope", "value": scope},
                          {"name": "count", "value": count}, {"name": "amount", "value": amount}]}


table("uae.wps.table.returns.v1", "Which employee credits go back to WPS in a returns file", "first",
      [("refused", "sum(items, i => %s)" % REFUSED), ("stillUnknown", "sum(items, i => %s)" % UNVERIFIED),
       ("allRefused", "count(items) > 0 and sum(items, i => %s) == count(items)" % REFUSED),
       ("amount", "sumWhere(items, i => %s, i => round2(i.ext.uae.wps.fixedPay + i.ext.uae.wps.variablePay))" % REFUSED)],
      ["returns", "returnScope", "count", "amount"],
      [{"when": {"stillUnknown": {"gt": 0}}, "then": {"returns": "unverified", "returnScope": "none", "count": "refused", "amount": "amount"},
        "reason": "A credit's outcome is still unknown after the inquiry; nothing is returned until every credit is known"},
       {"when": {"allRefused": True}, "then": {"returns": "returns", "returnScope": "whole-file", "count": "refused", "amount": "amount"},
        "reason": "Every credit was refused; the whole file is returned"},
       {"when": {"refused": {"gt": 0}}, "then": {"returns": "returns", "returnScope": "partial", "count": "refused", "amount": "amount"},
        "reason": "Some credits were refused and are returned; the others were paid"},
       {"when": {}, "then": {"returns": "none", "returnScope": "none", "count": "refused", "amount": "amount"}, "reason": "Every credit posted"}],
      [{"name": "every credit posted", "given": RET("posted", "posted"), "expect": DEC("none", "none", 0, 0)},
       {"name": "one credit refused is a partial return; its line is rounded half away from zero", "given": RET("rejected", "posted"),
        "expect": DEC("returns", "partial", 1, 1500.01)},
       {"name": "every credit refused is the whole file; the amount is the sum of the rounded lines", "given": RET("rejected", "rejected"),
        "expect": DEC("returns", "whole-file", 2, 3500.01)},
       {"name": "an unknown credit the inquiry found not posted is returned", "given": RET("posted", "unknown", verify=[None, "mismatch"]),
        "expect": DEC("returns", "partial", 1, 2000)},
       {"name": "an unknown credit the inquiry found posted is not returned", "given": RET("posted", "unknown", verify=[None, "verified"]),
        "expect": DEC("none", "none", 0, 0)},
       {"name": "an unknown credit the inquiry could not settle stops the decision", "given": RET("rejected", "unknown"),
        "expect": DEC("unverified", "none", 1, 1500.01)}],
      p=WP)


# --------------------------------------------------------------------- ports
def port(name, title, party, ops, p=None, base=FACTORY):
    emit({"kind": "port", "name": name, "release": REL, "title": title, "party": party,
          "operations": [{"name": n, "style": s} for n, s in ops], "provenance": prov(**(p or {}))}, base)


RR, SEND, RECV = "request-reply", "send", "receive"
port("p.port.core-banking.v1", "Core banking", "core banking system",
     [("reserve", RR), ("release", RR), ("post", RR), ("verify", RR), ("reverse", RR)])
port("p.port.sanctions.v1", "Sanctions screening", "sanctions screening engine", [("screen", RR)])
port("p.port.fraud.v1", "Fraud screening", "fraud engine", [("score", RR)])
port("p.port.channel.v1", "Originating channel", "initiating channel", [("notify", SEND)])
port("p.port.facts-out.v1", "Outward facts", "downstream consumers of published facts", [("publish", SEND)])
# Owner's decisions of 23 November 2026 (Issue I-182, R-09-150, R-09-152): where operator alerts go, and the payment events the
# store's outbox streams out. Each bank binds them in its configuration release like any port.
port("p.port.notification.v1", "Operator notification", "the bank's alerting service for operators", [("alert", SEND)])
port("p.port.event-stream.v1", "Payment event stream", "the bank's event streaming platform", [("publish", SEND)])
# Owner's decision of 23 November 2026 (Issue I-189, R-09-177): the bank's exchange-rate service, which the rates feed asks on its
# schedule and publishes as a configuration release.
port("p.port.fx-rates.v1", "Exchange rates", "the bank's exchange-rate service", [("rates", RR)])
port("p.port.payment-record.v1", "Payment record", "payment record store", [("read", RR), ("find-duplicate", RR)])
port("p.port.operations-queue.v1", "Operations queue", "operations console", [("create-item", SEND), ("decision", RECV)])
port("p.port.liquidity.v1", "Liquidity position", "liquidity service", [("position", RR)])
# Files to and from a scheme or a channel, and their decryption (R-12-12): the transport and the keys are the bank's binding.
port("p.port.file-transfer.v1", "File transfer", "file transfer service", [("put", SEND), ("get", RR), ("list", RR)])
port("p.port.decryption.v1", "File decryption", "decryption service", [("decrypt", RR)])
# The pair of decryption (R-12-13, Issue I-87): a file the bank sends, encrypted for its recipient.
port("p.port.encryption.v1", "File encryption", "encryption service", [("encrypt", RR)])
port("p.port.party-register.v1", "Party register", "bank party register", [("lookup", RR)])
port("uae.p.port.participant-directory.v1", "Participant directory", "central bank participant directory", [("lookup", RR)])
port("uae.p.port.calendar.v1", "Business calendar", "business calendar service", [("business-date", RR)])
port("uae.wps.port.wps.v1", "Wages protection system", "central bank wages protection system",
     [("submit-file", SEND), ("receive-file", RECV)], p=WP)
port("uae.wps.port.labour-ministry.v1", "Labour ministry employer status", "labour ministry establishment register",
     [("employer-status", RR)], p=WP)
port("uae.wps.port.employer-channel.v1", "Employer channel", "employer file channel", [("receive-file", RECV)], p=WP)
port("b.port.reference-data.v1", "Bank reference data", "bank reference data service", [("cost-centre", RR)], base=EXAMPLES)
port("b.port.treasury.v1", "Bank treasury", "bank treasury system", [("notify", SEND)], base=EXAMPLES)


# ---------------------------------------------------------------- simulators
def sc(name, *steps, **kw):
    d = {"name": name, "steps": list(steps)}
    d.update(kw)
    return d


def sim(name, title, direction, port_, stands, scenarios, self_check=None, p=None, lang="typescript", base=FACTORY):
    doc = {"kind": "simulator", "name": name, "release": REL, "title": title, "direction": direction, "port": port_,
           "standsFor": stands, "state": {"keyedBy": "message id"}}
    if self_check:
        doc["selfCheck"] = self_check
    doc["scenarios"] = scenarios
    doc["implementation"] = {"binding": "script", "language": lang, "script": "scripts/%s.%s" % (name, "ts" if lang == "typescript" else "py")}
    doc["provenance"] = prov(**(p or {}))
    emit(doc, base)


def outward_set(req, ok, reject_code):
    """The standard scenario set every outward simulator carries."""
    return [sc("accept", {"expect": req}, {"send": ok, "status": "accepted"}),
            sc("reject", {"expect": req}, {"send": ok, "status": "rejected", "reasonCode": reject_code}),
            sc("pending-then-final", {"expect": req}, {"send": ok, "status": "pending"}, {"send": ok, "status": "accepted", "after": "PT30S"}),
            sc("no-reply", {"expect": req}, {"noReply": True}),
            sc("late-reply", {"expect": req}, {"send": ok, "status": "accepted", "after": "PT1H"}),
            sc("duplicate-reply", {"expect": req}, {"send": ok, "status": "accepted", "duplicate": True}),
            sc("fail-twice-then-accept", {"expect": req, "failTimes": 2}, {"send": ok, "status": "accepted"}),
            sc("malformed-reply", {"expect": req}, {"send": ok, "status": "accepted"}, malformed=True)]


sim("p.sim.core-banking.v1", "Core banking simulator", "outward", "p.port.core-banking.v1", "core banking system",
    outward_set("p.core.request", "p.core.reply", "AM04")
    # the first request is refused, the next two accepted: one credit of a file fails, the rest post
    + [sc("reject-first-then-accept", {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "rejected", "reasonCode": "AC04"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "accepted"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "accepted"}),
       # both credits of a file are refused, then the return is recorded: the whole file goes back (Issue I-40)
       sc("reject-two-then-accept", {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "rejected", "reasonCode": "AC04"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "rejected", "reasonCode": "AC04"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "accepted"}),
       # the first credit's outcome is unknown; the inquiry finds it not posted, so it is returned (Issue I-39)
       sc("unknown-first-then-not-posted", {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "pending"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "rejected", "reasonCode": "AC04"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "accepted"},
          {"expect": "p.core.request"}, {"send": "p.core.reply", "status": "accepted"})])
sim("p.sim.sanctions.v1", "Sanctions engine simulator", "outward", "p.port.sanctions.v1", "sanctions screening engine",
    outward_set("p.sanctions.request", "p.sanctions.reply", "RR04"))
sim("p.sim.fraud.v1", "Fraud engine simulator", "outward", "p.port.fraud.v1", "fraud engine",
    outward_set("p.fraud.request", "p.fraud.reply", "FRAD"))
sim("p.sim.liquidity.v1", "Liquidity service simulator", "outward", "p.port.liquidity.v1", "liquidity service",
    outward_set("p.liquidity.request", "p.liquidity.reply", "AM04"))
sim("p.sim.payment-record.v1", "Payment record simulator", "outward", "p.port.payment-record.v1", "payment record store",
    outward_set("p.record.request", "p.record.reply", "NOOR"))
sim("p.sim.file-transfer.v1", "File transfer simulator", "outward", "p.port.file-transfer.v1", "file transfer service",
    outward_set("p.file.request", "p.file.reply", "NARR"))
sim("p.sim.decryption.v1", "Decryption simulator", "outward", "p.port.decryption.v1", "decryption service",
    outward_set("p.decrypt.request", "p.decrypt.reply", "NARR"))
sim("p.sim.encryption.v1", "Encryption simulator", "outward", "p.port.encryption.v1", "encryption service",
    outward_set("p.encrypt.request", "p.encrypt.reply", "NARR"))
sim("p.sim.channel.v1", "Channel simulator", "outward", "p.port.channel.v1", "initiating channel",
    outward_set("p.channel.notify", "p.channel.ack", "NARR"))
sim("p.sim.facts-out.v1", "Fact consumer simulator", "outward", "p.port.facts-out.v1", "downstream consumers of payment facts",
    outward_set("p.facts.publish", "p.facts.ack", "NARR"))
sim("p.sim.notification.v1", "Operator notification simulator", "outward", "p.port.notification.v1", "the bank's alerting service for operators",
    outward_set("p.notification.alert", "p.notification.ack", "NARR"))
sim("p.sim.event-stream.v1", "Payment event stream simulator", "outward", "p.port.event-stream.v1", "the bank's event streaming platform",
    outward_set("p.events.publish", "p.events.ack", "NARR"))
sim("p.sim.fx-rates.v1", "Exchange-rate service simulator", "outward", "p.port.fx-rates.v1", "the bank's exchange-rate service",
    outward_set("p.fx.rates.request", "p.fx.rates.reply", "NARR"))
sim("p.sim.operations.v1", "Operator simulator", "inward", "p.port.operations-queue.v1", "operations console user",
    [sc("release", {"expect": "p.ops.item"}, {"send": "p.ops.decision", "status": "released", "after": "PT5M"}),
     sc("reject", {"expect": "p.ops.item"}, {"send": "p.ops.decision", "status": "rejected", "after": "PT5M"}),
     sc("approve", {"expect": "p.ops.item"}, {"send": "p.ops.decision", "status": "approved", "after": "PT5M"}),
     sc("decline", {"expect": "p.ops.item"}, {"send": "p.ops.decision", "status": "declined", "after": "PT5M"}),
     sc("never-decides", {"expect": "p.ops.item"}, {"noReply": True})])
sim("uae.p.sim.participant-directory.v1", "Participant directory simulator", "outward", "uae.p.port.participant-directory.v1",
    "central bank participant directory", outward_set("uae.p.directory.request", "uae.p.directory.reply", "RC01"))
sim("uae.p.sim.calendar.v1", "Business calendar simulator", "outward", "uae.p.port.calendar.v1", "business calendar service",
    outward_set("uae.p.calendar.request", "uae.p.calendar.reply", "DT01"))
sim("uae.wps.sim.wps-outward.v1", "WPS simulator, replies to the bank", "outward", "uae.wps.port.wps.v1",
    "central bank wages protection system",
    [sc("accept", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "family": "sif", "status": "accepted", "after": "PT10M"}),
     sc("reject", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "family": "sif", "status": "rejected", "reasonCode": "AM10", "after": "PT10M"}),
     sc("no-reply", {"expect": "uae.wps.sif-submission"}, {"noReply": True}),
     sc("late-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "family": "sif", "status": "accepted", "after": "P2D"}),
     sc("duplicate-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "family": "sif", "status": "accepted", "after": "PT10M", "duplicate": True}),
     sc("fail-twice-then-accept", {"expect": "uae.wps.sif-submission", "failTimes": 2}, {"send": "uae.wps.ack", "family": "sif", "status": "accepted", "after": "PT10M"}),
     sc("refund-settled", {"expect": "uae.wps.rfr"}, {"send": "uae.wps.ack", "family": "rfr", "status": "accepted", "after": "PT10M"}, {"send": "uae.wps.rsu", "after": "P3D"}),
     sc("refund-declined", {"expect": "uae.wps.rfr"}, {"send": "uae.wps.ack", "family": "rfr", "status": "accepted", "after": "PT10M"}, {"send": "uae.wps.rsu", "status": "rejected", "after": "P3D"}),
     sc("receipt-accepted", {"expect": "uae.wps.prc"}, {"send": "uae.wps.ack", "family": "prc", "status": "accepted", "after": "PT10M"}),
     sc("disbursement-accepted", {"expect": "uae.wps.dif"}, {"send": "uae.wps.ack", "family": "dif", "status": "accepted", "after": "PT10M"}),
     sc("malformed-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "family": "sif"}, malformed=True)],
    self_check=["uae.wps.ruleset.salary-file.v1"], p=WP)
def payment_file():
    """The agent's conversation for one payment file, up to the disbursement file. Fresh dicts, so YAML writes no aliases."""
    return ({"send": "uae.wps.pif"}, {"send": "uae.wps.psi"},
            {"expect": "uae.wps.prc"}, {"send": "uae.wps.ack", "family": "prc", "status": "accepted", "after": "PT10M"}, {"expect": "uae.wps.dif"},
            {"send": "uae.wps.ack", "family": "dif", "status": "accepted", "after": "PT10M"})


def payment_file_dif_refused():
    """As payment_file, but WPS refuses the disbursement file (a DIF NAK)."""
    return ({"send": "uae.wps.pif"}, {"send": "uae.wps.psi"},
            {"expect": "uae.wps.prc"}, {"send": "uae.wps.ack", "family": "prc", "status": "accepted", "after": "PT10M"}, {"expect": "uae.wps.dif"},
            {"send": "uae.wps.ack", "family": "dif", "status": "rejected", "reasonCode": "FF01", "after": "PT10M"})



sim("uae.wps.sim.wps-inward.v1", "WPS simulator, sends to the bank", "inward", "uae.wps.port.wps.v1",
    "central bank wages protection system",
    [sc("payment-file", *payment_file()),
     sc("payment-file-wrong-agent", {"send": "uae.wps.pif"}, {"send": "uae.wps.psi"}),
     sc("payment-file-returns-accepted", *payment_file(), {"expect": "uae.wps.rfa"}, {"send": "uae.wps.ack", "family": "rfa", "status": "accepted", "after": "PT10M"}),
     sc("payment-file-returns-refused", *payment_file(), {"expect": "uae.wps.rfa"},
        {"send": "uae.wps.ack", "family": "rfa", "status": "rejected", "reasonCode": "FF01", "after": "PT10M"}),
     sc("payment-file-disbursement-refused-returns-accepted", *payment_file_dif_refused(), {"expect": "uae.wps.rfa"},
        {"send": "uae.wps.ack", "family": "rfa", "status": "accepted", "after": "PT10M"}),
     sc("payment-file-returns-no-reply", *payment_file(), {"expect": "uae.wps.rfa"}, {"noReply": True}),
     sc("return-file", {"send": "uae.wps.rtc"})], self_check=["uae.wps.ruleset.payment-file.v1"], p=WP)
# WPS asks the bank, as the employees' bank, to refund lines of an earlier payment file (R-12-21, the source's RRI flow): the
# refund request instruction (RRI), then the acknowledgements of the bank's disbursement cancellation request (DCR) and of its
# refund request response (RRR). A simulator of its own, because the refund request is no payment file (its self-check would not fit).
def refund_request(dcr="accepted"):
    steps = [{"send": "uae.wps.rri"}, {"expect": "uae.wps.dcr"},
             {"send": "uae.wps.ack", "family": "dcr", "status": dcr, "after": "PT10M", **({"reasonCode": "FF01"} if dcr != "accepted" else {})}]
    if dcr == "accepted":
        steps += [{"expect": "uae.wps.rrr"}, {"send": "uae.wps.ack", "family": "rrr", "status": "accepted", "after": "PT10M"}]
    return steps


sim("uae.wps.sim.wps-refunds.v1", "WPS simulator, a refund request to the bank as the agent", "inward", "uae.wps.port.wps.v1",
    "central bank wages protection system",
    [sc("refund-request", *refund_request()),
     sc("refund-request-refused", {"send": "uae.wps.rri"}, {"expect": "uae.wps.rrr"},
        {"send": "uae.wps.ack", "family": "rrr", "status": "accepted", "after": "PT10M"}),
     sc("refund-request-return-refused", *refund_request("rejected")),
     sc("refund-request-no-reply", {"send": "uae.wps.rri"}, {"expect": "uae.wps.dcr"}, {"noReply": True})], p=WP)
# The bank's party register (R-12-27): registered (accept), not registered, inactive, or registered with other details than supplied.
sim("p.sim.party-register.v1", "Party register simulator", "outward", "p.port.party-register.v1", "bank party register",
    outward_set("p.party-register.request", "p.party-register.reply", "NARR")
    + [sc("not-registered", {"expect": "p.party-register.request"}, {"send": "p.party-register.reply", "status": "not-found"}),
       sc("inactive", {"expect": "p.party-register.request"}, {"send": "p.party-register.reply", "status": "inactive"}),
       sc("mismatch", {"expect": "p.party-register.request"}, {"send": "p.party-register.reply", "status": "mismatch"})])
# The ministry answers an employer's status: active (accept), suspended, or not on its register (Issue I-88).
sim("uae.wps.sim.labour-ministry.v1", "Labour ministry simulator", "outward", "uae.wps.port.labour-ministry.v1", "labour ministry establishment register",
    outward_set("uae.wps.employer-status.request", "uae.wps.employer-status.reply", "NARR")
    + [sc("suspended", {"expect": "uae.wps.employer-status.request"}, {"send": "uae.wps.employer-status.reply", "status": "suspended"}),
       sc("not-registered", {"expect": "uae.wps.employer-status.request"}, {"send": "uae.wps.employer-status.reply", "status": "not-found"})], p=WP)
sim("uae.wps.sim.employer-channel.v1", "Employer simulator", "inward", "uae.wps.port.employer-channel.v1", "employer file channel",
    [sc("valid-file", {"send": "uae.wps.sif"}),
     sc("valid-file-scr-last", {"send": "uae.wps.sif"}),
     sc("bad-control-total", {"send": "uae.wps.sif"}, malformed=True),
     sc("bad-file-name", {"send": "uae.wps.sif"}, malformed=True)], self_check=["uae.wps.ruleset.salary-file.v1"], p=WP)
sim("b.sim.treasury.v1", "Bank treasury simulator", "outward", "b.port.treasury.v1", "bank treasury system",
    outward_set("b.treasury.notify", "b.treasury.ack", "NARR"), lang="python", base=EXAMPLES)
sim("b.sim.reference-data.v1", "Bank reference data simulator", "outward", "b.port.reference-data.v1", "bank reference data service",
    outward_set("b.reference.request", "b.reference.reply", "NARR"), lang="python", base=EXAMPLES)


# ---------------------------------------------------------------- extensions
def ext(name, title, root, fields, p=None, base=FACTORY):
    emit({"kind": "extension", "name": name, "release": REL, "title": title, "root": root,
          "fields": [dict(path=f[0], isoType=f[1], level=f[2], description=f[3], **(f[4] if len(f) > 4 else {})) for f in fields],
          "provenance": prov(**(p or {}))}, base)


ext("uae.wps.ext.salary.v1", "WPS salary fields", "ext.uae.wps",
    [("employerId", "Max35Text", "batch", "Employer unique id, thirteen digits", {"source": "SCR"}),
     ("fileCreationDate", "ISODate", "batch", "File creation date", {"source": "SCR"}),
     ("fileCreationTime", "ISOTime", "batch", "File creation time", {"source": "SCR"}),
     ("salaryMonth", "ISOYearMonth", "batch", "Salary month", {"source": "SCR"}),
     ("employerReference", "Max35Text", "batch", "Optional employer reference", {"source": "SCR"}),
     ("personId", "Max35Text", "item", "Employee person id, fourteen digits", {"source": "EDR", "personalData": True}),
     ("payStartDate", "ISODate", "item", "Pay period start", {"source": "EDR"}),
     ("payEndDate", "ISODate", "item", "Pay period end", {"source": "EDR"}),
     ("daysInPeriod", "Number", "item", "Days paid in the period", {"source": "EDR"}),
     ("fixedPay", "ActiveCurrencyAndAmount", "item", "Fixed income", {"source": "EDR", "personalData": True}),
     ("variablePay", "ActiveCurrencyAndAmount", "item", "Variable income", {"source": "EDR", "personalData": True}),
     ("leaveDays", "Number", "item", "Days of unpaid leave in the period", {"source": "EDR"}),
     # 1.1.0 (Issue I-100): the EVP record's breakdown of the line, RmtInf/Strd semantics. Provisional until the rulebook confirms
     # the EVP layout; the line's amount stays fixed plus variable pay.
     *[(f, "ActiveCurrencyAndAmount", "item", d, {"source": "EVP", "personalData": True}) for f, d in (
         ("housingAllowance", "Housing allowance in the variable pay"), ("conveyanceAllowance", "Conveyance allowance in the variable pay"),
         ("medicalAllowance", "Medical allowance in the variable pay"), ("annualPassageAllowance", "Annual passage allowance in the variable pay"),
         ("overtimeAllowance", "Overtime allowance in the variable pay"), ("otherAllowances", "Other allowances in the variable pay"),
         ("leaveEncashment", "Leave encashment in the variable pay"))],
     # 1.1.0 (Issues I-88, I-100): SplmtryData semantics, no ISO element
     ("uploadChannel", "Max35Text", "batch", "The employer channel the file was uploaded through, as the channel declared it (for example IB or CBX)", {"source": "channel upload"}),
     ("employerStatus", "Max35Text", "batch", "The employer's status at the labour ministry as its register answered: active, suspended or unknown", {"source": "labour ministry"}),
     # 1.2.0 (R-12-21): the refund request instruction's fields with no ISO element. camt.056 carries the reason as a code of its
     # own set (CxlRsnInf/Rsn); the scheme's codes are kept as the scheme gives them until the rulebook maps them (Issue I-134).
     ("establishmentReference", "Max35Text", "batch", "The employer's establishment reference a refund request instruction names", {"source": "RRI file name"}),
     ("fileName", "Max140Text", "batch", "The name of the scheme's file the order came in", {"source": "RRI file name"}),
     ("refundReason", "Max35Text", "item", "The scheme's reason code for refunding a line", {"source": "DRR"}),
     ("payrollWpsFileReference", "Max35Text", "item", "The WPS reference of the payment file whose credit a line refunds", {"source": "DRR"})], p=WP)
ext("b.ext.bank.v1", "Example bank fields", "ext.b",
    [("costCentre", "Max35Text", "payment", "Bank cost centre for the debtor account")], base=EXAMPLES)



# --------------------------------------------------------------------- tests
def bench(*rows):
    return [{"port": p_, "simulator": s, "scenario": c} for p_, s, c in rows]


def test(tid, title, target_, level, given, expect, *, bench_=None, date=DATE, tenant=None, conformance=False, base=FACTORY, scope=None):
    doc = {"kind": "test", "id": tid, "title": title, "target": target_, "level": level}
    if conformance:
        doc["conformance"] = True
    doc["evaluationDate"] = date
    if tenant:
        doc["tenant"] = tenant
    if bench_:
        doc["bench"] = bench_
    doc["given"] = given
    doc["expect"] = expect
    doc["_scope"] = scope or scope_of(target_)
    return emit(doc, base)


CAL = ("uae.p.port.calendar.v1", "uae.p.sim.calendar.v1", "accept")


def salary_file(*lines):
    """A synthetic outward WPS salary file as a canonical context: one item per line, the batch carries the total."""
    items = [{"payment": {"instructedAmount": {"value": v, "currency": "AED"}, "creditorAccount": {"iban": IBAN_OK},
                          "creditorAgent": {"clearingMemberId": "203320101"}}} for v in lines]
    return {"context": {"header": {"paymentId": "P-1", "endToEndId": "E2E-1", "fileId": "F-1", "kind": "payment", "direction": "outward",
                                   "segment": "corporate", "scheme": "uae.wps"},
                        "payment": {"instructedAmount": {"value": sum(lines), "currency": "AED"}, "debtorAccount": {"iban": IBAN_OK},
                                    "debtorAgent": {"clearingMemberId": "302620122"}},
                        "batch": {"id": "F-1", "numberOfTransactions": len(lines), "controlSum": sum(lines)},
                        "items": items}}


PAY_CTX = salary_file(1500)

WPS, WSIM = "uae.wps.port.wps.v1", "uae.wps.sim.wps-outward.v1"
WPS_FILE = {"inbound": "samples/wps-sif-valid.sif"}
# The salary file is encrypted for WPS before it is sent (Issue I-87, R-12-13), and the employer's labour ministry status is asked
# before the file's checks (Issue I-88, R-12-14). The factory's tests name both simulators.
ENC = ("p.port.encryption.v1", "p.sim.encryption.v1", "accept")
LM, LMSIM = "uae.wps.port.labour-ministry.v1", "uae.wps.sim.labour-ministry.v1"
MIN = (LM, LMSIM, "accept")
# R-12-27: the employer is looked up in the bank's party register before its checks; the factory's tests name the simulator.
REG, REGSIM = "p.port.party-register.v1", "p.sim.party-register.v1"
OUT_HEAD = ["business-date:set", "cut-off:taken", "corporate:registered", "employer-status:found", "file-checks:pass", "record-checks:pass", "record-iban:pass", "render:mapped"]
test("WPS-OUT-001", "Salary file acknowledged", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "settled", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 1}],
      "trace": OUT_HEAD + ["submit:accepted", "end:settled"],
      "detail": ["submit.encrypt:encrypted", "submit.send:accepted", "submit.await-ack:received", "submit.read-ack:ack"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, MIN, (REG, REGSIM, "accept")))
test("WPS-OUT-002", "Salary file refused by WPS", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "AM10"}, bench_=bench((WPS, WSIM, "reject"), CAL, ENC, MIN, (REG, REGSIM, "accept")))
test("WPS-OUT-003", "No acknowledgement within the wait ends pending", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "pending", "virtualElapsed": "P1D", "trace": OUT_HEAD + ["submit:pending", "end:pending"],
                                                        "detail": ["submit.encrypt:encrypted", "submit.send:accepted", "submit.await-ack:timeout"]},
     bench_=bench((WPS, WSIM, "no-reply"), CAL, ENC, MIN, (REG, REGSIM, "accept")))
test("WPS-OUT-004", "A control total that does not match is rejected before anything is sent", "uae.wps.wf.salary-file-out.v1", "leg",
     {"inbound": "samples/wps-sif-bad-control-total.sif"},
     {"outcome": "rejected", "reasonCode": "AM10", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, MIN, (REG, REGSIM, "accept")))
test("WPS-OUT-005", "A salary record with an invalid IBAN is rejected by the cluster check", "uae.wps.wf.salary-file-out.v1", "leg",
     {"inbound": "samples/wps-sif-bad-iban.sif"},
     {"outcome": "rejected", "reasonCode": "AC03", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": ["business-date:set", "cut-off:taken", "corporate:registered", "employer-status:found", "file-checks:pass", "record-checks:pass", "record-iban:reject", "end:rejected"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, MIN, (REG, REGSIM, "accept")))
# Issue I-88: the source's bank forks refused the file of an employer the labour ministry has suspended (WPS_RULES_FAILED).
test("WPS-OUT-006", "The file of an employer the labour ministry has suspended is refused before anything is sent", "uae.wps.wf.salary-file-out.v1",
     "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "RR04", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": ["business-date:set", "cut-off:taken", "corporate:registered", "employer-status:found", "file-checks:reject", "end:rejected"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, (LM, LMSIM, "suspended"), (REG, REGSIM, "accept")))
test("WPS-OUT-007", "An employer the labour ministry does not know is not refused for it", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "settled", "trace": ["business-date:set", "cut-off:taken", "corporate:registered", "employer-status:not-found", "file-checks:pass", "record-checks:pass", "record-iban:pass",
                                      "render:mapped", "submit:accepted", "end:settled"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, (LM, LMSIM, "not-registered"), (REG, REGSIM, "accept")))
# Issue I-87: a file the encryption service refuses is not sent.
test("WPS-OUT-008", "A salary file the encryption service refuses is rejected and never sent", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "NARR", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": OUT_HEAD + ["submit:rejected", "end:rejected"], "detail": ["submit.encrypt:refused"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ("p.port.encryption.v1", "p.sim.encryption.v1", "reject"), MIN, (REG, REGSIM, "accept")))
# R-12-27 (Issue I-149): as in the source's pre-check, a file whose employer the bank has not registered, or registered with other
# details than the channel supplied, is refused before anything else is asked or sent.
test("WPS-OUT-009", "The file of an employer the bank has not registered is refused before anything is sent", "uae.wps.wf.salary-file-out.v1",
     "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "RR04", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": ["business-date:set", "cut-off:taken", "corporate:not-registered", "end:rejected"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, MIN, (REG, REGSIM, "not-registered")))
test("WPS-OUT-010", "The file of an employer registered with another debit account is refused before anything is sent", "uae.wps.wf.salary-file-out.v1",
     "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "BE01", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": ["business-date:set", "cut-off:taken", "corporate:mismatch", "end:rejected"]},
     bench_=bench((WPS, WSIM, "accept"), CAL, ENC, MIN, (REG, REGSIM, "mismatch")))
test("WPS-EXC-001", "The composite alone: encrypted, sent, acknowledged, read", "uae.wps.exchange.submit-salary-file.v1", "activity", WPS_FILE,
     {"outcome": "accepted", "detail": ["encrypt:encrypted", "send:accepted", "await-ack:received", "read-ack:ack"]}, bench_=bench((WPS, WSIM, "accept"), ENC),
     conformance=True)
test("WPS-EXC-002", "The composite alone: no acknowledgement within the reply time is pending", "uae.wps.exchange.submit-salary-file.v1", "activity",
     WPS_FILE, {"outcome": "pending", "virtualElapsed": "P1D", "detail": ["encrypt:encrypted", "send:accepted", "await-ack:timeout"]},
     bench_=bench((WPS, WSIM, "no-reply"), ENC), conformance=True)
test("WPS-EXC-003", "The composite alone: a file the encryption service refuses is rejected unsent", "uae.wps.exchange.submit-salary-file.v1", "activity",
     WPS_FILE, {"outcome": "rejected", "detail": ["encrypt:refused"], "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}]},
     bench_=bench((WPS, WSIM, "accept"), ("p.port.encryption.v1", "p.sim.encryption.v1", "reject")))
test("WPS-MAP-001", "Salary file maps in and back out unchanged", "uae.wps.map.sif-in.v1", "activity", WPS_FILE,
     {"outcome": "mapped"}, conformance=True)
test("WPS-MAP-002", "The SCR-last channel variant maps to the same canonical batch", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-valid-scr-last.sif"}, {"outcome": "mapped"}, conformance=True)
# Issue I-100: the channel's upload (initiator, employer name and debit account, channel) and the EVP allowances reach the batch.
test("WPS-MAP-003", "The channel's upload and the EVP allowances map into the batch and its lines", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-upload-evp.sif"},
     {"outcome": "mapped", "writes": {"batch.initiatingParty.id": "U-0001", "batch.debtor.name": "Example Trading LLC",
                                      # R-02-21, R-12-27: the employer's establishment id as the debtor's organisation id
                                      "batch.debtor.organisationId.id": "0000001234567", "batch.debtor.organisationId.scheme": "MOL",
                                      "batch.debtorAccount.iban": IBAN_OK, "ext.uae.wps.uploadChannel": "IB",
                                      "items.0.ext.uae.wps.housingAllowance": "200.00", "items.0.ext.uae.wps.otherAllowances": "50.00",
                                      "items.0.ext.uae.wps.leaveEncashment": "0.00", "items.0.payment.instructedAmount.value": "1500.00",
                                      "items.1.ext.uae.wps.fixedPay": "900.00", "batch.controlSum": "2400.00"}})
# Issue I-109: a control total of sixteen significant digits keeps every digit, as a decimal string (R-02-17).
test("WPS-MAP-004", "A salary file whose total has sixteen digits maps every digit exactly", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-large-total.sif"},
     {"outcome": "mapped", "writes": {"batch.controlSum": "12345678901234.56", "items.0.payment.instructedAmount.value": "12345678901234.56",
                                      "items.0.ext.uae.wps.fixedPay": "12345678901233.99", "items.0.ext.uae.wps.variablePay": "0.57"}})
# Issue I-115: a line's account type is the one the channel declared at upload, and its bank's name the participant directory's.
test("WPS-MAP-005", "Each line's account type comes from the channel's upload and its bank's name from the participant directory", "uae.wps.map.sif-in.v1",
     "activity", {"inbound": "samples/wps-sif-upload-account-type.sif"},
     {"outcome": "mapped", "writes": {"items.0.payment.creditorAccount.type": "CACC", "items.0.payment.creditorAgent.name": "Example Bank One",
                                      "items.1.payment.creditorAccount.type": "CACC", "items.1.payment.creditorAgent.name": "Example Bank Two"}},
     tenant="example-bank")
# Issue I-195 (R-05-85): a file with no `# file:` line, as a bank's channel drops it on the SFTP server or posts it to the File API,
# takes its name from the inbound message's fileName; with neither, the edge rule refuses it as before.
test("WPS-MAP-006", "A salary file with no file line takes its name from the File API", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-no-file-line.sif", "fileName": "0000001234567261117083000.sif"},
     {"outcome": "mapped", "writes": {"header.fileId": "0000001234567261117083000", "batch.id": "0000001234567261117083000",
                                      "batch.controlSum": "1500.00"}})
test("WPS-MAP-007", "A salary file with no file line and no name from the File API is unmappable", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-no-file-line.sif"}, {"outcome": "unmappable", "reasonCode": "FF01"})
test("WPS-ENR-001", "The labour ministry knows the employer: its status is written", "uae.wps.enrich.employer-status.v1", "activity",
     {"context": {"header": {"paymentId": "P-1", "scheme": "uae.wps"}, "ext": {"uae": {"wps": {"employerId": "0000001234567"}}}}},
     {"outcome": "found", "writes": {"ext.uae.wps.employerStatus": "suspended"}}, bench_=bench((LM, LMSIM, "suspended")))
test("WPS-ENR-002", "The labour ministry does not know the employer: no status, and the file is not refused for it", "uae.wps.enrich.employer-status.v1",
     "activity", {"context": {"header": {"paymentId": "P-1", "scheme": "uae.wps"}, "ext": {"uae": {"wps": {"employerId": "0000001234567"}}}}},
     {"outcome": "not-found", "writes": {"ext.uae.wps.employerStatus": "unknown"}}, bench_=bench((LM, LMSIM, "not-registered")))
REG_CTX = {"context": {"header": {"paymentId": "P-1", "scheme": "uae.wps"},
                       "batch": {"debtor": {"organisationId": {"id": "0000001234567", "scheme": "MOL"}}, "debtorAccount": {"iban": IBAN_OK}}}}
test("P-REG-001", "A registered, active debtor is registered", "p.enrich.party-register.v1", "activity", REG_CTX,
     {"outcome": "registered", "writes": {"facts.partyRegister.found": True, "facts.partyRegister.active": True}}, bench_=bench((REG, REGSIM, "accept")))
test("P-REG-002", "A debtor the register does not hold is not registered", "p.enrich.party-register.v1", "activity", REG_CTX,
     {"outcome": "not-registered", "reasonCode": "RR04", "writes": {"facts.partyRegister.found": False}}, bench_=bench((REG, REGSIM, "not-registered")))
test("P-REG-003", "An inactive registration is not registered", "p.enrich.party-register.v1", "activity", REG_CTX,
     {"outcome": "not-registered", "reasonCode": "RR04", "writes": {"facts.partyRegister.found": True, "facts.partyRegister.active": False}},
     bench_=bench((REG, REGSIM, "inactive")))
test("P-REG-004", "A registration whose details differ from what was supplied is a mismatch", "p.enrich.party-register.v1", "activity", REG_CTX,
     {"outcome": "mismatch", "reasonCode": "BE01", "writes": {"facts.partyRegister.found": True}}, bench_=bench((REG, REGSIM, "mismatch")))
test("WPS-IN-001", "Payment file confirmed, employees credited, disbursement reported", "p.wf.inward.v2", "end-to-end",
     {"inbound": "samples/wps-pif-valid.pif"},
     {"outcome": "settled", "messages": [{"port": WPS, "message": "uae.wps.prc", "count": 1}, {"port": WPS, "message": "uae.wps.dif", "count": 1},
                                         {"port": WPS, "message": "uae.wps.rfa", "count": 0}]},
     bench_=bench((WPS, "uae.wps.sim.wps-inward.v1", "payment-file"), ("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"),
                  ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept"), ENC), tenant="example-bank", scope="uae.wps")
# The agent's returns file (RFA): one of the two credits is refused, so the leg returns it to WPS.
PIF = {"inbound": "samples/wps-pif-valid.pif"}
AGENT_LEG = "uae.wps.wf.agent-payment-in.v2"


def returns_bench(wps_scenario, core="reject-first-then-accept"):
    return bench((WPS, "uae.wps.sim.wps-inward.v1", wps_scenario), ("p.port.core-banking.v1", "p.sim.core-banking.v1", core),
                 ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept"),
                 ("p.port.operations-queue.v1", "p.sim.operations.v1", "reject"), ENC)


def rfa_head(credits=("credit[0]:rejected", "credit[1]:posted"), dif="accepted"):
    return (["supplement:received", "file-checks:pass", "render-receipt:mapped", "confirm-receipt:accepted", "posting-window:open", *credits,
             "render-disbursement:mapped", "report-disbursement:%s" % dif, "returns:returns", "render-returns:mapped"])


_RFA_HEAD = rfa_head()
TRACE_RFA = {"ack": _RFA_HEAD + ["return-credits:accepted", "record-return:posted", "end:settled"],
             "nak": _RFA_HEAD + ["return-credits:rejected", "returns-operations:rejected", "end:pending"],
             "timeout": _RFA_HEAD + ["return-credits:pending", "end:pending"]}


def rfa_detail(dif="ack"):
    # Every file the agent sends is encrypted for WPS first (Issue I-117, R-12-17).
    return ["supplement.await-supplement:received", "confirm-receipt.encrypt:encrypted", "confirm-receipt.send:accepted", "confirm-receipt.await-ack:received",
            "confirm-receipt.read-ack:ack", "posting-window.window:open", "report-disbursement.encrypt:encrypted", "report-disbursement.send:accepted",
            "report-disbursement.await-ack:received", "report-disbursement.read-ack:%s" % dif, "return-credits.upload-hold:PT0S",
            "return-credits.encrypt:encrypted", "return-credits.send:accepted"]


_RFA_DETAIL = rfa_detail()
DETAIL_RFA = {"ack": _RFA_DETAIL + ["return-credits.await-ack:received", "return-credits.read-ack:ack"],
              "nak": _RFA_DETAIL + ["return-credits.await-ack:received", "return-credits.read-ack:nak"],
              "timeout": _RFA_DETAIL + ["return-credits.await-ack:timeout"]}
RFA_SENT = [{"port": WPS, "message": "uae.wps.dif", "count": 1}, {"port": WPS, "message": "uae.wps.rfa", "count": 1}]
test("WPS-IN-002", "A refused credit is returned in a returns file; on ACK the return is recorded", AGENT_LEG, "end-to-end", PIF,
     {"outcome": "settled", "messages": RFA_SENT, "trace": TRACE_RFA["ack"], "detail": DETAIL_RFA["ack"]},
     bench_=returns_bench("payment-file-returns-accepted"), tenant="example-bank")
test("WPS-IN-003", "A returns file that WPS refuses goes to operations and ends pending", AGENT_LEG, "end-to-end", PIF,
     {"outcome": "pending", "messages": RFA_SENT, "trace": TRACE_RFA["nak"], "detail": DETAIL_RFA["nak"]},
     bench_=returns_bench("payment-file-returns-refused"), tenant="example-bank")
# The example bank waits ten business days for the acknowledgement (its value of replyWait, Issue I-41): two weeks.
# The bank's ten business days counted on its business calendar (R-09-183, Issue I-166): the platform's UAE table makes 1 to 3 December
# 2026 holidays, so the wait from 17 November ends on 4 December, seventeen days on (fourteen on Saturday and Sunday alone).
test("WPS-IN-004", "No acknowledgement of the returns file within the bank's wait ends pending", AGENT_LEG, "end-to-end", PIF,
     {"outcome": "pending", "messages": RFA_SENT, "virtualElapsed": "P17D", "trace": TRACE_RFA["timeout"], "detail": DETAIL_RFA["timeout"]},
     bench_=returns_bench("payment-file-returns-no-reply"), tenant="example-bank")
# Issue I-40: no credit reached an employee and the whole amount went back, so the leg ends returned.
test("WPS-IN-005", "Every credit refused: the whole file is returned and the leg ends returned", AGENT_LEG, "end-to-end", PIF,
     {"outcome": "returned", "messages": RFA_SENT,
      "trace": rfa_head(("credit[0]:rejected", "credit[1]:rejected")) + ["return-credits:accepted", "record-return:posted", "end:returned"],
      "detail": DETAIL_RFA["ack"]},
     bench_=returns_bench("payment-file-returns-accepted", "reject-two-then-accept"), tenant="example-bank")
# Issue I-39: a credit whose outcome is unknown is asked about first; the inquiry finds it not posted, so it is returned.
test("WPS-IN-006", "A credit whose outcome is unknown is verified before the returns are decided, and returned when not posted", AGENT_LEG,
     "end-to-end", PIF,
     {"outcome": "settled", "messages": RFA_SENT,
      "trace": rfa_head(("credit[0]:unknown", "verify-credit[0]:mismatch", "credit[1]:posted")) + ["return-credits:accepted", "record-return:posted", "end:settled"],
      "detail": DETAIL_RFA["ack"]},
     bench_=returns_bench("payment-file-returns-accepted", "unknown-first-then-not-posted"), tenant="example-bank")
# Issue I-39: the returns are decided after the disbursement file's NAK too; the file then stays with operations.
test("WPS-IN-007", "After a refused disbursement file the refused credits are still returned, and the leg ends pending", AGENT_LEG,
     "end-to-end", PIF,
     {"outcome": "pending", "messages": RFA_SENT,
      "trace": rfa_head(dif="rejected") + ["return-credits:accepted", "record-return:posted", "end:pending"],
      "detail": rfa_detail("nak") + ["return-credits.await-ack:received", "return-credits.read-ack:ack"]},
     bench_=returns_bench("payment-file-disbursement-refused-returns-accepted"), tenant="example-bank")
# Plan P2: the source's PIF layout (EDR, EVP, PCR), mapped by the 1.1.0 map, and by the runtime's native code-behind when the
# run gives it to the runtime (R-08-16, e2e-wps-inward-native). The same file as WPS-IN-001 otherwise.
test("WPS-IN-008", "A payment file in the source's layout is confirmed, credited and reported", "p.wf.inward.v2", "end-to-end",
     {"inbound": "samples/wps-pif-ssm.pif"},
     {"outcome": "settled", "messages": [{"port": WPS, "message": "uae.wps.prc", "count": 1}, {"port": WPS, "message": "uae.wps.dif", "count": 1},
                                         {"port": WPS, "message": "uae.wps.rfa", "count": 0}]},
     bench_=bench((WPS, "uae.wps.sim.wps-inward.v1", "payment-file"), ("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"),
                  ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept"), ENC), tenant="example-bank", scope="uae.wps")
# Issue I-117: a receipt confirmation the encryption service refuses is never sent; nothing after it is sent or credited, and the
# leg ends pending as it does for a refused confirmation.
test("WPS-IN-009", "A receipt confirmation the encryption service refuses is not sent, and the file stays pending with nothing credited", AGENT_LEG,
     "end-to-end", PIF,
     {"outcome": "pending", "trace": ["supplement:received", "file-checks:pass", "render-receipt:mapped", "confirm-receipt:rejected", "end:pending"], "messages": [{"port": WPS, "message": "uae.wps.prc", "count": 0}, {"port": WPS, "message": "uae.wps.dif", "count": 0}],
      "detail": ["supplement.await-supplement:received", "confirm-receipt.encrypt:refused"]},
     bench_=bench((WPS, "uae.wps.sim.wps-inward.v1", "payment-file"), ("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"),
                  ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept"),
                  ("p.port.operations-queue.v1", "p.sim.operations.v1", "reject"), ("p.port.encryption.v1", "p.sim.encryption.v1", "reject")),
     tenant="example-bank")
# Plan P2: a return to the employer's bank; the map names the employer's account, and the leg credits it in the posting window.
test("WPS-RTC-001", "A return file credits the employer's account", "uae.wps.wf.return-in.v1", "end-to-end",
     {"inbound": "samples/wps-rtc-valid.rtc"},
     {"outcome": "settled", "trace": ["posting-window:open", "credit-employer:posted", "end:settled"], "detail": ["posting-window.window:open"]},
     bench_=bench((WPS, "uae.wps.sim.wps-inward.v1", "return-file"), ("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"),
                  ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept")), tenant="example-bank")
# R-12-20: FX and charges are platform activities on the bank's values. The example bank's are synthetic (Issue I-58 for a real
# bank's): AED to USD at 0.2723, and the source's worked example of its charge configuration (a fixed 5.00 plus 0.10 per cent,
# floor 5, ceiling 250, VAT 5 per cent): a line of 100.00 bears 5.10 and 0.26 VAT, 5.36 in all.
def whole(x):
    return int(x) if float(x).is_integer() else round(x, 2)


def refund_lines(*lines, currency=None):
    items = [{"payment": {"instructedAmount": {"value": v, "currency": "AED"},
                          "debtorAccount": {"iban": IBAN_OK, **({"currency": currency} if currency else {})}}} for v in lines]
    return {"context": {"header": {"paymentId": "R-1", "kind": "refund", "direction": "inward", "scheme": "uae.wps", "originalPaymentId": "P-0"},
                        # a whole total as an integer, so the YAML and every reader's canonical JSON agree (600, not 600.0)
                        "batch": {"id": "R-1", "numberOfTransactions": len(lines), "controlSum": whole(sum(lines)), "currency": "AED"},
                        "items": items}}


test("P-FX-001", "A line in its account's own currency converts at one", "p.enrich.fx-rate.v1", "activity", refund_lines(100.25, 499.75),
     {"outcome": "converted", "writes": {"facts.fx.rate": 1, "facts.fx.amount": 600, "facts.fx.currency": "AED"}})
test("P-FX-002", "A line in another currency converts at the bank's rate, exactly, half away from zero", "p.enrich.fx-rate.v1", "activity",
     refund_lines(100, 0.05, currency="USD"),
     # 27.23 and 0.01 (0.013615, half away from zero): the sum of the rounded lines, 27.24
     {"outcome": "converted", "writes": {"facts.fx.rate": 0.2723, "facts.fx.amount": 27.24, "facts.fx.currency": "USD"}}, tenant="example-bank")
test("P-FX-003", "A line in another currency with no rate the bank has set has no rate", "p.enrich.fx-rate.v1", "activity",
     refund_lines(100, currency="USD"), {"outcome": "no-rate", "writes": {"facts.fx.missing": "AED/USD"}})
# The staleness limit (R-09-178, Issue I-189): the example bank's rates of 1 November 2026 (synthetic) carry the feed's asOf; the test's
# clock is 06:00 on 17 November, so the AED/EUR rate of 15 November 09:00 is older than the default PT24H and AED/GBP of 16 November
# 12:00 is not.
test("P-FX-004", "A rate older than the bank's staleness limit is refused with fx.RateStale", "p.enrich.fx-rate.v1", "activity",
     refund_lines(100, currency="EUR"),
     {"outcome": "no-rate", "writes": {"facts.fx.stale": "AED/EUR", "facts.fx.code": "fx.RateStale", "facts.fx.maxRateAge": "PT24H"}}, tenant="example-bank")
test("P-FX-005", "A rate the feed gave within the staleness limit converts, and the line keeps its asOf", "p.enrich.fx-rate.v1", "activity",
     refund_lines(100, currency="GBP"),
     {"outcome": "converted", "writes": {"facts.fx.rate": 0.2134, "facts.fx.amount": 21.34, "facts.fx.currency": "GBP"}}, tenant="example-bank")
# requireAsOf (R-09-247, owner's decision of 24 November 2026, Issue I-278): a bank on the rates feed sets it, so the example bank's
# typed AED/USD rate, which has no asOf, is refused like a stale one; its feed's AED/GBP rate, dated, still converts.
test("P-FX-006", "With requireAsOf a rate with no asOf, typed by hand, is refused with fx.RateUndated", "p.enrich.fx-rate.v1", "activity",
     {**refund_lines(100, currency="USD"), "parameters": {"requireAsOf": True}},
     {"outcome": "no-rate", "writes": {"facts.fx.undated": "AED/USD", "facts.fx.code": "fx.RateUndated", "facts.fx.requireAsOf": True}}, tenant="example-bank")
test("P-FX-007", "With requireAsOf a rate the feed gave within the staleness limit still converts", "p.enrich.fx-rate.v1", "activity",
     {**refund_lines(100, currency="GBP"), "parameters": {"requireAsOf": True}},
     {"outcome": "converted", "writes": {"facts.fx.rate": 0.2134, "facts.fx.amount": 21.34, "facts.fx.currency": "GBP"}}, tenant="example-bank")
test("P-CHG-001", "A line is priced on the bank's slab: fixed plus a percentage, then VAT", "p.enrich.charges.v1", "activity", refund_lines(100, 499.75),
     # 5.10 and 0.26 VAT on 100.00; 5.49975, so 5.50 and 0.28 (0.275) on 499.75: the sums of the rounded lines
     {"outcome": "priced", "writes": {"facts.charges.amount": 10.6, "facts.charges.tax.amount": 0.54, "facts.charges.totalChargesAndTaxAmount": 11.14}},
     tenant="example-bank")
test("P-CHG-002", "With no slab the bank has set, a line bears no charge", "p.enrich.charges.v1", "activity", refund_lines(100),
     {"outcome": "none", "writes": {"facts.charges.amount": 0, "facts.charges.totalChargesAndTaxAmount": 0}})
# R-12-19, R-12-21: a refund request instruction from WPS is a new refund order on p.wf.refund.v1; WPS is its leg. The order names
# the earlier payment file by its id; the platform converts, prices, blocks, holds for approval and posts; the leg returns the funds
# (DCR) and answers (RRR). A refusal is answered by the refuse leg, with nothing posted.
RRI = {"inbound": "samples/wps-rri-valid.rri"}
REFUND = "p.wf.refund.v1"
REFUNDS_SIM = "uae.wps.sim.wps-refunds.v1"


def refund_bench(wps="refund-request", core="accept", record="accept", ops="approve"):
    return bench((WPS, REFUNDS_SIM, wps), ("p.port.core-banking.v1", "p.sim.core-banking.v1", core),
                 ("p.port.payment-record.v1", "p.sim.payment-record.v1", record), ("p.port.operations-queue.v1", "p.sim.operations.v1", ops), ENC,
                 ("p.port.channel.v1", "p.sim.channel.v1", "accept"), ("p.port.facts-out.v1", "p.sim.facts-out.v1", "accept"))


REFUND_HEAD = ["original:found", "fx:converted", "charges:priced", "reserve:reserved", "approval:approved"]
REFUND_FINALLY = ["finally:notify", "finally:publish"]
test("WPS-RRI-001", "A refund request instruction maps to a refund order that names the earlier payment file", "uae.wps.map.rri-in.v1", "activity", RRI,
     {"outcome": "mapped", "writes": {"header.originalPaymentId": "000000000000000000000001", "header.fileId": "609000042725",
                                      "batch.controlSum": 600, "batch.numberOfTransactions": 2, "items.0.payment.instructedAmount.value": 100,
                                      "items.1.ext.uae.wps.refundReason": "003", "ext.uae.wps.establishmentReference": "0000000001234"}},
     tenant="example-bank")
test("WPS-RRI-002", "A refund order: converted, priced, blocked, approved and posted by the platform; WPS returns the funds and answers",
     REFUND, "end-to-end", RRI,
     {"outcome": "settled", "trace": REFUND_HEAD + ["post:posted", "verify:verified", "leg:settled"] + REFUND_FINALLY + ["end:settled"],
      "messages": [{"port": WPS, "message": "uae.wps.dcr", "count": 1}, {"port": WPS, "message": "uae.wps.rrr", "count": 1}]},
     bench_=refund_bench(), tenant="example-bank", scope="uae.wps")
test("WPS-RRI-003", "The approver declines the refund: the block is released, nothing is posted, and WPS is answered by the refuse leg",
     REFUND, "end-to-end", RRI,
     {"outcome": "rejected", "trace": ["original:found", "fx:converted", "charges:priced", "reserve:reserved", "approval:declined", "refuse-declined:refused",
                                       "undo:reserve"] + REFUND_FINALLY + ["end:rejected"],
      "messages": [{"port": WPS, "message": "uae.wps.dcr", "count": 0}, {"port": WPS, "message": "uae.wps.rrr", "count": 1}]},
     bench_=refund_bench("refund-request-refused", ops="decline"), tenant="example-bank", scope="uae.wps")
test("WPS-RRI-004", "An account without the funds to block refuses the refund before any approval", REFUND, "end-to-end", RRI,
     {"outcome": "rejected", "trace": ["original:found", "fx:converted", "charges:priced", "reserve:insufficient", "refuse-insufficient:refused"] + REFUND_FINALLY + ["end:rejected"],
      "messages": [{"port": WPS, "message": "uae.wps.dcr", "count": 0}, {"port": WPS, "message": "uae.wps.rrr", "count": 1}]},
     bench_=refund_bench("refund-request-refused", core="reject"), tenant="example-bank", scope="uae.wps")
test("WPS-RRI-005", "A refund of a payment the record does not hold is refused", REFUND, "end-to-end", RRI,
     {"outcome": "rejected", "trace": ["original:not-found", "refuse-unknown:refused"] + REFUND_FINALLY + ["end:rejected"],
      "messages": [{"port": WPS, "message": "uae.wps.rrr", "count": 1}]},
     bench_=refund_bench("refund-request-refused", record="reject"), tenant="example-bank", scope="uae.wps")
test("WPS-RRI-006", "WPS refuses the returned funds: posted, so the order goes to review, and no answer is sent", REFUND, "end-to-end", RRI,
     {"outcome": "review", "trace": REFUND_HEAD + ["post:posted", "verify:verified", "leg:rejected"] + REFUND_FINALLY + ["end:review"],
      "messages": [{"port": WPS, "message": "uae.wps.dcr", "count": 1}, {"port": WPS, "message": "uae.wps.rrr", "count": 0}]},
     bench_=refund_bench("refund-request-return-refused"), tenant="example-bank", scope="uae.wps")
test("WPS-RRI-007", "The agent's refund leg alone: the funds returned and the request answered, each encrypted and acknowledged",
     "uae.wps.wf.refund-request-in.v1", "end-to-end", RRI,
     {"outcome": "settled", "trace": ["render-return:mapped", "return-funds:accepted", "render-answer:mapped", "answer:accepted", "end:settled"],
      "detail": ["return-funds.encrypt:encrypted", "return-funds.send:accepted", "return-funds.await-ack:received", "return-funds.read-ack:ack",
                 "answer.encrypt:encrypted", "answer.send:accepted", "answer.await-ack:received", "answer.read-ack:ack"]},
     bench_=refund_bench(), tenant="example-bank")
# R-12-23 (Issue I-136): the order's posting reads the platform's FX and charges. Each line posts its converted amount, then its
# charge and its VAT as entries of their own, on exact decimals; the template prices nothing itself.
def with_facts(ctx, lines, fx=None, charges=None):
    """A refund order's context with the facts its FX and charges steps wrote: each line's (items[].facts) and the order's."""
    c = copy.deepcopy(ctx)
    for item, (lfx, lch) in zip(c["context"]["items"], lines):
        item["facts"] = {k: v for k, v in (("fx", lfx), ("charges", lch)) if v is not None}
    c["context"]["facts"] = {k: v for k, v in (("fx", fx), ("charges", charges)) if v is not None}
    return c


def line_fx(amount, rate=1, to="AED"):
    return {"outcome": "converted", "from": "AED", "to": to, "rate": rate, "source": "same-currency" if to == "AED" else "bank", "amount": amount}


def line_charge(amount, vat, currency="AED"):
    return {"outcome": "priced" if amount else "none", "amount": amount, "currency": currency, "tax": {"rate": 5, "amount": vat},
            "totalChargesAndTaxAmount": round(amount + vat, 2)}


CORE_ACCEPT = bench(("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"))
POST2 = "p.post.accounting.v2"
test("P-POST-001", "A refund order posts each line's converted amount, then its charge and its VAT as entries of their own", POST2, "activity",
     with_facts(refund_lines(100, 499.75), [(line_fx(100), line_charge(5.1, 0.26)), (line_fx(499.75), line_charge(5.5, 0.28))],
                fx={"outcome": "converted", "lines": 2, "currency": "AED", "amount": 599.75, "rate": 1},
                charges={"outcome": "priced", "lines": 2, "amount": 10.6, "currency": "AED", "tax": {"rate": 5, "amount": 0.54}, "totalChargesAndTaxAmount": 11.14}),
     # 599.75 debited from the lines' accounts, 10.60 charged and 0.54 VAT: 610.89 in all, each line's entries in line order
     {"outcome": "posted", "writes": {"facts.posting.amount": 599.75, "facts.posting.currency": "AED", "facts.posting.charges.amount": 10.6,
                                      "facts.posting.charges.tax.amount": 0.54, "facts.posting.charges.totalChargesAndTaxAmount": 11.14,
                                      "facts.posting.totalAmount": 610.89, "facts.posting.entryCount": 12,
                                      "facts.posting.entries.0.creditDebitIndicator": "DBIT", "facts.posting.entries.0.amount": 100,
                                      "facts.posting.entries.1.creditDebitIndicator": "CRDT", "facts.posting.entries.1.amount": 100,
                                      "facts.posting.entries.2.amount": 5.1, "facts.posting.entries.2.transactionCode": "CHG",
                                      "facts.posting.entries.4.amount": 0.26, "facts.posting.entries.4.transactionCode": "VAT",
                                      "facts.posting.entries.6.amount": 499.75, "facts.posting.entries.8.amount": 5.5, "facts.posting.entries.10.amount": 0.28}},
     bench_=CORE_ACCEPT)
test("P-POST-002", "A line converted into another currency posts the converted amount in that currency, with no charge entries when none was priced",
     POST2, "activity",
     with_facts(refund_lines(100, currency="USD"), [(line_fx(27.23, 0.2723, "USD"), line_charge(0, 0, "USD"))],
                fx={"outcome": "converted", "lines": 1, "currency": "USD", "amount": 27.23, "rate": 0.2723},
                charges={"outcome": "none", "lines": 1, "amount": 0, "currency": "USD", "tax": {"rate": 5, "amount": 0}, "totalChargesAndTaxAmount": 0}),
     {"outcome": "posted", "writes": {"facts.posting.amount": 27.23, "facts.posting.currency": "USD", "facts.posting.totalAmount": 27.23,
                                      "facts.posting.entryCount": 2, "facts.posting.entries.0.currency": "USD", "facts.posting.entries.0.amount": 27.23}},
     bench_=CORE_ACCEPT)
REFUND_OUT_CTX = {"context": {"header": {"paymentId": "R-2", "fileId": "RFR-2", "endToEndId": "RFR-2", "kind": "refund", "direction": "outward",
                                         "scheme": "uae.wps", "originalPaymentId": "P-1"},
                              "payment": {"creditorAccount": {"iban": IBAN_OK}, "instructedAmount": {"value": 600, "currency": "AED"}},
                              "batch": {"id": "RFR-2", "numberOfTransactions": 1, "controlSum": 600, "currency": "AED"}}}
test("P-POST-003", "An outward refund order credits its customer's account with the settled amount and debits the charge and the VAT",
     POST2, "activity",
     {"context": dict(REFUND_OUT_CTX["context"], facts={
         "fx": {"outcome": "converted", "lines": 1, "currency": "AED", "amount": 600, "rate": 1},
         "charges": {"outcome": "priced", "lines": 1, "amount": 5.6, "currency": "AED", "tax": {"rate": 5, "amount": 0.28}, "totalChargesAndTaxAmount": 5.88}})},
     # 600.00 credited, 5.60 and 0.28 debited: 594.12 net to the customer
     {"outcome": "posted", "writes": {"facts.posting.amount": 600, "facts.posting.totalAmount": 594.12, "facts.posting.entryCount": 6,
                                      "facts.posting.entries.0.creditDebitIndicator": "CRDT", "facts.posting.entries.0.amount": 600,
                                      "facts.posting.entries.1.creditDebitIndicator": "DBIT", "facts.posting.entries.2.creditDebitIndicator": "DBIT",
                                      "facts.posting.entries.2.amount": 5.6, "facts.posting.entries.4.amount": 0.28}},
     bench_=CORE_ACCEPT)
test("P-POST-004", "Core banking refuses the posting: it is rejected, with its entries kept", POST2, "activity",
     with_facts(refund_lines(100), [(line_fx(100), None)], fx={"outcome": "converted", "lines": 1, "currency": "AED", "amount": 100, "rate": 1}),
     {"outcome": "rejected", "writes": {"facts.posting.outcome": "rejected", "facts.posting.entryCount": 2}},
     bench_=bench(("p.port.core-banking.v1", "p.sim.core-banking.v1", "reject")))
# R-12-24: the employer's side of a refund is an outward refund order on p.wf.refund-out.v1, WPS its leg. The leg builds the refund
# request (RFR), encrypts and sends it, reads its acknowledgement and waits for the settlement (RSU); the platform then converts,
# prices, posts the credit to the customer's account and verifies it. The leg moves no money (L47).
REFUND_OUT = "p.wf.refund-out.v1"


def refund_out_bench(wps="refund-settled", core="accept", record="accept"):
    return bench((WPS, "uae.wps.sim.wps-outward.v1", wps), ("p.port.core-banking.v1", "p.sim.core-banking.v1", core),
                 ("p.port.payment-record.v1", "p.sim.payment-record.v1", record), ENC,
                 ("p.port.channel.v1", "p.sim.channel.v1", "accept"), ("p.port.facts-out.v1", "p.sim.facts-out.v1", "accept"))


test("WPS-RFR-001", "An outward refund order: WPS settles the refund the bank asked for, and the platform credits its customer",
     REFUND_OUT, "end-to-end", REFUND_OUT_CTX,
     {"outcome": "settled", "trace": ["original:found", "leg:settled", "fx:converted", "charges:priced", "post:posted", "verify:verified"] + REFUND_FINALLY + ["end:settled"],
      "messages": [{"port": WPS, "message": "uae.wps.rfr", "count": 1}]},
     bench_=refund_out_bench(), tenant="example-bank", scope="uae.wps")
test("WPS-RFR-002", "WPS declines the refund: nothing is posted and the order is rejected", REFUND_OUT, "end-to-end", REFUND_OUT_CTX,
     {"outcome": "rejected", "trace": ["original:found", "leg:rejected"] + REFUND_FINALLY + ["end:rejected"],
      "messages": [{"port": WPS, "message": "uae.wps.rfr", "count": 1}]},
     bench_=refund_out_bench("refund-declined"), tenant="example-bank", scope="uae.wps")
test("WPS-RFR-003", "The employer's refund leg alone: the request built, encrypted, sent and acknowledged, and the settlement read",
     "uae.wps.wf.refund-out.v2", "end-to-end", REFUND_OUT_CTX,
     {"outcome": "settled", "trace": ["render:mapped", "request:accepted", "end:settled"],
      "detail": ["request.encrypt:encrypted", "request.send:accepted", "request.await-ack:received", "request.read-ack:ack",
                 "request.await-settlement:received", "request.read-settlement:settled"]},
     bench_=refund_out_bench(), tenant="example-bank")
# Issue I-87: the encryption block alone, one test per outcome (R-03-16).
test("P-ENC-001", "A file the encryption service encrypts is encrypted", "p.protect.encrypt-file.v1", "activity", PAY_CTX,
     {"outcome": "encrypted"}, bench_=bench(("p.port.encryption.v1", "p.sim.encryption.v1", "accept")))
test("P-ENC-002", "A file the encryption service refuses is refused, and not retried", "p.protect.encrypt-file.v1", "activity", PAY_CTX,
     {"outcome": "refused", "attempts": {"protect": 1}}, bench_=bench(("p.port.encryption.v1", "p.sim.encryption.v1", "reject")))
# Issue I-117: the decryption block alone, one test per outcome (R-03-16).
test("P-DEC-001", "A received file the decryption service decrypts is decrypted", "p.protect.decrypt-file.v1", "activity", PAY_CTX,
     {"outcome": "decrypted"}, bench_=bench(("p.port.decryption.v1", "p.sim.decryption.v1", "accept")))
test("P-DEC-002", "A received file the decryption service refuses is refused, and not retried", "p.protect.decrypt-file.v1", "activity", PAY_CTX,
     {"outcome": "refused", "attempts": {"protect": 1}}, bench_=bench(("p.port.decryption.v1", "p.sim.decryption.v1", "reject")))
test("P-RES-001", "Reservation survives two transient failures with one reservation made", "p.reserve.funds.v1", "activity", PAY_CTX,
     {"outcome": "reserved", "attempts": {"reserve": 3}}, bench_=bench(("p.port.core-banking.v1", "p.sim.core-banking.v1", "fail-twice-then-accept")),
     conformance=True)
test("P-RES-002", "Insufficient funds is a business outcome and is not retried", "p.reserve.funds.v1", "activity", PAY_CTX,
     {"outcome": "insufficient", "attempts": {"reserve": 1}}, bench_=bench(("p.port.core-banking.v1", "p.sim.core-banking.v1", "reject")), conformance=True)
test("BANK-HOOK-001", "A salary file whose total is over the bank's internal limit is held before release", "b.hook.pre-release-approval.v1", "hook",
     salary_file(150000, 150000), {"outcome": "hold"}, tenant="example-bank", base=EXAMPLES)
test("BANK-HOOK-002", "A salary file under the bank's internal limit continues", "b.hook.pre-release-approval.v1", "hook",
     salary_file(60, 40), {"outcome": "continue"}, tenant="example-bank", base=EXAMPLES)
test("BANK-LIM-001", "The bank's lower limit applies from its effective date", "b.validate.internal-limit.v1", "activity",
     salary_file(150000), {"outcome": "reject", "findings": [{"reasonCode": "AM02"}]}, date="2027-01-02", tenant="example-bank", base=EXAMPLES)
test("BANK-LIM-002", "The same file passes the day before the bank's lower limit starts", "b.validate.internal-limit.v1", "activity",
     salary_file(150000), {"outcome": "pass", "findings": []}, date="2026-12-31", tenant="example-bank", base=EXAMPLES)

# ------------------------------------------------------------------- writing
NAME = re.compile(r"^[a-z0-9]+(\.[a-z0-9-]+)+\.v[0-9]+$")


def names_in(doc, own=None):
    """Every artefact name a document mentions, other than its own."""
    if isinstance(doc, dict):
        own = own or doc.get("name")
        return set().union(*[names_in(v, own) for v in doc.values()]) if doc else set()
    if isinstance(doc, list):
        return set().union(*[names_in(v, own) for v in doc]) if doc else set()
    return {doc} if isinstance(doc, str) and NAME.match(doc) and doc != own else set()


def canon_hash(doc):
    return "sha256:" + hashlib.sha256(json.dumps(doc, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


class Dumper(yaml.SafeDumper):
    def increase_indent(self, flow=False, indentless=False):
        return super().increase_indent(flow, False)


def dump(doc, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("# Generated by tools/build_factory.py. Edit the generator, not this file.\n"
                    + yaml.dump(doc, Dumper=Dumper, sort_keys=False, allow_unicode=True, width=110))


def target(base, doc):
    name = doc.get("name") or doc["id"]
    scope = doc.pop("_scope", None) or scope_of(name)
    folder = "bank" if base is EXAMPLES else scope
    return base / folder / FOLDER[doc["kind"]] / (name + ".yaml")


# The shared packs of the factory, in dependency order. tools/validate.py (L34) holds factory/ to this list,
# through the inventory written below: a folder that is not a pack here is not factory content.
SHARED_PACKS = ("p", "uae.p", "uae.wps")
HEADER = "# Generated by tools/build_factory.py. Edit the generator, not this file."


def main():
    written = set()
    for base, doc in OUT:
        t = target(base, doc)
        dump(doc, t)
        written.add(t)
    # a generated file the generator no longer emits is removed, so nothing stale stays in a pack
    for root in (FACTORY, EXAMPLES / "bank"):
        for f in root.rglob("*.yaml"):
            if f not in written and f.read_text().startswith(HEADER):
                f.unlink()
                print("removed stale", f.relative_to(ROOT))
    inventory = {"kind": "inventory", "release": FACTORY_RELEASE, "date": DATE, "packs": []}
    packs = [(FACTORY / s, s, "shared") for s in SHARED_PACKS] + [(EXAMPLES / "bank", "b", "bank")]
    for folder, scope, ptype in packs:
        arts, has_sim = [], False
        for f in sorted(folder.rglob("*.yaml")):
            if f.name == "pack.yaml":
                continue
            doc = yaml.safe_load(f.read_text())
            kind = doc.get("kind")
            if kind == "configuration-set":
                continue
            name = doc.get("name") or doc.get("id") or (doc.get("scope") + ".scheme")
            has_sim = has_sim or kind == "simulator"
            arts.append({"name": name, "kind": kind, "release": doc.get("release", REL), "hash": canon_hash(doc),
                         "path": str(f.relative_to(folder))})
        pack = {"kind": "pack", "scope": scope, "release": PACK_RELEASES[scope], "title": "Factory pack for %s" % scope if ptype == "shared" else "Example bank pack",
                "packType": ptype, "origin": "factory" if ptype == "shared" else "authored"}
        if ptype == "bank":
            pack["tenant"] = "example-bank"
            pack["licensedSchemes"] = LICENSED_SCHEMES["example-bank"]
        deps = {"p": [], "uae.p": ["p"], "uae.wps": ["p", "uae.p"]}.get(scope)
        if scope == "b":
            # A bank pack depends on the scopes its artefacts, its tests among them, refer to, with their parents,
            # as the studio derives it (packManifest; Issue I-43).
            reach = set()
            for f in sorted(folder.rglob("*.yaml")):
                doc = yaml.safe_load(f.read_text())
                if f.name != "pack.yaml" and doc.get("kind") != "configuration-set":
                    reach |= {scope_of(r) for r in names_in(doc)} - {"b", "ck"}
            for s_ in list(reach):
                reach |= {"p"} | ({"%s.p" % s_.split(".")[0]} if s_ not in ("p",) and not s_.endswith(".p") else set())
            deps = [d for d in SHARED_PACKS if d in reach]
        if deps:
            pack["dependsOn"] = [{"scope": d, "release": PACK_RELEASES[d]} for d in deps]
        pack["artefacts"] = arts
        pack["containsSimulators"] = has_sim
        pack["promotable"] = not has_sim
        (folder / "pack.yaml").write_text("# Generated by tools/build_factory.py. The lock file of this pack.\n"
                                          + yaml.dump(pack, Dumper=Dumper, sort_keys=False, width=110))
        counts = {}
        for a in arts:
            counts[a["kind"]] = counts.get(a["kind"], 0) + 1
        inventory["packs"].append({"scope": scope, "packType": ptype, "counts": counts, "total": len(arts),
                                   "artefacts": [{"name": a["name"], "kind": a["kind"]} for a in arts]})
    (FACTORY / "inventory.yaml").write_text("# Generated by tools/build_factory.py.\n"
                                            + yaml.dump(inventory, Dumper=Dumper, sort_keys=False, width=110))
    for p_ in inventory["packs"]:
        print("%-9s %3d artefacts  %s" % (p_["scope"], p_["total"], p_["counts"]))


if __name__ == "__main__":
    main()
