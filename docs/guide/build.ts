// Builds docs/guide/studio-guide.html from template.html. The documents of part 6 come from the
// browser walk-through's own sources (studio/test/blackbox/demo-scheme.ts), so the guide shows
// exactly what that test builds. Run from studio/: npx tsx ../docs/guide/build.ts
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { toYaml } from '../../studio/src/core/yaml.js';
import * as demo from '../../studio/test/blackbox/demo-scheme.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n$/, '');

const TRAINING_RULE = {
  kind: 'rule', name: 'p.rule.training-amount-positive.v1', release: '1.0.0', title: 'Training rule, the amount is positive', ruleKind: 'field', level: 'payment', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'Payment', as: 'p', where: 'p.amount <= 0' } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'payment.amount', reasonCode: 'AM12' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { Payment: { amount: 1 } }, expect: { findings: [] } },
    { name: 'fails', evaluationDate: '2026-11-17', given: { Payment: { amount: 0 } }, expect: { findings: [{ reasonCode: 'AM12' }] } },
  ],
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Training exercise'] },
};

// Exercise 9a: a check written as a script, with no port, so it is pure (L57), run on the bench as a draft (I-341).
const TRAINING_ACTIVITY = {
  kind: 'activity', name: 'p.validate.training-period.v1', release: '1.0.0', title: 'Training check, the period is a year and a month', type: 'validate',
  reads: ['header'], writes: ['facts.period'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }],
  effect: 'pure', implementation: { binding: 'script', language: 'typescript', script: 'scripts/p.validate.training-period.v1.ts' },
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Training exercise'] },
};
const TRAINING_SCRIPT = [
  'export async function run({ ctx }) {',
  "  const ok = /^\\d{4}-\\d{2}$/.test(String(ctx.header.period ?? ''));",
  '  ctx.facts = { ...(ctx.facts ?? {}), period: { ok } };',
  "  return ok ? 'pass' : { outcome: 'reject', reasonCode: 'DT01' };",
  '}',
  '',
].join('\n');

const fill: Record<string, string> = {
  TRAINING_RULE: toYaml(TRAINING_RULE), TRAINING_ACTIVITY: toYaml(TRAINING_ACTIVITY), TRAINING_SCRIPT,
  VALID: demo.VALID, USD: demo.USD, GARBAGE: demo.GARBAGE,
  PORT: toYaml(demo.PORT), SIM: toYaml(demo.SIM), RULE: toYaml(demo.RULE), RULESET: toYaml(demo.RULESET), VALIDATE: toYaml(demo.VALIDATE),
  FILE_IN: toYaml(demo.FILE_IN), FILE_IN_SCRIPT: demo.FILE_IN_SCRIPT, FILE_OUT: toYaml(demo.FILE_OUT), ACK_IN: toYaml(demo.ACK_IN), SEND: toYaml(demo.SEND),
  SUBMIT: toYaml(demo.SUBMIT), SUBFLOW: toYaml(demo.SUBFLOW), LEG: toYaml(demo.LEG), SCHEME: toYaml(demo.SCHEME),
};

let html = fs.readFileSync(path.join(here, 'template.html'), 'utf8');
html = html.replace(/\{\{([A-Z_]+)\}\}/g, (_, k) => {
  if (!(k in fill)) throw new Error(`No content for {{${k}}}`);
  return esc(fill[k]);
});
const out = path.join(here, 'studio-guide.html');
fs.writeFileSync(out, html);
console.log(`${out}: ${html.length} bytes`);
