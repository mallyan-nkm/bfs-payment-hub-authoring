import { useEffect, useRef, useState } from 'react';
import type { Client, WorkQueue } from './api';
import { show } from './format';
import { bulkSummary, FILTERS, holder, keyOf, selectionItems } from './work';

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
      <div className="tabs" role="tablist" aria-label="Queue">
        {FILTERS.map((f) => (
          <button key={f.id} role="tab" aria-selected={filter === f.id} className={filter === f.id ? 'active' : ''} onClick={() => { setFilter(f.id); setSelected(new Set()); }}>
            {f.label} <span className="muted">({data?.counts?.[f.id] ?? '…'})</span>
          </button>
        ))}
      </div>
      <form className="filters" aria-label="Bulk change" onSubmit={(e) => e.preventDefault()}>
        <label><span>Reason</span><input name="work-remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></label>
        <label><span>Give to (supervisor)</span><input name="work-assignee" value={assignee} onChange={(e) => setAssignee(e.target.value)} placeholder="the person's id" /></label>
        <button type="button" disabled={!selected.size} onClick={() => bulk('assign')}>{assignee ? 'Give to' : 'Take'} ({selected.size})</button>
        <button type="button" disabled={!selected.size} onClick={() => bulk('escalate')}>Escalate</button>
        <button type="button" disabled={!selected.size} onClick={() => bulk('release')}>Release</button>
      </form>
      {note && <p className="muted" data-testid="work-note">{note}</p>}
      {error && <p className="error">{error}</p>}
      <p className="muted" data-testid="work-total">{data ? `${data.total} item(s)` : 'Loading…'}</p>
      <table className="grid" aria-label="Work queue">
        <thead><tr><th /><th>Waiting</th><th>Payment</th><th>Tenant</th><th>Scheme</th><th>Why</th><th className="num">Amount</th><th>With</th></tr></thead>
        <tbody>
          {data?.rows.map((r) => (
            <tr key={keyOf(r)} data-testid="work-row">
              <td><input type="checkbox" aria-label={`Select ${r.payment}`} checked={selected.has(keyOf(r))} onChange={() => toggle(keyOf(r))} /></td>
              <td>{show(r.waitingSince, 'datetime')}</td>
              <td className="mono"><a href={`#/payments/${encodeURIComponent(r.payment)}`} onClick={(e) => { e.preventDefault(); go(`#/payments/${encodeURIComponent(r.payment)}`); }}>{r.payment}</a></td>
              <td>{r.tenant}</td>
              <td>{r.scheme ?? '—'}</td>
              <td>{r.what}</td>
              <td className="num">{r.amount != null ? `${show(r.amount, 'amount')} ${r.currency ?? ''}` : '—'}</td>
              <td data-testid="work-holder">{holder(r)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
