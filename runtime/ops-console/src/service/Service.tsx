import { useEffect, useState, type ReactNode } from 'react';
import { ServiceError, type ActionResult, type ActionSpec, type Answer, type ErrorEntry, type ErrorLink, type Health, type LogItem, type Release, type ServiceClient, type Version } from './api';
import { PAGES, diffSummary, hashQuery, logQuery, runbookHref, stateTone, when } from './format';

// The service console (spec R-09-39 to R-09-44): a section of the runtime's web app for service engineers. Every action
// asks for a person's confirmation and a reason; every error shown links to its runbook in the error dictionary.

function Badge({ s }: { s?: string }) {
  return <span className={`badge ${stateTone(s)}`}>{s ?? '—'}</span>;
}

function Errors({ errors }: { errors?: ErrorLink[] }) {
  return <>{(errors ?? []).map((e) => <a key={e.code} className="badge fail" data-testid="error-link" href={runbookHref(e.code)} title={e.title}>{e.code}</a>)}</>;
}

/** A refusal, with its code linked to the runbook. */
function Problem({ e }: { e: unknown }) {
  if (!e) return null;
  const err = e as ServiceError;
  return (
    <p className="error" role="alert">
      {err.message}{' '}
      {err.code && <a href={runbookHref(err.code)} data-testid="error-link">Runbook for {err.code}</a>}
    </p>
  );
}

/** A person's confirmation: what will happen, and why, before anything is sent. */
function Confirm({ label, detail, onConfirm, onCancel }: { label: string; detail?: ReactNode; onConfirm(reason: string): Promise<void>; onCancel(): void }) {
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <div className="confirm" role="dialog" aria-label={`Confirm ${label}`}>
      {detail && <div className="note">{detail}</div>}
      <label>
        <span>Why (recorded with your name)</span>
        <textarea name="reason" value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>
      <div className="action-buttons">
        <button className="primary" disabled={!reason.trim() || busy} onClick={async () => { setBusy(true); try { await onConfirm(reason.trim()); } finally { setBusy(false); } }}>Confirm {label}</button>
        <button onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}

function Done({ r }: { r: ActionResult | any }) {
  if (!r) return null;
  return (
    <p role="status" data-testid="result">
      {r.name ?? r.releaseId ?? 'Done'}: <Badge s={r.status ?? r.state ?? (r.verified ? 'verified' : 'done')} />
      {r.needs && <span> waits for a {r.needs}</span>}
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
      <p className="summary">Overall <Badge s={h?.status} /> sampled {when(h?.sampledAt)} <button onClick={() => load(true)}>Check now</button></p>
      <Problem e={err} />
      <table className="grid" aria-label="Health">
        <thead><tr><th>Component</th><th>Group</th><th>State</th><th>Detail</th><th>Version</th><th>History</th><th>Links</th></tr></thead>
        <tbody>{h?.components.map((c) => (
          <tr key={c.name} data-testid="health-row" data-name={c.name}>
            <td className="mono">{c.name}</td><td>{c.group}</td><td><Badge s={c.status} /></td><td>{c.detail}</td><td className="mono">{c.version ?? '—'}</td>
            <td aria-label="history">{c.history.map((x, i) => <span key={i} className={`dot ${stateTone(x.status)}`} title={`${when(x.at)} ${x.status} ${x.ms} ms`} />)}</td>
            <td>{c.code && <a href={runbookHref(c.code)} data-testid="error-link">{c.code}</a>} {c.dashboard && <a href={c.dashboard} target="_blank" rel="noreferrer">Dashboard</a>}</td>
          </tr>
        ))}</tbody>
      </table>
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
  const load = () => { api.versions().then(setVersions).catch(setErr); api.releases().then(setReleases).catch(setErr); };
  useEffect(load, []);
  const go = (label: string, run: (reason: string) => Promise<any>, detail?: ReactNode) => { setResult(null); setErr(null); setAsk({ label, run, detail }); };
  return (
    <section>
      <h1>Content</h1>
      <p className="note">Signed versions on GitHub, read only, of every kind: workflow packages, a bank's distributions and its configuration releases. A version is verified against the trusted signers before it is stored. Activating, draining, rolling back and removing need a ServiceApprover. A distribution is removed only when no payment pinned to it is open and its longest declared wait has passed.</p>
      <Problem e={err} />
      <Done r={result} />
      {ask && <Confirm label={ask.label} detail={ask.detail} onCancel={() => setAsk(null)} onConfirm={async (reason) => {
        try { setResult(await ask.run(reason)); setAsk(null); load(); } catch (e) { setErr(e); setAsk(null); load(); }
      }} />}
      <h2>Offered on GitHub</h2>
      <table className="grid" aria-label="Versions">
        <thead><tr><th>Tag</th><th>Kind</th><th>Bank</th><th>State</th><th></th></tr></thead>
        <tbody>{versions?.map((v) => (
          <tr key={v.tag} data-testid="version-row" data-tag={v.tag}>
            <td className="mono">{v.tag}</td><td>{v.kind}</td><td>{(v as any).bank ?? ''}</td><td><Badge s={v.state} /></td>
            <td>{v.installable ? (
              <div className="action-buttons">
                <button onClick={() => go(`verify ${v.release}`, (r) => api.verify(v.tag, r))}>Verify</button>
                <button onClick={() => api.diff(v.tag).then(setDiff).catch(setErr)}>Difference</button>
                {v.state === 'offered' && <button onClick={() => go(`install ${v.release}`, (r) => api.install(v.tag, r))}>Install</button>}
              </div>
            ) : <span className="note">{v.notInstallable}</span>}</td>
          </tr>
        ))}</tbody>
      </table>
      {diff && (
        <div className="card" data-testid="diff">
          <h3>{diff.releaseId} against {diff.active ?? 'nothing active'}</h3>
          <p>{diffSummary(diff)}</p>
          <ul>{[...(diff.packs ?? []), ...diff.flows, ...diff.artefacts, ...diff.parameters].map((x: any) => <li key={x.key}><span className="mono">{x.key}</span> {x.change}: {x.active ?? '—'} → {x.offered ?? '—'}</li>)}</ul>
        </div>
      )}
      <h2>Installed</h2>
      <table className="grid" aria-label="Releases">
        <thead><tr><th>Release</th><th>Kind</th><th>State</th><th className="num">Open payments</th><th>Stored</th><th></th></tr></thead>
        <tbody>{releases?.map((r) => (
          <tr key={r.releaseId} data-testid="release-row" data-release={r.releaseId}>
            <td className="mono">{r.releaseId}</td><td>{(r as any).kind}</td><td><Badge s={r.state} /></td><td className="num">{r.openPayments}</td><td>{when(r.storedAt)} by {r.storedBy}</td>
            <td><div className="action-buttons">
              {(r as any).kind !== 'configuration' && <>
              {r.state !== 'active' && r.state !== 'removed' && <button onClick={() => go(`activate ${r.release}`, (reason) => api.act('activate', r.releaseId, reason))}>Activate</button>}
              {r.state === 'active' && (r as any).kind === 'distribution' && <button onClick={() => go(`drain ${r.releaseId}`, (reason) => api.act('drain', r.releaseId, reason))}>Drain</button>}
              {r.state === 'active' && <button onClick={() => go(`roll back ${r.scope}`, (reason) => api.act('rollback', r.scope, reason))}>Roll back</button>}
              {r.state !== 'active' && r.state !== 'removed' && <button disabled={r.openPayments > 0} title={r.openPayments > 0 ? 'Refused while a payment pinned to it is open' : ''} onClick={() => go(`remove ${r.release}`, (reason) => api.act('remove', r.releaseId, reason))}>Remove</button>}
              </>}
              <button onClick={() => go(`re-verify ${r.release}`, (reason) => api.act('reverify-package', r.releaseId, reason))}>Re-verify</button>
            </div></td>
          </tr>
        ))}</tbody>
      </table>
    </section>
  );
}

function LogsPage({ api }: { api: ServiceClient }) {
  const init = hashQuery(location.hash);
  const [f, setF] = useState<Record<string, string>>({ code: init.code ?? '', leg: '', scheme: '', tenant: '', payment: init.payment ?? '', from: '', to: '' });
  const [items, setItems] = useState<LogItem[] | null>(null);
  const [err, setErr] = useState<unknown>(null);
  const load = () => api.logs(logQuery(f)).then((r) => setItems(r.items)).catch(setErr);
  useEffect(() => { load(); }, []);
  return (
    <section>
      <h1>Error and event log</h1>
      <div className="filters">
        {Object.keys(f).map((k) => (
          <label key={k}><span>{k}</span><input name={k} value={f[k]} onChange={(e) => setF({ ...f, [k]: e.target.value })} placeholder={k === 'from' || k === 'to' ? '2026-11-22T00:00Z' : ''} /></label>
        ))}
        <button className="primary" onClick={load}>Filter</button>
      </div>
      <Problem e={err} />
      <div className="table-wrap">
        <table className="grid" aria-label="Log">
          <thead><tr><th>At</th><th>Level</th><th>Where</th><th>What</th><th>Errors</th></tr></thead>
          <tbody>{items?.map((i, n) => (
            <tr key={n} data-testid="log-row" className={i.level === 'Error' ? 'error-row' : ''}>
              <td className="mono">{when(i.at)}</td><td><Badge s={i.level === 'Error' ? 'fail' : 'ok'} /></td>
              <td>{i.payment ? <a href={i.payment} title="Open in the operations console">{i.subject}</a> : <span className="mono">{i.subject ?? i.category}</span>} {i.tenant && <span className="muted">{i.tenant}</span>} {i.scheme && <span className="muted">{i.scheme}</span>}</td>
              <td>{i.source === 'fact' ? <><span className="mono">{i.kind}:{i.code}</span> {i.step && <span className="muted">{i.step} ({i.activity})</span>} <span className="muted">{i.actor}</span></> : i.message}</td>
              <td><Errors errors={i.errors} /></td>
            </tr>
          ))}</tbody>
        </table>
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
        <p className="summary"><Badge s={e.category} /> {e.retryable ? 'retryable' : 'not retryable'} {e.isoReasonCode && <>ISO 20022 <span className="mono">{e.isoReasonCode}</span></>} <span className="muted">{e.dictionary}, raised by {e.raisedBy.join(', ')}</span></p>
        <p>{e.meaning}</p>
        <h2>Likely causes</h2><ul>{e.causes.map((c) => <li key={c}>{c}</li>)}</ul>
        <h2>Runbook</h2>
        <h3>Checks</h3><ol>{e.runbook.checks.map((c) => <li key={c}>{c}</li>)}</ol>
        <h3>Safe actions</h3><ul>{e.runbook.safeActions.map((c) => <li key={c}>{c}</li>)}</ul>
        {e.actionsOffered.length > 0 && <><h3>Troubleshooting actions</h3><ul>{e.actionsOffered.map((a) => (
          <li key={a.id}><a href={`#/service/actions?action=${a.id}`}>{a.title}</a> <Badge s={a.path === 'built' ? 'ok' : 'fail'} />{a.risky && <span className="muted"> needs a ServiceApprover</span>}{a.missing && <span className="note"> {a.missing}</span>}</li>
        ))}</ul></>}
        <h3>Escalation</h3><p>{e.runbook.escalation}</p>
      </>}
    </section>
  );
  const shown = list?.filter((x) => !q || x.code.toLowerCase().includes(q.toLowerCase()) || (x.title ?? '').toLowerCase().includes(q.toLowerCase()));
  return (
    <section>
      <h1>Error dictionary and runbooks</h1>
      <div className="filters"><label><span>Search</span><input name="q" value={q} onChange={(ev) => setQ(ev.target.value)} /></label></div>
      <Problem e={err} />
      <table className="grid" aria-label="Errors">
        <thead><tr><th>Code</th><th>Scope</th><th>Title</th><th>Kind</th><th>Retryable</th><th>ISO</th></tr></thead>
        <tbody>{shown?.map((x) => (
          <tr key={`${x.scope}/${x.code}`}><td><a className="mono" href={x.link}>{x.code}</a></td><td>{x.scope}</td><td>{x.title}</td><td>{x.category}</td><td>{x.retryable ? 'yes' : 'no'}</td><td className="mono">{x.iso ?? ''}</td></tr>
        ))}</tbody>
      </table>
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
      <p className="note">Each action is recorded with your name, the time and your reason. A risky one is carried out only when a ServiceApprover, another person, approves it. An action on a running payment reaches its engine through the engine's host, which records the delivery on the payment's timeline; one that does not apply to the payment now is refused with its code.</p>
      <Problem e={err} />
      <Done r={result} />
      <div className="filters">
        <label><span>Action</span>
          <select name="action" value={action} onChange={(e) => { setAction(e.target.value); setConfirming(false); }}>
            <option value="">Choose…</option>
            {catalogue?.map((a) => <option key={a.id} value={a.id}>{a.title}{a.path === 'missing' ? ' (not built)' : ''}</option>)}
          </select>
        </label>
        <label><span>{spec?.target ?? 'target'}</span><input name="target" value={target} onChange={(e) => setTarget(e.target.value)} /></label>
        <label><span>Parameters (JSON)</span><input name="parameters" value={params} onChange={(e) => setParams(e.target.value)} /></label>
        <button className="primary" disabled={!spec} onClick={() => { setResult(null); setErr(null); setConfirming(true); }}>Ask for it</button>
      </div>
      {spec && <p className="note" data-testid="action-spec">{spec.does} {spec.risky && 'It needs a ServiceApprover.'} {spec.missing}</p>}
      {confirming && spec && <Confirm label={spec.title.toLowerCase()} detail={<>{spec.title} on <span className="mono">{target || '(none)'}</span>.</>} onCancel={() => setConfirming(false)} onConfirm={async (reason) => {
        try { setResult(await api.act(spec.id, target || undefined, reason, params ? JSON.parse(params) : undefined)); } catch (e) { setErr(e); }
        setConfirming(false); load();
      }} />}
      <h2>Waiting for a ServiceApprover</h2>
      {pending?.length === 0 && <p className="muted">Nothing waits.</p>}
      <table className="grid" aria-label="Pending actions">
        <thead><tr><th>Action</th><th>Target</th><th>Asked by</th><th>Why</th><th></th></tr></thead>
        <tbody>{pending?.map((p) => (
          <tr key={p.data.action} data-testid="pending-row">
            <td>{p.data.name}</td><td className="mono">{p.data.target ?? '—'}</td><td>{p.data.by} <span className="muted">{when(p.at)}</span></td><td>{p.data.why}</td>
            <td>{approver && <div className="action-buttons"><button onClick={() => setDeciding({ id: p.data.action, approve: true })}>Approve</button><button onClick={() => setDeciding({ id: p.data.action, approve: false })}>Reject</button></div>}</td>
          </tr>
        ))}</tbody>
      </table>
      {deciding && <Confirm label={deciding.approve ? 'approval' : 'rejection'} onCancel={() => setDeciding(null)} onConfirm={async (reason) => {
        try { setResult(await api.decide(deciding.id, deciding.approve, reason)); } catch (e) { setErr(e); }
        setDeciding(null); load();
      }} />}
      <h2>Catalogue</h2>
      <table className="grid" aria-label="Catalogue">
        <thead><tr><th>Action</th><th>Path</th><th>Approver</th><th>What it does</th></tr></thead>
        <tbody>{catalogue?.map((a) => (
          <tr key={a.id}><td className="mono">{a.id}</td><td><Badge s={a.path === 'built' ? 'ok' : 'fail'} />{a.path}</td><td>{a.risky ? 'yes' : 'no'}</td><td>{a.does} <span className="note">{a.missing}</span></td></tr>
        ))}</tbody>
      </table>
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
      <p className="note">A signed backup of the releases' ids, activations, configuration values, bindings (secret references only, never a value), the role map and the trusted signers. A restore goes only into an empty or a test runtime, after a dry run.</p>
      <Problem e={err} />
      <div className="action-buttons">
        <button onClick={() => setAsk('export')}>Export a backup</button>
        <input type="file" name="backup" accept=".zip" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        <button disabled={!file} onClick={() => api.verifyBackup(file!).then(setResult).catch(setErr)}>Verify</button>
        <button disabled={!file} onClick={() => setAsk('dry')}>Dry run</button>
        <button disabled={!file} onClick={() => setAsk('restore')}>Restore</button>
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
        <div key={i} className="card" data-testid="answer">
          <p><strong>{t.question}</strong></p>
          <p style={{ whiteSpace: 'pre-wrap' }}>{t.a.answer}</p>
          <p className="muted">Sources: {t.a.sources.map((s) => s.startsWith('error:') ? <a key={s} href={runbookHref(s.slice(6))} style={{ marginRight: 8 }}>{s}</a> : <span key={s} className="mono" style={{ marginRight: 8 }}>{s}</span>)}</p>
          {t.a.proposals.map((p, j) => (
            <p key={j} data-testid="proposal">Proposed: <a href={p.confirm}>{p.title}</a> on <span className="mono">{p.target ?? '—'}</span>, by the runbook <a href={p.runbookLink}>{p.runbook}</a>. {p.why} {p.risky && <span className="muted">Needs a ServiceApprover.</span>}</p>
          ))}
        </div>
      ))}
      <div className="filters">
        <label style={{ flex: 1 }}><span>Question</span><textarea name="question" value={q} onChange={(e) => setQ(e.target.value)} /></label>
        <label><span>Payment</span><input name="payment" value={payment} onChange={(e) => setPayment(e.target.value)} /></label>
        <button className="primary" disabled={!q.trim() || busy} onClick={async () => {
          setBusy(true); setErr(null);
          try { const a = await api.ask(q, payment || undefined, turns.map((t) => ({ question: t.question, answer: t.a.answer }))); setTurns([...turns, { question: q, a }]); setQ(''); } catch (e) { setErr(e); }
          setBusy(false);
        }}>Ask</button>
      </div>
    </section>
  );
}

/** The service section: its pages in a tab bar, routed under #/service/<page>/… */
export function ServiceSection({ api, path, approver }: { api: ServiceClient; path: string[]; approver: boolean }) {
  const page = PAGES.find((p) => p.id === path[0])?.id ?? 'health';
  return (
    <div>
      <nav className="tabs" aria-label="Service">
        {PAGES.map((p) => <a key={p.id} href={`#/service/${p.id}`} className={`tab ${p.id === page ? 'active' : ''}`}>{p.title}</a>)}
      </nav>
      {page === 'health' && <HealthPage api={api} />}
      {page === 'content' && <ContentPage api={api} />}
      {page === 'logs' && <LogsPage key={location.hash} api={api} />}
      {page === 'errors' && <ErrorsPage api={api} code={path[1]} />}
      {page === 'actions' && <ActionsPage key={location.hash} api={api} approver={approver} />}
      {page === 'backup' && <BackupPage api={api} />}
      {page === 'assistant' && <AssistantPage api={api} />}
    </div>
  );
}
