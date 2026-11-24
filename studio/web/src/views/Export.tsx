import { useMemo, useState } from 'react';
import { downloadUrl, get, post, type Summary } from '../api';
import { Badge, ErrorBox, Issues, KV, Loading, Section, Tabs, useLoad, useStudio } from '../ui';
import { GitBranch } from './GitBranch';
import { Button, Checkbox, Link, Radio, RadioGroup, SearchBox, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';

// Export (F-30, F-31): choose workflows, target and tenant; preview; download the zip.

const TARGETS = ['temporal', 'dtfx', 'flowable', 'elsa'] as const;

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
          <Section title="Workflows" count={chosen.length} actions={<SearchBox placeholder="Filter" value={filter} onChange={(_, d) => setFilter(d.value)} aria-label="Filter workflows" />}>
            {flows.loading && !flows.data ? <Loading /> : (
              <ul className="plain checklist">
                {list.map((f) => (
                  <li key={f.key}>
                    <Checkbox className="check" checked={chosen.includes(f.name)} onChange={() => toggle(f.name)} label={<>
                      <span className="mono">{f.name}</span> <span className="muted small">{f.type} {f.release}</span> {f.provenance === 'provisional' && <Badge tone="prov">provisional</Badge>}
                    </>} />
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
        <div className="col side">
          <Section title="Target and tenant">
            <div className="stack">
              <RadioGroup layout="horizontal" aria-label="Target" value={target} onChange={(_, d) => setTarget(d.value as (typeof TARGETS)[number])}>
                {TARGETS.map((t) => <Radio key={t} value={t} label={t} />)}
              </RadioGroup>
              <label className="field">
                <span>Tenant</span>
                <Select value={exTenant} onChange={(e) => setExTenant(e.target.value)} aria-label="Export tenant">
                  {(meta?.tenants ?? [tenant]).filter((t) => t === tenant).map((t) => <option key={t} value={t}>{t === meta?.owner ? `${t} (shared pack)` : `${t} (bank pack)`}</option>)}
                </Select>
              </label>
              <Checkbox className="check" checked={withSims} onChange={(e) => setWithSims(e.target.checked)} aria-label="With simulators" label="With simulators, for a test deployment (the package is then not promotable)" />
              <Button appearance="primary" disabled={!chosen.length || busy} onClick={preview}>{busy ? 'Building…' : 'Preview'}</Button>
              <Button as="a" icon={<ArrowDownloadRegular />} disabledFocusable={!chosen.length} href={chosen.length ? href : undefined} download>Download the zip</Button>
            </div>
          </Section>
        </div>
      </div>
      <ErrorBox error={error} />
      <GitBranch />
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
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Task</TableHeaderCell><TableHeaderCell>Queue</TableHeaderCell><TableHeaderCell>Binding</TableHeaderCell></TableRow></TableHeader>
                <TableBody>{(m?.tasks ?? []).map((t: any, i: number) => <TableRow key={i}><TableCell>{t.kind}</TableCell><TableCell className="mono">{t.task}</TableCell><TableCell className="mono">{t.queue}</TableCell><TableCell>{t.binding ?? ''}</TableCell></TableRow>)}</TableBody>
              </Table>
            </Section>
          )}
          {tab === 'files' && (
            <Section title="Files">
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Path</TableHeaderCell><TableHeaderCell>Bytes</TableHeaderCell><TableHeaderCell>Hash</TableHeaderCell></TableRow></TableHeader>
                <TableBody>
                  {Object.entries<number>(res.files ?? {}).map(([p, n]) => {
                    const h = (m?.files ?? []).find((f: any) => f.path === p)?.hash;
                    return <TableRow key={p}><TableCell className="mono">{res.preview?.[p] ? <Link as="button" className="mono" onClick={() => { setFile(p); setTab('preview'); }}>{p}</Link> : p}</TableCell><TableCell>{n}</TableCell><TableCell className="mono small">{h ? String(h).replace('sha256:', '').slice(0, 12) : ''}</TableCell></TableRow>;
                  })}
                </TableBody>
              </Table>
            </Section>
          )}
          {tab === 'portability' && (
            <Section title="Portability notes">
              {!portability?.length ? <p className="muted">No portability notes.</p> : (
                <Table size="small" className="grid compact">
                  <TableHeader><TableRow><TableHeaderCell>Flow</TableHeaderCell><TableHeaderCell>Construct</TableHeaderCell><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Note</TableHeaderCell></TableRow></TableHeader>
                  <TableBody>{portability.map((p, i) => <TableRow key={i}><TableCell className="mono small">{p.flow}</TableCell><TableCell>{p.construct}</TableCell><TableCell><Badge tone={p.severity === 'error' ? 'fail' : p.severity === 'warning' ? 'warn' : 'muted'}>{p.severity}</Badge></TableCell><TableCell>{p.note}</TableCell></TableRow>)}</TableBody>
                </Table>
              )}
            </Section>
          )}
          {tab === 'preview' && (
            <Section title="File preview" actions={
              <Select value={file} onChange={(e) => setFile(e.target.value)} aria-label="Preview file">
                {Object.keys(res.preview ?? {}).map((k) => <option key={k}>{k}</option>)}
              </Select>
            }>
              {file && <pre className="code tall">{res.preview[file]}</pre>}
            </Section>
          )}
        </>
      )}
    </div>
  );
}
