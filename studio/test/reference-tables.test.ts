import { describe, expect, it } from 'vitest';
import { parseYaml } from '../src/core/yaml.js';
import { referenceTableProblems, referenceValueProblems, resolveReferenceValues, rowsAt } from '../src/core/reference.js';
import { buildConfigurationRelease, buildDistribution, referenceTablesFor } from '../src/export/distribution.js';
import { Run } from '../src/interp/interpreter.js';
import { checkAll } from '../src/checks/checks.js';
import { clone, freshEnv } from './helpers.js';
import './support/signing-env.js';

// Reference tables (R-01-25, R-11-51; owner's decisions of 23 November 2026): market, scheme and bank reference data as
// effective-dated tables in the studio, shipped in a bank's configuration release, never in a distribution. The participant
// directory is the Central Bank's register of licensed banks, October 2026, with no routing code but the one the source holds;
// a bank's participant list reads names by routing code from it.

describe('the reference tables of the factory and the example bank (R-01-25)', () => {
  it('loads the Central Bank\'s register as the participant directory: 63 banks, each with its licence and identification number', () => {
    const env = freshEnv();
    const dir = env.store.doc('uae.p.ref.participant-directory.v1');
    expect(dir.kind).toBe('reference-table');
    expect(dir.source.url).toBe('https://centralbank.ae/media/ffco0wgo/cb-register-oct-2026.pdf');
    const rows = rowsAt(dir, '2026-11-24');
    expect(rows).toHaveLength(63);
    expect(rows.filter((r: any) => r.licenceType === 'national')).toHaveLength(25);
    expect(new Set(rows.map((r: any) => r.identificationNumber)).size).toBe(63);
    // The register gives no routing code; the one filled is the one the TransactHub source holds, and the table says so.
    expect(rows.filter((r: any) => r.routingCode).map((r: any) => r.registerNo)).toEqual([24]);
    expect(dir.source.note).toMatch(/ReseedWpsParametersWithIdentityIds/);
    expect(rowsAt(dir, '2026-09-30')).toEqual([]);
    for (const n of ['uae.p.ref.emirates.v1', 'uae.p.ref.free-zones.v1', 'uae.p.ref.business-calendar.v1', 'p.ref.purpose-codes.v1']) expect(env.store.doc(n)?.kind, n).toBe('reference-table');
    expect(env.store.doc('b.ref.branches.v1', undefined, 'example-bank')?.kind).toBe('reference-table');
    expect(checkAll(env).filter((i) => i.code === 'L49' || i.code === 'L50')).toEqual([]);
  });

  it('refuses a row that does not fit, a repeated key, editions out of order and a reference to no key (L49)', () => {
    const env = freshEnv();
    const tables = new Map(env.store.byKind('reference-table').map((e) => [e.name, e.doc]));
    const t = clone(env.store.doc('uae.p.ref.free-zones.v1'));
    t.editions[0].rows.push({ code: 'jafza', name: 'x', active: true }, { code: 'DIFC', name: 'again', active: true }, { code: 'NEWZ', name: 'y', emirate: 'AE-XX', active: 'yes' });
    t.editions.push({ validFrom: '2026-01-01', rows: [] });
    const p = referenceTableProblems(t, tables);
    expect(p.some((m) => /code "jafza" does not match/.test(m))).toBe(true);
    expect(p.some((m) => /repeats the key DIFC/.test(m))).toBe(true);
    expect(p.some((m) => /emirate "AE-XX" is no key of uae.p.ref.emirates.v1/.test(m))).toBe(true);
    expect(p.some((m) => /active is not a boolean/.test(m))).toBe(true);
    expect(p).toContain('its editions are not in strictly increasing validFrom order');
  });
});

describe('a bank\'s participant list read from the participant directory (R-11-51, Issue I-130)', () => {
  it('reads one value per edition, leaving out a bank whose routing code is not yet filled (L50)', () => {
    const env = freshEnv({ banks: ['dib'] } as any);
    const set = env.store.configurationSet('dib');
    const v = set.values.find((x: any) => x.parameter === 'uae.wps.table.participant-names.v1#participants');
    expect(v.fromReference.table).toBe('uae.p.ref.participant-directory.v1');
    const resolved = resolveReferenceValues([v], (n) => env.store.doc(n));
    expect(resolved).toEqual([{ parameter: v.parameter, validFrom: '2026-11-24', value: [{ routingCode: '802420101', name: 'Dubai Islamic Bank P.J.S.C' }] }]);
    // A later edition starts a value of its own from its date.
    const t = clone(env.store.doc('uae.p.ref.participant-directory.v1'));
    t.editions.push({ validFrom: '2027-01-01', rows: t.editions[0].rows.map((r: any) => (r.registerNo === 40 ? { ...r, routingCode: '999999999' } : r)) });
    expect(resolveReferenceValues([v], () => t).map((x) => [x.validFrom, x.value.length])).toEqual([['2026-11-24', 1], ['2027-01-01', 2]]);
    const decl = env.store.doc('uae.wps.table.participant-names.v1').parameters.participants;
    expect(referenceValueProblems({ ...v, fromReference: { ...v.fromReference, columns: { name: 'bankName' } } }, decl, t)).toEqual([`${v.parameter} reads column bankName, which uae.p.ref.participant-directory.v1 does not have`]);
    expect(referenceValueProblems({ ...v, fromReference: { table: 'uae.p.ref.nothing.v1', columns: {} } }, decl, undefined)[0]).toMatch(/no reference table/);
  });

  it('gives the table the names on the studio interpreter, by date', () => {
    const env = freshEnv({ banks: ['dib'] } as any);
    const it = new Run({ store: env.store, tenant: 'dib', evaluationDate: '2026-11-24' } as any);
    const decl = env.store.doc('uae.wps.table.participant-names.v1').parameters.participants;
    expect(it.lookupParam('uae.wps.table.participant-names.v1', 'participants', decl, '2026-11-24')).toEqual([{ routingCode: '802420101', name: 'Dubai Islamic Bank P.J.S.C' }]);
    expect(it.lookupParam('uae.wps.table.participant-names.v1', 'participants', decl, '2026-11-23')).toEqual([]);
  });
});

describe('the reference tables travel in the configuration release, never in a distribution (R-01-25, R-09-51)', () => {
  it('carries every table the bank sees, each with its editions, and the values a table gives', () => {
    const env = freshEnv();
    const dist = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.24-1' });
    expect(Object.keys(dist.files).some((f) => f.includes('.ref.'))).toBe(false);
    expect(referenceTablesFor(env, 'example-bank').map((t) => t.name)).toEqual(['b.ref.bank-calendar.v1', 'b.ref.branches.v1', 'b.ref.cut-offs.v1', 'p.ref.purpose-codes.v1', 'uae.p.ref.business-calendar.v1', 'uae.p.ref.emirates.v1', 'uae.p.ref.free-zones.v1', 'uae.p.ref.participant-directory.v1']);
    const set = clone(env.store.configurationSet('example-bank'));
    set.values = set.values.filter((v: any) => v.parameter !== 'uae.wps.table.participant-names.v1#participants');
    set.values.push({ parameter: 'uae.wps.table.participant-names.v1#participants', fromReference: { table: 'uae.p.ref.participant-directory.v1', columns: { routingCode: 'routingCode', name: 'name' } }, validFrom: '2026-12-01' });
    const c = buildConfigurationRelease(env, { bank: 'example-bank', release: '1.7.1', against: [{ releaseId: dist.releaseId, parameterContract: dist.manifest.parameterContract }], set });
    expect(Object.keys(c.files).filter((f) => f.startsWith('reference/')).sort()).toEqual(['reference/b.ref.bank-calendar.v1.yaml', 'reference/b.ref.branches.v1.yaml', 'reference/b.ref.cut-offs.v1.yaml', 'reference/p.ref.purpose-codes.v1.yaml', 'reference/uae.p.ref.business-calendar.v1.yaml', 'reference/uae.p.ref.emirates.v1.yaml', 'reference/uae.p.ref.free-zones.v1.yaml', 'reference/uae.p.ref.participant-directory.v1.yaml']);
    expect(c.manifest.referenceTables).toBe(8);
    expect(c.manifest.billOfMaterials.artefacts.map((a: any) => a.path)).toContain('reference/uae.p.ref.participant-directory.v1.yaml');
    const shipped = parseYaml(c.files['configuration/configuration-set.yaml']).values.find((v: any) => v.parameter === 'uae.wps.table.participant-names.v1#participants');
    expect(shipped).toEqual({ parameter: 'uae.wps.table.participant-names.v1#participants', value: [{ routingCode: '802420101', name: 'Dubai Islamic Bank P.J.S.C' }], validFrom: '2026-12-01' });
    expect(parseYaml(c.files['reference/uae.p.ref.participant-directory.v1.yaml']).editions[0].rows).toHaveLength(63);
  });
});
