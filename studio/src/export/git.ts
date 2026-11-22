import { createHash } from 'node:crypto';
import { toYaml } from '../core/yaml.js';
import { OWNER_TENANT, StoreError, type Settings } from '../core/store.js';
import { SETTINGS_PATH, scopeOfName } from '../content/layout.js';
import { TOKEN_ENV } from '../content/github.js';
import type { StudioEnv } from '../env.js';
import { Forbidden, permit } from '../governance/governance.js';
import { exportPackage, packageHash, signPackage, type Target } from './exporter.js';
import { claimsOf, NoRootWorkflow, SignatureRejected } from './signing.js';

// The content branch (plan W8; R-09-19 to R-09-22 as amended, R-11-21 to R-11-25). The studio is
// connected to one repository and branch by its environment (TH_CONTENT_REPO, TH_CONTENT_BRANCH,
// TH_GITHUB_TOKEN, TH_GITHUB_API) and reads and writes nothing else. Each published change set is one
// commit there (or, in pull-request mode, one commit on its own branch and a pull request): the
// documents at their releases, the engine package of each workflow, the change set record, its audit
// and, if the settings name an environment, an activation. The branch is insert-only: a file the
// studio wrote is never rewritten, and the studio never force-pushes. The token is read from the
// environment at each request and is never stored, shown, logged or written to the audit.

export type GitMode = Settings['mode'];

const TARGETS: Target[] = ['temporal', 'dtfx', 'flowable', 'elsa'];

/** Who changes the studio's settings: a shared checker or service operations of the owner. */
function mayChangeSettings(env: StudioEnv, actor: string): string | undefined {
  return permit(env, actor, OWNER_TENANT, 'author', { kind: 'settings' });
}

/** The connection and the settings, and what the studio wrote lately. Never the token: only whether there is one. */
export function gitStatus(env: StudioEnv) {
  const c = env.store.content;
  const host = c ? new URL(c.conn.api).host : undefined;
  return {
    connection: c ? { repository: c.repository, branch: c.conn.branch, root: c.conn.root, api: host, head: c.head } : null,
    tokenPresent: !!process.env[TOKEN_ENV],
    settings: env.store.settings,
    drafts: c ? [...c.drafts.values()].filter((d) => env.store.all().some((e) => e.changeSet === d.id && (e.status === 'draft' || e.status === 'in-review'))).map((d) => ({ id: d.id, branch: d.branch, members: env.store.all().filter((e) => e.changeSet === d.id && (e.status === 'draft' || e.status === 'in-review')).map((e) => e.key), awaitingMerge: env.store.all().some((e) => e.changeSet === d.id && e.awaitingMerge) })) : [],
    pending: env.store.extra.pendingChangeSets ?? [],
    writes: (c?.writes ?? []).slice(-20).reverse(),
    /** Change set branches deleted once the content branch took them in (R-11-26). */
    deletedBranches: (c?.deletedBranches ?? []).slice(-20).reverse(),
    /** Change set branches kept because someone else moved them since the studio saw them (Issue I-61). */
    keptBranches: (c?.keptBranches ?? []).slice(-20).reverse(),
  };
}

/** Change how the studio publishes (R-09-19): commit or pull-request mode, the engine package, an environment. */
export function changeSettings(env: StudioEnv, actor: string, input: Partial<Settings>): Settings {
  const why = mayChangeSettings(env, actor);
  if (why) {
    env.store.record({ tenant: OWNER_TENANT, actor, action: 'change-settings', key: 'settings', outcome: 'refused', detail: why });
    throw new Forbidden(why);
  }
  const mode: GitMode = input.mode === 'pull-request' ? 'pull-request' : input.mode === 'commit' || input.mode === undefined ? (input.mode ?? env.store.settings.mode) : (() => { throw new StoreError('git', `No mode ${input.mode}: commit or pull-request`); })();
  const target = (input.target ?? env.store.settings.target) as Target;
  if (!TARGETS.includes(target)) throw new StoreError('git', `No engine target ${target}`);
  const environment = input.environment === undefined ? env.store.settings.environment : input.environment || undefined;
  if (environment !== undefined && !/^[a-z][a-z0-9-]*$/.test(environment)) throw new StoreError('git', 'An environment is lower case letters, digits and hyphens');
  const distributions = input.distributions === undefined ? env.store.settings.distributions : input.distributions === true;
  const settings: Settings = { mode, target, ...(environment ? { environment } : {}), ...(distributions ? { distributions: true } : {}), changedBy: actor, changedAt: new Date().toISOString() };
  env.store.replaceStateFile(SETTINGS_PATH, { kind: 'studio-settings', settings }, `Settings: ${mode}, ${target}${environment ? `, ${environment}` : ''}\n\nChanged by ${actor}.`, { tenant: OWNER_TENANT, actor, action: 'change-settings', key: 'settings', detail: `${mode} (${target}${environment ? `, ${environment}` : ''})` });
  env.store.settings = settings;
  return settings;
}

/** The files a change set adds beside its documents: each workflow's engine package and, when set, an activation. Their paths carry the release, so they never change. */
export function filesFor(env: StudioEnv, changeSet: any, s: Pick<Settings, 'target' | 'environment'>) {
  const files: Record<string, string> = {};
  const skipped: { workflow: string; why: string }[] = [];
  // One annotated tag per released version, on the publishing commit: what a runtime's package source lists (R-09-30).
  const tags: { name: string; message: string }[] = [];
  for (const m of changeSet.members) {
    const e = env.store.entries.get(m.key) ?? env.store.all().find((x) => x.name === m.name && x.release === m.release && x.status === 'published');
    if (!e) throw new StoreError('git', `${m.name}@${m.release} of ${changeSet.id} is not in the store`);
    if (e.kind !== 'workflow') continue;
    try {
      const pkg = exportPackage(env, { workflows: [e.name], target: s.target as Target, tenant: changeSet.tenant !== OWNER_TENANT ? changeSet.tenant : undefined });
      // A package with no root workflow of its own (a subflow, a segment) is not a release the runtime takes.
      claimsOf(pkg.manifest);
      const dir = `releases/${scopeOfName(e.name)}/${e.name}/${e.release}/${s.target}`;
      // Signed at release (R-09-23): the runtime takes no package from the branch without its signature.
      const signed = signPackage(pkg, changeSet.id);
      for (const [f, content] of Object.entries(signed.files)) files[`${dir}/${f}`] = content;
      files[`${dir}/package.sha256`] = `${packageHash(pkg)}\n`;
      const st = JSON.parse(signed.files['release.json']);
      tags.push({
        name: `release/${e.name}/${e.release}/${s.target}`,
        message: [`Release ${st.releaseId}`, '', `Package: ${dir}/`, `Change set: ${changeSet.id}`, `Signed by key: ${st.keyId}`, `Statement sha256: ${createHash('sha256').update(signed.files['release.json']).digest('hex')}`, `Package hash: ${packageHash(pkg)}`].join('\n'),
      });
    } catch (err: any) {
      // Without the studio's signing key nothing is released: the publish stops and says why.
      if (err instanceof SignatureRejected && !(err instanceof NoRootWorkflow)) throw new StoreError('signing', err.message);
      // A leg or a segment is exported with the workflow that uses it, not on its own.
      skipped.push({ workflow: e.name, why: String(err?.message ?? err) });
    }
  }
  if (s.environment) {
    files[`environments/${s.environment}/activations/${changeSet.id}.yaml`] = toYaml({
      kind: 'activation', environment: s.environment, changeSet: changeSet.id, effectiveFrom: changeSet.publishedAt,
      releases: changeSet.members.map((m: any) => ({ name: m.name, release: m.release })),
    });
  }
  return { files, skipped, tags };
}

/** The commit message of a published change set: its members, its makers and its checker (R-09-20). */
export function commitMessage(changeSet: any, skipped: { workflow: string }[]): string {
  const lines = [
    `Release change set ${changeSet.id}: ${changeSet.members.length} artefact${changeSet.members.length === 1 ? '' : 's'}`,
    '',
    ...changeSet.members.map((m: any) => `- ${m.name}@${m.release} (${m.kind})`),
  ];
  if (skipped.length) lines.push('', `Not packaged on their own: ${skipped.map((s) => s.workflow).join(', ')}`);
  lines.push('', `Tenant: ${changeSet.tenant}`, `Maker: ${changeSet.makers.join(', ')}`, `Checker: ${changeSet.checker}`, `Published: ${changeSet.publishedAt}`);
  return lines.join('\n');
}
