import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import type { AddressInfo } from 'node:net';
import * as path from 'node:path';

// A fake GitHub REST API, in memory: exactly the endpoints the studio's content layer uses (get ref,
// get tree, get blob, create blob, create tree, create commit, update ref, create ref, delete ref,
// matching refs, compare, create pull request). Objects are hashed as Git hashes them, so a SHA means what it means
// on GitHub, and a ref update that is not a fast-forward is refused with 422, as GitHub refuses it.
// It holds nothing on disk. Every test of the studio's store runs against it.

type Obj =
  | { type: 'blob'; data: Buffer }
  | { type: 'tree'; entries: TreeEntry[] }
  | { type: 'commit'; tree: string; parents: string[]; message: string; author: { name: string; email: string; date: string } };

interface TreeEntry { name: string; mode: string; type: 'blob' | 'tree'; sha: string }

interface Repo { refs: Map<string, string>; pulls: Pull[] }

export interface Pull { number: number; title: string; head: string; base: string; body: string; state: 'open' | 'closed'; merged: boolean }

export interface FakeRequest { method: string; path: string }

const sha1 = (b: Buffer) => createHash('sha1').update(b).digest('hex');

/** Git sorts tree entries by name, a tree's name taken as if it ended in a slash. */
const sortKey = (e: TreeEntry) => (e.type === 'tree' ? `${e.name}/` : e.name);

export class FakeGitHub {
  readonly objects = new Map<string, Obj>();
  readonly repos = new Map<string, Repo>();
  /** Every request, method and path only: no header, so never a token. */
  readonly requests: FakeRequest[] = [];
  /** Ref updates that asked to force. The studio never asks; a test checks that it did not. */
  forced = 0;
  /** Branches deleted through the API, as `<repo> <branch>`. */
  readonly deleted: string[] = [];
  /** Called before a ref update is checked: a test moves the branch here to play another writer. */
  beforeRefUpdate?: (repo: string, branch: string) => void;
  private server?: http.Server;
  url = '';
  private clock = Date.parse('2026-11-21T09:00:00Z');

  /** `token`: when set, a request without `Authorization: Bearer <token>` (or `token <token>`) gets 401. */
  constructor(private readonly token?: string) {}

  // ------------------------------------------------------------------ objects

  private put(o: Obj): string {
    let body: Buffer;
    if (o.type === 'blob') body = o.data;
    else if (o.type === 'tree') {
      const sorted = [...o.entries].sort((a, b) => (sortKey(a) < sortKey(b) ? -1 : sortKey(a) > sortKey(b) ? 1 : 0));
      o.entries = sorted;
      body = Buffer.concat(sorted.map((e) => Buffer.concat([Buffer.from(`${e.mode} ${e.name}\0`), Buffer.from(e.sha, 'hex')])));
    } else {
      const secs = Math.floor(Date.parse(o.author.date) / 1000);
      const who = `${o.author.name} <${o.author.email}> ${secs} +0000`;
      body = Buffer.from([`tree ${o.tree}`, ...o.parents.map((p) => `parent ${p}`), `author ${who}`, `committer ${who}`, '', o.message].join('\n'));
    }
    const sha = sha1(Buffer.concat([Buffer.from(`${o.type} ${body.length}\0`), body]));
    if (!this.objects.has(sha)) this.objects.set(sha, o);
    return sha;
  }

  blob(text: string | Buffer): string {
    return this.put({ type: 'blob', data: Buffer.isBuffer(text) ? text : Buffer.from(text, 'utf8') });
  }

  private get<T extends Obj['type']>(sha: string, type: T): Extract<Obj, { type: T }> | undefined {
    const o = this.objects.get(sha);
    return o && o.type === type ? (o as any) : undefined;
  }

  /** A tree built from a flat map of paths, or one changed from a base tree. A null sha deletes. */
  private buildTree(base: string | undefined, changes: { path: string; mode: string; type: 'blob' | 'tree'; sha: string | null }[]): string {
    const entries = new Map<string, TreeEntry>((base ? this.get(base, 'tree')?.entries ?? [] : []).map((e) => [e.name, { ...e }]));
    const nested = new Map<string, typeof changes>();
    for (const c of changes) {
      const [head, ...rest] = c.path.split('/');
      if (rest.length) {
        if (!nested.has(head)) nested.set(head, []);
        nested.get(head)!.push({ ...c, path: rest.join('/') });
      } else if (c.sha === null) entries.delete(head);
      else entries.set(head, { name: head, mode: c.mode, type: c.type, sha: c.sha });
    }
    for (const [dir, sub] of nested) {
      const cur = entries.get(dir);
      const sha = this.buildTree(cur?.type === 'tree' ? cur.sha : undefined, sub);
      if (this.get(sha, 'tree')!.entries.length === 0) entries.delete(dir);
      else entries.set(dir, { name: dir, mode: '40000', type: 'tree', sha });
    }
    return this.put({ type: 'tree', entries: [...entries.values()] });
  }

  /** Every file of a tree, path to blob sha. */
  private flat(tree: string, prefix = '', out = new Map<string, string>()): Map<string, string> {
    for (const e of this.get(tree, 'tree')?.entries ?? []) {
      const p = prefix + e.name;
      if (e.type === 'tree') this.flat(e.sha, p + '/', out);
      else out.set(p, e.sha);
    }
    return out;
  }

  private treeOf(ish: string, repo?: Repo): string | undefined {
    if (this.get(ish, 'tree')) return ish;
    const c = this.get(ish, 'commit');
    if (c) return c.tree;
    const ref = repo?.refs.get(ish);
    return ref ? this.get(ref, 'commit')?.tree : undefined;
  }

  private ancestors(sha: string): Set<string> {
    const seen = new Set<string>();
    const queue = [sha];
    while (queue.length) {
      const s = queue.shift()!;
      if (seen.has(s)) continue;
      seen.add(s);
      queue.push(...(this.get(s, 'commit')?.parents ?? []));
    }
    return seen;
  }

  private commit(tree: string, parents: string[], message: string, author = { name: 'Someone', email: 'someone@example.invalid' }): string {
    this.clock += 1000;
    return this.put({ type: 'commit', tree, parents, message, author: { ...author, date: new Date(this.clock).toISOString() } });
  }

  // --------------------------------------------------------- for tests, in process

  repo(full: string): Repo {
    if (!this.repos.has(full)) this.repos.set(full, { refs: new Map(), pulls: [] });
    return this.repos.get(full)!;
  }

  /** A repository with one branch holding these files (a path under the repository root to its text). */
  seed(full: string, branch: string, files: Record<string, string | Buffer>, message = 'Start'): string {
    const tree = this.buildTree(undefined, Object.entries(files).map(([p, t]) => ({ path: p, mode: '100644', type: 'blob' as const, sha: this.blob(t) })));
    const sha = this.commit(tree, [], message);
    this.repo(full).refs.set(branch, sha);
    return sha;
  }

  /** A repository whose files are a folder on disk, each under `prefix`. Reading only: nothing is written. */
  seedFromDir(full: string, branch: string, dir: string, prefix: string): string {
    const files: Record<string, Buffer> = {};
    const walk = (d: string, rel: string) => {
      for (const ent of fs.readdirSync(d, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
        const p = path.join(d, ent.name);
        const r = rel ? `${rel}/${ent.name}` : ent.name;
        if (ent.isDirectory()) walk(p, r);
        else if (ent.isFile()) files[prefix ? `${prefix}/${r}` : r] = fs.readFileSync(p);
      }
    };
    walk(dir, '');
    return this.seed(full, branch, files, `Seed from ${path.basename(dir)}`);
  }

  /** Another repository with the same refs, sharing the objects: as a fork does. */
  fork(from: string, to: string): void {
    const src = this.repo(from);
    this.repos.set(to, { refs: new Map(src.refs), pulls: [] });
  }

  head(full: string, branch: string): string | undefined {
    return this.repos.get(full)?.refs.get(branch);
  }

  branches(full: string): string[] {
    return [...(this.repos.get(full)?.refs.keys() ?? [])].sort();
  }

  /** The files of a branch, path to text. */
  files(full: string, branch: string): Record<string, string> {
    const head = this.head(full, branch);
    if (!head) return {};
    const out: Record<string, string> = {};
    for (const [p, s] of this.flat(this.get(head, 'commit')!.tree)) out[p] = this.get(s, 'blob')!.data.toString('utf8');
    return out;
  }

  /** The commit messages of a branch, newest first, following first parents. */
  log(full: string, branch: string): { sha: string; message: string; parents: string[]; author: string }[] {
    const out: { sha: string; message: string; parents: string[]; author: string }[] = [];
    let s = this.head(full, branch);
    while (s) {
      const c = this.get(s, 'commit')!;
      out.push({ sha: s, message: c.message, parents: c.parents, author: c.author.name });
      s = c.parents[0];
    }
    return out;
  }

  /** Someone else commits to a branch (an edit on GitHub, another studio). */
  commitFiles(full: string, branch: string, files: Record<string, string | null>, message = 'An edit elsewhere'): string {
    const repo = this.repo(full);
    const head = repo.refs.get(branch);
    const base = head ? this.get(head, 'commit')!.tree : undefined;
    const tree = this.buildTree(base, Object.entries(files).map(([p, t]) => ({ path: p, mode: '100644', type: 'blob' as const, sha: t === null ? null : this.blob(t) })));
    const sha = this.commit(tree, head ? [head] : [], message);
    repo.refs.set(branch, sha);
    return sha;
  }

  /** Merge a pull request, as GitHub's merge button does: a merge commit, path by path. */
  mergePull(full: string, number: number): string {
    const repo = this.repo(full);
    const pr = repo.pulls.find((p) => p.number === number);
    if (!pr || pr.state !== 'open') throw new Error(`No open pull request ${number}`);
    const base = repo.refs.get(pr.base)!;
    const head = repo.refs.get(pr.head)!;
    const mb = this.mergeBase(base, head);
    const at = (c: string | undefined) => (c ? this.flat(this.get(c, 'commit')!.tree) : new Map<string, string>());
    const [B, H, M] = [at(base), at(head), at(mb)];
    const out = new Map(B);
    for (const p of new Set([...H.keys(), ...M.keys()])) {
      const h = H.get(p);
      const m = M.get(p);
      if (h === m) continue;
      const b = B.get(p);
      if (b !== m && b !== h) throw new Error(`Merge conflict on ${p}`);
      if (h === undefined) out.delete(p);
      else out.set(p, h);
    }
    const tree = this.buildTree(undefined, [...out].map(([p, s]) => ({ path: p, mode: '100644', type: 'blob' as const, sha: s })));
    const sha = this.commit(tree, [base, head], `Merge pull request #${number} from ${pr.head}`);
    repo.refs.set(pr.base, sha);
    pr.state = 'closed';
    pr.merged = true;
    return sha;
  }

  private mergeBase(a: string, b: string): string | undefined {
    const A = this.ancestors(a);
    const queue = [b];
    const seen = new Set<string>();
    while (queue.length) {
      const s = queue.shift()!;
      if (seen.has(s)) continue;
      seen.add(s);
      if (A.has(s)) return s;
      queue.push(...(this.get(s, 'commit')?.parents ?? []));
    }
    return undefined;
  }

  // -------------------------------------------------------------------- HTTP

  /** `unref`: do not keep the process alive for it (a command that reads, then exits). */
  async start(opts: { unref?: boolean; port?: number } = {}): Promise<string> {
    this.server = http.createServer((req, res) => {
      let raw = '';
      req.on('data', (c) => (raw += c));
      req.on('end', () => {
        const send = (status: number, body: any) => (status === 204 ? res.writeHead(204).end() : res.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body)));
        try {
          const url = new URL(req.url ?? '/', 'http://fake');
          this.requests.push({ method: req.method ?? 'GET', path: url.pathname });
          if (this.token) {
            const auth = String(req.headers.authorization ?? '');
            if (auth !== `Bearer ${this.token}` && auth !== `token ${this.token}`) return send(401, { message: 'Bad credentials' });
          }
          const r = this.handle(req.method ?? 'GET', url, raw ? JSON.parse(raw) : undefined);
          send(r.status, r.body);
        } catch (e: any) {
          send(500, { message: String(e?.message ?? e) });
        }
      });
    });
    // Keep idle connections open longer than a test waits, so a client never reuses one being closed.
    this.server.keepAliveTimeout = 120_000;
    await new Promise<void>((r) => this.server!.listen(opts.port ?? 0, '127.0.0.1', () => r()));
    if (opts.unref) this.server.unref();
    this.url = `http://127.0.0.1:${(this.server!.address() as AddressInfo).port}`;
    return this.url;
  }

  async stop(): Promise<void> {
    if (!this.server) return;
    this.server.closeAllConnections?.();
    await new Promise<void>((r) => this.server!.close(() => r()));
    this.server = undefined;
  }

  private handle(method: string, url: URL, body: any): { status: number; body: any } {
    const m = /^\/repos\/([^/]+)\/([^/]+)\/(.*)$/.exec(url.pathname);
    if (!m) return { status: 404, body: { message: 'Not Found' } };
    const full = `${m[1]}/${m[2]}`;
    const rest = decodeURIComponent(m[3]);
    const repo = this.repos.get(full);
    if (!repo) return { status: 404, body: { message: 'Not Found' } };
    const nf = { status: 404, body: { message: 'Not Found' } };
    const unprocessable = (message: string) => ({ status: 422, body: { message } });
    const refBody = (name: string, sha: string) => ({ ref: `refs/heads/${name}`, object: { sha, type: 'commit' } });
    let x: RegExpExecArray | null;

    if (method === 'GET' && (x = /^git\/ref\/heads\/(.+)$/.exec(rest))) {
      const sha = repo.refs.get(x[1]);
      return sha ? { status: 200, body: refBody(x[1], sha) } : nf;
    }
    if (method === 'GET' && (x = /^git\/matching-refs\/heads\/(.*)$/.exec(rest))) {
      const prefix = x[1];
      return { status: 200, body: [...repo.refs].filter(([n]) => n.startsWith(prefix)).sort(([a], [b]) => a.localeCompare(b)).map(([n, s]) => refBody(n, s)) };
    }
    if (method === 'GET' && (x = /^git\/trees\/(.+)$/.exec(rest))) {
      const tree = this.treeOf(x[1], repo);
      if (!tree) return nf;
      const recursive = url.searchParams.has('recursive');
      const list: any[] = [];
      const walk = (t: string, prefix: string) => {
        for (const e of this.get(t, 'tree')!.entries) {
          const p = prefix + e.name;
          list.push({ path: p, mode: e.mode === '40000' ? '040000' : e.mode, type: e.type, sha: e.sha, ...(e.type === 'blob' ? { size: this.get(e.sha, 'blob')!.data.length } : {}) });
          if (recursive && e.type === 'tree') walk(e.sha, p + '/');
        }
      };
      walk(tree, '');
      return { status: 200, body: { sha: tree, tree: list, truncated: false } };
    }
    if (method === 'GET' && (x = /^git\/blobs\/([0-9a-f]{40})$/.exec(rest))) {
      const b = this.get(x[1], 'blob');
      return b ? { status: 200, body: { sha: x[1], size: b.data.length, encoding: 'base64', content: b.data.toString('base64') } } : nf;
    }
    if (method === 'POST' && rest === 'git/blobs') {
      if (typeof body?.content !== 'string') return unprocessable('content is required');
      const data = body.encoding === 'base64' ? Buffer.from(body.content, 'base64') : Buffer.from(body.content, 'utf8');
      return { status: 201, body: { sha: this.blob(data) } };
    }
    if (method === 'POST' && rest === 'git/trees') {
      if (body?.base_tree && !this.get(body.base_tree, 'tree')) return unprocessable('base_tree is not a tree');
      for (const e of body?.tree ?? []) if (typeof e.content !== 'string' && e.sha !== null && !this.objects.has(e.sha)) return unprocessable(`No object ${e.sha}`);
      // An entry with `content` is stored as a blob of that text, as GitHub does.
      const sha = this.buildTree(body?.base_tree, (body?.tree ?? []).map((e: any) => ({ path: e.path, mode: e.mode === '040000' ? '40000' : e.mode, type: e.type, sha: typeof e.content === 'string' ? this.blob(e.content) : e.sha })));
      return { status: 201, body: { sha } };
    }
    if (method === 'POST' && rest === 'git/commits') {
      if (!this.get(body?.tree, 'tree')) return unprocessable('tree is not a tree');
      for (const p of body?.parents ?? []) if (!this.get(p, 'commit')) return unprocessable(`No commit ${p}`);
      const sha = this.commit(body.tree, body.parents ?? [], String(body.message ?? ''), body.author ?? undefined);
      return { status: 201, body: { sha, tree: { sha: body.tree }, parents: (body.parents ?? []).map((p: string) => ({ sha: p })) } };
    }
    if (method === 'PATCH' && (x = /^git\/refs\/heads\/(.+)$/.exec(rest))) {
      // A test's chance to be someone else writing first.
      this.beforeRefUpdate?.(full, x[1]);
      const cur = repo.refs.get(x[1]);
      if (!cur) return unprocessable('Reference does not exist');
      if (!this.get(body?.sha, 'commit')) return unprocessable('Object does not exist');
      if (body.force) this.forced++;
      else if (!this.ancestors(body.sha).has(cur)) return unprocessable('Update is not a fast forward');
      repo.refs.set(x[1], body.sha);
      return { status: 200, body: refBody(x[1], body.sha) };
    }
    if (method === 'DELETE' && (x = /^git\/refs\/heads\/(.+)$/.exec(rest))) {
      if (!repo.refs.has(x[1])) return unprocessable('Reference does not exist');
      repo.refs.delete(x[1]);
      this.deleted.push(`${full} ${x[1]}`);
      return { status: 204, body: undefined };
    }
    if (method === 'POST' && rest === 'git/refs') {
      const name = String(body?.ref ?? '').replace(/^refs\/heads\//, '');
      if (!name || name === body?.ref) return unprocessable('ref must start with refs/heads/');
      if (repo.refs.has(name)) return unprocessable('Reference already exists');
      if (!this.get(body?.sha, 'commit')) return unprocessable('Object does not exist');
      repo.refs.set(name, body.sha);
      return { status: 201, body: refBody(name, body.sha) };
    }
    if (method === 'GET' && (x = /^compare\/(.+?)\.\.\.(.+)$/.exec(rest))) {
      const resolve = (r: string) => repo.refs.get(r) ?? (this.get(r, 'commit') ? r : undefined);
      const base = resolve(x[1]);
      const head = resolve(x[2]);
      if (!base || !head) return nf;
      const A = this.ancestors(base);
      const H = this.ancestors(head);
      const ahead = [...H].filter((s) => !A.has(s)).length;
      const behind = [...A].filter((s) => !H.has(s)).length;
      const mb = this.mergeBase(base, head);
      const from = mb ? this.flat(this.get(mb, 'commit')!.tree) : new Map<string, string>();
      const to = this.flat(this.get(head, 'commit')!.tree);
      const files: any[] = [];
      for (const p of [...new Set([...from.keys(), ...to.keys()])].sort()) {
        if (from.get(p) === to.get(p)) continue;
        files.push({ filename: p, sha: to.get(p) ?? null, status: !from.has(p) ? 'added' : !to.has(p) ? 'removed' : 'modified' });
      }
      const status = ahead === 0 && behind === 0 ? 'identical' : ahead === 0 ? 'behind' : behind === 0 ? 'ahead' : 'diverged';
      return { status: 200, body: { status, ahead_by: ahead, behind_by: behind, merge_base_commit: mb ? { sha: mb } : null, files } };
    }
    if (method === 'POST' && rest === 'pulls') {
      if (!repo.refs.has(body?.head) || !repo.refs.has(body?.base)) return unprocessable('head or base does not exist');
      if (repo.pulls.some((p) => p.head === body.head && p.base === body.base && p.state === 'open')) return unprocessable(`A pull request already exists for ${body.head}`);
      const pr: Pull = { number: repo.pulls.length + 1, title: String(body.title ?? ''), head: body.head, base: body.base, body: String(body.body ?? ''), state: 'open', merged: false };
      repo.pulls.push(pr);
      return { status: 201, body: { number: pr.number, state: pr.state, html_url: `https://github.example.invalid/${full}/pull/${pr.number}`, head: { ref: pr.head }, base: { ref: pr.base } } };
    }
    return nf;
  }
}

/** The repository root, from this file: studio/test/support. */
export const REPO_ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..', '..', '..');
