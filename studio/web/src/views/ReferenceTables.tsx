import { useEffect, useState } from 'react';
import { get, post } from '../api';
import { ErrorBox, Issues, Loading, useLoad, useStudio } from '../ui';

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
  const table = data.data?.tables?.find((t: any) => t.name === name) ?? data.data?.tables?.[0];
  useEffect(() => {
    if (!table?.start) return;
    setName(table.name);
    const last = table.start.editions?.[table.start.editions.length - 1];
    setRows((last?.rows ?? []).map((r: any) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, cellText(v)]))));
    setValidFrom('');
    setIssues(table.issues);
  }, [table?.name, data.data]);
  if (data.loading) return <Loading />;
  if (data.error) return <ErrorBox error={data.error} />;
  if (!table) return <p className="muted small">The bank has no reference table of its own yet.</p>;
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
        data.reload();
      }
    } catch (e: any) { setError(e.message); }
  };
  return (
    <div aria-label="Reference tables">
      <label className="field"><span>Table</span>
        <select aria-label="Reference table" value={table.name} onChange={(e) => setName(e.target.value)}>
          {data.data.tables.map((t: any) => <option key={t.name} value={t.name}>{t.title} ({t.name})</option>)}
        </select>
      </label>
      <p className="small muted">
        Released {table.released?.release ?? 'never'}{table.draft ? `, draft ${table.draft.release} in progress` : ''}. Editions: {(table.released?.editions ?? []).map((e: any) => e.validFrom).join(', ') || 'none'}.
        A new edition starts at its valid-from date; the next release is {table.start.release}.
      </p>
      <label className="field"><span>New edition valid from</span><input aria-label="Valid from" type="date" value={validFrom} disabled={!configurator} onChange={(e) => setValidFrom(e.target.value)} /></label>
      <table className="grid small" aria-label={`Rows of ${table.name}`}>
        <thead><tr>{cols.map((c) => <th key={c.id}>{c.title ?? c.id}</th>)}<th /></tr></thead>
        <tbody>{rows.map((r, i) => (
          <tr key={i}>
            {cols.map((c) => (
              <td key={c.id}>{c.allowed ? (
                <select aria-label={`${c.id} ${i + 1}`} value={r[c.id] ?? ''} disabled={!configurator} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, [c.id]: e.target.value } : x)))}>
                  <option value="" />{c.allowed.map((a: any) => <option key={String(a)} value={String(a)}>{String(a)}</option>)}
                </select>
              ) : (
                <input aria-label={`${c.id} ${i + 1}`} value={r[c.id] ?? ''} disabled={!configurator} size={12} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, [c.id]: e.target.value } : x)))} />
              )}</td>
            ))}
            <td>{configurator && <button className="link" onClick={() => setRows(rows.filter((_, j) => j !== i))}>Remove</button>}</td>
          </tr>
        ))}</tbody>
      </table>
      {configurator && (
        <div className="row">
          <button onClick={() => setRows([...rows, {}])}>Add a row</button>
          <button onClick={() => run('check')} disabled={!validFrom}>Check</button>
          <button className="primary" onClick={() => run('save')} disabled={!validFrom}>Save as draft</button>
        </div>
      )}
      {note && <p className="small" role="status">{note}</p>}
      {error && <p className="error">{error}</p>}
      <Issues issues={issues} empty="Every row fits its columns, the key is not repeated and the editions run in date order." />
    </div>
  );
}
