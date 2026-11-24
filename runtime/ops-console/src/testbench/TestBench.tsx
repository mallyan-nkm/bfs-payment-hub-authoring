import { useEffect, useMemo, useState } from 'react';
import type { BenchClient } from './api';
import { Button, Card, Checkbox, Input, Select, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';
import { Badge } from '../ui';

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
      <TabList className="tabs" aria-label="Test bench" selectedValue={page} onTabSelect={(_, d) => { const id = String(d.value); location.hash = `#/testbench${id ? '/' + id : ''}`; }}>
        {PAGES.map(([id, title]) => (
          <Tab key={id} value={id} data-testid={`nav-${id || 'run'}`}>{title}</Tab>
        ))}
      </TabList>
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
  // The default engine (R-09-79): Temporal, on the Temporal SQL Server plugin in a deployment; the others a tester may start.
  const health = useLoad<any>(() => fetch('/testbench/api/health').then((r) => r.json()));
  const defaults: string[] = health?.defaultEngines ?? ['temporal'];
  const [profile, setProfile] = useState('');
  const [engines, setEngines] = useState<string[]>();
  const [dbs, setDbs] = useState<string[]>();
  const [tenant, setTenant] = useState<string>();
  const [bench, setBench] = useState<string>();
  const [date, setDate] = useState<string>();
  const [job, setJob] = useState<any>();
  const [err, setErr] = useState<string>();
  const p = profiles?.find((x) => x.name === profile);

  useEffect(() => { if (profiles?.length && !profile) setProfile((profiles.find((x) => (x.engines ?? []).length === 0) ?? profiles[0]).name); }, [profiles]);
  useEffect(() => { setEngines(undefined); setDbs(undefined); setTenant(undefined); setBench(undefined); setDate(undefined); }, [profile]);
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

  // A profile that names no engines runs on the default engine (R-09-79), as the service resolves it.
  const eff = { engines: engines ?? p?.engines ?? defaults, dbs: dbs ?? p?.databases ?? [], tenant: tenant ?? p?.tenant, bench: bench ?? p?.bench, date: date ?? p?.clock?.date ?? '' };
  const toggle = (list: string[], x: string) => (list.includes(x) ? list.filter((y) => y !== x) : [...list, x]);
  const run = async () => {
    setErr(undefined);
    if (!data) return setErr('Choose the data first');
    const req: any = { profile, data: data.mode === 'sample' ? { sample: data.sample } : { text: data.text, name: data.name } };
    if (engines) req.engines = engines;
    if (dbs) req.databases = dbs;
    if (tenant) req.tenant = tenant;
    if (bench) req.bench = bench;
    // The business date (Issue I-331): the profile's by default; a tester may move it on or after the bank's values.
    if (date) req.clock = { ...(p?.clock ?? {}), date };
    try {
      const j = await api.call('/runs', req);
      setJob(j);
      go(`#/testbench/runs/${j.id}`);
    } catch (e: any) { setErr(e.message); }
  };

  return (
    <div>
      <Card className="card">
        <h2>1. Profile</h2>
        <Select data-testid="profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
          {profiles?.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
        </Select>
        <p className="muted">{p?.title}</p>
        <div className="filters">
          <fieldset><legend>Engines</legend>{ENGINES.map((e) => <Checkbox key={e} data-testid={`engine-${e}`} checked={eff.engines.includes(e)} onChange={() => setEngines(toggle(eff.engines, e))} label={<>{e}{defaults.includes(e) && <Badge className="badge" data-testid="default-engine"> default{e === 'temporal' && health?.temporal && health.temporal !== 'test-server' ? ` (${health.temporal})` : ''}</Badge>}</>} />)}<Checkbox checked disabled label={<>interpreter (reference)</>} /></fieldset>
          <fieldset><legend>Database</legend>{DATABASES.map((d) => <Checkbox key={d} checked={eff.dbs.includes(d)} onChange={() => setDbs(toggle(eff.dbs, d))} label={<>{d}</>} />)}</fieldset>
          <fieldset><legend>Tenant</legend><Select value={eff.tenant} onChange={(e) => setTenant(e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant}>{t.tenant}</option>)}</Select></fieldset>
          <fieldset><legend>Simulators</legend><Select data-testid="bench" value={eff.bench} onChange={(e) => setBench(e.target.value)}>{benches && Object.keys(benches).map((b) => <option key={b}>{b}</option>)}</Select>
            <ul className="muted">{benches?.[eff.bench]?.ports.map((x: string) => <li key={x} className="mono">{x}</li>)}</ul></fieldset>
          <fieldset><legend>Business date</legend><Input type="date" data-testid="business-date" value={eff.date} onChange={(e) => setDate(e.target.value || undefined)} />
            <p className="muted">The run starts at {p?.clock?.time ?? '06:00'} UTC that day; a bank's values apply from their own date.</p></fieldset>
        </div>
      </Card>
      <Card className="card">
        <h2>2. Data</h2>
        <div className="filters">
          <label>Library sample{' '}
            <Select data-testid="sample" value={data?.mode === 'sample' ? data.sample : ''} onChange={(e) => e.target.value && setData({ mode: 'sample', sample: e.target.value })}>
              <option value="">choose…</option>
              {library?.map((s) => <option key={s.id} value={s.id}>{s.id}: {s.title} (expect {s.expect})</option>)}
            </Select>
          </label>
          <label>Upload a file <input type="file" data-testid="upload" onChange={async (e) => { const f = e.target.files?.[0]; if (f) setData({ mode: 'text', text: await f.text(), name: f.name }); }} /></label>
        </div>
        <Textarea data-testid="paste" className="mono" rows={5} style={{ width: '100%' }} placeholder="…or paste a scheme file, an ISO 20022 message or a canonical JSON payment"
          value={data?.mode === 'text' ? data.text : ''} onChange={(e) => setData({ mode: 'text', text: e.target.value, name: 'pasted' })} />
        {data && <p className="muted">Data: {data.mode === 'sample' ? `the sample ${data.sample}` : `${data.name}, ${data.text.length} characters`}</p>}
      </Card>
      <p>
        <Button appearance="primary" data-testid="run" onClick={run} disabled={!data || !profile || (job && job.status !== 'done')}>Run</Button>
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
    <Card className="card" data-testid="result">
      <h2>Run <span className="mono">{job.id}</span> <Badge data-testid="status" className={`badge ${job.status === 'done' ? (passed ? 'pass' : 'fail') : 'warn'}`}>{job.status === 'done' ? (passed ? 'passed' : 'failed') : job.status}</Badge></h2>
      {job.status !== 'done' && <ul className="summary" data-testid="progress">{Object.entries(states).map(([k, v]) => <li key={k}><b>{k}</b> {v}</li>)}</ul>}
      {r && (
        <>
          <div className="table-wrap">
            <Table size="small" className="grid" data-testid="summary">
              <TableHeader><TableRow><TableHeaderCell>Run</TableHeaderCell><TableHeaderCell>Outcome</TableHeaderCell><TableHeaderCell>Same as the interpreter</TableHeaderCell><TableHeaderCell>Time</TableHeaderCell><TableHeaderCell>Store</TableHeaderCell></TableRow></TableHeader>
              <TableBody>
                {columns.map((c) => (
                  <TableRow key={c.label} data-testid={`row-${c.label.split(' ')[0]}`}>
                    <TableCell>{c.label}</TableCell>
                    <TableCell>{c.run.error ? <span className="error">error: {String(c.run.error).split('\n')[0]}</span> : c.run.outcome}{c.run.retried && <span className="muted" title={c.run.retried}> (run twice, I-93)</span>}</TableCell>
                    <TableCell>{c.run.comparison ? (c.run.comparison.same ? <Badge className="badge pass">yes</Badge> : <Badge className="badge fail">no</Badge>) : ''}</TableCell>
                    <TableCell>{ms(c.run.timings?.totalMs ?? c.run.elapsedMs)}</TableCell>
                    <TableCell>{store(c.run)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
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
            <Button onClick={() => api.openReport(job.id)}>Open the report</Button>
            <Input placeholder="case id, e.g. TB-BANK-001" value={caseId} onChange={(e) => setCaseId(e.target.value)} data-testid="case-id" />
            <Button data-testid="save-case" disabled={!caseId} onClick={async () => { try { setSaved((await api.call(`/runs/${job.id}/case`, { id: caseId })).saved); } catch (e: any) { setSaved(e.message); } }}>Save as regression case</Button>
            {saved && <span data-testid="saved">{saved}</span>}
          </div>
        </>
      )}
    </Card>
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
      <Card className="card">
        <h2>Library</h2>
        <div className="table-wrap">
          <Table size="small" className="grid" data-testid="library">
            <TableHeader><TableRow><TableHeaderCell>Sample</TableHeaderCell><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Direction</TableHeaderCell><TableHeaderCell>Scenario</TableHeaderCell><TableHeaderCell>Expect</TableHeaderCell><TableHeaderCell>Bench</TableHeaderCell><TableHeaderCell>Profiles</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
            <TableBody>{library?.map((s) => (
              <TableRow key={s.id}><TableCell title={s.title} className="mono">{s.id}</TableCell><TableCell>{s.scheme}</TableCell><TableCell>{s.direction}</TableCell><TableCell>{s.scenario}</TableCell><TableCell>{s.expect}</TableCell><TableCell>{s.bench}</TableCell><TableCell>{(s.profiles ?? []).join(', ')}</TableCell>
                <TableCell><Button onClick={() => onUse({ mode: 'sample', sample: s.id })}>Use</Button></TableCell></TableRow>
            ))}</TableBody>
          </Table>
        </div>
      </Card>
      <Card className="card">
        <h2>Builder</h2>
        <div className="filters">
          <label>Template <Select data-testid="template" value={recipe.template} onChange={(e) => set('template', e.target.value)}>{templates?.templates.map((t: string) => <option key={t}>{t}</option>)}</Select></label>
          <label>Bank <Select value={recipe.tenant} onChange={(e) => set('tenant', e.target.value)}>{templates?.tenants.map((t: any) => <option key={t.tenant} value={t.tenant}>{t.tenant} {t.routingCode ? `(${t.routingCode})` : '(no routing code)'}</option>)}</Select></label>
          <label>Lines <Input type="number" min={1} max={100000} value={recipe.lines} onChange={(e) => set('lines', e.target.value)} /></label>
          <label>Amounts from <Input type="number" value={recipe.min ?? ''} placeholder="500" onChange={(e) => set('min', e.target.value)} /> to <Input type="number" value={recipe.max ?? ''} placeholder="5000" onChange={(e) => set('max', e.target.value)} /></label>
          <label>Value date <Input value={recipe.valueDate} onChange={(e) => set('valueDate', e.target.value)} /></label>
          <label>Currency <Input style={{ width: '7ch' }} value={recipe.currency} onChange={(e) => set('currency', e.target.value)} /></label>
          <label>Employer id <Input value={recipe.employerId ?? ''} placeholder="generated" onChange={(e) => set('employerId', e.target.value || undefined)} /></label>
          <label>Seed <Input type="number" value={recipe.seed} onChange={(e) => set('seed', e.target.value)} /></label>
        </div>
        <fieldset><legend>Inject an error on purpose</legend>
          {templates && Object.entries<string>(templates.injections).map(([k, v]) => (
            <Checkbox key={k} title={v} checked={recipe.inject.includes(k)} onChange={() => set('inject', recipe.inject.includes(k) ? recipe.inject.filter((x: string) => x !== k) : [...recipe.inject, k])} label={<>{k}</>} />
          ))}
        </fieldset>
        {/* Build waits for a template: before the bench's templates arrive the recipe names none, and the bench refuses it. */}
        <p><Button appearance="primary" data-testid="build" disabled={!recipe.template} onClick={build}>Build</Button>{built?.suggestedBench && <span className="muted"> use with the bench {built.suggestedBench}</span>}</p>
        <Textarea data-testid="built" className="mono" rows={10} style={{ width: '100%' }} value={text} onChange={(e) => setText(e.target.value)} placeholder="The built data appears here; edit it as you like." />
        <div className="action-buttons">
          <Button data-testid="validate" disabled={!text} onClick={() => validate(false)}>Validate the format</Button>
          <Button disabled={!text} onClick={() => validate(true)}>Validate with the scheme's rules (interpreter)</Button>
          <Button disabled={!text} data-testid="use-built" onClick={() => onUse({ mode: 'text', text, name: built?.name ?? 'built' })}>Use in a run</Button>
          <Input placeholder="sample id" value={sampleId} onChange={(e) => setSampleId(e.target.value)} />
          <Button disabled={!text || !sampleId} onClick={async () => { try { setMsg((await api.call('/data/samples', { id: sampleId, text, name: built?.name ?? 'sample', tags: { scenario: recipe.inject.join(', ') || 'happy path', bench: built?.suggestedBench, build: recipe } })).saved); } catch (e: any) { setMsg(e.message); } }}>Save to the library</Button>
        </div>
        {msg && <p data-testid="data-msg">{msg}</p>}
        {validation && (
          <div data-testid="validation">
            <p><Badge className={`badge ${validation.valid ? 'pass' : 'fail'}`}>{validation.valid ? 'Valid' : 'Not valid'}</Badge>{validation.rules ? ` the interpreter: ${validation.rules.outcome ?? validation.rules.error}` : ''}</p>
            <ul>{[...validation.format, ...validation.synthetic].map((f: any, i: number) => <li key={i}>{f.code} {f.where}: {f.message}</li>)}</ul>
          </div>
        )}
      </Card>
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
      <Card className="card">
        <h2>Regression cases</h2>
        <div className="table-wrap">
          <Table size="small" className="grid"><TableHeader><TableRow><TableHeaderCell>Suite</TableHeaderCell><TableHeaderCell>Case</TableHeaderCell><TableHeaderCell>Profile</TableHeaderCell><TableHeaderCell>Expect</TableHeaderCell><TableHeaderCell>File</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{cases?.map((c) => <TableRow key={c.file}><TableCell>{c.suite}</TableCell><TableCell title={c.title} className="mono">{c.id}</TableCell><TableCell>{c.profile}</TableCell><TableCell>{c.expect}</TableCell><TableCell className="mono">{c.file}</TableCell></TableRow>)}</TableBody></Table>
        </div>
        <div className="action-buttons">
          <label>Database <Select value={dbs} onChange={(e) => setDbs(e.target.value)}><option value="">as each case's profile</option>{DATABASES.map((d) => <option key={d}>{d}</option>)}</Select></label>
          <Button onClick={() => start('shipped')}>Run the shipped suite</Button>
          <Button onClick={() => start('saved')}>Run my saved cases</Button>
        </div>
      </Card>
      {job && (
        <Card className="card"><h2>Suite <span className="mono">{job.id}</span>: {job.status === 'done' ? (job.result?.passed ? 'passed' : 'failed') : job.status}</h2>
          <ul>{(job.progress ?? []).filter((l: string) => l.startsWith('case ')).map((l: string, i: number) => <li key={i}>{l}</li>)}</ul>
        </Card>
      )}
    </div>
  );
}

function HistoryView({ api, go }: { api: BenchClient; go(h: string): void }) {
  const runs = useLoad<any[]>(() => api.call('/runs'));
  return (
    <Card className="card"><h2>History</h2>
      <div className="table-wrap">
        <Table size="small" className="grid" data-testid="history"><TableHeader><TableRow><TableHeaderCell>Run</TableHeaderCell><TableHeaderCell>Profile</TableHeaderCell><TableHeaderCell>Input</TableHeaderCell><TableHeaderCell>By</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell></TableRow></TableHeader>
          <TableBody>{runs?.map((r) => (
            <TableRow key={r.id} className="clickable" onClick={() => go(`#/testbench/runs/${r.id}`)}>
              <TableCell className="mono">{r.id}</TableCell><TableCell>{r.profile}</TableCell><TableCell>{r.input}</TableCell><TableCell>{r.actor ?? ''}</TableCell>
              <TableCell>{r.status !== 'done' ? r.status : <Badge className={`badge ${r.passed ? 'pass' : 'fail'}`}>{r.passed ? 'passed' : 'failed'}</Badge>}</TableCell>
            </TableRow>
          ))}</TableBody></Table>
      </div>
    </Card>
  );
}
