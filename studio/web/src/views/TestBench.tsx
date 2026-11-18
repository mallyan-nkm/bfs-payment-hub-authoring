import { useEffect, useMemo, useRef, useState } from 'react';
import { get, post, refusal, type Issue, type Summary, type TestRunReport } from '../api';
import { Badge, ErrorBox, Issues, Loading, PassFail, Section, useLoad, useStudio } from '../ui';
import { StringList, toYamlText } from '../authoring';
import { RunReport } from './RunReport';
import { useAssist } from '../assist';

// The test bench (F-16 to F-19): stored tests with run and run-all, and an ad hoc bench.

interface TestRow { id: string; title: string; target: string; level: string; tenant?: string; scope: string; bench: any[]; lastResult?: 'pass' | 'fail' }
interface BenchRow { port: string; simulator: string; scenario: string }
interface SignalRow { signal: string; at: string }

const SIGNALS = ['pause', 'resume', 'cancel', 're-screen'];

export function TestBench({ query }: { query: URLSearchParams }) {
  const { tenant } = useStudio();
  const tests = useLoad(() => get<TestRow[]>('/api/tests'), [tenant]);
  const [report, setReport] = useState<TestRunReport>();
  const [source, setSource] = useState<any>();
  const [running, setRunning] = useState<string>();
  const [error, setError] = useState<string>();
  const [all, setAll] = useState<any[]>();
  const [filter, setFilter] = useState('');
  const reportRef = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<'tests' | 'bench'>(query.get('bench') ? 'bench' : 'tests');

  const show = (r: TestRunReport, src?: any) => {
    setReport(r);
    setSource(src);
    setTimeout(() => reportRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  };

  const run = async (id: string) => {
    setRunning(id);
    setError(undefined);
    try {
      const [r, t] = await Promise.all([post<TestRunReport>(`/api/tests/${encodeURIComponent(id)}/run`, {}), get(`/api/tests/${encodeURIComponent(id)}`).catch(() => undefined)]);
      show(r, t?.test);
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
      <div ref={reportRef}>{report && <RunReport key={`${report.id}|${report.elapsed}|${report.trace.join()}`} report={report} footer={<SaveAsTest report={report} source={source} />} />}</div>
    </div>
  );
}

function AdHocBench({ initialTarget, tests, onReport }: { initialTarget: string; tests: TestRow[]; onReport: (r: TestRunReport, src?: any) => void }) {
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
  const [message, setMessage] = useState('');
  const [expect, setExpect] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [lastRun, setLastRun] = useState<any>();

  const targets = useLoad(async () => {
    const kinds = ['workflow', 'hook', 'activity'];
    const r = await Promise.all(kinds.map((k) => get(`/api/catalogue?kind=${k}`)));
    return r.flatMap((x) => x.items).filter((i: Summary) => i.status === 'published') as Summary[];
  }, [tenant]);
  const ports = useLoad(() => get('/api/catalogue?kind=port').then((r) => r.items as Summary[]), [tenant]);
  // The start messages of every scheme's dispatch table: a file from the bank's channel runs as a
  // payment end to end when the bench names the message it is (R-07-14).
  const startMessages = useLoad(async () => {
    const schemes = (await get('/api/schemes')) as { scope: string }[];
    const docs = await Promise.all(schemes.map((s) => get(`/api/schemes/${s.scope}`)));
    return docs.flatMap((d: any) => (d.scheme?.messages ?? []).filter((m: any) => m.disposition?.start).map((m: any) => ({ id: m.id as string, leg: m.disposition.start.leg as string })));
  }, [tenant]);
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
      const body = {
        target, level: level || undefined, evaluationDate: date, tenant: runTenant,
        bench: bench.filter((b) => b.port).map((b) => ({ port: b.port, simulator: b.simulator || undefined, scenario: b.scenario || undefined })),
        signals: signals.filter((s) => s.signal && s.at).map((s) => ({ signal: s.signal, at: s.at })),
        context: input === 'context' ? ctx : undefined,
        inbound: input === 'inbound' && inbound ? inbound : undefined,
        message: input === 'inbound' && inbound && message ? message : undefined,
        expect: exp,
      };
      const r = await post<TestRunReport>('/api/bench/run', body, tenant);
      const given: any = {};
      if (body.context) given.context = body.context;
      if (body.inbound) given.inbound = body.inbound;
      if (body.message) given.message = body.message;
      if (body.signals.length) given.signals = body.signals;
      setLastRun({ outcome: r.outcome, trace: r.trace, reasonCode: (r as any).reasonCode, error: (r as any).error });
      onReport(r, { target, level: r.level, evaluationDate: date, tenant: runTenant !== 'owner' ? runTenant : undefined, bench: body.bench, given });
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };

  // The bench set-up as one document, for the assistant: it may read it and propose a new one.
  let ctxDoc: any = context;
  try { ctxDoc = context.trim() ? JSON.parse(context) : undefined; } catch { /* keep the text */ }
  useAssist({
    surface: 'bench', kind: 'bench', topic: 'bench', report: lastRun,
    doc: { target, level: level || undefined, evaluationDate: date, tenant: runTenant, bench, input, context: input === 'context' ? ctxDoc : undefined, inbound: input === 'inbound' ? inbound : undefined, message: message || undefined, expect: expect.trim() || undefined },
    setDoc: (d: any) => {
      if (!d) return;
      setTarget(d.target ?? ''); setLevel(d.level ?? ''); if (d.evaluationDate) setDate(d.evaluationDate); if (d.tenant) setRunTenant(d.tenant);
      setBench((d.bench ?? []).map((b: any) => ({ port: b.port ?? '', simulator: b.simulator ?? '', scenario: b.scenario ?? '' })));
      if (d.inbound) { setInput('inbound'); setInbound(d.inbound); setMessage(d.message ?? ''); }
      else { setInput('context'); setContext(d.context === undefined ? '' : typeof d.context === 'string' ? d.context : JSON.stringify(d.context, null, 2)); }
      setExpect(d.expect === undefined ? '' : typeof d.expect === 'string' ? d.expect : JSON.stringify(d.expect, null, 2));
    },
  });

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
        <div className="stack">
          <select value={inbound} onChange={(e) => setInbound(e.target.value)} aria-label="Inbound sample">
            <option value="">Choose a sample</option>
            {(samples.data ?? []).map((s) => <option key={s}>{s}</option>)}
          </select>
          <AddSample onSaved={(name) => { samples.reload(); setInbound(name); }} />
          <label className="field">
            <span>Start message (for a whole payment from the bank's channel)</span>
            <select value={message} onChange={(e) => setMessage(e.target.value)} aria-label="Start message">
              <option value="">None: an inward simulator sends it, or the leg starts alone</option>
              {(startMessages.data ?? []).map((m) => <option key={m.id} value={m.id}>{m.id} (starts {m.leg})</option>)}
            </select>
          </label>
        </div>
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


/**
 * Turn a run into a saved test (C15-S4): the assertions are captured from the run and can be
 * edited, checked against a fresh run, and saved as a test draft.
 */
function SaveAsTest({ report, source }: { report: TestRunReport; source?: any }) {
  const { actor } = useStudio();
  const rec = report.record;
  const root = rec?.instances?.find((i) => !i.parent) ?? rec?.instances?.[0];
  const outMsgs = useMemo(() => {
    const m = new Map<string, { port: string; message: string; count: number }>();
    for (const x of rec?.messages ?? []) {
      if (x.from !== 'platform' || !x.type) continue;
      const k = `${x.port}|${x.type}`;
      m.set(k, { port: x.port, message: x.type, count: (m.get(k)?.count ?? 0) + 1 });
    }
    return [...m.values()];
  }, [rec]);
  const retried = Object.fromEntries(Object.entries(root?.attempts ?? {}).filter(([, n]) => (n as number) > 1));
  const [id, setId] = useState(`${report.id === 'BENCH-RUN' ? 'NEW' : report.id}-CAPTURED`);
  const [title, setTitle] = useState(`Captured from a run of ${report.target}`);
  const [on, setOn] = useState<Record<string, boolean>>({ outcome: !!report.outcome, reasonCode: !!root?.reasonCode, trace: true, detail: !!report.detail?.length, messages: outMsgs.length > 0, attempts: Object.keys(retried).length > 0, virtualElapsed: false });
  const [outcome, setOutcome] = useState(report.outcome ?? '');
  const [reasonCode, setReasonCode] = useState(root?.reasonCode ?? '');
  const [trace, setTrace] = useState<string[]>(report.trace);
  const [detail, setDetail] = useState<string[]>(report.detail ?? []);
  const [messages, setMessages] = useState(outMsgs);
  const [attempts, setAttempts] = useState(JSON.stringify(retried));
  const [elapsed, setElapsed] = useState(report.elapsed);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: string; text: string; issues?: Issue[] }>();
  const [check, setCheck] = useState<TestRunReport>();
  if (!source) return <Section title="Save the run as a test"><p className="muted small">Run a stored test or the ad hoc bench to capture its assertions.</p></Section>;

  const expect = () => {
    const e: any = {};
    if (on.outcome && outcome) e.outcome = outcome;
    if (on.reasonCode && reasonCode) e.reasonCode = reasonCode;
    if (on.messages && messages.length) e.messages = messages;
    if (on.trace) e.trace = trace;
    if (on.detail && detail.length) e.detail = detail;
    if (on.attempts) {
      try {
        const a = JSON.parse(attempts || '{}');
        if (Object.keys(a).length) e.attempts = a;
      } catch {
        /* shown below */
      }
    }
    if (on.virtualElapsed && elapsed) e.virtualElapsed = elapsed;
    return e;
  };
  const testDoc = () => ({
    kind: 'test', id, release: '1.0.0', title, target: source.target, level: source.level ?? report.level, evaluationDate: source.evaluationDate ?? '2026-11-17',
    ...(source.tenant ? { tenant: source.tenant } : {}), ...(source.bench?.length ? { bench: source.bench } : {}), given: source.given ?? {}, expect: expect(),
  });
  const verify = async () => {
    setBusy(true);
    setMsg(undefined);
    try {
      const g = source.given ?? {};
      setCheck(await post<TestRunReport>('/api/bench/run', { target: source.target, level: source.level, evaluationDate: source.evaluationDate, tenant: source.tenant, bench: source.bench, context: g.context, inbound: g.inbound, message: g.message, signals: g.signals, expect: expect() }));
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    } finally {
      setBusy(false);
    }
  };
  const save = async () => {
    setBusy(true);
    setMsg(undefined);
    try {
      const r = await post('/api/drafts', { doc: testDoc(), actor });
      const errs = (r.issues as Issue[]).filter((i) => i.severity === 'error');
      setMsg({ tone: errs.length ? 'warn' : 'pass', text: `Saved the test draft ${r.draft.key} as ${actor}.`, issues: r.issues });
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    } finally {
      setBusy(false);
    }
  };
  const box = (k: string, label: string) => <label className="check"><input type="checkbox" checked={!!on[k]} onChange={(e) => setOn((o) => ({ ...o, [k]: e.target.checked }))} /> {label}</label>;
  let attemptsErr: string | undefined;
  try {
    JSON.parse(attempts || '{}');
  } catch (e: any) {
    attemptsErr = e.message;
  }
  return (
    <Section title="Save the run as a test (assertions)">
      <p className="muted small">The assertions come from this run. Edit them, check them against a fresh run, and save the test as a draft. A changed trace or detail turns the test red.</p>
      <div className="form-grid">
        <label className="field"><span>Test id</span><input value={id} onChange={(e) => setId(e.target.value)} className="mono" aria-label="Test id" /></label>
        <label className="field wide"><span>Title</span><input value={title} onChange={(e) => setTitle(e.target.value)} aria-label="Test title" /></label>
        <label className="field"><span>Target</span><code className="break">{source.target}</code></label>
      </div>
      <div className="assert-grid">
        <div>{box('outcome', 'Outcome')}<input value={outcome} onChange={(e) => setOutcome(e.target.value)} className="mono" disabled={!on.outcome} aria-label="Expected outcome" /></div>
        <div>{box('reasonCode', 'Reason code')}<input value={reasonCode} onChange={(e) => setReasonCode(e.target.value)} className="mono" disabled={!on.reasonCode} aria-label="Expected reason code" /></div>
        <div>{box('virtualElapsed', 'Virtual time')}<input value={elapsed} onChange={(e) => setElapsed(e.target.value)} className="mono" disabled={!on.virtualElapsed} aria-label="Expected virtual time" /></div>
        <div>{box('attempts', 'Attempts per step')}<input value={attempts} onChange={(e) => setAttempts(e.target.value)} className="mono" disabled={!on.attempts} aria-label="Expected attempts JSON" />{attemptsErr && <div className="small fail-text">Not JSON</div>}</div>
      </div>
      <div className="assert-grid two">
        <div>{box('trace', 'Business trace')}{on.trace && <StringList value={trace} onChange={(v) => setTrace(v ?? [])} placeholder="step:outcome" label="Add a trace entry" />}</div>
        <div>{box('detail', 'Detail inside composite steps')}{on.detail && <StringList value={detail} onChange={(v) => setDetail(v ?? [])} placeholder="step.inner:outcome" label="Add a detail entry" />}</div>
      </div>
      <div>
        {box('messages', 'Messages sent')}
        {on.messages && (
          <table className="grid compact">
            <thead><tr><th>Port</th><th>Message</th><th>Count</th><th></th></tr></thead>
            <tbody>
              {messages.map((m, i) => (
                <tr key={i}>
                  <td className="mono small">{m.port}</td><td className="mono small">{m.message}</td>
                  <td><input value={m.count} onChange={(e) => setMessages(messages.map((x, n) => (n === i ? { ...x, count: Number(e.target.value) || 0 } : x)))} size={3} aria-label={`Count of ${m.message}`} /></td>
                  <td><button className="link danger" onClick={() => setMessages(messages.filter((_, n) => n !== i))}>Remove</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <div className="row-actions">
        <button onClick={verify} disabled={busy}>{busy ? 'Running…' : 'Check against a fresh run'}</button>
        <button className="primary" onClick={save} disabled={busy || !id}>Save as a test draft as {actor}</button>
        {check && <span>{check.passed ? <Badge tone="pass">the assertions hold</Badge> : <Badge tone="fail">{check.checks.filter((c) => !c.passed).map((c) => c.what).join(', ')} red</Badge>}</span>}
      </div>
      {msg && <div className={`alert ${msg.tone}`} role="status">{msg.text}{msg.issues && <Issues issues={msg.issues} />}</div>}
      <details>
        <summary>The test as YAML</summary>
        <pre className="code">{toYamlText(testDoc())}</pre>
      </details>
    </Section>
  );
}

// A synthetic sample file a test or a bench run names (C12-S4). Never personal data.
function AddSample({ onSaved }: { onSaved: (name: string) => void }) {
  const { actor } = useStudio();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [content, setContent] = useState('');
  const [msg, setMsg] = useState<string>();
  const save = async () => {
    setMsg(undefined);
    try {
      const r = await post('/api/samples', { actor, name, content });
      onSaved(r.sample);
      setOpen(false);
    } catch (e: any) {
      setMsg(refusal(e));
    }
  };
  if (!open) return <button className="link" onClick={() => setOpen(true)}>Add a sample file</button>;
  return (
    <div className="stack">
      <label className="field"><span>Sample file name</span><input value={name} onChange={(e) => setName(e.target.value)} placeholder="demo-credit-file-valid.csv" aria-label="Sample file name" /></label>
      <textarea className="code-input" rows={6} value={content} onChange={(e) => setContent(e.target.value)} spellCheck={false} aria-label="Sample content" placeholder="Synthetic content only. Never real names or account numbers." />
      <div><button onClick={save} disabled={!name || !content}>Save the sample as {actor}</button> <button className="link" onClick={() => setOpen(false)}>Cancel</button></div>
      {msg && <div className="alert fail" role="status">{msg}</div>}
    </div>
  );
}
