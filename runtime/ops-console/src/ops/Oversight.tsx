import { useEffect, useState } from 'react';
import type { Client } from './api';
import { show } from './format';
import { alertOffers, alertState, ledgerGroups, ratesWarning, type AlertList, type LedgerBank, type Rates } from './oversight';

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
  const run = async (what: () => Promise<any>, done: string) => {
    setError(null);
    try { await what(); setNote(done); setRemarks(''); await load(); } catch (e: any) { setError(e.message); }
  };
  return (
    <section>
      <h1>Fraud alerts</h1>
      <p className="muted">Payments the bank's fraud engine found suspect. Take one, note what you found, then release or refuse its payment: that is the payment's own action, with its second approver where the view asks for one.</p>
      <label><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> Show closed and released alerts</label>
      <form className="filters" aria-label="Alert work" onSubmit={(e) => e.preventDefault()}>
        <label><span>Remarks</span><input name="alert-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
      </form>
      {note && <p className="muted" data-testid="alert-note">{note}</p>}
      {error && <p className="error" role="alert">{error}</p>}
      <p className="muted" data-testid="alert-total">{data ? `${data.total} alert(s), ${data.open} open` : 'Loading…'}</p>
      <table className="grid" aria-label="Fraud alerts">
        <thead><tr><th>Raised</th><th>Payment</th><th>Tenant</th><th>Reason</th><th>State</th><th className="num">Amount</th><th>With</th><th>Work</th></tr></thead>
        <tbody>{data?.rows.map((a) => {
          const o = alertOffers(a);
          return (
            <tr key={a.item} data-testid="alert-row">
              <td>{show(a.raisedAt, 'datetime')}</td>
              <td className="mono"><a data-testid="alert-link" href={`#${a.link}`} onClick={(e) => { e.preventDefault(); go(`#${a.link}`); }}>{a.payment}</a></td>
              <td>{a.tenant}</td>
              <td data-testid="alert-reason">{a.reason ?? 'suspect'}{a.step ? ` at ${a.step}` : ''}</td>
              <td data-testid="alert-state">{alertState(a)}</td>
              <td className="num">{show(a.amount, 'amount')} {a.currency}</td>
              <td>{a.escalated ? `Escalated (level ${a.level})` : a.assigneeName ?? a.assignee ?? 'Unassigned'}</td>
              <td className="actions">
                {!a.closed && <button type="button" onClick={() => run(() => api.work('assign', 'alert', a.item), 'Taken')}>Take</button>}
                <button type="button" disabled={!remarks} onClick={() => run(() => api.work('note', 'alert', a.item, remarks), 'Noted')}>Note</button>
                {o.release && <button type="button" onClick={() => run(() => api.alertAction(a.item, 'release', remarks || undefined), 'Release of the payment asked')}>Release payment</button>}
                {o.refuse && <button type="button" disabled={!remarks} onClick={() => run(() => api.alertAction(a.item, 'refuse', remarks), 'Refusal of the payment asked')}>Refuse payment</button>}
                {o.close && <button type="button" disabled={!remarks} onClick={() => run(() => api.work('close', 'alert', a.item, remarks), 'Closed')}>Close</button>}
              </td>
            </tr>
          );
        })}</tbody>
      </table>
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
      {error && <p className="error" role="alert">{error}</p>}
      {banks?.map((b) => (
        <div key={b.tenant}>
          <h2>{b.tenant}</h2>
          {ledgerGroups(b).map((g) => (
            <table key={g.activity} className="grid" aria-label={`Ledger accounts of ${g.activity}`}>
              <caption className="mono small">{g.activity}</caption>
              <thead><tr><th>Account</th><th>Number</th><th>From</th><th>Configuration</th></tr></thead>
              <tbody>{g.accounts.map((a) => (
                <tr key={a.parameter} data-testid="ledger-row">
                  <td>{a.title}</td>
                  <td className="mono" data-testid="ledger-account">{a.set ? a.account : <span className="muted">not set</span>}</td>
                  <td>{a.effectiveFrom ? show(a.effectiveFrom, 'datetime') : '—'}</td>
                  <td className="mono small">{a.configurationSet ?? '—'}</td>
                </tr>
              ))}</tbody>
            </table>
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
  return <span className="badge fail" role="status" data-testid="rates-stale" title="A payment refuses a rate older than the bank's limit (fx.RateStale)">{w}</span>;
}
