import { createHash } from 'node:crypto';
import { GitHubClient, GitHubError } from './github.js';
import { DRAFT_PREFIX, READ_PREFIXES, STATE_ACTIONS, changeSetAuditPath, changeSetPath, draftBranch, eventFileName, isYaml } from './layout.js';

// The content store (R-11-21 to R-11-24). The studio's only store is a branch of a GitHub repository:
// its `content-configurations/` folder on the content branch, and one branch per change set in
// progress, `studio/<change set id>`. It is read through the GitHub REST API into memory, cached by
// commit SHA, and written through the Git Data API: a tree with its files' text, a commit, and a ref update that
// must fast-forward. Nothing is written to disk: no workspace file, no data folder, no clone.
//
// Reading cost (R-11-26, R-11-27). The audit grows by a file per event, so the audit files of the
// content branch are not read at start: only those the studio's state depends on (a retirement, who set
// a configuration set) and the newest event, whose hash the next event chains to. The rest are read
// when the audit is opened, and cached by blob SHA. A change set branch that the content branch has
// taken in is deleted: in commit mode right after the publish, in pull-request mode when the studio
// next reads the branch and finds the pull request merged. No other branch is ever deleted.

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
  /** A command that only reads (check, test, kit): it deletes no merged change set branch. */
  readOnly?: boolean;
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

/** Forget every cached commit, tree and blob: for a test that counts what a cold start reads. */
export function forgetCache(): void {
  SNAPSHOTS.clear();
  BLOBS.clear();
}

/** A change to one file. `create`: append-only, refused if the file exists with other text (R-09-21, R-11-23). `replace`: the file must still hold `expect`. `free`: a draft branch's own file. */
export interface Change { path: string; text: string | null; guard: 'create' | 'replace' | 'free'; expect?: string | null }

export interface WriteResult { status: 'committed' | 'unchanged' | 'pull-request'; branch: string; commit?: string; files: string[]; pullRequest?: { url: string; number: number; branch: string; opened: true }; deletedBranches?: string[]; tags?: string[]; keptBranches?: KeptBranch[] }
/** A change set branch the studio would have deleted, and kept: it moved since the studio saw it (Issue I-61). */
export interface KeptBranch { branch: string; expected: string; found?: string; reason: string }

/** An annotated release tag to put on the commit a publish makes (R-09-30). */
export interface ReleaseTag { name: string; message: string }

interface Op {
  branch: string;
  message: string;
  changes: Change[];
  /** Release tags on the commit, once it is on the content branch. */
  tags?: ReleaseTag[];
  /** A commit to add as a second parent: the change set's branch, when its publish takes it whole. */
  merge?: () => string | undefined;
  pull?: { title: string; body: string };
  /** Change set ids whose branches hold nothing the commit does not put on the content branch: deleted once it is there (R-11-26). */
  prune?: string[];
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

/**
 * Which of the content branch's files are read at start: all but the audit, and of the audit only the
 * events the studio's state depends on and the newest event, whose hash the next one chains to (R-11-27).
 */
function atStart(paths: string[]): (p: string) => boolean {
  let newest = '';
  for (const p of paths) {
    const n = eventFileName(p);
    if (n && n.stamp > newest) newest = n.stamp;
  }
  return (p) => {
    if (!p.startsWith('audit/')) return true;
    if (!p.startsWith('audit/events/')) return false;
    const n = eventFileName(p);
    return !n || !n.action || STATE_ACTIONS.includes(n.action) || n.stamp === newest;
  };
}

/** The SHA Git gives a blob of this text. */
function gitBlobSha(text: string): string {
  const body = Buffer.from(text, 'utf8');
  return createHash('sha1').update(Buffer.concat([Buffer.from(`blob ${body.length}\0`), body])).digest('hex');
}

/** Tree entries in parts of at most 200 entries and about 1 MB of text. */
function parts<T extends { text: string | null }>(entries: T[]): T[][] {
  const out: T[][] = [];
  let cur: T[] = [];
  let size = 0;
  for (const e of entries) {
    const n = e.text?.length ?? 0;
    if (cur.length && (cur.length >= 200 || size + n > 1_000_000)) {
      out.push(cur);
      cur = [];
      size = 0;
    }
    cur.push(e);
    size += n;
  }
  if (cur.length || !out.length) out.push(cur);
  return out;
}

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
  /** Audit files of the content branch not read yet, path to blob SHA: read when the audit is opened (R-11-27). */
  readonly unread = new Map<string, string>();
  /** Change set branches the studio deleted, newest last (R-11-26). */
  readonly deletedBranches: string[] = [];
  /** Change set branches the studio did not delete because someone else had moved them (Issue I-61). */
  readonly keptBranches: KeptBranch[] = [];
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
    for (const [k, v] of this.unread) s.unread.set(k, v);
    const snap = SNAPSHOTS.get(this.key(this.head));
    if (snap) SNAPSHOTS.set(s.key(this.head), snap);
    return s;
  }

  private async read() {
    let head: string | undefined;
    try {
      head = await this.client.getRef(this.conn.branch);
    } catch (e: any) {
      if (e instanceof GitHubError && e.status === 409) throw new ContentError('content', `${this.repository} is empty: it has no commit, so it has no branch ${this.conn.branch}. Give it a first commit and create the branch; the studio never creates its content branch.`);
      throw new ContentError('content', `The studio cannot read ${this.repository}: ${e?.message ?? e}`);
    }
    if (!head) throw new ContentError('content', `${this.repository} has no branch ${this.conn.branch}. Create it first; the studio never creates its content branch.`);
    this.head = head;
    const snap = await this.snapshot(head);
    this.files.clear();
    this.unread.clear();
    const paths = [...snap.files.keys()].filter(wanted).sort();
    const start = atStart(paths);
    const eager = paths.filter(start);
    for (const p of paths) if (!start(p)) this.unread.set(p, snap.files.get(p)!);
    const texts = await pool(eager, 16, (p) => this.text(snap.files.get(p)!));
    eager.forEach((p, i) => this.files.set(p, texts[i]));
    // The change sets in progress: each studio/ branch with commits the content branch has not taken.
    this.drafts.clear();
    const merged: { branch: string; sha: string }[] = [];
    for (const r of await this.client.listStudioRefs()) {
      this.branchesSeen.add(r.branch);
      const id = r.branch.slice(DRAFT_PREFIX.length);
      // Taken in: its change set is published, or its audit is on the content branch (a branch whose drafts
      // were published with another change set's).
      const published = this.files.has(changeSetPath(id)) || this.has(changeSetAuditPath(id));
      const cmp = await this.client.compare(this.conn.branch, r.branch);
      if (cmp.ahead_by === 0) {
        if (published) merged.push(r);
        continue;
      }
      const bsnap = await this.snapshot(r.sha);
      const diff = [...bsnap.files].filter(([p, sha]) => wanted(p) && snap.files.get(p) !== sha).map(([p]) => p).sort();
      if (!diff.length) {
        // Taken in another way (a squash or rebase merge): nothing on it differs from the content branch.
        if (published) merged.push(r);
        continue;
      }
      const bt = await pool(diff, 16, (p) => this.text(bsnap.files.get(p)!));
      this.drafts.set(id, { id, branch: r.branch, head: r.sha, files: new Map(diff.map((p, i) => [p, bt[i]])) });
    }
    // A published change set's branch that the content branch has taken in: its pull request merged (R-11-26).
    if (!this.conn.readOnly) for (const r of merged) await this.deleteMerged(r.branch, r.sha);
  }

  /**
   * Delete a change set branch the content branch has taken in, if it still points where the studio saw
   * it. GitHub's delete takes no expected SHA (Issue I-61), so the studio reads the ref again right before
   * the delete and refuses when it has moved: the branch is kept, with the commit it found, and named in
   * the result. What is left is the moment between that read and the delete: a push in it is lost with the
   * branch. A failure is not an error: the branch is tried again at the next read.
   */
  private async deleteMerged(branch: string, sha: string): Promise<boolean | KeptBranch> {
    try {
      const now = await this.client.getRef(branch);
      if (now === undefined) return false;
      if (now !== sha) {
        const kept: KeptBranch = { branch, expected: sha, found: now, reason: `${branch} moved from ${sha.slice(0, 12)} to ${now.slice(0, 12)} since the studio saw it; it is kept, and what was pushed to it is not lost` };
        this.keptBranches.push(kept);
        return kept;
      }
      await this.client.deleteChangeSetBranch(branch);
      this.deletedBranches.push(branch);
      this.drafts.delete(branch.slice(DRAFT_PREFIX.length));
      return true;
    } catch {
      return false;
    }
  }

  /** Is this path on the content branch, read or not? */
  has(p: string): boolean {
    return this.files.has(p) || this.unread.has(p);
  }

  /** Read every audit file not read yet (R-11-27). A blob is fetched once: its SHA is the cache key. */
  async readUnread(): Promise<Map<string, string>> {
    const list = [...this.unread].sort(([a], [b]) => a.localeCompare(b));
    const texts = await pool(list, 16, ([, sha]) => this.text(sha));
    const out = new Map<string, string>();
    list.forEach(([p], i) => {
      out.set(p, texts[i]);
      this.files.set(p, texts[i]);
      this.unread.delete(p);
    });
    return out;
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
  write(branch: string, message: string, changes: Change[], opts: { merge?: () => string | undefined; pull?: { title: string; body: string }; prune?: string[]; tags?: ReleaseTag[] } = {}): Promise<WriteResult> {
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
      const op: Op = { branch, message, changes, merge: opts.merge, pull: opts.pull, prune: opts.prune, tags: opts.tags, done, failed };
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
    // Each file's text goes inline in the tree request, so a commit of many files is a few requests, not
    // one per file (GitHub limits how many objects a client creates a minute). Its blob SHA is the one
    // Git gives the text, worked out here. A large commit builds its tree in parts, each on the last.
    const entries = changes.map((c) => ({ path: this.full(c.path), mode: '100644' as const, type: 'blob' as const, sha: c.text === null ? null : gitBlobSha(c.text), text: c.text }));
    let tree = snap.rootTree;
    for (const part of parts(entries)) {
      tree = await this.client.createTree(tree, part.map((e) => (e.text === null ? { path: e.path, mode: e.mode, type: e.type, sha: null } : { path: e.path, mode: e.mode, type: e.type, content: e.text })));
    }
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
      let second: string | undefined;
      if (changes.length) {
        second = op.merge?.();
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
      // Each change set branch the publish emptied is done with: everything on it is now on the content
      // branch. It is deleted if it is still where the studio saw it (R-11-26).
      const deletedBranches: string[] = [];
      const keptBranches: KeptBranch[] = [];
      if (onContent && changes.length) {
        for (const id of op.prune ?? []) {
          const at = this.drafts.get(id)?.head;
          const r = at ? await this.deleteMerged(draftBranch(id), at) : false;
          if (r === true) deletedBranches.push(draftBranch(id));
          else if (r) keptBranches.push(r);
        }
      }
      // Each released package's version is an annotated tag on this commit: created once, never moved (R-09-30).
      const tags: string[] = [];
      if (onContent && changes.length) {
        for (const t of op.tags ?? []) {
          try {
            await this.client.createReleaseTag(t.name, t.message, commit);
          } catch (e) {
            if (e instanceof GitHubError && e.status === 422) throw new ContentError('R-09-30', `The release tag ${t.name} already exists on ${this.repository}: a release tag is never moved. The commit ${commit.slice(0, 12)} is on ${this.conn.branch}; publish a new release.`);
            throw e;
          }
          tags.push(t.name);
        }
      }
      return { status: 'committed', branch: op.branch, commit, files, ...(deletedBranches.length ? { deletedBranches } : {}), ...(tags.length ? { tags } : {}), ...(keptBranches.length ? { keptBranches } : {}) };
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
      prune: [...new Set(list.flatMap((o) => o.prune ?? []))],
      tags: list.flatMap((o) => o.tags ?? []),
      done: (r) => list.forEach((o) => o.done(r)),
      failed: (e) => list.forEach((o) => o.failed(e)),
    };
  });
}

export { draftBranch };
