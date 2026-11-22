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

/** Build a read-only-root view: reading a part of the context that is not declared fails the run. */
function guardReads(view: any, reads: string[], writes: string[] = []): any {
  const roots = new Set([...reads, ...writes].map((r) => r.replace('[]', '').split('.')[0]));
  return new Proxy(view, {
    get(target, prop, recv) {
      if (typeof prop === 'string' && !roots.has(prop) && !(prop in Object.prototype) && prop !== 'toJSON' && prop !== 'then') {
        throw new ContractViolation('R-03-07', `The script read ctx.${prop}, which is not in its declared reads`);
      }
      return Reflect.get(target, prop, recv);
    },
  });
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
  const logs: { msg: string; data?: any }[] = [];
  const nowIso = api.clock.now();
  const sandbox: any = {
    console: { log: (...a: any[]) => logs.push({ msg: a.map(String).join(' ') }) },
    JSON, Math: Object.create(Math, { random: { value: () => { throw new ContractViolation('L28', 'Math.random is not available to a script'); } } }),
    Number, String, Boolean, Array, Object, RegExp, Error, Map, Set, Promise, parseInt, parseFloat, isNaN, isFinite,
  };
  const ctxVm = vm.createContext(sandbox, { codeGeneration: { strings: false, wasm: false } });
  // A deterministic Date: `new Date()` and `Date.now()` give the virtual clock's time.
  vm.runInContext(
    `(() => { const D = Date; const now = D.parse(${JSON.stringify(nowIso)});
      class VDate extends D { constructor(...a) { if (a.length) super(...a); else super(now); } static now() { return now; } }
      globalThis.Date = VDate; })()`,
    ctxVm,
  );
  const mod: any = { exports: {} };
  const fn = compileScript(source, file).runInContext(ctxVm, { timeout: timeoutMs });
  fn(mod.exports, mod);
  const entry = mod.exports.run ?? mod.exports.default;
  if (typeof entry !== 'function') throw new ScriptError('ScriptShape', `${file} exports no run function`);
  const guarded = guardReads(view, reads, writes);
  const result = await entry({ ...api, ctx: guarded, log: { info: (msg: string, data?: any) => logs.push({ msg, data }) } });
  const outcome = typeof result === 'string' ? result : result?.outcome;
  const reasonCode = typeof result === 'object' ? result?.reasonCode : undefined;
  const changes = diff(before, view);
  for (const c of changes) {
    const p = c.path.replace(/\[\d+\]/g, '[]');
    if (!coveredByAny(writes, p) && !coveredByAny(writes, p.replace(/^items\[\]/, 'items[]'))) {
      throw new ContractViolation('R-03-07', `The script wrote ${c.path}, which is not in its declared writes`);
    }
  }
  return { outcome, reasonCode, changes, ctx: view, logs };
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
