import { useEffect, useState } from 'react';
import { auditQuery, auditRow, chainText, newestFirst, shortHash, type AuditFilter, type AuditRow, type ChainState } from './audit';
import { Button, Input, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

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
  const field = (k: keyof AuditFilter, label: string, type: 'text' | 'date' = 'text') => (
    <label><span>{label}</span><Input name={k} aria-label={label} type={type} value={filter[k] ?? ''} onChange={(e) => setFilter({ ...filter, [k]: e.target.value })} /></label>
  );
  return (
    <section>
      <h1>{title}</h1>
      <p className="summary" data-testid="chain">
        <Badge className={`badge ${chain ? (chain.ok ? 'pass' : 'fail') : ''}`}>{chain ? (chain.ok ? 'verifies' : 'broken') : '—'}</Badge> {chainText(chain)}
      </p>
      <div className="filters">
        {field('person', 'Person')}
        {field('action', 'Action')}
        {field('from', 'From', 'date')}
        {field('to', 'To', 'date')}
        <Button appearance="primary" onClick={run}>Filter</Button>
      </div>
      {error && <Alert tone="fail" className="error" role="alert">{error}</Alert>}
      {rows?.length === 0 && !error && <p className="muted">No fact matches.</p>}
      <Table size="small" className="grid" aria-label="Audit trail">
        <TableHeader><TableRow><TableHeaderCell>When</TableHeaderCell><TableHeaderCell>Who</TableHeaderCell><TableHeaderCell>What</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell><TableHeaderCell>Fact</TableHeaderCell><TableHeaderCell>Hash</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{rows?.map((r) => (
          <TableRow key={r.key} data-testid="audit-row">
            <TableCell>{String(r.at).replace('T', ' ').replace(/\.\d+/, '')}</TableCell>
            <TableCell>{r.by ?? r.actor} <span className="muted mono">{r.by ? r.actor : ''}</span></TableCell>
            <TableCell>{r.kind}{r.code ? ` ${r.code}` : ''}{r.action ? <span className="muted"> {r.action}</span> : null}</TableCell>
            <TableCell>{r.why ?? '—'}</TableCell>
            <TableCell className="mono">{r.subject ? `${r.subject} ` : ''}#{r.seq}</TableCell>
            <TableCell className="mono" title={r.hash}>{shortHash(r.hash)}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}
