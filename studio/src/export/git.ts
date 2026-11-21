import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { toYaml } from '../core/yaml.js';
import { OWNER_TENANT, StoreError } from '../core/store.js';
import type { StudioEnv } from '../env.js';
import { users } from '../governance/governance.js';
import { exportPackage, packageHash, type Target } from './exporter.js';

// Export to a connected Git branch (plan W8; R-09-19 to R-09-22). An owner connects a repository and
// a branch; each published change set is written to it as one commit: the documents at their
// releases, the engine package of each workflow, the change set record and, if the connection names
// an environment, an activation. The branch is insert-only, as the runtime's release table is: a file
// the studio wrote is never rewritten, and the studio never force-pushes. The token is read from the
// environment (TH_GITHUB_TOKEN) at the moment of the push and is never stored, shown or logged.

export type GitMode = 'commit' | 'pull-request';

export interface GitConnection {
  remote: string;
  branch: string;
  mode: GitMode;
  target: Target;
  /** When set, each export also writes an activation for this environment. */
  environment?: string;
  /** Export every change set as it is published. */
  autoExport: boolean;
  connectedBy: string;
  connectedAt: string;
}

export interface GitExport {
  changeSet: string;
  status: 'committed' | 'pull-request' | 'unchanged';
  commit?: string;
  branch: string;
  files: string[];
  skipped: { workflow: string; why: string }[];
  pullRequest?: { branch: string; url?: string; opened: boolean; reason?: string };
  at: string;
  by: string;
}

const AUTHOR = ['-c', 'user.name=TransactHub Studio', '-c', 'user.email=studio@transacthub.invalid'];
const TOKEN_ENV = 'TH_GITHUB_TOKEN';

/** A GitHub https remote, an ssh remote, or a local path or file URL (tests, an on-premises mirror). */
export function remoteKind(remote: string): 'github-https' | 'ssh' | 'local' | undefined {
  if (/^https:\/\/github\.com\/[\w.-]+\/[\w.-]+?(\.git)?\/?$/.test(remote)) return 'github-https';
  if (/^(ssh:\/\/)?git@[\w.-]+[:/][\w./-]+$/.test(remote)) return 'ssh';
  if (remote.startsWith('file://') || path.isAbsolute(remote)) return 'local';
  return undefined;
}

function githubRepo(remote: string): { owner: string; repo: string } | undefined {
  const m = /^https:\/\/github\.com\/([\w.-]+)\/([\w.-]+?)(\.git)?\/?$/.exec(remote);
  return m ? { owner: m[1], repo: m[2] } : undefined;
}

function tokenArgs(remote: string): string[] {
  const token = process.env[TOKEN_ENV];
  if (!token || remoteKind(remote) !== 'github-https') return [];
  const basic = Buffer.from(`x-access-token:${token}`).toString('base64');
  return ['-c', `http.https://github.com/.extraheader=AUTHORIZATION: basic ${basic}`];
}

/** Whatever git said, with the token taken out. */
function scrub(text: string): string {
  const token = process.env[TOKEN_ENV];
  let t = text;
  if (token) t = t.split(token).join('***').split(Buffer.from(`x-access-token:${token}`).toString('base64')).join('***');
  return t.trim();
}

function git(remote: string, args: string[], cwd?: string) {
  const r = spawnSync('git', [...tokenArgs(remote), ...AUTHOR, ...args], {
    cwd, encoding: 'utf8', env: { ...process.env, GIT_TERMINAL_PROMPT: '0', GIT_ASKPASS: 'echo' }, timeout: 120000,
  });
  return { ok: r.status === 0, out: scrub(r.stdout ?? ''), err: scrub(r.stderr ?? (r.error?.message ?? '')) };
}

function mayConnect(env: StudioEnv, actor: string): string | undefined {
  const u = users(env).find((x) => x.id === actor);
  if (!u) return `${actor} is not a user of the studio`;
  if (u.tenant !== OWNER_TENANT) return 'Only the owner connects the studio to a repository';
  return u.roles.includes('shared checker') || u.roles.includes('service operations') ? undefined : `${actor} may not connect a repository: a shared checker or service operations does`;
}

export function connection(env: StudioEnv): GitConnection | undefined {
  return env.store.extra.git?.connection;
}

export function gitStatus(env: StudioEnv) {
  const c = connection(env);
  return { connection: c ?? null, tokenPresent: !!process.env[TOKEN_ENV], tokenNeeded: c ? remoteKind(c.remote) === 'github-https' : false, exports: (env.store.extra.git?.exports ?? []).slice(-20).reverse() };
}

/** Connect (or reconnect): the branch must exist and be readable with the credentials the studio has. */
export function connect(env: StudioEnv, actor: string, input: Partial<GitConnection>): GitConnection {
  const why = mayConnect(env, actor);
  if (why) {
    env.store.record({ tenant: OWNER_TENANT, actor, action: 'connect-git', key: input.remote ?? '', outcome: 'refused', detail: why });
    throw new StoreError('R-06', why);
  }
  const remote = String(input.remote ?? '').trim();
  const branch = String(input.branch ?? '').trim();
  if (!remoteKind(remote)) throw new StoreError('git', `Not a repository the studio can use: ${remote || '(none)'}. Give https://github.com/<owner>/<repo>, an ssh remote, or a local path.`);
  if (!/^[\w][\w./-]*$/.test(branch) || branch.includes('..')) throw new StoreError('git', `Not a branch name: ${branch || '(none)'}`);
  const mode: GitMode = input.mode === 'pull-request' ? 'pull-request' : 'commit';
  const target = (input.target ?? 'temporal') as Target;
  if (!['temporal', 'dtfx', 'flowable', 'elsa'].includes(target)) throw new StoreError('git', `No engine target ${target}`);
  if (input.environment !== undefined && input.environment !== '' && !/^[a-z][a-z0-9-]*$/.test(input.environment)) throw new StoreError('git', 'An environment is lower case letters, digits and hyphens');
  if (remoteKind(remote) === 'github-https' && !process.env[TOKEN_ENV]) throw new StoreError('git', `Set ${TOKEN_ENV} in the studio's environment (a token with contents: write${mode === 'pull-request' ? ' and pull requests: write' : ''} on the repository), then connect again.`);
  const ls = git(remote, ['ls-remote', '--heads', remote, `refs/heads/${branch}`]);
  if (!ls.ok) throw new StoreError('git', `The studio cannot read ${remote}: ${ls.err.split('\n').pop()}`);
  if (!ls.out) throw new StoreError('git', `${remote} has no branch ${branch}. Create it first; the studio never creates the branch it exports to.`);
  const c: GitConnection = { remote, branch, mode, target, environment: input.environment || undefined, autoExport: input.autoExport !== false, connectedBy: actor, connectedAt: new Date().toISOString() };
  env.store.extra.git = { ...(env.store.extra.git ?? {}), connection: c };
  env.store.record({ tenant: OWNER_TENANT, actor, action: 'connect-git', key: remote, detail: `${branch} (${mode}, ${target})` });
  env.store.persist();
  return c;
}

export function disconnect(env: StudioEnv, actor: string) {
  const why = mayConnect(env, actor);
  if (why) throw new StoreError('R-06', why);
  const c = connection(env);
  if (env.store.extra.git) delete env.store.extra.git.connection;
  env.store.record({ tenant: OWNER_TENANT, actor, action: 'disconnect-git', key: c?.remote ?? '' });
  env.store.persist();
}

const scopeOf = (name: string) => (name.split('.')[0] === 'p' || name.split('.')[0] === 'b' ? name.split('.')[0] : name.split('.').slice(0, 2).join('.'));

/** The files a change set becomes on the branch. Their paths carry the release, so they never change. */
export function filesFor(env: StudioEnv, changeSet: any, c: Pick<GitConnection, 'target' | 'environment'>) {
  const files: Record<string, string> = {};
  const skipped: { workflow: string; why: string }[] = [];
  for (const m of changeSet.members) {
    const e = env.store.entries.get(m.key) ?? env.store.all().find((x) => x.name === m.name && x.release === m.release && x.status === 'published');
    if (!e) throw new StoreError('git', `${m.name}@${m.release} of ${changeSet.id} is not in the store`);
    const base = e.kind === 'configuration-set' ? `banks/${e.tenant}/${e.name}` : `artefacts/${scopeOf(e.name)}/${e.name}`;
    files[`${base}/${e.release}.yaml`] = toYaml(e.doc);
    if (e.kind === 'workflow') {
      try {
        const pkg = exportPackage(env, { workflows: [e.name], target: c.target, tenant: changeSet.tenant !== OWNER_TENANT ? changeSet.tenant : undefined });
        const dir = `releases/${scopeOf(e.name)}/${e.name}/${e.release}/${c.target}`;
        for (const [f, content] of Object.entries(pkg.files)) files[`${dir}/${f}`] = content;
        files[`${dir}/package.sha256`] = `${packageHash(pkg)}\n`;
      } catch (err: any) {
        // A leg or a segment is exported with the workflow that uses it, not on its own.
        skipped.push({ workflow: e.name, why: String(err?.message ?? err) });
      }
    }
  }
  const record = { kind: 'change-set', ...changeSet };
  files[`change-sets/${changeSet.id}.yaml`] = toYaml(record);
  if (c.environment) {
    files[`environments/${c.environment}/activations/${changeSet.id}.yaml`] = toYaml({
      kind: 'activation', environment: c.environment, changeSet: changeSet.id, effectiveFrom: changeSet.publishedAt,
      releases: changeSet.members.map((m: any) => ({ name: m.name, release: m.release })),
    });
  }
  return { files, skipped };
}

function workdir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'th-git-export-'));
}

/** Clone the branch, check insert-only against it and write the files. Returns what it wrote and what clashed. */
function prepare(c: GitConnection, branch: string, files: Record<string, string>, fresh: boolean) {
  const dir = workdir();
  const cl = fresh
    ? git(c.remote, ['clone', '--quiet', '--depth', '1', '--branch', branch, '--single-branch', c.remote, dir])
    : { ok: true, out: '', err: '' };
  if (!cl.ok) throw new StoreError('git', `Cannot fetch ${c.remote} ${branch}: ${cl.err.split('\n').pop()}`);
  const written: string[] = [];
  const clashes: string[] = [];
  for (const [rel, content] of Object.entries(files).sort(([a], [b]) => a.localeCompare(b))) {
    const f = path.join(dir, rel);
    if (fs.existsSync(f)) {
      if (fs.readFileSync(f, 'utf8') !== content) clashes.push(rel);
      continue;
    }
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, content);
    written.push(rel);
  }
  return { dir, written, clashes };
}

function message(changeSet: any, skipped: { workflow: string }[]): string {
  const lines = [
    `Release change set ${changeSet.id}: ${changeSet.members.length} artefact${changeSet.members.length === 1 ? '' : 's'}`,
    '',
    ...changeSet.members.map((m: any) => `- ${m.name}@${m.release} (${m.kind})`),
  ];
  if (skipped.length) lines.push('', `Not packaged on their own: ${skipped.map((s) => s.workflow).join(', ')}`);
  lines.push('', `Tenant: ${changeSet.tenant}`, `Maker: ${changeSet.makers.join(', ')}`, `Checker: ${changeSet.checker}`, `Published: ${changeSet.publishedAt}`);
  return lines.join('\n');
}

async function openPullRequest(c: GitConnection, head: string, changeSet: any) {
  const repo = githubRepo(c.remote);
  const token = process.env[TOKEN_ENV];
  if (!repo) return { branch: head, opened: false, reason: 'Not a GitHub remote: the branch is pushed; open the review in the repository host.' };
  if (!token) return { branch: head, opened: false, reason: `${TOKEN_ENV} is not set` };
  const res = await fetch(`https://api.github.com/repos/${repo.owner}/${repo.repo}/pulls`, {
    method: 'POST',
    headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json', 'content-type': 'application/json', 'user-agent': 'transacthub-studio' },
    body: JSON.stringify({ title: `Release change set ${changeSet.id}`, head, base: c.branch, body: `${message(changeSet, [])}\n\nWritten by the studio: published after maker-checker.` }),
  });
  const body: any = await res.json().catch(() => ({}));
  if (!res.ok) return { branch: head, opened: false, reason: scrub(`GitHub said ${res.status}: ${body?.message ?? ''}`) };
  return { branch: head, opened: true, url: body.html_url as string };
}

/**
 * Write one published change set to the connected branch (R-09-20). Exporting it again changes
 * nothing; a file that exists on the branch with other content stops the export and is named (R-09-21).
 */
export async function exportChangeSet(env: StudioEnv, actor: string, id: string): Promise<GitExport> {
  const c = connection(env);
  if (!c) throw new StoreError('git', 'The studio is not connected to a repository');
  const u = users(env).find((x) => x.id === actor);
  if (!u) throw new StoreError('R-06', `${actor} is not a user of the studio`);
  if (u.roles.includes('auditor')) throw new StoreError('R-06', 'An auditor changes nothing and exports nothing');
  const changeSet = (env.store.extra.changeSets ?? []).find((x: any) => x.id === id);
  if (!changeSet) throw new StoreError('not-found', `No change set ${id}`);
  if (u.tenant !== OWNER_TENANT && u.tenant !== changeSet.tenant) throw new StoreError('R-06', `${actor} may not export another tenant's change set`);
  const { files, skipped } = filesFor(env, changeSet, c);
  const head = c.mode === 'pull-request' ? `studio/${id.toLowerCase()}` : c.branch;
  const done = (x: Omit<GitExport, 'at' | 'by' | 'changeSet' | 'skipped'>): GitExport => {
    const r: GitExport = { changeSet: id, skipped, at: new Date().toISOString(), by: actor, ...x };
    const g = (env.store.extra.git ??= {});
    (g.exports ??= []).push(r);
    env.store.record({ tenant: changeSet.tenant, actor, action: 'export-git', key: id, detail: `${r.status} ${r.branch}${r.commit ? ` ${r.commit.slice(0, 12)}` : ''}` });
    env.store.persist();
    return r;
  };

  if (c.mode === 'pull-request') {
    // The review branch is the change set's own: once pushed, it is the record of it.
    const exists = git(c.remote, ['ls-remote', '--heads', c.remote, `refs/heads/${head}`]);
    if (exists.ok && exists.out) {
      const p = prepare(c, head, files, true);
      fs.rmSync(p.dir, { recursive: true, force: true });
      if (p.clashes.length) throw new StoreError('git', `The review branch ${head} already holds other content for ${p.clashes.join(', ')}`);
      if (!p.written.length) return done({ status: 'unchanged', branch: head, files: [] });
    }
  }

  for (let attempt = 0; attempt < 3; attempt++) {
    const p = prepare(c, c.branch, files, true);
    try {
      if (p.clashes.length) {
        env.store.record({ tenant: changeSet.tenant, actor, action: 'export-git', key: id, outcome: 'refused', detail: `insert-only: ${p.clashes.join(', ')}` });
        throw new StoreError('R-09-21', `The branch ${c.branch} already holds other content for ${p.clashes.length} file(s) the studio would write: ${p.clashes.slice(0, 5).join(', ')}${p.clashes.length > 5 ? ', …' : ''}. A release is never rewritten: publish a new release.`);
      }
      if (!p.written.length) return done({ status: 'unchanged', branch: c.branch, files: [] });
      if (c.mode === 'pull-request') {
        const co = git(c.remote, ['checkout', '--quiet', '-b', head], p.dir);
        if (!co.ok) throw new StoreError('git', co.err);
      }
      const add = git(c.remote, ['add', '--', ...p.written], p.dir);
      const commit = add.ok ? git(c.remote, ['commit', '--quiet', '-m', message(changeSet, skipped)], p.dir) : add;
      if (!commit.ok) throw new StoreError('git', `Cannot commit: ${commit.err}`);
      const sha = git(c.remote, ['rev-parse', 'HEAD'], p.dir).out;
      const push = git(c.remote, ['push', '--quiet', 'origin', `HEAD:refs/heads/${head}`], p.dir);
      if (push.ok) {
        if (c.mode === 'pull-request') {
          const pr = await openPullRequest(c, head, changeSet);
          return done({ status: 'pull-request', commit: sha, branch: head, files: p.written, pullRequest: pr });
        }
        return done({ status: 'committed', commit: sha, branch: c.branch, files: p.written });
      }
      // The branch moved while we wrote: start again from its new head (never force).
      if (!/rejected|non-fast-forward|fetch first|stale info/i.test(push.err) || attempt === 2) throw new StoreError('git', `Push refused: ${push.err.split('\n').slice(-2).join(' ')}`);
    } finally {
      fs.rmSync(p.dir, { recursive: true, force: true });
    }
  }
  throw new StoreError('git', 'The branch kept moving; try again');
}

/** After a publish: export it if the connection says so. A failed export never undoes the publish. */
export async function afterPublish(env: StudioEnv, actor: string, changeSetId: string): Promise<GitExport | { error: string } | undefined> {
  const c = connection(env);
  if (!c?.autoExport) return undefined;
  try {
    return await exportChangeSet(env, actor, changeSetId);
  } catch (e: any) {
    return { error: String(e?.message ?? e) };
  }
}

/** For tests: a bare repository with one commit on a branch. */
export function initBareRepo(dir: string, branch = 'main'): string {
  const bare = path.join(dir, 'remote.git');
  const seed = path.join(dir, 'seed');
  fs.mkdirSync(seed, { recursive: true });
  const run = (args: string[], cwd?: string) => {
    const r = spawnSync('git', [...AUTHOR, ...args], { cwd, encoding: 'utf8' });
    if (r.status !== 0) throw new Error(r.stderr);
    return r.stdout.trim();
  };
  run(['init', '--quiet', '--bare', '--initial-branch', branch, bare]);
  run(['init', '--quiet', '--initial-branch', branch, seed]);
  fs.writeFileSync(path.join(seed, 'README.md'), '# content-configurations\n');
  run(['add', '.'], seed);
  run(['commit', '--quiet', '-m', 'Start'], seed);
  run(['push', '--quiet', bare, `HEAD:refs/heads/${branch}`], seed);
  return bare;
}

export function sha256(s: string) {
  return createHash('sha256').update(s).digest('hex');
}
