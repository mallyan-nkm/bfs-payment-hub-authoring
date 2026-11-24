import { describe, expect, it } from 'vitest';
import { applyDefaultCurrency, bankDefaultCurrency, clusterOf, defaultCurrencyDecl, withDefaultCurrency } from '../src/core/currency.js';
import { bankCurrencyOf } from '../src/export/distribution.js';
import { runTest } from '../src/testing/runner.js';
import { freshEnv } from './helpers.js';

// The country's default currency (R-02-24; owner's decision of 24 November 2026): a value of the country cluster, AED for the UAE,
// which a bank of the cluster inherits and may set for itself; the platform p names none.
describe("the country cluster's default currency (R-02-24)", () => {
  const env = freshEnv();
  const acts = env.store.current().filter((e) => e.kind === 'activity').map((e) => e.doc);

  it('is declared by the UAE cluster, by role, and by no platform artefact', () => {
    const d = defaultCurrencyDecl(acts, 'uae')!;
    expect([d.owner, d.parameter, d.decl.default, d.decl.bounds.pattern]).toEqual(['uae.p.enrich.default-currency.v1', 'currency', 'AED', '^[A-Z]{3}$']);
    expect(acts.filter((a) => a.name.startsWith('p.')).flatMap((a) => Object.values<any>(a.parameters ?? {})).some((p) => p.role === 'default-currency')).toBe(false);
    expect([clusterOf('uae.wps'), clusterOf('p'), clusterOf('b')]).toEqual(['uae', undefined, undefined]);
  });

  it('gives a UAE bank that sets nothing AED, keeps a bank\'s own, and gives none where the cluster declares none', () => {
    expect(bankCurrencyOf(env, 'example-bank', [])).toBe('AED');
    const own = [{ parameter: 'uae.p.enrich.default-currency.v1#currency', value: 'USD', validFrom: '2026-01-01' }];
    expect(bankCurrencyOf(env, 'example-bank', own)).toBe('USD');
    expect(bankDefaultCurrency(defaultCurrencyDecl(acts, 'xx'), [], '2026-11-24')).toBeUndefined();
    // A payment that names none takes it; one that names its own keeps it; with no default, none is given, and the canonical
    // checks then refuse it (R-09-231).
    const file: any = { batch: { id: 'F-1' }, items: [{ payment: { instructedAmount: { value: '1.00' } } }] };
    expect(applyDefaultCurrency(file, 'AED')).toBe(true);
    expect(file.batch.currency).toBe('AED');
    const named: any = { batch: { id: 'F-2', currency: 'EUR' } };
    expect(applyDefaultCurrency(named, 'AED')).toBe(false);
    const bare: any = { batch: { id: 'F-3' } };
    expect(applyDefaultCurrency(bare, undefined)).toBe(false);
    expect(bare.batch.currency).toBeUndefined();
  });

  it("gives a core-banking binding that names no currency the bank's default in its configuration release", () => {
    const b = withDefaultCurrency([
      { port: 'p.port.core-banking.v1', adapter: 'simulator', settings: { openingBalance: '1000000' } },
      { port: 'p.port.core-banking.v1', adapter: 'gateway-rest', settings: { currency: 'USD' } },
      { port: 'p.port.core-banking.v1', adapter: 'file-bulk', settings: {} },
      { port: 'p.port.sanctions.v1', adapter: 'gateway-rest', settings: {} },
    ], 'AED');
    expect(b.map((x) => x.settings.currency ?? x.settings.localCurrency ?? null)).toEqual(['AED', 'USD', 'AED', null]);
  });

  it('the cluster activity applies it, and a bank may set its own (UAEP-CUR-001 to 003)', async () => {
    for (const id of ['UAEP-CUR-001', 'UAEP-CUR-002', 'UAEP-CUR-003']) expect((await runTest(env, env.store.test(id)!.doc)).passed, id).toBe(true);
  });
});
