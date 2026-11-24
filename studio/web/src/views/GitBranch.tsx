import { useEffect, useState } from 'react';
import { get, post, put } from '../api';
import { Badge, ErrorBox, KV, Section, useLoad, useStudio } from '../ui';
import { AsUser } from './Governance';
import { Button, Input, Select, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';

// The content branch (R-09-19 to R-09-22, R-11-21 to R-11-25). The studio's only store is a GitHub
// branch, named by its environment. Each change set in progress is its own branch, studio/<id>; each
// published change set is one commit on the content branch (or a pull request in pull-request mode).
// Nothing is kept on disk. The token comes from TH_GITHUB_TOKEN and is never stored or shown.

const TARGETS = ['temporal', 'dtfx', 'flowable', 'elsa'];

export function GitBranch() {
  const { tenant } = useStudio();
  const status = useLoad(() => get('/api/git'), [tenant]);
  const sets = useLoad(() => get<any[]>('/api/change-sets'), [tenant]);
  const [as, setAs] = useState('');
  const [form, setForm] = useState({ mode: 'commit', target: 'temporal', environment: '' });
  const [error, setError] = useState<string>();
  const [msg, setMsg] = useState<string>();
  const [busy, setBusy] = useState(false);
  const c = status.data?.connection;
  const s = status.data?.settings;
  useEffect(() => {
    if (s) setForm({ mode: s.mode, target: s.target, environment: s.environment ?? '' });
  }, [s?.mode, s?.target, s?.environment]);

  const act = async (fn: () => Promise<any>, ok: (r: any) => string) => {
    setBusy(true);
    setError(undefined);
    setMsg(undefined);
    try {
      setMsg(ok(await fn()));
      status.reload();
      sets.reload();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  };
  const set = (k: string, v: any) => setForm((f) => ({ ...f, [k]: v }));

  return (
    <Section title="Content branch" actions={c ? <Badge tone="pass">connected</Badge> : <Badge tone="muted">not connected</Badge>}>
      <p className="muted small">The studio reads and writes only this branch, through the GitHub API, and keeps no copy on disk. Each change set in progress is its own branch, <code>studio/&lt;id&gt;</code>, one commit per draft save. Approval publishes the change set as one commit, or opens a pull request in pull-request mode. A published file is never rewritten. The token comes from <code>TH_GITHUB_TOKEN</code> in the studio's environment and is never stored or shown.</p>
      {c && (
        <KV rows={[
          ['Repository', <code data-testid="git-remote">{c.repository}</code>],
          ['Branch', <code data-testid="git-branch">{c.branch}</code>],
          ['Folder', <code>{c.root}/</code>],
          ['Head', <code>{String(c.head).slice(0, 12)}</code>],
          ['API', <code>{c.api}</code>],
          ['Mode', s?.mode === 'pull-request' ? 'a pull request per change set' : 'a commit per change set'],
          ['Engine package', <code>{s?.target}</code>],
          ['Environment', s?.environment ?? '(no activation)'],
          ['Token', status.data?.tokenPresent ? 'present in the environment' : 'not in the environment'],
        ]} />
      )}
      <Button disabled={busy} onClick={() => act(() => post('/api/git/reload', {}), (r) => `Read ${r.connection?.branch} again at ${String(r.connection?.head).slice(0, 12)}.`)}>Read the branch again</Button>

      <h4>How the studio publishes</h4>
      <div className="stack">
        <AsUser value={as} onChange={setAs} prefer={(u) => u.roles.includes('platform approver') || u.roles.includes('service operations')} label="Changing as" />
        <label className="field"><span>Mode</span>
          <Select value={form.mode} onChange={(e) => set('mode', e.target.value)} aria-label="Mode"><option value="commit">A commit per change set</option><option value="pull-request">A pull request per change set</option></Select>
        </label>
        <label className="field"><span>Engine package</span>
          <Select value={form.target} onChange={(e) => set('target', e.target.value)} aria-label="Engine package">{TARGETS.map((t) => <option key={t}>{t}</option>)}</Select>
        </label>
        <label className="field"><span>Environment (optional)</span><Input value={form.environment} onChange={(e) => set('environment', e.target.value)} placeholder="uat" aria-label="Environment" /></label>
        <Button appearance="primary" disabled={busy} onClick={() => act(() => put('/api/git', { actor: as, ...form }), (r) => `Saved: ${r.settings.mode}, ${r.settings.target}${r.settings.environment ? `, ${r.settings.environment}` : ''}.`)}>Save settings</Button>
      </div>

      <h4>Change sets in progress</h4>
      <Table size="small" className="grid compact" data-testid="git-drafts">
        <TableHeader><TableRow><TableHeaderCell>Change set</TableHeaderCell><TableHeaderCell>Branch</TableHeaderCell><TableHeaderCell>Drafts</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
        <TableBody>
          {(status.data?.drafts ?? []).map((d: any) => (
            <TableRow key={d.id}><TableCell className="mono">{d.id}</TableCell><TableCell className="mono small">{d.branch}</TableCell><TableCell className="small">{d.members.join(', ')}</TableCell><TableCell>{d.awaitingMerge ? <Badge tone="warn">pull request open</Badge> : <Badge tone="muted">drafting</Badge>}</TableCell></TableRow>
          ))}
        </TableBody>
      </Table>

      <h4>Published change sets</h4>
      <Table size="small" className="grid compact" data-testid="git-change-sets">
        <TableHeader><TableRow><TableHeaderCell>Change set</TableHeaderCell><TableHeaderCell>Members</TableHeaderCell><TableHeaderCell>Published</TableHeaderCell><TableHeaderCell>On the branch</TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {(sets.data ?? []).slice().reverse().map((x: any) => (
            <TableRow key={x.id}>
              <TableCell className="mono">{x.id}</TableCell>
              <TableCell className="small">{x.origin === 'seed' ? `the seed: ${x.members.length} documents from spec/` : (x.members ?? []).map((m: any) => `${m.name}@${m.release}`).join(', ') || (x.waivers ? `waiver ${x.waivers.map((w: any) => w.scenario).join(', ')}` : (x.files ?? []).join(', '))}</TableCell>
              <TableCell className="small">{String(x.publishedAt).slice(0, 16).replace('T', ' ')}</TableCell>
              <TableCell><Badge tone="pass">written</Badge></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {msg && <p role="status" className="ok-line">{msg}</p>}
      <ErrorBox error={error} />
    </Section>
  );
}
