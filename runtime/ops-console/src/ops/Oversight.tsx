import { useEffect, useState } from 'react';
import type { Client } from './api';
import { show } from './format';
import { alertOffers, alertState, askedNote, ledgerGroups, ratesWarning, type AlertList, type LedgerBank, type Rates } from './oversight';
import { Button, Checkbox, Input, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

// The fraud alert list (R-05-90), the bank's ledger accounts for supervisors (R-05-91) and the "rates stale" warning (R-05-92).
// An alert is worked as any work item; releasing or refusing its payment is the payment's own action, with the view's rules.

export function Alerts({ api, go }: { api: Client; go(h: string): void }) {
  const [all, setAll] = useState(false);
  const [data, setData] = useState<AlertList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [remarks, setRemarks] = useState('');
  const load = () => api.alerts(all).then((d) => { setData(d); setError(null); }).catch((e) => setError(e.message));
  useEffect(() => { load(); }, [all]);
  const run = async (what: () => Promise<any>, done: string | ((answer: any) => string)) => {
    setError(null);
    try { const answer = await what(); setNote(typeof done === 'string' ? done : done(answer)); setRemarks(''); await load(); } catch (e: any) { setError(e.message); }
  };
  return (
    <section>
      <h1>Fraud alerts</h1>
      <p className="muted">Payments the bank's fraud engine found suspect. Take one, note what you found, then release or refuse its payment: that is the payment's own action, with its second approver where the view asks for one.</p>
      <Checkbox checked={all} onChange={(e) => setAll(e.target.checked)} label={<>Show closed and released alerts</>} />
      <form className="filters" aria-label="Alert work" onSubmit={(e) => e.preventDefault()}>
        <label><span>Remarks</span><Input name="alert-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      </form>
      {note && <p className="muted" data-testid="alert-note">{note}</p>}
      {error && <Alert tone="fail" className="error" role="alert">{error}</Alert>}
      <p className="muted" data-testid="alert-total">{data ? `${data.total} alert(s), ${data.open} open` : 'Loading…'}</p>
      <Table size="small" className="grid" aria-label="Fraud alerts">
        <TableHeader><TableRow><TableHeaderCell>Raised</TableHeaderCell><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Reason</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell className="num">Amount</TableHeaderCell><TableHeaderCell>With</TableHeaderCell><TableHeaderCell>Work</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{data?.rows.map((a) => {
          const o = alertOffers(a);
          return (
            <TableRow key={a.item} data-testid="alert-row">
              <TableCell>{show(a.raisedAt, 'datetime')}</TableCell>
              <TableCell className="mono"><a data-testid="alert-link" href={`#${a.link}`} onClick={(e) => { e.preventDefault(); go(`#${a.link}`); }}>{a.payment}</a></TableCell>
              <TableCell>{a.tenant}</TableCell>
              <TableCell data-testid="alert-reason">{a.reason ?? 'suspect'}{a.step ? ` at ${a.step}` : ''}</TableCell>
              <TableCell data-testid="alert-state">{alertState(a)}</TableCell>
              <TableCell className="num">{show(a.amount, 'amount')} {a.currency}</TableCell>
              <TableCell>{a.escalated ? `Escalated (level ${a.level})` : a.assigneeName ?? a.assignee ?? 'Unassigned'}</TableCell>
              <TableCell className="actions">
                {!a.closed && <Button type="button" onClick={() => run(() => api.work('assign', 'alert', a.item), 'Taken')}>Take</Button>}
                <Button type="button" disabled={!remarks} onClick={() => run(() => api.work('note', 'alert', a.item, remarks), 'Noted')}>Note</Button>
                {o.release && <Button type="button" onClick={() => run(() => api.alertAction(a.item, 'release', remarks || undefined), (r) => askedNote('release', r))}>Release payment</Button>}
                {o.refuse && <Button type="button" disabled={!remarks} onClick={() => run(() => api.alertAction(a.item, 'refuse', remarks), (r) => askedNote('refuse', r))}>Refuse payment</Button>}
                {o.close && <Button type="button" disabled={!remarks} onClick={() => run(() => api.work('close', 'alert', a.item, remarks), 'Closed')}>Close</Button>}
              </TableCell>
            </TableRow>
          );
        })}</TableBody>
      </Table>
    </section>
  );
}

export function Ledger({ api }: { api: Client }) {
  const [banks, setBanks] = useState<LedgerBank[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.ledger().then((d) => setBanks(d.banks)).catch((e) => setError(e.message)); }, []);
  return (
    <section>
      <h1>Ledger accounts</h1>
      <p className="muted">The bank's own accounts that payments post against, as its configuration in force names them. Read-only: they are changed in the studio, as a new configuration release.</p>
      {error && <Alert tone="fail" className="error" role="alert">{error}</Alert>}
      {banks?.map((b) => (
        <div key={b.tenant}>
          <h2>{b.tenant}</h2>
          {ledgerGroups(b).map((g) => (
            <Table size="small" key={g.activity} className="grid" aria-label={`Ledger accounts of ${g.activity}`}>
              <caption className="mono small">{g.activity}</caption>
              <TableHeader><TableRow><TableHeaderCell>Account</TableHeaderCell><TableHeaderCell>Number</TableHeaderCell><TableHeaderCell>From</TableHeaderCell><TableHeaderCell>Configuration</TableHeaderCell></TableRow></TableHeader>
              <TableBody>{g.accounts.map((a) => (
                <TableRow key={a.parameter} data-testid="ledger-row">
                  <TableCell>{a.title}</TableCell>
                  <TableCell className="mono" data-testid="ledger-account">{a.set ? a.account : <span className="muted">not set</span>}</TableCell>
                  <TableCell>{a.effectiveFrom ? show(a.effectiveFrom, 'datetime') : '—'}</TableCell>
                  <TableCell className="mono small">{a.configurationSet ?? '—'}</TableCell>
                </TableRow>
              ))}</TableBody>
            </Table>
          ))}
        </div>
      ))}
    </section>
  );
}

/** The warning in the console's top bar, while a rate in force is older than the bank allows. */
export function RatesBadge({ api }: { api: Client }) {
  const [rates, setRates] = useState<Rates | null>(null);
  useEffect(() => {
    const load = () => api.rates().then(setRates).catch(() => setRates(null));
    load();
    const t = setInterval(load, 60000);
    return () => clearInterval(t);
  }, []);
  const w = ratesWarning(rates);
  if (!w) return null;
  return <Badge className="badge fail" role="status" data-testid="rates-stale" title="A payment refuses a rate older than the bank's limit (fx.RateStale)">{w}</Badge>;
}
