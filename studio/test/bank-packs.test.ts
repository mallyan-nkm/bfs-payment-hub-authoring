import { describe, expect, it } from 'vitest';
import { envFor, type StudioEnv } from '../src/env.js';
import { checkAll, checkDocument } from '../src/checks/checks.js';
import { buildPack, compareManifest } from '../src/governance/packs.js';
import * as gov from '../src/governance/governance.js';
import { StoreError } from '../src/core/store.js';
import { ContentStore } from '../src/content/content-store.js';
import { runTest } from '../src/testing/runner.js';
import { freshEnv } from './helpers.js';
import { connection, fake, forkRepo } from './support/content.js';

// A bank pack in its own folder on the content branch, content-configurations/banks/<bank>/ (plan W6; D5
// as amended 2026-11-21; Issue I-43). The studio loads it with its tenant, validates it as it does the
// example bank, and runs its tests.

const BANK = 'dib';
const TENANT = 'dib';
let shared: StudioEnv | undefined;
const env = () => (shared ??= freshEnv({ banks: [BANK] }));
const own = (e: StudioEnv) => e.store.all().filter((x) => x.tenant === TENANT);
const ROOT = 'content-configurations';

describe('a bank pack from content-configurations/banks (I-43)', () => {
  it('is found by its pack.yaml on the branch; a name without one is not a pack', () => {
    const packs = [...env().store.content!.files.keys()].filter((p) => /^banks\/[^/]+\/pack\.yaml$/.test(p)).map((p) => p.split('/')[1]);
    expect(packs).toContain(BANK);
    // The example bank's pack is seeded beside its runtime values (R-11-25), and always loaded.
    expect(packs).toContain('example-bank');
    expect(env().store.bankPacks.get('example-bank')).toBe('example-bank');
    expect(() => freshEnv({ banks: ['no-such-bank'] })).toThrow(/No bank pack no-such-bank/);
  });

  it('loads every document as the tenant of its pack, and leaves the runtime formats out', () => {
    const e = env();
    expect(e.store.tenants()).toContain(TENANT);
    const kinds = own(e).map((x) => x.kind).sort();
    expect(kinds).toEqual(expect.arrayContaining(['pack', 'configuration-set', 'hook', 'activity', 'port', 'simulator', 'test']));
    expect(kinds).not.toContain('port-bindings');
    expect(kinds).not.toContain('configuration-values');
    expect(own(e).every((x) => x.scope === 'b' || x.kind === 'test')).toBe(true);
    // Not loaded unless asked for: the shared studio sees only the example bank.
    expect(freshEnv().store.tenants()).not.toContain(TENANT);
  });

  it('refuses a document that names another tenant, and a pack that is not a bank pack', async () => {
    const full = forkRepo();
    const cs = fake().files(full, 'main')[`${ROOT}/banks/${BANK}/configuration-set.yaml`];
    fake().commitFiles(full, 'main', { [`${ROOT}/banks/${BANK}/configuration-set.yaml`]: cs.replace(`tenant: ${TENANT}`, 'tenant: other-bank') });
    const read = async () => envFor(await ContentStore.open(connection(fake(), full)), { banks: [BANK] });
    await expect(read()).rejects.toThrow(StoreError);
    fake().commitFiles(full, 'main', { [`${ROOT}/banks/${BANK}/pack.yaml`]: 'kind: pack\nscope: uae.wps\nrelease: 1.0.0\npackType: shared\n' });
    await expect(read()).rejects.toThrow(/not a bank pack/);
  });

  it('passes the schemas and the static checks: bounds, hook contracts, bindings, pins', () => {
    const e = env();
    for (const x of own(e)) expect(e.schemas.validate(x.doc), x.key).toEqual([]);
    const names = new Set(own(e).map((x) => x.name));
    expect(checkAll(e).filter((i) => i.severity === 'error')).toEqual([]);
    expect(checkAll(e).filter((i) => names.has(i.key))).toEqual([]);
    const cs = e.store.configurationSet(TENANT);
    expect(gov.checkConfiguration(e, TENANT, cs).filter((i) => i.severity === 'error')).toEqual([]);
    // Each value sets a parameter the factory declares, by its real name.
    for (const v of cs.values) expect(e.store.doc(v.parameter.split('#')[0])?.parameters?.[v.parameter.split('#')[1]], v.parameter).toBeDefined();
  });

  it('catches a value outside its bounds, an unknown hook point and a hook bound to the wrong one', () => {
    const e = env();
    const cs = e.store.configurationSet(TENANT);
    const bad = {
      ...cs,
      values: [...cs.values, { parameter: 'p.rule.amount-within-platform-limit.v1#maxAmount', value: 1e15, validFrom: '2026-11-21' }, { parameter: 'uae.wps.rule.agent-is-this-bank.v1#noSuchParameter', value: 1, validFrom: '2026-11-21' }],
      bindings: [
        { hookPoint: 'p.wf.corporate.v1#no-such-point', hook: 'b.hook.labour-ministry-notify.v1', release: '1.0.0', validFrom: '2026-11-21' },
        { hookPoint: 'p.wf.corporate.v1#on-close', hook: 'b.hook.employer-aml-check.v1', release: '1.0.0', validFrom: '2026-11-21' },
      ],
    };
    const msgs = checkDocument(e, bad, TENANT).map((i) => i.message).join(' | ');
    expect(msgs).toMatch(/noSuchParameter is not a declared parameter/);
    expect(msgs).toMatch(/unknown hook point p.wf.corporate.v1#no-such-point/);
    expect(msgs).toMatch(/b.hook.employer-aml-check.v1 does not implement p.wf.corporate.v1#on-close/);
    // Binding the carried hooks where they belong is accepted.
    const bound = { ...cs, bindings: [
      { hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: 'b.hook.employer-aml-check.v1', release: '1.0.0', validFrom: '2026-11-21' },
      { hookPoint: 'p.wf.corporate.v1#on-close', hook: 'b.hook.labour-ministry-notify.v1', release: '1.0.0', validFrom: '2026-11-21' },
    ] };
    expect(checkDocument(e, bound, TENANT).filter((i) => i.severity === 'error')).toEqual([]);
  });

  it('has a lock file equal to the one the studio generates, and exports without simulators or tests', () => {
    const e = env();
    expect(compareManifest(e, 'b', TENANT)).toEqual({ equal: true, differences: [] });
    // The example bank's lock file is unchanged by a second bank, and the studio and tools/build_factory.py derive the
    // same dependencies: the scopes every artefact reaches, its tests among them (I-43, "Found, not fixed").
    expect(compareManifest(e, 'b')).toEqual({ equal: true, differences: [] });
    expect(e.store.doc('pack:b:dib')?.dependsOn ?? e.store.all().find((x) => x.kind === 'pack' && x.tenant === TENANT)?.doc.dependsOn).toEqual([
      { scope: 'p', release: '1.15.0' }, { scope: 'uae.p', release: '1.15.0' }, { scope: 'uae.wps', release: '1.15.0' },
    ]);
    const pack = buildPack(e, 'b', 'bank', TENANT);
    expect(pack.refused).toBeUndefined();
    expect(pack.manifest.promotable).toBe(true);
    expect(pack.manifest.tenant).toBe(TENANT);
    expect(pack.manifest.artefacts.map((a: any) => a.kind)).not.toContain('simulator');
    expect(pack.manifest.artefacts.every((a: any) => own(e).some((x) => x.name === a.name))).toBe(true);
  });

  it("runs the bank's tests on the interpreter: its routing code decides, from its validFrom", async () => {
    const e = env();
    const tests = own(e).filter((x) => x.kind === 'test');
    expect(tests.length).toBeGreaterThanOrEqual(5);
    for (const t of tests) {
      const r = await runTest(e, t.doc);
      expect(r.passed, `${t.name}: ${r.error ?? JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    }
  }, 120000);
});
