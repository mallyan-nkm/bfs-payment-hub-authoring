// Scheme credit transfer (ISO 20022 pacs.008) to the canonical payment.

function tag(xml: string, path: string[]): string | undefined {
  let cur = xml;
  for (const t of path) {
    const m = new RegExp(`<${t}(?:\\s[^>]*)?>([\\s\\S]*?)</${t}>`).exec(cur);
    if (!m) return undefined;
    cur = m[1];
  }
  return cur.trim();
}

function attr(xml: string, t: string, a: string): string | undefined {
  const m = new RegExp(`<${t}[^>]*\\s${a}="([^"]*)"`).exec(xml);
  return m ? m[1] : undefined;
}

function party(xml: string, t: string) {
  const p = tag(xml, [t]);
  if (!p) return undefined;
  const town = tag(p, ['PstlAdr', 'TwnNm']);
  const ctry = tag(p, ['PstlAdr', 'Ctry']);
  return { name: tag(p, ['Nm']), ...(town || ctry ? { postalAddress: { townName: town, country: ctry } } : {}) };
}

export async function run({ ctx }) {
  const inbound = ctx.messages?.inbound;
  const xml: string = inbound?.payload ?? '';
  if (!xml || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const tx = tag(xml, ['FIToFICstmrCdtTrf', 'CdtTrfTxInf']);
  if (!tx) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const amount = tag(tx, ['IntrBkSttlmAmt']);
  const endToEndId = tag(tx, ['PmtId', 'EndToEndId']);
  if (!amount || !endToEndId) return { outcome: 'unmappable', reasonCode: 'FF01' };
  ctx.header = { endToEndId, uetr: tag(tx, ['PmtId', 'UETR']), segment: 'commercial' };
  ctx.payment = {
    instructedAmount: { value: Number(amount), currency: attr(tx, 'IntrBkSttlmAmt', 'Ccy') },
    debtor: party(tx, 'Dbtr'),
    debtorAccount: { iban: tag(tx, ['DbtrAcct', 'Id', 'IBAN']) },
    debtorAgent: { clearingMemberId: tag(tx, ['DbtrAgt', 'FinInstnId', 'ClrSysMmbId', 'MmbId']) },
    creditor: party(tx, 'Cdtr'),
    creditorAccount: { iban: tag(tx, ['CdtrAcct', 'Id', 'IBAN']) },
    creditorAgent: { clearingMemberId: tag(tx, ['CdtrAgt', 'FinInstnId', 'ClrSysMmbId', 'MmbId']) },
    purpose: tag(tx, ['Purp', 'Cd']),
    remittanceInformation: { unstructured: tag(tx, ['RmtInf', 'Ustrd']) },
  };
  return 'mapped';
}
