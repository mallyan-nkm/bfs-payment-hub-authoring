// Reference behaviour of an activity, by type (Part 3, 3.2). It runs when an activity has no
// script-behind in the studio. It honours the contract: it reads only the declared view, writes
// only declared paths, reaches outside only through declared ports, and derives its outcome from
// the rule service, the decision table or the simulator's reply.

import type { SessionResult } from '../rules/service.js';
import type { SimResponse, Message } from '../sim/runtime.js';

export interface ActivityIO {
  /** Send a message on a port; the simulator answers. */
  call(port: string, opts: { expectsSyncReply: boolean; type?: string }): Promise<SimResponse>;
  /** Wait for a message of a type correlated to this payment, as a hold does. */
  waitFor(type: string, timeoutMs: number | null): Promise<Message | null>;
  rules(ruleSet: string, level?: 'batch' | 'item', facts?: any): SessionResult;
  table(name: string): SessionResult;
  hang(): Promise<never>;
  now(): string;
  today(): string;
  param(name: string): any;
  sentType(): string | undefined;
  received(): Message | undefined;
}

export interface ActivityResult {
  outcome: string;
  reasonCode?: string;
  writes: Record<string, any>;
  decisions?: Record<string, any>;
  ruleTrace?: SessionResult;
  reply?: Message;
}

export class TransientFailure extends Error {
  constructor(public errorName = 'TransientError') {
    super(errorName);
  }
}

export class ActivityFailure extends Error {
  constructor(public errorName: string, message: string, public retryable = false) {
    super(message);
  }
}

const POSITIVE = ['accepted', 'released', 'approved', 'ok', 'found', 'clear', 'settled', 'ack', 'done', 'posted', 'reserved', 'sufficient', 'matched', 'verified', 'unique', 'resolved', 'notified', 'responded', 'set', 'decided', 'mapped', 'pass'];
const NEGATIVE = ['rejected', 'declined', 'nak', 'hit', 'suspect', 'insufficient', 'not-found', 'unknown-party', 'mismatch', 'duplicate', 'unmatched', 'refused'];

function names(act: any): string[] {
  return (act.outcomes ?? []).map((o: any) => o.name);
}

export function primaryOutcome(act: any): string {
  const p = (act.outcomes ?? []).find((o: any) => o.primary);
  return p ? p.name : names(act)[0];
}

/** The first non-primary outcome that is a business negative (not pending, unknown or expired). */
export function negativeOutcome(act: any): string | undefined {
  const prim = primaryOutcome(act);
  return names(act).find((n) => n !== prim && !['pending', 'unknown', 'expired'].includes(n)) ?? names(act).find((n) => n === 'unknown');
}

/** Map a reply status to one of the activity's outcomes. */
export function outcomeForStatus(act: any, status: string | undefined): string | null {
  const ns = names(act);
  const prim = primaryOutcome(act);
  if (!status) return prim;
  if (ns.includes(status)) return status;
  if (POSITIVE.includes(status)) return prim;
  if (status === 'pending') return ns.includes('pending') ? 'pending' : ns.includes('unknown') ? 'unknown' : null;
  if (NEGATIVE.includes(status)) return negativeOutcome(act) ?? prim;
  return prim;
}

export function expectsSyncReply(act: any): boolean {
  if (act.type === 'exchange') {
    if (act.mode === 'inquire') return true;
    if (act.mode === 'respond') return false;
    return names(act).includes('pending');
  }
  if (act.type === 'hold') return false;
  return true;
}

const STATUS_CODE: Record<string, string> = {
  accepted: 'ACCP', settled: 'ACSC', rejected: 'RJCT', pending: 'PDNG', ack: 'ACCP', nak: 'RJCT', unknown: 'PDNG', responded: 'ACCP',
};

function genericWrites(act: any, outcome: string, reply?: Message, extra: Record<string, any> = {}): Record<string, any> {
  const w: Record<string, any> = {};
  for (const p of act.writes ?? []) {
    if (p.startsWith('facts')) w[p] = { outcome, result: outcome, ...(reply?.status ? { status: reply.status } : {}), ...(reply?.reasonCode ? { reasonCode: reply.reasonCode } : {}), ...extra };
    else if (p === 'status') w[p] = { code: STATUS_CODE[outcome] ?? outcome.toUpperCase(), ...(reply?.reasonCode ? { reasonCode: reply.reasonCode } : {}) };
  }
  return w;
}

export async function reference(act: any, view: any, io: ActivityIO): Promise<ActivityResult> {
  const prim = primaryOutcome(act);
  const ports: string[] = act.ports ?? [];
  const level = act.level;

  switch (act.type) {
    case 'validate': {
      if (act.ruleSets?.length) {
        const results = act.ruleSets.map((rs: string) => io.rules(rs, level));
        const findings = results.flatMap((r: SessionResult) => r.findings);
        const worst = results.reduce((w: string, r: SessionResult) => rank(r.worst) > rank(w) ? r.worst : w, 'pass');
        const ns = names(act);
        const outcome = worst === 'reject' ? (ns.includes('reject') ? 'reject' : negativeOutcome(act) ?? prim)
          : worst === 'repair' ? (ns.includes('repair') ? 'repair' : ns.includes('reject') ? 'reject' : prim)
          : prim;
        const blocking = findings.find((f: any) => f.severity === 'reject') ?? findings.find((f: any) => f.severity === 'repair');
        const writes: Record<string, any> = {};
        for (const p of act.writes ?? []) if (p.startsWith('facts')) writes[p] = { worst, findings };
        const merged: SessionResult = { ...results[0], findings, worst, trace: results.flatMap((r: SessionResult) => r.trace) };
        return { outcome, reasonCode: outcome !== prim ? blocking?.reasonCode : undefined, writes, ruleTrace: merged };
      }
      break; // a validate block with a port, such as a duplicate check
    }
    case 'decide': {
      const decisions: Record<string, any> = {};
      let reason: string | undefined;
      let last: SessionResult | undefined;
      for (const t of act.ruleSets ?? []) {
        last = io.table(t);
        for (const d of last.decisions) {
          decisions[d.name] = d.value;
          reason ??= d.reason;
        }
      }
      const ns = names(act);
      const values = Object.values(decisions);
      let outcome = prim;
      const named = values.find((v) => typeof v === 'string' && ns.includes(v));
      if (named) outcome = named as string;
      else if (values.length === 0 || values.every((v) => v === 'none' || v === null)) outcome = ns.find((n) => n !== prim) ?? prim;
      const writes: Record<string, any> = {};
      for (const p of act.writes ?? []) if (p.startsWith('facts')) writes[p] = { ...decisions, ...(reason ? { reason } : {}) };
      return { outcome, writes, decisions, ruleTrace: last };
    }
    case 'map': {
      if (act.map?.direction === 'in') {
        const m = io.received();
        if (!m) return { outcome: prim, writes: {} };
        if (m.malformed) return { outcome: negativeOutcomeOrSecond(act), reasonCode: 'FF01', writes: {} };
        const outcome = outcomeForStatus(act, m.status) ?? prim;
        const writes = genericWrites(act, outcome, m);
        return { outcome, reasonCode: outcome !== prim ? m.reasonCode : undefined, writes, reply: m };
      }
      // Outward: render the scheme message; the context keeps a reference only.
      return { outcome: prim, writes: { 'messages.rendered': { by: act.name, at: io.now() } } };
    }
    case 'hold': {
      const port = ports[0];
      if (port) await io.call(port, { expectsSyncReply: false });
      const expiry = io.param('expiry');
      const ms = expiry ? durationToMs(expiry) : null;
      const decision = await io.waitFor('p.ops.decision', ms);
      if (!decision) return { outcome: names(act).includes('expired') ? 'expired' : negativeOutcomeOrSecond(act), writes: genericWrites(act, 'expired') };
      const outcome = outcomeForStatus(act, decision.status) ?? prim;
      return { outcome, writes: genericWrites(act, outcome, decision), reply: decision };
    }
  }

  // Everything else reaches its port: enrich, screen, reserve, exchange, post, notify, reconcile.
  if (ports.length === 0) return { outcome: prim, writes: genericWrites(act, prim) };
  const sync = expectsSyncReply(act);
  const res = await io.call(ports[0], { expectsSyncReply: sync, type: io.sentType() });
  if (res.kind === 'transient') throw new TransientFailure(res.error);
  if (res.kind === 'no-reply') return io.hang();
  if (res.kind === 'ack') return { outcome: prim, writes: genericWrites(act, prim) };
  const m = res.message;
  if (m.malformed) {
    if (names(act).includes('unknown')) return { outcome: 'unknown', writes: genericWrites(act, 'unknown', m) };
    throw new ActivityFailure('MalformedReply', `The reply from ${m.port} is malformed`);
  }
  const outcome = outcomeForStatus(act, m.status);
  if (outcome === null) return io.hang();
  return { outcome, reasonCode: outcome !== prim ? m.reasonCode : undefined, writes: genericWrites(act, outcome, m), reply: m };
}

function negativeOutcomeOrSecond(act: any): string {
  return negativeOutcome(act) ?? names(act)[1] ?? primaryOutcome(act);
}

function rank(s: string): number {
  return { pass: 0, warn: 1, repair: 2, reject: 3 }[s as 'pass'] ?? 0;
}

function durationToMs(d: string): number {
  const m = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/.exec(d);
  if (!m) return 5 * 86400000;
  return ((((Number(m[1] ?? 0) * 24) + Number(m[2] ?? 0)) * 60 + Number(m[3] ?? 0)) * 60 + Number(m[4] ?? 0)) * 1000;
}
