import { useEffect, useMemo, useRef, useState } from 'react';
import { downloadUrl, post, refusal } from '../api';
import { Alert, Badge, ErrorBox, PassFail, Section, useStudio } from '../ui';
import { Button, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, ToggleButton } from '@fluentui/react-components';
import { ArrowDownloadRegular, FolderZipRegular } from '@fluentui/react-icons';

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
  const fileRef = useRef<HTMLInputElement>(null);
  const { tenant, actor, meta, go } = useStudio();
  const [bundle, setBundle] = useState<string>();
  const [label, setLabel] = useState<string>();
  const [report, setReport] = useState<any>();
  const [result, setResult] = useState<any>();
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const [show, setShow] = useState<'errors' | 'warnings' | 'all'>('errors');
  // The part of a bundle the signed-in user imports (R-11-30): the shared part for the owner's users,
  // the bank part, into its own tenant, for a bank configurator.
  const bankPart = tenant !== (meta?.owner ?? 'owner');

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
  };
  const preview = async () => {
    if (!bundle) return;
    setBusy('preview'); setError(undefined); setResult(undefined);
    try {
      setReport(await post('/api/import/preview', { bundle }, tenant));
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
      const r = await post('/api/import/apply', { bundle }, tenant);
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
        <p className="muted">Bring a scheme in from its source code. An AI agent reads the source with the extraction prompt and writes a bundle: the studio's documents, scripts, samples, tests, a bank's configuration, an inventory of every file and unit of the source, and its findings. Preview the bundle here, then import it as drafts. Nothing is a version until an approver who is not the maker approves it.</p>
      </div>
      <div className="cols">
        <div className="col">
          <Section title="1. The bundle">
            <div className="row-actions">
              <Button icon={<FolderZipRegular />} onClick={() => fileRef.current?.click()}>Choose a bundle zip</Button>
              <input ref={fileRef} type="file" accept=".zip,application/zip" aria-label="Extraction bundle" style={{ display: 'none' }} onChange={(e) => choose(e.target.files?.[0])} />
              <Button onClick={training}>Use the training bundle</Button>
            </div>
            {label && <p className="small">Chosen: <b>{label}</b></p>}
            <p className="muted small">{bankPart ? `You import the bank part of the bundle (scope b, configuration sets) into ${tenant}. The shared part is imported by the platform.` : 'You import the shared part of the bundle. A bank part (scope b, configuration sets) is left for that bank: its configurator imports it once the shared part is a version.'}</p>
            <div className="row-actions">
              <Button appearance="primary" disabled={!bundle || !!busy} onClick={preview}>{busy === 'preview' ? 'Checking…' : 'Preview'}</Button>
              <Button disabled={!report || !!busy || (!report.ok)} onClick={apply} title={report && !report.ok ? 'Fix the errors first' : undefined}>{busy === 'apply' ? 'Importing…' : `Import as drafts, as ${actor}`}</Button>
            </div>
            <ErrorBox error={error} />
          </Section>
        </div>
        <div className="col side">
          <Section title="The extraction prompt">
            <p className="small">Give this prompt to an AI coding agent with the source code of a scheme written as workflows, such as WPS. It says what to look for in every file, where each thing goes in the studio, and how to check the bundle before handing it back.</p>
            <div className="row-actions">
              <Button as="a" icon={<ArrowDownloadRegular />} href={downloadUrl('/api/import/prompt')}>Download the extraction prompt</Button>
              <Button as="a" icon={<ArrowDownloadRegular />} href={downloadUrl('/api/import/sample')}>Download the training bundle</Button>
            </div>
          </Section>
        </div>
      </div>

      {result?.applied && (
        <Alert tone="pass" role="status">
          Imported {result.saved.length} as drafts{result.skipped.length ? `; ${result.skipped.length} unchanged, reused or left for their own author` : ''}. Nothing is published: {result.changeSets.length} change set{result.changeSets.length === 1 ? '' : 's'} wait for an approver. <Link as="button" onClick={() => go('/governance')}>Open Governance</Link>
        </Alert>
      )}
      {result && !result.applied && <Alert tone="fail" role="status">Not imported: the bundle has errors.</Alert>}

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
                <Table size="small" className="grid compact" data-testid="import-coverage">
                  <TableBody>
                    <TableRow><TableHeaderCell>Source files</TableHeaderCell><TableCell>{report.inventory.sourceFiles.total}</TableCell></TableRow>
                    <TableRow><TableHeaderCell>Inventoried</TableHeaderCell><TableCell>{report.inventory.sourceFiles.inventoried}</TableCell></TableRow>
                    <TableRow><TableHeaderCell>Excluded, with a reason</TableHeaderCell><TableCell>{report.inventory.sourceFiles.excluded}</TableCell></TableRow>
                    <TableRow className={report.inventory.sourceFiles.missing.length ? 'row-fail' : ''}><TableHeaderCell>Neither</TableHeaderCell><TableCell>{report.inventory.sourceFiles.missing.length}</TableCell></TableRow>
                    <TableRow><TableHeaderCell>Items of the source</TableHeaderCell><TableCell>{report.inventory.total}: {Object.entries(report.inventory.byTarget).map(([k, v]) => `${v} ${k}`).join(', ')}</TableCell></TableRow>
                    <TableRow><TableHeaderCell>By what they are</TableHeaderCell><TableCell className="small">{Object.entries(report.inventory.bySourceKind).map(([k, v]) => `${v} ${k}`).join(', ')}</TableCell></TableRow>
                    {Object.keys(report.inventory.reuse).length > 0 && <TableRow><TableHeaderCell>Platform reuse</TableHeaderCell><TableCell>{Object.entries(report.inventory.reuse).map(([k, v]) => `${v} ${k}`).join(', ')}</TableCell></TableRow>}
                  </TableBody>
                </Table>
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
                <Table size="small" className="grid compact" data-testid="import-tests">
                  <TableHeader><TableRow><TableHeaderCell>Test</TableHeaderCell><TableHeaderCell>Target</TableHeaderCell><TableHeaderCell>Ends</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell></TableRow></TableHeader>
                  <TableBody>{report.tests.map((t: any) => <TableRow key={t.id}><TableCell className="mono small">{t.id}</TableCell><TableCell className="mono small">{t.target}</TableCell><TableCell className="mono small">{t.outcome}</TableCell><TableCell><PassFail passed={!!t.passed} />{!t.passed && <div className="small fail-text">{t.error ?? t.failed?.join('; ')}</div>}</TableCell></TableRow>)}</TableBody>
                </Table>
              </Section>
              <Section title="Checks" actions={<span className="row-actions">
                {(['errors', 'warnings', 'all'] as const).map((k) => <ToggleButton key={k} size="small" checked={show === k} onClick={() => setShow(k)}>{k === 'errors' ? `Errors (${errors})` : k === 'warnings' ? `Warnings (${warnings})` : 'All'}</ToggleButton>)}
              </span>}>
                {shown.length ? (
                  <Table size="small" className="grid compact" data-testid="import-issues">
                    <TableBody>{shown.slice(0, 300).map((i, n) => <TableRow key={n} className={i.severity === 'error' ? 'row-fail' : ''}><TableCell><Badge tone={i.severity === 'error' ? 'fail' : 'warn'}>{i.code}</Badge></TableCell><TableCell className="mono small break">{i.where}</TableCell><TableCell className="small">{i.message}</TableCell></TableRow>)}</TableBody>
                  </Table>
                ) : <p className="small muted">None.</p>}
              </Section>
            </div>
          </div>

          <Section title="Everything in the bundle" count={report.items.length}>
            <div className="table-wrap tall">
              <Table size="small" className="grid compact" data-testid="import-items">
                <TableHeader><TableRow><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Goes to</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>From the source</TableHeaderCell><TableHeaderCell>Checks</TableHeaderCell></TableRow></TableHeader>
                <TableBody>
                  {report.items.map((i: any) => (
                    <TableRow key={i.key}>
                      <TableCell className="small">{i.kind}</TableCell>
                      <TableCell className="mono small break">{i.name}{i.release ? `@${i.release}` : ''}</TableCell>
                      <TableCell className="small">{i.tenant}</TableCell>
                      <TableCell><Badge tone={i.status === 'new' ? 'info' : i.status === 'unchanged' ? 'muted' : 'warn'}>{i.status}</Badge>{i.existing && <div className="small muted">published {i.existing}{i.proposedRelease ? `; propose ${i.proposedRelease}` : ''}</div>}</TableCell>
                      <TableCell className="small mono break">{i.sources.slice(0, 2).join(', ')}{i.sources.length > 2 ? ` +${i.sources.length - 2}` : ''}</TableCell>
                      <TableCell>{i.issues.some((x: any) => x.severity === 'error') ? <Badge tone="fail">{i.issues.filter((x: any) => x.severity === 'error').length} error(s)</Badge> : i.issues.length ? <Badge tone="warn">{i.issues.length}</Badge> : <Badge tone="pass">ok</Badge>}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </Section>
        </>
      )}
    </div>
  );
}
