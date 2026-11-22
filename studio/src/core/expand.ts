import { clone, contentHash } from './canonical.js';

// Expansion of composite steps (Part 5, 5.6; C04-S6; R-05-35). A pure function: the same input
// gives the same output. For each step that calls a composite activity it
//   1. takes the composite's subflow at its pinned release, expanded in its turn;
//   2. prefixes each inner step id, and each outcomeOf that names one, with the calling step's id;
//   3. gives an await with no timeout the reply time of the calling step: the step's policy, else the activity's;
//   4. wraps the result in a block that carries the calling step's id and its handle.
// Segments are inlined by the interpreter as it runs (OP-3); a `use` step is left as it is.

export type DocResolver = (name: string) => any;

export class ExpansionError extends Error {
  constructor(public step: string | undefined, message: string) {
    super(message);
  }
}

export function isComposite(a: any): boolean {
  return !!a && a.kind === 'activity' && a.implementation?.binding === 'subflow';
}

/** Every (holder, key) in a step whose value is a step list. */
function* nested(st: any): Generator<[any, string | number]> {
  for (const [k, a] of Object.entries(st.handle ?? {})) if (Array.isArray(a)) yield [st.handle, k];
  if (st.switch) {
    for (const k of Object.keys(st.switch.cases ?? {})) yield [st.switch.cases, k];
    if ('default' in st.switch) yield [st.switch, 'default'];
  }
  if (st.parallel) for (let i = 0; i < (st.parallel.branches ?? []).length; i++) yield [st.parallel.branches, i];
  for (const k of ['forEach', 'loop', 'block']) if (st[k]) yield [st[k], 'do'];
  if (st.await && Array.isArray(st.await.onTimeout)) yield [st.await, 'onTimeout'];
  if (st.try) {
    yield [st, 'try'];
    for (const c of Array.isArray(st.catch) ? st.catch : []) if (Array.isArray(c?.do)) yield [c, 'do'];
  }
}

export function walkSteps(steps: any[] | undefined, out: any[] = []): any[] {
  for (const st of steps ?? []) {
    out.push(st);
    for (const [h, k] of nested(st)) walkSteps(h[k], out);
  }
  return out;
}

/** The await's wait as the composite gives it: its replyTimeout and calendar, and the parameters a bank may set them by (R-05-43). */
function replyWait(aw: any, policy: any, owner?: string): any {
  const out: any = { ...aw, timeout: policy.replyTimeout };
  if (policy.replyCalendar && !('calendar' in out)) out.calendar = policy.replyCalendar;
  if (owner && policy.replyTimeoutParameter) out.timeoutParameter = `${owner}#${policy.replyTimeoutParameter}`;
  if (owner && policy.replyCalendarParameter && 'calendar' in out) out.calendarParameter = `${owner}#${policy.replyCalendarParameter}`;
  return Object.fromEntries(['message', 'correlateBy', 'timeout', 'timeoutParameter', 'calendar', 'calendarParameter', 'onTimeout'].filter((k) => k in out).map((k) => [k, out[k]]));
}

/** The composite's policy under the calling step's: a step that fixes the wait itself overrides the bank's parameter. */
function stepPolicyOver(act: any, stepPolicy?: any): any {
  const policy = { ...(act?.policy ?? {}), ...(stepPolicy ?? {}) };
  if (stepPolicy && 'replyTimeout' in stepPolicy) delete policy.replyTimeoutParameter;
  if (stepPolicy && 'replyCalendar' in stepPolicy) delete policy.replyCalendarParameter;
  return policy;
}

function rename(steps: any[], prefix: string, policy: any, owner?: string): any[] {
  const inner = new Set(walkSteps(steps).filter((s) => s.id).map((s) => s.id));
  for (const st of walkSteps(steps)) {
    if (st.id) st.id = `${prefix}.${st.id}`;
    const refs = [st.loop?.until, st.switch?.by];
    for (const ref of refs) if (ref?.outcomeOf && inner.has(ref.outcomeOf)) ref.outcomeOf = `${prefix}.${ref.outcomeOf}`;
    if (st.await && !('timeout' in st.await)) {
      if (!policy.replyTimeout) throw new ExpansionError(prefix, `${prefix}: an await has no timeout and the calling step has no replyTimeout (L32)`);
      st.await = replyWait(st.await, policy, owner);
    }
  }
  return steps;
}

function expandSteps(steps: any[], resolve: DocResolver, stack: string[]): any[] {
  const out: any[] = [];
  for (const s of steps ?? []) {
    const st = clone(s);
    for (const [h, k] of [...nested(st)]) h[k] = expandSteps(h[k], resolve, stack);
    const act = st.call ? resolve(st.call) : undefined;
    if (st.call && isComposite(act)) {
      const sub = resolve(act.implementation.subflow);
      if (!sub) throw new ExpansionError(st.id, `The subflow ${act.implementation.subflow} of ${act.name} does not resolve`);
      if (stack.includes(act.name)) throw new ExpansionError(st.id, `The composite ${act.name} calls itself through its subflow`);
      if (!st.id) throw new ExpansionError(undefined, `A step that calls the composite ${act.name} needs an id (L30)`);
      const policy = stepPolicyOver(act, st.policy);
      const body = rename(expandSteps(clone(sub.do), resolve, [...stack, act.name]), st.id, policy, act.name);
      const block: any = { id: st.id, block: { of: act.name, by: sub.name, do: body } };
      if (st.handle && Object.keys(st.handle).length) block.handle = st.handle;
      out.push(block);
    } else out.push(st);
  }
  return out;
}

/** Does a workflow call a composite anywhere? */
export function callsComposite(doc: any, resolve: DocResolver): boolean {
  return [...walkSteps(doc.do), ...walkSteps(doc.finally), ...walkSteps(doc.deadline?.do)].some((s) => s.call && isComposite(resolve(s.call)));
}

/**
 * The expanded form of a workflow, carrying the authored release and hash it came from
 * (R-05-36). A workflow that calls no composite is returned as it is.
 */
export function expandWorkflow(doc: any, resolve: DocResolver): any {
  if (!doc || doc.kind !== 'workflow' || doc.form === 'expanded' || !callsComposite(doc, resolve)) return doc;
  const parts: any = {};
  if (doc.do) parts.do = expandSteps(doc.do, resolve, []);
  if (doc.finally) parts.finally = expandSteps(doc.finally, resolve, []);
  if (doc.deadline) parts.deadline = { ...doc.deadline, do: expandSteps(doc.deadline.do ?? [], resolve, []) };
  return { ...doc, ...parts, form: 'expanded', expandedFrom: { release: doc.release, hash: contentHash(doc) } };
}

/** Run a subflow alone, as a composite's own test does: its steps keep their own ids. */
export function expandSubflow(sub: any, resolve: DocResolver, act: any, stepPolicy?: any): any {
  const policy = stepPolicyOver(act, stepPolicy);
  const body = expandSteps(clone(sub.do), resolve, [act?.name ?? sub.name]);
  for (const st of walkSteps(body)) {
    if (st.await && !('timeout' in st.await) && policy.replyTimeout) st.await = replyWait(st.await, policy, act?.name);
  }
  return { ...sub, do: body, form: 'expanded', expandedFrom: { release: sub.release, hash: contentHash(sub) } };
}

/** What must hold of every expanded workflow (the check in tools/expand.py). */
export function checkExpanded(doc: any, resolve: DocResolver): string[] {
  const problems: string[] = [];
  const steps = [...walkSteps(doc.do), ...walkSteps(doc.finally), ...walkSteps(doc.deadline?.do)];
  const ids = steps.filter((s) => s.id).map((s) => s.id);
  for (const id of [...new Set(ids.filter((i, n) => ids.indexOf(i) !== n))].sort()) problems.push(`step id ${id} is used twice`);
  for (const st of steps) {
    if (st.call && isComposite(resolve(st.call))) problems.push(`step ${st.id} still calls the composite ${st.call}`);
    if (st.await && !('timeout' in st.await)) problems.push(`await ${st.id} has no timeout`);
    if (st.block) {
      const outs = new Set<string>((resolve(st.block.of)?.outcomes ?? []).map((o: any) => o.name));
      const given = new Set<string>();
      for (const s of walkSteps(st.block.do)) {
        if ('return' in s) given.add(s.return);
        for (const a of [...Object.values(s.handle ?? {}), s.await?.onTimeout]) if (a && !Array.isArray(a) && typeof a === 'object' && 'return' in (a as any)) given.add((a as any).return);
      }
      for (const o of [...given].filter((g) => !outs.has(g)).sort()) problems.push(`block ${st.id} returns ${o}, which ${st.block.of} does not declare`);
    }
  }
  return problems;
}
