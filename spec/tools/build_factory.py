#!/usr/bin/env python3
"""Generate the repetitive factory artefacts (activities, rules, ports, simulators,
extensions), then the pack manifests and the inventory.

Hand-written artefacts (workflows, scheme manifests, tests, hooks, configuration sets)
live beside the generated ones and are picked up by the manifest step.

Run:  python3 tools/build_factory.py
"""
import hashlib
import json
import pathlib

import yaml

ROOT = pathlib.Path(__file__).resolve().parent.parent
FACTORY = ROOT / "factory"
EXAMPLES = ROOT / "examples"
REL = "1.0.0"
DATE = "2026-11-17"
FOLDER = {"activity": "activities", "rule": "rules", "ruleset": "rules", "table": "rules",
          "port": "ports", "simulator": "simulators", "extension": "extensions", "test": "tests"}
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


def emit(doc, base=FACTORY):
    OUT.append((base, doc))
    return doc


# ---------------------------------------------------------------- activities
BUILTIN = {"validate": "rule-service", "decide": "rule-service"}


def act(name, title, reads, writes, outcomes, effect, *, level=None, mode=None, ports=None,
        rule_sets=None, policy=None, comp=None, params=None, map_=None, service=None,
        lang="typescript", p=None, base=FACTORY, invoked_by=None, sub=None):
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
    svc = service or BUILTIN.get(typ)
    doc["implementation"] = {"binding": "subflow", "subflow": sub} if sub else ({"binding": "builtin", "service": svc} if svc
                             else {"binding": "script", "language": lang, "script": "scripts/%s.%s" % (name, "ts" if lang == "typescript" else "py")})
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
act("p.post.accounting.v1", "Post with an accounting template", ["header", "payment", "facts.route", "facts.reservation"],
    ["facts.posting"], ["posted", "rejected", "unknown"], "write", ports=["p.port.core-banking.v1"], policy=STD,
    comp="p.post.reverse.v1", service="accounting",
    params={"template": {"type": "code", "default": "default", "description": "Accounting template id."}})
act("p.post.verify.v1", "Verify the posting really happened", ["facts.posting"], ["facts.postingVerified"],
    ["verified", "mismatch"], "read", ports=["p.port.core-banking.v1"], service="accounting")
act("p.post.reverse.v1", "Reverse a posting", ["facts.posting"], ["facts.posting"], ["reversed"], "write",
    ports=["p.port.core-banking.v1"], policy=STD, service="accounting")
act("p.notify.channel.v1", "Tell the originating channel", ["header", "status"], ["facts.notified"], ["notified"],
    "write", ports=["p.port.channel.v1"], policy=STD)
act("p.notify.publish-fact.v1", "Publish a fact outward", ["header", "status"], ["facts.published"], ["notified"],
    "write", ports=["p.port.facts-out.v1"], policy=STD)
act("p.hold.repair.v1", "Park for repair", ["header", "facts.validation"], ["facts.repair", "payment", "batch"],
    ["released", "rejected", "expired"], "write", ports=["p.port.operations-queue.v1"], service="operations",
    params={"expiry": {"type": "duration", "default": "P2D", "description": "How long a repair item may wait."}})
act("p.hold.approval.v1", "Park for approval", ["header", "facts.screening"], ["facts.approval"],
    ["approved", "declined", "expired"], "write", ports=["p.port.operations-queue.v1"], service="operations",
    params={"expiry": {"type": "duration", "default": "P1D", "description": "How long an approval may wait."}})
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


def wmap(name, title, direction, model, reads, writes, outcomes=("mapped", "unmappable"), **kw):
    m = {"direction": direction, "sourceModel": model, "roundTrip": direction == "in"}
    m.update(kw)
    return act(name, title, reads, writes, list(outcomes), "pure", map_=m, p=WP)


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
             {"from": "constant SALA", "to": "batch.categoryPurpose"}])
wmap("uae.wps.map.sif-out.v1", "Canonical batch to salary file", "out", "WPS SIF: EDR and SCR records",
     ["batch", "items[]", W], ["messages"])
wmap("uae.wps.map.ack-in.v1", "ACK or NAK to status", "in", "WPS ACK and NAK", ["messages"], ["status"],
     outcomes=("ack", "nak"))
wmap("uae.wps.map.pif-in.v1", "Payment file and details to inward batch", "in", "WPS PIF with PSI",
     ["messages"], ["header", "batch", "items[]", W])
wmap("uae.wps.map.prc-out.v1", "Receipt confirmation", "out", "WPS PRC", ["header", "batch", "status"], ["messages"])
wmap("uae.wps.map.dif-out.v1", "Disbursement file", "out", "WPS DIF", ["header", "batch", "items[]", "status"], ["messages"])
wmap("uae.wps.map.rfr-out.v1", "Refund request", "out", "WPS RFR", ["header", "batch", "items[]"], ["messages"])
wmap("uae.wps.map.rsu-in.v1", "Refund settlement to status", "in", "WPS RSU", ["messages"], ["status", "facts.settlement"],
     outcomes=("settled", "declined"))
wmap("uae.wps.map.refund-order-in.v1", "Employer refund order to canonical request", "in", "Employer refund order",
     ["messages"], ["header", "batch", "items[]"])
wmap("uae.wps.map.rtc-in.v1", "Return file to canonical return", "in", "WPS RTC", ["messages"], ["header", "batch", "items[]", "status"])
act("uae.wps.validate.salary-file.v1", "Salary file checks", ["batch", "items[]", W], ["facts.validation"],
    ["pass", "repair", "reject"], "pure", level="batch", rule_sets=["uae.wps.ruleset.salary-file.v1"], p=WP)
act("uae.wps.validate.salary-record.v1", "Salary record checks", ["items[]"], ["facts.validation"],
    ["pass", "reject"], "pure", level="item", rule_sets=["uae.wps.ruleset.salary-record.v1"], p=WP)
act("uae.wps.validate.payment-file.v1", "Inward payment file checks", ["batch", "items[]", W], ["facts.validation"],
    ["pass", "reject"], "pure", level="batch", rule_sets=["uae.wps.ruleset.payment-file.v1"], p=WP)
for nm, ttl, mode in [("send-salary-file", "Submit the salary file to WPS", "send"),
                      ("send-refund-request", "Submit a refund request to WPS", "send"),
                      ("send-receipt-confirmation", "Send the receipt confirmation", "report"),
                      ("send-disbursement-file", "Send the disbursement file", "report")]:
    act("uae.wps.exchange.%s.v1" % nm, ttl, ["header", "messages"], ["status", "messages"],
        ["accepted", "rejected", "unknown"], "external", mode=mode, ports=["uae.wps.port.wps.v1"], policy=STD, p=WP)

# composite exchanges: one block to the designer, a subflow underneath (design/05, section 5.6)
WPS_PORT = ["uae.wps.port.wps.v1"]
act("uae.wps.exchange.submit-salary-file.v1", "Submit the salary file and get the acknowledgement", ["header", "messages"], ["status", "messages"],
    ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.submit-salary-file.v1", p=WP)
act("uae.wps.exchange.confirm-receipt.v1", "Confirm receipt of the payment file and get the acknowledgement", ["header", "messages"],
    ["status", "messages"], ["accepted", "rejected", "pending"], "external", mode="report", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.confirm-receipt.v1", p=WP)
act("uae.wps.exchange.request-refund.v1", "Request a refund and wait for its settlement", ["header", "messages"],
    ["status", "facts.settlement", "messages"], ["accepted", "rejected", "pending"], "external", mode="send", ports=WPS_PORT,
    policy={"replyTimeout": "P1D", "replyCalendar": "business"}, sub="uae.wps.sub.request-refund.v1", p=WP)
act("uae.wps.exchange.receive-supplement.v1", "Wait for the supplementary file that pairs with the payment file", ["header"], ["messages"],
    ["received", "missing"], "read", mode="receive", ports=WPS_PORT, policy={"replyTimeout": "PT2H"},
    sub="uae.wps.sub.receive-supplement.v1", p=WP)




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
ruleset("uae.wps.ruleset.salary-file.v1", "Salary file checks", "validation",
        ["uae.wps.rule.employer-id-format.v1", "uae.wps.rule.employer-routing-code-format.v1", "uae.wps.rule.currency-aed.v1",
         "uae.wps.rule.record-count-matches.v1", "uae.wps.rule.control-total-matches.v1"], stages=["field", "cross-field"], p=WP)
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
def table(name, title, hit, inputs, outputs, rows, examples, params=None):
    doc = {"kind": "table", "name": name, "release": REL, "title": title, "hitPolicy": hit}
    if params:
        doc["parameters"] = params
    doc.update({"inputs": [{"name": n, "expression": e} for n, e in inputs], "outputs": outputs, "rows": rows,
                "examples": examples, "provenance": prov()})
    return emit(doc)


table("p.table.workflow-select.v1", "Which platform workflow runs a payment", "unique",
      [("kind", "header.kind"), ("direction", "header.direction"), ("segment", "header.segment")], ["workflow"],
      [{"when": {"kind": "request"}, "then": {"workflow": "p.wf.request.v1"}, "reason": "Requests and exceptions move no money through the lifecycle"},
       {"when": {"kind": "payment", "direction": "inward"}, "then": {"workflow": "p.wf.inward.v1"}, "reason": "Every inward payment"},
       {"when": {"kind": "return", "direction": "inward"}, "then": {"workflow": "p.wf.inward.v1"}, "reason": "Every inward return"},
       {"when": {"kind": "payment", "direction": "outward", "segment": "corporate"}, "then": {"workflow": "p.wf.corporate.v1"}, "reason": "File and high-value rails"},
       {"when": {"kind": "payment", "direction": "outward", "segment": "commercial"}, "then": {"workflow": "p.wf.commercial.v1"}, "reason": "Instant rails"}],
      [{"name": "inward payment", "given": {"Header": {"kind": "payment", "direction": "inward", "segment": "commercial"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.inward.v1"}]}},
       {"name": "outward corporate", "given": {"Header": {"kind": "payment", "direction": "outward", "segment": "corporate"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.corporate.v1"}]}},
       {"name": "request", "given": {"Header": {"kind": "request", "direction": "outward", "segment": "corporate"}}, "expect": {"decisions": [{"name": "workflow", "value": "p.wf.request.v1"}]}}])
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
port("p.port.payment-record.v1", "Payment record", "payment record store", [("read", RR), ("find-duplicate", RR)])
port("p.port.operations-queue.v1", "Operations queue", "operations console", [("create-item", SEND), ("decision", RECV)])
port("p.port.liquidity.v1", "Liquidity position", "liquidity service", [("position", RR)])
port("uae.p.port.participant-directory.v1", "Participant directory", "central bank participant directory", [("lookup", RR)])
port("uae.p.port.calendar.v1", "Business calendar", "business calendar service", [("business-date", RR)])
port("uae.wps.port.wps.v1", "Wages protection system", "central bank wages protection system",
     [("submit-file", SEND), ("receive-file", RECV)], p=WP)
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
    outward_set("p.core.request", "p.core.reply", "AM04"))
sim("p.sim.sanctions.v1", "Sanctions engine simulator", "outward", "p.port.sanctions.v1", "sanctions screening engine",
    outward_set("p.sanctions.request", "p.sanctions.reply", "RR04"))
sim("p.sim.fraud.v1", "Fraud engine simulator", "outward", "p.port.fraud.v1", "fraud engine",
    outward_set("p.fraud.request", "p.fraud.reply", "FRAD"))
sim("p.sim.liquidity.v1", "Liquidity service simulator", "outward", "p.port.liquidity.v1", "liquidity service",
    outward_set("p.liquidity.request", "p.liquidity.reply", "AM04"))
sim("p.sim.payment-record.v1", "Payment record simulator", "outward", "p.port.payment-record.v1", "payment record store",
    outward_set("p.record.request", "p.record.reply", "NOOR"))
sim("p.sim.channel.v1", "Channel simulator", "outward", "p.port.channel.v1", "initiating channel",
    outward_set("p.channel.notify", "p.channel.ack", "NARR"))
sim("p.sim.facts-out.v1", "Fact consumer simulator", "outward", "p.port.facts-out.v1", "downstream consumers of payment facts",
    outward_set("p.facts.publish", "p.facts.ack", "NARR"))
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
    [sc("accept", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}),
     sc("reject", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "status": "rejected", "reasonCode": "AM10", "after": "PT10M"}),
     sc("no-reply", {"expect": "uae.wps.sif-submission"}, {"noReply": True}),
     sc("late-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "status": "accepted", "after": "P2D"}),
     sc("duplicate-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M", "duplicate": True}),
     sc("fail-twice-then-accept", {"expect": "uae.wps.sif-submission", "failTimes": 2}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}),
     sc("refund-settled", {"expect": "uae.wps.rfr"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}, {"send": "uae.wps.rsu", "after": "P3D"}),
     sc("refund-declined", {"expect": "uae.wps.rfr"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}, {"send": "uae.wps.rsu", "status": "rejected", "after": "P3D"}),
     sc("receipt-accepted", {"expect": "uae.wps.prc"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}),
     sc("disbursement-accepted", {"expect": "uae.wps.dif"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}),
     sc("malformed-reply", {"expect": "uae.wps.sif-submission"}, {"send": "uae.wps.ack"}, malformed=True)],
    self_check=["uae.wps.ruleset.salary-file.v1"], p=WP)
sim("uae.wps.sim.wps-inward.v1", "WPS simulator, sends to the bank", "inward", "uae.wps.port.wps.v1",
    "central bank wages protection system",
    [sc("payment-file", {"send": "uae.wps.pif"}, {"send": "uae.wps.psi"},
        {"expect": "uae.wps.prc"}, {"send": "uae.wps.ack", "status": "accepted", "after": "PT10M"}, {"expect": "uae.wps.dif"}),
     sc("payment-file-wrong-agent", {"send": "uae.wps.pif"}, {"send": "uae.wps.psi"}),
     sc("return-file", {"send": "uae.wps.rtc"})], self_check=["uae.wps.ruleset.payment-file.v1"], p=WP)
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
     ("leaveDays", "Number", "item", "Days of unpaid leave in the period", {"source": "EDR"})], p=WP)
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
test("WPS-OUT-001", "Salary file acknowledged", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "settled", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 1}],
      "trace": ["business-date:set", "file-checks:pass", "record-checks:pass", "record-iban:pass", "render:mapped", "submit:accepted", "end:settled"],
      "detail": ["submit.send:accepted", "submit.await-ack:received", "submit.read-ack:ack"]},
     bench_=bench((WPS, WSIM, "accept"), CAL))
test("WPS-OUT-002", "Salary file refused by WPS", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "rejected", "reasonCode": "AM10"}, bench_=bench((WPS, WSIM, "reject"), CAL))
test("WPS-OUT-003", "No acknowledgement within the wait ends pending", "uae.wps.wf.salary-file-out.v1", "leg", WPS_FILE,
     {"outcome": "pending", "virtualElapsed": "P1D", "trace": ["business-date:set", "file-checks:pass", "record-checks:pass", "record-iban:pass", "render:mapped", "submit:pending", "end:pending"],
                                                        "detail": ["submit.send:accepted", "submit.await-ack:timeout"]},
     bench_=bench((WPS, WSIM, "no-reply"), CAL))
test("WPS-OUT-004", "A control total that does not match is rejected before anything is sent", "uae.wps.wf.salary-file-out.v1", "leg",
     {"inbound": "samples/wps-sif-bad-control-total.sif"},
     {"outcome": "rejected", "reasonCode": "AM10", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}]},
     bench_=bench((WPS, WSIM, "accept"), CAL))
test("WPS-OUT-005", "A salary record with an invalid IBAN is rejected by the cluster check", "uae.wps.wf.salary-file-out.v1", "leg",
     {"inbound": "samples/wps-sif-bad-iban.sif"},
     {"outcome": "rejected", "reasonCode": "AC03", "messages": [{"port": WPS, "message": "uae.wps.sif-submission", "count": 0}],
      "trace": ["business-date:set", "file-checks:pass", "record-checks:pass", "record-iban:reject", "end:rejected"]},
     bench_=bench((WPS, WSIM, "accept"), CAL))
test("WPS-EXC-001", "The composite alone: sent, acknowledged, read", "uae.wps.exchange.submit-salary-file.v1", "activity", WPS_FILE,
     {"outcome": "accepted", "detail": ["send:accepted", "await-ack:received", "read-ack:ack"]}, bench_=bench((WPS, WSIM, "accept")), conformance=True)
test("WPS-EXC-002", "The composite alone: no acknowledgement within the reply time is pending", "uae.wps.exchange.submit-salary-file.v1", "activity",
     WPS_FILE, {"outcome": "pending", "virtualElapsed": "P1D", "detail": ["send:accepted", "await-ack:timeout"]},
     bench_=bench((WPS, WSIM, "no-reply")), conformance=True)
test("WPS-MAP-001", "Salary file maps in and back out unchanged", "uae.wps.map.sif-in.v1", "activity", WPS_FILE,
     {"outcome": "mapped"}, conformance=True)
test("WPS-MAP-002", "The SCR-last channel variant maps to the same canonical batch", "uae.wps.map.sif-in.v1", "activity",
     {"inbound": "samples/wps-sif-valid-scr-last.sif"}, {"outcome": "mapped"}, conformance=True)
test("WPS-IN-001", "Payment file confirmed, employees credited, disbursement reported", "p.wf.inward.v1", "end-to-end",
     {"inbound": "samples/wps-pif-valid.pif"},
     {"outcome": "settled", "messages": [{"port": WPS, "message": "uae.wps.prc", "count": 1}, {"port": WPS, "message": "uae.wps.dif", "count": 1}]},
     bench_=bench((WPS, "uae.wps.sim.wps-inward.v1", "payment-file"), ("p.port.core-banking.v1", "p.sim.core-banking.v1", "accept"),
                  ("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), ("p.port.fraud.v1", "p.sim.fraud.v1", "accept")), tenant="example-bank", scope="uae.wps")
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
    inventory = {"kind": "inventory", "date": DATE, "packs": []}
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
        pack = {"kind": "pack", "scope": scope, "release": REL, "title": "Factory pack for %s" % scope if ptype == "shared" else "Example bank pack",
                "packType": ptype, "origin": "factory" if ptype == "shared" else "authored"}
        if ptype == "bank":
            pack["tenant"] = "example-bank"
        deps = {"p": [], "uae.p": ["p"], "uae.wps": ["p", "uae.p"], "b": ["p", "uae.p", "uae.wps"]}[scope]
        if deps:
            pack["dependsOn"] = [{"scope": d, "release": REL} for d in deps]
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
