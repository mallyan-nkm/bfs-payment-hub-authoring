import { useEffect, useState } from 'react';
import type { Client } from './api';
import { show } from './format';

// The console's controls beside the payments: the turnaround breaches (R-05-68), what is due for erasure and the legal holds
// (R-05-70), and the changes of the system that wait for a bank approver's consent: unmasks, erasures, a legal hold's release,
// a rebuild of the read model (R-09-77). Each request and each decision needs a reason.

export function Turnaround({ api, go }: { api: Client; go(h: string): void }) {
  const [open, setOpen] = useState(false);
  const [data, setData] = useState<{ total: number; rows: any[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const load = () => api.breaches(open).then(setData).catch((e) => setError(e.message));
    load();
    const t = setInterval(load, 5000);
    return () => clearInterval(t);
  }, [open]);
  return (
    <section>
      <h1>Turnaround breaches</h1>
      <p className="muted">Payments open, or that ended, later than the bank's turnaround limit after their acceptance. Each breach is alerted once.</p>
      <label><input type="checkbox" checked={open} onChange={(e) => setOpen(e.target.checked)} /> Open payments only</label>
      {error && <p className="error">{error}</p>}
      <p className="muted" data-testid="tat-total">{data ? `${data.total} breach(es)` : 'Loading…'}</p>
      <table className="grid" aria-label="Turnaround breaches">
        <thead><tr><th>Payment</th><th>Scheme</th><th>Accepted</th><th>Ended</th><th>Limit</th><th>Elapsed</th><th>Over by</th><th>Limit from</th></tr></thead>
        <tbody>{data?.rows.map((r) => (
          <tr key={r.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(r.id)}`)}>
            <td className="mono">{r.fileName ?? r.id}</td><td>{r.scheme}</td><td>{show(r.acceptedAt, 'datetime')}</td><td>{r.endedAt ? show(r.endedAt, 'datetime') : 'open'}</td>
            <td>{r.limit}</td><td>{r.elapsed}</td><td>{r.over}</td><td className="mono small">{r.parameter} ({r.source})</td>
          </tr>
        ))}</tbody>
      </table>
    </section>
  );
}

export function Retention({ api }: { api: Client }) {
  const [due, setDue] = useState<any | null>(null);
  const [holds, setHolds] = useState<any[]>([]);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const load = () => {
    api.due().then(setDue).catch((e) => setError(e.message));
    api.legalHolds().then(setHolds).catch(() => setHolds([]));
  };
  useEffect(load, []);
  const ask = async (what: () => Promise<any>, done: string) => {
    setError(null);
    try { await what(); setNote(done); load(); } catch (e: any) { setError(e.message); }
  };
  const reason = (q: string) => window.prompt(q) ?? '';
  return (
    <section>
      <h1>Retention and erasure</h1>
      <p className="muted">A payment is due for erasure once it ended longer ago than the bank's retention period. An erasure is asked for here and carried out when a bank approver, another person, consents. A legal hold blocks it.</p>
      {due && <p className="small">Periods in force: {Object.entries(due.periods ?? {}).map(([k, v]: any) => `${k} ${v.value} (${v.source})`).join('; ') || 'none'}</p>}
      {note && <p className="muted" data-testid="retention-note">{note}</p>}
      {error && <p className="error">{error}</p>}
      <table className="grid" aria-label="Due for erasure">
        <thead><tr><th>Payment</th><th>Scheme</th><th>Ended</th><th>Due</th><th>Legal hold</th><th /></tr></thead>
        <tbody>{due?.rows.map((r: any) => (
          <tr key={r.id}>
            <td className="mono">{r.id}</td><td>{r.scheme}</td><td>{show(r.endedAt, 'datetime')}</td><td>{show(r.dueAt, 'datetime')}</td><td>{r.legalHold ? 'yes' : 'no'}</td>
            <td>
              <button disabled={r.legalHold} onClick={() => ask(() => api.erase(r.id, reason('Why erase it?')), `Erasure of ${r.id} asked for; it waits for a bank approver.`)}>Ask to erase</button>
              <button onClick={() => ask(() => api.placeHold(r.id, reason('The matter the hold is for')), `Legal hold placed on ${r.id}.`)}>Legal hold</button>
            </td>
          </tr>
        ))}</tbody>
      </table>
      <h2>Legal holds</h2>
      <table className="grid" aria-label="Legal holds">
        <thead><tr><th>Payment</th><th>Why</th><th>Placed by</th><th>Placed at</th><th /></tr></thead>
        <tbody>{holds.map((h) => (
          <tr key={h.id}><td className="mono">{h.subject}</td><td>{h.why}</td><td>{h.placedBy}</td><td>{show(h.placedAt, 'datetime')}</td>
            <td><button onClick={() => ask(() => api.releaseHold(h.id, reason('Why release it?')), 'The release waits for a bank approver.')}>Ask to release</button></td></tr>
        ))}</tbody>
      </table>
    </section>
  );
}

export function Consents({ api }: { api: Client }) {
  const [rows, setRows] = useState<any[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [remarks, setRemarks] = useState('');
  const load = () => api.systemPending().then(setRows).catch((e) => { setError(e.message); setRows([]); });
  useEffect(() => { load(); }, []);
  const decide = async (id: string, approve: boolean) => {
    setError(null);
    try { await api.systemDecide(id, approve, remarks); await load(); } catch (e: any) { setError(e.message); }
  };
  return (
    <section>
      <h1>Waiting for a bank approver</h1>
      <p className="muted">Changes of the system another person asked for. The person who asked may not consent.</p>
      <label><span>Reason for your decision</span> <input name="consent-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      {error && <p className="error">{error}</p>}
      {rows?.length === 0 && <p className="muted">Nothing waits.</p>}
      <table className="grid" aria-label="System actions waiting">
        <thead><tr><th>Asked</th><th>What</th><th>About</th><th>Asked by</th><th>Why</th><th /></tr></thead>
        <tbody>{rows?.map((r) => (
          <tr key={r.data.action} data-testid="consent-row">
            <td>{show(r.at, 'datetime')}</td><td>{r.data.name}</td>
            <td className="mono small">{r.data.details ? [r.data.details.subject, r.data.details.field ?? r.data.details.hold].filter(Boolean).join(' · ') : '—'}</td>
            <td>{r.data.by}</td><td>{r.data.why}</td>
            <td><button disabled={!remarks} onClick={() => decide(r.data.action, true)}>Consent</button> <button disabled={!remarks} onClick={() => decide(r.data.action, false)}>Refuse</button></td>
          </tr>
        ))}</tbody>
      </table>
    </section>
  );
}
