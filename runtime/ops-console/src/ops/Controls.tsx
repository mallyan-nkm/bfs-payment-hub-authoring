import { useEffect, useState } from 'react';
import type { Client } from './api';
import { show } from './format';
import { Button, Checkbox, Input, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert } from '../ui';

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
      <Checkbox checked={open} onChange={(e) => setOpen(e.target.checked)} label={<>Open payments only</>} />
      {error && <Alert tone="fail" className="error">{error}</Alert>}
      <p className="muted" data-testid="tat-total">{data ? `${data.total} breach(es)` : 'Loading…'}</p>
      <Table size="small" className="grid" aria-label="Turnaround breaches">
        <TableHeader><TableRow><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Accepted</TableHeaderCell><TableHeaderCell>Ended</TableHeaderCell><TableHeaderCell>Limit</TableHeaderCell><TableHeaderCell>Elapsed</TableHeaderCell><TableHeaderCell>Over by</TableHeaderCell><TableHeaderCell>Limit from</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{data?.rows.map((r) => (
          <TableRow key={r.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(r.id)}`)}>
            <TableCell className="mono">{r.fileName ?? r.id}</TableCell><TableCell>{r.scheme}</TableCell><TableCell>{show(r.acceptedAt, 'datetime')}</TableCell><TableCell>{r.endedAt ? show(r.endedAt, 'datetime') : 'open'}</TableCell>
            <TableCell>{r.limit}</TableCell><TableCell>{r.elapsed}</TableCell><TableCell>{r.over}</TableCell><TableCell className="mono small">{r.parameter} ({r.source})</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
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
      {error && <Alert tone="fail" className="error">{error}</Alert>}
      <Table size="small" className="grid" aria-label="Due for erasure">
        <TableHeader><TableRow><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Ended</TableHeaderCell><TableHeaderCell>Due</TableHeaderCell><TableHeaderCell>Legal hold</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{due?.rows.map((r: any) => (
          <TableRow key={r.id}>
            <TableCell className="mono">{r.id}</TableCell><TableCell>{r.scheme}</TableCell><TableCell>{show(r.endedAt, 'datetime')}</TableCell><TableCell>{show(r.dueAt, 'datetime')}</TableCell><TableCell>{r.legalHold ? 'yes' : 'no'}</TableCell>
            <TableCell>
              <Button disabled={r.legalHold} onClick={() => ask(() => api.erase(r.id, reason('Why erase it?')), `Erasure of ${r.id} asked for; it waits for a bank approver.`)}>Ask to erase</Button>
              <Button onClick={() => ask(() => api.placeHold(r.id, reason('The matter the hold is for')), `Legal hold placed on ${r.id}.`)}>Legal hold</Button>
            </TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      <h2>Legal holds</h2>
      <Table size="small" className="grid" aria-label="Legal holds">
        <TableHeader><TableRow><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell><TableHeaderCell>Placed by</TableHeaderCell><TableHeaderCell>Placed at</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{holds.map((h) => (
          <TableRow key={h.id}><TableCell className="mono">{h.subject}</TableCell><TableCell>{h.why}</TableCell><TableCell>{h.placedBy}</TableCell><TableCell>{show(h.placedAt, 'datetime')}</TableCell>
            <TableCell><Button onClick={() => ask(() => api.releaseHold(h.id, reason('Why release it?')), 'The release waits for a bank approver.')}>Ask to release</Button></TableCell></TableRow>
        ))}</TableBody>
      </Table>
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
      <label><span>Reason for your decision</span> <Input name="consent-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      {error && <Alert tone="fail" className="error">{error}</Alert>}
      {rows?.length === 0 && <p className="muted">Nothing waits.</p>}
      <Table size="small" className="grid" aria-label="System actions waiting">
        <TableHeader><TableRow><TableHeaderCell>Asked</TableHeaderCell><TableHeaderCell>What</TableHeaderCell><TableHeaderCell>About</TableHeaderCell><TableHeaderCell>Asked by</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{rows?.map((r) => (
          <TableRow key={r.data.action} data-testid="consent-row">
            <TableCell>{show(r.at, 'datetime')}</TableCell><TableCell>{r.data.name}</TableCell>
            <TableCell className="mono small">{r.data.details ? [r.data.details.subject, r.data.details.field ?? r.data.details.hold].filter(Boolean).join(' · ') : '—'}</TableCell>
            <TableCell>{r.data.by}</TableCell><TableCell>{r.data.why}</TableCell>
            <TableCell><Button disabled={!remarks} onClick={() => decide(r.data.action, true)}>Consent</Button> <Button disabled={!remarks} onClick={() => decide(r.data.action, false)}>Refuse</Button></TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}
