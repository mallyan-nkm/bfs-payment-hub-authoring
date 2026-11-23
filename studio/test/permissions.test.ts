import { describe, expect, it } from 'vitest';
import { ACTIONS, KINDS, LEGACY_ROLES, ROLES, effectiveRoles, granted, landingOf, surfacesOf, type Action, type Kind, type Role, type ScopeClass } from '../src/governance/permissions.js';
import * as gov from '../src/governance/governance.js';
import { freshEnv } from './helpers.js';

// The permission table (R-11-30), cell by cell: every role, every kind, every action, in every scope
// class. The expected table is written out here, from the owner's decision of 2026-11-22, and not
// read from the code: a cell the code grants that is not here, or misses, fails.

// A reference table (R-01-25, owner's decision of 2026-11-23): the bank's own (its branches) by the Bank Configurator, the shared ones
// (participant directory, emirates, free zones, purpose codes, calendars) by the platform vendor's developers.
// A bank's lock file (`pack`, scope b, owner's decision of 2026-11-23, Issue I-205): the Bank Configurator proposes a licence, the
// Bank Approver approves it (R-11-75, R-11-76).
const BANK: Kind[] = ['hook', 'activity', 'script', 'rule', 'ruleset', 'table', 'configuration-set', 'port-bindings', 'reference-table', 'simulator', 'test', 'sample', 'pack'];
const DESIGN: Kind[] = ['workflow', 'scheme', 'operations-view', 'test', 'sample'];
const DEVELOP: Kind[] = ['activity', 'script', 'engine-binding', 'port', 'rule', 'ruleset', 'table', 'simulator', 'extension', 'error-dictionary', 'reference-table', 'test', 'sample'];
const SHARED: Kind[] = [...new Set([...DESIGN, ...DEVELOP])];

type Cell = `${ScopeClass}|${Kind}|${Action}`;
const cells = (scope: ScopeClass, kinds: Kind[], actions: Action[]): Cell[] => kinds.flatMap((k) => actions.map((a) => `${scope}|${k}|${a}` as Cell));

const EXPECTED: Record<Role, Cell[]> = {
  'bank configurator': [...cells('bank', BANK, ['author']), ...cells('bank', ['bench', 'import', 'export', 'deploy', 'assistant'], ['run'])],
  'bank approver': [...cells('bank', BANK, ['approve']), ...cells('bank', ['configuration-set'], ['accept']), ...cells('bank', ['bench', 'export', 'deploy'], ['run']), ...cells('bank', ['distribution', 'configuration-release'], ['approve'])],
  'workflow designer': [...cells('shared', [...DESIGN, 'supersession'], ['author']), ...cells('shared', ['bench', 'import', 'export', 'deploy', 'assistant'], ['run'])],
  'workflow developer': [...cells('shared', [...DEVELOP, 'supersession'], ['author']), ...cells('shared', ['bench', 'import', 'export', 'deploy', 'assistant'], ['run'])],
  'platform approver': [
    ...cells('shared', [...SHARED, 'supersession'], ['approve']), ...cells('shared', ['supersession'], ['author']),
    ...cells('studio', ['waiver'], ['approve']), ...cells('studio', ['release', 'in-flight'], ['retire']), ...cells('studio', ['settings'], ['author']),
    ...cells('shared', ['bench', 'export', 'deploy'], ['run']),
    // The distribution of every bank that takes a platform or scheme change (R-11-40).
    ...cells('shared', ['distribution'], ['approve']),
  ],
  administrator: cells('studio', ['users'], ['author', 'approve']),
  auditor: [],
  'service operations': [...cells('studio', ['release', 'in-flight'], ['retire']), ...cells('studio', ['settings'], ['author'])],
};

const SCOPES: ScopeClass[] = ['bank', 'shared', 'studio'];
const ACTS = ACTIONS.filter((a) => a !== 'read');

describe('the permission table (R-11-30)', () => {
  for (const role of ROLES) {
    it(`grants ${role} exactly its cells, and refuses every other`, () => {
      const want = new Set(EXPECTED[role]);
      const got: Cell[] = [];
      for (const scope of SCOPES) for (const kind of KINDS) for (const action of ACTS) if (granted([role], scope, kind, action).length) got.push(`${scope}|${kind}|${action}`);
      expect(got.sort()).toEqual([...want].sort());
    });
  }

  it('keeps the owner\'s forbidden list for a Bank Configurator', () => {
    for (const kind of ['workflow', 'scheme', 'port', 'extension', 'engine-binding'] as Kind[]) for (const scope of SCOPES) expect(granted(['bank configurator'], scope, kind, 'author')).toEqual([]);
    for (const kind of KINDS) expect(granted(['bank configurator'], 'shared', kind, 'author')).toEqual([]);
    for (const kind of KINDS) expect(granted(['bank configurator'], 'bank', kind, 'approve')).toEqual([]);
  });

  it('lets no maker approve, and no approver make, except a supersession, which a platform approver may draft', () => {
    for (const maker of ['bank configurator', 'workflow designer', 'workflow developer'] as Role[]) for (const s of SCOPES) for (const k of KINDS) expect(granted([maker], s, k, 'approve')).toEqual([]);
    for (const s of SCOPES) for (const k of KINDS.filter((x) => !['supersession', 'settings'].includes(x))) {
      expect(granted(['bank approver'], s, k, 'author')).toEqual([]);
      expect(granted(['platform approver'], s, k, 'author')).toEqual([]);
    }
  });

  it('reads the roles of earlier releases as today\'s, and an unknown role as nothing', () => {
    expect(effectiveRoles(['shared designer'])).toEqual(['workflow designer', 'workflow developer']);
    expect(effectiveRoles(['bank designer', 'bank checker'])).toEqual(['bank configurator', 'bank approver']);
    expect(effectiveRoles(['shared checker'])).toEqual(['platform approver', 'administrator']);
    expect(effectiveRoles(['superuser', 'admin'])).toEqual([]);
    expect(Object.keys(LEGACY_ROLES).length).toBe(4);
  });

  it('shows each role its surfaces and lands it on its own (R-10-11)', () => {
    expect(landingOf(['bank configurator'])).toBe('bank');
    expect(landingOf(['workflow designer'])).toBe('designer');
    expect(landingOf(['workflow developer'])).toBe('editors');
    expect(landingOf(['platform approver'])).toBe('governance');
    expect(landingOf([])).toBe('catalogue');
    expect(surfacesOf(['bank configurator'])).not.toContain('kit');
    expect(surfacesOf(['bank configurator'])).toContain('tenant');
    expect(surfacesOf(['workflow designer'])).not.toContain('bank');
    expect(surfacesOf(['administrator'])).toEqual(['catalogue', 'governance']);
  });
});

describe('permit, with the users and tenants (R-11-30, R-06-01, R-06-03)', () => {
  const env = freshEnv();
  const p = (actor: string, tenant: string, action: Action, kind: Kind, scope?: string) => gov.permit(env, actor, tenant, action, { kind, scope });

  it('refuses an unknown user, and a user acting for another tenant', () => {
    expect(p('nobody', 'owner', 'author', 'workflow', 'p')).toMatch(/not a user/);
    expect(p('example.maker', 'second-bank', 'author', 'hook', 'b')).toMatch(/R-06-01/);
    expect(p('shared.designer', 'example-bank', 'author', 'hook', 'b')).toMatch(/R-06-01/);
  });

  it('keeps a bank in scope b and the owner out of it', () => {
    expect(p('example.maker', 'example-bank', 'author', 'hook', 'b')).toBeUndefined();
    expect(p('example.maker', 'example-bank', 'author', 'rule', 'uae.p')).toMatch(/R-06-03/);
    expect(p('example.maker', 'example-bank', 'author', 'activity', 'p')).toMatch(/R-06-03/);
    expect(p('shared.designer', 'owner', 'author', 'activity', 'b')).toMatch(/R-06-03/);
  });

  it('answers each role of the default users as the table says', () => {
    expect(p('example.maker', 'example-bank', 'author', 'workflow', 'b')).toMatch(/may not author a workflow/);
    expect(p('example.maker', 'example-bank', 'author', 'configuration-set', 'b')).toBeUndefined();
    expect(p('example.maker', 'example-bank', 'run', 'bench', 'b')).toBeUndefined();
    expect(p('example.checker', 'example-bank', 'approve', 'configuration-set', 'b')).toBeUndefined();
    expect(p('example.checker', 'example-bank', 'author', 'configuration-set', 'b')).toMatch(/may not/);
    expect(p('workflow.designer', 'owner', 'author', 'workflow', 'uae.wps')).toBeUndefined();
    expect(p('workflow.designer', 'owner', 'author', 'activity', 'uae.wps')).toMatch(/may not/);
    expect(p('workflow.developer', 'owner', 'author', 'port', 'uae.wps')).toBeUndefined();
    expect(p('workflow.developer', 'owner', 'author', 'scheme', 'uae.wps')).toMatch(/may not/);
    expect(p('shared.checker', 'owner', 'approve', 'workflow', 'p')).toBeUndefined();
    expect(p('shared.checker', 'owner', 'author', 'users')).toMatch(/may not/);
    expect(p('users.admin', 'owner', 'author', 'users')).toBeUndefined();
    expect(p('shared.auditor', 'owner', 'run', 'export', 'p')).toMatch(/may not/);
    expect(p('example.auditor', 'example-bank', 'read', 'hook', 'b')).toBeUndefined();
  });

  it('does not let a bank role work in the owner tenant, or a platform role in a bank', () => {
    const e = freshEnv();
    e.store.extra.users = [{ id: 'mixed.user', tenant: 'owner', roles: ['bank configurator'] }, { id: 'mixed.bank', tenant: 'example-bank', roles: ['workflow designer'] }];
    expect(gov.permit(e, 'mixed.user', 'owner', 'author', { kind: 'rule', scope: 'p' })).toMatch(/may not/);
    expect(gov.permit(e, 'mixed.bank', 'example-bank', 'author', { kind: 'workflow', scope: 'b' })).toMatch(/may not/);
  });

  it('refuses a release approved by a maker of the change set, even one with an approver role (R-06-04)', async () => {
    const e = freshEnv();
    e.store.extra.users = [...gov.DEFAULT_USERS, { id: 'both.hands', tenant: 'owner', roles: ['workflow developer', 'platform approver'] }];
    const doc = { ...e.store.doc('uae.p.rule.iban-length.v1'), release: '1.0.9' };
    const d = e.store.saveDraft('owner', 'both.hands', doc);
    await expect(gov.approveChangeSet(e, 'owner', 'both.hands', [d.key])).rejects.toThrow(/Maker and checker must differ/);
  });
});

describe('the accounts (R-11-34)', () => {
  it('ties each account to one identity and one tenant, and a bank role to a bank tenant', () => {
    const env = freshEnv();
    const base = gov.DEFAULT_USERS;
    const propose = (list: any[]) => () => gov.proposeUsers(env, 'users.admin', list);
    expect(propose([...base, { id: 'dup.one', tenant: 'example-bank', roles: ['bank configurator'], subject: 'example.maker' }])).toThrow(/two accounts/);
    expect(propose([...base, { id: 'example.maker', tenant: 'second-bank', roles: ['bank configurator'] }])).toThrow(/twice/);
    expect(propose([...base, { id: 'owner.bank', tenant: 'owner', roles: ['bank approver'] }])).toThrow(/a bank role only in a bank tenant/);
    expect(propose([...base, { id: 'bank.platform', tenant: 'example-bank', roles: ['platform approver'] }])).toThrow(/a platform role only in the owner tenant/);
    expect(propose([...base, { id: 'third.configurator', tenant: 'third-bank', roles: ['bank configurator'], subject: 'idp-subject-0001' }])()).toMatchObject({ changeSet: expect.any(String) });
  });
});
