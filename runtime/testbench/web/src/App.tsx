import { useEffect, useMemo, useState } from 'react';
import { api, ApiError, openReport } from './api';
import { completeSignIn, config, signIn, signOut, token, type AuthConfig } from './auth';

type View = 'run' | 'data' | 'suite' | 'history';
type DataChoice = { mode: 'sample'; sample: string } | { mode: 'text'; text: string; name: string };
const ENGINES = ['temporal', 'dtfx', 'elsa', 'flowable'];
const DATABASES = ['PostgreSql', 'SqlServer', 'none'];

export function App() {
  const [cfg, setCfg] = useState<AuthConfig>();
  const [me, setMe] = useState<any>();
  const [error, setError] = useState<string>();
  const [view, setView] = useState<View>('run');
  const [data, setData] = useState<DataChoice>();
  const [shown, setShown] = useState<string>();

  useEffect(() => {
    (async () => {
      try {
        const c = await config();
        setCfg(c);
        await completeSignIn(c);
        if (token()) setMe(await api('/me'));
      } catch (e: any) {
        setError(e instanceof ApiError && e.status === 401 ? undefined : e.message);
        if (e instanceof ApiError && e.status === 401) signOut();
      }
    })();
  }, []);

  if (!cfg) return <main className="page"><p>{error ?? 'Loading…'}</p></main>;
  if (!me)
    return (
      <main className="page signin">
        <h1>Runtime test bench</h1>
        <p>Run data through the workflow engines and compare each with the studio interpreter.</p>
        {error && <p className="bad" data-testid="error">{error}</p>}
        <button data-testid="sign-in" onClick={() => signIn(cfg)}>Sign in{cfg.mode === 'dev' ? ' (developer identity provider)' : ' with your bank account'}</button>
        <p className="muted">Signed in with the bank's identity provider; your roles come from the bank's claims. The Tester role is needed.</p>
      </main>
    );

  return (
    <div className="page">
      <header>
        <h1>Runtime test bench</h1>
        <nav>
          {(['run', 'data', 'suite', 'history'] as View[]).map((v) => (
            <button key={v} className={view === v ? 'on' : ''} data-testid={`nav-${v}`} onClick={() => setView(v)}>
              {{ run: 'Run', data: 'Test data', suite: 'Suite', history: 'History' }[v]}
            </button>
          ))}
        </nav>
        <span className="who" data-testid="who">{me.name} · {me.roles.join(', ')}{cfg.mode === 'dev' ? ' · dev sign-in' : ''}</span>
        <button onClick={() => { signOut(); location.reload(); }}>Sign out</button>
      </header>
      {view === 'run' && <RunView data={data} setData={setData} shown={shown} setShown={setShown} />}
      {view === 'data' && <DataView onUse={(d) => { setData(d); setView('run'); }} />}
      {view === 'suite' && <SuiteView />}
      {view === 'history' && <HistoryView onOpen={(id) => { setShown(id); setView('run'); }} />}
    </div>
  );
}

function useLoad<T>(path: string, deps: any[] = []): [T | undefined, string | undefined] {
  const [v, setV] = useState<T>();
  const [e, setE] = useState<string>();
  useEffect(() => { api<T>(path).then(setV, (x) => setE(x.message)); }, deps);
  return [v, e];
}

function RunView({ data, setData, shown, setShown }: { data?: DataChoice; setData: (d: DataChoice) => void; shown?: string; setShown: (id?: string) => void }) {
  const [profiles] = useLoad<any[]>('/profiles');
  const [benches] = useLoad<Record<string, any>>('/benches');
  const [library] = useLoad<any[]>('/data/library');
  const [templates] = useLoad<any>('/data/templates');
  const [profile, setProfile] = useState<string>('');
  const [engines, setEngines] = useState<string[]>();
  const [dbs, setDbs] = useState<string[]>();
  const [tenant, setTenant] = useState<string>();
  const [bench, setBench] = useState<string>();
  const [job, setJob] = useState<any>();
  const [err, setErr] = useState<string>();
  const p = profiles?.find((x) => x.name === profile);

  useEffect(() => { if (profiles?.length && !profile) setProfile(profiles.find((x) => x.name === 'wps-reference')?.name ?? profiles[0].name); }, [profiles]);
  useEffect(() => { setEngines(undefined); setDbs(undefined); setTenant(undefined); setBench(undefined); }, [profile]);
  useEffect(() => {
    if (!shown) return;
    let stop = false;
    const tick = async () => {
      try {
        const j = await api(`/runs/${shown}`);
        if (stop) return;
        setJob(j);
        if (j.status !== 'done') setTimeout(tick, 1000);
      } catch (e: any) { setErr(e.message); }
    };
    tick();
    return () => { stop = true; };
  }, [shown]);

  const run = async () => {
    setErr(undefined);
    if (!data) return setErr('Choose the data first');
    const req: any = { profile, data: data.mode === 'sample' ? { sample: data.sample } : { text: data.text, name: data.name } };
    if (engines) req.engines = engines;
    if (dbs) req.databases = dbs;
    if (tenant) req.tenant = tenant;
    if (bench) req.bench = bench;
    try {
      const j = await api('/runs', req);
      setJob(j);
      setShown(j.id);
    } catch (e: any) { setErr(e.message); }
  };

  const eff = { engines: engines ?? p?.engines ?? [], dbs: dbs ?? p?.databases ?? [], tenant: tenant ?? p?.tenant, bench: bench ?? p?.bench };
  const toggle = (list: string[], x: string) => (list.includes(x) ? list.filter((y) => y !== x) : [...list, x]);

  return (
    <main>
      <section className="card">
        <h2>1. Profile</h2>
        <select data-testid="profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
          {profiles?.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
        </select>
        <p className="muted">{p?.title}</p>
        <div className="row">
          <fieldset><legend>Engines</legend>{ENGINES.map((e) => <label key={e}><input type="checkbox" checked={eff.engines.includes(e)} onChange={() => setEngines(toggle(eff.engines, e))} /> {e}</label>)}<label><input type="checkbox" checked disabled /> interpreter (reference)</label></fieldset>
          <fieldset><legend>Database</legend>{DATABASES.map((d) => <label key={d}><input type="checkbox" checked={eff.dbs.includes(d)} onChange={() => setDbs(toggle(eff.dbs, d))} /> {d}</label>)}</fieldset>
          <fieldset><legend>Tenant</legend><select value={eff.tenant} onChange={(e) => setTenant(e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant}>{t.tenant}</option>)}</select></fieldset>
          <fieldset><legend>Simulators</legend><select data-testid="bench" value={eff.bench} onChange={(e) => setBench(e.target.value)}>{benches && Object.keys(benches).map((b) => <option key={b}>{b}</option>)}</select>
            <ul className="ports">{benches?.[eff.bench]?.ports.map((x: string) => <li key={x}>{x}</li>)}</ul></fieldset>
        </div>
      </section>
      <section className="card">
        <h2>2. Data</h2>
        <div className="row">
          <label>Library sample{' '}
            <select data-testid="sample" value={data?.mode === 'sample' ? data.sample : ''} onChange={(e) => e.target.value && setData({ mode: 'sample', sample: e.target.value })}>
              <option value="">choose…</option>
              {library?.map((s) => <option key={s.id} value={s.id}>{s.id}: {s.title} (expect {s.expect})</option>)}
            </select>
          </label>
          <label>Upload a file <input type="file" data-testid="upload" onChange={async (e) => { const f = e.target.files?.[0]; if (f) setData({ mode: 'text', text: await f.text(), name: f.name }); }} /></label>
        </div>
        <textarea data-testid="paste" placeholder="…or paste a WPS SIF or PIF, an ISO 20022 pain.001 or pacs.008, or a canonical JSON payment" rows={5}
          value={data?.mode === 'text' ? data.text : ''} onChange={(e) => setData({ mode: 'text', text: e.target.value, name: 'pasted' })} />
        {data && <p className="muted">Data: {data.mode === 'sample' ? `the sample ${data.sample}` : `${data.name}, ${data.text.length} characters`}</p>}
      </section>
      <section className="card">
        <button className="primary" data-testid="run" onClick={run} disabled={!data || !profile || (job && job.status !== 'done')}>Run</button>
        {err && <span className="bad" data-testid="run-error"> {err}</span>}
      </section>
      {job && <Result job={job} />}
    </main>
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

function Result({ job }: { job: any }) {
  const r = job.result;
  const [caseId, setCaseId] = useState('');
  const [saved, setSaved] = useState<string>();
  const states = engineStates(job.progress ?? [], r?.plan?.engines ?? []);
  const columns = useMemo(() => (r ? [...(r.reference ? [{ label: 'interpreter (reference)', run: r.reference }] : []), ...(r.runs ?? []).map((x: any) => ({ label: `${x.engine} · ${x.database}`, run: x }))] : []), [r]);
  return (
    <section className="card" data-testid="result">
      <h2>Run {job.id} <span data-testid="status" className={job.status === 'done' ? (job.passed ?? r?.passed ? 'ok' : 'bad') : ''}>{job.status === 'done' ? (r?.passed ? 'passed' : 'failed') : job.status}</span></h2>
      {job.status !== 'done' && (
        <ul className="progress" data-testid="progress">{Object.entries(states).map(([k, v]) => <li key={k}><b>{k}</b> {v}</li>)}</ul>
      )}
      {r && (
        <>
          <table data-testid="summary">
            <thead><tr><th>Run</th><th>Outcome</th><th>Same as the interpreter</th><th>Time</th><th>Store</th></tr></thead>
            <tbody>
              {columns.map((c) => (
                <tr key={c.label} data-testid={`row-${c.label.split(' ')[0]}`}>
                  <td>{c.label}</td>
                  <td>{c.run.error ? <span className="bad">error: {String(c.run.error).split('\n')[0]}</span> : c.run.outcome}</td>
                  <td>{c.run.comparison ? (c.run.comparison.same ? <span className="ok">yes</span> : <span className="bad">no</span>) : ''}</td>
                  <td>{ms(c.run.timings?.totalMs ?? c.run.elapsedMs)}</td>
                  <td>{store(c.run)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="traces">
            {columns.map((c) => {
              const diff: string[] = c.run.comparison?.trace?.diff ?? [];
              return (
                <div key={c.label}><b>{c.label}</b>{diff.length > 0 && <span className="bad"> diff</span>}
                  <pre data-testid={`trace-${c.label.split(' ')[0]}`}>{(diff.length ? diff : c.run.trace ?? []).join('\n')}</pre>
                </div>
              );
            })}
          </div>
          {(r.failures ?? []).length > 0 && <ul className="bad" data-testid="failures">{r.failures.map((f: string) => <li key={f}>{f}</li>)}</ul>}
          <div className="row">
            <button onClick={() => openReport(job.id)}>Open the report</button>
            <input placeholder="case id, e.g. TB-BANK-001" value={caseId} onChange={(e) => setCaseId(e.target.value)} data-testid="case-id" />
            <button data-testid="save-case" disabled={!caseId} onClick={async () => { try { setSaved((await api(`/runs/${job.id}/case`, { id: caseId })).saved); } catch (e: any) { setSaved(e.message); } }}>Save as regression case</button>
            {saved && <span data-testid="saved">{saved}</span>}
          </div>
        </>
      )}
    </section>
  );
}

const ms = (n?: number) => (n === undefined ? '' : `${(n / 1000).toFixed(1)} s`);
function store(run: any) {
  const s = run.store;
  if (!s) return run.database === 'none' ? 'no store' : '';
  const rows = s.rows ?? {};
  return `${s.provider}: ${rows.bulks?.length ?? 0} bulk, ${rows.payments?.length ?? 0} payment(s), ${s.payloads?.length ?? 0} payload(s) ${s.ok ? 'rebuilt' : 'NOT rebuilt'}, chains ${rows.chains?.verified ?? 0}/${rows.chains?.subjects ?? 0}`;
}

function DataView({ onUse }: { onUse: (d: DataChoice) => void }) {
  const [library] = useLoad<any[]>('/data/library');
  const [templates] = useLoad<any>('/data/templates');
  const [recipe, setRecipe] = useState<any>({ template: 'wps-sif', tenant: 'example-bank', lines: 2, seed: 1, valueDate: '2026-11-17', currency: 'AED', inject: [] as string[] });
  const [built, setBuilt] = useState<any>();
  const [text, setText] = useState('');
  const [validation, setValidation] = useState<any>();
  const [sampleId, setSampleId] = useState('');
  const [msg, setMsg] = useState<string>();
  const set = (k: string, v: any) => setRecipe({ ...recipe, [k]: v });

  const build = async () => {
    setMsg(undefined);
    try {
      const b = await api('/data/build', { ...recipe, lines: Number(recipe.lines), seed: Number(recipe.seed), ...(recipe.min ? { amounts: { min: Number(recipe.min), max: Number(recipe.max || recipe.min) } } : {}) });
      setBuilt(b); setText(b.text); setValidation(b.validation);
    } catch (e: any) { setMsg(e.message); }
  };
  const validate = async (rules: boolean) => {
    try { setValidation(await api('/data/validate', { data: { text, name: built?.name ?? 'edited' }, tenant: recipe.tenant, rules })); } catch (e: any) { setMsg(e.message); }
  };
  return (
    <main>
      <section className="card">
        <h2>Library</h2>
        <table data-testid="library">
          <thead><tr><th>Sample</th><th>Scheme</th><th>Direction</th><th>Scenario</th><th>Expect</th><th>Bench</th><th>Profiles</th><th></th></tr></thead>
          <tbody>{library?.map((s) => (
            <tr key={s.id}><td title={s.title}>{s.id}</td><td>{s.scheme}</td><td>{s.direction}</td><td>{s.scenario}</td><td>{s.expect}</td><td>{s.bench}</td><td>{(s.profiles ?? []).join(', ')}</td>
              <td><button onClick={() => onUse({ mode: 'sample', sample: s.id })}>Use</button></td></tr>
          ))}</tbody>
        </table>
      </section>
      <section className="card">
        <h2>Builder</h2>
        <div className="row">
          <label>Template <select data-testid="template" value={recipe.template} onChange={(e) => set('template', e.target.value)}>{templates?.templates.map((t: string) => <option key={t}>{t}</option>)}</select></label>
          <label>Bank <select value={recipe.tenant} onChange={(e) => set('tenant', e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant} value={t.tenant}>{t.tenant} {t.routingCode ? `(${t.routingCode})` : '(no WPS routing code)'}</option>)}</select></label>
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
        <button className="primary" data-testid="build" onClick={build}>Build</button>
        {built?.suggestedBench && <span className="muted"> use with the bench {built.suggestedBench}</span>}
        <textarea data-testid="built" rows={10} value={text} onChange={(e) => setText(e.target.value)} placeholder="The built data appears here; edit it as you like." />
        <div className="row">
          <button data-testid="validate" disabled={!text} onClick={() => validate(false)}>Validate the format</button>
          <button disabled={!text} onClick={() => validate(true)}>Validate with the scheme's rules (interpreter)</button>
          <button disabled={!text} data-testid="use-built" onClick={() => onUse({ mode: 'text', text, name: built?.name ?? 'built' })}>Use in a run</button>
          <input placeholder="sample id" value={sampleId} onChange={(e) => setSampleId(e.target.value)} />
          <button disabled={!text || !sampleId} onClick={async () => { try { setMsg((await api('/data/samples', { id: sampleId, text, name: built?.name ?? 'sample', tags: { scenario: recipe.inject.join(', ') || 'happy path', bench: built?.suggestedBench, build: recipe } })).saved); } catch (e: any) { setMsg(e.message); } }}>Save to the library</button>
        </div>
        {msg && <p data-testid="data-msg">{msg}</p>}
        {validation && (
          <div data-testid="validation">
            <p className={validation.valid ? 'ok' : 'bad'}>{validation.valid ? 'Valid' : 'Not valid'}{validation.rules ? `; the interpreter: ${validation.rules.outcome ?? validation.rules.error}` : ''}</p>
            <ul>{[...validation.format, ...validation.synthetic].map((f: any, i: number) => <li key={i}>{f.code} {f.where}: {f.message}</li>)}</ul>
          </div>
        )}
      </section>
    </main>
  );
}

function SuiteView() {
  const [cases] = useLoad<any[]>('/cases');
  const [job, setJob] = useState<any>();
  const [dbs, setDbs] = useState<string>('');
  const start = async (suite: string) => {
    const j = await api('/suites', { suite, ...(dbs ? { databases: [dbs] } : {}) });
    setJob(j);
    const tick = async () => { const s = await api(`/suites/${j.id}`); setJob(s); if (s.status !== 'done') setTimeout(tick, 1500); };
    tick();
  };
  return (
    <main>
      <section className="card">
        <h2>Regression cases</h2>
        <table><thead><tr><th>Suite</th><th>Case</th><th>Profile</th><th>Expect</th><th>File</th></tr></thead>
          <tbody>{cases?.map((c) => <tr key={c.file}><td>{c.suite}</td><td title={c.title}>{c.id}</td><td>{c.profile}</td><td>{c.expect}</td><td>{c.file}</td></tr>)}</tbody></table>
        <div className="row">
          <label>Database <select value={dbs} onChange={(e) => setDbs(e.target.value)}><option value="">as each case's profile</option>{DATABASES.map((d) => <option key={d}>{d}</option>)}</select></label>
          <button onClick={() => start('shipped')}>Run the shipped suite</button>
          <button onClick={() => start('saved')}>Run my saved cases</button>
        </div>
      </section>
      {job && (
        <section className="card"><h2>Suite {job.id}: {job.status === 'done' ? (job.result?.passed ? 'passed' : 'failed') : job.status}</h2>
          <ul className="progress">{(job.progress ?? []).filter((l: string) => l.startsWith('case ')).map((l: string, i: number) => <li key={i}>{l}</li>)}</ul>
        </section>
      )}
    </main>
  );
}

function HistoryView({ onOpen }: { onOpen: (id: string) => void }) {
  const [runs] = useLoad<any[]>('/runs');
  return (
    <main><section className="card"><h2>History</h2>
      <table data-testid="history"><thead><tr><th>Run</th><th>Profile</th><th>Input</th><th>By</th><th>Result</th></tr></thead>
        <tbody>{runs?.map((r) => <tr key={r.id}><td><a href="#" onClick={(e) => { e.preventDefault(); onOpen(r.id); }}>{r.id}</a></td><td>{r.profile}</td><td>{r.input}</td><td>{r.actor ?? ''}</td><td>{r.status !== 'done' ? r.status : r.passed ? <span className="ok">passed</span> : <span className="bad">failed</span>}</td></tr>)}</tbody></table>
    </section></main>
  );
}
