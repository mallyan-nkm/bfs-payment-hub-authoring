// The documents of `uae.demo`, a synthetic scheme made up for the end-to-end test. It stands for no
// real scheme. Everything here is what a designer would type into the studio.

export const S = 'uae.demo';
export const MAKER = 'shared.designer';
export const CHECKER = 'shared.checker';
export const BANK = 'example-bank';
export const PROV = { origin: 'authored', status: 'provisional', clauses: ['Synthetic demonstration scheme; no rulebook'], note: 'Built from scratch in the end-to-end test.' };
export const HEADER = { paymentId: 'P-1', endToEndId: 'DEMO-0001', fileId: 'DEMO-0001', kind: 'payment', direction: 'outward', segment: 'corporate', scheme: S };
export const n = (type: string, name: string) => `${S}.${type}.${name}.v1`;

export const PORT = { kind: 'port', name: n('port', 'demo'), release: '1.0.0', title: 'Demo clearing house', party: 'demonstration clearing house', operations: [{ name: 'submit-file', style: 'send' }, { name: 'receive-file', style: 'receive' }], provenance: PROV };
export const reply = (status: string, extra: any = {}) => [{ expect: `${S}.file-submission`, ...(extra.failTimes ? { failTimes: extra.failTimes } : {}) }, ...(status === 'none' ? [{ noReply: true }] : [{ send: `${S}.ack`, status, after: extra.after ?? 'PT5M', ...(extra.duplicate ? { duplicate: true } : {}) }])];
export const SIM = {
  kind: 'simulator', name: n('sim', 'demo-outward'), release: '1.0.0', title: 'Demo clearing house, replies to the bank', direction: 'outward', port: PORT.name, standsFor: 'demonstration clearing house', state: { keyedBy: 'message id' },
  scenarios: [
    { name: 'accept', steps: reply('accepted') },
    { name: 'reject', steps: [{ expect: `${S}.file-submission` }, { send: `${S}.ack`, status: 'rejected', reasonCode: 'AC03', after: 'PT5M' }] },
    { name: 'pending-then-final', steps: [{ expect: `${S}.file-submission` }, { send: `${S}.ack`, status: 'pending', after: 'PT5M' }, { send: `${S}.ack`, status: 'accepted', after: 'PT1H' }] },
    { name: 'no-reply', steps: reply('none') },
    { name: 'late-reply', steps: reply('accepted', { after: 'P2D' }) },
    { name: 'duplicate-reply', steps: reply('accepted', { duplicate: true }) },
    { name: 'fail-twice-then-accept', steps: reply('accepted', { failTimes: 2 }) },
    { name: 'malformed-reply', malformed: true, steps: reply('accepted') },
  ],
  implementation: { binding: 'builtin', service: 'simulator-runtime' },
  provenance: PROV,
};
export const RULE = {
  kind: 'rule', name: n('rule', 'currency-aed'), release: '1.0.0', title: 'A demo file is in dirhams', ruleKind: 'field', level: 'batch', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'Batch', as: 'b', where: "b.currency != 'AED'" } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'batch.currency', reasonCode: 'AM03' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { Batch: { id: 'B1', currency: 'AED' } }, expect: { findings: [] } },
    { name: 'fails', evaluationDate: '2026-11-17', given: { Batch: { id: 'B1', currency: 'USD' } }, expect: { findings: [{ reasonCode: 'AM03' }] } },
  ],
  provenance: PROV,
};
export const RULESET = { kind: 'ruleset', name: n('ruleset', 'file'), release: '1.0.0', title: 'Demo file checks', mode: 'validation', stages: ['field'], rules: [RULE.name], provenance: PROV };
export const VALIDATE = { kind: 'activity', name: n('validate', 'file'), release: '1.0.0', title: 'Demo file checks', type: 'validate', level: 'batch', reads: ['batch', 'items[]'], writes: ['facts.validation'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure', ruleSets: [RULESET.name], implementation: { binding: 'builtin', service: 'rule-service' }, provenance: PROV };
export const FILE_IN = { kind: 'activity', name: n('map', 'file-in'), release: '1.0.0', title: 'Demo credit file to the canonical batch', type: 'map', reads: ['messages'], writes: ['header', 'batch', 'items[]'], outcomes: [{ name: 'mapped', primary: true }, { name: 'unmappable' }], effect: 'pure', map: { direction: 'in', sourceModel: 'Demo credit file: H and D records', roundTrip: false }, implementation: { binding: 'script', language: 'typescript', script: `scripts/${n('map', 'file-in')}.ts` }, provenance: PROV };
export const FILE_IN_SCRIPT = `// The demo credit file: one H record (file reference, debtor routing code, currency, count, total)
// and a D record per credit (IBAN, amount). The map reads the file; the dispatch table says what
// payment it starts (R-05-42).
export async function run({ ctx }) {
  const text = String(ctx.messages?.inbound?.payload ?? '');
  const ls = text.split(/\\r?\\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
  const h = ls.filter((l) => l.startsWith('H,'));
  const d = ls.filter((l) => l.startsWith('D,'));
  if (h.length !== 1 || d.length === 0) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const [, fileRef, routing, currency, count, total] = h[0].split(',');
  ctx.header = { ...(ctx.header ?? {}), fileId: fileRef, endToEndId: fileRef };
  ctx.batch = { id: fileRef, numberOfTransactions: Number(count), controlSum: Number(total), currency, debtorAgent: { clearingMemberId: routing } };
  ctx.items = d.map((l) => {
    const [, iban, amount] = l.split(',');
    return { payment: { creditorAccount: { iban }, instructedAmount: { value: Number(amount), currency } } };
  });
  return 'mapped';
}
`;
export const FILE_OUT = { kind: 'activity', name: n('map', 'file-out'), release: '1.0.0', title: 'Canonical batch to the demo submission', type: 'map', reads: ['batch', 'items[]'], writes: ['messages'], outcomes: [{ name: 'mapped', primary: true }, { name: 'unmappable' }], effect: 'pure', map: { direction: 'out', sourceModel: 'Demo submission file', roundTrip: false }, implementation: { binding: 'builtin', service: 'reference' }, provenance: PROV };
export const ACK_IN = { kind: 'activity', name: n('map', 'ack-in'), release: '1.0.0', title: 'Demo acknowledgement to status', type: 'map', reads: ['messages'], writes: ['status'], outcomes: [{ name: 'ack', primary: true }, { name: 'nak' }], effect: 'pure', map: { direction: 'in', sourceModel: 'Demo acknowledgement', roundTrip: true }, implementation: { binding: 'builtin', service: 'reference' }, provenance: PROV };
export const SEND = {
  kind: 'activity', name: n('exchange', 'send-file'), release: '1.0.0', title: 'Send the demo file', type: 'exchange', mode: 'send', reads: ['header', 'messages'], writes: ['status', 'messages'],
  outcomes: [{ name: 'accepted', primary: true }, { name: 'rejected' }, { name: 'unknown' }], errors: [{ name: 'TransientError', retryable: true }, { name: 'BusinessReject', retryable: false }],
  effect: 'external', idempotencyKey: "concat(header.paymentId, ':send-file')", ports: [PORT.name],
  policy: { attemptTimeout: 'PT10S', totalTimeout: 'PT1M', retry: { maxAttempts: 3, firstInterval: 'PT2S', backoff: 2, nonRetryable: ['BusinessReject'] } },
  implementation: { binding: 'builtin', service: 'reference' }, provenance: PROV,
};
export const SUBMIT = {
  kind: 'activity', name: n('exchange', 'submit-file'), release: '1.0.0', title: 'Submit the demo file and read the acknowledgement', type: 'exchange', mode: 'send', reads: ['header', 'messages'], writes: ['status', 'messages'],
  outcomes: [{ name: 'accepted', primary: true }, { name: 'rejected' }, { name: 'pending' }], errors: [{ name: 'TransientError', retryable: true }, { name: 'BusinessReject', retryable: false }],
  effect: 'external', idempotencyKey: "concat(header.paymentId, ':submit-file')", ports: [PORT.name], policy: { replyTimeout: 'P1D', replyCalendar: 'business' },
  implementation: { binding: 'subflow', subflow: `${S}.sub.submit-file.v1` }, provenance: PROV,
};
export const SUBFLOW = {
  kind: 'workflow', wfKind: 'subflow', name: `${S}.sub.submit-file.v1`, release: '1.0.0', dsl: '1.0.0', title: 'Submit the demo file and read the acknowledgement',
  description: 'Sends the file, waits for the acknowledgement for as long as the calling step allows, and reads it.',
  implements: { activity: SUBMIT.name }, exit: { outcomes: ['accepted', 'rejected', 'pending'] },
  // A file scheme's file is encrypted for its recipient before it is sent (L46, R-12-17).
  do: [
    { id: 'encrypt', call: 'p.protect.encrypt-file.v1', handle: { refused: { return: 'rejected' } } },
    { id: 'send', call: SEND.name, handle: { rejected: { return: 'rejected' }, unknown: { return: 'pending' } } },
    { id: 'await-ack', await: { message: `${S}.ack`, correlateBy: 'header.fileId', onTimeout: { return: 'pending' } } },
    { id: 'read-ack', call: ACK_IN.name, handle: { nak: { return: 'rejected' } } },
    { return: 'accepted' },
  ],
  provenance: PROV,
};
export const LEG = {
  kind: 'workflow', wfKind: 'leg', name: n('wf', 'credit-file-out'), release: '1.0.0', dsl: '1.0.0', title: 'Demo credit file, bank to clearing house',
  description: 'The scheme leg of a demo credit file: checks, an IBAN check per credit, render, submit.',
  direction: 'outward', implements: { slot: 'scheme-leg', of: ['p.wf.corporate.v1'] },
  entry: { reads: ['header', 'batch', 'items[]', 'facts.route', 'facts.reservation'] },
  exit: { writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'pending'] },
  do: [
    { id: 'business-date', call: 'uae.p.enrich.business-date.v1' },
    { id: 'file-checks', call: VALIDATE.name, handle: { reject: { end: 'rejected' } } },
    { forEach: { in: 'items[]', onItemFailure: 'stop', do: [{ id: 'record-iban', call: 'uae.p.validate.iban.v1', level: 'item', handle: { reject: { end: 'rejected' } } }] } },
    { id: 'render', call: FILE_OUT.name, handle: { unmappable: { end: 'rejected' } } },
    { id: 'submit', call: SUBMIT.name, handle: { rejected: { end: 'rejected' }, pending: { end: 'pending' } } },
    { end: 'settled' },
  ],
  provenance: PROV,
};
// The scheme manifest: its dispatch table routes each message. The credit file starts a payment
// with the new leg in the corporate workflow's slot; the acknowledgement is delivered to it.
export const SCHEME = {
  kind: 'scheme', scope: S, release: '1.0.0', title: 'Demo bulk credit file scheme (synthetic)', wire: 'file',
  messages: [
    { id: `${S}.credit-file`, title: 'Credit file from the corporate customer', direction: 'in', confirmed: true, disposition: { start: { kind: 'payment', direction: 'outward', segment: 'corporate', leg: LEG.name, intakeMap: FILE_IN.name } } },
    { id: `${S}.file-submission`, title: 'Credit file submitted to the clearing house', direction: 'out', confirmed: true, disposition: { sentBy: SEND.name } },
    { id: `${S}.ack`, title: 'Acknowledgement from the clearing house', direction: 'in', confirmed: true, disposition: { deliver: { correlateBy: 'file id' } } },
  ],
  legs: [LEG.name],
  ports: [PORT.name],
  provenance: PROV,
};

export const VALID = '# file: DEMO-0001.csv\nH,DEMO-0001,302620122,AED,2,1500.00\nD,AE070331234567890123456,1000.00\nD,AE970336543210987654321,500.00\n';
export const USD = '# file: DEMO-0002.csv\nH,DEMO-0002,302620122,USD,1,1000.00\nD,AE070331234567890123456,1000.00\n';
export const GARBAGE = 'this is not a demo file\n';

export const bench = (scenario: string, platform = false) => [
  { port: PORT.name, simulator: SIM.name, scenario },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'p.port.encryption.v1', simulator: 'p.sim.encryption.v1', scenario: 'accept' },
  ...(platform ? ['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })) : []),
];
export const T = (id: string, title: string, target: string, level: string, given: any, expect: any, b: any[] = [], tenant?: string) => ({ kind: 'test', id, title, target, level, evaluationDate: '2026-11-17', ...(tenant ? { tenant } : {}), ...(b.length ? { bench: b } : {}), given, expect });
export const TESTS = {
  validate: [
    T('DEMO-VAL-001', 'A file in dirhams passes', VALIDATE.name, 'activity', { context: { header: HEADER, batch: { id: 'B1', currency: 'AED' }, items: [] } }, { outcome: 'pass', findings: [] }),
    T('DEMO-VAL-002', 'A file in dollars is rejected', VALIDATE.name, 'activity', { context: { header: HEADER, batch: { id: 'B1', currency: 'USD' }, items: [] } }, { outcome: 'reject', findings: [{ reasonCode: 'AM03' }] }),
  ],
  fileIn: [
    T('DEMO-MAP-001', 'A demo file maps', FILE_IN.name, 'activity', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'mapped' }),
    T('DEMO-MAP-002', 'Something that is not a demo file does not', FILE_IN.name, 'activity', { inbound: 'samples/demo-credit-file-garbage.csv' }, { outcome: 'unmappable', reasonCode: 'FF01' }),
  ],
  submit: [
    T('DEMO-EXC-001', 'The composite alone: sent, acknowledged, read', SUBMIT.name, 'activity', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'accepted', detail: ['encrypt:encrypted', 'send:accepted', 'await-ack:received', 'read-ack:ack'] }, bench('accept')),
    T('DEMO-EXC-002', 'The composite alone: refused', SUBMIT.name, 'activity', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'rejected', detail: ['encrypt:encrypted', 'send:accepted', 'await-ack:received', 'read-ack:nak'] }, bench('reject')),
    T('DEMO-EXC-003', 'The composite alone: no acknowledgement within a business day', SUBMIT.name, 'activity', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'pending' }, bench('no-reply')),
  ],
  leg: [
    T('DEMO-OUT-001', 'The demo file is acknowledged', LEG.name, 'leg', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'settled', trace: ['business-date:set', 'file-checks:pass', 'record-iban[0]:pass', 'record-iban[1]:pass', 'render:mapped', 'submit:accepted', 'end:settled'], detail: ['submit.encrypt:encrypted', 'submit.send:accepted', 'submit.await-ack:received', 'submit.read-ack:ack'] }, bench('accept')),
    T('DEMO-OUT-002', 'Refused by the clearing house', LEG.name, 'leg', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'rejected', reasonCode: 'AC03' }, bench('reject')),
    T('DEMO-OUT-003', 'A file in dollars is rejected before anything is sent', LEG.name, 'leg', { inbound: 'samples/demo-credit-file-usd.csv' }, { outcome: 'rejected', reasonCode: 'AM03', trace: ['business-date:set', 'file-checks:reject', 'end:rejected'], messages: [{ port: PORT.name, message: `${S}.file-submission`, count: 0 }] }, bench('accept')),
    T('DEMO-OUT-004', 'No acknowledgement within a business day is pending', LEG.name, 'leg', { inbound: 'samples/demo-credit-file-valid.csv' }, { outcome: 'pending' }, bench('no-reply')),
  ],
  e2e: [T('DEMO-E2E-001', 'A demo file from the channel is paid end to end', 'p.wf.corporate.v1', 'end-to-end', { inbound: 'samples/demo-credit-file-valid.csv', message: `${S}.credit-file` }, { outcome: 'settled', messages: [{ port: PORT.name, message: `${S}.file-submission`, count: 1 }] }, bench('accept', true), BANK)],
};


/** The next minor of a release, as the configuration set's next release is made (I-187, I-204): 1.8.0 gives 1.9.0. */
export const nextMinor = (release: string) => release.replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);
