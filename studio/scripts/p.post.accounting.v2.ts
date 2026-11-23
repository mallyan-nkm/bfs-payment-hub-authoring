// Post an order with the accounting template (R-12-23, Issue I-136). A platform activity: it reads what the platform's own steps
// found, the converted amounts (facts.fx) and the charges with their VAT (facts.charges), and prices nothing itself.
//
// Each line of the order (each item, or the payment when the order has none) posts, in line order:
//   1. its converted amount (its facts.fx.amount in facts.fx.to, else its instructed amount), between the line's account and the
//      template's settlement account: a line that names a debtor account is debited, one that names only a creditor account is
//      credited;
//   2. its charge (facts.charges.amount), debited from the line's account to the charge income account;
//   3. the VAT on that charge (facts.charges.tax.amount), debited from the line's account to the VAT account.
// A charge or VAT of zero posts no entry. An order with no lines takes the order's own facts.fx and facts.charges. The amounts are
// exact (rules.money). The entries are camt.054's (Ntry: CdtDbtInd, Amt, the account masked to its last four characters); the legs
// keep the accounts for a reversal (p.post.reverse.v1). totalAmount is what the customer bears: the amount with the charge and the
// VAT where the line is debited, the amount less them where it is credited. Core banking answers through p.port.core-banking.v1:
// accepted is posted, refused is rejected, no clear answer is unknown, never posted again blindly.

const mask = (a: string) => (a.length <= 4 ? '*'.repeat(a.length) : '*'.repeat(a.length - 4) + a.slice(-4));
const accountOf = (acc: any) => String(acc?.id ?? acc?.iban ?? acc?.other ?? '');
const branchOf = (a: string) => (a.length > 9 && /^\d+$/.test(a) ? a.slice(0, 3) : '');

export async function run({ ctx, params, ports, rules }) {
  const m = rules.money;
  const lined = Array.isArray(ctx.items) && ctx.items.length > 0;
  const lines: any[] = lined ? ctx.items : [{ payment: ctx.payment ?? {}, facts: { fx: ctx.facts?.fx, charges: ctx.facts?.charges } }];
  const reference = String(ctx.header?.paymentId ?? ctx.header?.endToEndId ?? '');
  const narration = `${params.narration} ${reference}`.trim();
  const legs: any[] = [];
  const detail: any[] = [];
  const leg = (kind: string, account: string, amount: any, currency: string, txnCode: string, text: string) =>
    legs.push({ kind, branch: branchOf(account), account, amount, currency, txnCode, narration: text });
  let customerTotal: any = 0;
  for (const [i, line] of lines.entries()) {
    const p = line?.payment ?? {};
    const fx = line?.facts?.fx;
    const ch = line?.facts?.charges;
    const amount = fx?.amount ?? p.instructedAmount?.value ?? 0;
    const currency = String(fx?.to ?? fx?.currency ?? p.instructedAmount?.currency ?? 'AED');
    const debit = p.debtorAccount !== undefined;
    const account = accountOf(debit ? p.debtorAccount : p.creditorAccount);
    if (!account) return { outcome: 'rejected', reasonCode: 'AC01' };
    leg(debit ? 'debit' : 'credit', account, amount, currency, params.transactionCode, narration);
    leg(debit ? 'credit' : 'debit', params.settlementAccount, amount, currency, params.transactionCode, narration);
    const charge = ch?.amount ?? 0;
    const vat = ch?.tax?.amount ?? 0;
    if (m.cmp(charge, 0) > 0) {
      leg('debit', account, charge, currency, params.chargeTransactionCode, params.chargeNarration);
      leg('credit', params.chargeIncomeAccount, charge, currency, params.chargeTransactionCode, params.chargeNarration);
    }
    if (m.cmp(vat, 0) > 0) {
      leg('debit', account, vat, currency, params.vatTransactionCode, params.chargeNarration);
      leg('credit', params.vatAccount, vat, currency, params.vatTransactionCode, params.chargeNarration);
    }
    const borne = m.add(charge, vat);
    customerTotal = debit ? m.add(customerTotal, amount, borne) : m.add(customerTotal, m.sub(amount, borne));
    detail.push({ line: i, side: debit ? 'debit' : 'credit', amount, currency, charge, vat });
  }
  const currencies = [...new Set(detail.map((d) => d.currency))];
  const chargeAmount = m.sum(detail, (d) => d.charge);
  const vatAmount = m.sum(detail, (d) => d.vat);
  const vatRate = lines.map((l) => l?.facts?.charges?.tax?.rate).find((r) => r !== undefined) ?? ctx.facts?.charges?.tax?.rate ?? 0;
  const reply = await ports.request('p.port.core-banking.v1');
  const outcome = reply.malformed ? 'unknown' : reply.status === 'rejected' ? 'rejected' : reply.status === 'pending' || reply.status === 'unknown' ? 'unknown' : 'posted';
  const posting: any = {
    outcome, result: outcome, template: params.template, reference, reversalTransactionCode: params.reversalTransactionCode, legs,
    entries: legs.map((l) => ({ creditDebitIndicator: l.kind === 'debit' ? 'DBIT' : 'CRDT', amount: l.amount, currency: l.currency, account: mask(l.account), branch: l.branch, transactionCode: l.txnCode })),
    entryCount: legs.length, lines: detail, amount: m.sum(detail, (d) => d.amount), totalAmount: customerTotal,
  };
  if (currencies.length === 1) posting.currency = currencies[0];
  if (m.cmp(m.add(chargeAmount, vatAmount), 0) > 0) {
    posting.charges = { amount: chargeAmount, ...(currencies.length === 1 ? { currency: currencies[0] } : {}), type: 'BANK', tax: { rate: vatRate, amount: vatAmount }, totalChargesAndTaxAmount: m.add(chargeAmount, vatAmount) };
  }
  if (reply.data?.hostReference) posting.hostReference = reply.data.hostReference;
  ctx.facts = { ...(ctx.facts ?? {}), posting };
  return outcome === 'posted' ? 'posted' : { outcome, reasonCode: 'NARR' };
}
