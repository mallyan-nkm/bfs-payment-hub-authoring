import { Fragment, useEffect, useMemo, useState, type ReactNode } from 'react';
import type { TestRunReport } from '../api';
import { Badge, Section } from '../ui';

// The workflow debugger (F-17; docs/ISSUES.md I-32). A debug run goes end to end on the studio's
// interpreter and virtual clock, and keeps the data after every event. Here the designer steps
// through it: forwards and back, into a leg or a hook and out again, to the next breakpoint. At each
// stop: where the run is in the workflow, what just happened, what it decided and found, what data
// changed, and all the data as it stands. Runs are deterministic, so stepping back is exact, and
// nothing here touches a running payment.

type Ev = { i: number; at: number; instance: string; step?: string; type: string; detail?: any; data?: any; same?: boolean; omitted?: boolean };
type Inst = { id: string; kind: string; workflow: string; release: string; parent?: string; outcome?: string };

/** The events a step lands on by default. "Every event" adds attempts, replies and the rest. */
const STOPS = new Set(['message-in', 'start', 'outcome', 'branch', 'slot', 'hook', 'hook-unbound', 'for-each', 'item-failed', 'await-start', 'await-received', 'await-timeout', 'join', 'signal', 'compensate', 'catch', 'deadline', 'end', 'failed', 'gave-up', 'attempt-failed', 'retry-wait', 'block-start', 'block-end', 'unmatched']);

/** A step's id, as the outline knows it: no item index. */
const stepKey = (s?: string) => (s ?? '').replace(/\[\d+\]/g, '');
const fmt = (ms: number) => (ms < 1000 ? `${ms} ms` : ms < 60000 ? `${(ms / 1000).toFixed(ms % 1000 ? 1 : 0)} s` : `${Math.floor(ms / 60000)} min ${Math.round((ms % 60000) / 1000)} s`);

/** Leaves of a JSON value by path, to see what changed. */
function leaves(v: any, p = '', out: Map<string, string> = new Map()): Map<string, string> {
  if (v && typeof v === 'object') {
    const entries = Array.isArray(v) ? v.map((x, i) => [`[${i}]`, x] as const) : Object.entries(v).map(([k, x]) => [p ? `.${k}` : k, x] as const);
    if (!entries.length) out.set(p, Array.isArray(v) ? '[]' : '{}');
    for (const [k, x] of entries) leaves(x, `${p}${k}`, out);
  } else out.set(p, JSON.stringify(v));
  return out;
}
function changes(before: any, after: any): { path: string; before?: string; after?: string }[] {
  const a = leaves(before ?? {});
  const b = leaves(after ?? {});
  const out: { path: string; before?: string; after?: string }[] = [];
  for (const [k, v] of b) if (a.get(k) !== v) out.push({ path: k, before: a.get(k), after: v });
  for (const [k, v] of a) if (!b.has(k)) out.push({ path: k, before: v });
  return out;
}

/** The steps of a workflow document, in order, with their depth: the outline the debugger marks. */
function outline(doc: any, docs: Record<string, any> = {}): { key: string; text: string; depth: number }[] {
  const out: { key: string; text: string; depth: number }[] = [];
  const walk = (steps: any[], depth: number, prefix: string) => {
    for (const st of steps ?? []) {
      if (!st || typeof st !== 'object') continue;
      const id = st.id;
      const key = id ? `${prefix}${id}` : '';
      const named = (what: string) => (id ? `${id}  ${what}` : what);
      if (st.call) out.push({ key, text: `${id ?? 'call'}  ${st.call}`, depth });
      else if ('end' in st && !st.forEach) out.push({ key: key || `end:${st.end}`, text: `end ${st.end}`, depth });
      else if ('return' in st && Object.keys(st).length <= 2) out.push({ key: key || `return:${st.return}`, text: `return ${st.return}`, depth });
      else if (st.forEach) { out.push({ key: key || 'for-each', text: `for each ${st.forEach.in === 'items[]' ? 'line' : st.forEach.in}`, depth }); walk(st.forEach.do, depth + 1, prefix); }
      else if (st.switch) { out.push({ key, text: named('choose a branch'), depth }); for (const [c, s] of Object.entries<any>(st.switch.cases ?? {})) { out.push({ key: `${key}?${c}`, text: `case ${c}`, depth: depth + 1 }); walk(s, depth + 2, prefix); } if (st.switch.default) { out.push({ key: `${key}?default`, text: 'default', depth: depth + 1 }); walk(st.switch.default, depth + 2, prefix); } }
      else if (st.parallel) { out.push({ key, text: named('in parallel'), depth }); (st.parallel.branches ?? []).forEach((b: any[]) => walk(b, depth + 1, prefix)); }
      else if (st.block) { out.push({ key, text: `${id}  ${st.block.of ?? 'block'}`, depth }); walk(st.block.do, depth + 1, `${prefix}${id}.`); }
      else if (st.slot) out.push({ key, text: `${id ?? st.slot}  slot ${st.slot}: the leg runs here`, depth });
      else if (st.hook) out.push({ key: st.hook, text: `hook point ${st.hook}`, depth });
      else if (st.await) out.push({ key, text: named(`wait for ${st.await.message}`), depth });
      else if (st.try) { out.push({ key, text: named('try'), depth }); walk(st.try, depth + 1, prefix); }
      else if (st.loop) { out.push({ key, text: named('loop'), depth }); walk(st.loop.do, depth + 1, prefix); }
      else if (st.use) { out.push({ key: key || `use:${st.use}`, text: named(`use ${st.use}: its steps run here`), depth }); walk(docs[`use:${st.use}`]?.do, depth + 1, prefix); }
      else if (st.wait) out.push({ key, text: `wait ${st.wait}`, depth });
      for (const [o, a] of Object.entries<any>(st.handle ?? {})) if (Array.isArray(a)) { out.push({ key: `${key}!${o}`, text: `if ${o}`, depth: depth + 1 }); walk(a, depth + 2, prefix); }
    }
  };
  walk(doc?.do ?? [], 0, '');
  if (doc?.finally) { out.push({ key: 'finally', text: 'finally', depth: 0 }); walk(doc.finally, 1, ''); }
  return out;
}

/** What happened at an event, in plain words. */
function describe(e: Ev, inst?: Inst): string {
  const d = e.detail ?? {};
  switch (e.type) {
    case 'message-in': return `A message arrived: ${d.type ?? d.message ?? ''}.`;
    case 'start': return `${inst?.workflow ?? 'The workflow'} started${inst?.kind ? ` (${inst.kind})` : ''}.`;
    case 'outcome': return `Step ${e.step} ran ${d.activity ?? 'its block'}${d.release ? `@${d.release}` : ''}. Its outcome is ${d.outcome}, after ${d.attempts} attempt${d.attempts === 1 ? '' : 's'}${d.reasonCode ? `, reason ${d.reasonCode}` : ''}.`;
    case 'branch': return `Switch ${e.step ?? ''}: ${JSON.stringify(d.by)} is ${JSON.stringify(d.value)}, so the ${d.case} branch runs.`;
    case 'slot': return `Slot ${e.step}: the leg ${d.leg}@${d.release} runs here.`;
    case 'hook': return `Hook point ${e.step}: the bank's hook ${d.hook} (${d.type}) answered ${d.outcome}.`;
    case 'hook-unbound': return `Hook point ${e.step}: no hook is bound, so nothing runs.`;
    case 'for-each': return `For each: ${d.items ?? d.n ?? '?'} item(s)${d.chunked ? ', in chunks' : ''}.`;
    case 'item-failed': return `An item failed at ${e.step}.`;
    case 'await-start': return `It waits for ${d.message ?? 'a message'}${d.timeout ? `, for ${d.timeout} at most` : ''}.`;
    case 'await-received': return `The awaited ${d.message ?? 'message'} arrived.`;
    case 'await-timeout': return `No ${d.message ?? 'message'} came in time.`;
    case 'join': return `The parallel branches joined (${d.join ?? 'all'}).`;
    case 'signal': return `Signal ${d.signal ?? ''} arrived.`;
    case 'compensate': return `Undo: ${e.step} is compensated by ${d.compensation ?? d.activity ?? 'its compensation'}.`;
    case 'catch': return `The error was caught at ${e.step}.`;
    case 'deadline': return 'The deadline passed.';
    case 'end': return `${inst?.workflow ?? 'The workflow'} ended as ${d.outcome ?? inst?.outcome ?? '?'}.`;
    case 'failed': return `It failed: ${d.message ?? d.error ?? ''}`;
    case 'gave-up': return `Retries are spent: the outcome is ${d.outcome}.`;
    case 'attempt': return `Attempt ${d.attempt} of ${e.step}${d.timeout ? `, with ${d.timeout} to answer` : ''}.`;
    case 'attempt-failed': return `Attempt ${d.attempt} of ${e.step} failed: ${d.error}${d.retryable ? ', and may be retried' : ', and is not retried'}.`;
    case 'retry-wait': return `It waits ${fmt(d.ms)} before trying ${e.step} again.`;
    case 'block-start': return `Composite ${e.step} starts: the steps inside it follow.`;
    case 'block-end': return `Composite ${e.step} ended${d.outcome ? ` as ${d.outcome}` : ''}.`;
    case 'message-out': return `It sent ${d.type ?? d.message ?? 'a message'}${d.port ? ` on ${d.port}` : ''}.`;
    case 'reply': return `A reply came: ${d.type ?? d.status ?? ''}.`;
    case 'step-start': return `Step ${e.step} starts.`;
    default: return `${e.type}${e.step ? ` at ${e.step}` : ''}.`;
  }
}

/** A JSON value as a tree, with the changed paths marked. */
function Tree({ v, path, changed, depth = 0 }: { v: any; path: string; changed: Set<string>; depth?: number }) {
  const [open, setOpen] = useState(depth < 2 || [...changed].some((c) => c.startsWith(path)));
  const hot = [...changed].some((c) => c === path || c.startsWith(path + '.') || c.startsWith(path + '['));
  if (v === null || typeof v !== 'object') return <span className={changed.has(path) ? 'dbg-changed' : ''}>{JSON.stringify(v)}</span>;
  const entries = Array.isArray(v) ? v.map((x, i) => [`[${i}]`, x] as [string, any]) : Object.entries(v);
  if (!entries.length) return <span>{Array.isArray(v) ? '[]' : '{}'}</span>;
  return (
    <span>
      <button className={`link small dbg-fold ${hot ? 'dbg-hot' : ''}`} onClick={() => setOpen(!open)} aria-label={`${open ? 'Fold' : 'Unfold'} ${path || 'the data'}`}>{open ? '▾' : '▸'} {Array.isArray(v) ? `${v.length} item${v.length === 1 ? '' : 's'}` : `${entries.length} field${entries.length === 1 ? '' : 's'}`}</button>
      {open && (
        <ul className="dbg-tree">
          {entries.map(([k, x]) => {
            const p = k.startsWith('[') ? `${path}${k}` : path ? `${path}.${k}` : k;
            return <li key={k}><span className={changed.has(p) ? 'dbg-key dbg-changed' : 'dbg-key'}>{k}</span>: <Tree v={x} path={p} changed={changed} depth={depth + 1} /></li>;
          })}
        </ul>
      )}
    </span>
  );
}

export function Debugger({ report, onClose }: { report: TestRunReport; onClose?: () => void }) {
  const rec: any = report.record ?? {};
  const instances: Inst[] = rec.instances ?? [];
  const docs: Record<string, any> = rec.docs ?? {};
  const all: Ev[] = useMemo(() => (rec.timeline ?? []).map((e: any, i: number) => ({ ...e, i })), [rec.timeline]);
  const [everything, setEverythingState] = useState(false);
  const stops = useMemo(() => all.filter((e) => everything || STOPS.has(e.type)), [all, everything]);
  const [p, setP] = useState(0);
  const [breaks, setBreaks] = useState<Set<string>>(new Set());
  // Showing more or fewer events keeps the run where it is: on the same event, or the next one shown.
  const setEverything = (v: boolean) => {
    const at = stops[p]?.i ?? 0;
    const next = all.filter((e) => v || STOPS.has(e.type));
    const n = next.findIndex((e) => e.i >= at);
    setEverythingState(v);
    setP(n < 0 ? next.length - 1 : n);
  };
  useEffect(() => setP((x) => Math.min(x, Math.max(0, stops.length - 1))), [stops.length]);

  const byId = useMemo(() => new Map(instances.map((i) => [i.id, i])), [instances]);
  // The data of each instance after every event: snapshots carried forward where it did not change.
  const dataAt = useMemo(() => {
    const last = new Map<string, any>();
    return all.map((e) => {
      if (e.data !== undefined) last.set(e.instance, e.data);
      return { data: last.get(e.instance), omitted: !!e.omitted };
    });
  }, [all]);
  const ancestors = (id: string): string[] => {
    const out: string[] = [];
    for (let i = byId.get(id); i?.parent; i = byId.get(i.parent)) out.push(i.parent);
    return out;
  };

  const cur = stops[p];
  const ol = useMemo(() => outline(cur ? docs[cur.instance] : undefined, docs), [docs, cur?.instance]);
  if (!cur) return <Section title="Debugger"><p className="muted">The run recorded no events.</p></Section>;
  const inst = byId.get(cur.instance);
  // What the run has done in this instance so far: the outcome of each step reached.
  const done = new Map<string, string>();
  for (const e of all) {
    if (e.i > cur.i) break;
    if (e.instance !== cur.instance) continue;
    if (e.type === 'outcome') done.set(stepKey(e.step), e.detail?.outcome);
    if (e.type === 'branch') done.set(`${e.step}?${e.detail?.case}`, 'taken');
    if (e.type === 'slot' || e.type === 'hook') done.set(e.type === 'hook' ? stepKey(e.step) : stepKey(e.step), e.detail?.outcome ?? e.detail?.leg ?? 'ran');
    if (e.type === 'hook-unbound') done.set(stepKey(e.step), 'unbound');
  }
  const here = cur.type === 'end' ? `end:${cur.detail?.outcome ?? inst?.outcome}` : stepKey(cur.step);
  const prevData = (() => {
    for (let j = cur.i - 1; j >= 0; j--) if (all[j].instance === cur.instance) return dataAt[j].data;
    return undefined;
  })();
  const now = dataAt[cur.i];
  const delta: { path: string; before?: string; after?: string }[] = cur.detail?.delta
    ? cur.detail.delta.map((d: any) => ({ path: d.path, before: d.before === undefined ? undefined : JSON.stringify(d.before), after: d.after === undefined ? undefined : JSON.stringify(d.after) }))
    : changes(prevData, now.data);
  const changedSet = new Set(changes(prevData, now.data).map((c) => c.path));
  const bpKey = (wf: string | undefined, step: string) => `${wf}#${step}`;
  const breakAt = (e: Ev) => breaks.has(bpKey(byId.get(e.instance)?.workflow, stepKey(e.step)));

  // Stepping.
  const go = (n: number) => setP(Math.max(0, Math.min(stops.length - 1, n)));
  const stepOver = () => {
    // The next stop in this instance, or in one that is not inside it (it ended, or a sibling).
    for (let j = p + 1; j < stops.length; j++) if (stops[j].instance === cur.instance || !ancestors(stops[j].instance).includes(cur.instance)) return go(j);
    go(stops.length - 1);
  };
  const stepOut = () => {
    const up = ancestors(cur.instance);
    for (let j = p + 1; j < stops.length; j++) if (up.includes(stops[j].instance)) return go(j);
    go(stops.length - 1);
  };
  const cont = () => {
    for (let j = p + 1; j < stops.length; j++) if (breakAt(stops[j])) return go(j);
    go(stops.length - 1);
  };
  const toggleBreak = (key: string) => {
    const k = bpKey(inst?.workflow, key);
    setBreaks((b) => { const n = new Set(b); if (n.has(k)) n.delete(k); else n.add(k); return n; });
  };
  const chain = [...ancestors(cur.instance).reverse(), cur.instance].map((id) => byId.get(id)).filter(Boolean) as Inst[];
  const d = cur.detail ?? {};
  const decisions = d.decisions && Object.keys(d.decisions).length ? d.decisions : undefined;

  return (
    <Section title="Debugger" actions={<span className="row-actions">{onClose && <button className="link" onClick={onClose}>Show the run report</button>}</span>}>
      <div className="dbg-bar" role="toolbar" aria-label="Debugger controls">
        <button onClick={() => go(0)} disabled={p === 0} title="Back to the first event">⏮ Restart</button>
        <button onClick={() => go(p - 1)} disabled={p === 0} title="One event back">◀ Step back</button>
        <button className="primary" onClick={() => go(p + 1)} disabled={p >= stops.length - 1} title="The next event, going into a leg or hook">Step into ▶</button>
        <button onClick={stepOver} disabled={p >= stops.length - 1} title="The next event at this level, over a leg or hook">Step over ⤼</button>
        <button onClick={stepOut} disabled={!inst?.parent} title="Back up to the workflow that started this one">Step out ⤴</button>
        <button onClick={cont} disabled={p >= stops.length - 1} title="Run to the next breakpoint, or to the end">Continue ⏭</button>
        <span className="dbg-pos" aria-live="polite">Event {p + 1} of {stops.length} · {fmt(cur.at)} on the virtual clock</span>
        <label className="check small"><input type="checkbox" checked={everything} onChange={(e) => setEverything(e.target.checked)} /> every event</label>
      </div>

      <div className="dbg-cols">
        <div className="dbg-left">
          <div className="small muted">Where the run is</div>
          <div className="dbg-chain">
            {!inst && <span className="strong">The front door <span className="muted small">(a message arriving, before it reaches a workflow)</span></span>}
            {chain.map((c, n) => <Fragment key={c.id}>{n > 0 && <span className="muted"> › </span>}<span className={c.id === cur.instance ? 'strong' : ''}>{c.workflow} <span className="muted small">({c.kind})</span></span></Fragment>)}
          </div>
          <ol className="dbg-outline" aria-label="Workflow steps">
            {ol.map((s, n) => {
              const outc = done.get(s.key);
              const isHere = !!s.key && (s.key === here || s.key === `${here}?${d.case}`);
              const bp = breaks.has(bpKey(inst?.workflow, s.key));
              return (
                <li key={n} className={`${isHere ? 'dbg-here' : ''} ${outc ? 'dbg-done' : ''}`} style={{ paddingLeft: 6 + s.depth * 14 }} data-step={s.key}>
                  <button className={`dbg-bp ${bp ? 'on' : ''}`} onClick={() => s.key && toggleBreak(s.key)} aria-label={`Breakpoint at ${s.key || s.text}`} aria-pressed={bp} title="Stop here on Continue">●</button>
                  <span className="mono small">{s.text}</span>
                  {outc && <Badge tone={/reject|fail|declin|unknown/.test(outc) ? 'fail' : 'pass'}>{outc}</Badge>}
                </li>
              );
            })}
            {!ol.length && inst && <li className="muted small">This is {inst?.kind === 'intake' ? 'the front door reading the inbound message' : 'not a workflow with steps'}.</li>}
          </ol>
        </div>

        <div className="dbg-right">
          <div className="dock-card" data-testid="dbg-now">
            <div className="small muted">What happened</div>
            <p className="dbg-what"><b>{describe(cur, inst)}</b></p>
            {d.policy && <p className="small muted">Policy: {Object.entries(d.policy).map(([k, v]) => `${k} ${JSON.stringify(v)}`).join(', ')}</p>}
          </div>
          {(decisions || d.findings?.length || d.rulesFired?.length) && (
            <div className="dock-card" data-testid="dbg-decisions">
              <div className="small muted">Decisions and rules</div>
              {decisions && <table className="grid compact"><tbody>{Object.entries<any>(decisions).map(([k, v]) => <tr key={k}><th className="mono small">{k}</th><td className="mono small">{JSON.stringify(v)}</td></tr>)}</tbody></table>}
              {d.findings?.length > 0 && <ul className="small">{d.findings.map((f: any, n: number) => <li key={n}><Badge tone={f.severity === 'reject' ? 'fail' : 'warn'}>{f.severity}</Badge> <code>{f.reasonCode}</code> at <code>{f.path}</code>{f.message ? `: ${f.message}` : ''}{f.rule ? <span className="muted"> ({f.rule})</span> : null}</li>)}</ul>}
              {d.findings && !d.findings.length && <p className="small">No finding: every rule passed.</p>}
              {d.rulesFired?.length > 0 && <p className="small muted">Rules and tables that ran: {d.rulesFired.map((r: string) => <code key={r}>{r} </code>)}</p>}
            </div>
          )}
          <div className="dock-card" data-testid="dbg-changed">
            <div className="small muted">Data changed by this event</div>
            {delta.length ? (
              <table className="grid compact">
                <thead><tr><th>Path</th><th>Before</th><th>After</th></tr></thead>
                <tbody>{delta.slice(0, 60).map((c, n) => <tr key={n}><td className="mono small">{c.path}</td><td className="mono small muted">{c.before ?? '(none)'}</td><td className="mono small dbg-changed">{c.after ?? '(removed)'}</td></tr>)}</tbody>
              </table>
            ) : <p className="small muted">Nothing changed.</p>}
            {delta.length > 60 && <p className="small muted">And {delta.length - 60} more.</p>}
          </div>
          <div className="dock-card" data-testid="dbg-data">
            <div className="small muted">All the data now{inst ? ` (${inst.workflow})` : ''}</div>
            {!inst ? <p className="small muted">The front door holds no workflow data: the message goes to the instance it belongs to. Step on to see that instance.</p>
              : now.omitted ? <p className="small muted">This run is large: the data after this event was not kept. Debug a smaller sample to see it.</p>
              : now.data ? <div className="mono small dbg-data"><Tree key={cur.i} v={now.data} path="" changed={changedSet} /></div>
              : <p className="small muted">No data yet.</p>}
          </div>
        </div>
      </div>

      <details className="dbg-events">
        <summary className="small">The whole run, event by event ({stops.length})</summary>
        <ol className="dbg-list">
          {stops.map((e, n) => (
            <li key={e.i} className={n === p ? 'dbg-here' : ''}>
              <button className="link small" onClick={() => go(n)}>{fmt(e.at)} · {byId.get(e.instance)?.workflow?.split('.').slice(-2, -1)[0] ?? e.instance} · {describe(e, byId.get(e.instance))}</button>
            </li>
          ))}
        </ol>
      </details>
    </Section>
  );
}

export function DebugHint(): ReactNode {
  return <p className="muted small">Debug step by step runs the same thing, keeps the data after every event, and opens the debugger: step forwards and back, into a leg or hook, and to breakpoints.</p>;
}
