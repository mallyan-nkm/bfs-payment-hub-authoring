// A typed client for exactly the GitHub REST endpoints the content store uses, and no other
// (R-11-08 to R-11-12 as amended). Reads: get ref, matching refs under `studio/`, get tree, get blob,
// compare. Writes, through the Git Data API: create tree (each file's text inline), create commit, create ref,
// update ref (never forced), and delete ref, for a change set branch only (`studio/<id>`, R-11-26).
// And one for pull-request mode: create pull request.
//
// The token may be a placeholder: where a proxy authenticates the requests to the API host, any value
// will do, and the client never checks its format.
//
// The token is read from the environment at each request and is never stored on an object, shown,
// logged or written anywhere (R-09-19). An error names the endpoint and GitHub's message, with the
// token taken out.
//
// Requests go through the proxy the environment names, as curl and git do: HTTPS_PROXY (or HTTP_PROXY for an
// http: API base), unless NO_PROXY names the host. Node's fetch ignores those variables, so the client sends
// its requests with undici's EnvHttpProxyAgent. TLS trusts Node's store, which takes NODE_EXTRA_CA_CERTS (a
// proxy's or a bank's own certificate authority).

import { EnvHttpProxyAgent, fetch as undiciFetch, type Dispatcher } from 'undici';

export const TOKEN_ENV = 'TH_GITHUB_TOKEN';

const PROXY_ENV = ['HTTPS_PROXY', 'https_proxy', 'HTTP_PROXY', 'http_proxy', 'NO_PROXY', 'no_proxy'];
let dispatcher: { key: string; agent: Dispatcher } | undefined;

/** The dispatcher for the API: the environment's proxy settings, read again when they change. */
export function proxyDispatcher(env: NodeJS.ProcessEnv = process.env): Dispatcher {
  const key = PROXY_ENV.map((n) => env[n] ?? '').join('\n');
  if (dispatcher?.key !== key) {
    const old = dispatcher?.agent;
    dispatcher = { key, agent: new EnvHttpProxyAgent() };
    void old?.close().catch(() => undefined);
  }
  return dispatcher.agent;
}

export interface TreeItem { path: string; mode: string; type: 'blob' | 'tree' | 'commit'; sha: string; size?: number }
export interface Tree { sha: string; tree: TreeItem[]; truncated: boolean }
export interface Ref { ref: string; object: { sha: string; type: string } }
export interface Compare { status: 'identical' | 'ahead' | 'behind' | 'diverged'; ahead_by: number; behind_by: number; files?: { filename: string; status: string; sha: string | null }[] }
/** A file in a new tree: by an existing blob's SHA (null deletes it), or by its text, which GitHub stores as a blob. */
export type NewTreeEntry = { path: string; mode: '100644'; type: 'blob'; sha: string | null } | { path: string; mode: '100644'; type: 'blob'; content: string };
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
  'GET /git/matching-refs/heads/studio',
  'GET /git/trees/{sha}',
  'GET /git/blobs/{sha}',
  'GET /compare/{base}...{head}',
  'POST /git/trees',
  'POST /git/commits',
  'POST /git/refs',
  'PATCH /git/refs/heads/{branch}',
  'DELETE /git/refs/heads/studio/{id}',
  'POST /pulls',
  'POST /git/tags',
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
    let res: Awaited<ReturnType<typeof undiciFetch>>;
    // A read is tried once more when the connection drops (a kept-alive socket closed under it).
    for (let attempt = 0; ; attempt++) {
      try {
        res = await undiciFetch(this.base + endpoint, {
          dispatcher: proxyDispatcher(),
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
        break;
      } catch (e: any) {
        if (method === 'GET' && attempt === 0) continue;
        throw new GitHubError(0, `${method} ${endpoint}`, this.scrub(`GitHub cannot be reached at ${this.opts.api}: ${e?.cause?.message ?? e?.message ?? e}`));
      }
    }
    if (res.status === 404 && allow404) return undefined;
    if (res.status === 204) return undefined;
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
    // No trailing slash: some proxies refuse a path that ends in one. `studio` also matches
    // `studio-x`, so the list is narrowed to `studio/` here.
    const r = (await this.call<Ref[]>('GET', '/git/matching-refs/heads/studio', undefined, true)) ?? [];
    return r.map((x) => ({ branch: x.ref.replace(/^refs\/heads\//, ''), sha: x.object.sha })).filter((x) => x.branch.startsWith('studio/'));
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

  /**
   * Delete a change set's branch, `studio/<id>`, once the content branch has taken it in (R-11-26). Any
   * other branch is refused here, before a request is made: the studio never deletes another branch.
   */
  async deleteChangeSetBranch(branch: string): Promise<void> {
    if (!/^studio\/CS-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{3,}$/.test(branch)) throw new GitHubError(0, 'DELETE /git/refs', `The studio deletes only a change set branch (studio/CS-<date>-<n>), not ${branch}`);
    await this.call('DELETE', `/git/refs/heads/${branch}`, undefined, true);
  }

  /**
   * An annotated tag on a commit: a tag object, then its ref (R-09-30). Only a release tag (`release/<workflow>/<release>/<target>`)
   * is made, and never moved: GitHub refuses a second ref of the same name, and the studio never forces one.
   */
  async createReleaseTag(name: string, message: string, commit: string): Promise<string> {
    if (!/^release\/[a-z0-9.-]+\/[0-9]+\.[0-9]+\.[0-9]+\/[a-z]+$/.test(name) && !/^dist\/[a-z0-9-]+\/[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[1-9][0-9]*$/.test(name) && !/^config\/[a-z0-9-]+\/[0-9]+\.[0-9]+\.[0-9]+(\+bindings\.[0-9]+\.[0-9]+\.[0-9]+)?$/.test(name)) throw new GitHubError(0, 'POST /git/tags', `The studio makes only release tags (release/<workflow>/<release>/<target>, dist/<bank>/<id>, config/<bank>/<release>[+bindings.<release>]), not ${name}`);
    const date = new Date().toISOString();
    const tag = (await this.call<{ sha: string }>('POST', '/git/tags', { tag: name, message, object: commit, type: 'commit', tagger: { ...AUTHOR, date } }))!.sha;
    await this.call('POST', '/git/refs', { ref: `refs/tags/${name}`, sha: tag });
    return tag;
  }

  async createPullRequest(head: string, base: string, title: string, body: string): Promise<PullRequest> {
    return (await this.call<PullRequest>('POST', '/pulls', { head, base, title, body }))!;
  }
}

export { AUTHOR };
