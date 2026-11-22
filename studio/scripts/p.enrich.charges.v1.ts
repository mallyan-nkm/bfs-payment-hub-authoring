// Price the bank's charge and its VAT on each line of an order (R-12-20). A platform activity: the slabs, the floor, the ceiling
// and the VAT are the bank's values (p.enrich.charges.v1#chargeSlabs, #minCharge, #maxCharge, #vatPercent), effective-dated in
// its configuration; the factory's defaults price nothing.
//
// A line is priced on its converted amount (facts.fx, else its instructed amount): the first slab, in ascending upTo, whose upTo
// is not below the amount gives fixed + amount x percent / 100. The floor and the ceiling apply where they are above zero. The
// charge is rounded to two decimals, half away from zero, and the VAT is charge x vatPercent / 100, rounded the same way, per line.
// The order's charge and VAT are the sums of the rounded lines, not the rounded sums. The shape is ChrgsInf (Part 2, facts.posting):
// each line's on the line (items[].facts.charges), the order's totals in facts.charges.

export async function run({ ctx, params, rules }) {
  const m = rules.money;
  const slabs: any[] = (Array.isArray(params.chargeSlabs) ? params.chargeSlabs : []).filter((s) => s && s.upTo !== undefined)
    .sort((a, b) => m.cmp(a.upTo, b.upTo));
  const floor = params.minCharge ?? 0;
  const ceiling = params.maxCharge ?? 0;
  const vat = params.vatPercent ?? 0;
  const lined = Array.isArray(ctx.items) && ctx.items.length > 0;
  const items: any[] = lined ? ctx.items : [{ payment: ctx.payment ?? {}, facts: { fx: ctx.facts?.fx } }];
  const lines: any[] = [];
  let priced = 0;
  items.forEach((item) => {
    const fx = item?.facts?.fx;
    const amount = fx?.amount ?? item?.payment?.instructedAmount?.value ?? 0;
    const currency = fx?.to ?? item?.payment?.instructedAmount?.currency;
    const slab = slabs.find((s) => m.cmp(s.upTo, amount) >= 0);
    if (!slab) {
      lines.push({ outcome: 'none', amount: 0, currency, tax: { rate: vat, amount: 0 }, totalChargesAndTaxAmount: 0 });
      return;
    }
    priced++;
    let base = m.add(slab.fixed ?? 0, m.div(m.mul(amount, slab.percent ?? 0), 100));
    if (m.cmp(floor, 0) > 0 && m.cmp(base, floor) < 0) base = floor;
    if (m.cmp(ceiling, 0) > 0 && m.cmp(base, ceiling) > 0) base = ceiling;
    const charge = m.round2(base);
    const tax = m.round2(m.div(m.mul(charge, vat), 100));
    lines.push({ outcome: 'priced', amount: charge, currency, slab: slab.upTo, tax: { rate: vat, amount: tax }, totalChargesAndTaxAmount: m.add(charge, tax) });
  });
  if (lined) ctx.items = ctx.items.map((item: any, i: number) => ({ ...item, facts: { ...(item.facts ?? {}), charges: lines[i] } }));
  const outcome = priced ? 'priced' : 'none';
  const amount = m.sum(lines, (l) => l.amount);
  const taxAmount = m.sum(lines, (l) => l.tax.amount);
  const currencies = [...new Set(lines.map((l) => l.currency))];
  ctx.facts = {
    ...(ctx.facts ?? {}),
    charges: {
      outcome, result: outcome, lines: lines.length, amount, ...(currencies.length === 1 ? { currency: currencies[0] } : {}),
      tax: { rate: vat, amount: taxAmount }, totalChargesAndTaxAmount: m.add(amount, taxAmount),
    },
  };
  return outcome;
}
