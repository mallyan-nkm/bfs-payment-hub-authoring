import { createHash } from 'node:crypto';
import { beforeAll, describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import * as gov from '../src/governance/governance.js';
import {
  buildConfigurationRelease, buildDistribution, checkSlotContracts, isoSeconds, licensedSchemes, longestWait, parameterContract, slotContracts,
} from '../src/export/distribution.js';
import { parseTrustedSigners, verifyFiles, SignatureRejected } from '../src/export/signing.js';
import { buildAndPublish, nextId } from '../src/governance/distributions.js';
import { granted } from '../src/governance/permissions.js';
import { clone, freshEnv } from './helpers.js';
import { fake } from './support/content.js';
import { testSigning } from './support/signing-env.js';
import * as fs from 'node:fs';

// A bank's distribution and configuration release (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, R-09-48 to R-09-52, R-11-37 to
// R-11-40): the bill of materials, the slot contract, the parameter contract, the signature and who may build one.

const sha = (t: string) => `sha256:${createHash('sha256').update(t).digest('hex')}`;
const trust = () => parseTrustedSigners(fs.readFileSync(testSigning().trustFile, 'utf8'));
let env: ReturnType<typeof freshEnv>;
let dist: ReturnType<typeof buildDistribution>;

beforeAll(() => {
  env = freshEnv();
  dist = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.22-1', changeSets: ['CS-TEST-1'] });
}, 120000);

describe('a distribution', () => {
  it('holds the platform, the schemes the bank licenses, its bank pack and the engine binding, each artefact with its SHA-256', () => {
    expect(licensedSchemes(env, 'example-bank')).toEqual(['uae.wps']);
    const m = dist.manifest;
    expect(m).toMatchObject({ kind: 'distribution', id: '2026.11.22-1', bank: 'example-bank', engine: 'temporal', schemes: ['uae.wps'], platform: ['p', 'uae.p'] });
    expect(m.billOfMaterials.packs.map((p: any) => p.scope)).toEqual(['p', 'uae.p', 'uae.wps', 'b']);
    for (const a of m.billOfMaterials.artefacts) expect(sha(dist.files[a.path]), a.path).toBe(a.sha256);
    expect(m.billOfMaterials.artefacts.some((a: any) => a.kind === 'engine-binding' && a.name === 'p.binding.temporal.v1' && a.release === '1.1.0')).toBe(true);
    // The error dictionaries, the operations views and every platform workflow expanded, with its slots filled.
    expect(Object.keys(dist.files).some((f) => f.startsWith('errors/'))).toBe(true);
    expect(Object.keys(dist.files).some((f) => f.startsWith('ops/'))).toBe(true);
    const slots = parseYaml(dist.files['workflows/slots.yaml']).slots;
    expect(slots.find((s: any) => s.workflow === 'p.wf.corporate.v1' && s.slot === 'scheme-leg').legs.some((l: string) => l.startsWith('uae.wps.wf.salary-file-out.v1@'))).toBe(true);
    expect(Object.keys(dist.files).some((f) => /^engine\/p\.wf\.corporate\.v1\/release\.json$/.test(f))).toBe(true);
    // No simulator, no values: they are not part of a distribution.
    expect(Object.keys(dist.files).some((f) => f.startsWith('simulators/') || /\/configuration\//.test(f))).toBe(false);
    expect(m.slotContracts.some((s: any) => s.contract === 'scheme-leg@1' && s.scheme === 'uae.wps')).toBe(true);
  });

  it('is signed as a distribution: its statement carries the bill of materials and the parameter contract, and any change is refused', () => {
    const v = verifyFiles(dist.files, { releaseId: 'dist/example-bank@2026.11.22-1', tenant: 'example-bank', kind: 'distribution' }, trust());
    expect(v.kind).toBe('distribution');
    const st = JSON.parse(dist.files['release.json']);
    expect(st.billOfMaterials).toEqual(dist.manifest.billOfMaterials);
    expect(st.parameterContract).toEqual(dist.manifest.parameterContract);
    expect(() => verifyFiles(dist.files, { releaseId: 'dist/example-bank@2026.11.22-1', tenant: 'example-bank' }, trust())).toThrow(/not a workflow-package/);
    const changed = { ...dist.files, 'binding.yaml': dist.files['binding.yaml'] + '# changed\n' };
    expect(() => verifyFiles(changed, { releaseId: 'dist/example-bank@2026.11.22-1', tenant: 'example-bank', kind: 'distribution' }, trust())).toThrow(SignatureRejected);
  });

  it('declares its parameter contract (every dial, its type and bounds) and its longest wait', () => {
    const c = dist.manifest.parameterContract;
    const ack = c.parameters.find((p: any) => p.parameter === 'uae.wps.exchange.submit-salary-file.v1#ackWait');
    expect(ack).toMatchObject({ type: 'duration', bounds: { min: 'PT1H', max: 'P7D' } });
    expect(c.parameters.every((p: any) => p.type && p.default !== undefined)).toBe(true);
    expect(isoSeconds(dist.manifest.longestWait)).toBeGreaterThanOrEqual(30 * 86400);
    expect(longestWait({ parameters: [] })).toBe('PT0S');
    const bad = parameterContract([{ name: 'p.x.v1', parameters: { wait: { type: 'duration', default: 'P9D', bounds: { min: 'P1D', max: 'P7D' } }, n: { type: 'number' } } }]);
    expect(bad.problems).toEqual(['p.x.v1#wait: its default "P9D" is outside its own bounds {"min":"P1D","max":"P7D"}', 'p.x.v1#n declares no default']);
  });

  it('is refused when a scheme does not fit the slot contract of its platform', () => {
    const platform = env.store.byKind('workflow').map((e) => e.doc).filter((d) => d.wfKind === 'platform');
    expect(slotContracts(platform).map((s) => s.id)).toContain('scheme-leg@1');
    const legs = env.store.byKind('workflow').map((e) => e.doc).filter((d) => d.wfKind === 'leg' && d.name.startsWith('uae.wps.'));
    expect(checkSlotContracts(platform, [{ scope: 'uae.wps', docs: legs }]).problems).toEqual([]);
    const wrongMajor = clone(legs[0]);
    wrongMajor.implements.of = wrongMajor.implements.of.map((o: string) => o.replace(/\.v1$/, '.v2'));
    expect(checkSlotContracts(platform, [{ scope: 'uae.wps', docs: [wrongMajor] }]).problems[0]).toMatch(/fills scheme-.*@2 of p\.wf\..*\.v2, but the platform offers scheme-.*@1/);
    // The leg's latest release, the one a build takes (1.1.0 since the labour ministry lookup, Issue I-88).
    const extraWrite = clone(env.store.latest('uae.wps.wf.salary-file-out.v1')!.doc);
    extraWrite.entry.reads = [...extraWrite.entry.reads, 'facts.posting'];
    expect(checkSlotContracts(platform, [{ scope: 'uae.wps', docs: [extraWrite] }]).problems[0]).toMatch(/does not fit the slot contract scheme-leg@1 of p\.wf\.corporate\.v1: read facts\.posting/);
    // In the build: the scheme's leg in the store no longer fits, and the build is refused.
    const e = freshEnv();
    const leg = e.store.all().find((x) => x.name === 'uae.wps.wf.salary-file-out.v1' && x.status === 'published' && x.release === extraWrite.release)!;
    leg.doc = { ...leg.doc, exit: { ...leg.doc.exit, outcomes: [...leg.doc.exit.outcomes, 'lost'] } };
    expect(() => buildDistribution(e, { bank: 'example-bank', engine: 'temporal', id: '2026.11.22-9' })).toThrow(/does not fit the slot contract scheme-leg@1 of p\.wf\.corporate\.v1: outcome lost/);
  });

  it('takes the next id of its bank and day', async () => {
    const e = freshEnv();
    expect(await nextId(e, 'example-bank', '2026-11-22')).toBe('2026.11.22-1');
  });
});

describe('a configuration release', () => {
  const against = () => [{ releaseId: dist.releaseId, parameterContract: dist.manifest.parameterContract }];

  it('carries the values with their validFrom, the port bindings by secretRef and the contracts it was checked against, signed', () => {
    const c = buildConfigurationRelease(env, { bank: 'example-bank', release: '1.2.0', against: against(), bindings: [{ port: 'p.port.core-banking.v1', adapter: 'gateway-rest', settings: { baseUrl: 'https://core.bank.example.test' }, secretRef: 'env:TH_CORE_TOKEN', validFrom: '2026-12-01' }] });
    expect(c.manifest.requiresContract).toEqual([{ distribution: 'dist/example-bank@2026.11.22-1', sha256: expect.stringMatching(/^sha256:/) }]);
    expect(parseYaml(c.files['configuration/configuration-set.yaml']).values.every((v: any) => v.validFrom)).toBe(true);
    expect(parseYaml(c.files['bindings/port-bindings.yaml']).bindings[0].secretRef).toBe('env:TH_CORE_TOKEN');
    expect(verifyFiles(c.files, { releaseId: 'config/example-bank@1.2.0', tenant: 'example-bank', kind: 'configuration' }, trust()).kind).toBe('configuration');
  });

  it('is refused for a value no distribution declares, a value out of bounds, or a credential that is not a reference', () => {
    const set = clone(env.store.configurationSet('example-bank'));
    set.values.push({ parameter: 'p.nothing.v1#dial', value: 1, validFrom: '2026-12-01' });
    expect(() => buildConfigurationRelease(env, { bank: 'example-bank', release: '1.2.1', against: against(), set })).toThrow(/p\.nothing\.v1#dial is not a dial of dist\/example-bank@2026\.11\.22-1/);
    const out = clone(env.store.configurationSet('example-bank'));
    out.values.push({ parameter: 'uae.wps.exchange.submit-salary-file.v1#ackWait', value: 'P30D', validFrom: '2026-12-01' });
    expect(() => buildConfigurationRelease(env, { bank: 'example-bank', release: '1.2.2', against: against(), set: out })).toThrow(/ackWait = "P30D" is outside its bounds/);
    expect(() => buildConfigurationRelease(env, { bank: 'example-bank', release: '1.2.3', against: against(), bindings: [{ port: 'p.port.core-banking.v1', adapter: 'gateway-rest', settings: { apiToken: 'synthetic' }, validFrom: '2026-12-01' }] })).toThrow(/apiToken as a setting: give it as secretRef/);
    expect(() => buildConfigurationRelease(env, { bank: 'example-bank', release: '1.2.4', against: [] })).toThrow(/build one first/);
  });
});

describe('who builds them (R-11-40)', () => {
  it('follows the permission table: a platform approver any bank\'s distribution, a bank approver its own and its configuration releases', async () => {
    expect(granted(['platform approver'], 'shared', 'distribution', 'approve').length).toBe(1);
    expect(granted(['bank approver'], 'bank', 'distribution', 'approve').length).toBe(1);
    expect(granted(['bank approver'], 'bank', 'configuration-release', 'approve').length).toBe(1);
    for (const r of ['workflow designer', 'workflow developer', 'bank configurator', 'auditor', 'service operations'])
      expect(granted([r], r.startsWith('bank') ? 'bank' : 'shared', 'distribution', 'approve'), r).toEqual([]);
    const e = freshEnv();
    await expect(buildAndPublish(e, 'example.maker', 'example-bank', { kind: 'distribution', bank: 'example-bank' })).rejects.toThrow(/may not approve a distribution/);
    await expect(buildAndPublish(e, 'example.checker', 'example-bank', { kind: 'distribution', bank: 'second-bank' })).rejects.toThrow(/not second-bank/);
    const r = await buildAndPublish(e, 'example.checker', 'example-bank', { kind: 'distribution', bank: 'example-bank', engine: 'temporal' });
    expect(r.tag).toMatch(/^dist\/example-bank\/\d{4}\.\d{2}\.\d{2}-1$/);
    const files = fake().files(e.store.content!.repository, 'main');
    expect(files[`content-configurations/${r.folder}/release.sig`]).toBeDefined();
    // The next one on the same day takes the next counter; a configuration release is checked against it.
    const r2 = await buildAndPublish(e, 'shared.checker', 'owner', { kind: 'distribution', bank: 'example-bank', engine: 'temporal' });
    expect(r2.id.endsWith('-2')).toBe(true);
    const c = await buildAndPublish(e, 'example.checker', 'example-bank', { kind: 'configuration', bank: 'example-bank', release: '9.0.0' });
    expect(c.manifest.requiresContract.map((x: any) => x.distribution)).toEqual([`dist/example-bank@${r.id}`, `dist/example-bank@${r2.id}`]);
    await expect(buildAndPublish(e, 'shared.checker', 'owner', { kind: 'configuration', bank: 'example-bank' })).rejects.toThrow(/own bank approver/);
  }, 240000);
});

describe('on approval of a change set (R-11-40)', () => {
  const ACT = { kind: 'activity', name: 'p.enrich.dist-note.v1', release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'], outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' }, provenance: { origin: 'authored', status: 'provisional', clauses: ['Distribution test'] } };

  it("a platform change approved by a platform approver builds a distribution for every bank that takes it, in the change set's commit", async () => {
    const e = freshEnv();
    e.store.settings = { ...e.store.settings, distributions: true };
    const d = e.store.saveDraft('owner', 'shared.designer', ACT);
    const r: any = await gov.approveChangeSet(e, 'owner', 'shared.checker', [d.key]);
    const git = await r.write;
    expect(r.bundles.map((b: any) => b.releaseId)).toEqual([expect.stringMatching(/^dist\/example-bank@\d{4}\.\d{2}\.\d{2}-1$/)]);
    expect(git.tags).toContain(r.bundles[0].tag);
    const files = fake().files(e.store.content!.repository, 'main');
    const m = parseYaml(files[`content-configurations/${r.bundles[0].folder}/manifest.yaml`]);
    expect(m.changeSets).toEqual([r.changeSet.id]);
    expect(m.billOfMaterials.artefacts.some((a: any) => a.name === 'p.enrich.dist-note.v1')).toBe(true);
  }, 240000);

  it("a bank's configuration change approved by its bank approver builds a configuration release against its distributions", async () => {
    const e = freshEnv();
    e.store.settings = { ...e.store.settings, distributions: true };
    await buildAndPublish(e, 'example.checker', 'example-bank', { kind: 'distribution', bank: 'example-bank', engine: 'temporal' });
    const set = clone(e.store.configurationSet('example-bank'));
    set.release = '7.0.0';
    const d: any = gov.saveConfiguration(e, 'example-bank', 'example.maker', toYaml(set));
    const r: any = await gov.approveChangeSet(e, 'example-bank', 'example.checker', [d.key]);
    await r.write;
    expect(r.bundles.map((b: any) => b.releaseId)).toEqual(['config/example-bank@7.0.0']);
  }, 240000);
});
