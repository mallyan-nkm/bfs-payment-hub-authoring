import { useMemo, useState } from 'react';
import { get, post, type KitResult } from '../api';
import { Badge, ErrorBox, Loading, PassFail, Section, Val, useLoad } from '../ui';

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
        <button className="primary" onClick={() => run()} disabled={busy}>{busy ? 'Running…' : `Run all ${scenarios.length}`}</button>
        <select value={construct} onChange={(e) => setConstruct(e.target.value)} aria-label="Construct">
          <option value="">Every construct</option>
          {constructs.map((c) => <option key={c}>{c}</option>)}
        </select>
        <label className="check"><input type="checkbox" checked={onlyFailed} onChange={(e) => setOnlyFailed(e.target.checked)} /> Failed only</label>
        {results && <Badge tone={passed === results.length ? 'pass' : 'fail'}>{passed} of {results.length} pass</Badge>}
      </div>
      {kit.loading ? <Loading what="the kit" /> : (
        <Section title="Scenarios" count={scenarios.length}>
          <div className="table-wrap">
            <table className="grid">
              <thead><tr><th>Scenario</th><th>Construct</th><th>Title</th><th>Result</th><th>Virtual time</th><th></th></tr></thead>
              <tbody>
                {rows.map((s) => {
                  const r = byId.get(s.id);
                  const isOpen = open === s.id;
                  return [
                    <tr key={s.id} className={r && !r.passed ? 'row-fail clickable' : 'clickable'} onClick={() => setOpen(isOpen ? undefined : s.id)}>
                      <td className="mono nowrap">{s.id}</td>
                      <td>{s.construct}</td>
                      <td>{s.title}</td>
                      <td>{r ? <PassFail passed={r.passed} /> : <span className="muted small">not run</span>}</td>
                      <td className="mono small">{r?.elapsed !== undefined ? `${r.elapsed} ms` : ''}</td>
                      <td><button onClick={(e) => { e.stopPropagation(); void run([s.id]); setOpen(s.id); }} disabled={busy}>Run</button></td>
                    </tr>,
                    isOpen && (
                      <tr key={`${s.id}-d`} className="sub">
                        <td colSpan={6}><KitDetail scenario={s} result={r} /></td>
                      </tr>
                    ),
                  ];
                })}
              </tbody>
            </table>
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
      {result?.error && <div className="alert fail small">{result.error}</div>}
      <table className="grid compact trace-compare">
        <thead><tr><th>#</th><th>Expected</th><th>Actual</th><th></th></tr></thead>
        <tbody>
          {Array.from({ length: n }, (_, i) => {
            const same = expected[i] === actual[i];
            return (
              <tr key={i} className={result && !same ? 'row-fail' : ''}>
                <td className="muted">{i + 1}</td>
                <td className="mono">{expected[i] ?? ''}</td>
                <td className="mono">{result ? actual[i] ?? <span className="muted">missing</span> : <span className="muted">not run</span>}</td>
                <td>{result && (same ? <Badge tone="pass">same</Badge> : <Badge tone="fail">differs</Badge>)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {result?.checks.find((c) => c.what === 'detail') && <DetailCompare check={result.checks.find((c) => c.what === 'detail')!} />}
      {result && result.checks.length > 0 && (
        <>
          <h4>Checks</h4>
          <table className="grid compact">
            <thead><tr><th>Check</th><th>Expected</th><th>Actual</th><th>Result</th></tr></thead>
            <tbody>{result.checks.map((c, i) => <tr key={i}><td>{c.what}</td><td><Val v={c.expected} /></td><td><Val v={c.actual} /></td><td><PassFail passed={c.passed} /></td></tr>)}</tbody>
          </table>
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
      <table className="grid compact trace-compare">
        <thead><tr><th>#</th><th>Expected</th><th>Actual</th><th></th></tr></thead>
        <tbody>
          {Array.from({ length: n }, (_, i) => (
            <tr key={i} className={e[i] !== a[i] ? 'row-fail' : ''}>
              <td className="muted">{i + 1}</td>
              <td className="mono">{e[i] ?? ''}</td>
              <td className="mono">{a[i] ?? <span className="muted">missing</span>}</td>
              <td>{e[i] === a[i] ? <Badge tone="pass">same</Badge> : <Badge tone="fail">differs</Badge>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
