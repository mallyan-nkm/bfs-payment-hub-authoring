import { useState } from 'react';
import { del, get, post, q, type Issue, type Summary } from '../api';
import { ArtefactLink, Badge, ErrorBox, Issues, KV, Loading, PassFail, Section, Tabs, Val, useLoad, useStudio } from '../ui';

// Governance (F-24, F-25, F-32): drafts, the audit log, supersessions and the whole-store checks.

export function Governance() {
  const [tab, setTab] = useState<'drafts' | 'audit' | 'supersessions' | 'checks'>('drafts');
  return (
    <div className="view">
      <div className="view-head">
        <h2>Governance</h2>
        <p className="muted">Drafts waiting for publish, who changed what and who approved it, supersessions, and every static check across the store.</p>
      </div>
      <Tabs tabs={[{ id: 'drafts', label: 'Drafts' }, { id: 'audit', label: 'Audit log' }, { id: 'supersessions', label: 'Supersessions' }, { id: 'checks', label: 'Store checks' }]} value={tab} onChange={setTab} />
      {tab === 'drafts' && <Drafts />}
      {tab === 'audit' && <Audit />}
      {tab === 'supersessions' && <Supersessions />}
      {tab === 'checks' && <StoreChecks />}
    </div>
  );
}

function Drafts() {
  const { tenant, actor, go } = useStudio();
  const drafts = useLoad(() => get<Summary[]>('/api/drafts'), [tenant]);
  const [error, setError] = useState<string>();
  const remove = async (key: string) => {
    if (!window.confirm(`Delete draft ${key}?`)) return;
    try {
      await del(`/api/drafts/${encodeURIComponent(key)}${q({ actor })}`);
      drafts.reload();
    } catch (e: any) {
      setError(e.message);
    }
  };
  return (
    <Section title={`Drafts of ${tenant}`} count={drafts.data?.length}>
      <ErrorBox error={drafts.error || error} />
      {drafts.loading && !drafts.data ? <Loading /> : !drafts.data?.length ? <p className="muted">No drafts.</p> : (
        <table className="grid">
          <thead><tr><th>Draft</th><th>Kind</th><th>Title</th><th>Errors</th><th></th></tr></thead>
          <tbody>
            {drafts.data.map((d) => (
              <tr key={d.key}>
                <td><ArtefactLink name={d.name} keyOf={d.key} /> <span className="mono muted">@{d.release}</span></td>
                <td>{d.kind} {d.type}</td>
                <td>{d.title}</td>
                <td>{d.issues ? <Badge tone="fail">{d.issues}</Badge> : <Badge tone="pass">none</Badge>}</td>
                <td className="nowrap">
                  {['workflow', 'hook'].includes(d.kind) && <button onClick={() => go(`/designer?open=${encodeURIComponent(d.key)}&draft=1`)}>Open</button>}{' '}
                  <button className="link danger" onClick={() => remove(d.key)}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
  );
}

function Audit() {
  const { tenant } = useStudio();
  const audit = useLoad(() => get<any[]>('/api/audit'), [tenant]);
  return (
    <Section title="Audit log" count={audit.data?.length}>
      <p className="muted small">Newest first. A publish names its maker and a different checker.</p>
      <ErrorBox error={audit.error} />
      {audit.loading && !audit.data ? <Loading /> : !audit.data?.length ? <p className="muted">Nothing recorded yet in this data root.</p> : (
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>When</th><th>Tenant</th><th>Who</th><th>Action</th><th>Artefact</th><th>Detail</th></tr></thead>
            <tbody>
              {audit.data.map((a, i) => (
                <tr key={i}>
                  <td className="mono small nowrap">{a.at ?? a.when ?? ''}</td>
                  <td>{a.tenant}</td>
                  <td>{a.actor}</td>
                  <td><Badge tone={a.action === 'publish' ? 'pass' : a.action === 'delete-draft' ? 'warn' : 'info'}>{a.action}</Badge></td>
                  <td className="mono small break">{a.key}</td>
                  <td className="small">{a.detail ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  );
}

function Supersessions() {
  const sups = useLoad(() => get<any[]>('/api/supersessions'), []);
  const [results, setResults] = useState<Record<string, any>>({});
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const test = async (s: any) => {
    setBusy(s.id);
    setError(undefined);
    try {
      const workflows = (s.scope?.workflowReleases ?? []).map((w: string) => w.split('@')[0]);
      const r = await post('/api/supersessions/test', { artefact: s.artefact, replaces: s.replaces, with: s.with, workflows });
      setResults((m) => ({ ...m, [s.id]: r }));
    } catch (e: any) {
      setError(`${s.id}: ${e.message}`);
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <>
      <ErrorBox error={sups.error || error} />
      {sups.loading && !sups.data ? <Loading /> : !sups.data?.length ? <p className="muted">No supersessions.</p> : sups.data.map((s) => {
        const r = results[s.id];
        return (
          <Section key={s.id} title={<span className="mono">{s.id}</span>} actions={<button className="primary" onClick={() => test(s)} disabled={busy === s.id}>{busy === s.id ? 'Testing…' : 'Test'}</button>}>
            <KV rows={[
              ['Artefact', <ArtefactLink name={s.artefact} />],
              ['Replaces', <><code>{s.replaces}</code> with <code>{s.with}</code></>],
              ['Reason', s.reason],
              ['Scope', <Val v={s.scope} />],
              ['Evidence', <Val v={s.evidence} />],
              ['Approval', s.approval ? <>maker <b>{s.approval.maker}</b>, checker <b>{s.approval.checker}</b></> : <span className="muted">none</span>],
            ]} />
            <h4>Issues</h4>
            <Issues issues={s.issues as Issue[]} empty="No issues." />
            {r && (
              <>
                <h4>Test result {r.green ? <Badge tone="pass">green</Badge> : <Badge tone="fail">not green</Badge>}</h4>
                {!r.results.length ? <p className="muted small">No test targets the workflows in scope, so nothing reran.</p> : (
                  <table className="grid compact">
                    <thead><tr><th>Test</th><th>Target</th><th>Outcome</th><th>Result</th></tr></thead>
                    <tbody>{r.results.map((x: any) => <tr key={x.id}><td className="mono"><a href={`#/tests?run=${encodeURIComponent(x.id)}`}>{x.id}</a></td><td className="mono small">{x.target}</td><td><code>{x.outcome}</code></td><td><PassFail passed={x.passed} /></td></tr>)}</tbody>
                  </table>
                )}
              </>
            )}
          </Section>
        );
      })}
    </>
  );
}

function StoreChecks() {
  const { meta } = useStudio();
  const checks = useLoad(() => get('/api/checks'), []);
  const [code, setCode] = useState('');
  const [sev, setSev] = useState('');
  const issues: Issue[] = checks.data?.issues ?? [];
  const codes = [...new Set(issues.map((i) => i.code))].sort();
  const rows = issues.filter((i) => (!code || i.code === code) && (!sev || i.severity === sev));
  const errors = issues.filter((i) => i.severity === 'error').length;
  return (
    <Section title="Every static check across the store" count={issues.length} actions={<>{errors ? <Badge tone="fail">{errors} errors</Badge> : <Badge tone="pass">no errors</Badge>} <Badge tone="warn">{issues.length - errors} warnings</Badge></>}>
      <ErrorBox error={checks.error} />
      <div className="toolbar">
        <select value={code} onChange={(e) => setCode(e.target.value)} aria-label="Check">
          <option value="">Every check</option>
          {codes.map((c) => <option key={c} value={c}>{c} {meta?.checks?.[c] ?? ''}</option>)}
        </select>
        <select value={sev} onChange={(e) => setSev(e.target.value)} aria-label="Severity">
          <option value="">Any severity</option>
          <option value="error">error</option>
          <option value="warning">warning</option>
        </select>
      </div>
      {checks.loading && !checks.data ? <Loading /> : (
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>Check</th><th>Severity</th><th>Artefact</th><th>Step</th><th>Message</th></tr></thead>
            <tbody>
              {rows.map((i, n) => (
                <tr key={n}>
                  <td title={meta?.checks?.[i.code]}><Badge tone={i.severity === 'error' ? 'fail' : 'warn'}>{i.code}</Badge></td>
                  <td>{i.severity}</td>
                  <td className="small">{i.key && /\.v\d+$|^scheme:/.test(i.key) ? <ArtefactLink name={i.key} /> : <span className="mono">{i.key}</span>}</td>
                  <td className="mono">{i.step ?? ''}</td>
                  <td>{i.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <details>
        <summary>The check catalogue</summary>
        <table className="grid compact">
          <tbody>{Object.entries(checks.data?.catalogue ?? {}).map(([k, v]) => <tr key={k}><td className="mono">{k}</td><td>{String(v)}</td></tr>)}</tbody>
        </table>
      </details>
    </Section>
  );
}
