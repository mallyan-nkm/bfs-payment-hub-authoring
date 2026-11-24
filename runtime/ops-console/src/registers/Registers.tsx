import { useEffect, useState } from 'react';
import { show } from '../ops/format';
import type { Change, RegisterField, RegisterKind, RegistersClient, RegistersInfo, Version } from './api';
import { changedFields, missing, stateLabel, toInput } from './form';
import { Button, Card, Checkbox, Input, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

// The registers area (R-05-62 to R-05-64): the bank's registers, starting with the corporate registration. Rendered from each
// kind's declaration: its key, its fields and their types, which are masked. A maker proposes a change; it waits until a
// different person with the bank approver's role consents, and the history shows every version.

function value(f: RegisterField, v: unknown): string {
  if (f.type === 'boolean') return v === true ? 'Yes' : v === false ? 'No' : '—';
  if (Array.isArray(v)) return v.length ? v.join(', ') : '—';
  return show(v, f.type === 'date' ? 'date' : undefined);
}

function RecordForm({ kind, before, onSubmit, busy }: { kind: RegisterKind; before: Record<string, any> | null; onSubmit(fields: Record<string, unknown>, validFrom: string, remarks: string): void; busy: boolean }) {
  const [edited, setEdited] = useState<Record<string, string | boolean>>(() => Object.fromEntries(kind.fields.map((f) => [f.id, toInput(f, before?.[f.id])])));
  const [validFrom, setValidFrom] = useState('');
  const [remarks, setRemarks] = useState('');
  const need = before ? [] : missing(kind, edited);
  const fields = changedFields(kind, before, edited);
  return (
    <form className="register-form" aria-label="Propose a change" onSubmit={(e) => { e.preventDefault(); onSubmit(fields, validFrom, remarks); }}>
      <div className="form-grid">
        {kind.fields.map((f) => (
          <label key={f.id} data-field={f.id}>
            <span>{f.title}{f.required ? ' *' : ''}{f.personal ? <span className="muted"> (personal, masked)</span> : null}</span>
            {f.type === 'boolean'
              ? <Checkbox name={f.id} checked={edited[f.id] === true} onChange={(e) => setEdited({ ...edited, [f.id]: e.target.checked })} />
              : <Input name={f.id} type={f.type === 'date' ? 'date' : 'text'} value={String(edited[f.id] ?? '')} disabled={!!before && f.id === kind.key}
                  placeholder={f.type === 'list' ? 'comma separated' : f.references ? `a code of ${f.references}` : ''}
                  onChange={(e) => setEdited({ ...edited, [f.id]: e.target.value })} />}
          </label>
        ))}
        <label><span>From (optional)</span><Input type="date" name="validFrom" value={validFrom} onChange={(e) => setValidFrom(e.target.value)} /></label>
        <label className="wide"><span>Remarks</span><Textarea name="remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      </div>
      {need.length > 0 && <p className="muted">Required: {need.join(', ')}</p>}
      <Button appearance="primary" type="submit" disabled={busy || need.length > 0 || Object.keys(fields).length === 0}>Propose the change</Button>
      <p className="muted">The change takes effect only when a bank approver other than you consents to it.</p>
    </form>
  );
}

function History({ kind, rows }: { kind: RegisterKind; rows: Version[] }) {
  return (
    <Table size="small" className="grid" aria-label="History">
      <TableHeader><TableRow><TableHeaderCell>Version</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>From</TableHeaderCell><TableHeaderCell>Made by</TableHeaderCell><TableHeaderCell>Decided by</TableHeaderCell><TableHeaderCell>In effect from</TableHeaderCell><TableHeaderCell>{kind.fields.find((f) => f.id === kind.titleField)?.title ?? 'Record'}</TableHeaderCell></TableRow></TableHeader>
      <TableBody>{rows.map((v) => (
        <TableRow key={v.id} data-testid="version" data-state={v.state}>
          <TableCell>{v.versionNo}</TableCell>
          <TableCell><Badge className={`badge ${v.state === 'approved' ? 'pass' : v.state === 'rejected' ? 'fail' : 'warn'}`}>{stateLabel(v.state)}</Badge></TableCell>
          <TableCell>{show(v.validFrom, 'datetime')}</TableCell><TableCell>{v.madeBy}</TableCell><TableCell>{v.decidedBy ?? '—'}</TableCell><TableCell>{show(v.effectiveFrom, 'datetime')}</TableCell>
          <TableCell>{show(v.fields[kind.titleField])}</TableCell>
        </TableRow>
      ))}</TableBody>
    </Table>
  );
}

function RecordDetail({ api, kind, keyValue, tenant, maker, go }: { api: RegistersClient; kind: RegisterKind; keyValue: string; tenant: string; maker: boolean; go(h: string): void }) {
  const [data, setData] = useState<{ current: Record<string, any> | null; history: Version[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = () => api.record(kind.name, keyValue, tenant).then(setData).catch((e) => setError(String(e.message ?? e)));
  useEffect(() => { load(); }, [keyValue]);
  if (error) return <Alert tone="fail" className="error">{error}</Alert>;
  if (!data) return <p className="muted">Loading…</p>;
  const waiting = data.history.some((v) => v.state === 'pending');
  return (
    <section>
      <p><a href={`#/registers/${kind.name}`}>{kind.title}</a></p>
      <h1 className="mono">{keyValue}</h1>
      {data.current ? (
        <dl className="fields" data-testid="record">
          {kind.fields.map((f) => <div key={f.id} data-field={f.id}><dt>{f.title}</dt><dd>{value(f, data.current![f.id])}</dd></div>)}
        </dl>
      ) : <p className="muted" data-testid="not-in-force">No version is in force yet.</p>}
      {waiting && <Alert tone="warn" className="waiting" data-testid="waiting">A change waits for the bank approver's consent.</Alert>}
      {maker && !waiting && (editing
        ? <RecordForm kind={kind} before={data.current} busy={busy} onSubmit={async (fields, validFrom, remarks) => {
            setBusy(true);
            try { await api.propose(kind.name, tenant, fields, validFrom || undefined, remarks || undefined); setMsg('Proposed: it waits for consent'); setEditing(false); await load(); }
            catch (e: any) { setMsg(String(e.message ?? e)); }
            finally { setBusy(false); }
          }} />
        : <Button onClick={() => setEditing(true)}>Change</Button>)}
      {msg && <Alert tone="info" className="note" role="status">{msg}</Alert>}
      <h2>History</h2>
      <History kind={kind} rows={data.history} />
      <p><Link as="button" onClick={() => go('#/registers/changes')}>Changes waiting for consent</Link></p>
    </section>
  );
}

function RecordList({ api, kind, tenant, maker, go }: { api: RegistersClient; kind: RegisterKind; tenant: string; maker: boolean; go(h: string): void }) {
  const [q, setQ] = useState('');
  const [rows, setRows] = useState<Record<string, any>[] | null>(null);
  const [total, setTotal] = useState(0);
  const [adding, setAdding] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = (text = q) => api.search(kind.name, { tenant, ...(text ? { q: text } : {}) }).then((r) => { setRows(r.rows); setTotal(r.total); }).catch((e) => setMsg(String(e.message ?? e)));
  useEffect(() => { load(); }, [kind.name, tenant]);
  const cols = [kind.fields.find((f) => f.id === kind.key)!, ...kind.fields.filter((f) => f.id === kind.titleField && f.id !== kind.key), ...kind.fields.filter((f) => f.lookup && f.id !== kind.key && f.type !== 'list'), ...kind.fields.filter((f) => f.type === 'boolean').slice(0, 2)];
  return (
    <section>
      <h1>{kind.title}</h1>
      <p className="muted">{kind.description}</p>
      <form className="filters" onSubmit={(e) => { e.preventDefault(); load(); }}>
        <Input aria-label="Search" placeholder="Search by id, name, or a lookup field" value={q} onChange={(e) => setQ(e.target.value)} />
        <Button type="submit">Search</Button>
        {maker && <Button type="button" onClick={() => setAdding(!adding)}>New {kind.title.toLowerCase()}</Button>}
        <span className="muted">{total} in force</span>
      </form>
      {adding && <RecordForm kind={kind} before={null} busy={busy} onSubmit={async (fields, validFrom, remarks) => {
        setBusy(true);
        try { await api.propose(kind.name, tenant, fields, validFrom || undefined, remarks || undefined); setMsg('Proposed: it waits for consent'); setAdding(false); }
        catch (e: any) { setMsg(String(e.message ?? e)); }
        finally { setBusy(false); }
      }} />}
      {msg && <Alert tone="info" className="note" role="status">{msg}</Alert>}
      <Table size="small" className="grid" aria-label="Records">
        <TableHeader><TableRow>{cols.map((c) => <TableHeaderCell key={c.id}>{c.title}</TableHeaderCell>)}</TableRow></TableHeader>
        <TableBody>{rows?.map((r) => (
          <TableRow key={String(r[kind.key])} className="clickable" data-testid="record-row" onClick={() => go(`#/registers/${kind.name}/${encodeURIComponent(String(r[kind.key]))}`)}>
            {cols.map((c) => <TableCell key={c.id}>{value(c, r[c.id])}</TableCell>)}
          </TableRow>
        ))}</TableBody>
      </Table>
      {rows?.length === 0 && <p className="muted">No record in force.</p>}
    </section>
  );
}

function Changes({ api, kinds }: { api: RegistersClient; kinds: RegisterKind[] }) {
  const [rows, setRows] = useState<Change[] | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const load = () => api.pending().then(setRows).catch((e) => setMsg(String(e.message ?? e)));
  useEffect(() => { load(); }, []);
  const decide = async (c: Change, approve: boolean) => {
    try { await api.decide(c.id, approve); setMsg(`${approve ? 'Consented to' : 'Refused'} ${c.id}`); await load(); }
    catch (e: any) { setMsg(String(e.message ?? e)); }
  };
  return (
    <section>
      <h1>Register changes waiting for consent</h1>
      {msg && <Alert tone="info" className="note" role="status">{msg}</Alert>}
      {rows?.length === 0 && <p className="muted">Nothing waits.</p>}
      {rows?.map((c) => {
        const kind = kinds.find((k) => k.name === c.kind);
        // The fields an approver checks first: the record's title and the fields it is looked up by.
        const shown = kind?.fields.filter((f) => f.id !== kind.key && (f.id === kind.titleField || f.lookup || f.type === 'boolean')) ?? [];
        return (
          <Card className="card" key={c.id} data-testid="change" data-change={c.id}>
            <h3>{kind?.title ?? c.kind}: {c.count} record(s) <span className="muted mono">{c.id}</span></h3>
            <p className="muted">Proposed by {c.madeBy} on {show(c.madeAt, 'datetime')} from the {c.source}{c.remarks ? `: ${c.remarks}` : ''}</p>
            <Table size="small" className="grid" aria-label={`Records of ${c.id}`}>
              <TableHeader><TableRow><TableHeaderCell>Key</TableHeaderCell><TableHeaderCell>Version</TableHeaderCell>{shown.map((f) => <TableHeaderCell key={f.id}>{f.title}</TableHeaderCell>)}</TableRow></TableHeader>
              <TableBody>{c.records.map((r) => (
                <TableRow key={r.key}><TableCell className="mono">{r.key}</TableCell><TableCell>{r.versionNo}</TableCell>{shown.map((f) => <TableCell key={f.id}>{value(f, r.fields[f.id])}</TableCell>)}</TableRow>
              ))}</TableBody>
            </Table>
            {c.mayDecide
              ? <div className="action-buttons"><Button appearance="primary" onClick={() => decide(c, true)}>Consent</Button><Button onClick={() => decide(c, false)}>Refuse</Button></div>
              : <p className="muted" data-testid="not-yours">A bank approver other than its maker consents to it.</p>}
          </Card>
        );
      })}
    </section>
  );
}

export function RegistersSection({ api, path, go }: { api: RegistersClient; path: string[]; go(h: string): void }) {
  const [info, setInfo] = useState<RegistersInfo | null | string>(null);
  useEffect(() => { api.info().then(setInfo).catch((e) => setInfo(String(e.message ?? e))); }, []);
  if (info === null) return <p className="muted">Loading…</p>;
  if (typeof info === 'string') return <Alert tone="fail" className="error" data-testid="registers-refused">{info}</Alert>;
  const tenants = Object.keys(info.tenants).filter((t) => t !== '*');
  const tenant = tenants[0];
  if (!tenant) return <p className="muted">You have no register role in a bank.</p>;
  const roles = info.tenants[tenant];
  if (path[0] === 'changes') return <Changes api={api} kinds={info.kinds} />;
  const kind = info.kinds.find((k) => k.name === path[0]) ?? info.kinds[0];
  return (
    <div>
      <nav className="subnav" aria-label="Registers">
        {info.kinds.map((k) => <a key={k.name} href={`#/registers/${k.name}`} className={k === kind ? 'active' : ''}>{k.title}</a>)}
        <a href="#/registers/changes">Waiting for consent</a>
      </nav>
      {path[1]
        ? <RecordDetail api={api} kind={kind} keyValue={path[1]} tenant={tenant} maker={roles.maker} go={go} />
        : <RecordList api={api} kind={kind} tenant={tenant} maker={roles.maker} go={go} />}
    </div>
  );
}
