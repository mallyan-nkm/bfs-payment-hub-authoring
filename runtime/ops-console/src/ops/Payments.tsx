import { useEffect, useMemo, useState } from 'react';
import type { Client, Facets, PaymentRow, SavedFilter, View } from './api';
import { queryToSave } from './work';
import { columnsFor, filtersFor, show, tone } from './format';

// The list: filters every payment has (tenant, scheme, leg, direction, status, outcome, hold, dates, amounts, ids and file
// names), then the declared filters of the chosen scheme's view, and the columns its views declare.

const GENERIC: { key: string; label: string; kind: 'select' | 'text' | 'date' | 'number'; from?: keyof Facets }[] = [
  { key: 'tenant', label: 'Tenant', kind: 'select', from: 'tenants' },
  { key: 'scheme', label: 'Scheme', kind: 'select', from: 'schemes' },
  { key: 'leg', label: 'Leg', kind: 'select', from: 'legs' },
  { key: 'legOutcome', label: 'Leg outcome', kind: 'select', from: 'legOutcomes' },
  { key: 'direction', label: 'Direction', kind: 'select', from: 'directions' },
  { key: 'status', label: 'Status', kind: 'select', from: 'statuses' },
  { key: 'outcome', label: 'Last outcome', kind: 'select', from: 'outcomes' },
  { key: 'hold', label: 'Hold', kind: 'select' },
  { key: 'holdReason', label: 'Hold reason', kind: 'select', from: 'holdReasons' },
  { key: 'from', label: 'Received from', kind: 'date' },
  { key: 'to', label: 'Received to', kind: 'date' },
  { key: 'minAmount', label: 'Amount from', kind: 'number' },
  { key: 'maxAmount', label: 'Amount to', kind: 'number' },
  { key: 'q', label: 'Id, reference or file name', kind: 'text' },
];

function readQuery(): Record<string, string> {
  const i = location.hash.indexOf('?');
  return i < 0 ? {} : Object.fromEntries(new URLSearchParams(location.hash.slice(i + 1)));
}

export function Payments({ api, views, go }: { api: Client; views: View[]; go(h: string): void }) {
  const [facets, setFacets] = useState<Facets | null>(null);
  const [q, setQ] = useState<Record<string, string>>(readQuery);
  const [data, setData] = useState<{ total: number; rows: PaymentRow[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<SavedFilter[]>([]);
  const loadSaved = () => api.filters().then(setSaved).catch(() => setSaved([]));
  useEffect(() => { api.facets().then(setFacets).catch((e) => setError(e.message)); loadSaved(); }, []);
  useEffect(() => {
    let live = true;
    const load = () => api.payments({ ...q, pageSize: '50' }).then((d) => live && setData(d)).catch((e) => live && setError(e.message));
    load();
    const t = setInterval(load, 3000);
    return () => { live = false; clearInterval(t); };
  }, [JSON.stringify(q)]);
  const scheme = q.scheme || (facets?.schemes.length === 1 ? facets.schemes[0] : undefined);
  const columns = useMemo(() => columnsFor(views, scheme), [views, scheme]);
  const declared = useMemo(() => filtersFor(views, scheme), [views, scheme]);
  const set = (k: string, v: string) => {
    const next = { ...q, [k]: v };
    if (!v) delete next[k];
    setQ(next);
    history.replaceState(null, '', `#/payments?${new URLSearchParams(next)}`);
  };
  const options = (from?: keyof Facets) => (from && facets ? (facets[from] as string[]) : []);
  return (
    <section>
      <h1>Payments</h1>
      <form className="filters" aria-label="Filters" onSubmit={(e) => e.preventDefault()}>
        {GENERIC.map((f) => (
          <label key={f.key}>
            <span>{f.label}</span>
            {f.kind === 'select' ? (
              <select name={f.key} value={q[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)}>
                <option value="">All</option>
                {(f.key === 'hold' ? ['open'] : options(f.from)).map((o) => <option key={o} value={o}>{o}</option>)}
              </select>
            ) : (
              <input name={f.key} type={f.kind} value={q[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)} />
            )}
          </label>
        ))}
        {declared.map((f) =>
          f.match === 'range' ? (
            <span key={f.key} className="range">
              <label><span>{f.label} from</span><input name={`f:${f.key}:min`} type="number" value={q[`f:${f.key}:min`] ?? ''} onChange={(e) => set(`f:${f.key}:min`, e.target.value)} /></label>
              <label><span>{f.label} to</span><input name={`f:${f.key}:max`} type="number" value={q[`f:${f.key}:max`] ?? ''} onChange={(e) => set(`f:${f.key}:max`, e.target.value)} /></label>
            </span>
          ) : (
            <label key={f.key}>
              <span>{f.label}</span>
              <input name={`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`} value={q[`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`] ?? ''}
                onChange={(e) => set(`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`, e.target.value)} />
            </label>
          ),
        )}
        <button type="button" className="link" onClick={() => { setQ({}); history.replaceState(null, '', '#/payments'); }}>Clear</button>
      </form>
      {/* Saved filters (R-05-71): each person's own, kept as facts. */}
      <div className="filters" aria-label="Saved filters">
        <label><span>Saved filters</span>
          <select name="saved-filter" value="" onChange={(e) => { const f = saved.find((x) => x.id === e.target.value); if (f) { setQ(f.query); history.replaceState(null, '', `#/payments?${new URLSearchParams(f.query)}`); } }}>
            <option value="">Apply a saved filter…</option>
            {saved.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <button type="button" onClick={async () => {
          const name = window.prompt('Name this filter') ?? '';
          if (!name.trim()) return;
          try { await api.saveFilter(name.trim(), queryToSave(q)); await loadSaved(); } catch (e: any) { setError(e.message); }
        }}>Save this filter</button>
        {saved.map((f) => <button key={f.id} type="button" className="link" aria-label={`Remove saved filter ${f.name}`} onClick={() => api.removeFilter(f.id).then(loadSaved)}>✕ {f.name}</button>)}
      </div>
      {error && <p className="error">{error}</p>}
      <p className="muted" data-testid="total">{data ? `${data.total} payment(s) and bulk(s)` : 'Loading…'}</p>
      <div className="table-wrap">
        <table className="grid" aria-label="Payments">
          <thead>
            <tr>
              <th>Received</th><th>Tenant</th><th>Scheme</th><th>Kind</th><th>Direction</th><th>Reference</th><th className="num">Amount</th><th className="num">Count</th>
              <th>Legs</th>
              {columns.map((c) => <th key={`${c.from}:${c.key}`} className={c.format === 'amount' || c.format === 'count' ? 'num' : ''}>{c.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {data?.rows.map((r) => (
              <tr key={r.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(r.id)}`)} data-testid="payment-row">
                <td>{show(r.acceptedAt, 'datetime')}</td>
                <td>{r.tenant}</td>
                <td>{r.scheme}</td>
                <td>{r.kind}</td>
                <td>{r.direction}</td>
                <td className="mono"><a href={`#/payments/${encodeURIComponent(r.id)}`}>{r.fileName ?? r.messageId ?? r.endToEndId ?? r.id}</a></td>
                <td className="num">{show(r.amount, 'amount')} {r.currency}</td>
                <td className="num">{r.count}</td>
                <td>{r.legs.map((l) => <span key={l.id} className={`badge ${tone(l.lastOutcome)}`} title={l.leg}>{l.title}: {l.lastOutcome ?? '—'}</span>)}</td>
                {columns.map((c) => {
                  const v = c.from === 'subject' ? r.fields?.[c.key] : r.legs.find((l) => l.leg === c.from)?.fields?.[c.key];
                  return <td key={`${c.from}:${c.key}`} className={c.format === 'amount' || c.format === 'count' ? 'num' : ''}>{c.format === 'status' ? <span className={`badge ${tone(v)}`}>{show(v)}</span> : show(v, c.format)}</td>;
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
