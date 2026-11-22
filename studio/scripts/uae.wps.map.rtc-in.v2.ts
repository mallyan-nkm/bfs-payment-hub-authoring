// WPS return to the employer's bank (RTC) to the canonical inward return (uae.wps.map.rtc-in.v2, release 1.0.0).
//
// The layout is the factory's and provisional: the source parses no RTC lines (its ParseRtcRecords takes only a file name, a
// count and a total). One RTH line, then one RTD line per returned employee:
//   RTH,<employer id>,<employer account>,<employer's bank routing code>,<count>,<total>,<currency>
//   RTD,<person id>,<employee account>,<amount>,<reason>
// The payment-level creditor account is the employer's, so the leg's posting credits it (plan P2), at the bank the RTH names
// (this bank: the payment file checks hold it to the bank's own routing codes). The scheme settles the return: the debtor account
// is the return file, by its id, and the posting debits the bank's settlement account. The count and the total
// must match the lines. The runtime's native code-behind reads the same layout (R-08-16).

export async function run({ ctx }) {
  const inbound = ctx.messages?.inbound;
  const text: string = inbound?.payload ?? '';
  if (!text || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  let fileName: string | undefined = inbound?.fileName;
  const ls = text.split(/\r?\n/).map((l) => l.trim()).filter((l) => {
    if (!l) return false;
    if (l.startsWith('#')) { const m = /^#\s*file:\s*(\S+)/.exec(l); if (m) fileName ??= m[1]; return false; }
    return true;
  });
  const head = ls.filter((l) => l.startsWith('RTH,'));
  if (head.length !== 1) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const h = head[0].split(',');
  if (h.length !== 7 || !/^\d+$/.test(h[4]) || !/^\d+(\.\d+)?$/.test(h[5])) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const [, employerId, employerAccount, agent, count, total, currency] = h;
  const cents = (s: string) => Math.round(Number(s) * 100);
  const items: any[] = [];
  let sum = 0;
  for (const l of ls.filter((x) => x.startsWith('RTD,'))) {
    const d = l.split(',');
    if (d.length !== 5 || !/^\d+(\.\d+)?$/.test(d[3])) return { outcome: 'unmappable', reasonCode: 'FF01' };
    sum += cents(d[3]);
    items.push({
      payment: { creditorAccount: { iban: d[2] }, instructedAmount: { value: Number(d[3]), currency } },
      ext: { uae: { wps: { personId: d[1], returnReason: d[4] } } },
    });
  }
  if (items.length !== Number(count) || sum !== cents(total)) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const fileId = (fileName ?? `RTC-${employerId}`).replace(/\.rtc$/i, '');
  ctx.header = { fileId, endToEndId: fileId, direction: 'inward' };
  ctx.batch = { id: fileId, numberOfTransactions: Number(count), controlSum: Number(total), currency, categoryPurpose: 'SALA', creditorAgent: { clearingMemberId: agent } };
  ctx.payment = { creditorAccount: { iban: employerAccount }, creditorAgent: { clearingMemberId: agent }, debtorAccount: { other: fileId }, instructedAmount: { value: Number(total), currency } };
  ctx.ext = { uae: { wps: { employerId, fileName } } };
  ctx.items = items;
  ctx.status = { code: 'RCVD' };
  return 'mapped';
}
