// WPS payment information file (PIF), one per agent, to the canonical batch (release 1.1.0).
//
// Two layouts. The source's (TransactHub InwardPifParser): EDR, EVP and PCR records of ten fields, whole-file all-or-nothing,
// exactly one PCR whose count and total (the sum of fixed plus variable pay, exact) match the EDRs, no duplicate employee and no
// EVP without its EDR, and the 12-character WPS file id from the end of the file name's base. The runtime's native code-behind
// is that parser (R-08-16). The factory's first layout (release 1.0.0: PDR lines and an eleven-field PCR) is still read, so a
// file mapped before keeps mapping the same way.

const money = (s: string) => Math.round(Number(s) * 100) / 100;
const AMOUNT = /^\d+(\.\d{1,4})?$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const INT = /^\d+$/;

function lines(text: string) {
  let fileName: string | undefined;
  const ls = text.split(/\r\n|\n|\r/).map((l) => l.trim()).filter((l) => {
    if (!l) return false;
    if (l.startsWith('#')) { const m = /^#\s*file:\s*(\S+)/.exec(l); if (m) fileName = m[1]; return false; }
    return true;
  });
  return { ls, fileName };
}

/** The source's layout: EDR, EVP, PCR. Returns undefined when the file is refused. */
function source(ls: string[], fileName: string | undefined, ctx: any) {
  const base = (fileName ?? '').replace(/\.[^.]*$/, '');
  if (base.length < 12) return undefined;
  const fileId = base.slice(-12);
  const edrs: string[][] = [], evps: string[][] = [], pcrs: string[][] = [];
  for (const l of ls) {
    const f = l.split(',');
    if (f[0] === 'EDR') edrs.push(f); else if (f[0] === 'EVP') evps.push(f); else if (f[0] === 'PCR') pcrs.push(f);
    else continue;
    if (f.length !== 10) return undefined;
  }
  if (pcrs.length !== 1) return undefined;
  const pcr = pcrs[0];
  for (const e of edrs) {
    if (!DATE.test(e[4]) || !DATE.test(e[5]) || !INT.test(e[6]) || !AMOUNT.test(e[7]) || !AMOUNT.test(e[8]) || !INT.test(e[9])) return undefined;
  }
  if (!DATE.test(pcr[3]) || !/^\d{4}$/.test(pcr[4]) || !/^\d{6}$/.test(pcr[5]) || !INT.test(pcr[6]) || !AMOUNT.test(pcr[7]) || !/^[A-Za-z]{3}$/.test(pcr[8])) return undefined;
  if (Number(pcr[6]) !== edrs.length) return undefined;
  // Exact at four decimals: in ten-thousandths, as integers.
  const units = (s: string) => Math.round(Number(s) * 10000);
  if (edrs.reduce((t, e) => t + units(e[7]) + units(e[8]), 0) !== units(pcr[7])) return undefined;
  const ids = new Set<string>();
  for (const e of edrs) { if (ids.has(e[1])) return undefined; ids.add(e[1]); }
  for (const v of evps) if (!ids.has(v[1])) return undefined;
  const withEvp = new Set(evps.map((v) => v[1]));
  const currency = pcr[8];
  ctx.header = { fileId, endToEndId: fileId };
  ctx.batch = {
    id: fileId, numberOfTransactions: Number(pcr[6]), controlSum: Number(pcr[7]), currency, categoryPurpose: 'SALA',
    creditorAgent: { clearingMemberId: edrs[0]?.[2] ?? '' }, debtorAgent: { clearingMemberId: pcr[2] },
  };
  ctx.ext = { uae: { wps: { employerId: pcr[1], fileCreationDate: pcr[3], fileCreationTime: pcr[4], salaryMonth: pcr[5], employerReference: pcr[9], fileName, evpCount: evps.length } } };
  ctx.items = edrs.map((e) => {
    const fixedPay = Number(e[7]);
    const variablePay = Number(e[8]);
    return {
      payment: { creditorAgent: { clearingMemberId: e[2] }, creditorAccount: { iban: e[3] }, instructedAmount: { value: money(String(fixedPay + variablePay)), currency } },
      ext: { uae: { wps: { personId: e[1], payStartDate: e[4], payEndDate: e[5], daysInPeriod: Number(e[6]), fixedPay, variablePay, leaveDays: Number(e[9]), hasVariablePay: withEvp.has(e[1]) } } },
    };
  });
  return 'mapped';
}

/** The factory's first layout: PDR lines and an eleven-field PCR (release 1.0.0). */
function first(ls: string[], fileName: string | undefined, ctx: any) {
  const pcr = ls.filter((l) => l.startsWith('PCR,'));
  const pdr = ls.filter((l) => l.startsWith('PDR,'));
  if (pcr.length !== 1) return undefined;
  const [, agentCode, employerId, , creationDate, creationTime, salaryMonth, count, total, currency] = pcr[0].split(',');
  const fileId = (fileName ?? `${agentCode}-${creationDate}-${creationTime}`).replace(/\.pif$/i, '');
  ctx.header = { fileId, endToEndId: fileId };
  ctx.batch = { id: fileId, numberOfTransactions: Number(count), controlSum: money(total), currency, categoryPurpose: 'SALA', creditorAgent: { clearingMemberId: agentCode } };
  ctx.ext = { uae: { wps: { employerId, fileCreationDate: creationDate, fileCreationTime: creationTime, salaryMonth } } };
  ctx.items = pdr.map((line) => {
    const [, personId, agent, iban, payStartDate, payEndDate, days, fixed, variable, leave] = line.split(',');
    const fixedPay = money(fixed);
    const variablePay = money(variable);
    return {
      payment: { creditorAgent: { clearingMemberId: agent }, creditorAccount: { iban }, instructedAmount: { value: Math.round((fixedPay + variablePay) * 100) / 100, currency } },
      ext: { uae: { wps: { personId, payStartDate, payEndDate, daysInPeriod: Number(days), fixedPay, variablePay, leaveDays: Number(leave) } } },
    };
  });
  return 'mapped';
}

export async function run({ ctx }) {
  const inbound = ctx.messages?.inbound;
  const text: string = inbound?.payload ?? '';
  if (!text || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const { ls, fileName } = lines(text);
  const name = inbound?.fileName ?? fileName;
  const isSource = ls.some((l) => l.startsWith('EDR,')) || (ls.some((l) => l.startsWith('PCR,')) && !ls.some((l) => l.startsWith('PDR,')) && ls.find((l) => l.startsWith('PCR,'))!.split(',').length === 10);
  const outcome = isSource ? source(ls, name, ctx) : first(ls, name, ctx);
  return outcome ?? { outcome: 'unmappable', reasonCode: 'FF01' };
}
