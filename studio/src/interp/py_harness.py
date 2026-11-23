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


# The builtins above are no wall: a script can climb from any object to the interpreter's modules through
# __class__ and __subclasses__. So the harness also installs an audit hook before the script runs (R-08-18), which
# CPython does not let Python code remove: from then on, no file is opened, no process, socket, library or new module
# is reached, and no further code is compiled. The allowed modules are imported first, so their own imports are done.
for _m in ALLOWED_MODULES:
    _real_import(_m)

_BLOCKED = ("open", "os.", "subprocess.", "socket.", "ctypes.", "shutil.", "glob.", "pty.", "fcntl.", "mmap.",
            "urllib.", "http.", "ftplib.", "smtplib.", "telnetlib.", "webbrowser.", "marshal.", "pickle.", "sqlite3.",
            "sys._getframe", "sys.setprofile", "sys.settrace", "code.__new__", "function.__new__", "cpython.",
            "_posixsubprocess.", "signal.", "resource.", "syslog.", "winreg.", "msvcrt.", "gc.get_referrers",
            "gc.get_referents", "gc.get_objects", "object.__setattr__", "object.__delattr__")
_lock = {"exec": 1}


def _audit(event, args):
    if event == "import":
        if str(args[0]).split(".")[0] not in ALLOWED_MODULES:
            raise RuntimeError("module %s is outside the injected interface" % args[0])
    elif event in ("exec", "compile"):
        if _lock["exec"] <= 0:
            raise RuntimeError("a script compiles no code at run time")
        _lock["exec"] -= 1
    elif any(event == b or (b.endswith(".") and event.startswith(b)) for b in _BLOCKED):
        raise RuntimeError("%s is outside the injected interface" % event)


def _landlock():
    """Refuse every file and every TCP connection to this process from now on, by the kernel (Linux Landlock; R-08-20).

    The ruleset handles every file access and, from ABI 4, TCP bind and connect, and grants none; from ABI 6 it also scopes
    abstract sockets and signals to this process. Files already open (stdin, stdout, stderr) stay usable. Returns the ABI, or
    0 where the kernel has no Landlock; the audit hook still refuses then.
    """
    try:
        import ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        syscall = libc.syscall
        syscall.restype = ctypes.c_long
        abi = syscall(444, None, ctypes.c_size_t(0), ctypes.c_uint32(1))  # landlock_create_ruleset(NULL, 0, VERSION)
        if abi < 1:
            return 0
        fs_bits = (1 << 13) - 1
        if abi >= 2:
            fs_bits |= 1 << 13
        if abi >= 3:
            fs_bits |= 1 << 14
        if abi >= 5:
            fs_bits |= 1 << 15
        fields = [("fs", ctypes.c_uint64)]
        if abi >= 4:
            fields.append(("net", ctypes.c_uint64))
        if abi >= 6:
            fields.append(("scoped", ctypes.c_uint64))

        class Attr(ctypes.Structure):
            _fields_ = fields

        attr = Attr()
        attr.fs = fs_bits
        if abi >= 4:
            attr.net = 3  # bind and connect over TCP
        if abi >= 6:
            attr.scoped = 3  # abstract unix sockets and signals
        fd = syscall(444, ctypes.byref(attr), ctypes.c_size_t(ctypes.sizeof(attr)), ctypes.c_uint32(0))
        if fd < 0:
            return 0
        if libc.prctl(38, 1, 0, 0, 0) != 0:  # PR_SET_NO_NEW_PRIVS
            return 0
        if syscall(446, ctypes.c_int(fd), ctypes.c_uint32(0)) != 0:  # landlock_restrict_self
            return 0
        return abi
    except Exception:  # noqa: BLE001 - no Landlock here: the audit hook still refuses
        return 0


def _confine(memory_mb):
    """The process limits a script runs under (R-08-20): memory, no file it may write, no core file, and Landlock."""
    try:
        import resource
        lim = int(memory_mb) * 1024 * 1024
        resource.setrlimit(resource.RLIMIT_AS, (lim, lim))
        resource.setrlimit(resource.RLIMIT_FSIZE, (0, 0))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    except Exception:  # noqa: BLE001 - a platform without these limits keeps the others
        pass
    return _landlock()


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
    memory = 256
    if "--memory-mb" in sys.argv:
        memory = int(sys.argv[sys.argv.index("--memory-mb") + 1])
    _confine(memory)
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
        code = compile(first["source"], first.get("file", "script.py"), "exec")
        sys.addaudithook(_audit)
        exec(code, g)
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
