/** The owner tenant (core/store.ts OWNER_TENANT), here to keep this module free of imports. */
const OWNER_TENANT = 'owner';

// Where each thing lives on the content branch, under `content-configurations/` (R-11-08, R-11-21 to
// R-11-25). The runtime's pipeline reads `releases/` and `environments/` (POST /import-branch); the
// studio reads the rest. Every path below is relative to the content root.

export const DRAFT_PREFIX = 'studio/';

/** The branch of a change set in progress (R-11-22). */
export const draftBranch = (id: string) => `${DRAFT_PREFIX}${id}`;

export const scopeOfName = (name: string) => (name.split('.')[0] === 'p' || name.split('.')[0] === 'b' ? name.split('.')[0] : name.split('.').slice(0, 2).join('.'));

const safe = (s: string) => String(s).replace(/[^A-Za-z0-9._@-]+/g, '-');

/** Where a document the studio publishes lives: its path carries its release, so it never changes. */
export function documentPath(doc: any, tenant: string): string {
  const bank = tenant && tenant !== OWNER_TENANT;
  if (doc.kind === 'configuration-set') return `banks/${safe(tenant || doc.tenant)}/configuration-sets/${safe(doc.release)}.yaml`;
  const base = bank ? `banks/${safe(tenant)}/artefacts` : 'artefacts';
  if (doc.kind === 'test') return `${base}/${safe(scopeOfTest(doc))}/tests/${safe(doc.id)}.yaml`;
  if (doc.kind === 'scheme') return `${base}/${safe(doc.scope)}/scheme/${safe(doc.release)}.yaml`;
  if (doc.kind === 'pack') return `${base}/${safe(doc.scope)}/pack/${safe(doc.release)}.yaml`;
  if (doc.kind === 'supersession') return `supersessions/${safe(doc.id)}.yaml`;
  if (doc.kind === 'engine-binding') return `${base}/bindings/${safe(doc.target)}.yaml`;
  if (doc.name && doc.release) return `${base}/${safe(scopeOfName(doc.name))}/${safe(doc.name)}/${safe(doc.release)}.yaml`;
  return `${base}/${safe(doc.scope ?? 'p')}/${safe(doc.kind)}/${safe(doc.name ?? doc.id ?? doc.kind)}${doc.release ? `@${safe(doc.release)}` : ''}.yaml`;
}

function scopeOfTest(doc: any): string {
  return doc.target ? scopeOfName(doc.target) : 'p';
}

/** A script-behind: the owner's beside the shared artefacts, a bank's in its own folder. */
export const scriptPath = (tenant: string, activity: string, language: 'typescript' | 'python') =>
  `${tenant && tenant !== OWNER_TENANT ? `banks/${safe(tenant)}/` : ''}scripts/${safe(activity)}.${language === 'python' ? 'py' : 'ts'}`;

/** A synthetic sample a designer added for tests. */
export const samplePath = (name: string) => `samples/${safe(name)}`;

export const changeSetPath = (id: string) => `change-sets/${safe(id)}.yaml`;
/** The audit of a change set: every event of it, from the first draft to the publish (R-11-23). */
export const changeSetAuditPath = (id: string) => `audit/change-sets/${safe(id)}.yaml`;
/** An event that is not part of a change set: one file each, its name giving its time and action (R-11-23, R-11-27). */
export const eventAuditPath = (at: string, hash: string, action: string) =>
  `audit/events/${at.slice(0, 10)}/${at.slice(11).replace(/[:.]/g, '').replace('Z', '')}-${safeAction(action)}-${hash.replace(/^sha256:/, '').slice(0, 12)}.yaml`;

const safeAction = (a: string) => String(a).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'event';

/** An event file's time (sortable) and action, from its name; the action is absent in a name without one. */
export function eventFileName(p: string): { stamp: string; action?: string } | undefined {
  const m = /^audit\/events\/(\d{4}-\d{2}-\d{2})\/(\d+)-(?:([a-z0-9-]+)-)?([0-9a-f]{12})\.yaml$/.exec(p);
  return m ? { stamp: `${m[1]}/${m[2]}`, action: m[3] } : undefined;
}

/**
 * The events the studio's state depends on: a retirement, and who set a configuration set. Their files
 * are read at start; every other audit file is read when the audit is opened (R-11-27).
 */
export const STATE_ACTIONS = ['retire', 'set-configuration'];
/** The studio's users and their roles: one file, changed only through maker-checker (R-11-23, Issue I-44). */
export const USERS_PATH = 'governance/users.yaml';
/** The studio's settings: publish mode, engine target, environment (R-09-19). */
export const SETTINGS_PATH = 'governance/settings.yaml';
/** In-flight counts that service operations supplies: one file each, the latest applies. */
export const inFlightPath = (at: string) => `governance/in-flight/${at.replace(/[:.]/g, '')}.yaml`;
/** A supersession once handed over: the document, its evidence and both approvals. */
export const handedOverPath = (id: string) => `supersessions/handed-over/${safe(id)}.yaml`;

/** Folders of a bank that the studio writes itself, beside the bank pack's own layout. */
export const BANK_STUDIO_FOLDERS = ['artefacts', 'configuration-sets', 'scripts'];

/** Prefixes of what the studio reads into memory; `releases/` and `environments/` are the runtime's. */
export const READ_PREFIXES = ['artefacts/', 'banks/', 'change-sets/', 'audit/', 'governance/', 'supersessions/', 'samples/', 'scripts/'];

export const isYaml = (p: string) => p.endsWith('.yaml') || p.endsWith('.yml');
