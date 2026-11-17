import { useEffect, useMemo, useRef, useState } from 'react';
import YAML from 'yaml';
import { ApiError, del, get, post, q, type Issue, type Summary } from '../api';
import { Badge, ErrorBox, Issues, Loading, Provenance, Section, StatusBadge, useDebounced, useLoad, useStudio } from '../ui';
import { Outline } from './Outline';

// The workflow designer (F-12 to F-14, F-22): create or open, edit with checks on every edit,
// see the outline, the interface diff and the proposed release, save, publish.

const WF_KINDS = ['platform', 'leg', 'segment', 'handler'] as const;

function parseLocal(text: string): { doc?: any; error?: string } {
  try {
    return { doc: YAML.parse(text) };
  } catch (e: any) {
    return { error: String(e?.message ?? e).split('\n')[0] };
  }
}

export function Designer({ query }: { query: URLSearchParams }) {
  const { tenant, actor, go } = useStudio();
  const [yaml, setYaml] = useState('');
  const [origin, setOrigin] = useState<string>('');
  const [draftKey, setDraftKey] = useState<string>();
  const [check, setCheck] = useState<{ issues: Issue[]; interface?: any }>();
  const [checking, setChecking] = useState(false);
  const [checkError, setCheckError] = useState<string>();
  const [selected, setSelected] = useState<string>();
  const [message, setMessage] = useState<{ tone: 'pass' | 'fail' | 'warn'; text: string; issues?: Issue[] }>();
  const [checker, setChecker] = useState('');
  const [busy, setBusy] = useState(false);
  const area = useRef<HTMLTextAreaElement>(null);
  const drafts = useLoad(() => get<Summary[]>('/api/drafts'), [tenant]);

  const dYaml = useDebounced(yaml, 350);
  const local = useMemo(() => parseLocal(dYaml), [dYaml]);
  const [lastDoc, setLastDoc] = useState<any>();
  useEffect(() => {
    if (local.doc) setLastDoc(local.doc);
  }, [local.doc]);

  // Checks on every edit (R-10-03).
  useEffect(() => {
    if (!dYaml.trim()) {
      setCheck(undefined);
      return;
    }
    let live = true;
    setChecking(true);
    post('/api/check', { yaml: dYaml })
      .then((r) => live && (setCheck(r), setCheckError(undefined)))
      .catch((e) => live && setCheckError(e.message))
      .finally(() => live && setChecking(false));
    return () => {
      live = false;
    };
  }, [dYaml, tenant]);

  // Open from the URL: ?open=<name> (new release), ?open=<key>&draft=1, ?clone=<name>.
  const opened = useRef('');
  useEffect(() => {
    const open = query.get('open');
    const sig = query.toString();
    if (!open || opened.current === sig) return;
    opened.current = sig;
    if (query.get('draft')) openDraft(open);
    else newRelease(open);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query]);

  const load = (y: string, from: string, key?: string) => {
    setYaml(y);
    setOrigin(from);
    setDraftKey(key);
    setMessage(undefined);
    setSelected(undefined);
  };

  async function openDraft(key: string) {
    try {
      const name = key.split('@')[0];
      const a = await get(`/api/artefacts/${encodeURIComponent(name)}${q({ key })}`);
      load(a.yaml, `Draft ${key}`, key);
    } catch (e: any) {
      setMessage({ tone: 'fail', text: e.message });
    }
  }
  async function newRelease(name: string) {
    try {
      const r = await post('/api/new', { from: 'clone', source: name });
      load(r.yaml, `New release of ${name}. The checks propose the release the interface change needs.`);
    } catch (e: any) {
      setMessage({ tone: 'fail', text: e.message });
    }
  }

  const saveDraft = async () => {
    setBusy(true);
    setMessage(undefined);
    try {
      const r = await post('/api/drafts', { yaml, actor });
      setDraftKey(r.draft.key);
      setCheck({ issues: r.issues, interface: r.interface });
      const errs = (r.issues as Issue[]).filter((i) => i.severity === 'error').length;
      setMessage({ tone: errs ? 'warn' : 'pass', text: `Saved draft ${r.draft.key} as ${actor}.${errs ? ` It has ${errs} error(s); fix them before publish.` : ''}` });
      drafts.reload();
    } catch (e: any) {
      setMessage({ tone: 'fail', text: e.message });
    } finally {
      setBusy(false);
    }
  };

  const publish = async () => {
    if (!draftKey) return;
    setBusy(true);
    setMessage(undefined);
    try {
      const r = await post('/api/publish', { key: draftKey, maker: actor, checker });
      setMessage({ tone: 'pass', text: `Published ${r.published.key}. Maker ${actor}, checker ${checker}.` });
      setDraftKey(undefined);
      drafts.reload();
    } catch (e: any) {
      const status = e instanceof ApiError ? e.status : 0;
      setMessage({ tone: 'fail', text: `${status ? `${status}: ` : ''}${e.message}`, issues: e instanceof ApiError ? e.body?.issues : undefined });
    } finally {
      setBusy(false);
    }
  };

  const removeDraft = async (key: string) => {
    if (!window.confirm(`Delete draft ${key}?`)) return;
    try {
      await del(`/api/drafts/${encodeURIComponent(key)}${q({ actor })}`);
      if (key === draftKey) setDraftKey(undefined);
      drafts.reload();
    } catch (e: any) {
      setMessage({ tone: 'fail', text: e.message });
    }
  };

  /** Select a step: highlight it in the outline and in the YAML. */
  const selectStep = (step: string) => {
    setSelected(step);
    const ta = area.current;
    if (!ta) return;
    const patterns = [new RegExp(`(^|\\n)(\\s*-?\\s*)id:\\s*${escapeRe(step)}\\s*(\\n|$)`), new RegExp(`(call|slot|hook|use):\\s*${escapeRe(step)}\\s*(\\n|$)`)];
    for (const p of patterns) {
      const m = p.exec(ta.value);
      if (m) {
        const start = m.index + (m[1]?.length ?? 0);
        ta.focus();
        ta.setSelectionRange(start, start + m[0].trimEnd().length - (m[1]?.length ?? 0));
        const line = ta.value.slice(0, start).split('\n').length;
        ta.scrollTop = Math.max(0, (line - 4) * 18);
        return;
      }
    }
  };

  const issues = check?.issues ?? [];
  const errors = issues.filter((i) => i.severity === 'error').length;
  const warnings = issues.length - errors;
  const doc = local.doc ?? lastDoc;
  const sameMaker = checker.trim() !== '' && checker.trim() === actor.trim();

  return (
    <div className="view">
      <div className="view-head">
        <h2>Workflow designer</h2>
        <p className="muted">Create a platform workflow, leg, segment, handler or hook. Every static check runs on every edit and names the step it concerns.</p>
      </div>
      <div className="cols">
        <div className="col">
          <NewPanel initialClone={query.get('clone') ?? undefined} onLoad={(y, from) => load(y, from)} />
        </div>
        <div className="col side">
          <OpenPanel drafts={drafts} onOpenDraft={openDraft} onNewRelease={newRelease} onDelete={removeDraft} current={draftKey} />
        </div>
      </div>

      {!yaml && !message && <p className="muted pad">Start a new workflow above, or open a draft or a published workflow.</p>}
      {message && (
        <div className={`alert ${message.tone}`} role="status">
          {message.text}
          {message.issues && <Issues issues={message.issues} onStep={selectStep} />}
        </div>
      )}

      {yaml && (
        <>
          <div className="editor-bar">
            <div className="grow">
              <span className="muted small">{origin}</span>
              {doc?.name && <div><b className="mono">{doc.name}</b> <span className="mono muted">@{String(doc.release ?? '')}</span> {doc.provenance?.status && <Provenance status={doc.provenance.status} />} {draftKey && <StatusBadge status="draft" />}</div>}
            </div>
            <div className="check-state">
              {checking ? <Badge>checking…</Badge> : checkError ? <Badge tone="fail">check failed</Badge> : errors ? <Badge tone="fail">{errors} error{errors === 1 ? '' : 's'}</Badge> : <Badge tone="pass">no errors</Badge>}
              {warnings > 0 && <Badge tone="warn">{warnings} warning{warnings === 1 ? '' : 's'}</Badge>}
            </div>
          </div>
          <div className="designer">
            <div className="pane">
              <label className="sr-only" htmlFor="yaml-editor">Workflow YAML</label>
              <textarea id="yaml-editor" ref={area} className="yaml-editor" spellCheck={false} value={yaml} onChange={(e) => setYaml(e.target.value)} />
              {local.error && <div className="alert warn small">YAML does not parse: {local.error}. The outline shows the last good version.</div>}
            </div>
            <div className="pane">
              <Section title="Outline">
                {doc ? <Outline doc={doc} issues={issues} selected={selected} onSelect={selectStep} /> : <Loading />}
              </Section>
            </div>
          </div>
          <div className="cols">
            <div className="col">
              <Section title="Checks" count={issues.length}>
                <ErrorBox error={checkError} />
                <Issues issues={issues} onStep={selectStep} empty="No issues. Every static check passes." />
              </Section>
            </div>
            <div className="col side">
              <Section title="Interface and release">
                <InterfacePanel iface={check?.interface} doc={doc} />
              </Section>
              <Section title="Save and publish">
                <div className="stack">
                  <button className="primary" disabled={busy} onClick={saveDraft}>Save draft as {actor}</button>
                  <label className="field">
                    <span>Checker</span>
                    <input value={checker} onChange={(e) => setChecker(e.target.value)} placeholder="A different person from the maker" />
                  </label>
                  {sameMaker && <p className="small fail-text">The checker must differ from the maker ({actor}).</p>}
                  <button disabled={busy || !draftKey || !checker.trim()} onClick={publish} title={!draftKey ? 'Save a draft first' : undefined}>Publish draft</button>
                  {!draftKey && <p className="muted small">Save a draft first. Publish takes the saved draft, not the editor text.</p>}
                  {draftKey && <p className="muted small">Draft <code>{draftKey}</code>. <button className="link" onClick={() => go(`/artefact/${encodeURIComponent(draftKey.split('@')[0])}?key=${encodeURIComponent(draftKey)}`)}>Open its page</button></p>}
                </div>
              </Section>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function escapeRe(s: string) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function InterfacePanel({ iface, doc }: { iface?: any; doc?: any }) {
  if (!iface) return <p className="muted small">{doc?.name ? `No published release of ${doc.name} yet, so there is no interface diff. This will be the first release.` : 'No interface diff.'}</p>;
  const tone = iface.required === 'major' ? 'fail' : iface.required === 'minor' ? 'warn' : 'pass';
  return (
    <div className="stack">
      <div>
        Previous <code>{iface.previous}</code>, change needs <Badge tone={tone}>{iface.required}</Badge>, proposed release <b className="mono">{iface.proposed}</b>
      </div>
      {doc?.release && String(doc.release) !== iface.proposed && iface.required !== 'none' && (
        <p className="small warn-text">The document says {String(doc.release)}. Set the release to {iface.proposed}.</p>
      )}
      {iface.changes?.length ? (
        <table className="grid compact">
          <thead><tr><th>Part</th><th>Change</th><th>Impact</th></tr></thead>
          <tbody>{iface.changes.map((c: any, i: number) => <tr key={i}><td>{c.part}</td><td>{c.change}</td><td><Badge tone={c.impact === 'major' ? 'fail' : c.impact === 'minor' ? 'warn' : 'muted'}>{c.impact}</Badge></td></tr>)}</tbody>
        </table>
      ) : <p className="muted small">No interface change.</p>}
    </div>
  );
}

function NewPanel({ initialClone, onLoad }: { initialClone?: string; onLoad: (yaml: string, from: string) => void }) {
  const { tenant } = useStudio();
  const [kind, setKind] = useState<'workflow' | 'hook'>('workflow');
  const [wfKind, setWfKind] = useState<(typeof WF_KINDS)[number]>('leg');
  const [from, setFrom] = useState<'blank' | 'template' | 'clone'>(initialClone ? 'clone' : 'template');
  const [name, setName] = useState('');
  const [title, setTitle] = useState('');
  const [source, setSource] = useState(initialClone ?? '');
  const [error, setError] = useState<string>();
  const flows = useLoad(async () => {
    const [w, h] = await Promise.all([get('/api/catalogue?kind=workflow'), get('/api/catalogue?kind=hook')]);
    return [...w.items, ...h.items].filter((i: Summary) => i.status === 'published') as Summary[];
  }, [tenant]);

  const create = async () => {
    setError(undefined);
    try {
      const body = from === 'clone' ? { from, source, name: name || undefined, title: title || undefined } : { kind, wfKind: kind === 'workflow' ? wfKind : undefined, from, name: name || undefined, title: title || undefined };
      const r = await post('/api/new', body);
      onLoad(r.yaml, from === 'clone' ? `Cloned from ${source}` : `New ${kind === 'workflow' ? wfKind : 'hook'}, ${from === 'template' ? 'from the template' : 'blank'}`);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Section title="New">
      <div className="form-grid">
        <label className="field">
          <span>Start</span>
          <select value={from} onChange={(e) => setFrom(e.target.value as any)} aria-label="Start from">
            <option value="blank">Blank</option>
            <option value="template">From a template</option>
            <option value="clone">Clone a release</option>
          </select>
        </label>
        {from !== 'clone' ? (
          <>
            <label className="field">
              <span>Kind</span>
              <select value={kind} onChange={(e) => setKind(e.target.value as any)} aria-label="Kind">
                <option value="workflow">workflow</option>
                <option value="hook">hook</option>
              </select>
            </label>
            {kind === 'workflow' && (
              <label className="field">
                <span>Workflow kind</span>
                <select value={wfKind} onChange={(e) => setWfKind(e.target.value as any)} aria-label="Workflow kind">
                  {WF_KINDS.map((k) => <option key={k}>{k}</option>)}
                </select>
              </label>
            )}
          </>
        ) : (
          <label className="field wide">
            <span>Source</span>
            <select value={source} onChange={(e) => setSource(e.target.value)} aria-label="Clone source">
              <option value="">Choose a published workflow or hook</option>
              {(flows.data ?? []).map((f) => <option key={f.key} value={f.name}>{f.name} @{f.release}</option>)}
            </select>
          </label>
        )}
        <label className="field wide">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder={from === 'clone' ? 'Leave empty for a new release of the source' : 'scope.type.name.v1, for example uae.aani.wf.my-leg.v1'} className="mono" />
        </label>
        <label className="field wide">
          <span>Title</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Short and plain" />
        </label>
      </div>
      <div className="row-actions">
        <button className="primary" onClick={create} disabled={from === 'clone' && !source}>Create</button>
      </div>
      <ErrorBox error={error} />
    </Section>
  );
}

function OpenPanel({ drafts, onOpenDraft, onNewRelease, onDelete, current }: { drafts: { data?: Summary[]; error?: string; loading: boolean }; onOpenDraft: (k: string) => void; onNewRelease: (n: string) => void; onDelete: (k: string) => void; current?: string }) {
  const { tenant } = useStudio();
  const [pick, setPick] = useState('');
  const flows = useLoad(async () => {
    const [w, h] = await Promise.all([get('/api/catalogue?kind=workflow'), get('/api/catalogue?kind=hook')]);
    return [...w.items, ...h.items].filter((i: Summary) => i.status === 'published') as Summary[];
  }, [tenant]);
  const editable = (flows.data ?? []).filter((f) => tenant === 'owner' || f.tenant === tenant);
  return (
    <Section title="Open">
      <h4>Drafts of {tenant}</h4>
      <ErrorBox error={drafts.error} />
      {drafts.data && !drafts.data.length && <p className="muted small">No drafts.</p>}
      <ul className="plain">
        {(drafts.data ?? []).map((d) => (
          <li key={d.key} className={d.key === current ? 'sel' : ''}>
            <button className="link mono" onClick={() => onOpenDraft(d.key)}>{d.key}</button>{' '}
            {d.issues ? <Badge tone="fail">{d.issues} error{d.issues === 1 ? '' : 's'}</Badge> : <Badge tone="pass">no errors</Badge>}{' '}
            <button className="link danger" onClick={() => onDelete(d.key)}>Delete</button>
          </li>
        ))}
      </ul>
      <h4>New release of a published one</h4>
      <div className="inline-form">
        <select value={pick} onChange={(e) => setPick(e.target.value)} aria-label="Published workflow">
          <option value="">Choose</option>
          {editable.map((f) => <option key={f.key} value={f.name}>{f.name} @{f.release}</option>)}
        </select>
        <button disabled={!pick} onClick={() => onNewRelease(pick)}>Open</button>
      </div>
      {tenant !== 'owner' && <p className="muted small">A bank tenant may release only its own workflows and hooks. Shared content is read-only.</p>}
    </Section>
  );
}
