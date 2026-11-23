"""A stand-in for a Python script that has climbed past the audit hook (R-08-20, Issue I-119).

It runs as the harness runs, with the same command, flags and environment, loads the harness's own confinement, applies it as
the harness does before it reads a script, and tries what such a script would. It reports what it reached on stdout.
"""
import json
import os
import socket
import sys

job = json.loads(sys.stdin.readline())
g = {"__name__": "harness"}
with open(job["harness"]) as f:
    exec(compile(f.read(), job["harness"], "exec"), g)
abi = g["_confine"](256)

out = {"landlock": abi, "env": sorted(os.environ.keys())}


def attempt(name, f):
    try:
        out[name] = {"reached": True, "value": f()}
    except Exception as e:  # noqa: BLE001
        out[name] = {"reached": False, "error": type(e).__name__}


def read(p, mode="r"):
    with open(p, mode) as f:
        return len(f.read())


def write(p):
    with open(p, "w") as f:
        return f.write("x")


def connect():
    s = socket.create_connection(("127.0.0.1", job["port"]), timeout=3)
    s.close()
    return "connected"


attempt("readStudioFile", lambda: read(job["studioFile"]))
attempt("readParentEnviron", lambda: read("/proc/%s/environ" % job["ppid"], "rb"))
attempt("writeFile", lambda: write("/tmp/th-escape-probe-py.txt"))
attempt("listRoot", lambda: len(os.listdir("/")))
attempt("network", connect)
sys.stdout.write(json.dumps(out) + "\n")
