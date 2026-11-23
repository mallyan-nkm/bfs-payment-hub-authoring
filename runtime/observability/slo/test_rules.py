#!/usr/bin/env python3
"""The generated alert rules parse and fire on synthetic data (R-09-72).

Every rule's expression is parsed by a small PromQL evaluator for the forms the generator writes (an instant or range
selector with label matchers; rate and increase; sum, max and min, with by; a comparison with a number; and), each name
it reads must be an instrument a host defines, and each alert is run over synthetic series: it fires where the target is
broken for its `for` duration, and not where the target holds. No Prometheus is needed.

Run: python3 runtime/observability/slo/test_rules.py
"""
import pathlib
import re
import subprocess
import sys

import yaml

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
RULES = HERE.parent / "alerts" / "transacthub.rules.yaml"

# ------------------------------------------------------------------ a PromQL subset

TOKEN = re.compile(r"\s*(?:(?P<num>\d+(?:\.\d+)?)|(?P<id>[A-Za-z_:][A-Za-z0-9_:]*)|(?P<str>\"[^\"]*\")|(?P<dur>\[\d+[smhd]\])|(?P<op>==|!=|=~|<=|>=|[(){},<>=]))")


def tokens(s):
    out, i = [], 0
    while i < len(s):
        m = TOKEN.match(s, i)
        if not m or m.end() == i:
            if s[i:].strip() == "":
                break
            raise SyntaxError("cannot read %r" % s[i:])
        kind = m.lastgroup
        out.append((kind, m.group(kind)))
        i = m.end()
    return out


class Parser:
    def __init__(self, s):
        self.t = tokens(s)
        self.i = 0

    def peek(self, v=None):
        if self.i >= len(self.t):
            return None
        return self.t[self.i] if v is None or self.t[self.i][1] == v else None

    def take(self, v=None):
        tok = self.peek()
        if tok is None or (v is not None and tok[1] != v):
            raise SyntaxError("expected %r at %r" % (v, tok))
        self.i += 1
        return tok

    def parse(self):
        e = self.and_()
        if self.i != len(self.t):
            raise SyntaxError("left over: %r" % (self.t[self.i:],))
        return e

    def and_(self):
        e = self.cmp()
        while self.peek("and"):
            self.take()
            e = ("and", e, self.cmp())
        return e

    def cmp(self):
        e = self.agg()
        if self.peek() and self.peek()[1] in ("<", ">", "<=", ">=", "==", "!="):
            op = self.take()[1]
            e = ("cmp", op, e, float(self.take()[1]))
        return e

    def agg(self):
        tok = self.peek()
        if tok and tok[1] in ("sum", "max", "min"):
            fn = self.take()[1]
            by = []
            if self.peek("by"):
                self.take()
                self.take("(")
                while not self.peek(")"):
                    by.append(self.take()[1])
                    if self.peek(","):
                        self.take()
                self.take(")")
            self.take("(")
            inner = self.inner()
            self.take(")")
            return ("agg", fn, by, inner)
        return self.inner()

    def inner(self):
        tok = self.peek()
        if tok and tok[1] in ("rate", "increase"):
            fn = self.take()[1]
            self.take("(")
            sel = self.selector()
            kind, dur = self.take()
            if kind != "dur":
                raise SyntaxError("%s needs a range" % fn)
            self.take(")")
            return ("range", fn, sel, seconds(dur[1:-1]))
        return ("instant", self.selector())

    def selector(self):
        kind, name = self.take()
        if kind != "id":
            raise SyntaxError("a metric name, not %r" % name)
        matchers = []
        if self.peek("{"):
            self.take("{")
            while not self.peek("}"):
                label = self.take()[1]
                op = self.take()[1]
                value = self.take()[1].strip('"')
                matchers.append((label, op, value))
                if self.peek(","):
                    self.take()
            self.take("}")
        return (name, matchers)


def seconds(d):
    n, u = int(d[:-1]), d[-1]
    return n * {"s": 1, "m": 60, "h": 3600, "d": 86400}[u]


def matches(labels, matchers):
    for label, op, value in matchers:
        v = labels.get(label, "")
        if op == "=" and v != value or op == "!=" and v == value or op == "=~" and not re.fullmatch(value, v):
            return False
    return True


def evaluate(e, series, t):
    """series: {name: [(labels dict, [(time, value), ...])]}. Answers {labels tuple: value} at time t."""
    kind = e[0]
    if kind == "instant":
        name, m = e[1]
        out = {}
        for labels, samples in series.get(name, []):
            if not matches(labels, m):
                continue
            past = [v for (ts, v) in samples if ts <= t and ts > t - 300]
            if past:
                out[tuple(sorted(labels.items()))] = past[-1]
        return out
    if kind == "range":
        _, fn, (name, m), rng = e
        out = {}
        for labels, samples in series.get(name, []):
            if not matches(labels, m):
                continue
            w = [(ts, v) for (ts, v) in samples if t - rng <= ts <= t]
            if len(w) < 2:
                continue
            inc = sum(max(0, b[1] - a[1]) if b[1] >= a[1] else b[1] for a, b in zip(w, w[1:]))
            out[tuple(sorted(labels.items()))] = inc / (w[-1][0] - w[0][0]) if fn == "rate" else inc
        return out
    if kind == "agg":
        _, fn, by, inner = e
        groups = {}
        for labels, v in evaluate(inner, series, t).items():
            key = tuple((k, v2) for k, v2 in labels if k in by)
            groups.setdefault(key, []).append(v)
        f = {"sum": sum, "max": max, "min": min}[fn]
        return {k: f(vs) for k, vs in groups.items()}
    if kind == "cmp":
        _, op, inner, n = e
        test = {"<": lambda a: a < n, ">": lambda a: a > n, "<=": lambda a: a <= n, ">=": lambda a: a >= n, "==": lambda a: a == n, "!=": lambda a: a != n}[op]
        return {k: v for k, v in evaluate(inner, series, t).items() if test(v)}
    if kind == "and":
        left, right = evaluate(e[1], series, t), evaluate(e[2], series, t)
        return {k: v for k, v in left.items() if k in right}
    raise ValueError(kind)


def names(e):
    if e[0] == "instant":
        return {e[1][0]}
    if e[0] == "range":
        return {e[2][0]}
    if e[0] == "agg":
        return names(e[3])
    if e[0] == "cmp":
        return names(e[2])
    return names(e[1]) | names(e[2])


def fires(rule, series, until, step=30):
    """Does the alert fire at `until`, its condition having held for its `for` at every evaluation before?"""
    expr = Parser(rule["expr"]).parse()
    hold = seconds(rule["for"]) if rule.get("for") else 0
    t, since = 0, None
    while t <= until:
        if evaluate(expr, series, t):
            since = t if since is None else since
        else:
            since = None
        t += step
    return since is not None and until - since >= hold


# ------------------------------------------------------------------ synthetic data

def counter(rate_per_s, start=0, end=7200, step=30, labels=None):
    return (labels or {}, [(t, rate_per_s * t) for t in range(start, end + 1, step)])


def gauge(value_at, start=0, end=7200, step=30, labels=None):
    return (labels or {}, [(t, value_at(t)) for t in range(start, end + 1, step)])


def known_instruments():
    """Every th_ instrument a host defines, as its Prometheus names may begin."""
    found = set()
    for f in list((ROOT / "runtime" / "src").rglob("*.cs")) + list((ROOT / "studio" / "engines" / "shared").glob("*.cs")):
        found |= set(re.findall(r'Create(?:Counter|Histogram|ObservableGauge|UpDownCounter)(?:<\w+>)?\(\s*"(th\.[a-z._]+)"', f.read_text(errors="replace")))
    found |= set(re.findall(r"create(?:Counter|Histogram|UpDownCounter)\('(th\.[a-z._]+)'", (ROOT / "studio" / "src" / "telemetry" / "telemetry.ts").read_text()))
    return {n.replace(".", "_") for n in found}


def main():
    problems = []
    # The rules are current with the targets.
    r = subprocess.run([sys.executable, str(HERE / "build_rules.py"), "--check"], capture_output=True, text=True)
    if r.returncode != 0:
        problems.append(r.stdout.strip())
    doc = yaml.safe_load(RULES.read_text())
    known = known_instruments()
    alerts = {}
    for g in doc["groups"]:
        for rule in g["rules"]:
            try:
                e = Parser(rule["expr"]).parse()
            except SyntaxError as x:
                problems.append("%s does not parse: %s" % (rule.get("alert") or rule.get("record"), x))
                continue
            for n in names(e):
                base = re.sub(r"_(total|seconds|bucket|count|sum)$", "", n)
                if base not in known and n not in known:
                    problems.append("%s reads %s, which no host defines" % (rule.get("alert") or rule.get("record"), n))
            if "alert" in rule:
                alerts[rule["alert"]] = rule
    wps = {"th_scheme": "uae.wps", "th_message": "uae.wps.sif", "th_tenant": "example-bank"}
    hour, day = 3600, 86400
    cases = [
        # Throughput: busy at 30 lines a second fires; 60 a second, or idle at 30 with nothing queued, does not.
        ("TransactHubLineThroughputBelowTarget", True, {"th_payment_lines_total": [counter(30)], "th_queue_in_flight": [gauge(lambda t: 12)]}, 2 * hour),
        ("TransactHubLineThroughputBelowTarget", False, {"th_payment_lines_total": [counter(60)], "th_queue_in_flight": [gauge(lambda t: 12)]}, 2 * hour),
        ("TransactHubLineThroughputBelowTarget", False, {"th_payment_lines_total": [counter(30)], "th_queue_in_flight": [gauge(lambda t: 0)]}, 2 * hour),
        # Below target for less than its 15 minutes: not yet.
        ("TransactHubLineThroughputBelowTarget", False, {"th_payment_lines_total": [(({}), [(t, 60 * t if t < 6600 else 60 * 6600 + 30 * (t - 6600)) for t in range(0, 7201, 30)])], "th_queue_in_flight": [gauge(lambda t: 3)]}, 2 * hour),
        # The acknowledgement: a file waiting 25 hours is overdue, 19 hours is due, 2 hours is neither.
        ("TransactHubAckOverdue", True, {"th_await_oldest_seconds": [gauge(lambda t: 25 * hour, labels=wps)]}, hour),
        ("TransactHubAckOverdue", False, {"th_await_oldest_seconds": [gauge(lambda t: 19 * hour, labels=wps)]}, hour),
        ("TransactHubAckDue", True, {"th_await_oldest_seconds": [gauge(lambda t: 19 * hour, labels=wps)]}, hour),
        ("TransactHubAckDue", False, {"th_await_oldest_seconds": [gauge(lambda t: 2 * hour, labels=wps)]}, hour),
        # Another scheme's waits are not WPS's.
        ("TransactHubAckOverdue", False, {"th_await_oldest_seconds": [gauge(lambda t: 2 * day, labels={**wps, "th_scheme": "uae.ipi"})]}, hour),
        # Holds: an expiry in the last hour fires; none does not; a hold open 20 hours of its day is due.
        ("TransactHubHoldExpired", True, {"th_holds_expired_total": [(({"th_tenant": "example-bank"}), [(t, 0 if t < 3000 else 1) for t in range(0, 3601, 30)])]}, 3600),
        ("TransactHubHoldExpired", False, {"th_holds_expired_total": [counter(0, end=3600, labels={"th_tenant": "example-bank"})]}, 3600),
        ("TransactHubHoldDue", True, {"th_holds_oldest_seconds": [gauge(lambda t: 20 * hour, labels={"th_tenant": "example-bank", "th_hold_kind": "approval"})]}, hour),
        ("TransactHubHoldDue", False, {"th_holds_oldest_seconds": [gauge(lambda t: 10 * hour, labels={"th_tenant": "example-bank", "th_hold_kind": "approval"})]}, hour),
    ]
    for name, want, series, at in cases:
        if name not in alerts:
            problems.append("no alert %s" % name)
            continue
        got = fires(alerts[name], series, at)
        if got != want:
            problems.append("%s %s on %s, and should %s" % (name, "fires" if got else "does not fire", sorted(series), "fire" if want else "not"))
    # The target not agreed yet has its recording rule and no alert (Issue I-143).
    if "TransactHubOpenPaymentsAboveTarget" in alerts:
        problems.append("an alert on open payments is generated, but no number is agreed (I-143)")
    for p in problems:
        print("FAIL", p)
    print("%d alert rule(s), %d case(s): %s" % (len(alerts), len(cases), "all pass" if not problems else "%d problem(s)" % len(problems)))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
