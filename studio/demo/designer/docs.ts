// The documents the designer and developer video (demo/designer.ts) builds, kept in one place so that the video and its dry
// run build exactly the same things. Every value is synthetic.

const PROV = { origin: 'authored', status: 'provisional', clauses: ['Training example; not a scheme rule'] };

// ---------------------------------------------------------------- a rule, written in the form

export const MONTH_RULE = {
  name: 'uae.wps.rule.salary-month-valid.v1',
  title: 'The salary month is a real month',
  where: "not matches(w.salaryMonth, '^(0[1-9]|1[0-2])[0-9]{4}$')",
  path: 'ext.uae.wps.salaryMonth',
  code: 'DT01',
};

// ---------------------------------------------------------------- an activity that needs a script

/** The contract: what the activity reads, writes and ends with. The behaviour is the script below. */
export const PERIOD_ACTIVITY = {
  kind: 'activity', name: 'uae.wps.validate.pay-period.v1', release: '1.0.0', title: 'Each line is paid for a period inside the salary month',
  description: 'Each line\'s pay period starts and ends inside the file\'s salary month, and its days paid are the days from start to end.',
  type: 'validate', level: 'batch', reads: ['ext.uae.wps', 'items[]'], writes: ['facts.validation'],
  outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'read',
  implementation: { binding: 'script', language: 'typescript', script: 'scripts/uae.wps.validate.pay-period.v1.ts' },
  provenance: PROV,
};

/** A first try that the studio refuses: it reads the system clock. */
export const PERIOD_SCRIPT_CLOCK = `export async function run({ ctx }) {
  const today = new Date();
  return 'pass';
}
`;

/** The script-behind, typed in the studio's editor. Dates are ISO text, so the check needs no date library and no clock. */
export const PERIOD_SCRIPT = `// Each line's pay period must lie inside the file's salary month (MMYYYY),
// start on or before its end, and pay the days from start to end, both included.
export async function run({ ctx, log }) {
  const month = String(ctx.ext?.uae?.wps?.salaryMonth ?? '');
  const yearMonth = month.slice(2) + '-' + month.slice(0, 2);
  const findings = [];
  (ctx.items ?? []).forEach((item, i) => {
    const line = item?.ext?.uae?.wps ?? {};
    const start = String(line.payStartDate ?? '');
    const end = String(line.payEndDate ?? '');
    const path = 'items[' + i + '].ext.uae.wps';
    if (start.slice(0, 7) !== yearMonth || end.slice(0, 7) !== yearMonth || start > end) {
      findings.push({ severity: 'reject', path: path + '.payStartDate', reasonCode: 'DT01',
        message: 'The pay period is not inside the salary month' });
      return;
    }
    const days = Number(end.slice(8)) - Number(start.slice(8)) + 1;
    if (Number(line.daysInPeriod) !== days) {
      findings.push({ severity: 'reject', path: path + '.daysInPeriod', reasonCode: 'DT01',
        message: 'The days paid are not the days of the period' });
    }
  });
  const worst = findings.length ? 'reject' : 'pass';
  ctx.facts = { ...(ctx.facts ?? {}), validation: { worst, findings } };
  log.info('pay periods checked', { lines: (ctx.items ?? []).length, findings: findings.length });
  return findings.length ? 'reject' : 'pass';
}
`;

export const HEADER = { paymentId: 'P-1', endToEndId: 'E2E-1', kind: 'payment', direction: 'outward', segment: 'corporate', scheme: 'uae.wps' };
const line = (days: number) => ({ ext: { uae: { wps: { payStartDate: '2026-10-01', payEndDate: '2026-10-31', daysInPeriod: days } } } });
/** A small context for the bench: one salary file for October 2026 with two lines; the second pays `days`. */
export const periodContext = (days: number) => JSON.stringify({ header: HEADER, ext: { uae: { wps: { salaryMonth: '102026' } } }, items: [line(31), line(days)] }, null, 2);

/** A synthetic salary file whose second line pays 30 days for a 31-day period. */
export const BAD_PERIOD_SAMPLE = `# file: 0000001234567261117083100.sif
SCR,0000001234567,302620122,2026-11-17,0831,102026,2,1500.00,AED,REF-0002
EDR,00001234567890,203320101,AE070331234567890123456,2026-10-01,2026-10-31,31,800.00,200.00,0
EDR,00001234567891,203320101,AE970336543210987654321,2026-10-01,2026-10-31,30,400.00,100.00,0
`;
export const BAD_PERIOD_SAMPLE_NAME = 'wps-sif-bad-period.sif';

// ---------------------------------------------------------------- a leg that uses it

/** A WPS leg: the shipped steps of a salary file, with the new pay period check after the file checks. */
export const PERIOD_LEG_HEADER = {
  kind: 'workflow', wfKind: 'leg', name: 'uae.wps.wf.salary-file-periods.v1', release: '1.0.0', dsl: '1.0.0',
  title: 'Salary file, with a pay period check',
  description: 'Composed on the canvas in the designer and developer video from blocks that exist and one written there.',
  direction: 'outward', implements: { slot: 'scheme-leg', of: ['p.wf.corporate.v1'] },
  entry: { reads: ['header', 'batch', 'items[]', 'ext.uae.wps', 'facts.reservation'] },
  exit: { writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'returned', 'pending'] },
  do: [{ end: 'settled' }],
  provenance: PROV,
};
export const PERIOD_LEG = {
  ...PERIOD_LEG_HEADER,
  do: [
    { id: 'business-date', call: 'uae.p.enrich.business-date.v1' },
    { id: 'file-checks', call: 'uae.wps.validate.salary-file.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } },
    { id: 'pay-period', call: PERIOD_ACTIVITY.name, handle: { reject: { end: 'rejected' } } },
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
