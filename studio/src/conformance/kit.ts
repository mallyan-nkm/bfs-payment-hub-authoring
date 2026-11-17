import * as fs from 'node:fs';
import * as path from 'node:path';
import { clone, contentHash } from '../core/canonical.js';
import { addDuration, DEFAULT_CALENDAR, durationMs } from '../core/duration.js';
import { getPath } from '../core/paths.js';
import { parseYaml, parseYamlAll } from '../core/yaml.js';
import { Run, runInbound, runWorkflow, startInstance, type Instance, type RunConfig } from '../interp/interpreter.js';
import type { StudioEnv } from '../env.js';

// The conformance kit runner (C23). It runs every scenario of spec/conformance/kit.yaml on the
// studio interpreter and compares the business trace (R-C-03, R-C-04). The adapter here is the
// studio's; an engine adapter replaces runScenario and keeps the comparison.

export interface KitResult {
  id: string;
  construct: string;
  title: string;
  passed: boolean;
  expected: string[];
  actual: string[];
  checks: { what: string; expected: any; actual: any; passed: boolean }[];
  error?: string;
  notes?: string[];
  elapsed?: number;
}

export interface KitContent {
  manifest: any;
  library: any[];
  scenarios: any;
}

export function loadKit(env: StudioEnv): KitContent {
  const manifest = parseYaml(fs.readFileSync(path.join(env.specRoot, 'conformance', 'kit.yaml'), 'utf8'));
  const library = parseYamlAll(fs.readFileSync(path.join(env.studioRoot, 'conformance', 'library.yaml'), 'utf8')).filter(Boolean);
  const scenarios = parseYaml(fs.readFileSync(path.join(env.studioRoot, 'conformance', 'scenarios.yaml'), 'utf8'));
  return { manifest, library, scenarios };
}

function deepMerge(a: any, b: any): any {
  if (!b) return clone(a);
  if (!a || typeof a !== 'object' || Array.isArray(a) || typeof b !== 'object' || Array.isArray(b)) return clone(b);
  const out = clone(a);
  for (const [k, v] of Object.entries(b)) out[k] = deepMerge(out[k], v);
  return out;
}

/** Build the scenario's workflow documents and the run configuration. */
export function buildScenario(env: StudioEnv, kit: KitContent, sc: any) {
  const name = sc.workflowName ?? (sc.handler ? 'ck.kit.handler.answer.v1' : sc.platform ? 'ck.p.wf.platform.v1' : `ck.kit.wf.${sc.id}.v1`);
  const docs: Record<string, any> = {};
  for (const d of kit.library) docs[`${d.name ?? d.scope}@${d.release}`] = d;
  for (const d of sc.documents ?? []) docs[`${d.name}@${d.release}`] = d;
  const base: any = {
    kind: 'workflow',
    wfKind: sc.handler ? 'handler' : 'platform',
    name,
    release: '1.0.0',
    dsl: '1.0.0',
    title: `Conformance scenario ${sc.id}`,
    ...(sc.handler ? {} : { entry: { reads: ['header', 'payment'] } }),
    exit: sc.workflow.exit ?? { outcomes: collectEnds(sc.workflow) },
    ...(sc.platform ? { slots: [{ name: 'scheme-leg', contract: { reads: ['header', 'facts.route'], writes: ['status'], outcomes: ['settled', 'rejected', 'pending'], primaryOutcome: 'settled' } }] } : {}),
    ...sc.workflow,
  };
  if (!base.exit.outcomes.length) base.exit.outcomes = ['done'];
  const releases = sc.releases ?? [{ release: '1.0.0' }];
  for (const r of releases) {
    const d = clone({ ...base, release: r.release });
    if (r.pins) d.artefacts = Object.entries<string>(r.pins).map(([n, rel]) => ({ name: n, release: rel, hash: contentHash(docs[`${n}@${rel}`] ?? {}) }));
    docs[`${name}@${r.release}`] = d;
  }
  const evaluationDate = sc.evaluationDate ?? kit.scenarios.defaults.evaluationDate;
  let ctx = deepMerge(kit.scenarios.defaults.context, sc.context ?? {});
  if (sc.generateItems) ctx.items = Array.from({ length: sc.generateItems }, (_, i) => ({ payment: { instructedAmount: { value: i + 1, currency: 'AED' } } }));
  const cfg: RunConfig = {
    store: env.store,
    scripts: env.scripts,
    evaluationDate,
    bench: sc.bench ?? [],
    stubs: sc.stubs,
    signals: sc.signals,
    values: sc.values,
    supersessions: sc.supersessions,
    bindings: sc.bindings ?? [],
    availableFrom: sc.availableFrom,
    documents: docs,
  };
  return { name, docs, cfg, ctx };
}

function collectEnds(wf: any): string[] {
  const out = new Set<string>();
  const walk = (x: any) => {
    if (Array.isArray(x)) return x.forEach(walk);
    if (x && typeof x === 'object') {
      if (typeof x.end === 'string') out.add(x.end);
      Object.values(x).forEach(walk);
    }
  };
  walk(wf.do);
  walk(wf.deadline);
  return [...out];
}

function shortName(n: string) {
  const p = n.split('.');
  return p.length > 2 ? p[p.length - 2] : n;
}

export async function runScenario(env: StudioEnv, kit: KitContent, sc: any, expectedEntry: any): Promise<KitResult> {
  const res: KitResult = { id: sc.id, construct: expectedEntry?.construct ?? '', title: expectedEntry?.title ?? '', passed: false, expected: expectedEntry?.expect ?? [], actual: [], checks: [], notes: [] };
  try {
    const { name, cfg, ctx } = buildScenario(env, kit, sc);
    let run: Run;
    let root: Instance | undefined;
    const roots: { label: string; inst?: Instance }[] = [];
    if (sc.instances) {
      run = new Run(cfg);
      for (const spec of sc.instances) {
        const entry = { label: spec.label as string, inst: undefined as Instance | undefined };
        roots.push(entry);
        run.clock.at(run.clock.start + durationMs(spec.at), () => {
          const doc = run.resolveDoc(name, spec.workflowRelease);
          void startInstance(run, doc, clone(ctx), 'platform').then((i) => (entry.inst = i));
        });
      }
      await run.drive(() => roots.every((r) => r.inst?.ended));
      root = roots[0].inst;
    } else if (sc.handler) {
      const r = await runInbound(cfg);
      run = r.run;
      root = run.handlerRuns[0];
    } else {
      const r = await runWorkflow(cfg, name, ctx);
      run = r.run;
      root = r.root;
    }
    if (!root) throw new Error('The scenario did not start');
    res.elapsed = (root.endedAt ?? run.clock.now) - root.startedAt;
    let actual = viewTrace(sc, root, roots);
    if (sc.traceDrop) actual = actual.filter((t) => !sc.traceDrop.includes(t));
    res.actual = actual;
    const traceOk = equalWithFreeOrder(res.expected, actual, sc.freeOrder);
    res.checks.push({ what: 'trace', expected: res.expected, actual, passed: traceOk });
    // R-05-38: the comparison includes the detail, the steps inside composite steps.
    if (expectedEntry?.detail) res.checks.push({ what: 'detail', expected: expectedEntry.detail, actual: root.detail, passed: JSON.stringify(expectedEntry.detail) === JSON.stringify(root.detail) });
    checkExtras(sc, expectedEntry, root, run, res);
    if (root.failed) res.notes!.push(`failed: ${root.failed}`);
    res.passed = res.checks.every((c) => c.passed);
  } catch (e: any) {
    res.error = String(e?.message ?? e);
  }
  return res;
}

function viewTrace(sc: any, root: Instance, roots: { label: string; inst?: Instance }[]): string[] {
  switch (sc.traceView ?? 'business') {
    case 'forEach-summary': {
      const out: string[] = [];
      const seen = new Set<string>();
      let inItems = false;
      for (const t of root.trace) {
        const m = /^[\w-]+\[(\d+)\]:/.exec(t);
        if (m) {
          seen.add(m[1]);
          if (!inItems) { out.push('__items__'); inItems = true; }
          continue;
        }
        inItems = false;
        out.push(t);
      }
      return out.map((t) => (t === '__items__' ? `items:${seen.size}` : t));
    }
    case 'deltas': {
      const out: string[] = [];
      for (const t of root.trace) {
        out.push(t);
        const id = t.split(':')[0];
        const d = root.deltas.find((x) => x.step === id);
        if (d && d.delta.length && !t.startsWith('end:')) out.push(`delta:${id}`);
      }
      return out;
    }
    case 'rule-trace': {
      const out: string[] = [];
      for (const t of root.trace) {
        out.push(t);
        const id = t.split(':')[0];
        if (root.ruleTraces.some((r) => r.step === id) && !t.startsWith('end:')) out.push('ruleTrace:stored');
      }
      return out;
    }
    case 'releases': {
      const re = new RegExp(sc.traceFilter ?? '.');
      return root.releaseTrace.filter((t) => re.test(t));
    }
    case 'instance-releases':
      return roots.flatMap((r) => (r.inst?.steps ?? []).map((s) => `${r.label}:${s.step}@${s.release}`));
    default:
      return root.trace;
  }
}

function equalWithFreeOrder(expected: string[], actual: string[], free?: number[][]): boolean {
  if (expected.length !== actual.length) return false;
  const used = new Set<number>();
  for (const group of free ?? []) {
    const e = group.map((i) => expected[i]).sort();
    const a = group.map((i) => actual[i]).sort();
    if (JSON.stringify(e) !== JSON.stringify(a)) return false;
    group.forEach((i) => used.add(i));
  }
  return expected.every((x, i) => used.has(i) || x === actual[i]);
}

function checkExtras(sc: any, exp: any, root: Instance, run: Run, res: KitResult) {
  const add = (what: string, expected: any, actual: any, passed: boolean) => res.checks.push({ what, expected, actual, passed });
  const c = { ...(exp ?? {}), ...(sc.checks ?? {}) };
  if (c.attempts) for (const [k, v] of Object.entries<number>(c.attempts)) add(`attempts ${k}`, v, root.attempts[k] ?? 0, (root.attempts[k] ?? 0) === v);
  if (c.accepted !== undefined) {
    const ids = new Set(run.messages.filter((m) => m.from === 'platform').map((m) => m.id));
    add('accepted logical messages', c.accepted, ids.size, ids.size === c.accepted);
  }
  if (c.distinctIds !== undefined) {
    const sends = run.messages.filter((m) => m.from === 'platform' && m.key && m.key.endsWith(':send'));
    const keys = new Set(sends.map((m) => m.key));
    add('distinct business ids sent', c.distinctIds, keys.size, keys.size === c.distinctIds);
  }
  if (c.effective) {
    const step = c.effective.step ?? 'a';
    const rec = root.steps.find((s) => s.step === step);
    add('effective attemptTimeout', c.effective.attemptTimeout, rec?.policy?.attemptTimeout, rec?.policy?.attemptTimeout === c.effective.attemptTimeout);
    if (c.effective.source) add('source of attemptTimeout', c.effective.source, rec?.policySources?.attemptTimeout, rec?.policySources?.attemptTimeout === c.effective.source);
  }
  if (c.virtualElapsed && !sc.checks?.virtualElapsedBusiness) {
    const ms = durationMs(c.virtualElapsed);
    add('virtual elapsed', c.virtualElapsed, res.elapsed, res.elapsed === ms);
  }
  if (sc.checks?.virtualElapsedBusiness) {
    const start = run.clock.start;
    const ms = addDuration(start, sc.checks.virtualElapsedBusiness, DEFAULT_CALENDAR) - start;
    add('virtual elapsed on the business calendar', ms, res.elapsed, res.elapsed === ms);
  }
  if (c.unmatched !== undefined) add('unmatched messages', c.unmatched, run.unmatched.length, run.unmatched.length === c.unmatched);
  if (c.duplicates !== undefined) add('duplicates ignored', c.duplicates, run.duplicates.length, run.duplicates.length === c.duplicates);
  if (c.early !== undefined) {
    const n = run.timeline.filter((e) => e.type === 'message-kept').length;
    add('messages kept for a later await', c.early, n, n === c.early);
  }
  if (c.durableInstances !== undefined) {
    const n = run.allInstances().filter((i) => i.durable).length;
    add('durable instances', c.durableInstances, n, n === c.durableInstances);
  }
  if (c.forbiddenInState) {
    const keys = new Set(root.engineStates.flatMap((s) => Object.keys(s ?? {})));
    const bad = c.forbiddenInState.filter((k: string) => keys.has(k.replace('[]', '')) || keys.has(k));
    add('engine state holds references only', [], bad, bad.length === 0);
  }
  if (c.notBefore) {
    const rec = root.steps.find((s) => s.step === c.notBefore.step);
    add(`${c.notBefore.step} starts after resume`, durationMs(c.notBefore.at), rec?.startedAt, (rec?.startedAt ?? -1) >= durationMs(c.notBefore.at));
  }
  if (c.unchanged) add(`${c.unchanged} unchanged`, undefined, getPath(root.ctx, c.unchanged), getPath(root.ctx, c.unchanged) === undefined);
}

export async function runKit(env: StudioEnv, only: string[] = []): Promise<KitResult[]> {
  const kit = loadKit(env);
  const byId = new Map<string, any>(kit.manifest.scenarios.map((s: any) => [s.id, s]));
  const out: KitResult[] = [];
  for (const sc of kit.scenarios.scenarios) {
    if (only.length && !only.includes(sc.id)) continue;
    out.push(await runScenario(env, kit, sc, byId.get(sc.id)));
  }
  // A scenario in the manifest with no studio content is a red scenario (R-C-01).
  for (const m of kit.manifest.scenarios) {
    if (only.length && !only.includes(m.id)) continue;
    if (!kit.scenarios.scenarios.some((s: any) => s.id === m.id)) out.push({ id: m.id, construct: m.construct, title: m.title, passed: false, expected: m.expect, actual: [], checks: [], error: 'No scenario content' });
  }
  return out;
}

void shortName;
