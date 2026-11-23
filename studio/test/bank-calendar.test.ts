import { describe, expect, it } from 'vitest';
import { dispatchAs as dispatch } from './support/as-user.js';
import { parseYaml } from '../src/core/yaml.js';
import { rowsAt } from '../src/core/reference.js';
import { buildAndPublish } from '../src/governance/distributions.js';
import * as gov from '../src/governance/governance.js';
import { classify } from '../src/export/distribution.js';
import { freshEnv } from './helpers.js';
import { fake } from './support/content.js';

// A bank's calendar and cut-offs in the studio (R-11-52): reference tables of the bank's own, edited by the Bank Configurator as a
// new edition, approved by the Bank Approver, and carried to the runtime in the bank's configuration release, never in a
// distribution (R-01-25).

const as = (env: any, user: string, method: string, path: string, body?: any) => dispatch(env, { method, path, query: {}, body: body ?? {}, user });

describe('the bank\'s calendar and cut-offs (R-11-52)', () => {
  it('lists the bank\'s own tables, each with the document its next edition starts from', async () => {
    const env = freshEnv();
    const r = await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/reference-tables');
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    expect(r.body.tables.map((t: any) => t.name)).toEqual(['b.ref.bank-calendar.v1', 'b.ref.branches.v1', 'b.ref.cut-offs.v1', 'b.ref.fx-rates.v1']);
    const cut = r.body.tables.find((t: any) => t.name === 'b.ref.cut-offs.v1');
    expect(cut.released.release).toBe('1.0.0');
    expect(cut.start.release).toBe('1.1.0');
    expect(rowsAt(cut.released, '2026-11-24')).toEqual([{ scheme: 'uae.wps', direction: 'outward', cutOff: '14:00', timeZone: 'Asia/Dubai', afterCutOff: 'next-business-day' }]);
    expect(cut.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    expect((await as(env, 'example.maker', 'GET', '/api/tenants/second-bank/reference-tables')).status).toBe(403);
  });

  it('a bank\'s own table is values: approving it builds a configuration release and no distribution', () => {
    expect(classify([{ kind: 'reference-table', name: 'b.ref.cut-offs.v1' }], 'example-bank')).toEqual({ values: true, bankPack: false, shared: [] });
    expect(classify([{ kind: 'reference-table', name: 'b.ref.cut-offs.v1' }, { kind: 'hook', name: 'b.hook.x.v1' }], 'example-bank')).toMatchObject({ values: true, bankPack: true });
  });

  it('the configurator adds an edition, the approver approves it, and the configuration release carries it', async () => {
    const env = freshEnv();
    env.store.settings = { ...env.store.settings, distributions: true };
    await buildAndPublish(env, 'example.checker', 'example-bank', { kind: 'distribution', bank: 'example-bank', engine: 'temporal' });
    const start = (await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/reference-tables')).body.tables.find((t: any) => t.name === 'b.ref.cut-offs.v1').start;
    const doc = { ...start, editions: [...start.editions, { validFrom: '2027-01-01', rows: [
      { scheme: 'uae.wps', direction: 'outward', cutOff: '13:30', timeZone: 'Asia/Dubai', afterCutOff: 'next-business-day' },
      { scheme: 'uae.wps', direction: 'inward', cutOff: '16:00', timeZone: 'Asia/Dubai', afterCutOff: 'refuse' },
    ] }] };
    // A row that does not fit is refused before saving (L49).
    const bad = await as(env, 'example.maker', 'POST', '/api/tenants/example-bank/reference-tables/check', { doc: { ...doc, editions: [...start.editions, { validFrom: '2027-01-01', rows: [{ scheme: 'uae.wps', direction: 'outward', cutOff: '25:00', timeZone: 'Asia/Dubai', afterCutOff: 'later' }] }] } });
    expect(bad.body.issues.filter((i: any) => i.code === 'L49').map((i: any) => i.message).join('\n')).toMatch(/cutOff "25:00" does not match[\s\S]*afterCutOff "later" is not one of/);
    expect((await as(env, 'example.maker', 'POST', '/api/tenants/example-bank/reference-tables/check', { doc: { ...doc, name: 'uae.p.ref.business-calendar.v1' } })).body.issues[0].code).toBe('R-11-52');
    // The table of permissions: a platform designer may not author it, the configurator saves it, and may not approve it.
    expect((await as(env, 'workflow.designer', 'POST', '/api/drafts', { doc })).status).toBe(403);
    const saved = await as(env, 'example.maker', 'POST', '/api/drafts', { doc });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    expect(saved.body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const key = saved.body.draft.key;
    expect((await as(env, 'example.maker', 'POST', '/api/publish', { key })).status).toBe(403);
    const r: any = await gov.approveChangeSet(env, 'example-bank', 'example.checker', [key]);
    await r.write;
    // The approval builds a configuration release, no distribution, and the release carries the new edition.
    expect(r.bundles.map((b: any) => b.kind)).toEqual(['configuration']);
    const cfg = r.bundles[0];
    const files = fake().files(env.store.content!.repository, 'main');
    const shipped = parseYaml(files[`content-configurations/${cfg.folder}/reference/b.ref.cut-offs.v1.yaml`]);
    expect(shipped.release).toBe('1.1.0');
    expect(shipped.editions.map((e: any) => e.validFrom)).toEqual(['2026-10-01', '2027-01-01']);
    expect(rowsAt(shipped, '2027-02-01')).toHaveLength(2);
    expect(rowsAt(shipped, '2026-12-01')[0].cutOff).toBe('14:00');
    expect(files[`content-configurations/${cfg.folder}/reference/b.ref.bank-calendar.v1.yaml`]).toBeDefined();
    // The editor starts the next edition from what was released.
    const next = (await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/reference-tables')).body.tables.find((t: any) => t.name === 'b.ref.cut-offs.v1');
    expect([next.released.release, next.start.release]).toEqual(['1.1.0', '1.2.0']);
  });
});
