// The documents the hands-on training video builds, kept in one place so that the video and its dry
// run build exactly the same things. All values are synthetic.

const PROV = { origin: 'authored', status: 'provisional', clauses: ['Training example; not a scheme rule'] };
const BANK_PROV = { origin: 'authored', status: 'confirmed', clauses: ['Example bank policy for training'] };

// 2.1 and 2.2: a rule, its rule set and an activity, in the WPS scope (shared content).
export const MONTH_RULE = {
  name: 'uae.wps.rule.salary-month-valid.v1',
  title: 'The salary month is a real month',
  where: "not matches(w.salaryMonth, '^(0[1-9]|1[0-2])[0-9]{4}$')",
  path: 'ext.uae.wps.salaryMonth',
  code: 'DT01',
};
/** The whole rule, as the form builds it. */
export const MONTH_RULE_DOC = {
  kind: 'rule', name: MONTH_RULE.name, release: '1.0.0', title: MONTH_RULE.title, ruleKind: 'field', level: 'batch', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'WpsBatch', as: 'w', where: MONTH_RULE.where } }],
  then: [{ yield: { finding: { severity: 'reject', path: MONTH_RULE.path, reasonCode: MONTH_RULE.code } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '102026' } }, expect: { findings: [] } },
    { name: 'fails', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '132026' } }, expect: { findings: [{ reasonCode: MONTH_RULE.code }] } },
    { name: 'boundary, December', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '122026' } }, expect: { findings: [] } },
  ],
  provenance: PROV,
};
export const MONTH_RULESET = {
  kind: 'ruleset', name: 'uae.wps.ruleset.salary-month.v1', release: '1.0.0', title: 'Salary month checks', mode: 'validation', stages: ['field'],
  rules: [MONTH_RULE.name], provenance: PROV,
};
export const MONTH_ACTIVITY = {
  name: 'uae.wps.validate.salary-month.v1', title: 'Salary month check', ruleset: MONTH_RULESET.name,
};
export const MONTH_ACTIVITY_DOC = {
  kind: 'activity', name: MONTH_ACTIVITY.name, release: '1.0.0', title: MONTH_ACTIVITY.title, type: 'validate', level: 'batch',
  reads: ['ext.uae.wps'], writes: ['facts.validation'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure',
  ruleSets: [MONTH_RULESET.name], implementation: { binding: 'builtin', service: 'rule-service' }, provenance: PROV,
};
export const HEADER = { paymentId: 'P-1', endToEndId: 'E2E-1', kind: 'payment', direction: 'outward', segment: 'corporate', scheme: 'uae.wps' };
export const contextWithMonth = (month: string) => JSON.stringify({ header: HEADER, ext: { uae: { wps: { salaryMonth: month } } } }, null, 2);

// 2.3: what the AI assistant is asked for.
export const AI_INSTRUCTION = 'Write a field rule named uae.wps.rule.salary-days-valid.v1 at level item. It checks that each salary line\'s days in the period is between 1 and 31. When it does not hold, yield a reject finding on items[].ext.uae.wps.daysInPeriod with ISO reason code DT01. Include a passing example and a failing example. Cite the clause each part comes from. Anything the source text does not say, ask as a question; do not guess.';
export const AI_SOURCE = 'Synthetic training extract. Each employee record in a salary information file states the number of days in the pay period. The value is a whole number from 1 to 31. A record outside that range makes the file invalid.';

// 2.4: a leg composed from blocks that already exist. Only its header is written as text; the steps
// are added on the canvas.
export const COMPOSED_LEG_HEADER = {
  kind: 'workflow', wfKind: 'leg', name: 'uae.wps.wf.salary-file-checked.v1', release: '1.0.0', dsl: '1.0.0',
  title: 'Salary file, with a salary month check',
  description: 'Composed on the canvas in the training video from blocks that already exist.',
  direction: 'outward', implements: { slot: 'scheme-leg', of: ['p.wf.corporate.v1'] },
  entry: { reads: ['header', 'batch', 'items[]', 'ext.uae.wps', 'facts.reservation'] },
  exit: { writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'returned', 'pending'] },
  do: [{ end: 'settled' }],
  provenance: PROV,
};

// 2.6: a bank's own rule, rule set, check and hook: hold a salary file with more lines than the bank allows.
export const BANK_RULE = {
  kind: 'rule', name: 'b.rule.file-size.v1', release: '1.0.0', title: 'A file has no more lines than the bank allows', ruleKind: 'policy', level: 'batch',
  parameters: { maxLines: { type: 'integer', default: 1000, bounds: { min: 1, max: 100000 }, description: 'The most lines a salary file may have before it needs approval.' } },
  repeatability: 'non-repeatable',
  when: [
    { match: { fact: 'Param', as: 'maxLines', where: "maxLines.name == 'maxLines'" } },
    { match: { fact: 'Batch', as: 'b' } },
    { having: 'b.numberOfTransactions > maxLines.value' },
  ],
  then: [{ yield: { finding: { severity: 'reject', path: 'batch.numberOfTransactions', reasonCode: 'AM18', message: 'More lines than the bank allows without approval' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { Batch: { id: 'B1', numberOfTransactions: 2 }, Param: { name: 'maxLines', value: 5 } }, expect: { findings: [] } },
    { name: 'boundary, exactly the limit', evaluationDate: '2026-11-17', given: { Batch: { id: 'B1', numberOfTransactions: 5 }, Param: { name: 'maxLines', value: 5 } }, expect: { findings: [] } },
    { name: 'fails', evaluationDate: '2026-11-17', given: { Batch: { id: 'B1', numberOfTransactions: 6 }, Param: { name: 'maxLines', value: 5 } }, expect: { findings: [{ reasonCode: 'AM18' }] } },
  ],
  provenance: BANK_PROV,
};
export const BANK_RULESET = { kind: 'ruleset', name: 'b.ruleset.file-size.v1', release: '1.0.0', title: 'File size', mode: 'validation', stages: ['policy'], rules: [BANK_RULE.name], provenance: BANK_PROV };
export const BANK_ACTIVITY = {
  kind: 'activity', name: 'b.validate.file-size.v1', release: '1.0.0', title: 'File size check', type: 'validate', level: 'batch',
  reads: ['batch'], writes: ['facts.validation'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure',
  ruleSets: [BANK_RULESET.name], implementation: { binding: 'builtin', service: 'rule-service' }, provenance: BANK_PROV,
};
export const BANK_HOOK = {
  kind: 'hook', name: 'b.hook.large-file-approval.v1', release: '1.0.0', title: 'Hold large salary files for approval',
  implements: { hookPoint: 'after-validate', of: 'p.seg.check-and-screen.v1' }, type: 'halt', outcomes: ['continue', 'hold', 'reject'],
  do: [{ id: 'file-size', call: BANK_ACTIVITY.name, handle: { reject: { end: 'hold' } } }, { end: 'continue' }],
  provenance: BANK_PROV,
};
export const hookContext = (lines: number) => JSON.stringify({ header: HEADER, batch: { id: 'B1', numberOfTransactions: lines, currency: 'AED' } }, null, 2);

/** The whole composed leg: the canvas adds the first steps by hand and the rest is pasted. */
export const COMPOSED_LEG = {
  ...COMPOSED_LEG_HEADER,
  do: [
    { id: 'business-date', call: 'uae.p.enrich.business-date.v1' },
    { id: 'file-checks', call: 'uae.wps.validate.salary-file.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } },
    { id: 'month-check', call: MONTH_ACTIVITY.name, handle: { reject: { end: 'rejected' } } },
    { forEach: { in: 'items[]', onItemFailure: 'stop', do: [
      { id: 'record-checks', call: 'uae.wps.validate.salary-record.v1', level: 'item', handle: { reject: { end: 'rejected' } } },
      { id: 'record-iban', call: 'uae.p.validate.iban.v1', level: 'item', handle: { reject: { end: 'rejected' } } },
    ] } },
    { id: 'render', call: 'uae.wps.map.sif-out.v1', handle: { unmappable: { end: 'rejected' } } },
    { id: 'submit', call: 'uae.wps.exchange.submit-salary-file.v1', handle: { rejected: { end: 'rejected' }, pending: { end: 'pending' } } },
    { end: 'settled' },
  ],
};
export const WPS_PORTS: [string, string][] = [['uae.wps.port.wps.v1', 'uae.wps.sim.wps-outward.v1'], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1']];
