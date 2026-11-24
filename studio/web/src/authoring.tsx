import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import YAML from 'yaml';
import { APPROVER_ROLES, ApiError, MAKER_ROLES, get, post, refusal, type GateResult, type Issue, type Summary } from './api';
import { Badge, ErrorBox, Issues, Json, Section, useDebounced, useStudio } from './ui';
import { Button, Input, Link, Select, Textarea } from '@fluentui/react-components';

// Shared pieces of the authoring screens: the gates, save and publish through maker-checker,
// paths from the canonical model, and small list editors.

// ------------------------------------------------------------------ data

const docCache = new Map<string, Promise<any>>();

/** An artefact's page data, cached for the session. Used to read an activity's outcomes and binding. */
export function artefact(name: string, tenant: string): Promise<any> {
  const k = `${tenant}|${name}`;
  if (!docCache.has(k)) {
    const p = get(`/api/artefacts/${encodeURIComponent(name)}`, tenant);
    p.catch(() => docCache.delete(k));
    docCache.set(k, p);
  }
  return docCache.get(k)!;
}

/** The documents for a set of names, loaded once each. */
export function useDocs(names: string[]): Record<string, any> {
  const { tenant } = useStudio();
  const [map, setMap] = useState<Record<string, any>>({});
  const key = [...new Set(names.filter(Boolean))].sort().join('|');
  useEffect(() => {
    let live = true;
    for (const n of key.split('|').filter(Boolean)) {
      if (map[n] !== undefined) continue;
      artefact(n, tenant).then((a) => live && setMap((m) => ({ ...m, [n]: a.doc })), () => live && setMap((m) => ({ ...m, [n]: null })));
    }
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, tenant]);
  return map;
}

const catCache = new Map<string, Promise<Summary[]>>();
export function useCatalogue(kind: string): Summary[] {
  const { tenant } = useStudio();
  const [items, setItems] = useState<Summary[]>([]);
  useEffect(() => {
    const k = `${tenant}|${kind}`;
    if (!catCache.has(k)) catCache.set(k, get(`/api/catalogue?kind=${encodeURIComponent(kind)}`).then((r) => r.items as Summary[]));
    let live = true;
    catCache.get(k)!.then((x) => live && setItems(x), () => live && setItems([]));
    return () => {
      live = false;
    };
  }, [kind, tenant]);
  return items;
}
export function forgetCatalogue() {
  catCache.clear();
  docCache.clear();
}

export interface ModelNode { path: string; name: string; isoType?: string; personalData?: boolean; description?: string; origin?: string; children?: ModelNode[] }
let modelPromise: Promise<{ roots: ModelNode[]; flat: ModelNode[] }> | undefined;
/** The canonical model (GET /api/model), as the tree and as a flat list of paths. */
export function useModel() {
  const [m, setM] = useState<{ roots: ModelNode[]; flat: ModelNode[] }>();
  useEffect(() => {
    modelPromise ??= get('/api/model').then((r) => {
      const flat: ModelNode[] = [];
      const walk = (n: ModelNode) => {
        flat.push(n);
        (n.children ?? []).forEach(walk);
      };
      (r.roots ?? []).forEach(walk);
      return { roots: r.roots ?? [], flat };
    });
    modelPromise.then(setM, () => (modelPromise = undefined));
  }, []);
  return m;
}

/** Does a context path resolve in the model? A path under a fact or extension root resolves by its root. */
export function resolves(path: string, flat: ModelNode[]): boolean {
  if (flat.some((n) => n.path === path)) return true;
  const root = path.split('.')[0];
  return root === 'facts' || (root === 'ext' && flat.some((n) => path.startsWith(n.path + '.') && n.origin === 'extension'));
}

// ------------------------------------------------------------------ gates

/** Run the publish gates on a document as it is edited (POST /api/gates). */
export function useGates(doc: any, extra?: { tests?: any[]; script?: any }, ms = 600) {
  const { tenant } = useStudio();
  const sig = useDebounced(useMemo(() => JSON.stringify({ doc, extra }), [doc, extra]), ms);
  const [state, setState] = useState<{ result?: GateResult; busy: boolean; error?: string }>({ busy: false });
  useEffect(() => {
    const { doc: d, extra: x } = JSON.parse(sig);
    if (!d || typeof d !== 'object' || !d.kind) {
      setState({ busy: false });
      return;
    }
    let live = true;
    setState((s) => ({ ...s, busy: true }));
    post<GateResult>('/api/gates', { doc: d, ...(x ?? {}) })
      .then((r) => live && setState({ result: r, busy: false }))
      .catch((e) => live && setState({ busy: false, error: e.message }));
    return () => {
      live = false;
    };
  }, [sig, tenant]);
  return state;
}

function GateDetail({ detail }: { detail: any }) {
  if (detail === undefined || detail === null || (Array.isArray(detail) && !detail.length)) return null;
  if (Array.isArray(detail) && detail.every((d) => d && typeof d === 'object' && 'code' in d)) return <Issues issues={detail as Issue[]} />;
  if (Array.isArray(detail)) {
    return (
      <ul className="plain small gate-items">
        {detail.map((d, i) => {
          if (typeof d === 'string') return <li key={i}>{d}</li>;
          if (d.problem) return <li key={i} className="fail-text">{d.problem}</li>;
          const name = d.example ?? d.id;
          return (
            <li key={i}>
              {name !== undefined && <><Badge tone={d.passed ? 'pass' : 'fail'}>{d.passed ? 'pass' : 'fail'}</Badge> <span className="mono">{d.example !== undefined ? `example ${name}` : name}</span></>}
              {d.error && <span className="fail-text"> {d.error}</span>}
              {d.failed?.length > 0 && <span className="fail-text"> {d.failed.map((c: any) => c.what).join(', ')}</span>}
              {!d.passed && d.actual && <details className="inline"><summary className="small"> actual</summary><Json value={d.actual} /></details>}
            </li>
          );
        })}
      </ul>
    );
  }
  if (typeof detail === 'string') return <p className="small">{detail}</p>;
  return <Json value={detail} max={1200} />;
}

/** The gates in order, the first red one highlighted with its evidence (C16-S1). */
export function Gates({ state, title = 'Gates' }: { state: { result?: GateResult; busy: boolean; error?: string }; title?: ReactNode }) {
  const r = state.result;
  const first = r?.gates.find((g) => !g.passed);
  return (
    <Section title={title} actions={state.busy ? <Badge>running…</Badge> : r ? (r.passed ? <Badge tone="pass">every gate green</Badge> : <Badge tone="fail">red at {first?.gate}</Badge>) : undefined}>
      <ErrorBox error={state.error} />
      {!r && !state.error && <p className="muted small">{state.busy ? 'Running the gates…' : 'The gates run on every edit.'}</p>}
      {r && (
        <ol className="gates">
          {r.gates.map((g, i) => (
            <li key={i} className={g === first ? 'gate first-red' : g.passed ? 'gate pass' : 'gate fail'}>
              <div className="gate-head">
                <Badge tone={g.passed ? 'pass' : 'fail'}>{g.passed ? 'green' : 'red'}</Badge>
                <b>{g.gate}</b>
                {g === first && <span className="small fail-text">first red gate</span>}
              </div>
              {(!g.passed || g.gate === 'tests') && <GateDetail detail={g.detail} />}
            </li>
          ))}
        </ol>
      )}
    </Section>
  );
}

// ---------------------------------------------------------- save and publish

export interface Saved { key: string; issues: Issue[]; iface?: any }

/**
 * Save as a draft as the signed-in user: a maker's save needs no approval (R-11-31). The maker asks
 * for the change set to be released; an approver who is not a maker of it approves it as a version.
 * The server decides both from the session; refusals are shown as it gives them.
 */
export function DraftActions({ doc, yaml, onSaved, draftKey, setDraftKey, label = 'draft' }: { doc?: any; yaml?: string; onSaved?: (s: Saved) => void; draftKey?: string; setDraftKey: (k?: string) => void; label?: string }) {
  const { actor, roles, go } = useStudio();
  const approver = roles.some((r) => APPROVER_ROLES.includes(r));
  const maker = roles.some((r) => MAKER_ROLES.includes(r));
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'pass' | 'fail' | 'warn'; text: string; issues?: Issue[] }>();

  // What releasing this draft takes with it: the change set the studio generates (R-11-19).
  const [set, setSet] = useState<string[]>([]);
  useEffect(() => {
    if (!draftKey) { setSet([]); return; }
    get<any>(`/api/change-set?key=${encodeURIComponent(draftKey)}`).then((r) => setSet((r.members ?? []).map((m: any) => m.key))).catch(() => setSet([]));
  }, [draftKey, msg]);

  const save = async () => {
    setBusy(true);
    setMsg(undefined);
    try {
      const r = await post('/api/drafts', doc ? { doc } : { yaml });
      setDraftKey(r.draft.key);
      const errs = (r.issues as Issue[]).filter((i) => i.severity === 'error').length;
      setMsg({ tone: errs ? 'warn' : 'pass', text: `Saved ${label} ${r.draft.key} as ${actor}.${errs ? ` It has ${errs} error(s); fix them before it is released.` : ''}` });
      onSaved?.({ key: r.draft.key, issues: r.issues, iface: r.interface });
      forgetCatalogue();
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e), issues: e instanceof ApiError ? e.body?.issues : undefined });
    } finally {
      setBusy(false);
    }
  };
  const requestRelease = async () => {
    if (!draftKey) return;
    setBusy(true);
    try {
      const r = await post('/api/request-release', { key: draftKey });
      setMsg({ tone: 'pass', text: `Release requested for the change set of ${draftKey} (${r.requested.length} draft(s)). An approver who is not a maker approves it as a version.` });
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    } finally {
      setBusy(false);
    }
  };
  const publish = async () => {
    if (!draftKey) return;
    setBusy(true);
    setMsg(undefined);
    try {
      const r = await post('/api/publish', { key: draftKey });
      setMsg({ tone: 'pass', text: `Change set ${r.changeSet.id} approved as a version: ${r.published.map((p: any) => p.key).join(', ')}. Approved by ${actor}.` });
      setDraftKey(undefined);
      forgetCatalogue();
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e), issues: e instanceof ApiError ? e.body?.issues : undefined });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="stack">
      {maker && <Button appearance="primary" disabled={busy || (!doc && !yaml)} onClick={save}>Save {label}</Button>}
      {maker && <Button disabled={busy || !draftKey} onClick={requestRelease} title="Ask an approver to make this change set a version">Request release</Button>}
      {set.length > 1 && (
        <div className="small">
          <strong>Change set.</strong> Its release takes these drafts with it, checked and tested together:
          {set.filter((k) => k !== draftKey).map((k) => <div key={k} className="mono">{k.replace(/#draft$/, '')}</div>)}
        </div>
      )}
      {approver && <Button appearance="primary" disabled={busy || !draftKey} onClick={publish} title={!draftKey ? 'Open a draft first' : undefined}>Approve as version</Button>}
      <p className="muted small">
        Saving needs no approval. A change set becomes a version when an approver for its area, who is not a maker of it, approves it, with no error and green tests.
        {draftKey && <> Draft <code>{draftKey}</code>. <Link as="button" onClick={() => go(`/artefact/${encodeURIComponent(draftKey.split('@')[0])}?key=${encodeURIComponent(draftKey)}`)}>Open its page</Link></>}
      </p>
      {msg && (
        <div className={`alert ${msg.tone}`} role="status">
          {msg.text}
          {msg.issues && <Issues issues={msg.issues} />}
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ inputs

/** A list of strings edited as chips with an input. */
export function StringList({ value, onChange, placeholder, suggestions, check, mono = true, label }: { value?: string[]; onChange: (v: string[] | undefined) => void; placeholder?: string; suggestions?: string[]; check?: (s: string) => string | undefined; mono?: boolean; label?: string }) {
  const [text, setText] = useState('');
  const id = useRef(`dl-${Math.random().toString(36).slice(2)}`).current;
  const items = value ?? [];
  const add = () => {
    const t = text.trim();
    if (!t || items.includes(t)) return;
    onChange([...items, t]);
    setText('');
  };
  return (
    <div className="string-list">
      <span className="chips">
        {items.map((s, i) => {
          const why = check?.(s);
          return (
            <span key={s + i} className={why ? 'chip bad' : 'chip'} title={why}>
              <span className={mono ? 'mono' : ''}>{s}</span>
              {why && <Badge tone="fail">does not resolve</Badge>}
              <Link as="button" className="danger" aria-label={`Remove ${s}`} onClick={() => onChange(items.filter((_, n) => n !== i).length ? items.filter((_, n) => n !== i) : undefined)}>×</Link>
            </span>
          );
        })}
      </span>
      <span className="inline-form">
        <Input list={suggestions ? id : undefined} value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), add())} placeholder={placeholder} className={mono ? 'mono' : ''} aria-label={label ?? placeholder ?? 'Add'} />
        <Button onClick={add} disabled={!text.trim()}>Add</Button>
      </span>
      {suggestions && <datalist id={id}>{suggestions.slice(0, 400).map((s) => <option key={s} value={s} />)}</datalist>}
    </div>
  );
}

/** A text input that writes undefined when empty, so the document stays clean. */
export function Txt({ value, onChange, placeholder, mono, label, list, size, className }: { value: any; onChange: (v: string | undefined) => void; placeholder?: string; mono?: boolean; label?: string; list?: string; size?: number; className?: string }) {
  return <Input style={{ width: 'NaNch' }} value={value === undefined || value === null ? '' : String(value)} onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)} placeholder={placeholder} className={[mono ? 'mono' : '', className ?? ''].join(' ')} aria-label={label ?? placeholder} list={list} />;
}

export function Sel({ value, onChange, options, label, blank }: { value: any; onChange: (v: string | undefined) => void; options: (string | [string, string])[]; label: string; blank?: string }) {
  return (
    <Select value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)} aria-label={label}>
      {blank !== undefined && <option value="">{blank}</option>}
      {options.map((o) => (Array.isArray(o) ? <option key={o[0]} value={o[0]}>{o[1]}</option> : <option key={o}>{o}</option>))}
    </Select>
  );
}

/** Parse a scalar typed in a cell: numbers and booleans as such, else the text. */
export function scalar(s: string | undefined): any {
  if (s === undefined) return undefined;
  const t = s.trim();
  if (t === '') return undefined;
  if (t === 'true') return true;
  if (t === 'false') return false;
  if (/^-?\d+(\.\d+)?$/.test(t)) return Number(t);
  return s;
}

/**
 * Remove undefined keys, so a form writes a clean document. Empty objects stay: an example may
 * give an empty fact, and what the gates see is exactly what is saved.
 */
export function clean<T>(x: T): T {
  if (Array.isArray(x)) return x.map(clean) as any;
  if (x && typeof x === 'object') {
    const out: any = {};
    for (const [k, v] of Object.entries(x)) {
      const c = clean(v);
      if (c !== undefined) out[k] = c;
    }
    return out;
  }
  return x;
}

/** An object, or undefined when it has no key left. */
export function orNone<T extends object>(x: T): T | undefined {
  return Object.keys(clean(x)).length ? clean(x) : undefined;
}

export function toYamlText(doc: any): string {
  return YAML.stringify(doc, { lineWidth: 0 });
}

/** A JSON object edited as text; reports a parse error and keeps the last good value. */
export function JsonField({ value, onChange, rows = 4, label }: { value: any; onChange: (v: any) => void; rows?: number; label: string }) {
  const [text, setText] = useState(() => (value === undefined ? '' : JSON.stringify(value, null, 1)));
  const [err, setErr] = useState<string>();
  const last = useRef(value);
  useEffect(() => {
    if (JSON.stringify(value) !== JSON.stringify(last.current)) {
      setText(value === undefined ? '' : JSON.stringify(value, null, 1));
      last.current = value;
    }
  }, [value]);
  return (
    <div>
      <Textarea className="code-input" rows={rows} value={text} spellCheck={false} aria-label={label} onChange={(e) => {
        setText(e.target.value);
        try {
          const v = e.target.value.trim() ? JSON.parse(e.target.value) : undefined;
          last.current = v;
          setErr(undefined);
          onChange(v);
        } catch (x: any) {
          setErr(x.message);
        }
      }} />
      {err && <div className="small fail-text">Not JSON: {err}</div>}
    </div>
  );
}
