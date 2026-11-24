import { useEffect, useMemo, useState } from 'react';
import type { Client, Facets, PaymentRow, SavedFilter, View } from './api';
import { queryToSave } from './work';
import { columnsFor, filtersFor, show, tone } from './format';
import { Button, Card, Input, Link, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, ToggleButton, Toolbar } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

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
              <Select name={f.key} value={q[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)}>
                <option value="">All</option>
                {(f.key === 'hold' ? ['open'] : options(f.from)).map((o) => <option key={o} value={o}>{o}</option>)}
              </Select>
            ) : (
              <Input name={f.key} type={f.kind} value={q[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)} />
            )}
          </label>
        ))}
        {declared.map((f) =>
          f.match === 'range' ? (
            <span key={f.key} className="range">
              <label><span>{f.label} from</span><Input name={`f:${f.key}:min`} type="number" value={q[`f:${f.key}:min`] ?? ''} onChange={(e) => set(`f:${f.key}:min`, e.target.value)} /></label>
              <label><span>{f.label} to</span><Input name={`f:${f.key}:max`} type="number" value={q[`f:${f.key}:max`] ?? ''} onChange={(e) => set(`f:${f.key}:max`, e.target.value)} /></label>
            </span>
          ) : (
            <label key={f.key}>
              <span>{f.label}</span>
              <Input name={`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`} value={q[`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`] ?? ''}
                onChange={(e) => set(`f:${f.key}${f.match === 'contains' ? ':contains' : ''}`, e.target.value)} />
            </label>
          ),
        )}
        <Link as="button" type="button" onClick={() => { setQ({}); history.replaceState(null, '', '#/payments'); }}>Clear</Link>
      </form>
      {/* Saved filters (R-05-71): each person's own, kept as facts. */}
      <div className="filters" aria-label="Saved filters">
        <label><span>Saved filters</span>
          <Select name="saved-filter" value="" onChange={(e) => { const f = saved.find((x) => x.id === e.target.value); if (f) { setQ(f.query); history.replaceState(null, '', `#/payments?${new URLSearchParams(f.query)}`); } }}>
            <option value="">Apply a saved filter…</option>
            {saved.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </Select>
        </label>
        <Button type="button" onClick={async () => {
          const name = window.prompt('Name this filter') ?? '';
          if (!name.trim()) return;
          try { await api.saveFilter(name.trim(), queryToSave(q)); await loadSaved(); } catch (e: any) { setError(e.message); }
        }}>Save this filter</Button>
        {saved.map((f) => <Link as="button" key={f.id} type="button" aria-label={`Remove saved filter ${f.name}`} onClick={() => api.removeFilter(f.id).then(loadSaved)}>✕ {f.name}</Link>)}
      </div>
      {error && <Alert tone="fail" className="error">{error}</Alert>}
      {/* Summary cards of the page shown, from the rows already loaded: no call of their own. */}
      {data && (
        <div className="summary-cards" aria-label="Summary of the payments shown">
          {([['Shown', data.rows.length, 'info'], ['Settled', data.rows.filter((r) => tone(r.status) === 'pass').length, 'pass'], ['Waiting', data.rows.filter((r) => tone(r.status) === 'warn' || r.hold).length, 'warn'], ['Refused or rejected', data.rows.filter((r) => tone(r.status) === 'fail').length, 'fail']] as const).map(([label, n, t]) => (
            <Card key={label} className={`summary-card ${t}`} appearance="filled-alternative" size="small">
              <span className="summary-label">{label}</span>
              <span className="summary-value">{n}</span>
            </Card>
          ))}
        </div>
      )}
      {/* Quick status filters: the same filter as the Status list above. */}
      {facets && facets.statuses.length > 0 && (
        <Toolbar aria-label="Status" className="quick-filters" size="small">
          <ToggleButton size="small" shape="circular" checked={!q.status} onClick={() => set('status', '')}>All</ToggleButton>
          {facets.statuses.map((st) => <ToggleButton key={st} size="small" shape="circular" checked={q.status === st} onClick={() => set('status', q.status === st ? '' : st)}>{st}</ToggleButton>)}
        </Toolbar>
      )}
      <p className="muted" data-testid="total">{data ? `${data.total} payment(s) and bulk(s)` : 'Loading…'}</p>
      <div className="table-wrap">
        <Table size="small" className="grid" aria-label="Payments">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Received</TableHeaderCell><TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Direction</TableHeaderCell><TableHeaderCell>Reference</TableHeaderCell><TableHeaderCell className="num">Amount</TableHeaderCell><TableHeaderCell className="num">Count</TableHeaderCell>
              <TableHeaderCell>Legs</TableHeaderCell>
              {columns.map((c) => <TableHeaderCell key={`${c.from}:${c.key}`} className={c.format === 'amount' || c.format === 'count' ? 'num' : ''}>{c.label}</TableHeaderCell>)}
            </TableRow>
          </TableHeader>
          <TableBody>
            {data?.rows.map((r) => (
              <TableRow key={r.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(r.id)}`)} data-testid="payment-row">
                <TableCell>{show(r.acceptedAt, 'datetime')}</TableCell>
                <TableCell>{r.tenant}</TableCell>
                <TableCell>{r.scheme}</TableCell>
                <TableCell>{r.kind}</TableCell>
                <TableCell>{r.direction}</TableCell>
                <TableCell className="mono"><a href={`#/payments/${encodeURIComponent(r.id)}`}>{r.fileName ?? r.messageId ?? r.endToEndId ?? r.id}</a></TableCell>
                <TableCell className="num">{show(r.amount, 'amount')} {r.currency}</TableCell>
                <TableCell className="num">{r.count}</TableCell>
                <TableCell>{r.legs.map((l) => <Badge key={l.id} className={`badge ${tone(l.lastOutcome)}`} title={l.leg}>{l.title}: {l.lastOutcome ?? '—'}</Badge>)}</TableCell>
                {columns.map((c) => {
                  const v = c.from === 'subject' ? r.fields?.[c.key] : r.legs.find((l) => l.leg === c.from)?.fields?.[c.key];
                  return <TableCell key={`${c.from}:${c.key}`} className={c.format === 'amount' || c.format === 'count' ? 'num' : ''}>{c.format === 'status' ? <Badge className={`badge ${tone(v)}`}>{show(v)}</Badge> : show(v, c.format)}</TableCell>;
                })}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </section>
  );
}
