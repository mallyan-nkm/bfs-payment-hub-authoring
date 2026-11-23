import { useEffect, useState } from 'react';
import { auditQuery, auditRow, chainText, newestFirst, shortHash, type AuditFilter, type AuditRow, type ChainState } from './audit';

// The audit page of a console (R-05-75, R-05-76, Issue I-202): its own chain, newest first, filtered by person, action and date,
// each row with its hash, and whether the chain verifies. Each console gives the page its loader.

export function AuditTrail({ title, load }: { title: string; load(q: Record<string, string>): Promise<{ rows: any[]; chain: ChainState }> }) {
  const [filter, setFilter] = useState<AuditFilter>({});
  const [rows, setRows] = useState<AuditRow[] | null>(null);
  const [chain, setChain] = useState<ChainState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const run = () => load(auditQuery(filter))
    .then((r) => { setRows(newestFirst(r.rows.map(auditRow))); setChain(r.chain); setError(null); })
    .catch((e) => { setRows([]); setError(String(e.message ?? e)); });
  useEffect(() => { run(); }, []);
  const field = (k: keyof AuditFilter, label: string, type = 'text') => (
    <label><span>{label}</span><input name={k} aria-label={label} type={type} value={filter[k] ?? ''} onChange={(e) => setFilter({ ...filter, [k]: e.target.value })} /></label>
  );
  return (
    <section>
      <h1>{title}</h1>
      <p className="summary" data-testid="chain">
        <span className={`badge ${chain ? (chain.ok ? 'pass' : 'fail') : ''}`}>{chain ? (chain.ok ? 'verifies' : 'broken') : '—'}</span> {chainText(chain)}
      </p>
      <div className="filters">
        {field('person', 'Person')}
        {field('action', 'Action')}
        {field('from', 'From', 'date')}
        {field('to', 'To', 'date')}
        <button className="primary" onClick={run}>Filter</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {rows?.length === 0 && !error && <p className="muted">No fact matches.</p>}
      <table className="grid" aria-label="Audit trail">
        <thead><tr><th>When</th><th>Who</th><th>What</th><th>Why</th><th>Fact</th><th>Hash</th></tr></thead>
        <tbody>{rows?.map((r) => (
          <tr key={r.key} data-testid="audit-row">
            <td>{String(r.at).replace('T', ' ').replace(/\.\d+/, '')}</td>
            <td>{r.by ?? r.actor} <span className="muted mono">{r.by ? r.actor : ''}</span></td>
            <td>{r.kind}{r.code ? ` ${r.code}` : ''}{r.action ? <span className="muted"> {r.action}</span> : null}</td>
            <td>{r.why ?? '—'}</td>
            <td className="mono">{r.subject ? `${r.subject} ` : ''}#{r.seq}</td>
            <td className="mono" title={r.hash}>{shortHash(r.hash)}</td>
          </tr>
        ))}</tbody>
      </table>
    </section>
  );
}
