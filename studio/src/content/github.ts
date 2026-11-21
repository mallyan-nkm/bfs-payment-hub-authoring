// A typed client for exactly the GitHub REST endpoints the content store uses, and no other
// (R-11-08 to R-11-12 as amended). Reads: get ref, matching refs under `studio/`, get tree, get blob,
// compare. Writes, through the Git Data API: create blob, create tree, create commit, create ref,
// update ref (never forced). And one for pull-request mode: create pull request.
//
// The token is read from the environment at each request and is never stored on an object, shown,
// logged or written anywhere (R-09-19). An error names the endpoint and GitHub's message, with the
// token taken out.

export const TOKEN_ENV = 'TH_GITHUB_TOKEN';

export interface TreeItem { path: string; mode: string; type: 'blob' | 'tree' | 'commit'; sha: string; size?: number }
export interface Tree { sha: string; tree: TreeItem[]; truncated: boolean }
export interface Ref { ref: string; object: { sha: string; type: string } }
export interface Compare { status: 'identical' | 'ahead' | 'behind' | 'diverged'; ahead_by: number; behind_by: number; files?: { filename: string; status: string; sha: string | null }[] }
export interface NewTreeEntry { path: string; mode: '100644'; type: 'blob'; sha: string | null }
export interface PullRequest { number: number; html_url: string; state: string }

export class GitHubError extends Error {
  constructor(public status: number, public endpoint: string, message: string) {
    super(message);
  }
}

export interface GitHubClientOptions {
  /** The API base, `https://api.github.com` unless `TH_GITHUB_API` says otherwise. */
  api: string;
  owner: string;
  repo: string;
  /** Where the token comes from. Default: the environment, read at each request. */
  token?: () => string | undefined;
}

/** The endpoints the store calls, by name, for tests that check no other is used. */
export const ENDPOINTS = [
  'GET /git/ref/heads/{branch}',
  'GET /git/matching-refs/heads/studio/',
  'GET /git/trees/{sha}',
  'GET /git/blobs/{sha}',
  'GET /compare/{base}...{head}',
  'POST /git/blobs',
  'POST /git/trees',
  'POST /git/commits',
  'POST /git/refs',
  'PATCH /git/refs/heads/{branch}',
  'POST /pulls',
] as const;

const AUTHOR = { name: 'TransactHub Studio', email: 'studio@transacthub.invalid' };

export class GitHubClient {
  private readonly token: () => string | undefined;
  private readonly base: string;

  constructor(private readonly opts: GitHubClientOptions) {
    this.token = opts.token ?? (() => process.env[TOKEN_ENV] || undefined);
    this.base = `${opts.api.replace(/\/+$/, '')}/repos/${encodeURIComponent(opts.owner)}/${encodeURIComponent(opts.repo)}`;
  }

  get repository(): string {
    return `${this.opts.owner}/${this.opts.repo}`;
  }

  /** Whatever GitHub said, with the token taken out. */
  private scrub(text: string): string {
    const t = this.token();
    return t ? text.split(t).join('***') : text;
  }

  private async call<T>(method: string, endpoint: string, body?: any, allow404 = false): Promise<T | undefined> {
    const token = this.token();
    let res: Response;
    try {
      res = await fetch(this.base + endpoint, {
        method,
        headers: {
          accept: 'application/vnd.github+json',
          'x-github-api-version': '2022-11-28',
          'user-agent': 'transacthub-studio',
          ...(token ? { authorization: `Bearer ${token}` } : {}),
          ...(body !== undefined ? { 'content-type': 'application/json' } : {}),
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
      });
    } catch (e: any) {
      throw new GitHubError(0, `${method} ${endpoint}`, this.scrub(`GitHub cannot be reached at ${this.opts.api}: ${e?.cause?.message ?? e?.message ?? e}`));
    }
    if (res.status === 404 && allow404) return undefined;
    const text = await res.text();
    let data: any;
    try {
      data = text ? JSON.parse(text) : undefined;
    } catch {
      data = { message: text.slice(0, 200) };
    }
    if (!res.ok) throw new GitHubError(res.status, `${method} ${endpoint}`, this.scrub(`GitHub said ${res.status} to ${method} ${endpoint.split('?')[0]}: ${data?.message ?? ''}`));
    return data as T;
  }

  // ------------------------------------------------------------------ reads

  /** The commit a branch points at, or undefined when there is no such branch. */
  async getRef(branch: string): Promise<string | undefined> {
    const r = await this.call<Ref>('GET', `/git/ref/heads/${branch}`, undefined, true);
    return r?.object.sha;
  }

  /** The branches under `studio/`: one per change set in progress (R-11-22). */
  async listStudioRefs(): Promise<{ branch: string; sha: string }[]> {
    const r = (await this.call<Ref[]>('GET', '/git/matching-refs/heads/studio/', undefined, true)) ?? [];
    return r.map((x) => ({ branch: x.ref.replace(/^refs\/heads\//, ''), sha: x.object.sha }));
  }

  /** A tree, by its sha or by a commit's. `recursive` lists every path below it. */
  async getTree(sha: string, recursive = false): Promise<Tree> {
    const t = (await this.call<Tree>('GET', `/git/trees/${sha}${recursive ? '?recursive=1' : ''}`))!;
    if (t.truncated) throw new GitHubError(200, 'GET /git/trees', `The tree ${sha} is too large for one read; the studio does not page trees`);
    return t;
  }

  async getBlob(sha: string): Promise<Buffer> {
    const b = (await this.call<{ content: string; encoding: string }>('GET', `/git/blobs/${sha}`))!;
    return b.encoding === 'base64' ? Buffer.from(b.content, 'base64') : Buffer.from(b.content, 'utf8');
  }

  /** What `head` has that `base` has not, and the other way. */
  async compare(base: string, head: string): Promise<Compare> {
    return (await this.call<Compare>('GET', `/compare/${base}...${head}`))!;
  }

  // ----------------------------------------------------------------- writes

  async createBlob(content: string): Promise<string> {
    return (await this.call<{ sha: string }>('POST', '/git/blobs', { content: Buffer.from(content, 'utf8').toString('base64'), encoding: 'base64' }))!.sha;
  }

  async createTree(baseTree: string | undefined, tree: NewTreeEntry[]): Promise<string> {
    return (await this.call<{ sha: string }>('POST', '/git/trees', { ...(baseTree ? { base_tree: baseTree } : {}), tree }))!.sha;
  }

  async createCommit(message: string, tree: string, parents: string[]): Promise<string> {
    const date = new Date().toISOString();
    return (await this.call<{ sha: string }>('POST', '/git/commits', { message, tree, parents, author: { ...AUTHOR, date }, committer: { ...AUTHOR, date } }))!.sha;
  }

  async createRef(branch: string, sha: string): Promise<void> {
    await this.call('POST', '/git/refs', { ref: `refs/heads/${branch}`, sha });
  }

  /** Move a branch. Never forced: GitHub refuses it with 422 unless it fast-forwards (R-09-21). */
  async updateRef(branch: string, sha: string): Promise<void> {
    await this.call('PATCH', `/git/refs/heads/${branch}`, { sha, force: false });
  }

  async createPullRequest(head: string, base: string, title: string, body: string): Promise<PullRequest> {
    return (await this.call<PullRequest>('POST', '/pulls', { head, base, title, body }))!;
  }
}

export { AUTHOR };
