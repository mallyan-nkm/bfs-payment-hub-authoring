import type { ReactNode } from 'react';
import type { Issue } from '../api';

// A structured outline of a workflow or hook: the DSL as nested blocks (Part 5.3).
// Steps with issues are marked; clicking a step selects it.

const KINDS = ['call', 'switch', 'parallel', 'forEach', 'loop', 'await', 'wait', 'slot', 'use', 'try', 'hook', 'end'] as const;

export function stepKind(st: any): string {
  if (!st || typeof st !== 'object') return 'unknown';
  return KINDS.find((k) => k in st) ?? (('continue' in st) ? 'continue' : Object.keys(st)[0] ?? 'unknown');
}

export function stepLabel(st: any): string {
  if (!st || typeof st !== 'object') return String(st);
  return st.id ?? st.call ?? st.slot ?? st.hook ?? st.use ?? Object.keys(st)[0];
}

interface Props {
  doc: any;
  issues?: Issue[];
  selected?: string;
  onSelect?: (step: string) => void;
}

export function Outline({ doc, issues = [], selected, onSelect }: Props) {
  if (!doc || typeof doc !== 'object') return <p className="muted">Nothing to outline.</p>;
  const byStep = new Map<string, Issue[]>();
  for (const i of issues) if (i.step) byStep.set(i.step, [...(byStep.get(i.step) ?? []), i]);
  const ctx = { byStep, selected, onSelect };
  const general = issues.filter((i) => !i.step);
  return (
    <div className="outline">
      <div className="ol-head">
        <span className="ol-kind k-doc">{doc.kind ?? 'document'}{doc.wfKind ? ` · ${doc.wfKind}` : ''}{doc.type && doc.kind === 'hook' ? ` · ${doc.type}` : ''}</span>
        <span className="mono">{doc.name}</span>
        {doc.release && <span className="muted mono">@{String(doc.release)}</span>}
        {general.length > 0 && <span className="ol-flag" title={general.map((i) => `${i.code}: ${i.message}`).join('\n')}>{general.length} general</span>}
      </div>
      <Interface doc={doc} />
      {doc.deadline && (
        <Group title={<><span className="ol-kind k-deadline">deadline</span> after <code>{String(doc.deadline.after)}</code></>}>
          <Steps steps={doc.deadline.do} ctx={ctx} />
        </Group>
      )}
      <Group title={<span className="ol-kind k-do">do</span>}>
        <Steps steps={doc.do} ctx={ctx} />
      </Group>
      {doc.finally && (
        <Group title={<span className="ol-kind k-finally">finally</span>}>
          <Steps steps={doc.finally} ctx={ctx} />
        </Group>
      )}
    </div>
  );
}

function Interface({ doc }: { doc: any }) {
  const rows: ReactNode[] = [];
  if (doc.implements) rows.push(<span key="impl">implements {doc.implements.slot ? <>slot <code>{doc.implements.slot}</code></> : doc.implements.hookPoint ? <>hook point <code>{doc.implements.hookPoint}</code></> : null} of <code>{[].concat(doc.implements.of ?? []).join(', ')}</code></span>);
  if (doc.entry?.reads) rows.push(<span key="in">reads <code>{doc.entry.reads.join(', ')}</code></span>);
  if (doc.exit?.outcomes) rows.push(<span key="out">outcomes <code>{doc.exit.outcomes.join(', ')}</code></span>);
  if (doc.outcomes && doc.kind === 'hook') rows.push(<span key="hout">outcomes <code>{[].concat(doc.outcomes).map((o: any) => (typeof o === 'string' ? o : o.name)).join(', ')}</code></span>);
  if (doc.slots?.length) rows.push(<span key="slots">slots <code>{doc.slots.map((s: any) => s.name).join(', ')}</code></span>);
  if (doc.hookPoints?.length) rows.push(<span key="hp">hook points <code>{doc.hookPoints.map((s: any) => `${s.name} (${s.type})`).join(', ')}</code></span>);
  if (doc.signals?.length) rows.push(<span key="sig">signals <code>{doc.signals.join(', ')}</code></span>);
  if (!rows.length) return null;
  return <div className="ol-iface">{rows}</div>;
}

function Group({ title, children }: { title: ReactNode; children: ReactNode }) {
  return (
    <div className="ol-group">
      <div className="ol-group-title">{title}</div>
      {children}
    </div>
  );
}

interface Ctx { byStep: Map<string, Issue[]>; selected?: string; onSelect?: (s: string) => void }

function Steps({ steps, ctx }: { steps: any; ctx: Ctx }) {
  if (!Array.isArray(steps)) {
    if (steps === undefined || steps === null) return <p className="muted small">No steps.</p>;
    return <div className="ol-step bad">Steps must be a list.</div>;
  }
  if (!steps.length) return <p className="muted small">No steps.</p>;
  return (
    <ol className="ol-steps">
      {steps.map((st, i) => (
        <li key={i}><Step st={st} ctx={ctx} /></li>
      ))}
    </ol>
  );
}

function Action({ a, ctx }: { a: any; ctx: Ctx }) {
  if (Array.isArray(a)) return <Steps steps={a} ctx={ctx} />;
  if (a && typeof a === 'object') {
    if ('end' in a) return <span className="ol-action"><span className="ol-kind k-end">end</span> <code>{String(a.end)}</code>{a.compensate && <span className="ol-tag">compensate {String(a.compensate)}</span>}</span>;
    if ('continue' in a) return <span className="ol-action"><span className="ol-kind k-continue">continue</span></span>;
    return <Steps steps={[a]} ctx={ctx} />;
  }
  return <code>{String(a)}</code>;
}

function Branch({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="ol-branch">
      <div className="ol-branch-label">{label}</div>
      <div className="ol-branch-body">{children}</div>
    </div>
  );
}

function Step({ st, ctx }: { st: any; ctx: Ctx }) {
  const kind = stepKind(st);
  const label = stepLabel(st);
  const issues = ctx.byStep.get(label) ?? (st?.id ? ctx.byStep.get(st.id) : undefined) ?? [];
  const hasErr = issues.some((i) => i.severity === 'error');
  const cls = ['ol-step', `s-${kind}`, issues.length ? (hasErr ? 'has-error' : 'has-warning') : '', ctx.selected && ctx.selected === label ? 'selected' : ''].filter(Boolean).join(' ');
  let head: ReactNode = null;
  let body: ReactNode = null;
  switch (kind) {
    case 'call':
      head = <><code className="strong">{st.call}</code>{st.level && <span className="ol-tag">level {st.level}</span>}{st.policy && <span className="ol-tag" title={JSON.stringify(st.policy)}>policy override</span>}</>;
      break;
    case 'slot':
      head = <>slot <code className="strong">{st.slot}</code></>;
      break;
    case 'use':
      head = <>segment <code className="strong">{st.use}</code></>;
      break;
    case 'hook':
      head = <>hook point <code className="strong">{st.hook}</code></>;
      break;
    case 'wait':
      head = <>timer <code>{String(st.wait)}</code></>;
      break;
    case 'end':
      head = <><code className="strong">{String(st.end)}</code>{st.compensate && <span className="ol-tag">compensate {String(st.compensate)}</span>}</>;
      break;
    case 'await': {
      const a = st.await ?? {};
      head = <>message <code className="strong">{a.message}</code> by <code>{a.correlateBy}</code>, timeout <code>{String(a.timeout)}</code>{a.calendar && <span className="ol-tag">calendar {a.calendar}</span>}</>;
      body = a.onTimeout !== undefined && <Branch label={<span className="ol-case timeout">on timeout</span>}><Action a={a.onTimeout} ctx={ctx} /></Branch>;
      break;
    }
    case 'switch': {
      const s = st.switch ?? {};
      const by = s.by ? (s.by.outcomeOf ? `outcome of ${s.by.outcomeOf}` : s.by.decision ? `decision ${s.by.decision}` : JSON.stringify(s.by)) : '';
      head = <>by <code>{by}</code></>;
      body = (
        <>
          {Object.entries<any>(s.cases ?? {}).map(([k, v]) => <Branch key={k} label={<span className="ol-case">case {k}</span>}><Action a={v} ctx={ctx} /></Branch>)}
          {s.default !== undefined && <Branch label={<span className="ol-case">default</span>}><Action a={s.default} ctx={ctx} /></Branch>}
        </>
      );
      break;
    }
    case 'parallel': {
      const p = st.parallel ?? {};
      head = <>join <code>{p.join ?? 'all'}</code>, {(p.branches ?? []).length} branches</>;
      body = (p.branches ?? []).map((b: any, i: number) => <Branch key={i} label={<span className="ol-case">branch {i + 1}</span>}><Action a={b} ctx={ctx} /></Branch>);
      break;
    }
    case 'forEach': {
      const f = st.forEach ?? {};
      head = <>in <code>{f.in}</code>{f.concurrency && <span className="ol-tag">concurrency {f.concurrency}</span>}{f.onItemFailure && <span className="ol-tag">on item failure {f.onItemFailure}</span>}</>;
      body = <Branch label={<span className="ol-case">each item</span>}><Steps steps={f.do} ctx={ctx} /></Branch>;
      break;
    }
    case 'loop': {
      const l = st.loop ?? {};
      const until = l.until ? (l.until.outcomeOf ? `outcome of ${l.until.outcomeOf} in ${[].concat(l.until.in ?? []).join(', ')}` : JSON.stringify(l.until)) : '';
      head = <>max <code>{String(l.max)}</code>{l.every && <>, every <code>{String(l.every)}</code></>}{until && <>, until <code>{until}</code></>}</>;
      body = <Branch label={<span className="ol-case">repeat</span>}><Steps steps={l.do} ctx={ctx} /></Branch>;
      break;
    }
    case 'try':
      head = <>{st.compensate && <span className="ol-tag">compensate {String(st.compensate)}</span>}</>;
      body = (
        <>
          <Branch label={<span className="ol-case">scope</span>}><Steps steps={st.try} ctx={ctx} /></Branch>
          {st.catch !== undefined && (
            typeof st.catch === 'object' && !Array.isArray(st.catch) && !('end' in st.catch) && !('continue' in st.catch) && !stepKindIsStep(st.catch)
              ? Object.entries<any>(st.catch).map(([k, v]) => <Branch key={k} label={<span className="ol-case timeout">catch {k}</span>}><Action a={v} ctx={ctx} /></Branch>)
              : <Branch label={<span className="ol-case timeout">catch</span>}><Action a={st.catch} ctx={ctx} /></Branch>
          )}
        </>
      );
      break;
    default:
      head = <code>{JSON.stringify(st)}</code>;
  }
  const handle = st && typeof st === 'object' && st.handle && typeof st.handle === 'object' ? Object.entries<any>(st.handle) : [];
  return (
    <div className={cls}>
      <div className="ol-line" onClick={(e) => { e.stopPropagation(); ctx.onSelect?.(label); }} title={issues.map((i) => `${i.code}: ${i.message}`).join('\n') || undefined}>
        <span className={`ol-kind k-${kind}`}>{kind}</span>
        {st?.id && <span className="ol-id">{st.id}</span>}
        <span className="ol-main">{head}</span>
        {issues.length > 0 && <span className="ol-flag">{issues.map((i) => i.code).join(' ')}</span>}
      </div>
      {(body || handle.length > 0) && (
        <div className="ol-children">
          {body}
          {handle.map(([k, v]) => <Branch key={k} label={<span className="ol-case outcome">{k}</span>}><Action a={v} ctx={ctx} /></Branch>)}
        </div>
      )}
    </div>
  );
}

function stepKindIsStep(x: any) {
  return KINDS.some((k) => k in x);
}
