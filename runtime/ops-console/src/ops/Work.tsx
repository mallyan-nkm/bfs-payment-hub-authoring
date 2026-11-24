import { useEffect, useRef, useState } from 'react';
import type { Client, WorkQueue } from './api';
import { show } from './format';
import { bulkSummary, FILTERS, holder, keyOf, selectionItems } from './work';
import { Button, Checkbox, Input, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert } from '../ui';

// The work queue (R-05-71): the payments that wait for a person and the actions that wait for a second approver, with who holds
// each. A selection is taken, escalated, released or given to someone in one go; the API applies each item's own rules and records
// each as its own fact. The queue follows the store live: a long poll answers when anything was written, and the list reloads.

export function Work({ api, go }: { api: Client; go(h: string): void }) {
  const [filter, setFilter] = useState<string>('all');
  const [data, setData] = useState<WorkQueue | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [note, setNote] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [remarks, setRemarks] = useState('');
  const [assignee, setAssignee] = useState('');
  const cursor = useRef(0);
  const load = () => api.queue(filter).then((d) => { setData(d); cursor.current = d.cursor; setError(null); }).catch((e) => setError(e.message));

  useEffect(() => { load(); }, [filter]);
  // Live: wait for the store to move, then reload; again and again while the page is open.
  useEffect(() => {
    let live = true;
    (async () => {
      while (live) {
        try {
          const c = await api.changes(cursor.current, 20);
          if (!live) break;
          if (c.changed) { cursor.current = c.cursor; await load(); }
        } catch { await new Promise((r) => setTimeout(r, 2000)); }
      }
    })();
    return () => { live = false; };
  }, [filter]);

  const toggle = (k: string) => { const s = new Set(selected); if (s.has(k)) s.delete(k); else s.add(k); setSelected(s); };
  const bulk = async (op: string) => {
    if (!data) return;
    setNote('');
    try {
      const r = await api.workBulk(op, selectionItems(data.rows, selected), remarks || undefined, op === 'assign' && assignee ? assignee : undefined);
      setNote(bulkSummary(r));
      setSelected(new Set());
      await load();
    } catch (e: any) { setError(e.message); }
  };

  return (
    <section>
      <h1>Work queue</h1>
      <TabList className="tabs" aria-label="Queue" selectedValue={filter} onTabSelect={(_, d) => { setFilter(d.value as typeof filter); setSelected(new Set()); }}>
        {FILTERS.map((f) => (
          <Tab key={f.id} value={f.id}>
            {f.label} <span className="muted">({data?.counts?.[f.id] ?? '…'})</span>
          </Tab>
        ))}
      </TabList>
      <form className="filters" aria-label="Bulk change" onSubmit={(e) => e.preventDefault()}>
        <label><span>Reason</span><Input name="work-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
        <label><span>Give to (supervisor)</span><Input name="work-assignee" value={assignee} onChange={(e) => setAssignee(e.target.value)} placeholder="the person's id" /></label>
        <Button type="button" disabled={!selected.size} onClick={() => bulk('assign')}>{assignee ? 'Give to' : 'Take'} ({selected.size})</Button>
        <Button type="button" disabled={!selected.size} onClick={() => bulk('escalate')}>Escalate</Button>
        <Button type="button" disabled={!selected.size} onClick={() => bulk('release')}>Release</Button>
      </form>
      {note && <p className="muted" data-testid="work-note">{note}</p>}
      {error && <Alert tone="fail" className="error">{error}</Alert>}
      <p className="muted" data-testid="work-total">{data ? `${data.total} item(s)` : 'Loading…'}</p>
      <Table size="small" className="grid" aria-label="Work queue">
        <TableHeader><TableRow><TableHeaderCell /><TableHeaderCell>Waiting</TableHeaderCell><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell><TableHeaderCell className="num">Amount</TableHeaderCell><TableHeaderCell>With</TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {data?.rows.map((r) => (
            <TableRow key={keyOf(r)} data-testid="work-row">
              <TableCell><Checkbox aria-label={`Select ${r.payment}`} checked={selected.has(keyOf(r))} onChange={() => toggle(keyOf(r))} /></TableCell>
              <TableCell>{show(r.waitingSince, 'datetime')}</TableCell>
              <TableCell className="mono"><a href={`#/payments/${encodeURIComponent(r.payment)}`} onClick={(e) => { e.preventDefault(); go(`#/payments/${encodeURIComponent(r.payment)}`); }}>{r.payment}</a></TableCell>
              <TableCell>{r.tenant}</TableCell>
              <TableCell>{r.scheme ?? '—'}</TableCell>
              <TableCell>{r.what}</TableCell>
              <TableCell className="num">{r.amount != null ? `${show(r.amount, 'amount')} ${r.currency ?? ''}` : '—'}</TableCell>
              <TableCell data-testid="work-holder">{holder(r)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </section>
  );
}
