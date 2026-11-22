#!/usr/bin/env python3
"""Validate every artefact in factory/ and examples/ against its schema, then run the
static checks that design/05 names. Exit code 1 on any error.

Run:  python3 tools/validate.py
This script is the reference for build spec C04 (static checks). It is not the product.
"""
import json
import pathlib
import sys

import yaml
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCHEMA_OF = {"activity": "activity", "rule": "rule", "ruleset": "rule", "table": "rule", "workflow": "workflow",
             "simulator": "simulator", "scheme": "scheme", "hook": "hook", "configuration-set": "configuration-set",
             "pack": "pack", "engine-binding": "engine-binding", "test": "test-case", "extension": "extension",
             "port": "port", "supersession": "supersession"}
errors, warnings = [], []


def err(code, where, msg):
    errors.append("%s  %s: %s" % (code, where, msg))


def warn(code, where, msg):
    warnings.append("%s  %s: %s" % (code, where, msg))


def seconds(d):
    """The length of an ISO 8601 duration such as P1D or PT10M, in seconds."""
    import re
    m = re.fullmatch(r"P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?", str(d))
    if not m:
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
        for k in sorted(set(d.get("policy") or {}) - {"replyTimeout", "replyCalendar"}):
            err("L30", path, "a composite carries no %s; attempts and retries belong to the steps of its subflow" % k)
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
        val, lo, hi = v["value"], b.get("min"), b.get("max")
        if p.get("type") == "duration":
            val, lo, hi = seconds(val), seconds(lo) if lo is not None else None, seconds(hi) if hi is not None else None
        if hi is not None and val > hi or lo is not None and val < lo:
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
STUDIO_FOLDERS = ("artefacts", "configuration-sets", "scripts")
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
