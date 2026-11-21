import { GitHubClient, GitHubError } from './github.js';
import { DRAFT_PREFIX, READ_PREFIXES, changeSetPath, draftBranch, isYaml } from './layout.js';

// The content store (R-11-21 to R-11-24). The studio's only store is a branch of a GitHub repository:
// its `content-configurations/` folder on the content branch, and one branch per change set in
// progress, `studio/<change set id>`. It is read through the GitHub REST API into memory, cached by
// commit SHA, and written through the Git Data API: blobs, a tree, a commit, and a ref update that
// must fast-forward. Nothing is written to disk: no workspace file, no data folder, no clone.

export interface ContentConnection {
  /** The API base, `https://api.github.com` by default (`TH_GITHUB_API`). */
  api: string;
  owner: string;
  repo: string;
  /** The content branch (`TH_CONTENT_BRANCH`, default `main`). */
  branch: string;
  /** The folder on the branch that holds the content (`TH_CONTENT_ROOT`, default `content-configurations`). */
  root: string;
  /** Where the token comes from; by default `TH_GITHUB_TOKEN`, read at each request and never stored. */
  token?: () => string | undefined;
}

export class ContentError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

/** The connection the environment names, or undefined when `TH_CONTENT_REPO` is not set. */
export function connectionFromEnv(e: NodeJS.ProcessEnv = process.env): ContentConnection | undefined {
  const full = (e.TH_CONTENT_REPO ?? '').trim();
  if (!full) return undefined;
  const m = /^([\w.-]+)\/([\w.-]+)$/.exec(full);
  if (!m) throw new ContentError('content', `TH_CONTENT_REPO is ${full}: write it as owner/repo`);
  const branch = (e.TH_CONTENT_BRANCH ?? 'main').trim() || 'main';
  if (!/^[\w][\w./-]*$/.test(branch) || branch.includes('..') || branch.startsWith(DRAFT_PREFIX)) throw new ContentError('content', `TH_CONTENT_BRANCH is not a content branch: ${branch}`);
  return { api: (e.TH_GITHUB_API ?? 'https://api.github.com').trim(), owner: m[1], repo: m[2], branch, root: (e.TH_CONTENT_ROOT ?? 'content-configurations').replace(/^\/+|\/+$/g, '') };
}

export const NOT_CONNECTED = 'The studio reads its content from a GitHub branch and keeps no local copy. Set TH_CONTENT_REPO=owner/repo, TH_CONTENT_BRANCH and TH_GITHUB_TOKEN (and TH_GITHUB_API for another API base), or run `npm run studio:dev` for an in-memory one seeded from content-configurations/.';

/** A commit's content folder: path (under the root) to blob sha, and the root tree to build on. */
export interface Snapshot { commit: string; rootTree: string; files: Map<string, string> }

/** Process-wide caches. A commit, a tree and a blob never change, so their SHA is the key. */
const SNAPSHOTS = new Map<string, Snapshot>();
const BLOBS = new Map<string, string>();

/** A change to one file. `create`: append-only, refused if the file exists with other text (R-09-21, R-11-23). `replace`: the file must still hold `expect`. `free`: a draft branch's own file. */
export interface Change { path: string; text: string | null; guard: 'create' | 'replace' | 'free'; expect?: string | null }

export interface WriteResult { status: 'committed' | 'unchanged' | 'pull-request'; branch: string; commit?: string; files: string[]; pullRequest?: { url: string; number: number; branch: string; opened: true } }

interface Op {
  branch: string;
  message: string;
  changes: Change[];
  /** A commit to add as a second parent: the change set's branch, when its publish takes it whole. */
  merge?: () => string | undefined;
  pull?: { title: string; body: string };
  done: (r: WriteResult) => void;
  failed: (e: unknown) => void;
}

export interface DraftBranch {
  id: string;
  branch: string;
  head?: string;
  /** The files that differ from the content branch: the change set's drafts, scripts and audit. */
  files: Map<string, string>;
}

const wanted = (p: string) =>
  READ_PREFIXES.some((x) => p.startsWith(x)) &&
  (isYaml(p) || /(^|\/)samples\/[^/]+$/.test(p) || /(^|\/)scripts\/[^/]+\.(ts|js|py)$/.test(p));

async function pool<T, R>(items: T[], n: number, fn: (x: T) => Promise<R>): Promise<R[]> {
  const out: R[] = new Array(items.length);
  let i = 0;
  await Promise.all(Array.from({ length: Math.min(n, items.length) }, async () => {
    while (i < items.length) {
      const k = i++;
      out[k] = await fn(items[k]);
    }
  }));
  return out;
}

export class ContentStore {
  readonly client: GitHubClient;
  /** The content branch's head as the studio last read or wrote it. */
  head = '';
  /** The content branch's files the studio reads, path to text, as it last read or wrote them. */
  readonly files = new Map<string, string>();
  readonly drafts = new Map<string, DraftBranch>();
  /** Every `studio/` branch seen, open or not: a change set id is never used twice. */
  readonly branchesSeen = new Set<string>();
  private allocated = new Set<string>();
  private queue: Promise<void> = Promise.resolve();
  private pending = 0;
  private failure?: unknown;
  private tx?: Op[];
  /** What the studio wrote, newest last: for a status page and for tests. */
  readonly writes: WriteResult[] = [];

  private constructor(readonly conn: ContentConnection) {
    this.client = new GitHubClient({ api: conn.api, owner: conn.owner, repo: conn.repo, token: conn.token });
  }

  get repository(): string {
    return `${this.conn.owner}/${this.conn.repo}`;
  }

  private key(commit: string) {
    return `${this.conn.api}|${this.repository}|${this.conn.root}|${commit}`;
  }

  /** Read the content branch and every open change set branch into memory. */
  static async open(conn: ContentConnection): Promise<ContentStore> {
    const s = new ContentStore(conn);
    await s.read();
    return s;
  }

  /**
   * Another store over a copy of this one's repository (tests: the fake's fork of a seeded repository).
   * It starts from what this one has read, so it makes no request until it writes.
   */
  forkInto(conn: ContentConnection): ContentStore {
    const s = new ContentStore(conn);
    s.head = this.head;
    for (const [k, v] of this.files) s.files.set(k, v);
    const snap = SNAPSHOTS.get(this.key(this.head));
    if (snap) SNAPSHOTS.set(s.key(this.head), snap);
    return s;
  }

  private async read() {
    let head: string | undefined;
    try {
      head = await this.client.getRef(this.conn.branch);
    } catch (e: any) {
      throw new ContentError('content', `The studio cannot read ${this.repository}: ${e?.message ?? e}`);
    }
    if (!head) throw new ContentError('content', `${this.repository} has no branch ${this.conn.branch}. Create it first; the studio never creates its content branch.`);
    this.head = head;
    const snap = await this.snapshot(head);
    this.files.clear();
    const paths = [...snap.files.keys()].filter(wanted).sort();
    const texts = await pool(paths, 16, (p) => this.text(snap.files.get(p)!));
    paths.forEach((p, i) => this.files.set(p, texts[i]));
    // The change sets in progress: each studio/ branch with commits the content branch has not taken.
    this.drafts.clear();
    for (const r of await this.client.listStudioRefs()) {
      this.branchesSeen.add(r.branch);
      const id = r.branch.slice(DRAFT_PREFIX.length);
      const cmp = await this.client.compare(this.conn.branch, r.branch);
      if (cmp.ahead_by === 0) continue;
      const bsnap = await this.snapshot(r.sha);
      const diff = [...bsnap.files].filter(([p, sha]) => wanted(p) && snap.files.get(p) !== sha).map(([p]) => p).sort();
      if (!diff.length) continue;
      const bt = await pool(diff, 16, (p) => this.text(bsnap.files.get(p)!));
      this.drafts.set(id, { id, branch: r.branch, head: r.sha, files: new Map(diff.map((p, i) => [p, bt[i]])) });
    }
  }

  /** Read again from GitHub: what others published since, and their change sets in progress. */
  async reload(): Promise<void> {
    await this.flush().catch(() => undefined);
    await this.read();
  }

  /** Has the content branch moved since the studio last read or wrote it? */
  async moved(): Promise<boolean> {
    return (await this.client.getRef(this.conn.branch)) !== this.head;
  }

  private async text(sha: string): Promise<string> {
    let t = BLOBS.get(sha);
    if (t === undefined) {
      t = (await this.client.getBlob(sha)).toString('utf8');
      BLOBS.set(sha, t);
    }
    return t;
  }

  /** A commit's content folder, from the cache or from GitHub. */
  async snapshot(commit: string): Promise<Snapshot> {
    const k = this.key(commit);
    const hit = SNAPSHOTS.get(k);
    if (hit) return hit;
    const top = await this.client.getTree(commit);
    let files = new Map<string, string>();
    let sub: string | undefined = top.sha;
    let items = top.tree;
    for (const seg of this.conn.root ? this.conn.root.split('/') : []) {
      const e = items.find((x) => x.path === seg && x.type === 'tree');
      sub = e?.sha;
      if (!sub) break;
      items = (await this.client.getTree(sub)).tree;
    }
    if (sub) files = new Map((await this.client.getTree(sub, true)).tree.filter((x) => x.type === 'blob').map((x) => [x.path, x.sha]));
    const snap: Snapshot = { commit, rootTree: top.sha, files };
    SNAPSHOTS.set(k, snap);
    return snap;
  }

  // ------------------------------------------------------------------ change set ids

  /** A new change set id, `CS-<date>-<nnn>`, used by no branch and no published change set. */
  allocateChangeSetId(date = new Date().toISOString().slice(0, 10)): string {
    const prefix = `CS-${date}-`;
    const used = new Set<string>([...this.allocated, ...[...this.branchesSeen].map((b) => b.slice(DRAFT_PREFIX.length)), ...this.drafts.keys()]);
    for (const p of this.files.keys()) {
      const m = /^change-sets\/(.+)\.yaml$/.exec(p);
      if (m) used.add(m[1]);
    }
    let n = 1;
    for (const id of used) if (id.startsWith(prefix)) n = Math.max(n, Number(id.slice(prefix.length)) + 1 || n);
    const id = `${prefix}${String(n).padStart(3, '0')}`;
    this.allocated.add(id);
    return id;
  }

  /** Is this change set published, its record on the content branch? */
  published(id: string): boolean {
    return this.files.has(changeSetPath(id));
  }

  // ------------------------------------------------------------------------ writes

  /** Group every write made in `fn` into one commit per branch. */
  transaction<T>(fn: () => T): T {
    if (this.tx) return fn();
    this.tx = [];
    try {
      const r = fn();
      const ops = this.tx;
      this.tx = undefined;
      for (const op of merge(ops)) this.send(op);
      return r;
    } catch (e) {
      this.tx = undefined;
      throw e;
    }
  }

  /**
   * Write to the content branch or to a change set's branch. The in-memory view changes at once; the
   * commit goes out in order, in the background. `flush()` waits for it and reports a failure.
   */
  write(branch: string, message: string, changes: Change[], opts: { merge?: () => string | undefined; pull?: { title: string; body: string } } = {}): Promise<WriteResult> {
    if (branch === this.conn.branch) {
      for (const c of changes) {
        if (c.text === null) this.files.delete(c.path);
        else if (c.guard !== 'create' || !this.files.has(c.path)) this.files.set(c.path, c.text);
      }
    } else {
      const id = branch.slice(DRAFT_PREFIX.length);
      const d = this.drafts.get(id) ?? { id, branch, files: new Map<string, string>() };
      this.drafts.set(id, d);
      for (const c of changes) {
        if (c.text === null) d.files.delete(c.path);
        else d.files.set(c.path, c.text);
      }
    }
    const p = new Promise<WriteResult>((done, failed) => {
      const op: Op = { branch, message, changes, merge: opts.merge, pull: opts.pull, done, failed };
      if (this.tx) this.tx.push(op);
      else this.send(op);
    });
    // A caller need not wait: a failure is reported by flush(), not as an unhandled rejection.
    p.catch(() => undefined);
    return p;
  }

  private send(op: Op) {
    this.pending++;
    this.queue = this.queue.then(async () => {
      try {
        const r = await this.run(op);
        this.writes.push(r);
        op.done(r);
      } catch (e) {
        this.failure ??= e;
        op.failed(e);
      } finally {
        this.pending--;
      }
    });
  }

  /** Wait until every write so far is on GitHub. A write that failed is reported once, here. */
  async flush(): Promise<void> {
    await this.queue;
    const f = this.failure;
    this.failure = undefined;
    if (f) throw f;
  }

  get busy(): boolean {
    return this.pending > 0;
  }

  private full(p: string) {
    return this.conn.root ? `${this.conn.root}/${p}` : p;
  }

  /** The changes that still apply on top of a snapshot; a clash with an append-only file stops it. */
  private async resolve(snap: Snapshot, changes: Change[]): Promise<Change[]> {
    const out: Change[] = [];
    const clashes: string[] = [];
    for (const c of changes) {
      const sha = snap.files.get(c.path);
      const now = sha === undefined ? null : await this.text(sha);
      if (c.guard === 'create') {
        if (now === null) out.push(c);
        else if (now !== c.text) clashes.push(c.path);
      } else if (c.guard === 'replace') {
        if (now === c.text) continue;
        if (c.expect !== undefined && now !== c.expect) {
          throw new ContentError('R-11-23', `${this.full(c.path)} changed on ${this.conn.branch} since the studio read it. Nothing was written. Reload and try again.`);
        }
        out.push(c);
      } else if (now !== c.text) out.push(c);
    }
    if (clashes.length) {
      throw new ContentError('R-09-21', `The branch ${this.conn.branch} already holds other content for ${clashes.length} file(s) the studio would write: ${clashes.slice(0, 5).map((p) => this.full(p)).join(', ')}${clashes.length > 5 ? ', …' : ''}. A published file is never rewritten: publish a new release.`);
    }
    return out;
  }

  private async commit(snap: Snapshot, changes: Change[], message: string, parents: string[]): Promise<{ commit: string; snap: Snapshot }> {
    const entries = await pool(changes, 8, async (c) => ({ path: this.full(c.path), mode: '100644' as const, type: 'blob' as const, sha: c.text === null ? null : await this.client.createBlob(c.text) }));
    const tree = await this.client.createTree(snap.rootTree, entries);
    const commit = await this.client.createCommit(message, tree, parents);
    const files = new Map(snap.files);
    changes.forEach((c, i) => {
      if (c.text === null) files.delete(c.path);
      else {
        files.set(c.path, entries[i].sha!);
        BLOBS.set(entries[i].sha!, c.text);
      }
    });
    const next: Snapshot = { commit, rootTree: tree, files };
    SNAPSHOTS.set(this.key(commit), next);
    return { commit, snap: next };
  }

  private async run(op: Op): Promise<WriteResult> {
    const onContent = op.branch === this.conn.branch;
    const d = onContent ? undefined : this.drafts.get(op.branch.slice(DRAFT_PREFIX.length));
    for (let attempt = 0; ; attempt++) {
      // A new change set branch starts from the content branch's head.
      const base = onContent ? this.head : d?.head ?? this.head;
      const snap = await this.snapshot(base);
      const changes = await this.resolve(snap, op.changes);
      if (!changes.length && !op.pull) return { status: 'unchanged', branch: op.branch, commit: base, files: [] };
      let commit = base;
      if (changes.length) {
        const second = op.merge?.();
        const parents = [base, ...(second && second !== base ? [second] : [])];
        commit = (await this.commit(snap, changes, op.message, parents)).commit;
      }
      try {
        if (!onContent && !d?.head) {
          await this.client.createRef(op.branch, commit);
          this.branchesSeen.add(op.branch);
        } else if (commit !== base) await this.client.updateRef(op.branch, commit);
      } catch (e) {
        if (!(e instanceof GitHubError) || e.status !== 422) throw e;
        if (!onContent && !d?.head) throw new ContentError('conflict', `The branch ${op.branch} already exists on ${this.repository}: another studio took this change set id. Reload and save again.`);
        if (attempt === 0) {
          // Someone else wrote first. Re-read the branch head, check the write again on top of it, and retry once.
          const now = await this.client.getRef(op.branch);
          if (onContent) this.head = now ?? this.head;
          else if (d) d.head = now ?? d.head;
          continue;
        }
        throw new ContentError('conflict', `The branch ${op.branch} moved again while the studio wrote to it. The studio re-read it and tried once more; nothing was forced. Reload and try again.`);
      }
      if (onContent) this.head = commit;
      else if (d) d.head = commit;
      const files = changes.map((c) => this.full(c.path));
      if (op.pull) {
        const pr = await this.client.createPullRequest(op.branch, this.conn.branch, op.pull.title, op.pull.body);
        return { status: 'pull-request', branch: op.branch, commit, files, pullRequest: { url: pr.html_url, number: pr.number, branch: op.branch, opened: true } };
      }
      return { status: 'committed', branch: op.branch, commit, files };
    }
  }
}

/** Ops on one branch, merged into one: the later change to a path wins. */
function merge(ops: Op[]): Op[] {
  const by = new Map<string, Op[]>();
  for (const op of ops) by.set(op.branch, [...(by.get(op.branch) ?? []), op]);
  return [...by.values()].map((list) => {
    if (list.length === 1) return list[0];
    const changes = new Map<string, Change>();
    for (const op of list) for (const c of op.changes) changes.set(c.path, c);
    const main = list.find((o) => o.pull || o.merge) ?? list[0];
    return {
      branch: list[0].branch,
      message: main.message,
      changes: [...changes.values()],
      merge: list.find((o) => o.merge)?.merge,
      pull: list.find((o) => o.pull)?.pull,
      done: (r) => list.forEach((o) => o.done(r)),
      failed: (e) => list.forEach((o) => o.failed(e)),
    };
  });
}

export { draftBranch };
