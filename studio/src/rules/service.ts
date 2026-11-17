import { compile, evaluate, evalExpr, interpolate, truthy, type Env } from './expr.js';

// The rule service (Part 4; C06). One session per rule-set call; nothing survives the call.
// NRules semantics: `when` matches facts and binds them; `then` yields findings, decisions or facts.
// Rule logic is pinned; parameter values are resolved by date (and tenant) before the session starts.

export type Facts = Record<string, any[]>; // fact type -> instances

export interface Finding {
  rule: string;
  release: string;
  severity: 'reject' | 'repair' | 'warn';
  path: string;
  reasonCode: string;
  schemeCode?: string;
  message?: string;
}

export interface Decision {
  rule: string;
  name: string;
  value: any;
  reason?: string;
}

export interface RuleTraceEntry {
  rule: string;
  release: string;
  stage?: string;
  fired: boolean;
  activations: number;
  bindings?: Record<string, any>[];
}

export interface SessionResult {
  findings: Finding[];
  decisions: Decision[];
  facts: Facts;
  trace: RuleTraceEntry[];
  worst: 'pass' | 'warn' | 'repair' | 'reject';
  stoppedAtStage?: string;
  parameters: Record<string, any>;
}

/** Resolves a rule, rule set or table by major line to a pinned document. */
export type Resolver = (name: string) => any;

/** Resolves a parameter value for a rule at a date (R-01-17, R-04-09). */
export type ParamLookup = (owner: string, param: string, decl: any, date: string) => any;

export const defaultParamLookup: ParamLookup = (_o, _p, decl) => decl?.default;

const SEVERITY_RANK = { pass: 0, warn: 1, repair: 2, reject: 3 } as const;

export interface SessionOptions {
  resolve: Resolver;
  lookup?: ParamLookup;
  evaluationDate: string;
  businessDate?: string;
}

// ------------------------------------------------------------------- matching

type Binding = Env;

function patternVar(p: any): string {
  return p.as ?? p.fact.charAt(0).toLowerCase() + p.fact.slice(1);
}

function matchesPattern(p: any, fact: any, env: Binding): boolean {
  if (!p.where) return true;
  return truthy(evalExpr(p.where, { ...env, [patternVar(p)]: fact }));
}

/** Evaluate a `when` list into the set of activations (each a full binding). */
function activations(conds: any[], facts: Facts, start: Binding[]): Binding[] {
  let envs = start;
  for (const c of conds) {
    const next: Binding[] = [];
    for (const env of envs) {
      if (c.match) {
        for (const f of facts[c.match.fact] ?? []) if (matchesPattern(c.match, f, env)) next.push({ ...env, [patternVar(c.match)]: f });
      } else if (c.exists) {
        if ((facts[c.exists.fact] ?? []).some((f) => matchesPattern(c.exists, f, env))) next.push(env);
      } else if (c.not) {
        if (!(facts[c.not.fact] ?? []).some((f) => matchesPattern(c.not, f, env))) next.push(env);
      } else if (c.all) {
        if ((facts[c.all.fact] ?? []).every((f) => matchesPattern(c.all, f, env))) next.push(env);
      } else if (c.query) {
        const q = c.query;
        const matched = (facts[q.match.fact] ?? []).filter((f) => matchesPattern(q.match, f, env));
        if (q.groupBy) {
          const groups = new Map<string, any[]>();
          for (const f of matched) {
            const k = JSON.stringify(evalExpr(q.groupBy, { ...env, [patternVar(q.match)]: f }));
            if (!groups.has(k)) groups.set(k, []);
            groups.get(k)!.push(f);
          }
          for (const g of groups.values()) next.push({ ...env, [q.as]: g });
        } else {
          next.push({ ...env, [q.as]: matched });
        }
      } else if (c.let) {
        const e = { ...env };
        for (const [k, v] of Object.entries(c.let)) e[k] = evalExpr(v as string, e);
        next.push(e);
      } else if (c.having) {
        if (truthy(evalExpr(c.having, env))) next.push(env);
      } else if (c.and) {
        next.push(...activations(c.and, facts, [env]));
      } else if (c.or) {
        const seen = new Set<string>();
        for (const branch of c.or) {
          for (const b of activations([branch], facts, [env])) {
            const k = JSON.stringify(b);
            if (!seen.has(k)) { seen.add(k); next.push(b); }
          }
        }
      }
    }
    envs = next;
    if (envs.length === 0) break;
  }
  return envs;
}

/** Facts for each declared parameter: `Param` instances and a variable per name. */
function paramFacts(rule: any, opts: SessionOptions, owner: string, overrides: Record<string, any>): Record<string, any> {
  const out: Record<string, any> = {};
  for (const [name, decl] of Object.entries<any>(rule.parameters ?? {})) {
    const date = decl.lookupDate === 'business' && opts.businessDate ? opts.businessDate : opts.evaluationDate;
    const value = name in overrides ? overrides[name] : (opts.lookup ?? defaultParamLookup)(owner, name, decl, date);
    out[name] = { name, value };
  }
  return out;
}

function bindingSummary(env: Binding): Record<string, any> {
  const out: Record<string, any> = {};
  for (const [k, v] of Object.entries(env)) {
    if (typeof v !== 'function') out[k] = v;
  }
  return out;
}

// ----------------------------------------------------------------- one rule

interface RuleFire {
  findings: Finding[];
  decisions: Decision[];
  newFacts: { type: string; value: any }[];
  trace: RuleTraceEntry;
}

function fireRule(rule: any, facts: Facts, opts: SessionOptions, paramOverrides: Record<string, any>, stage?: string): RuleFire {
  const params = paramFacts(rule, opts, rule.name, paramOverrides);
  // Param facts are inserted per rule: a rule sees the parameters it declares.
  const ruleFacts: Facts = { ...facts, Param: [...(facts.Param ?? []), ...Object.values(params)] };
  const base: Binding = { evaluationDate: opts.evaluationDate };
  const acts = activations(rule.when ?? [], ruleFacts, [base]);
  const out: RuleFire = { findings: [], decisions: [], newFacts: [], trace: { rule: rule.name, release: rule.release, stage, fired: acts.length > 0, activations: acts.length } };
  if (acts.length) out.trace.bindings = acts.slice(0, 5).map(bindingSummary);
  // Non-repeatable: a validation rule yields one finding however many matches (R-04-02).
  const firing = rule.ruleKind === 'decision' || (rule.then ?? []).some((t: any) => t.yield?.fact) ? acts : acts.slice(0, 1);
  for (const env of firing) {
    for (const t of rule.then ?? []) {
      const y = t.yield ?? {};
      if (y.finding) {
        const f = y.finding;
        out.findings.push({
          rule: rule.name, release: rule.release, severity: f.severity, path: f.path, reasonCode: f.reasonCode,
          ...(f.schemeCode ? { schemeCode: f.schemeCode } : {}),
          ...(f.message ? { message: interpolate(f.message, env) } : {}),
        });
      }
      if (y.decision) {
        const v = typeof y.decision.value === 'string' && y.decision.value.startsWith('=') ? evalExpr(y.decision.value.slice(1), env) : y.decision.value;
        out.decisions.push({ rule: rule.name, name: y.decision.name, value: v, reason: y.decision.reason });
      }
      if (y.fact) {
        const { type, ...rest } = y.fact;
        const value: any = {};
        for (const [k, v] of Object.entries(rest)) value[k] = typeof v === 'string' && v.startsWith('=') ? evalExpr(v.slice(1), env) : v;
        out.newFacts.push({ type, value });
      }
    }
  }
  return out;
}

// ------------------------------------------------------------------ rule sets

export function runRuleSet(name: string, facts: Facts, opts: SessionOptions, paramOverrides: Record<string, Record<string, any>> = {}): SessionResult {
  const set = opts.resolve(name);
  if (!set) throw new Error(`Rule set ${name} does not resolve`);
  if (set.kind === 'table') return runTable(set, facts, opts, paramOverrides[set.name] ?? {});
  if (set.kind === 'rule') return runRules([set], set.mode ?? 'validation', undefined, facts, opts, paramOverrides);
  const rules = (set.rules as string[]).map((r) => {
    const d = opts.resolve(r);
    if (!d) throw new Error(`Rule ${r} in ${name} does not resolve`);
    return d;
  });
  return runRules(rules, set.mode, set.stages, facts, opts, paramOverrides);
}

function runRules(rules: any[], mode: string, stages: string[] | undefined, inFacts: Facts, opts: SessionOptions, paramOverrides: Record<string, Record<string, any>>): SessionResult {
  // One session: a private copy of the facts.
  const facts: Facts = JSON.parse(JSON.stringify(inFacts));
  const res: SessionResult = { findings: [], decisions: [], facts, trace: [], worst: 'pass', parameters: {} };
  for (const r of rules) for (const [k, decl] of Object.entries<any>(r.parameters ?? {})) {
    const date = decl.lookupDate === 'business' && opts.businessDate ? opts.businessDate : opts.evaluationDate;
    res.parameters[`${r.name}#${k}`] = k in (paramOverrides[r.name] ?? {}) ? paramOverrides[r.name][k] : (opts.lookup ?? defaultParamLookup)(r.name, k, decl, date);
  }

  if (mode === 'validation') {
    // Stages run in order; a blocking finding stops entry to the next stage (R-04-14).
    const order = stages && stages.length ? stages : [...new Set(rules.map((r) => r.ruleKind))];
    const staged = new Set<string>();
    for (const stage of order) {
      const inStage = rules.filter((r) => r.ruleKind === stage || (!order.includes(r.ruleKind) && stage === order[order.length - 1]));
      for (const r of inStage) {
        staged.add(r.name);
        const f = fireRule(r, facts, opts, paramOverrides[r.name] ?? {}, stage);
        res.findings.push(...f.findings);
        res.trace.push(f.trace);
      }
      if (res.findings.some((f) => f.severity === 'reject' || f.severity === 'repair')) {
        res.stoppedAtStage = stage;
        break;
      }
    }
    // Rules whose kind is in no stage run after the declared stages, unless stopped.
    if (!res.stoppedAtStage) {
      for (const r of rules.filter((r) => !staged.has(r.name))) {
        const f = fireRule(r, facts, opts, paramOverrides[r.name] ?? {});
        res.findings.push(...f.findings);
        res.trace.push(f.trace);
      }
    }
  } else {
    // Decision set: activations fire in priority order; yielded facts may activate other rules.
    const fired = new Set<string>();
    const sorted = [...rules].sort((a, b) => (b.priority ?? 0) - (a.priority ?? 0));
    let changed = true;
    let rounds = 0;
    while (changed && rounds++ < 100) {
      changed = false;
      for (const r of sorted) {
        if (fired.has(r.name)) continue; // non-repeatable
        const f = fireRule(r, facts, opts, paramOverrides[r.name] ?? {});
        if (!f.trace.fired) continue;
        fired.add(r.name);
        res.trace.push(f.trace);
        res.findings.push(...f.findings);
        res.decisions.push(...f.decisions);
        for (const nf of f.newFacts) {
          (facts[nf.type] ??= []).push(nf.value);
          changed = true;
        }
        if (f.newFacts.length) break; // re-evaluate from the top with the new facts
      }
    }
    for (const r of sorted) if (!fired.has(r.name)) res.trace.push({ rule: r.name, release: r.release, fired: false, activations: 0 });
  }
  res.worst = res.findings.reduce<SessionResult['worst']>((w, f) => (SEVERITY_RANK[f.severity] > SEVERITY_RANK[w] ? f.severity : w), 'pass');
  return res;
}

// --------------------------------------------------------------- decision tables

const FACT_TO_PART: Record<string, string> = { Header: 'header', Payment: 'payment', Batch: 'batch', Status: 'status', Facts: 'facts' };

/** Build the expression environment of a table from facts: the context parts and the parameters. */
function tableEnv(facts: Facts, params: Record<string, any>): Env {
  const env: Env = {};
  for (const [fact, part] of Object.entries(FACT_TO_PART)) if (facts[fact]?.length) env[part] = facts[fact][0];
  if (facts.Item) env.items = facts.Item;
  for (const [k, v] of Object.entries(params)) env[k] = v;
  return env;
}

export function rowMatches(when: Record<string, any>, inputs: Record<string, any>): boolean {
  for (const [k, cond] of Object.entries(when ?? {})) {
    const v = inputs[k];
    if (cond && typeof cond === 'object' && !Array.isArray(cond)) {
      if ('in' in cond && !(cond.in as any[]).some((x) => JSON.stringify(x) === JSON.stringify(v))) return false;
      if ('not' in cond && JSON.stringify(cond.not) === JSON.stringify(v)) return false;
      if ('lt' in cond && !(Number(v) < Number(cond.lt))) return false;
      if ('lte' in cond && !(Number(v) <= Number(cond.lte))) return false;
      if ('gt' in cond && !(Number(v) > Number(cond.gt))) return false;
      if ('gte' in cond && !(Number(v) >= Number(cond.gte))) return false;
      continue;
    }
    if (Array.isArray(cond)) {
      if (!cond.some((x) => JSON.stringify(x) === JSON.stringify(v))) return false;
      continue;
    }
    if (JSON.stringify(cond) !== JSON.stringify(v ?? null) && !(cond === v)) return false;
  }
  return true;
}

export function runTable(table: any, facts: Facts, opts: SessionOptions, overrides: Record<string, any> = {}): SessionResult {
  const params = paramFacts(table, opts, table.name, overrides);
  const env = tableEnv(facts, params);
  const inputs: Record<string, any> = {};
  for (const i of table.inputs ?? []) {
    try {
      inputs[i.name] = evaluate(compile(i.expression), env);
    } catch {
      inputs[i.name] = undefined;
    }
  }
  const matched: number[] = [];
  (table.rows ?? []).forEach((row: any, idx: number) => {
    if (rowMatches(row.when, inputs)) matched.push(idx);
  });
  const res: SessionResult = {
    findings: [], decisions: [], facts, worst: 'pass', parameters: Object.fromEntries(Object.entries(params).map(([k, v]) => [`${table.name}#${k}`, v.value])),
    trace: [{ rule: table.name, release: table.release, fired: matched.length > 0, activations: matched.length, bindings: [{ inputs, rows: matched }] }],
  };
  let hits: number[] = [];
  if (table.hitPolicy === 'first') hits = matched.slice(0, 1);
  else if (table.hitPolicy === 'unique') {
    if (matched.length > 1) throw new Error(`Table ${table.name} is unique but rows ${matched.join(', ')} all match`);
    hits = matched;
  } else hits = matched;
  for (const h of hits) {
    const row = table.rows[h];
    for (const [k, v] of Object.entries<any>(row.then ?? {})) {
      // An output may name an input; it then takes that input's value.
      const value = typeof v === 'string' && v in inputs ? inputs[v] : v;
      res.decisions.push({ rule: table.name, name: k, value, reason: row.reason });
    }
  }
  return res;
}

// -------------------------------------------------------------- table analysis

export interface TableReport {
  overlaps: [number, number][];
  gaps: Record<string, any>[];
  hasCatchAll: boolean;
}

/** Gaps and overlaps in a table (R-04-17, R-04-18), over the values its rows name. */
export function analyseTable(table: any): TableReport {
  const rows: any[] = table.rows ?? [];
  const domains: Record<string, any[]> = {};
  for (const i of table.inputs ?? []) domains[i.name] = [];
  for (const r of rows) for (const [k, v] of Object.entries<any>(r.when ?? {})) {
    if (!domains[k]) domains[k] = [];
    const vals = Array.isArray(v) ? v : v && typeof v === 'object' && 'in' in v ? v.in : [v];
    for (const x of vals) if (!domains[k].some((y) => JSON.stringify(y) === JSON.stringify(x))) domains[k].push(x);
  }
  for (const k of Object.keys(domains)) domains[k].push('__other__');
  const keys = Object.keys(domains);
  const combos: Record<string, any>[] = [];
  const build = (i: number, acc: Record<string, any>) => {
    if (combos.length > 2000) return;
    if (i === keys.length) { combos.push({ ...acc }); return; }
    for (const v of domains[keys[i]]) build(i + 1, { ...acc, [keys[i]]: v });
  };
  build(0, {});
  const overlaps = new Set<string>();
  const gaps: Record<string, any>[] = [];
  for (const c of combos) {
    const m = rows.map((r, i) => (rowMatches(r.when, c) ? i : -1)).filter((i) => i >= 0);
    if (m.length === 0) gaps.push(Object.fromEntries(Object.entries(c).map(([k, v]) => [k, v === '__other__' ? '(any other)' : v])));
    for (let a = 0; a < m.length; a++) for (let b = a + 1; b < m.length; b++) overlaps.add(`${m[a]},${m[b]}`);
  }
  return {
    overlaps: [...overlaps].map((s) => s.split(',').map(Number) as [number, number]),
    gaps: gaps.slice(0, 20),
    hasCatchAll: rows.some((r) => Object.keys(r.when ?? {}).length === 0),
  };
}

// --------------------------------------------------------------- examples runner

export interface ExampleResult {
  name: string;
  passed: boolean;
  expected: any;
  actual: { findings: any[]; decisions: any[] };
  error?: string;
}

/** Turn example `given` into facts: single objects become one-element lists; Param overrides. */
export function factsFromGiven(given: any): { facts: Facts; params: Record<string, any> } {
  const facts: Facts = {};
  const params: Record<string, any> = {};
  for (const [k, v] of Object.entries<any>(given ?? {})) {
    const list = Array.isArray(v) ? v : [v];
    if (k === 'Param') for (const p of list) params[p.name] = p.value;
    else facts[k] = list;
  }
  return { facts, params };
}

function subsetMatch(expected: any[], actual: any[]): boolean {
  if (expected.length !== actual.length) return false;
  const pool = [...actual];
  for (const e of expected) {
    const i = pool.findIndex((a) => Object.entries(e).every(([k, v]) => JSON.stringify(a[k]) === JSON.stringify(v)));
    if (i < 0) return false;
    pool.splice(i, 1);
  }
  return true;
}

/** Run the examples a rule or table carries (C06-S5). */
export function runExamples(doc: any, resolve: Resolver): ExampleResult[] {
  return (doc.examples ?? []).map((ex: any) => {
    const { facts, params } = factsFromGiven(ex.given);
    const opts: SessionOptions = { resolve, evaluationDate: ex.evaluationDate ?? '2026-11-17' };
    try {
      let res: SessionResult;
      if (doc.kind === 'table') res = runTable(doc, facts, opts, params);
      else res = runRules([doc], doc.ruleKind === 'decision' ? 'decision' : 'validation', undefined, facts, opts, { [doc.name]: params });
      const actual = {
        findings: res.findings.map((f) => ({ reasonCode: f.reasonCode, path: f.path, severity: f.severity })),
        decisions: res.decisions.map((d) => ({ name: d.name, value: d.value })),
      };
      let passed = true;
      if (ex.expect.findings) passed &&= subsetMatch(ex.expect.findings, actual.findings);
      if (ex.expect.decisions) passed &&= subsetMatch(ex.expect.decisions, actual.decisions);
      return { name: ex.name, passed, expected: ex.expect, actual };
    } catch (e: any) {
      return { name: ex.name, passed: false, expected: ex.expect, actual: { findings: [], decisions: [] }, error: e.message };
    }
  });
}
