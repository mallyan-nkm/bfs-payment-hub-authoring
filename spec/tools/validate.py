#!/usr/bin/env python3
"""Validate every artefact in factory/ and examples/ against its schema, then run the
static checks that design/05 names. Exit code 1 on any error.

Run:  python3 tools/validate.py
This script is the reference for build spec C04 (static checks). It is not the product.
"""
import hashlib
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
             "error-dictionary": "error-dictionary"}
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
    if d.get("kind") in ("activity", "rule", "ruleset", "table", "workflow", "simulator", "port", "extension") and scope_of(name) != "b":
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
        if w is None or w.get("kind") != "workflow" or w["release"] != rel:
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
STUDIO_FOLDERS = ("artefacts", "configuration-sets", "scripts", "packs")
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
# holds a reference (env:, file:, vault:, akv:) or nothing; a secretRef holds a reference; no text is a private key.
# It reads the spec's content and everything under content-configurations/. The studio check is the same rule.
SECRET_KEY = re.compile(r"(^|[a-z0-9_])(password|passwd|passphrase|secret|token|apikey|api_key|accesskey|access_key|privatekey|private_key|clientsecret|client_secret|credentials?)$", re.I)
SECRET_REF = re.compile(r"^(env|file|vault|akv):\S+$")
PRIVATE_KEY_PEM = re.compile(r"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----")


def literal_secrets(v, at="", key=None):
    if isinstance(v, str):
        if PRIVATE_KEY_PEM.search(v):
            yield at, "is a private key"
        elif key and key.lower().endswith("secretref") and not SECRET_REF.match(v):
            yield at, "%s must be a reference (env:NAME, file:/path or vault:mount/path#field), not a value" % key
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
# ------------------------------------------------------- operations views
# L38 an operations view (R-05-48, R-05-49) says only what the store can answer: its scheme and legs exist, each leg is told
# apart by steps it has, each source reads a step the leg (or platform workflow) has, an extension field that is
# registered, a message of the scheme's catalogue or a known column; a personal value declares its mask; columns and
# filters name declared fields; and an action applies only where the flow can take it (a hold decision where that
# hold is reached, a signal the platform workflow handles).
OPS_PROPS = {
    "subject": {"id", "kind", "tenant", "scheme", "direction", "status", "lastOutcome", "lastStep", "amount", "currency", "count",
                "acceptedAt", "releaseId", "hold", "holdReason"},
    "bulk": {"id", "messageId", "paymentInformationId", "numberOfTransactions", "controlSum", "requestedExecutionDate",
             "creationDateTime", "acceptedAt", "categoryPurposeCode", "releaseId", "debtorName", "debtorAccount.iban",
             "debtorAccount.other", "debtorAccount.currency", "debtorAgent.bicfi", "debtorAgent.memberId", "debtorAgent.clearingSystem"},
    "payment": {"id", "endToEndId", "instructionId", "transactionId", "uetr", "amount", "currency", "settlementDate", "purposeCode",
                "creditorAccount.iban", "creditorAccount.other", "creditorAgent.bicfi", "creditorAgent.memberId",
                "debtorAccount.iban", "debtorAccount.other", "debtorAgent.bicfi", "debtorAgent.memberId"},
    "line": {"id", "lineNo", "endToEndId", "amount", "currency", "creditorAccount.iban", "creditorAccount.other",
             "creditorAgent.bicfi", "creditorAgent.memberId", "creditorAgent.clearingSystem"},
    "hold": {"id", "kind", "status", "reason", "decision", "openedAt", "decidedAt", "decidedBy"},
    "header": {"scheme", "direction", "kind", "segment", "fileId", "endToEndId"},
}
OPS_PERSONAL = {"bulk.debtorName", "bulk.debtorAccount.iban", "bulk.debtorAccount.other", "payment.creditorAccount.iban",
                "payment.creditorAccount.other", "payment.debtorAccount.iban", "payment.debtorAccount.other",
                "line.creditorAccount.iban", "line.creditorAccount.other"}
OPS_HOLDS = {"approve": ("p.hold.approval.v1", "approved"), "decline": ("p.hold.approval.v1", "declined"),
             "release": ("p.hold.repair.v1", "released"), "reject": ("p.hold.repair.v1", "rejected")}
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
            if name in OPS_HOLDS:
                act, outcome = OPS_HOLDS[name]
                hold = by_name.get(act) or {}
                if outcome not in {o["name"] for o in hold.get("outcomes") or []} or not any(reached([w], act) for w in flows):
                    err("L38", at, "action %s decides %s, which no flow of this view reaches" % (name, act))
            elif name in ("cancel", "re-screen", "pause", "resume"):
                hosts = flows if not scheme_scope else sorted({w for l in flows for w in ((by_name.get(l) or {}).get("implements") or {}).get("of") or []})
                if not hosts or not all(name in (by_name.get(w) or {}).get("signals", []) for w in hosts):
                    err("L38", at, "action %s is a signal that the platform workflows here do not all handle" % name)
            if a.get("secondApprover") and a["secondApprover"] == "Operator" and a["role"] == "Supervisor":
                err("L38", at, "action %s asks a supervisor's action to be approved by an operator" % name)
# ------------------------------------------------- published releases never change
# L41 (R-11-35, R-11-36) every file a seed published is recorded, with the SHA-256 of its text, in its change set's ledger
# (content-configurations/change-sets/ledger/<id>.yaml), and still has that text: a published release is never edited. A path
# is published once, except a bank pack's lock file, which a later ledger replaces by naming the hash it replaces and where
# the earlier lock is kept. Every seeded file is in a ledger. And a factory artefact at a release the branch already holds
# is exactly what was seeded: a change to spec/factory needs a new release before it is seeded.
CC = ROOT.parent / "content-configurations"
BANK_LOCK = re.compile(r"^banks/[^/]+/pack\.yaml$")
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
            again = pth in recorded and not (BANK_LOCK.match(pth) and pth in replaced and replaced[pth].get("sha256") == recorded[pth][0])
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
