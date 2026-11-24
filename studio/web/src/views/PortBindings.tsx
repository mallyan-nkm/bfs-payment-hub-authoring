import { useEffect, useState } from 'react';
import { get, post } from '../api';
import { Badge, ErrorBox, Issues, Loading, useLoad, useStudio } from '../ui';
import { Button, Input, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';

// The bank's port bindings (R-11-47, Issue I-70): which adapter answers each port, from which moment, with which
// settings. A Bank Configurator edits them in scope b; a credential is a secret reference (secretRef), never a
// setting. Each save is a draft; a Bank Approver approves the change set as a version, and the approval builds the
// bank's configuration release that carries them to the runtime. A switch is a new row with a later validFrom.

interface Row { port: string; adapter: string; validFrom: string; settings: string; secretRef: string }

const toRows = (doc: any): Row[] => (doc?.bindings ?? []).map((b: any) => ({
  port: b.port ?? '', adapter: b.adapter ?? '', validFrom: String(b.validFrom ?? ''), secretRef: b.secretRef ?? '',
  settings: Object.entries(b.settings ?? {}).map(([k, v]) => `${k}=${v}`).join('\n'),
}));

function toDoc(base: any, rows: Row[]) {
  return {
    ...base,
    bindings: rows.map((r) => {
      const settings = Object.fromEntries(r.settings.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => [l.slice(0, l.indexOf('=') < 0 ? l.length : l.indexOf('=')).trim(), l.indexOf('=') < 0 ? '' : l.slice(l.indexOf('=') + 1).trim()]));
      return { port: r.port, adapter: r.adapter, validFrom: r.validFrom, ...(Object.keys(settings).length ? { settings } : {}), ...(r.secretRef ? { secretRef: r.secretRef } : {}) };
    }),
  };
}

export function PortBindings() {
  const { tenant, roles } = useStudio();
  const configurator = roles.includes('bank configurator');
  const data = useLoad(() => get<any>(`/api/tenants/${encodeURIComponent(tenant)}/port-bindings`), [tenant]);
  const [rows, setRows] = useState<Row[]>([]);
  const [issues, setIssues] = useState<any[] | undefined>();
  const [note, setNote] = useState<string>('');
  const [error, setError] = useState<string | undefined>();
  useEffect(() => {
    if (data.data) {
      setRows(toRows(data.data.start));
      setIssues(data.data.issues);
    }
  }, [data.data]);
  if (data.loading) return <Loading />;
  if (data.error) return <ErrorBox error={data.error} />;
  const d = data.data;
  const base = d.start;
  const set = (i: number, k: keyof Row, v: string) => setRows(rows.map((r, j) => (j === i ? { ...r, [k]: v } : r)));
  const run = async (what: 'check' | 'save') => {
    setError(undefined);
    setNote('');
    try {
      const doc = toDoc(base, rows);
      if (what === 'check') setIssues((await post(`/api/tenants/${encodeURIComponent(tenant)}/port-bindings/check`, { doc })).issues);
      else {
        const r = await post('/api/drafts', { doc });
        setIssues(r.issues);
        setNote(`Saved as draft ${r.draft.key}. A bank approver approves it into a configuration release.`);
        data.reload();
      }
    } catch (e: any) {
      setError(e.message);
    }
  };
  return (
    <div className="port-bindings">
      <p className="small">
        {d.released ? <>Released: <code>{d.released.release}</code>. </> : <>Not released from the studio yet: the runtime reads {d.runtime.map((r: any) => <code key={r.file}>{r.file.split('/').pop()}</code>).reduce((a: any, b: any) => [a, ', ', b])}. </>}
        {d.draft ? <Badge tone="warn">draft {d.draft.release}</Badge> : <>Editing release <code>{base.release}</code>.</>}
      </p>
      <Table size="small" className="grid small" aria-label="Port bindings">
        <TableHeader><TableRow><TableHeaderCell>Port</TableHeaderCell><TableHeaderCell>Adapter</TableHeaderCell><TableHeaderCell>Valid from</TableHeaderCell><TableHeaderCell>Settings (key=value)</TableHeaderCell><TableHeaderCell>Secret reference</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>
          {rows.map((r, i) => (
            <TableRow key={i}>
              <TableCell>
                <Select aria-label={`Port ${i + 1}`} value={r.port} disabled={!configurator} onChange={(e) => set(i, 'port', e.target.value)}>
                  {!d.ports.some((p: any) => p.name === r.port) && <option value={r.port}>{r.port || 'choose a port'}</option>}
                  {d.ports.map((p: any) => <option key={p.name} value={p.name}>{p.name}</option>)}
                </Select>
              </TableCell>
              <TableCell><Input style={{ width: '15ch' }} aria-label={`Adapter ${i + 1}`} value={r.adapter} disabled={!configurator} onChange={(e) => set(i, 'adapter', e.target.value)} /></TableCell>
              <TableCell><Input style={{ width: '23ch' }} aria-label={`Valid from ${i + 1}`} value={r.validFrom} disabled={!configurator} onChange={(e) => set(i, 'validFrom', e.target.value)} /></TableCell>
              <TableCell><Textarea aria-label={`Settings ${i + 1}`} value={r.settings} disabled={!configurator} onChange={(e) => set(i, 'settings', e.target.value)} rows={Math.min(6, Math.max(2, r.settings.split('\n').length))} cols={32} /></TableCell>
              <TableCell><Input style={{ width: '25ch' }} aria-label={`Secret reference ${i + 1}`} value={r.secretRef} disabled={!configurator} placeholder="env:NAME" onChange={(e) => set(i, 'secretRef', e.target.value)} /></TableCell>
              <TableCell>{configurator && <Button aria-label={`Remove binding ${i + 1}`} onClick={() => setRows(rows.filter((_, j) => j !== i))}>Remove</Button>}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {configurator && (
        <div className="row">
          <Button onClick={() => setRows([...rows, { port: d.ports[0]?.name ?? '', adapter: '', validFrom: new Date().toISOString().slice(0, 10), settings: '', secretRef: '' }])}>Add binding</Button>
          <Button onClick={() => run('check')}>Check</Button>
          <Button appearance="primary" onClick={() => run('save')}>Save draft</Button>
        </div>
      )}
      <ErrorBox error={error} />
      {note && <p className="small">{note}</p>}
      <Issues issues={issues} empty="Every binding names a port the bank may bind, an adapter and a valid-from date; credentials are references." />
    </div>
  );
}
