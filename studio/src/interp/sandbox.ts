import * as fs from 'node:fs';
import * as path from 'node:path';
import { createRequire } from 'node:module';

// TypeScript is loaded only to transpile a .ts script; a deployed worker runs precompiled JavaScript.
const req: NodeRequire = typeof require !== 'undefined' ? require : createRequire(import.meta.url);
import { coveredByAny, diff } from '../core/paths.js';
import { acquireWorker, scriptWallMs } from './isolation.js';

// The TypeScript script sandbox (Part 8; C07). A script-behind sees only the injected
// interface: ctx, ports, rules, params, clock, ids, log. No network, file system, system
// clock, randomness or environment. It reads and writes only its declared paths.
//
// A script runs in a worker process of its own (R-08-20, Issue I-119, `script-worker.cjs`, `isolation.ts`): an empty
// environment, no file, no network, a heap limit, JSON over stdio. Inside it the context holds only its own realm's objects
// (R-08-18): the interface is rebuilt inside it from JSON, and the worker is reached through one function that takes and gives
// text. Every slice of the script, after an await too, runs under the timeout.

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

const transpiled = new Map<string, string>();

/** A script as the worker runs it: TypeScript transpiled to CommonJS here, in the studio, so the worker needs no compiler. */
export function transpileScript(source: string, file = 'script.ts'): string {
  const key = file + '\n' + source;
  let js = transpiled.get(key);
  if (js === undefined) {
    js = source;
    if (!file.endsWith('.js') && !file.endsWith('.cjs')) {
      const ts = req('typescript');
      js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, strict: false }, fileName: file }).outputText as string;
    }
    if (transpiled.size > 2000) transpiled.clear();
    transpiled.set(key, js);
  }
  return js;
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

/**
 * Run a TypeScript script-behind in a worker process of its own (R-08-20, Issue I-119): nothing of the studio is in that
 * process. The injected interface stays here; the script reaches it through JSON messages only. `partition` is the tenant the
 * run is for: a worker serves one tenant only.
 */
export async function runScript(
  source: string,
  file: string,
  view: any,
  reads: string[],
  writes: string[],
  api: Omit<ScriptApi, 'ctx'>,
  timeoutMs = 1000,
  partition = 'owner',
  hold: { enter(): void; leave(): void } = { enter() {}, leave() {} },
): Promise<ScriptResult> {
  const before = JSON.parse(JSON.stringify(view));
  const roots = [...new Set([...reads, ...writes].map((r) => r.replace('[]', '').split('.')[0]))];
  let js: string;
  try {
    js = transpileScript(source, file);
  } catch (e: any) {
    throw new ScriptError('ScriptError', `${file}: ${String(e?.message ?? e)}`);
  }
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
  // Business time does not move while the script waits for a worker or computes in one.
  hold.enter();
  const worker = await acquireWorker(partition);
  let retire = false;
  const text = await new Promise<string>((resolve, reject) => {
      let open = true;
      let pending = 0;
      let settles = 0;
      const wall = setTimeout(() => {
        open = false;
        worker.discard();
        reject(new ScriptError('Timeout', `${file} ran past ${scriptWallMs()} ms, its port calls included; its worker was stopped`));
      }, scriptWallMs());
      const end = (f: () => void) => {
        if (!open) return;
        open = false;
        clearTimeout(wall);
        hold.leave();
        f();
      };
      worker.onExit((why) => end(() => reject(new ScriptError(/memory/.test(why) ? 'ResourceLimit' : 'ScriptError', `${file}: ${why}`))));
      worker.onLine((line) => {
        if (!open) return;
        let m: any;
        try {
          m = JSON.parse(line);
        } catch {
          return end(() => {
            worker.discard();
            reject(new ScriptError('ScriptError', `${file}: the script worker wrote something that is not its protocol`));
          });
        }
        if (m.t === 'done') return end(() => ((retire = m.retire === true), resolve(String(m.text))));
        // The worker waits on its ports only, and has taken every answer sent: business time may move.
        if (m.t === 'wait') return void (m.consumed === settles && hold.leave());
        if (m.t !== 'call') return;
        // The one door from the script to the studio: JSON text in, JSON text (or a promise of it) out; it never throws.
        let value: any;
        try {
          const { fn, self } = lookup(api, String(m.path));
          if (typeof fn !== 'function') return worker.send({ t: 'sync', text: JSON.stringify({ error: { name: 'TypeError', message: `${m.path} is not a function of the injected interface` } }) });
          value = fn.apply(self, JSON.parse(String(m.args)));
        } catch (e) {
          return worker.send({ t: 'sync', text: asError(e) });
        }
        if (value && typeof value.then === 'function') {
          const id = ++pending;
          worker.send({ t: 'pending', id });
          Promise.resolve(value).then(
            (v) => open && (hold.enter(), settles++, worker.send({ t: 'settle', id, text: answer(v) })),
            (e) => open && (hold.enter(), settles++, worker.send({ t: 'settle', id, text: asError(e) })),
          );
          return;
        }
        worker.send({ t: 'sync', text: answer(value) });
      });
      worker.send({ t: 'run', js, file, shape: JSON.stringify(shapeOf(api)), view: JSON.stringify(view), roots: JSON.stringify(roots), now: api.clock.now(), timeoutMs });
  });
  // A worker whose run was stopped by its timeout takes no other run.
  if (retire) worker.discard();
  else worker.release();
  const r = JSON.parse(text);
  if (r.failure) {
    const f = r.failure;
    if (typeof f.hostError === 'number' && f.hostError >= 0 && f.hostError < thrown.length) throw thrown[f.hostError];
    if (f.name === 'ScriptShape') throw new ScriptError('ScriptShape', `${file} ${f.message}`);
    if (f.name === 'Timeout') throw new ScriptError('Timeout', f.message);
    if (f.name === 'CompileError') throw new ScriptError('ScriptError', `${file}: ${f.message}`);
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

  /** The same folders over other scripts in memory: one tenant's (R-11-48). */
  over(memory: () => Iterable<InMemoryScript>): ScriptRegistry {
    return new ScriptRegistry(this.dirs, memory);
  }

  /** The same registry, with more scripts searched first: an import's, before it saves them. */
  withFirst(scripts: InMemoryScript[]): ScriptRegistry {
    const outer = this;
    return new (class extends ScriptRegistry {
      over(memory: () => Iterable<InMemoryScript>) {
        return outer.over(memory).withFirst(scripts);
      }
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
