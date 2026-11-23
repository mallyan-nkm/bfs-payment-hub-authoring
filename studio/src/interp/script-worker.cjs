'use strict';
// The TypeScript script-behind worker (R-08-20, Issue I-119). A process of its own with nothing of the studio in it: the
// studio starts it with an empty environment, under Node's permission model with no grant (no file read or write, no child
// process, no worker thread, no native addon, no WASI), in a network namespace of its own where the platform has one, and
// with a heap limit. It holds no credential, no content and no module of the studio: only this file, `node:vm` and `node:fs`
// on its standard input and output. It talks JSON lines on stdin and stdout, one script at a time:
//
//   studio -> worker  {"t":"run", js, file, shape, view, roots, now, timeoutMs}
//   worker -> studio  {"t":"call", path, args}             a function of the injected interface
//   studio -> worker  {"t":"sync", text}                    its answer at once (JSON text)
//                     {"t":"pending", id} ... {"t":"settle", id, text}   an answer that comes later
//   worker -> studio  {"t":"done", text}                    the run's end (JSON text)
//
// Inside the worker the script still runs in a context of its own that holds no object of the worker's realm (R-08-18), and
// every slice of it runs under its timeout.
const vm = require('node:vm');
const fs = require('node:fs');

/**
 * The code that runs inside a script's context, before the script: it builds the injected interface out of the context's own
 * objects (R-08-18). Nothing of the worker's realm reaches the script, so `x.constructor.constructor` never gives the worker's
 * Function. The worker is reached through `call`, which takes and gives JSON text only and never throws, and the run ends
 * through `done`, which takes JSON text. Both live in this closure only.
 */
const BRIDGE = `(function (call, done, shapeJson, viewJson, rootsJson, nowIso) {
  'use strict';
  const J = JSON, O = Object, P = Promise, E = Error, A = Array, R = Reflect;
  const then = P.prototype.then;
  const decode = (s) => (s === undefined || s === 'undefined' ? undefined : J.parse(s));
  function raise(err) {
    const e = new E(String(err.message));
    e.name = String(err.name || 'Error');
    if (err.code !== undefined) e.code = err.code;
    if (err.hostError !== undefined) O.defineProperty(e, '__hostError', { value: err.hostError });
    throw e;
  }
  function answer(text) {
    const r = decode(text);
    if (r && r.error) raise(r.error);
    return r ? r.value : undefined;
  }
  function invoke(path, args) {
    if (path === 'rules.money.sum' && typeof args[1] === 'function') args = [A.isArray(args[0]) ? args[0].map(args[1]) : []];
    // An answer that comes later is handed back through \`reply\`, a function of this context: the script awaits a promise
    // of its own realm, never the worker's.
    let resolve;
    const later = new P((res) => { resolve = res; });
    const r = call(path, J.stringify(args, (k, v) => (typeof v === 'function' ? undefined : v)), (text) => resolve(text));
    if (typeof r === 'string') return answer(r);
    return R.apply(then, later, [answer]);
  }
  function build(shape, path) {
    if (shape && typeof shape === 'object' && !A.isArray(shape)) {
      if (shape.__fn === true) return (...args) => invoke(path, args);
      const o = {};
      for (const k of O.keys(shape)) o[k] = build(shape[k], path ? path + '.' + k : k);
      return O.freeze(o);
    }
    return shape;
  }
  // A deterministic Date and no randomness (C07-S3).
  const D = Date, now = D.parse(nowIso);
  class VDate extends D { constructor(...a) { if (a.length) super(...a); else super(now); } static now() { return now; } }
  globalThis.Date = VDate;
  globalThis.Math = O.create(Math, { random: { value: () => { const e = new E('Math.random is not available to a script'); e.name = 'ContractViolation'; e.code = 'L28'; throw e; } } });
  const logs = [];
  globalThis.console = { log: (...a) => { logs.push({ msg: a.map(String).join(' ') }); } };
  // Reading a part of the context that is not declared fails the run (R-03-07).
  const roots = new Set(decode(rootsJson));
  const view = decode(viewJson);
  const guarded = new Proxy(view, {
    get(target, prop, recv) {
      if (typeof prop === 'string' && !roots.has(prop) && !(prop in O.prototype) && prop !== 'toJSON' && prop !== 'then') {
        const e = new E('The script read ctx.' + prop + ', which is not in its declared reads');
        e.name = 'ContractViolation'; e.code = 'R-03-07'; throw e;
      }
      return R.get(target, prop, recv);
    },
  });
  const api = build(decode(shapeJson), '');
  const finish = (r) => done(J.stringify(r));
  const failure = (e) => {
    let f;
    try { f = { name: String(e && e.name), message: String(e && e.message !== undefined ? e.message : e), code: e && e.code !== undefined ? String(e.code) : undefined, hostError: e && e.__hostError }; }
    catch (x) { f = { name: 'ScriptError', message: 'The script threw a value that cannot be read' }; }
    finish({ failure: f });
  };
  return (moduleFn) => {
    const m = { exports: {} };
    try { moduleFn(m.exports, m); } catch (e) { return failure(e); }
    const entry = m.exports.run || m.exports.default;
    if (typeof entry !== 'function') return finish({ failure: { name: 'ScriptShape', message: 'exports no run function' } });
    let p;
    try { p = P.resolve(entry({ ...api, ctx: guarded, log: { info: (msg, data) => { logs.push({ msg: String(msg), data: data === undefined ? undefined : J.parse(J.stringify(data)) }); } } })); }
    catch (e) { return failure(e); }
    R.apply(then, p, [(result) => {
      try {
        const outcome = typeof result === 'string' ? result : result && result.outcome;
        const reasonCode = result && typeof result === 'object' ? result.reasonCode : undefined;
        finish({ outcome: outcome === undefined ? undefined : String(outcome), reasonCode: reasonCode === undefined ? undefined : String(reasonCode), ctx: view, logs });
      } catch (e) { failure(e); }
    }, failure]);
  };
})`;

const bridge = new vm.Script(BRIDGE, { filename: 'th-script-bridge.js' });

// ------------------------------------------------------------------ JSON lines on stdin and stdout, read synchronously

let pendingText = '';
const chunk = Buffer.alloc(1 << 16);
const idle = new Int32Array(new SharedArrayBuffer(4));

function readLine() {
  for (;;) {
    const i = pendingText.indexOf('\n');
    if (i >= 0) {
      const line = pendingText.slice(0, i);
      pendingText = pendingText.slice(i + 1);
      return line;
    }
    let n;
    try {
      n = fs.readSync(0, chunk, 0, chunk.length, null);
    } catch (e) {
      if (e && e.code === 'EAGAIN') { Atomics.wait(idle, 0, 0, 5); continue; }
      throw e;
    }
    if (n === 0) process.exit(0); // the studio closed the pipe: nothing more to run
    pendingText += chunk.toString('utf8', 0, n);
  }
}

function send(msg) {
  const text = JSON.stringify(msg) + '\n';
  let off = 0;
  const buf = Buffer.from(text, 'utf8');
  while (off < buf.length) {
    try {
      off += fs.writeSync(1, buf, off, buf.length - off);
    } catch (e) {
      if (e && e.code === 'EAGAIN') { Atomics.wait(idle, 0, 0, 5); continue; }
      throw e;
    }
  }
}

// ------------------------------------------------------------------ one run

function runOne(job) {
  const timeout = { timeout: job.timeoutMs };
  const replies = new Map();
  const settled = [];
  let consumed = 0;
  let finished;
  /** The one door from the script to the studio: JSON text in, JSON text (or nothing, for an answer that comes later) out. */
  const call = (path, argsJson, reply) => {
    send({ t: 'call', path: String(path), args: String(argsJson) });
    for (;;) {
      const m = JSON.parse(readLine());
      if (m.t === 'sync') return String(m.text);
      if (m.t === 'pending') { replies.set(m.id, reply); return undefined; }
      if (m.t === 'settle') settled.push(m);
    }
  };
  // A fresh context with its own intrinsics; strings and WebAssembly cannot be compiled to code in it. Its microtasks run
  // inside each evaluation, so the timeout bounds every slice of the script, the ones after an await too.
  const ctxVm = vm.createContext({}, { codeGeneration: { strings: false, wasm: false }, microtaskMode: 'afterEvaluate' });
  const timedOut = () => ({ failure: { name: 'Timeout', message: `The script ran past ${job.timeoutMs} ms without returning to its interface` } });
  try {
    const start = bridge.runInContext(ctxVm, timeout)(call, (t) => { if (finished === undefined) finished = String(t); }, job.shape, job.view, job.roots, job.now);
    let moduleFn;
    try {
      moduleFn = new vm.Script(`(function (exports, module) { ${job.js}\n })`, { filename: job.file }).runInContext(ctxVm, timeout);
    } catch (e) {
      if (/Script execution timed out/.test(String(e && e.message))) return { failure: { name: 'Timeout', message: `${job.file} ran past ${job.timeoutMs} ms` }, retire: true };
      return { failure: { name: 'CompileError', message: String(e && e.message) } };
    }
    ctxVm.__th_start = start;
    ctxVm.__th_module = moduleFn;
    // The script's top level and its run start inside one timed evaluation; the two names are gone before it runs.
    vm.runInContext('(() => { const s = __th_start, f = __th_module; delete globalThis.__th_start; delete globalThis.__th_module; s(f); })()', ctxVm, timeout);
  } catch (e) {
    if (/Script execution timed out/.test(String(e && e.message))) return { failure: { name: 'Timeout', message: `${job.file} ran past ${job.timeoutMs} ms` }, retire: true };
    return { failure: { name: 'ScriptError', message: `${job.file}: ${String(e && e.message)}` } };
  }
  while (finished === undefined) {
    if (!replies.size && !settled.length) return { failure: { name: 'ScriptError', message: `${job.file} awaited something its interface never answers` } };
    // Waiting on the studio only: its business time may move until an answer comes. The count of answers taken tells the
    // studio whether one it sent is still on its way here.
    if (!settled.length) send({ t: 'wait', consumed });
    const m = settled.length ? settled.shift() : JSON.parse(readLine());
    if (m.t !== 'settle') continue;
    consumed++;
    const reply = replies.get(m.id);
    if (!reply) continue;
    replies.delete(m.id);
    reply(String(m.text));
    try {
      vm.runInContext('0', ctxVm, timeout);
    } catch {
      return { ...timedOut(), retire: true };
    }
  }
  return { text: finished };
}

for (;;) {
  const job = JSON.parse(readLine());
  if (job.t !== 'run') continue;
  const r = runOne(job);
  send({ t: 'done', text: r.text !== undefined ? r.text : JSON.stringify({ failure: r.failure }), retire: r.retire === true });
  // A run that was stopped by its timeout leaves its context half way: this worker takes no other run.
  if (r.retire) process.exit(0);
}
