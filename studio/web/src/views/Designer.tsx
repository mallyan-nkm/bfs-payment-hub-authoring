import { useEffect, useMemo, useRef, useState } from 'react';
import YAML from 'yaml';
import { del, get, post, q, refusal, type Issue, type Summary } from '../api';
import { Badge, ErrorBox, Issues, Loading, Provenance, Section, StatusBadge, Tabs, useDebounced, useLoad, useStudio } from '../ui';
import { DraftActions, toYamlText } from '../authoring';
import { expandWorkflow, sameDoc, useResolver } from '../expand';
import { Canvas } from './Canvas';
import { Outline } from './Outline';
import { useAssist } from '../assist';

// The workflow designer (F-12 to F-14, F-22): create or open, edit with checks on every edit,
// see the outline, the interface diff and the proposed release, save, publish.

const WF_KINDS = ['platform', 'leg', 'segment', 'handler', 'subflow'] as const;

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
  const [view, setView] = useState<'canvas' | 'outline' | 'expanded'>('canvas');
  const area = useRef<HTMLTextAreaElement>(null);
  const drafts = useLoad(() => get<Summary[]>('/api/drafts'), [tenant]);

  // The document follows the YAML on every keystroke; the checks run on a short pause (R-10-03).
  const local = useMemo(() => parseLocal(yaml), [yaml]);
  const [lastDoc, setLastDoc] = useState<any>();
  useEffect(() => {
    if (local.doc) setLastDoc(local.doc);
  }, [local.doc]);
  const dYaml = useDebounced(yaml, 300);

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

  // Open from the URL: ?open=<name> (new release), ?open=<key>&draft=1, ?clone=<name>, ?view=expanded.
  const opened = useRef('');
  useEffect(() => {
    const open = query.get('open');
    const sig = query.toString();
    if (query.get('view') === 'expanded') setView('expanded');
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

  const removeDraft = async (key: string) => {
    if (!window.confirm(`Delete draft ${key}?`)) return;
    try {
      await del(`/api/drafts/${encodeURIComponent(key)}${q({ actor })}`);
      if (key === draftKey) setDraftKey(undefined);
      drafts.reload();
    } catch (e: any) {
      setMessage({ tone: 'fail', text: refusal(e) });
    }
  };

  /** Select a step: highlight it on the canvas and in the YAML. */
  const selectStep = (step: string) => {
    setSelected(step);
    const ta = area.current;
    if (!ta) return;
    const patterns = [new RegExp(`(^|\\n)(\\s*-?\\s*)id:\\s*${escapeRe(step)}\\s*(\\n|$)`), new RegExp(`(call|slot|hook|use):\\s*${escapeRe(step)}\\s*(\\n|$)`)];
    for (const p of patterns) {
      const m = p.exec(ta.value);
      if (m) {
        const start = m.index + (m[1]?.length ?? 0);
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
  const resolver = useResolver(doc, tenant);
  const onCanvas = (next: any) => setYaml(toYamlText(next));
  useAssist({ surface: 'designer', kind: doc?.kind, doc: yaml ? doc : undefined, setDoc: (d) => setYaml(toYamlText(d)), selected, topic: 'designer' });

  return (
    <div className="view">
      <div className="view-head">
        <h2>Workflow designer</h2>
        <p className="muted">Create a platform workflow, leg, segment, handler, subflow or hook. Compose it on the canvas or in the YAML; the two stay in step. Every static check runs on every edit and shows at the step it concerns.</p>
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
              {local.error && <div className="alert warn small">YAML does not parse: {local.error}. The canvas shows the last good version and does not edit until the YAML parses.</div>}
            </div>
            <div className="pane">
              <Section
                title={
                  <Tabs
                    tabs={[{ id: 'canvas', label: 'Canvas' }, { id: 'outline', label: 'Outline' }, { id: 'expanded', label: 'Expanded form' }]}
                    value={view}
                    onChange={setView}
                  />
                }
              >
                {!doc ? <Loading /> : view === 'canvas' ? (
                  local.error ? <Outline doc={doc} issues={issues} selected={selected} onSelect={selectStep} /> : <Canvas doc={doc} onChange={onCanvas} issues={issues} docs={resolver.docs} selected={selected} onSelect={selectStep} />
                ) : view === 'outline' ? (
                  <Outline doc={doc} issues={issues} selected={selected} onSelect={selectStep} />
                ) : (
                  <ExpandedView doc={doc} resolver={resolver} issues={issues} />
                )}
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
                <DraftActions yaml={yaml} draftKey={draftKey} setDraftKey={(k) => { setDraftKey(k); drafts.reload(); }} onSaved={(r) => setCheck({ issues: r.issues, interface: r.iface })} />
              </Section>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

/** The expanded form (F-34): what the interpreter and every engine run. Derived, read-only. */
function ExpandedView({ doc, resolver, issues }: { doc: any; resolver: { resolve: (n: string) => any; ready: boolean }; issues: Issue[] }) {
  const { tenant } = useStudio();
  const x = useMemo(() => (resolver.ready ? expandWorkflow(doc, resolver.resolve) : undefined), [doc, resolver]);
  const [server, setServer] = useState<{ same?: boolean; error?: string; release?: string }>();
  useEffect(() => setServer(undefined), [doc]);
  const compare = async () => {
    try {
      // The release in the editor may not be stored yet: compare with it if it is, else with the latest.
      const r = await get(`/api/expanded/${encodeURIComponent(doc.name)}${q({ release: doc.release })}`, tenant).catch(() => get(`/api/expanded/${encodeURIComponent(doc.name)}`, tenant));
      const pick = (d: any) => ({ do: d.do, finally: d.finally, deadline: d.deadline });
      setServer({ same: sameDoc(pick(r.doc), pick(x!.doc)), release: r.doc.release });
    } catch (e: any) {
      setServer({ error: e.message });
    }
  };
  if (!x) return <Loading what="the blocks the expansion reaches" />;
  return (
    <div className="stack">
      <p className="muted small">
        {x.expanded ? 'Each composite step is a block holding the steps of its subflow, with the calling step id in front. Nobody edits this form; it is derived from the authored form.' : 'This workflow calls no composite activity, so its expanded form is the authored form.'}
      </p>
      {x.problems.map((p, i) => <div key={i} className="alert fail small">{p}</div>)}
      <div className="inline-form">
        <button onClick={compare} disabled={!doc?.name}>Compare with the server's expansion</button>
        {server?.same === true && <Badge tone="pass">same as GET /api/expanded at {server.release}</Badge>}
        {server?.same === false && <Badge tone="warn">differs from the stored release {server.release}: the editor holds changes</Badge>}
        {server?.error && <span className="small fail-text">{server.error}</span>}
      </div>
      <Outline doc={x.doc} issues={issues} />
      <details>
        <summary>Expanded YAML</summary>
        <pre className="code tall">{toYamlText(x.doc)}</pre>
      </details>
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
      if (from !== 'clone' && kind === 'workflow' && wfKind === 'subflow') {
        // A subflow implements one composite and returns, never ends (R-05-34).
        const d = YAML.parse(r.yaml);
        d.name = name || 'uae.aani.sub.new.v1';
        d.implements = { activity: 'uae.aani.exchange.new.v1' };
        delete d.entry; delete d.slots; delete d.hookPoints; delete d.signals; delete d.deadline; delete d.finally;
        d.exit = { outcomes: ['accepted', 'rejected'] };
        d.do = [{ return: 'accepted' }];
        onLoad(toYamlText(d), 'New subflow. It implements one composite; await and return are offered on the canvas.');
        return;
      }
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
