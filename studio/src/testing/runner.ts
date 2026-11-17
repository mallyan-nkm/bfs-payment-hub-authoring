import * as fs from 'node:fs';
import * as path from 'node:path';
import { durationMs } from '../core/duration.js';
import { clone } from '../core/canonical.js';
import { getPath } from '../core/paths.js';
import type { Store } from '../core/store.js';
import { runRuleSet } from '../rules/service.js';
import { buildFacts } from '../interp/facts.js';
import { attachIntake, Instance, Run, runActivity, runInbound, runWorkflow, startInstance, type RunConfig, type RunResult } from '../interp/interpreter.js';
import type { ScriptRegistry } from '../interp/sandbox.js';

// The test runner (C11). Runs a test case at its level against its bench and compares the
// outcome, trace, attempts, messages, findings, reason code and virtual elapsed time.

export interface Check { what: string; expected: any; actual: any; passed: boolean; note?: string }

export interface TestRunReport {
  id: string;
  title?: string;
  target: string;
  level: string;
  passed: boolean;
  checks: Check[];
  error?: string;
  outcome?: string;
  trace: string[];
  releaseTrace: string[];
  elapsed: string;
  warnings: string[];
  record?: RunRecord;
}

/** What the studio shows of a run: the record the payment would carry. */
export interface RunRecord {
  status: string;
  instances: InstanceView[];
  timeline: any[];
  messages: any[];
  unmatched: any[];
  duplicates: any[];
  bench: any[];
  defaultBindings: any[];
  elapsedMs: number;
  startedAt: string;
}

export interface InstanceView {
  id: string;
  kind: string;
  workflow: string;
  release: string;
  parent?: string;
  outcome?: string;
  reasonCode?: string;
  failed?: string;
  durable: boolean;
  trace: string[];
  releaseTrace: string[];
  steps: any[];
  deltas: any[];
  ruleTraces: any[];
  attempts: Record<string, number>;
  pins: Record<string, string>;
  bindings: any[];
  supersessions: any[];
  engineState?: any;
  context: any;
  startedAt: number;
  endedAt?: number;
}

export function viewInstance(i: Instance): InstanceView {
  return {
    id: i.id, kind: i.kind, workflow: i.name, release: i.doc?.release, parent: i.parent?.id, outcome: i.outcome, reasonCode: i.reasonCode,
    failed: i.failed, durable: i.durable, trace: i.trace, releaseTrace: i.releaseTrace, steps: i.steps, deltas: i.deltas,
    ruleTraces: i.ruleTraces.map((r) => ({ step: r.step, worst: r.result.worst, findings: r.result.findings, decisions: r.result.decisions, trace: r.result.trace, parameters: r.result.parameters })),
    attempts: i.attempts, pins: Object.fromEntries(i.pins), bindings: i.bindings, supersessions: i.supersessionsApplied,
    engineState: i.engineStates[i.engineStates.length - 1], context: clone(i.ctx), startedAt: i.run.clock.start === 0 ? 0 : i.startedAt - i.run.clock.start, endedAt: i.endedAt !== undefined ? i.endedAt - i.run.clock.start : undefined,
  };
}

export function recordOf(r: RunResult, cfg: RunConfig): RunRecord {
  const run = r.run;
  return {
    status: r.status,
    instances: [...run.allInstances(), ...run.handlerRuns].map(viewInstance),
    timeline: run.timeline,
    messages: run.messages.map((m) => ({ ...m, payload: m.payload === undefined ? undefined : '(held by reference)' })),
    unmatched: run.unmatched.map((u) => ({ type: u.message.type, reason: u.reason, at: u.message.at - run.clock.start })),
    duplicates: run.duplicates.map((m) => ({ type: m.type, at: m.at - run.clock.start })),
    bench: cfg.bench ?? [],
    defaultBindings: run.defaultBindings,
    elapsedMs: run.clock.elapsed,
    startedAt: run.clock.iso(run.clock.start),
  };
}

export interface RunnerEnv {
  store: Store;
  scripts: ScriptRegistry;
  samplesDirs: string[];
}

export function loadSample(env: RunnerEnv, ref: string): { payload: string; name: string } {
  const name = ref.replace(/^samples\//, '');
  for (const d of env.samplesDirs) {
    const f = path.join(d, name);
    if (fs.existsSync(f)) return { payload: fs.readFileSync(f, 'utf8'), name };
  }
  throw new Error(`Sample ${ref} does not exist`);
}

/** Trace equality (R-C-04). An item index is matched when the batch has one item. */
export function traceEqual(expected: string[], actual: string[]): boolean {
  if (expected.length !== actual.length) return false;
  return expected.every((e, i) => e === actual[i] || e === actual[i].replace(/\[0\]:/, ':'));
}

function singleItemNormalise(trace: string[]): string[] {
  const multi = trace.some((t) => /\[[1-9]\d*\]:/.test(t));
  return multi ? trace : trace.map((t) => t.replace(/\[0\]:/, ':'));
}

export function baseConfig(env: RunnerEnv, test: any, extra: Partial<RunConfig> = {}): RunConfig {
  const cfg: RunConfig = {
    store: env.store,
    scripts: env.scripts,
    tenant: test.tenant,
    evaluationDate: test.evaluationDate,
    bench: test.bench ?? [],
    signals: (test.given?.signals ?? []).map((s: any) => ({ after: s.at, signal: s.signal })),
    ...extra,
  };
  if (test.given?.inbound) {
    const s = loadSample(env, test.given.inbound);
    cfg.inbound = { payload: s.payload };
  }
  return cfg;
}

export async function runTest(env: RunnerEnv, test: any, extra: Partial<RunConfig> = {}): Promise<TestRunReport> {
  const report: TestRunReport = { id: test.id, title: test.title, target: test.target, level: test.level, passed: false, checks: [], trace: [], releaseTrace: [], elapsed: 'PT0S', warnings: [] };
  try {
    const cfg = baseConfig(env, test, extra);
    if (test.level === 'ruleset') return checkRuleSet(env, test, report);
    let result: RunResult;
    let root: Instance | undefined;
    const ctx = clone(test.given?.context ?? {});
    switch (test.level) {
      case 'activity': {
        const act = env.store.doc(test.target);
        if (cfg.inbound) prepareInbound(ctx, cfg);
        result = await runActivityTest(cfg, test.target, ctx, act?.type ?? 'activity');
        root = result.root;
        break;
      }
      case 'hook':
        result = await runWorkflow(cfg, test.target, ctx);
        root = result.root;
        break;
      case 'leg':
        if (cfg.inbound) result = await runLegFromInbound(env, cfg, test.target);
        else result = await runWorkflow(cfg, test.target, ctx);
        root = result.run.instances.find((i) => i.doc.name === test.target) ?? result.root;
        break;
      case 'handler':
      case 'end-to-end':
        result = await runInbound(cfg);
        root = [...result.run.instances, ...result.run.handlerRuns].find((i) => i.doc.name === test.target) ?? result.run.instances.find((i) => i.kind === 'platform') ?? result.root;
        break;
      default:
        throw new Error(`Unknown level ${test.level}`);
    }
    report.record = recordOf(result, cfg);
    report.warnings = result.run.warnings;
    if (!root) throw new Error(`The run did not start ${test.target}`);
    report.outcome = root.outcome;
    report.trace = singleItemNormalise(root.trace);
    report.releaseTrace = root.releaseTrace;
    const elapsedMs = (root.endedAt ?? result.run.clock.now) - root.startedAt;
    report.elapsed = msToIso(elapsedMs);
    compare(test.expect ?? {}, root, result, report, elapsedMs);
    if (!root.ended) report.checks.push({ what: 'ended', expected: true, actual: false, passed: false, note: `The run stopped ${result.status}` });
    report.passed = report.checks.every((c) => c.passed);
  } catch (e: any) {
    report.error = String(e?.stack ?? e);
    report.passed = false;
  }
  return report;
}

function prepareInbound(ctx: any, cfg: RunConfig) {
  ctx.messages ??= {};
  ctx.messages.inbound = { id: 'inbound-1', type: 'inbound' };
  (cfg as any).__inboundMessage = { id: 'inbound-1', type: 'inbound', port: '', from: 'simulator', at: 0, correlation: {}, payload: cfg.inbound?.payload };
}

async function runActivityTest(cfg: RunConfig, name: string, ctx: any, stepId: string): Promise<RunResult> {
  const inbound = (cfg as any).__inboundMessage;
  if (!inbound) {
    const r = await runActivity(cfg, name, ctx);
    return renameStep(r, name, stepId);
  }
  // A map test: the message is held by the run; the context holds the reference.
  const run = new Run(cfg);
  run.messages.push({ ...inbound, at: run.clock.now });
  const act = run.resolveDoc(name);
  const wf = { kind: 'workflow', wfKind: 'activity', name: `${name}#test`, release: act.release, exit: { outcomes: act.outcomes.map((o: any) => o.name) }, do: [{ id: stepId, call: name, handle: Object.fromEntries(act.outcomes.filter((o: any) => !o.primary).map((o: any) => [o.name, { end: o.name }])) }] };
  let root: Instance | undefined;
  void startInstance(run, wf, ctx, 'activity').then((i) => (root = i));
  const status = await run.drive(() => root !== undefined);
  return { run, root, status };
}

function renameStep(r: RunResult, _name: string, _stepId: string): RunResult {
  return r;
}

/** A leg test that starts from a source file: the dispatch table's intake map, then the leg. */
async function runLegFromInbound(env: RunnerEnv, cfg: RunConfig, leg: string): Promise<RunResult> {
  const run = new Run(cfg);
  attachIntake(run);
  const scheme = env.store.byKind('scheme').map((e) => e.doc).find((s) => (s.messages ?? []).some((m: any) => m.disposition?.start?.leg === leg));
  const entry = scheme?.messages.find((m: any) => m.disposition?.start?.leg === leg);
  if (!entry) throw new Error(`No start message in a dispatch table names leg ${leg}`);
  const inbound = { id: run.newId('in'), type: entry.id, port: '', from: 'simulator' as const, at: run.clock.now, correlation: {}, payload: cfg.inbound?.payload, conversation: 'conv-1' };
  run.messages.push(inbound);
  const ctx: any = { header: { conversation: 'conv-1', scheme: scheme.scope, kind: entry.disposition.start.kind, direction: 'outward', receivedAt: run.clock.iso() }, messages: { inbound: { id: inbound.id, type: inbound.type } } };
  let root: Instance | undefined;
  void (async () => {
    const map = entry.disposition.start.intakeMap;
    if (map) {
      const intake = { kind: 'workflow', wfKind: 'intake', name: `${map}#intake`, release: '0.0.0', exit: { outcomes: ['mapped', 'unmappable'] }, do: [{ id: 'intake', call: map, handle: { unmappable: { end: 'unmappable' } } }] };
      const it = await startInstance(run, intake, ctx, 'intake');
      if (it.outcome !== 'mapped') { root = it; return; }
    }
    ctx.header.paymentId ??= run.newId('P');
    ctx.header.direction = 'outward';
    root = await startInstance(run, run.resolveDoc(leg), ctx, 'leg');
  })();
  const status = await run.drive(() => root !== undefined && run.instances.every((i) => i.ended));
  return { run, root, status };
}

function compare(exp: any, root: Instance, result: RunResult, report: TestRunReport, elapsedMs: number) {
  const add = (what: string, expected: any, actual: any, passed: boolean, note?: string) => report.checks.push({ what, expected, actual, passed, note });
  if (exp.outcome !== undefined) add('outcome', exp.outcome, root.outcome, exp.outcome === root.outcome, root.failed);
  if (exp.reasonCode !== undefined) add('reasonCode', exp.reasonCode, root.reasonCode, exp.reasonCode === root.reasonCode);
  if (exp.trace) add('trace', exp.trace, report.trace, traceEqual(exp.trace, report.trace));
  if (exp.attempts) for (const [k, v] of Object.entries<number>(exp.attempts)) add(`attempts.${k}`, v, root.attempts[k] ?? 0, (root.attempts[k] ?? 0) === v);
  if (exp.virtualElapsed) {
    const ms = durationMs(exp.virtualElapsed);
    add('virtualElapsed', exp.virtualElapsed, msToIso(elapsedMs), Math.abs(elapsedMs - ms) < 60000 || (exp.virtualElapsed.includes('D') && elapsedMs >= ms && elapsedMs < ms + 3 * 86400000));
  }
  if (exp.messages) for (const m of exp.messages) {
    const n = result.run.messages.filter((x) => x.from === 'platform' && x.port === m.port && x.type === m.message).length;
    const distinct = new Set(result.run.messages.filter((x) => x.from === 'platform' && x.port === m.port && x.type === m.message).map((x) => x.id)).size;
    const count = m.count ?? 1;
    // A count is of transmissions on the wire; retries of one logical message share its id.
    add(`messages ${m.message} on ${m.port}`, count, n, n === count, n !== distinct ? `${n} transmissions carried ${distinct} logical message(s)` : undefined);
  }
  if (exp.findings) {
    const found = root.ruleTraces.flatMap((r) => r.result.findings);
    const ok = exp.findings.length === 0 ? found.filter((f) => f.severity !== 'warn').length === 0 : exp.findings.every((e: any) => found.some((f: any) => Object.entries(e).every(([k, v]) => JSON.stringify(f[k]) === JSON.stringify(v))));
    add('findings', exp.findings, found.map((f) => ({ reasonCode: f.reasonCode, path: f.path, severity: f.severity })), ok);
  }
  if (exp.decisions) {
    const ok = exp.decisions.every((d: any) => JSON.stringify(root.decisions[d.name]) === JSON.stringify(d.value));
    add('decisions', exp.decisions, root.decisions, ok);
  }
  if (exp.writes) for (const [p, v] of Object.entries(exp.writes)) add(`writes ${p}`, v, getPath(root.ctx, p), JSON.stringify(getPath(root.ctx, p)) === JSON.stringify(v));
  if (exp.status) add('status', exp.status, root.ctx?.status?.code, root.ctx?.status?.code === exp.status);
}

function checkRuleSet(env: RunnerEnv, test: any, report: TestRunReport): TestRunReport {
  const ctx = test.given?.context ?? {};
  const facts = buildFacts({ ctx, reads: ['header', 'payment', 'batch', 'items[]', 'status', 'facts', 'ext'], extensions: env.store.byKind('extension').map((e) => e.doc) });
  const res = runRuleSet(test.target, facts, { resolve: (n) => env.store.doc(n), evaluationDate: test.evaluationDate });
  const exp = test.expect ?? {};
  if (exp.findings) report.checks.push({ what: 'findings', expected: exp.findings, actual: res.findings, passed: exp.findings.every((e: any) => res.findings.some((f: any) => Object.entries(e).every(([k, v]) => f[k] === v))) && (exp.findings.length > 0 || res.findings.length === 0) });
  report.passed = report.checks.every((c) => c.passed);
  return report;
}

export function msToIso(ms: number): string {
  if (ms <= 0) return 'PT0S';
  let s = Math.round(ms / 1000);
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60); s -= m * 60;
  return 'P' + (d ? `${d}D` : '') + (h || m || s ? 'T' + (h ? `${h}H` : '') + (m ? `${m}M` : '') + (s ? `${s}S` : '') : '');
}

export async function runAllTests(env: RunnerEnv, filter?: (t: any) => boolean): Promise<TestRunReport[]> {
  const tests = env.store.byKind('test').map((e) => e.doc).filter((t) => (filter ? filter(t) : true));
  tests.sort((a, b) => (a.id < b.id ? -1 : 1));
  const out: TestRunReport[] = [];
  for (const t of tests) out.push(await runTest(env, t));
  return out;
}
