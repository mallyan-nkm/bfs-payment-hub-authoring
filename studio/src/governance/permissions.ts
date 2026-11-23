// The studio's permission table (R-11-29 to R-11-33, Issue I-65). One table, from role, scope, kind
// and action to allowed or not. Every write, publish and approval of the API asks it, through
// `permit` in governance.ts; the web app reads it only to choose what to show (R-10-11).
//
// Roles, as the owner decided them on 2026-11-22:
//   bank configurator   a bank's hooks, rules and rule values, dials, simulators and scenarios, port
//                       bindings, and the test bench, in scope b of its own tenant only
//   bank approver       approves what a bank configurator changed, and accepts shared releases
//   workflow designer   composes workflows, subflows and schemes on the canvas from existing blocks
//   workflow developer  builds the blocks: activities, scripts, native bindings, ports, rules,
//                       tables, simulators, extensions
//   platform approver   approves what a designer or a developer changed; retires; waives
//   administrator       the users and roles, itself through maker-checker
//   auditor             reads; changes nothing and exports nothing
//   service operations  in-flight counts, retirement and the publishing settings

export const ROLES = [
  'bank configurator', 'bank approver',
  'workflow designer', 'workflow developer', 'platform approver',
  'administrator', 'auditor', 'service operations',
] as const;
export type Role = (typeof ROLES)[number];

/** The roles of earlier releases of the studio, read as the roles that hold the same rights (Issue I-69). */
export const LEGACY_ROLES: Record<string, Role[]> = {
  'shared designer': ['workflow designer', 'workflow developer'],
  'shared checker': ['platform approver', 'administrator'],
  'bank designer': ['bank configurator'],
  'bank checker': ['bank approver'],
};

/** Which tenant a role belongs to: a bank role only in a bank tenant, a platform role only in the owner tenant. */
export const ROLE_TENANT: Record<Role, 'bank' | 'owner' | 'either'> = {
  'bank configurator': 'bank', 'bank approver': 'bank',
  'workflow designer': 'owner', 'workflow developer': 'owner', 'platform approver': 'owner',
  administrator: 'owner', auditor: 'either', 'service operations': 'owner',
};

/** What a permission is about. Artefact kinds are the documents' `kind`; the rest are studio functions. */
export const ARTEFACT_KINDS = [
  'workflow', 'scheme', 'activity', 'script', 'engine-binding', 'port', 'rule', 'ruleset', 'table',
  'simulator', 'extension', 'hook', 'configuration-set', 'port-bindings', 'test', 'sample', 'supersession', 'operations-view', 'error-dictionary', 'reference-table',
] as const;
export const STUDIO_KINDS = ['bench', 'import', 'export', 'deploy', 'waiver', 'settings', 'release', 'in-flight', 'users', 'assistant', 'distribution', 'configuration-release'] as const;
export type Kind = (typeof ARTEFACT_KINDS)[number] | (typeof STUDIO_KINDS)[number];
export const KINDS: Kind[] = [...ARTEFACT_KINDS, ...STUDIO_KINDS];

/**
 * The actions. `author` is a maker's: save, change or delete a draft, and request its release. A
 * maker's saves need no approval. `approve` is the one checker step: it makes a change set's drafts a
 * released version, the thing that is exported to the runtime; the approver is never a maker of the
 * change set (R-06-04, R-11-19). `accept` is a bank's acceptance of a shared release; `run` runs a
 * bench, an import, an export or the assistant; `retire` retires a release or records in-flight counts.
 */
export const ACTIONS = ['read', 'author', 'approve', 'accept', 'run', 'retire'] as const;
export type Action = (typeof ACTIONS)[number];

/** Where: a bank's own scope `b`, the shared scopes (`p`, `<cluster>.p`, `<cluster>.<scheme>`), or the studio itself. */
export type ScopeClass = 'bank' | 'shared' | 'studio';

export interface Grant { role: Role; scope: ScopeClass; kinds: readonly Kind[]; actions: readonly Action[] }

const BANK_KINDS = ['hook', 'activity', 'script', 'rule', 'ruleset', 'table', 'configuration-set', 'port-bindings', 'reference-table', 'simulator', 'test', 'sample'] as const;
// An operations view (R-05-48) declares a workflow's operator actions; it is designed with the workflow (I-69).
const DESIGNER_KINDS = ['workflow', 'scheme', 'operations-view', 'test', 'sample'] as const;
// An error dictionary (R-05-51) documents the errors and codes the blocks raise; it is built with them (I-69).
const DEVELOPER_KINDS = ['activity', 'script', 'engine-binding', 'port', 'rule', 'ruleset', 'table', 'simulator', 'extension', 'error-dictionary', 'reference-table', 'test', 'sample'] as const;
const SHARED_KINDS = [...new Set<Kind>([...DESIGNER_KINDS, ...DEVELOPER_KINDS])];

/**
 * The table. A role may do an action on a kind in a scope class when a row says so; nothing else is
 * allowed. Reading is open to every user of a tenant for what the tenant may see (R-06-01), and is
 * not in the table. A kind that is not here, or an action that is not, is refused.
 */
export const GRANTS: readonly Grant[] = [
  // Bank Configurator: scope b of its own tenant.
  { role: 'bank configurator', scope: 'bank', kinds: BANK_KINDS, actions: ['author'] },
  { role: 'bank configurator', scope: 'bank', kinds: ['bench'], actions: ['run'] },
  { role: 'bank configurator', scope: 'bank', kinds: ['import', 'export', 'deploy', 'assistant'], actions: ['run'] },
  // Bank approver: approves the configurator's changes and accepts shared releases.
  { role: 'bank approver', scope: 'bank', kinds: BANK_KINDS, actions: ['approve'] },
  { role: 'bank approver', scope: 'bank', kinds: ['configuration-set'], actions: ['accept'] },
  { role: 'bank approver', scope: 'bank', kinds: ['bench', 'export', 'deploy'], actions: ['run'] },
  // A bank's distribution when its bank pack changes, and its configuration releases (R-11-40).
  { role: 'bank approver', scope: 'bank', kinds: ['distribution', 'configuration-release'], actions: ['approve'] },
  // Workflow Designer: composition on the canvas, no code.
  { role: 'workflow designer', scope: 'shared', kinds: DESIGNER_KINDS, actions: ['author'] },
  { role: 'workflow designer', scope: 'shared', kinds: ['supersession'], actions: ['author'] },
  { role: 'workflow designer', scope: 'shared', kinds: ['bench', 'import', 'export', 'deploy', 'assistant'], actions: ['run'] },
  // Workflow Developer: the building blocks.
  { role: 'workflow developer', scope: 'shared', kinds: DEVELOPER_KINDS, actions: ['author'] },
  { role: 'workflow developer', scope: 'shared', kinds: ['supersession'], actions: ['author'] },
  { role: 'workflow developer', scope: 'shared', kinds: ['bench', 'import', 'export', 'deploy', 'assistant'], actions: ['run'] },
  // Platform approver: approves designer and developer changes.
  { role: 'platform approver', scope: 'shared', kinds: [...SHARED_KINDS, 'supersession'], actions: ['approve'] },
  { role: 'platform approver', scope: 'shared', kinds: ['supersession'], actions: ['author'] },
  { role: 'platform approver', scope: 'studio', kinds: ['waiver'], actions: ['approve'] },
  { role: 'platform approver', scope: 'studio', kinds: ['release', 'in-flight'], actions: ['retire'] },
  { role: 'platform approver', scope: 'studio', kinds: ['settings'], actions: ['author'] },
  { role: 'platform approver', scope: 'shared', kinds: ['bench', 'export', 'deploy'], actions: ['run'] },
  // The distribution of every bank that takes an approved platform or scheme change (R-11-40).
  { role: 'platform approver', scope: 'shared', kinds: ['distribution'], actions: ['approve'] },
  // Administrator: users and roles only, through maker-checker.
  { role: 'administrator', scope: 'studio', kinds: ['users'], actions: ['author', 'approve'] },
  // Service operations: what the runtime tells the studio, and how it publishes.
  { role: 'service operations', scope: 'studio', kinds: ['release', 'in-flight'], actions: ['retire'] },
  { role: 'service operations', scope: 'studio', kinds: ['settings'], actions: ['author'] },
  // Auditor: nothing beyond reading.
];

/** The scope class of a scope: `b` is a bank's, every other scope is shared. */
export function scopeClass(scope: string | undefined, kind: Kind): ScopeClass {
  if ((STUDIO_KINDS as readonly string[]).includes(kind) && !['bench', 'import', 'export', 'deploy', 'assistant', 'distribution', 'configuration-release'].includes(kind)) return 'studio';
  return scope === 'b' ? 'bank' : 'shared';
}

/** Does the table allow it? The rows that grant it, or none. */
export function granted(roles: readonly string[], scope: ScopeClass, kind: Kind, action: Action): Grant[] {
  return GRANTS.filter((g) => roles.includes(g.role) && g.scope === scope && g.kinds.includes(kind) && g.actions.includes(action));
}

/** Roles a user holds, with the roles of earlier releases read as today's. Unknown names grant nothing. */
export function effectiveRoles(roles: readonly string[] | undefined): Role[] {
  const out = new Set<Role>();
  for (const r of roles ?? []) {
    if ((ROLES as readonly string[]).includes(r)) out.add(r as Role);
    for (const x of LEGACY_ROLES[r] ?? []) out.add(x);
  }
  return [...out];
}

/** The kind of a document, as the table names it. A configuration set is the bank's dials and hook bindings. */
export function kindOf(doc: any): Kind | undefined {
  const k = doc?.kind;
  return (KINDS as string[]).includes(k) ? (k as Kind) : undefined;
}

/** The web app's surfaces, by role (R-10-11). The API is the control; this only chooses what to show. */
export const SURFACES: Record<Role, string[]> = {
  'bank configurator': ['bank', 'designer', 'catalogue', 'data', 'editors', 'import', 'tests', 'tenant', 'export', 'assistant', 'governance'],
  'bank approver': ['bank', 'catalogue', 'tests', 'tenant', 'governance', 'export'],
  'workflow designer': ['designer', 'editors', 'catalogue', 'data', 'assistant', 'import', 'tests', 'kit', 'export', 'governance'],
  'workflow developer': ['editors', 'catalogue', 'data', 'assistant', 'import', 'tests', 'kit', 'export', 'governance'],
  'platform approver': ['governance', 'catalogue', 'data', 'tests', 'kit', 'export'],
  administrator: ['governance', 'catalogue'],
  auditor: ['catalogue', 'governance', 'kit'],
  'service operations': ['governance', 'catalogue', 'kit'],
};

/** Where a role lands when it signs in. */
export const LANDING: Record<Role, string> = {
  'bank configurator': 'bank', 'bank approver': 'governance',
  'workflow designer': 'designer', 'workflow developer': 'editors', 'platform approver': 'governance',
  administrator: 'governance', auditor: 'catalogue', 'service operations': 'governance',
};

const ORDER = ['bank', 'designer', 'editors', 'catalogue', 'data', 'assistant', 'import', 'tests', 'kit', 'tenant', 'export', 'governance'];

export function surfacesOf(roles: readonly Role[]): string[] {
  const s = new Set(roles.flatMap((r) => SURFACES[r]));
  return ORDER.filter((x) => s.has(x));
}

export function landingOf(roles: readonly Role[]): string {
  const first = ROLES.find((r) => roles.includes(r));
  return first ? LANDING[first] : 'catalogue';
}

/** The table as rows, for the documentation and the governance view. */
export function permissionRows() {
  return GRANTS.map((g) => ({ role: g.role, scope: g.scope, kinds: [...g.kinds], actions: [...g.actions] }));
}
