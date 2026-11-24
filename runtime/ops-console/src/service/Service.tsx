import { useEffect, useState, type ReactNode } from 'react';
import { ServiceError, type ActionResult, type ActionSpec, type Answer, type ErrorEntry, type ErrorLink, type Health, type LogItem, type Release, type ServiceClient, type StuckPin, type Trust, type ValuesInForce, type Version } from './api';
import { PAGES, diffSummary, hashQuery, logQuery, runbookHref, stateTone, stepError, when } from './format';
import { AuditTrail } from '../shell/AuditTrail';
import { Button, Card, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Select, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

// The service console (spec R-09-39 to R-09-44): a section of the runtime's web app for service engineers. Every action
// asks for a person's confirmation and a reason; every error shown links to its runbook in the error dictionary.

function StateBadge({ s }: { s?: string }) {
  return <Badge className={`badge ${stateTone(s)}`}>{s ?? '—'}</Badge>;
}

function Errors({ errors }: { errors?: ErrorLink[] }) {
  return <>{(errors ?? []).map((e) => <a key={e.code} className="badge-link" data-testid="error-link" href={runbookHref(e.code)} title={e.title}><Badge tone="fail">{e.code}</Badge></a>)}</>;
}

/** A refusal, with its code linked to the runbook. */
function Problem({ e }: { e: unknown }) {
  if (!e) return null;
  const err = e as ServiceError;
  return (
    <Alert tone="fail" className="error" role="alert">
      {err.message}{' '}
      {err.code && <a href={runbookHref(err.code)} data-testid="error-link">Runbook for {err.code}</a>}
    </Alert>
  );
}

/** A person's confirmation: what will happen, and why, before anything is sent. */
function Confirm({ label, detail, onConfirm, onCancel }: { label: string; detail?: ReactNode; onConfirm(reason: string): Promise<void>; onCancel(): void }) {
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    // A Fluent dialog: modal, focus kept inside it, Escape cancels.
    <Dialog open modalType="modal" onOpenChange={(_, d) => { if (!d.open) onCancel(); }}>
      <DialogSurface className="confirm" aria-label={`Confirm ${label}`}>
        <DialogBody>
          <DialogTitle>Confirm {label}</DialogTitle>
          <DialogContent className="confirm-content">
            {detail && <div className="note">{detail}</div>}
            <label>
              <span>Why (recorded with your name)</span>
              <Textarea name="reason" value={reason} onChange={(e) => setReason(e.target.value)} />
            </label>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!reason.trim() || busy} onClick={async () => { setBusy(true); try { await onConfirm(reason.trim()); } finally { setBusy(false); } }}>Confirm {label}</Button>
            <Button onClick={onCancel}>Cancel</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function Done({ r }: { r: ActionResult | any }) {
  if (!r) return null;
  return (
    <p role="status" data-testid="result">
      {r.name ?? r.releaseId ?? 'Done'}: <StateBadge s={r.status ?? r.state ?? (r.verified ? 'verified' : 'done')} />
      {r.needs && <span> waits for the consent of a {r.needs === 'BankApprover' ? 'bank approver' : r.needs}</span>}
      {r.code && <> <a href={runbookHref(r.code)} data-testid="error-link">{r.code}</a></>}
      {r.result?.message && <span className="note"> {r.result.message}</span>}
    </p>
  );
}

function HealthPage({ api }: { api: ServiceClient }) {
  const [h, setH] = useState<Health | null>(null);
  const [err, setErr] = useState<unknown>(null);
  const load = (refresh = false) => api.health(refresh).then(setH).catch(setErr);
  useEffect(() => { load(true); }, []);
  return (
    <section>
      <h1>Health</h1>
      <p className="summary">Overall <StateBadge s={h?.status} /> sampled {when(h?.sampledAt)} <Button onClick={() => load(true)}>Check now</Button></p>
      <Problem e={err} />
      <Table size="small" className="grid" aria-label="Health">
        <TableHeader><TableRow><TableHeaderCell>Component</TableHeaderCell><TableHeaderCell>Group</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>Detail</TableHeaderCell><TableHeaderCell>Version</TableHeaderCell><TableHeaderCell>History</TableHeaderCell><TableHeaderCell>Links</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{h?.components.map((c) => (
          <TableRow key={c.name} data-testid="health-row" data-name={c.name}>
            <TableCell className="mono wrap">{c.name}</TableCell><TableCell>{c.group}</TableCell><TableCell><StateBadge s={c.status} /></TableCell><TableCell className="wrap">{c.detail}</TableCell><TableCell className="mono">{c.version ?? '—'}</TableCell>
            <TableCell aria-label="history">{c.history.map((x, i) => <span key={i} className={`dot ${stateTone(x.status)}`} title={`${when(x.at)} ${x.status} ${x.ms} ms`} />)}</TableCell>
            <TableCell>{c.code && <a href={runbookHref(c.code)} data-testid="error-link">{c.code}</a>} {c.dashboard && <a href={c.dashboard} target="_blank" rel="noreferrer">Dashboard</a>}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {h && Object.keys(h.dashboards).length > 0 && (
        <p>Dashboards: {Object.entries(h.dashboards).map(([k, v]) => <a key={k} href={v} target="_blank" rel="noreferrer" style={{ marginRight: 12 }}>{k}</a>)}</p>
      )}
    </section>
  );
}

function ContentPage({ api }: { api: ServiceClient }) {
  const [versions, setVersions] = useState<Version[] | null>(null);
  const [releases, setReleases] = useState<Release[] | null>(null);
  const [ask, setAsk] = useState<{ label: string; detail?: ReactNode; run(reason: string): Promise<any> } | null>(null);
  const [result, setResult] = useState<any>(null);
  const [err, setErr] = useState<unknown>(null);
  const [diff, setDiff] = useState<any>(null);
  const [pins, setPins] = useState<StuckPin[] | null>(null);
  const [values, setValues] = useState<ValuesInForce | null>(null);
  const load = () => {
    api.versions().then(setVersions).catch(setErr); api.releases().then(setReleases).catch(setErr);
    api.stuckPins().then(setPins).catch(setErr); api.values().then(setValues).catch(setErr);
  };
  useEffect(load, []);
  const go = (label: string, run: (reason: string) => Promise<any>, detail?: ReactNode) => { setResult(null); setErr(null); setAsk({ label, run, detail }); };
  return (
    <section>
      <h1>Content</h1>
      <p className="note">Signed versions on GitHub, read only, of every kind: workflow packages, a bank's distributions and its configuration releases. A version is verified against the trusted signers before it is stored. Installing, activating, draining, rolling back and removing each wait for a bank approver's consent, another person's. This runtime serves one bank: another bank's version is listed, never installed. A distribution is removed only when no payment pinned to it is open and its longest declared wait has passed.</p>
      <Problem e={err} />
      <Done r={result} />
      {ask && <Confirm label={ask.label} detail={ask.detail} onCancel={() => setAsk(null)} onConfirm={async (reason) => {
        try { setResult(await ask.run(reason)); setAsk(null); load(); } catch (e) { setErr(e); setAsk(null); load(); }
      }} />}
      <h2>Offered on GitHub</h2>
      <Table size="small" className="grid" aria-label="Versions">
        <TableHeader><TableRow><TableHeaderCell>Tag</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Bank</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>{versions?.map((v) => (
          <TableRow key={v.tag} data-testid="version-row" data-tag={v.tag}>
            <TableCell className="mono">{v.tag}</TableCell><TableCell>{v.kind}</TableCell><TableCell>{(v as any).bank ?? ''}</TableCell><TableCell><StateBadge s={v.state} /></TableCell>
            <TableCell>{v.installable ? (
              <div className="action-buttons">
                <Button onClick={() => go(`verify ${v.release}`, (r) => api.verify(v.tag, r))}>Verify</Button>
                <Button onClick={() => api.diff(v.tag).then(setDiff).catch(setErr)}>Difference</Button>
                {v.state === 'offered' && <Button onClick={() => go(`install ${v.release}`, (r) => api.install(v.tag, r))}>Install</Button>}
              </div>
            ) : <span className="note">{v.notInstallable}</span>}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {diff && (
        <Card className="card" data-testid="diff">
          <h3>{diff.releaseId} against {diff.active ?? 'nothing active'}</h3>
          <p>{diffSummary(diff)}</p>
          <ul>{[...(diff.packs ?? []), ...diff.flows, ...diff.artefacts, ...diff.parameters].map((x: any) => <li key={x.key}><span className="mono">{x.key}</span> {x.change}: {x.active ?? '—'} → {x.offered ?? '—'}</li>)}</ul>
        </Card>
      )}
      <h2>Installed</h2>
      <Table size="small" className="grid" aria-label="Releases">
        <TableHeader><TableRow><TableHeaderCell>Release</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell className="num">Open payments</TableHeaderCell><TableHeaderCell>Stored</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>{releases?.map((r) => (
          <TableRow key={r.releaseId} data-testid="release-row" data-release={r.releaseId}>
            <TableCell className="mono">{r.releaseId}</TableCell><TableCell>{(r as any).kind}</TableCell><TableCell><StateBadge s={r.state} /></TableCell><TableCell className="num">{r.openPayments}</TableCell><TableCell>{when(r.storedAt)} by {r.storedBy}</TableCell>
            <TableCell><div className="action-buttons">
              {(r as any).kind !== 'configuration' && <>
              {r.state !== 'active' && r.state !== 'removed' && <Button onClick={() => go(`activate ${r.release}`, (reason) => api.act('activate', r.releaseId, reason))}>Activate</Button>}
              {r.state === 'active' && (r as any).kind === 'distribution' && <Button onClick={() => go(`drain ${r.releaseId}`, (reason) => api.act('drain', r.releaseId, reason))}>Drain</Button>}
              {r.state === 'active' && <Button onClick={() => go(`roll back ${r.scope}`, (reason) => api.act('rollback', r.scope, reason))}>Roll back</Button>}
              {r.state !== 'active' && r.state !== 'removed' && <Button disabled={r.openPayments > 0} title={r.openPayments > 0 ? 'Refused while a payment pinned to it is open' : ''} onClick={() => go(`remove ${r.release}`, (reason) => api.act('remove', r.releaseId, reason))}>Remove</Button>}
              </>}
              {(r as any).kind === 'configuration' && r.state === 'awaiting-consent' && <Button onClick={() => go(`put ${r.release} in force`, (reason) => api.act('put-in-force', r.releaseId, reason))}>Put in force</Button>}
              <Button onClick={() => go(`re-verify ${r.release}`, (reason) => api.act('reverify-package', r.releaseId, reason))}>Re-verify</Button>
            </div></TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      <h2>Values in force</h2>
      <p className="note">What the workers and the engine hosts read at each attempt, from the configuration releases put in force: each value with the configuration set it came from and the day it took effect. A value with a later day takes over on that day, for payments in flight too; logic does not change.</p>
      <Table size="small" className="grid" aria-label="Values in force">
        <TableHeader><TableRow><TableHeaderCell>Parameter</TableHeaderCell><TableHeaderCell>Value</TableHeaderCell><TableHeaderCell>Configuration set</TableHeaderCell><TableHeaderCell>From</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{values?.values.map((v) => (
          <TableRow key={v.parameter} data-testid="value-row" data-parameter={v.parameter}>
            <TableCell className="mono">{v.parameter}</TableCell><TableCell className="mono wrap" title={JSON.stringify(v.value)}>{shortJson(v.value)}</TableCell><TableCell className="mono">{v.configurationSet}</TableCell><TableCell>{when(v.effectiveFrom)}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {values && values.hookBindings.length > 0 && <p data-testid="hook-bindings">Hook bindings: {values.hookBindings.map((h) => <span key={h.hookPoint} className="mono" style={{ marginRight: 12 }}>{h.hookPoint} → {h.hook}{h.release ? `@${h.release}` : ''}</span>)}</p>}
      <h2>Payments pinned but never started</h2>
      <p className="note">A payment the front door pinned to a distribution whose start never reached an engine. Each one counts as an open payment of its distribution, which cannot drain until it is ended, with a bank approver's consent.</p>
      {pins?.length === 0 && <p className="muted" data-testid="no-stuck-pins">None: every payment pinned to a distribution reached its engine.</p>}
      {pins && pins.length > 0 && <Table size="small" className="grid" aria-label="Pins with no payment">
        <TableHeader><TableRow><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Distribution</TableHeaderCell><TableHeaderCell>Pinned</TableHeaderCell><TableHeaderCell>Last fact</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>{pins.map((x) => (
          <TableRow key={x.paymentId} data-testid="stuck-pin-row">
            <TableCell className="mono">{x.paymentId}</TableCell><TableCell className="mono">{x.distribution}</TableCell><TableCell>{when(x.pinnedAt)}</TableCell><TableCell>{x.lastFact ?? '—'}</TableCell>
            <TableCell><Button onClick={() => go(`end the pin of ${x.paymentId}`, (reason) => api.act('end-pin', x.paymentId, reason))}>End pin</Button></TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>}
    </section>
  );
}

/** A value for a table cell: its JSON, cut short. */
function shortJson(v: unknown): string {
  const t = typeof v === 'string' ? v : JSON.stringify(v);
  return t.length > 90 ? `${t.slice(0, 88)}…` : t;
}

function TrustPage({ api }: { api: ServiceClient }) {
  const [t, setT] = useState<Trust | null>(null);
  const [ask, setAsk] = useState<{ label: string; run(reason: string): Promise<any> } | null>(null);
  const [result, setResult] = useState<any>(null);
  const [err, setErr] = useState<unknown>(null);
  const load = () => api.trust().then(setT).catch(setErr);
  useEffect(() => { load(); }, []);
  return (
    <section>
      <h1>Signers and native modules</h1>
      <p className="note">Every version is verified against these signers before it is stored, and again on Re-verify. A distribution's native modules are its compiled activities: each file is listed in the signed bill of materials with its SHA-256, checked when the distribution is installed and again when the native host loads it. A key is added or retired with the Rotate a trusted key action, with a bank approver's consent.</p>
      <Problem e={err} />
      {t?.problem && <Alert tone="fail" role="alert">{t.problem}</Alert>}
      <Done r={result} />
      {ask && <Confirm label={ask.label} onCancel={() => setAsk(null)} onConfirm={async (reason) => {
        try { setResult(await ask.run(reason)); } catch (e) { setErr(e); }
        setAsk(null); load();
      }} />}
      <h2>Trusted signers</h2>
      <Table size="small" className="grid" aria-label="Trusted signers">
        <TableHeader><TableRow><TableHeaderCell>Key</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>Signs</TableHeaderCell><TableHeaderCell>Retires or revoked</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{t?.signers.map((k) => (
          <TableRow key={k.keyId} data-testid="signer-row">
            <TableCell className="mono">{k.keyId}</TableCell><TableCell><StateBadge s={k.state} /></TableCell>
            <TableCell>{k.feed ? `the ${k.feed} feed's configuration releases` : k.kinds?.join(', ') ?? 'every kind'}</TableCell>
            <TableCell>{k.revokedAt ? `revoked ${when(k.revokedAt)} ${k.reason ?? ''}` : k.retiredAt ? when(k.retiredAt) : '—'}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      <h2>Native modules of each distribution</h2>
      {t?.distributions.map((d) => (
        <Card key={d.releaseId} className="card" data-testid="native-distribution">
          <h3><span className="mono">{d.releaseId}</span> <StateBadge s={d.state} /> {d.contract && <span className="muted">native contract {d.contract}</span>}{' '}
            <Button onClick={() => { setResult(null); setErr(null); setAsk({ label: `re-verify ${d.releaseId}`, run: (reason) => api.act('reverify-package', d.releaseId, reason) }); }}>Re-verify</Button></h3>
          {d.modules.length === 0 && <p className="muted">No native module.</p>}
          {d.modules.length > 0 && <Table size="small" className="grid" aria-label={`Native modules of ${d.releaseId}`}>
            <TableHeader><TableRow><TableHeaderCell>Scope</TableHeaderCell><TableHeaderCell>File</TableHeaderCell><TableHeaderCell>SHA-256</TableHeaderCell><TableHeaderCell className="num">Activities</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{d.modules.flatMap((m) => m.files.map((f) => (
              <TableRow key={`${m.scope}/${f.name}`} data-testid="native-file-row">
                <TableCell className="mono">{m.scope}</TableCell><TableCell className="mono">{f.name}</TableCell><TableCell className="mono" title={f.sha256}>{f.sha256.slice(0, 23)}…</TableCell>
                <TableCell className="num" title={m.activities.join(', ')}>{f.name === `${m.assembly}.dll` ? m.activities.length : ''}</TableCell>
              </TableRow>
            )))}</TableBody>
          </Table>}
        </Card>
      ))}
    </section>
  );
}

function LogsPage({ api }: { api: ServiceClient }) {
  const init = hashQuery(location.hash);
  // trace: a fact's, this host's and the other hosts' logs of one trace (R-09-69, R-09-71); the back end is asked by trace or time.
  const [f, setF] = useState<Record<string, string>>({ code: init.code ?? '', leg: '', scheme: '', tenant: '', payment: init.payment ?? '', trace: init.trace ?? '', from: '', to: '' });
  const [items, setItems] = useState<LogItem[] | null>(null);
  const [err, setErr] = useState<unknown>(null);
  const load = () => api.logs(logQuery(f)).then((r) => setItems(r.items)).catch(setErr);
  useEffect(() => { load(); }, []);
  return (
    <section>
      <h1>Error and event log</h1>
      <div className="filters">
        {Object.keys(f).map((k) => (
          <label key={k}><span>{k}</span><Input name={k} value={f[k]} onChange={(e) => setF({ ...f, [k]: e.target.value })} placeholder={k === 'from' || k === 'to' ? '2026-11-22T00:00Z' : ''} /></label>
        ))}
        <Button appearance="primary" onClick={load}>Filter</Button>
      </div>
      <Problem e={err} />
      <div className="table-wrap">
        <Table size="small" className="grid" aria-label="Log">
          <TableHeader><TableRow><TableHeaderCell>At</TableHeaderCell><TableHeaderCell>Level</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell><TableHeaderCell>What</TableHeaderCell><TableHeaderCell>Errors</TableHeaderCell></TableRow></TableHeader>
          <TableBody>{items?.map((i, n) => (
            <TableRow key={n} data-testid="log-row" className={i.level === 'Error' ? 'error-row' : ''}>
              <TableCell className="mono">{when(i.at)}</TableCell><TableCell><StateBadge s={i.level === 'Error' ? 'fail' : 'ok'} /></TableCell>
              <TableCell>{i.payment ? <a href={i.payment} title="Open in the operations console">{i.subject}</a> : <span className="mono">{i.subject ?? i.category}</span>} {i.tenant && <span className="muted">{i.tenant}</span>} {i.scheme && <span className="muted">{i.scheme}</span>}</TableCell>
              <TableCell>{i.source === 'fact' ? <><span className="mono">{i.kind}:{i.code}</span> {i.step && <span className="muted">{i.step} ({i.activity})</span>} {stepError(i.error, i.errors) && <span className="mono" data-testid="step-error">{stepError(i.error, i.errors)}</span>} <span className="muted">{i.actor}</span></> : <>{i.source === 'backend' && <Badge className="badge">{i.category}</Badge>} {i.message}</>}
                {i.traceId && <>{' '}{i.trace ? <a className="mono" href={i.trace} target="_blank" rel="noreferrer">trace {i.traceId.slice(0, 8)}</a> : <a className="mono" href={`#/service/logs?trace=${i.traceId}`}>trace {i.traceId.slice(0, 8)}</a>}</>}</TableCell>
              <TableCell><Errors errors={i.errors} /></TableCell>
            </TableRow>
          ))}</TableBody>
        </Table>
      </div>
      {items?.length === 0 && <p className="muted">Nothing matches.</p>}
    </section>
  );
}

function ErrorsPage({ api, code }: { api: ServiceClient; code?: string }) {
  const [list, setList] = useState<any[] | null>(null);
  const [q, setQ] = useState('');
  const [e, setE] = useState<ErrorEntry | null>(null);
  const [err, setErr] = useState<unknown>(null);
  useEffect(() => { if (code) api.error(code).then(setE).catch(setErr); else api.errors().then(setList).catch(setErr); }, [code]);
  if (code) return (
    <section data-testid="runbook">
      <p><a href="#/service/errors">All errors</a></p>
      <Problem e={err} />
      {e && <>
        <h1><span className="mono">{e.code}</span> {e.title}</h1>
        <p className="summary"><StateBadge s={e.category} /> {e.retryable ? 'retryable' : 'not retryable'} {e.isoReasonCode && <>ISO 20022 <span className="mono">{e.isoReasonCode}</span></>} <span className="muted">{e.dictionary}, raised by {e.raisedBy.join(', ')}</span></p>
        <p>{e.meaning}</p>
        <h2>Likely causes</h2><ul>{e.causes.map((c) => <li key={c}>{c}</li>)}</ul>
        <h2>Runbook</h2>
        <h3>Checks</h3><ol>{e.runbook.checks.map((c) => <li key={c}>{c}</li>)}</ol>
        <h3>Safe actions</h3><ul>{e.runbook.safeActions.map((c) => <li key={c}>{c}</li>)}</ul>
        {e.actionsOffered.length > 0 && <><h3>Troubleshooting actions</h3><ul>{e.actionsOffered.map((a) => (
          <li key={a.id}><a href={`#/service/actions?action=${a.id}`}>{a.title}</a> <StateBadge s={a.path === 'built' ? 'ok' : 'fail'} />{a.risky && <span className="muted"> needs a bank approver's consent</span>}{a.missing && <span className="note"> {a.missing}</span>}</li>
        ))}</ul></>}
        <h3>Escalation</h3><p>{e.runbook.escalation}</p>
      </>}
    </section>
  );
  const shown = list?.filter((x) => !q || x.code.toLowerCase().includes(q.toLowerCase()) || (x.title ?? '').toLowerCase().includes(q.toLowerCase()));
  return (
    <section>
      <h1>Error dictionary and runbooks</h1>
      <div className="filters"><label><span>Search</span><Input name="q" value={q} onChange={(ev) => setQ(ev.target.value)} /></label></div>
      <Problem e={err} />
      <Table size="small" className="grid" aria-label="Errors">
        <TableHeader><TableRow><TableHeaderCell>Code</TableHeaderCell><TableHeaderCell>Scope</TableHeaderCell><TableHeaderCell>Title</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Retryable</TableHeaderCell><TableHeaderCell>ISO</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{shown?.map((x) => (
          <TableRow key={`${x.scope}/${x.code}`}><TableCell><a className="mono" href={x.link}>{x.code}</a></TableCell><TableCell>{x.scope}</TableCell><TableCell>{x.title}</TableCell><TableCell>{x.category}</TableCell><TableCell>{x.retryable ? 'yes' : 'no'}</TableCell><TableCell className="mono">{x.iso ?? ''}</TableCell></TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}

function ActionsPage({ api, approver }: { api: ServiceClient; approver: boolean }) {
  const init = hashQuery(location.hash);
  const [catalogue, setCatalogue] = useState<ActionSpec[] | null>(null);
  const [action, setAction] = useState(init.action ?? '');
  const [target, setTarget] = useState(init.target ?? '');
  const [params, setParams] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [pending, setPending] = useState<any[] | null>(null);
  const [deciding, setDeciding] = useState<{ id: string; approve: boolean } | null>(null);
  const [result, setResult] = useState<any>(null);
  const [err, setErr] = useState<unknown>(null);
  const load = () => { api.actions().then(setCatalogue).catch(setErr); api.pending().then(setPending).catch(setErr); };
  useEffect(load, []);
  const spec = catalogue?.find((a) => a.id === action);
  return (
    <section>
      <h1>Troubleshooting actions</h1>
      <p className="note">Each action is recorded with your name, the time and your reason. One that changes the system is carried out only when a bank approver, another person, consents. An action on a running payment reaches its engine through the engine's host, which records the delivery on the payment's timeline; one that does not apply to the payment now is refused with its code.</p>
      <Problem e={err} />
      <Done r={result} />
      <div className="filters">
        <label><span>Action</span>
          <Select name="action" value={action} onChange={(e) => { setAction(e.target.value); setConfirming(false); }}>
            <option value="">Choose…</option>
            {catalogue?.map((a) => <option key={a.id} value={a.id}>{a.title}{a.path === 'missing' ? ' (not built)' : ''}</option>)}
          </Select>
        </label>
        <label><span>{spec?.target ?? 'target'}</span><Input name="target" value={target} onChange={(e) => setTarget(e.target.value)} /></label>
        <label><span>Parameters (JSON)</span><Input name="parameters" value={params} onChange={(e) => setParams(e.target.value)} /></label>
        <Button appearance="primary" disabled={!spec} onClick={() => { setResult(null); setErr(null); setConfirming(true); }}>Ask for it</Button>
      </div>
      {spec && <p className="note" data-testid="action-spec">{spec.does} {spec.risky && 'It needs a bank approver\'s consent.'} {spec.missing}</p>}
      {confirming && spec && <Confirm label={spec.title.toLowerCase()} detail={<>{spec.title} on <span className="mono">{target || '(none)'}</span>.</>} onCancel={() => setConfirming(false)} onConfirm={async (reason) => {
        try { setResult(await api.act(spec.id, target || undefined, reason, params ? JSON.parse(params) : undefined)); } catch (e) { setErr(e); }
        setConfirming(false); load();
      }} />}
      <h2>Waiting for a bank approver's consent</h2>
      {pending?.length === 0 && <p className="muted">Nothing waits.</p>}
      <Table size="small" className="grid" aria-label="Pending actions">
        <TableHeader><TableRow><TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Target</TableHeaderCell><TableHeaderCell>Asked by</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>{pending?.map((p) => (
          <TableRow key={p.data.action} data-testid="pending-row">
            <TableCell>{p.data.name}</TableCell><TableCell className="mono">{p.data.target ?? '—'}</TableCell><TableCell>{p.data.by} <span className="muted">{when(p.at)}</span></TableCell><TableCell className="wrap">{p.data.why}</TableCell>
            <TableCell>{approver && <div className="action-buttons"><Button onClick={() => setDeciding({ id: p.data.action, approve: true })}>Approve</Button><Button onClick={() => setDeciding({ id: p.data.action, approve: false })}>Reject</Button></div>}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {deciding && <Confirm label={deciding.approve ? 'approval' : 'rejection'} onCancel={() => setDeciding(null)} onConfirm={async (reason) => {
        try { setResult(await api.decide(deciding.id, deciding.approve, reason)); } catch (e) { setErr(e); }
        setDeciding(null); load();
      }} />}
      <h2>Catalogue</h2>
      <Table size="small" className="grid" aria-label="Catalogue">
        <TableHeader><TableRow><TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Path</TableHeaderCell><TableHeaderCell>Approver</TableHeaderCell><TableHeaderCell>What it does</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{catalogue?.map((a) => (
          <TableRow key={a.id}><TableCell className="mono">{a.id}</TableCell><TableCell><StateBadge s={a.path === 'built' ? 'ok' : 'fail'} />{a.path}</TableCell><TableCell>{a.risky ? 'yes' : 'no'}</TableCell><TableCell className="wrap">{a.does} <span className="note">{a.missing}</span></TableCell></TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}

function BackupPage({ api }: { api: ServiceClient }) {
  const [ask, setAsk] = useState<null | 'export' | 'dry' | 'restore'>(null);
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<any>(null);
  const [err, setErr] = useState<unknown>(null);
  return (
    <section>
      <h1>Configuration backup and restore</h1>
      <p className="note">A signed backup of the releases' ids, activations, configuration values, bindings (secret references only, never a value), the role map and the trusted signers. A restore goes only into an empty or a test runtime, after a dry run, and only once a bank approver consents.</p>
      <Problem e={err} />
      <div className="action-buttons">
        <Button onClick={() => setAsk('export')}>Export a backup</Button>
        <input type="file" name="backup" accept=".zip" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        <Button disabled={!file} onClick={() => api.verifyBackup(file!).then(setResult).catch(setErr)}>Verify</Button>
        <Button disabled={!file} onClick={() => setAsk('dry')}>Dry run</Button>
        <Button disabled={!file} onClick={() => setAsk('restore')}>Restore</Button>
      </div>
      {ask && <Confirm label={ask === 'export' ? 'backup' : ask === 'dry' ? 'dry run' : 'restore'} onCancel={() => setAsk(null)} onConfirm={async (reason) => {
        setErr(null);
        try {
          if (ask === 'export') {
            const blob = await api.backup(reason);
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = 'transacthub-backup.zip';
            a.click();
            setResult({ exported: true });
          } else setResult(await api.restore(file!, ask === 'dry', reason));
        } catch (e) { setErr(e); }
        setAsk(null);
      }} />}
      {result && <pre className="mono card" data-testid="backup-result">{JSON.stringify(result, null, 2)}</pre>}
    </section>
  );
}

function AssistantPage({ api }: { api: ServiceClient }) {
  const [status, setStatus] = useState<{ enabled: boolean; model: string } | null>(null);
  const [q, setQ] = useState('');
  const [payment, setPayment] = useState(hashQuery(location.hash).payment ?? '');
  const [turns, setTurns] = useState<{ question: string; a: Answer }[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<unknown>(null);
  useEffect(() => { api.assistantStatus().then(setStatus).catch(setErr); }, []);
  if (status && !status.enabled) return <section><h1>Assistant</h1><p className="muted">The assistant is off (TH_ASSISTANT_ENABLED). Use the runbooks directly.</p></section>;
  return (
    <section>
      <h1>Troubleshooting assistant</h1>
      <p className="note">Personal data is masked before anything is sent to the model ({status?.model}). It reads only; it may propose an action, which you confirm on the Actions page. Its answers cite their sources.</p>
      <Problem e={err} />
      {turns.map((t, i) => (
        <Card key={i} className="card" data-testid="answer">
          <p><strong>{t.question}</strong></p>
          <p style={{ whiteSpace: 'pre-wrap' }}>{t.a.answer}</p>
          <p className="muted">Sources: {t.a.sources.map((s) => s.startsWith('error:') ? <a key={s} href={runbookHref(s.slice(6))} style={{ marginRight: 8 }}>{s}</a> : <span key={s} className="mono" style={{ marginRight: 8 }}>{s}</span>)}</p>
          {t.a.proposals.map((p, j) => (
            <p key={j} data-testid="proposal">Proposed: <a href={p.confirm}>{p.title}</a> on <span className="mono">{p.target ?? '—'}</span>, by the runbook <a href={p.runbookLink}>{p.runbook}</a>. {p.why} {p.risky && <span className="muted">Needs a bank approver's consent.</span>}</p>
          ))}
        </Card>
      ))}
      <div className="filters">
        <label style={{ flex: 1 }}><span>Question</span><Textarea name="question" value={q} onChange={(e) => setQ(e.target.value)} /></label>
        <label><span>Payment</span><Input name="payment" value={payment} onChange={(e) => setPayment(e.target.value)} /></label>
        <Button appearance="primary" disabled={!q.trim() || busy} onClick={async () => {
          setBusy(true); setErr(null);
          try { const a = await api.ask(q, payment || undefined, turns.map((t) => ({ question: t.question, answer: t.a.answer }))); setTurns([...turns, { question: q, a }]); setQ(''); } catch (e) { setErr(e); }
          setBusy(false);
        }}>Ask</Button>
      </div>
    </section>
  );
}

/** The service section: its pages in a tab bar, routed under #/service/<page>/… */
export function ServiceSection({ api, path, approver }: { api: ServiceClient; path: string[]; approver: boolean }) {
  const page = PAGES.find((p) => p.id === path[0])?.id ?? 'health';
  return (
    <div>
      <TabList className="tabs" aria-label="Service" selectedValue={page} onTabSelect={(_, d) => { location.hash = `#/service/${d.value}`; }}>
        {PAGES.map((p) => <Tab key={p.id} value={p.id}>{p.title}</Tab>)}
      </TabList>
      {page === 'health' && <HealthPage api={api} />}
      {page === 'content' && <ContentPage api={api} />}
      {page === 'logs' && <LogsPage key={location.hash} api={api} />}
      {page === 'errors' && <ErrorsPage api={api} code={path[1]} />}
      {page === 'actions' && <ActionsPage key={location.hash} api={api} approver={approver} />}
      {page === 'backup' && <BackupPage api={api} />}
      {page === 'trust' && <TrustPage api={api} />}
      {page === 'assistant' && <AssistantPage api={api} />}
      {page === 'audit' && <AuditTrail title="Service audit" load={async (q) => {
        const [rows, chain] = await Promise.all([api.audit(q), api.auditChain()]);
        return { rows, chain };
      }} />}
    </div>
  );
}
