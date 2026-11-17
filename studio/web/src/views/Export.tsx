import { useMemo, useState } from 'react';
import { downloadUrl, get, post, type Summary } from '../api';
import { Badge, ErrorBox, Issues, KV, Loading, Section, Tabs, useLoad, useStudio } from '../ui';

// Export (F-30, F-31): choose workflows, target and tenant; preview; download the zip.

const TARGETS = ['temporal', 'dtfx', 'flowable'] as const;

export function Export({ query }: { query: URLSearchParams }) {
  const { tenant, meta } = useStudio();
  const flows = useLoad(() => get('/api/catalogue?kind=workflow').then((r) => (r.items as Summary[]).filter((i) => i.status === 'published')), [tenant]);
  const [chosen, setChosen] = useState<string[]>(() => (query.get('workflows') ?? '').split(',').filter(Boolean));
  const [target, setTarget] = useState<(typeof TARGETS)[number]>((query.get('target') as any) ?? 'temporal');
  const [exTenant, setExTenant] = useState(tenant);
  const [withSims, setWithSims] = useState(false);
  const [filter, setFilter] = useState('');
  const [res, setRes] = useState<any>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [file, setFile] = useState<string>();
  const [tab, setTab] = useState<'manifest' | 'tasks' | 'files' | 'portability' | 'preview'>('manifest');

  const toggle = (n: string) => setChosen((c) => (c.includes(n) ? c.filter((x) => x !== n) : [...c, n]));
  const preview = async () => {
    setBusy(true);
    setError(undefined);
    try {
      const r = await post('/api/export', { workflows: chosen, target, tenant: exTenant, withSimulators: withSims });
      setRes(r);
      const bpmn = Object.keys(r.preview ?? {}).find((k) => k.endsWith('.bpmn20.xml'));
      setFile(bpmn ?? Object.keys(r.preview ?? {})[0]);
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };

  const portability = useMemo(() => {
    const raw = res?.preview?.['portability.json'];
    if (!raw) return undefined;
    try {
      return JSON.parse(raw) as any[];
    } catch {
      return undefined;
    }
  }, [res]);

  const list = (flows.data ?? []).filter((f) => !filter || f.name.includes(filter) || f.title.toLowerCase().includes(filter.toLowerCase()));
  const href = downloadUrl(`/api/export/download?workflows=${encodeURIComponent(chosen.join(','))}&target=${target}${withSims ? '&withSimulators=true' : ''}`, exTenant);
  const m = res?.manifest;

  return (
    <div className="view">
      <div className="view-head">
        <h2>Export</h2>
        <p className="muted">Choose workflows, a target engine and a tenant. Preview the package and its binding, then download it. Simulators never go into a promotable pack.</p>
      </div>
      <div className="cols">
        <div className="col">
          <Section title="Workflows" count={chosen.length} actions={<input type="search" placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter workflows" />}>
            {flows.loading && !flows.data ? <Loading /> : (
              <ul className="plain checklist">
                {list.map((f) => (
                  <li key={f.key}>
                    <label className="check">
                      <input type="checkbox" checked={chosen.includes(f.name)} onChange={() => toggle(f.name)} />
                      <span className="mono">{f.name}</span> <span className="muted small">{f.type} {f.release}</span> {f.provenance === 'provisional' && <Badge tone="prov">provisional</Badge>}
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
        <div className="col side">
          <Section title="Target and tenant">
            <div className="stack">
              <div className="seg" role="radiogroup" aria-label="Target">
                {TARGETS.map((t) => <button key={t} role="radio" aria-checked={target === t} className={target === t ? 'on' : ''} onClick={() => setTarget(t)}>{t}</button>)}
              </div>
              <label className="field">
                <span>Tenant</span>
                <select value={exTenant} onChange={(e) => setExTenant(e.target.value)} aria-label="Export tenant">
                  {(meta?.tenants ?? [tenant]).map((t) => <option key={t} value={t}>{t === meta?.owner ? `${t} (shared pack)` : `${t} (bank pack)`}</option>)}
                </select>
              </label>
              <label className="check">
                <input type="checkbox" checked={withSims} onChange={(e) => setWithSims(e.target.checked)} aria-label="With simulators" />
                <span>With simulators, for a test deployment (the package is then not promotable)</span>
              </label>
              <button className="primary" disabled={!chosen.length || busy} onClick={preview}>{busy ? 'Building…' : 'Preview'}</button>
              <a className={chosen.length ? 'button' : 'button disabled'} href={chosen.length ? href : undefined} download aria-disabled={!chosen.length}>Download the zip</a>
            </div>
          </Section>
        </div>
      </div>
      <ErrorBox error={error} />
      {res && (
        <>
          <Section title="Package" actions={<>{m?.promotable ? <Badge tone="pass">promotable</Badge> : <Badge tone="warn">not promotable</Badge>} {m?.containsSimulators ? <Badge tone="warn">contains simulators</Badge> : <Badge tone="pass">no simulators</Badge>}</>}>
            <KV rows={[
              ['Target', <code>{m?.target}</code>],
              ['Strategy', m?.strategy],
              ['DSL', m?.dsl],
              ['Task naming', <code>{m?.taskNaming}</code>],
              ['Flows', <ul className="plain">{(m?.flows ?? []).map((f: any) => <li key={f.name}><span className="mono">{f.name}@{f.release}</span> <span className="muted small">{f.wfKind}, {f.pins} pins, {String(f.hash).replace('sha256:', '').slice(0, 12)}</span></li>)}</ul>],
            ]} />
            <h4>Issues</h4>
            <Issues issues={res.issues} empty="No issues." />
          </Section>
          <Tabs
            tabs={[
              { id: 'manifest', label: 'Manifest' },
              { id: 'tasks', label: `Tasks (${m?.tasks?.length ?? 0})` },
              { id: 'files', label: `Files (${Object.keys(res.files ?? {}).length})` },
              { id: 'portability', label: `Portability (${portability?.length ?? 0})` },
              { id: 'preview', label: 'File preview' },
            ]}
            value={tab}
            onChange={setTab}
          />
          {tab === 'manifest' && <Section title="manifest.yaml"><pre className="code tall">{res.preview?.['manifest.yaml'] ?? JSON.stringify(m, null, 2)}</pre></Section>}
          {tab === 'tasks' && (
            <Section title="Tasks">
              <table className="grid compact">
                <thead><tr><th>Kind</th><th>Task</th><th>Queue</th><th>Binding</th></tr></thead>
                <tbody>{(m?.tasks ?? []).map((t: any, i: number) => <tr key={i}><td>{t.kind}</td><td className="mono">{t.task}</td><td className="mono">{t.queue}</td><td>{t.binding ?? ''}</td></tr>)}</tbody>
              </table>
            </Section>
          )}
          {tab === 'files' && (
            <Section title="Files">
              <table className="grid compact">
                <thead><tr><th>Path</th><th>Bytes</th><th>Hash</th></tr></thead>
                <tbody>
                  {Object.entries<number>(res.files ?? {}).map(([p, n]) => {
                    const h = (m?.files ?? []).find((f: any) => f.path === p)?.hash;
                    return <tr key={p}><td className="mono">{res.preview?.[p] ? <button className="link mono" onClick={() => { setFile(p); setTab('preview'); }}>{p}</button> : p}</td><td>{n}</td><td className="mono small">{h ? String(h).replace('sha256:', '').slice(0, 12) : ''}</td></tr>;
                  })}
                </tbody>
              </table>
            </Section>
          )}
          {tab === 'portability' && (
            <Section title="Portability notes">
              {!portability?.length ? <p className="muted">No portability notes.</p> : (
                <table className="grid compact">
                  <thead><tr><th>Flow</th><th>Construct</th><th>Severity</th><th>Note</th></tr></thead>
                  <tbody>{portability.map((p, i) => <tr key={i}><td className="mono small">{p.flow}</td><td>{p.construct}</td><td><Badge tone={p.severity === 'error' ? 'fail' : p.severity === 'warning' ? 'warn' : 'muted'}>{p.severity}</Badge></td><td>{p.note}</td></tr>)}</tbody>
                </table>
              )}
            </Section>
          )}
          {tab === 'preview' && (
            <Section title="File preview" actions={
              <select value={file} onChange={(e) => setFile(e.target.value)} aria-label="Preview file">
                {Object.keys(res.preview ?? {}).map((k) => <option key={k}>{k}</option>)}
              </select>
            }>
              {file && <pre className="code tall">{res.preview[file]}</pre>}
            </Section>
          )}
        </>
      )}
    </div>
  );
}
