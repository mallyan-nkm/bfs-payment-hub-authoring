import { useEffect, useState } from 'react';
import YAML from 'yaml';
import { get, isDesigner, post, refusal, type Issue, type User } from '../api';
import { ArtefactLink, Badge, ErrorBox, Issues, KV, Loading, PassFail, Section, Val, useDebounced, useLoad, useStudio } from '../ui';
import { toYamlText } from '../authoring';
import { useAssist } from '../assist';

// The tenant view (F-26 to F-29): the configuration set, its parameters and hook points,
// an editor with checks, and the compatibility run.

export function Tenant() {
  const { tenant, meta } = useStudio();
  const banks = (meta?.tenants ?? []).filter((t) => t !== meta?.owner);
  const [who, setWho] = useState(tenant !== (meta?.owner ?? 'owner') ? tenant : banks[0] ?? '');
  useEffect(() => {
    if (!who && banks[0]) setWho(banks[0]);
  }, [banks, who]);
  const cfg = useLoad(() => (who ? get(`/api/tenants/${encodeURIComponent(who)}/configuration`) : Promise.resolve(undefined)), [who]);
  const [yaml, setYaml] = useState('');
  const [checked, setChecked] = useState<Issue[]>();
  const [checkErr, setCheckErr] = useState<string>();
  const [compat, setCompat] = useState<any>();
  const [compatBusy, setCompatBusy] = useState(false);
  const [compatErr, setCompatErr] = useState<string>();
  const [paramFilter, setParamFilter] = useState('');

  useEffect(() => {
    if (cfg.data) {
      setYaml(cfg.data.yaml ?? '');
      setChecked(undefined);
      setCompat(undefined);
    }
  }, [cfg.data]);

  const dYaml = useDebounced(yaml, 400);
  useEffect(() => {
    if (!who || !dYaml || dYaml === cfg.data?.yaml) {
      setChecked(undefined);
      return;
    }
    let live = true;
    post(`/api/tenants/${encodeURIComponent(who)}/configuration/validate`, { yaml: dYaml }, who)
      .then((r) => live && (setChecked(r.issues), setCheckErr(undefined)))
      .catch((e) => live && setCheckErr(e.message));
    return () => {
      live = false;
    };
  }, [dYaml, who, cfg.data]);

  let cfgDoc: any;
  try {
    cfgDoc = yaml ? YAML.parse(yaml) : undefined;
  } catch {
    cfgDoc = undefined;
  }
  useAssist({ surface: 'tenant', kind: 'configuration-set', doc: cfgDoc, setDoc: (d) => setYaml(toYamlText(d)), tenant: who || undefined, topic: who });
  const [candidates, setCandidates] = useState('');
  const bankUsers = useLoad(() => (who ? get<User[]>('/api/users', who) : Promise.resolve([])), [who]);
  const [saveAs, setSaveAs] = useState('');
  useEffect(() => {
    const us = bankUsers.data ?? [];
    if (!us.some((u) => u.id === saveAs)) setSaveAs((us.find(isDesigner) ?? us[0])?.id ?? '');
  }, [bankUsers.data, saveAs]);
  const [saveMsg, setSaveMsg] = useState<{ tone: string; text: string }>();
  const settable = useLoad(() => (who ? get<any[]>(`/api/tenants/${encodeURIComponent(who)}/settable`, who) : Promise.resolve([])), [who]);
  const exp = useLoad(() => (who ? get(`/api/tenants/${encodeURIComponent(who)}/export`, who) : Promise.resolve(undefined)), [who]);
  const save = async () => {
    setSaveMsg(undefined);
    try {
      const r = await post(`/api/tenants/${encodeURIComponent(who)}/configuration`, { actor: saveAs, yaml }, who);
      setSaveMsg({ tone: 'pass', text: `Saved ${r.saved.key} as ${saveAs}.` });
      cfg.reload();
      exp.reload();
    } catch (e: any) {
      setSaveMsg({ tone: 'fail', text: refusal(e) });
    }
  };
  /** Change the YAML through its document, so a form action keeps the editor and the checks in step. */
  const edit = (fn: (d: any) => void) => {
    try {
      const d = YAML.parse(yaml) ?? {};
      fn(d);
      setYaml(toYamlText(d));
    } catch (e: any) {
      setCheckErr(`The YAML does not parse, so the form cannot change it: ${e.message}`);
    }
  };
  const bump = () => edit((d) => {
    const [a, b, c] = String(d.release ?? '1.0.0').split('.').map(Number);
    d.release = `${a}.${b}.${(c || 0) + 1}`;
  });
  const addValue = (p: any) => edit((d) => {
    d.values = [...(d.values ?? []), { parameter: p.parameter, value: p.default, validFrom: '2026-11-17' }];
  });
  const runCompat = async () => {
    setCompatBusy(true);
    setCompatErr(undefined);
    try {
      const docs = candidates.trim() ? YAML.parseAllDocuments(candidates).map((d) => d.toJSON()).filter(Boolean) : [];
      setCompat(await post(`/api/tenants/${encodeURIComponent(who)}/compatibility-run`, { candidates: docs }, who));
    } catch (e: any) {
      setCompatErr(e.message);
    } finally {
      setCompatBusy(false);
    }
  };

  const d = cfg.data;
  const params = (d?.parameters ?? []).filter((p: any) => !paramFilter || p.parameter.toLowerCase().includes(paramFilter.toLowerCase()) || (paramFilter === 'set' && p.values.length));
  const editedIssues = checked;

  return (
    <div className="view">
      <div className="view-head">
        <h2>Tenant</h2>
        <p className="muted">A bank sees shared content read-only. It sets parameter values inside bounds and binds hooks to declared hook points, with effective dates.</p>
      </div>
      <div className="toolbar">
        <label className="field-inline">
          <span>Bank</span>
          <select value={who} onChange={(e) => setWho(e.target.value)} aria-label="Bank">
            {banks.map((b) => <option key={b}>{b}</option>)}
          </select>
        </label>
        {tenant === meta?.owner && <span className="muted small">You act as the shared content owner. Switch the tenant at the top to act for a bank.</span>}
      </div>
      <ErrorBox error={cfg.error} />
      {cfg.loading && !d ? <Loading what="the configuration set" /> : d && (
        <>
          {!d.configurationSet && <div className="alert warn">{who} has no configuration set.</div>}
          {d.configurationSet && (
            <Section title={<>Configuration set <span className="mono">{d.configurationSet.title ?? ''}</span></>} actions={<Badge>release {d.configurationSet.release}</Badge>}>
              <div className="cols">
                <div className="col">
                  <h4>Accepted shared releases</h4>
                  <div className="chips">{(d.configurationSet.accepts ?? []).map((a: any) => <span key={a.scope} className="chip mono">{a.scope} {a.release}</span>)}</div>
                </div>
                <div className="col">
                  <h4>Approval</h4>
                  {d.configurationSet.approval ? <span>maker <b>{d.configurationSet.approval.maker}</b>, checker <b>{d.configurationSet.approval.checker}</b></span> : <span className="muted">none</span>}
                </div>
              </div>
              <h4>Issues in the stored set</h4>
              <Issues issues={d.issues} empty="No issues." />
            </Section>
          )}

          <Section title="Parameters" count={d.parameters.length} actions={<input type="search" placeholder="Filter, or type set" value={paramFilter} onChange={(e) => setParamFilter(e.target.value)} aria-label="Filter parameters" />}>
            <div className="table-wrap">
              <table className="grid compact">
                <thead><tr><th>Parameter</th><th>Type</th><th>Default</th><th>Bounds</th><th>Values for {who}</th></tr></thead>
                <tbody>
                  {params.map((p: any) => (
                    <tr key={p.parameter}>
                      <td><ArtefactLink name={p.owner} /><div className="mono small"># {p.name}</div>{p.description && <div className="muted small">{p.description}</div>}</td>
                      <td>{p.type}{p.currency ? ` ${p.currency}` : ''}</td>
                      <td><Val v={p.default} /></td>
                      <td>{p.bounds ? <Val v={p.bounds} /> : <span className="muted">none</span>}</td>
                      <td>
                        {p.values.length ? (
                          <ul className="plain">{p.values.map((v: any, i: number) => <li key={i}><Val v={v.value} /> <span className="muted small">from {v.validFrom}{v.validTo ? ` to ${v.validTo}` : ''}</span></li>)}</ul>
                        ) : <span className="muted small">default</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Section>

          <Section title="Hook points" count={d.hookPoints.length}>
            <div className="table-wrap">
              <table className="grid compact">
                <thead><tr><th>Hook point</th><th>Type</th><th>Contract</th><th>Policy</th><th>Bindings for {who}</th></tr></thead>
                <tbody>
                  {d.hookPoints.map((h: any) => (
                    <tr key={h.hookPoint}>
                      <td><ArtefactLink name={h.owner} /><div className="mono small"># {h.name}</div>{h.description && <div className="muted small">{h.description}</div>}</td>
                      <td><Badge tone="info">{h.type}</Badge></td>
                      <td className="small">
                        {h.contract?.reads && <div>reads <code>{h.contract.reads.join(', ')}</code></div>}
                        {h.contract?.writes && <div>writes <code>{h.contract.writes.join(', ')}</code></div>}
                      </td>
                      <td className="small"><Val v={h.policy} /></td>
                      <td>{h.bindings.length ? <ul className="plain">{h.bindings.map((b: any, i: number) => <li key={i}><ArtefactLink name={b.hook} release={b.release} /> <span className="muted small">from {b.validFrom}{b.validTo ? ` to ${b.validTo}` : ''}</span></li>)}</ul> : <span className="muted small">not bound</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Section>

          <div className="cols">
            <div className="col">
              <Section title="Edit the configuration set" actions={yaml !== d.yaml ? <button onClick={() => setYaml(d.yaml)}>Reset</button> : undefined}>
                <p className="muted small">Change a value or a binding, with its effective dates. The set is validated on every edit: a value outside its bounds or an overlapping period shows L14. Save writes a new release of the set.</p>
                <textarea className="yaml-editor short" spellCheck={false} value={yaml} onChange={(e) => setYaml(e.target.value)} aria-label="Configuration set YAML" />
                <ErrorBox error={checkErr} />
                {editedIssues ? <Issues issues={editedIssues} empty="The edited set passes every check." /> : <p className="muted small">{yaml === d.yaml ? 'Unchanged.' : 'Validating…'}</p>}
                <div className="row-actions">
                  <button onClick={bump}>Bump the release</button>
                  <label className="field-inline">
                    <span>Save as</span>
                    <select value={saveAs} onChange={(e) => setSaveAs(e.target.value)} aria-label="Save as">
                      {(bankUsers.data ?? []).map((u) => <option key={u.id} value={u.id}>{u.id} ({u.roles.join(', ')})</option>)}
                    </select>
                  </label>
                  <button className="primary" disabled={!saveAs || yaml === d.yaml || !!editedIssues?.some((i) => i.severity === 'error')} onClick={save}>Save the configuration set</button>
                </div>
                {saveMsg && <div className={`alert ${saveMsg.tone}`} role="status">{saveMsg.text}</div>}
              </Section>
              <Section title="Parameters this bank may set" count={settable.data?.length}>
                <ErrorBox error={settable.error} />
                <div className="table-wrap tall">
                  <table className="grid compact">
                    <thead><tr><th>Parameter</th><th>Type</th><th>Default</th><th>Bounds</th><th></th></tr></thead>
                    <tbody>
                      {(settable.data ?? []).map((p) => (
                        <tr key={p.parameter}>
                          <td><span className="mono small">{p.parameter}</span>{p.description && <div className="muted small">{p.description}</div>}</td>
                          <td>{p.type}{p.currency ? ` ${p.currency}` : ''}</td>
                          <td><Val v={p.default} /></td>
                          <td>{p.bounds ? <Val v={p.bounds} /> : <span className="muted">none</span>}</td>
                          <td><button className="link" onClick={() => addValue(p)}>Set a value</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </Section>
            </div>
            <div className="col side">
              <Section title="Compatibility run" actions={<button className="primary" onClick={runCompat} disabled={compatBusy || !who}>{compatBusy ? 'Running…' : 'Run'}</button>}>
                <p className="muted small">Before {who} accepts a shared release: each bound hook point must still exist with a compatible contract, and each of the bank's hook tests must pass. Paste candidate releases as YAML, separated by ---; leave it empty to run against the current releases.</p>
                <textarea className="code-input" rows={6} value={candidates} onChange={(e) => setCandidates(e.target.value)} spellCheck={false} placeholder={'kind: workflow\nname: p.wf.corporate.v1\nrelease: 1.1.0\n...'} aria-label="Candidate releases YAML" />
                <ErrorBox error={compatErr} />
                {compat && (
                  <>
                    <p>{compat.accepted ? <Badge tone="pass">may accept</Badge> : <Badge tone="fail">do not accept</Badge>} {compat.candidates?.length ? <>against {compat.candidates.join(', ')}</> : 'against the current releases'}</p>
                    <h4>Hook bindings</h4>
                    {!compat.bindings?.length ? <p className="muted small">No hook is bound.</p> : (
                      <table className="grid compact">
                        <thead><tr><th>Hook point</th><th>Hook</th><th>Compatible</th></tr></thead>
                        <tbody>{compat.bindings.map((b: any) => <tr key={b.hookPoint} className={b.compatible ? '' : 'row-fail'}><td className="mono small break">{b.hookPoint}</td><td className="mono small break">{b.hook}</td><td>{b.compatible ? <Badge tone="pass">yes</Badge> : <><Badge tone="fail">no</Badge><div className="small">{b.reason}</div></>}</td></tr>)}</tbody>
                      </table>
                    )}
                    <h4>Hook tests</h4>
                    <table className="grid compact">
                      <thead><tr><th>Test</th><th>Outcome</th><th>Result</th></tr></thead>
                      <tbody>
                        {compat.results.map((r: any) => (
                          <tr key={r.id}>
                            <td className="mono"><a href={`#/tests?run=${encodeURIComponent(r.id)}`}>{r.id}</a><div className="small muted break">{r.target}</div></td>
                            <td><code>{r.outcome}</code></td>
                            <td><PassFail passed={r.passed} />{r.failed?.length > 0 && <div className="small">{r.failed.map((c: any) => c.what).join(', ')}</div>}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </>
                )}
              </Section>
              <Section title="Tenant export">
                <ErrorBox error={exp.error} />
                {exp.data && (
                  <>
                    <p className="muted small">The shared packs {who} accepted, by hash and unchanged, its bank pack and its configuration set.</p>
                    <table className="grid compact">
                      <thead><tr><th>Pack</th><th>Release</th><th>Files</th><th>Hash</th></tr></thead>
                      <tbody>
                        {exp.data.shared.map((x: any) => <tr key={x.scope}><td className="mono">{x.scope}</td><td className="mono">{x.release}</td><td>{x.files}</td><td className="mono small" title={x.hash}>{x.hash.replace('sha256:', '').slice(0, 12)}</td></tr>)}
                        <tr><td className="mono">b (bank)</td><td></td><td>{exp.data.bank.files}</td><td className="mono small" title={exp.data.bank.hash}>{exp.data.bank.hash.replace('sha256:', '').slice(0, 12)}</td></tr>
                      </tbody>
                    </table>
                    <KV rows={[['Configuration set', exp.data.configuration ? <code>release {exp.data.configuration.release}</code> : <span className="muted">none</span>]]} />
                  </>
                )}
              </Section>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
