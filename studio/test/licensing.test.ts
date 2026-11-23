import { beforeAll, describe, expect, it } from 'vitest';
import type { Req } from '../src/server/api.js';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { lockFileOf, schemesBound } from '../src/governance/licensing.js';
import { licensedSchemes } from '../src/export/distribution.js';
import type { StudioEnv } from '../src/env.js';
import { dispatchAs as dispatch } from './support/as-user.js';
import { clone, freshEnv, restart } from './helpers.js';
import { fake } from './support/content.js';

// Licensing a scheme to a bank from the studio (R-11-75 to R-11-78, Issue I-205), through the API in process. A Bank
// Configurator proposes the bank's next lock file with a further scheme; a Bank Approver who is not its maker approves it;
// the approval publishes the lock file at a path of its own on the content branch and the bank's next distribution carries the
// scheme; at every approval of the bank's change set, each scheme its configuration binds must be licensed.

const BANK = 'example-bank';
const S = 'uae.licdemo';
const SCHEME = {
  kind: 'scheme', scope: S, release: '1.0.0', title: 'Licence demonstration scheme (synthetic)', wire: 'file',
  messages: [{ id: `${S}.note`, title: 'A synthetic note', direction: 'in', confirmed: true, disposition: { deliver: { correlateBy: 'file id' } } }],
  legs: [], ports: [],
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Synthetic scheme for the licence test; no rulebook'] },
};

let env: StudioEnv;
const call = (method: string, path: string, body?: any, user?: string) => {
  const [p, q = ''] = path.split('?');
  const req: Omit<Req, 'user'> & { user?: string } = { method, path: p, query: Object.fromEntries(new URLSearchParams(q)), body: body ?? {}, ...(user ? { user } : {}) };
  return dispatch(env, req);
};
const licensing = (user = 'example.maker') => call('GET', `/api/tenants/${BANK}/licensing`, undefined, user);
const propose = (scheme: string, user = 'example.maker') => call('POST', `/api/tenants/${BANK}/licensing`, { scheme }, user);
const publish = (key: string, checker: string, tenant = BANK) => call('POST', '/api/publish', { tenant, key, checker }, checker);
const nextMinor = (r: string) => r.replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);

beforeAll(async () => {
  env = freshEnv();
  env.store.settings = { ...env.store.settings, distributions: true };
  // A further scheme of the factory, as the owner would publish it (the new-scheme black-box test builds and publishes a whole
  // one through the API, then licenses it: test/blackbox/e2e-new-scheme.test.ts). Here its manifest is enough.
  env.store.add(SCHEME, `artefacts/${S}/scheme/1.0.0.yaml`, 'studio', false, 'published', 'owner');
}, 240000);

describe('the schemes a bank licenses (R-11-75)', () => {
  it('shows the lock file in force and every scheme of the factory, licensed or not', async () => {
    const r = await licensing();
    expect(r.status).toBe(200);
    expect(r.body.licensedSchemes).toEqual(['uae.wps']);
    expect(r.body.schemes.find((s: any) => s.scope === 'uae.wps')).toMatchObject({ licensed: true, bound: true });
    expect(r.body.schemes.find((s: any) => s.scope === S)).toMatchObject({ licensed: false, bound: false });
    expect(r.body.draft).toBeNull();
  });

  it('reads the schemes a configuration binds: accepted, enabled as a rail, dialled or hooked; never a platform scope', () => {
    expect(schemesBound(env, { accepts: [{ scope: 'p' }, { scope: 'uae.p' }, { scope: S }] })).toEqual([S]);
    expect(schemesBound(env, { values: [{ parameter: 'p.table.rail-choice.v1#enabledRails', value: [S, 'uae.none'] }] })).toEqual([S]);
    expect(schemesBound(env, { values: [{ parameter: 'uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes', value: [] }] })).toEqual(['uae.wps']);
    expect(schemesBound(env, { bindings: [{ hookPoint: 'uae.wps.wf.salary-file-out.v1#pre-release' }] })).toEqual(['uae.wps']);
  });

  it('refuses a configuration that binds a scheme the bank does not license, at approval (R-11-77)', async () => {
    const set = clone(env.store.configurationSet(BANK));
    set.release = nextMinor(set.release);
    set.accepts.push({ scope: S, release: '1.0.0' });
    const saved = await call('POST', `/api/tenants/${BANK}/configuration`, { yaml: toYaml(set) }, 'example.maker');
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    const r = await publish(saved.body.draft.key, 'example.checker');
    expect(r.status).toBe(409);
    expect(r.body.error).toMatch(new RegExp(`R-11-77 the configuration of ${BANK} binds ${S.replace('.', '\\.')}, which the bank pack does not license`));
    expect((await call('DELETE', `/api/drafts/${encodeURIComponent(saved.body.draft.key)}`, undefined, 'example.maker')).status).toBe(200);
  }, 240000);

  it('lets only the bank configurator propose, and only a scheme of the factory it does not license yet', async () => {
    expect((await propose(S, 'example.checker')).status).toBe(403);
    expect((await call('POST', `/api/tenants/${BANK}/licensing`, { scheme: S }, 'shared.designer')).status).toBe(403);
    expect((await propose('uae.none')).body.error).toMatch(/no scheme of the factory/);
    expect((await propose('uae.wps')).body.error).toMatch(/already licenses uae\.wps/);
  });

  let key = '';
  let release = '';
  it('proposes the next lock file as a draft: the next minor, the scheme added, the artefacts as they are', async () => {
    const before = lockFileOf(env, BANK)!;
    const r = await propose(S);
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    key = r.body.draft.key;
    release = r.body.lock.release;
    expect(release).toBe(nextMinor(before.release));
    expect(r.body.lock.licensedSchemes).toEqual([S, 'uae.wps']);
    expect(r.body.lock.artefacts).toEqual(before.doc.artefacts);
    expect(r.body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const l = (await licensing()).body;
    expect(l.draft).toMatchObject({ key, release, licensedSchemes: [S, 'uae.wps'] });
    // Not in force until approved: a distribution still carries the schemes of the lock file in force.
    expect(licensedSchemes(env, BANK)).toEqual(['uae.wps']);
    expect((await propose(S)).body.error).toMatch(/already proposes/);
  });

  it('refuses the maker as checker (R-06-04)', async () => {
    const r = await publish(key, 'example.maker');
    expect(r.status).toBe(403);
  });

  it('refuses a lock file that is not the one the studio writes for the bank now (R-11-76)', async () => {
    const forged = clone(lockFileOf(env, BANK)!.doc);
    forged.release = '99.0.0';
    forged.licensedSchemes = [S, 'uae.wps'];
    forged.artefacts = forged.artefacts.slice(1);
    // The hand-written lock file replaces the proposal's draft; the approval refuses it and publishes nothing.
    const d = await call('POST', '/api/drafts', { doc: forged }, 'example.maker');
    expect(d.status, JSON.stringify(d.body)).toBe(200);
    const r = await publish(d.body.draft.key, 'example.checker');
    expect(r.status).toBe(409);
    expect(r.body.error).toMatch(/R-11-76 The lock file is not the one the studio writes/);
    expect((await call('DELETE', `/api/drafts/${encodeURIComponent(d.body.draft.key)}`, undefined, 'example.maker')).status).toBe(200);
    expect(licensedSchemes(env, BANK)).toEqual(['uae.wps']);
  }, 240000);

  it("publishes on the bank approver's approval, insert-only, and the bank's next distribution carries the scheme (R-11-78)", async () => {
    expect(env.store.entries.has(key)).toBe(true);
    const r = await publish(key, 'example.checker');
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    expect(r.body.published.map((p: any) => p.key)).toEqual([`${BANK}/pack:b:${BANK}@${release}`]);
    const files = fake().files(env.store.content!.repository, 'main');
    const at = `content-configurations/banks/${BANK}/artefacts/b/pack/${release}.yaml`;
    expect(parseYaml(files[at]).licensedSchemes).toEqual([S, 'uae.wps']);
    // The lock file the seed put in place stays as it was: the new release is a file of its own.
    expect(parseYaml(files[`content-configurations/banks/${BANK}/pack.yaml`].replace(/^#.*\n/, '')).licensedSchemes).toEqual(['uae.wps']);
    expect(licensedSchemes(env, BANK)).toEqual([S, 'uae.wps']);
    const dist = r.body.bundles.find((b: any) => b.kind === 'distribution');
    expect(dist, JSON.stringify(r.body.bundles)).toBeDefined();
    const m = parseYaml(files[`content-configurations/${dist.folder}/manifest.yaml`]);
    expect(m.billOfMaterials.packs.map((p: any) => p.scope)).toContain(S);
    expect(m.billOfMaterials.packs.find((p: any) => p.scope === 'b').release).toBe(release);
    expect((await licensing()).body).toMatchObject({ release, licensedSchemes: [S, 'uae.wps'], draft: null });
  }, 240000);

  it('keeps the licence when the studio starts again, read from the branch', async () => {
    const again = await restart(env);
    // The scheme manifest of this test is in memory only; the lock file is read from the branch.
    expect(lockFileOf(again, BANK)).toMatchObject({ release, doc: { licensedSchemes: [S, 'uae.wps'] } });
  }, 120000);

  it('then approves a configuration that accepts the licensed scheme', async () => {
    const set = clone(env.store.configurationSet(BANK));
    set.release = nextMinor(set.release);
    set.accepts.push({ scope: S, release: '1.0.0' });
    const saved = await call('POST', `/api/tenants/${BANK}/configuration`, { yaml: toYaml(set) }, 'example.maker');
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    const r = await publish(saved.body.draft.key, 'example.checker');
    expect(r.status, JSON.stringify(r.body)).toBe(200);
  }, 240000);
});
