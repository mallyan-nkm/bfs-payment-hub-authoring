import { useEffect, useState } from 'react';
import type { Client, ReportGroup, Dashboard, FixedReport } from './api';

// Reports, the dashboard and the CSV export (R-05-59, Issue I-68). Everything here comes from the read model and the
// operations views: the totals, by scheme, status and day; for each scheme the measures and filters its view declares; a
// summary by any dimension of the read model or a view's exact filter; and the list as CSV. No scheme has a screen of its
// own. The server answers only for the tenants the operator holds a role for, and every personal field is masked as its
// view declares, in the export too.

function money(g: { amounts: { currency: string; amount: number }[] }) {
  return g.amounts.map((a) => `${a.amount.toLocaleString('en', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${a.currency}`).join(', ') || '—';
}

function Groups({ title, groups, label }: { title: string; groups: ReportGroup[]; label?: string }) {
  return (
    <div className="card">
      <h3>{title}</h3>
      <table className="grid" aria-label={title}>
        <thead><tr><th>{label ?? 'Value'}</th><th>Payments</th><th>Lines</th><th>Amount</th></tr></thead>
        <tbody>{groups.map((g) => <tr key={g.key}><td className="mono">{g.key}</td><td>{g.payments}</td><td>{g.lines}</td><td>{money(g)}</td></tr>)}</tbody>
      </table>
    </div>
  );
}

export function Reports({ api }: { api: Client }) {
  const [filter, setFilter] = useState<Record<string, string>>({ scheme: '', from: '', to: '' });
  const [dash, setDash] = useState<Dashboard | null>(null);
  const [meta, setMeta] = useState<{ dimensions: string[]; fields: { key: string; label: string; scheme?: string }[] } | null>(null);
  const [by, setBy] = useState('status');
  const [summary, setSummary] = useState<{ groups: ReportGroup[]; payments: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const q = () => Object.fromEntries(Object.entries(filter).filter(([, v]) => v));
  const load = () => {
    setError(null);
    api.dashboard(q()).then(setDash).catch((e) => setError(e.message));
    api.summary({ ...q(), by }).then(setSummary).catch((e) => setError(e.message));
  };
  useEffect(() => { api.reports().then(setMeta).catch((e) => setError(e.message)); load(); }, []);
  useEffect(() => { api.summary({ ...q(), by }).then(setSummary).catch((e) => setError(e.message)); }, [by]);
  const download = async () => {
    const r = await fetch(`/ops/api/reports/export.csv?${new URLSearchParams(q())}`, { headers: api.headers() });
    if (!r.ok) { setError(`The export was refused (${r.status})`); return; }
    const url = URL.createObjectURL(await r.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = r.headers.get('content-disposition')?.match(/filename="?([^";]+)/)?.[1] ?? 'payments.csv';
    a.click();
    URL.revokeObjectURL(url);
  };
  return (
    <section>
      <h1>Reports</h1>
      <div className="filters">
        <label><span>Scheme</span><input name="scheme" value={filter.scheme} onChange={(e) => setFilter({ ...filter, scheme: e.target.value })} placeholder="all" /></label>
        <label><span>From</span><input name="from" type="date" value={filter.from} onChange={(e) => setFilter({ ...filter, from: e.target.value })} /></label>
        <label><span>To</span><input name="to" type="date" value={filter.to} onChange={(e) => setFilter({ ...filter, to: e.target.value })} /></label>
        <button className="primary" onClick={load}>Show</button>
        <button onClick={download}>Export CSV</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {dash && (
        <>
          <div className="cards" aria-label="Totals">
            <div className="card"><h3>Payments</h3><p className="big" data-testid="report-payments">{dash.payments}</p></div>
            <div className="card"><h3>Lines</h3><p className="big">{dash.lines}</p></div>
            <div className="card"><h3>Amount</h3><p className="big">{money(dash)}</p></div>
          </div>
          {dash.cut && <p className="muted">The report is cut short at its limit: narrow the dates.</p>}
          <div className="cards">
            <Groups title="By scheme" groups={dash.byScheme} label="Scheme" />
            <Groups title="By status" groups={dash.byStatus} label="Status" />
            <Groups title="By day" groups={dash.byDay} label="Day" />
            <Groups title="Holds" groups={dash.holds} label="Hold" />
          </div>
          {dash.schemes.map((s) => (
            <div key={s.view} className="card" aria-label={`Scheme ${s.scheme}`}>
              <h2>{s.title}</h2>
              <p className="muted">{s.payments} payment(s), from the view {s.view}</p>
              <table className="grid" aria-label={`Measures of ${s.scheme}`}>
                <thead><tr>{s.measures.map((m) => <th key={m.id}>{m.label}</th>)}</tr></thead>
                <tbody><tr>{s.measures.map((m) => <td key={m.id}>{m.format === 'amount' ? m.total.toLocaleString('en', { minimumFractionDigits: 2 }) : m.total}</td>)}</tr></tbody>
              </table>
              <div className="cards">
                {s.dimensions.map((d) => (
                  <div key={d.id} className="card">
                    <h3>{d.label}</h3>
                    <ul className="plain">{d.values.map((v) => <li key={v.value}><span className="mono">{v.value}</span>: {v.payments}</li>)}</ul>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </>
      )}
      <div className="card">
        <h2>Summary</h2>
        <label><span>Group by</span>{' '}
          <select aria-label="Group by" value={by} onChange={(e) => setBy(e.target.value)}>
            {(meta?.dimensions ?? []).map((d) => <option key={d} value={d}>{d}</option>)}
            {(meta?.fields ?? []).map((f) => <option key={f.key} value={f.key}>{f.scheme ? `${f.scheme}: ` : ''}{f.label}</option>)}
          </select>
        </label>
        {summary && <Groups title={`By ${by}`} groups={summary.groups} />}
      </div>
      <FixedReports api={api} q={q} onError={setError} />
    </section>
  );
}

/** The fixed reports the views declare (R-05-67): each a CSV of declared fields, masked; every download is an audit fact (R-05-65). */
function FixedReports({ api, q, onError }: { api: Client; q(): Record<string, string>; onError(e: string): void }) {
  const [list, setList] = useState<FixedReport[] | null>(null);
  const [last, setLast] = useState<string>('');
  useEffect(() => { api.fixedReports().then(setList).catch((e) => onError(e.message)); }, []);
  const download = async (r: FixedReport) => {
    const query = Object.fromEntries(Object.entries(q()).filter(([k]) => k !== 'scheme'));
    const res = await fetch(`${api.fixedReportUrl(r.view, r.id)}?${new URLSearchParams(query)}`, { headers: api.headers() });
    if (!res.ok) { onError(`The report was refused (${res.status})`); return; }
    setLast(`${r.title}: ${res.headers.get('x-th-rows') ?? '?'} row(s), audit fact ${res.headers.get('x-th-export-fact') ?? '?'}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = res.headers.get('content-disposition')?.match(/filename="?([^";]+)/)?.[1] ?? `${r.id}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };
  if (!list?.length) return null;
  return (
    <div className="card">
      <h2>Fixed reports</h2>
      <table className="grid" aria-label="Fixed reports">
        <thead><tr><th>Report</th><th>Scheme</th><th>One row per</th><th>Columns</th><th /></tr></thead>
        <tbody>{list.map((r) => (
          <tr key={`${r.view}/${r.id}`}>
            <td>{r.title}</td><td>{r.scheme ?? 'all'}</td><td>{r.rows === 'subject' ? 'file' : r.rows === 'lines' ? 'line' : 'upload'}</td>
            <td className="small">{r.columns.map((c) => c.label).join(', ')}</td>
            <td><button onClick={() => download(r)} aria-label={`Download ${r.title}`}>CSV</button></td>
          </tr>
        ))}</tbody>
      </table>
      {last && <p className="muted" data-testid="fixed-report-note">{last}</p>}
    </div>
  );
}
