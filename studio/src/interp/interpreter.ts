import { businessCalendarIn, resolveReferenceValues, type CalendarDays } from '../core/reference.js';
import { addDuration, durationMs, DEFAULT_CALENDAR, isDuration } from '../core/duration.js';
import { clone } from '../core/canonical.js';
import { coveredByAny, getPath, pick, setPath, type DeltaEntry } from '../core/paths.js';
import { compareReleases } from '../core/names.js';
import type { Store } from '../core/store.js';
import { evalExpr } from '../rules/expr.js';
import { moneyHelpers } from '../core/decimal.js';
import { runRuleSet, runTable, type SessionResult } from '../rules/service.js';
import { SimulatorInstance, type Message, type SimResponse } from '../sim/runtime.js';
import { Cancelled, Scope, VirtualClock } from './clock.js';
import { buildFacts } from './facts.js';
import { effectivePolicy, isRetryable, policyValues, retryInterval, type EffectivePolicy } from './policy.js';
import { ActivityFailure, TransientFailure, holdDecision, outcomeForStatus, primaryOutcome, reference, type ActivityIO, type ActivityResult } from './reference.js';
import { ContractViolation, runScript, ScriptError, type ScriptRegistry } from './sandbox.js';
import { callsComposite, expandSubflow, expandWorkflow, isComposite } from '../core/expand.js';
import { runPythonScript } from './pysandbox.js';
import { attrs, inSpan, meters, SpanKind, SpanStatusCode, traceHeaders, tracer } from '../telemetry/telemetry.js';
import { forgetNativeToken, nativeAuthHeaders, nativeFetch } from './native-auth.js';
import { attemptDistribution } from './attempt-distribution.js';

// The studio interpreter (C09). It runs the workflow DSL on the virtual clock and is the
// reference for every engine. It never evaluates rules, reads values or calls ports inline:
// those run in activities (R-05-25, R-09-06).

export const SIGNALS = ['pause', 'resume', 'cancel', 're-screen'] as const;
/**
 * The operator's controls of a parked step (R-05-55): `retry` gives it one more attempt, `redrive` runs it again with its full
 * retry policy. They are not signals a workflow declares (R-05-12): any parked step takes them.
 */
// requeue: a dead-lettered (parked) step run again with its full retry policy, as redrive (R-09-91).
export const OPERATOR_CONTROLS = ['retry', 'redrive', 'requeue'] as const;

export interface BenchEntry { port: string; simulator: string; scenario: string }

export interface Supersession { artefact: string; replaces: string; with: string; id?: string; effectiveAt?: string }

export interface StubSpec {
  outcomes?: string[]; // per call; the last repeats
  failTimes?: number; // transient failures before the first success
  hangTimes?: number; // attempts that never answer
  latency?: string; // time each attempt takes
  writes?: Record<string, any>;
  reasonCode?: string;
  error?: string; // a non-retryable error
  decisions?: Record<string, any>;
  emit?: { type: string; after?: string; status?: string }; // a message the party sends back
}

/** The bench's word for a port the runtime answers through the bank's port binding, in place of a simulator. */
export const RUNTIME_PORT = 'runtime';

export interface RunConfig {
  store: Store;
  scripts: ScriptRegistry;
  tenant?: string;
  evaluationDate: string;
  startTime?: string; // HH:MM:SS, UTC
  bench?: BenchEntry[];
  supersessions?: Supersession[];
  signals?: { after: string; signal: string; step?: string }[];
  stubs?: Record<string, StubSpec>;
  values?: { parameter: string; value: any; validFrom: string; validTo?: string }[];
  documents?: Record<string, any>; // extra documents by name@release or name (kit content, drafts under test)
  /** The inbound payload; with `message`, the front door receives it as that catalogue message, as from the bank's own channel. */
  inbound?: { payload?: any; malformed?: boolean; message?: string };
  limit?: string;
  bindings?: any[]; // hook bindings, in place of the tenant's configuration set
  availableFrom?: Record<string, string>; // name@release -> virtual time it is published
  /** A deployed worker's values from the runtime, in force at the attempt's time (parameter -> value); set per attempt. */
  liveValues?: Record<string, any>;
  /** The debugger (F-17): each event also carries the instance's data after it, and each step what it decided. */
  debug?: boolean;
}

export interface TimelineEvent {
  at: number; // virtual ms since the run started
  instance: string;
  step?: string;
  type: string;
  detail?: any;
  /** Debug runs: the instance's data after the event, or `same` when it did not change. */
  data?: any;
  same?: boolean;
  omitted?: boolean;
}

/** A debug run keeps at most this much data, in characters, across its snapshots; beyond, events say so. */
export const DEBUG_DATA_LIMIT = 20_000_000;

export interface StepRecord {
  step: string;
  activity?: string;
  release?: string;
  pinned?: string;
  supersededBy?: string;
  outcome?: string;
  attempts: number;
  policy?: Record<string, any>;
  policySources?: Record<string, string>;
  delta: DeltaEntry[];
  startedAt: number;
  endedAt?: number;
  item?: number;
}

export class EndFlow {
  constructor(public outcome: string, public compensate = false) {}
}

/** A `return` inside a block gives the block its outcome (Part 5, 5.6). */
export class ReturnFlow {
  constructor(public outcome: string) {}
}

export class StepFailure extends Error {
  constructor(public step: string, message: string, public errorName = 'Failure') {
    super(message);
  }
}

export interface TryFrame { writes: CompletedWrite[] }
export interface CompletedWrite { step: string; activity: string; compensation: string }

export interface Frame {
  inst: Instance;
  ctx: any;
  scope: Scope;
  hookOwner: string; // the workflow or segment that declares hook points for these steps
  tryStack: TryFrame[];
  inFinally?: boolean;
  detail?: boolean; // inside a block: steps go to the detail, not the business trace
  item?: { index: number; item: any; parent: any };
  label?: (id: string) => string;
  blockStartedAt?: number; // when the innermost composite step began (R-05-43)
  sink?: ItemSink; // an item of a forEach whose items run side by side: its entries, put in item order (R-05-53)
}

/** What one item of a forEach recorded while items ran side by side; the forEach puts it into the trace in item order (R-05-53). */
interface ItemSink {
  trace: string[];
  detail: string[];
  releaseTrace: string[];
}

let idSeq = 0;

/** Can an await that names this family (or none) take this message? A message with no family fits any await (R-05-44). */
export function familyFits(awaitFamily: string | undefined, m: { family?: string }): boolean {
  return !awaitFamily || !m.family || m.family === awaitFamily;
}

export class Instance {
  readonly id: string;
  trace: string[] = [];
  detail: string[] = []; // the steps inside composite steps (R-05-38)
  expandedFrom?: { release: string; hash: string };
  releaseTrace: string[] = [];
  timeline: TimelineEvent[] = [];
  steps: StepRecord[] = [];
  outcomes = new Map<string, string>(); // last outcome per step id
  decisions: Record<string, any> = {};
  ruleTraces: { step: string; result: SessionResult }[] = [];
  deltas: { step: string; release?: string; delta: DeltaEntry[] }[] = [];
  attempts: Record<string, number> = {};
  completedWrites: CompletedWrite[] = [];
  children: Instance[] = [];
  outcome?: string;
  reasonCode?: string;
  ended = false;
  failed?: string;
  startedAt: number;
  endedAt?: number;
  pins = new Map<string, string>();
  bindings: any[] = [];
  supersessionsApplied: { artefact: string; from: string; to: string; step: string; id?: string }[] = [];
  mailbox: Message[] = [];
  consumed = new Set<string>();
  expiredAwaits = new Set<string>();
  awaitedKeys = new Set<string>(); // the message of every await this instance has started (R-05-43)
  awaiting?: { type: string; family?: string; key?: string; value?: any; resolve: (m: Message) => void; stepId: string };
  waiters: { type: string; resolve: (m: Message) => void }[] = [];
  paused = false;
  resumeWaiters: (() => void)[] = [];
  /** The step parked on a failure, waiting for an operator's control or its park time (R-05-55). */
  parked?: { step: string; resolve: (control: string | null) => void };
  bodyScope!: Scope;
  screenSteps: { step: any; frame: Frame }[] = [];
  legs: Record<string, string> = {};
  engineStates: any[] = [];
  durable: boolean;
  callCounts = new Map<string, number>();

  constructor(readonly run: Run, public doc: any, public ctx: any, readonly kind: string, readonly parent?: Instance) {
    this.id = `${kind}-${++idSeq}`;
    this.startedAt = run.clock.now;
    this.durable = kind !== 'handler' && kind !== 'activity';
  }

  get name(): string {
    return this.doc?.name ?? this.doc?.id ?? this.kind;
  }

  private lastData?: string;

  event(type: string, step?: string, detail?: any) {
    const e: TimelineEvent = { at: this.run.clock.elapsed, instance: this.id, step, type, detail };
    if (this.run.cfg.debug) {
      // The data after this event, kept only when it changed since this instance's last event.
      const s = JSON.stringify(this.ctx ?? {});
      if (s === this.lastData) e.same = true;
      else if (this.run.debugData + s.length > DEBUG_DATA_LIMIT) e.omitted = true;
      else {
        e.data = JSON.parse(s);
        this.run.debugData += s.length;
        this.lastData = s;
      }
    }
    this.timeline.push(e);
    this.run.timeline.push(e);
  }

  /** Engine state carries references only (R-02-08, R-09-10): header, facts and message refs. */
  engineState() {
    return clone({ header: this.ctx.header, facts: this.ctx.facts, refs: this.ctx.messages });
  }

  pendingTrace: string[] = [];

  async gate(scope: Scope) {
    // A signal is seen at the next step boundary, as an engine sees it between workflow tasks.
    if (this.pendingTrace.length) {
      this.trace.push(...this.pendingTrace);
      this.pendingTrace = [];
    }
    while (this.paused) {
      await new Promise<void>((resolve, reject) => {
        this.resumeWaiters.push(resolve);
        scope.onCancel((r) => reject(new Cancelled(r)));
      });
    }
    scope.throwIfCancelled();
  }
}

// ======================================================================= the run

export class Run {
  clock: VirtualClock;
  instances: Instance[] = [];
  handlerRuns: Instance[] = [];
  timeline: TimelineEvent[] = [];
  debugData = 0;
  messages: Message[] = [];
  unmatched: { message: Message; reason: string }[] = [];
  duplicates: Message[] = [];
  sims = new Map<string, SimulatorInstance>();
  mailbox: Message[] = [];
  selfChecks: { simulator: string; scenario: string; message: string; ruleSet: string; malformed: boolean; worst: string; findings: any[] }[] = [];

  /**
   * Self-check (R-07-06, C10-S3): a message a simulator emits must pass the simulator's
   * self-check rule sets, unless its scenario is marked malformed.
   */
  selfCheck(m: Message, ctx: any) {
    const sim = this.sims.get(m.port);
    if (!sim || m.from !== 'simulator') return;
    for (const rs of sim.sim.selfCheck ?? []) {
      const facts = buildFacts({ ctx, reads: ['header', 'payment', 'batch', 'items[]', 'status', 'facts', 'ext'], extensions: this.extensions() });
      const res = runRuleSet(rs, facts, { resolve: (n) => this.resolveDoc(n), lookup: (o, p, d, date) => this.lookupParam(o, p, d, date), evaluationDate: this.clock.today() });
      this.selfChecks.push({ simulator: sim.sim.name, scenario: sim.scenarioName, message: m.type, ruleSet: rs, malformed: !!m.malformed || sim.malformed, worst: res.worst, findings: res.findings });
    }
  }

  /**
   * Take an early message of a type that belongs to this instance's payment (R-05-30): one of the await's family
   * (R-05-44), received after the step that holds the await began (R-05-43). One received before is not this
   * await's: it is recorded as unmatched and handed to operations (R-05-31).
   */
  takeEarly(inst: Instance, type: string, family?: string, since = Number.NEGATIVE_INFINITY): Message | undefined {
    const fits = (x: Message) => x.type === type && familyFits(family, x) && this.correlatesPublic(inst, x);
    for (const stale of this.mailbox.filter((x) => fits(x) && (x.receivedAt ?? x.at) < since)) {
      this.mailbox = this.mailbox.filter((x) => x !== stale);
      this.unmatched.push({ message: stale, reason: 'received before the await began' });
      inst.event('unmatched', undefined, { type: stale.type, reason: 'received before the await began' });
    }
    const m = this.mailbox.find(fits);
    if (m) this.mailbox = this.mailbox.filter((x) => x !== m);
    return m;
  }

  correlatesPublic(i: Instance, m: Message) {
    return this.correlates(i, m);
  }
  defaultBindings: BenchEntry[] = [];
  warnings: string[] = [];
  private seq = 0;
  readonly docs: Record<string, any>;
  readonly configSet: any;

  constructor(readonly cfg: RunConfig) {
    const start = Date.parse(`${cfg.evaluationDate}T${cfg.startTime ?? '06:00:00'}Z`);
    this.clock = new VirtualClock(start);
    this.docs = cfg.documents ?? {};
    this.configSet = cfg.tenant ? cfg.store.configurationSet(cfg.tenant) : undefined;
    for (const b of cfg.bench ?? []) if (b.simulator !== RUNTIME_PORT) this.bindSim(b, cfg.inbound);
  }

  /** A port the bench gives to the runtime: its builtin activities run in the runtime's native host, on the bank's port binding (R-08-15). */
  onRuntime(port: string): boolean {
    return (this.cfg.bench ?? []).some((b) => b.port === port && b.simulator === RUNTIME_PORT);
  }

  newId(prefix: string) {
    return `${prefix}-${++this.seq}`;
  }

  // ----------------------------------------------------------- resolution

  /** Resolve a major line to a document: kit documents, then the store's latest published release. */
  available(key: string): boolean {
    const from = this.cfg.availableFrom?.[key];
    return !from || this.clock.elapsed >= durationMs(from);
  }

  resolveDoc(name: string, release?: string): any {
    if (release && this.docs[`${name}@${release}`]) return this.docs[`${name}@${release}`];
    if (!release && this.docs[name]) return this.docs[name];
    if (!release) {
      const kitReleases = Object.keys(this.docs).filter((k) => k.startsWith(name + '@') && this.available(k)).sort((a, b) => compareReleases(a.split('@')[1], b.split('@')[1]));
      if (kitReleases.length) return this.docs[kitReleases[kitReleases.length - 1]];
    }
    return this.cfg.store.doc(name, release, this.cfg.tenant);
  }

  /** The release an instance runs for a line: its pin, unless a supersession applies to a step not yet started. */
  resolveFor(inst: Instance, name: string, stepId: string): { doc: any; release: string; pinned: string; superseded?: string } {
    let pinned = inst.pins.get(name);
    if (!pinned) {
      const d = this.resolveDoc(name);
      if (!d) throw new StepFailure(stepId, `${name} does not resolve to a published release (L03)`);
      pinned = d.release;
      inst.pins.set(name, pinned!);
    }
    let release = pinned!;
    let superseded: string | undefined;
    for (const s of this.cfg.supersessions ?? []) {
      if (s.artefact !== name || s.replaces !== pinned) continue;
      if (s.effectiveAt && this.clock.elapsed < durationMs(s.effectiveAt)) continue;
      release = s.with;
      superseded = s.with;
      if (!inst.supersessionsApplied.some((a) => a.artefact === name && a.step === stepId)) {
        inst.supersessionsApplied.push({ artefact: name, from: pinned!, to: s.with, step: stepId, id: s.id });
        inst.releaseTrace.push(`supersede:${shortName(name)} ${pinned} to ${s.with}`);
        inst.event('supersede', stepId, { artefact: name, from: pinned, to: s.with, id: s.id });
      }
    }
    const doc = this.resolveDoc(name, release);
    if (!doc) throw new StepFailure(stepId, `${name}@${release} does not resolve`);
    return { doc, release, pinned: pinned!, superseded };
  }

  /** Parameter values are looked up by date at each step that reads them (R-01-17). */
  lookupParam(owner: string, param: string, decl: any, date: string): any {
    const key = `${owner}#${param}`;
    // A deployed worker on a runtime (TH_RUNTIME_URL): the bank's values as the runtime holds them at the attempt's time,
    // published there after the package was built (plan P6). They take the place of the package's configuration set.
    if (this.cfg.liveValues && key in this.cfg.liveValues) return this.cfg.liveValues[key];
    // A value read from a reference table (R-11-51) is read here, edition by edition, from the table the store holds.
    const values = resolveReferenceValues([...(this.configSet?.values ?? []), ...(this.cfg.values ?? [])].filter((v: any) => v.parameter === key),
      (n) => this.resolveDoc(n));
    const inForce = values.filter((v: any) => v.validFrom <= date && (!v.validTo || v.validTo >= date));
    inForce.sort((a: any, b: any) => (a.validFrom < b.validFrom ? -1 : a.validFrom > b.validFrom ? 1 : 0));
    if (inForce.length) return inForce[inForce.length - 1].value;
    return decl?.default;
  }

  /**
   * The bank's business calendar on a date (R-09-88, Issue I-127): the reference table of domain business-calendar the run sees
   * (the bank's own, else the platform's), as weekend days and holidays; the default calendar (Saturday and Sunday) without one.
   */
  businessCalendar(date: string): CalendarDays {
    const names = new Set<string>([...this.cfg.store.byKind('reference-table').map((e) => e.name), ...Object.values<any>(this.docs).filter((d) => d?.kind === 'reference-table').map((d) => d.name)]);
    const tables = [...names].map((n) => this.resolveDoc(n)).filter(Boolean);
    return businessCalendarIn(tables, date) ?? { weekend: [...DEFAULT_CALENDAR.weekend], holidays: [...DEFAULT_CALENDAR.holidays] };
  }

  /** Hook bindings in force when an instance starts (R-06-13). */
  bindingsAt(date: string): any[] {
    const all = [...(this.cfg.bindings ?? this.configSet?.bindings ?? [])];
    return all.filter((b: any) => b.validFrom <= date && (!b.validTo || b.validTo >= date));
  }

  extensions(): any[] {
    return [...this.cfg.store.byKind('extension').map((e) => e.doc), ...Object.values(this.docs).filter((d: any) => d?.kind === 'extension')];
  }

  // -------------------------------------------------------------- simulators

  bindSim(b: BenchEntry, inbound?: RunConfig['inbound']) {
    const sim = this.resolveDoc(b.simulator);
    if (!sim) throw new Error(`Simulator ${b.simulator} does not resolve`);
    const inst = new SimulatorInstance(sim, b.scenario, {
      clock: this.clock,
      deliver: (m) => this.frontDoor(m),
      newId: (p) => this.newId(p),
      log: (event, detail) => this.timeline.push({ at: this.clock.elapsed, instance: 'simulator', type: event, detail }),
    }, sim.direction === 'inward' ? inbound : undefined);
    this.sims.set(b.port, inst);
    return inst;
  }

  /** A port with no bench entry uses its first simulator in scenario accept, and the run says so. */
  simFor(port: string): SimulatorInstance {
    let s = this.sims.get(port);
    if (s) return s;
    const candidates = [
      ...Object.values(this.docs).filter((d: any) => d?.kind === 'simulator' && d.port === port),
      ...this.cfg.store.byKind('simulator').map((e) => e.doc).filter((d) => d.port === port),
    ].filter((d: any) => d.direction === 'outward' || (d.scenarios ?? []).some((x: any) => x.name === 'accept'));
    candidates.sort((a: any, b: any) => (a.name < b.name ? -1 : 1));
    const sim = candidates[0] ?? [...this.cfg.store.byKind('simulator').map((e) => e.doc)].find((d) => d.port === port);
    if (!sim) throw new StepFailure('', `No simulator implements port ${port} (L22)`);
    const scenario = (sim.scenarios ?? []).some((x: any) => x.name === 'accept') ? 'accept' : sim.scenarios[0].name;
    const entry = { port, simulator: sim.name, scenario };
    this.defaultBindings.push(entry);
    this.warnings.push(`Port ${port} is not in the bench; it used ${sim.name} in scenario ${scenario}`);
    return this.bindSim(entry);
  }

  // -------------------------------------------------------------- front door

  /** Every message that reaches the platform passes the front door (Part 5, 5.3; R-05-30 to R-05-32). */
  frontDoor(m: Message) {
    m.receivedAt = this.clock.now;
    this.messages.push(m);
    this.timeline.push({ at: this.clock.elapsed, instance: 'front-door', type: 'message-in', detail: { id: m.id, type: m.type, status: m.status, copy: m.copy } });
    const entry = this.catalogueEntry(m.type);
    const disp = entry?.disposition ?? {};
    if (disp.start || disp.handler) {
      if (m.copy) {
        this.duplicates.push(m);
        return;
      }
      this.onStart?.(m, entry);
      return;
    }
    // Deliver: find the instance by correlation.
    const candidates = this.allInstances().filter((i) => this.correlates(i, m));
    const alive = candidates.find((i) => !i.ended && ((i.awaiting?.type === m.type && familyFits(i.awaiting.family, m)) || i.waiters.some((w) => w.type === m.type))) ?? [...candidates].reverse().find((i) => !i.ended);
    const target = alive ?? candidates[candidates.length - 1];
    if (!target) {
      this.unmatched.push({ message: m, reason: 'no instance' });
      this.timeline.push({ at: this.clock.elapsed, instance: 'front-door', type: 'unmatched', detail: { type: m.type, reason: 'no instance' } });
      return;
    }
    const id = m.inReplyTo ? `${m.type}:${m.inReplyTo}:${m.status ?? ''}` : m.id;
    const seen = (x: Message) => (x.inReplyTo ? `${x.type}:${x.inReplyTo}:${x.status ?? ''}` : x.id) === id;
    if (candidates.some((c) => c.consumed.has(id)) || this.mailbox.some(seen)) {
      this.duplicates.push(m);
      target.event('duplicate-ignored', undefined, { type: m.type });
      return;
    }
    if (target.ended) {
      this.unmatched.push({ message: m, reason: 'instance ended' });
      target.event('unmatched', undefined, { type: m.type, reason: 'instance ended' });
      return;
    }
    const w = target.waiters.find((x) => x.type === m.type);
    if (w) {
      target.waiters = target.waiters.filter((x) => x !== w);
      target.consumed.add(id);
      w.resolve(m);
      return;
    }
    if (target.awaiting && target.awaiting.type === m.type && familyFits(target.awaiting.family, m)) {
      const a = target.awaiting;
      target.awaiting = undefined;
      target.consumed.add(id);
      a.resolve(m);
      return;
    }
    if (target.expiredAwaits.has(m.type)) {
      this.unmatched.push({ message: m, reason: 'await timed out' });
      target.event('unmatched', undefined, { type: m.type, reason: 'await timed out' });
      return;
    }
    // Early: kept for the await that will consume it, whichever instance of the payment that is.
    this.mailbox.push(m);
    target.event('message-kept', undefined, { type: m.type });
  }

  onStart?: (m: Message, entry: any) => void;

  catalogueEntry(type: string): any {
    const scope = type.split('.').slice(0, 2).join('.');
    const scheme = this.cfg.store.scheme(scope) ?? Object.values(this.docs).find((d: any) => d?.kind === 'scheme' && d.scope === scope);
    return scheme?.messages?.find((x: any) => x.id === type);
  }

  allInstances(): Instance[] {
    const out: Instance[] = [];
    const add = (i: Instance) => {
      out.push(i);
      i.children.forEach(add);
    };
    this.instances.forEach(add);
    return out;
  }

  private correlates(i: Instance, m: Message): boolean {
    if (m.conversation && i.ctx?.header?.conversation === m.conversation) return true;
    const c = m.correlation ?? {};
    const h = i.ctx?.header ?? {};
    if (c.paymentId && h.paymentId === c.paymentId) return true;
    if (c.endToEndId && h.endToEndId === c.endToEndId) return true;
    if (c.fileId && h.fileId === c.fileId) return true;
    return false;
  }

  // ------------------------------------------------------------------ driving

  async drive(done: () => boolean) {
    const limit = this.cfg.limit ? durationMs(this.cfg.limit) : undefined;
    return this.clock.run(done, limit);
  }

  scheduleSignals(inst: Instance) {
    for (const s of this.cfg.signals ?? []) {
      this.clock.at(this.clock.start + durationMs(s.after), () => this.signal(inst, s.signal), `signal ${s.signal}`);
    }
  }

  /** The fixed signals (R-05-12). */
  signal(inst: Instance, signal: string) {
    if (OPERATOR_CONTROLS.includes(signal as any)) return this.operator(inst, signal);
    if (!SIGNALS.includes(signal as any)) throw new Error(`Unknown signal ${signal}`);
    if (inst.ended) return;
    inst.event('signal', undefined, { signal });
    if (signal === 'pause') {
      inst.paused = true;
      if (inst.awaiting) inst.trace.push(`${inst.awaiting.stepId}:waiting`, 'signal:pause');
      else inst.pendingTrace.push('signal:pause');
      return;
    }
    if (inst.awaiting) inst.trace.push(`${inst.awaiting.stepId}:waiting`);
    inst.trace.push(...inst.pendingTrace.splice(0), `signal:${signal}`);
    if (signal === 'resume') {
      inst.paused = false;
      const ws = inst.resumeWaiters;
      inst.resumeWaiters = [];
      ws.forEach((w) => w());
    } else if (signal === 'cancel') {
      inst.bodyScope.cancel({ signal: 'cancel' });
    } else if (signal === 're-screen') {
      void this.rescreen(inst);
    }
  }

  /** An operator's control of a parked step (R-05-55). A control for an instance with no parked step changes nothing. */
  operator(inst: Instance, control: string) {
    if (inst.ended || !inst.parked) return;
    const p = inst.parked;
    inst.parked = undefined;
    inst.event('operator', p.step, { control });
    inst.trace.push(`operator:${control}`);
    p.resolve(control);
  }

  /** Re-screen runs the screen steps again on a waiting payment (CK-SIGNAL-03). */
  private async rescreen(inst: Instance) {
    for (const s of inst.screenSteps) {
      try {
        await execCall(s.step, { ...s.frame, scope: inst.bodyScope.child() });
      } catch (e) {
        if (e instanceof EndFlow) {
          inst.bodyScope.cancel({ end: e });
          return;
        }
        if (!(e instanceof Cancelled)) inst.bodyScope.cancel({ failure: e });
      }
    }
  }
}

function shortName(name: string): string {
  const parts = name.split('.');
  return parts.length > 2 ? parts[parts.length - 2] : name;
}

// =================================================================== instances

export async function startInstance(run: Run, doc: any, ctx: any, kind: string, parent?: Instance, opts: { legs?: Record<string, string>; scope?: Scope } = {}): Promise<Instance> {
  const inst = new Instance(run, doc, ctx, kind, parent);
  if (parent) parent.children.push(inst);
  else if (kind === 'handler') run.handlerRuns.push(inst);
  else run.instances.push(inst);
  inst.legs = opts.legs ?? {};
  // The interpreter runs the expanded form and records the authored release and hash (R-05-36).
  if ((doc.kind === 'workflow' || doc.kind === 'hook') && callsComposite(doc, (n) => run.resolveDoc(n))) {
    inst.doc = expandWorkflow(doc.kind === 'hook' ? { ...doc, kind: 'workflow' } : doc, (n) => run.resolveDoc(n));
    if (doc.kind === 'hook') inst.doc = { ...inst.doc, kind: 'hook' };
  }
  if (doc.form === 'expanded' || inst.doc.form === 'expanded') inst.expandedFrom = inst.doc.expandedFrom;
  // Pins are taken when the instance starts (R-01-16); bindings likewise (R-06-13).
  inst.pins = pinsFrom(run, doc);
  inst.bindings = run.bindingsAt(run.clock.today());
  inst.releaseTrace.push(`${shortName(doc.name)}@${doc.release}`);
  inst.event('start', undefined, { workflow: doc.name, release: doc.release, kind });
  if (!parent && kind !== 'handler') run.scheduleSignals(inst);
  await runWorkflowBody(inst, opts.scope ?? new Scope());
  return inst;
}

function pinsFrom(run: Run, doc: any): Map<string, string> {
  const pins = new Map<string, string>();
  for (const a of doc.artefacts ?? []) pins.set(a.name, a.release);
  // Studio runs without an artefacts block pin the latest published release at start.
  void run;
  return pins;
}

async function runWorkflowBody(inst: Instance, outer: Scope) {
  const run = inst.run;
  const doc = inst.doc;
  const body = outer.child();
  inst.bodyScope = body;
  const frame: Frame = { inst, ctx: inst.ctx, scope: body, hookOwner: doc.name, tryStack: [], detail: doc.wfKind === 'subflow' };
  let outcome: string | undefined;
  let deadlineOff = () => {};
  if (doc.deadline?.after) {
    const at = addDuration(run.clock.now, doc.deadline.after);
    deadlineOff = run.clock.at(at, () => body.cancel({ deadline: true }), `deadline of ${doc.name}`);
  }
  try {
    await execSteps(doc.do ?? [], frame);
    outcome = doc.exit?.outcomes?.[0] ?? 'done';
  } catch (e: any) {
    outcome = await settle(e, inst, frame);
  } finally {
    deadlineOff();
  }
  // Finally runs on every end, after any compensation (R-05-14).
  if (doc.finally?.length) {
    const fscope = outer.child();
    const fframe: Frame = { ...frame, scope: fscope, inFinally: true, label: (id) => `finally:${id}` };
    try {
      await execSteps(doc.finally, fframe);
    } catch (e: any) {
      if (!(e instanceof EndFlow)) inst.event('finally-failed', undefined, { error: String(e?.message ?? e) });
    }
  }
  inst.outcome = outcome;
  inst.ended = true;
  inst.endedAt = run.clock.now;
  inst.trace.push(`end:${outcome}`);
  inst.releaseTrace.push(`end:${outcome}`);
  inst.event('end', undefined, { outcome });
  for (const w of inst.waiters) void w;
}

/** Turn whatever ended the body into an exit outcome, running compensation and handlers as needed. */
async function settle(e: any, inst: Instance, frame: Frame): Promise<string> {
  const doc = inst.doc;
  if (e instanceof EndFlow) return e.outcome;
  if (e instanceof ReturnFlow) return e.outcome; // a subflow run alone
  if (e instanceof Cancelled) {
    const r = e.reason;
    if (r?.end instanceof EndFlow) return r.end.outcome;
    if (r?.failure) return fail(inst, r.failure);
    if (r?.deadline) {
      inst.trace.push('deadline:fired');
      inst.event('deadline');
      const dframe: Frame = { ...frame, scope: new Scope() };
      try {
        await execSteps(doc.deadline.do ?? [], dframe);
        return doc.exit?.outcomes?.[0] ?? 'late';
      } catch (x: any) {
        if (x instanceof EndFlow) return x.outcome;
        return fail(inst, x);
      }
    }
    if (r?.signal === 'cancel') {
      // Cancel runs compensation for completed steps in reverse order, then finally (R-05-13).
      await compensate(inst.completedWrites, { ...frame, scope: new Scope() });
      return 'cancelled';
    }
    return 'cancelled';
  }
  return fail(inst, e);
}

function fail(inst: Instance, e: any): string {
  inst.failed = String(e?.message ?? e);
  inst.event('failed', (e as StepFailure)?.step, { error: inst.failed });
  return 'failed';
}

// ======================================================================= steps

async function execSteps(steps: any[], frame: Frame) {
  for (const st of steps ?? []) {
    await frame.inst.gate(frame.scope);
    await execStep(st, frame);
  }
}

function label(frame: Frame, id: string): string {
  if (frame.label) return frame.label(id);
  if (frame.item) return `${id}[${frame.item.index}]`;
  return id;
}

async function execStep(st: any, frame: Frame) {
  if ('call' in st) return execCall(st, frame);
  if ('end' in st && Object.keys(st).every((k) => ['end', 'compensate', 'id'].includes(k))) {
    if (st.compensate === 'all' || st.compensate === true) await compensateScope(frame);
    throw new EndFlow(st.end);
  }
  if ('switch' in st) return execSwitch(st, frame);
  if ('parallel' in st) return execParallel(st, frame);
  if ('forEach' in st) return execForEach(st, frame);
  if ('loop' in st) return execLoop(st, frame);
  if ('await' in st) return execAwait(st, frame);
  if ('wait' in st) return execWait(st, frame);
  if ('slot' in st) return execSlot(st, frame);
  if ('use' in st) return execUse(st, frame);
  if ('try' in st) return execTry(st, frame);
  if ('hook' in st) return execHook(st, frame);
  if ('block' in st) return execBlock(st, frame);
  if ('return' in st && Object.keys(st).every((k) => ['return', 'id'].includes(k))) throw new ReturnFlow(st.return);
  throw new StepFailure(st.id ?? '?', `Unknown step ${JSON.stringify(Object.keys(st))}`);
}

/** Run the action for a non-primary outcome: steps, an end, or continue. */
async function runAction(action: any, frame: Frame) {
  if (Array.isArray(action)) return execSteps(action, frame);
  if (action && typeof action === 'object') {
    if ('end' in action) {
      if (action.compensate === 'all' || action.compensate === true) await compensateScope(frame);
      throw new EndFlow(action.end);
    }
    if ('return' in action) throw new ReturnFlow(action.return);
    if (action.continue) return;
  }
}

/** Put a trace entry where it belongs: the business trace, or the detail inside a block. */
function record(frame: Frame, entry: string) {
  const to = frame.sink ?? frame.inst;
  (frame.detail ? to.detail : to.trace).push(entry);
}

/** A composite step in the expanded form: its subflow's steps run as a frame (C09-S12). */
async function execBlock(st: any, frame: Frame) {
  const inst = frame.inst;
  const id = st.id;
  const act = inst.run.resolveFor(inst, st.block.of, id).doc;
  if (st.block.by) inst.run.resolveFor(inst, st.block.by, id);
  inst.event('block-start', label(frame, id), { of: st.block.of, by: st.block.by });
  let outcome = primaryOutcome(act);
  try {
    await execSteps(st.block.do ?? [], { ...frame, detail: true, blockStartedAt: inst.run.clock.now });
  } catch (e) {
    if (e instanceof ReturnFlow) outcome = e.outcome;
    else throw e;
  }
  inst.outcomes.set(id, outcome);
  record(frame, frame.inFinally ? `finally:${id}` : `${label(frame, id)}:${outcome}`);
  inst.event('block-end', label(frame, id), { outcome });
  if (outcome === primaryOutcome(act)) return;
  const action = st.handle?.[outcome];
  if (action === undefined) throw new StepFailure(id, `Outcome ${outcome} of ${id} is not handled (L05)`);
  await runAction(action, frame);
}

async function compensateScope(frame: Frame) {
  const top = frame.tryStack[frame.tryStack.length - 1];
  const writes = top ? top.writes : frame.inst.completedWrites;
  await compensate(writes, frame);
}

/** Run the compensation of completed write steps in reverse order (R-05-15). */
async function compensate(writes: CompletedWrite[], frame: Frame) {
  const inst = frame.inst;
  for (const w of [...writes].reverse()) {
    const done = inst.completedWrites.indexOf(w);
    if (done < 0) continue;
    inst.completedWrites.splice(done, 1);
    for (const t of frame.tryStack) t.writes = t.writes.filter((x) => x !== w);
    (frame.sink ?? inst).trace.push(`undo:${w.step}`);
    inst.event('compensate', w.step, { compensation: w.compensation });
    try {
      await runActivityStep({ id: `undo-${w.step}`, call: w.compensation }, { ...frame, scope: new Scope() }, { silent: true });
    } catch (e: any) {
      inst.event('compensation-failed', w.step, { error: String(e?.message ?? e) });
    }
  }
}

// ------------------------------------------------------------------------ call

async function execCall(st: any, frame: Frame) {
  const inst = frame.inst;
  const id = st.id ?? shortName(st.call);
  const { outcome, act } = await runActivityStep(st, frame);
  const prim = primaryOutcome(act);
  inst.outcomes.set(id, outcome);
  if (act.type === 'screen' && !frame.inFinally && !inst.screenSteps.some((s) => s.step === st)) inst.screenSteps.push({ step: st, frame });
  if (outcome === prim) {
    if (act.compensation && (act.effect === 'write' || act.effect === 'external')) {
      const w = { step: id, activity: act.name, compensation: act.compensation };
      inst.completedWrites.push(w);
      for (const t of frame.tryStack) t.writes.push(w);
    }
    return;
  }
  const action = st.handle?.[outcome];
  if (action === undefined) {
    // An outcome nobody handles; lint L05 refuses this at publish.
    throw new StepFailure(id, `Outcome ${outcome} of ${id} is not handled (L05)`);
  }
  await runAction(action, frame);
}

/** Run one activity with its effective policy. Returns the outcome; trace and record are kept. */
async function runActivityStep(st: any, frame: Frame, opts: { silent?: boolean } = {}): Promise<{ outcome: string; act: any }> {
  const inst = frame.inst;
  const run = inst.run;
  const id = st.id ?? shortName(st.call);
  const resolved = run.resolveFor(inst, st.call, id);
  const act = resolved.doc;
  if (!act || act.kind !== 'activity') throw new StepFailure(id, `${st.call} is not an activity`);
  const policy = effectivePolicy(act, st.policy);
  const rec: StepRecord = {
    step: label(frame, id), activity: act.name, release: resolved.release, pinned: resolved.pinned, supersededBy: resolved.superseded,
    attempts: 0, policy: policyValues(policy), policySources: Object.fromEntries(Object.entries(policy).map(([k, v]) => [k, v.source])),
    delta: [], startedAt: run.clock.elapsed, item: frame.item?.index,
  };
  inst.steps.push(rec);
  inst.event('step-start', label(frame, id), { activity: act.name, release: resolved.release, policy: rec.policy });
  if (!opts.silent) (frame.sink ?? inst).releaseTrace.push(`${shortName(act.name)}@${resolved.release}`);
  const level = st.level ?? act.level;
  let result: ActivityResult;
  let attempts = 0;
  let once = false;
  let parkedBefore = false;
  for (;;) {
    try {
      const r = await attemptLoop(act, st, policy, frame, id, level, once);
      result = r.result;
      attempts = parkedBefore ? countAttempts(inst, frame, id) : r.attempts;
      break;
    } catch (e) {
      // A step that failed parks, when its policy says so, for an operator's retry or re-drive (R-05-55).
      if (!(e instanceof StepFailure) || !st.policy?.park || opts.silent) throw e;
      parkedBefore = true;
      record(frame, `${label(frame, id)}:stuck`);
      inst.event('stuck', label(frame, id), { error: e.errorName, park: st.policy.park });
      const control = await park(frame, id, st.policy.park);
      if (!control) throw e;
      once = control === 'retry';
    }
  }
  rec.attempts = attempts;
  inst.attempts[id] = (inst.attempts[id] ?? 0) + attempts;
  const outcome = result.outcome;
  // Apply the writes to the context, inside the declared paths only (R-03-07).
  const delta = applyWrites(frame, act, result.writes, id);
  rec.delta = delta;
  rec.outcome = outcome;
  rec.endedAt = run.clock.elapsed;
  inst.deltas.push({ step: label(frame, id), release: resolved.release, delta });
  if (result.decisions) Object.assign(inst.decisions, result.decisions);
  if (result.ruleTrace) inst.ruleTraces.push({ step: label(frame, id), result: result.ruleTrace });
  if (result.reasonCode) inst.reasonCode = result.reasonCode;
  inst.engineStates.push(inst.engineState());
  if (!opts.silent) record(frame, frame.inFinally ? `finally:${id}` : `${label(frame, id)}:${outcome}`);
  inst.event('outcome', label(frame, id), {
    outcome, attempts, reasonCode: result.reasonCode,
    // What the step decided and found, for the debugger (F-17).
    ...(run.cfg.debug ? {
      activity: act.name, release: resolved.release, delta,
      decisions: result.decisions, worst: result.ruleTrace?.worst, findings: result.ruleTrace?.findings,
      rulesFired: (result.ruleTrace?.trace ?? []).map((t: any) => t.rule ?? t.name).filter(Boolean),
    } : {}),
  });
  return { outcome, act };
}

function applyWrites(frame: Frame, act: any, writes: Record<string, any>, stepId: string): DeltaEntry[] {
  const delta: DeltaEntry[] = [];
  const declared: string[] = act.writes ?? [];
  for (const [p, v] of Object.entries(writes ?? {})) {
    if (!coveredByAny(declared, p.replace(/\[\d+\]/g, '[]'))) throw new StepFailure(stepId, `${act.name} wrote ${p}, outside its declared writes (R-03-07)`);
    const before = clone(getPath(frame.ctx, p));
    setPath(frame.ctx, p, v);
    delta.push({ path: frame.item ? `items[${frame.item.index}].${p}` : p, before, after: clone(v) });
  }
  if (frame.item) syncItem(frame);
  return delta;
}

function syncItem(frame: Frame) {
  const { item } = frame.item!;
  item.payment = frame.ctx.payment;
  item.ext = frame.ctx.ext;
  item.facts = frame.ctx.facts;
}

class Hang extends Error {}

/** A parked step waits for an operator's control, or for its park time to pass (null). */
function park(frame: Frame, id: string, duration: string): Promise<string | null> {
  const inst = frame.inst;
  const clock = inst.run.clock;
  return new Promise<string | null>((resolve, reject) => {
    let settled = false;
    const done = (v: string | null) => { if (settled) return; settled = true; off(); inst.parked = undefined; resolve(v); };
    const off = clock.at(addDuration(clock.now, duration), () => done(null), `park ${id}`);
    inst.parked = { step: label(frame, id), resolve: done };
    frame.scope.onCancel((r) => { if (settled) return; settled = true; off(); inst.parked = undefined; reject(new Cancelled(r)); });
  });
}

async function attemptLoop(act: any, st: any, policy: EffectivePolicy, frame: Frame, id: string, level?: string, once = false): Promise<{ result: ActivityResult; attempts: number }> {
  const inst = frame.inst;
  const clock = inst.run.clock;
  const startAt = clock.now;
  const total = policy.totalTimeout.value ? addDuration(startAt, policy.totalTimeout.value) : Infinity;
  const max = once ? 1 : Math.max(1, policy.maxAttempts.value);
  const callNo = (inst.callCounts.get(id) ?? 0) + 1;
  inst.callCounts.set(id, callNo);
  let lastError: any;
  for (let attempt = 1; attempt <= max; attempt++) {
    frame.scope.throwIfCancelled();
    const attemptScope = frame.scope.child();
    const attemptMs = policy.attemptTimeout.value ? durationMs(policy.attemptTimeout.value) : null;
    const limitAt = Math.min(attemptMs === null ? Infinity : clock.now + attemptMs, total);
    let timedOut = false;
    const offTimer = Number.isFinite(limitAt) ? clock.at(limitAt, () => { timedOut = true; attemptScope.cancel({ timeout: true }); }, `attempt timeout ${id}`) : () => {};
    inst.event('attempt', label(frame, id), { attempt, timeout: policy.attemptTimeout.value });
    try {
      const result = await invoke(act, st, frame, attemptScope, id, level, callNo, attempt);
      offTimer();
      attemptScope.dispose();
      return { result, attempts: attempt };
    } catch (e: any) {
      offTimer();
      attemptScope.dispose();
      if (e instanceof Cancelled && !timedOut) throw e; // the workflow was cancelled
      if (e instanceof EndFlow) throw e;
      const name = timedOut ? 'Timeout' : e instanceof TransientFailure ? e.errorName : e instanceof ScriptError ? e.errorName : e instanceof ActivityFailure ? e.errorName : e instanceof ContractViolation ? 'ContractViolation' : 'Error';
      lastError = e;
      const retryable = e instanceof ContractViolation ? false : e instanceof ActivityFailure ? e.retryable : isRetryable(policy, name, act.errors);
      inst.event('attempt-failed', label(frame, id), { attempt, error: name, retryable, late: timedOut ? 'a late result is ignored' : undefined });
      if (!retryable) {
        throw new StepFailure(id, `${act.name} failed: ${e?.message ?? name}`, name);
      }
      if (attempt >= max) break;
      const wait = retryInterval(policy, attempt);
      if (clock.now + wait >= total) break;
      inst.event('retry-wait', label(frame, id), { ms: wait });
      await clock.sleep(wait, frame.scope, `backoff ${id}`);
    }
  }
  // Retries are spent or the total timeout is reached: the unknown outcome, where declared.
  if ((act.outcomes ?? []).some((o: any) => o.name === 'unknown')) {
    inst.event('gave-up', label(frame, id), { outcome: 'unknown' });
    return { result: { outcome: 'unknown', writes: {} }, attempts: inst.run.clock.now >= 0 ? countAttempts(inst, frame, id) : 0 };
  }
  throw new StepFailure(id, `${act.name} failed after retries: ${lastError?.message ?? lastError}`, 'RetriesExhausted');
}

function countAttempts(inst: Instance, frame: Frame, id: string): number {
  const l = label(frame, id);
  let n = 0;
  for (let i = inst.timeline.length - 1; i >= 0; i--) {
    const e = inst.timeline[i];
    if (e.step === l && e.type === 'step-start') break;
    if (e.step === l && e.type === 'attempt') n++;
  }
  return n;
}

/** One attempt: a stub (kit), a script-behind, or the reference behaviour. */
export async function invoke(act: any, st: any, frame: Frame, scope: Scope, id: string, level: string | undefined, callNo: number, attempt: number): Promise<ActivityResult> {
  const inst = frame.inst;
  const run = inst.run;
  const view = viewFor(frame, act);
  const io = makeIO(act, frame, scope, id, view, level, callNo);
  const stub = run.cfg.stubs?.[`step:${id}`] ?? run.cfg.stubs?.[act.name];
  if (stub) return runStub(stub, act, frame, scope, callNo, attempt);
  // R-08-16: a run that gives this activity to the runtime calls the native host, not the script.
  const script = nativeHere(act) ? undefined : run.cfg.scripts.find(act.name, act.release);
  if (script) {
    const params: Record<string, any> = {};
    for (const [k, decl] of Object.entries<any>(act.parameters ?? {})) params[k] = run.lookupParam(act.name, k, decl, run.clock.today());
    const runner = script.language === 'python' ? runPythonScript : runScript;
    const res = await Promise.race([
      runner(script.source, script.file, view, act.reads ?? [], act.writes ?? [], {
        ports: scriptPorts(io, act),
        // `rules.money`: exact decimal helpers, the rule language's arithmetic (R-04-23).
        rules: { run: (rs: string, facts?: any) => io.rules(rs, level as any, facts), table: (t: string) => io.table(t), money: moneyHelpers },
        params,
        clock: { now: () => run.clock.iso(), today: () => run.clock.today() },
        ids: { next: (p = 'id') => `${p}-${inst.id}-${id}-${callNo}` },
        log: { info: () => {} },
        // One tenant's scripts run in workers of that tenant only (R-08-20).
      }, undefined, run.cfg.tenant ?? 'owner', run.clock.hold()),
      scope.never(),
    ]);
    // Leaf changes merge into the context; the item list is written whole.
    const writes: Record<string, any> = {};
    for (const c of res.changes) {
      if (c.after === undefined) continue;
      if (c.path.startsWith('items')) writes['items[]'] = getPath(res.ctx, 'items');
      // A list inside a fact (a posting's entries and legs) is written whole too, so it stays a list in the context.
      else if (c.path.includes('[')) { const list = c.path.slice(0, c.path.indexOf('[')); writes[list] = getPath(res.ctx, list); }
      else writes[c.path] = c.after;
    }
    if (!(act.outcomes ?? []).some((o: any) => o.name === res.outcome)) throw new ContractViolation('R-03-02', `${act.name} returned ${res.outcome}, which is not one of its outcomes`);
    return { outcome: res.outcome, reasonCode: res.reasonCode, writes, ruleTrace: (io as any).lastRuleTrace, decisions: (io as any).lastDecisions };
  }
  // A native code-behind runs in the runtime's native host (R-08-14), with the same view and parameters a script gets.
  // So does a builtin activity whose port the bench gives to the runtime (R-08-15): the runtime's code answers it
  // through the bank's port binding in force at the engine's time, not a simulator.
  if (act.implementation?.binding === 'native' || nativeHere(act) || (act.implementation?.binding === 'builtin' && (act.ports ?? []).some((p: string) => run.onRuntime(p)))) {
    const params: Record<string, any> = {};
    for (const [k, decl] of Object.entries<any>(act.parameters ?? {})) params[k] = run.lookupParam(act.name, k, decl, run.clock.today());
    const r = await run.clock.external(() => runNative(act, view, params, run.clock.iso(), run.cfg.tenant, level, inst.id));
    // The runtime opened the hold and answered at once (Issue I-98): the decision comes later, as the operator's message.
    if (r.held) return holdDecision(act, io);
    return r;
  }
  try {
    return await reference(act, view, io);
  } catch (e) {
    if (e instanceof Hang) return scope.never();
    throw e;
  }
}

/**
 * Whether this attempt goes to the runtime's native host though the activity has a script-behind or a builtin (R-08-16): the
 * activity says the runtime implements it (`implementation.native: true`), and the run gives it to the runtime
 * (`TH_NATIVE_ACTIVITIES` names it, or is `*`, and does not name it with `!`). Without a native host such an attempt fails, as R-08-14 says.
 */
export function nativeHere(act: any): boolean {
  if (act.implementation?.native !== true) return false;
  const list = (process.env.TH_NATIVE_ACTIVITIES ?? '').split(',').map((s) => s.trim()).filter(Boolean);
  // `!<name>` keeps an activity on the studio although `*` gives the rest to the runtime.
  if (list.includes(`!${act.name}`) || list.includes(`!${act.name}@${act.release}`)) return false;
  return list.includes('*') || list.includes(act.name) || list.includes(`${act.name}@${act.release}`);
}

/** One attempt of a native code-behind on the runtime's native host (R-08-14). */
/** `instance`: the run instance the attempt is for, so the host can keep a file it receives or sends with its leg (Issue I-97). */
export async function runNative(act: any, view: any, params: Record<string, any>, now: string, tenant?: string, level?: string, instance?: string): Promise<ActivityResult> {
  const host = process.env.TH_NATIVE_HOST;
  if (!host) throw new ActivityFailure('NativeHostUnavailable', `${act.name} is native code-behind: it runs in the runtime's native host. Set TH_NATIVE_HOST (for example http://127.0.0.1:7300).`);
  let res: any;
  // The native host's ports are the bank's: the call is a port call span, and carries the trace (R-09-33).
  const t0 = performance.now();
  const portAttrs = attrs({ 'th.port': 'native-host', 'th.activity': act.name });
  try {
    res = await inSpan(`th.port.call native-host`, portAttrs, async () => {
      // The native host takes the bank's machine callers only (R-08-17, R-08-21): the worker's own client-credentials token,
      // kept fresh, or a token by reference, and over mutual TLS where it is configured (native-auth.ts).
      // The attempt names its payment's distribution, as the engine sent it (R-09-130); else the worker's own (TH_DISTRIBUTION, set
      // by whoever starts it). The host serves the attempt from that distribution's own native assemblies, by activity and release
      // (R-09-122), and refuses it when the worker's distribution is another one (R-09-131). The studio's interpreter names none.
      const workerDistribution = process.env.TH_DISTRIBUTION || undefined;
      const distribution = attemptDistribution() ?? workerDistribution;
      const body = JSON.stringify({ activity: act.name, release: act.release, ...(distribution ? { distribution } : {}), ...(workerDistribution ? { workerDistribution } : {}), view, params, now, ...(tenant ? { tenant } : {}), ...(level ? { level } : {}), ...(instance ? { instance } : {}) });
      const call = async () => nativeFetch(`${host.replace(/\/$/, '')}/native`, { method: 'POST', headers: { 'content-type': 'application/json', ...traceHeaders(), ...(await nativeAuthHeaders()) }, body });
      let r = await call();
      // A token the host no longer takes is fetched again once.
      if (r.status === 401 && process.env.TH_NATIVE_TOKEN_URL) {
        forgetNativeToken();
        r = await call();
      }
      if (r.status === 401) return { kind: 'error', name: 'NotAuthenticated', retryable: false, message: 'The native host did not take the worker\'s credentials (R-08-17)' };
      return r.json();
    }, { kind: SpanKind.CLIENT });
  } catch (e: any) {
    meters().portErrors.add(1, { ...portAttrs, 'th.error': 'NativeHostUnreachable' });
    throw new TransientFailure('NativeHostUnreachable');
  } finally {
    meters().portCall.record((performance.now() - t0) / 1000, portAttrs);
  }
  if (res.kind === 'error') throw new ActivityFailure(res.name ?? 'NativeFailure', res.message ?? 'native failure', !!res.retryable);
  if (res.kind === 'held' && act.type === 'hold') return { outcome: '', writes: {}, held: { hold: res.hold, expiry: res.expiry } };
  if (!(act.outcomes ?? []).some((o: any) => o.name === res.outcome)) throw new ContractViolation('R-03-02', `${act.name} returned ${res.outcome}, which is not one of its outcomes`);
  const writes: Record<string, any> = {};
  for (const [k, v] of Object.entries(res.writes ?? {})) {
    if (!coveredByAny(act.writes ?? [], k.replace(/\[\d+\]/g, '[]'))) throw new ContractViolation('R-08-14', `${act.name} wrote ${k}, outside its declared writes`);
    writes[k] = v;
  }
  return { outcome: res.outcome, reasonCode: res.reasonCode ?? undefined, writes };
}

function scriptPorts(io: ActivityIO, act: any) {
  return {
    /** Send and take the reply on the same connection. */
    request: async (port: string) => toReply(await io.call(port, { expectsSyncReply: true, type: io.sentType() }), io),
    /** Send; any reply comes later through the front door. */
    send: async (port: string) => toReply(await io.call(port, { expectsSyncReply: false, type: io.sentType() }), io),
    declared: act.ports ?? [],
  };
}

async function toReply(r: SimResponse, io: ActivityIO) {
  if (r.kind === 'transient') throw new TransientFailure(r.error);
  if (r.kind === 'no-reply') return io.hang();
  if (r.kind === 'ack') return { status: 'accepted' };
  return { status: r.message.status, reasonCode: r.message.reasonCode, malformed: r.message.malformed, data: r.message.payload };
}

async function runStub(stub: StubSpec, act: any, frame: Frame, scope: Scope, callNo: number, attempt: number): Promise<ActivityResult> {
  const run = frame.inst.run;
  const clock = run.clock;
  if (stub.emit && attempt === 1) {
    const m: Message = { id: run.newId('party'), type: stub.emit.type, port: act.ports?.[0] ?? '', from: 'simulator', at: clock.now, status: stub.emit.status, correlation: correlationOf(frame.ctx), conversation: frame.ctx?.header?.conversation };
    clock.at(stub.emit.after ? addDuration(clock.now, stub.emit.after) : clock.now, () => run.frontDoor(m), `party sends ${m.type}`);
  }
  if (stub.latency) await clock.sleep(durationMs(stub.latency), scope);
  if (stub.error) throw new ActivityFailure(stub.error, `stub error ${stub.error}`, false);
  if (callNo === 1 && attempt <= (stub.hangTimes ?? 0)) return scope.never();
  if (callNo === 1 && attempt <= (stub.hangTimes ?? 0) + (stub.failTimes ?? 0)) throw new TransientFailure();
  const outs = stub.outcomes ?? [primaryOutcome(act)];
  const outcome = outs[Math.min(callNo - 1, outs.length - 1)];
  const writes: Record<string, any> = {};
  for (const [p, v] of Object.entries(stub.writes ?? {})) writes[p] = v;
  if (frame.inst.run.cfg.stubs && (act.ports ?? []).length && (act.type === 'exchange' || act.effect === 'external')) {
    frame.inst.run.messages.push({ id: `stub-${frame.inst.id}-${callNo}`, type: act.name, port: act.ports[0], from: 'platform', at: clock.now, correlation: correlationOf(frame.ctx), key: act.idempotencyKey ? safeEval(act.idempotencyKey, frame.ctx) : undefined, status: outcome });
  }
  return { outcome, writes, decisions: stub.decisions, reasonCode: outcome !== primaryOutcome(act) ? stub.reasonCode : undefined };
}

/** The activity's view of the context: its declared reads, with message payloads resolved for maps. */
function viewFor(frame: Frame, act: any): any {
  const view = pick(frame.ctx, act.reads ?? []);
  if (act.type === 'map' && (act.reads ?? []).includes('messages')) {
    const run = frame.inst.run;
    const resolve = (ref: any) => {
      if (!ref?.id) return ref;
      const m = run.messages.find((x) => x.id === ref.id);
      // The scheme's status, reason and family go with it, so a native code-behind reads a reply as the reference does (R-08-16).
      return m ? { ...ref, payload: m.payload, malformed: m.malformed, ...(m.status ? { status: m.status } : {}), ...(m.reasonCode ? { reasonCode: m.reasonCode } : {}), ...((m as any).family ? { family: (m as any).family } : {}) } : ref;
    };
    if (view.messages?.inbound) view.messages.inbound = resolve(view.messages.inbound);
    if (view.messages?.lastReceived) view.messages.lastReceived = resolve(view.messages.lastReceived);
    if (view.messages?.received) for (const k of Object.keys(view.messages.received)) view.messages.received[k] = resolve(view.messages.received[k]);
  }
  return view;
}

function correlationOf(ctx: any) {
  const h = ctx?.header ?? {};
  return { paymentId: h.paymentId, endToEndId: h.endToEndId, fileId: h.fileId, uetr: h.uetr, conversation: h.conversation };
}

function safeEval(expr: string, ctx: any): string | undefined {
  try {
    return String(evalExpr(expr, ctx));
  } catch {
    return undefined;
  }
}

function makeIO(act: any, frame: Frame, scope: Scope, stepId: string, view: any, level: string | undefined, callNo: number): ActivityIO {
  const inst = frame.inst;
  const run = inst.run;
  const sentType = () => {
    for (const s of run.cfg.store.byKind('scheme').map((e) => e.doc)) {
      const m = (s.messages ?? []).find((x: any) => x.disposition?.sentBy === act.name);
      if (m) return m.id as string;
    }
    return undefined;
  };
  const io: any = {
    sentType,
    call: async (port: string, o: { expectsSyncReply: boolean; type?: string }) => {
      if (!(act.ports ?? []).includes(port)) throw new ContractViolation('R-03-06', `${act.name} reached port ${port}, which it does not declare`);
      const key = act.idempotencyKey ? safeEval(act.idempotencyKey, frame.ctx) : undefined;
      const msg: Message = {
        id: `${key ?? `${act.name}:${inst.id}`}#${callNo}`,
        type: o.type ?? '',
        port,
        from: 'platform',
        at: run.clock.now,
        correlation: correlationOf(frame.ctx),
        conversation: frame.ctx?.header?.conversation,
        key,
      };
      run.messages.push(msg);
      inst.event('message-out', label(frame, stepId), { port, type: msg.type, id: msg.id });
      const sim = run.simFor(port);
      // A port call is a span of its own and a latency (R-09-33), made and ended here with no await, so the
      // interpreter's order of events is what it was.
      const t0 = performance.now();
      const portAttrs = attrs({ 'th.port': port, 'th.activity': act.name });
      const span = tracer().startSpan(`th.port.call ${port}`, { kind: SpanKind.CLIENT, attributes: attrs({ ...portAttrs, 'th.message.type': msg.type, 'th.payment.id': frame.ctx?.header?.paymentId }) });
      let res: SimResponse;
      try {
        res = sim.receive(msg, o.expectsSyncReply);
      } catch (e: any) {
        span.setStatus({ code: SpanStatusCode.ERROR, message: e?.name ?? 'Error' });
        span.end();
        meters().portErrors.add(1, { ...portAttrs, 'th.error': e?.name ?? 'Error' });
        throw e;
      }
      const result = res.kind === 'reply' ? res.message.status ?? 'reply' : res.kind;
      span.setAttribute('th.port.result', result);
      if (res.kind === 'transient' || res.kind === 'no-reply') {
        span.setStatus({ code: SpanStatusCode.ERROR, message: res.kind });
        meters().portErrors.add(1, { ...portAttrs, 'th.error': res.kind });
      }
      span.end();
      meters().portCall.record((performance.now() - t0) / 1000, { ...portAttrs, 'th.port.result': result });
      if (res.kind === 'reply') {
        run.messages.push(res.message);
        inst.event('reply', label(frame, stepId), { status: res.message.status, reasonCode: res.message.reasonCode });
      } else inst.event(res.kind, label(frame, stepId), {});
      return res;
    },
    waitFor: (type: string, timeoutMs: number | null) =>
      new Promise<Message | null>((resolve, reject) => {
        const early = run.takeEarly(inst, type);
        if (early) {
          inst.consumed.add(early.id);
          return resolve(early);
        }
        let off = () => {};
        const w = { type, resolve: (m: Message) => { off(); resolve(m); } };
        inst.waiters.push(w);
        const kill = timeoutMs !== null ? run.clock.at(run.clock.now + timeoutMs, () => { inst.waiters = inst.waiters.filter((x) => x !== w); off(); resolve(null); }) : () => {};
        const off0 = scope.onCancel((r) => { kill(); inst.waiters = inst.waiters.filter((x) => x !== w); reject(new Cancelled(r)); });
        off = () => { kill(); off0(); };
      }),
    rules: (rs: string, lvl?: string, given?: any) => {
      const { doc } = run.resolveFor(inst, rs, stepId);
      const facts = given ?? buildFacts({ ctx: view, reads: act.reads ?? [], level: (lvl ?? level) as any, extensions: run.extensions() });
      const res = runRuleSet(doc.name, facts, {
        resolve: (n) => run.resolveFor(inst, n, stepId).doc,
        lookup: (owner, p, decl, date) => run.lookupParam(owner, p, decl, date),
        evaluationDate: run.clock.today(),
        businessDate: frame.ctx?.header?.businessDate,
      });
      io.lastRuleTrace = res;
      return res;
    },
    table: (t: string) => {
      const { doc } = run.resolveFor(inst, t, stepId);
      const facts = buildFacts({ ctx: view, reads: act.reads ?? [], extensions: run.extensions() });
      const res = runTable(doc, facts, {
        resolve: (n) => run.resolveDoc(n),
        lookup: (owner, p, decl, date) => run.lookupParam(owner, p, decl, date),
        evaluationDate: run.clock.today(),
      });
      io.lastDecisions = Object.fromEntries(res.decisions.map((d) => [d.name, d.value]));
      return res;
    },
    hang: () => scope.never(),
    now: () => run.clock.iso(),
    today: () => run.clock.today(),
    param: (name: string) => run.lookupParam(act.name, name, act.parameters?.[name], run.clock.today()),
    nowMs: () => run.clock.now,
    calendar: () => run.businessCalendar(run.clock.today()),
    received: () => {
      const refs = frame.ctx?.messages ?? {};
      const last = refs.lastReceived ?? refs.inbound;
      return last ? run.messages.find((m) => m.id === last.id) : undefined;
    },
  };
  return io as ActivityIO;
}

// ---------------------------------------------------------------------- switch

async function execSwitch(st: any, frame: Frame) {
  const sw = st.switch;
  let value: any;
  if (sw.by?.outcomeOf) value = frame.inst.outcomes.get(sw.by.outcomeOf);
  else if (sw.by?.decision) value = frame.inst.decisions[sw.by.decision];
  const key = value === undefined || value === null ? undefined : String(value);
  const chosen = key !== undefined && sw.cases && key in sw.cases ? key : sw.default ? 'default' : undefined;
  const steps = key !== undefined && sw.cases && key in sw.cases ? sw.cases[key] : sw.default;
  if (frame.inst.run.cfg.debug) frame.inst.event('branch', st.id, { by: sw.by, value: value ?? null, case: chosen ?? 'none' });
  if (steps) await execSteps(steps, frame);
}

// -------------------------------------------------------------------- parallel

async function execParallel(st: any, frame: Frame) {
  const ps = frame.scope.child();
  let firstError: any;
  await Promise.all(
    (st.parallel.branches ?? []).map(async (branch: any[]) => {
      try {
        await execSteps(branch, { ...frame, scope: ps.child() });
      } catch (e) {
        if (firstError === undefined && !(e instanceof Cancelled && ps.isCancelled && firstError !== undefined)) {
          if (!(e instanceof Cancelled) || !ps.isCancelled) {
            firstError = e;
            ps.cancel({ sibling: true });
          } else if (firstError === undefined && frame.scope.isCancelled) {
            firstError = e;
          }
        }
      }
    }),
  );
  ps.dispose();
  if (firstError !== undefined) throw firstError;
  frame.scope.throwIfCancelled();
  record(frame, `join:${st.parallel.join ?? 'all'}`);
  frame.inst.event('join', undefined, { join: st.parallel.join ?? 'all' });
}

// --------------------------------------------------------------------- forEach

async function execForEach(st: any, frame: Frame) {
  const fe = st.forEach;
  const items: any[] = getPath(frame.ctx, fe.in) ?? [];
  const concurrency = Math.max(1, fe.concurrency ?? 1);
  const onFail = fe.onItemFailure ?? 'stop';
  frame.inst.event('for-each', undefined, { items: items.length, concurrency, onItemFailure: onFail });
  let next = 0;
  let stopError: any;
  // Items side by side (R-05-53): each records into its own sink, and the sinks go into the trace in item order, as far as every
  // earlier item has ended. The first item that stops the forEach is the last whose entries are kept, as item by item.
  const ordered = concurrency > 1 && items.length > 1;
  const sinks: (ItemSink | undefined)[] = [];
  const ended: boolean[] = [];
  let failedAt = Infinity;
  let firstFailure: any;
  let flushed = 0;
  const flush = (all: boolean) => {
    const to = frame.sink ?? frame.inst;
    while (flushed < items.length && flushed <= failedAt && (all || ended[flushed])) {
      const s = sinks[flushed];
      if (s) {
        to.trace.push(...s.trace);
        to.detail.push(...s.detail);
        to.releaseTrace.push(...s.releaseTrace);
        sinks[flushed] = undefined;
      }
      flushed++;
    }
  };
  const worker = async () => {
    while (next < items.length && stopError === undefined) {
      const index = next++;
      const item = items[index];
      // A call with level item binds payment to the item's payment (R-05-28).
      const itemCtx = { ...frame.ctx, payment: item.payment ?? {}, ext: item.ext ?? {}, facts: item.facts ?? {}, items: [item] };
      const sink = ordered ? (sinks[index] = { trace: [], detail: [], releaseTrace: [] }) : undefined;
      const iframe: Frame = { ...frame, ctx: itemCtx, item: { index, item, parent: frame.ctx }, ...(sink ? { sink } : {}) };
      try {
        await execSteps(fe.do ?? [], iframe);
      } catch (e) {
        if (e instanceof EndFlow && onFail === 'continue') {
          frame.inst.event('item-failed', `item[${index}]`, { outcome: e.outcome });
        } else {
          if (stopError === undefined) stopError = e;
          if (index < failedAt) { failedAt = index; firstFailure = e; }
        }
      }
      if (ordered) { ended[index] = true; flush(false); }
    }
  };
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length || 1) }, worker));
  if (ordered) {
    flush(true);
    if (firstFailure !== undefined) throw firstFailure;
  }
  if (stopError !== undefined) throw stopError;
}

// ------------------------------------------------------------------------ loop

async function execLoop(st: any, frame: Frame) {
  const lp = st.loop;
  const max = lp.max ?? 1;
  for (let i = 0; i < max; i++) {
    await execSteps(lp.do ?? [], frame);
    if (lp.until?.outcomeOf) {
      const o = frame.inst.outcomes.get(lp.until.outcomeOf);
      if (o !== undefined && (lp.until.in ?? []).includes(o)) return;
    }
    if (i < max - 1 && lp.every) await frame.inst.run.clock.sleep(durationMs(lp.every), frame.scope, 'loop every');
  }
}

// ---------------------------------------------------------------- await, wait

async function execAwait(st: any, frame: Frame) {
  const inst = frame.inst;
  const run = inst.run;
  const id = st.id ?? 'await';
  // A wait the bank may set (R-05-47): the composite's parameter, as in force on the day the await starts; the timeout is its default.
  const dated = (ref: string | undefined, fallback: any) => {
    if (!ref) return fallback;
    const [owner, param] = ref.split('#');
    return run.lookupParam(owner, param, { default: fallback }, run.clock.today()) ?? fallback;
  };
  const aw = { ...st.await, calendar: dated(st.await.calendarParameter, st.await.calendar) };
  // R-05-45: a timeout may itself name a parameter; R-05-47: a timeout from the policy may have a bank's parameter.
  const timeout = dated(st.await.timeoutParameter, durationOf(st.await.timeout, frame));
  inst.event('await-start', id, { message: aw.message, family: aw.family, timeout, calendar: aw.calendar });
  // R-05-43: once an await of this instance has waited for this message, a later await takes only a message
  // received after the step that holds it began; an earlier one answered the earlier exchange.
  const since = inst.awaitedKeys.has(aw.message) ? frame.blockStartedAt ?? run.clock.now : Number.NEGATIVE_INFINITY;
  inst.awaitedKeys.add(aw.message);
  const early = run.takeEarly(inst, aw.message, aw.family, since);
  let msg: Message | null = null;
  if (early) {
    inst.consumed.add(early.inReplyTo ? `${early.type}:${early.inReplyTo}:${early.status ?? ''}` : early.id);
    msg = early;
  } else {
    msg = await new Promise<Message | null>((resolve, reject) => {
      let kill = () => {};
      const off = frame.scope.onCancel((r) => {
        kill();
        inst.awaiting = undefined;
        reject(new Cancelled(r));
      });
      inst.awaiting = { type: aw.message, family: aw.family, stepId: id, resolve: (m) => { kill(); off(); resolve(m); } };
      if (timeout) {
        const at = addDuration(run.clock.now, timeout, aw.calendar === 'business' ? DEFAULT_CALENDAR : undefined);
        kill = run.clock.at(at, () => {
          off();
          inst.awaiting = undefined;
          inst.expiredAwaits.add(aw.message);
          resolve(null);
        }, `await timeout ${id}`);
      }
    });
  }
  if (!msg) {
    record(frame, `${label(frame, id)}:timeout`);
    inst.event('await-timeout', id, {});
    if (aw.onTimeout) await runAction(aw.onTimeout, frame);
    return;
  }
  record(frame, `${label(frame, id)}:received`);
  inst.event('await-received', id, { type: msg.type, status: msg.status });
  frame.ctx.messages ??= {};
  frame.ctx.messages.received ??= {};
  const ref = { id: msg.id, type: msg.type };
  frame.ctx.messages.received[msg.type] = ref;
  frame.ctx.messages.lastReceived = ref;
}

/**
 * A duration as a step names it: a literal, or a parameter of the composite whose subflow holds the step, looked up
 * for the tenant on the business date the step starts, else its default (R-05-45).
 */
function durationOf(v: any, frame: Frame): string | undefined {
  if (!v || typeof v !== 'object') return v;
  const run = frame.inst.run;
  const [owner, param] = String(v.parameter).split('#');
  if (!param) throw new StepFailure('wait', `The parameter ${v.parameter} is not named in full; run the expanded form`);
  return run.lookupParam(owner, param, { default: v.default ?? run.resolveDoc(owner)?.parameters?.[param]?.default }, run.clock.today());
}

async function execWait(st: any, frame: Frame) {
  const d = durationOf(st.wait, frame);
  if (!isDuration(d)) throw new StepFailure(st.id ?? 'wait', `wait needs a duration, not ${d}`);
  frame.inst.event('wait', st.id, { duration: d });
  await frame.inst.run.clock.sleepUntil(addDuration(frame.inst.run.clock.now, d), frame.scope, 'wait');
  record(frame, `${st.id ?? 'wait'}:${d}`);
}

// ------------------------------------------------------------------------ slot

async function execSlot(st: any, frame: Frame) {
  const inst = frame.inst;
  const run = inst.run;
  const doc = inst.doc;
  const id = st.id ?? st.slot;
  const slot = (doc.slots ?? []).find((s: any) => s.name === st.slot);
  if (!slot) throw new StepFailure(id, `Slot ${st.slot} is not declared`);
  const legName = chooseLeg(run, inst, st.slot, frame.ctx);
  if (!legName) throw new StepFailure(id, `No leg fills slot ${st.slot} for this payment`);
  // A leg is pinned when it starts (R-01-16).
  const legDoc = run.resolveDoc(legName);
  if (!legDoc) throw new StepFailure(id, `Leg ${legName} does not resolve`);
  inst.releaseTrace.push(`leg@${legDoc.release}`);
  inst.event('slot', id, { leg: legName, release: legDoc.release });
  const childCtx = clone(frame.ctx);
  const child = await startInstance(run, legDoc, childCtx, 'leg', inst, { scope: frame.scope });
  frame.scope.throwIfCancelled();
  // The slot contract says what the leg may write back.
  for (const w of slot.contract?.writes ?? []) {
    const v = getPath(childCtx, w);
    if (v !== undefined) setPath(frame.ctx, w, v);
  }
  if (child.reasonCode) inst.reasonCode = child.reasonCode;
  const outcome = child.outcome ?? 'failed';
  inst.outcomes.set(id, outcome);
  record(frame, `${label(frame, id)}:${outcome}`);
  const primary = slot.contract?.primaryOutcome ?? slot.contract?.outcomes?.[0];
  if (outcome === primary) return;
  const action = st.handle?.[outcome];
  if (action === undefined) throw new StepFailure(id, `Leg outcome ${outcome} is not handled (L05)`);
  await runAction(action, frame);
}

function chooseLeg(run: Run, inst: Instance, slot: string, ctx: any): string | undefined {
  if (inst.legs[slot]) return inst.legs[slot];
  // The route decision names the rail; the rail's scheme names the leg for this slot.
  const rail = ctx?.facts?.route?.rail ?? ctx?.header?.scheme;
  const scheme = rail ? run.cfg.store.scheme(rail) ?? Object.values(run.docs).find((d: any) => d?.kind === 'scheme' && d.scope === rail) : undefined;
  for (const l of scheme?.legs ?? []) {
    const d = run.resolveDoc(l);
    if (d?.implements?.slot === slot && (d.implements.of ?? []).includes(inst.doc.name)) return l;
  }
  // Kit content: a leg in the run's documents that implements the slot of this workflow.
  for (const d of Object.values<any>(run.docs)) {
    if (d?.kind === 'workflow' && d.wfKind === 'leg' && d.implements?.slot === slot && (d.implements.of ?? []).includes(inst.doc.name)) return d.name;
  }
  return undefined;
}

// ------------------------------------------------------------------------- use

async function execUse(st: any, frame: Frame) {
  const seg = frame.inst.run.resolveFor(frame.inst, st.use, st.use).doc;
  if (!seg || seg.wfKind !== 'segment') throw new StepFailure(st.use, `${st.use} is not a segment`);
  // Segments are inlined: their steps run in this frame; their hook points belong to the segment.
  await execSteps(seg.do ?? [], { ...frame, hookOwner: seg.name });
}

// ------------------------------------------------------------------------- try

async function execTry(st: any, frame: Frame) {
  const tf: TryFrame = { writes: [] };
  const tframe: Frame = { ...frame, tryStack: [...frame.tryStack, tf] };
  try {
    await execSteps(st.try, tframe);
  } catch (e) {
    if (e instanceof EndFlow || e instanceof Cancelled) throw e;
    if (st.compensate === 'all') await compensate(tf.writes, frame);
    if (st.catch) {
      frame.inst.event('catch', undefined, { error: String((e as any)?.message ?? e) });
      await execSteps(st.catch, frame);
      return;
    }
    throw e;
  }
}

// ------------------------------------------------------------------------ hook

async function execHook(st: any, frame: Frame) {
  const inst = frame.inst;
  const run = inst.run;
  const name = st.hook;
  const ownerDoc = run.resolveDoc(frame.hookOwner) ?? inst.doc;
  const point = (ownerDoc.hookPoints ?? []).find((h: any) => h.name === name) ?? (inst.doc.hookPoints ?? []).find((h: any) => h.name === name);
  if (!point) throw new StepFailure(name, `Hook point ${name} is not declared (L06)`);
  const key = `${frame.hookOwner}#${name}`;
  const binding = inst.bindings.find((b: any) => b.hookPoint === key);
  if (!binding) {
    inst.event('hook-unbound', name, { hookPoint: key });
    return; // An unbound hook point does nothing and costs nothing (R-06-11).
  }
  const hook = run.resolveDoc(binding.hook, binding.release);
  if (!hook) throw new StepFailure(name, `Hook ${binding.hook}@${binding.release} does not resolve`);
  (frame.sink ?? inst).releaseTrace.push(`hook@${hook.release}`);
  const policy = point.policy ?? {};
  const hookTrace = (frame.sink ?? inst).trace;
  const traceAt = hookTrace.length;
  hookTrace.push(`hook:pending`);
  const runIt = async (): Promise<{ outcome: string; failed?: string }> => {
    const hctx = clone(frame.ctx);
    const scope = new Scope();
    let timedOut = false;
    const off = policy.timeout ? run.clock.at(addDuration(run.clock.now, policy.timeout), () => { timedOut = true; scope.cancel({ timeout: true }); }) : () => {};
    try {
      const child = await startInstance(run, { ...hook, exit: { outcomes: hook.outcomes } }, hctx, 'hook', inst, { scope });
      off();
      if (timedOut) return { outcome: 'failed', failed: 'timeout' };
      if (child.failed) return { outcome: 'failed', failed: child.failed };
      // A hook reads and writes only what the hook point's contract allows (R-06-07).
      const allowed: string[] = [...(point.contract?.writes ?? [])];
      const changes = diffPaths(frame.ctx, hctx);
      // A hook's blocks may derive facts, as check L13 allows; they never reach the payment unless the
      // hook point's contract lets a transform copy them back.
      const outside = changes.filter((p) => !coveredByAny(allowed, p) && !p.startsWith('facts.'));
      if (outside.length) return { outcome: 'failed', failed: `wrote outside its contract: ${outside.join(', ')}` };
      if (point.type === 'transform') for (const w of allowed) {
        const v = getPath(hctx, w);
        if (v !== undefined) setPath(frame.ctx, w, v);
      }
      return { outcome: child.outcome ?? 'done' };
    } catch (e: any) {
      off();
      return { outcome: 'failed', failed: String(e?.message ?? e) };
    }
  };
  if (point.type === 'notify') {
    // A notify hook must not delay or change the flow (R-06-09).
    void runIt().then((r) => {
      hookTrace[traceAt] = `hook:${r.outcome === 'failed' ? 'failed' : r.outcome}`;
      inst.event('hook', name, { hook: hook.name, type: 'notify', ...r });
    });
    return;
  }
  const r = await runIt();
  hookTrace[traceAt] = `hook:${r.outcome}`;
  inst.event('hook', name, { hook: hook.name, type: point.type, ...r });
  let outcome = r.outcome;
  if (outcome === 'failed') outcome = policy.onFailure === 'fail-closed' ? 'hold' : 'continue';
  if (point.type === 'transform') {
    if (outcome === 'hold') await holdFor(point, frame);
    return;
  }
  if (outcome === 'hold') return holdFor(point, frame);
  if (outcome === 'reject') {
    await compensateScope(frame);
    throw new EndFlow('rejected');
  }
}

async function holdFor(point: any, frame: Frame) {
  const holdBlock = point.policy?.onHold ?? 'p.hold.approval.v1';
  const { outcome, act } = await runActivityStep({ id: 'hold', call: holdBlock }, frame);
  if (outcome === primaryOutcome(act)) return;
  await compensateScope(frame);
  throw new EndFlow(outcome === 'expired' ? 'review' : 'rejected');
}

function diffPaths(a: any, b: any, prefix = ''): string[] {
  const out: string[] = [];
  const keys = new Set([...Object.keys(a ?? {}), ...Object.keys(b ?? {})]);
  for (const k of keys) {
    const p = prefix ? `${prefix}.${k}` : k;
    const x = a?.[k];
    const y = b?.[k];
    if (JSON.stringify(x) === JSON.stringify(y)) continue;
    if (x && y && typeof x === 'object' && typeof y === 'object' && !Array.isArray(x)) out.push(...diffPaths(x, y, p));
    else out.push(p);
  }
  return out;
}

// ================================================================ entry points

export interface RunResult {
  run: Run;
  root?: Instance;
  status: 'done' | 'idle' | 'limit';
}

/** Run a workflow (platform, leg or handler) from a given context. */
export async function runWorkflow(cfg: RunConfig, name: string, ctx: any, opts: { legs?: Record<string, string> } = {}): Promise<RunResult> {
  const run = new Run(cfg);
  attachIntake(run);
  const doc = run.resolveDoc(name);
  if (!doc) throw new Error(`${name} does not resolve`);
  let root: Instance | undefined;
  for (const [, sim] of run.sims) if (sim.sim.direction === 'inward') sim.start(ctx?.header?.conversation ?? 'conv-1');
  const p = startInstance(run, doc, ctx, doc.wfKind === 'handler' ? 'handler' : doc.wfKind ?? 'workflow', undefined, opts).then((i) => (root = i));
  // Run on after the workflow ends, so that late messages reach the front door and are recorded.
  const status = await run.drive(() => root !== undefined && run.instances.every((i) => i.ended) && run.clock.pending() === 0);
  await settleBackground(run);
  void p;
  return { run, root: root ?? run.instances[0] ?? run.handlerRuns[0], status };
}

/** Run one activity alone (an activity test), with its policy. */
export async function runActivity(cfg: RunConfig, name: string, ctx: any): Promise<RunResult> {
  const run = new Run(cfg);
  const act = run.resolveDoc(name);
  if (!act) throw new Error(`${name} does not resolve`);
  if (isComposite(act)) return runComposite(run, act, ctx);
  const stepId = act.type; // an activity test names the step by the activity's type
  const wf = { kind: 'workflow', wfKind: 'activity', name: `${name}#test`, release: act.release, exit: { outcomes: (act.outcomes ?? []).map((o: any) => o.name) }, do: [{ id: stepId, call: name, handle: Object.fromEntries((act.outcomes ?? []).filter((o: any) => !o.primary).map((o: any) => [o.name, { end: o.name }])) }] };
  let root: Instance | undefined;
  void startInstance(run, wf, ctx, 'activity').then((i) => (root = i));
  const status = await run.drive(() => root !== undefined);
  return { run, root, status };
}

/** Deliver inbound messages from the bench's inward simulators and run what they start. */
export async function runInbound(cfg: RunConfig, opts: { stopAfterFirst?: boolean } = {}): Promise<RunResult> {
  const run = new Run(cfg);
  attachIntake(run);
  let conv = 0;
  for (const [, sim] of run.sims) if (sim.sim.direction === 'inward') sim.start(`conv-${++conv}`);
  // A file from the bank's customer (an employer's salary file) reaches the front door directly.
  if (cfg.inbound?.message) {
    if (!run.catalogueEntry(cfg.inbound.message)?.disposition?.start) throw new Error(`${cfg.inbound.message} is not a start message in any dispatch table (R-07-14)`);
    const m: Message = { id: run.newId('in'), type: cfg.inbound.message, port: '', from: 'simulator', at: run.clock.now, correlation: {}, payload: cfg.inbound.payload, conversation: `conv-${++conv}`, ...(cfg.inbound.malformed ? { malformed: true } : {}) } as Message;
    run.clock.at(run.clock.now, () => run.frontDoor(m), `channel sends ${m.type}`);
  }
  const status = await run.drive(() => {
    const roots = [...run.instances, ...run.handlerRuns];
    return roots.length > 0 && roots.every((i) => i.ended) && run.clock.pending() === 0;
  });
  await settleBackground(run);
  void opts;
  return { run, root: run.instances[0] ?? run.handlerRuns[0], status };
}

async function settleBackground(run: Run) {
  for (let i = 0; i < 3; i++) await new Promise((r) => setImmediate(r));
}

/**
 * The intake at the front door: a `start` message runs its intake map, the workflow-select
 * decision picks the platform workflow, and the leg named in the dispatch table fills the slot.
 * A `handler` message runs its handler with no durable state.
 */
/** What a start entry of a dispatch table says about the payment it starts (R-05-42). */
export function routeOf(start: any): Record<string, string> {
  if (!start) return {};
  return Object.fromEntries(Object.entries({ kind: start.kind, direction: start.direction, segment: start.segment }).filter(([, v]) => v !== undefined)) as Record<string, string>;
}

export function attachIntake(run: Run) {
  run.onStart = (m, entry) => {
    void (async () => {
      const disp = entry.disposition;
      const scope = entry.id.split('.').slice(0, 2).join('.');
      const ctx: any = {
        header: { conversation: m.conversation, scheme: scope, direction: entry.direction === 'in' ? 'inward' : 'outward', receivedAt: run.clock.iso(), ...routeOf(disp.start) },
        messages: { inbound: { id: m.id, type: m.type } },
      };
      if (disp.handler) {
        const doc = run.resolveDoc(disp.handler);
        await startInstance(run, doc, ctx, 'handler');
        return;
      }
      // Intake map, run as the first step of an intake instance.
      if (disp.start.intakeMap) {
        const intake = { kind: 'workflow', wfKind: 'intake', name: `${disp.start.intakeMap}#intake`, release: '0.0.0', exit: { outcomes: ['mapped', 'unmappable'] }, do: [{ id: 'intake', call: disp.start.intakeMap, handle: { unmappable: { end: 'unmappable' } } }] };
        const it = await startInstance(run, intake, ctx, 'intake');
        run.selfCheck(m, ctx);
        if (it.outcome !== 'mapped') return;
      }
      // The dispatch table routes, not the intake map (R-05-42): its values stand after the map.
      Object.assign(ctx.header, routeOf(disp.start), { scheme: scope });
      ctx.header.paymentId ??= run.newId('P');
      // Which platform workflow: the workflow-select decision (p.decide.workflow.v1, invoked by the front door).
      const sel = run.resolveDoc('p.table.workflow-select.v1');
      let wfName: string | undefined;
      if (sel) {
        const res = runTable(sel, { Header: [ctx.header] }, { resolve: (n) => run.resolveDoc(n), evaluationDate: run.clock.today() });
        wfName = res.decisions.find((d) => d.name === 'workflow')?.value;
      }
      if (!wfName) return;
      const doc = run.resolveDoc(wfName);
      const legs: Record<string, string> = { 'scheme-leg': disp.start.leg };
      if (disp.start.refuseLeg) legs['scheme-refuse'] = disp.start.refuseLeg;
      await startInstance(run, doc, ctx, 'platform', undefined, { legs });
    })();
  };
}


/**
 * A composite tested alone (R-03-23): its subflow runs with its own step ids, the inner steps go
 * to the detail, and the outcome is what the subflow returns.
 */
export async function runComposite(run: Run, act: any, ctx: any): Promise<RunResult> {
  const sub = run.resolveDoc(act.implementation.subflow);
  if (!sub) throw new Error(`The subflow ${act.implementation.subflow} does not resolve`);
  const doc = { ...expandSubflow(sub, (n) => run.resolveDoc(n), act), exit: { outcomes: (act.outcomes ?? []).map((o: any) => o.name) } };
  let root: Instance | undefined;
  void startInstance(run, doc, ctx, 'subflow').then((i) => (root = i));
  const status = await run.drive(() => root !== undefined && run.clock.pending() === 0);
  return { run, root, status };
}
