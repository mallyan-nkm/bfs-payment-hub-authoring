import { useEffect, useMemo, useState } from 'react';
import { downloadUrl, get, post, refusal, type User } from '../api';
import { Badge, ErrorBox, PassFail, Section, useStudio } from '../ui';

// Import (I-33): an extraction bundle that an AI agent wrote from a scheme's source code
// (docs/import/EXTRACTION-PROMPT.md). Preview it: what it holds, how much of the source it accounts
// for, every check, and its tests run against itself. Then import it as drafts. Nothing is published
// here: a checker approves each change set in Governance.

type Issue = { severity: string; code: string; where: string; message: string };

function toBase64(buf: ArrayBuffer): string {
  let s = '';
  const bytes = new Uint8Array(buf);
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

export function Import() {
  const { tenant, actor, meta, go } = useStudio();
  const [bundle, setBundle] = useState<string>();
  const [label, setLabel] = useState<string>();
  const [bankTenant, setBankTenant] = useState('');
  const [bankActor, setBankActor] = useState('');
  const [bankUsers, setBankUsers] = useState<User[]>([]);
  const [report, setReport] = useState<any>();
  const [result, setResult] = useState<any>();
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const [show, setShow] = useState<'errors' | 'warnings' | 'all'>('errors');
  const banks = (meta?.tenants ?? []).filter((t) => t !== meta?.owner);

  useEffect(() => {
    if (!bankTenant) { setBankUsers([]); setBankActor(''); return; }
    get<User[]>('/api/users', bankTenant).then((u) => {
      setBankUsers(u);
      setBankActor(u.find((x) => x.roles.includes('bank-designer') || x.roles.includes('designer'))?.id ?? u[0]?.id ?? '');
    }, () => setBankUsers([]));
  }, [bankTenant]);

  const choose = async (f?: File) => {
    if (!f) return;
    setReport(undefined); setResult(undefined); setError(undefined);
    setBundle(toBase64(await f.arrayBuffer()));
    setLabel(f.name);
  };
  const training = async () => {
    setReport(undefined); setResult(undefined); setError(undefined);
    const r = await fetch(downloadUrl('/api/import/sample'));
    setBundle(toBase64(await r.arrayBuffer()));
    setLabel('The training bundle: WPS, extracted as uae.wpsx');
    if (banks.includes('example-bank')) setBankTenant('example-bank');
  };
  const preview = async () => {
    if (!bundle) return;
    setBusy('preview'); setError(undefined); setResult(undefined);
    try {
      setReport(await post('/api/import/preview', { bundle, bankTenant: bankTenant || undefined }, tenant));
    } catch (e: any) {
      setError(refusal(e));
    } finally {
      setBusy(undefined);
    }
  };
  const apply = async () => {
    if (!bundle) return;
    setBusy('apply'); setError(undefined);
    try {
      const r = await post('/api/import/apply', { bundle, actor, bankTenant: bankTenant || undefined, bankActor: bankActor || undefined }, tenant);
      setResult(r);
      setReport(r.report);
    } catch (e: any) {
      setError(refusal(e));
    } finally {
      setBusy(undefined);
    }
  };

  const issues: Issue[] = useMemo(() => !report ? [] : [...report.issues, ...report.items.flatMap((i: any) => i.issues), ...report.scripts.flatMap((s: any) => s.issues), ...report.samples.flatMap((s: any) => s.issues)], [report]);
  const shown = issues.filter((i) => show === 'all' || (show === 'errors' ? i.severity === 'error' : i.severity === 'warning'));
  const errors = issues.filter((i) => i.severity === 'error').length;
  const warnings = issues.filter((i) => i.severity === 'warning').length;

  return (
    <div className="view">
      <div className="view-head">
        <h2>Import</h2>
        <p className="muted">Bring a scheme in from its source code. An AI agent reads the source with the extraction prompt and writes a bundle: the studio's documents, scripts, samples, tests, a bank's configuration, an inventory of every file and unit of the source, and its findings. Preview the bundle here, then import it as drafts. Nothing is published until a checker approves it.</p>
      </div>
      <div className="cols">
        <div className="col">
          <Section title="1. The bundle">
            <div className="row-actions">
              <label className="button">Choose a bundle zip<input type="file" accept=".zip,application/zip" aria-label="Extraction bundle" style={{ display: 'none' }} onChange={(e) => choose(e.target.files?.[0])} /></label>
              <button onClick={training}>Use the training bundle</button>
            </div>
            {label && <p className="small">Chosen: <b>{label}</b></p>}
            <label className="field">
              <span>Bank tenant for bank content (scope b, configuration)</span>
              <select value={bankTenant} onChange={(e) => setBankTenant(e.target.value)} aria-label="Bank tenant">
                <option value="">None</option>
                {banks.map((b) => <option key={b}>{b}</option>)}
              </select>
            </label>
            {bankTenant && (
              <label className="field">
                <span>Saved for the bank as</span>
                <select value={bankActor} onChange={(e) => setBankActor(e.target.value)} aria-label="Bank actor">
                  {bankUsers.map((u) => <option key={u.id} value={u.id}>{u.id} ({u.roles.join(', ')})</option>)}
                </select>
              </label>
            )}
            <div className="row-actions">
              <button className="primary" disabled={!bundle || !!busy} onClick={preview}>{busy === 'preview' ? 'Checking…' : 'Preview'}</button>
              <button disabled={!report || !!busy || (!report.ok)} onClick={apply} title={report && !report.ok ? 'Fix the errors first' : undefined}>{busy === 'apply' ? 'Importing…' : `Import as drafts, as ${actor}`}</button>
            </div>
            <ErrorBox error={error} />
          </Section>
        </div>
        <div className="col side">
          <Section title="The extraction prompt">
            <p className="small">Give this prompt to an AI coding agent with the source code of a scheme written as workflows, such as WPS. It says what to look for in every file, where each thing goes in the studio, and how to check the bundle before handing it back.</p>
            <div className="row-actions">
              <a className="button" href={downloadUrl('/api/import/prompt')}>Download the extraction prompt</a>
              <a className="button" href={downloadUrl('/api/import/sample')}>Download the training bundle</a>
            </div>
          </Section>
        </div>
      </div>

      {result?.applied && (
        <div className="alert pass" role="status">
          Imported {result.saved.length} as drafts{result.skipped.length ? `; ${result.skipped.length} unchanged and reused` : ''}. Nothing is published: {result.changeSets.length} change set{result.changeSets.length === 1 ? '' : 's'} wait for a checker. <button className="link" onClick={() => go('/governance')}>Open Governance</button>
        </div>
      )}
      {result && !result.applied && <div className="alert fail" role="status">Not imported: the bundle has errors.</div>}

      {report && (
        <>
          <Section title="2. The preview" actions={report.ok ? <Badge tone="pass">may be imported</Badge> : <Badge tone="fail">{errors} error{errors === 1 ? '' : 's'}</Badge>}>
            <p><b>{report.manifest.scope}</b> from {report.manifest.source?.system ?? '?'} <code>{report.manifest.source?.repository}@{report.manifest.source?.revision}</code>
              {report.manifest.validatedBy?.expert ? <> · validated by {report.manifest.validatedBy.expert}</> : <> · <Badge tone="warn">not yet validated by an expert</Badge></>}</p>
            <div className="chips" data-testid="import-summary">
              {Object.entries<number>(report.summary).filter(([, v]) => v).map(([k, v]) => <span key={k} className="chip"><b>{v}</b> {k}</span>)}
            </div>
          </Section>

          <div className="cols">
            <div className="col">
              <Section title="Coverage of the source">
                <table className="grid compact" data-testid="import-coverage">
                  <tbody>
                    <tr><th>Source files</th><td>{report.inventory.sourceFiles.total}</td></tr>
                    <tr><th>Inventoried</th><td>{report.inventory.sourceFiles.inventoried}</td></tr>
                    <tr><th>Excluded, with a reason</th><td>{report.inventory.sourceFiles.excluded}</td></tr>
                    <tr className={report.inventory.sourceFiles.missing.length ? 'row-fail' : ''}><th>Neither</th><td>{report.inventory.sourceFiles.missing.length}</td></tr>
                    <tr><th>Items of the source</th><td>{report.inventory.total}: {Object.entries(report.inventory.byTarget).map(([k, v]) => `${v} ${k}`).join(', ')}</td></tr>
                    <tr><th>By what they are</th><td className="small">{Object.entries(report.inventory.bySourceKind).map(([k, v]) => `${v} ${k}`).join(', ')}</td></tr>
                    {Object.keys(report.inventory.reuse).length > 0 && <tr><th>Platform reuse</th><td>{Object.entries(report.inventory.reuse).map(([k, v]) => `${v} ${k}`).join(', ')}</td></tr>}
                  </tbody>
                </table>
                {report.inventory.sourceFiles.missing.length > 0 && <ul className="small fail-text">{report.inventory.sourceFiles.missing.map((f: string) => <li key={f}><code>{f}</code></li>)}</ul>}
              </Section>
              {[['Unmapped, for the expert', report.inventory.unmapped], ['Retired', report.inventory.retired], ['Infrastructure', report.inventory.infrastructure]].map(([title, list]: any) => list.length > 0 && (
                <Section key={title} title={title} count={list.length}>
                  <ul className="small">{list.map((u: any) => <li key={u.id}><code>{u.id}</code> <code>{u.source?.path}{u.source?.symbol ? ` ${u.source.symbol}` : ''}</code>: {u.note ?? u.summary}</li>)}</ul>
                </Section>
              ))}
              <Section title="Findings" count={report.findings.length}>
                <ul className="small">{report.findings.map((f: any, i: number) => <li key={i}><Badge tone={f.kind === 'difference' || f.kind === 'risk' ? 'warn' : 'info'}>{f.kind}</Badge> {f.text} <span className="muted">({f.source})</span></li>)}</ul>
              </Section>
            </div>
            <div className="col">
              <Section title="Tests, run against the bundle itself" count={report.tests.length}>
                <table className="grid compact" data-testid="import-tests">
                  <thead><tr><th>Test</th><th>Target</th><th>Ends</th><th>Result</th></tr></thead>
                  <tbody>{report.tests.map((t: any) => <tr key={t.id}><td className="mono small">{t.id}</td><td className="mono small">{t.target}</td><td className="mono small">{t.outcome}</td><td><PassFail passed={!!t.passed} />{!t.passed && <div className="small fail-text">{t.error ?? t.failed?.join('; ')}</div>}</td></tr>)}</tbody>
                </table>
              </Section>
              <Section title="Checks" actions={<span className="row-actions">
                {(['errors', 'warnings', 'all'] as const).map((k) => <button key={k} className={show === k ? 'tab on' : 'tab'} onClick={() => setShow(k)}>{k === 'errors' ? `Errors (${errors})` : k === 'warnings' ? `Warnings (${warnings})` : 'All'}</button>)}
              </span>}>
                {shown.length ? (
                  <table className="grid compact" data-testid="import-issues">
                    <tbody>{shown.slice(0, 300).map((i, n) => <tr key={n} className={i.severity === 'error' ? 'row-fail' : ''}><td><Badge tone={i.severity === 'error' ? 'fail' : 'warn'}>{i.code}</Badge></td><td className="mono small break">{i.where}</td><td className="small">{i.message}</td></tr>)}</tbody>
                  </table>
                ) : <p className="small muted">None.</p>}
              </Section>
            </div>
          </div>

          <Section title="Everything in the bundle" count={report.items.length}>
            <div className="table-wrap tall">
              <table className="grid compact" data-testid="import-items">
                <thead><tr><th>Kind</th><th>Name</th><th>Goes to</th><th>Status</th><th>From the source</th><th>Checks</th></tr></thead>
                <tbody>
                  {report.items.map((i: any) => (
                    <tr key={i.key}>
                      <td className="small">{i.kind}</td>
                      <td className="mono small break">{i.name}{i.release ? `@${i.release}` : ''}</td>
                      <td className="small">{i.tenant}</td>
                      <td><Badge tone={i.status === 'new' ? 'info' : i.status === 'unchanged' ? 'muted' : 'warn'}>{i.status}</Badge>{i.existing && <div className="small muted">published {i.existing}{i.proposedRelease ? `; propose ${i.proposedRelease}` : ''}</div>}</td>
                      <td className="small mono break">{i.sources.slice(0, 2).join(', ')}{i.sources.length > 2 ? ` +${i.sources.length - 2}` : ''}</td>
                      <td>{i.issues.some((x: any) => x.severity === 'error') ? <Badge tone="fail">{i.issues.filter((x: any) => x.severity === 'error').length} error(s)</Badge> : i.issues.length ? <Badge tone="warn">{i.issues.length}</Badge> : <Badge tone="pass">ok</Badge>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Section>
        </>
      )}
    </div>
  );
}
