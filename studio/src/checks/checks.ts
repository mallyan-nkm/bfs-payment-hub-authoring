import * as fs from 'node:fs';
import * as path from 'node:path';
import { compareReleases, parseName, parseRelease } from '../core/names.js';
import { coveredByAny } from '../core/paths.js';
import { analyseTable, runTable } from '../rules/service.js';
import { lintScript } from '../interp/sandbox.js';
import { effectivePolicy } from '../interp/policy.js';
import { addDuration, durationMs } from '../core/duration.js';
import { callsComposite, checkExpanded, expandWorkflow } from '../core/expand.js';
import { flowablePortable } from '../export/bpmn.js';
import { literalSecretsInText } from '../secrets/scan.js';
import type { StudioEnv } from '../env.js';

// Static checks (C04). A port of the reference validator (spec/tools/validate.py) that runs on
// one document as it is edited and on the whole store, and names the step each issue concerns.
// It adds the checks the catalogue marks "to build": L19, L20, L21, L23, L25, L26, L28, L29.

export interface Issue {
  code: string;
  severity: 'error' | 'warning';
  key: string; // artefact name, test id or scheme scope
  step?: string;
  message: string;
  file?: string;
}

export const CHECK_CATALOGUE: Record<string, string> = {
  L00: 'Schema validity',
  L02: 'Scope direction',
  L03: 'References resolve',
  L04: 'Major in name equals major of release',
  L05: 'Outcomes handled',
  L06: 'Hook points declared',
  L07: 'Handler restrictions',
  L08: 'Slots and legs agree',
  L09: 'Bank-neutral shared content',
  L10: 'Dispatch coverage',
  L11: 'Unused blocks say what runs them',
  L12: 'Validation sets yield findings only',
  L13: 'Hook stays inside its hook point contract',
  L14: 'Configuration values inside bounds; bindings resolve',
  L15: 'Test bench matches ports and scenarios',
  L16: 'Awaited messages are deliver messages',
  L17: 'Parameters declared',
  L18: 'Extension paths registered',
  L19: 'Data flow: reads are written earlier',
  L20: 'Effects: retry needs idempotency; writes have compensation',
  L21: 'Budgets fit the deadline',
  L22: 'Every port has a simulator; reachable ports are bound in the bench',
  L23: 'Pins complete',
  L24: 'Portability per target',
  L25: 'Decision tables and yield graphs',
  L26: 'Semver matches the interface diff',
  L27: 'Supersession rules',
  L28: 'Script determinism',
  L29: 'Step ids unique after inlining',
  L30: 'A composite and its subflow agree; a step that calls a composite has an id',
  L31: 'await and return only in a subflow; block only in the expanded form',
  L32: 'An await with no timeout has a reply time to take',
  L33: 'A start message routes: its kind, direction and segment select one platform workflow, and its leg fills its slot',
  L34: 'The factory holds only the packs its inventory lists',
  L35: 'A bank pack in content-configurations/banks/<bank>/ is its tenant\'s, and its lock file is current',
  L36: 'An await names a family its message\'s dispatch entry declares',
  L37: 'No literal secret: a binding, configuration or package names a secret by reference only (env:, file:, vault:)',
};

// ---------------------------------------------------------------- walking

export function walk(steps: any[] | undefined, out: any[] = []): any[] {
  for (const st of steps ?? []) {
    if (!st || typeof st !== 'object') continue;
    out.push(st);
    for (const action of Object.values(st.handle ?? {})) if (Array.isArray(action)) walk(action, out);
    if (st.switch) {
      for (const c of Object.values<any>(st.switch.cases ?? {})) walk(c, out);
      walk(st.switch.default, out);
    }
    if (st.parallel) for (const b of st.parallel.branches ?? []) walk(b, out);
    if (st.forEach) walk(st.forEach.do, out);
    if (st.loop) walk(st.loop.do, out);
    if (st.block) walk(st.block.do, out);
    if (st.await && Array.isArray(st.await.onTimeout)) walk(st.await.onTimeout, out);
    if (st.try) {
      walk(st.try, out);
      if (Array.isArray(st.catch)) walk(st.catch, out);
    }
  }
  return out;
}

export function allSteps(d: any): any[] {
  return [...walk(d.do), ...walk(d.finally), ...walk(d.deadline?.do)];
}

export function stepLabel(st: any): string {
  return st.id ?? st.call ?? st.slot ?? st.hook ?? st.use ?? Object.keys(st)[0];
}

export function refsOf(d: any): Set<string> {
  const out = new Set<string>();
  if (!d || typeof d !== 'object') return out;
  const kind = d.kind;
  for (const k of ['ports', 'ruleSets', 'rules', 'selfCheck']) for (const r of d[k] ?? []) out.add(r);
  for (const k of ['compensation', 'port']) if (typeof d[k] === 'string' && kind !== 'port') out.add(d[k]);
  for (const r of d.map?.edgeRules ?? []) out.add(r);
  if (d.implementation?.subflow) out.add(d.implementation.subflow);
  if (kind === 'workflow' || kind === 'hook') {
    if (d.implements?.activity) out.add(d.implements.activity);
    const of = d.implements?.of;
    for (const o of Array.isArray(of) ? of : of ? [of] : []) out.add(o);
    for (const hp of d.hookPoints ?? []) if (hp.policy?.onHold) out.add(hp.policy.onHold);
    for (const st of allSteps(d)) for (const k of ['call', 'use']) if (st[k]) out.add(st[k]);
  }
  if (kind === 'scheme') {
    for (const k of ['legs', 'handlers', 'ports', 'extensions']) for (const r of d[k] ?? []) out.add(r);
    for (const m of d.messages ?? []) {
      const disp = m.disposition ?? {};
      if (disp.start) for (const k of ['leg', 'refuseLeg', 'intakeMap']) if (disp.start[k]) out.add(disp.start[k]);
      for (const k of ['handler', 'sentBy']) if (disp[k]) out.add(disp[k]);
    }
  }
  if (kind === 'test') {
    out.add(d.target);
    for (const b of d.bench ?? []) {
      out.add(b.port);
      out.add(b.simulator);
    }
  }
  return out;
}

function scopeOf(name: string): string {
  const parts = name.split('.');
  return parts[0] === 'p' || parts[0] === 'b' ? parts[0] : parts.slice(0, 2).join('.');
}

function tier(scope: string) {
  return scope === 'p' || scope === 'b' ? scope : scope.endsWith('.p') ? 'cluster.p' : 'scheme';
}

function allowed(own: string, s: string): boolean {
  switch (tier(own)) {
    case 'p': return s === 'p';
    case 'cluster.p': return s === 'p' || s === own;
    case 'scheme': return s === 'p' || s === own || s === own.split('.')[0] + '.p';
    default: return true;
  }
}

// ---------------------------------------------------------------- checker

export class Checker {
  byName = new Map<string, any>();
  docs: any[] = [];
  schemes = new Map<string, any>();
  extensionRoots: string[] = [];
  denyNames: string[] = [];

  constructor(docs: any[], denyNames: string[] = [], private scriptOf?: (name: string) => { source: string; language: string } | undefined) {
    this.docs = docs;
    for (const d of docs) {
      if (d.name && d.kind !== 'pack') {
        const prev = this.byName.get(d.name);
        if (!prev || compareReleases(d.release ?? '0.0.0', prev.release ?? '0.0.0') >= 0) this.byName.set(d.name, d);
      }
      if (d.kind === 'scheme') this.schemes.set(d.scope, d);
      if (d.kind === 'extension') this.extensionRoots.push(d.root);
    }
    const tenants = docs.filter((d) => d.tenant).map((d) => String(d.tenant).toLowerCase());
    this.denyNames = [...new Set([...tenants, ...denyNames.map((n) => n.toLowerCase())])].filter(Boolean);
  }

  static fromEnv(env: StudioEnv, overlay: any[] = [], tenant?: string): Checker {
    const docs = env.store.all().filter((e) => e.status === 'published' && (!tenant || e.tenant === 'owner' || e.tenant === tenant)).map((e) => e.doc);
    const names = new Set(overlay.map((d) => `${d.kind}:${d.name ?? d.scope ?? d.id}`));
    const merged = [...docs.filter((d) => !names.has(`${d.kind}:${d.name ?? d.scope ?? d.id}`)), ...overlay];
    let deny: string[] = [];
    const f = path.join(env.specRoot, 'tools', 'deny-names.txt');
    if (fs.existsSync(f)) deny = fs.readFileSync(f, 'utf8').split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
    // Script-behinds are files on the branch, the factory's among them (R-11-25).
    return new Checker(merged, deny, (name) => env.scripts.find(name));
  }

  hookPoints(d: any, depth = 0): Record<string, any> {
    const pts: Record<string, any> = {};
    for (const h of d?.hookPoints ?? []) pts[h.name] = { ...h, owner: d.name };
    if (depth > 5) return pts;
    for (const st of allSteps(d ?? {})) if (st.use && this.byName.has(st.use)) Object.assign(pts, this.hookPoints(this.byName.get(st.use), depth + 1));
    return pts;
  }

  closure(name: string, seen = new Set<string>()): Set<string> {
    for (const r of refsOf(this.byName.get(name))) {
      if (!seen.has(r)) {
        seen.add(r);
        this.closure(r, seen);
      }
    }
    return seen;
  }

  // ------------------------------------------------------------ per document

  check(d: any): Issue[] {
    const out: Issue[] = [];
    const key = d.name ?? d.id ?? d.scope ?? d.kind;
    const err = (code: string, message: string, step?: string) => out.push({ code, severity: 'error', key, message, step });
    const warn = (code: string, message: string, step?: string) => out.push({ code, severity: 'warning', key, message, step });
    const kind = d.kind;
    const name: string | undefined = d.name;

    // L02, L03, L04
    if (!['pack', 'inventory', 'conformance-kit', 'engine-binding', 'supersession', 'configuration-set'].includes(kind)) {
      const own = name ? scopeOf(name) : kind === 'scheme' ? d.scope : undefined;
      for (const r of [...refsOf(d)].sort()) {
        if (!this.byName.has(r)) {
          err('L03', `Reference ${r} does not resolve`, this.stepReferring(d, r));
          continue;
        }
        if (own && kind !== 'test' && !allowed(own, scopeOf(r))) err('L02', `${own} scope may not depend on ${r}`, this.stepReferring(d, r));
      }
      if (name && name.includes('.v') && d.release) {
        const major = name.slice(name.lastIndexOf('.v') + 2);
        if (major !== String(d.release).split('.')[0]) err('L04', `Name says v${major} but release is ${d.release}`);
      }
      if (name && !parseName(name) && kind !== 'test') err('L00', `${name} does not follow <scope>.<type>.<name>.v<major>`);
    }

    if (kind === 'workflow' || kind === 'hook') this.checkFlow(d, err, warn);
    if (kind === 'workflow' || kind === 'hook' || kind === 'activity') this.checkComposites(d, err);
    if (kind === 'hook') this.checkHook(d, err);
    if (kind === 'scheme') this.checkScheme(d, err, warn);
    if (kind === 'rule') {
      const declared = new Set(Object.keys(d.parameters ?? {}));
      for (const c of d.when ?? []) {
        const m = c.match;
        if (m?.fact === 'Param' && !declared.has(m.as)) err('L17', `Uses parameter ${m.as} but does not declare it`);
      }
    }
    if (kind === 'ruleset') this.checkRuleSet(d, err);
    if (kind === 'table') this.checkTable(d, err, warn);
    if (kind === 'activity') this.checkActivity(d, err, warn);
    if (kind === 'configuration-set') this.checkConfigurationSet(d, err);
    if (kind === 'test') this.checkTest(d, err, warn);
    if (kind === 'port' && !this.docs.some((x) => x.kind === 'simulator' && x.port === d.name)) err('L22', `${d.name} has no simulator`);
    if (kind === 'supersession') this.checkSupersession(d, err);

    // L09: shared content does not name a bank or vendor.
    if (['activity', 'rule', 'ruleset', 'table', 'workflow', 'simulator', 'port', 'extension'].includes(kind) && name && scopeOf(name) !== 'b') {
      const text = JSON.stringify({ name: d.name, title: d.title, description: d.description, party: d.party, standsFor: d.standsFor }).toLowerCase();
      for (const t of this.denyNames) if (t && text.includes(t)) err('L09', `Shared content names a bank or vendor: ${t}`);
    }
    if (d.artefacts) this.checkPins(d, err);
    return out;
  }

  private stepReferring(d: any, r: string): string | undefined {
    for (const st of allSteps(d)) if (st.call === r || st.use === r) return stepLabel(st);
    return undefined;
  }

  private checkFlow(d: any, err: (c: string, m: string, s?: string) => void, warn: (c: string, m: string, s?: string) => void) {
    const slots: Record<string, any> = Object.fromEntries((d.slots ?? []).map((s: any) => [s.name, s.contract]));
    const pts = d.kind === 'workflow' ? this.hookPoints(d) : {};
    for (const st of allSteps(d)) {
      const label = stepLabel(st);
      if (st.call) {
        const a = this.byName.get(st.call);
        if (a?.kind === 'activity') {
          const outs = new Set<string>((a.outcomes ?? []).map((o: any) => o.name));
          const primary = (a.outcomes ?? []).find((o: any) => o.primary)?.name;
          const handled = new Set(Object.keys(st.handle ?? {}));
          for (const m of [...outs].filter((o) => o !== primary && !handled.has(o)).sort()) err('L05', `Step ${label} does not handle outcome ${m} of ${st.call}`, label);
          for (const x of [...handled].filter((h) => !outs.has(h)).sort()) err('L05', `Step ${label} handles ${x}, which ${st.call} never returns`, label);
          // L20: retry needs idempotency.
          const pol = effectivePolicy(a, st.policy);
          if (pol.maxAttempts.value > 1 && (a.effect === 'write' || a.effect === 'external') && !a.idempotencyKey) err('L20', `Step ${label} retries ${a.name}, which writes and has no idempotency key`, label);
        } else if (a && a.kind !== 'activity') err('L05', `Step ${label} calls ${st.call}, which is a ${a.kind}, not an activity`, label);
      }
      if (st.slot) {
        if (!slots[st.slot]) err('L08', `Slot ${st.slot} is not declared`, label);
        else {
          const c = slots[st.slot];
          const need = (c.outcomes ?? []).filter((o: string) => o !== c.primaryOutcome);
          for (const m of need.filter((o: string) => !(st.handle ?? {})[o]).sort()) err('L05', `Slot step ${label} does not handle leg outcome ${m}`, label);
        }
      }
      if (st.hook && d.kind === 'workflow' && !pts[st.hook]) err('L06', `Hook point ${st.hook} is not declared here or in a segment used here`, label);
      if (st.await && d.kind === 'workflow') {
        const sch = this.schemes.get(scopeOf(d.name));
        const hit = (sch?.messages ?? []).find((m: any) => m.id === st.await.message);
        if (!hit || !hit.disposition?.deliver) err('L16', `Awaited message ${st.await.message} is not a deliver message in the scheme catalogue`, label);
        else if (st.await.family && !(hit.disposition.deliver.families ?? []).includes(st.await.family)) err('L36', `Await ${st.id ?? '?'} takes family ${st.await.family} of ${st.await.message}, which its dispatch entry does not declare`, label);
      }
    }
    if (d.kind === 'workflow' && d.wfKind === 'handler') {
      for (const st of allSteps(d)) {
        if (!['call', 'switch', 'end'].some((k) => k in st)) err('L07', `A handler may only call, switch or end; found ${Object.keys(st).sort().join(', ')}`, stepLabel(st));
        const a = st.call ? this.byName.get(st.call) : undefined;
        if (a) {
          const ok = ['pure', 'read'].includes(a.effect) || (a.type === 'exchange' && a.mode === 'respond');
          if (!ok || a.type === 'hold' || this.waits(a)) err('L07', `Handler calls ${st.call}, which waits or writes`, stepLabel(st));
        }
      }
    }
    if (d.kind === 'workflow' && d.wfKind === 'leg') {
      for (const of of d.implements?.of ?? []) {
        const host = this.byName.get(of);
        const contract = (host?.slots ?? []).find((s: any) => s.name === d.implements.slot)?.contract;
        if (!contract) err('L08', `${of} has no slot ${d.implements.slot}`);
        else for (const o of (d.exit?.outcomes ?? []).filter((x: string) => !contract.outcomes.includes(x)).sort()) err('L08', `Leg outcome ${o} is not in the slot contract of ${of}`);
      }
    }
    // L29: step ids unique after segments are inlined.
    const seen = new Map<string, number>();
    const visit = (doc: any, depth: number) => {
      for (const st of allSteps(doc)) {
        if (st.id) seen.set(st.id, (seen.get(st.id) ?? 0) + 1);
        if (st.use && depth < 5) visit(this.byName.get(st.use) ?? {}, depth + 1);
      }
    };
    visit(d, 0);
    for (const [id, n] of seen) if (n > 1) err('L29', `Step id ${id} appears ${n} times after segments are inlined`, id);
    // L19: data flow.
    if (d.kind === 'workflow' && (d.wfKind === 'platform' || d.wfKind === 'leg')) this.checkDataFlow(d, warn);
    // R-05-39: a check that needs the whole flow runs on the expanded form and reports at the
    // authored step, naming the inner step.
    let flow = d;
    if (d.kind === 'workflow' && d.wfKind !== 'subflow' && callsComposite(d, (n) => this.byName.get(n))) {
      try {
        flow = expandWorkflow(d, (n) => this.byName.get(n));
        for (const p of checkExpanded(flow, (n) => this.byName.get(n))) {
          const inner = /(?:step|await|block) (\S+)/.exec(p)?.[1];
          err(p.includes('twice') ? 'L29' : 'L30', `In the expanded form: ${p}`, inner?.split('.')[0]);
        }
      } catch (e: any) {
        err('L32', `The workflow does not expand: ${e.message}`, e.step);
      }
    }
    // L24: portability per target. A construct Flowable cannot express is an error for that target,
    // and p content must stay in the portable core (R-05-26, R-09-09; C22-S5).
    if (d.kind === 'workflow' && d.wfKind !== 'segment' && d.wfKind !== 'subflow' && d.wfKind !== 'handler') {
      for (const it of flowablePortable(flow).steps) err('L24', `Not portable to Flowable: ${it.note}${it.alternative ? ` Use ${it.alternative}.` : ''}`, it.step);
    }
    // L21: budgets fit the deadline.
    if (d.kind === 'workflow' && d.deadline?.after) {
      const budget = this.longestBudget(flow.do ?? []);
      const deadline = addDuration(0, d.deadline.after);
      if (budget > deadline) warn('L21', `The attempt budgets on the longest path sum to ${Math.round(budget / 1000)}s, more than the deadline ${d.deadline.after}`);
    }
  }

  /** True when calling this activity can park the workflow: a hold, or a composite whose subflow waits. */
  waits(a: any, seen: string[] = []): boolean {
    if (a?.type === 'hold') return true;
    const sub = this.byName.get(a?.implementation?.subflow);
    if (!sub || seen.includes(a.name)) return false;
    for (const st of allSteps(sub)) {
      if (st.await || st.wait) return true;
      const inner = this.byName.get(st.call);
      if (inner?.kind === 'activity' && this.waits(inner, [...seen, a.name])) return true;
    }
    return false;
  }

  /** L30, L31, L32: composites and subflows (Part 5, 5.6; R-03-19 to R-03-21, R-05-33 to R-05-40). */
  private checkComposites(d: any, err: (c: string, m: string, s?: string) => void) {
    const composite = (a: any) => !!a && a.kind === 'activity' && a.implementation?.binding === 'subflow';
    const actions = (doc: any): any[] => {
      const out: any[] = [];
      for (const st of allSteps(doc)) {
        for (const a of Object.values(st.handle ?? {})) if (a && !Array.isArray(a) && typeof a === 'object') out.push(a);
        if (st.await?.onTimeout && !Array.isArray(st.await.onTimeout)) out.push(st.await.onTimeout);
      }
      return out;
    };
    const returns = (doc: any) => new Set<string>([...allSteps(doc).filter((s) => 'return' in s).map((s) => s.return), ...actions(doc).filter((a) => 'return' in a).map((a) => a.return)]);
    if (composite(d)) {
      const sub = this.byName.get(d.implementation.subflow);
      if (!sub || sub.wfKind !== 'subflow') err('L30', `${d.implementation.subflow} is not a subflow`);
      else if (sub.implements?.activity !== d.name) err('L30', `${sub.name} does not implement ${d.name}`);
      else {
        const outs = new Set<string>((d.outcomes ?? []).map((o: any) => o.name));
        const ret = returns(sub);
        for (const o of [...ret].filter((x) => !outs.has(x)).sort()) err('L30', `Its subflow returns ${o}, which the activity does not declare`);
        for (const o of [...outs].filter((x) => !ret.has(x)).sort()) err('L30', `Declares outcome ${o}, which its subflow never returns`);
      }
      for (const k of Object.keys(d.policy ?? {}).filter((k) => !['replyTimeout', 'replyCalendar', 'replyTimeoutParameter', 'replyCalendarParameter'].includes(k)).sort()) err('L30', `A composite carries no ${k}; attempts and retries belong to the steps of its subflow`);
      // R-05-47: a reply wait a bank may set is a parameter the composite declares, of the right type, with the policy as its default.
      for (const [key, type, dflt] of [['replyTimeoutParameter', 'duration', 'replyTimeout'], ['replyCalendarParameter', 'code', 'replyCalendar']] as const) {
        const name = d.policy?.[key];
        if (name === undefined) continue;
        const decl = d.parameters?.[name];
        if (!decl || decl.type !== type) err('L30', `${key} names ${name}, which is not a ${type} parameter of this activity`);
        else if (d.policy?.[dflt] === undefined || decl.default !== d.policy[dflt]) err('L30', `${key} ${name} defaults to ${JSON.stringify(decl.default)}, not the policy's ${dflt} ${JSON.stringify(d.policy?.[dflt])}`);
      }
    }
    if (d.kind !== 'workflow' && d.kind !== 'hook') return;
    const isSub = d.wfKind === 'subflow';
    if (isSub) {
      const act = this.byName.get(d.implements?.activity);
      if (!composite(act) || act.implementation.subflow !== d.name) err('L30', `${d.implements?.activity} is not a composite activity bound to this subflow`);
      for (const st of allSteps(d)) for (const k of ['end', 'slot', 'hook', 'use'].filter((k) => k in st)) err('L30', `A subflow may not contain ${k}; it returns an outcome`, stepLabel(st));
      for (const a of actions(d)) if ('end' in a) err('L30', 'A subflow may not end the workflow; it returns an outcome');
    }
    for (const st of allSteps(d)) {
      if (st.call && composite(this.byName.get(st.call)) && !st.id) err('L30', `A step that calls the composite ${st.call} needs an id`);
      if (st.await && !isSub) err('L31', `Step ${st.id ?? '?'} awaits ${st.await.message}; an await belongs in the subflow of a composite activity`, stepLabel(st));
      if (st.block) err('L31', 'A block appears only in the expanded form', stepLabel(st));
      if ('return' in st && !isSub) err('L31', 'Return appears only in a subflow', stepLabel(st));
      if (st.await && !('timeout' in st.await) && isSub) {
        const act = this.byName.get(d.implements?.activity) ?? {};
        if (!act.policy?.replyTimeout) err('L32', `Await ${st.id ?? '?'} has no timeout and ${act.name} declares no replyTimeout`, stepLabel(st));
      }
      // L17 (R-05-45): a wait or an await timeout that names a parameter names a duration parameter of the subflow's composite.
      for (const ref of [st.wait, st.await?.timeout]) {
        if (!ref || typeof ref !== 'object') continue;
        const act = this.byName.get(d.implements?.activity) ?? {};
        const decl = isSub ? act.parameters?.[ref.parameter] : undefined;
        if (!isSub) err('L17', `Step ${st.id ?? '?'} names parameter ${ref.parameter}; only a subflow may, from its composite`, stepLabel(st));
        else if (!decl) err('L17', `Step ${st.id ?? '?'} names parameter ${ref.parameter}, which ${act.name} does not declare`, stepLabel(st));
        else if (decl.type !== 'duration') err('L17', `Step ${st.id ?? '?'} names parameter ${ref.parameter}, which is not a duration`, stepLabel(st));
      }
    }
    if (!isSub) for (const a of actions(d)) if ('return' in a) err('L31', 'Return appears only in a subflow');
  }

  /** Sum of attempt budgets of port-reaching activities on the longest path, in ms. */
  longestBudget(steps: any[]): number {
    let sum = 0;
    for (const st of steps ?? []) {
      if (st.call) {
        const a = this.byName.get(st.call);
        if (a && (a.ports ?? []).length) {
          const p = effectivePolicy(a, st.policy);
          const attempt = p.attemptTimeout.value ? durationMs(p.attemptTimeout.value) : 0;
          let b = attempt * p.maxAttempts.value;
          for (let i = 1; i < p.maxAttempts.value; i++) b += durationMs(p.firstInterval.value) * Math.pow(p.backoff.value, i - 1);
          if (p.totalTimeout.value) b = Math.min(b, durationMs(p.totalTimeout.value));
          sum += b;
        }
        const branches = Object.values(st.handle ?? {}).filter(Array.isArray) as any[][];
        sum += Math.max(0, ...branches.map((x) => this.longestBudget(x)));
      } else if (st.await?.timeout) {
        sum += addDuration(0, st.await.timeout);
      } else if (st.loop) {
        sum += (st.loop.max ?? 1) * this.longestBudget(st.loop.do) + Math.max(0, (st.loop.max ?? 1) - 1) * (st.loop.every ? durationMs(st.loop.every) : 0);
      } else if (st.parallel) {
        sum += Math.max(0, ...(st.parallel.branches ?? []).map((b: any) => this.longestBudget(b)));
      } else if (st.switch) {
        sum += Math.max(0, ...Object.values<any>(st.switch.cases ?? {}).map((c) => this.longestBudget(c)), this.longestBudget(st.switch.default ?? []));
      } else if (st.try) sum += this.longestBudget(st.try);
      else if (st.block) {
        sum += this.longestBudget(st.block.do);
        const branches = Object.values(st.handle ?? {}).filter(Array.isArray) as any[][];
        sum += Math.max(0, ...branches.map((x) => this.longestBudget(x)));
      }
      else if (st.use) sum += this.longestBudget(this.byName.get(st.use)?.do ?? []);
    }
    return sum;
  }

  /** L19: every path a step reads is written earlier, by intake, or is in entry.reads. */
  private checkDataFlow(d: any, warn: (c: string, m: string, s?: string) => void) {
    const avail = new Set<string>([...(d.entry?.reads ?? []), 'messages']);
    if (d.wfKind === 'leg' || d.direction === 'inward') for (const p of ['header', 'payment', 'batch', 'items[]', 'ext']) avail.add(p);
    const has = (p: string) => [...avail].some((a) => coveredByAny([a], p) || coveredByAny([p], a));
    const visit = (steps: any[]) => {
      for (const st of steps ?? []) {
        const a = st.call ? this.byName.get(st.call) : undefined;
        if (a?.kind === 'activity') {
          for (const r of a.reads ?? []) if (!has(r)) warn('L19', `Step ${stepLabel(st)} reads ${r}, which no earlier step writes`, stepLabel(st));
          for (const w of a.writes ?? []) avail.add(w);
          for (const act of Object.values(st.handle ?? {})) if (Array.isArray(act)) visit(act);
        }
        if (st.slot) for (const w of (d.slots ?? []).find((s: any) => s.name === st.slot)?.contract?.writes ?? []) avail.add(w);
        if (st.use) visit(this.byName.get(st.use)?.do ?? []);
        if (st.parallel) for (const b of st.parallel.branches ?? []) visit(b);
        if (st.forEach) visit(st.forEach.do);
        if (st.loop) visit(st.loop.do);
        if (st.try) visit(st.try);
        if (st.switch) {
          for (const c of Object.values<any>(st.switch.cases ?? {})) visit(c);
          visit(st.switch.default ?? []);
        }
        if (st.await) avail.add('messages');
      }
    };
    visit(d.do ?? []);
  }

  private checkHook(d: any, err: (c: string, m: string, s?: string) => void) {
    const host = this.byName.get(d.implements?.of);
    const pt = (host ? this.hookPoints(host) : {})[d.implements?.hookPoint];
    if (!pt) {
      err('L13', `Hook point ${d.implements?.hookPoint} is not declared by ${d.implements?.of}`);
      return;
    }
    if (pt.type !== d.type) err('L13', `Hook is ${d.type} but the hook point is ${pt.type}`);
    const allowedWrites: string[] = d.type === 'transform' ? pt.contract?.writes ?? [] : [];
    for (const st of allSteps(d)) {
      const a = this.byName.get(st.call ?? '');
      if (!a || a.kind !== 'activity') continue;
      for (const w of a.writes ?? []) {
        const inside = allowedWrites.some((x: string) => w === x || w.startsWith(x + '.'));
        if (!inside && !w.startsWith('facts.')) err('L13', `${a.name} writes ${w}, outside the hook point contract`, stepLabel(st));
      }
    }
  }

  private checkScheme(d: any, err: (c: string, m: string) => void, warn: (c: string, m: string) => void) {
    const planned = (d.messages ?? []).filter((m: any) => m.disposition?.planned).map((m: any) => m.id);
    if (planned.length) warn('L10', `Not publishable: ${planned.length} planned message(s): ${planned.join(', ')}`);
    const awaited = new Set<string>();
    for (const w of this.docs) if (w.kind === 'workflow' && scopeOf(w.name) === d.scope) for (const st of allSteps(w)) if (st.await) awaited.add(st.await.message);
    for (const m of d.messages ?? []) if (m.disposition?.deliver && !awaited.has(m.id)) err('L10', `${m.id} is a deliver message but no workflow of ${d.scope} awaits it`);
    // L33 (R-05-42): the dispatch table routes a start message. Its kind, direction and segment select
    // one platform workflow through workflow-select, and the leg it names fills that workflow's slot.
    const select = this.docs.find((x) => x.name === 'p.table.workflow-select.v1');
    for (const m of d.messages ?? []) {
      const st = m.disposition?.start;
      if (!st || !select) continue;
      let chosen: string | undefined;
      try {
        const r = runTable(select, { Header: [{ kind: st.kind, direction: st.direction, segment: st.segment }] }, { resolve: (n) => this.docs.find((x) => x.name === n), evaluationDate: '2026-11-17' });
        chosen = r.decisions.find((x) => x.name === 'workflow')?.value;
      } catch {
        chosen = undefined;
      }
      if (!chosen) {
        err('L33', `${m.id} starts kind ${st.kind ?? '?'}, direction ${st.direction ?? '?'}, segment ${st.segment ?? 'none'}, which workflow-select maps to no single platform workflow`);
        continue;
      }
      for (const legName of [st.leg, st.refuseLeg].filter(Boolean)) {
        const leg = this.docs.find((x) => x.name === legName);
        if (leg && !(leg.implements?.of ?? []).includes(chosen)) err('L33', `${m.id} routes to ${chosen}, but its leg ${legName} fills a slot of ${(leg.implements?.of ?? []).join(', ') || 'nothing'}`);
      }
    }
  }

  private checkRuleSet(d: any, err: (c: string, m: string) => void) {
    if (d.mode === 'validation') {
      for (const r of d.rules ?? []) for (const a of this.byName.get(r)?.then ?? []) if (!a.yield?.finding) err('L12', `Validation set contains ${r}, which yields something other than a finding`);
    }
    if (d.mode === 'decision') {
      // L25: the yield graph of a decision set is acyclic.
      const produces = new Map<string, string[]>();
      const consumes = new Map<string, string[]>();
      for (const r of d.rules ?? []) {
        const rule = this.byName.get(r);
        produces.set(r, (rule?.then ?? []).filter((t: any) => t.yield?.fact).map((t: any) => t.yield.fact.type));
        consumes.set(r, (rule?.when ?? []).map((c: any) => c.match?.fact ?? c.exists?.fact ?? c.not?.fact).filter(Boolean));
      }
      const edges = new Map<string, string[]>();
      for (const [a, ps] of produces) edges.set(a, [...consumes].filter(([, cs]) => cs.some((c) => ps.includes(c))).map(([b]) => b));
      const state = new Map<string, number>();
      const dfs = (n: string): boolean => {
        state.set(n, 1);
        for (const m of edges.get(n) ?? []) {
          if (state.get(m) === 1) return true;
          if (!state.get(m) && dfs(m)) return true;
        }
        state.set(n, 2);
        return false;
      };
      for (const n of edges.keys()) if (!state.get(n) && dfs(n)) {
        err('L25', `The yield graph of ${d.name} has a cycle through ${n}`);
        break;
      }
    }
  }

  private checkTable(d: any, err: (c: string, m: string) => void, warn: (c: string, m: string) => void) {
    const rows = d.rows ?? [];
    if (d.hitPolicy === 'first' && (!rows.length || Object.keys(rows[rows.length - 1].when ?? {}).length !== 0)) err('L25', `A first table must end with a catch-all row`);
    if (d.hitPolicy === 'unique') {
      const rep = analyseTable(d);
      for (const [a, b] of rep.overlaps) err('L25', `Rows ${a + 1} and ${b + 1} overlap in a unique table`);
      if (rep.gaps.length) warn('L25', `${rep.gaps.length} input combination(s) match no row, for example ${JSON.stringify(rep.gaps[0])}`);
    }
  }

  private checkActivity(d: any, err: (c: string, m: string) => void, warn: (c: string, m: string) => void) {
    for (const p of [...(d.reads ?? []), ...(d.writes ?? [])]) {
      if (p.startsWith('ext.') && !this.extensionRoots.some((r) => p === r || p.startsWith(r + '.'))) err('L18', `${p} is not under a registered extension root`);
    }
    if (d.effect === 'write' && ['reserve', 'post'].includes(d.type) && !d.compensation && !/release|reverse|verify|undo/.test(d.name)) {
      warn('L20', `${d.name} writes and names no compensation (R-03-12 SHOULD)`);
    }
    // L28: the script-behind uses only the injected interface.
    if (this.scriptOf && d.implementation?.binding === 'script') {
      const sc = this.scriptOf(d.name);
      if (sc && sc.language === 'typescript') for (const m of lintScript(sc.source)) err('L28', m);
    }
    // L11: unused blocks.
    const reached = this.reached();
    const invokedBy = d.invokedBy ?? 'workflow';
    if (invokedBy === 'workflow' && !reached.has(d.name)) warn('L11', `${d.name} is reached by no workflow, hook or scheme and declares no invokedBy`);
    if (invokedBy !== 'workflow' && reached.has(d.name) && this.calledByWorkflow(d.name)) err('L11', `${d.name} declares invokedBy ${invokedBy} but a workflow calls it`);
  }

  private reachedCache?: Set<string>;
  reached(): Set<string> {
    if (this.reachedCache) return this.reachedCache;
    const r = new Set<string>();
    for (const d of this.docs) {
      if (['workflow', 'hook', 'scheme'].includes(d.kind)) refsOf(d).forEach((x) => r.add(x));
      if (d.kind === 'activity' && (d.invokedBy ?? 'workflow') !== 'workflow') refsOf(d).forEach((x) => r.add(x));
    }
    for (const n of [...r]) this.closure(n).forEach((x) => r.add(x));
    this.reachedCache = r;
    return r;
  }

  private calledByWorkflow(name: string): boolean {
    return this.docs.some((d) => (d.kind === 'workflow' || d.kind === 'hook') && allSteps(d).some((s) => s.call === name));
  }

  private checkConfigurationSet(d: any, err: (c: string, m: string) => void) {
    for (const v of d.values ?? []) {
      const [art, par] = String(v.parameter).split('#');
      const p = this.byName.get(art)?.parameters?.[par];
      if (!p) {
        err('L14', `${v.parameter} is not a declared parameter`);
        continue;
      }
      const b = p.bounds ?? {};
      if (!withinBounds(p, v.value)) err('L14', `${v.parameter} = ${JSON.stringify(v.value)} is outside its bounds ${JSON.stringify(b)}`);
    }
    for (const b of d.bindings ?? []) {
      const [wf, hp] = String(b.hookPoint).split('#');
      const hook = this.byName.get(b.hook);
      if (!this.byName.has(wf) || !this.hookPoints(this.byName.get(wf))[hp]) err('L14', `Binding names an unknown hook point ${b.hookPoint}`);
      else if (!hook || hook.implements?.hookPoint !== hp || hook.implements?.of !== wf) err('L14', `${b.hook} does not implement ${b.hookPoint}`);
      else if (hook.release !== b.release && !this.docs.some((x) => x.name === b.hook && x.release === b.release)) err('L14', `Binding pins ${b.release} but the hook is at ${hook.release}`);
      else {
        // R-06-10: a bank may tighten a hook point's timeout, never loosen it.
        const pt = this.hookPoints(this.byName.get(wf))[hp];
        if (b.timeout && pt.policy?.timeout && durationMs(b.timeout) > durationMs(pt.policy.timeout)) err('L14', `Binding loosens the timeout of ${b.hookPoint}`);
      }
    }
  }

  private checkTest(d: any, err: (c: string, m: string) => void, warn: (c: string, m: string) => void) {
    for (const b of d.bench ?? []) {
      const s = this.byName.get(b.simulator);
      if (s?.kind === 'simulator') {
        if (s.port !== b.port) err('L15', `${b.simulator} does not implement ${b.port}`);
        if (!(s.scenarios ?? []).some((x: any) => x.name === b.scenario)) err('L15', `${b.simulator} has no scenario ${b.scenario}`);
      }
    }
    // L22, second half: every port the target can reach is bound in the bench.
    const reachable = new Set<string>();
    const seen = new Set<string>();
    const visit = (n: string) => {
      if (seen.has(n)) return;
      seen.add(n);
      const x = this.byName.get(n);
      if (!x) return;
      if (x.kind === 'activity') {
        for (const p of x.ports ?? []) reachable.add(p);
        if (x.compensation) visit(x.compensation);
        return;
      }
      if (x.kind === 'workflow' || x.kind === 'hook') {
        for (const st of allSteps(x)) {
          if (st.call) visit(st.call);
          if (st.use) visit(st.use);
        }
        for (const hp of x.hookPoints ?? []) if (hp.policy?.onHold) visit(hp.policy.onHold);
      }
    };
    visit(d.target);
    const bound = new Set((d.bench ?? []).map((b: any) => b.port));
    const unbound = [...reachable].filter((p) => !bound.has(p)).sort();
    if (unbound.length && d.level !== 'ruleset') warn('L22', `Ports the target can reach are not in the bench: ${unbound.join(', ')}. The run uses their first simulator in scenario accept.`);
  }

  private checkSupersession(d: any, err: (c: string, m: string) => void) {
    const art = this.byName.get(d.artefact);
    if (!art) {
      err('L27', `${d.artefact} does not resolve`);
      return;
    }
    const o = parseRelease(d.replaces) ?? [0, 0, 0];
    const n = parseRelease(d.with) ?? [0, 0, 0];
    if (n[0] !== o[0] || compareReleases(d.with, d.replaces) <= 0) err('L27', `${d.replaces} to ${d.with} is not a forward move within one major line`);
    const inScope = new Set<string>();
    for (const wr of d.scope?.workflowReleases ?? []) {
      const [wf, rel] = String(wr).split('@');
      inScope.add(`${wf}@${rel}`);
      const w = this.byName.get(wf);
      if (!w || w.kind !== 'workflow' || w.release !== rel) err('L27', `${wr} is not a workflow release in this repository`);
      else if (!this.closure(wf).has(d.artefact)) err('L27', `${wr} does not use ${d.artefact}, so there is nothing to supersede`);
    }
    for (const t of d.evidence?.testRuns ?? []) if (!inScope.has(`${t.workflow}@${t.release}`)) err('L27', `Evidence names ${t.workflow}@${t.release}, which is outside the scope`);
    if (d.approval?.maker === d.approval?.checker) err('L27', 'Maker and checker are the same person');
  }

  /** L23: an exported document pins every artefact in its closure, exactly. */
  private checkPins(d: any, err: (c: string, m: string) => void) {
    const pinned = new Map<string, any>((d.artefacts ?? []).map((a: any) => [a.name, a]));
    for (const n of pinnable(this, d)) if (!pinned.has(n)) err('L23', `${n} is used but not pinned`);
    for (const a of d.artefacts ?? []) {
      if (!a.release || !/^\d+\.\d+\.\d+$/.test(a.release)) err('L23', `${a.name} is not pinned to an exact release`);
      if (!a.hash || !/^sha256:[0-9a-f]{64}$/.test(a.hash)) err('L23', `${a.name} has no content hash`);
    }
  }

  checkAll(): Issue[] {
    const out: Issue[] = [];
    for (const d of this.docs) out.push(...this.check(d));
    return out;
  }
}

/** What an exported workflow or hook must pin: its closure of blocks, rules and segments. Legs and ports are pinned on their own. */
export function pinnable(c: Checker, d: any): string[] {
  const out = new Set<string>();
  const visit = (doc: any) => {
    for (const r of refsOf(doc)) {
      const x = c.byName.get(r);
      if (!x || out.has(r)) continue;
      if (['activity', 'rule', 'ruleset', 'table'].includes(x.kind) || (x.kind === 'workflow' && (x.wfKind === 'segment' || x.wfKind === 'subflow'))) {
        out.add(r);
        visit(x);
      }
    }
  };
  visit(d);
  return [...out].sort();
}

// ---------------------------------------------------------- semver (L26)

export interface InterfaceDiff {
  required: 'major' | 'minor' | 'patch' | 'none';
  changes: { part: string; change: string; impact: 'major' | 'minor' | 'patch' }[];
}

/** The interface of an artefact (R-01-09) and the semver its change implies (R-01-08, L26). */
/**
 * Whether a value lies inside its parameter's bounds (R-04-08, R-06-21): min and max for numbers and durations, allowed and
 * pattern for codes and strings, and each item of a list (each field of an item that is an object). The same rule as
 * tools/validate.py's within_bounds.
 */
export function withinBounds(decl: any, value: any): boolean {
  const b = decl?.bounds ?? {};
  if (decl?.type === 'list') return Array.isArray(value) && value.every((x) => withinBounds({ type: 'item', bounds: b }, x));
  if (value && typeof value === 'object' && !Array.isArray(value)) return Object.values(value).every((x) => withinBounds({ type: 'item', bounds: b }, x));
  if (decl?.type === 'duration') {
    if (typeof value !== 'string' || !/^P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+S)?)?$/.test(value) || value === 'P' || value === 'PT') return false;
    const v = durationMs(value);
    if ((b.min !== undefined && v < durationMs(b.min)) || (b.max !== undefined && v > durationMs(b.max))) return false;
  } else if (typeof value === 'number') {
    if ((b.max !== undefined && value > b.max) || (b.min !== undefined && value < b.min)) return false;
  }
  if (b.allowed && !b.allowed.includes(value)) return false;
  if (b.pattern && typeof value === 'string' && !new RegExp(b.pattern).test(value)) return false;
  return true;
}

export function interfaceDiff(prev: any, next: any): InterfaceDiff {
  const changes: InterfaceDiff['changes'] = [];
  const set = (x: any) => new Set<string>((x ?? []).map((v: any) => (typeof v === 'string' ? v : v.name ?? JSON.stringify(v))));
  const cmp = (part: string, a: any, b: any, addImpact: 'major' | 'minor', removeImpact: 'major' | 'minor' = 'major') => {
    const A = set(a);
    const B = set(b);
    for (const x of B) if (!A.has(x)) changes.push({ part, change: `adds ${x}`, impact: addImpact });
    for (const x of A) if (!B.has(x)) changes.push({ part, change: `removes ${x}`, impact: removeImpact });
  };
  cmp('outcomes', prev.outcomes ?? prev.exit?.outcomes, next.outcomes ?? next.exit?.outcomes, 'major');
  cmp('reads', prev.reads ?? prev.entry?.reads, next.reads ?? next.entry?.reads, 'major');
  cmp('writes', prev.writes ?? prev.exit?.writes, next.writes ?? next.exit?.writes, 'major');
  cmp('errors', prev.errors, next.errors, 'major');
  cmp('ports', prev.ports, next.ports, 'major');
  cmp('parameters', Object.keys(prev.parameters ?? {}), Object.keys(next.parameters ?? {}), 'minor');
  cmp('slots', prev.slots, next.slots, 'major');
  // A hook point is part of the interface: adding one is a minor; changing or removing one is a major (R-05-10).
  cmp('hookPoints', prev.hookPoints, next.hookPoints, 'minor');
  for (const hp of prev.hookPoints ?? []) {
    const n = (next.hookPoints ?? []).find((x: any) => x.name === hp.name);
    if (n && JSON.stringify(n) !== JSON.stringify(hp)) changes.push({ part: 'hookPoints', change: `changes ${hp.name}`, impact: 'major' });
  }
  if (JSON.stringify(stripMeta(prev)) !== JSON.stringify(stripMeta(next)) && changes.length === 0) changes.push({ part: 'body', change: 'changes function, not interface', impact: 'minor' });
  const order = { none: 0, patch: 1, minor: 2, major: 3 } as const;
  let required: InterfaceDiff['required'] = changes.length ? 'patch' : 'none';
  for (const c of changes) if (order[c.impact] > order[required]) required = c.impact;
  return { required, changes };
}

function stripMeta(d: any) {
  const { release, provenance, title, description, ...rest } = d ?? {};
  void release; void provenance; void title; void description;
  return rest;
}

export function releaseBump(prev: string, next: string): 'major' | 'minor' | 'patch' | 'none' | 'backwards' {
  const a = parseRelease(prev) ?? [0, 0, 0];
  const b = parseRelease(next) ?? [0, 0, 0];
  if (compareReleases(next, prev) <= 0) return compareReleases(next, prev) === 0 ? 'none' : 'backwards';
  if (b[0] > a[0]) return 'major';
  if (b[1] > a[1]) return 'minor';
  return 'patch';
}

export function proposeRelease(prev: string, required: InterfaceDiff['required']): string {
  const [M, m, p] = parseRelease(prev) ?? [1, 0, 0];
  if (required === 'major') return `${M + 1}.0.0`;
  if (required === 'minor') return `${M}.${m + 1}.0`;
  return `${M}.${m}.${p + 1}`;
}

/** L26 for a draft against the latest published release of its line. A major goes in the name, so a major change needs a new line. */
export function semverIssue(prev: any | undefined, next: any): Issue | undefined {
  if (!prev) return undefined;
  const diff = interfaceDiff(prev, next);
  const bump = releaseBump(prev.release, next.release);
  const order = { none: 0, backwards: -1, patch: 1, minor: 2, major: 3 } as const;
  if (diff.required === 'major') {
    return { code: 'L26', severity: 'error', key: next.name, message: `The interface changed (${diff.changes.filter((c) => c.impact === 'major').map((c) => `${c.part} ${c.change}`).join('; ')}). That is a major: release it as a new major line ${next.name.replace(/\.v\d+$/, `.v${(parseRelease(prev.release)?.[0] ?? 1) + 1}`)}.` };
  }
  if (order[bump] < order[diff.required]) return { code: 'L26', severity: 'error', key: next.name, message: `The change needs at least a ${diff.required}; ${prev.release} to ${next.release} is a ${bump}. Propose ${proposeRelease(prev.release, diff.required)}.` };
  return undefined;
}

/**
 * L34 (R-12-11): the factory holds only the packs its inventory lists. Every folder under factory/ is a
 * listed shared pack, every listed shared pack has a folder, and every artefact in a pack's folder is in
 * that pack's scope. `files` are the spec documents with their paths relative to the spec root.
 */
export function factoryPackIssues(inventory: any | undefined, folders: string[], files: { path: string; doc: any }[]): Issue[] {
  const out: Issue[] = [];
  const err = (key: string, message: string, file?: string) => out.push({ code: 'L34', severity: 'error', key, message, file });
  if (!inventory) {
    err('factory', 'The factory has no inventory', 'factory/inventory.yaml');
    return out;
  }
  const listed = new Set<string>((inventory.packs ?? []).filter((p: any) => p.packType === 'shared').map((p: any) => p.scope));
  for (const f of [...folders].sort()) if (!listed.has(f)) err(f, `${f} is not a pack the factory inventory lists`, `factory/${f}/`);
  for (const s of [...listed].sort()) if (!folders.includes(s)) err(s, `The inventory lists pack ${s}, which has no folder under factory/`, 'factory/inventory.yaml');
  for (const { path: file, doc } of files) {
    const parts = file.split(/[\\/]/);
    if (parts[0] !== 'factory' || parts.length < 3 || !listed.has(parts[1])) continue;
    const own = doc.kind === 'pack' || doc.kind === 'scheme' ? doc.scope : doc.kind === 'test' ? undefined : doc.name ? scopeOf(doc.name) : undefined;
    if (own && own !== parts[1]) err(doc.name ?? doc.scope, `${own} content is in the folder of pack ${parts[1]}`, file);
  }
  return out;
}

export function checkAll(env: StudioEnv): Issue[] {
  const c = Checker.fromEnv(env);
  const issues = c.checkAll();
  // The factory as the seed published it (R-11-25): each entry names its source in spec/factory, so
  // the pack folders are read from the branch, not from spec/ on disk.
  const spec = env.store.all().filter((e) => e.source === 'spec');
  const seeded = spec.filter((e) => e.path.split(/[\\/]/)[0] === 'factory');
  if (seeded.length) {
    const folders = [...new Set(seeded.map((e) => e.path.split(/[\\/]/)).filter((p) => p.length > 2).map((p) => p[1]))].sort();
    const inventory = seeded.find((e) => e.kind === 'inventory')?.doc;
    issues.push(...factoryPackIssues(inventory, folders, spec.map((e) => ({ path: e.path, doc: e.doc }))));
  }
  for (const e of env.store.all().filter((x) => x.status === 'published')) {
    for (const s of env.schemas.validate(e.doc)) issues.push({ code: 'L00', severity: 'error', key: e.name, message: s.message, file: e.path });
  }
  issues.push(...literalSecretIssues(env));
  return issues;
}

/**
 * L37 (R-06-23): every file on the content branch (bank bindings and configuration, artefacts, released packages) and
 * every document in the store names its secrets by reference only, and holds no private key.
 */
export function literalSecretIssues(env: StudioEnv): Issue[] {
  const out: Issue[] = [];
  const files = env.store.content?.files;
  if (files) {
    for (const [p, text] of [...files].sort(([a], [b]) => a.localeCompare(b)))
      for (const s of literalSecretsInText(text, p)) out.push({ code: 'L37', severity: 'error', key: p, step: s.at || undefined, message: `${p}${s.at ? ' at ' + s.at : ''} ${s.why}`, file: p });
  } else {
    for (const e of env.store.all())
      for (const s of literalSecretsInText(JSON.stringify(e.doc), `${e.path}.json`)) out.push({ code: 'L37', severity: 'error', key: e.name, step: s.at || undefined, message: `${e.path}${s.at ? ' at ' + s.at : ''} ${s.why}`, file: e.path });
  }
  return out;
}

/**
 * Check one document. `alongside` are documents taken as present with it: the tenant's other drafts
 * while it is edited, or the rest of a change set at publish.
 */
export function checkDocument(env: StudioEnv, doc: any, tenant?: string, alongside: any[] = []): Issue[] {
  const issues: Issue[] = env.schemas.validate(doc).map((s) => ({ code: 'L00', severity: 'error' as const, key: doc?.name ?? doc?.id ?? '', message: s.message + (s.hint ? ` ${s.hint}` : ''), step: s.path }));
  try {
    const self = `${doc?.kind}:${doc?.name ?? doc?.scope ?? doc?.id}`;
    const c = Checker.fromEnv(env, [...alongside.filter((d) => `${d?.kind}:${d?.name ?? d?.scope ?? d?.id}` !== self), doc], tenant);
    issues.push(...c.check(doc));
    const prev = doc?.name ? env.store.latest(doc.name, tenant)?.doc : undefined;
    if (prev && prev.release !== doc.release) {
      const s = semverIssue(prev, doc);
      if (s) issues.push(s);
    }
  } catch (e: any) {
    issues.push({ code: 'L00', severity: 'error', key: doc?.name ?? '', message: `The checks could not run: ${e?.message ?? e}` });
  }
  return issues;
}
