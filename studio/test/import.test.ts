import { describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { analyse, apply, readBundle, type BundleFile } from '../src/import/bundle.js';
import { wpsTrainingBundle } from '../src/import/sample.js';
import { dispatchAs as dispatch } from './support/as-user.js';
import { freshEnv } from './helpers.js';

// Import of an extraction bundle (I-33): the training bundle (WPS, extracted as uae.wpsx) imports
// clean, saves as drafts only, and publishes and runs like any authored content; every way an
// extraction can be incomplete or unsafe is caught.

const OPTS = { tenant: 'owner', bankTenant: 'example-bank' };
const errors = (r: any) => [...r.issues, ...r.items.flatMap((i: any) => i.issues), ...r.scripts.flatMap((s: any) => s.issues), ...r.samples.flatMap((s: any) => s.issues)].filter((i: any) => i.severity === 'error');
const codes = (r: any) => errors(r).map((e: any) => e.code);
/** The training bundle with one file changed. */
function edited(env: any, file: string, change: (content: string) => string | undefined): BundleFile[] {
  return wpsTrainingBundle(env).flatMap((f) => {
    if (f.name !== file) return [f];
    const c = change(f.content);
    return c === undefined ? [] : [{ ...f, content: c }];
  });
}
const yamlEdit = (fn: (d: any) => void) => (c: string) => { const d = parseYaml(c); fn(d); return toYaml(d); };

describe('import: the training bundle', () => {
  it('previews clean: every source file accounted for, every test green against the bundle itself', async () => {
    const env = freshEnv();
    const r = await analyse(env, readBundle(wpsTrainingBundle(env)), OPTS);
    expect(errors(r)).toEqual([]);
    expect(r.ok).toBe(true);
    // Plan P2 added the PSI and RRI maps, the RTC map's script, two samples and two tests (WPS-IN-008, WPS-RTC-001); the operations view and the error dictionary one more each.
    // The labour ministry lookup, its port, simulator and rule, its script, two samples and eight tests (Issues I-88, I-100, I-109).
    // The participant directory, a sample and two tests (WPS-MAP-005, WPS-IN-009; Issues I-115, I-117).
    expect(r.summary).toMatchObject({ documents: 69, activities: 30, rules: 12, workflows: 11, schemes: 1, configurationSets: 1, tests: 28, scripts: 4, samples: 12, new: 98, testsPassed: 28, testsFailed: 0 });
    expect(r.inventory.sourceFiles).toMatchObject({ inventoried: 114, excluded: 1, missing: [] });
    // Nothing was saved by a preview.
    expect(env.store.drafts('owner')).toEqual([]);
  });

  it('applies as drafts only, then publishes through maker-checker and runs like authored content', async () => {
    const env = freshEnv();
    const res = await apply(env, readBundle(wpsTrainingBundle(env)), { ...OPTS, actor: 'shared.designer', bankActor: 'example.maker' });
    expect(res.applied).toBe(true);
    expect(res.saved.length).toBeGreaterThan(70);
    expect(env.store.all().filter((e) => e.name.startsWith('uae.wpsx.') && e.status === 'published')).toEqual([]);
    expect(env.store.drafts('example-bank').map((d) => d.kind)).toContain('configuration-set');
    expect(env.scripts.find('uae.wpsx.map.sif-in.v1')?.source).toMatch(/wpsx/);
    // Provenance records where it came from.
    expect(env.store.drafts('owner').find((d) => d.name === 'uae.wpsx.wf.salary-file-out.v1')!.doc.provenance.note).toMatch(/Imported from WPS salary files.*not yet validated by an expert/);
    // A checker approves each change set; the imported tests then pass on the published content.
    const call = (method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body });
    for (const set of res.changeSets) {
      const d = env.store.drafts('owner').find((x) => x.key === set[0]) ?? env.store.drafts('example-bank').find((x) => x.key === set[0]);
      if (!d) continue;
      const pub = await call('POST', '/api/publish', { tenant: d.tenant, key: d.key, checker: d.tenant === 'owner' ? 'shared.checker' : 'example.checker' });
      expect(pub.status, JSON.stringify(pub.body).slice(0, 400)).toBe(200);
    }
    const run = await call('POST', '/api/tests/WPSX-OUT-001/run');
    expect(run.body.passed, JSON.stringify(run.body.checks?.filter((c: any) => !c.passed))).toBe(true);
    expect(run.body.trace.at(-1)).toBe('end:settled');
  });

  it('a second import of the same, once published, is unchanged and saves nothing new', async () => {
    const env = freshEnv();
    const first = await apply(env, readBundle(wpsTrainingBundle(env)), { ...OPTS, actor: 'shared.designer', bankActor: 'example.maker' });
    for (const set of first.changeSets) {
      const d = [...env.store.drafts('owner'), ...env.store.drafts('example-bank')].find((x) => x.key === set[0]);
      if (d) await dispatch(env, { method: 'POST', path: '/api/publish', query: {}, body: { tenant: d.tenant, key: d.key, checker: d.tenant === 'owner' ? 'shared.checker' : 'example.checker' } });
    }
    const r = await analyse(env, readBundle(wpsTrainingBundle(env)), OPTS);
    expect(r.items.filter((i) => i.kind === 'activity').every((i) => i.status === 'unchanged')).toBe(true);
  });
});

describe('import: what it refuses', () => {
  it('a source file neither inventoried nor excluded: not comprehensive', async () => {
    const env = freshEnv();
    const files = edited(env, 'inventory.yaml', yamlEdit((d) => { d.items = d.items.filter((i: any) => i.source.path !== 'factory/uae.wps/rules/uae.wps.rule.currency-aed.v1.yaml'); }));
    const r = await analyse(env, readBundle(files), OPTS);
    expect(errors(r).find((e: any) => e.code === 'I64')?.where).toBe('factory/uae.wps/rules/uae.wps.rule.currency-aed.v1.yaml');
    // And the document no source explains.
    expect(errors(r).some((e: any) => e.code === 'I40' && e.where === 'uae.wpsx.rule.currency-aed.v1')).toBe(true);
  });

  it('an unmapped item must say why, and is listed for the expert', async () => {
    const env = freshEnv();
    const add = (note?: string) => edited(env, 'inventory.yaml', yamlEdit((d) => {
      d.items.push({ id: 'SRC-9001', source: { path: 'factory/uae.wps/scheme.yaml', kind: 'message', lines: '40-52' }, summary: 'The refund order', target: { kind: 'unmapped', ...(note ? {} : {}) }, ...(note ? { note } : {}) });
    }));
    expect(codes(await analyse(env, readBundle(add()), OPTS))).toContain('I62');
    const r = await analyse(env, readBundle(add('Its layout is not in the public guide')), OPTS);
    expect(r.ok).toBe(true);
    expect(r.inventory.unmapped.map((u: any) => u.id)).toEqual(['SRC-9001']);
  });

  it('an item that maps to nothing that exists', async () => {
    const env = freshEnv();
    const files = edited(env, 'inventory.yaml', yamlEdit((d) => { d.items[0].target.name = 'uae.wpsx.validate.nowhere.v1'; }));
    expect(codes(await analyse(env, readBundle(files), OPTS))).toContain('I61');
  });

  it('personal data, a bank name in shared content, and a script outside the injected interface', async () => {
    const env = freshEnv();
    const sample = 'samples/wpsx-sif-valid.sif';
    const pd = edited(env, sample, (c) => `${c}\n# contact: someone@example.com\n`);
    expect(errors(await analyse(env, readBundle(pd), OPTS)).some((e: any) => e.code === 'I30' && e.where === 'wpsx-sif-valid.sif')).toBe(true);
    const bank = edited(env, 'artefacts/rules/uae.wpsx.rule.currency-aed.v1.yaml', yamlEdit((d) => { d.title = 'Currency check as example-bank does it'; }));
    expect(codes(await analyse(env, readBundle(bank), OPTS))).toContain('L09');
    const script = edited(env, 'scripts/uae.wpsx.map.sif-in.v1.ts', (c) => c.replace('export async function run', 'const t = Date.now();\nexport async function run'));
    expect(codes(await analyse(env, readBundle(script), OPTS))).toContain('L28');
  });

  it('a release of a published line with different content, a scope outside the bundle, no manifest', async () => {
    const env = freshEnv();
    // The bundle carries a changed p.* block at the release already published.
    const files = [...wpsTrainingBundle(env)];
    const iban = toYaml({ ...env.store.doc('uae.p.validate.iban.v1'), title: 'Changed in the source' });
    files.push({ name: 'artefacts/activities/uae.p.validate.iban.v1.yaml', content: iban });
    const inv = files.find((f) => f.name === 'inventory.yaml')!;
    inv.content = yamlEdit((d) => d.items.push({ id: 'SRC-9002', source: { path: 'factory/uae.wps/scheme.yaml', kind: 'activity' }, target: { kind: 'artefact', name: 'uae.p.validate.iban.v1' }, reuse: 'with-change' }))(inv.content);
    const r = await analyse(env, readBundle(files), OPTS);
    const item = r.items.find((i) => i.name === 'uae.p.validate.iban.v1')!;
    expect(item.status).toBe('changed');
    expect(item.issues.find((i) => i.code === 'R-01-10')?.message).toMatch(/Give this one release 1\.\d+\.\d+/);
    expect(r.inventory.reuse['with-change']).toBe(1);

    // a rule of a scheme outside the bundle: a synthetic copy under another scope
    const other = [...wpsTrainingBundle(env), { name: 'artefacts/rules/x.yaml', content: toYaml({ ...env.store.doc('p.rule.amount-within-platform-limit.v1'), name: 'uae.other.rule.amount-within-limit.v1' }) }];
    expect(codes(await analyse(env, readBundle(other), OPTS))).toContain('I22');

    expect(codes(await analyse(env, readBundle(wpsTrainingBundle(env).filter((f) => f.name !== 'extraction.yaml')), OPTS))).toContain('I10');
  });

  it('bank content needs a bank tenant to go to', async () => {
    const env = freshEnv();
    expect(codes(await analyse(env, readBundle(wpsTrainingBundle(env)), { tenant: 'owner' }))).toContain('I14');
  });

  it('the API previews a zip, applies it, and serves the training bundle and the prompt', async () => {
    const env = freshEnv();
    const call = (method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body });
    const zip = await call('GET', '/api/import/sample');
    expect(zip.status).toBe(200);
    const bundle = Buffer.from(zip.body).toString('base64');
    // A platform user imports the shared part and may not name a bank; the bank part is left for the bank (R-11-30).
    expect((await call('POST', '/api/import/preview', { bundle, bankTenant: 'example-bank' })).status).toBe(403);
    const pv = await call('POST', '/api/import/preview', { bundle });
    expect(pv.body.ok, JSON.stringify(pv.body.issues)).toBe(true);
    expect(pv.body.issues.some((i: any) => i.code === 'I15')).toBe(true);
    const ap = await call('POST', '/api/import/apply', { bundle, actor: 'shared.designer' });
    expect(ap.body.applied).toBe(true);
    expect(env.store.drafts('example-bank')).toEqual([]);
    expect(ap.body.skipped.some((x: string) => /bank part/.test(x))).toBe(true);
    for (const set of ap.body.changeSets) {
      const r = await call('POST', '/api/publish', { key: set[0], checker: 'shared.checker' });
      expect(r.status, JSON.stringify(r.body).slice(0, 300)).toBe(200);
    }
    // The bank configurator imports the bank part, into its own tenant only.
    const bank = (method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body, user: 'example.maker' });
    expect((await bank('POST', '/api/import/apply', { bundle, bankTenant: 'second-bank' })).status).toBe(403);
    const bp = await bank('POST', '/api/import/apply', { bundle });
    expect(bp.body.applied, JSON.stringify(bp.body.report?.issues ?? bp.body).slice(0, 600)).toBe(true);
    expect(env.store.drafts('example-bank').map((d) => d.kind)).toEqual(['configuration-set']);
    // An auditor may not import: refused whole, before anything is saved.
    const env2 = freshEnv();
    const refused = await dispatch(env2, { method: 'POST', path: '/api/import/apply', query: {}, body: { bundle, actor: 'shared.auditor' } });
    expect(refused.status).toBe(403);
    expect(refused.body.error).toMatch(/auditor/i);
    expect(env2.store.drafts('owner')).toEqual([]);
    const prompt = await call('GET', '/api/import/prompt');
    expect(prompt.status).toBe(200);
    expect(Buffer.from(prompt.body).toString('utf8')).toMatch(/extraction\.yaml/);
  });
});
