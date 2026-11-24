import { useEffect, useState } from 'react';
import { APPROVER_ROLES, MAKER_ROLES, del, downloadUrl, get, post, q, refusal, type Issue, type Summary, type User } from '../api';
import { Alert, ArtefactLink, Badge, ErrorBox, Issues, KV, List, Loading, PassFail, Section, Tabs, Val, useLoad, useStudio } from '../ui';
import { useCatalogue } from '../authoring';
import { Button, Checkbox, Input, Link, SearchBox, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea } from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';

// Governance (F-24, F-25, F-32): drafts, the audit log, supersessions and the whole-store checks.

type Tab = 'drafts' | 'users' | 'releases' | 'supersessions' | 'packs' | 'conformance' | 'audit' | 'coverage' | 'bindings' | 'checks';
const TABS: { id: Tab; label: string }[] = [
  { id: 'drafts', label: 'Review and approve' },
  { id: 'users', label: 'Users and roles' },
  { id: 'releases', label: 'Pins, retirement, propagation' },
  { id: 'supersessions', label: 'Supersessions' },
  { id: 'packs', label: 'Packs' },
  { id: 'conformance', label: 'Conformance' },
  { id: 'audit', label: 'Audit log' },
  { id: 'coverage', label: 'Coverage' },
  { id: 'bindings', label: 'Bindings and stubs' },
  { id: 'checks', label: 'Store checks' },
];

export function Governance({ query }: { query: URLSearchParams }) {
  const { go } = useStudio();
  const tab = (TABS.find((t) => t.id === query.get('tab'))?.id ?? 'drafts') as Tab;
  return (
    <div className="view">
      <div className="view-head">
        <h2>Governance</h2>
        <p className="muted">Who may do what, drafts in review, which releases are pinned and which may retire, supersessions, packs and their release gate, conformance, the audit log and coverage.</p>
      </div>
      <Tabs tabs={TABS} value={tab} onChange={(t) => go(`/governance?tab=${t}`)} />
      {tab === 'drafts' && <Drafts />}
      {tab === 'users' && <Users />}
      {tab === 'releases' && <Releases initial={query.get('name') ?? ''} />}
      {tab === 'supersessions' && <><SupersessionWizard /><Supersessions /></>}
      {tab === 'packs' && <Packs />}
      {tab === 'conformance' && <Conformance />}
      {tab === 'audit' && <Audit />}
      {tab === 'coverage' && <Coverage />}
      {tab === 'bindings' && <Bindings />}
      {tab === 'checks' && <StoreChecks />}
    </div>
  );
}

/**
 * Who an action is taken as: always the signed-in user (R-11-32). There is no picker: the server takes
 * the user from the session. `prefer` says which role the action needs, to tell the user beforehand.
 */
export function AsUser({ value, onChange, prefer, label = 'As' }: { value: string; onChange: (v: string) => void; prefer: (u: User) => boolean; label?: string }) {
  const { actor, roles, tenant } = useStudio();
  useEffect(() => {
    if (value !== actor) onChange(actor);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [actor]);
  const may = prefer({ id: actor, tenant, roles });
  return (
    <span className="field-inline" aria-label={label}>
      <span>{label}</span> <b className="mono">{actor}</b>
      {!may && <Badge tone="warn" title="Your roles do not include the one this needs; the server will refuse">not your role</Badge>}
    </span>
  );
}

const may = (...roles: string[]) => (u: User) => u.roles.some((r) => roles.includes(r));

function Drafts() {
  const { tenant, actor, users, roles, go } = useStudio();
  const drafts = useLoad(() => get<Summary[]>('/api/drafts'), [tenant]);
  // The change set the studio generates for each draft: what approving it publishes (R-11-19).
  const sets = useLoad(async () => {
    const out: Record<string, string[]> = {};
    for (const d of drafts.data ?? []) out[d.key] = ((await get<any>(`/api/change-set${q({ key: d.key })}`)).members ?? []).map((m: any) => m.key);
    return out;
  }, [drafts.data]);
  const [error, setError] = useState<string>();
  const [msg, setMsg] = useState<{ tone: string; text: string }>();
  const [checker, setChecker] = useState('');
  const [busy, setBusy] = useState<string>();
  const remove = async (key: string) => {
    if (!window.confirm(`Delete draft ${key}?`)) return;
    try {
      await del(`/api/drafts/${encodeURIComponent(key)}`);
      drafts.reload();
    } catch (e: any) {
      setError(refusal(e));
    }
  };
  const act = async (key: string, what: 'review' | 'publish') => {
    setBusy(key);
    setMsg(undefined);
    try {
      if (what === 'review') {
        await post('/api/request-release', { key });
        setMsg({ tone: 'pass', text: `Release requested for the change set of ${key}, by ${actor}.` });
      } else {
        const r = await post('/api/publish', { key });
        setMsg({ tone: 'pass', text: `Change set ${r.changeSet.id} approved as a version by ${actor}: ${r.published.map((p: any) => p.key).join(', ')}.` });
      }
      drafts.reload();
    } catch (e: any) {
      setMsg({ tone: 'fail', text: `${key}: ${refusal(e)}` });
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <Section title={`Drafts of ${tenant}`} count={drafts.data?.length} actions={<AsUser value={checker} onChange={setChecker} prefer={(u) => u.roles.some((r) => r.endsWith('approver'))} label="Working as" />}>
      <p className="muted small">A maker's saves need no approval. The maker requests the release of a change set; an approver for its area who is not a maker of it approves it as a version: the draft and the drafts it refers to or that refer to it, checked and tested together. Approval refuses on an error or a red test, and then nothing is published.</p>
      <ErrorBox error={drafts.error || error} />
      {msg && <div className={`alert ${msg.tone}`} role="status">{msg.text}</div>}
      {drafts.loading && !drafts.data ? <Loading /> : !drafts.data?.length ? <p className="muted">No drafts.</p> : (
        <div className="table-wrap">
          <Table size="small" className="grid">
            <TableHeader><TableRow><TableHeaderCell>Draft</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Title</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>Errors</TableHeaderCell><TableHeaderCell>Publishes with</TableHeaderCell><TableHeaderCell></TableHeaderCell></TableRow></TableHeader>
            <TableBody>
              {drafts.data.map((d) => (
                <TableRow key={d.key}>
                  <TableCell><ArtefactLink name={d.name} keyOf={d.key} /> <span className="mono muted">@{d.release}</span></TableCell>
                  <TableCell>{d.kind} {d.type}</TableCell>
                  <TableCell>{d.title}</TableCell>
                  <TableCell><Badge tone={d.status === 'in-review' ? 'info' : 'warn'}>{d.status}</Badge></TableCell>
                  <TableCell>{d.issues ? <Badge tone="fail">{d.issues}</Badge> : <Badge tone="pass">none</Badge>}</TableCell>
                  <TableCell className="small">{(sets.data?.[d.key] ?? []).filter((k) => k !== d.key).map((k) => <div key={k} className="mono">{k.replace(/#draft$/, '')}</div>)}{(sets.data?.[d.key] ?? []).length <= 1 && <span className="muted">alone</span>}</TableCell>
                  <TableCell className="nowrap">
                    {['workflow', 'hook'].includes(d.kind) && <Button onClick={() => go(`/designer?open=${encodeURIComponent(d.key)}&draft=1`)}>Open</Button>}{' '}
                    {roles.some((r) => MAKER_ROLES.includes(r)) && <Button disabled={busy === d.key || d.status === 'in-review'} onClick={() => act(d.key, 'review')}>Request release</Button>}{' '}
                    {roles.some((r) => APPROVER_ROLES.includes(r)) && <Button appearance="primary" disabled={busy === d.key} onClick={() => act(d.key, 'publish')}>{busy === d.key ? 'Working…' : 'Approve as version'}</Button>}{' '}
                    {roles.some((r) => MAKER_ROLES.includes(r)) && <Link as="button" className="danger" onClick={() => remove(d.key)}>Delete</Link>}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {users.length > 0 && <p className="muted small">Users of {tenant}: {users.map((u) => u.id).join(', ')}.</p>}
    </Section>
  );
}

const ACTIONS = ['author', 'approve', 'accept', 'run', 'retire'];
const KINDS = ['workflow', 'scheme', 'activity', 'script', 'engine-binding', 'port', 'rule', 'ruleset', 'table', 'simulator', 'extension', 'hook', 'configuration-set', 'port-bindings', 'test', 'sample', 'supersession', 'bench', 'import', 'export', 'deploy', 'waiver', 'settings', 'release', 'in-flight', 'users', 'assistant'];

function Users() {
  const { tenant, meta } = useStudio();
  const users = useLoad(() => get<User[]>('/api/users'), [tenant]);
  const table = useLoad(() => get<{ grants: { role: string; scope: string; kinds: string[]; actions: string[] }[] }>('/api/permissions'), []);
  const [who, setWho] = useState('');
  const [action, setAction] = useState('author');
  const [kind, setKind] = useState('workflow');
  const [scope, setScope] = useState('');
  const [res, setRes] = useState<any>();
  const ask = async () => setRes(await get(`/api/permit${q({ user: who, action, kind, scope: scope || undefined })}`).catch((e) => ({ error: e.message })));
  return (
    <div className="cols">
      <div className="col">
        <Section title={`Users ${tenant === meta?.owner ? 'of every tenant' : `of ${tenant}`}`} count={users.data?.length}>
          <ErrorBox error={users.error} />
          <p className="muted small">Names are role-shaped placeholders; no real person is named. A user acts in one tenant only. A change to the users is proposed by one administrator and approved by another.</p>
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>User</TableHeaderCell><TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Roles</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{(users.data ?? []).map((u) => <TableRow key={u.id}><TableCell className="mono">{u.id}</TableCell><TableCell>{u.tenant}</TableCell><TableCell>{u.roles.map((r) => <Badge key={r} tone="info">{r}</Badge>)}</TableCell></TableRow>)}</TableBody>
          </Table>
          <h4>The permission table</h4>
          <p className="muted small">The server checks every write, release and approval against this table. Reading is open to every user of a tenant for what the tenant may see.</p>
          <ErrorBox error={table.error} />
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>Role</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell><TableHeaderCell>May</TableHeaderCell><TableHeaderCell>On</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{(table.data?.grants ?? []).map((g, i) => <TableRow key={i}><TableCell>{g.role}</TableCell><TableCell>{g.scope === 'bank' ? 'scope b of its bank' : g.scope === 'shared' ? 'shared scopes' : 'the studio'}</TableCell><TableCell>{g.actions.join(', ')}</TableCell><TableCell className="small">{g.kinds.join(', ')}</TableCell></TableRow>)}</TableBody>
          </Table>
        </Section>
      </div>
      <div className="col side">
        <Section title="May this user do this?">
          <div className="stack">
            <Select value={who} onChange={(e) => setWho(e.target.value)} aria-label="User"><option value="">Choose a user</option>{(users.data ?? []).map((u) => <option key={u.id}>{u.id}</option>)}</Select>
            <Select value={action} onChange={(e) => setAction(e.target.value)} aria-label="Action">{ACTIONS.map((a) => <option key={a}>{a}</option>)}</Select>
            <Select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind">{KINDS.map((a) => <option key={a}>{a}</option>)}</Select>
            <Input value={scope} onChange={(e) => setScope(e.target.value)} placeholder="scope, for example p or b (optional)" className="mono" aria-label="Scope" />
            <Button disabled={!who} onClick={ask}>Ask</Button>
            {res && (res.error ? <p className="fail-text small">{res.error}</p> : res.allowed ? <Alert tone="pass" className="small">Allowed.</Alert> : <Alert tone="fail" className="small">Refused: {res.reason}</Alert>)}
          </div>
        </Section>
      </div>
    </div>
  );
}

function Releases({ initial }: { initial: string }) {
  const names = [...useCatalogue('activity'), ...useCatalogue('rule'), ...useCatalogue('workflow'), ...useCatalogue('table'), ...useCatalogue('ruleset')].filter((s) => s.status === 'published').map((s) => s.name);
  const [name, setName] = useState(initial || 'p.reserve.funds.v1');
  const [release, setRelease] = useState('1.0.0');
  const [pins, setPins] = useState<any>();
  const [as, setAs] = useState('');
  const [msg, setMsg] = useState<{ tone: string; text: string }>();
  const [counts, setCounts] = useState('{\n  "p.reserve.funds.v1@1.0.0": 0\n}');
  const [asOf, setAsOf] = useState('2026-11-17T09:00:00Z');
  const [prop, setProp] = useState<any>();
  const [propBusy, setPropBusy] = useState(false);
  const [newRel, setNewRel] = useState('');
  const look = async (keep = false) => {
    if (!keep) setMsg(undefined);
    try {
      setPins(await get(`/api/releases/${encodeURIComponent(name)}/pins${q({ release })}`));
    } catch (e: any) {
      setPins(undefined);
      setMsg({ tone: 'fail', text: refusal(e) });
    }
  };
  const retire = async () => {
    if (!window.confirm(`Retire ${name}@${release} as ${as}?`)) return;
    try {
      const r = await post('/api/retire', { actor: as, name, release });
      setMsg({ tone: 'pass', text: `Retired ${r.retired.key}.` });
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    }
    void look(true);
  };
  const importCounts = async () => {
    try {
      const c = JSON.parse(counts);
      await post('/api/in-flight', { actor: as, counts: c, asOf });
      setMsg({ tone: 'pass', text: `Imported in-flight counts as of ${asOf}.` });
      void look(true);
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    }
  };
  const propagate = async () => {
    setPropBusy(true);
    setProp(undefined);
    try {
      setProp(await get(`/api/propagate${q({ name, release: newRel })}`));
    } catch (e: any) {
      setProp({ error: refusal(e) });
    } finally {
      setPropBusy(false);
    }
  };
  return (
    <>
      <Section title="A release and who pins it (F-24)">
        <div className="toolbar">
          <Input list="rel-names" value={name} onChange={(e) => setName(e.target.value)} className="mono grow" aria-label="Artefact name" />
          <datalist id="rel-names">{[...new Set(names)].map((n) => <option key={n} value={n} />)}</datalist>
          <Input style={{ width: '11ch' }} value={release} onChange={(e) => setRelease(e.target.value)} className="mono" aria-label="Release" />
          <Button appearance="primary" onClick={() => look()}>Show pins</Button>
        </div>
        {msg && <div className={`alert ${msg.tone}`} role="status">{msg.text}</div>}
        {pins && (
          <div className="cols">
            <div className="col">
              <KV rows={[
                ['Release', <code>{pins.name}@{pins.release}</code>],
                ['Pinned by', pins.pinnedBy.length ? <span className="chips">{pins.pinnedBy.map((p: any) => <span key={p.workflow} className="chip"><ArtefactLink name={p.workflow} release={p.release} /></span>)}</span> : <Badge tone="pass">nothing</Badge>],
                ['In flight', <>{pins.inFlight.count} instance(s){pins.inFlight.asOf ? <span className="muted small"> as of {pins.inFlight.asOf}</span> : <span className="muted small"> (no import yet)</span>}</>],
                ['May retire', pins.mayRetire ? <Badge tone="pass">yes</Badge> : <Badge tone="fail">no, a pinned release cannot be removed</Badge>],
              ]} />
            </div>
            <div className="col side">
              <div className="stack">
                <AsUser value={as} onChange={setAs} prefer={may('platform approver', 'service operations')} label="Retire as" />
                <Button className="danger-btn" onClick={retire}>Retire {pins.name}@{pins.release}</Button>
                <p className="muted small">The server refuses while anything pins it or an instance in flight is pinned to it.</p>
              </div>
            </div>
          </div>
        )}
      </Section>
      <div className="cols">
        <div className="col">
          <Section title="Propagate a new release of a block (F-23)">
            <p className="muted small">For a new release of {name}: every workflow on that line that pins it, its tests rerun with the new release, and the proposed new workflow release.</p>
            <div className="inline-form">
              <Input value={newRel} onChange={(e) => setNewRel(e.target.value)} placeholder="new release, for example 1.0.1" className="mono" aria-label="New release" />
              <Button disabled={!newRel || propBusy} onClick={propagate}>{propBusy ? 'Rerunning…' : 'Propagate'}</Button>
            </div>
            {prop?.error && <Alert tone="fail" className="small">{prop.error}</Alert>}
            {prop && !prop.error && (
              <>
                <p>{prop.artefact} {prop.from} to {prop.to}: a <Badge tone={prop.change === 'major' ? 'fail' : prop.change === 'minor' ? 'warn' : 'pass'}>{prop.change}</Badge></p>
                <Table size="small" className="grid compact">
                  <TableHeader><TableRow><TableHeaderCell>Workflow</TableHeaderCell><TableHeaderCell>Tests rerun</TableHeaderCell><TableHeaderCell>Proposed release</TableHeaderCell><TableHeaderCell>New pin</TableHeaderCell></TableRow></TableHeader>
                  <TableBody>
                    {prop.workflows.map((w: any) => (
                      <TableRow key={w.workflow}>
                        <TableCell><ArtefactLink name={w.workflow} release={w.release} /></TableCell>
                        <TableCell className="small">{w.tests.length ? w.tests.map((t: any) => <div key={t.id}><PassFail passed={t.passed} /> <span className="mono">{t.id}</span></div>) : <span className="muted">none</span>}</TableCell>
                        <TableCell>{w.proposed ? <b className="mono">{w.proposed}</b> : <span className="warn-text small">{w.blockedBy}</span>}</TableCell>
                        <TableCell className="mono small break">{w.newPin.name}@{w.newPin.release}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {!prop.workflows.length && <p className="muted small">No workflow on this line pins {name}.</p>}
              </>
            )}
          </Section>
        </div>
        <div className="col side">
          <Section title="Import in-flight counts">
            <p className="muted small">Service operations supplies the count of instances pinned to each release. The studio never reads a payment.</p>
            <div className="stack">
              <AsUser value={as} onChange={setAs} prefer={may('service operations')} label="Import as" />
              <Textarea className="code-input" rows={4} value={counts} onChange={(e) => setCounts(e.target.value)} aria-label="In-flight counts JSON" />
              <Input value={asOf} onChange={(e) => setAsOf(e.target.value)} className="mono" aria-label="As of" />
              <Button onClick={importCounts}>Import</Button>
            </div>
          </Section>
        </div>
      </div>
    </>
  );
}

function SupersessionWizard() {
  const { actor, users } = useStudio();
  const [artefact, setArtefact] = useState('uae.p.rule.iban-checksum.v1');
  const [replaces, setReplaces] = useState('1.0.0');
  const [by, setBy] = useState('1.0.1');
  const [reason, setReason] = useState('A patch that corrects the check-digit rule applies to payments already started, at the steps they have not yet reached.');
  const [doc, setDoc] = useState<any>();
  const [ev, setEv] = useState<any>();
  const [done, setDone] = useState<any>();
  const [checker, setChecker] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string>();
  const step = done ? 3 : ev ? 2 : doc ? 1 : 0;
  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    setErr(undefined);
    try {
      await fn();
    } catch (e: any) {
      setErr(refusal(e));
    } finally {
      setBusy(false);
    }
  };
  const green = ev?.evidence?.testRuns?.length > 0 && ev.evidence.testRuns.every((r: any) => r.result === 'green');
  return (
    <Section title="Supersession wizard (F-25)" actions={<span className="wizard-steps">{['Draft', 'Evidence', 'Hand over'].map((s, i) => <Badge key={s} tone={step > i ? 'pass' : step === i ? 'info' : 'muted'}>{i + 1}. {s}</Badge>)}</span>}>
      <p className="muted small">A supersession swaps a release of logic for the payments already in flight. It is drafted, tested by rerunning every pinned workflow's tests with the replacement swapped in, then handed over with a maker and a different checker. The studio never applies it.</p>
      <div className="form-grid">
        <label className="field wide"><span>Artefact</span><Input value={artefact} onChange={(e) => setArtefact(e.target.value)} className="mono" aria-label="Superseded artefact" /></label>
        <label className="field"><span>Replaces</span><Input value={replaces} onChange={(e) => setReplaces(e.target.value)} className="mono" aria-label="Replaces release" /></label>
        <label className="field"><span>With</span><Input value={by} onChange={(e) => setBy(e.target.value)} className="mono" aria-label="With release" /></label>
        <label className="field wide"><span>Reason</span><Input value={reason} onChange={(e) => setReason(e.target.value)} aria-label="Reason" /></label>
      </div>
      <div className="row-actions">
        <Button appearance="primary" disabled={busy} onClick={() => run(async () => { setEv(undefined); setDone(undefined); setDoc(await post('/api/supersessions/draft', { artefact, replaces, with: by, reason, maker: actor })); })}>1. Draft as {actor}</Button>
        <Button disabled={busy || !doc} onClick={() => run(async () => setEv(await post('/api/supersessions/evidence', { doc })))}>{busy && doc && !ev ? 'Rerunning…' : '2. Rerun the pinned tests'}</Button>
        <AsUser value={checker} onChange={setChecker} prefer={(u) => u.roles.includes('platform approver')} label="Checker" />
        <Button disabled={busy || !ev} onClick={() => run(async () => setDone(await post('/api/supersessions/hand-over', { doc: ev, checker })))}>3. Hand over</Button>
      </div>
      <ErrorBox error={err} />
      {doc && !ev && (
        <>
          <h4>Draft {doc.id}</h4>
          <KV rows={[['Scope', <Val v={doc.scope} />], ['Maker', doc.approval?.maker]]} />
          {!doc.scope?.workflowReleases?.length && <p className="small warn-text">No published workflow release pins {artefact}@{replaces}, so nothing would be superseded.</p>}
        </>
      )}
      {ev && (
        <>
          <h4>Evidence {green ? <Badge tone="pass">green</Badge> : <Badge tone="fail">not green</Badge>}</h4>
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>Workflow release</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell><TableHeaderCell>Tests</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{(ev.details ?? []).map((r: any) => <TableRow key={r.workflow}><TableCell><ArtefactLink name={r.workflow} release={r.release} /></TableCell><TableCell><Badge tone={r.result === 'green' ? 'pass' : 'fail'}>{r.result}</Badge></TableCell><TableCell className="small"><span className="chips">{r.tests.map((t: any) => <span key={t.id} className="nowrap"><PassFail passed={t.passed} /> <span className="mono">{t.id}</span></span>)}</span></TableCell></TableRow>)}</TableBody>
          </Table>
        </>
      )}
      {done && (
        <>
          <Alert tone="pass">Handed over {done.document.id}: maker {done.document.approval.maker}, checker {done.document.approval.checker}.</Alert>
          <pre className="code">{done.yaml}</pre>
        </>
      )}
      {users.length === 0 && <Loading what="users" />}
      <HandedOver key={done?.document?.id ?? 'none'} />
    </Section>
  );
}

function HandedOver() {
  const h = useLoad(() => get<any[]>('/api/supersessions/handed-over'), []);
  if (!h.data?.length) return null;
  return (
    <details>
      <summary>Handed over in this data root ({h.data.length})</summary>
      <Table size="small" className="grid compact">
        <TableHeader><TableRow><TableHeaderCell>Id</TableHeaderCell><TableHeaderCell>Artefact</TableHeaderCell><TableHeaderCell>Replaces</TableHeaderCell><TableHeaderCell>With</TableHeaderCell><TableHeaderCell>Maker</TableHeaderCell><TableHeaderCell>Checker</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{h.data.map((s) => <TableRow key={s.id}><TableCell className="mono">{s.id}</TableCell><TableCell><ArtefactLink name={s.artefact} /></TableCell><TableCell className="mono">{s.replaces}</TableCell><TableCell className="mono">{s.with}</TableCell><TableCell>{s.approval?.maker}</TableCell><TableCell>{s.approval?.checker}</TableCell></TableRow>)}</TableBody>
      </Table>
    </details>
  );
}

function Packs() {
  const { tenant, meta } = useStudio();
  const packs = useLoad(() => get<any[]>('/api/packs'), []);
  const [scope, setScope] = useState('uae.wps');
  const man = useLoad(() => get(`/api/packs/${encodeURIComponent(scope)}/manifest`), [scope]);
  const [kind, setKind] = useState<'shared' | 'bank' | 'test'>('shared');
  const [built, setBuilt] = useState<any>();
  const [gate, setGate] = useState<any>();
  const [busy, setBusy] = useState<string>();
  const bankTenant = tenant !== meta?.owner ? tenant : undefined;
  const build = async () => {
    setBusy('build');
    try {
      setBuilt(await post('/api/packs/build', { scope, kind, tenant: kind === 'bank' ? bankTenant : undefined }));
    } catch (e: any) {
      setBuilt({ error: refusal(e) });
    } finally {
      setBusy(undefined);
    }
  };
  const runGate = async () => {
    setBusy('gate');
    try {
      setGate(await get(`/api/packs/${encodeURIComponent(scope)}/release-gate`));
    } catch (e: any) {
      setGate({ error: refusal(e) });
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <>
      <Section title="Pack manifests compared with the repository" count={packs.data?.length}>
        <ErrorBox error={packs.error} />
        <Table size="small" className="grid compact">
          <TableHeader><TableRow><TableHeaderCell>Scope</TableHeaderCell><TableHeaderCell>Generated manifest equals the repository's</TableHeaderCell><TableHeaderCell>Differences</TableHeaderCell></TableRow></TableHeader>
          <TableBody>
            {(packs.data ?? []).map((p) => (
              <TableRow key={p.scope} className={p.scope === scope ? 'sel clickable' : 'clickable'} onClick={() => { setScope(p.scope); setBuilt(undefined); setGate(undefined); }}>
                <TableCell className="mono"><Link as="button">{p.scope}</Link></TableCell>
                <TableCell>{p.equal ? <Badge tone="pass">equal</Badge> : <Badge tone="warn">differs</Badge>}</TableCell>
                <TableCell className="small break">{p.differences?.length ? p.differences.map((d: string, i: number) => <div key={i}>{d}</div>) : <span className="muted">none</span>}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>
      <div className="cols">
        <div className="col">
          <Section title={<>Manifest of <span className="mono">{scope}</span></>} count={man.data?.manifest?.artefacts?.length}>
            <ErrorBox error={man.error} />
            {man.loading && !man.data ? <Loading /> : man.data && (
              <>
                <KV rows={[['Release', <code>{man.data.manifest.release}</code>], ['Type', man.data.manifest.packType], ['Depends on', <span className="chips">{(man.data.manifest.dependsOn ?? []).map((d: any) => <span key={d.scope} className="chip mono">{d.scope} {d.release}</span>)}</span>]]} />
                <div className="table-wrap tall">
                  <Table size="small" className="grid compact">
                    <TableHeader><TableRow><TableHeaderCell>Artefact</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Release</TableHeaderCell><TableHeaderCell>Hash</TableHeaderCell></TableRow></TableHeader>
                    <TableBody>{(man.data.manifest.artefacts ?? []).map((a: any) => <TableRow key={a.name}><TableCell><ArtefactLink name={a.name} /></TableCell><TableCell>{a.kind}</TableCell><TableCell className="mono">{a.release}</TableCell><TableCell className="mono small" title={a.hash}>{String(a.hash).replace('sha256:', '').slice(0, 12)}</TableCell></TableRow>)}</TableBody>
                  </Table>
                </div>
              </>
            )}
          </Section>
        </div>
        <div className="col side">
          <Section title="Build and download">
            <div className="stack">
              <Select value={kind} onChange={(e) => { setKind(e.target.value as any); setBuilt(undefined); }} aria-label="Pack kind">
                <option value="shared">shared pack</option>
                <option value="bank">bank pack{bankTenant ? ` for ${bankTenant}` : ''}</option>
                <option value="test">test pack</option>
              </Select>
              <Button onClick={build} disabled={busy === 'build'}>{busy === 'build' ? 'Building…' : 'Build'}</Button>
              {built?.error && <Alert tone="fail" className="small">{built.error}</Alert>}
              {built && !built.error && (built.refused ? <Alert tone="fail" className="small">Refused: the pack has red checks.<Issues issues={built.refused} /></Alert> : <Alert tone="pass" className="small">{built.files.length} files. No simulator in a promotable pack.</Alert>)}
              <Button as="a" icon={<ArrowDownloadRegular />} href={downloadUrl(`/api/packs/download${q({ scope, kind, tenant: kind === 'bank' ? bankTenant : undefined })}`)}>Download the zip</Button>
            </div>
          </Section>
          <Section title="Release gate">
            <Button onClick={runGate} disabled={busy === 'gate'}>{busy === 'gate' ? 'Running the kit…' : `Run the release gate for ${scope}`}</Button>
            {gate?.error && <Alert tone="fail" className="small">{gate.error}</Alert>}
            {gate && !gate.error && (
              <KV rows={[
                ['Checks', gate.checks.green ? <Badge tone="pass">green</Badge> : <Badge tone="fail">{gate.checks.errors?.length} red</Badge>],
                ['Planned messages', gate.planned.publishable ? <Badge tone="pass">none</Badge> : <span><Badge tone="fail">not publishable</Badge> <span className="small">{gate.planned.messages.join(', ')}</span></span>],
                ['Conformance on studio', gate.conformance.green ? <Badge tone="pass">green</Badge> : <span><Badge tone="fail">red</Badge> <span className="small mono">{gate.conformance.red.join(', ')}</span></span>],
                ['Engine reports', Object.keys(gate.engines ?? {}).length ? <Val v={gate.engines} /> : <span className="muted small">none imported</span>],
              ]} />
            )}
          </Section>
        </div>
      </div>
    </>
  );
}

function Conformance() {
  const { actor, users } = useStudio();
  const [target, setTarget] = useState('studio');
  const [rep, setRep] = useState<any>();
  const [busy, setBusy] = useState(false);
  const [scenario, setScenario] = useState('');
  const [reason, setReason] = useState('');
  const [as, setAs] = useState('');
  const [msg, setMsg] = useState<{ tone: string; text: string }>();
  const load = async () => {
    setBusy(true);
    try {
      setRep(await get(`/api/conformance/report${q({ target })}`));
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    } finally {
      setBusy(false);
    }
  };
  const waive = async () => {
    try {
      await post('/api/conformance/waivers', { actor: as, target, scenario, reason });
      setMsg({ tone: 'pass', text: `Waived ${scenario} on ${target}, approved by ${as}.` });
      void load();
    } catch (e: any) {
      setMsg({ tone: 'fail', text: refusal(e) });
    }
  };
  void actor;
  void users;
  const rows: any[] = rep?.rows ?? [];
  return (
    <div className="cols">
      <div className="col">
        <Section title="Conformance report" actions={<><Input style={{ width: '13ch' }} value={target} onChange={(e) => setTarget(e.target.value)} className="mono" aria-label="Target" /><Button appearance="primary" onClick={load} disabled={busy}>{busy ? 'Running the kit…' : 'Run the report'}</Button></>}>
          <p className="muted small">The conformance kit on a target, with waivers. A red scenario that is not waived blocks the release.</p>
          {msg && <div className={`alert ${msg.tone}`} role="status">{msg.text}</div>}
          {rep && (
            <>
              <p>Kit {rep.kitRelease} of {rep.date}: {rep.green ? <Badge tone="pass">green</Badge> : <Badge tone="fail">red</Badge>} {rows.filter((r) => r.result === 'pass').length} pass, {rows.filter((r) => r.result === 'waived').length} waived, {rows.filter((r) => r.result === 'fail').length} fail</p>
              <div className="table-wrap tall">
                <Table size="small" className="grid compact">
                  <TableHeader><TableRow><TableHeaderCell>Scenario</TableHeaderCell><TableHeaderCell>Construct</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell><TableHeaderCell>Waiver</TableHeaderCell></TableRow></TableHeader>
                  <TableBody>{rows.map((r) => <TableRow key={r.scenario} className={r.result === 'fail' ? 'row-fail' : r.result === 'waived' ? 'row-warn' : ''}><TableCell className="mono">{r.scenario}</TableCell><TableCell>{r.construct}</TableCell><TableCell><Badge tone={r.result === 'pass' ? 'pass' : r.result === 'waived' ? 'warn' : 'fail'}>{r.result}</Badge></TableCell><TableCell className="small">{r.waiver ? `${r.waiver.reason} (${r.waiver.approver})` : ''}</TableCell></TableRow>)}</TableBody>
                </Table>
              </div>
            </>
          )}
        </Section>
      </div>
      <div className="col side">
        <Section title="Add a waiver">
          <div className="stack">
            <Select value={scenario} onChange={(e) => setScenario(e.target.value)} aria-label="Scenario">
              <option value="">Choose a scenario</option>
              {rows.map((r) => <option key={r.scenario} value={r.scenario}>{r.scenario} ({r.result})</option>)}
            </Select>
            <Input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="The reason, required" aria-label="Waiver reason" />
            <AsUser value={as} onChange={setAs} prefer={may('platform approver')} label="Approve as" />
            <Button disabled={!scenario || !reason} onClick={waive}>Waive</Button>
            <p className="muted small">Run the report first to list the scenarios.</p>
          </div>
        </Section>
      </div>
    </div>
  );
}

function Coverage() {
  const tc = useLoad(() => get<any[]>('/api/test-coverage'), []);
  const [onlyGaps, setOnlyGaps] = useState(true);
  const [kind, setKind] = useState('');
  const [scope, setScope] = useState('uae.wps');
  const rc = useLoad(() => get(`/api/coverage/${encodeURIComponent(scope)}`), [scope]);
  const rows = (tc.data ?? []).filter((r) => (!onlyGaps || r.untested.length) && (!kind || r.kind === kind));
  const total = (tc.data ?? []).reduce((n, r) => n + r.outcomes.length, 0);
  const untested = (tc.data ?? []).reduce((n, r) => n + r.untested.length, 0);
  return (
    <div className="cols">
      <div className="col">
        <Section title="Test coverage: outcomes no test expects" count={rows.length} actions={
          <>
            <Select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind"><option value="">every kind</option><option>activity</option><option>hook</option><option>workflow</option></Select>
            <Checkbox className="check small" checked={onlyGaps} onChange={(e) => setOnlyGaps(e.target.checked)} label={<>Only with untested outcomes</>} />
          </>
        }>
          <ErrorBox error={tc.error} />
          {tc.data && <p className="small">{total - untested} of {total} outcomes have a test that expects them.</p>}
          <div className="table-wrap tall">
            <Table size="small" className="grid compact">
              <TableHeader><TableRow><TableHeaderCell>Artefact</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Tests</TableHeaderCell><TableHeaderCell>Untested outcomes</TableHeaderCell></TableRow></TableHeader>
              <TableBody>{rows.map((r) => <TableRow key={r.name}><TableCell><ArtefactLink name={r.name} /></TableCell><TableCell>{r.kind}</TableCell><TableCell className="small mono">{r.tests.join(', ') || <span className="muted">none</span>}</TableCell><TableCell>{r.untested.length ? r.untested.map((o: string) => <Badge key={o} tone="warn">{o}</Badge>) : <Badge tone="pass">none</Badge>}</TableCell></TableRow>)}</TableBody>
            </Table>
          </div>
        </Section>
      </div>
      <div className="col side">
        <Section title="Reason-code coverage per scheme" actions={<Select value={scope} onChange={(e) => setScope(e.target.value)} aria-label="Scheme scope">{['uae.wps', 'uae.p', 'p'].map((s) => <option key={s}>{s}</option>)}</Select>}>
          <p className="muted small">Each reason code the scheme's rules can yield, and the simulator scenarios that make it happen in a test.</p>
          <ErrorBox error={rc.error} />
          {rc.data && (
            <>
              <p className="small"><Badge tone="pass">{rc.data.covered.length} covered</Badge> <Badge tone="warn">{rc.data.open.length} open</Badge></p>
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Code</TableHeaderCell><TableHeaderCell>State</TableHeaderCell><TableHeaderCell>Yielded by</TableHeaderCell><TableHeaderCell>Scenarios</TableHeaderCell></TableRow></TableHeader>
                <TableBody>{rc.data.rows.map((r: any) => <TableRow key={r.code}><TableCell className="mono">{r.code}</TableCell><TableCell><Badge tone={r.state === 'covered' ? 'pass' : 'warn'}>{r.state}</Badge></TableCell><TableCell className="small">{r.rules.map((x: string) => <div key={x}><ArtefactLink name={x} /></div>)}</TableCell><TableCell className="small mono">{r.scenarios.join(', ') || <span className="muted">none</span>}</TableCell></TableRow>)}</TableBody>
              </Table>
            </>
          )}
        </Section>
      </div>
    </div>
  );
}

function Bindings() {
  const b = useLoad(() => get<any[]>('/api/bindings'), []);
  const sims = useLoad(() => get<any[]>('/api/simulators'), []);
  return (
    <div className="cols">
      <div className="col">
        <Section title="Engine bindings" count={b.data?.length}>
          <p className="muted small">The binding the studio generates per target engine, compared with the one in the repository.</p>
          <ErrorBox error={b.error} />
          {(b.data ?? []).map((x) => (
            <div key={x.target} className="binding">
              <h4>{x.target} {x.equal ? <Badge tone="pass">equal to the repository</Badge> : <Badge tone="warn">differs</Badge>} <Badge>{x.generated?.strategy}</Badge></h4>
              <KV rows={[
                ['Queues', <span className="chips">{Object.entries<any>(x.generated?.queues ?? {}).map(([k, v]) => <span key={k} className="chip mono">{k}: {v}</span>)}</span>],
                ['Task naming', <code>{x.generated?.taskNaming}</code>],
                ['Defaults', x.generated?.defaults ? <Val v={x.generated.defaults} /> : <span className="muted">none</span>],
                ['Extensions', x.generated?.extensions ? <Val v={x.generated.extensions} /> : <span className="muted">none</span>],
              ]} />
              {x.differences?.length > 0 && <List items={x.differences} />}
            </div>
          ))}
        </Section>
      </div>
      <div className="col side">
        <Section title="Simulator stubs" count={sims.data?.length}>
          <p className="muted small">A stand-alone stub per simulator, for a bank's own test environment. A simulator is never part of a promotable pack.</p>
          <ul className="plain">
            {(sims.data ?? []).map((s) => (
              <li key={s.name} className="stub-row">
                <ArtefactLink name={s.name} /> {s.missingStandard?.length ? <Badge tone="warn" title={s.missingStandard.join(', ')}>{s.missingStandard.length} missing</Badge> : null}{' '}
                <a href={downloadUrl(`/api/simulators/${encodeURIComponent(s.name)}/stub`)} className="small">download stub</a>
              </li>
            ))}
          </ul>
        </Section>
      </div>
    </div>
  );
}

function Audit() {
  const { tenant } = useStudio();
  const audit = useLoad(() => get<any[]>('/api/audit'), [tenant]);
  const [verify, setVerify] = useState<any>();
  const [filter, setFilter] = useState('');
  const check = async () => setVerify(await get('/api/audit/verify').catch((e) => ({ ok: false, error: e.message })));
  const rows = (audit.data ?? []).filter((a) => !filter || JSON.stringify(a).toLowerCase().includes(filter.toLowerCase()));
  return (
    <Section title="Audit log" count={audit.data?.length} actions={<><SearchBox value={filter} onChange={(_, d) => setFilter(d.value)} placeholder="Filter" aria-label="Filter the audit log" /><Button onClick={check}>Verify the chain</Button></>}>
      <p className="muted small">Newest first. A publish names its maker and a different checker. A refusal is recorded too. Each entry carries the hash of the one before it, so a change to the log breaks the chain.</p>
      {verify && (verify.ok ? <Alert tone="pass" className="small">The chain verifies: no entry was changed or removed.</Alert> : <Alert tone="fail" className="small">The chain does not verify. {verify.error ?? verify.reason ?? ''} {verify.brokenAt !== undefined ? `It breaks at entry ${verify.brokenAt}.` : ''}</Alert>)}
      <ErrorBox error={audit.error} />
      {audit.loading && !audit.data ? <Loading /> : !audit.data?.length ? <p className="muted">Nothing recorded yet in this data root.</p> : (
        <div className="table-wrap">
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>When</TableHeaderCell><TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Who</TableHeaderCell><TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Artefact</TableHeaderCell><TableHeaderCell>Detail</TableHeaderCell></TableRow></TableHeader>
            <TableBody>
              {rows.map((a, i) => (
                <TableRow key={i} className={a.outcome === 'refused' ? 'row-warn' : ''}>
                  <TableCell className="mono small nowrap">{a.at ?? a.when ?? ''}</TableCell>
                  <TableCell>{a.tenant}</TableCell>
                  <TableCell>{a.actor}</TableCell>
                  <TableCell><Badge tone={a.outcome === 'refused' ? 'fail' : a.action === 'publish' ? 'pass' : a.action === 'delete-draft' ? 'warn' : 'info'}>{a.action}{a.outcome === 'refused' ? ' refused' : ''}</Badge></TableCell>
                  <TableCell className="mono small break">{a.key}</TableCell>
                  <TableCell className="small">{a.detail ?? ''}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </Section>
  );
}

function Supersessions() {
  const sups = useLoad(() => get<any[]>('/api/supersessions'), []);
  const [results, setResults] = useState<Record<string, any>>({});
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const test = async (s: any) => {
    setBusy(s.id);
    setError(undefined);
    try {
      const workflows = (s.scope?.workflowReleases ?? []).map((w: string) => w.split('@')[0]);
      const r = await post('/api/supersessions/test', { artefact: s.artefact, replaces: s.replaces, with: s.with, workflows });
      setResults((m) => ({ ...m, [s.id]: r }));
    } catch (e: any) {
      setError(`${s.id}: ${e.message}`);
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <>
      <ErrorBox error={sups.error || error} />
      {sups.loading && !sups.data ? <Loading /> : !sups.data?.length ? <p className="muted">No supersessions.</p> : sups.data.map((s) => {
        const r = results[s.id];
        return (
          <Section key={s.id} title={<span className="mono">{s.id}</span>} actions={<Button appearance="primary" onClick={() => test(s)} disabled={busy === s.id}>{busy === s.id ? 'Testing…' : 'Test'}</Button>}>
            <KV rows={[
              ['Artefact', <ArtefactLink name={s.artefact} />],
              ['Replaces', <><code>{s.replaces}</code> with <code>{s.with}</code></>],
              ['Reason', s.reason],
              ['Scope', <Val v={s.scope} />],
              ['Evidence', <Val v={s.evidence} />],
              ['Approval', s.approval ? <>maker <b>{s.approval.maker}</b>, checker <b>{s.approval.checker}</b></> : <span className="muted">none</span>],
            ]} />
            <h4>Issues</h4>
            <Issues issues={s.issues as Issue[]} empty="No issues." />
            {r && (
              <>
                <h4>Test result {r.green ? <Badge tone="pass">green</Badge> : <Badge tone="fail">not green</Badge>}</h4>
                {!r.results.length ? <p className="muted small">No test targets the workflows in scope, so nothing reran.</p> : (
                  <Table size="small" className="grid compact">
                    <TableHeader><TableRow><TableHeaderCell>Test</TableHeaderCell><TableHeaderCell>Target</TableHeaderCell><TableHeaderCell>Outcome</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell></TableRow></TableHeader>
                    <TableBody>{r.results.map((x: any) => <TableRow key={x.id}><TableCell className="mono"><a href={`#/tests?run=${encodeURIComponent(x.id)}`}>{x.id}</a></TableCell><TableCell className="mono small">{x.target}</TableCell><TableCell><code>{x.outcome}</code></TableCell><TableCell><PassFail passed={x.passed} /></TableCell></TableRow>)}</TableBody>
                  </Table>
                )}
              </>
            )}
          </Section>
        );
      })}
    </>
  );
}

function StoreChecks() {
  const { meta } = useStudio();
  const checks = useLoad(() => get('/api/checks'), []);
  const [code, setCode] = useState('');
  const [sev, setSev] = useState('');
  const issues: Issue[] = checks.data?.issues ?? [];
  const codes = [...new Set(issues.map((i) => i.code))].sort();
  const rows = issues.filter((i) => (!code || i.code === code) && (!sev || i.severity === sev));
  const errors = issues.filter((i) => i.severity === 'error').length;
  return (
    <Section title="Every static check across the store" count={issues.length} actions={<>{errors ? <Badge tone="fail">{errors} errors</Badge> : <Badge tone="pass">no errors</Badge>} <Badge tone="warn">{issues.length - errors} warnings</Badge></>}>
      <ErrorBox error={checks.error} />
      <div className="toolbar">
        <Select value={code} onChange={(e) => setCode(e.target.value)} aria-label="Check">
          <option value="">Every check</option>
          {codes.map((c) => <option key={c} value={c}>{c} {meta?.checks?.[c] ?? ''}</option>)}
        </Select>
        <Select value={sev} onChange={(e) => setSev(e.target.value)} aria-label="Severity">
          <option value="">Any severity</option>
          <option value="error">error</option>
          <option value="warning">warning</option>
        </Select>
      </div>
      {checks.loading && !checks.data ? <Loading /> : (
        <div className="table-wrap">
          <Table size="small" className="grid compact">
            <TableHeader><TableRow><TableHeaderCell>Check</TableHeaderCell><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Artefact</TableHeaderCell><TableHeaderCell>Step</TableHeaderCell><TableHeaderCell>Message</TableHeaderCell></TableRow></TableHeader>
            <TableBody>
              {rows.map((i, n) => (
                <TableRow key={n}>
                  <TableCell title={meta?.checks?.[i.code]}><Badge tone={i.severity === 'error' ? 'fail' : 'warn'}>{i.code}</Badge></TableCell>
                  <TableCell>{i.severity}</TableCell>
                  <TableCell className="small">{i.key && /\.v\d+$|^scheme:/.test(i.key) ? <ArtefactLink name={i.key} /> : <span className="mono">{i.key}</span>}</TableCell>
                  <TableCell className="mono">{i.step ?? ''}</TableCell>
                  <TableCell>{i.message}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      <details>
        <summary>The check catalogue</summary>
        <Table size="small" className="grid compact">
          <TableBody>{Object.entries(checks.data?.catalogue ?? {}).map(([k, v]) => <TableRow key={k}><TableCell className="mono">{k}</TableCell><TableCell>{String(v)}</TableCell></TableRow>)}</TableBody>
        </Table>
      </details>
    </Section>
  );
}
