import { useEffect, useState } from 'react';
import { get, post } from '../api';
import { ErrorBox, Issues, Loading, useLoad, useStudio } from '../ui';
import { Button, Checkbox, Input, Link, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';

// A bank's own reference tables (R-11-52, Issue I-150): its calendar of closing days, its cut-off times and its branches. A Bank
// Configurator adds an edition (the rows in force from a date) and saves it as a draft at the table's next minor release; a Bank
// Approver approves it, and the approval builds the bank's configuration release that carries the table to the runtime, which
// serves it by date under /lookups (R-01-25, R-09-75). Earlier editions stay as they were.

type Row = Record<string, string>;

const cellText = (v: unknown) => (v === undefined || v === null ? '' : String(v));

function typed(col: any, v: string): unknown {
  if (v === '') return undefined;
  if (col.type === 'boolean') return v === 'true';
  if (col.type === 'number') return Number(v);
  return v;
}

export function ReferenceTables() {
  const { tenant, roles } = useStudio();
  const configurator = roles.includes('bank configurator');
  const data = useLoad(() => get<any>(`/api/tenants/${encodeURIComponent(tenant)}/reference-tables`), [tenant]);
  const [name, setName] = useState<string>('');
  const [validFrom, setValidFrom] = useState<string>('');
  const [rows, setRows] = useState<Row[]>([]);
  const [issues, setIssues] = useState<any[] | undefined>();
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | undefined>();
  // A table the Bank Configurator is starting (Issue I-150, R-11-106): its columns and key, then its first edition, saved as a draft at 1.0.0.
  const [fresh, setFresh] = useState<any | undefined>();
  const [creating, setCreating] = useState(false);
  const table = fresh
    ? { name: fresh.name, title: fresh.title, domain: fresh.domain, released: null, draft: null, start: fresh, issues: [] }
    : data.data?.tables?.find((t: any) => t.name === name) ?? data.data?.tables?.[0];
  useEffect(() => {
    if (!table?.start) return;
    setName(table.name);
    const last = table.start.editions?.[table.start.editions.length - 1];
    setRows((last?.rows ?? []).map((r: any) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, cellText(v)]))));
    setValidFrom('');
    setIssues(table.issues);
  }, [table?.name, data.data, fresh]);
  if (data.loading) return <Loading />;
  if (data.error) return <ErrorBox error={data.error} />;
  const newTable = configurator && (creating || !table) && (
    <NewTable tenant={tenant} onStart={(d) => { setFresh(d); setCreating(false); setName(d.name); }} onCancel={table ? () => setCreating(false) : undefined} />
  );
  if (!table) return <div aria-label="Reference tables"><p className="muted small">The bank has no reference table of its own yet.</p>{newTable}</div>;
  const cols: any[] = table.start.columns ?? [];
  const doc = () => ({
    ...table.start,
    editions: [...(table.start.editions ?? []), { validFrom, rows: rows.map((r) => Object.fromEntries(cols.map((c) => [c.id, typed(c, r[c.id] ?? '')]).filter(([, v]) => v !== undefined))) }],
  });
  const run = async (what: 'check' | 'save') => {
    setError(undefined);
    setNote('');
    try {
      if (what === 'check') setIssues((await post(`/api/tenants/${encodeURIComponent(tenant)}/reference-tables/check`, { doc: doc() })).issues);
      else {
        const r = await post('/api/drafts', { doc: doc() });
        setIssues(r.issues);
        setNote(`Saved as draft ${r.draft.key}. A bank approver approves it into a configuration release.`);
        setFresh(undefined);
        data.reload();
      }
    } catch (e: any) { setError(e.message); }
  };
  return (
    <div aria-label="Reference tables">
      <label className="field"><span>Table</span>
        <Select aria-label="Reference table" value={table.name} onChange={(e) => { setFresh(undefined); setName(e.target.value); }}>
          {fresh && <option value={fresh.name}>{fresh.title} ({fresh.name}, new)</option>}
          {data.data.tables.map((t: any) => <option key={t.name} value={t.name}>{t.title} ({t.name})</option>)}
        </Select>
      </label>
      {configurator && !creating && <Button onClick={() => setCreating(true)}>New table</Button>}
      {newTable}
      <p className="small muted">
        Released {table.released?.release ?? 'never'}{table.draft ? `, draft ${table.draft.release} in progress` : ''}. Editions: {(table.released?.editions ?? []).map((e: any) => e.validFrom).join(', ') || 'none'}.
        A new edition starts at its valid-from date; the next release is {table.start.release}.
      </p>
      <label className="field"><span>New edition valid from</span><Input aria-label="Valid from" type="date" value={validFrom} disabled={!configurator} onChange={(e) => setValidFrom(e.target.value)} /></label>
      <Table size="small" className="grid small" aria-label={`Rows of ${table.name}`}>
        <TableHeader><TableRow>{cols.map((c) => <TableHeaderCell key={c.id}>{c.title ?? c.id}</TableHeaderCell>)}<TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{rows.map((r, i) => (
          <TableRow key={i}>
            {cols.map((c) => (
              <TableCell key={c.id}>{c.allowed ? (
                <Select aria-label={`${c.id} ${i + 1}`} value={r[c.id] ?? ''} disabled={!configurator} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, [c.id]: e.target.value } : x)))}>
                  <option value="" />{c.allowed.map((a: any) => <option key={String(a)} value={String(a)}>{String(a)}</option>)}
                </Select>
              ) : (
                <Input style={{ width: '15ch' }} aria-label={`${c.id} ${i + 1}`} value={r[c.id] ?? ''} disabled={!configurator} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, [c.id]: e.target.value } : x)))} />
              )}</TableCell>
            ))}
            <TableCell>{configurator && <Link as="button" onClick={() => setRows(rows.filter((_, j) => j !== i))}>Remove</Link>}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {configurator && (
        <div className="row">
          <Button onClick={() => setRows([...rows, {}])}>Add a row</Button>
          <Button onClick={() => run('check')} disabled={!validFrom}>Check</Button>
          <Button appearance="primary" onClick={() => run('save')} disabled={!validFrom}>Save as draft</Button>
        </div>
      )}
      {note && <p className="small" role="status">{note}</p>}
      {error && <p className="error">{error}</p>}
      <Issues issues={issues} empty="Every row fits its columns, the key is not repeated and the editions run in date order." />
    </div>
  );
}

const TYPES = ['code', 'text', 'date', 'boolean', 'number', 'list'];
type Column = { id: string; type: string; title: string; required: boolean; allowed: string };

/**
 * A new reference table of the bank's own (Issue I-150, R-11-106): its name in scope b, title, domain, columns and key. Checked as a
 * table before its first edition is written, so the editor below fills rows that fit; nothing is saved until the edition is.
 */
function NewTable({ tenant, onStart, onCancel }: { tenant: string; onStart: (doc: any) => void; onCancel?: () => void }) {
  const [short, setShort] = useState('');
  const [title, setTitle] = useState('');
  const [domain, setDomain] = useState('');
  const [columns, setColumns] = useState<Column[]>([{ id: 'code', type: 'code', title: 'Code', required: true, allowed: '' }]);
  const [key, setKey] = useState('code');
  const [issues, setIssues] = useState<any[] | undefined>();
  const name = `b.ref.${short}.v1`;
  const doc = () => ({
    kind: 'reference-table', name, release: '1.0.0', title, domain, key: [key],
    columns: columns.map((c) => ({
      id: c.id, type: c.type, ...(c.title ? { title: c.title } : {}), ...(c.required ? { required: true } : {}),
      ...(c.allowed.trim() ? { allowed: c.allowed.split(',').map((a) => a.trim()).filter(Boolean) } : {}),
    })),
    editions: [],
  });
  const set = (i: number, patch: Partial<Column>) => setColumns(columns.map((c, j) => (j === i ? { ...c, ...patch } : c)));
  const start = async () => {
    // Checked with an empty edition, as a table has at least one: its columns and key are what is checked here.
    const r = await post(`/api/tenants/${encodeURIComponent(tenant)}/reference-tables/check`, { doc: { ...doc(), editions: [{ validFrom: '2000-01-01', rows: [] }] } });
    const errors = (r.issues ?? []).filter((x: any) => x.severity === 'error');
    setIssues(r.issues);
    if (!errors.length) onStart(doc());
  };
  return (
    <section aria-label="New reference table" className="card">
      <label className="field"><span>Name</span><Input aria-label="Table name" contentBefore="b.ref." contentAfter=".v1" value={short} onChange={(e) => setShort(e.target.value.toLowerCase())} /></label>
      <label className="field"><span>Title</span><Input aria-label="Table title" value={title} onChange={(e) => setTitle(e.target.value)} /></label>
      <label className="field"><span>Domain</span><Input aria-label="Table domain" value={domain} onChange={(e) => setDomain(e.target.value)} /></label>
      <Table size="small" className="grid small" aria-label="Columns of the new table">
        <TableHeader><TableRow><TableHeaderCell>Id</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Title</TableHeaderCell><TableHeaderCell>Required</TableHeaderCell><TableHeaderCell>Allowed values</TableHeaderCell><TableHeaderCell>Key</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>{columns.map((c, i) => (
          <TableRow key={i}>
            <TableCell><Input aria-label={`Column id ${i + 1}`} value={c.id} onChange={(e) => set(i, { id: e.target.value })} /></TableCell>
            <TableCell><Select aria-label={`Column type ${i + 1}`} value={c.type} onChange={(e) => set(i, { type: e.target.value })}>{TYPES.map((t) => <option key={t}>{t}</option>)}</Select></TableCell>
            <TableCell><Input aria-label={`Column title ${i + 1}`} value={c.title} onChange={(e) => set(i, { title: e.target.value })} /></TableCell>
            <TableCell><Checkbox aria-label={`Column required ${i + 1}`} checked={c.required} onChange={(_, d) => set(i, { required: !!d.checked })} /></TableCell>
            <TableCell><Input aria-label={`Column allowed ${i + 1}`} placeholder="a, b, c" value={c.allowed} onChange={(e) => set(i, { allowed: e.target.value })} /></TableCell>
            <TableCell><Checkbox aria-label={`Column key ${i + 1}`} checked={key === c.id} onChange={() => setKey(c.id)} /></TableCell>
            <TableCell>{columns.length > 1 && <Link as="button" onClick={() => setColumns(columns.filter((_, j) => j !== i))}>Remove</Link>}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      <div className="row">
        <Button onClick={() => setColumns([...columns, { id: '', type: 'text', title: '', required: false, allowed: '' }])}>Add a column</Button>
        <Button appearance="primary" onClick={() => void start()} disabled={!short || !title || !domain}>Start the table</Button>
        {onCancel && <Button onClick={onCancel}>Cancel</Button>}
      </div>
      <Issues issues={issues} empty="The columns and the key fit; write the first edition below." />
    </section>
  );
}
