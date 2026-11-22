// BPMN 2.0 generation for Flowable (C22). The engine receives the expanded form (R-09-17).
//
// What maps how (Part 9, 9.3 and 9.4; the patterns were chosen in the C22-S1 spike, see
// studio/engines/flowable/README.md):
// - Every activity is an external worker task, a wait state, so timer boundaries apply (R-09-12).
//   Its topic is `<queue>:<name>@<release>` (R-09-04, R-09-05).
// - A call is an embedded sub-process that holds the attempt loop: the attempt timeout is a timer
//   boundary on the task, a retryable error a BPMN error the worker throws, the backoff a timer
//   in a modelled loop, the total timeout a timer boundary on the sub-process. A spent loop or a
//   total timeout gives the unknown outcome where the activity declares it.
// - handle and switch are exclusive gateways, parallel is a parallel gateway, forEach is a
//   multi-instance sub-process, loop is a modelled loop with a timer, slot and hook are call
//   activities, block is an embedded sub-process whose error end events set the outcome.
// - await is an event-based gateway between a message catch and a timer. A message that arrives
//   before the catch is waiting is held by the binding (R-09-16).
// - end and return are BPMN errors caught on the boundary of the main sub-process and of the
//   block. Every end goes through the finally path before the end event (R-05-14).
// - A completed write registers a compensation handler; compensation is thrown in reverse order.
// - The four signals are event sub-processes, with a gate before every step for pause.
// - Elements carry `th:` annotations for the binding: the worker reads the step, the activity
//   and its policy; the business trace is written at `th:point` elements.
// The output is reproducible: the same input gives the same bytes (R-09-15).

import { effectivePolicy, isRetryable, retryInterval } from '../interp/policy.js';

export interface Ctx {
  queueOf: (name: string) => string;
  resolve: (name: string) => any;
  /**
   * Who runs the binding. `embedded` (the default, the kit's Java harness): the binding listens to
   * the engine in its process and writes the trace as elements start. `rest` (a deployment on the
   * Flowable REST server, D3): the binding is an external worker too, so every place it acts on is a
   * wait state, an external worker task on the topic BINDING_TOPIC, and it sets what the flow reads
   * when it completes that task. The flow is the same in both.
   */
  binding?: 'embedded' | 'rest';
}

export const TH_NS = 'urn:transacthub:flowable:1';
export const SIGNAL_MESSAGES: Record<string, string> = { pause: 'th.signal.pause', resume: 'th.signal.resume', cancel: 'th.signal.cancel', 're-screen': 'th.signal.re-screen' };
export const COMPENSATED_MESSAGE = 'th.compensated';
/** The topic of the binding's own tasks when the binding is a REST worker (Ctx.binding 'rest'). */
export const BINDING_TOPIC = 'th.binding';
const RESERVED = new Set(['start', 'main', 'deadline', 'end']);

interface Frag { entry: string; exits: string[] }
type Rec = 'business' | 'detail' | 'finally';

interface Opts {
  record: Rec;
  item: boolean;
  hookOwner: string;
  tryScope?: TryScope;
}

interface TryScope { markers: string[]; parent?: TryScope }

function esc(s: any) {
  return String(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export function safe(s: string) {
  return String(s).replace(/[^A-Za-z0-9_]/g, '_');
}

function shortName(n: string) {
  const p = String(n).split('.');
  return p.length > 2 ? p[p.length - 2] : n;
}

const v = (name: string) => `execution.getVariable('${name}')`;
const setVar = (name: string, value: string) => `\${execution.setVariable('${name}', '${value}')}`;
const outcomeVar = (id: string) => `outcome_${safe(id)}`;
const isoMs = (ms: number) => `PT${Number((ms / 1000).toFixed(3))}S`;

/** Shared state of one generated document: ids, errors, messages and process-level parts. */
class Gen {
  n = 0;
  errors = new Map<string, string>(); // id -> code
  messages = new Map<string, string>(); // id -> name
  usedIds = new Set<string>();
  signals = new Set<string>();
  endpoints = new Map<string, string>(); // outcome -> endpoint node at process level
  processLevel: string[] = []; // event sub-processes at process level (notify hooks)
  markers: { id: string; tries: TryScope[] }[] = []; // compensation markers in document order
  screenSteps: { st: any; opts: Opts }[] = [];
  readonly rest: boolean;
  constructor(readonly wf: any, readonly ctx: Ctx) {
    this.rest = ctx.binding === 'rest';
  }

  /**
   * A place the binding acts: a none event its listener sees start (embedded), or an external worker
   * task on the binding's topic, a wait state the binding completes (rest).
   */
  pointElement(id: string, name: string | undefined, inner: string) {
    const nm = name ? ` name="${esc(name)}"` : '';
    return this.rest
      ? `<serviceTask id="${id}"${nm} flowable:type="external-worker" flowable:topic="${BINDING_TOPIC}" flowable:exclusive="false">${inner}</serviceTask>`
      : `<intermediateThrowEvent id="${id}"${nm}>${inner}</intermediateThrowEvent>`;
  }

  /** A called process tells a REST binding who called it, and from where (`slot:<step>`, `hook:<point>`). */
  callerMappings(via: string) {
    return this.rest ? `<flowable:in sourceExpression="\${execution.processInstanceId}" target="th_parent"/><flowable:in sourceExpression="\${'${esc(via)}'}" target="th_via"/>` : '';
  }

  /** Call activities of a REST deployment call the processes of their own deployment. */
  get sameDeployment() {
    return this.rest ? ' flowable:sameDeployment="true"' : '';
  }

  id(prefix: string) {
    this.n += 1;
    return `th_${prefix}_${this.n}`;
  }

  /** The element id for a step: the step id itself, made unique and valid. */
  stepId(id: string, suffix = '') {
    let base = /^[A-Za-z_][A-Za-z0-9_.-]*$/.test(id) ? id : `s_${safe(id)}`;
    if (RESERVED.has(base) || base.startsWith('th_')) base = `step_${base}`;
    base += suffix;
    let out = base;
    for (let i = 2; this.usedIds.has(out); i++) out = `${base}_${i}`;
    this.usedIds.add(out);
    return out;
  }

  error(code: string, id = `err_${safe(code)}`) {
    this.errors.set(id, code);
    return id;
  }

  message(name: string) {
    const id = `msg_${safe(name)}`;
    this.messages.set(id, name);
    return id;
  }

  endpoint(outcome: string) {
    let id = this.endpoints.get(outcome);
    if (!id) {
      id = `th_endpoint_${safe(outcome)}`;
      this.endpoints.set(outcome, id);
    }
    return id;
  }

  resolveAct(name: string) {
    return this.ctx.resolve(name);
  }
}

function ann(tag: string, attrs: Record<string, any>) {
  const a = Object.entries(attrs).filter(([, x]) => x !== undefined && x !== null && x !== '').map(([k, x]) => ` ${k}="${esc(typeof x === 'string' ? x : JSON.stringify(x))}"`).join('');
  return `<th:${tag}${a}/>`;
}

function ext(...parts: (string | undefined)[]) {
  const p = parts.filter(Boolean);
  return p.length ? `<extensionElements>${p.join('')}</extensionElements>` : '';
}

/**
 * A duration as a step names it (R-05-45): a literal, or a parameter of the composite named in full by the
 * expansion, `{ parameter: '<activity>#<name>', default }`. The timer is written with the default, and a
 * parameter is marked `th:timer parameter default`: the binding takes the tenant's value in force on the
 * business date the timer starts, else the default, and places the timer on its clock by that value.
 */
export function timerDuration(v: any, step: string): { literal: string; parameter?: string; default?: string } {
  if (typeof v === 'string') return { literal: v };
  if (v && typeof v === 'object' && typeof v.parameter === 'string' && typeof v.default === 'string') {
    if (!v.parameter.includes('#')) throw new Error(`Step ${step}: the duration parameter ${v.parameter} is not named in full; generate from the expanded form`);
    return { literal: v.default, parameter: v.parameter, default: v.default };
  }
  throw new Error(`Step ${step}: a duration must be an ISO 8601 duration or a parameter with a default, not ${JSON.stringify(v)}`);
}

const listener = (expr: string, event = 'start') => `<flowable:executionListener event="${event}" expression="${esc(expr)}"/>`;

/** One container (the process, or a sub-process): its elements and flows. */
class B {
  els: string[] = [];
  flows: string[] = [];
  returns = new Set<string>();
  assocs: string[] = []; // artifacts come after every flow element (BPMN schema)
  constructor(readonly g: Gen, readonly o: Opts, readonly parent?: B) {}

  body(): string[] {
    return [...this.els, ...this.flows, ...this.assocs];
  }

  child(o: Partial<Opts> = {}) {
    return new B(this.g, { ...this.o, ...o }, this);
  }

  flow(from: string, to: string, cond?: string, name?: string, id?: string) {
    const fid = id ?? this.g.id('flow');
    const nm = name ? ` name="${esc(name)}"` : '';
    this.flows.push(cond
      ? `<sequenceFlow id="${fid}" sourceRef="${from}" targetRef="${to}"${nm}><conditionExpression xsi:type="tFormalExpression"><![CDATA[${cond}]]></conditionExpression></sequenceFlow>`
      : `<sequenceFlow id="${fid}" sourceRef="${from}" targetRef="${to}"${nm}/>`);
  }

  /** A none event that only marks a place: the binding writes the trace there. */
  point(attrs: Record<string, any>, name?: string, extra = '') {
    const id = this.g.id('rec');
    this.els.push(this.g.pointElement(id, name, ext(ann('point', attrs), extra)));
    return id;
  }

  /** A place only a REST binding needs (it has no listener): null when the binding is embedded. */
  restPoint(attrs: Record<string, any>): string | null {
    return this.g.rest ? this.point(attrs) : null;
  }

  /** A none event that sets a variable on the way through. */
  setter(name: string, value: string) {
    const id = this.g.id('set');
    this.els.push(`<intermediateThrowEvent id="${id}">${ext(listener(setVar(name, value)))}</intermediateThrowEvent>`);
    return id;
  }

  gateway(id: string, name?: string, deflt?: string) {
    this.els.push(`<exclusiveGateway id="${id}"${name ? ` name="${esc(name)}"` : ''}${deflt ? ` default="${deflt}"` : ''}/>`);
    return id;
  }

  /** End the flow with an outcome: an error in main, a flow to the endpoint at process level. */
  endWith(outcome: string, from: string, cond?: string, name?: string, compensate = false) {
    let src = from;
    if (compensate) {
      const c = this.compensation(this.o.tryScope);
      if (c) {
        this.flow(src, c.entry, cond, name);
        src = c.exit;
        cond = undefined;
        name = undefined;
      }
    }
    for (let b: B | undefined = this; b; b = b.parent) b.endsHere.add(outcome);
    const id = this.g.id('end');
    this.els.push(`<endEvent id="${id}" name="end:${esc(outcome)}"><errorEventDefinition errorRef="${this.g.error(`end:${outcome}`, `err_end_${safe(outcome)}`)}"/></endEvent>`);
    this.flow(src, id, cond, name);
  }

  endsHere = new Set<string>();
  failsHere = false;

  returnWith(outcome: string, from: string, cond?: string, name?: string) {
    this.returns.add(outcome);
    const id = this.g.id('return');
    this.els.push(`<endEvent id="${id}" name="return:${esc(outcome)}"><errorEventDefinition errorRef="${this.g.error(`return:${outcome}`, `ret_${safe(outcome)}`)}"/></endEvent>`);
    this.flow(from, id, cond, name);
  }

  /** A step failure (StepFailure in the interpreter): an error that ends the instance failed. */
  fail(from: string, cond?: string, name?: string, flowId?: string) {
    for (let b: B | undefined = this; b; b = b.parent) b.failsHere = true;
    const id = this.g.id('fail');
    this.els.push(`<endEvent id="${id}" name="failed"><errorEventDefinition errorRef="${this.g.error('th:failed', 'err_th_failed')}"/></endEvent>`);
    this.flow(from, id, cond, name, flowId);
  }

  /**
   * Compensation of the completed writes of a scope, in reverse order (R-05-15). One throw per
   * write step names its marker; the engine orders the instances of each by completion. Flowable
   * 7.1 does not wait for the handlers (waitForCompletion is not implemented), so the flow waits
   * on a message the binding sends when no handler is left.
   */
  compensation(scope?: TryScope): { entry: string; exit: string } | null {
    const markers = this.g.markers.filter((m) => !scope || m.tries.includes(scope)).map((m) => m.id).reverse();
    if (!markers.length) return null;
    let entry = '';
    let prev = '';
    for (const m of markers) {
      const id = this.g.id('compensate');
      this.els.push(`<intermediateThrowEvent id="${id}" name="compensate ${esc(m)}"><compensateEventDefinition activityRef="${m}"/></intermediateThrowEvent>`);
      if (prev) this.flow(prev, id);
      else entry = id;
      prev = id;
    }
    const wait = this.g.id('compensated');
    this.els.push(`<intermediateCatchEvent id="${wait}" name="compensated">${ext(ann('point', { kind: 'compensationWait' }))}<messageEventDefinition messageRef="${this.g.message(COMPENSATED_MESSAGE)}"/></intermediateCatchEvent>`);
    this.flow(prev, wait);
    return { entry, exit: wait };
  }

  /** The action for a non-primary outcome: steps, end, return, or continue (null). */
  action(a: any, from: string, cond: string | undefined, label: string): string[] | null {
    if (Array.isArray(a)) {
      const f = this.steps(a);
      if (!f) return null;
      this.flow(from, f.entry, cond, label);
      return f.exits;
    }
    if (a && typeof a === 'object' && 'end' in a) {
      this.endWith(a.end, from, cond, label, a.compensate === 'all' || a.compensate === true);
      return [];
    }
    if (a && typeof a === 'object' && 'return' in a) {
      this.returnWith(a.return, from, cond, label);
      return [];
    }
    return null;
  }

  steps(steps: any[]): Frag | null {
    let first: string | undefined;
    let exits: string[] = [];
    let open = true;
    for (const st of steps ?? []) {
      if (!open) break; // nothing after an end or a return is reachable
      const f = this.step(st);
      if (!f) continue;
      if (!first) first = f.entry;
      for (const e of exits) this.flow(e, f.entry);
      exits = f.exits;
      open = exits.length > 0;
    }
    return first ? { entry: first, exits } : null;
  }

  /** A gate before every step: a paused instance waits here for resume (R-05-12). */
  step(st: any): Frag | null {
    const real = this.stepBody(st);
    if (!real) return null;
    const gate = this.g.id('gate');
    const wait = this.g.id('paused');
    // A REST binding has no listener on the gate: a signal it holds is written at the next point.
    this.els.push(`<exclusiveGateway id="${gate}" default="${gate}_go">${this.g.rest ? '' : ext(ann('point', { kind: 'gate' }))}</exclusiveGateway>`);
    this.els.push(`<intermediateCatchEvent id="${wait}" name="paused"><signalEventDefinition signalRef="sig_th_resume"/></intermediateCatchEvent>`);
    this.flow(gate, wait, `\${${v('paused')} == true}`, 'paused');
    this.flow(wait, gate);
    this.flow(gate, real.entry, undefined, undefined, `${gate}_go`);
    return { entry: gate, exits: real.exits };
  }

  stepBody(st: any): Frag | null {
    if ('call' in st) return this.call(st);
    if ('end' in st && Object.keys(st).every((k) => ['end', 'compensate', 'id'].includes(k))) {
      const id = this.g.id('goto');
      this.els.push(`<intermediateThrowEvent id="${id}" name="end:${esc(st.end)}"/>`);
      this.endWith(st.end, id, undefined, undefined, st.compensate === 'all' || st.compensate === true);
      return { entry: id, exits: [] };
    }
    if ('return' in st && Object.keys(st).every((k) => ['return', 'id'].includes(k))) {
      const id = this.g.id('goto');
      this.els.push(`<intermediateThrowEvent id="${id}" name="return:${esc(st.return)}"/>`);
      this.returnWith(st.return, id);
      return { entry: id, exits: [] };
    }
    if ('switch' in st) return this.switchStep(st);
    if ('parallel' in st) return this.parallel(st);
    if ('forEach' in st) return this.forEach(st);
    if ('loop' in st) return this.loop(st);
    if ('await' in st) return this.await(st);
    if ('wait' in st) {
      const id = st.id ? this.g.stepId(st.id) : this.g.id('wait');
      const d = timerDuration(st.wait, st.id ?? 'wait');
      const meta = d.parameter ? ext(ann('timer', { parameter: d.parameter, default: d.default })) : '';
      this.els.push(`<intermediateCatchEvent id="${id}" name="wait:${esc(d.literal)}">${meta}<timerEventDefinition><timeDuration>${esc(d.literal)}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
      // With a parameter the binding writes the value the timer took (`timer`), after `prefix:`.
      const p = this.point({ kind: 'text', text: `${st.id ?? 'wait'}:${d.literal}`, record: this.o.record, ...(d.parameter ? { timer: id, prefix: st.id ?? 'wait' } : {}) });
      this.flow(id, p);
      return { entry: id, exits: [p] };
    }
    if ('slot' in st) return this.slot(st);
    if ('use' in st) {
      const seg = this.g.ctx.resolve(st.use);
      if (!seg) throw new Error(`Segment ${st.use} does not resolve`);
      const c = this.child({ hookOwner: seg.name });
      c.els = this.els;
      c.flows = this.flows;
      c.returns = this.returns;
      c.assocs = this.assocs;
      return c.steps(seg.do ?? []);
    }
    if ('try' in st) return this.tryStep(st);
    if ('hook' in st) return this.hook(st);
    if ('block' in st) return this.block(st);
    throw new Error(`The generator cannot express the step ${JSON.stringify(Object.keys(st))}`);
  }

  // ---------------------------------------------------------------------- call

  /**
   * The attempt loop of one activity, as the content of an embedded sub-process: the task, its
   * attempt timeout, retryable errors and the backoff. With `swallow` (a compensation handler) a
   * failure ends the handler instead of the instance, as the interpreter records and goes on.
   */
  attempts(sid: string, stepId: string, name: string, act: any, pol: ReturnType<typeof effectivePolicy>, o: { level?: string; record: string; item: boolean; swallow: boolean; policy: any; sources?: any }): B {
    const release = act.release ?? '0.0.0';
    const topic = `${this.g.ctx.queueOf(name)}:${name}@${release}`;
    const max = Math.max(1, pol.maxAttempts.value);
    const hasUnknown = !o.swallow && (act.outcomes ?? []).some((x: any) => x.name === 'unknown');
    const ov = outcomeVar(stepId);
    const inner = this.child();
    const task = `${sid}_task`;
    const merge = `${sid}_attempt`;
    const done = `${sid}_done`;
    const fail = (from: string) => (o.swallow ? inner.flow(from, done) : inner.fail(from));
    inner.els.push(`<dataObject id="${sid}_attempts" name="th_attempt" itemSubjectRef="xsd:int">${ext('<flowable:value>0</flowable:value>')}</dataObject>`);
    inner.els.push(`<startEvent id="${sid}_start"/>`, `<exclusiveGateway id="${merge}"/>`, `<endEvent id="${done}"/>`);
    inner.els.push(`<serviceTask id="${task}" name="${esc(stepId)}" flowable:type="external-worker" flowable:topic="${esc(topic)}" flowable:exclusive="false">${ext(
      listener(`\${execution.setVariable('th_attempt', ${v('th_attempt')} + 1)}`),
      ann('task', { step: stepId, activity: name, release, level: o.level, record: o.record, item: o.item ? 'true' : undefined, policy: o.policy, sources: o.sources }),
    )}</serviceTask>`);
    inner.flow(`${sid}_start`, merge);
    inner.flow(merge, task);
    inner.flow(task, done);
    // Retryable errors and a retryable attempt timeout go round the loop; anything else fails the step.
    const retryNames = new Set<string>(['TransientError', ...(act.errors ?? []).filter((e: any) => e.retryable).map((e: any) => e.name)]);
    const retryable = [...retryNames].filter((n) => isRetryable(pol, n, act.errors)).sort();
    const check = `${sid}_retry`;
    const exhausted = `${sid}_spent`;
    const retryPaths: string[] = [];
    for (const n of retryable) {
      const b = `${sid}_err_${safe(n)}`;
      inner.els.push(`<boundaryEvent id="${b}" attachedToRef="${task}"><errorEventDefinition errorRef="${this.g.error(n, `err_${safe(n)}`)}"/></boundaryEvent>`);
      retryPaths.push(b);
    }
    const fatal = `${sid}_fatal`;
    inner.els.push(`<boundaryEvent id="${fatal}" attachedToRef="${task}"><errorEventDefinition/></boundaryEvent>`);
    fail(fatal);
    if (pol.attemptTimeout.value) {
      const b = `${sid}_timeout`;
      inner.els.push(`<boundaryEvent id="${b}" attachedToRef="${task}" cancelActivity="true"><timerEventDefinition><timeDuration>${pol.attemptTimeout.value}</timeDuration></timerEventDefinition></boundaryEvent>`);
      if (isRetryable(pol, 'Timeout', act.errors)) retryPaths.push(b);
      else fail(b);
    }
    if (retryPaths.length) {
      inner.gateway(check, 'attempts left', `${check}_spent`);
      for (const b of retryPaths) inner.flow(b, check);
      if (max > 1) {
        const wait = `${sid}_backoff`;
        const intervals = Array.from({ length: max - 1 }, (_, i) => isoMs(retryInterval(pol, i + 1)));
        let expr = intervals[intervals.length - 1];
        let dur = `${expr}`;
        if (new Set(intervals).size > 1) {
          expr = `'${intervals[intervals.length - 1]}'`;
          for (let i = intervals.length - 2; i >= 0; i--) expr = `${v('th_attempt')} == ${i + 1} ? '${intervals[i]}' : (${expr})`;
          dur = `\${${expr}}`;
        }
        inner.els.push(`<intermediateCatchEvent id="${wait}" name="backoff"><timerEventDefinition><timeDuration>${esc(dur)}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
        inner.flow(check, wait, `\${${v('th_attempt')} < ${max}}`, 'retry');
        inner.flow(wait, merge);
      }
      inner.els.push(`<intermediateThrowEvent id="${exhausted}" name="retries spent"/>`);
      inner.flow(check, exhausted, undefined, undefined, `${check}_spent`);
      if (hasUnknown) {
        const s = inner.setter(ov, 'unknown');
        inner.flow(exhausted, s);
        inner.flow(s, done);
      } else fail(exhausted);
    }
    return inner;
  }

  call(st: any, elementSuffix = ''): Frag {
    const act = this.g.resolveAct(st.call);
    if (!act) throw new Error(`${st.call} does not resolve`);
    const stepId = st.id ?? shortName(st.call);
    const sid = this.g.stepId(stepId, elementSuffix);
    const release = act.release ?? '0.0.0';
    const pol = effectivePolicy(act, st.policy);
    const policy = Object.fromEntries(Object.entries(pol).map(([k, x]) => [k, x.value]));
    const sources = Object.fromEntries(Object.entries(pol).map(([k, x]) => [k, x.source]));
    const hasUnknown = (act.outcomes ?? []).some((o: any) => o.name === 'unknown');
    const ov = outcomeVar(stepId);
    const level = st.level ?? act.level;
    const inner = this.attempts(sid, stepId, st.call, act, pol, { level, record: this.o.record, item: this.o.item, swallow: false, policy, sources });
    this.els.push(`<subProcess id="${sid}" name="${esc(stepId)}">${ext(ann('call', { step: stepId, activity: st.call, release }))}`, ...inner.body().map((x) => '  ' + x), '</subProcess>');
    const after = this.g.id('called');
    this.gateway(after);
    this.flow(sid, after);
    if (pol.totalTimeout.value) {
      const b = `${sid}_total`;
      this.els.push(`<boundaryEvent id="${b}" attachedToRef="${sid}" cancelActivity="true"><timerEventDefinition><timeDuration>${pol.totalTimeout.value}</timeDuration></timerEventDefinition></boundaryEvent>`);
      if (hasUnknown) {
        const s = this.setter(ov, 'unknown');
        this.flow(b, s);
        this.flow(s, after);
      } else this.fail(b);
    }
    const rec = this.point({ kind: 'call', step: stepId, var: ov, record: this.o.record, item: this.o.item ? 'true' : undefined });
    this.flow(after, rec);
    if (act.type === 'screen' && this.o.record !== 'finally' && !elementSuffix) this.g.screenSteps.push({ st, opts: this.o });
    const primary = (act.outcomes ?? []).find((o: any) => o.primary)?.name ?? act.outcomes?.[0]?.name;
    const f = this.outcomes(rec, st, ov, primary, (act.outcomes ?? []).map((o: any) => o.name));
    // A completed write registers its compensation: a marker on the primary path carries the handler.
    if (act.compensation && (act.effect === 'write' || act.effect === 'external') && f.primaryExit && this.o.record !== 'finally') {
      const m = `${sid}_written`;
      const cb = `${sid}_compensate`;
      const undo = `${sid}_undo`;
      const comp = this.g.resolveAct(act.compensation);
      this.els.push(`<manualTask id="${m}" name="${esc(stepId)} written"/>`);
      this.els.push(`<boundaryEvent id="${cb}" attachedToRef="${m}"><compensateEventDefinition/></boundaryEvent>`);
      const upol = effectivePolicy(comp ?? {}, undefined);
      const uin = this.attempts(undo, `undo-${stepId}`, act.compensation, comp ?? {}, upol, { record: 'silent', item: false, swallow: true, policy: Object.fromEntries(Object.entries(upol).map(([k, x]) => [k, x.value])) });
      this.els.push(`<subProcess id="${undo}" name="undo ${esc(stepId)}" isForCompensation="true">${ext(ann('point', { kind: 'undo', step: stepId }))}`, ...uin.body().map((x) => '  ' + x), '</subProcess>');
      this.assocs.push(`<association id="${sid}_assoc" associationDirection="One" sourceRef="${cb}" targetRef="${undo}"/>`);
      const tries: TryScope[] = [];
      for (let t = this.o.tryScope; t; t = t.parent) tries.push(t);
      this.g.markers.push({ id: m, tries });
      // Re-route the primary exit through the marker.
      const idx = f.exits.indexOf(f.primaryExit);
      this.flow(f.primaryExit, m);
      f.exits[idx] = m;
    }
    return { entry: sid, exits: f.exits };
  }

  /**
   * The gateway on a step's outcome. The primary outcome carries on; a handled outcome runs its
   * action; an outcome nobody handles fails the step, as the interpreter does (L05).
   */
  outcomes(from: string, st: any, ov: string, primary: string | undefined, declared: string[]): { exits: string[]; primaryExit?: string } {
    const handle = st.handle ?? {};
    const lenient = this.o.record === 'finally';
    const others = declared.filter((o) => o !== primary && !(o in handle));
    if (!Object.keys(handle).length && (lenient || !others.length)) return { exits: [from], primaryExit: from };
    const gw = this.g.id('outcome');
    this.gateway(gw, ov, `${gw}_other`);
    this.flow(from, gw);
    const prim = this.g.id('primary');
    this.els.push(`<intermediateThrowEvent id="${prim}" name="${esc(primary ?? 'primary')}"/>`);
    const exits: string[] = [prim];
    let cont: string | undefined;
    for (const [o, a] of Object.entries<any>(handle)) {
      const cond = `\${${v(ov)} == '${o}'}`;
      const after = this.action(a, gw, cond, o);
      if (after === null) {
        if (!cont) { cont = this.g.id('continue'); this.gateway(cont); exits.push(cont); }
        this.flow(gw, cont, cond, `${o}, continue`);
      } else exits.push(...after);
    }
    if (lenient) this.flow(gw, prim, undefined, primary ?? 'primary', `${gw}_other`);
    else {
      this.flow(gw, prim, `\${${v(ov)} == '${primary}'}`, primary);
      this.fail(gw, undefined, 'not handled', `${gw}_other`);
    }
    return { exits, primaryExit: prim };
  }

  // -------------------------------------------------------------------- switch

  switchStep(st: any): Frag {
    const sw = st.switch;
    const by = sw.by?.outcomeOf ? outcomeVar(sw.by.outcomeOf) : `decision_${safe(sw.by?.decision)}`;
    const gw = this.g.id('switch');
    const join = this.g.id('join');
    this.gateway(gw, `switch ${by}`, `${gw}_default`);
    let joined = false;
    for (const [k, steps] of Object.entries<any>(sw.cases ?? {})) {
      const f = this.steps(steps);
      const cond = `\${${v(by)} == '${k}'}`;
      if (f) {
        this.flow(gw, f.entry, cond, k);
        f.exits.forEach((e) => { this.flow(e, join); joined = true; });
      } else { this.flow(gw, join, cond, k); joined = true; }
    }
    const d = this.steps(sw.default ?? []);
    if (d) {
      this.flow(gw, d.entry, undefined, 'default', `${gw}_default`);
      d.exits.forEach((e) => { this.flow(e, join); joined = true; });
    } else { this.flow(gw, join, undefined, 'default', `${gw}_default`); joined = true; }
    if (joined) this.gateway(join);
    return { entry: gw, exits: joined ? [join] : [] };
  }

  // ------------------------------------------------------------------ parallel

  parallel(st: any): Frag {
    const fork = this.g.id('fork');
    const join = this.g.id('joinAll');
    this.els.push(`<parallelGateway id="${fork}"/>`, `<parallelGateway id="${join}" name="join:${esc(st.parallel.join ?? 'all')}"/>`);
    for (const branch of st.parallel.branches ?? []) {
      const f = this.steps(branch);
      if (!f) { this.flow(fork, join); continue; }
      this.flow(fork, f.entry);
      // A parallel join waits for one token per incoming flow: a branch that can leave by more than
      // one exit (a handled outcome that continues) merges them first, so it gives the join one.
      if (f.exits.length > 1) {
        const merge = this.gateway(this.g.id('branchDone'));
        f.exits.forEach((e) => this.flow(e, merge));
        this.flow(merge, join);
      } else f.exits.forEach((e) => this.flow(e, join));
    }
    const p = this.point({ kind: 'text', text: `join:${st.parallel.join ?? 'all'}`, record: this.o.record });
    this.flow(join, p);
    return { entry: fork, exits: [p] };
  }

  // ------------------------------------------------------------------- forEach

  forEach(st: any): Frag {
    const fe = st.forEach;
    const id = st.id ? this.g.stepId(st.id) : this.g.id('forEach');
    const count = `th_count_${safe(String(fe.in).replace('[]', ''))}`;
    const inner = this.child({ item: true });
    const f = inner.steps(fe.do ?? []);
    const s = `${id}_start`;
    const e = `${id}_end`;
    const body: string[] = [];
    // Outcomes are local to each item.
    const ids = new Set<string>();
    const walk = (xs: any[]) => { for (const x of xs ?? []) { if (x.call) ids.add(x.id ?? shortName(x.call)); if (x.block) ids.add(x.id); for (const a of Object.values(x.handle ?? {})) if (Array.isArray(a)) walk(a); } };
    walk(fe.do);
    const conc = Math.max(1, fe.concurrency ?? 1);
    // The item's index in the list, kept local to the item: th_chunk * concurrency + loopCounter.
    const index = conc > 1 ? `\${execution.setVariable('th_item', ${v('th_chunk')} * ${conc} + ${v('loopCounter')})}` : `\${execution.setVariable('th_item', ${v('loopCounter')})}`;
    body.push(`<dataObject id="${id}_index" name="th_item" itemSubjectRef="xsd:int"/>`);
    for (const i of [...ids].sort()) body.push(`<dataObject id="${id}_${safe(i)}" name="${outcomeVar(i)}" itemSubjectRef="xsd:string"/>`);
    body.push(`<startEvent id="${s}">${ext(listener(index))}</startEvent>`, `<endEvent id="${e}"/>`);
    if (f) {
      inner.flow(s, f.entry);
      f.exits.forEach((x) => inner.flow(x, e));
    } else inner.flow(s, e);
    // onItemFailure continue: an end inside an item ends that item only.
    if ((fe.onItemFailure ?? 'stop') === 'continue') {
      for (const o of [...inner.endsHere].sort()) {
        const es = `${id}_itemEnd_${safe(o)}`;
        body.push(`<subProcess id="${es}" triggeredByEvent="true"><startEvent id="${es}_start"><errorEventDefinition errorRef="err_end_${safe(o)}"/></startEvent><endEvent id="${es}_end"/><sequenceFlow id="${es}_flow" sourceRef="${es}_start" targetRef="${es}_end"/></subProcess>`);
      }
    }
    const meta = ext(ann('forEach', { in: fe.in, count, onItemFailure: fe.onItemFailure ?? 'stop', concurrency: conc }));
    // A REST binding counts the items before the multi-instance starts (embedded: as it starts).
    const counted = this.restPoint({ kind: 'forEachStart', in: fe.in, count });
    if (counted) this.flow(counted, id);
    if (conc <= 1) {
      this.els.push(`<subProcess id="${id}" name="forEach ${esc(fe.in)}">${meta}`,
        `  <multiInstanceLoopCharacteristics isSequential="true"><loopCardinality>\${${v(count)}}</loopCardinality></multiInstanceLoopCharacteristics>`,
        ...body.map((x) => '  ' + x), ...inner.body().map((x) => '  ' + x), '</subProcess>');
    } else {
      // concurrency n: chunks of n items run in parallel, one chunk after another. Flowable has no
      // concurrency bound for a parallel multi-instance, and starts every instance in one transaction.
      const left = `${v(count)} - ${v('th_chunk')} * ${conc}`;
      this.els.push(`<subProcess id="${id}" name="forEach ${esc(fe.in)}">${meta}`,
        `  <multiInstanceLoopCharacteristics isSequential="true" flowable:elementIndexVariable="th_chunk"><loopCardinality>\${(${v(count)} + ${conc - 1}) / ${conc}}</loopCardinality></multiInstanceLoopCharacteristics>`,
        `  <startEvent id="${id}_chunkStart"/>`, `  <endEvent id="${id}_chunkEnd"/>`,
        `  <subProcess id="${id}__chunk" name="item">`,
        `    <multiInstanceLoopCharacteristics isSequential="false"><loopCardinality>\${${left} &lt; ${conc} ? ${left} : ${conc}}</loopCardinality></multiInstanceLoopCharacteristics>`,
        ...body.map((x) => '    ' + x), ...inner.body().map((x) => '    ' + x), '  </subProcess>',
        `  <sequenceFlow id="${id}_chunkIn" sourceRef="${id}_chunkStart" targetRef="${id}__chunk"/>`,
        `  <sequenceFlow id="${id}_chunkOut" sourceRef="${id}__chunk" targetRef="${id}_chunkEnd"/>`,
        '</subProcess>');
    }
    return { entry: counted ?? id, exits: [id] };
  }

  // ---------------------------------------------------------------------- loop

  loop(st: any): Frag {
    const lp = st.loop;
    const id = st.id ? this.g.stepId(st.id) : this.g.id('loop');
    const counter = `th_loop_${safe(id)}`;
    const inner = this.child();
    const s = `${id}_start`;
    const again = `${id}_again`;
    const count = `${id}_count`;
    const check = `${id}_until`;
    const e = `${id}_end`;
    inner.els.push(`<dataObject id="${id}_counter" name="${counter}" itemSubjectRef="xsd:int">${ext('<flowable:value>0</flowable:value>')}</dataObject>`);
    inner.els.push(`<startEvent id="${s}"/>`, `<exclusiveGateway id="${again}"/>`, `<endEvent id="${e}"/>`);
    inner.els.push(`<intermediateThrowEvent id="${count}" name="count">${ext(listener(`\${execution.setVariable('${counter}', ${v(counter)} + 1)}`))}</intermediateThrowEvent>`);
    inner.gateway(check, 'until', `${check}_again`);
    inner.flow(s, again);
    const f = inner.steps(lp.do ?? []);
    if (f) {
      inner.flow(again, f.entry);
      f.exits.forEach((x) => inner.flow(x, count));
    } else inner.flow(again, count);
    inner.flow(count, check);
    const until = lp.until?.outcomeOf ? (lp.until.in ?? []).map((o: string) => `${v(outcomeVar(lp.until.outcomeOf))} == '${o}'`).join(' || ') : '';
    inner.flow(check, e, `\${${until ? `${until} || ` : ''}${v(counter)} >= ${lp.max ?? 1}}`, 'done');
    if (lp.every) {
      const t = `${id}_every`;
      inner.els.push(`<intermediateCatchEvent id="${t}" name="every ${esc(lp.every)}"><timerEventDefinition><timeDuration>${esc(lp.every)}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
      inner.flow(check, t, undefined, 'again', `${check}_again`);
      inner.flow(t, again);
    } else inner.flow(check, again, undefined, 'again', `${check}_again`);
    for (const r of inner.returns) this.returns.add(r);
    this.els.push(`<subProcess id="${id}" name="loop">`, ...inner.body().map((x) => '  ' + x), '</subProcess>');
    return { entry: id, exits: [id] };
  }

  // --------------------------------------------------------------------- await

  await(st: any): Frag {
    const aw = st.await;
    const stepId = st.id ?? 'await';
    const gw = this.g.stepId(stepId);
    const msg = `${gw}_message`;
    const tmr = `${gw}_timeout`;
    this.els.push(`<eventBasedGateway id="${gw}" name="await ${esc(stepId)}"/>`);
    this.els.push(`<intermediateCatchEvent id="${msg}" name="${esc(stepId)}:received">${ext(ann('await', { step: stepId, message: aw.message, correlateBy: aw.correlateBy, family: aw.family }))}<messageEventDefinition messageRef="${this.g.message(aw.message)}"/></intermediateCatchEvent>`);
    this.flow(gw, msg);
    const got = this.point({ kind: 'awaitReceived', step: stepId, message: aw.message, record: this.o.record, item: this.o.item ? 'true' : undefined });
    this.flow(msg, got);
    const exits = [got];
    if (aw.timeout) {
      // The business calendar is the binding's: registered in the embedded engine, or, on a REST
      // server that has no such calendar, applied by the binding when it fires the timer.
      // A timeout from a parameter (R-05-45) is marked for the binding, which takes the tenant's value.
      const business = aw.calendar === 'business';
      // A wait a bank may set (R-05-47): the expanded await names the composite's parameters, the policy giving
      // the defaults; the binding takes the tenant's values in force, as for a parameter timeout.
      const d = typeof aw.timeout === 'string' && aw.timeoutParameter
        ? { literal: aw.timeout, parameter: aw.timeoutParameter as string, default: aw.timeout as string }
        : timerDuration(aw.timeout, stepId);
      const calParam: string | undefined = aw.calendarParameter;
      const cal = business && !this.g.rest ? ' flowable:businessCalendarName="business"' : '';
      const meta = (business && this.g.rest) || d.parameter || calParam
        ? ext(ann('timer', {
          ...(business ? { calendar: 'business' } : calParam ? { calendar: aw.calendar ?? 'elapsed' } : {}),
          duration: d.parameter ? undefined : d.literal, parameter: d.parameter, default: d.parameter || calParam ? d.literal : d.default, calendarParameter: calParam,
        }))
        : '';
      this.els.push(`<intermediateCatchEvent id="${tmr}" name="${esc(stepId)}:timeout">${meta}<timerEventDefinition${cal}><timeDuration>${esc(d.literal)}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
      this.flow(gw, tmr);
      const out = this.point({ kind: 'awaitTimeout', step: stepId, message: aw.message, record: this.o.record, item: this.o.item ? 'true' : undefined });
      this.flow(tmr, out);
      const after = aw.onTimeout ? this.action(aw.onTimeout, out, undefined, 'timeout') : null;
      if (after === null) exits.push(out);
      else exits.push(...after);
    }
    return { entry: gw, exits };
  }

  // ---------------------------------------------------------------------- slot

  slot(st: any): Frag {
    const stepId = st.id ?? st.slot;
    const id = this.g.stepId(stepId);
    const slot = (this.g.wf.slots ?? []).find((s: any) => s.name === st.slot);
    const ov = outcomeVar(stepId);
    // A REST binding names the leg before the call activity starts (embedded: as it starts).
    const chosen = this.restPoint({ kind: 'slotStart', slot: st.slot, step: stepId });
    if (chosen) this.flow(chosen, id);
    this.els.push(`<callActivity id="${id}" name="${esc(stepId)}" calledElement="\${${v(`th_leg_${safe(st.slot)}`)}}" flowable:inheritBusinessKey="true"${this.g.sameDeployment}>${ext(
      `<flowable:out source="th_outcome" target="${ov}"/>`,
      this.g.callerMappings(`slot:${stepId}`),
      ann('slot', { slot: st.slot, step: stepId, writes: (slot?.contract?.writes ?? []).join(',') }),
    )}</callActivity>`);
    const rec = this.point({ kind: 'slot', step: stepId, slot: st.slot, var: ov, record: this.o.record, writes: (slot?.contract?.writes ?? []).join(',') });
    this.flow(id, rec);
    const primary = slot?.contract?.primaryOutcome ?? slot?.contract?.outcomes?.[0];
    const f = this.outcomes(rec, st, ov, primary, slot?.contract?.outcomes ?? []);
    return { entry: chosen ?? id, exits: f.exits };
  }

  // ----------------------------------------------------------------------- try

  tryStep(st: any): Frag {
    const id = st.id ? this.g.stepId(st.id) : this.g.id('try');
    const scope: TryScope = { markers: [], parent: this.o.tryScope };
    const inner = this.child({ tryScope: scope });
    const f = inner.steps(st.try);
    const s = `${id}_start`;
    const e = `${id}_end`;
    inner.els.unshift(`<startEvent id="${s}"/>`, `<endEvent id="${e}"/>`);
    if (f) {
      inner.flow(s, f.entry);
      f.exits.forEach((x) => inner.flow(x, e));
    } else inner.flow(s, e);
    // A failure inside the try: compensate its writes if asked, then run the catch or fail on.
    const es = `${id}_failed`;
    const h = inner.child({ tryScope: scope });
    h.els.push(`<startEvent id="${es}_start"><errorEventDefinition errorRef="${this.g.error('th:failed', 'err_th_failed')}"/></startEvent>`, `<endEvent id="${es}_end"/>`);
    let at = `${es}_start`;
    if (st.compensate === 'all') {
      const c = h.compensation(scope);
      if (c) { h.flow(at, c.entry); at = c.exit; }
    }
    const catchSteps = Array.isArray(st.catch) ? st.catch.flatMap((c: any) => (Array.isArray(c?.do) ? c.do : [c])) : [];
    const cf = catchSteps.length ? h.steps(catchSteps) : null;
    if (cf) {
      h.flow(at, cf.entry);
      cf.exits.forEach((x) => h.flow(x, `${es}_end`));
    } else h.fail(at);
    inner.els.push(`<subProcess id="${es}" triggeredByEvent="true">`, ...h.body().map((x) => '  ' + x), '</subProcess>');
    for (const r of inner.returns) this.returns.add(r);
    this.els.push(`<subProcess id="${id}" name="try">`, ...inner.body().map((x) => '  ' + x), '</subProcess>');
    return { entry: id, exits: [id] };
  }

  // ---------------------------------------------------------------------- hook

  hook(st: any): Frag {
    const name = st.hook;
    const owner = this.g.ctx.resolve(this.o.hookOwner) ?? this.g.wf;
    const point = (owner.hookPoints ?? []).find((h: any) => h.name === name) ?? (this.g.wf.hookPoints ?? []).find((h: any) => h.name === name);
    if (!point) throw new Error(`Hook point ${name} is not declared (L06)`);
    const key = `${this.o.hookOwner}#${name}`;
    const hv = `th_hook_${safe(key)}`;
    const raw = `th_hookOutcome_${safe(name)}`;
    const eff = `th_hookEffect_${safe(name)}`;
    const pol = point.policy ?? {};
    const meta = { hookPoint: key, name, type: point.type, writes: (point.contract?.writes ?? []).join(','), onFailure: pol.onFailure ?? 'fail-open', timeout: pol.timeout };
    const gw = this.g.id('hookBound');
    const after = this.g.id('hookDone');
    this.gateway(gw, `hook ${name} bound`, `${gw}_bound`);
    this.gateway(after);
    this.flow(gw, after, `\${${v(hv)} == null}`, 'unbound');
    const callHook = (into: B, from: string, fromId?: string): string => {
      const id = this.g.stepId(`hook-${name}`);
      into.els.push(`<callActivity id="${id}" name="hook ${esc(name)}" calledElement="\${${v(hv)}}" flowable:inheritBusinessKey="true"${this.g.sameDeployment}>${ext(`<flowable:out source="th_outcome" target="${raw}"/>`, this.g.callerMappings(`hook:${key}`), ann('hook', meta))}</callActivity>`);
      into.flow(from, id, undefined, undefined, fromId);
      const res = into.point({ kind: 'hookResult', ...meta });
      into.flow(id, res);
      if (pol.timeout) {
        const b = `${id}_timeout`;
        into.els.push(`<boundaryEvent id="${b}" attachedToRef="${id}" cancelActivity="true"><timerEventDefinition><timeDuration>${esc(pol.timeout)}</timeDuration></timerEventDefinition></boundaryEvent>`);
        const s = into.setter(raw, 'failed');
        into.flow(b, s);
        into.flow(s, res);
      }
      return res;
    };
    if (point.type === 'notify') {
      // A notify hook must not delay or change the flow (R-06-09): it runs in an event sub-process.
      const sig = `sig_th_notify_${safe(name)}`;
      const thr = this.g.id('notify');
      // A REST binding marks the hook's place in the trace before the throw (embedded: on the throw).
      const marked = this.restPoint({ kind: 'hookStart', ...meta });
      this.els.push(`<intermediateThrowEvent id="${thr}" name="notify ${esc(name)}">${marked ? '' : ext(ann('point', { kind: 'hookStart', ...meta }))}<signalEventDefinition signalRef="${sig}"/></intermediateThrowEvent>`);
      if (marked) this.flow(marked, thr);
      this.flow(gw, marked ?? thr, undefined, 'bound', `${gw}_bound`);
      this.flow(thr, after);
      const es = new B(this.g, { record: 'business', item: false, hookOwner: this.o.hookOwner });
      const esId = `th_onNotify_${safe(name)}`;
      es.els.push(`<startEvent id="${esId}_start" isInterrupting="false"><signalEventDefinition signalRef="${sig}"/></startEvent>`, `<endEvent id="${esId}_end"/>`);
      const res = callHook(es, `${esId}_start`);
      es.flow(res, `${esId}_end`);
      this.g.processLevel.push(`<subProcess id="${esId}" triggeredByEvent="true">`, ...es.body().map((x) => '  ' + x), '</subProcess>');
      this.g.signals.add(sig);
      return { entry: gw, exits: [after] };
    }
    const res = callHook(this, gw, `${gw}_bound`);
    const og = this.g.id('hookEffect');
    this.gateway(og, eff, `${og}_continue`);
    this.flow(res, og);
    this.flow(og, after, undefined, 'continue', `${og}_continue`);
    // hold runs the hold block the hook point names; reject compensates and ends rejected.
    const hold = this.holdFor(point);
    this.flow(og, hold.entry, `\${${v(eff)} == 'hold'}`, 'hold');
    hold.exits.forEach((x) => this.flow(x, after));
    if (point.type !== 'transform') this.endWith('rejected', og, `\${${v(eff)} == 'reject'}`, 'reject', true);
    return { entry: gw, exits: [after] };
  }

  holdFor(point: any): Frag {
    const block = point.policy?.onHold ?? 'p.hold.approval.v1';
    const act = this.g.resolveAct(block);
    const primary = (act?.outcomes ?? []).find((o: any) => o.primary)?.name;
    const handle: Record<string, any> = {};
    for (const o of act?.outcomes ?? []) if (o.name !== primary) handle[o.name] = { end: o.name === 'expired' ? 'review' : 'rejected', compensate: 'all' };
    const f = this.call({ id: 'hold', call: block, handle });
    return f;
  }

  // --------------------------------------------------------------------- block

  block(st: any): Frag {
    // A composite step is an embedded sub-process; a return is an error end caught on its boundary.
    const id = this.g.id('block').replace(/^th_block_/, 'block_');
    const inner = this.child({ record: 'detail' });
    // A REST binding learns when the block began (R-05-43) at a point of its own (embedded: as it starts).
    const began = inner.restPoint({ kind: 'blockStart', step: st.id });
    const f = inner.steps(st.block.do ?? []);
    const act = this.g.resolveAct(st.block.of);
    const primary = act?.outcomes?.find((o: any) => o.primary)?.name ?? 'done';
    const ov = outcomeVar(st.id);
    const s = `${id}_start`;
    const e = `${id}_end`;
    inner.els.unshift(`<startEvent id="${s}"/>`, `<endEvent id="${e}"/>`);
    const first = began ?? f?.entry;
    if (began && f) inner.flow(began, f.entry);
    if (first) inner.flow(s, first);
    if (f) f.exits.forEach((x) => inner.flow(x, e));
    else if (began) inner.flow(began, e);
    else inner.flow(s, e);
    this.els.push(`<subProcess id="${id}" name="${esc(st.id)}"><documentation>Composite ${esc(st.block.of)}, subflow ${esc(st.block.by)}</documentation>${ext(ann('block', { step: st.id, of: st.block.of, by: st.block.by }))}`, ...inner.body().map((x) => '  ' + x), '</subProcess>');
    const join = this.g.id('returned');
    this.gateway(join);
    const ok = this.setter(ov, primary);
    this.flow(id, ok);
    this.flow(ok, join);
    for (const o of [...inner.returns].sort()) {
      const b = `${id}_ret_${safe(o)}`;
      this.els.push(`<boundaryEvent id="${b}" attachedToRef="${id}"><errorEventDefinition errorRef="${this.g.error(`return:${o}`, `ret_${safe(o)}`)}"/></boundaryEvent>`);
      const sv = this.setter(ov, o);
      this.flow(b, sv);
      this.flow(sv, join);
    }
    // The block's own trace entry is written where its step id is the element id.
    const rec = this.g.stepId(st.id);
    this.els.push(this.g.pointElement(rec, st.id, ext(ann('point', { kind: 'call', step: st.id, var: ov, record: this.o.record, item: this.o.item ? 'true' : undefined }))));
    this.flow(join, rec);
    const fr = this.outcomes(rec, st, ov, primary, (act?.outcomes ?? []).map((o: any) => o.name));
    return { entry: id, exits: fr.exits };
  }
}

/** The signal event sub-processes inside main: pause, resume, cancel, re-screen (R-05-12). */
function signalHandlers(g: Gen, main: B): string[] {
  const out: string[] = [];
  const sub = (id: string, interrupting: boolean, message: string, build: (b: B, start: string, end: string) => void) => {
    const b = main.child();
    b.els.push(`<startEvent id="${id}_start" isInterrupting="${interrupting}"><messageEventDefinition messageRef="${g.message(message)}"/></startEvent>`, `<endEvent id="${id}_end"/>`);
    build(b, `${id}_start`, `${id}_end`);
    out.push(`<subProcess id="${id}" triggeredByEvent="true">`, ...b.body().map((x) => '  ' + x), '</subProcess>');
  };
  sub('th_onPause', false, SIGNAL_MESSAGES.pause, (b, s, e) => {
    const t = 'th_pause_set';
    b.els.push(`<intermediateThrowEvent id="${t}">${ext(listener(`\${execution.setVariable('paused', true)}`))}</intermediateThrowEvent>`);
    b.flow(s, t);
    b.flow(t, e);
  });
  sub('th_onResume', false, SIGNAL_MESSAGES.resume, (b, s, e) => {
    const t = 'th_resume_set';
    const r = 'th_resume_throw';
    b.els.push(`<intermediateThrowEvent id="${t}">${ext(listener(`\${execution.setVariable('paused', false)}`))}</intermediateThrowEvent>`);
    b.els.push(`<intermediateThrowEvent id="${r}"><signalEventDefinition signalRef="sig_th_resume"/></intermediateThrowEvent>`);
    b.flow(s, t);
    b.flow(t, r);
    b.flow(r, e);
  });
  sub('th_onCancel', true, SIGNAL_MESSAGES.cancel, (b, s, e) => {
    // Cancel compensates the completed steps in reverse order, then ends cancelled; finally runs after.
    // It ends main normally (Flowable 7.1 loses main's boundary executions when an interrupting
    // event sub-process starts, so an error thrown from here finds no catch); the gateway after
    // main reads the mark.
    const c = b.compensation(undefined);
    let at = s;
    if (c) { b.flow(s, c.entry); at = c.exit; }
    const mark = b.setter('th_cancelled', 'true');
    b.flow(at, mark);
    b.flow(mark, e);
  });
  if (g.screenSteps.length) {
    sub('th_onRescreen', false, SIGNAL_MESSAGES['re-screen'], (b, s, e) => {
      // Re-screen runs the screen steps that have run, again, on a waiting payment.
      let at = s;
      for (const { st } of g.screenSteps) {
        const id = st.id ?? shortName(st.call);
        const gw = g.id('rescreen');
        const next = g.id('rescreened');
        b.gateway(gw, `screened ${id}`, `${gw}_skip`);
        b.gateway(next);
        b.flow(at, gw);
        const f = b.call(st, '__rescreen');
        b.flow(gw, f.entry, `\${${v(outcomeVar(id))} != null}`, 'screened');
        f.exits.forEach((x) => b.flow(x, next));
        b.flow(gw, next, undefined, 'not yet', `${gw}_skip`);
        at = next;
      }
      b.flow(at, e);
    });
  }
  return out;
}

/** Boundaries on a top-level container: each end outcome to its endpoint, a failure to failed. */
function catchEnds(g: Gen, b: B, container: string, proc: B, lines: string[], target: (o: string) => string) {
  for (const o of [...b.endsHere].sort()) {
    const id = `${container}_end_${safe(o)}`;
    lines.push(`<boundaryEvent id="${id}" attachedToRef="${container}"><errorEventDefinition errorRef="err_end_${safe(o)}"/></boundaryEvent>`);
    proc.flow(id, target(o), undefined, undefined, `th_f_${id}`);
  }
  if (b.failsHere) {
    const id = `${container}_failed`;
    lines.push(`<boundaryEvent id="${id}" attachedToRef="${container}"><errorEventDefinition errorRef="err_th_failed"/></boundaryEvent>`);
    proc.flow(id, target('failed'), undefined, undefined, `th_f_${id}`);
  }
}

function container(id: string, name: string, b: B, f: Frag | null, extra: string[] = []): string[] {
  const body: string[] = [`<startEvent id="${id}_start"/>`, `<endEvent id="${id}_end"/>`];
  if (f) {
    b.flow(`${id}_start`, f.entry);
    f.exits.forEach((x) => b.flow(x, `${id}_end`));
  } else b.flow(`${id}_start`, `${id}_end`);
  return [`<subProcess id="${id}" name="${esc(name)}">`, ...[...body, ...b.els, ...extra, ...b.flows, ...b.assocs].map((x) => '  ' + x), '</subProcess>'];
}

export function generateBpmn(wf: any, ctx: Ctx): string {
  const pid = safe(`${wf.name}@${wf.release}`);
  const g = new Gen(wf, ctx);
  const durable = wf.kind !== 'hook';
  const exitOutcomes: string[] = wf.exit?.outcomes ?? [];
  const primaryExit = exitOutcomes[0] ?? 'done';
  const base: Opts = { record: wf.wfKind === 'subflow' ? 'detail' : 'business', item: false, hookOwner: wf.name };
  const proc = new B(g, base);
  const lines: string[] = [];
  lines.push(`<startEvent id="start">${ext(listener(`\${execution.setVariable('paused', false)}`))}</startEvent>`);
  // main holds the flow; an end inside it is an error caught on its boundary.
  const main = new B(g, base);
  const f = main.steps(wf.do ?? []);
  const handlers = durable ? signalHandlers(g, main) : [];
  lines.push(...container('main', 'main', main, f, handlers));
  if (g.rest) {
    // A REST binding learns of each instance here, root or called, and gives it its hook bindings.
    lines.push(g.pointElement('th_started', 'started', ext(ann('point', { kind: 'processStart' }))));
    proc.flow('start', 'th_started', undefined, undefined, 'th_f_start');
    proc.flow('th_started', 'main', undefined, undefined, 'th_f_started');
  } else proc.flow('start', 'main', undefined, undefined, 'th_f_start');
  const mainDone = proc.gateway('th_main_done', 'cancelled?', 'th_f_main_done');
  proc.flow('main', mainDone, undefined, undefined, 'th_f_main');
  proc.flow(mainDone, g.endpoint(primaryExit), undefined, primaryExit, 'th_f_main_done');
  if (durable) proc.flow(mainDone, g.endpoint('cancelled'), `\${${v('th_cancelled')} == 'true'}`, 'cancelled', 'th_f_main_cancelled');
  catchEnds(g, main, 'main', proc, lines, (o) => g.endpoint(o));
  if (wf.deadline?.after) {
    // The deadline is a timer on the main sub-process, never an engine execution timeout (R-05-20).
    lines.push(`<boundaryEvent id="deadline" attachedToRef="main" cancelActivity="true"><timerEventDefinition><timeDuration>${esc(wf.deadline.after)}</timeDuration></timerEventDefinition></boundaryEvent>`);
    const p = proc.point({ kind: 'text', text: 'deadline:fired', record: 'business' });
    proc.flow('deadline', p, undefined, undefined, 'th_f_deadline');
    const db = new B(g, base);
    const df = db.steps(wf.deadline.do ?? []);
    lines.push(...container('th_deadline', 'deadline', db, df));
    proc.flow(p, 'th_deadline');
    proc.flow('th_deadline', g.endpoint(exitOutcomes[0] ?? 'late'), undefined, undefined, 'th_f_deadline_done');
    catchEnds(g, db, 'th_deadline', proc, lines, (o) => g.endpoint(o));
  }
  // Every end goes through finally (R-05-14), then ends with its outcome. An end or a failure
  // inside finally ends finally only.
  const fin = new B(g, { record: 'finally', item: false, hookOwner: wf.name });
  const ff = fin.steps(wf.finally ?? []);
  const endpointLines: string[] = [];
  for (const [o, id] of [...g.endpoints].sort(([a], [c]) => a.localeCompare(c))) {
    endpointLines.push(`<intermediateThrowEvent id="${id}" name="end:${esc(o)}">${ext(listener(setVar('th_outcome', o)))}</intermediateThrowEvent>`);
    proc.flow(id, 'th__finally', undefined, undefined, `th_f_${id}`);
  }
  endpointLines.push('<exclusiveGateway id="th__finally" name="finally"/>');
  if (ff) {
    endpointLines.push(...container('th_finally', 'finally', fin, ff));
    endpointLines.push('<boundaryEvent id="th_finally_stopped" attachedToRef="th_finally"><errorEventDefinition/></boundaryEvent>');
    proc.flow('th__finally', 'th_finally', undefined, undefined, 'th_f_finally');
    proc.flow('th_finally', 'th_end', undefined, undefined, 'th_f_finally_done');
    proc.flow('th_finally_stopped', 'th_end', undefined, undefined, 'th_f_finally_stopped');
  } else proc.flow('th__finally', 'th_end', undefined, undefined, 'th_f_finally');
  endpointLines.push(g.pointElement('th_end', 'end', ext(ann('point', { kind: 'end' }))), '<endEvent id="end"/>');
  proc.flow('th_end', 'end', undefined, undefined, 'th_f_end');

  const out: string[] = [];
  out.push('<?xml version="1.0" encoding="UTF-8"?>');
  out.push(`<!-- Generated by the TransactHub Designer Studio from ${esc(wf.name)}@${esc(wf.release)} (DSL ${esc(wf.dsl ?? '1.0.0')}). Do not edit; regenerate. -->`);
  out.push(`<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:flowable="http://flowable.org/bpmn" xmlns:th="${TH_NS}" targetNamespace="urn:transacthub:workflows">`);
  if (durable) for (const m of Object.values(SIGNAL_MESSAGES)) g.message(m);
  for (const [id, name] of [...g.messages].sort(([a], [b]) => a.localeCompare(b))) out.push(`  <message id="${id}" name="${esc(name)}"/>`);
  out.push(`  <signal id="sig_th_resume" name="th.resume" flowable:scope="processInstance"/>`);
  for (const s of [...g.signals].sort()) out.push(`  <signal id="${s}" name="${s}" flowable:scope="processInstance"/>`);
  for (const [id, code] of [...g.errors].sort(([a], [b]) => a.localeCompare(b))) out.push(`  <error id="${id}" errorCode="${esc(code)}"/>`);
  out.push(`  <process id="${pid}" name="${esc(wf.title ?? wf.name)}" isExecutable="true">`);
  out.push(`    ${ext(ann('workflow', { name: wf.name, release: wf.release, kind: wf.kind, wfKind: wf.wfKind, exit: exitOutcomes.join(',') }))}`);
  for (const x of [...lines, ...proc.els, ...endpointLines, ...g.processLevel, ...proc.flows]) out.push('    ' + x);
  out.push('  </process>');
  out.push('</definitions>', '');
  return out.join('\n');
}

// ----------------------------------------------------------- portability (C22-S5)

export interface PortabilityItem { flow: string; construct: string; target: string; note: string; severity: 'info' | 'warning' | 'error'; step?: string; alternative?: string }

/**
 * What a target does with each construct of a flow, by step (L24). For Flowable an `error` is a
 * construct outside the Flowable profile: the generator refuses it or the engine does not keep
 * its meaning. The report names the step and the portable alternative.
 */
export function portabilityReport(flows: any[], target: string): PortabilityItem[] {
  const out: PortabilityItem[] = [];
  const seen = new Set<string>();
  for (const f of flows) {
    const add = (construct: string, note: string, severity: PortabilityItem['severity'] = 'info', step?: string, alternative?: string) => {
      const k = `${f.name}|${construct}|${step ?? ''}|${note}`;
      if (!seen.has(k)) { seen.add(k); out.push({ flow: f.name, construct, target, note, severity, ...(step ? { step } : {}), ...(alternative ? { alternative } : {}) }); }
    };
    const steps: { st: any; inItem: boolean; inParallel: boolean }[] = [];
    const walk = (xs: any[], inItem = false, inParallel = false) => {
      for (const st of xs ?? []) {
        if (!st || typeof st !== 'object') continue;
        steps.push({ st, inItem, inParallel });
        for (const a of Object.values(st.handle ?? {})) if (Array.isArray(a)) walk(a, inItem, inParallel);
        if (Array.isArray(st.try)) walk(st.try, inItem, inParallel);
        if (Array.isArray(st.catch)) walk(st.catch.flatMap((c: any) => (Array.isArray(c?.do) ? c.do : [c])), inItem, inParallel);
        if (st.loop) walk(st.loop.do, inItem, inParallel);
        if (st.block) walk(st.block.do, inItem, inParallel);
        if (st.forEach) walk(st.forEach.do, true, inParallel);
        if (st.parallel) for (const x of st.parallel.branches ?? []) walk(x, inItem, true);
        if (st.switch) { for (const c of Object.values<any>(st.switch.cases ?? {})) walk(c, inItem, inParallel); walk(st.switch.default, inItem, inParallel); }
        if (st.await && Array.isArray(st.await.onTimeout)) walk(st.await.onTimeout, inItem, inParallel);
      }
    };
    walk(f.do);
    walk(f.finally);
    if (f.synchronous) add('synchronous', 'Marked synchronous (D9 open): the binding decides whether steps run as durable tasks.', 'warning');
    const known = ['call', 'end', 'return', 'switch', 'parallel', 'forEach', 'loop', 'await', 'wait', 'slot', 'use', 'try', 'hook', 'block'];
    for (const { st, inItem, inParallel } of steps) {
      const sid = st.id ?? (st.call ? shortName(st.call) : st.hook ? `hook ${st.hook}` : undefined);
      if (!known.some((k) => k in st) && target === 'flowable') add('unknown', `The generator cannot express a step with ${Object.keys(st).join(', ')}.`, 'error', sid, 'Use a construct of the portable core (Part 5, 5.3).');
      if (st.await) {
        if (target === 'flowable') add('await', 'A message that arrives before the catch event is held by the binding until the catch is waiting (R-09-16).', 'warning', sid);
        if (target === 'temporal') add('await', 'A signal with a timer; early signals are kept in workflow state.');
        if (target === 'dtfx') add('await', 'An external event raced with a durable timer; early events are kept in orchestration state.');
        if (target === 'elsa') add('await', 'The instance waits at an Elsa bookmark, raced with a timer; early messages are kept in its journal, in the workflow state.');
        if (st.await.calendar === 'business') add('await', 'Business calendar: the binding converts the timeout to business days.', 'warning', target === 'flowable' ? sid : undefined);
        // R-05-47: the timer is written with the policy's default, and the binding places it by the bank's value (docs/ISSUES.md I-57).
        if (target === 'flowable' && (st.await.timeoutParameter || st.await.calendarParameter)) add('await', `The wait is the bank's ${[st.await.timeoutParameter, st.await.calendarParameter].filter(Boolean).join(' and ')}: the timer is written with its default, ${st.await.timeout}, and the binding places it by the tenant's value (R-05-47).`, 'warning', sid);
        if (target === 'flowable' && inParallel) add('await', 'Two awaits for one message type in parallel branches cannot be told apart by the binding.', 'warning', sid, 'Await in sequence, or await distinct message types.');
        if (target === 'flowable' && st.await.family) add('await', `The binding delivers only a ${st.await.message} of family ${st.await.family} to this catch (R-05-44), and drops a held one older than the composite step (R-05-43).`, 'info', sid);
        if (target === 'flowable' && st.await.timeout && typeof st.await.timeout === 'object') add('await', 'The timeout is a parameter: the timer is written with its default, and the binding places it by the tenant\'s value (R-05-45).', 'warning', sid);
      }
      if (st.wait && typeof st.wait === 'object' && target === 'flowable') add('wait', 'The wait is a parameter: the timer is written with its default, and the binding places it by the tenant\'s value (R-05-45).', 'warning', sid ?? 'wait');
      if (st.forEach && (target === 'temporal' || target === 'dtfx' || target === 'elsa')) add('forEach', 'Large batches are chunked with continue-as-new; the chunk size comes from the binding (R-09-13).');
      if (st.forEach && target === 'flowable' && (st.forEach.concurrency ?? 1) > 1) add('forEach', `Step ${sid ?? 'forEach'}: concurrency ${st.forEach.concurrency} becomes a parallel multi-instance; Flowable starts every item at once and has no concurrency bound.`, 'warning', sid, 'Set concurrency 1 for a sequential multi-instance, or chunk the batch in the binding.');
      if (st.call && st.policy?.attemptTimeout && (target === 'dtfx' || target === 'elsa')) add('policy', `Step ${sid}: an attempt timeout abandons the late result; it does not cancel the task (R-09-11).`, 'warning');
      if (st.call && target === 'flowable') {
        add('policy', `Step ${sid}: attempts, backoff and total timeout are a modelled loop in an embedded sub-process, with timer boundaries.`, 'info', sid);
        if (inItem && inParallel) add('policy', `Step ${sid}: a step inside a parallel for each in a parallel branch shares outcome variables.`, 'warning', sid, 'Move the parallel branches into the item, or the item into a branch.');
      }
      if (st.try && target === 'flowable' && Array.isArray(st.catch)) add('try', `Step ${sid ?? 'try'}: catch runs in an error event sub-process of the try scope.`, 'info', sid);
      if (st.hook) add('hook', 'Bound per tenant at instance start; an unbound hook point is skipped.', 'info', target === 'flowable' ? sid : undefined);
      if (st.hook && target === 'flowable') add('hook', `Hook ${st.hook}: the binding enforces the hook point's contract on the hook's writes before the outcome is used.`, 'info', sid);
    }
    if (target === 'flowable' && steps.some((x) => x.st.call && x.inParallel)) {
      const writes = steps.filter((x) => x.inParallel && x.st.call && x.st.handle && Object.values(x.st.handle).some((a: any) => a?.compensate));
      for (const w of writes) add('compensate', `Step ${w.st.id}: compensation inside parallel branches runs in reverse document order, not reverse completion order.`, 'warning', w.st.id, 'Keep compensable writes out of parallel branches.');
    }
    if (f.deadline && target === 'flowable') add('deadline', 'An interrupting timer boundary on the main sub-process.');
    if (f.wfKind === 'handler') add('handler', 'A plain request handler at the interop boundary; no durable instance (R-09-14).');
  }
  return out;
}

/** A flow is portable to Flowable when the report holds no error for it (C22-S5). */
export function flowablePortable(flow: any): { portable: boolean; steps: PortabilityItem[] } {
  const items = portabilityReport([flow], 'flowable').filter((i) => i.severity === 'error');
  return { portable: items.length === 0, steps: items };
}
