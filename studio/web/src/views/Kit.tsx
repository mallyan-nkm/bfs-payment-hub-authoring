import { useMemo, useState } from 'react';
import { get, post, type KitResult } from '../api';
import { Alert, Badge, ErrorBox, Loading, PassFail, Section, Val, useLoad } from '../ui';
import { Button, Checkbox, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';

// The conformance kit (Annex C): every scenario, run on the studio interpreter, expected against actual.

export function Kit() {
  const kit = useLoad(() => get('/api/kit'), []);
  const [results, setResults] = useState<KitResult[]>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState<string>();
  const [construct, setConstruct] = useState('');
  const [onlyFailed, setOnlyFailed] = useState(false);

  const run = async (only: string[] = []) => {
    setBusy(true);
    setError(undefined);
    try {
      const r = await post<KitResult[]>('/api/kit/run', { only });
      setResults((prev) => (only.length && prev ? prev.map((p) => r.find((x) => x.id === p.id) ?? p).concat(r.filter((x) => !prev.some((p) => p.id === x.id))) : r));
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };

  const scenarios: any[] = kit.data?.scenarios ?? [];
  const constructs = useMemo(() => [...new Set(scenarios.map((s) => s.construct))], [scenarios]);
  const byId = new Map((results ?? []).map((r) => [r.id, r]));
  const passed = results?.filter((r) => r.passed).length ?? 0;
  const rows = scenarios.filter((s) => (!construct || s.construct === construct) && (!onlyFailed || byId.get(s.id)?.passed === false));

  return (
    <div className="view">
      <div className="view-head">
        <h2>Conformance kit</h2>
        <p className="muted">Every scenario of the kit{kit.data?.release ? ` release ${kit.data.release}` : ''}, run on the studio interpreter. A run passes when the business trace equals the expected trace and every extra check holds.</p>
      </div>
      <ErrorBox error={kit.error || error} />
      <div className="toolbar">
        <Button appearance="primary" onClick={() => run()} disabled={busy}>{busy ? 'Running…' : `Run all ${scenarios.length}`}</Button>
        <Select value={construct} onChange={(e) => setConstruct(e.target.value)} aria-label="Construct">
          <option value="">Every construct</option>
          {constructs.map((c) => <option key={c}>{c}</option>)}
        </Select>
        <Checkbox className="check" checked={onlyFailed} onChange={(e) => setOnlyFailed(e.target.checked)} label={<>Failed only</>} />
        {results && <Badge tone={passed === results.length ? 'pass' : 'fail'}>{passed} of {results.length} pass</Badge>}
      </div>
      {kit.loading ? <Loading what="the kit" /> : (
        <Section title="Scenarios" count={scenarios.length}>
          <div className="table-wrap">
            <Table size="small" className="grid">
              <TableHeader><TableRow><TableHeaderCell>Scenario</TableHeaderCell><TableHeaderCell>Construct</TableHeaderCell><TableHeaderCell>Title</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell><TableHeaderCell>Virtual time</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
              <TableBody>
                {rows.map((s) => {
                  const r = byId.get(s.id);
                  const isOpen = open === s.id;
                  return [
                    <TableRow key={s.id} className={r && !r.passed ? 'row-fail clickable' : 'clickable'} onClick={() => setOpen(isOpen ? undefined : s.id)}>
                      <TableCell className="mono nowrap">{s.id}</TableCell>
                      <TableCell>{s.construct}</TableCell>
                      <TableCell>{s.title}</TableCell>
                      <TableCell>{r ? <PassFail passed={r.passed} /> : <span className="muted small">not run</span>}</TableCell>
                      <TableCell className="mono small">{r?.elapsed !== undefined ? `${r.elapsed} ms` : ''}</TableCell>
                      <TableCell><Button onClick={(e) => { e.stopPropagation(); void run([s.id]); setOpen(s.id); }} disabled={busy}>Run</Button></TableCell>
                    </TableRow>,
                    isOpen && (
                      <TableRow key={`${s.id}-d`} className="sub">
                        <TableCell colSpan={6}><KitDetail scenario={s} result={r} /></TableCell>
                      </TableRow>
                    ),
                  ];
                })}
              </TableBody>
            </Table>
          </div>
        </Section>
      )}
    </div>
  );
}

function KitDetail({ scenario, result }: { scenario: any; result?: KitResult }) {
  const expected: string[] = result?.expected ?? scenario.expect ?? [];
  const actual: string[] = result?.actual ?? [];
  const n = Math.max(expected.length, actual.length);
  return (
    <div className="kit-detail">
      {result?.error && <Alert tone="fail" className="small">{result.error}</Alert>}
      <Table size="small" className="grid compact trace-compare">
        <TableHeader><TableRow><TableHeaderCell>#</TableHeaderCell><TableHeaderCell>Expected</TableHeaderCell><TableHeaderCell>Actual</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {Array.from({ length: n }, (_, i) => {
            const same = expected[i] === actual[i];
            return (
              <TableRow key={i} className={result && !same ? 'row-fail' : ''}>
                <TableCell className="muted">{i + 1}</TableCell>
                <TableCell className="mono">{expected[i] ?? ''}</TableCell>
                <TableCell className="mono">{result ? actual[i] ?? <span className="muted">missing</span> : <span className="muted">not run</span>}</TableCell>
                <TableCell>{result && (same ? <Badge tone="pass">same</Badge> : <Badge tone="fail">differs</Badge>)}</TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
      {result?.checks.find((c) => c.what === 'detail') && <DetailCompare check={result.checks.find((c) => c.what === 'detail')!} />}
      {result && result.checks.length > 0 && (
        <>
          <h4>Checks</h4>
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>Check</TableHeaderCell><TableHeaderCell>Expected</TableHeaderCell><TableHeaderCell>Actual</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{result.checks.map((c, i) => <TableRow key={i}><TableCell>{c.what}</TableCell><TableCell><Val v={c.expected} /></TableCell><TableCell><Val v={c.actual} /></TableCell><TableCell><PassFail passed={c.passed} /></TableCell></TableRow>)}</TableBody>
          </Table>
        </>
      )}
      {result?.notes && result.notes.length > 0 && <ul className="small">{result.notes.map((x, i) => <li key={i}>{x}</li>)}</ul>}
    </div>
  );
}

/** The detail, the steps inside composite steps, compared line by line (R-05-38). */
function DetailCompare({ check }: { check: { expected: any; actual: any; passed: boolean } }) {
  const e: string[] = check.expected ?? [];
  const a: string[] = check.actual ?? [];
  const n = Math.max(e.length, a.length);
  return (
    <>
      <h4>Detail, inside composite steps</h4>
      <Table size="small" className="grid compact trace-compare">
        <TableHeader><TableRow><TableHeaderCell>#</TableHeaderCell><TableHeaderCell>Expected</TableHeaderCell><TableHeaderCell>Actual</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {Array.from({ length: n }, (_, i) => (
            <TableRow key={i} className={e[i] !== a[i] ? 'row-fail' : ''}>
              <TableCell className="muted">{i + 1}</TableCell>
              <TableCell className="mono">{e[i] ?? ''}</TableCell>
              <TableCell className="mono">{a[i] ?? <span className="muted">missing</span>}</TableCell>
              <TableCell>{e[i] === a[i] ? <Badge tone="pass">same</Badge> : <Badge tone="fail">differs</Badge>}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </>
  );
}
