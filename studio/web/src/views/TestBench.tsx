import { useEffect, useMemo, useRef, useState } from 'react';
import { get, post, type Summary, type TestRunReport } from '../api';
import { ErrorBox, Loading, PassFail, Section, useLoad, useStudio } from '../ui';
import { RunReport } from './RunReport';

// The test bench (F-16 to F-19): stored tests with run and run-all, and an ad hoc bench.

interface TestRow { id: string; title: string; target: string; level: string; tenant?: string; scope: string; bench: any[]; lastResult?: 'pass' | 'fail' }
interface BenchRow { port: string; simulator: string; scenario: string }
interface SignalRow { signal: string; at: string }

const SIGNALS = ['pause', 'resume', 'cancel', 're-screen'];

export function TestBench({ query }: { query: URLSearchParams }) {
  const { tenant } = useStudio();
  const tests = useLoad(() => get<TestRow[]>('/api/tests'), [tenant]);
  const [report, setReport] = useState<TestRunReport>();
  const [running, setRunning] = useState<string>();
  const [error, setError] = useState<string>();
  const [all, setAll] = useState<any[]>();
  const [filter, setFilter] = useState('');
  const reportRef = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<'tests' | 'bench'>(query.get('bench') ? 'bench' : 'tests');

  const show = (r: TestRunReport) => {
    setReport(r);
    setTimeout(() => reportRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  };

  const run = async (id: string) => {
    setRunning(id);
    setError(undefined);
    try {
      show(await post<TestRunReport>(`/api/tests/${encodeURIComponent(id)}/run`, {}));
      tests.reload();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setRunning(undefined);
    }
  };
  const runAll = async () => {
    setRunning('*');
    setError(undefined);
    try {
      setAll(await post('/api/tests/run-all', {}));
      tests.reload();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setRunning(undefined);
    }
  };

  const benchParam = query.get('bench') ?? '';
  useEffect(() => {
    if (benchParam) setMode('bench');
  }, [benchParam]);

  const auto = useRef('');
  useEffect(() => {
    const id = query.get('run');
    if (id && auto.current !== id) {
      auto.current = id;
      setMode('tests');
      void run(id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query]);

  const rows = (tests.data ?? []).filter((t) => !filter || `${t.id} ${t.title} ${t.target} ${t.level}`.toLowerCase().includes(filter.toLowerCase()));
  const passedAll = all?.filter((x) => x.passed).length;

  return (
    <div className="view">
      <div className="view-head">
        <h2>Test bench</h2>
        <p className="muted">Functional tests only, on the studio interpreter with a virtual clock. Bind simulators to ports, set the date and tenant, send signals and assert on the result.</p>
      </div>
      <div className="tabs">
        <button className={mode === 'tests' ? 'tab on' : 'tab'} onClick={() => setMode('tests')}>Tests</button>
        <button className={mode === 'bench' ? 'tab on' : 'tab'} onClick={() => setMode('bench')}>Ad hoc bench</button>
      </div>
      <ErrorBox error={error} />
      {mode === 'tests' ? (
        <Section
          title="Tests"
          count={tests.data?.length}
          actions={
            <>
              <input type="search" placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter tests" />
              <button className="primary" onClick={runAll} disabled={!!running}>{running === '*' ? 'Running all…' : 'Run all'}</button>
            </>
          }
        >
          {all && (
            <div className={`alert ${passedAll === all.length ? 'pass' : 'fail'}`}>
              Run all: {passedAll} of {all.length} pass.
              {all.filter((x) => !x.passed).map((x) => (
                <div key={x.id} className="small">
                  <button className="link mono" onClick={() => run(x.id)}>{x.id}</button> {x.error ?? x.failed?.map((c: any) => c.what).join(', ')}
                </div>
              ))}
            </div>
          )}
          <ErrorBox error={tests.error} />
          {tests.loading && !tests.data ? <Loading what="tests" /> : (
            <div className="table-wrap">
              <table className="grid">
                <thead><tr><th>Test</th><th>Title</th><th>Level</th><th>Target</th><th>Tenant</th><th>Last result</th><th></th></tr></thead>
                <tbody>
                  {rows.map((t) => (
                    <tr key={t.id} className={report?.id === t.id ? 'sel' : ''}>
                      <td className="mono nowrap">{t.id}</td>
                      <td>{t.title}</td>
                      <td>{t.level}</td>
                      <td className="mono small break"><a href={`#/artefact/${encodeURIComponent(t.target)}`}>{t.target}</a></td>
                      <td>{t.tenant ?? <span className="muted">shared</span>}</td>
                      <td>{t.lastResult ? <PassFail passed={t.lastResult === 'pass'} /> : <span className="muted small">not run</span>}</td>
                      <td><button onClick={() => run(t.id)} disabled={!!running}>{running === t.id ? 'Running…' : 'Run'}</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Section>
      ) : (
        <AdHocBench key={benchParam} initialTarget={query.get('bench') ?? ''} tests={tests.data ?? []} onReport={show} />
      )}
      <div ref={reportRef}>{report && <RunReport report={report} />}</div>
    </div>
  );
}

function AdHocBench({ initialTarget, tests, onReport }: { initialTarget: string; tests: TestRow[]; onReport: (r: TestRunReport) => void }) {
  const { tenant, meta } = useStudio();
  const [target, setTarget] = useState(initialTarget);
  const [level, setLevel] = useState('');
  const [date, setDate] = useState('2026-11-17');
  const [runTenant, setRunTenant] = useState(tenant);
  const [bench, setBench] = useState<BenchRow[]>([]);
  const [signals, setSignals] = useState<SignalRow[]>([]);
  const [input, setInput] = useState<'context' | 'inbound'>('context');
  const [context, setContext] = useState('{\n  "header": { "paymentId": "P-1", "endToEndId": "E2E-1" }\n}');
  const [inbound, setInbound] = useState('');
  const [expect, setExpect] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const targets = useLoad(async () => {
    const kinds = ['workflow', 'hook', 'activity'];
    const r = await Promise.all(kinds.map((k) => get(`/api/catalogue?kind=${k}`)));
    return r.flatMap((x) => x.items).filter((i: Summary) => i.status === 'published') as Summary[];
  }, [tenant]);
  const ports = useLoad(() => get('/api/catalogue?kind=port').then((r) => r.items as Summary[]), [tenant]);
  const samples = useLoad(() => get<string[]>('/api/samples'), []);
  const simsByPort = useSimulators(bench.map((b) => b.port));

  const loadFromTest = (id: string) => {
    const t = tests.find((x) => x.id === id);
    if (!t) return;
    setTarget(t.target);
    setLevel(t.level);
    setBench(t.bench.map((b: any) => ({ port: b.port, simulator: b.simulator ?? '', scenario: b.scenario ?? '' })));
    get(`/api/tests/${encodeURIComponent(id)}`).then((r) => {
      const g = r.test?.given ?? {};
      if (r.test?.evaluationDate) setDate(r.test.evaluationDate);
      if (r.test?.tenant) setRunTenant(r.test.tenant);
      if (g.inbound) { setInput('inbound'); setInbound(g.inbound); } else { setInput('context'); setContext(JSON.stringify(g.context ?? {}, null, 2)); }
      setSignals((g.signals ?? []).map((s: any) => ({ signal: s.signal, at: s.at })));
      setExpect(r.test?.expect ? JSON.stringify(r.test.expect, null, 2) : '');
    }).catch((e) => setError(e.message));
  };

  const runBench = async () => {
    setError(undefined);
    let ctx: any;
    let exp: any;
    if (input === 'context') {
      try { ctx = context.trim() ? JSON.parse(context) : undefined; } catch (e: any) { setError(`The context is not JSON: ${e.message}`); return; }
    }
    try { exp = expect.trim() ? JSON.parse(expect) : undefined; } catch (e: any) { setError(`The expectation is not JSON: ${e.message}`); return; }
    setBusy(true);
    try {
      const r = await post<TestRunReport>('/api/bench/run', {
        target, level: level || undefined, evaluationDate: date, tenant: runTenant,
        bench: bench.filter((b) => b.port).map((b) => ({ port: b.port, simulator: b.simulator || undefined, scenario: b.scenario || undefined })),
        signals: signals.filter((s) => s.signal && s.at).map((s) => ({ signal: s.signal, at: s.at })),
        context: input === 'context' ? ctx : undefined,
        inbound: input === 'inbound' && inbound ? inbound : undefined,
        expect: exp,
      }, tenant);
      onReport(r);
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };

  const setRow = (i: number, patch: Partial<BenchRow>) => setBench((b) => b.map((x, n) => (n === i ? { ...x, ...patch } : x)));
  const setSig = (i: number, patch: Partial<SignalRow>) => setSignals((b) => b.map((x, n) => (n === i ? { ...x, ...patch } : x)));

  return (
    <Section title="Ad hoc bench">
      <div className="form-grid">
        <label className="field wide">
          <span>Start from a test (optional)</span>
          <select onChange={(e) => loadFromTest(e.target.value)} defaultValue="" aria-label="Load from test">
            <option value="">None</option>
            {tests.map((t) => <option key={t.id} value={t.id}>{t.id}: {t.title}</option>)}
          </select>
        </label>
        <label className="field wide">
          <span>Target</span>
          <select value={target} onChange={(e) => setTarget(e.target.value)} aria-label="Target">
            <option value="">Choose a workflow, hook or activity</option>
            {(targets.data ?? []).map((t) => <option key={t.key} value={t.name}>{t.kind}: {t.name}</option>)}
            {target && !(targets.data ?? []).some((t) => t.name === target) && <option value={target}>{target}</option>}
          </select>
        </label>
        <label className="field">
          <span>Level</span>
          <select value={level} onChange={(e) => setLevel(e.target.value)} aria-label="Level">
            <option value="">From the target</option>
            {['activity', 'leg', 'hook', 'handler', 'end-to-end'].map((l) => <option key={l}>{l}</option>)}
          </select>
        </label>
        <label className="field">
          <span>Evaluation date</span>
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </label>
        <label className="field">
          <span>Tenant for the run</span>
          <select value={runTenant} onChange={(e) => setRunTenant(e.target.value)} aria-label="Run tenant">
            {(meta?.tenants ?? [tenant]).map((t) => <option key={t}>{t}</option>)}
          </select>
        </label>
      </div>

      <h4>Ports and simulators</h4>
      {bench.length === 0 && <p className="muted small">No port bound. A port the run reaches uses its default simulator, and the report says so.</p>}
      {bench.map((b, i) => {
        const sims = simsByPort[b.port] ?? [];
        const sim = sims.find((s) => s.name === b.simulator);
        return (
          <div className="bench-row" key={i}>
            <select value={b.port} onChange={(e) => setRow(i, { port: e.target.value, simulator: '', scenario: '' })} aria-label="Port">
              <option value="">Port</option>
              {(ports.data ?? []).map((p) => <option key={p.key} value={p.name}>{p.name}</option>)}
            </select>
            <select value={b.simulator} onChange={(e) => setRow(i, { simulator: e.target.value, scenario: '' })} aria-label="Simulator">
              <option value="">Simulator</option>
              {sims.map((s) => <option key={s.name} value={s.name}>{s.name} ({s.direction})</option>)}
              {b.simulator && !sim && <option value={b.simulator}>{b.simulator}</option>}
            </select>
            <select value={b.scenario} onChange={(e) => setRow(i, { scenario: e.target.value })} aria-label="Scenario">
              <option value="">Scenario</option>
              {(sim?.scenarios ?? []).map((s: string) => <option key={s}>{s}</option>)}
              {b.scenario && !sim?.scenarios?.includes(b.scenario) && <option value={b.scenario}>{b.scenario}</option>}
            </select>
            <button className="link danger" onClick={() => setBench((x) => x.filter((_, n) => n !== i))}>Remove</button>
          </div>
        );
      })}
      <button onClick={() => setBench((b) => [...b, { port: '', simulator: '', scenario: '' }])}>Add a port</button>

      <h4>Signals</h4>
      {signals.map((s, i) => (
        <div className="bench-row" key={i}>
          <select value={s.signal} onChange={(e) => setSig(i, { signal: e.target.value })} aria-label="Signal">
            {SIGNALS.map((x) => <option key={x}>{x}</option>)}
          </select>
          <label className="field-inline"><span>at</span><input value={s.at} onChange={(e) => setSig(i, { at: e.target.value })} placeholder="PT30S" className="mono" size={8} /></label>
          <button className="link danger" onClick={() => setSignals((x) => x.filter((_, n) => n !== i))}>Remove</button>
        </div>
      ))}
      <button onClick={() => setSignals((s) => [...s, { signal: 'pause', at: 'PT30S' }])}>Add a signal</button>
      <p className="muted small">The time is an ISO 8601 duration on the virtual clock from the start of the run.</p>

      <h4>Input</h4>
      <div className="tabs small-tabs">
        <button className={input === 'context' ? 'tab on' : 'tab'} onClick={() => setInput('context')}>Context as JSON</button>
        <button className={input === 'inbound' ? 'tab on' : 'tab'} onClick={() => setInput('inbound')}>Inbound sample</button>
      </div>
      {input === 'context' ? (
        <textarea className="code-input" rows={8} value={context} onChange={(e) => setContext(e.target.value)} spellCheck={false} aria-label="Context JSON" />
      ) : (
        <select value={inbound} onChange={(e) => setInbound(e.target.value)} aria-label="Inbound sample">
          <option value="">Choose a sample</option>
          {(samples.data ?? []).map((s) => <option key={s}>{s}</option>)}
        </select>
      )}
      <h4>Expect (optional)</h4>
      <textarea className="code-input" rows={4} value={expect} onChange={(e) => setExpect(e.target.value)} spellCheck={false} placeholder='{ "outcome": "settled", "attempts": { "send": 3 } }' aria-label="Expectation JSON" />
      <ErrorBox error={error} />
      <div className="row-actions">
        <button className="primary" disabled={!target || busy} onClick={runBench}>{busy ? 'Running…' : 'Run the bench'}</button>
      </div>
    </Section>
  );
}

/** Simulators per port, loaded once per port. */
function useSimulators(ports: string[]) {
  const [map, setMap] = useState<Record<string, any[]>>({});
  const key = useMemo(() => [...new Set(ports.filter(Boolean))].sort().join(','), [ports]);
  useEffect(() => {
    for (const p of key.split(',').filter(Boolean)) {
      if (map[p]) continue;
      get<any[]>(`/api/simulators?port=${encodeURIComponent(p)}`).then((s) => setMap((m) => ({ ...m, [p]: s })), () => setMap((m) => ({ ...m, [p]: [] })));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return map;
}

