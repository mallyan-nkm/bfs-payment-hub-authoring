import { Fragment, useMemo, useState, type ReactNode } from 'react';
import { get, q } from '../api';
import { ArtefactLink, Badge, ErrorBox, Loading, PassFail, Section, Tabs, useDebounced, useLoad, useStudio } from '../ui';
import { useCatalogue, useModel, type ModelNode } from '../authoring';

// Data (F-03 to F-05, C03): the canonical model, the types generated per activity, the code
// registry with the reason-code report, and sample messages with their proof.

type Tab = 'paths' | 'model' | 'types' | 'registry' | 'samples';

export function DataTabs({ query, paths }: { query: URLSearchParams; paths: ReactNode }) {
  const { go } = useStudio();
  const tab = (['paths', 'model', 'types', 'registry', 'samples'].includes(query.get('tab') ?? '') ? query.get('tab') : 'paths') as Tab;
  return (
    <div className="view">
      <div className="view-head">
        <h2>ISO data</h2>
        <p className="muted">The canonical ISO 20022 model the context is made of, who reads and writes each element, the codes, and sample messages per scheme.</p>
      </div>
      <Tabs
        tabs={[
          { id: 'paths', label: 'Readers and writers' },
          { id: 'model', label: 'Canonical model' },
          { id: 'types', label: 'Generated types' },
          { id: 'registry', label: 'Code registry' },
          { id: 'samples', label: 'Sample messages' },
        ]}
        value={tab}
        onChange={(t) => go(`/data?tab=${t}`)}
      />
      {tab === 'paths' && paths}
      {tab === 'model' && <ModelBrowser />}
      {tab === 'types' && <TypesTab initial={query.get('activity') ?? ''} />}
      {tab === 'registry' && <RegistryTab />}
      {tab === 'samples' && <SamplesTab />}
    </div>
  );
}

// ------------------------------------------------------------------ model

function ModelBrowser() {
  const model = useModel();
  const [text, setText] = useState('');
  const [personal, setPersonal] = useState(false);
  const dq = useDebounced(text, 250);
  const search = useLoad(() => (dq ? get(`/api/model${q({ q: dq })}`) : Promise.resolve(undefined)), [dq]);
  const [open, setOpen] = useState<Set<string>>(() => new Set(['header', 'payment']));
  const toggle = (p: string) => setOpen((s) => {
    const n = new Set(s);
    if (n.has(p)) n.delete(p);
    else n.add(p);
    return n;
  });
  const counts = useMemo(() => ({ all: model?.flat.length ?? 0, personal: model?.flat.filter((n) => n.personalData).length ?? 0 }), [model]);
  const matches: ModelNode[] = (search.data?.matches ?? []).filter((n: ModelNode) => !personal || n.personalData);
  return (
    <>
      <div className="toolbar">
        <input type="search" className="grow" value={text} onChange={(e) => setText(e.target.value)} placeholder="Search an element, a path or an ISO type, for example instructedAmount or Max35Text" aria-label="Search the model" />
        <label className="check"><input type="checkbox" checked={personal} onChange={(e) => setPersonal(e.target.checked)} /> Personal data only</label>
        <span className="muted small">{counts.all} elements, {counts.personal} personal</span>
      </div>
      {dq ? (
        <Section title={`Matches for ${dq}`} count={matches.length}>
          <ErrorBox error={search.error} />
          {search.loading && !search.data ? <Loading /> : <ModelTable nodes={matches} />}
        </Section>
      ) : !model ? <Loading what="the model" /> : (
        <Section title="The context" actions={<><button className="link" onClick={() => setOpen(new Set(model.flat.filter((n) => n.children?.length).map((n) => n.path)))}>Open all</button> <button className="link" onClick={() => setOpen(new Set())}>Close all</button></>}>
          <ul className="mtree root">
            {model.roots.map((n) => <ModelTreeNode key={n.path} n={n} open={open} toggle={toggle} personal={personal} />)}
          </ul>
        </Section>
      )}
    </>
  );
}

function hasPersonal(n: ModelNode): boolean {
  return !!n.personalData || (n.children ?? []).some(hasPersonal);
}

function ModelTreeNode({ n, open, toggle, personal }: { n: ModelNode; open: Set<string>; toggle: (p: string) => void; personal: boolean }) {
  if (personal && !hasPersonal(n)) return null;
  const kids = n.children ?? [];
  const isOpen = open.has(n.path);
  return (
    <li>
      <div className="mtree-row">
        {kids.length ? <button className="link mtree-toggle" onClick={() => toggle(n.path)} aria-expanded={isOpen} aria-label={`${isOpen ? 'Close' : 'Open'} ${n.path}`}>{isOpen ? '▾' : '▸'}</button> : <span className="mtree-toggle" />}
        <span className="mono strong">{n.name}</span>
        <span className="mono muted small">{n.path}</span>
        {n.isoType && <Badge tone="info">{n.isoType}</Badge>}
        {n.personalData && <Badge tone="warn" title="Personal data. Must not appear in workflow state, logs, tests, samples or prompts.">personal data</Badge>}
        {n.origin && n.origin !== 'iso' && <Badge>{n.origin}</Badge>}
        {n.description && <span className="small muted mtree-desc">{n.description}</span>}
      </div>
      {kids.length > 0 && isOpen && <ul className="mtree">{kids.map((c) => <ModelTreeNode key={c.path} n={c} open={open} toggle={toggle} personal={personal} />)}</ul>}
    </li>
  );
}

function ModelTable({ nodes }: { nodes: ModelNode[] }) {
  if (!nodes.length) return <p className="muted">No element matches.</p>;
  return (
    <div className="table-wrap">
      <table className="grid compact">
        <thead><tr><th>Path</th><th>ISO type</th><th>Personal data</th><th>Origin</th><th>Description</th><th></th></tr></thead>
        <tbody>
          {nodes.map((n) => (
            <tr key={n.path}>
              <td className="mono break"><b>{n.path}</b></td>
              <td>{n.isoType && <code>{n.isoType}</code>}</td>
              <td>{n.personalData ? <Badge tone="warn">personal</Badge> : <span className="muted small">no</span>}</td>
              <td className="small">{n.origin}</td>
              <td className="small">{n.description}</td>
              <td><a className="small" href={`#/data?tab=paths&q=${encodeURIComponent(n.path)}`}>readers and writers</a></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ------------------------------------------------------------------ types

function TypesTab({ initial }: { initial: string }) {
  const acts = useCatalogue('activity').filter((a) => a.status === 'published');
  const [name, setName] = useState(initial);
  const [lang, setLang] = useState<'typescript' | 'python'>('typescript');
  const t = useLoad(() => (name ? get(`/api/types/${encodeURIComponent(name)}${q({ lang })}`) : Promise.resolve(undefined)), [name, lang]);
  const download = () => {
    if (!t.data) return;
    const url = URL.createObjectURL(new Blob([t.data.source], { type: 'text/plain' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = t.data.file;
    a.click();
    URL.revokeObjectURL(url);
  };
  return (
    <Section title="Types generated from an activity's reads and writes" actions={t.data && <button onClick={download}>Download {t.data.file}</button>}>
      <p className="muted small">A script-behind sees the context only through these types: what its contract reads and writes over the canonical model. Generated; do not edit.</p>
      <div className="toolbar">
        <select value={name} onChange={(e) => setName(e.target.value)} aria-label="Activity" className="grow">
          <option value="">Choose an activity</option>
          {acts.map((a) => <option key={a.key} value={a.name}>{a.name}</option>)}
        </select>
        <div className="seg" role="group" aria-label="Language">
          <button className={lang === 'typescript' ? 'on' : ''} onClick={() => setLang('typescript')}>TypeScript</button>
          <button className={lang === 'python' ? 'on' : ''} onClick={() => setLang('python')}>Python</button>
        </div>
        {name && <ArtefactLink name={name} />}
      </div>
      <ErrorBox error={t.error} />
      {t.loading && name && !t.data ? <Loading /> : t.data && <pre className="code tall">{t.data.source}</pre>}
    </Section>
  );
}

// ------------------------------------------------------------------ registry

function RegistryTab() {
  const [date, setDate] = useState('2026-11-17');
  const r = useLoad(() => get(`/api/registry${q({ date })}`), [date]);
  const [set, setSet] = useState('');
  const [code, setCode] = useState('');
  const [found, setFound] = useState<any>();
  const [only, setOnly] = useState<'all' | 'missing' | 'unused'>('all');
  const lookup = async () => {
    try {
      setFound(await get(`/api/registry/lookup${q({ set, code, date })}`));
    } catch (e: any) {
      setFound({ error: e.message });
    }
  };
  const d = r.data;
  const rows: any[] = (d?.report?.rows ?? []).filter((x: any) => only === 'all' || (only === 'missing' ? !x.found : !x.usedBy?.length));
  return (
    <>
      <div className="toolbar">
        <label className="field-inline"><span>Registry as of</span><input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></label>
        {d && <><Badge>version {d.version}</Badge> <Badge tone={d.provenance?.status === 'confirmed' ? 'pass' : 'prov'}>{d.provenance?.status}</Badge> <span className="muted small">{d.provenance?.note}</span></>}
      </div>
      <ErrorBox error={r.error} />
      {r.loading && !d ? <Loading /> : d && (
        <div className="cols">
          <div className="col">
            <Section title="Reason-code report" count={d.report?.rows?.length} actions={
              <select value={only} onChange={(e) => setOnly(e.target.value as any)} aria-label="Show">
                <option value="all">Every code the content uses</option>
                <option value="missing">Codes not in the registry</option>
                <option value="unused">Codes nothing uses</option>
              </select>
            }>
              <p className="muted small">Every reason code a rule, simulator or map yields, looked up in the registry by date. A code the registry does not hold is an error to fix.</p>
              <div className="table-wrap tall">
                <table className="grid compact">
                  <thead><tr><th>Code</th><th>In the registry</th><th>Set</th><th>Name</th><th>Used by</th></tr></thead>
                  <tbody>
                    {rows.map((x, i) => (
                      <tr key={i} className={x.found ? '' : 'row-fail'}>
                        <td className="mono">{x.code}</td>
                        <td>{x.found ? <Badge tone="pass">yes</Badge> : <Badge tone="fail">no</Badge>}</td>
                        <td className="small mono">{x.set}</td>
                        <td>{x.name}</td>
                        <td><span className="chips">{(x.usedBy ?? []).map((u: string) => <ArtefactLink key={u} name={u} />)}</span></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </Section>
          </div>
          <div className="col side">
            <Section title="Look up a code">
              <div className="stack">
                <select value={set} onChange={(e) => setSet(e.target.value)} aria-label="Code set">
                  <option value="">Choose a code set</option>
                  {Object.keys(d.sets ?? {}).map((s) => <option key={s}>{s}</option>)}
                </select>
                <input value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} placeholder="AC03" className="mono" aria-label="Code" />
                <button disabled={!set || !code} onClick={lookup}>Look up</button>
                {found && (found.error ? <p className="small fail-text">{found.error}</p> : <p className="small"><code>{found.code}</code> {found.name} <span className="muted">registry {found.version}</span></p>)}
              </div>
            </Section>
            <Section title="Code sets" count={Object.keys(d.sets ?? {}).length}>
              {Object.entries<any>(d.sets ?? {}).map(([k, v]) => (
                <details key={k}>
                  <summary className="mono small">{k} ({Object.keys(v).length})</summary>
                  <table className="grid compact"><tbody>{Object.entries<any>(v).map(([c, n]) => <tr key={c}><td className="mono">{c}</td><td>{n}</td></tr>)}</tbody></table>
                </details>
              ))}
            </Section>
          </div>
        </div>
      )}
    </>
  );
}

// ------------------------------------------------------------------ samples (F-05)

function SamplesTab() {
  const msgs = useLoad(() => get<string[]>('/api/sample-messages'), []);
  const [m, setM] = useState('');
  const [prove, setProve] = useState(true);
  const cur = m || msgs.data?.[0] || '';
  const s = useLoad(() => (cur ? get(`/api/sample-messages/${encodeURIComponent(cur)}${q({ prove: prove ? undefined : 'false' })}`) : Promise.resolve(undefined)), [cur, prove]);
  const [open, setOpen] = useState<string>();
  return (
    <Section title="Valid and invalid samples per scheme message" actions={
      <>
        <select value={cur} onChange={(e) => setM(e.target.value)} aria-label="Message">
          {(msgs.data ?? []).map((x) => <option key={x}>{x}</option>)}
        </select>
        <label className="check small"><input type="checkbox" checked={prove} onChange={(e) => setProve(e.target.checked)} /> Prove each against the scheme rules</label>
      </>
    }>
      <p className="muted small">A valid sample passes the scheme's rule sets. Each invalid one breaks one rule, and the proof shows that rule fires with its reason code. Values are synthetic.</p>
      <ErrorBox error={msgs.error || s.error} />
      {s.loading && !s.data ? <Loading what="the samples" /> : s.data && (
        <table className="grid compact">
          <thead><tr><th>Sample</th><th>Marked</th><th>Breaks</th><th>Proof</th><th>Findings</th><th></th></tr></thead>
          <tbody>
            {s.data.samples.map((x: any) => (
              <Fragment key={x.name}>
                <tr className={x.proof && !x.proof.proved ? 'row-fail' : ''}>
                  <td>{x.name}<div className="mono small muted">{x.fileName}</div></td>
                  <td>{x.valid ? <Badge tone="pass">valid</Badge> : <Badge tone="warn">invalid</Badge>}</td>
                  <td className="small">{x.breaks ? <><ArtefactLink name={x.breaks.rule} /> <code>{x.breaks.reasonCode}</code></> : <span className="muted">nothing</span>}</td>
                  <td>{x.proof ? <><PassFail passed={x.proof.proved} /> {x.proof.mapped ? <span className="muted small">mapped</span> : <span className="fail-text small">did not map</span>}</> : <span className="muted small">not proved</span>}</td>
                  <td className="small">{x.proof?.findings?.length ? x.proof.findings.map((f: any, i: number) => <div key={i}><code>{f.reasonCode}</code> {f.severity} <span className="muted">{f.rule}</span></div>) : <span className="muted">none</span>}</td>
                  <td><button className="link" onClick={() => setOpen(open === x.name ? undefined : x.name)}>{open === x.name ? 'Hide' : 'Show'}</button></td>
                </tr>
                {open === x.name && <tr className="sub"><td colSpan={6}><pre className="code">{x.content}</pre></td></tr>}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </Section>
  );
}
