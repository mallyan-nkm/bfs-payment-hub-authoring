import { useState } from 'react';
import { get, post, refusal } from '../api';
import { Badge, ErrorBox, Loading, useLoad, useStudio } from '../ui';

// The schemes a bank licenses (R-11-75 to R-11-78, Issue I-205). A Bank Configurator proposes the bank's next lock file with a
// further scheme; a Bank Approver who is not its maker approves it. The approval publishes the lock file on the content branch
// and builds the bank's next distribution, which carries the scheme. A configuration that binds a scheme the bank does not
// license is refused at approval.

export function Licensing() {
  const { tenant, roles } = useStudio();
  const configurator = roles.includes('bank configurator');
  const approver = roles.includes('bank approver');
  const data = useLoad(() => get<any>(`/api/tenants/${encodeURIComponent(tenant)}/licensing`), [tenant]);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | undefined>();
  if (data.loading) return <Loading />;
  if (data.error) return <ErrorBox error={data.error} />;
  const l = data.data;
  const draft = l.draft;
  const act = async (fn: () => Promise<string>) => {
    setError(undefined);
    setNote('');
    try { setNote(await fn()); data.reload(); } catch (e: any) { setError(refusal(e)); }
  };
  const license = (scope: string) => act(async () => {
    const r = await post(`/api/tenants/${encodeURIComponent(tenant)}/licensing`, { scheme: scope });
    return `Proposed lock file ${r.lock.release} licensing ${scope}, saved as draft ${r.draft.key}. A bank approver who is not you approves it.`;
  });
  const approve = () => act(async () => {
    const r = await post('/api/publish', { tenant, key: draft.key });
    const dist = (r.bundles ?? []).find((b: any) => b.kind === 'distribution');
    return `Lock file ${draft.release} approved in change set ${r.changeSet.id}${dist ? `; distribution ${dist.releaseId} carries ${draft.licensedSchemes.join(', ')}` : ''}.`;
  });
  return (
    <div aria-label="Schemes licensed">
      <p className="small muted">
        Lock file {l.release ?? 'none'} licenses {l.licensedSchemes.join(', ') || 'no scheme'}. A distribution of {tenant} carries the schemes it licenses and no other; a configuration that binds another is refused at approval.
      </p>
      <table className="grid small" aria-label="Schemes of the factory">
        <thead><tr><th>Scheme</th><th>Release</th><th>Licence</th><th>Configuration</th><th /></tr></thead>
        <tbody>{l.schemes.map((s: any) => {
          const proposed = draft?.licensedSchemes?.includes(s.scope) && !s.licensed;
          return (
            <tr key={s.scope} data-scheme={s.scope}>
              <td><span className="mono">{s.scope}</span> <span className="muted">{s.title}</span></td>
              <td className="mono">{s.release}</td>
              <td>{s.licensed ? <Badge tone="pass">licensed</Badge> : proposed ? <Badge tone="warn">proposed</Badge> : <Badge tone="muted">not licensed</Badge>}</td>
              <td>{s.bound ? 'bound' : '—'}</td>
              <td>{configurator && !s.licensed && !proposed && <button onClick={() => license(s.scope)}>License {s.scope}</button>}</td>
            </tr>
          );
        })}</tbody>
      </table>
      {draft && (
        <p className="small" data-testid="licence-draft">
          Proposed: lock file {draft.release} licensing {draft.licensedSchemes.join(', ')} (change set {draft.changeSet ?? 'new'}).{' '}
          {approver ? <button className="primary" onClick={approve}>Approve the licence</button> : <span className="muted">Waits for a bank approver.</span>}
        </p>
      )}
      {note && <p className="small" role="status">{note}</p>}
      {error && <p className="error">{error}</p>}
    </div>
  );
}
