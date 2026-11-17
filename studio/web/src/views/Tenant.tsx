import { useEffect, useState } from 'react';
import { get, post, type Issue } from '../api';
import { ArtefactLink, Badge, ErrorBox, Issues, Loading, PassFail, Section, Val, useDebounced, useLoad, useStudio } from '../ui';

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
    post(`/api/tenants/${encodeURIComponent(who)}/configuration/check`, { yaml: dYaml }, who)
      .then((r) => live && (setChecked(r.issues), setCheckErr(undefined)))
      .catch((e) => live && setCheckErr(e.message));
    return () => {
      live = false;
    };
  }, [dYaml, who, cfg.data]);

  const runCompat = async () => {
    setCompatBusy(true);
    setCompatErr(undefined);
    try {
      setCompat(await post(`/api/tenants/${encodeURIComponent(who)}/compatibility`, {}, who));
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
              <Section title="Edit and check the configuration set" actions={yaml !== d.yaml ? <button onClick={() => setYaml(d.yaml)}>Reset</button> : undefined}>
                <p className="muted small">Change a value or a binding. The set is checked as you type; a value outside its bounds shows L14. The check does not save.</p>
                <textarea className="yaml-editor short" spellCheck={false} value={yaml} onChange={(e) => setYaml(e.target.value)} aria-label="Configuration set YAML" />
                <ErrorBox error={checkErr} />
                {editedIssues ? <Issues issues={editedIssues} empty="The edited set passes every check." /> : <p className="muted small">{yaml === d.yaml ? 'Unchanged.' : 'Checking…'}</p>}
              </Section>
            </div>
            <div className="col side">
              <Section title="Compatibility run" actions={<button className="primary" onClick={runCompat} disabled={compatBusy || !who}>{compatBusy ? 'Running…' : 'Run'}</button>}>
                <p className="muted small">The bank's own tests, against the current shared releases, before it accepts them.</p>
                <ErrorBox error={compatErr} />
                {compat && (
                  <>
                    <p>{compat.accepted ? <Badge tone="pass">every test passes</Badge> : <Badge tone="fail">a test fails</Badge>} {compat.results.length} test{compat.results.length === 1 ? '' : 's'}</p>
                    <table className="grid compact">
                      <thead><tr><th>Test</th><th>Target</th><th>Outcome</th><th>Result</th></tr></thead>
                      <tbody>
                        {compat.results.map((r: any) => (
                          <tr key={r.id}>
                            <td className="mono"><a href={`#/tests?run=${encodeURIComponent(r.id)}`}>{r.id}</a></td>
                            <td className="mono small break">{r.target}</td>
                            <td><code>{r.outcome}</code></td>
                            <td><PassFail passed={r.passed} />{r.failed?.length > 0 && <div className="small">{r.failed.map((c: any) => c.what).join(', ')}</div>}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
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
