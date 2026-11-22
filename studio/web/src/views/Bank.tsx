import { get, type Summary } from '../api';
import { ArtefactLink, Badge, ErrorBox, Loading, Section, useLoad, useStudio } from '../ui';

// The bank workspace (R-10-08): where a Bank Configurator lands. Its own hooks, rules, dials,
// simulators and hook bindings in scope b, and the test bench for every workflow the bank can
// configure, run with its own configuration. Nothing here reaches shared content: the server refuses
// a change outside scope b of the signed-in user's bank (R-11-30).

function useBank(kind: string) {
  return useLoad(() => get<{ items: Summary[] }>(`/api/catalogue?kind=${kind}&scope=b`).then((r) => r.items), [kind]);
}

function Items({ items, empty, edit }: { items?: Summary[]; empty: string; edit?: string }) {
  if (!items) return <Loading />;
  if (!items.length) return <p className="muted small">{empty}</p>;
  return (
    <ul className="plain">
      {items.map((i) => (
        <li key={i.key}>
          <ArtefactLink name={i.name} release={i.release} keyOf={i.status === 'draft' || i.status === 'in-review' ? i.key : undefined} />{' '}
          {i.status !== 'published' && <Badge tone="warn">{i.status}</Badge>}{' '}
          {edit && <a className="small" href={edit === "designer" ? `#/designer?open=${encodeURIComponent(i.name)}` : `#/editors?kind=${edit}&open=${encodeURIComponent(i.name)}`}>edit</a>}
        </li>
      ))}
    </ul>
  );
}

export function Bank() {
  const { tenant, roles, go } = useStudio();
  const hooks = useBank('hook');
  const rules = useBank('rule');
  const tables = useBank('table');
  const activities = useBank('activity');
  const sims = useBank('simulator');
  const cfg = useLoad(() => get<any>(`/api/tenants/${encodeURIComponent(tenant)}/configuration`), [tenant]);
  const configurator = roles.includes('bank configurator');
  const params: any[] = cfg.data?.parameters ?? [];
  const points: any[] = cfg.data?.hookPoints ?? [];
  // The workflows this bank can configure: those that declare a hook point or a parameter it may set.
  const workflows = [...new Set([...points.map((p) => String(p.hookPoint).split('#')[0]), ...params.filter((p) => p.tenantSettable !== false && /\.(wf|seg|leg)\./.test(p.owner)).map((p) => p.owner)])].sort();
  const dialled = params.filter((p) => (p.values ?? []).length);
  const bound = points.filter((p) => (p.bindings ?? []).length);

  return (
    <div className="view">
      <div className="view-head">
        <h2>Bank workspace</h2>
        <p className="muted">
          {tenant}: hooks, rules, dials, simulators and hook bindings in scope <code>b</code>, and the test bench by workflow. Shared workflows, activities, rules, schemes and ports are read-only to a bank.
          {configurator ? ' Your saves need no approval; a bank approver who is not you approves a change set as a version.' : ' You approve what the bank configurator changed, as a version.'}
        </p>
      </div>
      <ErrorBox error={cfg.error} />
      <div className="cols">
        <div className="col">
          <Section title="Hooks" count={hooks.data?.length} actions={configurator && <button onClick={() => go('/designer')}>New hook</button>}>
            <Items items={hooks.data} empty="No hook yet." edit="designer" />
            <h4>Hook activities</h4>
            <Items items={activities.data} empty="No bank activity yet." edit="activity" />
          </Section>
          <Section title="Rules and rule values" count={(rules.data?.length ?? 0) + (tables.data?.length ?? 0)} actions={configurator && <button onClick={() => go('/editors?kind=rule')}>New rule</button>}>
            <Items items={[...(rules.data ?? []), ...(tables.data ?? [])]} empty="No bank rule yet." edit="rule" />
          </Section>
          <Section title="Simulators and scenarios" count={sims.data?.length} actions={configurator && <button onClick={() => go('/editors?kind=simulator')}>New simulator</button>}>
            <Items items={sims.data} empty="No bank simulator yet." edit="simulator" />
          </Section>
        </div>
        <div className="col side">
          <Section title="Dials" count={params.length} actions={<button onClick={() => go('/tenant')}>Set dials</button>}>
            {cfg.loading ? <Loading /> : <p className="small">{dialled.length} of {params.length} parameters have a value in configuration set {cfg.data?.configurationSet?.release ?? 'none'}. Every value lies inside the bounds the owner declared.</p>}
          </Section>
          <Section title="Hook bindings" count={bound.length} actions={<button onClick={() => go('/tenant')}>Bind hooks</button>}>
            <ul className="plain small">{bound.map((p) => <li key={p.hookPoint} className="mono">{p.hookPoint} → {p.bindings.map((b: any) => `${b.hook}@${b.release}`).join(', ')}</li>)}</ul>
            {!bound.length && <p className="muted small">No hook is bound.</p>}
          </Section>
          <Section title="Test bench by workflow" count={workflows.length}>
            <p className="muted small">Run any workflow this bank can configure, with {tenant}'s configuration.</p>
            <ul className="plain">{workflows.map((w) => <li key={w}><a href={`#/tests?bench=${encodeURIComponent(w)}`}>{w}</a></li>)}</ul>
          </Section>
        </div>
      </div>
    </div>
  );
}
