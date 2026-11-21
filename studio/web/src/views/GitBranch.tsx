import { useState } from 'react';
import { del, get, post, put, q } from '../api';
import { Badge, ErrorBox, KV, Section, useLoad, useStudio } from '../ui';
import { AsUser } from './Governance';

// Export to a connected Git branch (plan W8; R-09-19 to R-09-22). Connect a repository and a branch;
// each published change set is written to it as one commit, never rewritten, never forced.

const TARGETS = ['temporal', 'dtfx', 'flowable', 'elsa'];

export function GitBranch() {
  const { tenant } = useStudio();
  const status = useLoad(() => get('/api/git'), [tenant]);
  const sets = useLoad(() => get<any[]>('/api/change-sets'), [tenant]);
  const [as, setAs] = useState('');
  const [form, setForm] = useState({ remote: '', branch: 'main', mode: 'commit', target: 'temporal', environment: '', autoExport: true });
  const [error, setError] = useState<string>();
  const [msg, setMsg] = useState<string>();
  const [busy, setBusy] = useState(false);
  const c = status.data?.connection;
  const done = new Set<string>((status.data?.exports ?? []).filter((x: any) => x.status !== 'unchanged').map((x: any) => x.changeSet));

  const act = async (fn: () => Promise<any>, ok: (r: any) => string) => {
    setBusy(true);
    setError(undefined);
    setMsg(undefined);
    try {
      setMsg(ok(await fn()));
      status.reload();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };
  const set = (k: string, v: any) => setForm((f) => ({ ...f, [k]: v }));

  return (
    <Section title="Git branch" actions={c ? <Badge tone="pass">connected</Badge> : <Badge tone="muted">not connected</Badge>}>
      <p className="muted small">Each published change set is written to the connected branch as one commit: the documents at their releases, each workflow's engine package, the change set record and, if you name an environment, an activation. A file the studio wrote is never rewritten. The token comes from <code>TH_GITHUB_TOKEN</code> in the studio's environment and is never stored.</p>
      <AsUser value={as} onChange={setAs} prefer={(u) => u.roles.includes('shared checker')} label="Act as" />
      {c ? (
        <>
          <KV rows={[
            ['Repository', <code data-testid="git-remote">{c.remote}</code>],
            ['Branch', <code>{c.branch}</code>],
            ['Mode', c.mode === 'pull-request' ? 'a pull request per change set' : 'a commit per change set'],
            ['Engine package', <code>{c.target}</code>],
            ['Environment', c.environment ?? '(no activation)'],
            ['On publish', c.autoExport ? 'exported at once' : 'exported by hand'],
            ['Connected by', `${c.connectedBy}, ${String(c.connectedAt).slice(0, 16).replace('T', ' ')}`],
            ['Token', status.data?.tokenNeeded ? (status.data?.tokenPresent ? 'present in the environment' : 'missing: set TH_GITHUB_TOKEN') : 'not needed for this remote'],
          ]} />
          <button disabled={busy} onClick={() => act(() => del(`/api/git${q({ actor: as })}`), () => 'Disconnected.')}>Disconnect</button>
          <h4>Change sets</h4>
          <table className="grid compact" data-testid="git-change-sets">
            <thead><tr><th>Change set</th><th>Members</th><th>Published</th><th>On the branch</th><th /></tr></thead>
            <tbody>
              {(sets.data ?? []).slice().reverse().map((s: any) => (
                <tr key={s.id}>
                  <td className="mono">{s.id}</td>
                  <td className="small">{s.members.map((m: any) => `${m.name}@${m.release}`).join(', ')}</td>
                  <td className="small">{String(s.publishedAt).slice(0, 16).replace('T', ' ')}</td>
                  <td>{done.has(s.id) ? <Badge tone="pass">written</Badge> : <Badge tone="muted">not yet</Badge>}</td>
                  <td><button disabled={busy} onClick={() => act(() => post('/api/git/export', { actor: as, changeSet: s.id }), (r) => r.status === 'unchanged' ? `${s.id} is already on the branch.` : `${s.id}: ${r.files.length} file(s) ${r.status === 'pull-request' ? `pushed to ${r.branch}${r.pullRequest?.url ? `, pull request ${r.pullRequest.url}` : ''}` : `committed as ${String(r.commit).slice(0, 12)}`}.`)}>Export</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : (
        <div className="stack">
          <label className="field"><span>Repository</span><input value={form.remote} onChange={(e) => set('remote', e.target.value)} placeholder="https://github.com/<owner>/<repo>" aria-label="Repository" /></label>
          <label className="field"><span>Branch</span><input value={form.branch} onChange={(e) => set('branch', e.target.value)} aria-label="Branch" /></label>
          <label className="field"><span>Mode</span>
            <select value={form.mode} onChange={(e) => set('mode', e.target.value)} aria-label="Mode"><option value="commit">A commit per change set</option><option value="pull-request">A pull request per change set</option></select>
          </label>
          <label className="field"><span>Engine package</span>
            <select value={form.target} onChange={(e) => set('target', e.target.value)} aria-label="Engine package">{TARGETS.map((t) => <option key={t}>{t}</option>)}</select>
          </label>
          <label className="field"><span>Environment (optional)</span><input value={form.environment} onChange={(e) => set('environment', e.target.value)} placeholder="uat" aria-label="Environment" /></label>
          <label className="check"><input type="checkbox" checked={form.autoExport} onChange={(e) => set('autoExport', e.target.checked)} aria-label="Export on publish" /><span>Export each change set as it is published</span></label>
          <button className="primary" disabled={busy || !form.remote || !form.branch} onClick={() => act(() => put('/api/git', { actor: as, ...form }), (r) => `Connected to ${r.connection.remote} ${r.connection.branch}.`)}>Connect</button>
        </div>
      )}
      {msg && <p role="status" className="ok-line">{msg}</p>}
      <ErrorBox error={error} />
    </Section>
  );
}
