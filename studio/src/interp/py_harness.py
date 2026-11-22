"""Python script sandbox harness (Part 8; C08).

Runs one script-behind with only the injected interface: ctx, ports, rules, params, clock,
ids, log. The studio talks to it in JSON lines on stdin and stdout. A port call or a rule
call is a request to the studio; the studio answers on stdin. No file system, network,
system clock, randomness or environment: imports are limited to pure modules, and the
builtins have no open, input, eval, exec or __import__ beyond the allow list.
"""
import decimal
import json
import sys

_D = decimal.Decimal
_CTX = decimal.Context(prec=1000, rounding=decimal.ROUND_HALF_UP)


def _dec(v):
    """A value as an exact decimal, read by its text: a float by its shortest repr, never its binary value (R-02-17)."""
    if isinstance(v, bool):
        return _D(int(v))
    if isinstance(v, (int, _D)):
        return _D(v)
    if isinstance(v, float):
        return _D(repr(v))
    if isinstance(v, str):
        try:
            return _D(v.strip())
        except decimal.InvalidOperation:
            return _D(0)
    return _D(0)


def _text(d):
    """The canonical text: no exponent, no trailing zeros, no "-0"."""
    s = format(d, "f")
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def _value(d):
    """A number when it has at most 15 significant digits, else the decimal string, as the rule language gives it."""
    if d is None:
        return None
    s = _text(d)
    if len(s.lstrip("-").replace(".", "").lstrip("0")) > 15:
        return s
    return float(s) if "." in s else int(s)


class Money:
    """Exact decimal helpers on api.rules.money, the same arithmetic as the rule language (R-04-23)."""

    def add(self, *xs):
        acc = _D(0)
        for x in xs:
            acc = _CTX.add(acc, _dec(x))
        return _value(acc)

    def sub(self, a, b):
        return _value(_CTX.subtract(_dec(a), _dec(b)))

    def mul(self, a, b):
        return _value(_CTX.multiply(_dec(a), _dec(b)))

    def div(self, a, b):
        if _dec(b) == 0:
            return None
        return _value(_CTX.divide(_dec(a), _dec(b)).quantize(_D(1).scaleb(-20), rounding=decimal.ROUND_HALF_UP, context=_CTX))

    def sum(self, xs, f=None):
        return self.add(*[(f(x) if f else x) for x in (xs or [])])

    def round2(self, x):
        return _value(_dec(x).quantize(_D("0.01"), rounding=decimal.ROUND_HALF_UP, context=_CTX))

    def cmp(self, a, b):
        x, y = _dec(a), _dec(b)
        return -1 if x < y else 1 if x > y else 0

    def eq(self, a, b):
        return _dec(a) == _dec(b)

    def str(self, x):
        return _text(_dec(x))


ALLOWED_MODULES = {"re", "math", "decimal", "json", "itertools", "functools", "collections", "string"}
_real_import = __import__


def _import(name, globals=None, locals=None, fromlist=(), level=0):
    if name.split(".")[0] not in ALLOWED_MODULES:
        raise ImportError("module %s is outside the injected interface" % name)
    return _real_import(name, globals, locals, fromlist, level)


SAFE_BUILTINS = {k: __builtins__[k] if isinstance(__builtins__, dict) else getattr(__builtins__, k) for k in (
    "abs", "all", "any", "bool", "dict", "enumerate", "filter", "float", "int", "isinstance", "len", "list", "map",
    "max", "min", "range", "reversed", "round", "set", "sorted", "str", "sum", "tuple", "zip", "Exception",
    "ValueError", "KeyError", "TypeError", "None", "True", "False", "repr", "hasattr", "getattr", "iter", "next")
    if (k in __builtins__ if isinstance(__builtins__, dict) else hasattr(__builtins__, k))}
SAFE_BUILTINS["__import__"] = _import

_out = sys.stdout
_in = sys.stdin


def _send(msg):
    _out.write(json.dumps(msg) + "\n")
    _out.flush()


def _call(kind, **kw):
    _send({"call": kind, **kw})
    line = _in.readline()
    if not line:
        raise SystemExit(3)
    reply = json.loads(line)
    if "error" in reply:
        raise RuntimeError(reply["error"])
    return reply.get("value")


class ContractViolation(Exception):
    pass


class GuardedCtx(dict):
    """The context view. Reading a part that is not declared fails the run (R-03-07)."""

    def __init__(self, data, roots):
        super().__init__(data)
        self._roots = roots

    def __getitem__(self, k):
        if k not in self._roots:
            raise ContractViolation("The script read ctx[%r], which is not in its declared reads" % k)
        return super().__getitem__(k)

    def get(self, k, default=None):
        if k not in self._roots:
            raise ContractViolation("The script read ctx[%r], which is not in its declared reads" % k)
        return super().get(k, default)


class Api:
    pass


def main():
    first = json.loads(_in.readline())
    roots = set(first["roots"])
    ctx = GuardedCtx(first["view"], roots)
    logs = []
    api = Api()
    api.ctx = ctx
    api.params = first.get("params", {})

    class Ports:
        def request(self, port):
            return _call("request", port=port)

        def send(self, port):
            return _call("send", port=port)

    class Rules:
        money = Money()

        def run(self, name, facts=None):
            return _call("rules", name=name, facts=facts)

    class Clock:
        def now(self):
            return first["now"]

        def today(self):
            return first["now"][:10]

    counter = {"n": 0}

    class Ids:
        def next(self, prefix="id"):
            counter["n"] += 1
            return "%s-%s-%d" % (prefix, first.get("idBase", "x"), counter["n"])

    class Log:
        def info(self, msg, data=None):
            logs.append({"msg": msg, "data": data})

    api.ports, api.rules, api.clock, api.ids, api.log = Ports(), Rules(), Clock(), Ids(), Log()
    g = {"__builtins__": SAFE_BUILTINS, "__name__": "script"}
    try:
        exec(compile(first["source"], first.get("file", "script.py"), "exec"), g)
        if "run" not in g:
            raise ContractViolation("the script defines no run function")
        result = g["run"](api)
        outcome = result if isinstance(result, str) else (result or {}).get("outcome")
        reason = None if isinstance(result, str) else (result or {}).get("reasonCode")
        _send({"result": {"outcome": outcome, "reasonCode": reason, "ctx": dict(ctx), "logs": logs}})
    except ContractViolation as e:
        _send({"failure": {"code": "R-03-07", "message": str(e)}})
    except Exception as e:  # noqa: BLE001 - a script error is reported, not raised
        _send({"failure": {"code": "ScriptError", "message": "%s: %s" % (type(e).__name__, e)}})


if __name__ == "__main__":
    main()
