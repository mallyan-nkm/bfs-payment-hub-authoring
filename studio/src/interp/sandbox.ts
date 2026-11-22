import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vm from 'node:vm';
import { createRequire } from 'node:module';

// TypeScript is loaded only to transpile a .ts script; a deployed worker runs precompiled JavaScript.
const req: NodeRequire = typeof require !== 'undefined' ? require : createRequire(import.meta.url);
import { coveredByAny, diff } from '../core/paths.js';

// The TypeScript script sandbox (Part 8; C07). A script-behind sees only the injected
// interface: ctx, ports, rules, params, clock, ids, log. No network, file system, system
// clock, randomness or environment. It reads and writes only its declared paths.
//
// The context holds only its own realm's objects (R-08-18): the interface is rebuilt inside it from JSON, and the host is
// reached through one function that takes and gives text. A script cannot reach the host's Function, process or require
// through a constructor chain, and every slice of it, after an await too, runs under the timeout. It is still not a
// security boundary against hostile code (D14, Issue I-119): a shared installation runs bank scripts in a worker of their own.

export class ContractViolation extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

export class ScriptError extends Error {
  constructor(public errorName: string, message: string, public retryable?: boolean) {
    super(message);
  }
}

export interface ScriptApi {
  ctx: any;
  ports: any;
  rules: any;
  params: Record<string, any>;
  clock: { now(): string; today(): string };
  ids: { next(prefix?: string): string };
  log: { info(msg: string, data?: any): void };
}

export interface ScriptResult {
  outcome: string;
  reasonCode?: string;
  changes: { path: string; before: any; after: any }[];
  ctx: any;
  logs: { msg: string; data?: any }[];
}

const compiled = new Map<string, vm.Script>();

export function compileScript(source: string, file = 'script.ts'): vm.Script {
  const key = file + '\n' + source;
  let s = compiled.get(key);
  if (!s) {
    let js = source;
    if (!file.endsWith('.js') && !file.endsWith('.cjs')) {
      const ts = req('typescript');
      js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, strict: false }, fileName: file }).outputText;
    }
    s = new vm.Script(`(function (exports, module) { ${js}\n })`, { filename: file });
    compiled.set(key, s);
  }
  return s;
}

const FORBIDDEN = ['require', 'process', 'fetch', 'XMLHttpRequest', 'WebSocket', 'setTimeout', 'setInterval', 'setImmediate', 'queueMicrotask', 'Buffer', 'globalThis.process'];

/** Static determinism check (L28, C07-S3): a script may not name the forbidden globals. */
export function lintScript(source: string): string[] {
  const out: string[] = [];
  const bare = source.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/'[^']*'|"[^"]*"|`[^`]*`/g, '""');
  for (const f of ['require', 'process', 'fetch', 'XMLHttpRequest', 'WebSocket', 'setTimeout', 'setInterval', 'import(', 'Math.random', 'Date.now', 'new Date()', 'crypto', 'eval', 'Function(']) {
    const re = new RegExp('(^|[^A-Za-z0-9_.])' + f.replace(/[.()]/g, (c) => '\\' + c) + (f.endsWith('(') || f.endsWith(')') ? '' : '\\b'));
    if (re.test(bare)) out.push(`Script uses ${f.replace('(', '')}, which is outside the injected interface`);
  }
  return out;
}

/**
 * The code that runs inside a script's context, before the script: it builds the injected interface out of the context's own
 * objects (R-08-18). Nothing of the host's realm reaches the script: not an object, a function, a promise or an error, so
 * `x.constructor.constructor` never gives the host's Function. The host is reached through `call`, which takes and gives
 * JSON text only and never throws, and the run ends through `done`, which takes JSON text. Both live in this closure only.
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
    // of its own realm, never the host's.
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

const bridges = new Map<string, vm.Script>();
function bridgeScript(): vm.Script {
  let s = bridges.get('b');
  if (!s) bridges.set('b', (s = new vm.Script(BRIDGE, { filename: 'th-script-bridge.js' })));
  return s;
}

/** The shape of the injected interface: its data as it is, each function as a marker the context turns into a call. */
function shapeOf(api: any): any {
  if (typeof api === 'function') return { __fn: true };
  if (api && typeof api === 'object' && !Array.isArray(api)) return Object.fromEntries(Object.entries(api).map(([k, v]) => [k, shapeOf(v)]));
  return api;
}

function lookup(api: any, path: string): any {
  let cur = api;
  let self: any;
  for (const k of path.split('.')) {
    self = cur;
    cur = cur?.[k];
  }
  return { fn: cur, self };
}

export async function runScript(
  source: string,
  file: string,
  view: any,
  reads: string[],
  writes: string[],
  api: Omit<ScriptApi, 'ctx'>,
  timeoutMs = 1000,
): Promise<ScriptResult> {
  const before = JSON.parse(JSON.stringify(view));
  const roots = [...new Set([...reads, ...writes].map((r) => r.replace('[]', '').split('.')[0]))];
  // Errors the host's interface threw, kept here by number: the script gets their message, the run gets them back as they were.
  const thrown: unknown[] = [];
  const asError = (e: any) => {
    const hostError = thrown.push(e) - 1;
    try {
      return JSON.stringify({ error: { name: String(e?.name ?? 'Error'), message: String(e?.message ?? e), code: typeof e?.code === 'string' ? e.code : undefined, hostError } });
    } catch {
      return JSON.stringify({ error: { name: 'Error', message: 'The interface failed', hostError } });
    }
  };
  const answer = (value: unknown) => {
    try {
      return JSON.stringify({ value });
    } catch (e) {
      return asError(e);
    }
  };
  let drain: () => void = () => {};
  /** The one door from the script to the host: JSON text in, JSON text (or a promise of it) out; it never throws. */
  const call = (path: string, argsJson: string, reply: (text: string) => void): string | undefined => {
    try {
      const { fn, self } = lookup(api, path);
      if (typeof fn !== 'function') return JSON.stringify({ error: { name: 'TypeError', message: `${path} is not a function of the injected interface` } });
      const value = fn.apply(self, JSON.parse(argsJson));
      if (value && typeof (value as any).then === 'function') {
        // Answered with text only, through the context's own function, and the context's microtasks run at once under the
        // timeout: the script continues in the same turn of the event loop, as an await on the host would.
        void Promise.resolve(value).then(
          (v) => { reply(answer(v)); drain(); },
          (e) => { reply(asError(e)); drain(); },
        );
        return undefined;
      }
      return answer(value);
    } catch (e) {
      return asError(e);
    }
  };
  let settle!: (text: string) => void;
  const finished = new Promise<string>((r) => (settle = r));
  // A fresh context with its own intrinsics; strings and WebAssembly cannot be compiled to code in it. Its microtasks run
  // inside each evaluation, so the timeout bounds every slice of the script, the ones after an await too.
  const ctxVm = vm.createContext({}, { codeGeneration: { strings: false, wasm: false }, microtaskMode: 'afterEvaluate' });
  const timeout = { timeout: timeoutMs };
  drain = () => {
    try {
      vm.runInContext('0', ctxVm, timeout);
    } catch {
      settle(JSON.stringify({ failure: { name: 'Timeout', message: `The script ran past ${timeoutMs} ms without returning to its interface` } }));
    }
  };
  try {
    const start = bridgeScript().runInContext(ctxVm, timeout)(call, (t: string) => settle(String(t)), JSON.stringify(shapeOf(api)), JSON.stringify(view), JSON.stringify(roots), api.clock.now());
    (ctxVm as any).__th_start = start;
    (ctxVm as any).__th_module = compileScript(source, file).runInContext(ctxVm, timeout);
    // The script's top level and its run start inside one timed evaluation; the two names are gone before it runs.
    vm.runInContext('(() => { const s = __th_start, f = __th_module; delete globalThis.__th_start; delete globalThis.__th_module; s(f); })()', ctxVm, timeout);
  } catch (e: any) {
    if (/Script execution timed out/.test(String(e?.message))) throw new ScriptError('Timeout', `${file} ran past ${timeoutMs} ms`);
    throw new ScriptError('ScriptError', `${file}: ${String(e?.message ?? e)}`);
  }
  const r = JSON.parse(await finished);
  if (r.failure) {
    const f = r.failure;
    if (typeof f.hostError === 'number' && f.hostError >= 0 && f.hostError < thrown.length) throw thrown[f.hostError];
    if (f.name === 'ScriptShape') throw new ScriptError('ScriptShape', `${file} ${f.message}`);
    if (f.name === 'Timeout') throw new ScriptError('Timeout', f.message);
    if (f.name === 'ContractViolation') throw new ContractViolation(f.code ?? 'R-03-07', f.message);
    const e = new Error(f.message);
    e.name = f.name;
    throw e;
  }
  const out = r.ctx;
  const changes = diff(before, out);
  for (const c of changes) {
    const p = c.path.replace(/\[\d+\]/g, '[]');
    if (!coveredByAny(writes, p) && !coveredByAny(writes, p.replace(/^items\[\]/, 'items[]'))) {
      throw new ContractViolation('R-03-07', `The script wrote ${c.path}, which is not in its declared writes`);
    }
  }
  return { outcome: r.outcome, reasonCode: r.reasonCode, changes, ctx: out, logs: r.logs ?? [] };
}

export interface InMemoryScript { name: string; language: 'typescript' | 'python'; source: string; file: string }

/**
 * Script-behinds, searched in the folders given (a deployed worker's package), then in memory: in the
 * studio every script is a file on the content branch, the factory's (seeded from studio/scripts) and a
 * designer's (R-11-24, R-11-25).
 */
export class ScriptRegistry {
  constructor(private dirs: string[], private memory: () => Iterable<InMemoryScript> = () => []) {}

  /**
   * The script-behind of an activity. With a release, a script re-released with it (`<name>@<release>`) comes first, then the
   * script of the name: a script that changes is published beside the earlier one, never over it (R-08-10, R-11-36).
   */
  find(name: string, release?: string): { file: string; source: string; language: 'typescript' | 'python' } | undefined {
    if (release && !name.includes('@')) {
      const own = this.find(`${name}@${release}`);
      if (own) return own;
    }
    for (const d of this.dirs) {
      for (const [ext, language] of [['.ts', 'typescript'], ['.js', 'typescript'], ['.py', 'python']] as const) {
        const f = path.join(d, `${name}${ext}`);
        if (fs.existsSync(f)) return { file: f, source: fs.readFileSync(f, 'utf8'), language };
      }
    }
    for (const s of this.memory()) if (s.name === name) return { file: s.file, source: s.source, language: s.language };
    return undefined;
  }

  /** The same registry, with more scripts searched first: an import's, before it saves them. */
  withFirst(scripts: InMemoryScript[]): ScriptRegistry {
    const outer = this;
    return new (class extends ScriptRegistry {
      find(name: string, release?: string) {
        const s = (release && scripts.find((x) => x.name === `${name}@${release}`)) || scripts.find((x) => x.name === name);
        return s ? { file: s.file, source: s.source, language: s.language } : outer.find(name, release);
      }
      list() {
        return [...new Set([...scripts.map((x) => `${x.name}.${x.language === 'python' ? 'py' : 'ts'}`), ...outer.list()])].sort();
      }
    })([]);
  }

  list(): string[] {
    const out: string[] = [];
    for (const d of this.dirs) if (fs.existsSync(d)) out.push(...fs.readdirSync(d).filter((f) => f.endsWith('.ts') || f.endsWith('.js') || f.endsWith('.py')));
    for (const s of this.memory()) out.push(`${s.name}.${s.language === 'python' ? 'py' : 'ts'}`);
    return [...new Set(out)].sort();
  }
}

export { FORBIDDEN };
