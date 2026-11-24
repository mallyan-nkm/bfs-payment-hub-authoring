import { useEffect, useState } from 'react';
import type { Client, ReportGroup, Dashboard, FixedReport } from './api';
import { Button, Card, Input, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert } from '../ui';

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
    <Card className="card">
      <h3>{title}</h3>
      <Table size="small" className="grid" aria-label={title}>
        <TableHeader><TableRow><TableHeaderCell>{label ?? 'Value'}</TableHeaderCell><TableHeaderCell>Payments</TableHeaderCell><TableHeaderCell>Lines</TableHeaderCell><TableHeaderCell>Amount</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{groups.map((g) => <TableRow key={g.key}><TableCell className="mono">{g.key}</TableCell><TableCell>{g.payments}</TableCell><TableCell>{g.lines}</TableCell><TableCell>{money(g)}</TableCell></TableRow>)}</TableBody>
      </Table>
    </Card>
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
        <label><span>Scheme</span><Input name="scheme" value={filter.scheme} onChange={(e) => setFilter({ ...filter, scheme: e.target.value })} placeholder="all" /></label>
        <label><span>From</span><Input name="from" type="date" value={filter.from} onChange={(e) => setFilter({ ...filter, from: e.target.value })} /></label>
        <label><span>To</span><Input name="to" type="date" value={filter.to} onChange={(e) => setFilter({ ...filter, to: e.target.value })} /></label>
        <Button appearance="primary" onClick={load}>Show</Button>
        <Button onClick={download}>Export CSV</Button>
      </div>
      {error && <Alert tone="fail" className="error" role="alert">{error}</Alert>}
      {dash && (
        <>
          <div className="cards" aria-label="Totals">
            <Card className="card"><h3>Payments</h3><p className="big" data-testid="report-payments">{dash.payments}</p></Card>
            <Card className="card"><h3>Lines</h3><p className="big">{dash.lines}</p></Card>
            <Card className="card"><h3>Amount</h3><p className="big">{money(dash)}</p></Card>
          </div>
          {dash.cut && <p className="muted">The report is cut short at its limit: narrow the dates.</p>}
          <div className="cards">
            <Groups title="By scheme" groups={dash.byScheme} label="Scheme" />
            <Groups title="By status" groups={dash.byStatus} label="Status" />
            <Groups title="By day" groups={dash.byDay} label="Day" />
            <Groups title="Holds" groups={dash.holds} label="Hold" />
          </div>
          {dash.schemes.map((s) => (
            <Card key={s.view} className="card" aria-label={`Scheme ${s.scheme}`}>
              <h2>{s.title}</h2>
              <p className="muted">{s.payments} payment(s), from the view {s.view}</p>
              <Table size="small" className="grid" aria-label={`Measures of ${s.scheme}`}>
                <TableHeader><TableRow>{s.measures.map((m) => <TableHeaderCell key={m.id}>{m.label}</TableHeaderCell>)}</TableRow></TableHeader>
                <TableBody><TableRow>{s.measures.map((m) => <TableCell key={m.id}>{m.format === 'amount' ? m.total.toLocaleString('en', { minimumFractionDigits: 2 }) : m.total}</TableCell>)}</TableRow></TableBody>
              </Table>
              <div className="cards">
                {s.dimensions.map((d) => (
                  <Card key={d.id} className="card">
                    <h3>{d.label}</h3>
                    <ul className="plain">{d.values.map((v) => <li key={v.value}><span className="mono">{v.value}</span>: {v.payments}</li>)}</ul>
                  </Card>
                ))}
              </div>
            </Card>
          ))}
        </>
      )}
      <Card className="card">
        <h2>Summary</h2>
        <label><span>Group by</span>{' '}
          <Select aria-label="Group by" value={by} onChange={(e) => setBy(e.target.value)}>
            {(meta?.dimensions ?? []).map((d) => <option key={d} value={d}>{d}</option>)}
            {(meta?.fields ?? []).map((f) => <option key={f.key} value={f.key}>{f.scheme ? `${f.scheme}: ` : ''}{f.label}</option>)}
          </Select>
        </label>
        {summary && <Groups title={`By ${by}`} groups={summary.groups} />}
      </Card>
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
    <Card className="card">
      <h2>Fixed reports</h2>
      <Table size="small" className="grid" aria-label="Fixed reports">
        <TableHeader><TableRow><TableHeaderCell>Report</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>One row per</TableHeaderCell><TableHeaderCell>Columns</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{list.map((r) => (
          <TableRow key={`${r.view}/${r.id}`}>
            <TableCell>{r.title}</TableCell><TableCell>{r.scheme ?? 'all'}</TableCell><TableCell>{r.rows === 'subject' ? 'file' : r.rows === 'lines' ? 'line' : 'upload'}</TableCell>
            <TableCell className="small">{r.columns.map((c) => c.label).join(', ')}</TableCell>
            <TableCell><Button onClick={() => download(r)} aria-label={`Download ${r.title}`}>CSV</Button></TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {last && <p className="muted" data-testid="fixed-report-note">{last}</p>}
    </Card>
  );
}
