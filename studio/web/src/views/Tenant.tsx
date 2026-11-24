import { useEffect, useState } from 'react';
import YAML from 'yaml';
import { get, post, refusal, type Issue } from '../api';
import { Alert, ArtefactLink, Badge, ErrorBox, Issues, KV, Loading, PassFail, Section, Val, useDebounced, useLoad, useStudio } from '../ui';
import { toYamlText } from '../authoring';
import { nextRelease, patchAllowed } from '../release';
import { useAssist } from '../assist';
import { Button, Link, SearchBox, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';

// The tenant view (F-26 to F-29): the configuration set, its parameters and hook points,
// an editor with checks, and the compatibility run.

export function Tenant() {
  const { tenant, meta } = useStudio();
  // A bank's own configuration only: the signed-in user's tenant (R-06-01).
  const who = tenant !== (meta?.owner ?? 'owner') ? tenant : '';
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
  const { actor, roles } = useStudio();
  const mayConfigure = roles.includes('bank configurator');
  const [saveMsg, setSaveMsg] = useState<{ tone: string; text: string }>();
  const settable = useLoad(() => (who ? get<any[]>(`/api/tenants/${encodeURIComponent(who)}/settable`, who) : Promise.resolve([])), [who]);
  const exp = useLoad(() => (who ? get(`/api/tenants/${encodeURIComponent(who)}/export`, who) : Promise.resolve(undefined)), [who]);
  const save = async () => {
    setSaveMsg(undefined);
    try {
      const r = await post(`/api/tenants/${encodeURIComponent(who)}/configuration`, { yaml }, who);
      setSaveMsg({ tone: 'pass', text: `Saved ${r.draft.key} as ${actor}, as a draft. A bank approver who is not you approves it as a version in Governance.` });
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
  // A bank's configuration set takes the next minor, never a patch (lint L53, R-11-102); elsewhere the next patch.
  const bump = () => edit((d) => {
    d.release = nextRelease(d.release, patchAllowed(d.kind ?? 'configuration-set'));
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
        <span className="field-inline"><span>Bank</span> <b className="mono">{who || 'none'}</b></span>
        {tenant === meta?.owner && <span className="muted small">You work for the shared content owner. A bank's configuration is its own: its configurator and approver sign in to see it.</span>}
      </div>
      <ErrorBox error={cfg.error} />
      {cfg.loading && !d ? <Loading what="the configuration set" /> : d && (
        <>
          {!d.configurationSet && <Alert tone="warn">{who} has no configuration set.</Alert>}
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

          <Section title="Parameters" count={d.parameters.length} actions={<SearchBox placeholder="Filter, or type set" value={paramFilter} onChange={(_, d) => setParamFilter(d.value)} aria-label="Filter parameters" />}>
            <div className="table-wrap">
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Parameter</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Default</TableHeaderCell><TableHeaderCell>Bounds</TableHeaderCell><TableHeaderCell>Values for {who}</TableHeaderCell></TableRow></TableHeader>
                <TableBody>
                  {params.map((p: any) => (
                    <TableRow key={p.parameter}>
                      <TableCell><ArtefactLink name={p.owner} /><div className="mono small"># {p.name}</div>{p.description && <div className="muted small">{p.description}</div>}</TableCell>
                      <TableCell>{p.type}{p.currency ? ` ${p.currency}` : ''}</TableCell>
                      <TableCell><Val v={p.default} /></TableCell>
                      <TableCell>{p.bounds ? <Val v={p.bounds} /> : <span className="muted">none</span>}</TableCell>
                      <TableCell>
                        {p.values.length ? (
                          <ul className="plain">{p.values.map((v: any, i: number) => <li key={i}><Val v={v.value} /> <span className="muted small">from {v.validFrom}{v.validTo ? ` to ${v.validTo}` : ''}</span></li>)}</ul>
                        ) : <span className="muted small">default</span>}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </Section>

          <Section title="Hook points" count={d.hookPoints.length}>
            <div className="table-wrap">
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Hook point</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Contract</TableHeaderCell><TableHeaderCell>Policy</TableHeaderCell><TableHeaderCell>Bindings for {who}</TableHeaderCell></TableRow></TableHeader>
                <TableBody>
                  {d.hookPoints.map((h: any) => (
                    <TableRow key={h.hookPoint}>
                      <TableCell><ArtefactLink name={h.owner} /><div className="mono small"># {h.name}</div>{h.description && <div className="muted small">{h.description}</div>}</TableCell>
                      <TableCell><Badge tone="info">{h.type}</Badge></TableCell>
                      <TableCell className="small">
                        {h.contract?.reads && <div>reads <code>{h.contract.reads.join(', ')}</code></div>}
                        {h.contract?.writes && <div>writes <code>{h.contract.writes.join(', ')}</code></div>}
                      </TableCell>
                      <TableCell className="small"><Val v={h.policy} /></TableCell>
                      <TableCell>{h.bindings.length ? <ul className="plain">{h.bindings.map((b: any, i: number) => <li key={i}><ArtefactLink name={b.hook} release={b.release} /> <span className="muted small">from {b.validFrom}{b.validTo ? ` to ${b.validTo}` : ''}</span></li>)}</ul> : <span className="muted small">not bound</span>}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </Section>

          <div className="cols">
            <div className="col">
              <Section title="Edit the configuration set" actions={yaml !== d.yaml ? <Button onClick={() => setYaml(d.yaml)}>Reset</Button> : undefined}>
                <p className="muted small">Change a value or a binding, with its effective dates. The set is validated on every edit: a value outside its bounds or an overlapping period shows L14. Save writes a new release of the set as a draft; a bank approver makes it the version the runtime is given.</p>
                <Textarea className="yaml-editor short" spellCheck={false} value={yaml} onChange={(e) => setYaml(e.target.value)} aria-label="Configuration set YAML" />
                <ErrorBox error={checkErr} />
                {editedIssues ? <Issues issues={editedIssues} empty="The edited set passes every check." /> : <p className="muted small">{yaml === d.yaml ? 'Unchanged.' : 'Validating…'}</p>}
                <div className="row-actions">
                  <Button onClick={bump}>Bump the release</Button>
                  <Button appearance="primary" disabled={!mayConfigure || yaml === d.yaml || !!editedIssues?.some((i) => i.severity === 'error')} onClick={save}>Save the configuration set</Button>
                </div>
                {saveMsg && <div className={`alert ${saveMsg.tone}`} role="status">{saveMsg.text}</div>}
              </Section>
              <Section title="Parameters this bank may set" count={settable.data?.length}>
                <ErrorBox error={settable.error} />
                <div className="table-wrap tall">
                  <Table size="small" className="grid compact">
                    <TableHeader><TableRow><TableHeaderCell>Parameter</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Default</TableHeaderCell><TableHeaderCell>Bounds</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
                    <TableBody>
                      {(settable.data ?? []).map((p) => (
                        <TableRow key={p.parameter}>
                          <TableCell><span className="mono small">{p.parameter}</span>{p.description && <div className="muted small">{p.description}</div>}</TableCell>
                          <TableCell>{p.type}{p.currency ? ` ${p.currency}` : ''}</TableCell>
                          <TableCell><Val v={p.default} /></TableCell>
                          <TableCell>{p.bounds ? <Val v={p.bounds} /> : <span className="muted">none</span>}</TableCell>
                          <TableCell><Link as="button" onClick={() => addValue(p)}>Set a value</Link></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </div>
              </Section>
            </div>
            <div className="col side">
              <Section title="Compatibility run" actions={<Button appearance="primary" onClick={runCompat} disabled={compatBusy || !who}>{compatBusy ? 'Running…' : 'Run'}</Button>}>
                <p className="muted small">Before {who} accepts a shared release: each bound hook point must still exist with a compatible contract, and each of the bank's hook tests must pass. Paste candidate releases as YAML, separated by ---; leave it empty to run against the current releases.</p>
                <Textarea className="code-input" rows={6} value={candidates} onChange={(e) => setCandidates(e.target.value)} spellCheck={false} placeholder={'kind: workflow\nname: p.wf.corporate.v1\nrelease: 1.1.0\n...'} aria-label="Candidate releases YAML" />
                <ErrorBox error={compatErr} />
                {compat && (
                  <>
                    <p>{compat.accepted ? <Badge tone="pass">may accept</Badge> : <Badge tone="fail">do not accept</Badge>} {compat.candidates?.length ? <>against {compat.candidates.join(', ')}</> : 'against the current releases'}</p>
                    <h4>Hook bindings</h4>
                    {!compat.bindings?.length ? <p className="muted small">No hook is bound.</p> : (
                      <Table size="small" className="grid compact">
                        <TableHeader><TableRow><TableHeaderCell>Hook point</TableHeaderCell><TableHeaderCell>Hook</TableHeaderCell><TableHeaderCell>Compatible</TableHeaderCell></TableRow></TableHeader>
                        <TableBody>{compat.bindings.map((b: any) => <TableRow key={b.hookPoint} className={b.compatible ? '' : 'row-fail'}><TableCell className="mono small break">{b.hookPoint}</TableCell><TableCell className="mono small break">{b.hook}</TableCell><TableCell>{b.compatible ? <Badge tone="pass">yes</Badge> : <><Badge tone="fail">no</Badge><div className="small">{b.reason}</div></>}</TableCell></TableRow>)}</TableBody>
                      </Table>
                    )}
                    <h4>Hook tests</h4>
                    <Table size="small" className="grid compact">
                      <TableHeader><TableRow><TableHeaderCell>Test</TableHeaderCell><TableHeaderCell>Outcome</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell></TableRow></TableHeader>
                      <TableBody>
                        {compat.results.map((r: any) => (
                          <TableRow key={r.id}>
                            <TableCell className="mono"><a href={`#/tests?run=${encodeURIComponent(r.id)}`}>{r.id}</a><div className="small muted break">{r.target}</div></TableCell>
                            <TableCell><code>{r.outcome}</code></TableCell>
                            <TableCell><PassFail passed={r.passed} />{r.failed?.length > 0 && <div className="small">{r.failed.map((c: any) => c.what).join(', ')}</div>}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </>
                )}
              </Section>
              <Section title="Tenant export">
                <ErrorBox error={exp.error} />
                {exp.data && (
                  <>
                    <p className="muted small">The shared packs {who} accepted, by hash and unchanged, its bank pack and its configuration set.</p>
                    <Table size="small" className="grid compact">
                      <TableHeader><TableRow><TableHeaderCell>Pack</TableHeaderCell><TableHeaderCell>Release</TableHeaderCell><TableHeaderCell>Files</TableHeaderCell><TableHeaderCell>Hash</TableHeaderCell></TableRow></TableHeader>
                      <TableBody>
                        {exp.data.shared.map((x: any) => <TableRow key={x.scope}><TableCell className="mono">{x.scope}</TableCell><TableCell className="mono">{x.release}</TableCell><TableCell>{x.files}</TableCell><TableCell className="mono small" title={x.hash}>{x.hash.replace('sha256:', '').slice(0, 12)}</TableCell></TableRow>)}
                        <TableRow><TableCell className="mono">b (bank)</TableCell><TableCell></TableCell><TableCell>{exp.data.bank.files}</TableCell><TableCell className="mono small" title={exp.data.bank.hash}>{exp.data.bank.hash.replace('sha256:', '').slice(0, 12)}</TableCell></TableRow>
                      </TableBody>
                    </Table>
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
