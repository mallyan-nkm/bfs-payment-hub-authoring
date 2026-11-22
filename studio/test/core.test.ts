import { describe, expect, it } from 'vitest';
import { canonicalJson, contentHash } from '../src/core/canonical.js';
import { mayDependOn, parseName, compareReleases } from '../src/core/names.js';
import { addDuration, DEFAULT_CALENDAR, durationMs } from '../src/core/duration.js';
import { parseYaml } from '../src/core/yaml.js';
import { OWNER_TENANT } from '../src/core/store.js';
import { freshEnv } from './helpers.js';

describe('names and scopes (R-01-01 to R-01-04)', () => {
  it('parses every scope level', () => {
    expect(parseName('uae.p.validate.iban.v1')).toMatchObject({ scope: 'uae.p', scopeLevel: 'cluster-platform', type: 'validate', local: 'iban', major: 1 });
    expect(parseName('p.wf.corporate.v1')).toMatchObject({ scope: 'p', type: 'wf' });
    expect(parseName('uae.wps.wf.salary-file-out.v1')).toMatchObject({ scope: 'uae.wps', scopeLevel: 'scheme', scheme: 'wps' });
    expect(parseName('b.hook.add-cost-centre.v1')).toMatchObject({ scope: 'b', scopeLevel: 'bank' });
    expect(parseName('uae.wps.sub.submit-salary-file.v1')).toMatchObject({ type: 'sub' });
  });
  it('refuses names outside the grammar', () => {
    expect(parseName('platform.wf.corporate.v1')).toBeNull();
    expect(parseName('p.flow.corporate.v1')).toBeNull();
    expect(parseName('p.wf.corporate')).toBeNull();
  });
  it('follows table 1.3 for dependencies', () => {
    expect(mayDependOn('uae.wps', 'uae.p')).toBe(true);
    expect(mayDependOn('uae.wps', 'p')).toBe(true);
    expect(mayDependOn('uae.p', 'uae.wps')).toBe(false);
    expect(mayDependOn('p', 'uae.p')).toBe(false);
    expect(mayDependOn('uae.wps', 'b')).toBe(false);
    expect(mayDependOn('b', 'uae.wps')).toBe(true);
  });
  it('orders releases numerically', () => {
    expect(compareReleases('1.10.0', '1.9.9')).toBeGreaterThan(0);
  });
});

describe('canonical JSON and the content hash (R-01-12)', () => {
  it('orders keys and keeps numbers in ECMAScript form', () => {
    expect(canonicalJson({ b: 1, a: [true, null, 1.5, 'x'], c: { z: 1, y: 2 } })).toBe('{"a":[true,null,1.5,"x"],"b":1,"c":{"y":2,"z":1}}');
  });
  it('matches the hashes the factory pack manifests carry', () => {
    const env = freshEnv();
    let checked = 0;
    for (const p of env.store.byKind('pack')) for (const a of p.doc.artefacts ?? []) {
      const e = a.kind === 'test' ? env.store.test(a.name) : env.store.get(a.name, a.release);
      expect(e, a.name).toBeDefined();
      expect(e!.hash, a.name).toBe(a.hash);
      checked++;
    }
    expect(checked).toBeGreaterThan(140);
  });
});

describe('YAML as the spec tools read it', () => {
  it('keeps digit strings with a leading zero as strings, and dates as strings', () => {
    expect(parseYaml('a: 00001234567890\nb: 2026-11-17\nc: 12')).toEqual({ a: '00001234567890', b: '2026-11-17', c: 12 });
  });
});

describe('durations and the business calendar', () => {
  it('adds business days over a weekend', () => {
    const fri = Date.parse('2026-11-20T06:00:00Z');
    expect(new Date(addDuration(fri, 'P1D', DEFAULT_CALENDAR)).toISOString()).toBe('2026-11-23T06:00:00.000Z');
    expect(durationMs('PT1H30M')).toBe(5400000);
  });
});

describe('the store (C01, R-01-11, R-06-03, R-06-04)', () => {
  it('loads every document of the spec', () => {
    const env = freshEnv();
    expect(env.store.all().length).toBe(181); // 3 more for the error dictionaries of p, uae.p and uae.wps (R-05-51); 2 more for the operations views of p and uae.wps (R-05-48); 199 less the 44 documents of the removed second scheme, and the two bank lookup tests (I-37); 10 more for the agent's returns file (I-39); 5 more for its decision (I-39 to I-41: a composite and its subflow, three tests); 4 more for the file transfer and decryption ports and simulators (R-12-12)
    expect(env.store.tenants()).toEqual(['owner', 'example-bank']);
  });
  it('validates every document against its schema', () => {
    const env = freshEnv();
    for (const e of env.store.all()) expect(env.schemas.validate(e.doc), e.key).toEqual([]);
  });
  it('refuses a second write to a published release', () => {
    const env = freshEnv();
    const doc = env.store.doc('uae.p.validate.iban.v1');
    expect(() => env.store.saveDraft(OWNER_TENANT, 'maker', doc)).toThrow(/immutable/);
  });
  it('publishes a draft only with a checker who is not the maker', () => {
    const env = freshEnv();
    const doc = { ...env.store.doc('uae.p.validate.iban.v1'), release: '1.0.1' };
    const d = env.store.saveDraft(OWNER_TENANT, 'maker', doc);
    expect(() => env.store.publish(OWNER_TENANT, d.key, 'maker', 'maker')).toThrow(/differ/);
    const p = env.store.publish(OWNER_TENANT, d.key, 'maker', 'checker');
    expect(p.status).toBe('published');
    expect(env.store.latest('uae.p.validate.iban.v1')!.release).toBe('1.0.1');
    expect(env.store.audit.some((a) => a.action === 'publish' && a.key === 'uae.p.validate.iban.v1@1.0.1')).toBe(true);
  });
  it('keeps shared content read-only to a bank tenant', () => {
    const env = freshEnv();
    const doc = { ...env.store.doc('uae.p.validate.iban.v1'), release: '1.0.2' };
    expect(() => env.store.saveDraft('example-bank', 'bank.maker', doc)).toThrow(/read-only/);
    const hook = { ...env.store.doc('b.hook.treasury-notify.v1'), release: '1.0.1' };
    expect(env.store.saveDraft('example-bank', 'bank.maker', hook).status).toBe('draft');
  });
  it('shows a tenant shared content and its own, and nothing of another tenant (R-06-01)', () => {
    const env = freshEnv();
    const hook = { ...env.store.doc('b.hook.treasury-notify.v1'), release: '1.0.1' };
    env.store.saveDraft('example-bank', 'bank.maker', hook);
    expect(env.store.visible('another-bank').some((e) => e.tenant === 'example-bank')).toBe(false);
    expect(env.store.visible('example-bank').some((e) => e.status === 'draft')).toBe(true);
  });
});
