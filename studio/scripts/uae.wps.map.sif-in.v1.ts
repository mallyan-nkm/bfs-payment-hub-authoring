// WPS salary information file (SIF) to the canonical batch. Variants: SCR first or SCR last.
// Field mapping as declared in uae.wps.map.sif-in.v1. Runs the edge rule the map names.

function lines(text: string) {
  let fileName: string | undefined;
  const out: string[] = [];
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.trim();
    if (!l) continue;
    if (l.startsWith('#')) {
      const m = /^#\s*file:\s*(\S+)/.exec(l);
      if (m) fileName = m[1];
      continue;
    }
    out.push(l);
  }
  return { fileName, lines: out };
}

const money = (s: string) => Math.round(Number(s) * 100) / 100;

export async function run({ ctx, rules }) {
  const inbound = ctx.messages?.inbound;
  const text: string = inbound?.payload ?? '';
  if (!text || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const { fileName, lines: ls } = lines(text);
  const scr = ls.filter((l) => l.startsWith('SCR,'));
  const edr = ls.filter((l) => l.startsWith('EDR,'));
  if (scr.length !== 1 || edr.length === 0) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const variant = ls[0].startsWith('SCR,') ? 'scr-first' : ls[ls.length - 1].startsWith('SCR,') ? 'scr-last' : null;
  if (!variant) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const s = scr[0].split(',');
  if (s.length < 9) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const [, employerId, employerRoutingCode, creationDate, creationTime, salaryMonth, recordCount, totalSalary, currency, employerRef] = s;
  const fileId = (fileName ?? `${employerId}-${creationDate}-${creationTime}`).replace(/\.sif$/i, '');
  // The employer's file comes in, but the payment it starts goes out: an outward corporate payment.
  // The intake map says so, since the dispatch table does not (Issue I-26).
  ctx.header = { fileId, endToEndId: fileId, direction: 'outward', segment: 'corporate' };
  ctx.batch = {
    id: fileId,
    numberOfTransactions: Number(recordCount),
    controlSum: money(totalSalary),
    currency,
    categoryPurpose: 'SALA',
    debtorAgent: { clearingMemberId: employerRoutingCode },
  };
  ctx.ext = { uae: { wps: { employerId, fileCreationDate: creationDate, fileCreationTime: creationTime, salaryMonth, ...(employerRef ? { employerReference: employerRef } : {}) } } };
  const items = [];
  for (const line of edr) {
    const f = line.split(',');
    if (f.length < 10) return { outcome: 'unmappable', reasonCode: 'FF01' };
    const [, personId, agentRoutingCode, iban, payStartDate, payEndDate, days, fixed, variable, leave] = f;
    const fixedPay = money(fixed);
    const variablePay = money(variable);
    items.push({
      payment: {
        creditorAgent: { clearingMemberId: agentRoutingCode },
        creditorAccount: { iban },
        instructedAmount: { value: Math.round((fixedPay + variablePay) * 100) / 100, currency },
      },
      ext: { uae: { wps: { personId, payStartDate, payEndDate, daysInPeriod: Number(days), fixedPay, variablePay, leaveDays: Number(leave) } } },
    });
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
