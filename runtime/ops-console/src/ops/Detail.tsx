import { useEffect, useState } from 'react';
import type { Client, Detail as D, FieldDecl, Offer, Section, View } from './api';
import { documentRow, grouped, show, tone } from './format';
import { Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Card, Link, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';
import { deliveryNote, ruleTraceNote } from './oversight';

// A payment or bulk: its fields from the platform's and its scheme's views, each leg it has with that leg's declared
// fields, the lines of a bulk, its timeline (every fact of its chain), its documents, holds and actions, and the actions
// its views offer now. Rendered from the declarations alone.

function Value({ f, v }: { f: FieldDecl; v: unknown }) {
  if (f.format === 'status') return <Badge className={`badge ${tone(v)}`}>{show(v)}</Badge>;
  return <span className={f.format === 'code' || f.format === 'json' ? 'mono' : ''}>{show(v, f.format)}</span>;
}

/**
 * Unmasking (R-05-69): a masked field is asked for with a reason; a bank approver, another person, consents; the value is then
 * shown here to the person who asked, for the bank's window. The request is the console's, by ids only.
 */
function Unmask({ api, subject, field }: { api: Client; subject: string; field: FieldDecl }) {
  const [asked, setAsked] = useState<string | null>(null);
  const [shown, setShown] = useState<{ value: any; until: string } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  if (!field.mask || field.mask === 'none') return null;
  return (
    <span className="unmask">
      {shown ? <span className="mono" data-testid={`unmasked-${field.id}`}>{show(shown.value)} <span className="muted small">until {show(shown.until, 'datetime')}</span></span>
        : asked ? (
          <Link as="button" onClick={async () => {
            try { setShown(await api.unmasked(asked)); setMsg(null); } catch (e: any) { setMsg(e.message); }
          }}>Show</Link>
        ) : (
          <Link as="button" aria-label={`Unmask ${field.label}`} onClick={async () => {
            const why = window.prompt(`Why do you need to see ${field.label}?`) ?? '';
            if (!why.trim()) return;
            try { const r = await api.unmask(subject, `subject:${field.id}`, why); setAsked(r.action); setMsg('Asked; a bank approver must consent.'); } catch (e: any) { setMsg(e.message); }
          }}>Unmask</Link>
        )}
      {msg && <span className="muted small" role="status"> {msg}</span>}
    </span>
  );
}

function Fields({ section, values, testid, api, subject }: { section?: Section; values: Record<string, any>; testid?: string; api?: Client; subject?: string }) {
  const groups = grouped(section?.fields, section?.groups);
  return (
    <div className="cards" data-testid={testid}>
      {groups.map((g) => (
        <Card className="card" key={g.title}>
          <h3>{g.title}</h3>
          <dl>
            {g.fields.map((f) => (
              <div key={f.id} data-field={f.id}>
                <dt>{f.label}</dt>
                <dd><Value f={f} v={values?.[f.id]} /></dd>
                {api && subject && values?.[f.id] != null && values[f.id] !== '[erased]' && <Unmask api={api} subject={subject} field={f} />}
              </div>
            ))}
          </dl>
        </Card>
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
          <Button key={o.action} className={chosen?.action === o.action ? 'primary' : ''} onClick={() => { setChosen(o); setMsg(null); }}>
            {o.label}
          </Button>
        ))}
      </div>
      {chosen && (
        <div className="confirm">
          <p>
            {chosen.label}{chosen.secondApprover ? `: a ${chosen.secondApprover.toLowerCase()} must approve it before it is carried out.` : '.'}
          </p>
          <label>
            <span>Remarks{chosen.remarksRequired ? ' (required)' : ''}</span>
            <Textarea name="remarks" value={remarks} onChange={(e) => setRemarks(e.target.value)} />
          </label>
          <Button appearance="primary" disabled={chosen.remarksRequired && !remarks.trim()}
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
          </Button>
          <Link as="button" onClick={() => setChosen(null)}>Cancel</Link>
        </div>
      )}
      {msg && <Alert tone="info" className="note" role="status">{msg}</Alert>}
    </div>
  );
}

export function Detail({ api, views, id, go }: { api: Client; views: View[]; id: string; go(h: string): void }) {
  const [d, setD] = useState<D | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<'legs' | 'lines' | 'timeline' | 'documents' | 'actions'>('legs');
  const load = () => api.payment(id).then(setD).catch((e) => setError(e.message));
  useEffect(() => { load(); const t = setInterval(load, 3000); return () => clearInterval(t); }, [id]);
  if (error) return <Alert tone="fail" className="error">{error}</Alert>;
  if (!d) return <p className="muted">Loading…</p>;
  const p = d.payment;
  const mine = views.filter((v) => !v.scheme || v.scheme === p.scheme);
  const schemeView = mine.find((v) => v.scheme === p.scheme);
  const lineDecl = schemeView?.subject?.lines;
  const lineCols = (lineDecl?.columns ?? lineDecl?.fields.map((f) => f.id) ?? []).map((c) => lineDecl!.fields.find((f) => f.id === c)!).filter(Boolean);
  const docLabel = (kind: string) => schemeView?.subject?.documents?.find((x) => x.kind === kind)?.label ?? kind;
  return (
    <section>
      <Breadcrumb aria-label="Where you are" size="small" className="crumbs">
        <BreadcrumbItem><BreadcrumbButton href="#/payments" onClick={(e) => { e.preventDefault(); history.back(); }}>Payments</BreadcrumbButton></BreadcrumbItem>
        <BreadcrumbDivider />
        <BreadcrumbItem><BreadcrumbButton current className="mono">{p.id}</BreadcrumbButton></BreadcrumbItem>
      </Breadcrumb>
      <h1 className="mono">{p.fileName ?? p.messageId ?? p.endToEndId ?? p.id}</h1>
      <div className="summary">
        <span>{p.tenant}</span><span>{p.scheme}</span><span>{p.kind}</span><span>{p.direction}</span>
        <span>{show(p.amount, 'amount')} {p.currency}</span><span>{p.count} line(s)</span>
        <Badge className={`badge ${tone(p.status)}`}>{p.status ?? '—'}</Badge>
        {p.hold && <Badge className="badge warn" data-testid="hold-badge">Held: {p.holdReason ?? 'for a person'}</Badge>}
      </div>
      <h2>Actions</h2>
      <Actions api={api} id={p.id} offers={d.offers} reload={load} />
      {mine.map((v) => (
        <div key={v.name}>
          <h2>{v.subject?.title ?? v.title}</h2>
          <Fields section={v.subject} values={p.fields} testid={`fields-${v.name}`} api={api} subject={p.id} />
        </div>
      ))}
      <TabList className="tabs" selectedValue={tab} onTabSelect={(_, x) => setTab(x.value as typeof tab)}>
        {(['legs', 'lines', 'timeline', 'documents', 'actions'] as const).map((t) => (
          <Tab key={t} value={t}>
            {{ legs: `Legs (${p.legs.length})`, lines: `Lines (${d.lines.length})`, timeline: `Timeline (${d.timeline.length})`, documents: `Documents (${d.documents.length})`, actions: `Holds and actions (${d.holds.length + d.actions.length})` }[t]}
          </Tab>
        ))}
      </TabList>
      {tab === 'legs' && (p.legs.length === 0 ? <p className="muted">No leg of this payment has run a step yet.</p> : p.legs.map((l) => {
        const decl = schemeView?.legs?.find((x) => x.leg === l.leg);
        return (
          <div key={l.id} className="leg" data-testid="leg" data-leg={l.leg}>
            <h3>{l.title} <Badge className={`badge ${tone(l.lastOutcome)}`}>{l.lastStep}: {l.lastOutcome ?? '—'}</Badge></h3>
            <Fields section={decl} values={l.fields} />
          </div>
        );
      }))}
      {tab === 'lines' && (
        <div className="table-wrap">
          <Table size="small" className="grid" aria-label="Lines">
            <TableHeader><TableRow>{lineCols.map((f) => <TableHeaderCell key={f.id}>{f.label}</TableHeaderCell>)}</TableRow></TableHeader>
            <TableBody>{d.lines.map((l) => <TableRow key={l.id}>{lineCols.map((f) => <TableCell key={f.id}><Value f={f} v={l.fields?.[f.id]} /></TableCell>)}</TableRow>)}</TableBody>
          </Table>
        </div>
      )}
      {tab === 'timeline' && (
        <ol className="timeline" aria-label="Timeline">
          {d.timeline.map((t) => (
            <li key={t.seq}>
              <span className="mono muted">{show(t.at, 'datetime')}</span>{' '}
              <span className="kind">{t.kind}</span>{' '}
              <Badge className={`badge ${tone(t.code)}`}>{t.code ?? '—'}</Badge>{' '}
              {t.step && <span className="mono">{t.instance && t.instance.includes('/') ? `${t.instance.split('/').slice(1).join('/')} · ` : ''}{t.step}</span>}{' '}
              {t.activity && <span className="muted">{t.activity}</span>}{' '}
              {t.data?.reasonCode && <span className="mono">{t.data.reasonCode}</span>}{' '}
              {t.data?.reason && <span>{t.data.reason}</span>}{' '}
              {t.data?.remarks && <span>“{t.data.remarks}”</span>}{' '}
              {deliveryNote(t) && <span data-testid="delivery-note">{deliveryNote(t)}</span>}{' '}
              {ruleTraceNote(t) && <span className="muted" data-testid="rule-trace">{ruleTraceNote(t)}</span>}{' '}
              <span className="muted">by {t.actor}</span>
              {/* The trace the fact was written in (R-09-69): a link to the installation's trace view, else the id. */}
              {t.traceId && (t.trace
                ? <>{' '}<a className="mono small" href={t.trace} target="_blank" rel="noreferrer" aria-label={`Trace of fact ${t.seq}`}>trace {t.traceId.slice(0, 8)}</a></>
                : <>{' '}<span className="mono small muted" title={t.traceId}>trace {t.traceId.slice(0, 8)}</span></>)}
            </li>
          ))}
        </ol>
      )}
      {tab === 'documents' && (
        d.documents.length === 0 ? <p className="muted">No file is kept for this payment.</p> : (
          <Table size="small" className="grid" aria-label="Documents">
            <TableHeader><TableRow><TableHeaderCell>Document</TableHeaderCell><TableHeaderCell>File name</TableHeaderCell><TableHeaderCell>Direction</TableHeaderCell><TableHeaderCell>Leg</TableHeaderCell><TableHeaderCell className="num">Bytes</TableHeaderCell><TableHeaderCell>SHA-256</TableHeaderCell><TableHeaderCell>At</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
            <TableBody>
              {d.documents.map((x) => (
                <TableRow key={x.id}>
                  <TableCell>{docLabel(x.kind)}</TableCell><TableCell className="mono">{x.fileName ?? '—'}</TableCell><TableCell>{documentRow(x, p.legs).direction}</TableCell><TableCell className="mono">{documentRow(x, p.legs).leg}</TableCell><TableCell className="num">{x.size}</TableCell>
                  <TableCell className="mono" title={documentRow(x, p.legs).signature}>{documentRow(x, p.legs).hash}{x.signed ? ' (signed)' : ''}</TableCell><TableCell>{show(x.at, 'datetime')}</TableCell>
                  <TableCell><Link as="button" onClick={async () => {
                    const r = await fetch(api.documentUrl(p.id, x.id), { headers: api.headers() });
                    const url = URL.createObjectURL(await r.blob());
                    const a = document.createElement('a');
                    a.href = url; a.download = x.fileName ?? x.kind; a.click(); URL.revokeObjectURL(url);
                  }}>Download</Link></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )
      )}
      {tab === 'actions' && (
        <>
          <Table size="small" className="grid" aria-label="Holds">
            <TableHeader><TableRow><TableHeaderCell>Hold</TableHeaderCell><TableHeaderCell>Reason</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Decision</TableHeaderCell><TableHeaderCell>Opened</TableHeaderCell><TableHeaderCell>Decided by</TableHeaderCell><TableHeaderCell>Decided</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{d.holds.map((h) => <TableRow key={h.id}><TableCell>{h.kind}</TableCell><TableCell>{h.reason ?? '—'}</TableCell><TableCell><Badge className={`badge ${tone(h.status)}`}>{h.status}</Badge></TableCell><TableCell>{h.decision ?? '—'}</TableCell><TableCell>{show(h.openedAt, 'datetime')}</TableCell><TableCell>{h.decidedBy ?? '—'}</TableCell><TableCell>{show(h.decidedAt, 'datetime')}</TableCell></TableRow>)}</TableBody>
          </Table>
          <Table size="small" className="grid" aria-label="Operator actions">
            <TableHeader><TableRow><TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Asked by</TableHeaderCell><TableHeaderCell>Remarks</TableHeaderCell><TableHeaderCell>Approved by</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
            <TableBody>{d.actions.map((a) => (
              <TableRow key={a.id}>
                <TableCell>{a.action}</TableCell><TableCell><Badge className={`badge ${tone(a.status)}`}>{a.status}</Badge></TableCell><TableCell>{a.requestedBy}</TableCell><TableCell>{a.remarks ?? '—'}</TableCell><TableCell>{a.approvedBy ?? '—'}</TableCell><TableCell>{a.result ?? '—'}</TableCell>
                <TableCell>{a.status === 'pending-approval' && (
                  <>
                    <Button onClick={() => api.decide(p.id, a.id, true).then(load).catch((e) => setError(e.message))}>Approve</Button>{' '}
                    <Button onClick={() => api.decide(p.id, a.id, false).then(load).catch((e) => setError(e.message))}>Reject</Button>
                  </>
                )}</TableCell>
              </TableRow>
            ))}</TableBody>
          </Table>
        </>
      )}
      <p className="muted"><a href="#/payments" onClick={(e) => { e.preventDefault(); go('#/payments'); }}>All payments</a></p>
    </section>
  );
}
