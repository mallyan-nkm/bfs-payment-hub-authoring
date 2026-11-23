import { useEffect, useState } from 'react';
import { show } from '../ops/format';
import type { Change, RegisterField, RegisterKind, RegistersClient, RegistersInfo, Version } from './api';
import { changedFields, missing, stateLabel, toInput } from './form';

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
              ? <input type="checkbox" name={f.id} checked={edited[f.id] === true} onChange={(e) => setEdited({ ...edited, [f.id]: e.target.checked })} />
              : <input name={f.id} type={f.type === 'date' ? 'date' : 'text'} value={String(edited[f.id] ?? '')} disabled={!!before && f.id === kind.key}
                  placeholder={f.type === 'list' ? 'comma separated' : f.references ? `a code of ${f.references}` : ''}
                  onChange={(e) => setEdited({ ...edited, [f.id]: e.target.value })} />}
          </label>
        ))}
        <label><span>From (optional)</span><input type="date" name="validFrom" value={validFrom} onChange={(e) => setValidFrom(e.target.value)} /></label>
        <label className="wide"><span>Remarks</span><textarea name="remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      </div>
      {need.length > 0 && <p className="muted">Required: {need.join(', ')}</p>}
      <button className="primary" type="submit" disabled={busy || need.length > 0 || Object.keys(fields).length === 0}>Propose the change</button>
      <p className="muted">The change takes effect only when a bank approver other than you consents to it.</p>
    </form>
  );
}

function History({ kind, rows }: { kind: RegisterKind; rows: Version[] }) {
  return (
    <table className="grid" aria-label="History">
      <thead><tr><th>Version</th><th>State</th><th>From</th><th>Made by</th><th>Decided by</th><th>In effect from</th><th>{kind.fields.find((f) => f.id === kind.titleField)?.title ?? 'Record'}</th></tr></thead>
      <tbody>{rows.map((v) => (
        <tr key={v.id} data-testid="version" data-state={v.state}>
          <td>{v.versionNo}</td>
          <td><span className={`badge ${v.state === 'approved' ? 'pass' : v.state === 'rejected' ? 'fail' : 'warn'}`}>{stateLabel(v.state)}</span></td>
          <td>{show(v.validFrom, 'datetime')}</td><td>{v.madeBy}</td><td>{v.decidedBy ?? '—'}</td><td>{show(v.effectiveFrom, 'datetime')}</td>
          <td>{show(v.fields[kind.titleField])}</td>
        </tr>
      ))}</tbody>
    </table>
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
  if (error) return <p className="error">{error}</p>;
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
      {waiting && <p className="badge warn" data-testid="waiting">A change waits for the bank approver's consent.</p>}
      {maker && !waiting && (editing
        ? <RecordForm kind={kind} before={data.current} busy={busy} onSubmit={async (fields, validFrom, remarks) => {
            setBusy(true);
            try { await api.propose(kind.name, tenant, fields, validFrom || undefined, remarks || undefined); setMsg('Proposed: it waits for consent'); setEditing(false); await load(); }
            catch (e: any) { setMsg(String(e.message ?? e)); }
            finally { setBusy(false); }
          }} />
        : <button onClick={() => setEditing(true)}>Change</button>)}
      {msg && <p className="note" role="status">{msg}</p>}
      <h2>History</h2>
      <History kind={kind} rows={data.history} />
      <p><button className="link" onClick={() => go('#/registers/changes')}>Changes waiting for consent</button></p>
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
        <input aria-label="Search" placeholder="Search by id, name, or a lookup field" value={q} onChange={(e) => setQ(e.target.value)} />
        <button type="submit">Search</button>
        {maker && <button type="button" onClick={() => setAdding(!adding)}>New {kind.title.toLowerCase()}</button>}
        <span className="muted">{total} in force</span>
      </form>
      {adding && <RecordForm kind={kind} before={null} busy={busy} onSubmit={async (fields, validFrom, remarks) => {
        setBusy(true);
        try { await api.propose(kind.name, tenant, fields, validFrom || undefined, remarks || undefined); setMsg('Proposed: it waits for consent'); setAdding(false); }
        catch (e: any) { setMsg(String(e.message ?? e)); }
        finally { setBusy(false); }
      }} />}
      {msg && <p className="note" role="status">{msg}</p>}
      <table className="grid" aria-label="Records">
        <thead><tr>{cols.map((c) => <th key={c.id}>{c.title}</th>)}</tr></thead>
        <tbody>{rows?.map((r) => (
          <tr key={String(r[kind.key])} className="clickable" data-testid="record-row" onClick={() => go(`#/registers/${kind.name}/${encodeURIComponent(String(r[kind.key]))}`)}>
            {cols.map((c) => <td key={c.id}>{value(c, r[c.id])}</td>)}
          </tr>
        ))}</tbody>
      </table>
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
      {msg && <p className="note" role="status">{msg}</p>}
      {rows?.length === 0 && <p className="muted">Nothing waits.</p>}
      {rows?.map((c) => {
        const kind = kinds.find((k) => k.name === c.kind);
        // The fields an approver checks first: the record's title and the fields it is looked up by.
        const shown = kind?.fields.filter((f) => f.id !== kind.key && (f.id === kind.titleField || f.lookup || f.type === 'boolean')) ?? [];
        return (
          <div className="card" key={c.id} data-testid="change" data-change={c.id}>
            <h3>{kind?.title ?? c.kind}: {c.count} record(s) <span className="muted mono">{c.id}</span></h3>
            <p className="muted">Proposed by {c.madeBy} on {show(c.madeAt, 'datetime')} from the {c.source}{c.remarks ? `: ${c.remarks}` : ''}</p>
            <table className="grid" aria-label={`Records of ${c.id}`}>
              <thead><tr><th>Key</th><th>Version</th>{shown.map((f) => <th key={f.id}>{f.title}</th>)}</tr></thead>
              <tbody>{c.records.map((r) => (
                <tr key={r.key}><td className="mono">{r.key}</td><td>{r.versionNo}</td>{shown.map((f) => <td key={f.id}>{value(f, r.fields[f.id])}</td>)}</tr>
              ))}</tbody>
            </table>
            {c.mayDecide
              ? <div className="action-buttons"><button className="primary" onClick={() => decide(c, true)}>Consent</button><button onClick={() => decide(c, false)}>Refuse</button></div>
              : <p className="muted" data-testid="not-yours">A bank approver other than its maker consents to it.</p>}
          </div>
        );
      })}
    </section>
  );
}

export function RegistersSection({ api, path, go }: { api: RegistersClient; path: string[]; go(h: string): void }) {
  const [info, setInfo] = useState<RegistersInfo | null | string>(null);
  useEffect(() => { api.info().then(setInfo).catch((e) => setInfo(String(e.message ?? e))); }, []);
  if (info === null) return <p className="muted">Loading…</p>;
  if (typeof info === 'string') return <p className="error" data-testid="registers-refused">{info}</p>;
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
