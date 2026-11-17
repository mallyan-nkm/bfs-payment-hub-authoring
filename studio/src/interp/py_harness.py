"""Python script sandbox harness (Part 8; C08).

Runs one script-behind with only the injected interface: ctx, ports, rules, params, clock,
ids, log. The studio talks to it in JSON lines on stdin and stdout. A port call or a rule
call is a request to the studio; the studio answers on stdin. No file system, network,
system clock, randomness or environment: imports are limited to pure modules, and the
builtins have no open, input, eval, exec or __import__ beyond the allow list.
"""
import json
import sys

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
