// WPS salary information file (SIF) to the canonical batch, release 1.3.0. Variants: SCR first or SCR last.
// Field mapping as declared in uae.wps.map.sif-in.v1. Runs the edge rule the map names.
//
// 1.1.0 (Issue I-100) reads two things the 1.0.0 script did not:
// - the EVP record of a line (EVP,<person id>,<routing code>,<housing>,<conveyance>,<medical>,<annual passage>,<overtime>,
//   <other>,<leave encashment>), matched to its EDR by the person id, as the line's allowance fields;
// - what the employer's channel declared beside the file at upload, given as fixture lines before the records
//   (`# channel:`, `# initiated-by:`, `# debtor-name:`, `# debtor-account:`, like `# file:`): the upload channel, the
//   initiating party's id (GrpHdr/InitgPty, an id only), the employer's name (Dbtr/Nm) and its debit account (DbtrAcct).
// 1.2.0 (Issue I-115) writes each line's account type and its bank's name:
// - the account type (CdtrAcct/Tp) where the channel declared one at upload (`# account-type:`); the SIF itself has none;
// - the name of the line's bank (CdtrAgt/FinInstnId/Nm) from the scheme's participant directory, by the line's routing code
//   (uae.wps.table.participant-names.v1, a value looked up by date); a routing code the directory does not list gets no name.
// 1.3.0 (R-12-27) writes the employer's establishment id as the debtor's organisation id (Dbtr/Id/OrgId/Othr/Id, scheme MOL),
// by which the bank's party register is asked (p.enrich.party-register.v1).
// Amounts are exact (R-02-17, Issue I-109): each is written as a decimal string with two places, never through a binary
// double; a line's amount is the exact sum of its fixed and variable pay.

const HUNDRED = BigInt(100);
const ZERO = BigInt(0);
const UPLOAD: Record<string, string> = { channel: 'channel', 'initiated-by': 'initiatedBy', 'debtor-name': 'debtorName', 'debtor-account': 'debtorAccount', 'account-type': 'accountType' };

function lines(text: string) {
  let fileName: string | undefined;
  const upload: Record<string, string> = {};
  const out: string[] = [];
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.trim();
    if (!l) continue;
    if (l.startsWith('#')) {
      const m = /^#\s*([a-z-]+):\s*(.+?)\s*$/.exec(l);
      if (m && m[1] === 'file') fileName = m[2].split(/\s+/)[0];
      else if (m && UPLOAD[m[1]]) upload[UPLOAD[m[1]]] = m[2];
      continue;
    }
    out.push(l);
  }
  return { fileName, upload, lines: out };
}

/** An amount as its exact decimal: two places, in minor units, from the file's own text. */
function minor(s: string): bigint | null {
  const m = /^\s*(-?)(\d+)(?:\.(\d{1,2}))?\s*$/.exec(s ?? '');
  if (!m) return null;
  const v = BigInt(m[2]) * HUNDRED + BigInt((m[3] ?? '').padEnd(2, '0') || '0');
  return m[1] ? -v : v;
}

/** Minor units back to an amount, as a decimal string with two places (R-02-17, Issue I-109). */
function amount(v: bigint): string {
  const neg = v < ZERO;
  const a = neg ? -v : v;
  return `${neg ? '-' : ''}${a / HUNDRED}.${String(a % HUNDRED).padStart(2, '0')}`;
}

export async function run({ ctx, rules }) {
  const inbound = ctx.messages?.inbound;
  const text: string = inbound?.payload ?? '';
  if (!text || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const { fileName, upload, lines: ls } = lines(text);
  const scr = ls.filter((l) => l.startsWith('SCR,'));
  const edr = ls.filter((l) => l.startsWith('EDR,'));
  const evp = ls.filter((l) => l.startsWith('EVP,'));
  if (scr.length !== 1 || edr.length === 0) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const variant = ls[0].startsWith('SCR,') ? 'scr-first' : ls[ls.length - 1].startsWith('SCR,') ? 'scr-last' : null;
  if (!variant) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const s = scr[0].split(',');
  if (s.length < 9) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const [, employerId, employerRoutingCode, creationDate, creationTime, salaryMonth, recordCount, totalSalary, currency, employerRef] = s;
  const total = minor(totalSalary);
  if (total === null) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const fileId = (fileName ?? `${employerId}-${creationDate}-${creationTime}`).replace(/\.sif$/i, '');
  // The dispatch table says what payment this file starts (an outward corporate payment); the map
  // only reads the file and what its channel declared with it (R-05-42).
  ctx.header = { ...(ctx.header ?? {}), fileId, endToEndId: fileId };
  ctx.batch = {
    id: fileId,
    numberOfTransactions: Number(recordCount),
    controlSum: amount(total),
    currency,
    categoryPurpose: 'SALA',
    debtorAgent: { clearingMemberId: employerRoutingCode },
    ...(upload.initiatedBy ? { initiatingParty: { id: upload.initiatedBy } } : {}),
    debtor: { ...(upload.debtorName ? { name: upload.debtorName } : {}), organisationId: { id: employerId, scheme: 'MOL' } },
    ...(upload.debtorAccount ? { debtorAccount: { iban: upload.debtorAccount } } : {}),
  };
  ctx.ext = { uae: { wps: {
    employerId, fileCreationDate: creationDate, fileCreationTime: creationTime, salaryMonth,
    ...(employerRef ? { employerReference: employerRef } : {}),
    ...(upload.channel ? { uploadChannel: upload.channel } : {}),
  } } };
  // EVP records by person id: the allowances of the line with the same person id.
  const allowances = new Map<string, Record<string, string>>();
  const names = ['housingAllowance', 'conveyanceAllowance', 'medicalAllowance', 'annualPassageAllowance', 'overtimeAllowance', 'otherAllowances', 'leaveEncashment'];
  for (const line of evp) {
    const f = line.split(',');
    if (f.length < 10) return { outcome: 'unmappable', reasonCode: 'FF01' };
    const values: Record<string, string> = {};
    for (let i = 0; i < names.length; i++) {
      const v = minor(f[3 + i]);
      if (v === null) return { outcome: 'unmappable', reasonCode: 'FF01' };
      values[names[i]] = amount(v);
    }
    allowances.set(f[1], values);
  }
  const items = [];
  for (const line of edr) {
    const f = line.split(',');
    if (f.length < 10) return { outcome: 'unmappable', reasonCode: 'FF01' };
    const [, personId, agentRoutingCode, iban, payStartDate, payEndDate, days, fixed, variable, leave] = f;
    const fixedPay = minor(fixed);
    const variablePay = minor(variable);
    if (fixedPay === null || variablePay === null) return { outcome: 'unmappable', reasonCode: 'FF01' };
    items.push({
      payment: {
        creditorAgent: { clearingMemberId: agentRoutingCode },
        creditorAccount: { iban, ...(upload.accountType ? { type: upload.accountType } : {}) },
        instructedAmount: { value: amount(fixedPay + variablePay), currency },
      },
      ext: { uae: { wps: {
        personId, payStartDate, payEndDate, daysInPeriod: Number(days), fixedPay: amount(fixedPay), variablePay: amount(variablePay), leaveDays: Number(leave),
        ...(allowances.get(personId) ?? {}),
      } } },
    });
  }
  // Each line's bank by its routing code, from the participant directory in force.
  const directory: any[] = rules.table('uae.wps.table.participant-names.v1').decisions.find((d: any) => d.name === 'directory')?.value ?? [];
  const nameOf = new Map<string, string>(directory.filter((p: any) => p && p.routingCode && p.name).map((p: any) => [String(p.routingCode), String(p.name)]));
  for (const it of items as any[]) {
    const name = nameOf.get(it.payment.creditorAgent.clearingMemberId);
    if (name) it.payment.creditorAgent.name = name;
  }
  ctx.items = items;
  // The edge rule the map names: the file name agrees with the control record.
  const edge = rules.run('uae.wps.rule.file-name-matches-control.v1', {
    SourceFile: [{ name: fileName ?? '', size: text.length }],
    WpsBatch: [ctx.ext.uae.wps],
  });
  if (edge.worst === 'reject') return { outcome: 'unmappable', reasonCode: edge.findings[0]?.reasonCode };
  return 'mapped';
}
