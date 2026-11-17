import { clone } from '../core/canonical.js';
import { durationMs } from '../core/duration.js';
import { getPath, setPath, coveredByAny } from '../core/paths.js';
import { Cancelled, Scope } from '../interp/clock.js';
import { Instance, invoke, Run, type Frame, type RunConfig } from '../interp/interpreter.js';
import { TransientFailure, ActivityFailure } from '../interp/reference.js';
import { ContractViolation, ScriptError } from '../interp/sandbox.js';
import type { Message } from '../sim/runtime.js';
import type { StudioEnv } from '../env.js';
import type { BuiltScenario } from './kit.js';

// The party service: everything outside the orchestration, for an engine harness (C20 to C23).
// An engine runs the expanded workflow; for each activity attempt it asks this service, which runs
// the scenario's stubs, scripts-behind or reference behaviour against the scenario's simulators.
// Time is the engine's: each request carries the engine's `now`, and a simulator's later reply is
// handed back as a delivery due at an engine time, which the harness turns into a timer and then a
// signal (an external event, a message) to the workflow. The service never decides flow.

export interface AttemptRequest {
  instance: string; // the engine's instance id, for conversation state
  activity: string; // name
  release?: string;
  step: string; // the step id as written in the expanded form
  call: number; // the how-many-th execution of this step in the instance, from 1
  attempt: number; // from 1
  now: number; // engine time, epoch ms
  ctx: any; // the instance's context
  level?: 'item';
  policy?: any;
  messages?: { id: string; type: string; payload?: any }[];
}

export type AttemptResponse =
  | { kind: 'result'; outcome: string; reasonCode?: string; writes: Record<string, any>; decisions?: Record<string, any>; ruleTrace?: any; latencyMs?: number }
  | { kind: 'hang' } // the attempt never answers; the engine's attempt timeout ends it
  | { kind: 'error'; name: string; retryable: boolean; message: string };

export interface Delivery { at: number; message: Pick<Message, 'id' | 'type' | 'status' | 'reasonCode' | 'correlation' | 'conversation' | 'inReplyTo' | 'copy'> }

export class PartyService {
  readonly run: Run;
  private instances = new Map<string, Instance>();
  private due: Delivery[] = [];
  private latency: Record<string, number> = {};

  /** A party service for a kit scenario. */
  static forScenario(env: StudioEnv, built: BuiltScenario): PartyService {
    return new PartyService({ ...built.config, store: env.store, scripts: env.scripts, documents: built.documents } as RunConfig);
  }

  /** A party service over any run configuration: a deployed worker builds one from its environment. */
  constructor(base: RunConfig) {
    // A stub's latency is the engine's to spend on its own clock: the service answers at once and
    // reports how long the attempt takes (latencyMs).
    this.latency = Object.fromEntries(Object.entries<any>(base.stubs ?? {}).filter(([, v]) => v.latency).map(([k, v]) => [k, durationMs(v.latency)]));
    const stubs = Object.fromEntries(Object.entries<any>(base.stubs ?? {}).map(([k, v]) => [k, { ...v, latency: undefined }]));
    const cfg: RunConfig = { ...base, stubs };
    this.run = new Run(cfg);
    // A simulator's later reply is not run on the studio clock: it is handed to the engine.
    this.run.clock.at = (t: number, fn: () => void, label?: string) => {
      const before = this.run.messages.length;
      const captured: Message[] = [];
      const orig = this.run.frontDoor.bind(this.run);
      (this.run as any).frontDoor = (m: Message) => captured.push(m);
      try { fn(); } finally { (this.run as any).frontDoor = orig; }
      void before;
      for (const m of captured) this.due.push({ at: t, message: pickMsg(m) });
      void label;
      return () => {};
    };
  }

  /** Messages the inward simulators send first, as deliveries due at their engine times. */
  start(now: number, conversation = 'conv-1'): Delivery[] {
    this.run.clock.now = now;
    for (const [, sim] of this.run.sims) if (sim.sim.direction === 'inward') sim.start(conversation);
    return this.takeDeliveries();
  }

  takeDeliveries(): Delivery[] {
    const d = this.due;
    this.due = [];
    return d.sort((a, b) => a.at - b.at);
  }

  async attempt(req: AttemptRequest): Promise<AttemptResponse> {
    this.run.clock.now = req.now;
    // Messages the context refers to by claim-check, handed over with the request (a deployed worker holds no payloads).
    for (const m of req.messages ?? []) if (!this.run.messages.some((x) => x.id === m.id)) this.run.messages.push({ port: '', from: 'simulator', at: req.now, correlation: {}, ...m } as Message);
    let inst = this.instances.get(req.instance);
    if (!inst) {
      inst = new Instance(this.run, { kind: 'workflow', name: req.instance, release: '0.0.0' }, req.ctx, 'engine');
      this.instances.set(req.instance, inst);
    }
    inst.ctx = clone(req.ctx);
    inst.callCounts.set(req.step, req.call - 1);
    const act = req.release ? this.run.resolveDoc(req.activity, req.release) : this.run.resolveDoc(req.activity);
    if (!act) return { kind: 'error', name: 'NotFound', retryable: false, message: `${req.activity} does not resolve` };
    const scope = new Scope();
    let ctx = inst.ctx;
    let item: Frame['item'];
    if (req.level === 'item' && req.ctx.__item) {
      const it = req.ctx.__item;
      ctx = { ...inst.ctx, payment: it.payment ?? {}, ext: it.ext ?? {}, facts: it.facts ?? {}, items: [it] };
      item = { index: 0, item: it, parent: inst.ctx };
    }
    const frame: Frame = { inst, ctx, scope, hookOwner: '', tryStack: [], item };
    let settled = false;
    const p = invoke(act, { id: req.step, call: req.activity, policy: req.policy }, frame, scope, req.step, req.level, req.call, req.attempt).then(
      (r) => ({ ok: true as const, r }),
      (e) => ({ ok: false as const, e }),
    ).finally(() => (settled = true));
    // Let the attempt run to completion or to a wait; an attempt still waiting is a hang.
    for (let i = 0; i < 20 && !settled; i++) await new Promise((r) => setImmediate(r));
    if (!settled) {
      scope.cancel({ hang: true });
      void p;
      return { kind: 'hang' };
    }
    const out = await p;
    if (!out.ok) {
      const e = out.e;
      if (e instanceof Cancelled) return { kind: 'hang' };
      if (e instanceof TransientFailure) return { kind: 'error', name: e.errorName, retryable: true, message: e.message };
      if (e instanceof ActivityFailure) return { kind: 'error', name: e.errorName, retryable: e.retryable, message: e.message };
      if (e instanceof ScriptError) return { kind: 'error', name: e.errorName, retryable: !!e.retryable, message: e.message };
      if (e instanceof ContractViolation) return { kind: 'error', name: 'ContractViolation', retryable: false, message: e.message };
      return { kind: 'error', name: 'Error', retryable: false, message: String(e?.message ?? e) };
    }
    const r = out.r;
    const writes: Record<string, any> = {};
    for (const [k, v] of Object.entries(r.writes ?? {})) if (coveredByAny(act.writes ?? [], k.replace(/\[\d+\]/g, '[]'))) writes[k] = v;
    const latencyMs = this.latency[`step:${req.step}`] ?? this.latency[req.activity];
    return { kind: 'result', outcome: r.outcome, reasonCode: r.reasonCode, writes, decisions: r.decisions, ...(r.ruleTrace ? { ruleTrace: { worst: r.ruleTrace.worst, findings: r.ruleTrace.findings, trace: r.ruleTrace.trace } } : {}), ...(latencyMs ? { latencyMs } : {}) };
  }

  /** Messages the platform sent, for the comparison of messages and ids. */
  sent() {
    return this.run.messages.filter((m) => m.from === 'platform').map((m) => ({ from: m.from, id: m.id, key: m.key, type: m.type }));
  }
}

function pickMsg(m: Message): Delivery['message'] {
  return { id: m.id, type: m.type, status: m.status, reasonCode: m.reasonCode, correlation: m.correlation, conversation: m.conversation, inReplyTo: m.inReplyTo, copy: m.copy };
}

/** Apply an attempt's writes to an engine's context, as the studio does. */
export function applyWrites(ctx: any, writes: Record<string, any>) {
  for (const [p, v] of Object.entries(writes)) setPath(ctx, p, v);
  return ctx;
}

export { getPath };
