import { useEffect, useMemo, useState } from 'react';
import type { BenchClient } from './api';

// The Test bench section of the runtime's web app (runtime/testbench/README.md): pick a profile, send data, watch each
// engine run, see each beside the studio interpreter with the diff; build and validate test data; run suites; history.
// Routes: #/testbench (run), #/testbench/data, #/testbench/suite, #/testbench/history, #/testbench/runs/<id>.

type DataChoice = { mode: 'sample'; sample: string } | { mode: 'text'; text: string; name: string };
const ENGINES = ['temporal', 'dtfx', 'elsa', 'flowable'];
const DATABASES = ['PostgreSql', 'SqlServer', 'none'];
const PAGES: [string, string][] = [['', 'Run'], ['data', 'Test data'], ['suite', 'Suite'], ['history', 'History']];

function useLoad<T>(load: () => Promise<T>, deps: any[] = []): T | undefined {
  const [v, setV] = useState<T>();
  useEffect(() => { load().then(setV, () => setV(undefined)); }, deps);
  return v;
}

export function TestBenchSection({ api, path, go }: { api: BenchClient; path: string[]; go(h: string): void }) {
  const [data, setData] = useState<DataChoice>();
  const page = path[0] === 'runs' ? '' : path[0] ?? '';
  return (
    <section>
      <h1>Test bench</h1>
      <nav className="tabs" aria-label="Test bench">
        {PAGES.map(([id, title]) => (
          <a key={id} className={`tab ${page === id ? 'active' : ''}`} href={`#/testbench${id ? '/' + id : ''}`} data-testid={`nav-${id || 'run'}`}>{title}</a>
        ))}
      </nav>
      {page === '' && <RunView api={api} data={data} setData={setData} shown={path[0] === 'runs' ? path[1] : undefined} go={go} />}
      {page === 'data' && <DataView api={api} onUse={(d) => { setData(d); go('#/testbench'); }} />}
      {page === 'suite' && <SuiteView api={api} />}
      {page === 'history' && <HistoryView api={api} go={go} />}
    </section>
  );
}

function RunView({ api, data, setData, shown, go }: { api: BenchClient; data?: DataChoice; setData(d: DataChoice): void; shown?: string; go(h: string): void }) {
  const profiles = useLoad<any[]>(() => api.call('/profiles'));
  const benches = useLoad<Record<string, any>>(() => api.call('/benches'));
  const library = useLoad<any[]>(() => api.call('/data/library'));
  const templates = useLoad<any>(() => api.call('/data/templates'));
  const [profile, setProfile] = useState('');
  const [engines, setEngines] = useState<string[]>();
  const [dbs, setDbs] = useState<string[]>();
  const [tenant, setTenant] = useState<string>();
  const [bench, setBench] = useState<string>();
  const [job, setJob] = useState<any>();
  const [err, setErr] = useState<string>();
  const p = profiles?.find((x) => x.name === profile);

  useEffect(() => { if (profiles?.length && !profile) setProfile((profiles.find((x) => (x.engines ?? []).length === 0) ?? profiles[0]).name); }, [profiles]);
  useEffect(() => { setEngines(undefined); setDbs(undefined); setTenant(undefined); setBench(undefined); }, [profile]);
  useEffect(() => {
    if (!shown) return;
    let stop = false;
    const tick = async () => {
      try {
        const j = await api.call(`/runs/${shown}`);
        if (stop) return;
        setJob(j);
        if (j.status !== 'done') setTimeout(tick, 1000);
      } catch (e: any) { setErr(e.message); }
    };
    tick();
    return () => { stop = true; };
  }, [shown]);

  const eff = { engines: engines ?? p?.engines ?? [], dbs: dbs ?? p?.databases ?? [], tenant: tenant ?? p?.tenant, bench: bench ?? p?.bench };
  const toggle = (list: string[], x: string) => (list.includes(x) ? list.filter((y) => y !== x) : [...list, x]);
  const run = async () => {
    setErr(undefined);
    if (!data) return setErr('Choose the data first');
    const req: any = { profile, data: data.mode === 'sample' ? { sample: data.sample } : { text: data.text, name: data.name } };
    if (engines) req.engines = engines;
    if (dbs) req.databases = dbs;
    if (tenant) req.tenant = tenant;
    if (bench) req.bench = bench;
    try {
      const j = await api.call('/runs', req);
      setJob(j);
      go(`#/testbench/runs/${j.id}`);
    } catch (e: any) { setErr(e.message); }
  };

  return (
    <div>
      <div className="card">
        <h2>1. Profile</h2>
        <select data-testid="profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
          {profiles?.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
        </select>
        <p className="muted">{p?.title}</p>
        <div className="filters">
          <fieldset><legend>Engines</legend>{ENGINES.map((e) => <label key={e}><input type="checkbox" checked={eff.engines.includes(e)} onChange={() => setEngines(toggle(eff.engines, e))} /> {e}</label>)}<label><input type="checkbox" checked disabled /> interpreter (reference)</label></fieldset>
          <fieldset><legend>Database</legend>{DATABASES.map((d) => <label key={d}><input type="checkbox" checked={eff.dbs.includes(d)} onChange={() => setDbs(toggle(eff.dbs, d))} /> {d}</label>)}</fieldset>
          <fieldset><legend>Tenant</legend><select value={eff.tenant} onChange={(e) => setTenant(e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant}>{t.tenant}</option>)}</select></fieldset>
          <fieldset><legend>Simulators</legend><select data-testid="bench" value={eff.bench} onChange={(e) => setBench(e.target.value)}>{benches && Object.keys(benches).map((b) => <option key={b}>{b}</option>)}</select>
            <ul className="muted">{benches?.[eff.bench]?.ports.map((x: string) => <li key={x} className="mono">{x}</li>)}</ul></fieldset>
        </div>
      </div>
      <div className="card">
        <h2>2. Data</h2>
        <div className="filters">
          <label>Library sample{' '}
            <select data-testid="sample" value={data?.mode === 'sample' ? data.sample : ''} onChange={(e) => e.target.value && setData({ mode: 'sample', sample: e.target.value })}>
              <option value="">choose…</option>
              {library?.map((s) => <option key={s.id} value={s.id}>{s.id}: {s.title} (expect {s.expect})</option>)}
            </select>
          </label>
          <label>Upload a file <input type="file" data-testid="upload" onChange={async (e) => { const f = e.target.files?.[0]; if (f) setData({ mode: 'text', text: await f.text(), name: f.name }); }} /></label>
        </div>
        <textarea data-testid="paste" className="mono" rows={5} style={{ width: '100%' }} placeholder="…or paste a scheme file, an ISO 20022 message or a canonical JSON payment"
          value={data?.mode === 'text' ? data.text : ''} onChange={(e) => setData({ mode: 'text', text: e.target.value, name: 'pasted' })} />
        {data && <p className="muted">Data: {data.mode === 'sample' ? `the sample ${data.sample}` : `${data.name}, ${data.text.length} characters`}</p>}
      </div>
      <p>
        <button className="primary" data-testid="run" onClick={run} disabled={!data || !profile || (job && job.status !== 'done')}>Run</button>
        {err && <span className="error" data-testid="run-error"> {err}</span>}
      </p>
      {job && <Result api={api} job={job} />}
    </div>
  );
}

function engineStates(progress: string[], engines: string[]) {
  const st: Record<string, string> = {};
  for (const l of progress) {
    const m = /^(interpreter|temporal|dtfx|elsa|flowable)(?: on \w+)?: (.*?)(?: \((\w+)\))?$/.exec(l);
    if (m) st[m[3] ? `${m[1]} · ${m[3]}` : m[1]] = m[2];
  }
  for (const e of engines) if (!Object.keys(st).some((k) => k.startsWith(e))) st[e] = 'waiting';
  return st;
}

const ms = (n?: number) => (n === undefined || n === null ? '' : `${(n / 1000).toFixed(1)} s`);
function store(run: any) {
  const s = run.store;
  if (!s) return run.database === 'none' ? 'no store' : '';
  const rows = s.rows ?? {};
  return `${s.provider}: ${rows.bulks?.length ?? 0} bulk, ${rows.payments?.length ?? 0} payment(s), ${s.payloads?.length ?? 0} payload(s) ${s.ok ? 'rebuilt' : 'NOT rebuilt'}, chains ${rows.chains?.verified ?? 0}/${rows.chains?.subjects ?? 0}`;
}

function Result({ api, job }: { api: BenchClient; job: any }) {
  const r = job.result;
  const [caseId, setCaseId] = useState('');
  const [saved, setSaved] = useState<string>();
  const states = engineStates(job.progress ?? [], r?.plan?.engines ?? []);
  const columns = useMemo(() => (r ? [...(r.reference ? [{ label: 'interpreter (reference)', run: r.reference }] : []), ...(r.runs ?? []).map((x: any) => ({ label: `${x.engine} · ${x.database}`, run: x }))] : []), [r]);
  const passed = r?.passed;
  return (
    <div className="card" data-testid="result">
      <h2>Run <span className="mono">{job.id}</span> <span data-testid="status" className={`badge ${job.status === 'done' ? (passed ? 'pass' : 'fail') : 'warn'}`}>{job.status === 'done' ? (passed ? 'passed' : 'failed') : job.status}</span></h2>
      {job.status !== 'done' && <ul className="summary" data-testid="progress">{Object.entries(states).map(([k, v]) => <li key={k}><b>{k}</b> {v}</li>)}</ul>}
      {r && (
        <>
          <div className="table-wrap">
            <table className="grid" data-testid="summary">
              <thead><tr><th>Run</th><th>Outcome</th><th>Same as the interpreter</th><th>Time</th><th>Store</th></tr></thead>
              <tbody>
                {columns.map((c) => (
                  <tr key={c.label} data-testid={`row-${c.label.split(' ')[0]}`}>
                    <td>{c.label}</td>
                    <td>{c.run.error ? <span className="error">error: {String(c.run.error).split('\n')[0]}</span> : c.run.outcome}{c.run.retried && <span className="muted" title={c.run.retried}> (run twice, I-93)</span>}</td>
                    <td>{c.run.comparison ? (c.run.comparison.same ? <span className="badge pass">yes</span> : <span className="badge fail">no</span>) : ''}</td>
                    <td>{ms(c.run.timings?.totalMs ?? c.run.elapsedMs)}</td>
                    <td>{store(c.run)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="cards">
            {columns.map((c) => {
              const diff: string[] = c.run.comparison?.trace?.diff ?? [];
              return (
                <div key={c.label}><b>{c.label}</b>{diff.length > 0 && <span className="error"> diff</span>}
                  <pre className="card mono" data-testid={`trace-${c.label.split(' ')[0]}`}>{(diff.length ? diff : c.run.trace ?? []).join('\n')}</pre>
                </div>
              );
            })}
          </div>
          {(r.failures ?? []).length > 0 && <ul className="error" data-testid="failures">{r.failures.map((f: string) => <li key={f}>{f}</li>)}</ul>}
          <div className="action-buttons">
            <button onClick={() => api.openReport(job.id)}>Open the report</button>
            <input placeholder="case id, e.g. TB-BANK-001" value={caseId} onChange={(e) => setCaseId(e.target.value)} data-testid="case-id" />
            <button data-testid="save-case" disabled={!caseId} onClick={async () => { try { setSaved((await api.call(`/runs/${job.id}/case`, { id: caseId })).saved); } catch (e: any) { setSaved(e.message); } }}>Save as regression case</button>
            {saved && <span data-testid="saved">{saved}</span>}
          </div>
        </>
      )}
    </div>
  );
}

function DataView({ api, onUse }: { api: BenchClient; onUse(d: DataChoice): void }) {
  const library = useLoad<any[]>(() => api.call('/data/library'));
  const templates = useLoad<any>(() => api.call('/data/templates'));
  const [recipe, setRecipe] = useState<any>({ template: '', tenant: 'example-bank', lines: 2, seed: 1, valueDate: '2026-11-17', currency: 'AED', inject: [] as string[] });
  const [built, setBuilt] = useState<any>();
  const [text, setText] = useState('');
  const [validation, setValidation] = useState<any>();
  const [sampleId, setSampleId] = useState('');
  const [msg, setMsg] = useState<string>();
  const set = (k: string, v: any) => setRecipe({ ...recipe, [k]: v });
  // The first template the bench offers, until one is chosen: the console names no scheme of its own.
  useEffect(() => { if (templates?.templates?.length && !recipe.template) setRecipe((r: any) => ({ ...r, template: templates.templates[0] })); }, [templates]);
  const build = async () => {
    setMsg(undefined);
    try {
      const b = await api.call('/data/build', { ...recipe, lines: Number(recipe.lines), seed: Number(recipe.seed), ...(recipe.min ? { amounts: { min: Number(recipe.min), max: Number(recipe.max || recipe.min) } } : {}) });
      setBuilt(b); setText(b.text); setValidation(b.validation);
    } catch (e: any) { setMsg(e.message); }
  };
  const validate = async (rules: boolean) => {
    try { setValidation(await api.call('/data/validate', { data: { text, name: built?.name ?? 'edited' }, tenant: recipe.tenant, rules })); } catch (e: any) { setMsg(e.message); }
  };
  return (
    <div>
      <div className="card">
        <h2>Library</h2>
        <div className="table-wrap">
          <table className="grid" data-testid="library">
            <thead><tr><th>Sample</th><th>Scheme</th><th>Direction</th><th>Scenario</th><th>Expect</th><th>Bench</th><th>Profiles</th><th></th></tr></thead>
            <tbody>{library?.map((s) => (
              <tr key={s.id}><td title={s.title} className="mono">{s.id}</td><td>{s.scheme}</td><td>{s.direction}</td><td>{s.scenario}</td><td>{s.expect}</td><td>{s.bench}</td><td>{(s.profiles ?? []).join(', ')}</td>
                <td><button onClick={() => onUse({ mode: 'sample', sample: s.id })}>Use</button></td></tr>
            ))}</tbody>
          </table>
        </div>
      </div>
      <div className="card">
        <h2>Builder</h2>
        <div className="filters">
          <label>Template <select data-testid="template" value={recipe.template} onChange={(e) => set('template', e.target.value)}>{templates?.templates.map((t: string) => <option key={t}>{t}</option>)}</select></label>
          <label>Bank <select value={recipe.tenant} onChange={(e) => set('tenant', e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant} value={t.tenant}>{t.tenant} {t.routingCode ? `(${t.routingCode})` : '(no routing code)'}</option>)}</select></label>
          <label>Lines <input type="number" min={1} max={100000} value={recipe.lines} onChange={(e) => set('lines', e.target.value)} /></label>
          <label>Amounts from <input type="number" value={recipe.min ?? ''} placeholder="500" onChange={(e) => set('min', e.target.value)} /> to <input type="number" value={recipe.max ?? ''} placeholder="5000" onChange={(e) => set('max', e.target.value)} /></label>
          <label>Value date <input value={recipe.valueDate} onChange={(e) => set('valueDate', e.target.value)} /></label>
          <label>Currency <input value={recipe.currency} size={4} onChange={(e) => set('currency', e.target.value)} /></label>
          <label>Employer id <input value={recipe.employerId ?? ''} placeholder="generated" onChange={(e) => set('employerId', e.target.value || undefined)} /></label>
          <label>Seed <input type="number" value={recipe.seed} onChange={(e) => set('seed', e.target.value)} /></label>
        </div>
        <fieldset><legend>Inject an error on purpose</legend>
          {templates && Object.entries<string>(templates.injections).map(([k, v]) => (
            <label key={k} title={v}><input type="checkbox" checked={recipe.inject.includes(k)} onChange={() => set('inject', recipe.inject.includes(k) ? recipe.inject.filter((x: string) => x !== k) : [...recipe.inject, k])} /> {k}</label>
          ))}
        </fieldset>
        {/* Build waits for a template: before the bench's templates arrive the recipe names none, and the bench refuses it. */}
        <p><button className="primary" data-testid="build" disabled={!recipe.template} onClick={build}>Build</button>{built?.suggestedBench && <span className="muted"> use with the bench {built.suggestedBench}</span>}</p>
        <textarea data-testid="built" className="mono" rows={10} style={{ width: '100%' }} value={text} onChange={(e) => setText(e.target.value)} placeholder="The built data appears here; edit it as you like." />
        <div className="action-buttons">
          <button data-testid="validate" disabled={!text} onClick={() => validate(false)}>Validate the format</button>
          <button disabled={!text} onClick={() => validate(true)}>Validate with the scheme's rules (interpreter)</button>
          <button disabled={!text} data-testid="use-built" onClick={() => onUse({ mode: 'text', text, name: built?.name ?? 'built' })}>Use in a run</button>
          <input placeholder="sample id" value={sampleId} onChange={(e) => setSampleId(e.target.value)} />
          <button disabled={!text || !sampleId} onClick={async () => { try { setMsg((await api.call('/data/samples', { id: sampleId, text, name: built?.name ?? 'sample', tags: { scenario: recipe.inject.join(', ') || 'happy path', bench: built?.suggestedBench, build: recipe } })).saved); } catch (e: any) { setMsg(e.message); } }}>Save to the library</button>
        </div>
        {msg && <p data-testid="data-msg">{msg}</p>}
        {validation && (
          <div data-testid="validation">
            <p><span className={`badge ${validation.valid ? 'pass' : 'fail'}`}>{validation.valid ? 'Valid' : 'Not valid'}</span>{validation.rules ? ` the interpreter: ${validation.rules.outcome ?? validation.rules.error}` : ''}</p>
            <ul>{[...validation.format, ...validation.synthetic].map((f: any, i: number) => <li key={i}>{f.code} {f.where}: {f.message}</li>)}</ul>
          </div>
        )}
      </div>
    </div>
  );
}

function SuiteView({ api }: { api: BenchClient }) {
  const cases = useLoad<any[]>(() => api.call('/cases'));
  const [job, setJob] = useState<any>();
  const [dbs, setDbs] = useState('');
  const start = async (suite: string) => {
    const j = await api.call('/suites', { suite, ...(dbs ? { databases: [dbs] } : {}) });
    setJob(j);
    const tick = async () => { const s = await api.call(`/suites/${j.id}`); setJob(s); if (s.status !== 'done') setTimeout(tick, 1500); };
    tick();
  };
  return (
    <div>
      <div className="card">
        <h2>Regression cases</h2>
        <div className="table-wrap">
          <table className="grid"><thead><tr><th>Suite</th><th>Case</th><th>Profile</th><th>Expect</th><th>File</th></tr></thead>
            <tbody>{cases?.map((c) => <tr key={c.file}><td>{c.suite}</td><td title={c.title} className="mono">{c.id}</td><td>{c.profile}</td><td>{c.expect}</td><td className="mono">{c.file}</td></tr>)}</tbody></table>
        </div>
        <div className="action-buttons">
          <label>Database <select value={dbs} onChange={(e) => setDbs(e.target.value)}><option value="">as each case's profile</option>{DATABASES.map((d) => <option key={d}>{d}</option>)}</select></label>
          <button onClick={() => start('shipped')}>Run the shipped suite</button>
          <button onClick={() => start('saved')}>Run my saved cases</button>
        </div>
      </div>
      {job && (
        <div className="card"><h2>Suite <span className="mono">{job.id}</span>: {job.status === 'done' ? (job.result?.passed ? 'passed' : 'failed') : job.status}</h2>
          <ul>{(job.progress ?? []).filter((l: string) => l.startsWith('case ')).map((l: string, i: number) => <li key={i}>{l}</li>)}</ul>
        </div>
      )}
    </div>
  );
}

function HistoryView({ api, go }: { api: BenchClient; go(h: string): void }) {
  const runs = useLoad<any[]>(() => api.call('/runs'));
  return (
    <div className="card"><h2>History</h2>
      <div className="table-wrap">
        <table className="grid" data-testid="history"><thead><tr><th>Run</th><th>Profile</th><th>Input</th><th>By</th><th>Result</th></tr></thead>
          <tbody>{runs?.map((r) => (
            <tr key={r.id} className="clickable" onClick={() => go(`#/testbench/runs/${r.id}`)}>
              <td className="mono">{r.id}</td><td>{r.profile}</td><td>{r.input}</td><td>{r.actor ?? ''}</td>
              <td>{r.status !== 'done' ? r.status : <span className={`badge ${r.passed ? 'pass' : 'fail'}`}>{r.passed ? 'passed' : 'failed'}</span>}</td>
            </tr>
          ))}</tbody></table>
      </div>
    </div>
  );
}
