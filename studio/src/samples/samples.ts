import { buildFacts } from '../interp/facts.js';
import { runRuleSet } from '../rules/service.js';
import { runTest } from '../testing/runner.js';
import type { StudioEnv } from '../env.js';

// Sample messages per scheme (F-05, C12-S4): a valid sample, and invalid samples each labelled with
// the rule it breaks and the code that rule yields. Every value is synthetic. The samples are
// proved: the valid one passes the scheme's rule sets, each invalid one fails with its label.

export interface Sample { name: string; message: string; valid: boolean; breaks?: { rule: string; reasonCode: string }; fileName: string; content: string }

/** IBAN check digits (ISO 13616), so synthetic IBANs are valid unless a sample breaks them on purpose. */
export function iban(country: string, bban: string): string {
  const s = bban + country.split('').map((c) => String(c.charCodeAt(0) - 55)).join('') + '00';
  let rem = 0;
  for (const ch of s) rem = (rem * 10 + Number(ch)) % 97;
  return `${country}${String(98 - rem).padStart(2, '0')}${bban}`;
}

interface SifRecord { personId: string; agent: string; iban: string; fixed: number; variable: number }

function sif(o: { employerId?: string; employerRouting?: string; count?: number; total?: number; currency?: string; records: SifRecord[]; ref: string }): string {
  const records = o.records;
  const total = o.total ?? records.reduce((a, r) => a + r.fixed + r.variable, 0);
  const lines = [
    `# file: ${o.employerId ?? '0000001234567'}261117090000.sif`,
    `SCR,${o.employerId ?? '0000001234567'},${o.employerRouting ?? '302620122'},2026-11-17,0900,102026,${o.count ?? records.length},${total.toFixed(2)},${o.currency ?? 'AED'},${o.ref}`,
    ...records.map((r) => `EDR,${r.personId},${r.agent},${r.iban},2026-10-01,2026-10-31,31,${r.fixed.toFixed(2)},${r.variable.toFixed(2)},0`),
  ];
  return lines.join('\n') + '\n';
}

const REC = (n: number, over: Partial<SifRecord> = {}): SifRecord => ({ personId: `0000123456789${n}`, agent: '203320101', iban: iban('AE', `03300000000000000${String(n).padStart(2, '0')}`), fixed: 1000 + n * 100, variable: 50, ...over });

export const GENERATORS: Record<string, () => Sample[]> = {
  'uae.wps.sif': () => [
    { name: 'valid', message: 'uae.wps.sif', valid: true, fileName: 'wps-sif-generated-valid.sif', content: sif({ ref: 'GEN-1', records: [REC(1), REC(2)] }) },
    { name: 'control total', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.wps.rule.control-total-matches.v1', reasonCode: 'AM10' }, fileName: 'wps-sif-generated-bad-total.sif', content: sif({ ref: 'GEN-2', total: 1, records: [REC(1), REC(2)] }) },
    { name: 'record count', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.wps.rule.record-count-matches.v1', reasonCode: 'AM18' }, fileName: 'wps-sif-generated-bad-count.sif', content: sif({ ref: 'GEN-3', count: 3, records: [REC(1), REC(2)] }) },
    { name: 'currency', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.wps.rule.currency-aed.v1', reasonCode: 'AM03' }, fileName: 'wps-sif-generated-bad-currency.sif', content: sif({ ref: 'GEN-4', currency: 'USD', records: [REC(1)] }) },
    { name: 'employer routing code', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.wps.rule.employer-routing-code-format.v1', reasonCode: 'RC01' }, fileName: 'wps-sif-generated-bad-routing.sif', content: sif({ ref: 'GEN-5', employerRouting: '3026', records: [REC(1)] }) },
    { name: 'IBAN check digits', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.p.rule.iban-checksum.v1', reasonCode: 'AC03' }, fileName: 'wps-sif-generated-bad-iban.sif', content: sif({ ref: 'GEN-6', records: [REC(1, { iban: 'AE000330000000000000001' })] }) },
    { name: 'person id', message: 'uae.wps.sif', valid: false, breaks: { rule: 'uae.wps.rule.person-id-format.v1', reasonCode: 'FF01' }, fileName: 'wps-sif-generated-bad-person.sif', content: sif({ ref: 'GEN-7', records: [REC(1, { personId: '1234' })] }) },
  ],
};

/** The rule sets a scheme's start message must pass, by level. */
const RULESETS: Record<string, { batch: string[]; item: string[] }> = {
  'uae.wps.sif': { batch: ['uae.wps.ruleset.salary-file.v1'], item: ['uae.wps.ruleset.salary-record.v1', 'uae.p.ruleset.iban.v1'] },
};

export function messagesWithSamples(): string[] {
  return Object.keys(GENERATORS);
}

/** Map a sample with the scheme's intake map and run the scheme's rule sets on it. */
export async function proveSample(env: StudioEnv, s: Sample) {
  const scope = s.message.split('.').slice(0, 2).join('.');
  const entry = env.store.scheme(scope)?.messages?.find((m: any) => m.id === s.message);
  const map = entry?.disposition?.start?.intakeMap;
  if (!map) throw new Error(`${s.message} has no intake map`);
  // In memory: the sample is searched first and nothing is written (R-11-24).
  const r = await runTest({ ...env, sampleOverlay: { ...(env.sampleOverlay ?? {}), [s.fileName]: s.content } }, { kind: 'test', id: 'SAMPLE', target: map, level: 'activity', evaluationDate: '2026-11-17', given: { inbound: `samples/${s.fileName}` }, expect: {} });
  const inst = r.record?.instances.find((i) => i.kind === 'activity');
  const ctx = inst?.context ?? {};
  const findings: any[] = [];
  const resolve = (n: string) => env.store.doc(n);
  const all = ['header', 'payment', 'batch', 'items[]', 'status', 'facts', 'ext'];
  for (const rs of RULESETS[s.message]?.batch ?? []) findings.push(...runRuleSet(rs, buildFacts({ ctx, reads: all, extensions: env.store.byKind('extension').map((e) => e.doc) }), { resolve, evaluationDate: '2026-11-17' }).findings);
  for (const it of ctx.items ?? []) {
    const ictx = { ...ctx, payment: it.payment, ext: it.ext, items: [it] };
    for (const rs of RULESETS[s.message]?.item ?? []) findings.push(...runRuleSet(rs, buildFacts({ ctx: ictx, reads: all, level: 'item', extensions: env.store.byKind('extension').map((e) => e.doc) }), { resolve, evaluationDate: '2026-11-17' }).findings);
  }
  const blocking = findings.filter((f) => f.severity === 'reject' || f.severity === 'repair');
  const mapped = inst?.outcome === 'mapped';
  const ok = s.valid ? mapped && blocking.length === 0 : !mapped || blocking.some((f) => f.reasonCode === s.breaks!.reasonCode && f.rule === s.breaks!.rule);
  return { name: s.name, valid: s.valid, breaks: s.breaks, mapped, findings: findings.map((f) => ({ rule: f.rule, reasonCode: f.reasonCode, severity: f.severity })), proved: ok };
}

export async function samplesFor(env: StudioEnv, message: string, prove = true) {
  const gen = GENERATORS[message];
  if (!gen) return { message, samples: [], note: 'No generator for this message yet' };
  const samples = gen();
  const proofs = prove ? await Promise.all(samples.map((s) => proveSample(env, s))) : [];
  return { message, samples: samples.map((s, i) => ({ ...s, proof: proofs[i] })) };
}
