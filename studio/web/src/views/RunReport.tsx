import { Fragment, useMemo, useState, type ReactNode } from 'react';
import type { InstanceView, TestRunReport } from '../api';
import { ArtefactLink, Badge, Json, KV, List, PassFail, Section, Tabs, Val, fmtMs } from '../ui';

// A run report (F-17, F-19): checks, business trace, timeline on the virtual clock, instance tree,
// per-step context deltas, rule traces, messages and the bench.

function typeTone(t: string): 'pass' | 'fail' | 'warn' | 'info' | 'muted' | 'prov' {
  if (/fail|error|timeout|transient|malformed|cancel/.test(t)) return 'fail';
  if (/retry|wait|unmatched|duplicate|hold|pause/.test(t)) return 'warn';
  if (/outcome|end|reply|resume/.test(t)) return 'pass';
  if (/attempt|start|message|signal|await|deliver/.test(t)) return 'info';
  return 'muted';
}

function detailText(d: any): string {
  if (d === undefined || d === null) return '';
  if (typeof d !== 'object') return String(d);
  return Object.entries(d)
    .filter(([k, v]) => k !== 'policy' && v !== '' && v !== undefined && v !== null)
    .map(([k, v]) => `${k} ${typeof v === 'object' ? JSON.stringify(v) : String(v)}`)
    .join(', ');
}

/** The detail entries that belong to one business trace entry: those under its step id. */
function detailOf(report: TestRunReport, entry: string): string[] {
  const step = entry.slice(0, entry.lastIndexOf(':'));
  return (report.detail ?? []).filter((d) => d.startsWith(`${step}.`));
}

export function RunReport({ report, footer }: { report: TestRunReport; footer?: ReactNode }) {
  const rec = report.record;
  const [tab, setTab] = useState<'timeline' | 'instances' | 'messages' | 'bench'>('timeline');
  const failed = report.checks.filter((c) => !c.passed).length;
  return (
    <div className="report">
      <div className={`report-head ${report.passed ? 'pass' : 'fail'}`}>
        <div>
          <h3 className="mono">{report.id}</h3>
          {report.title && <div>{report.title}</div>}
          <div className="small">target <ArtefactLink name={report.target} />, level {report.level}</div>
        </div>
        <div className="report-stats">
          <PassFail passed={report.passed} />
          {report.outcome && <span>outcome <b className="mono">{report.outcome}</b></span>}
          <span>virtual time <b className="mono">{report.elapsed}</b></span>
          {rec && <span>run <b>{rec.status}</b></span>}
          {rec && <span className="muted small">clock starts {rec.startedAt}</span>}
        </div>
      </div>
      {report.error && <div className="alert fail"><b>The run failed.</b><pre className="code small">{report.error}</pre></div>}
      {report.warnings?.length > 0 && <div className="alert warn"><b>Warnings</b><List items={report.warnings} /></div>}

      <div className="cols">
        <div className="col">
          <Section title="Checks" count={report.checks.length} actions={report.checks.length ? <Badge tone={failed ? 'fail' : 'pass'}>{report.checks.length - failed} of {report.checks.length} pass</Badge> : undefined}>
            {report.checks.length === 0 ? <p className="muted">No expectation was set, so nothing was checked.</p> : (
              <table className="grid compact">
                <thead><tr><th>Check</th><th>Expected</th><th>Actual</th><th>Result</th></tr></thead>
                <tbody>
                  {report.checks.map((c, i) => (
                    <tr key={i} className={c.passed ? '' : 'row-fail'}>
                      <td>{c.what}{c.note && <div className="muted small">{c.note}</div>}</td>
                      <td><Val v={c.expected} /></td>
                      <td><Val v={c.actual} /></td>
                      <td><PassFail passed={c.passed} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Section>
        </div>
        <div className="col side">
          <div className="trace-pair">
            <Section title="Business trace" count={report.trace.length}>
              <p className="muted small">One entry per authored step.</p>
              <ol className="trace">
                {report.trace.map((t, i) => <li key={i} className={detailOf(report, t).length ? 'has-detail' : ''}><code>{t}</code>{detailOf(report, t).length > 0 && <span className="muted small"> {detailOf(report, t).length} inside</span>}</li>)}
              </ol>
            </Section>
            <Section title="Detail" count={report.detail?.length ?? 0}>
              <p className="muted small">The steps inside composite steps.</p>
              {report.detail?.length ? (
                <ol className="trace">{report.detail.map((t, i) => <li key={i}><code>{t}</code></li>)}</ol>
              ) : <p className="muted small">No composite step ran.</p>}
            </Section>
          </div>
          {report.releaseTrace.length > 0 && (
            <details className="panel pad">
              <summary className="small">Release trace ({report.releaseTrace.length})</summary>
              <ol className="trace">{report.releaseTrace.map((t, i) => <li key={i}><code>{t}</code></li>)}</ol>
            </details>
          )}
        </div>
      </div>

      {rec && (
        <>
          <Tabs
            tabs={[
              { id: 'timeline', label: `Timeline (${rec.timeline.length})` },
              { id: 'instances', label: `Instances (${rec.instances.length})` },
              { id: 'messages', label: `Messages (${rec.messages.length})` },
              { id: 'bench', label: 'Bench and bindings' },
            ]}
            value={tab}
            onChange={setTab}
          />
          {tab === 'timeline' && <Timeline timeline={rec.timeline} elapsedMs={rec.elapsedMs} />}
          {tab === 'instances' && <Instances instances={rec.instances} />}
          {tab === 'messages' && <Messages rec={rec} />}
          {tab === 'bench' && <Bench rec={rec} />}
        </>
      )}
      {footer}
    </div>
  );
}

interface Group { head: any; inner: any[]; end?: any }

/** Group the timeline: the events of the steps inside a composite sit under its block-start. */
function groupTimeline(rows: any[]): (any | Group)[] {
  const out: (any | Group)[] = [];
  const open: Group[] = [];
  for (const t of rows) {
    const g = open[open.length - 1];
    if (t.type === 'block-start') {
      const ng: Group = { head: t, inner: [] };
      (g ? g.inner : out).push(ng);
      open.push(ng);
      continue;
    }
    if (g && t.type === 'block-end' && t.step === g.head.step && t.instance === g.head.instance) {
      g.end = t;
      open.pop();
      continue;
    }
    if (g && t.instance === g.head.instance && String(t.step ?? '').startsWith(`${g.head.step}.`)) {
      g.inner.push(t);
      continue;
    }
    // An event outside the open block closes it (a block that did not end in this window).
    while (open.length && !(t.instance === open[open.length - 1].head.instance && String(t.step ?? '').startsWith(`${open[open.length - 1].head.step}.`))) open.pop();
    (open.length ? open[open.length - 1].inner : out).push(t);
  }
  return out;
}

const isGroup = (x: any): x is Group => x && 'head' in x && 'inner' in x;

function countEvents(g: Group): number {
  return g.inner.reduce((n, x) => n + (isGroup(x) ? countEvents(x) + 1 : 1), 0);
}

function Timeline({ timeline, elapsedMs }: { timeline: any[]; elapsedMs: number }) {
  const instances = useMemo(() => [...new Set(timeline.map((t) => t.instance))], [timeline]);
  const [inst, setInst] = useState('');
  const [onlyKey, setOnlyKey] = useState(false);
  const [open, setOpen] = useState<Set<string>>(new Set());
  const KEY = /attempt|retry|await|timeout|timer|wait|signal|message|unmatched|duplicate|outcome|end|start$|block/;
  const rows = timeline.filter((t) => (!inst || t.instance === inst) && (!onlyKey || KEY.test(t.type)));
  const grouped = useMemo(() => groupTimeline(rows), [rows]);
  const attempts = timeline.filter((t) => t.type === 'attempt');
  const perStep = new Map<string, number>();
  for (const a of attempts) perStep.set(`${a.instance} ${a.step}`, (perStep.get(`${a.instance} ${a.step}`) ?? 0) + 1);
  const multi = [...perStep].filter(([, n]) => n > 1);
  const blocks = timeline.filter((t) => t.type === 'block-start').map((t) => `${t.instance}|${t.step}|${t.at}`);
  const toggle = (k: string) => setOpen((s) => {
    const n = new Set(s);
    if (n.has(k)) n.delete(k);
    else n.add(k);
    return n;
  });
  let prevAt: number | undefined;
  const line = (t: any, depth: number, key: string) => {
    const tick = prevAt !== undefined && prevAt !== t.at;
    prevAt = t.at;
    return (
      <tr key={key} className={[tick ? 'tick' : '', depth ? 'nested' : ''].join(' ')}>
        <td className="mono nowrap">{fmtMs(t.at)}</td>
        <td className="mono small">{t.instance}</td>
        <td className="mono" style={{ paddingLeft: 7 + depth * 18 }}>{depth > 0 && <span className="muted">└ </span>}{t.step ?? ''}</td>
        <td><Badge tone={typeTone(t.type)}>{t.type}</Badge></td>
        <td className="small break">
          {detailText(t.detail)}
          {t.detail?.policy && <> <details className="inline"><summary>policy</summary><Json value={t.detail.policy} /></details></>}
        </td>
      </tr>
    );
  };
  const render = (items: (any | Group)[], depth: number, path: string): any[] => items.flatMap((x, i) => {
    if (!isGroup(x)) return [line(x, depth, `${path}-${i}`)];
    const k = `${x.head.instance}|${x.head.step}|${x.head.at}`;
    const isOpen = open.has(k);
    const tick = prevAt !== undefined && prevAt !== x.head.at;
    prevAt = x.head.at;
    const head = (
      <tr key={`${path}-${i}-h`} className={['block-row', tick ? 'tick' : '', depth ? 'nested' : ''].join(' ')} onClick={() => toggle(k)}>
        <td className="mono nowrap">{fmtMs(x.head.at)}</td>
        <td className="mono small">{x.head.instance}</td>
        <td className="mono" style={{ paddingLeft: 7 + depth * 18 }}>
          <button className="link" aria-expanded={isOpen} aria-label={`${isOpen ? 'Close' : 'Open'} the steps inside ${x.head.step}`}>{isOpen ? '▾' : '▸'}</button> <b>{x.head.step}</b>
        </td>
        <td><Badge tone="info">composite</Badge></td>
        <td className="small break">
          {x.head.detail?.of && <><code>{x.head.detail.of}</code> by <code>{x.head.detail.by}</code>, </>}
          {countEvents(x)} events inside{x.end ? <>, ends <code>{x.end.detail?.outcome}</code> at {fmtMs(x.end.at)}</> : ', not ended'}
        </td>
      </tr>
    );
    if (!isOpen) return [head];
    return [head, ...render(x.inner, depth + 1, `${path}-${i}`), ...(x.end ? [line(x.end, depth + 1, `${path}-${i}-e`)] : [])];
  });
  const body = render(grouped, 0, 'r');
  return (
    <Section title="Timeline on the virtual clock" actions={<span className="muted small">total {fmtMs(elapsedMs)}</span>}>
      {multi.length > 0 && (
        <p className="small">
          Retried: {multi.map(([k, n]) => <Badge key={k} tone="warn">{k}: {n} attempts</Badge>)}
        </p>
      )}
      <div className="toolbar">
        <select value={inst} onChange={(e) => setInst(e.target.value)} aria-label="Instance">
          <option value="">All instances</option>
          {instances.map((i) => <option key={i}>{i}</option>)}
        </select>
        <label className="check"><input type="checkbox" checked={onlyKey} onChange={(e) => setOnlyKey(e.target.checked)} /> Attempts, waits, timers, signals and messages only</label>
        {blocks.length > 0 && (
          <>
            <button onClick={() => setOpen(new Set(blocks))}>Open every composite</button>
            <button onClick={() => setOpen(new Set())}>Close them</button>
            <span className="muted small">The steps inside a composite are nested under it and closed at first.</span>
          </>
        )}
      </div>
      <div className="table-wrap tall">
        <table className="grid compact timeline">
          <thead><tr><th>At</th><th>Instance</th><th>Step</th><th>Event</th><th>Detail</th></tr></thead>
          <tbody>{body}</tbody>
        </table>
      </div>
    </Section>
  );
}

function Instances({ instances }: { instances: InstanceView[] }) {
  const roots = instances.filter((i) => !i.parent || !instances.some((x) => x.id === i.parent));
  const [sel, setSel] = useState(instances[0]?.id);
  const cur = instances.find((i) => i.id === sel);
  const node = (i: InstanceView, depth: number): any => (
    <li key={i.id}>
      <button className={i.id === sel ? 'tree-node on' : 'tree-node'} onClick={() => setSel(i.id)} style={{ paddingLeft: 6 + depth * 4 }}>
        <Badge tone="info">{i.kind}</Badge> <span className="mono">{i.workflow}</span><span className="muted mono">@{i.release}</span>{' '}
        {i.outcome && <Badge tone={i.failed ? 'fail' : 'pass'}>{i.outcome}</Badge>}
      </button>
      {instances.some((c) => c.parent === i.id) && <ul className="tree">{instances.filter((c) => c.parent === i.id).map((c) => node(c, depth + 1))}</ul>}
    </li>
  );
  return (
    <div className="cols tree-left">
      <div className="col side">
        <Section title="Instance tree">
          <ul className="tree root">{roots.map((r) => node(r, 0))}</ul>
        </Section>
      </div>
      <div className="col">{cur && <InstanceDetail i={cur} />}</div>
    </div>
  );
}

/** Steps with a dotted id run inside the composite step whose id comes before the dot. */
function nestSteps(i: InstanceView): { id: string; step?: any; inner?: any[]; outcome?: string }[] {
  const out: { id: string; step?: any; inner?: any[]; outcome?: string }[] = [];
  for (const s of i.steps ?? []) {
    const dot = String(s.step ?? '').indexOf('.');
    if (dot < 0) {
      out.push({ id: s.step, step: s });
      continue;
    }
    const id = s.step.slice(0, dot);
    const last = out[out.length - 1];
    if (last?.inner && last.id === id) last.inner.push(s);
    else out.push({ id, inner: [s], outcome: i.trace.find((t) => t.startsWith(`${id}:`))?.slice(id.length + 1) });
  }
  return out;
}

function StepRow({ s, nested }: { s: any; nested?: boolean }) {
  return (
    <tr className={nested ? 'nested' : ''}>
      <td className="mono" style={nested ? { paddingLeft: 26 } : undefined}>{nested && <span className="muted">└ </span>}{s.step}</td>
      <td>{s.activity && <ArtefactLink name={s.activity} release={s.release} />}{s.pinned && s.pinned !== s.release && <div className="small warn-text">pinned {s.pinned}</div>}</td>
      <td>{s.attempts}</td>
      <td>{s.outcome && <code>{s.outcome}</code>}</td>
      <td className="mono small nowrap">{fmtMs(s.startedAt)}{s.endedAt !== undefined && s.endedAt !== s.startedAt ? ` to ${fmtMs(s.endedAt)}` : ''}</td>
      <td className="small delta-cell"><Delta delta={s.delta} /></td>
    </tr>
  );
}

function InstanceDetail({ i }: { i: InstanceView }) {
  const [openBlocks, setOpenBlocks] = useState<Record<string, boolean>>({});
  return (
    <>
      <Section title={<>Instance <span className="mono">{i.id}</span></>}>
        <KV
          rows={[
            ['Workflow', <ArtefactLink name={i.workflow} release={i.release} />],
            ['Kind', i.kind],
            ['Outcome', i.outcome ? <code>{i.outcome}</code> : <span className="muted">not ended</span>],
            ['Reason code', i.reasonCode ? <code>{i.reasonCode}</code> : <span className="muted">none</span>],
            ['Failed', i.failed ? <span className="fail-text">{i.failed}</span> : <span className="muted">no</span>],
            ['Durable', i.durable ? 'yes' : 'no'],
            ['Ran the expanded form of', i.expandedFrom ? <span className="mono small">{i.release} {String(i.expandedFrom.hash).replace('sha256:', '').slice(0, 12)}</span> : <span className="muted">the authored form; it calls no composite</span>],
            ['Detail', i.detail?.length ? <span className="chips">{i.detail.map((d) => <code key={d}>{d}</code>)}</span> : <span className="muted">none</span>],
            ['Started, ended', <span className="mono">{fmtMs(i.startedAt)} to {i.endedAt !== undefined ? fmtMs(i.endedAt) : 'open'}</span>],
            ['Attempts', <span className="chips">{Object.entries(i.attempts ?? {}).map(([k, n]) => <Badge key={k} tone={n > 1 ? 'warn' : 'muted'}>{k} {n}</Badge>)}</span>],
            ['Hook bindings', i.bindings?.length ? <Val v={i.bindings} /> : <span className="muted">none</span>],
            ['Supersessions applied', i.supersessions?.length ? <Val v={i.supersessions} /> : <span className="muted">none</span>],
          ]}
        />
        <details>
          <summary>Pins ({Object.keys(i.pins ?? {}).length})</summary>
          <table className="grid compact">
            <tbody>{Object.entries(i.pins ?? {}).map(([k, v]) => <tr key={k}><td><ArtefactLink name={k} /></td><td className="mono">{v}</td></tr>)}</tbody>
          </table>
        </details>
      </Section>
      <Section title="Steps and context deltas" count={i.steps?.length}>
        {!i.steps?.length ? <p className="muted">No call steps ran.</p> : (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>Step</th><th>Activity and release</th><th>Attempts</th><th>Outcome</th><th>Time</th><th>Context delta</th></tr></thead>
              <tbody>
                {nestSteps(i).map((g, n) => g.inner ? (
                  <Fragment key={n}>
                    <tr className="block-row" onClick={() => setOpenBlocks((o) => ({ ...o, [g.id]: !o[g.id] }))}>
                      <td className="mono"><button className="link" aria-expanded={!!openBlocks[g.id]}>{openBlocks[g.id] ? '▾' : '▸'}</button> <b>{g.id}</b></td>
                      <td><Badge tone="info">composite</Badge></td>
                      <td>{g.inner.reduce((k: number, x: any) => k + (x.attempts ?? 0), 0)}</td>
                      <td>{g.outcome && <code>{g.outcome}</code>}</td>
                      <td className="mono small nowrap">{fmtMs(g.inner[0]?.startedAt)}{g.inner[g.inner.length - 1]?.endedAt !== undefined ? ` to ${fmtMs(g.inner[g.inner.length - 1].endedAt)}` : ''}</td>
                      <td className="small muted">{g.inner.length} step{g.inner.length === 1 ? '' : 's'} inside</td>
                    </tr>
                    {openBlocks[g.id] && g.inner.map((s: any, k: number) => <StepRow key={k} s={s} nested />)}
                  </Fragment>
                ) : <StepRow key={n} s={g.step} />)}
              </tbody>
            </table>
          </div>
        )}
        {i.deltas?.length > (i.steps?.length ?? 0) && (
          <details>
            <summary>All deltas ({i.deltas.length})</summary>
            {i.deltas.map((d: any, n: number) => <div key={n} className="small"><code>{d.step}</code> {d.release && <span className="muted">@{d.release}</span>} <Delta delta={d.delta} /></div>)}
          </details>
        )}
      </Section>
      <Section title="Rule traces" count={i.ruleTraces?.length}>
        {!i.ruleTraces?.length ? <p className="muted">No rule set ran.</p> : i.ruleTraces.map((r: any, n: number) => (
          <div key={n} className="rule-trace">
            <div><code>{r.step}</code> worst <Badge tone={r.worst === 'pass' ? 'pass' : r.worst === 'warn' ? 'warn' : 'fail'}>{r.worst}</Badge></div>
            {r.findings?.length > 0 && (
              <table className="grid compact">
                <thead><tr><th>Reason code</th><th>Severity</th><th>Path</th><th>Rule</th></tr></thead>
                <tbody>{r.findings.map((f: any, k: number) => <tr key={k}><td className="mono">{f.reasonCode}</td><td><Badge tone={f.severity === 'warn' ? 'warn' : 'fail'}>{f.severity}</Badge></td><td className="mono">{f.path}</td><td>{f.rule && <ArtefactLink name={f.rule} />}</td></tr>)}</tbody>
              </table>
            )}
            {r.decisions?.length > 0 && <div className="small">Decisions <Val v={r.decisions} /></div>}
            {r.parameters && Object.keys(r.parameters).length > 0 && <div className="small">Parameters <Val v={r.parameters} /></div>}
            <details>
              <summary className="small">Rules evaluated ({r.trace?.length ?? 0})</summary>
              <table className="grid compact">
                <thead><tr><th>Rule</th><th>Stage</th><th>Fired</th><th>Activations</th></tr></thead>
                <tbody>{(r.trace ?? []).map((t: any, k: number) => <tr key={k}><td><ArtefactLink name={t.rule} release={t.release} /></td><td>{t.stage}</td><td>{t.fired ? <Badge tone="warn">fired</Badge> : <Badge>no</Badge>}</td><td>{t.activations}</td></tr>)}</tbody>
              </table>
            </details>
          </div>
        ))}
      </Section>
      <Section title="Context at the end">
        <details>
          <summary>Context</summary>
          <Json value={i.context} />
        </details>
        {i.engineState && (
          <details>
            <summary>Last engine state</summary>
            <Json value={i.engineState} />
          </details>
        )}
      </Section>
    </>
  );
}

function Delta({ delta }: { delta: any }) {
  if (!delta || !Array.isArray(delta) || !delta.length) return <span className="muted">no change</span>;
  return (
    <ul className="plain">
      {delta.map((d: any, i: number) => (
        <li key={i}><code>{d.path}</code>{'before' in d && <> from <Val v={d.before} /></>} to <Val v={d.after} /></li>
      ))}
    </ul>
  );
}

function Messages({ rec }: { rec: NonNullable<TestRunReport['record']> }) {
  const t0 = Date.parse(rec.startedAt);
  const rel = (at: number) => (Number.isFinite(t0) && at > 1e11 ? fmtMs(at - t0) : fmtMs(at));
  return (
    <>
      <Section title="Messages in and out" count={rec.messages.length}>
        {!rec.messages.length ? <p className="muted">No message.</p> : (
          <div className="table-wrap tall">
            <table className="grid compact">
              <thead><tr><th>At</th><th>From</th><th>Port</th><th>Type</th><th>Id</th><th>Status</th><th>In reply to</th><th>Correlation</th></tr></thead>
              <tbody>
                {rec.messages.map((m, i) => (
                  <tr key={i}>
                    <td className="mono nowrap">{rel(m.at)}</td>
                    <td><Badge tone={m.from === 'platform' ? 'info' : 'muted'}>{m.from === 'platform' ? 'out' : 'in'} {m.from}</Badge></td>
                    <td className="mono small">{m.port}</td>
                    <td className="mono small">{m.type || <span className="muted">reply</span>}{m.malformed && <Badge tone="fail">malformed</Badge>}{m.copy ? <Badge tone="warn">copy {m.copy}</Badge> : null}</td>
                    <td className="mono small break">{m.id}</td>
                    <td>{m.status ?? ''}</td>
                    <td className="mono small break">{m.inReplyTo ?? ''}</td>
                    <td className="small"><Val v={m.correlation} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small">Payloads are held by reference and not shown.</p>
      </Section>
      <div className="cols">
        <div className="col">
          <Section title="Unmatched messages" count={rec.unmatched.length}>
            <List items={rec.unmatched} render={(u) => <><code>{u.type}</code> at {fmtMs(u.at)}: {u.reason}</>} empty="None." />
          </Section>
        </div>
        <div className="col">
          <Section title="Duplicate messages" count={rec.duplicates.length}>
            <List items={rec.duplicates} render={(u) => <><code>{u.type}</code> at {fmtMs(u.at)}</>} empty="None." />
          </Section>
        </div>
      </div>
    </>
  );
}

function Bench({ rec }: { rec: NonNullable<TestRunReport['record']> }) {
  return (
    <div className="cols">
      <div className="col">
        <Section title="Bench" count={rec.bench.length}>
          {!rec.bench.length ? <p className="muted">No port was bound in the bench.</p> : (
            <table className="grid compact">
              <thead><tr><th>Port</th><th>Simulator</th><th>Scenario</th></tr></thead>
              <tbody>{rec.bench.map((b, i) => <tr key={i}><td><ArtefactLink name={b.port} /></td><td>{b.simulator && <ArtefactLink name={b.simulator} />}</td><td className="mono">{b.scenario}</td></tr>)}</tbody>
            </table>
          )}
        </Section>
      </div>
      <div className="col">
        <Section title="Default bindings the run used" count={rec.defaultBindings.length}>
          {!rec.defaultBindings.length ? <p className="muted">None. Every port the run reached was bound in the bench.</p> : (
            <table className="grid compact">
              <thead><tr><th>Port</th><th>Simulator</th><th>Scenario</th><th>Note</th></tr></thead>
              <tbody>{rec.defaultBindings.map((b: any, i: number) => <tr key={i}><td>{b.port && <ArtefactLink name={b.port} />}</td><td>{b.simulator && <ArtefactLink name={b.simulator} />}</td><td className="mono">{b.scenario}</td><td className="small">{typeof b === 'string' ? b : b.note ?? b.reason ?? ''}</td></tr>)}</tbody>
            </table>
          )}
        </Section>
      </div>
    </div>
  );
}
