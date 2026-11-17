import { useEffect, useState } from 'react';
import { get } from './api';

// Expansion of composite steps in the browser (Part 5, 5.6). It mirrors studio/src/core/expand.ts
// so the canvas can show the expanded form of a workflow that is not saved yet. The server's
// expansion (GET /api/expanded/:name) stays the reference; the designer compares the two.

export type Resolver = (name: string) => any;

export const isComposite = (a: any) => !!a && a.kind === 'activity' && a.implementation?.binding === 'subflow';

const clone = <T,>(x: T): T => (x === undefined ? x : JSON.parse(JSON.stringify(x)));

function* nested(st: any): Generator<[any, string | number]> {
  if (!st || typeof st !== 'object') return;
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
  for (const st of Array.isArray(steps) ? steps : []) {
    out.push(st);
    for (const [h, k] of nested(st)) walkSteps(h[k], out);
  }
  return out;
}

export function allSteps(doc: any): any[] {
  return [...walkSteps(doc?.do), ...walkSteps(doc?.finally), ...walkSteps(doc?.deadline?.do)];
}

function rename(steps: any[], prefix: string, policy: any, problems: string[]): any[] {
  const inner = new Set(walkSteps(steps).filter((s) => s.id).map((s) => s.id));
  for (const st of walkSteps(steps)) {
    if (st.id) st.id = `${prefix}.${st.id}`;
    for (const ref of [st.loop?.until, st.switch?.by]) if (ref?.outcomeOf && inner.has(ref.outcomeOf)) ref.outcomeOf = `${prefix}.${ref.outcomeOf}`;
    if (st.await && !('timeout' in st.await)) {
      if (!policy.replyTimeout) {
        problems.push(`${prefix}: an await has no timeout and the calling step has no replyTimeout (L32)`);
        continue;
      }
      const aw: any = { ...st.await, timeout: policy.replyTimeout };
      if (policy.replyCalendar && !('calendar' in aw)) aw.calendar = policy.replyCalendar;
      st.await = Object.fromEntries(['message', 'correlateBy', 'timeout', 'calendar', 'onTimeout'].filter((k) => k in aw).map((k) => [k, aw[k]]));
    }
  }
  return steps;
}

function expandSteps(steps: any[], resolve: Resolver, stack: string[], problems: string[]): any[] {
  const out: any[] = [];
  for (const s of Array.isArray(steps) ? steps : []) {
    const st = clone(s);
    for (const [h, k] of [...nested(st)]) h[k] = expandSteps(h[k], resolve, stack, problems);
    const act = st?.call ? resolve(st.call) : undefined;
    if (st?.call && isComposite(act)) {
      const sub = resolve(act.implementation.subflow);
      if (!sub) {
        problems.push(`The subflow ${act.implementation.subflow} of ${act.name} does not resolve`);
        out.push(st);
        continue;
      }
      if (stack.includes(act.name)) {
        problems.push(`The composite ${act.name} calls itself through its subflow`);
        out.push(st);
        continue;
      }
      if (!st.id) problems.push(`A step that calls the composite ${act.name} needs an id (L30)`);
      const id = st.id ?? st.call;
      const policy = { ...(act.policy ?? {}), ...(st.policy ?? {}) };
      const body = rename(expandSteps(clone(sub.do ?? []), resolve, [...stack, act.name], problems), id, policy, problems);
      const block: any = { id, block: { of: act.name, by: sub.name, do: body } };
      if (st.handle && Object.keys(st.handle).length) block.handle = st.handle;
      out.push(block);
    } else out.push(st);
  }
  return out;
}

/** The steps of a subflow as they sit inside the calling step: ids prefixed, reply time filled in. */
export function prefixSteps(steps: any[], prefix: string, policy: any): any[] {
  return rename(clone(steps ?? []), prefix, policy ?? {}, []);
}

export function expandWorkflow(doc: any, resolve: Resolver): { doc: any; expanded: boolean; problems: string[] } {
  const problems: string[] = [];
  if (!doc || doc.kind !== 'workflow' || doc.form === 'expanded') return { doc, expanded: false, problems };
  if (!allSteps(doc).some((s) => s?.call && isComposite(resolve(s.call)))) return { doc, expanded: false, problems };
  const parts: any = {};
  if (doc.do) parts.do = expandSteps(doc.do, resolve, [], problems);
  if (doc.finally) parts.finally = expandSteps(doc.finally, resolve, [], problems);
  if (doc.deadline) parts.deadline = { ...doc.deadline, do: expandSteps(doc.deadline.do ?? [], resolve, [], problems) };
  return { doc: { ...doc, ...parts, form: 'expanded', expandedFrom: { release: doc.release, hash: '(computed by the server)' } }, expanded: true, problems };
}

const cache = new Map<string, Promise<any>>();
function load(name: string, tenant: string): Promise<any> {
  const k = `${tenant}|${name}`;
  if (!cache.has(k)) cache.set(k, get(`/api/artefacts/${encodeURIComponent(name)}`, tenant).then((a) => a.doc, () => null));
  return cache.get(k)!;
}

/**
 * Every document a workflow's expansion reaches: the activities it calls and, for a composite,
 * its subflow and what that calls, loaded until nothing new turns up.
 */
export function useResolver(doc: any, tenant: string): { resolve: Resolver; ready: boolean; docs: Record<string, any> } {
  const [docs, setDocs] = useState<Record<string, any>>({});
  const [ready, setReady] = useState(false);
  const sig = JSON.stringify(allSteps(doc).map((s) => s?.call).filter(Boolean).sort());
  useEffect(() => {
    let live = true;
    setReady(false);
    (async () => {
      const have: Record<string, any> = {};
      let todo: string[] = JSON.parse(sig);
      while (todo.length) {
        const names = [...new Set(todo)].filter((n) => !(n in have));
        todo = [];
        const loaded = await Promise.all(names.map((n) => load(n, tenant)));
        names.forEach((n, i) => (have[n] = loaded[i]));
        for (const n of names) {
          const d = have[n];
          if (isComposite(d) && !(d.implementation.subflow in have)) todo.push(d.implementation.subflow);
          if (d?.kind === 'workflow') todo.push(...allSteps(d).map((s) => s?.call).filter(Boolean));
        }
      }
      if (live) {
        setDocs(have);
        setReady(true);
      }
    })();
    return () => {
      live = false;
    };
  }, [sig, tenant]);
  return { resolve: (n) => docs[n] ?? undefined, ready, docs };
}

/** Compare two documents with key order ignored. */
export function sameDoc(a: any, b: any): boolean {
  const canon = (x: any): any => (Array.isArray(x) ? x.map(canon) : x && typeof x === 'object' ? Object.fromEntries(Object.keys(x).sort().map((k) => [k, canon(x[k])])) : x);
  return JSON.stringify(canon(a)) === JSON.stringify(canon(b));
}
