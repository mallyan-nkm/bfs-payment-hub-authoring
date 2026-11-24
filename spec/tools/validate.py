#!/usr/bin/env python3
"""Validate every artefact in factory/ and examples/ against its schema, then run the
static checks that design/05 names. Exit code 1 on any error.

Run:  python3 tools/validate.py
This script is the reference for build spec C04 (static checks). It is not the product.
"""
import hashlib
import datetime
import json
import pathlib
import re
import sys

import yaml
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCHEMA_OF = {"activity": "activity", "rule": "rule", "ruleset": "rule", "table": "rule", "workflow": "workflow",
             "simulator": "simulator", "scheme": "scheme", "hook": "hook", "configuration-set": "configuration-set",
             "pack": "pack", "engine-binding": "engine-binding", "test": "test-case", "extension": "extension",
             "port": "port", "supersession": "supersession", "operations-view": "operations-view",
             "error-dictionary": "error-dictionary", "reference-table": "reference-table"}
errors, warnings = [], []


def err(code, where, msg):
    errors.append("%s  %s: %s" % (code, where, msg))


def warn(code, where, msg):
    warnings.append("%s  %s: %s" % (code, where, msg))


def seconds(d):
    """The length of an ISO 8601 duration such as P1D or PT10M, in seconds."""
    import re
    m = re.fullmatch(r"P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?", str(d))
    if not m or str(d) in ("P", "PT"):
        return None
    dd, hh, mm, ss = (int(x or 0) for x in m.groups())
    return ((dd * 24 + hh) * 60 + mm) * 60 + ss


def scope_of(name):
    parts = name.split(".")
    return parts[0] if parts[0] in ("p", "b") else ".".join(parts[:2])


# ------------------------------------------------------------------ loading
schemas = {}
for f in sorted((ROOT / "schemas").glob("*.schema.json")):
    s = json.loads(f.read_text())
    Draft202012Validator.check_schema(s)
    schemas[s["$id"]] = s
registry = Registry().with_resources([(i, Resource.from_contents(s)) for i, s in schemas.items()])

docs = []  # (path, doc)
for base in ("factory", "examples", "conformance"):
    for f in sorted((ROOT / base).rglob("*.yaml")):
        for d in yaml.safe_load_all(f.read_text()):
            if isinstance(d, dict):
                docs.append((str(f.relative_to(ROOT)), d))

# L00 schema
for path, d in docs:
    kind = d.get("kind")
    if kind in ("inventory", "conformance-kit"):
        continue
    if kind not in SCHEMA_OF:
        err("L00", path, "unknown kind %r" % kind)
        continue
    v = Draft202012Validator(schemas["urn:th:schema:" + SCHEMA_OF[kind]], registry=registry)
    for e in sorted(v.iter_errors(d), key=lambda e: list(e.path)):
        err("L00", path, "%s at /%s" % (e.message[:160], "/".join(str(x) for x in e.path)))

by_name = {d["name"]: d for _, d in docs if "name" in d and d.get("kind") not in ("pack",)}
path_of = {d["name"]: p for p, d in docs if "name" in d}
schemes = {d["scope"]: d for _, d in docs if d.get("kind") == "scheme"}


# ------------------------------------------------------------- step walking
def walk(steps):
    """Yield every step in a step list, depth first."""
    for st in steps or []:
        yield st
        for key in ("handle",):
            for action in (st.get(key) or {}).values():
                if isinstance(action, list):
                    yield from walk(action)
        if "switch" in st:
            for case in st["switch"]["cases"].values():
                yield from walk(case)
            yield from walk(st["switch"].get("default"))
        if "parallel" in st:
            for br in st["parallel"]["branches"]:
                yield from walk(br)
        for key in ("forEach", "loop"):
            if key in st:
                yield from walk(st[key]["do"])
        if "await" in st and isinstance(st["await"]["onTimeout"], list):
            yield from walk(st["await"]["onTimeout"])
        if "try" in st:
            yield from walk(st["try"])
            for c in st.get("catch", []):
                if isinstance(c["do"], list):
                    yield from walk(c["do"])


def all_steps(d):
    yield from walk(d.get("do"))
    yield from walk(d.get("finally"))
    yield from walk((d.get("deadline") or {}).get("do"))


def refs_of(d):
    """Every artefact name a document refers to."""
    out = set()
    kind = d.get("kind")
    for k in ("ports", "ruleSets", "rules", "selfCheck"):
        out.update(d.get(k) or [])
    for k in ("compensation", "port"):
        if isinstance(d.get(k), str) and kind != "port":
            out.add(d[k])
    out.update((d.get("map") or {}).get("edgeRules") or [])
    if (d.get("implementation") or {}).get("subflow"):
        out.add(d["implementation"]["subflow"])
    if kind in ("workflow", "hook"):
        imp = d.get("implements") or {}
        if imp.get("activity"):
            out.add(imp["activity"])
        of = imp.get("of")
        out.update(of if isinstance(of, list) else [of] if of else [])
        for hp in d.get("hookPoints") or []:
            if hp["policy"].get("onHold"):
                out.add(hp["policy"]["onHold"])
        for st in all_steps(d):
            for k in ("call", "use"):
                if k in st:
                    out.add(st[k])
    if kind == "scheme":
        for k in ("legs", "handlers", "ports", "extensions"):
            out.update(d.get(k) or [])
        for m in d["messages"]:
            disp = m["disposition"]
            if "start" in disp:
                out.update(v for k, v in disp["start"].items() if k in ("leg", "refuseLeg", "intakeMap"))
            for k in ("handler", "sentBy"):
                if k in disp:
                    out.add(disp[k])
            # a report's reconcile activity and map, a deliver message's correlation map (R-09-210, R-09-212)
            if "reconcile" in disp:
                out.update([disp["reconcile"]["activity"], disp["reconcile"]["map"]])
            if (disp.get("deliver") or {}).get("correlationMap"):
                out.add(disp["deliver"]["correlationMap"])
    if kind == "reference-table":
        out |= {c["references"] for c in d.get("columns") or [] if c.get("references")}
    if kind == "test":
        out.add(d["target"])
        for b in d.get("bench") or []:
            out.update([b["port"], b["simulator"]])
    return out


ALLOWED = {"p": lambda s, own: s == "p",
           "cluster.p": lambda s, own: s == "p" or s == own,
           "scheme": lambda s, own: s == "p" or s == own or s == own.split(".")[0] + ".p",
           "b": lambda s, own: True}


def tier(scope):
    return scope if scope in ("p", "b") else ("cluster.p" if scope.endswith(".p") else "scheme")


for path, d in docs:
    kind, name = d.get("kind"), d.get("name")
    if kind in ("pack", "inventory", "conformance-kit", "engine-binding", "supersession", "configuration-set"):
        continue
    own = scope_of(name) if name else (d.get("scope") if kind == "scheme" else None)
    for r in sorted(refs_of(d)):
        # L03 references resolve
        if r not in by_name:
            err("L03", path, "reference %s does not resolve" % r)
            continue
        # L02 scope direction (tests may reach across; they are not shipped content)
        if own and kind != "test" and not ALLOWED[tier(own)](scope_of(r), own):
            err("L02", path, "%s scope may not depend on %s" % (own, r))
    # L04 the major in the name equals the major of the release
    if name and ".v" in name and "release" in d:
        if name.rsplit(".v", 1)[1] != d["release"].split(".")[0]:
            err("L04", path, "name says v%s but release is %s" % (name.rsplit(".v", 1)[1], d["release"]))


def waits(a, seen=()):
    """True when calling this activity can park the workflow: a hold, or a composite whose subflow awaits."""
    if a.get("type") == "hold":
        return True
    sub = by_name.get((a.get("implementation") or {}).get("subflow"))
    if not sub or a["name"] in seen:
        return False
    for st in all_steps(sub):
        if "await" in st or "wait" in st:
            return True
        inner = by_name.get(st.get("call"))
        if inner and inner.get("kind") == "activity" and waits(inner, seen + (a["name"],)):
            return True
    return False


# ------------------------------------------------------- workflow checks
def hook_points(d, seen=None):
    pts = {h["name"]: h for h in d.get("hookPoints") or []}
    for st in all_steps(d):
        if "use" in st and st["use"] in by_name:
            pts.update(hook_points(by_name[st["use"]]))
    return pts


for path, d in docs:
    if d.get("kind") not in ("workflow", "hook"):
        continue
    slots = {s["name"]: s["contract"] for s in d.get("slots") or []}
    pts = hook_points(d) if d["kind"] == "workflow" else {}
    for st in all_steps(d):
        # L05 every non-primary outcome is handled, and nothing else is
        if "call" in st and st["call"] in by_name and by_name[st["call"]].get("kind") == "activity":
            a = by_name[st["call"]]
            outs = {o["name"] for o in a["outcomes"]}
            primary = next(o["name"] for o in a["outcomes"] if o.get("primary"))
            handled = set((st.get("handle") or {}).keys())
            for missing in sorted(outs - {primary} - handled):
                err("L05", path, "step %s does not handle outcome %r of %s" % (st.get("id", "?"), missing, st["call"]))
            for extra in sorted(handled - outs):
                err("L05", path, "step %s handles %r, which %s never returns" % (st.get("id", "?"), extra, st["call"]))
        if "slot" in st:
            if st["slot"] not in slots:
                err("L08", path, "slot %r is not declared" % st["slot"])
            else:
                c = slots[st["slot"]]
                need = set(c["outcomes"]) - {c.get("primaryOutcome")}
                for missing in sorted(need - set((st.get("handle") or {}).keys())):
                    err("L05", path, "slot step %s does not handle leg outcome %r" % (st.get("id", "?"), missing))
        # L06 hook steps name declared hook points
        if "hook" in st and d["kind"] == "workflow" and st["hook"] not in pts:
            err("L06", path, "hook point %r is not declared here or in a segment used here" % st["hook"])
    if d["kind"] == "workflow" and d["wfKind"] == "handler":
        # L07 a handler has no await, timer, hold or write
        for st in all_steps(d):
            if not ({"call", "switch", "end"} & set(st)):
                err("L07", path, "a handler may only call, switch or end; found %s" % sorted(st))
            if "call" in st and st["call"] in by_name:
                a = by_name[st["call"]]
                ok = a["effect"] in ("pure", "read") or (a["type"] == "exchange" and a.get("mode") == "respond")
                if not ok or a["type"] == "hold" or waits(a):
                    err("L07", path, "handler calls %s, which waits or writes" % st["call"])
    if d["kind"] == "workflow":
        # L45 a step that parks (R-05-55) is a call outside a handler, to an activity that may run again (pure, read or with an
        # idempotency key), and parks for at least a minute and at most thirty days
        for st in all_steps(d):
            park = (st.get("policy") or {}).get("park")
            if park is None:
                continue
            sid = st.get("id", "?")
            if d["wfKind"] == "handler":
                err("L45", path, "step %s of a handler parks; a handler holds no state" % sid)
            if "call" not in st:
                err("L45", path, "step %s parks but calls no activity" % sid)
            elif st["call"] in by_name:
                a = by_name[st["call"]]
                if a.get("effect") not in ("pure", "read") and not a.get("idempotencyKey"):
                    err("L45", path, "step %s parks on %s, which may not run again: it is %s with no idempotency key" % (sid, st["call"], a.get("effect")))
            s = seconds(park)
            if s is not None and not (60 <= s <= 30 * 86400):
                err("L45", path, "step %s parks for %s: a park is at least PT1M and at most P30D" % (sid, park))
    if d["kind"] == "workflow" and d["wfKind"] == "leg":
        # L08 a leg fills a declared slot and returns only what the slot allows
        for of in d["implements"]["of"]:
            host = by_name.get(of)
            contract = {s["name"]: s["contract"] for s in (host or {}).get("slots") or []}.get(d["implements"]["slot"])
            if not contract:
                err("L08", path, "%s has no slot %r" % (of, d["implements"]["slot"]))
            else:
                for o in sorted(set(d["exit"]["outcomes"]) - set(contract["outcomes"])):
                    err("L08", path, "leg outcome %r is not in the slot contract of %s" % (o, of))
    # L16 awaited messages are in the scheme catalogue with a deliver disposition
    if d["kind"] == "workflow":
        sch = schemes.get(scope_of(d["name"]))
        for st in all_steps(d):
            if "await" in st:
                msg = st["await"]["message"]
                hit = [m for m in (sch or {}).get("messages", []) if m["id"] == msg]
                if not hit or "deliver" not in hit[0]["disposition"]:
                    err("L16", path, "awaited message %s is not a deliver message in the scheme catalogue" % msg)
                elif "family" in st["await"] and st["await"]["family"] not in (hit[0]["disposition"]["deliver"].get("families") or []):
                    err("L36", path, "await %s takes family %r of %s, which its dispatch entry does not declare" % (
                        st.get("id", "?"), st["await"]["family"], msg))
        # L46 (R-12-17) on a scheme whose wire is a file, a step that sends or reports a file (an exchange of mode send or
        # report that is not a composite) comes after a protect step that encrypts it (Issue I-117)
        if (sch or {}).get("wire") == "file":
            protected = False
            for st in all_steps(d):
                a = by_name.get(st.get("call")) if "call" in st else None
                if not a or a.get("kind") != "activity":
                    continue
                if a.get("type") == "protect":
                    protected = True
                elif a.get("type") == "exchange" and a.get("mode") in ("send", "report") and (a.get("implementation") or {}).get("binding") != "subflow" and not protected:
                    err("L46", path, "step %s sends %s on a file scheme before any protect step encrypts it" % (st.get("id", "?"), a["name"]))

# ------------------------------------------- composites and subflows
def composite(a):
    return bool(a) and a.get("kind") == "activity" and (a.get("implementation") or {}).get("binding") == "subflow"


def actions(d):
    """Every single-action object in a workflow: handle entries and await timeouts that are not step lists."""
    for st in all_steps(d):
        for a in (st.get("handle") or {}).values():
            if isinstance(a, dict):
                yield a
        if "await" in st and isinstance(st["await"]["onTimeout"], dict):
            yield st["await"]["onTimeout"]


def returns(d):
    return {st["return"] for st in all_steps(d) if "return" in st} | {a["return"] for a in actions(d) if "return" in a}


for path, d in docs:
    if composite(d):
        # L30 a composite and its subflow agree
        sub = by_name.get(d["implementation"]["subflow"])
        if not sub or sub.get("wfKind") != "subflow":
            err("L30", path, "%s is not a subflow" % d["implementation"]["subflow"])
        elif (sub.get("implements") or {}).get("activity") != d["name"]:
            err("L30", path, "%s does not implement %s" % (sub["name"], d["name"]))
        else:
            outs = {o["name"] for o in d["outcomes"]}
            for o in sorted(returns(sub) - outs):
                err("L30", path, "its subflow returns %r, which the activity does not declare" % o)
            for o in sorted(outs - returns(sub)):
                err("L30", path, "declares outcome %r, which its subflow never returns" % o)
        for k in sorted(set(d.get("policy") or {}) - {"replyTimeout", "replyCalendar", "replyTimeoutParameter", "replyCalendarParameter"}):
            err("L30", path, "a composite carries no %s; attempts and retries belong to the steps of its subflow" % k)
        # L30 (R-05-47) a reply wait a bank may set is a parameter the composite declares, of the right type, with the policy as its default
        pol = d.get("policy") or {}
        for key, typ, dflt in (("replyTimeoutParameter", "duration", "replyTimeout"), ("replyCalendarParameter", "code", "replyCalendar")):
            if key not in pol:
                continue
            decl = (d.get("parameters") or {}).get(pol[key])
            if not decl or decl.get("type") != typ:
                err("L30", path, "%s names %r, which is not a %s parameter of this activity" % (key, pol[key], typ))
            elif dflt not in pol or decl.get("default") != pol[dflt]:
                err("L30", path, "%s %r defaults to %r, not the policy's %s %r" % (key, pol[key], decl.get("default"), dflt, pol.get(dflt)))
    if d.get("kind") != "workflow":
        continue
    sub = d["wfKind"] == "subflow"
    if sub:
        act = by_name.get(d["implements"]["activity"])
        if not composite(act) or act["implementation"]["subflow"] != d["name"]:
            err("L30", path, "%s is not a composite activity bound to this subflow" % d["implements"]["activity"])
        for st in all_steps(d):
            for k in sorted({"end", "slot", "hook", "use"} & set(st)):
                err("L30", path, "a subflow may not contain %s; it returns an outcome" % k)
        for a in actions(d):
            if "end" in a:
                err("L30", path, "a subflow may not end the workflow; it returns an outcome")
    for st in all_steps(d):
        # L30 a step that calls a composite has an id, so its inner steps can be named
        if "call" in st and composite(by_name.get(st["call"])) and "id" not in st:
            err("L30", path, "a step that calls the composite %s needs an id" % st["call"])
        # L31 the designer's workflow shows no wait for a message; the expanded form is never stored as authored
        if "await" in st and not sub:
            err("L31", path, "step %s awaits %s; an await belongs in the subflow of a composite activity" % (st.get("id", "?"), st["await"]["message"]))
        if "block" in st:
            err("L31", path, "a block appears only in the expanded form")
        if "return" in st and not sub:
            err("L31", path, "return appears only in a subflow")
        # L32 an await with no timeout takes the reply time of the calling step, so the composite must declare one
        if "await" in st and "timeout" not in st["await"] and sub:
            act = by_name.get(d["implements"]["activity"]) or {}
            if "replyTimeout" not in (act.get("policy") or {}):
                err("L32", path, "await %s has no timeout and %s declares no replyTimeout" % (st.get("id", "?"), act.get("name")))
        # L17 a wait or an await timeout that names a parameter names one of the subflow's composite
        for holder, key in (((st, "wait"),) if "wait" in st else ()) + (((st["await"], "timeout"),) if "await" in st else ()):
            ref = holder.get(key)
            if not isinstance(ref, dict):
                continue
            act = by_name.get((d.get("implements") or {}).get("activity")) or {}
            decl = (act.get("parameters") or {}).get(ref["parameter"]) if sub else None
            if not sub:
                err("L17", path, "step %s names parameter %r; only a subflow may, from its composite" % (st.get("id", "?"), ref["parameter"]))
            elif decl is None:
                err("L17", path, "step %s names parameter %r, which %s does not declare" % (st.get("id", "?"), ref["parameter"], act.get("name")))
            elif decl.get("type") != "duration":
                err("L17", path, "step %s names parameter %r, which is not a duration" % (st.get("id", "?"), ref["parameter"]))
    if not sub:
        for a in actions(d):
            if "return" in a:
                err("L31", path, "return appears only in a subflow")

# ------------------------------------------------------------ hook checks
for path, d in docs:
    if d.get("kind") != "hook":
        continue
    host = by_name.get(d["implements"]["of"])
    pt = (hook_points(host) if host else {}).get(d["implements"]["hookPoint"])
    if not pt:
        err("L13", path, "hook point %s is not declared by %s" % (d["implements"]["hookPoint"], d["implements"]["of"]))
        continue
    if pt["type"] != d["type"]:
        err("L13", path, "hook is %s but the hook point is %s" % (d["type"], pt["type"]))
    allowed = pt["contract"].get("writes", []) if d["type"] == "transform" else []
    for st in all_steps(d):
        a = by_name.get(st.get("call", ""))
        if not a or a.get("kind") != "activity":
            continue
        for w in a["writes"]:
            inside = any(w == x or w.startswith(x + ".") for x in allowed)
            if not inside and not w.startswith("facts."):
                err("L13", path, "%s writes %s, outside the hook point contract" % (a["name"], w))


def select_workflow(start):
    """The platform workflow workflow-select picks for a start entry, or None (no row, or several for a unique table)."""
    table = by_name.get("p.table.workflow-select.v1")
    if not table:
        return None
    facts = {"kind": start.get("kind"), "direction": start.get("direction"), "segment": start.get("segment")}
    inputs = {i["name"]: i.get("expression", "") for i in table["inputs"]}
    hits = []
    for row in table["rows"]:
        ok = True
        for name, want in (row.get("when") or {}).items():
            expr = inputs.get(name, "")
            got = facts.get(expr[len("header."):]) if expr.startswith("header.") else None
            if got != want:
                ok = False
                break
        if ok:
            hits.append(row["then"]["workflow"])
    if table.get("hitPolicy") == "first":
        return hits[0] if hits else None
    return hits[0] if len(hits) == 1 else None

# ---------------------------------------------------------- scheme checks
for path, d in docs:
    if d.get("kind") != "scheme":
        continue
    planned = [m["id"] for m in d["messages"] if "planned" in m["disposition"]]
    if planned:
        warn("L10", path, "not publishable: %d planned message(s): %s" % (len(planned), ", ".join(planned)))
    # L10 a deliver message is awaited by at least one workflow of the scheme
    awaited = {st["await"]["message"] for _, w in docs if w.get("kind") == "workflow" and scope_of(w["name"]) == d["scope"]
               for st in all_steps(w) if "await" in st}
    for m in d["messages"]:
        if "deliver" in m["disposition"] and m["id"] not in awaited:
            err("L10", path, "%s is a deliver message but no workflow of %s awaits it" % (m["id"], d["scope"]))
    # L55 (R-09-209 to R-09-212; Issues I-159, I-164, I-235, I-267) what a message declares beside its disposition holds: a file
    # block on a file or mixed wire, with a name pattern that compiles; a reply that answers messages of its own scheme that it
    # sends out; a report's reconcile activity and map; a deliver message's correlation map, an inward map that reads the message
    ids = {m["id"]: m for m in d["messages"]}
    for m in d["messages"]:
        mid = m["id"]
        f = m.get("file")
        if f is not None:
            if d.get("wire") not in ("file", "mixed"):
                err("L55", path, "%s declares a file block, but %s's wire is %s, not a file" % (mid, d["scope"], d.get("wire", "unstated")))
            if "namePattern" in f:
                try:
                    re.compile(f["namePattern"])
                except re.error as e:
                    err("L55", path, "%s's file name pattern does not compile: %s" % (mid, e))
        for r in m.get("repliesTo") or []:
            if m["direction"] != "in":
                err("L55", path, "%s answers %s but is not a message the bank receives" % (mid, r))
            if r not in ids:
                err("L55", path, "%s answers %s, which is no message of %s" % (mid, r, d["scope"]))
            elif ids[r]["direction"] != "out":
                err("L55", path, "%s answers %s, which the bank does not send" % (mid, r))
        rec = m["disposition"].get("reconcile")
        if rec:
            if m["direction"] != "in":
                err("L55", path, "%s is reconciled, but the bank does not receive it" % mid)
            act = by_name.get(rec["activity"])
            if not act or act.get("kind") != "activity" or act.get("type") != "reconcile":
                err("L55", path, "%s's reconcile activity %s is not a reconcile activity" % (mid, rec["activity"]))
            mp = by_name.get(rec["map"])
            if not mp or mp.get("type") != "map" or (mp.get("map") or {}).get("direction") != "in":
                err("L55", path, "%s's report map %s is not an inward map" % (mid, rec["map"]))
        cm = (m["disposition"].get("deliver") or {}).get("correlationMap")
        if cm:
            mp = by_name.get(cm)
            if not mp or mp.get("type") != "map" or (mp.get("map") or {}).get("direction") != "in" or "messages" not in (mp.get("reads") or []):
                err("L55", path, "%s's correlation map %s is not an inward map that reads the message" % (mid, cm))
    # L33 a start message routes: its kind, direction and segment select, through workflow-select,
    # one platform workflow, and the leg it names fills that workflow's slot (R-05-42)
    for m in d["messages"]:
        st = m["disposition"].get("start")
        if not st:
            continue
        chosen = select_workflow(st)
        if chosen is None:
            err("L33", path, "%s starts kind %s, direction %s, segment %s, which workflow-select maps to no single platform workflow" % (
                m["id"], st.get("kind", "?"), st.get("direction", "?"), st.get("segment", "none")))
            continue
        for leg_name in [st["leg"]] + ([st["refuseLeg"]] if st.get("refuseLeg") else []):
            leg = by_name.get(leg_name)
            if leg and chosen not in (leg.get("implements") or {}).get("of", []):
                err("L33", path, "%s routes to %s, but its leg %s fills a slot of %s" % (
                    m["id"], chosen, leg_name, ", ".join((leg.get("implements") or {}).get("of", [])) or "nothing"))

# ------------------------------------------------- rules and parameters
for path, d in docs:
    if d.get("kind") == "rule":
        declared = set((d.get("parameters") or {}).keys())
        for c in d["when"]:
            m = c.get("match") or {}
            if m.get("fact") == "Param" and m.get("as") not in declared:
                err("L17", path, "uses parameter %r but does not declare it" % m.get("as"))
    if d.get("kind") == "ruleset" and d["mode"] == "validation":
        for r in d["rules"]:
            for a in (by_name.get(r) or {}).get("then", []):
                if "finding" not in a.get("yield", {}):
                    err("L12", path, "validation set contains %s, which yields something other than a finding" % r)

# -------------------------------------------------- extensions registered
roots = [d["root"] for _, d in docs if d.get("kind") == "extension"]
for path, d in docs:
    if d.get("kind") != "activity":
        continue
    for w in d["reads"] + d["writes"]:
        if w.startswith("ext.") and not any(w == r or w.startswith(r + ".") for r in roots):
            err("L18", path, "%s is not under a registered extension root" % w)

# --------------------------------------------------- configuration sets
def within_bounds(decl, value):
    """R-04-08, R-06-21: min and max for numbers and durations; allowed and pattern for codes and strings; each item of a list."""
    b = decl.get("bounds") or {}
    if decl.get("type") == "list":
        if not isinstance(value, list):
            return False
        return all(within_bounds({"type": "item", "bounds": b}, x) for x in value)
    if isinstance(value, dict):
        return all(within_bounds({"type": "item", "bounds": b}, x) for x in value.values())
    if decl.get("type") == "duration":
        v = seconds(value)
        if v is None:
            return False
        if "min" in b and v < seconds(b["min"]) or "max" in b and v > seconds(b["max"]):
            return False
    elif isinstance(value, (int, float)) and not isinstance(value, bool):
        if "max" in b and value > b["max"] or "min" in b and value < b["min"]:
            return False
    if "allowed" in b and value not in b["allowed"]:
        return False
    if "pattern" in b and isinstance(value, str) and not re.search(b["pattern"], value):
        return False
    return True


REF_TYPES = {"code": str, "text": str, "date": str, "boolean": bool, "number": (int, float), "list": list}


def reference_table_problems(d, tables):
    """L49 (R-01-25): a reference table's key and columns agree, its editions are in date order, and every row fits."""
    out = []
    cols = {c["id"]: c for c in d.get("columns") or []}
    if len(cols) != len(d.get("columns") or []):
        out.append("names a column twice")
    for k in d.get("key") or []:
        if k not in cols:
            out.append("its key names %s, which is not a column" % k)
    for c in cols.values():
        r = c.get("references")
        if r and (r not in tables or r == d.get("name")):
            out.append("column %s references %s, which is no other reference table" % (c["id"], r))
    dates = [str(e.get("validFrom")) for e in d.get("editions") or []]
    if dates != sorted(dates) or len(set(dates)) != len(dates):
        out.append("its editions are not in strictly increasing validFrom order")
    for e in d.get("editions") or []:
        seen = set()
        for i, row in enumerate(e.get("rows") or []):
            at = "edition %s, row %d" % (e.get("validFrom"), i + 1)
            for k in row:
                if k not in cols:
                    out.append("%s has %s, which is not a column" % (at, k))
            for c in cols.values():
                v = row.get(c["id"])
                if v is None or v == "":
                    if c.get("required") or c["id"] in (d.get("key") or []):
                        out.append("%s has no %s" % (at, c["id"]))
                    continue
                if not isinstance(v, REF_TYPES[c["type"]]) or (c["type"] == "number" and isinstance(v, bool)):
                    out.append("%s: %s is not a %s" % (at, c["id"], c["type"]))
                elif c.get("pattern") and isinstance(v, str) and not re.search(c["pattern"], v):
                    out.append("%s: %s %r does not match %s" % (at, c["id"], v, c["pattern"]))
                elif c.get("allowed") and v not in c["allowed"]:
                    out.append("%s: %s %r is not one of %s" % (at, c["id"], v, c["allowed"]))
                elif c.get("references") in tables:
                    ref = tables[c["references"]]
                    keys = {str(r.get((ref.get("key") or [""])[0])) for ed in ref.get("editions") or [] for r in ed.get("rows") or []}
                    if str(v) not in keys:
                        out.append("%s: %s %r is no key of %s" % (at, c["id"], v, c["references"]))
            key = tuple(str(row.get(k)) for k in d.get("key") or [])
            if key in seen:
                out.append("%s repeats the key %s" % (at, "/".join(key)))
            seen.add(key)
    return out


def reference_value_problems(v, decl):
    """L50 (R-11-51): a value read from a reference table names a published one and its columns, for a list parameter."""
    out = []
    fr = v["fromReference"]
    t = by_name.get(fr.get("table"))
    if not t or t.get("kind") != "reference-table":
        return ["%s reads %s, which is no reference table" % (v["parameter"], fr.get("table"))]
    if decl.get("type") != "list":
        out.append("%s reads a reference table, but it is a %s, not a list" % (v["parameter"], decl.get("type")))
    cols = {c["id"] for c in t.get("columns") or []}
    for field, col in (fr.get("columns") or {}).items():
        if col not in cols:
            out.append("%s reads column %s, which %s does not have" % (v["parameter"], col, t["name"]))
    return out


for path, d in docs:
    if d.get("kind") != "configuration-set":
        continue
    for v in d["values"]:
        art, par = v["parameter"].split("#")
        p = ((by_name.get(art) or {}).get("parameters") or {}).get(par)
        if p is None:
            err("L14", path, "%s is not a declared parameter" % v["parameter"])
            continue
        b = p.get("bounds") or {}
        if "fromReference" in v:
            for m in reference_value_problems(v, p):
                err("L50", path, m)
            continue
        if not within_bounds(p, v["value"]):
            err("L14", path, "%s = %r is outside its bounds %r" % (v["parameter"], v["value"], b))
    for b in d["bindings"]:
        wf, hp = b["hookPoint"].split("#")
        hook = by_name.get(b["hook"])
        if wf not in by_name or hp not in hook_points(by_name[wf]):
            err("L14", path, "binding names an unknown hook point %s" % b["hookPoint"])
        elif not hook or hook["implements"] != {"hookPoint": hp, "of": wf}:
            err("L14", path, "%s does not implement %s" % (b["hook"], b["hookPoint"]))
        elif hook["release"] != b["release"]:
            err("L14", path, "binding pins %s but the hook is at %s" % (b["release"], hook["release"]))

# ------------------------------------------------------- test benches
for path, d in docs:
    if d.get("kind") != "test":
        continue
    for b in d.get("bench") or []:
        s = by_name.get(b["simulator"])
        if s and s.get("kind") == "simulator":
            if s["port"] != b["port"]:
                err("L15", path, "%s does not implement %s" % (b["simulator"], b["port"]))
            if b["scenario"] not in {x["name"] for x in s["scenarios"]}:
                err("L15", path, "%s has no scenario %r" % (b["simulator"], b["scenario"]))

# L22, the static half: every port has at least one simulator. That each port a test can
# reach is bound in its bench needs the data-flow walk and is left to build spec C04.
simulated = {d["port"] for _, d in docs if d.get("kind") == "simulator"}
for path, d in docs:
    if d.get("kind") == "port" and d["name"] not in simulated:
        err("L22", path, "%s has no simulator" % d["name"])

# -------------------------------------------------------- bank neutral
tenants = {d.get("tenant") for _, d in docs if d.get("tenant")} | {l.strip().lower() for l in (ROOT / "tools" / "deny-names.txt").read_text().splitlines() if l.strip() and not l.startswith("#")}
for path, d in docs:
    name = d.get("name") or ""
    if d.get("kind") in ("activity", "rule", "ruleset", "table", "workflow", "simulator", "port", "extension", "reference-table") and scope_of(name) != "b":
        text = json.dumps({k: d.get(k) for k in ("name", "title", "description", "party", "standsFor")}).lower()
        for t in sorted(tenants):
            if t and t in text:
                err("L09", path, "shared content names a bank or vendor: %r" % t)

# ------------------------------------------------------ unused blocks
def closure(name, seen=None):
    """Every artefact reachable from one artefact."""
    seen = set() if seen is None else seen
    for r in refs_of(by_name.get(name) or {}):
        if r not in seen:
            seen.add(r)
            closure(r, seen)
    return seen


reached = set()
for _, d in docs:
    if d.get("kind") in ("workflow", "hook", "scheme"):
        reached |= refs_of(d)
    if d.get("kind") == "activity" and d.get("invokedBy", "workflow") != "workflow":
        reached |= refs_of(d)
for n in sorted(reached):
    reached |= closure(n)
for path, d in docs:
    # L11 a block that nothing reaches says what runs it
    if d.get("kind") in ("activity", "ruleset", "rule", "table") and d["name"] not in reached \
            and d.get("invokedBy", "workflow") == "workflow":
        warn("L11", path, "%s is reached by no workflow, hook or scheme and declares no invokedBy" % d["name"])
    if d.get("kind") == "activity" and d.get("invokedBy", "workflow") != "workflow" and d["name"] in reached:
        err("L11", path, "%s declares invokedBy %s but a workflow calls it" % (d["name"], d["invokedBy"]))

# ------------------------------------------------------- supersessions
def ver(r):
    return tuple(int(x) for x in r.split("."))


for path, d in docs:
    if d.get("kind") != "supersession":
        continue
    art = by_name.get(d["artefact"])
    if art is None:
        err("L27", path, "%s does not resolve" % d["artefact"])
        continue
    old, new = ver(d["replaces"]), ver(d["with"])
    if new[0] != old[0] or new <= old:
        err("L27", path, "%s to %s is not a forward move within one major line" % (d["replaces"], d["with"]))
    if art["release"] != d["replaces"]:
        err("L27", path, "replaces %s but %s is at %s" % (d["replaces"], d["artefact"], art["release"]))
    in_scope = set()
    for wr in d["scope"].get("workflowReleases") or []:
        wf, _, rel = wr.partition("@")
        w = by_name.get(wf)
        in_scope.add((wf, rel))
        # A workflow release in flight is the latest or one the content branch published: a later release does not end it.
        published = w is not None and (ROOT.parent / "content-configurations" / "artefacts" / scope_of(wf) / wf / ("%s.yaml" % rel)).is_file()
        if w is None or w.get("kind") != "workflow" or (w["release"] != rel and not published):
            err("L27", path, "%s is not a workflow release in this repository" % wr)
        elif d["artefact"] not in closure(wf):
            err("L27", path, "%s does not use %s, so there is nothing to supersede" % (wr, d["artefact"]))
    for t in d["evidence"]["testRuns"]:
        if (t["workflow"], t["release"]) not in in_scope:
            err("L27", path, "evidence names %s@%s, which is outside the scope" % (t["workflow"], t["release"]))
    if d["approval"]["maker"] == d["approval"]["checker"]:
        err("L27", path, "maker and checker are the same person")

# ------------------------------------------------------- native code-behinds
# L40 an activity that says the runtime's native host implements it as well (R-08-16) keeps a script-behind or a builtin for
# every other run: `native: true` goes with binding script or builtin only.
for path, d in docs:
    impl = d.get("implementation") or {}
    if d.get("kind") == "activity" and impl.get("native") and impl.get("binding") not in ("script", "builtin"):
        err("L40", path, "%s says the native host implements it too, but its binding is %s; native: true needs a script or a builtin"
            % (d["name"], impl.get("binding")))

# ------------------------------------------------------- factory packs
# L34 the factory holds only the packs its inventory lists (R-12-11): every folder under factory/
# is a listed shared pack with a manifest, every listed shared pack has a folder, and every
# artefact in a pack's folder is in that pack's scope.
inventory = next((d for _, d in docs if d.get("kind") == "inventory"), None)
listed = {p["scope"] for p in (inventory or {}).get("packs", []) if p.get("packType") == "shared"}
if inventory is None:
    err("L34", "factory/inventory.yaml", "the factory has no inventory")
folders = {f.name for f in (ROOT / "factory").iterdir() if f.is_dir()}
for s in sorted(folders - listed):
    err("L34", "factory/%s/" % s, "%s is not a pack the factory inventory lists" % s)
for s in sorted(listed - folders):
    err("L34", "factory/inventory.yaml", "lists pack %s, which has no folder under factory/" % s)
for path, d in docs:
    parts = pathlib.PurePath(path).parts
    if parts[0] != "factory" or len(parts) < 3:
        continue
    folder, kind = parts[1], d.get("kind")
    if kind == "pack" or kind == "scheme":
        own = d.get("scope")
    elif kind == "test":
        own = None
    else:
        own = scope_of(d["name"]) if d.get("name") else None
    if folder in listed and own and own != folder:
        err("L34", path, "%s content is in the folder of pack %s" % (own, folder))

# ------------------------------------------------- bank packs on the branch
# L35 a bank pack may live in content-configurations/banks/<bank>/ (R-11-28, Issue I-43). Its pack.yaml is a bank
# pack that names its tenant; every studio artefact in the folder is valid, is that tenant's, is in scope b or is
# a test, and refers only to what the factory or the folder holds; the runtime's documents (port bindings and
# configuration values) are not pack artefacts; and the lock file lists every artefact with its release, hash and
# path, and nothing else. The studio's own folders there (artefacts/, configuration-sets/, scripts/) hold what it
# publishes and are checked by the studio.
BANKS = ROOT.parent / "content-configurations" / "banks"
RUNTIME_KINDS = ("port-bindings", "configuration-values")
# packs/ keeps a bank pack's earlier lock files, as published (R-11-35); L41 holds them to their hashes.
STUDIO_FOLDERS = ("artefacts", "configuration-sets", "port-bindings", "scripts", "packs")
factory_names = set(by_name)
for bank in sorted(f for f in (BANKS.iterdir() if BANKS.is_dir() else []) if f.is_dir()):
    where = "content-configurations/banks/%s" % bank.name
    pf = bank / "pack.yaml"
    if not pf.is_file():
        err("L35", where + "/", "a bank folder has no pack.yaml")
        continue
    pack = yaml.safe_load(pf.read_text()) or {}
    for e in Draft202012Validator(schemas["urn:th:schema:pack"], registry=registry).iter_errors(pack):
        err("L35", where + "/pack.yaml", "%s at /%s" % (e.message[:160], "/".join(str(x) for x in e.path)))
    if pack.get("kind") != "pack" or pack.get("packType") != "bank" or pack.get("scope") != "b" or not pack.get("tenant"):
        err("L35", where + "/pack.yaml", "is not a bank pack: it needs kind pack, packType bank, scope b and a tenant")
        continue
    tenant = pack["tenant"]
    # R-11-37 (Issue I-111): a bank pack names the schemes the bank licenses, each a scheme of the factory.
    lic = pack.get("licensedSchemes")
    if not isinstance(lic, list) or not lic:
        err("L35", where + "/pack.yaml", "names no licensedSchemes: a bank pack lists the schemes the bank licenses (R-11-37)")
    for s_ in lic if isinstance(lic, list) else []:
        if s_ not in schemes:
            err("L35", where + "/pack.yaml", "licenses %s, which is no scheme of the factory" % s_)
    found, own = {}, {}
    for f in sorted(bank.rglob("*.yaml")):
        rel = f.relative_to(bank)
        if f == pf or rel.parts[0] in STUDIO_FOLDERS:
            continue
        for d in yaml.safe_load_all(f.read_text()):
            if not isinstance(d, dict) or d.get("kind") in RUNTIME_KINDS:
                continue
            at = "%s/%s" % (where, rel)
            kind = d.get("kind")
            if kind not in SCHEMA_OF:
                err("L35", at, "unknown kind %r" % kind)
                continue
            for e in sorted(Draft202012Validator(schemas["urn:th:schema:" + SCHEMA_OF[kind]], registry=registry).iter_errors(d), key=lambda e: list(e.path)):
                err("L35", at, "%s at /%s" % (e.message[:160], "/".join(str(x) for x in e.path)))
            if d.get("tenant") and d["tenant"] != tenant:
                err("L35", at, "names tenant %s, but its pack is %s's" % (d["tenant"], tenant))
            if d.get("name") and scope_of(d["name"]) != "b":
                err("L35", at, "%s is not in scope b; a bank pack holds the bank's own artefacts" % d["name"])
            if kind == "configuration-set":
                continue
            name = d.get("name") or d.get("id")
            found[name] = {"name": name, "kind": kind, "release": d.get("release", "1.0.0"),
                           "hash": "sha256:" + __import__("hashlib").sha256(json.dumps(d, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest(),
                           "path": str(rel)}
            if d.get("name"):
                own[d["name"]] = d
    for name, d in sorted(own.items()):
        for r in sorted(refs_of(d)):
            if r not in factory_names and r not in own and scope_of(r) != "ck":
                err("L35", "%s/%s" % (where, found[name]["path"]), "%s refers to %s, which neither the factory nor the folder holds" % (name, r))
    locked = {a.get("name"): a for a in pack.get("artefacts") or []}
    for name in sorted(set(found) - set(locked)):
        err("L35", where + "/pack.yaml", "the lock file does not list %s (%s)" % (name, found[name]["path"]))
    for name in sorted(set(locked) - set(found)):
        err("L35", where + "/pack.yaml", "the lock file lists %s, which the folder does not hold" % name)
    for name in sorted(set(found) & set(locked)):
        for k in ("kind", "release", "hash", "path"):
            if locked[name].get(k) != found[name][k]:
                err("L35", where + "/pack.yaml", "%s: the lock file's %s is %s, the folder's is %s" % (name, k, locked[name].get(k), found[name][k]))
    if bool(pack.get("containsSimulators")) != any(a["kind"] == "simulator" for a in found.values()) or pack.get("promotable") == pack.get("containsSimulators"):
        err("L35", where + "/pack.yaml", "containsSimulators and promotable do not match the folder")

# ------------------------------------------------- no literal secret
# L37 no binding, configuration or package carries a literal secret (R-06-23). A key that names a secret (password,
# passphrase, secret, token, apiKey, accessKey, privateKey, clientSecret, credential, or a camel-case name ending in one)
# holds a reference (env:, file:, vault:, akv:, aws-sm:, aws-ssm:; R-06-22) or nothing; a secretRef holds a reference; no text is a private key.
# It reads the spec's content and everything under content-configurations/. The studio check is the same rule.
SECRET_KEY = re.compile(r"(^|[a-z0-9_])(password|passwd|passphrase|secret|token|apikey|api_key|accesskey|access_key|privatekey|private_key|clientsecret|client_secret|credentials?)$", re.I)
SECRET_REF = re.compile(r"^(env|file|vault|akv|aws-sm|aws-ssm):\S+$")
REF_FORMS = "env:NAME, file:/path, vault:mount/path#field, akv:vault/secret[#version], aws-sm:region/secret-id[#field] or aws-ssm:region/parameter"
PRIVATE_KEY_PEM = re.compile(r"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----")


def literal_secrets(v, at="", key=None):
    if isinstance(v, str):
        if PRIVATE_KEY_PEM.search(v):
            yield at, "is a private key"
        elif key and key.lower().endswith("secretref") and not SECRET_REF.match(v):
            yield at, "%s must be a reference (%s), not a value" % (key, REF_FORMS)
        elif key and SECRET_KEY.search(key) and v and not SECRET_REF.match(v):
            yield at, "%s holds a value: name the secret by reference instead" % key
    elif isinstance(v, (int, float)) and not isinstance(v, bool) and key and SECRET_KEY.search(key):
        yield at, "%s holds a value: name the secret by reference instead" % key
    elif isinstance(v, list):
        for i, x in enumerate(v):
            yield from literal_secrets(x, "%s[%d]" % (at, i), key)
    elif isinstance(v, dict):
        for k, x in v.items():
            yield from literal_secrets(x, "%s.%s" % (at, k) if at else str(k), str(k))


CONTENT = ROOT.parent / "content-configurations"
for top, label in [(ROOT / b, b) for b in ("factory", "examples", "conformance")] + [(CONTENT, "../content-configurations")]:
    if not top.is_dir():
        continue
    for f in sorted(p for p in top.rglob("*") if p.is_file() and p.suffix in (".yaml", ".yml", ".json", ".js", ".py", ".ts", ".md")):
        where = "%s/%s" % (label, f.relative_to(top))
        text = f.read_text(errors="replace")
        found = []
        if f.suffix in (".yaml", ".yml", ".json"):
            try:
                for d in (yaml.safe_load_all(text) if f.suffix != ".json" else [json.loads(text)]):
                    found.extend(literal_secrets(d))
            except Exception:
                pass
        if not found and PRIVATE_KEY_PEM.search(text):
            found.append(("", "holds a private key"))
        for at, why in found:
            err("L37", where, "%s%s" % ("at %s " % at if at else "", why))
# ------------------------------------------------------- port bindings
# L48 a bank's port bindings (R-11-47, Issue I-70) are values the runtime reads: each document in a bank folder of
# content-configurations/banks/ has the port-bindings shape, names its folder's tenant, and binds each port to an
# adapter from a validFrom; every port it binds is a published port of the factory or the bank's own; no port is bound
# twice from one validFrom in one document; a credential is a secretRef. The studio's releases sit in port-bindings/,
# one file per release, named by it. The studio runs the same check on a draft (studio/src/checks/checks.ts).
PORT_NAMES = {n for n, d in by_name.items() if d.get("kind") == "port"}


def as_text(v):
    if isinstance(v, (datetime.date, datetime.datetime)):
        return v.isoformat().replace("+00:00", "Z")
    if isinstance(v, dict):
        return {k: as_text(x) for k, x in v.items()}
    if isinstance(v, list):
        return [as_text(x) for x in v]
    return v


for bank in sorted(f for f in (BANKS.iterdir() if BANKS.is_dir() else []) if f.is_dir()):
    where = "content-configurations/banks/%s" % bank.name
    pf = bank / "pack.yaml"
    tenant = (yaml.safe_load(pf.read_text()) or {}).get("tenant") if pf.is_file() else None
    own_ports, bound = set(), []
    for f in sorted(bank.rglob("*.yaml")):
        rel = f.relative_to(bank)
        if rel.parts[0] == "packs":
            continue
        for d in yaml.safe_load_all(f.read_text()):
            if isinstance(d, dict) and d.get("kind") == "port" and d.get("name"):
                own_ports.add(d["name"])
            elif isinstance(d, dict) and d.get("kind") == "port-bindings":
                bound.append((rel, as_text(d)))
    for rel, d in bound:
        at = "%s/%s" % (where, rel)
        for e in sorted(Draft202012Validator(schemas["urn:th:schema:port-bindings"], registry=registry).iter_errors(d), key=lambda e: list(e.path)):
            err("L48", at, "%s at /%s" % (e.message[:160], "/".join(str(x) for x in e.path)))
        if tenant and d.get("tenant") != tenant:
            err("L48", at, "names tenant %s, but its folder is %s's" % (d.get("tenant"), tenant))
        if rel.parts[0] == "port-bindings" and (not d.get("release") or rel.name != "%s.yaml" % d.get("release")):
            err("L48", at, "a released set of port bindings is port-bindings/<release>.yaml and names that release")
        seen = set()
        for b in d.get("bindings") or []:
            if not isinstance(b, dict):
                continue
            port = b.get("port")
            if port not in PORT_NAMES and port not in own_ports:
                err("L48", at, "binds %s, which is no port of the factory or of the bank's pack" % port)
            if (port, b.get("validFrom")) in seen:
                err("L48", at, "binds %s twice from %s" % (port, b.get("validFrom")))
            seen.add((port, b.get("validFrom")))

# ------------------------------------------------------- the rates feed and the studio's configuration releases
# L52 (R-11-101, Issue I-276): a bank whose port bindings bind the rates port p.port.fx-rates.v1 takes its rates from the feed, so its
# configuration (the folder's configuration-set.yaml and its newest release in configuration-sets/) sets no p.enrich.fx-rate.v1#rates:
# a studio release that set them would be newer than the feed's and its typed rates would win until the feed ran again.
# L53 (R-11-102, Issue I-276): the studio never makes a patch release of a bank's configuration; the feed numbers its releases as the
# patches after the bank's newest, so the two lines of numbers never meet. A configuration release in configuration-sets/ is a major
# or a minor (x.y.0), as is every configuration set the factory's examples hold.
FX_PORT, FX_RATES = "p.port.fx-rates.v1", "p.enrich.fx-rate.v1#rates"


def release_key(r):
    try:
        return tuple(int(x) for x in str(r).split("."))
    except ValueError:
        return (0,)


for bank in sorted(f for f in (BANKS.iterdir() if BANKS.is_dir() else []) if f.is_dir()):
    where = "content-configurations/banks/%s" % bank.name
    feed = False
    for f in sorted(bank.glob("*.yaml")) + sorted((bank / "port-bindings").glob("*.yaml") if (bank / "port-bindings").is_dir() else []):
        for d in yaml.safe_load_all(f.read_text()):
            if isinstance(d, dict) and d.get("kind") == "port-bindings" and any(isinstance(b, dict) and b.get("port") == FX_PORT for b in d.get("bindings") or []):
                feed = True
    sets = []
    if (bank / "configuration-set.yaml").is_file():
        sets.append(("configuration-set.yaml", yaml.safe_load((bank / "configuration-set.yaml").read_text()) or {}))
    released = []
    for f in sorted((bank / "configuration-sets").glob("*.yaml")) if (bank / "configuration-sets").is_dir() else []:
        d = yaml.safe_load(f.read_text()) or {}
        if not str(f.stem).endswith(".0"):
            err("L53", "%s/configuration-sets/%s" % (where, f.name), "is a patch release of the bank's configuration: the studio releases a major or a minor, and the rates feed numbers its releases as the patches after the newest")
        released.append((release_key(f.stem), "configuration-sets/%s" % f.name, d))
    if released:
        _, name, d = max(released, key=lambda x: x[0])
        sets.append((name, d))
    if feed:
        for name, d in sets:
            if any(isinstance(v, dict) and v.get("parameter") == FX_RATES for v in d.get("values") or []):
                err("L52", "%s/%s" % (where, name), "sets %s, but the bank binds %s: its rates come from the feed, and typed rates in a newer release would win over them" % (FX_RATES, FX_PORT))

for path, d in docs:
    if d.get("kind") == "configuration-set" and d.get("release") and not str(d.get("release")).endswith(".0"):
        err("L53", path, "configuration set %s is a patch release: a bank's configuration is released as a major or a minor" % d.get("release"))

# ------------------------------------------------------- operations views
# L38 an operations view (R-05-48, R-05-49) says only what the store can answer: its scheme and legs exist, each leg is told
# apart by steps it has, each source reads a step the leg (or platform workflow) has, an extension field that is
# registered, a message of the scheme's catalogue or a known column; a personal value declares its mask; columns and
# filters name declared fields; and an action applies only where the flow can take it (a hold decision where that
# hold is reached, a signal the platform workflow handles).
OPS_PROPS = {
    "subject": {"id", "kind", "tenant", "scheme", "direction", "status", "lastOutcome", "lastStep", "amount", "currency", "count",
                "acceptedAt", "releaseId", "hold", "holdReason", "endedAt", "endOutcome", "duration"},
    "bulk": {"id", "messageId", "paymentInformationId", "numberOfTransactions", "controlSum", "requestedExecutionDate",
             "creationDateTime", "acceptedAt", "categoryPurposeCode", "releaseId", "debtorName", "debtorAccount.iban",
             "debtorAccount.other", "debtorAccount.currency", "debtorAccount.type", "debtorAgent.bicfi", "debtorAgent.memberId",
             "debtorAgent.clearingSystem", "initiatingParty.id"},
    "payment": {"id", "endToEndId", "instructionId", "transactionId", "uetr", "amount", "currency", "settlementDate", "purposeCode",
                "creditorAccount.iban", "creditorAccount.other", "creditorAccount.type", "creditorAgent.bicfi", "creditorAgent.memberId",
                "creditorAgent.name", "debtorAccount.iban", "debtorAccount.other", "debtorAgent.bicfi", "debtorAgent.memberId"},
    "line": {"id", "lineNo", "endToEndId", "amount", "currency", "creditorAccount.iban", "creditorAccount.other", "creditorAccount.type",
             "creditorAgent.bicfi", "creditorAgent.memberId", "creditorAgent.clearingSystem", "creditorAgent.name"},
    "hold": {"id", "kind", "status", "reason", "decision", "openedAt", "decidedAt", "decidedBy"},
    "header": {"scheme", "direction", "kind", "segment", "fileId", "endToEndId", "originalPaymentId"},
}
OPS_PERSONAL = {"bulk.debtorName", "bulk.initiatingParty.id", "bulk.debtorAccount.iban", "bulk.debtorAccount.other", "payment.creditorAccount.iban",
                "payment.creditorAccount.other", "payment.debtorAccount.iban", "payment.debtorAccount.other",
                "line.creditorAccount.iban", "line.creditorAccount.other"}
OPS_HOLDS = {"approve": ("p.hold.approval.v1", "approved"), "decline": ("p.hold.approval.v1", "declined"),
             "release": ("p.hold.repair.v1", "released"), "reject": ("p.hold.repair.v1", "rejected")}
# R-05-66, R-05-67: the dials the operations console reads from a view, and the attributes of a kept document a report may show.
OPS_DIALS = {"tatLimit", "unmaskWindow", "retentionPeriod"}
OPS_SCHEME_DIALS = {"tatLimit", "retentionPeriod"}
OPS_DOCUMENT_ATTRS = {"fileName", "at", "size", "source", "direction", "signed"}
STEP_SRC = re.compile(r"^step:(?P<path>[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*?)\.(?P<attr>outcome|reasonCode|at|attempt|activity|instance|status(\..+)?|writes\..+)$")
MSG_SRC = re.compile(r"^message:(?P<kind>[a-z0-9.\-]+?)\.(?:fileName|at|size|count|direction)$")


def flow_steps(wf, seen=()):
    """The step ids of a workflow with its segments inlined, each mapped to the activity it calls (or None)."""
    out = {}
    if not wf or wf.get("name") in seen:
        return out
    for st in all_steps(wf):
        if st.get("id"):
            out[st["id"]] = st.get("call")
        if st.get("use"):
            out.update(flow_steps(by_name.get(st["use"]), seen + (wf.get("name"),)))
    return out


def step_exists(path_, steps):
    head, _, inner = path_.partition(".")
    if head not in steps:
        return False
    if not inner:
        return True
    a = by_name.get(steps[head])
    sub = by_name.get((a or {}).get("implementation", {}).get("subflow")) if composite(a) else None
    return bool(sub) and inner in flow_steps(sub)


def reached(wf_names, activity):
    """True when one of the workflows (their segments, legs and subflows) calls the activity, or a hook point holds with it."""
    todo, seen = list(wf_names), set()
    while todo:
        n = todo.pop()
        if n in seen:
            continue
        seen.add(n)
        d = by_name.get(n)
        if not d:
            continue
        own = {st.get("call") for st in all_steps(d)} | {hp["policy"].get("onHold") for hp in d.get("hookPoints") or []}
        if activity in own:
            return True
        # Down into what the flow runs (its segments and subflows), never up into the platform workflow a leg fills.
        todo.extend(st["use"] for st in all_steps(d) if st.get("use"))
        for a in (by_name.get(st.get("call")) for st in all_steps(d) if st.get("call")):
            if composite(a):
                todo.append(a["implementation"]["subflow"])
    return False


ext_fields = {}
for _, d in docs:
    if d.get("kind") == "extension":
        for f in d.get("fields") or []:
            ext_fields["%s.%s" % (d["root"], f["path"])] = f

for path, d in docs:
    if d.get("kind") != "operations-view":
        continue
    scheme_scope = d.get("scheme")
    own = scope_of(d["name"])
    if scheme_scope:
        sch = schemes.get(scheme_scope)
        if sch is None or own != scheme_scope:
            err("L38", path, "%s is a view of scheme %s, which is not its own scope's scheme" % (d["name"], scheme_scope))
            continue
        platform = []
        leg_names = set(sch.get("legs") or [])
        catalogue = {m["id"] for m in sch.get("messages") or []}
    else:
        platform = d.get("platform") or []
        if own != "p" or not platform:
            err("L38", path, "a view with no scheme is the platform's: it is in scope p and lists the platform workflows it shows")
        for w in platform:
            if (by_name.get(w) or {}).get("wfKind") != "platform":
                err("L38", path, "%s is not a platform workflow" % w)
        leg_names, catalogue = set(), set()
    platform_steps = {}
    for w in platform:
        platform_steps.update(flow_steps(by_name.get(w)))
    sections = [("subject", d.get("subject") or {}, None)] + [("leg %s" % l.get("leg"), l, l.get("leg")) for l in d.get("legs") or []]
    all_leg_steps = {}
    for _, l, leg in sections[1:]:
        all_leg_steps.update(flow_steps(by_name.get(leg)))
    for where, sec, leg in sections:
        at = "%s %s" % (path, where)
        if leg is not None:
            wf = by_name.get(leg)
            if leg not in leg_names or not wf or wf.get("wfKind") != "leg":
                err("L38", at, "%s is not a leg of scheme %s" % (leg, scheme_scope))
                continue
            steps = flow_steps(wf)
            sel = sec.get("selectBy") or {}
            if sel.get("direction") and sel["direction"] != wf.get("direction"):
                err("L38", at, "selects direction %s, but the leg is %s" % (sel["direction"], wf.get("direction")))
            for s in sel.get("steps") or []:
                if s not in steps:
                    err("L38", at, "is told apart by step %s, which the leg does not have" % s)
            flows = [leg]
        else:
            steps = platform_steps if not scheme_scope else all_leg_steps
            flows = platform if not scheme_scope else sorted(leg_names)
        ids = {}
        for kind_, fields in (("field", sec.get("fields") or []), ("line field", (sec.get("lines") or {}).get("fields") or [])):
            for f in fields:
                key = (kind_, f["id"])
                if key in ids:
                    err("L38", at, "declares %s %s twice" % (kind_, f["id"]))
                ids[key] = f
                src = f["source"].strip()
                root = re.split(r"[.:(]", src, maxsplit=1)[0]
                problem = None
                personal = src in OPS_PERSONAL
                if src.startswith("step:"):
                    m = STEP_SRC.match(src)
                    if not m:
                        problem = "is not a step source (step:<step>.<outcome|reasonCode|at|attempt|status|writes...>)"
                    elif not step_exists(m.group("path"), steps):
                        problem = "reads step %s, which %s does not have" % (m.group("path"), leg or "any flow of the view")
                elif src.startswith("message:"):
                    m = MSG_SRC.match(src)
                    if not m or m.group("kind") not in catalogue:
                        problem = "reads a message that is not in the scheme's catalogue"
                elif src.startswith("lines."):
                    if not re.match(r"^lines\.(count|count\(.+\)|sum\(line\..+\))$", src):
                        problem = "is not lines.count, lines.count(<line source> = <value>) or lines.sum(<line source>)"
                elif root in ("ext", "line") and (src.startswith("ext.") or src.startswith("line.ext.")):
                    p = src[5:] if src.startswith("line.") else src
                    ef = ext_fields.get(p)
                    want = "item" if kind_ == "line field" else None
                    if ef is None:
                        problem = "reads %s, which no extension registers" % p
                    elif want and ef["level"] != want:
                        problem = "reads %s, which is not an item field" % p
                    elif not want and ef["level"] == "item":
                        problem = "reads item field %s outside the lines" % p
                    personal = personal or bool(ef and ef.get("personalData"))
                elif src.startswith("line.facts.") and kind_ == "line field":
                    pass
                elif root in OPS_PROPS and src[len(root) + 1:] in OPS_PROPS[root]:
                    if root == "line" and kind_ != "line field":
                        problem = "reads a line outside the lines"
                else:
                    problem = "is not a source the console can read"
                if problem:
                    err("L38", at, "%s %s %s" % (kind_, f["id"], problem))
                # R-05-49: a value that is personal data says how it is masked; showing it whole is a choice the old screen made
                if personal and "mask" not in f:
                    err("L38", at, "%s %s reads personal data (%s) and declares no mask" % (kind_, f["id"], src))
                if f.get("mask") == "none" and not f.get("oldScreen"):
                    err("L38", at, "%s %s is shown unmasked without naming the old screen that showed it" % (kind_, f["id"]))
        fields = {i for (k, i) in ids if k == "field"}
        lfields = {i for (k, i) in ids if k == "line field"}
        for c in sec.get("columns") or []:
            if c not in fields:
                err("L38", at, "column %s is not a declared field" % c)
        for c in (sec.get("lines") or {}).get("columns") or []:
            if c not in lfields:
                err("L38", at, "line column %s is not a declared line field" % c)
        for flt in sec.get("filters") or []:
            if flt["field"] not in fields:
                err("L38", at, "filter %s is not a declared field" % flt["field"])
        for g in {f.get("group") for (k, _), f in ids.items() if f.get("group")} - {g["id"] for g in sec.get("groups") or []}:
            err("L38", at, "group %s is not declared" % g)
        for a in sec.get("actions") or []:
            name = a["action"]
            # R-05-61 (Issue I-135): an action may name the platform workflows it applies to; each is one the view shows, and the
            # checks below hold the action to those workflows only.
            only = ((a.get("when") or {}).get("workflow")) or None
            shown = flows if not scheme_scope else sorted({w for l in flows for w in ((by_name.get(l) or {}).get("implements") or {}).get("of") or []})
            if only is not None:
                for w in only:
                    if w not in shown:
                        err("L38", at, "action %s names %s, which is not a platform workflow this view shows" % (name, w))
            if name in OPS_HOLDS:
                act, outcome = OPS_HOLDS[name]
                hold = by_name.get(act) or {}
                if outcome not in {o["name"] for o in hold.get("outcomes") or []} or not any(reached([w], act) for w in flows):
                    err("L38", at, "action %s decides %s, which no flow of this view reaches" % (name, act))
            elif name in ("cancel", "re-screen", "pause", "resume"):
                hosts = shown if only is None else [w for w in shown if w in only]
                if not hosts or not all(name in (by_name.get(w) or {}).get("signals", []) for w in hosts):
                    err("L38", at, "action %s is a signal that the platform workflows here do not all handle" % name)
            # R-05-94 (Issue I-279): an action held to the step that led to its hold names a hold and an activity a flow of the view reaches.
            led = (a.get("when") or {}).get("activity")
            if led is not None:
                if not (a.get("when") or {}).get("hold"):
                    err("L38", at, "action %s names the activity that led to its hold (%s) but no hold" % (name, led))
                elif led not in by_name or not any(reached([w], led) for w in flows):
                    err("L38", at, "action %s is held to a hold after %s, which no flow of this view reaches" % (name, led))
            if a.get("secondApprover") and a["secondApprover"] == "Operator" and a["role"] == "Supervisor":
                err("L38", at, "action %s asks a supervisor's action to be approved by an operator" % name)
        # R-05-67 (Issue I-144): a fixed report is the subject's, and every column is a declared field, a declared line field
        # or a document's attribute; a report of documents lists a kind the view declares.
        for rep in sec.get("reports") or []:
            rid = rep.get("id")
            if leg is not None:
                err("L38", at, "report %s is declared on a leg; reports are the subject's" % rid)
                continue
            kinds = {doc["kind"] for doc in sec.get("documents") or []}
            if rep.get("rows") == "documents" and rep.get("document") not in kinds:
                err("L38", at, "report %s lists documents of kind %s, which the view does not declare" % (rid, rep.get("document")))
            if rep.get("rows") != "documents" and rep.get("document"):
                err("L38", at, "report %s names a document kind but its rows are not documents" % rid)
            for c in rep.get("columns") or []:
                if c.startswith("line."):
                    ok = c[5:] in lfields and rep.get("rows") == "lines"
                elif c.startswith("document."):
                    ok = c[9:] in OPS_DOCUMENT_ATTRS and rep.get("rows") == "documents"
                else:
                    ok = c in fields
                if not ok:
                    err("L38", at, "report %s column %s is not a declared field of its rows" % (rid, c))
    # R-05-66: a view declares only the dials the operations console reads, each a duration.
    for k, decl in (d.get("parameters") or {}).items():
        allowed = OPS_DIALS if not scheme_scope else OPS_SCHEME_DIALS
        if k not in allowed or decl.get("type") != "duration":
            err("L38", path, "parameter %s is not a dial the operations console reads (%s, each a duration)" % (k, ", ".join(sorted(allowed))))
# ------------------------------------------------- published releases never change
# L41 (R-11-35, R-11-36) every file a seed published is recorded, with the SHA-256 of its text, in its change set's ledger
# (content-configurations/change-sets/ledger/<id>.yaml), and still has that text: a published release is never edited. A path
# is published once, except a bank pack's lock file, which a later ledger replaces by naming the hash it replaces and where
# the earlier lock is kept. Engine bindings are released like every other artefact (`artefacts/bindings/<name>/<release>.yaml`,
# owner's decision of 2026-11-22, Issue I-83) and are never replaced; the one replacement of the old unreleased bindings file,
# recorded before that decision (BINDINGS_REPLACED_BEFORE_RELEASES), stays on the record as it was. Every seeded file is in a ledger. And a factory artefact at a release the branch already holds
# is exactly what was seeded: a change to spec/factory needs a new release before it is seeded.
CC = ROOT.parent / "content-configurations"
BANK_LOCK = re.compile(r"^banks/[^/]+/pack\.yaml$")
# The replacement of the old, unreleased bindings file that a ledger recorded before bindings had releases (Issue I-83).
BINDINGS_REPLACED_BEFORE_RELEASES = {("artefacts/bindings/bindings.yaml", "CS-2026-11-22-005.yaml")}
SEED_MARK = "Generated by `npm run studio -- seed-content`"
SEED_HEADER = "# Generated by `npm run studio -- seed-content` from spec/%s. Edit the source and seed again; never edit this file.\n"


def sha256(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


if (CC / "change-sets" / "ledger").is_dir():
    recorded = {}
    for lf in sorted((CC / "change-sets" / "ledger").glob("*.yaml")):
        led = yaml.safe_load(lf.read_text()) or {}
        at = "content-configurations/change-sets/ledger/" + lf.name
        replaced = {r.get("path"): r for r in led.get("replaced") or []}
        for row in led.get("files") or []:
            pth, h = row.get("path"), row.get("sha256")
            replaces = pth in replaced and replaced[pth].get("sha256") == (recorded.get(pth) or ("",))[0]
            again = pth in recorded and not ((BANK_LOCK.match(pth) or (pth, lf.name) in BINDINGS_REPLACED_BEFORE_RELEASES) and replaces)
            if again:
                err("L41", at, "publishes %s again; ledger %s published it, and a published file is never replaced" % (pth, recorded[pth][1]))
            recorded[pth] = (h, lf.name)
    for pth, (h, lf) in sorted(recorded.items()):
        f = CC / pth
        if not f.is_file():
            err("L41", "content-configurations/" + pth, "ledger %s published it, and it is gone" % lf)
        elif sha256(f.read_text()) != h:
            err("L41", "content-configurations/" + pth, "has changed since ledger %s published it; a published release is never edited: release a new one" % lf)
    for f in sorted(CC.rglob("*")):
        if not f.is_file():
            continue
        try:
            first = f.read_text().split("\n", 1)[0]
        except UnicodeDecodeError:
            continue
        rel = f.relative_to(CC).as_posix()
        if SEED_MARK in first and rel not in recorded:
            err("L41", "content-configurations/" + rel, "is a seeded file that no change set's ledger records")
    # the factory at a release the branch holds is what was seeded
    for path, d in docs:
        if not path.startswith("factory/") or not d.get("release"):
            continue
        scope = d.get("scope") if d.get("kind") in ("scheme", "pack") else (d.get("name") and scope_of(d["name"]))
        if not scope:
            continue
        where = "artefacts/%s/%s/%s.yaml" % (scope, {"scheme": "scheme", "pack": "pack"}.get(d.get("kind"), d.get("name")), d["release"])
        seeded = CC / where
        if seeded.is_file() and seeded.read_text() != SEED_HEADER % path + (ROOT / path).read_text():
            err("L41", path, "%s@%s is not what the branch published at that release (content-configurations/%s): a published release "
                "is never edited; give the change a new release" % (d.get("name") or d.get("scope"), d["release"], where))

# L39 the error dictionary (R-05-51, R-05-52): every error an activity declares, every reason code a rule, rule set or
# table gives, every reason code a port's party answers (as its simulators answer for it), every code a port boundary,
# the interpreter and each engine binding raise, has an entry, raised by that kind of thing, in a dictionary of the
# artefact's own scope or a scope it may depend on (a scheme: its cluster platform and p; a cluster platform: p; b: any).
# Codes are unique in a dictionary; an entry for an ISO reason code a rule or a port raises names it as its isoReasonCode.
# The runtime's own codes (store, signing, auth, consoles) are checked against the same dictionaries by
# runtime/tests/TransactHub.Service.Tests (ErrorDictionaryTests).
PORT_RAISES = ["TransientError", "Timeout", "PortNotBound", "SecretUnavailable"]
ENGINE_RAISES = {
    "interpreter": ["Failure", "Timeout", "RetriesExhausted"],
    "temporal": ["temporal.ServerUnreachable", "temporal.WorkflowTaskFailed"],
    "dtfx": ["dtfx.OrchestrationFailed"],
    "flowable": ["flowable.Unreachable", "flowable.DeadLetterJob", "th:nonRetryable"],
    "elsa": ["elsa.WorkflowFaulted"],
}
dictionaries = {}  # scope -> {code: entry}
for path, d in docs:
    if d.get("kind") != "error-dictionary":
        continue
    sc = scope_of(d["name"])
    seen = dictionaries.setdefault(sc, {})
    for e in d.get("entries") or []:
        if e["code"] in seen:
            err("L39", path, "%s has two entries for code %s" % (d["name"], e["code"]))
        seen[e["code"]] = e
        if re.fullmatch(r"[A-Z0-9]{4}", e["code"]) and ({"rule", "port"} & set(e.get("raisedBy") or [])) and e.get("isoReasonCode") != e["code"]:
            err("L39", path, "%s is an ISO reason code a rule or a port gives, but its entry names isoReasonCode %r" % (e["code"], e.get("isoReasonCode")))


def dictionary_scopes(sc):
    if sc == "b":
        return list(dictionaries)
    if sc == "p":
        return ["p"]
    if sc.endswith(".p"):
        return [sc, "p"]
    return [sc, sc.split(".")[0] + ".p", "p"]


def covered(sc, code, raiser):
    for s in dictionary_scopes(sc):
        e = dictionaries.get(s, {}).get(code)
        if e is not None:
            return raiser in (e.get("raisedBy") or []), s
    return None, None


def need(path, sc, code, raiser, what):
    ok, at = covered(sc, code, raiser)
    if ok is None:
        err("L39", path, "%s raises %s, which no error dictionary of %s has" % (what, code, " or ".join(dictionary_scopes(sc)) or "any scope"))
    elif not ok:
        err("L39", path, "%s raises %s, but the entry in %s's dictionary is not raised by %s" % (what, code, at, raiser))


def reason_codes(o, acc):
    if isinstance(o, dict):
        for k, v in o.items():
            if k == "reasonCode" and isinstance(v, str):
                acc.add(v)
            else:
                reason_codes(v, acc)
    elif isinstance(o, list):
        for x in o:
            reason_codes(x, acc)


if dictionaries:
    for path, d in docs:
        kind, name = d.get("kind"), d.get("name")
        if not name or kind in ("test", "error-dictionary", "pack"):
            continue
        sc = scope_of(name)
        if kind == "activity":
            for e in d.get("errors") or []:
                need(path, sc, e["name"], "activity", "activity %s" % name)
        if kind in ("rule", "ruleset", "table", "activity", "simulator"):
            acc = set()
            reason_codes(d, acc)
            for c in sorted(acc):
                need(path, sc, c, "port" if kind == "simulator" else "rule", "%s %s" % (kind, name))
        if kind == "port":
            for c in PORT_RAISES:
                need(path, sc, c, "port", "port %s" % name)
    targets = sorted({d["target"] for _, d in docs if d.get("kind") == "engine-binding"})
    for t in ["interpreter"] + targets:
        for c in ENGINE_RAISES.get(t, []):
            need("engine-binding %s" % t, "p", c, "interpreter" if t == "interpreter" else "engine:" + t, "the %s" % ("interpreter" if t == "interpreter" else t + " binding"))
else:
    err("L39", "factory/p/errors", "no error dictionary: the platform's errors have no entries")

# ------------------------------------------------------------- exact money
# L42 money is exact (R-02-17, R-04-23): no expression compares a difference with a fraction between 0 and 1 (a
# floating-point tolerance, which exact decimals never need), and a number given in examples, parameter defaults
# and bounds, configuration values and test cases has at most 15 significant digits (a longer amount is a string,
# since a binary double cannot hold it).
from decimal import Decimal as _Dec

COMPARE = re.compile(r"(<=|>=|<|>)")
FRACTION = re.compile(r"^\(?\s*0?\.\d*[1-9]\d*\s*\)?$")
SPLIT = re.compile(r"\band\b|\bor\b|&&|\|\||,|=>")


def significant(v):
    """Significant digits of a number as JSON and YAML readers take it: a float by its shortest repr."""
    t = format(_Dec(repr(v)) if isinstance(v, float) else _Dec(v), "f").lstrip("-")
    if "." in t:
        t = t.rstrip("0").rstrip(".")
    return len(t.replace(".", "").lstrip("0"))


def long_numbers(v, at=""):
    if isinstance(v, bool):
        return
    if isinstance(v, (int, float)):
        if significant(v) > 15:
            yield at or "(value)", v
    elif isinstance(v, dict):
        for k, x in v.items():
            yield from long_numbers(x, "%s.%s" % (at, k) if at else str(k))
    elif isinstance(v, list):
        for i, x in enumerate(v):
            yield from long_numbers(x, "%s[%d]" % (at, i))


def expressions(d):
    """The expression strings of a rule or a table."""
    out = []
    for c in d.get("when") or []:
        for k in ("having",):
            if isinstance(c.get(k), str):
                out.append(c[k])
        for v in (c.get("let") or {}).values():
            if isinstance(v, str):
                out.append(v)
        for k in ("match", "exists", "not", "all"):
            if isinstance(c.get(k), dict) and isinstance(c[k].get("where"), str):
                out.append(c[k]["where"])
        q = c.get("query") or {}
        if isinstance(q.get("match"), dict) and isinstance(q["match"].get("where"), str):
            out.append(q["match"]["where"])
    for i in d.get("inputs") or []:
        if isinstance(i, dict) and isinstance(i.get("expression"), str):
            out.append(i["expression"])
    return out


def tolerance(expr):
    for part in SPLIT.split(expr):
        sides = COMPARE.split(part)
        for i in range(1, len(sides) - 1, 2):
            left, right = sides[i - 1].strip(), sides[i + 1].strip()
            for lit, other in ((right, left), (left, right)):
                if FRACTION.match(lit) and re.search(r"[\w)\]]\s+-\s|[\w)\]]-[\w(]", other):
                    return "%s %s %s" % (left, sides[i], right)
    return None


for path, d in docs:
    kind = d.get("kind")
    if kind in ("rule", "table"):
        for e in expressions(d):
            t = tolerance(e)
            if t:
                err("L42", path, "%s compares a difference with a tolerance (%s); amounts are exact decimals, compare them directly"
                    % (d.get("name"), t))
        for n, ex in enumerate(d.get("examples") or []):
            for at, v in long_numbers(ex.get("given") or {}):
                err("L42", path, "example %s gives %s = %r, more than 15 significant digits; write the amount as a decimal string"
                    % (ex.get("name", n), at, v))
    if kind in ("rule", "table", "activity"):
        for pn, decl in (d.get("parameters") or {}).items():
            if isinstance(decl, dict):
                for at, v in long_numbers({k: decl.get(k) for k in ("default", "bounds") if k in decl}):
                    err("L42", path, "parameter %s: %s = %r has more than 15 significant digits; write it as a decimal string" % (pn, at, v))
    if kind in ("configuration-set", "test"):
        for at, v in long_numbers({k: x for k, x in d.items() if k not in ("release", "dsl")}):
            err("L42", path, "%s = %r has more than 15 significant digits; write the amount as a decimal string" % (at, v))
# L43 (R-11-38) a leg fills the slot contract <slot>@<major> of a platform workflow: the workflow it names is a platform
# workflow at that major with the slot, and the leg reads nothing beyond the slot's contract and its scheme's own extensions,
# and writes (its exit) nothing beyond the slot's writes and its scheme's own extensions (Issue I-112). A distribution is
# built only from legs that fit (studio/src/export/distribution.ts, checkSlotContracts). A write the owner has still to decide
# would be listed here and reported as a warning; none is open since the WPS returns went into p.wf.inward.v2's scheme-leg@2
# (owner's decision of 23 November 2026, Issue I-141, R-09-87).
SLOT_WRITE_OPEN = {}
def beyond(paths, allowed, own):
    out = []
    for x in paths or []:
        if any(x == a or x.startswith(a + ".") or x.startswith(a + "[") for a in allowed) or x.startswith("ext." + own):
            continue
        out.append(x)
    return out


for path, d in docs:
    if d.get("kind") != "workflow" or d.get("wfKind") != "leg":
        continue
    imp = d.get("implements") or {}
    for of in imp.get("of") or []:
        host = by_name.get(of)
        if host is None or host.get("kind") != "workflow":
            continue
        if host.get("wfKind") != "platform":
            err("L43", path, "fills %s of %s, which is not a platform workflow" % (imp.get("slot"), of))
            continue
        contract = {s["name"]: s["contract"] for s in host.get("slots") or []}.get(imp.get("slot"))
        if not contract:
            continue
        for x in beyond((d.get("entry") or {}).get("reads"), contract.get("reads") or [], scope_of(d["name"])):
            err("L43", path, "reads %s, which the slot contract %s@%s of %s does not give it" % (x, imp.get("slot"), of.rsplit(".v", 1)[-1], of))
        for x in beyond((d.get("exit") or {}).get("writes"), contract.get("writes") or [], scope_of(d["name"])):
            issue = SLOT_WRITE_OPEN.get((d["name"], of, x))
            (warn if issue else err)("L43", path, "writes %s, which the slot contract %s@%s of %s does not let it write%s" % (
                x, imp.get("slot"), of.rsplit(".v", 1)[-1], of, " (open: Issue %s)" % issue if issue else ""))

# L47 (R-12-19) a platform workflow that blocks or posts money owns its order's money: a leg that fills one of its slots calls no
# reserve or post activity, directly, through a segment or through a composite's subflow. The platform's lifecycle is the order's,
# so a second scheme fills the same slot without changing it, and no scheme pack holds the order's posting (Issue I-133).
MONEY_TYPES = ("reserve", "post")


def money_steps(wf, seen=()):
    out = []
    if not wf or wf.get("name") in seen:
        return out
    for st in all_steps(wf):
        if st.get("use"):
            out += money_steps(by_name.get(st["use"]), seen + (wf.get("name"),))
        a = by_name.get(st.get("call")) if st.get("call") else None
        if not a or a.get("kind") != "activity":
            continue
        if a.get("type") in MONEY_TYPES:
            out.append((st.get("id", "?"), a["name"]))
        elif composite(a):
            out += [(st.get("id", "?") + "." + i, n) for i, n in money_steps(by_name.get(a["implementation"]["subflow"]), seen + (wf.get("name"),))]
    return out


for path, d in docs:
    if d.get("kind") != "workflow" or d.get("wfKind") != "leg":
        continue
    for of in (d.get("implements") or {}).get("of") or []:
        host = by_name.get(of)
        if not host or host.get("wfKind") != "platform" or not money_steps(host):
            continue
        for sid, name in money_steps(d):
            err("L47", path, "step %s calls %s, but %s, whose slot it fills, owns the order's blocks and postings" % (sid, name, of))

# L44 (R-11-39) the parameter contract: every parameter declares its type and a default, and the default lies within its
# own bounds; a distribution carries every dial with its type and bounds, and a configuration release is checked against it.
for path, d in docs:
    params = d.get("parameters")
    if not isinstance(params, dict):
        continue
    for k, decl in params.items():
        name = "%s#%s" % (d.get("name"), k)
        if not isinstance(decl, dict) or not decl.get("type"):
            err("L44", path, "%s declares no type" % name)
            continue
        if "default" not in decl:
            err("L44", path, "%s declares no default" % name)
        elif not within_bounds(decl, decl["default"]):
            err("L44", path, "%s: its default %r is outside its own bounds %r" % (name, decl["default"], decl.get("bounds")))
        # L56 (R-05-100, Issue I-282) a ledger account is an account: a string or a code of an activity, with its kind
        if decl.get("role") == "ledger-account":
            if d.get("kind") != "activity":
                err("L56", path, "%s is marked a ledger account, but only an activity's parameter names one" % name)
            if decl["type"] not in ("string", "code"):
                err("L56", path, "%s is marked a ledger account, but it is of type %s, not a string or a code" % (name, decl["type"]))

# L49 (R-01-25) a reference table: its key and columns agree, its editions run in date order, every row fits its columns,
# a column that references another table holds that table's keys, and a key is not repeated within an edition.
ref_tables = {d["name"]: d for _, d in docs if d.get("kind") == "reference-table"}
for path, d in docs:
    if d.get("kind") == "reference-table":
        for m in reference_table_problems(d, ref_tables):
            err("L49", path, m)

# L51 (R-09-118, Issue I-190) the conformance kit's size is stated in one place, conformance/kit.yaml: a design text that
# counts its scenarios ("holds N scenarios", "N of N scenarios", "N kit scenarios") names the number the kit holds.
kit_size = sum(len(d.get("scenarios") or []) for _, d in docs if d.get("kind") == "conformance-kit")
COUNTED = re.compile(r"\b(?:holds|has|of|all)\s+(\d+)\s+(?:conformance\s+|kit\s+)?scenarios\b|\b(\d+)\s+(?:conformance|kit)\s+scenarios\b", re.I)
if kit_size and (ROOT / "design").is_dir():
    for f in sorted((ROOT / "design").glob("*.md")):
        for n, line in enumerate(f.read_text().splitlines(), 1):
            for m in COUNTED.finditer(line):
                said = int(m.group(1) or m.group(2))
                if said != kit_size:
                    err("L51", "%s:%d" % (f.relative_to(ROOT), n), "counts %d conformance scenarios, but conformance/kit.yaml holds %d: say \"every scenario of conformance/kit.yaml\"" % (said, kit_size))

# L54 (R-03-25, R-03-26, Issue I-303) every attempt of an activity is at least once on every engine (a crash, an expired lock),
# so an activity with an outside effect carries an idempotency key whether or not it retries: one whose effect is write or
# external, one whose type changes something outside it (an exchange that sends, responds or reports; a reserve, hold, notify or
# screen), and one that names a port whose operations only send. The key derives from business ids, the same on every attempt:
# it reads the header and not the clock, a new id or the attempt.
# A screen counts by its effect: a screening lookup may only read (Issue I-316).
OUTSIDE_TYPES = {"reserve", "hold", "notify"}
NOT_A_BUSINESS_ID = re.compile(r"\b(?:now|clock|ids|uuid|random|attempt)\b")
for path, d in docs:
    if d.get("kind") != "activity":
        continue
    why = None
    if d.get("effect") in ("write", "external"):
        why = "its effect is %s" % d["effect"]
    elif d.get("type") in OUTSIDE_TYPES:
        why = "it is a %s" % d["type"]
    elif d.get("type") == "exchange" and d.get("mode") in ("send", "respond", "report"):
        why = "it is an exchange that %ss" % ("report" if d["mode"] == "report" else d["mode"])
    else:
        for pn in d.get("ports") or []:
            ops = (by_name.get(pn) or {}).get("operations") or []
            if ops and all(o.get("style") == "send" for o in ops):
                why = "its port %s only sends" % pn
                break
    if why is None:
        continue
    key = d.get("idempotencyKey")
    if not key:
        err("L54", path, "%s has an outside effect (%s) but no idempotency key: every attempt is at least once" % (d.get("name"), why))
    elif "header." not in key or NOT_A_BUSINESS_ID.search(key):
        err("L54", path, "%s's idempotency key %r is not the same on every attempt: it must read a business id of the header and not the clock, a new id or the attempt" % (d.get("name"), key))

# ---------------------------------------------------------------- report
kinds = {}
for _, d in docs:
    kinds[d.get("kind")] = kinds.get(d.get("kind"), 0) + 1
print("documents: %d  %s" % (len(docs), dict(sorted(kinds.items()))))
for w in warnings:
    print("WARN  " + w)
for e in errors:
    print("ERROR " + e)
print("%d error(s), %d warning(s)" % (len(errors), len(warnings)))
sys.exit(1 if errors else 0)
