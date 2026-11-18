import { Fragment, useEffect, useMemo, useState, type ReactNode } from 'react';
import YAML from 'yaml';
import { downloadUrl, get, post, refusal, type Issue, type Summary, type TestRunReport } from '../api';
import { ArtefactLink, Badge, ErrorBox, Issues, Loading, PassFail, Section, Tabs, useDebounced, useLoad, useStudio } from '../ui';
import { DraftActions, Gates, JsonField, Sel, StringList, Txt, clean, orNone, resolves, scalar, toYamlText, useCatalogue, useDocs, useGates, useModel } from '../authoring';
import { useResolver } from '../expand';
import { useAssist } from '../assist';
import { Canvas, getIn, setIn } from './Canvas';

// The artefact editors (C13, F-06 to F-11, F-36): forms that write valid documents for a rule,
// a decision table, an activity contract, a map, a simulator and a composite with its subflow.
// Each saves as a draft and shows the publish gates on every edit, the first red one highlighted.

type Kind = 'rule' | 'ruleset' | 'table' | 'activity' | 'map' | 'simulator' | 'composite' | 'port' | 'scheme';
type Path = (string | number)[];
type Set = (p: Path, v: any) => void;

const KINDS: { id: Kind; label: string; catalogue: string; open: string }[] = [
  { id: 'rule', label: 'Rule', catalogue: 'rule', open: 'uae.aani.rule.amount-within-limit.v1' },
  { id: 'ruleset', label: 'Rule set', catalogue: 'ruleset', open: 'uae.wps.ruleset.salary-file.v1' },
  { id: 'table', label: 'Decision table', catalogue: 'table', open: 'p.table.workflow-select.v1' },
  { id: 'activity', label: 'Activity contract', catalogue: 'activity', open: 'p.reserve.funds.v1' },
  { id: 'map', label: 'Map', catalogue: 'activity', open: 'uae.wps.map.sif-in.v1' },
  { id: 'simulator', label: 'Simulator', catalogue: 'simulator', open: 'uae.p.sim.participant-directory.v1' },
  { id: 'composite', label: 'Composite and subflow', catalogue: 'activity', open: 'uae.wps.exchange.submit-salary-file.v1' },
  { id: 'port', label: 'Port', catalogue: 'port', open: 'uae.wps.port.wps.v1' },
  { id: 'scheme', label: 'Scheme and dispatch table', catalogue: 'scheme', open: '' },
];

const STANDARD = ['accept', 'reject', 'pending-then-final', 'no-reply', 'late-reply', 'duplicate-reply', 'fail-twice-then-accept', 'malformed-reply'];

const PROV = { origin: 'authored', status: 'provisional' };

function starter(kind: Kind): any {
  switch (kind) {
    case 'ruleset':
      return { kind: 'ruleset', name: 'uae.demo.ruleset.file.v1', release: '1.0.0', title: 'New rule set', mode: 'validation', stages: ['field'], rules: ['uae.demo.rule.currency-aed.v1'], provenance: PROV };
    case 'port':
      return {
        kind: 'port', name: 'uae.demo.port.demo.v1', release: '1.0.0', title: 'New port', party: 'the party behind the port',
        operations: [{ name: 'submit-file', style: 'send' }, { name: 'receive-file', style: 'receive' }], provenance: PROV,
      };
    case 'scheme':
      return {
        kind: 'scheme', scope: 'uae.demo', release: '1.0.0', title: 'New scheme', wire: 'file',
        messages: [
          { id: 'uae.demo.credit-file', title: 'File that starts a payment', direction: 'in', confirmed: true, disposition: { start: { kind: 'payment', direction: 'outward', segment: 'corporate', leg: 'uae.demo.wf.credit-file-out.v1', intakeMap: 'uae.demo.map.file-in.v1' } } },
          { id: 'uae.demo.file-submission', title: 'File sent to the scheme', direction: 'out', confirmed: true, disposition: { sentBy: 'uae.demo.exchange.send-file.v1' } },
          { id: 'uae.demo.ack', title: 'Acknowledgement from the scheme', direction: 'in', confirmed: true, disposition: { deliver: { correlateBy: 'file id' } } },
        ],
        legs: ['uae.demo.wf.credit-file-out.v1'], ports: ['uae.demo.port.demo.v1'], provenance: PROV,
      };
    case 'rule':
      return {
        kind: 'rule', name: 'uae.aani.rule.new.v1', release: '1.0.0', title: 'New rule', ruleKind: 'field', repeatability: 'non-repeatable',
        when: [{ match: { fact: 'Payment', as: 'p' } }, { having: 'isBlank(p.purpose)' }],
        then: [{ yield: { finding: { severity: 'repair', path: 'payment.purpose', reasonCode: 'NARR' } } }],
        examples: [
          { name: 'passes', evaluationDate: '2026-11-17', given: { Payment: { purpose: 'SALA' } }, expect: { findings: [] } },
          { name: 'fails', evaluationDate: '2026-11-17', given: { Payment: {} }, expect: { findings: [{ reasonCode: 'NARR' }] } },
        ],
        provenance: PROV,
      };
    case 'table':
      return {
        kind: 'table', name: 'p.table.new.v1', release: '1.0.0', title: 'New decision table', hitPolicy: 'unique',
        inputs: [{ name: 'kind', expression: 'header.kind' }], outputs: ['route'],
        rows: [{ when: { kind: 'payment' }, then: { route: 'standard' } }, { when: {}, then: { route: 'review' }, reason: 'Catch-all' }],
        examples: [
          { name: 'payment', given: { Header: { kind: 'payment' } }, expect: { decisions: [{ name: 'route', value: 'standard' }] } },
          { name: 'other', given: { Header: { kind: 'request' } }, expect: { decisions: [{ name: 'route', value: 'review' }] } },
        ],
        provenance: PROV,
      };
    case 'activity':
      return {
        kind: 'activity', name: 'uae.aani.validate.new.v1', release: '1.0.0', title: 'New activity', type: 'validate', reads: ['header', 'payment'], writes: ['facts.validation'],
        outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'read', implementation: { binding: 'builtin', service: 'rule-service' }, provenance: PROV,
      };
    case 'map':
      return {
        kind: 'activity', name: 'uae.wps.map.new-in.v1', release: '1.0.0', title: 'New map', type: 'map', reads: ['messages'], writes: ['payment'],
        outcomes: [{ name: 'mapped', primary: true }, { name: 'malformed' }], effect: 'pure',
        map: { direction: 'in', sourceModel: 'WPS SIF', fields: [{ from: 'SCR.employerId', to: 'payment.debtor.id' }], roundTrip: true },
        implementation: { binding: 'script', language: 'typescript', script: 'scripts/uae.wps.map.new-in.v1.ts' }, provenance: PROV,
      };
    case 'simulator':
      return {
        kind: 'simulator', name: 'uae.p.sim.new.v1', release: '1.0.0', title: 'New simulator', direction: 'outward', port: 'uae.p.port.participant-directory.v1',
        standsFor: 'the party behind the port', scenarios: [{ name: 'accept', steps: [{ expect: 'uae.p.directory.request' }, { send: 'uae.p.directory.reply', status: 'accepted' }] }],
        implementation: { binding: 'script', language: 'typescript', script: 'scripts/uae.p.sim.new.v1.ts' }, provenance: PROV,
      };
    case 'composite':
      return {
        kind: 'activity', name: 'uae.aani.exchange.new.v1', release: '1.0.0', title: 'New composite exchange', type: 'exchange', mode: 'send',
        reads: ['header', 'messages'], writes: ['status', 'messages'], outcomes: [{ name: 'accepted', primary: true }, { name: 'rejected' }, { name: 'pending' }],
        errors: [{ name: 'TransientError', retryable: true }], effect: 'external', idempotencyKey: "concat(header.paymentId, ':new')", ports: ['uae.aani.port.switch.v1'],
        policy: { replyTimeout: 'PT30S' }, implementation: { binding: 'subflow', subflow: 'uae.aani.sub.new.v1' }, provenance: PROV,
      };
  }
}

function subflowStarter(act: any): any {
  const outs: string[] = (act?.outcomes ?? []).map((o: any) => o.name);
  return {
    kind: 'workflow', wfKind: 'subflow', name: act?.implementation?.subflow ?? 'uae.aani.sub.new.v1', release: '1.0.0', dsl: '1.0.0', title: `What ${act?.name ?? 'the composite'} does underneath`,
    implements: { activity: act?.name }, exit: { outcomes: outs },
    do: [
      { id: 'send', call: 'uae.aani.exchange.send-credit.v1', handle: { rejected: { return: 'rejected' }, unknown: { return: 'pending' } } },
      { return: outs[0] ?? 'accepted' },
    ],
    provenance: PROV,
  };
}

export function Editors({ query }: { query: URLSearchParams }) {
  const { go } = useStudio();
  const kind = (KINDS.find((k) => k.id === query.get('kind'))?.id ?? 'rule') as Kind;
  return (
    <div className="view">
      <div className="view-head">
        <h2>Artefact editors</h2>
        <p className="muted">Define a rule, a decision table, an activity contract, a map, a simulator or a composite in a form. The form writes the document; the gates run on every edit; save it as a draft and publish through maker-checker.</p>
      </div>
      <Tabs tabs={KINDS.map((k) => ({ id: k.id, label: k.label }))} value={kind} onChange={(k) => go(`/editors?kind=${k}`)} />
      <EditorFor key={`${kind}|${query.get('open') ?? ''}`} kind={kind} open={query.get('open') ?? undefined} />
    </div>
  );
}

function EditorFor({ kind, open }: { kind: Kind; open?: string }) {
  const { tenant } = useStudio();
  const meta = KINDS.find((k) => k.id === kind)!;
  const [doc, setDoc] = useState<any>(() => starter(kind));
  const [origin, setOrigin] = useState('A new document from the starter.');
  const [page, setPage] = useState<any>();
  const [error, setError] = useState<string>();
  const [draftKey, setDraftKey] = useState<string>();
  const [sub, setSub] = useState<any>();
  const items = useCatalogue(meta.catalogue);
  const choices = items.filter((i) => i.status === 'published' && (kind === 'map' ? i.type === 'map' : kind === 'activity' ? i.type !== 'map' : kind === 'composite' ? i.type === 'exchange' : true));

  const openRelease = async (name: string) => {
    setError(undefined);
    try {
      const [r, a] = await Promise.all([post('/api/new', { from: 'clone', source: name }), get(`/api/artefacts/${encodeURIComponent(name)}`)]);
      // Keep the source's provenance so a rebuilt document can keep its hash; the clone note is shown instead.
      const d = { ...r.doc, provenance: a.doc.provenance ?? r.doc.provenance };
      setDoc(d);
      setPage(a);
      setDraftKey(undefined);
      setOrigin(`A new release of ${name}@${a.entry.release}: ${d.release}. Change what you need; the gates say what the change implies.`);
      if (kind === 'composite') {
        const sf = a.doc.implementation?.subflow;
        if (sf) {
          const s = await post('/api/new', { from: 'clone', source: sf });
          const sa = await get(`/api/artefacts/${encodeURIComponent(sf)}`);
          setSub({ ...s.doc, provenance: sa.doc.provenance ?? s.doc.provenance });
        } else setSub(subflowStarter(d));
      }
    } catch (e: any) {
      setError(e.message);
    }
  };
  useEffect(() => {
    if (open) void openRelease(open);
    else if (kind === 'composite') setSub(subflowStarter(starter('composite')));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, kind, tenant]);

  const set: Set = (p, v) => setDoc((d: any) => setIn(d, p, v));
  const gates = useGates(doc);
  useAssist({ surface: 'editor', kind, doc, setDoc, topic: kind });

  let form: ReactNode;
  switch (kind) {
    case 'rule': form = <RuleForm doc={doc} set={set} gates={gates.result} />; break;
    case 'table': form = <TableForm doc={doc} set={set} gates={gates.result} analysis={page?.analysis} />; break;
    case 'activity': form = <ActivityForm doc={doc} set={set} />; break;
    case 'map': form = <><ActivityForm doc={doc} set={set} /><TestsOf name={doc.name} /></>; break;
    case 'simulator': form = <SimulatorForm doc={doc} set={set} />; break;
    case 'composite': form = <ActivityForm doc={doc} set={set} composite />; break;
    case 'ruleset': form = <RuleSetForm doc={doc} set={set} />; break;
    case 'port': form = <PortForm doc={doc} />; break;
    case 'scheme': form = <SchemeForm doc={doc} />; break;
  }

  return (
    <>
      <div className="toolbar">
        {kind !== 'scheme' && <label className="field-inline">
          <span>Open a published {meta.label.toLowerCase()} as a new release</span>
          <select value="" onChange={(e) => e.target.value && openRelease(e.target.value)} aria-label={`Open a published ${meta.label}`}>
            <option value="">Choose</option>
            {choices.map((c) => <option key={c.key} value={c.name}>{c.name} @{c.release}</option>)}
          </select>
        </label>}
        {meta.open && <button onClick={() => openRelease(meta.open)}>Open {meta.open}</button>}
        <button onClick={() => { setDoc(starter(kind)); setPage(undefined); setDraftKey(undefined); setOrigin('A new document from the starter.'); if (kind === 'composite') setSub(subflowStarter(starter('composite'))); }}>Start new</button>
      </div>
      <ErrorBox error={error} />
      <p className="muted small">{origin}</p>
      <div className="cols">
        <div className="col">{form}</div>
        <div className="col side">
          <Gates state={gates} title={kind === 'composite' ? 'Gates of the composite' : 'Gates'} />
          <Section title="Save and publish">
            <DraftActions doc={clean(doc)} draftKey={draftKey} setDraftKey={setDraftKey} />
          </Section>
          <YamlPane doc={doc} onDoc={setDoc} />
          {(kind === 'activity' || kind === 'map') && doc.implementation?.binding === 'script' && <ScriptBehind doc={doc} />}
          {(kind === 'activity' || kind === 'composite' || kind === 'map') && <Untested name={doc.name} />}
          {kind === 'simulator' && doc.name && <Section title="Stub"><a className="button" href={downloadUrl(`/api/simulators/${encodeURIComponent(doc.name)}/stub`)}>Download the stub of the published {doc.name}</a><p className="muted small">A stand-alone stub that plays the scenarios, for a bank's own test environment.</p></Section>}
        </div>
      </div>
      {kind === 'composite' && sub && <SubflowEditor act={doc} sub={sub} setSub={setSub} />}
      {kind === 'composite' && <TestsOf name={doc.name} title="Tests of the composite alone" />}
    </>
  );
}

function RuleSetForm({ doc, set }: { doc: any; set: Set }) {
  const rules = useCatalogue('rule').filter((r) => r.status === 'published').map((r) => r.name);
  return (
    <Section title="Rule set">
      <p className="muted small">A rule set groups rules for a validate or decide activity. Its mode is validation (findings) or decision (decisions); its stages run in order, and a blocking finding stops the next stage.</p>
      <div className="stack">
        <Sel label="Mode" value={doc.mode} onChange={(v) => set(['mode'], v)} options={['validation', 'decision']} />
        <label className="field"><span>Stages, in order</span><StringList value={doc.stages} onChange={(v) => set(['stages'], v)} label="Stages" mono /></label>
        <label className="field"><span>Rules</span><StringList value={doc.rules} onChange={(v) => set(['rules'], v)} suggestions={rules} label="Rules" /></label>
      </div>
    </Section>
  );
}

// A port and a scheme manifest are short documents: the form shows what matters, the YAML pane edits.
function PortForm({ doc }: { doc: any }) {
  return (
    <Section title="Port">
      <p className="muted small">A port is the connection to one party: a scheme, core banking, screening. Every port needs a simulator (check L22); publish the two together. Edit the document in the YAML pane.</p>
      <table className="grid compact"><tbody>
        <tr><th>Name</th><td className="mono">{doc.name}</td></tr>
        <tr><th>Party</th><td>{doc.party}</td></tr>
        {(doc.operations ?? []).map((o: any) => <tr key={o.name}><th>Operation</th><td className="mono">{o.name} ({o.style})</td></tr>)}
      </tbody></table>
    </Section>
  );
}

function SchemeForm({ doc }: { doc: any }) {
  return (
    <Section title="Scheme and dispatch table">
      <p className="muted small">The dispatch table routes each message. A start message names the payment it starts (kind, direction and, for an outward payment, segment), its intake map and its leg (R-05-42, check L33). A deliver message reaches the workflow that awaits it; an outward message names the block that sends it. Edit the document in the YAML pane; publish it with its legs, composites and subflows as one change set.</p>
      <div className="table-wrap"><table className="grid compact">
        <thead><tr><th>Message</th><th>Direction</th><th>Disposition</th></tr></thead>
        <tbody>
          {(doc.messages ?? []).map((m: any) => {
            const d = m.disposition ?? {};
            const what = d.start ? `start: ${d.start.kind ?? '?'} ${d.start.direction ?? '?'}${d.start.segment ? ' ' + d.start.segment : ''}, leg ${d.start.leg}, intake ${d.start.intakeMap}` : d.deliver ? `deliver by ${d.deliver.correlateBy}` : d.sentBy ? `sent by ${d.sentBy}` : d.handler ? `handler ${d.handler}` : d.planned ? 'planned' : JSON.stringify(d);
            return <tr key={m.id}><td className="mono">{m.id}</td><td>{m.direction}</td><td className="small">{what}</td></tr>;
          })}
        </tbody>
      </table></div>
      <p className="small">Legs: <span className="mono">{(doc.legs ?? []).join(', ') || 'none'}</span>. Ports: <span className="mono">{(doc.ports ?? []).join(', ') || 'none'}</span>.</p>
    </Section>
  );
}

const SCRIPT_STARTER = `// The script-behind: it uses only what is injected (ctx, ports, rules, params, clock, ids, log).
// It changes ctx and returns the outcome.
export async function run({ ctx }) {
  return 'mapped';
}
`;

// The script of an activity bound to a script (C07). It belongs to the activity's draft: save the
// draft first. Once the activity is published, its script is fixed.
function ScriptBehind({ doc }: { doc: any }) {
  const { actor } = useStudio();
  const [source, setSource] = useState(SCRIPT_STARTER);
  const [msg, setMsg] = useState<{ tone: string; text: string }>();
  const save = async () => {
    setMsg(undefined);
    try {
      const r = await post('/api/scripts', { actor, activity: doc.name, language: doc.implementation?.language ?? 'typescript', source });
      setMsg({ tone: 'pass', text: `Saved ${r.file} as ${actor}.` });
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    }
  };
  return (
    <Section title="Script-behind">
      <p className="muted small">Save the activity as a draft first. The script is checked as you save: no network, files, clock, randomness or environment.</p>
      <textarea className="yaml-editor short" spellCheck={false} value={source} onChange={(e) => setSource(e.target.value)} aria-label="Script source" />
      <button onClick={save}>Save the script as {actor}</button>
      {msg && <div className={`alert ${msg.tone}`} role="status">{msg.text}</div>}
    </Section>
  );
}

function YamlPane({ doc, onDoc }: { doc: any; onDoc: (d: any) => void }) {
  const [edit, setEdit] = useState(false);
  const [text, setText] = useState('');
  const [err, setErr] = useState<string>();
  const y = useMemo(() => toYamlText(clean(doc)), [doc]);
  useEffect(() => {
    if (!edit) setText(y);
  }, [y, edit]);
  return (
    <Section title="Document" actions={<button className="link" onClick={() => setEdit((e) => !e)}>{edit ? 'Done' : 'Edit the YAML'}</button>}>
      {edit ? (
        <>
          <textarea className="yaml-editor short" spellCheck={false} value={text} aria-label="Document YAML" onChange={(e) => {
            setText(e.target.value);
            try {
              const d = YAML.parse(e.target.value);
              setErr(undefined);
              if (d && typeof d === 'object') onDoc(d);
            } catch (x: any) {
              setErr(String(x.message).split('\n')[0]);
            }
          }} />
          {err && <div className="small fail-text">YAML does not parse: {err}</div>}
        </>
      ) : <pre className="code">{y}</pre>}
    </Section>
  );
}

function Untested({ name }: { name?: string }) {
  const r = useLoad(() => (name ? get(`/api/test-coverage/${encodeURIComponent(name)}`) : Promise.resolve(undefined)), [name]);
  if (!name) return null;
  return (
    <Section title="Test coverage of the published release">
      {r.error ? <p className="muted small">{name} is not published yet, so no test targets it.</p> : r.data && (
        <>
          <p className="small">Tests: {r.data.tests.length ? r.data.tests.map((t: string) => <a key={t} className="mono" href={`#/tests?run=${encodeURIComponent(t)}`}>{t} </a>) : <span className="muted">none</span>}</p>
          <p className="small">Untested outcomes: {r.data.untested.length ? r.data.untested.map((o: string) => <Badge key={o} tone="warn">{o}</Badge>) : <Badge tone="pass">none</Badge>}</p>
        </>
      )}
    </Section>
  );
}

/** The stored tests that target an artefact, run from the editor (F-06: WPS-MAP-001 and 002). */
function TestsOf({ name, title = 'Tests of the published release' }: { name?: string; title?: string }) {
  const tests = useLoad(() => get<any[]>('/api/tests'), []);
  const mine = (tests.data ?? []).filter((t) => t.target === name);
  const [res, setRes] = useState<Record<string, TestRunReport | string>>({});
  const run = async (id: string) => {
    setRes((r) => ({ ...r, [id]: 'running' }));
    try {
      const r = await post<TestRunReport>(`/api/tests/${encodeURIComponent(id)}/run`, {});
      setRes((m) => ({ ...m, [id]: r }));
    } catch (e: any) {
      setRes((m) => ({ ...m, [id]: e.message }));
    }
  };
  return (
    <Section title={title} count={mine.length} actions={mine.length ? <button onClick={() => mine.forEach((t) => run(t.id))}>Run them all</button> : undefined}>
      {!mine.length ? <p className="muted small">No stored test targets {name}.</p> : (
        <table className="grid compact">
          <thead><tr><th>Test</th><th>Title</th><th>Result</th><th>Trace and detail</th><th></th></tr></thead>
          <tbody>
            {mine.map((t) => {
              const r = res[t.id];
              return (
                <tr key={t.id}>
                  <td className="mono nowrap"><a href={`#/tests?run=${encodeURIComponent(t.id)}`}>{t.id}</a></td>
                  <td>{t.title}</td>
                  <td>{r === 'running' ? <Badge>running…</Badge> : typeof r === 'string' ? <span className="fail-text small">{r}</span> : r ? <PassFail passed={r.passed} /> : <span className="muted small">not run</span>}</td>
                  <td className="small mono break">{r && typeof r === 'object' && <>{r.trace.join(' ')}{r.detail?.length ? <div className="muted">detail {r.detail.join(' ')}</div> : null}</>}</td>
                  <td><button onClick={() => run(t.id)}>Run</button></td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </Section>
  );
}

// ------------------------------------------------------------------ shared fields

function Head({ doc, set, children }: { doc: any; set: Set; children?: ReactNode }) {
  return (
    <div className="form-grid">
      <label className="field wide"><span>Name</span><Txt value={doc.name} onChange={(v) => set(['name'], v)} mono label="Name" /></label>
      <label className="field"><span>Release</span><Txt value={doc.release} onChange={(v) => set(['release'], v)} mono label="Release" /></label>
      <label className="field wide"><span>Title</span><Txt value={doc.title} onChange={(v) => set(['title'], v)} label="Title" /></label>
      {children}
      <label className="field wide"><span>Description</span><Txt value={doc.description} onChange={(v) => set(['description'], v)} label="Description" /></label>
    </div>
  );
}

function ModelList({ id }: { id: string }) {
  const m = useModel();
  return <datalist id={id}>{(m?.flat ?? []).map((n) => <option key={n.path} value={n.path}>{n.isoType}</option>)}</datalist>;
}

function ParametersEditor({ params, set }: { params?: Record<string, any>; set: (v: any) => void }) {
  const p = params ?? {};
  const [name, setName] = useState('');
  const put = (k: string, v: any) => {
    const next = { ...p, [k]: v };
    if (v === undefined) delete next[k];
    set(Object.keys(next).length ? next : undefined);
  };
  return (
    <Section title="Parameters" count={Object.keys(p).length}>
      <p className="muted small">A value a bank may change is a parameter with a default and bounds, never a constant.</p>
      {Object.keys(p).length > 0 && (
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>Name</th><th>Type</th><th>Default</th><th>Currency</th><th>Min</th><th>Max</th><th>Description</th><th></th></tr></thead>
            <tbody>
              {Object.entries<any>(p).map(([k, d]) => {
                const out = d.bounds && ((d.bounds.max !== undefined && Number(d.default) > Number(d.bounds.max)) || (d.bounds.min !== undefined && Number(d.default) < Number(d.bounds.min)));
                return (
                  <tr key={k} className={out ? 'row-fail' : ''}>
                    <td className="mono">{k}</td>
                    <td><Sel value={d.type} onChange={(v) => put(k, { ...d, type: v })} options={['amount', 'integer', 'decimal', 'boolean', 'string', 'duration', 'code', 'list']} label="Parameter type" /></td>
                    <td><Txt value={d.default} onChange={(v) => put(k, { ...d, default: scalar(v) })} label="Default" size={8} /></td>
                    <td><Txt value={d.currency} onChange={(v) => put(k, clean({ ...d, currency: v }))} label="Currency" size={4} /></td>
                    <td><Txt value={d.bounds?.min} onChange={(v) => put(k, clean({ ...d, bounds: orNone({ ...(d.bounds ?? {}), min: scalar(v) }) }))} label="Minimum" size={7} /></td>
                    <td><Txt value={d.bounds?.max} onChange={(v) => put(k, clean({ ...d, bounds: orNone({ ...(d.bounds ?? {}), max: scalar(v) }) }))} label="Maximum" size={7} /></td>
                    <td><Txt value={d.description} onChange={(v) => put(k, clean({ ...d, description: v }))} label="Parameter description" /></td>
                    <td><button className="link danger" onClick={() => put(k, undefined)}>Remove</button></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <div className="inline-form">
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="parameterName" className="mono" aria-label="New parameter name" />
        <button disabled={!/^[a-z][A-Za-z0-9]*$/.test(name) || name in p} onClick={() => { put(name, { type: 'amount', default: 0, bounds: { min: 0 } }); setName(''); }}>Add a parameter</button>
      </div>
    </Section>
  );
}

/** Examples: given facts and the expected findings or decisions, each run live through the gates. */
function ExamplesEditor({ examples, set, gates, decisions }: { examples?: any[]; set: (v: any) => void; gates?: any; decisions?: boolean }) {
  const xs = examples ?? [];
  const results: any[] = (gates?.gates ?? []).find((g: any) => g.gate === 'tests')?.detail ?? [];
  const put = (i: number, v: any) => set(xs.map((x, n) => (n === i ? v : x)));
  return (
    <Section title="Examples" count={xs.length} actions={xs.length < 2 ? <Badge tone="fail">two at least before publish</Badge> : undefined}>
      <p className="muted small">Each example runs on every edit. A rule ships one passing and one failing example at least, and a boundary case for any threshold.</p>
      {xs.map((x, i) => {
        const r = results.find((d) => d.example === x.name);
        return (
          <div key={i} className={`example ${r ? (r.passed ? 'ex-pass' : 'ex-fail') : ''}`}>
            <div className="bench-row">
              <Txt value={x.name} onChange={(v) => put(i, { ...x, name: v ?? '' })} label="Example name" placeholder="name" />
              <Txt value={x.evaluationDate} onChange={(v) => put(i, clean({ ...x, evaluationDate: v }))} label="Evaluation date" placeholder="2026-11-17" mono size={10} />
              {r ? <PassFail passed={r.passed} /> : <Badge>not run</Badge>}
              <button className="link danger" onClick={() => set(xs.filter((_, n) => n !== i))}>Remove</button>
            </div>
            <div className="ex-cols">
              <div>
                <span className="small muted">Given (facts as JSON)</span>
                <JsonField value={x.given} onChange={(v) => put(i, { ...x, given: v ?? {} })} rows={4} label={`Given of ${x.name}`} />
              </div>
              <div>
                {decisions ? (
                  <>
                    <span className="small muted">Expect decisions (JSON)</span>
                    <JsonField value={x.expect?.decisions} onChange={(v) => put(i, { ...x, expect: clean({ ...x.expect, decisions: v }) })} rows={3} label={`Expected decisions of ${x.name}`} />
                  </>
                ) : (
                  <>
                    <span className="small muted">Expect findings, by reason code (none means it passes)</span>
                    <StringList value={(x.expect?.findings ?? []).map((f: any) => f.reasonCode)} onChange={(v) => put(i, { ...x, expect: { ...(x.expect ?? {}), findings: (v ?? []).map((c) => ({ reasonCode: c })) } })} placeholder="AM02" label={`Expected reason codes of ${x.name}`} />
                  </>
                )}
                {r && !r.passed && <div className="small fail-text">Actual: <code>{JSON.stringify(r.actual)}</code></div>}
              </div>
            </div>
          </div>
        );
      })}
      <button onClick={() => set([...xs, { name: `example ${xs.length + 1}`, evaluationDate: '2026-11-17', given: {}, expect: decisions ? { decisions: [] } : { findings: [] } }])}>Add an example</button>
    </Section>
  );
}

// ------------------------------------------------------------------ rule (C13-S1)

const COND_KINDS = ['match', 'exists', 'not', 'all', 'having', 'let', 'query', 'or', 'and'];

function RuleForm({ doc, set, gates }: { doc: any; set: Set; gates?: any }) {
  const when: any[] = doc.when ?? [];
  const then: any[] = doc.then ?? [];
  const putW = (i: number, v: any) => set(['when'], when.map((x, n) => (n === i ? v : x)));
  const putT = (i: number, v: any) => set(['then'], then.map((x, n) => (n === i ? v : x)));
  return (
    <>
      <Section title="Rule">
        <Head doc={doc} set={set}>
          <label className="field"><span>Rule kind</span><Sel value={doc.ruleKind} onChange={(v) => set(['ruleKind'], v)} options={['structural', 'field', 'cross-field', 'reference', 'policy', 'decision']} label="Rule kind" /></label>
          <label className="field"><span>Level</span><Sel value={doc.level} onChange={(v) => set(['level'], v)} options={['payment', 'batch', 'item']} label="Rule level" blank="payment" /></label>
          <label className="field"><span>Priority</span><Txt value={doc.priority} onChange={(v) => set(['priority'], v ? Number(v) : undefined)} label="Priority" /></label>
        </Head>
      </Section>
      <Section title="When" count={when.length}>
        <p className="muted small">Patterns match facts of the canonical model: Payment, Header, Batch, Item, Param. A condition is an expression over the names they bind.</p>
        <ModelList id="rule-paths" />
        {when.map((c, i) => {
          const k = Object.keys(c ?? {})[0] ?? 'match';
          const pattern = ['match', 'exists', 'not', 'all'].includes(k);
          return (
            <div key={i} className="cond-row">
              <Sel value={k} onChange={(nk) => putW(i, nk === 'having' ? { having: '' } : nk === 'let' ? { let: { x: '' } } : ['query', 'or', 'and'].includes(nk!) ? { [nk!]: c[k] } : { [nk!]: pattern ? c[k] : { fact: 'Payment', as: 'p' } })} options={COND_KINDS} label="Condition kind" />
              {pattern ? (
                <>
                  <Txt value={c[k]?.fact} onChange={(v) => putW(i, { [k]: clean({ ...c[k], fact: v ?? '' }) })} label="Fact" placeholder="Payment" mono size={10} />
                  <span className="muted small">as</span>
                  <Txt value={c[k]?.as} onChange={(v) => putW(i, { [k]: clean({ ...c[k], as: v }) })} label="Binds as" placeholder="p" mono size={6} />
                  <span className="muted small">where</span>
                  <Txt value={c[k]?.where} onChange={(v) => putW(i, { [k]: clean({ ...c[k], where: v }) })} label="Where" placeholder="expression" mono className="grow" />
                </>
              ) : k === 'having' ? (
                <Txt value={c.having} onChange={(v) => putW(i, { having: v ?? '' })} label="Having" placeholder="p.instructedAmount.value > maxAmount.value" mono className="grow" />
              ) : (
                <div className="grow"><JsonField value={c[k]} onChange={(v) => putW(i, { [k]: v })} rows={3} label={`${k} condition`} /></div>
              )}
              <button className="link" disabled={i === 0} onClick={() => { const n = [...when]; [n[i - 1], n[i]] = [n[i], n[i - 1]]; set(['when'], n); }} aria-label="Move up">↑</button>
              <button className="link danger" onClick={() => set(['when'], when.filter((_, n) => n !== i))}>Remove</button>
            </div>
          );
        })}
        <div className="row-actions">
          <button onClick={() => set(['when'], [...when, { match: { fact: 'Payment', as: 'p' } }])}>Add a pattern</button>
          <button onClick={() => set(['when'], [...when, { having: '' }])}>Add a condition</button>
        </div>
      </Section>
      <Section title="Then" count={then.length}>
        {then.map((t, i) => {
          const y = t.yield ?? {};
          const k = Object.keys(y)[0] ?? 'finding';
          const f = y[k] ?? {};
          return (
            <div key={i} className="cond-row">
              <span className="muted small">yield</span>
              <Sel value={k} onChange={(nk) => putT(i, { yield: nk === 'finding' ? { finding: { severity: 'reject', path: '', reasonCode: '' } } : nk === 'decision' ? { decision: { name: '', value: '' } } : { fact: { type: 'Fact' } } })} options={['finding', 'decision', 'fact']} label="Yield kind" />
              {k === 'finding' && (
                <>
                  <Sel value={f.severity} onChange={(v) => putT(i, { yield: { finding: { ...f, severity: v } } })} options={['reject', 'repair', 'warn']} label="Severity" />
                  <Txt value={f.path} onChange={(v) => putT(i, { yield: { finding: { ...f, path: v ?? '' } } })} label="Finding path" placeholder="payment.instructedAmount.value" mono list="rule-paths" className="grow" />
                  <Txt value={f.reasonCode} onChange={(v) => putT(i, { yield: { finding: { ...f, reasonCode: v ?? '' } } })} label="Reason code" placeholder="AM02" mono size={6} />
                  <Txt value={f.schemeCode} onChange={(v) => putT(i, { yield: { finding: clean({ ...f, schemeCode: v }) } })} label="Scheme code" placeholder="scheme code" mono size={8} />
                </>
              )}
              {k === 'decision' && (
                <>
                  <Txt value={f.name} onChange={(v) => putT(i, { yield: { decision: { ...f, name: v ?? '' } } })} label="Decision name" placeholder="name" mono />
                  <Txt value={f.value} onChange={(v) => putT(i, { yield: { decision: { ...f, value: scalar(v) ?? '' } } })} label="Decision value" placeholder="value" mono />
                  <Txt value={f.reason} onChange={(v) => putT(i, { yield: { decision: clean({ ...f, reason: v }) } })} label="Decision reason" placeholder="reason" />
                </>
              )}
              {k === 'fact' && <div className="grow"><JsonField value={f} onChange={(v) => putT(i, { yield: { fact: v } })} rows={2} label="Fact" /></div>}
              <button className="link danger" onClick={() => set(['then'], then.filter((_, n) => n !== i))}>Remove</button>
            </div>
          );
        })}
        <button onClick={() => set(['then'], [...then, { yield: { finding: { severity: 'reject', path: '', reasonCode: '' } } }])}>Add an action</button>
      </Section>
      <ParametersEditor params={doc.parameters} set={(v) => set(['parameters'], v)} />
      <ExamplesEditor examples={doc.examples} set={(v) => set(['examples'], v)} gates={gates} />
    </>
  );
}

// ------------------------------------------------------------------ decision table (C13-S2)

const ANY = '(any other)';

/** Gaps and overlaps of a table as edited: the same analysis the artefact page shows for a stored release. */
export function analyse(doc: any): { overlaps: number[][]; gaps: Record<string, string>[]; hasCatchAll: boolean } {
  const ins: string[] = (doc.inputs ?? []).map((i: any) => i.name);
  const rows: any[] = doc.rows ?? [];
  const cell = (r: any, k: string) => (r?.when?.[k] === undefined || r?.when?.[k] === '' ? undefined : String(r.when[k]));
  const overlaps: number[][] = [];
  for (let a = 0; a < rows.length; a++) for (let b = a + 1; b < rows.length; b++) {
    if (ins.every((k) => cell(rows[a], k) === undefined || cell(rows[b], k) === undefined || cell(rows[a], k) === cell(rows[b], k))) overlaps.push([a, b]);
  }
  const hasCatchAll = rows.some((r) => ins.every((k) => cell(r, k) === undefined));
  const domains = ins.map((k) => [...new Set(rows.map((r) => cell(r, k)).filter((x): x is string => x !== undefined)), ANY]);
  const gaps: Record<string, string>[] = [];
  const total = domains.reduce((n, d) => n * d.length, 1);
  if (!hasCatchAll && total <= 2000) {
    const walk = (i: number, acc: Record<string, string>) => {
      if (i === ins.length) {
        const hit = rows.some((r) => ins.every((k) => cell(r, k) === undefined || (acc[k] !== ANY && cell(r, k) === acc[k])));
        if (!hit) gaps.push({ ...acc });
        return;
      }
      for (const v of domains[i]) walk(i + 1, { ...acc, [ins[i]]: v });
    };
    walk(0, {});
  }
  return { overlaps, gaps, hasCatchAll };
}

function TableForm({ doc, set, gates, analysis }: { doc: any; set: Set; gates?: any; analysis?: any }) {
  const ins: any[] = doc.inputs ?? [];
  const outs: string[] = doc.outputs ?? [];
  const rows: any[] = doc.rows ?? [];
  const a = useMemo(() => analyse(doc), [doc]);
  const inOverlap = new Set(a.overlaps.flat());
  const blocking = doc.hitPolicy === 'unique' && a.overlaps.length > 0;
  return (
    <>
      <Section title="Decision table">
        <Head doc={doc} set={set}>
          <label className="field"><span>Hit policy</span><Sel value={doc.hitPolicy} onChange={(v) => set(['hitPolicy'], v)} options={[['unique', 'unique: one row at most'], ['first', 'first: the first row that matches'], ['collect', 'collect: every row that matches']]} label="Hit policy" /></label>
        </Head>
        <ModelList id="table-paths" />
        <h4>Inputs</h4>
        {ins.map((inp, i) => (
          <div key={i} className="bench-row">
            <Txt value={inp.name} onChange={(v) => set(['inputs', i, 'name'], v ?? '')} label="Input name" placeholder="name" mono />
            <Txt value={inp.expression} onChange={(v) => set(['inputs', i, 'expression'], v ?? '')} label="Input expression" placeholder="header.kind" mono list="table-paths" className="grow" />
            <button className="link danger" onClick={() => set(['inputs'], ins.filter((_, n) => n !== i))}>Remove</button>
          </div>
        ))}
        <button onClick={() => set(['inputs'], [...ins, { name: `input${ins.length + 1}`, expression: '' }])}>Add an input</button>
        <h4>Outputs</h4>
        <StringList value={outs} onChange={(v) => set(['outputs'], v ?? [])} placeholder="output name" />
      </Section>
      <Section title="Rows" count={rows.length} actions={blocking ? <Badge tone="fail">overlap: publish is blocked</Badge> : a.overlaps.length ? <Badge tone="warn">overlaps allowed by {doc.hitPolicy}</Badge> : <Badge tone="pass">no overlap</Badge>}>
        <p className="muted small">A blank cell matches any value. Rows that overlap are marked; under hit policy unique an overlap blocks publish.</p>
        <div className="table-wrap">
          <table className="grid compact dt-grid">
            <thead>
              <tr><th>#</th>{ins.map((i) => <th key={`i-${i.name}`} className="dt-in">{i.name}</th>)}{outs.map((o) => <th key={`o-${o}`} className="dt-out">{o}</th>)}<th>Reason</th><th></th></tr>
            </thead>
            <tbody>
              {rows.map((r, n) => (
                <tr key={n} className={inOverlap.has(n) ? (blocking ? 'row-fail' : 'row-warn') : ''}>
                  <td className="muted">{n + 1}{inOverlap.has(n) && <div className="small">overlaps {a.overlaps.filter((p) => p.includes(n)).map((p) => (p[0] === n ? p[1] : p[0]) + 1).join(', ')}</div>}</td>
                  {ins.map((i) => <td key={`i-${i.name}`}><Txt value={r.when?.[i.name]} onChange={(v) => set(['rows', n, 'when'], clean({ ...(r.when ?? {}), [i.name]: scalar(v) }) ?? {})} label={`Row ${n + 1} ${i.name}`} placeholder="any" size={10} /></td>)}
                  {outs.map((o) => <td key={`o-${o}`}><Txt value={r.then?.[o]} onChange={(v) => set(['rows', n, 'then'], clean({ ...(r.then ?? {}), [o]: scalar(v) }) ?? {})} label={`Row ${n + 1} ${o}`} size={14} /></td>)}
                  <td><Txt value={r.reason} onChange={(v) => set(['rows', n, 'reason'], v)} label={`Row ${n + 1} reason`} /></td>
                  <td className="nowrap">
                    <button className="link" disabled={n === 0} onClick={() => { const x = [...rows]; [x[n - 1], x[n]] = [x[n], x[n - 1]]; set(['rows'], x); }} aria-label="Move row up">↑</button>{' '}
                    <button className="link danger" onClick={() => set(['rows'], rows.filter((_, k) => k !== n))}>Remove</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <button onClick={() => set(['rows'], [...rows, { when: {}, then: {} }])}>Add a row</button>
        <h4>Gaps</h4>
        {a.hasCatchAll ? <p className="ok-line small">A catch-all row covers every other combination.</p> : !a.gaps.length ? <p className="ok-line small">No gap among the values the rows name.</p> : (
          <>
            <p className="small warn-text">{a.gaps.length} combination{a.gaps.length === 1 ? '' : 's'} no row covers. Add a row, or a catch-all row with every cell blank.</p>
            <div className="table-wrap" style={{ maxHeight: 200 }}>
              <table className="grid compact"><thead><tr>{ins.map((i) => <th key={i.name}>{i.name}</th>)}</tr></thead>
                <tbody>{a.gaps.slice(0, 40).map((g, k) => <tr key={k}>{ins.map((i) => <td key={i.name} className="mono small">{g[i.name]}</td>)}</tr>)}</tbody>
              </table>
            </div>
          </>
        )}
        {analysis && <p className="muted small">The stored release this was opened from: {analysis.overlaps.length} overlap(s), {analysis.gaps.length} gap(s), catch-all {analysis.hasCatchAll ? 'yes' : 'no'} (server analysis).</p>}
      </Section>
      <ParametersEditor params={doc.parameters} set={(v) => set(['parameters'], v)} />
      <ExamplesEditor examples={doc.examples} set={(v) => set(['examples'], v)} gates={gates} decisions />
    </>
  );
}

// ------------------------------------------------------------------ activity contract (C13-S3, C13-S4, C13-S6)

const TYPES = ['map', 'validate', 'enrich', 'screen', 'decide', 'reserve', 'exchange', 'post', 'notify', 'hold', 'reconcile'];

function ActivityForm({ doc, set, composite }: { doc: any; set: Set; composite?: boolean }) {
  const model = useModel();
  const ports = useCatalogue('port').map((p) => p.name);
  const rulesets = useCatalogue('ruleset').map((p) => p.name);
  const flat = model?.flat ?? [];
  const paths = flat.map((n) => n.path);
  const check = (p: string) => (!model ? undefined : resolves(p, flat) ? undefined : `${p} is not in the canonical model`);
  const outs: any[] = doc.outcomes ?? [];
  const errs: any[] = doc.errors ?? [];
  const primaries = outs.filter((o) => o.primary).length;
  const needsKey = ['write', 'external'].includes(doc.effect) && !doc.idempotencyKey;
  const pol = doc.policy ?? {};
  const isComp = doc.implementation?.binding === 'subflow';
  const nonReply = Object.keys(pol).filter((k) => !['replyTimeout', 'replyCalendar'].includes(k));
  const personal = (doc.reads ?? []).filter((r: string) => flat.find((n) => n.path === r)?.personalData);
  const problems: string[] = [];
  if (primaries !== 1) problems.push(`Exactly one outcome is primary; this has ${primaries}.`);
  if (needsKey) problems.push(`An activity with effect ${doc.effect} needs an idempotency key.`);
  if (doc.type === 'exchange' && (!doc.mode || !doc.ports?.length)) problems.push('An exchange needs a mode and its ports.');
  if (doc.type === 'map' && !doc.map) problems.push('A map needs its map section.');
  if (['validate', 'decide'].includes(doc.type) && !doc.ruleSets?.length && doc.effect !== 'read') problems.push(`A ${doc.type} block names its rule sets, or reads with effect read.`);
  if (isComp && nonReply.length) problems.push(`A composite's policy holds only replyTimeout and replyCalendar; remove ${nonReply.join(', ')} (R-03-21).`);
  if (composite && !isComp) problems.push('A composite has the implementation binding subflow.');
  return (
    <>
      <Section title={composite ? 'Composite contract' : doc.type === 'map' ? 'Map contract' : 'Activity contract'} actions={problems.length ? <Badge tone="fail">{problems.length} to fix</Badge> : <Badge tone="pass">form complete</Badge>}>
        {problems.length > 0 && <ul className="form-problems">{problems.map((p) => <li key={p}>{p}</li>)}</ul>}
        <Head doc={doc} set={set}>
          <label className="field"><span>Type</span><Sel value={doc.type} onChange={(v) => set(['type'], v)} options={TYPES} label="Activity type" /></label>
          {doc.type === 'exchange' && <label className="field"><span>Mode</span><Sel value={doc.mode} onChange={(v) => set(['mode'], v)} options={['send', 'respond', 'inquire', 'report', 'receive']} label="Mode" blank="choose" /></label>}
          <label className="field"><span>Level</span><Sel value={doc.level} onChange={(v) => set(['level'], v)} options={['payment', 'batch', 'item']} label="Activity level" blank="payment" /></label>
          <label className="field"><span>Invoked by</span><Sel value={doc.invokedBy} onChange={(v) => set(['invokedBy'], v)} options={['workflow', 'front-door', 'schedule']} label="Invoked by" blank="workflow" /></label>
          <label className="field"><span>Effect</span><Sel value={doc.effect} onChange={(v) => set(['effect'], v)} options={['pure', 'read', 'write', 'external']} label="Effect" /></label>
          <label className="field wide"><span>Idempotency key {needsKey && <b className="fail-text">required</b>}</span><Txt value={doc.idempotencyKey} onChange={(v) => set(['idempotencyKey'], v)} mono label="Idempotency key" placeholder="concat(header.paymentId, ':step')" /></label>
          <label className="field wide"><span>Compensation</span><Txt value={doc.compensation} onChange={(v) => set(['compensation'], v)} mono label="Compensation" placeholder="an activity that undoes this one" /></label>
        </Head>
        <h4>Reads</h4>
        <StringList value={doc.reads} onChange={(v) => set(['reads'], v)} suggestions={paths} check={check} placeholder="a context path, for example payment.instructedAmount" label="Add a read" />
        {personal.length > 0 && <p className="small warn-text">Reads personal data: {personal.join(', ')}. Keep it out of logs, tests and samples.</p>}
        <h4>Writes</h4>
        <StringList value={doc.writes} onChange={(v) => set(['writes'], v)} suggestions={paths} check={check} placeholder="a context path" label="Add a write" />
        <h4>Outcomes</h4>
        <table className="grid compact">
          <thead><tr><th>Primary</th><th>Name</th><th>Description</th><th></th></tr></thead>
          <tbody>
            {outs.map((o, i) => (
              <tr key={i}>
                <td><input type="radio" name="primary" checked={!!o.primary} onChange={() => set(['outcomes'], outs.map((x, n) => clean({ ...x, primary: n === i ? true : undefined })))} aria-label={`Primary ${o.name}`} /></td>
                <td><Txt value={o.name} onChange={(v) => set(['outcomes', i, 'name'], v ?? '')} mono label="Outcome name" /></td>
                <td><Txt value={o.description} onChange={(v) => set(['outcomes', i], clean({ ...o, description: v }))} label="Outcome description" /></td>
                <td><button className="link danger" onClick={() => set(['outcomes'], outs.filter((_, n) => n !== i))}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button onClick={() => set(['outcomes'], [...outs, { name: `outcome-${outs.length + 1}`, ...(outs.length ? {} : { primary: true }) }])}>Add an outcome</button>
        <p className="muted small">The primary outcome falls through to the next step. A workflow handles every other one.</p>
        <h4>Errors</h4>
        <table className="grid compact">
          <thead><tr><th>Name</th><th>Retryable</th><th>Reason code</th><th>Description</th><th></th></tr></thead>
          <tbody>
            {errs.map((x, i) => (
              <tr key={i}>
                <td><Txt value={x.name} onChange={(v) => set(['errors', i, 'name'], v ?? '')} mono label="Error name" /></td>
                <td><input type="checkbox" checked={!!x.retryable} onChange={(e) => set(['errors', i, 'retryable'], e.target.checked)} aria-label={`Retryable ${x.name}`} /></td>
                <td><Txt value={x.reasonCode} onChange={(v) => set(['errors', i], clean({ ...x, reasonCode: v }))} mono label="Error reason code" size={6} /></td>
                <td><Txt value={x.description} onChange={(v) => set(['errors', i], clean({ ...x, description: v }))} label="Error description" /></td>
                <td><button className="link danger" onClick={() => set(['errors'], errs.filter((_, n) => n !== i).length ? errs.filter((_, n) => n !== i) : undefined)}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button onClick={() => set(['errors'], [...errs, { name: 'TransientError', retryable: true }])}>Add an error</button>
        <h4>Ports</h4>
        <StringList value={doc.ports} onChange={(v) => set(['ports'], v)} suggestions={ports} check={(p) => (ports.length && !ports.includes(p) ? `${p} is not a published port` : undefined)} placeholder="a port" label="Add a port" />
        {['validate', 'decide', 'screen'].includes(doc.type) && (
          <>
            <h4>Rule sets</h4>
            <StringList value={doc.ruleSets} onChange={(v) => set(['ruleSets'], v)} suggestions={rulesets} placeholder="a rule set" label="Add a rule set" />
          </>
        )}
      </Section>
      {doc.type === 'map' && <MapSection doc={doc} set={set} paths={paths} />}
      <Section title="Policy">
        <p className="muted small">{isComp ? 'A composite holds only how long it waits for its reply. Its inner steps carry their own attempt and retry policy.' : 'Leave a field empty to take the type default.'}</p>
        <div className="form-grid">
          {!isComp && (
            <>
              <label className="field"><span>Attempt timeout</span><Txt value={pol.attemptTimeout} onChange={(v) => set(['policy'], orNone({ ...pol, attemptTimeout: v }))} mono label="Attempt timeout" placeholder="PT5S" /></label>
              <label className="field"><span>Total timeout</span><Txt value={pol.totalTimeout} onChange={(v) => set(['policy'], orNone({ ...pol, totalTimeout: v }))} mono label="Total timeout" /></label>
              <label className="field"><span>Max attempts</span><Txt value={pol.retry?.maxAttempts} onChange={(v) => set(['policy'], orNone({ ...pol, retry: v ? { ...(pol.retry ?? {}), maxAttempts: Number(v) } : undefined }))} label="Max attempts" /></label>
              <label className="field"><span>First interval</span><Txt value={pol.retry?.firstInterval} onChange={(v) => set(['policy'], orNone({ ...pol, retry: pol.retry ? clean({ ...pol.retry, firstInterval: v }) : undefined }))} mono label="First interval" /></label>
              <label className="field"><span>Backoff</span><Txt value={pol.retry?.backoff} onChange={(v) => set(['policy'], orNone({ ...pol, retry: pol.retry ? clean({ ...pol.retry, backoff: v ? Number(v) : undefined }) : undefined }))} label="Backoff" /></label>
            </>
          )}
          <label className="field"><span>Reply time</span><Txt value={pol.replyTimeout} onChange={(v) => set(['policy'], orNone({ ...pol, replyTimeout: v }))} mono label="Reply timeout" placeholder={isComp ? 'required when an await has none' : 'composites only'} /></label>
          <label className="field"><span>Reply calendar</span><Sel value={pol.replyCalendar} onChange={(v) => set(['policy'], orNone({ ...pol, replyCalendar: v }))} options={['elapsed', 'business']} label="Reply calendar" blank="elapsed" /></label>
        </div>
      </Section>
      <Section title="Implementation">
        <div className="form-grid">
          <label className="field"><span>Binding</span><Sel value={doc.implementation?.binding} onChange={(v) => set(['implementation'], clean({ binding: v, ...(v === 'subflow' ? { subflow: doc.implementation?.subflow ?? doc.name?.replace('.exchange.', '.sub.') } : { language: doc.implementation?.language, script: doc.implementation?.script, service: doc.implementation?.service }) }))} options={['script', 'native', 'builtin', 'subflow']} label="Binding" /></label>
          {doc.implementation?.binding === 'script' && (
            <>
              <label className="field"><span>Language</span><Sel value={doc.implementation?.language} onChange={(v) => set(['implementation', 'language'], v)} options={['typescript', 'python']} label="Language" blank="choose" /></label>
              <label className="field wide"><span>Script</span><Txt value={doc.implementation?.script} onChange={(v) => set(['implementation', 'script'], v)} mono label="Script" /></label>
            </>
          )}
          {['builtin', 'native'].includes(doc.implementation?.binding) && <label className="field wide"><span>Service</span><Txt value={doc.implementation?.service} onChange={(v) => set(['implementation', 'service'], v)} mono label="Service" /></label>}
          {isComp && <label className="field wide"><span>Subflow</span><Txt value={doc.implementation?.subflow} onChange={(v) => set(['implementation', 'subflow'], v)} mono label="Subflow" /></label>}
        </div>
        {doc.name && <p className="small"><a href={`#/data?tab=types&activity=${encodeURIComponent(doc.name)}`}>See the generated types for {doc.name}</a></p>}
      </Section>
      <ParametersEditor params={doc.parameters} set={(v) => set(['parameters'], v)} />
    </>
  );
}

function MapSection({ doc, set, paths }: { doc: any; set: Set; paths: string[] }) {
  const m = doc.map ?? {};
  const fields: any[] = m.fields ?? [];
  const dupTo = new Set(fields.map((f) => f.to).filter((t, i, a) => t && a.indexOf(t) !== i));
  const setM = (k: string, v: any) => set(['map'], clean({ ...m, [k]: v }));
  return (
    <Section title="Field mapping" count={fields.length}>
      <div className="form-grid">
        <label className="field"><span>Direction</span><Sel value={m.direction} onChange={(v) => setM('direction', v)} options={[['in', 'in: external to canonical'], ['out', 'out: canonical to external']]} label="Map direction" /></label>
        <label className="field wide"><span>Source model</span><Txt value={m.sourceModel} onChange={(v) => setM('sourceModel', v)} label="Source model" placeholder="WPS SIF" /></label>
        <label className="check"><input type="checkbox" checked={!!m.roundTrip} onChange={(e) => setM('roundTrip', e.target.checked || undefined)} /> Round trip tested</label>
      </div>
      <datalist id="map-paths">{paths.map((p) => <option key={p} value={p} />)}</datalist>
      <table className="grid compact">
        <thead><tr><th>{m.direction === 'out' ? 'From (canonical)' : 'From (external)'}</th><th>{m.direction === 'out' ? 'To (external)' : 'To (canonical)'}</th><th>Via</th><th></th><th></th></tr></thead>
        <tbody>
          {fields.map((f, i) => {
            const lossy = !!f.via || dupTo.has(f.to);
            return (
              <tr key={i} className={lossy ? 'row-warn' : ''}>
                <td><Txt value={f.from} onChange={(v) => set(['map', 'fields', i, 'from'], v ?? '')} mono label="From" list={m.direction === 'out' ? 'map-paths' : undefined} /></td>
                <td><Txt value={f.to} onChange={(v) => set(['map', 'fields', i, 'to'], v ?? '')} mono label="To" list={m.direction === 'out' ? undefined : 'map-paths'} /></td>
                <td><Txt value={f.via} onChange={(v) => set(['map', 'fields', i], clean({ ...f, via: v }))} mono label="Via" placeholder="a function" size={12} /></td>
                <td className="small">{lossy && <Badge tone="warn" title={dupTo.has(f.to) ? 'Two fields map to one target: the way back cannot tell them apart' : 'A function between the two sides: confirm the round trip gives the value back'}>may lose data</Badge>}</td>
                <td><button className="link danger" onClick={() => set(['map', 'fields'], fields.filter((_, n) => n !== i))}>Remove</button></td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <button onClick={() => set(['map', 'fields'], [...fields, { from: '', to: '' }])}>Add a field</button>
      <h4>Variants and edge rules</h4>
      <div className="form-grid">
        <label className="field wide"><span>Variants</span><StringList value={m.variants} onChange={(v) => setM('variants', v)} placeholder="variant" mono={false} /></label>
        <label className="field wide"><span>Edge rules</span><StringList value={m.edgeRules} onChange={(v) => setM('edgeRules', v)} placeholder="a rule set" /></label>
      </div>
      {!fields.length && <p className="muted small">This map is a script-behind with no field rows. Its tests below prove what it does.</p>}
    </Section>
  );
}

// ------------------------------------------------------------------ simulator (C13-S5)

const SIM_STEP_KEYS = ['expect', 'send', 'status', 'reasonCode', 'after', 'noReply', 'failTimes', 'duplicate'] as const;

function seedScenario(name: string, doc: any): any {
  const first = (doc.scenarios ?? [])[0]?.steps ?? [];
  const req = first.find((s: any) => s.expect)?.expect ?? 'request';
  const rep = first.find((s: any) => s.send)?.send ?? 'reply';
  const ask = { expect: req };
  switch (name) {
    case 'accept': return { name, steps: [ask, { send: rep, status: 'accepted' }] };
    case 'reject': return { name, steps: [ask, { send: rep, status: 'rejected', reasonCode: 'NARR' }] };
    case 'pending-then-final': return { name, steps: [ask, { send: rep, status: 'pending' }, { send: rep, status: 'accepted', after: 'PT30S' }] };
    case 'no-reply': return { name, steps: [ask, { noReply: true }] };
    case 'late-reply': return { name, steps: [ask, { send: rep, status: 'accepted', after: 'PT1H' }] };
    case 'duplicate-reply': return { name, steps: [ask, { send: rep, status: 'accepted', duplicate: true }] };
    case 'fail-twice-then-accept': return { name, steps: [{ ...ask, failTimes: 2 }, { send: rep, status: 'accepted' }] };
    case 'malformed-reply': return { name, steps: [ask, { send: rep, status: 'accepted' }], malformed: true };
  }
  return { name, steps: [ask] };
}

function SimulatorForm({ doc, set }: { doc: any; set: Set }) {
  const ports = useCatalogue('port');
  const rulesets = useCatalogue('ruleset').map((r) => r.name);
  const sc: any[] = doc.scenarios ?? [];
  const have = new Set(sc.map((s) => s.name));
  const missing = doc.direction === 'inward' ? [] : STANDARD.filter((s) => !have.has(s));
  return (
    <>
      <Section title="Simulator">
        <Head doc={doc} set={set}>
          <label className="field"><span>Direction</span><Sel value={doc.direction} onChange={(v) => set(['direction'], v)} options={[['outward', 'outward: the party the platform sends to'], ['inward', 'inward: the party that sends to the platform']]} label="Direction" /></label>
          <label className="field wide"><span>Port</span><Sel value={doc.port} onChange={(v) => set(['port'], v)} options={ports.map((p) => p.name)} label="Port" blank="choose" /></label>
          <label className="field wide"><span>Stands for</span><Txt value={doc.standsFor} onChange={(v) => set(['standsFor'], v)} label="Stands for" /></label>
          <label className="field"><span>State keyed by</span><Txt value={doc.state?.keyedBy} onChange={(v) => set(['state'], v ? { keyedBy: v } : undefined)} label="State keyed by" /></label>
        </Head>
        <h4>Self-check rule sets</h4>
        <StringList value={doc.selfCheck} onChange={(v) => set(['selfCheck'], v)} suggestions={rulesets} placeholder="a rule set every emitted message passes" />
      </Section>
      <Section title="Standard scenarios" actions={missing.length ? <Badge tone="fail">{missing.length} missing: publish is blocked</Badge> : <Badge tone="pass">all eight present</Badge>}>
        <div className="chips">
          {STANDARD.map((s) => (have.has(s) ? <Badge key={s} tone="pass">{s}</Badge> : <button key={s} className="chip-btn" onClick={() => set(['scenarios'], [...sc, seedScenario(s, doc)])} title="Add it from the standard set">+ {s}</button>))}
        </div>
        {missing.length > 1 && <div className="row-actions"><button onClick={() => set(['scenarios'], [...sc, ...missing.map((m) => seedScenario(m, doc))])}>Add every missing standard scenario</button></div>}
      </Section>
      <Section title="Scenarios" count={sc.length}>
        {sc.map((s, i) => (
          <div key={i} className="scenario">
            <div className="bench-row">
              <Txt value={s.name} onChange={(v) => set(['scenarios', i, 'name'], v ?? '')} mono label="Scenario name" />
              {STANDARD.includes(s.name) && <Badge tone="info">standard</Badge>}
              <label className="check small"><input type="checkbox" checked={!!s.malformed} onChange={(e) => set(['scenarios', i, 'malformed'], e.target.checked || undefined)} /> malformed (skips self-check)</label>
              <Txt value={s.description} onChange={(v) => set(['scenarios', i, 'description'], v)} label="Scenario description" placeholder="description" className="grow" />
              <button className="link danger" onClick={() => set(['scenarios'], sc.filter((_, n) => n !== i))}>Remove</button>
            </div>
            <div className="table-wrap">
              <table className="grid compact sim-steps">
                <thead><tr><th>#</th>{SIM_STEP_KEYS.map((k) => <th key={k}>{k}</th>)}<th></th></tr></thead>
                <tbody>
                  {(s.steps ?? []).map((st: any, j: number) => (
                    <tr key={j}>
                      <td className="muted">{j + 1}</td>
                      {SIM_STEP_KEYS.map((k) => (
                        <td key={k}>
                          {k === 'noReply' || k === 'duplicate' ? (
                            <input type="checkbox" checked={!!st[k]} onChange={(e) => set(['scenarios', i, 'steps', j], clean({ ...st, [k]: e.target.checked || undefined }))} aria-label={`${s.name} step ${j + 1} ${k}`} />
                          ) : (
                            <Txt value={st[k]} onChange={(v) => set(['scenarios', i, 'steps', j], clean({ ...st, [k]: k === 'failTimes' ? (v ? Number(v) : undefined) : v }))} mono label={`${s.name} step ${j + 1} ${k}`} size={k === 'expect' || k === 'send' ? 16 : 7} />
                          )}
                        </td>
                      ))}
                      <td><button className="link danger" onClick={() => set(['scenarios', i, 'steps'], (s.steps as any[]).filter((_, n) => n !== j))}>Remove</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <button onClick={() => set(['scenarios', i, 'steps'], [...(s.steps ?? []), { send: '', status: 'accepted' }])}>Add a step</button>
          </div>
        ))}
        <div className="row-actions"><button onClick={() => set(['scenarios'], [...sc, { name: `scenario-${sc.length + 1}`, steps: [{ expect: '' }] }])}>Add a scenario</button></div>
      </Section>
    </>
  );
}

// ------------------------------------------------------------------ composite subflow (C13-S6)

/** The subflow behind a composite, composed on the canvas where await and return are offered. */
function SubflowEditor({ act, sub, setSub }: { act: any; sub: any; setSub: (d: any) => void }) {
  const { tenant } = useStudio();
  const [draftKey, setDraftKey] = useState<string>();
  const [selected, setSelected] = useState<string>();
  const dsub = useDebounced(sub, 300);
  const [issues, setIssues] = useState<Issue[]>([]);
  const [err, setErr] = useState<string>();
  useEffect(() => {
    let live = true;
    post('/api/check', { doc: clean(dsub) }).then((r) => live && (setIssues(r.issues), setErr(undefined)), (e) => live && setErr(refusal(e)));
    return () => {
      live = false;
    };
  }, [dsub, tenant]);
  const resolver = useResolver(sub, tenant);
  const gates = useGates(clean(sub));
  const outs: string[] = (act?.outcomes ?? []).map((o: any) => o.name);
  const exit: string[] = sub.exit?.outcomes ?? [];
  const agree = outs.length === exit.length && outs.every((o) => exit.includes(o));
  const docs = useDocs([sub.implements?.activity]);
  void docs;
  return (
    <>
      <h3 className="sub-head">Subflow <span className="mono">{sub.name}</span></h3>
      {!agree && (
        <div className="alert warn small">
          The composite's outcomes ({outs.join(', ')}) and the subflow's ({exit.join(', ')}) differ. They must be the same (R-03-20, L30).{' '}
          <button className="link" onClick={() => setSub({ ...sub, exit: { ...(sub.exit ?? {}), outcomes: outs } })}>Take the composite's outcomes</button>
        </div>
      )}
      <div className="cols">
        <div className="col">
          <Section title="Canvas" actions={<Badge tone={issues.some((i) => i.severity === 'error') ? 'fail' : 'pass'}>{issues.filter((i) => i.severity === 'error').length} errors</Badge>}>
            <ErrorBox error={err} />
            <Canvas doc={sub} onChange={setSub} issues={issues} docs={resolver.docs} selected={selected} onSelect={setSelected} />
          </Section>
          <Section title="Checks on the subflow" count={issues.length}>
            <Issues issues={issues} onStep={setSelected} empty="No issues. L30, L31 and L32 pass." />
          </Section>
        </div>
        <div className="col side">
          <Gates state={gates} title="Gates of the subflow" />
          <Section title="Save and publish the subflow">
            <DraftActions doc={clean(sub)} draftKey={draftKey} setDraftKey={setDraftKey} label="subflow draft" />
          </Section>
          <YamlPane doc={sub} onDoc={setSub} />
        </div>
      </div>
    </>
  );
}

export const _internal = { getIn, Fragment };
