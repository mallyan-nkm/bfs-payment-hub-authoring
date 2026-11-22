import { useEffect, useState } from 'react';
import type { Client, Detail as D, FieldDecl, Offer, Section, View } from './api';
import { grouped, show, tone } from './format';

// A payment or bulk: its fields from the platform's and its scheme's views, each leg it has with that leg's declared
// fields, the lines of a bulk, its timeline (every fact of its chain), its documents, holds and actions, and the actions
// its views offer now. Rendered from the declarations alone.

function Value({ f, v }: { f: FieldDecl; v: unknown }) {
  if (f.format === 'status') return <span className={`badge ${tone(v)}`}>{show(v)}</span>;
  return <span className={f.format === 'code' || f.format === 'json' ? 'mono' : ''}>{show(v, f.format)}</span>;
}

function Fields({ section, values, testid }: { section?: Section; values: Record<string, any>; testid?: string }) {
  const groups = grouped(section?.fields, section?.groups);
  return (
    <div className="cards" data-testid={testid}>
      {groups.map((g) => (
        <div className="card" key={g.title}>
          <h3>{g.title}</h3>
          <dl>
            {g.fields.map((f) => (
              <div key={f.id} data-field={f.id}>
                <dt>{f.label}</dt>
                <dd><Value f={f} v={values?.[f.id]} /></dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  );
}

function Actions({ api, id, offers, reload }: { api: Client; id: string; offers: Offer[]; reload(): void }) {
  const [chosen, setChosen] = useState<Offer | null>(null);
  const [remarks, setRemarks] = useState('');
  const [msg, setMsg] = useState<string | null>(null);
  if (offers.length === 0) return <p className="muted">No action applies to this payment now.</p>;
  return (
    <div className="actions" aria-label="Actions">
      <div className="action-buttons">
        {offers.map((o) => (
          <button key={o.action} className={chosen?.action === o.action ? 'primary' : ''} onClick={() => { setChosen(o); setMsg(null); }}>
            {o.label}
          </button>
        ))}
      </div>
      {chosen && (
        <div className="confirm">
          <p>
            {chosen.label}{chosen.secondApprover ? `: a ${chosen.secondApprover.toLowerCase()} must approve it before it is carried out.` : '.'}
          </p>
          <label>
            <span>Remarks{chosen.remarksRequired ? ' (required)' : ''}</span>
            <textarea name="remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} />
          </label>
          <button className="primary" disabled={chosen.remarksRequired && !remarks.trim()}
            onClick={async () => {
              try {
                const r = await api.act(id, chosen.action, remarks || undefined);
                setMsg(`${chosen.label}: ${r.status}${r.result ? ` (${r.result})` : ''}`);
                setChosen(null);
                setRemarks('');
                reload();
              } catch (e: any) { setMsg(e.message); }
            }}>
            Confirm {chosen.label.toLowerCase()}
          </button>
          <button className="link" onClick={() => setChosen(null)}>Cancel</button>
        </div>
      )}
      {msg && <p className="note" role="status">{msg}</p>}
    </div>
  );
}

export function Detail({ api, views, id, go }: { api: Client; views: View[]; id: string; go(h: string): void }) {
  const [d, setD] = useState<D | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<'legs' | 'lines' | 'timeline' | 'documents' | 'actions'>('legs');
  const load = () => api.payment(id).then(setD).catch((e) => setError(e.message));
  useEffect(() => { load(); const t = setInterval(load, 3000); return () => clearInterval(t); }, [id]);
  if (error) return <p className="error">{error}</p>;
  if (!d) return <p className="muted">Loading…</p>;
  const p = d.payment;
  const mine = views.filter((v) => !v.scheme || v.scheme === p.scheme);
  const schemeView = mine.find((v) => v.scheme === p.scheme);
  const lineDecl = schemeView?.subject?.lines;
  const lineCols = (lineDecl?.columns ?? lineDecl?.fields.map((f) => f.id) ?? []).map((c) => lineDecl!.fields.find((f) => f.id === c)!).filter(Boolean);
  const docLabel = (kind: string) => schemeView?.subject?.documents?.find((x) => x.kind === kind)?.label ?? kind;
  return (
    <section>
      <p><a href="#/payments" onClick={(e) => { e.preventDefault(); history.back(); }}>← Payments</a></p>
      <h1 className="mono">{p.fileName ?? p.messageId ?? p.endToEndId ?? p.id}</h1>
      <div className="summary">
        <span>{p.tenant}</span><span>{p.scheme}</span><span>{p.kind}</span><span>{p.direction}</span>
        <span>{show(p.amount, 'amount')} {p.currency}</span><span>{p.count} line(s)</span>
        <span className={`badge ${tone(p.status)}`}>{p.status ?? '—'}</span>
        {p.hold && <span className="badge warn" data-testid="hold-badge">Held: {p.holdReason ?? 'for a person'}</span>}
      </div>
      <h2>Actions</h2>
      <Actions api={api} id={p.id} offers={d.offers} reload={load} />
      {mine.map((v) => (
        <div key={v.name}>
          <h2>{v.subject?.title ?? v.title}</h2>
          <Fields section={v.subject} values={p.fields} testid={`fields-${v.name}`} />
        </div>
      ))}
      <div className="tabs" role="tablist">
        {(['legs', 'lines', 'timeline', 'documents', 'actions'] as const).map((t) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>
            {{ legs: `Legs (${p.legs.length})`, lines: `Lines (${d.lines.length})`, timeline: `Timeline (${d.timeline.length})`, documents: `Documents (${d.documents.length})`, actions: `Holds and actions (${d.holds.length + d.actions.length})` }[t]}
          </button>
        ))}
      </div>
      {tab === 'legs' && (p.legs.length === 0 ? <p className="muted">No leg of this payment has run a step yet.</p> : p.legs.map((l) => {
        const decl = schemeView?.legs?.find((x) => x.leg === l.leg);
        return (
          <div key={l.id} className="leg" data-testid="leg" data-leg={l.leg}>
            <h3>{l.title} <span className={`badge ${tone(l.lastOutcome)}`}>{l.lastStep}: {l.lastOutcome ?? '—'}</span></h3>
            <Fields section={decl} values={l.fields} />
          </div>
        );
      }))}
      {tab === 'lines' && (
        <div className="table-wrap">
          <table className="grid" aria-label="Lines">
            <thead><tr>{lineCols.map((f) => <th key={f.id}>{f.label}</th>)}</tr></thead>
            <tbody>{d.lines.map((l) => <tr key={l.id}>{lineCols.map((f) => <td key={f.id}><Value f={f} v={l.fields?.[f.id]} /></td>)}</tr>)}</tbody>
          </table>
        </div>
      )}
      {tab === 'timeline' && (
        <ol className="timeline" aria-label="Timeline">
          {d.timeline.map((t) => (
            <li key={t.seq}>
              <span className="mono muted">{show(t.at, 'datetime')}</span>{' '}
              <span className="kind">{t.kind}</span>{' '}
              <span className={`badge ${tone(t.code)}`}>{t.code ?? '—'}</span>{' '}
              {t.step && <span className="mono">{t.instance && t.instance.includes('/') ? `${t.instance.split('/').slice(1).join('/')} · ` : ''}{t.step}</span>}{' '}
              {t.activity && <span className="muted">{t.activity}</span>}{' '}
              {t.data?.reasonCode && <span className="mono">{t.data.reasonCode}</span>}{' '}
              {t.data?.reason && <span>{t.data.reason}</span>}{' '}
              {t.data?.remarks && <span>“{t.data.remarks}”</span>}{' '}
              <span className="muted">by {t.actor}</span>
            </li>
          ))}
        </ol>
      )}
      {tab === 'documents' && (
        d.documents.length === 0 ? <p className="muted">No file is kept for this payment.</p> : (
          <table className="grid" aria-label="Documents">
            <thead><tr><th>Document</th><th>File name</th><th>Direction</th><th className="num">Bytes</th><th>At</th><th /></tr></thead>
            <tbody>
              {d.documents.map((x) => (
                <tr key={x.id}>
                  <td>{docLabel(x.kind)}</td><td className="mono">{x.fileName ?? '—'}</td><td>{x.direction}</td><td className="num">{x.size}</td><td>{show(x.at, 'datetime')}</td>
                  <td><button className="link" onClick={async () => {
                    const r = await fetch(api.documentUrl(p.id, x.id), { headers: api.headers() });
                    const url = URL.createObjectURL(await r.blob());
                    const a = document.createElement('a');
                    a.href = url; a.download = x.fileName ?? x.kind; a.click(); URL.revokeObjectURL(url);
                  }}>Download</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )
      )}
      {tab === 'actions' && (
        <>
          <table className="grid" aria-label="Holds">
            <thead><tr><th>Hold</th><th>Reason</th><th>Status</th><th>Decision</th><th>Opened</th><th>Decided by</th><th>Decided</th></tr></thead>
            <tbody>{d.holds.map((h) => <tr key={h.id}><td>{h.kind}</td><td>{h.reason ?? '—'}</td><td><span className={`badge ${tone(h.status)}`}>{h.status}</span></td><td>{h.decision ?? '—'}</td><td>{show(h.openedAt, 'datetime')}</td><td>{h.decidedBy ?? '—'}</td><td>{show(h.decidedAt, 'datetime')}</td></tr>)}</tbody>
          </table>
          <table className="grid" aria-label="Operator actions">
            <thead><tr><th>Action</th><th>Status</th><th>Asked by</th><th>Remarks</th><th>Approved by</th><th>Result</th><th /></tr></thead>
            <tbody>{d.actions.map((a) => (
              <tr key={a.id}>
                <td>{a.action}</td><td><span className={`badge ${tone(a.status)}`}>{a.status}</span></td><td>{a.requestedBy}</td><td>{a.remarks ?? '—'}</td><td>{a.approvedBy ?? '—'}</td><td>{a.result ?? '—'}</td>
                <td>{a.status === 'pending-approval' && (
                  <>
                    <button onClick={() => api.decide(p.id, a.id, true).then(load).catch((e) => setError(e.message))}>Approve</button>{' '}
                    <button onClick={() => api.decide(p.id, a.id, false).then(load).catch((e) => setError(e.message))}>Reject</button>
                  </>
                )}</td>
              </tr>
            ))}</tbody>
          </table>
        </>
      )}
      <p className="muted"><a href="#/payments" onClick={(e) => { e.preventDefault(); go('#/payments'); }}>All payments</a></p>
    </section>
  );
}
