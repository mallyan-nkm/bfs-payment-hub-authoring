import { useEffect, useState } from 'react';
import type { Client } from './api';
import { show, tone } from './format';
import { Button, Checkbox, Input, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Badge } from '../ui';

// The reconciliation break queue (R-09-92): the breaks the scheme reports' runs recorded, each resolved once. An explanation is
// recorded at once; adopting the scheme's status changes the system and waits for a bank approver, another person (R-09-77).

export interface Break {
  break: string; run: string; state: 'open' | 'pending-consent' | 'resolved'; kind: string; reference: string; payment?: string | null;
  expected?: string | null; reported?: string | null; reportedStatus?: string | null; tenant: string; scheme: string; file: string;
  pending?: { resolution: string; why: string; by: string } | null; history: { code: string; at: string; actor: string; data: any }[];
}

/** What a break is, in a person's words. */
export function describeBreak(b: Pick<Break, 'kind' | 'expected' | 'reported' | 'reportedStatus'>): string {
  switch (b.kind) {
    case 'not-in-store': return 'The scheme reports a payment this bank has no record of';
    case 'amount': return `The amount differs: ${b.expected} here, ${b.reported} in the report`;
    case 'currency': return `The currency differs: ${b.expected} here, ${b.reported} in the report`;
    case 'status': return `The status differs: ${b.expected} here, ${b.reported} in the report`;
    default: return b.kind;
  }
}

/** The resolutions a break offers its viewer: adopting a status only where the report gives one for a known payment. */
export function resolutionsFor(b: Pick<Break, 'state' | 'payment' | 'reportedStatus'>): string[] {
  if (b.state !== 'open') return [];
  return b.payment && b.reportedStatus ? ['explain', 'adopt-status'] : ['explain'];
}

export function Breaks({ api, go }: { api: Client; go(h: string): void }) {
  // A bank approver of any tenant sees the consent controls; the API holds each consent to the break's own tenant.
  const [approver, setApprover] = useState(false);
  useEffect(() => { api.me().then((m) => setApprover(Object.values(m.tenants ?? {}).some((r) => r.includes('BankApprover')))).catch(() => setApprover(false)); }, []);
  const [rows, setRows] = useState<Break[] | null>(null);
  const [all, setAll] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [remarks, setRemarks] = useState<Record<string, string>>({});
  const load = () => api.breaks(all).then(setRows).catch((e) => { setRows([]); setMsg(String(e.message ?? e)); });
  useEffect(() => { load(); }, [all]);
  async function run(p: Promise<any>) {
    try { const r = await p; setMsg(`${r.break}: ${r.status}`); load(); } catch (e: any) { setMsg(String(e.message ?? e)); }
  }
  return (
    <section>
      <h1>Reconciliation breaks</h1>
      <Checkbox checked={all} onChange={(e) => setAll(e.target.checked)} label={<>Show resolved breaks too</>} />
      {msg && <p className="muted" role="status">{msg}</p>}
      {rows?.length === 0 && <p className="muted">No break is open.</p>}
      <Table size="small" className="grid" aria-label="Reconciliation breaks">
        <TableHeader><TableRow><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>Break</TableHeaderCell><TableHeaderCell>Reference</TableHeaderCell><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Report</TableHeaderCell><TableHeaderCell>Since</TableHeaderCell><TableHeaderCell>Resolve</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{rows?.map((b) => (
          <TableRow key={b.break}>
            <TableCell><Badge className={`badge ${tone(b.state === 'resolved' ? 'settled' : b.state === 'open' ? 'review' : 'pending')}`}>{b.state}</Badge></TableCell>
            <TableCell>{describeBreak(b)}</TableCell>
            <TableCell className="mono">{b.reference}</TableCell>
            <TableCell className="mono">{b.payment ? <a href={`#/payments/${encodeURIComponent(b.payment)}`} onClick={(e) => { e.preventDefault(); go(`#/payments/${encodeURIComponent(b.payment!)}`); }}>{b.payment}</a> : '—'}</TableCell>
            <TableCell className="mono">{b.file}</TableCell>
            <TableCell>{show(b.history[0]?.at, 'datetime')}</TableCell>
            <TableCell>
              {(resolutionsFor(b).length > 0 || (b.state === 'pending-consent' && approver)) && (
                <Input aria-label={`Reason for ${b.break}`} placeholder="Reason (required)" value={remarks[b.break] ?? ''} onChange={(e) => setRemarks({ ...remarks, [b.break]: e.target.value })} />
              )}
              {resolutionsFor(b).map((r) => (
                <Button key={r} onClick={() => run(api.resolveBreak(b.break, r, remarks[b.break]))}>{r === 'explain' ? 'Explain' : 'Adopt the scheme\'s status'}</Button>
              ))}
              {b.state === 'pending-consent' && (approver
                ? <>
                    <span className="muted">{b.pending?.by} asks to {b.pending?.resolution}: {b.pending?.why}</span>
                    <Button appearance="primary" onClick={() => run(api.consentBreak(b.break, true, remarks[b.break]))}>Consent</Button>
                    <Button onClick={() => run(api.consentBreak(b.break, false, remarks[b.break]))}>Refuse</Button>
                  </>
                : <span className="muted">Waits for a bank approver</span>)}
            </TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}
