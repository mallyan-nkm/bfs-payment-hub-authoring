import { addDuration } from '../core/duration.js';
import type { VirtualClock } from '../interp/clock.js';

// The simulator runtime (Part 7; C10). A simulator stands in for one party on one port and
// plays one scenario. It keeps conversation state keyed by message id, so a retry of the same
// message gets the same answer and a later inquiry gets a consistent one (R-07-05).
// Delays run on the virtual clock (R-07-04).

export interface Message {
  id: string;
  type: string;
  port: string;
  from: 'platform' | 'simulator';
  at: number;
  status?: string;
  reasonCode?: string;
  correlation: Record<string, any>;
  conversation?: string; // the run-level conversation it belongs to
  inReplyTo?: string;
  family?: string; // what the front door reads from the message, such as the file an acknowledgement answers (R-05-44)
  receivedAt?: number; // when the front door took it (R-05-43)
  payload?: any; // the raw content; held by the run, referenced from the context
  malformed?: boolean;
  copy?: number;
  key?: string; // idempotency key of a platform message
}

export type SimResponse =
  | { kind: 'reply'; message: Message }
  | { kind: 'no-reply' }
  | { kind: 'transient'; error: string }
  | { kind: 'ack' };

export interface SimHost {
  clock: VirtualClock;
  /** Hand a message from the simulator to the platform's front door. */
  deliver(m: Message): void;
  newId(prefix: string): string;
  log(event: string, detail: any): void;
}

interface Conversation {
  failsLeft: number;
  response?: SimResponse;
  pendingSteps?: any[];
}

export class SimulatorInstance {
  cursor = 0;
  readonly received: Message[] = [];
  readonly emitted: Message[] = [];
  readonly unexpected: Message[] = [];
  private conversations = new Map<string, Conversation>();
  readonly steps: any[];

  constructor(readonly sim: any, readonly scenarioName: string, readonly host: SimHost, readonly inbound?: { payload?: any; malformed?: boolean }) {
    const sc = (sim.scenarios ?? []).find((s: any) => s.name === scenarioName);
    if (!sc) throw new Error(`Simulator ${sim.name} has no scenario ${scenarioName}`);
    this.scenario = sc;
    this.steps = sc.steps ?? [];
  }
  readonly scenario: any;

  get port(): string {
    return this.sim.port;
  }

  get malformed(): boolean {
    return !!this.scenario.malformed;
  }

  /** Inward simulators begin by sending; play the leading send steps (R-07 kinds: inward). */
  start(conversation: string) {
    if (this.sim.direction !== 'inward') return;
    let first = true;
    while (this.cursor < this.steps.length && this.steps[this.cursor].send) {
      const st = this.steps[this.cursor++];
      const payload = first ? this.inbound?.payload : undefined;
      this.emit(st, { correlation: {}, conversation }, undefined, payload);
      first = false;
    }
  }

  /** A message from the platform arrives at this simulator's port. */
  receive(m: Message, expectsSyncReply: boolean): SimResponse {
    this.received.push(m);
    const conv = this.conversations.get(m.id);
    if (conv) {
      // A retry of a message already seen.
      if (conv.failsLeft > 0) {
        conv.failsLeft--;
        return { kind: 'transient', error: 'TransientError' };
      }
      if (conv.pendingSteps) {
        const steps = conv.pendingSteps;
        conv.pendingSteps = undefined;
        conv.response = this.play(steps, m, expectsSyncReply);
      }
      return conv.response ?? { kind: 'no-reply' };
    }
    // A simulator that keys its state (by message id, say) holds one conversation per key: a new
    // key after the scenario has played out starts the scenario again.
    if (this.cursor >= this.steps.length && this.sim.state?.keyedBy && this.steps.some((x: any) => x.expect)) this.cursor = this.steps.findIndex((x: any) => x.expect);
    const st = this.steps[this.cursor];
    if (!st || !st.expect || (st.expect && m.type && !typeMatches(st.expect, m.type))) {
      this.unexpected.push(m);
      this.host.log('sim-unexpected', { sim: this.sim.name, message: m.type, at: this.cursor });
      return expectsSyncReply ? { kind: 'no-reply' } : { kind: 'ack' };
    }
    this.cursor++;
    // Steps after the expect, up to the next expect, are this conversation's reply.
    const replySteps: any[] = [];
    while (this.cursor < this.steps.length && !this.steps[this.cursor].expect) replySteps.push(this.steps[this.cursor++]);
    const failTimes = Number(st.failTimes ?? 0);
    if (failTimes > 0) {
      this.conversations.set(m.id, { failsLeft: failTimes - 1, pendingSteps: replySteps });
      return { kind: 'transient', error: 'TransientError' };
    }
    const response = this.play(replySteps, m, expectsSyncReply);
    this.conversations.set(m.id, { failsLeft: 0, response });
    return response;
  }

  private play(steps: any[], req: Message, expectsSyncReply: boolean): SimResponse {
    let response: SimResponse | undefined;
    let noReply = false;
    for (const st of steps) {
      if (st.noReply) {
        noReply = true;
        continue;
      }
      if (st.wait) continue;
      if (!st.send) continue;
      const sync = expectsSyncReply && !response && !st.after && !noReply;
      const msg = this.emit(st, { correlation: req.correlation, conversation: req.conversation }, req, undefined, sync);
      if (sync) response = { kind: 'reply', message: msg };
    }
    if (response) return response;
    // A send that expects no reply on the connection is taken by the transport; no reply means
    // no later message, not a hanging attempt.
    if (!expectsSyncReply) return { kind: 'ack' };
    void noReply;
    return { kind: 'no-reply' };
  }

  private emit(st: any, base: { correlation: any; conversation?: string }, req: Message | undefined, payload: any, sync = false): Message {
    const make = (copy: number): Message => ({
      id: copy === 0 ? this.host.newId('sim') : '',
      type: st.send,
      port: this.sim.port,
      from: 'simulator',
      at: this.host.clock.now,
      status: st.status,
      reasonCode: st.reasonCode,
      correlation: { ...base.correlation },
      conversation: base.conversation,
      inReplyTo: req?.id,
      ...(st.family ? { family: st.family } : {}),
      payload,
      malformed: this.malformed || !!st.malformed || (!!this.inbound?.malformed && !req),
      copy,
    });
    const msg = make(0);
    this.emitted.push(msg);
    if (sync) return msg;
    const when = st.after ? addDuration(this.host.clock.now, st.after) : this.host.clock.now;
    this.host.clock.at(when, () => this.host.deliver(msg), `sim ${this.sim.name} sends ${st.send}`);
    if (st.duplicate) {
      const dup = { ...msg, copy: 1 };
      this.host.clock.at(when, () => this.host.deliver(dup), `sim ${this.sim.name} sends a copy of ${st.send}`);
    }
    return msg;
  }
}

function typeMatches(expected: string, actual: string): boolean {
  return expected === actual;
}

/** The standard scenario set every outward simulator carries (R-07-02). */
export const STANDARD_SCENARIOS = ['accept', 'reject', 'pending-then-final', 'no-reply', 'late-reply', 'duplicate-reply', 'fail-twice-then-accept', 'malformed-reply'];

export function missingStandardScenarios(sim: any): string[] {
  if (sim.direction !== 'outward') return [];
  const have = new Set((sim.scenarios ?? []).map((s: any) => s.name));
  return STANDARD_SCENARIOS.filter((s) => !have.has(s));
}

/** Coverage of reason codes by scenarios (R-07-08). */
export function reasonCodeCoverage(sim: any, codes: string[]): { covered: string[]; missing: string[] } {
  const seen = new Set<string>();
  for (const sc of sim.scenarios ?? []) for (const st of sc.steps ?? []) if (st.reasonCode) seen.add(st.reasonCode);
  return { covered: codes.filter((c) => seen.has(c)), missing: codes.filter((c) => !seen.has(c)) };
}
