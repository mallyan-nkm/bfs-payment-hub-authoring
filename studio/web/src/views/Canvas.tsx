import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { get, type Issue } from '../api';
import { Badge } from '../ui';
import { Sel, Txt, StringList, useCatalogue } from '../authoring';
import { isComposite, prefixSteps } from '../expand';
import { Outline, stepKind, stepLabel } from './Outline';

// The canvas (C14-S2, C14-S3, C14-S6): compose a workflow from steps without writing YAML.
// Each change writes the document, the YAML editor follows, and the checks run on every edit.
// A step that calls a composite activity is one block; it opens read-only to the steps it expands to.

type Path = (string | number)[];

export function getIn(o: any, p: Path): any {
  return p.reduce((x, k) => (x === undefined || x === null ? undefined : x[k]), o);
}

/** Set a value at a path without changing the input. Undefined removes an object key. */
export function setIn(o: any, p: Path, v: any): any {
  if (!p.length) return v;
  const [k, ...rest] = p;
  const base = Array.isArray(o) ? [...o] : { ...(o ?? {}) };
  const next = setIn(base[k as any], rest, v);
  if (next === undefined && !Array.isArray(base)) delete (base as any)[k];
  else (base as any)[k] = next;
  return base;
}

const BASE_KINDS = ['call', 'switch', 'parallel', 'forEach', 'loop', 'wait', 'slot', 'use', 'try', 'hook', 'end'] as const;
const SUBFLOW_KINDS = ['await', 'return'] as const;

interface Ctx {
  doc: any;
  set: (p: Path, v: any) => void;
  docs: Record<string, any>;
  byStep: Map<string, Issue[]>;
  selected?: string;
  onSelect: (s: string) => void;
  clip?: any;
  setClip: (s: any) => void;
  subflow: boolean;
  ids: string[];
}
const C = createContext<Ctx>(null as any);

function newId(kind: string, ids: string[]) {
  let n = 1;
  while (ids.includes(`${kind}-${n}`)) n++;
  return `${kind}-${n}`;
}

function template(kind: string, doc: any, ids: string[]): any {
  const exits: string[] = doc?.exit?.outcomes ?? [];
  switch (kind) {
    case 'call': return { id: newId('step', ids), call: '' };
    case 'switch': return { switch: { by: { outcomeOf: ids[ids.length - 1] ?? '' }, cases: { case1: [] } } };
    case 'parallel': return { parallel: { branches: [[], []], join: 'all' } };
    case 'forEach': return { forEach: { in: 'items[]', do: [] } };
    case 'loop': return { loop: { max: 3, do: [] } };
    case 'await': return { id: newId('await', ids), await: { message: '', correlateBy: 'header.paymentId', onTimeout: { return: exits.find((o) => o !== exits[0]) ?? exits[0] ?? '' } } };
    case 'wait': return { wait: 'PT1M' };
    case 'slot': return { id: newId('slot', ids), slot: doc?.slots?.[0]?.name ?? '' };
    case 'use': return { use: '' };
    case 'try': return { try: [], catch: [{ error: 'BusinessReject', do: { end: exits[1] ?? 'failed' } }] };
    case 'hook': return { hook: doc?.hookPoints?.[0]?.name ?? '' };
    case 'end': return { end: exits[0] ?? 'done' };
    case 'return': return { return: exits[0] ?? '' };
  }
  return {};
}

export interface CanvasProps {
  doc: any;
  onChange: (doc: any) => void;
  issues: Issue[];
  docs: Record<string, any>;
  selected?: string;
  onSelect: (s: string) => void;
}

export function Canvas({ doc, onChange, issues, docs, selected, onSelect }: CanvasProps) {
  const [clip, setClip] = useState<any>();
  const byStep = useMemo(() => {
    const m = new Map<string, Issue[]>();
    for (const i of issues) if (i.step) m.set(i.step, [...(m.get(i.step) ?? []), i]);
    return m;
  }, [issues]);
  const ids = useMemo(() => collectIds(doc), [doc]);
  const ctx: Ctx = { doc, set: (p, v) => onChange(setIn(doc, p, v)), docs, byStep, selected, onSelect, clip, setClip, subflow: doc?.wfKind === 'subflow', ids };
  const general = issues.filter((i) => !i.step);
  if (!doc || typeof doc !== 'object') return <p className="muted">Nothing to compose.</p>;
  return (
    <C.Provider value={ctx}>
      <div className="canvas">
        <InterfaceEditor />
        {general.length > 0 && (
          <div className="cv-general small">
            {general.map((i, n) => <div key={n} className={i.severity === 'error' ? 'fail-text' : 'warn-text'}><b>{i.code}</b> {i.message}</div>)}
          </div>
        )}
        {clip && (
          <div className="alert warn small cv-clip">
            Holding a <b>{stepKind(clip)}</b> step <code>{stepLabel(clip)}</code>. Use "Paste here" on any lane to put it back. <button className="link" onClick={() => setClip(undefined)}>Discard it</button>
          </div>
        )}
        {doc.deadline ? (
          <Lane title={<><span className="ol-kind k-deadline">deadline</span> after <Txt value={doc.deadline.after} onChange={(v) => ctx.set(['deadline', 'after'], v)} mono size={8} label="Deadline after" /></>} onRemove={() => ctx.set(['deadline'], undefined)}>
            <StepList path={['deadline', 'do']} />
          </Lane>
        ) : null}
        <Lane title={<span className="ol-kind k-do">do</span>}>
          <StepList path={['do']} />
        </Lane>
        {doc.finally ? (
          <Lane title={<span className="ol-kind k-finally">finally</span>} onRemove={() => ctx.set(['finally'], undefined)}>
            <StepList path={['finally']} />
          </Lane>
        ) : null}
        <div className="row-actions">
          {!doc.deadline && doc.wfKind !== 'subflow' && <button onClick={() => ctx.set(['deadline'], { after: 'PT8S', do: [] })}>Add a deadline lane</button>}
          {!doc.finally && doc.wfKind !== 'subflow' && <button onClick={() => ctx.set(['finally'], [])}>Add a finally lane</button>}
        </div>
      </div>
    </C.Provider>
  );
}

function collectIds(doc: any): string[] {
  const out: string[] = [];
  const walk = (x: any) => {
    if (Array.isArray(x)) x.forEach(walk);
    else if (x && typeof x === 'object') {
      if (typeof x.id === 'string' && ('call' in x || 'slot' in x || 'await' in x)) out.push(x.id);
      Object.values(x).forEach(walk);
    }
  };
  walk([doc?.do, doc?.finally, doc?.deadline?.do]);
  return out;
}

function Lane({ title, children, onRemove }: { title: ReactNode; children: ReactNode; onRemove?: () => void }) {
  return (
    <div className="cv-lane">
      <div className="cv-lane-head">
        {title}
        {onRemove && <button className="link danger small" onClick={onRemove}>Remove the lane</button>}
      </div>
      {children}
    </div>
  );
}

/** The interface of the workflow: exit outcomes, slots, hook points and signals (C14-S3). */
function InterfaceEditor() {
  const { doc, set } = useContext(C);
  const [open, setOpen] = useState(false);
  return (
    <details className="cv-iface" open={open} onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary>
        <span className="ol-kind k-doc">{doc.kind}{doc.wfKind ? ` · ${doc.wfKind}` : ''}</span> Interface: outcomes <code>{(doc.exit?.outcomes ?? []).join(', ') || 'none'}</code>
        {doc.slots?.length ? <>, slots <code>{doc.slots.map((s: any) => s.name).join(', ')}</code></> : null}
        {doc.hookPoints?.length ? <>, hook points <code>{doc.hookPoints.map((s: any) => s.name).join(', ')}</code></> : null}
      </summary>
      {open && (
        <div className="cv-iface-body">
          <div className="form-grid">
            <label className="field wide"><span>Exit outcomes</span><StringList value={doc.exit?.outcomes} onChange={(v) => set(['exit', 'outcomes'], v)} placeholder="Add an outcome" /></label>
            {doc.wfKind !== 'subflow' && <label className="field wide"><span>Signals</span><StringList value={doc.signals} onChange={(v) => set(['signals'], v)} placeholder="pause, resume, cancel, re-screen" /></label>}
            {doc.wfKind === 'subflow' && <label className="field wide"><span>Implements activity</span><Txt value={doc.implements?.activity} onChange={(v) => set(['implements', 'activity'], v)} mono label="Implements activity" /></label>}
          </div>
          {doc.wfKind !== 'subflow' && (
            <>
              <h4>Slots</h4>
              {(doc.slots ?? []).map((s: any, i: number) => (
                <div key={i} className="bench-row">
                  <Txt value={s.name} onChange={(v) => set(['slots', i, 'name'], v)} mono label="Slot name" placeholder="name" />
                  <span className="small muted">outcomes</span>
                  <StringList value={s.contract?.outcomes} onChange={(v) => set(['slots', i, 'contract', 'outcomes'], v)} placeholder="outcome" />
                  <Sel value={s.contract?.primaryOutcome} onChange={(v) => set(['slots', i, 'contract', 'primaryOutcome'], v)} options={s.contract?.outcomes ?? []} label="Primary outcome" blank="primary outcome" />
                  <button className="link danger" onClick={() => set(['slots'], (doc.slots as any[]).filter((_, n) => n !== i).length ? (doc.slots as any[]).filter((_, n) => n !== i) : undefined)}>Remove</button>
                </div>
              ))}
              <button onClick={() => set(['slots'], [...(doc.slots ?? []), { name: 'new-slot', contract: { reads: [], writes: [], outcomes: ['done'], primaryOutcome: 'done' } }])}>Declare a slot</button>
              <h4>Hook points</h4>
              {(doc.hookPoints ?? []).map((h: any, i: number) => (
                <div key={i} className="bench-row">
                  <Txt value={h.name} onChange={(v) => set(['hookPoints', i, 'name'], v)} mono label="Hook point name" placeholder="name" />
                  <Sel value={h.type} onChange={(v) => set(['hookPoints', i, 'type'], v)} options={['transform', 'gate', 'notify']} label="Hook point type" />
                  <span className="small muted">writes</span>
                  <StringList value={h.contract?.writes} onChange={(v) => set(['hookPoints', i, 'contract', 'writes'], v)} placeholder="path" />
                  <button className="link danger" onClick={() => set(['hookPoints'], (doc.hookPoints as any[]).filter((_, n) => n !== i).length ? (doc.hookPoints as any[]).filter((_, n) => n !== i) : undefined)}>Remove</button>
                </div>
              ))}
              <button onClick={() => set(['hookPoints'], [...(doc.hookPoints ?? []), { name: 'new-hook-point', type: 'notify', contract: { reads: ['header'] } }])}>Declare a hook point</button>
            </>
          )}
        </div>
      )}
    </details>
  );
}

function StepList({ path }: { path: Path }) {
  const ctx = useContext(C);
  const steps = getIn(ctx.doc, path);
  if (steps !== undefined && !Array.isArray(steps)) return <div className="ol-step bad">Steps must be a list.</div>;
  const list: any[] = steps ?? [];
  const insert = (at: number, st: any) => {
    ctx.set(path, [...list.slice(0, at), st, ...list.slice(at)]);
    ctx.onSelect(stepLabel(st));
  };
  return (
    <ol className="cv-steps">
      {list.map((st, i) => (
        <li key={i}>
          <Inserter onInsert={(st) => insert(i, st)} />
          <StepCard st={st} path={[...path, i]} index={i} count={list.length}
            onMove={(d) => {
              const j = i + d;
              if (j < 0 || j >= list.length) return;
              const next = [...list];
              [next[i], next[j]] = [next[j], next[i]];
              ctx.set(path, next);
            }}
            onDelete={() => ctx.set(path, list.filter((_, n) => n !== i))}
            onCut={() => {
              ctx.setClip(st);
              ctx.set(path, list.filter((_, n) => n !== i));
            }}
          />
        </li>
      ))}
      <li><Inserter onInsert={(st) => insert(list.length, st)} last empty={!list.length} /></li>
    </ol>
  );
}

function Inserter({ onInsert, last, empty }: { onInsert: (st: any) => void; last?: boolean; empty?: boolean }) {
  const ctx = useContext(C);
  const [open, setOpen] = useState(false);
  const kinds = [...BASE_KINDS, ...(ctx.subflow ? SUBFLOW_KINDS : [])];
  if (!open) {
    return (
      <div className={last ? 'cv-insert last' : 'cv-insert'}>
        <button className="cv-plus" onClick={() => setOpen(true)} title="Insert a step here" aria-label="Insert a step here">+{last ? (empty ? ' Add the first step' : ' Add a step') : ''}</button>
        {ctx.clip && <button className="link small" onClick={() => { onInsert(ctx.clip); ctx.setClip(undefined); }}>Paste here</button>}
      </div>
    );
  }
  return (
    <div className="cv-palette" role="menu">
      {kinds.map((k) => (
        <button key={k} className={`cv-pal k-${k}`} onClick={() => { onInsert(template(k, ctx.doc, ctx.ids)); setOpen(false); }}>{k}</button>
      ))}
      <button className="link" onClick={() => setOpen(false)}>Cancel</button>
      {!ctx.subflow && <span className="muted small">await and return are offered only in a subflow (L31).</span>}
    </div>
  );
}

function StepCard({ st, path, index, count, onMove, onDelete, onCut }: { st: any; path: Path; index: number; count: number; onMove: (d: number) => void; onDelete: () => void; onCut: () => void }) {
  const ctx = useContext(C);
  const kind = stepKind(st);
  const label = stepLabel(st);
  const issues = ctx.byStep.get(label) ?? (st?.id ? ctx.byStep.get(st.id) : undefined) ?? [];
  const inner = [...ctx.byStep.entries()].filter(([k]) => st?.id && k.startsWith(`${st.id}.`)).flatMap(([, v]) => v);
  const hasErr = [...issues, ...inner].some((i) => i.severity === 'error');
  const sel = ctx.selected === label;
  const act = st?.call ? ctx.docs[st.call] : undefined;
  const composite = isComposite(act);
  const cls = ['cv-step', `s-${kind}`, issues.length || inner.length ? (hasErr ? 'has-error' : 'has-warning') : '', sel ? 'selected' : '', composite ? 'composite' : ''].filter(Boolean).join(' ');
  const set = (rel: Path, v: any) => ctx.set([...path, ...rel], v);
  return (
    <div className={cls} data-step={label}>
      <div className="cv-line" onClick={(e) => { e.stopPropagation(); ctx.onSelect(label); }}>
        <span className={`ol-kind k-${kind}`}>{kind}</span>
        {composite && <Badge tone="info" title="A composite activity: one block to the designer; its subflow sends, waits and reads">composite</Badge>}
        {st?.id && <span className="ol-id">{st.id}</span>}
        <span className="ol-main"><Summary st={st} kind={kind} /></span>
        <span className="cv-tools" onClick={(e) => e.stopPropagation()}>
          <button className="link" disabled={index === 0} onClick={() => onMove(-1)} title="Move up" aria-label={`Move ${label} up`}>↑</button>
          <button className="link" disabled={index === count - 1} onClick={() => onMove(1)} title="Move down" aria-label={`Move ${label} down`}>↓</button>
          <button className="link" onClick={onCut} title="Cut, to paste in another place" aria-label={`Cut ${label}`}>cut</button>
          <button className="link danger" onClick={onDelete} title="Delete the step" aria-label={`Delete ${label}`}>delete</button>
        </span>
      </div>
      {issues.length > 0 && (
        <ul className="cv-issues">
          {issues.map((i, n) => <li key={n} className={i.severity === 'error' ? 'fail-text' : 'warn-text'}><b>{i.code}</b> {i.message}</li>)}
        </ul>
      )}
      {inner.length > 0 && (
        <ul className="cv-issues">
          {inner.map((i, n) => <li key={n} className={i.severity === 'error' ? 'fail-text' : 'warn-text'}><b>{i.code}</b> at the inner step <code>{i.step}</code>: {i.message}</li>)}
        </ul>
      )}
      {sel && <div className="cv-edit" onClick={(e) => e.stopPropagation()}><StepEditor st={st} kind={kind} set={set} /></div>}
      <Children st={st} kind={kind} path={path} />
    </div>
  );
}

function Summary({ st, kind }: { st: any; kind: string }) {
  switch (kind) {
    case 'call': return st.call ? <code className="strong">{st.call}</code> : <span className="fail-text">choose a block</span>;
    case 'slot': return <>slot <code className="strong">{st.slot}</code></>;
    case 'use': return st.use ? <>segment <code className="strong">{st.use}</code></> : <span className="fail-text">choose a segment</span>;
    case 'hook': return <>hook point <code className="strong">{st.hook}</code></>;
    case 'wait': return <>timer <code>{String(st.wait)}</code></>;
    case 'end': return <><code className="strong">{String(st.end)}</code>{st.compensate && <span className="ol-tag">compensate all</span>}</>;
    case 'return': return <>return <code className="strong">{String(st.return)}</code></>;
    case 'await': return <>message <code className="strong">{st.await?.message || '?'}</code> by <code>{st.await?.correlateBy}</code>{st.await?.timeout ? <>, timeout <code>{st.await.timeout}</code></> : <span className="ol-tag">reply time of the calling step</span>}</>;
    case 'switch': {
      const by = st.switch?.by ?? {};
      return <>by <code>{by.outcomeOf ? `outcome of ${by.outcomeOf}` : by.decision ? `decision ${by.decision}` : '?'}</code></>;
    }
    case 'parallel': return <>join all, {(st.parallel?.branches ?? []).length} branches</>;
    case 'forEach': return <>in <code>{st.forEach?.in}</code></>;
    case 'loop': return <>max <code>{String(st.loop?.max)}</code>{st.loop?.every && <>, every <code>{st.loop.every}</code></>}</>;
    case 'try': return <>{(Array.isArray(st.catch) ? st.catch : []).length} catch{st.compensate && <span className="ol-tag">compensate all</span>}</>;
  }
  return <code>{JSON.stringify(st)}</code>;
}

/** The step lists inside a step, always shown, each with its own insert points. */
function Children({ st, kind, path }: { st: any; kind: string; path: Path }) {
  const ctx = useContext(C);
  const parts: ReactNode[] = [];
  if (kind === 'switch') {
    for (const k of Object.keys(st.switch?.cases ?? {})) parts.push(<Branch key={`c-${k}`} label={<span className="ol-case">case {k}</span>}><StepList path={[...path, 'switch', 'cases', k]} /></Branch>);
    if (st.switch && 'default' in st.switch) parts.push(<Branch key="def" label={<span className="ol-case">default</span>}><StepList path={[...path, 'switch', 'default']} /></Branch>);
  }
  if (kind === 'parallel') (st.parallel?.branches ?? []).forEach((_: any, i: number) => parts.push(<Branch key={`b-${i}`} label={<span className="ol-case">branch {i + 1}</span>}><StepList path={[...path, 'parallel', 'branches', i]} /></Branch>));
  if (kind === 'forEach') parts.push(<Branch key="fe" label={<span className="ol-case">each item</span>}><StepList path={[...path, 'forEach', 'do']} /></Branch>);
  if (kind === 'loop') parts.push(<Branch key="lp" label={<span className="ol-case">repeat</span>}><StepList path={[...path, 'loop', 'do']} /></Branch>);
  if (kind === 'try') {
    parts.push(<Branch key="try" label={<span className="ol-case">scope</span>}><StepList path={[...path, 'try']} /></Branch>);
    (Array.isArray(st.catch) ? st.catch : []).forEach((c: any, i: number) => {
      if (Array.isArray(c?.do)) parts.push(<Branch key={`ct-${i}`} label={<span className="ol-case timeout">catch {c.error}</span>}><StepList path={[...path, 'catch', i, 'do']} /></Branch>);
    });
  }
  if (kind === 'await' && Array.isArray(st.await?.onTimeout)) parts.push(<Branch key="to" label={<span className="ol-case timeout">on timeout</span>}><StepList path={[...path, 'await', 'onTimeout']} /></Branch>);
  for (const [k, v] of Object.entries<any>(st?.handle ?? {})) {
    if (Array.isArray(v)) parts.push(<Branch key={`h-${k}`} label={<span className="ol-case outcome">{k}</span>}><StepList path={[...path, 'handle', k]} /></Branch>);
    else parts.push(<Branch key={`h-${k}`} label={<span className="ol-case outcome">{k}</span>}><ActionText a={v} /></Branch>);
  }
  const act = st?.call ? ctx.docs[st.call] : undefined;
  if (isComposite(act)) parts.push(<CompositeInside key="inside" st={st} act={act} />);
  if (!parts.length) return null;
  return <div className="ol-children">{parts}</div>;
}

function ActionText({ a }: { a: any }) {
  if (a && typeof a === 'object' && 'end' in a) return <span className="ol-action"><span className="ol-kind k-end">end</span> <code>{String(a.end)}</code>{a.compensate && <span className="ol-tag">compensate all</span>}</span>;
  if (a && typeof a === 'object' && 'return' in a) return <span className="ol-action"><span className="ol-kind k-return">return</span> <code>{String(a.return)}</code></span>;
  if (a && typeof a === 'object' && 'continue' in a) return <span className="ol-action"><span className="ol-kind k-continue">continue</span></span>;
  return <code>{JSON.stringify(a)}</code>;
}

function Branch({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="ol-branch">
      <div className="ol-branch-label">{label}</div>
      <div className="ol-branch-body">{children}</div>
    </div>
  );
}

const expandedCache = new Map<string, Promise<any>>();
/** A composite step opened read-only: the steps its subflow expands to, with the calling step's id in front. */
function CompositeInside({ st, act }: { st: any; act: any }) {
  const [open, setOpen] = useState(false);
  const [sub, setSub] = useState<any>();
  const [err, setErr] = useState<string>();
  const name = act.implementation.subflow;
  useEffect(() => {
    if (!open || sub) return;
    if (!expandedCache.has(name)) expandedCache.set(name, get(`/api/expanded/${encodeURIComponent(name)}`));
    expandedCache.get(name)!.then((r) => setSub(r.doc), (e) => setErr(e.message));
  }, [open, name, sub]);
  const prefixed = useMemo(() => {
    if (!sub) return undefined;
    const policy = { ...(act.policy ?? {}), ...(st.policy ?? {}) };
    const inner = prefixSteps(sub.do ?? [], st.id ?? st.call, policy);
    return { kind: 'workflow', wfKind: 'subflow', name, release: sub.release, do: inner };
  }, [sub, st, act, name]);
  return (
    <div className="cv-inside">
      <button className="link small" onClick={() => setOpen((o) => !o)} aria-expanded={open}>{open ? '▾ Hide' : '▸ Show'} the steps inside <code>{st.id ?? st.call}</code> ({name})</button>
      {open && (
        <div className="cv-inside-body">
          {err && <div className="small fail-text">{err}</div>}
          {!prefixed && !err && <span className="muted small">Loading the expansion…</span>}
          {prefixed && (
            <>
              <p className="muted small">Read-only. The subflow belongs to the scheme's owner; change it there and this workflow gets a new pin.</p>
              <Outline doc={prefixed} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ step editors

function StepEditor({ st, kind, set }: { st: any; kind: string; set: (p: Path, v: any) => void }) {
  const ctx = useContext(C);
  const doc = ctx.doc;
  const exits: string[] = doc?.exit?.outcomes ?? [];
  const idField = (
    <label className="field"><span>Step id</span><Txt value={st.id} onChange={(v) => { set(['id'], v); ctx.onSelect(v ?? stepLabel({ ...st, id: undefined })); }} mono label="Step id" /></label>
  );
  switch (kind) {
    case 'call': return <CallEditor st={st} set={set} idField={idField} />;
    case 'slot': {
      const slot = (doc.slots ?? []).find((s: any) => s.name === st.slot);
      const outs: string[] = (slot?.contract?.outcomes ?? []).filter((o: string) => o !== slot?.contract?.primaryOutcome);
      return (
        <>
          <div className="form-grid">
            {idField}
            <label className="field"><span>Slot</span><Sel value={st.slot} onChange={(v) => set(['slot'], v)} options={(doc.slots ?? []).map((s: any) => s.name)} label="Slot" blank="choose" /></label>
          </div>
          <HandleEditor outcomes={outs} primary={slot?.contract?.primaryOutcome} handle={st.handle} set={(v) => set(['handle'], v)} />
        </>
      );
    }
    case 'use': {
      return <label className="field"><span>Segment</span><BlockPicker kind="workflow" filter={(s) => s.type === 'segment'} value={st.use} onPick={(n) => set(['use'], n)} /></label>;
    }
    case 'hook':
      return <label className="field"><span>Hook point</span><Sel value={st.hook} onChange={(v) => set(['hook'], v)} options={(doc.hookPoints ?? []).map((h: any) => h.name)} label="Hook point" blank="choose" /></label>;
    case 'wait':
      return <label className="field"><span>Wait for</span><Txt value={st.wait} onChange={(v) => set(['wait'], v)} mono label="Wait duration" placeholder="PT1M" /></label>;
    case 'end':
      return (
        <div className="form-grid">
          <label className="field"><span>Outcome</span><Txt value={st.end} onChange={(v) => set(['end'], v)} mono label="End outcome" list="cv-exits" /></label>
          <label className="check"><input type="checkbox" checked={!!st.compensate} onChange={(e) => set(['compensate'], e.target.checked ? 'all' : undefined)} /> Compensate all first</label>
          <datalist id="cv-exits">{exits.map((o) => <option key={o} value={o} />)}</datalist>
        </div>
      );
    case 'return':
      return <label className="field"><span>Return the outcome</span><Sel value={st.return} onChange={(v) => set(['return'], v)} options={exits} label="Return outcome" blank="choose" /></label>;
    case 'await': {
      const a = st.await ?? {};
      return (
        <>
          <div className="form-grid">
            {idField}
            <label className="field"><span>Message</span><Txt value={a.message} onChange={(v) => set(['await', 'message'], v)} mono label="Awaited message" placeholder="uae.wps.ack" /></label>
            <label className="field"><span>Correlate by</span><Txt value={a.correlateBy} onChange={(v) => set(['await', 'correlateBy'], v)} mono label="Correlate by" /></label>
            <label className="field"><span>Timeout</span><Txt value={a.timeout} onChange={(v) => set(['await', 'timeout'], v)} mono label="Await timeout" placeholder="the calling step's replyTimeout" /></label>
            <label className="field"><span>Calendar</span><Sel value={a.calendar} onChange={(v) => set(['await', 'calendar'], v)} options={['elapsed', 'business']} label="Calendar" blank="from the calling step" /></label>
          </div>
          <h4>On timeout</h4>
          <ActionEditor value={a.onTimeout} onChange={(v) => set(['await', 'onTimeout'], v)} />
        </>
      );
    }
    case 'switch': {
      const s = st.switch ?? {};
      const byKind = s.by?.decision !== undefined ? 'decision' : 'outcomeOf';
      return (
        <>
          <div className="form-grid">
            <label className="field"><span>Branch by</span><Sel value={byKind} onChange={(v) => set(['switch', 'by'], v === 'decision' ? { decision: '' } : { outcomeOf: ctx.ids[0] ?? '' })} options={[['outcomeOf', 'outcome of a step'], ['decision', 'a decision']]} label="Branch by" /></label>
            {byKind === 'outcomeOf' ? (
              <label className="field"><span>Step</span><Sel value={s.by?.outcomeOf} onChange={(v) => set(['switch', 'by', 'outcomeOf'], v ?? '')} options={ctx.ids} label="Outcome of step" blank="choose" /></label>
            ) : (
              <label className="field"><span>Decision</span><Txt value={s.by?.decision} onChange={(v) => set(['switch', 'by', 'decision'], v ?? '')} mono label="Decision" /></label>
            )}
          </div>
          <h4>Cases</h4>
          <KeyList keys={Object.keys(s.cases ?? {})} onAdd={(k) => set(['switch', 'cases', k], [])} onRemove={(k) => set(['switch', 'cases', k], undefined)} placeholder="case value" />
          <label className="check"><input type="checkbox" checked={'default' in s} onChange={(e) => set(['switch', 'default'], e.target.checked ? [] : undefined)} /> A default branch</label>
        </>
      );
    }
    case 'parallel': {
      const b: any[] = st.parallel?.branches ?? [];
      return (
        <div className="row-actions">
          <button onClick={() => set(['parallel', 'branches'], [...b, []])}>Add a branch</button>
          <button disabled={b.length <= 2} onClick={() => set(['parallel', 'branches'], b.slice(0, -1))}>Remove the last branch</button>
          <span className="muted small">A parallel step has two branches at least and joins all.</span>
        </div>
      );
    }
    case 'forEach': {
      const f = st.forEach ?? {};
      return (
        <div className="form-grid">
          <label className="field"><span>In</span><Txt value={f.in} onChange={(v) => set(['forEach', 'in'], v)} mono label="For each in" /></label>
          <label className="field"><span>Concurrency</span><Txt value={f.concurrency} onChange={(v) => set(['forEach', 'concurrency'], v ? Number(v) : undefined)} label="Concurrency" /></label>
          <label className="field"><span>On item failure</span><Sel value={f.onItemFailure} onChange={(v) => set(['forEach', 'onItemFailure'], v)} options={['continue', 'stop']} label="On item failure" blank="default" /></label>
        </div>
      );
    }
    case 'loop': {
      const l = st.loop ?? {};
      return (
        <div className="form-grid">
          <label className="field"><span>Max</span><Txt value={l.max} onChange={(v) => set(['loop', 'max'], v ? Number(v) : undefined)} label="Loop max" /></label>
          <label className="field"><span>Every</span><Txt value={l.every} onChange={(v) => set(['loop', 'every'], v)} mono label="Loop every" placeholder="PT2S" /></label>
          <label className="field"><span>Until the outcome of</span><Sel value={l.until?.outcomeOf} onChange={(v) => set(['loop', 'until'], v ? { outcomeOf: v, in: l.until?.in ?? [] } : undefined)} options={ctx.ids} label="Until outcome of" blank="no condition" /></label>
          {l.until && <label className="field wide"><span>is one of</span><StringList value={l.until.in} onChange={(v) => set(['loop', 'until', 'in'], v ?? [])} placeholder="outcome" /></label>}
        </div>
      );
    }
    case 'try': {
      const cs: any[] = Array.isArray(st.catch) ? st.catch : [];
      return (
        <>
          <label className="check"><input type="checkbox" checked={!!st.compensate} onChange={(e) => set(['compensate'], e.target.checked ? 'all' : undefined)} /> Compensate all on a caught error</label>
          <h4>Catch</h4>
          {cs.map((c, i) => (
            <div key={i} className="cv-catch">
              <Txt value={c.error} onChange={(v) => set(['catch', i, 'error'], v ?? '')} mono label="Error name" placeholder="TransientError" />
              <ActionEditor value={c.do} onChange={(v) => set(['catch', i, 'do'], v)} />
              <button className="link danger" onClick={() => set(['catch'], cs.filter((_, n) => n !== i))}>Remove</button>
            </div>
          ))}
          <button onClick={() => set(['catch'], [...cs, { error: 'BusinessReject', do: { continue: true } }])}>Add a catch</button>
        </>
      );
    }
  }
  return <p className="muted small">Edit this step in the YAML.</p>;
}

function KeyList({ keys, onAdd, onRemove, placeholder }: { keys: string[]; onAdd: (k: string) => void; onRemove: (k: string) => void; placeholder: string }) {
  const [t, setT] = useState('');
  return (
    <div className="chips">
      {keys.map((k) => <span key={k} className="chip mono">{k} <button className="link danger" onClick={() => onRemove(k)} aria-label={`Remove ${k}`}>×</button></span>)}
      <span className="inline-form"><input value={t} onChange={(e) => setT(e.target.value)} placeholder={placeholder} className="mono" aria-label={placeholder} /><button disabled={!t.trim() || keys.includes(t.trim())} onClick={() => { onAdd(t.trim()); setT(''); }}>Add</button></span>
    </div>
  );
}

/** Pick a block from the catalogue by search (F-13). */
function BlockPicker({ kind = 'activity', value, onPick, filter }: { kind?: string; value?: string; onPick: (name: string) => void; filter?: (s: any) => boolean }) {
  const items = useCatalogue(kind);
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(!value);
  const hits = useMemo(() => {
    const t = q.toLowerCase();
    return items.filter((s) => s.status === 'published' && (!filter || filter(s)) && (!t || `${s.name} ${s.title} ${s.type}`.toLowerCase().includes(t))).slice(0, 14);
  }, [items, q, filter]);
  if (!open) {
    return (
      <span className="inline-form">
        <code className="strong break">{value}</code>
        <button onClick={() => setOpen(true)}>Change</button>
      </span>
    );
  }
  return (
    <div className="picker">
      <input type="search" autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder={`Search the ${kind}s by name, title or type`} aria-label="Search the catalogue" />
      <ul className="picker-list">
        {hits.map((s) => (
          <li key={s.key}>
            <button className="picker-item" onClick={() => { onPick(s.name); setOpen(false); setQ(''); }}>
              <span className="mono">{s.name}</span> <span className="muted small">{s.type} · {s.title}</span>
            </button>
          </li>
        ))}
        {!hits.length && <li className="muted small">No match.</li>}
      </ul>
      {value && <button className="link small" onClick={() => setOpen(false)}>Keep {value}</button>}
    </div>
  );
}

function CallEditor({ st, set, idField }: { st: any; set: (p: Path, v: any) => void; idField: ReactNode }) {
  const ctx = useContext(C);
  const act = st.call ? ctx.docs[st.call] : undefined;
  const composite = isComposite(act);
  const outs: any[] = act?.outcomes ?? [];
  const primary = outs.find((o) => o.primary)?.name;
  const pol = st.policy ?? {};
  const setPol = (k: string, v: any) => {
    const next = { ...pol, [k]: v };
    if (v === undefined) delete next[k];
    set(['policy'], Object.keys(next).length ? next : undefined);
  };
  const reply = pol.replyTimeout ? { v: pol.replyTimeout, from: 'step override' } : act?.policy?.replyTimeout ? { v: act.policy.replyTimeout, from: 'activity default' } : undefined;
  const cal = pol.replyCalendar ? { v: pol.replyCalendar, from: 'step override' } : act?.policy?.replyCalendar ? { v: act.policy.replyCalendar, from: 'activity default' } : undefined;
  return (
    <>
      <div className="form-grid">
        {idField}
        <label className="field wide"><span>Block</span><BlockPicker value={st.call} onPick={(n) => set(['call'], n)} /></label>
        <label className="field"><span>Level</span><Sel value={st.level} onChange={(v) => set(['level'], v)} options={['payment', 'batch', 'item']} label="Level" blank="the block's level" /></label>
      </div>
      {st.call && act === undefined && <p className="muted small">Loading the contract of {st.call}…</p>}
      {act === null && <p className="small fail-text">{st.call} does not resolve.</p>}
      {act && (
        <>
          <p className="small muted">
            {act.title}. Type <code>{act.type}</code>{act.mode && <> mode <code>{act.mode}</code></>}, effect <code>{act.effect}</code>, outcomes {outs.map((o) => <code key={o.name} className={o.primary ? 'strong' : ''}>{o.name}{o.primary ? ' (primary)' : ''} </code>)}
          </p>
          <HandleEditor outcomes={outs.filter((o) => !o.primary).map((o) => o.name)} primary={primary} handle={st.handle} set={(v) => set(['handle'], v)} />
          <h4>Policy override</h4>
          {composite ? (
            <>
              <div className="form-grid">
                <label className="field"><span>Reply time</span><Txt value={pol.replyTimeout} onChange={(v) => setPol('replyTimeout', v)} mono label="Reply timeout" placeholder={act.policy?.replyTimeout ?? 'P1D'} /></label>
                <label className="field"><span>Reply calendar</span><Sel value={pol.replyCalendar} onChange={(v) => setPol('replyCalendar', v)} options={['elapsed', 'business']} label="Reply calendar" blank={act.policy?.replyCalendar ? `activity: ${act.policy.replyCalendar}` : 'none'} /></label>
              </div>
              <table className="grid compact">
                <thead><tr><th>Setting</th><th>Effective value</th><th>Comes from</th></tr></thead>
                <tbody>
                  <tr><td>replyTimeout</td><td>{reply ? <code>{reply.v}</code> : <span className="fail-text">none (L32)</span>}</td><td>{reply && <Badge tone={reply.from === 'step override' ? 'warn' : 'info'}>{reply.from}</Badge>}</td></tr>
                  <tr><td>replyCalendar</td><td>{cal ? <code>{cal.v}</code> : <span className="muted">elapsed</span>}</td><td>{cal ? <Badge tone={cal.from === 'step override' ? 'warn' : 'info'}>{cal.from}</Badge> : <Badge>type default</Badge>}</td></tr>
                </tbody>
              </table>
              <p className="muted small">A composite's own steps carry their attempt and retry policy; the designer sets only how long the block waits for its reply.</p>
            </>
          ) : (
            <div className="form-grid">
              <label className="field"><span>Attempt timeout</span><Txt value={pol.attemptTimeout} onChange={(v) => setPol('attemptTimeout', v)} mono label="Attempt timeout" placeholder={act.policy?.attemptTimeout ?? 'type default'} /></label>
              <label className="field"><span>Total timeout</span><Txt value={pol.totalTimeout} onChange={(v) => setPol('totalTimeout', v)} mono label="Total timeout" placeholder={act.policy?.totalTimeout ?? 'type default'} /></label>
              <label className="field"><span>Max attempts</span><Txt value={pol.retry?.maxAttempts} onChange={(v) => setPol('retry', v ? { ...(pol.retry ?? {}), maxAttempts: Number(v) } : undefined)} label="Max attempts" placeholder={String(act.policy?.retry?.maxAttempts ?? 'type default')} /></label>
            </div>
          )}
        </>
      )}
    </>
  );
}

/** One row per non-primary outcome: every one must be handled, nothing else may be (L05). */
function HandleEditor({ outcomes, primary, handle, set }: { outcomes: string[]; primary?: string; handle?: Record<string, any>; set: (v: any) => void }) {
  const h = handle ?? {};
  const extra = Object.keys(h).filter((k) => !outcomes.includes(k));
  const put = (k: string, v: any) => {
    const next = { ...h, [k]: v };
    if (v === undefined) delete next[k];
    set(Object.keys(next).length ? next : undefined);
  };
  return (
    <>
      <h4>Handle</h4>
      {primary && <p className="muted small">The primary outcome <code>{primary}</code> falls through to the next step.</p>}
      {!outcomes.length && <p className="muted small">No other outcome to handle.</p>}
      <table className="grid compact handle-table">
        <tbody>
          {outcomes.map((o) => (
            <tr key={o} className={h[o] === undefined ? 'row-fail' : ''}>
              <td className="mono nowrap">{o}</td>
              <td><ActionEditor value={h[o]} onChange={(v) => put(o, v)} allowNone outcome={o} /></td>
            </tr>
          ))}
          {extra.map((o) => (
            <tr key={o} className="row-warn">
              <td className="mono nowrap">{o}</td>
              <td><span className="warn-text small">Not an outcome of this block. </span><button className="link danger" onClick={() => put(o, undefined)}>Remove</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function ActionEditor({ value, onChange, allowNone, outcome }: { value: any; onChange: (v: any) => void; allowNone?: boolean; outcome?: string }) {
  const ctx = useContext(C);
  const exits: string[] = ctx.doc?.exit?.outcomes ?? [];
  const fallback = outcome && exits.includes(outcome) ? outcome : exits[1] ?? exits[0] ?? 'failed';
  const mode = value === undefined ? 'none' : Array.isArray(value) ? 'steps' : value && typeof value === 'object' ? ('end' in value ? 'end' : 'return' in value ? 'return' : 'continue' in value ? 'continue' : 'other') : 'other';
  const modes: [string, string][] = [...(allowNone ? [['none', 'not handled'] as [string, string]] : []), ['end', 'end the workflow'], ['continue', 'record and carry on'], ['steps', 'run steps'], ...(ctx.subflow ? [['return', 'return an outcome'] as [string, string]] : [])];
  return (
    <span className="action-editor">
      <select value={mode} aria-label="Action" onChange={(e) => {
        const m = e.target.value;
        onChange(m === 'none' ? undefined : m === 'end' ? { end: fallback } : m === 'continue' ? { continue: true } : m === 'steps' ? [] : m === 'return' ? { return: fallback } : value);
      }}>
        {modes.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        {mode === 'other' && <option value="other">as in the YAML</option>}
      </select>
      {mode === 'end' && (
        <>
          <input value={value.end ?? ''} onChange={(e) => onChange({ ...value, end: e.target.value })} className="mono" list="cv-exit-list" aria-label="End outcome" size={12} />
          <label className="check small"><input type="checkbox" checked={!!value.compensate} onChange={(e) => onChange(e.target.checked ? { ...value, compensate: 'all' } : { end: value.end })} /> compensate</label>
        </>
      )}
      {mode === 'return' && <Sel value={value.return} onChange={(v) => onChange({ return: v ?? '' })} options={exits} label="Return outcome" blank="choose" />}
      {mode === 'steps' && <span className="muted small">The steps show under the step.</span>}
      <datalist id="cv-exit-list">{exits.map((o) => <option key={o} value={o} />)}</datalist>
    </span>
  );
}
